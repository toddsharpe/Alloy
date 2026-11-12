// A self-test of thread suspension as the runtime's GC uses it: a thread spinning with known register contents is suspended, read with GetThreadContext, redirected through a trampoline with SetThreadContext and resumed, and every value is checked
#include "win32.h"

#ifndef CONTEXT_EXCEPTION_REQUEST
#define CONTEXT_EXCEPTION_REQUEST 0x40000000
#define CONTEXT_EXCEPTION_REPORTING 0x80000000
#define CONTEXT_SERVICE_ACTIVE 0x10000000
#define CONTEXT_EXCEPTION_ACTIVE 0x08000000
#endif

void PhxLogFormat(const char* format, ...);
void PhxSpinWithRegisters(volatile LONG* stop, volatile ULONG64* counter);
void* PhxSpinLoopAddress(void);
void* PhxSpinEndAddress(void);
void PhxRedirectTrampoline(void);

// What PhxSpinWithRegisters keeps in each register (win32.asm): rcx rdx rbx rbp rsi rdi r8 r9 r10 r11 r12 r13 r14 r15, then the low halves of xmm6-15
static const ULONG64 s_pattern[14] = { 0x0C0C0C0C0C0C0C0C, 0x0D0D0D0D0D0D0D0D, 0x0B0B0B0B0B0B0B0B, 0x0B9B9B9B9B9B9B9B, 0x0515151515151515, 0x0D1D1D1D1D1D1D1D, 0x0808080808080808, 0x0909090909090909, 0x1010101010101010, 0x1111111111111111, 0x0C12121212121212, 0x0C13131313131313, 0x0C14141414141414, 0x0C15151515151515 };

// Filled by the trampoline: rax rcx rdx rbx rsp rbp rsi rdi r8-r15 (16 slots), rflags, then xmm6-15 (10 slots of 16 bytes)
volatile ULONG64 g_phxTrampolineRegs[40];
static __declspec(align(16)) CONTEXT s_savedContext;
static volatile LONG s_stop;
static volatile ULONG64 s_counter;
static volatile LONG s_hits;
static LONG s_failures;
static LONG s_reported;

static void PhxFail(const char* what, ULONG64 expected, ULONG64 actual)
{
	s_failures++;
	if (s_reported++ < 12)
		PhxLogFormat("ctxtest: %s expected %016llX got %016llX", what, expected, actual);
}

// Called from the trampoline on the redirected thread after it recorded its registers; resumes the thread where it was suspended
void PhxTrampolineResume(void)
{
	_InterlockedIncrement(&s_hits);
	RtlRestoreContext(&s_savedContext, NULL);
}

static DWORD PhxSpinner(PVOID parameter)
{
	(void)parameter;
	PhxSpinWithRegisters(&s_stop, &s_counter);
	return 0;
}

static ULONG64* PhxReg(CONTEXT* context, int index)
{
	static const int offsets[14] = { 1, 2, 3, 5, 6, 7, 8, 9, 10, 11, 12, 13, 14, 15 };
	return &context->Rax + offsets[index];
}

static void PhxCheckPattern(CONTEXT* context, const char* when)
{
	static const char* names[14] = { "rcx", "rdx", "rbx", "rbp", "rsi", "rdi", "r8", "r9", "r10", "r11", "r12", "r13", "r14", "r15" };
	for (int i = 0; i < 14; i++)
	{
		if (*PhxReg(context, i) != s_pattern[i])
			PhxFail(names[i], s_pattern[i], *PhxReg(context, i));
	}
	for (int i = 0; i < 10; i++)
	{
		ULONG64 low = context->Fx.Xmm[6 + i].Low;
		if (low != 0xF600000000000000 + (ULONG64)i)
			PhxFail("xmm", 0xF600000000000000 + (ULONG64)i, low);
	}
	ULONG64 loop = (ULONG64)PhxSpinLoopAddress();
	ULONG64 end = (ULONG64)PhxSpinEndAddress();
	if (context->Rip < loop || context->Rip > end)
		PhxFail(when, loop, context->Rip);
	if ((context->EFlags & 0x200) == 0)
		PhxFail("eflags.if", 0x200, context->EFlags);
}

// Runs the checks for about the given milliseconds; returns the number of failures, having logged the first few
int PhxContextSelfTest(int milliseconds)
{
	s_stop = 0;
	s_counter = 0;
	s_hits = 0;
	s_failures = 0;
	s_reported = 0;
	HANDLE spinner = CreateThread(NULL, 256 * 1024, PhxSpinner, NULL, 0, NULL);
	if (spinner == NULL)
		return -1;
	Sleep(20);
	DWORD started = (DWORD)GetTickCount64();
	int rounds = 0, redirects = 0, inService = 0;
	while ((int)((DWORD)GetTickCount64() - started) < milliseconds)
	{
		if (SuspendThread(spinner) == (DWORD)-1)
		{
			PhxFail("SuspendThread", 0, GetLastError());
			break;
		}
		CONTEXT context;
		context.ContextFlags = CONTEXT_FULL | CONTEXT_EXCEPTION_REQUEST;
		if (!GetThreadContext(spinner, &context))
			PhxFail("GetThreadContext", 1, 0);
		rounds++;
		if ((context.ContextFlags & CONTEXT_EXCEPTION_REPORTING) == 0)
			PhxFail("reporting flag", CONTEXT_EXCEPTION_REPORTING, context.ContextFlags);
		if ((context.ContextFlags & CONTEXT_SERVICE_ACTIVE) != 0)
		{
			inService++;
			ResumeThread(spinner);
			Sleep(0);
			continue;
		}
		PhxCheckPattern(&context, "rip");
		CONTEXT again;
		again.ContextFlags = CONTEXT_FULL;
		GetThreadContext(spinner, &again);
		if (again.Rip != context.Rip || again.Rsp != context.Rsp || again.Rax != context.Rax || again.EFlags != context.EFlags)
			PhxFail("second read", context.Rip, again.Rip);

		if ((rounds & 1) == 0)
		{
			// Redirect as the GC does: the thread resumes in the trampoline, which records what it sees and restores the saved context
			ULONG64 before = s_counter;
			s_savedContext = context;
			CONTEXT redirected = context;
			redirected.Rip = (ULONG64)PhxRedirectTrampoline;
			LONG hits = s_hits;
			if (!SetThreadContext(spinner, &redirected))
				PhxFail("SetThreadContext", 1, 0);
			ResumeThread(spinner);
			DWORD waited = (DWORD)GetTickCount64();
			while (s_hits == hits && (int)((DWORD)GetTickCount64() - waited) < 1000)
				Sleep(0);
			if (s_hits == hits)
			{
				PhxFail("trampoline never ran", 1, 0);
				break;
			}
			redirects++;
			static const int slots[16] = { 0, 1, 2, 3, 4, 5, 6, 7, 8, 9, 10, 11, 12, 13, 14, 15 };
			for (int i = 0; i < 16; i++)
			{
				ULONG64 expected = (&context.Rax)[slots[i]];
				if (g_phxTrampolineRegs[i] != expected)
					PhxFail("trampoline gpr", expected, g_phxTrampolineRegs[i]);
			}
			if ((g_phxTrampolineRegs[16] & 0xCD5) != (context.EFlags & 0xCD5))
				PhxFail("trampoline flags", context.EFlags, g_phxTrampolineRegs[16]);
			for (int i = 0; i < 10; i++)
			{
				if (g_phxTrampolineRegs[18 + i * 2] != context.Fx.Xmm[6 + i].Low || g_phxTrampolineRegs[19 + i * 2] != context.Fx.Xmm[6 + i].High)
					PhxFail("trampoline xmm", context.Fx.Xmm[6 + i].Low, g_phxTrampolineRegs[18 + i * 2]);
			}
			DWORD moved = (DWORD)GetTickCount64();
			while (s_counter == before && (int)((DWORD)GetTickCount64() - moved) < 1000)
				Sleep(0);
			if (s_counter == before)
				PhxFail("spinner stopped after redirect", 1, 0);
		}
		else
		{
			if (!SetThreadContext(spinner, &context))
				PhxFail("SetThreadContext same", 1, 0);
			ResumeThread(spinner);
			Sleep(0);
		}
	}
	s_stop = 1;
	WaitForSingleObject(spinner, 2000);
	CloseHandle(spinner);
	PhxLogFormat("ctxtest: %d rounds, %d redirects, %d in a system call, %d failures, counter %llu", rounds, redirects, inService, s_failures, s_counter);
	return s_failures;
}
