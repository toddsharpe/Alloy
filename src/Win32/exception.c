// Exceptions: the dispatcher the kernel enters on a ring-3 fault, the vectored handlers it runs, RaiseException, fail fast, and the function table lookups the unwinder and runtime use
#include "win32.h"

#define PhxMaxVectoredHandlers 8

typedef struct { PVECTORED_EXCEPTION_HANDLER Handler; BOOL First; } PhxVectoredHandler;

static PhxLock s_exceptionLock;
static PhxVectoredHandler s_vectoredHandlers[PhxMaxVectoredHandlers];
static int s_vectoredCount;
static PTOP_LEVEL_EXCEPTION_FILTER s_unhandledFilter;

__declspec(noreturn) static void PhxUnhandled(EXCEPTION_RECORD* record, CONTEXT* context)
{
	PhxLogFlush();
	PhxLogFormat("Unhandled exception 0x%08X at %p (rsp %p, info %p %p)", record->ExceptionCode, record->ExceptionAddress, (PVOID)context->Rsp, (PVOID)record->ExceptionInformation[0], (PVOID)record->ExceptionInformation[1]);
	PhxSyscall1(PhxSysExitProcess, (ULONG64)record->ExceptionCode);
	for (;;)
	{
	}
}

// Windows' order: first-call handlers, then the rest; a handler that resumes gets the context restored through an iretq
static void PhxDispatch(EXCEPTION_RECORD* record, CONTEXT* context)
{
	EXCEPTION_POINTERS pointers = { record, context };
	for (int pass = 0; pass < 2; pass++)
	{
		for (int i = 0; i < s_vectoredCount; i++)
		{
			PhxVectoredHandler handler = s_vectoredHandlers[i];
			if (handler.Handler == NULL || (handler.First != 0) != (pass == 0))
				continue;
			if (handler.Handler(&pointers) == EXCEPTION_CONTINUE_EXECUTION)
				RtlRestoreContext(context, record);
		}
	}
	if (s_unhandledFilter != NULL && s_unhandledFilter(&pointers) == EXCEPTION_CONTINUE_EXECUTION)
		RtlRestoreContext(context, record);
	PhxUnhandled(record, context);
}

// The kernel lands here with the record and the faulting thread's context on its stack (Arch/UserContext.cs ExceptionDelivery)
static void PhxExceptionDispatcher(EXCEPTION_RECORD* record, CONTEXT* context)
{
	PhxDispatch(record, context);
}

void PhxInitializeExceptions(void)
{
	PhxSyscall1(PhxSysSetExceptionDispatcher, (ULONG64)&PhxExceptionDispatcher);
}

PVOID AddVectoredExceptionHandler(ULONG first, PVECTORED_EXCEPTION_HANDLER handler)
{
	PhxLockAcquire(&s_exceptionLock);
	PhxVectoredHandler* slot = NULL;
	for (int i = 0; i < PhxMaxVectoredHandlers; i++)
	{
		if (s_vectoredHandlers[i].Handler == NULL)
		{
			slot = &s_vectoredHandlers[i];
			slot->Handler = handler;
			slot->First = first != 0;
			if (i >= s_vectoredCount)
				s_vectoredCount = i + 1;
			break;
		}
	}
	PhxLockRelease(&s_exceptionLock);
	return slot;
}

PTOP_LEVEL_EXCEPTION_FILTER SetUnhandledExceptionFilter(PTOP_LEVEL_EXCEPTION_FILTER filter)
{
	PTOP_LEVEL_EXCEPTION_FILTER previous = s_unhandledFilter;
	s_unhandledFilter = filter;
	return previous;
}

LONG UnhandledExceptionFilter(EXCEPTION_POINTERS* pointers)
{
	PhxLogFormat("Unhandled exception 0x%08X at %p", pointers->ExceptionRecord->ExceptionCode, pointers->ExceptionRecord->ExceptionAddress);
	return EXCEPTION_EXECUTE_HANDLER;
}

// A software exception goes to the same handlers a fault does, with the caller as its address
void RaiseException(DWORD code, DWORD flags, DWORD count, const ULONG_PTR* arguments)
{
	CONTEXT context;
	EXCEPTION_RECORD record;
	RtlCaptureContext(&context);
	memset(&record, 0, sizeof(record));
	record.ExceptionCode = code;
	record.ExceptionFlags = flags & EXCEPTION_NONCONTINUABLE;
	record.ExceptionAddress = (PVOID)context.Rip;
	record.NumberParameters = count > 15 ? 15 : count;
	for (DWORD i = 0; i < record.NumberParameters; i++)
		record.ExceptionInformation[i] = arguments[i];
	PhxDispatch(&record, &context);
}

void RaiseFailFastException(EXCEPTION_RECORD* record, CONTEXT* context, DWORD flags)
{
	(void)flags;
	DWORD code = record != NULL ? record->ExceptionCode : STATUS_FAIL_FAST_EXCEPTION;
	PVOID address = record != NULL ? record->ExceptionAddress : context != NULL ? (PVOID)context->Rip : NULL;
	PhxFatal(code, "FailFast: exception 0x%08X at %p", code, address);
}

BOOL TerminateProcess(HANDLE process, DWORD exitCode)
{
	(void)process;
	PhxLogFlush();
	PhxSyscall1(PhxSysExitProcess, exitCode);
	return TRUE;
}

// The layer's own last words: one line to the kernel log, then the process ends with the given code
void PhxFatal(DWORD exitCode, const char* format, ...)
{
	char line[200];
	va_list arguments;
	va_start(arguments, format);
	int length = PhxFormatV(line, sizeof(line), format, arguments);
	va_end(arguments);
	PhxLogFlush();
	PhxSyscall2(PhxSysDebugPrint, line, length < (int)sizeof(line) ? (size_t)length : sizeof(line) - 1);
	PhxSyscall1(PhxSysExitProcess, exitCode);
	for (;;)
	{
	}
}

void abort(void)
{
	PhxFatal(3, "abort() called");
}

void terminate(void)
{
	PhxFatal(3, "terminate() called");
}

BOOL IsDebuggerPresent(void)
{
	return FALSE;
}

void DebugBreak(void)
{
	__debugbreak();
}

// What a Windows 10 x64 process answers for the features the CRT and runtime ask about; fast fail is off so gs failures report instead of trapping
BOOL IsProcessorFeaturePresent(DWORD feature)
{
	switch (feature)
	{
		case 2:
		case 3:
		case 6:
		case 8:
		case 9:
		case 10:
		case 12:
		case 13:
		case 14:
		case 15:
		case 36:
		case 37:
		case 38:
			return TRUE;
		default:
			return FALSE;
	}
}

// The image's exception directory, sorted by the linker, is the only function table there is
RUNTIME_FUNCTION* RtlLookupFunctionEntry(DWORD64 pc, DWORD64* imageBase, PVOID historyTable)
{
	(void)historyTable;
	if (pc < (DWORD64)g_phx.ImageBase || pc >= (DWORD64)g_phx.ImageBase + g_phx.ImageSize || g_phx.Functions == NULL)
		return NULL;
	*imageBase = (DWORD64)g_phx.ImageBase;
	DWORD rva = (DWORD)(pc - (DWORD64)g_phx.ImageBase);
	DWORD low = 0;
	DWORD high = g_phx.FunctionCount;
	while (low < high)
	{
		DWORD middle = low + (high - low) / 2;
		const RUNTIME_FUNCTION* function = &g_phx.Functions[middle];
		if (rva < function->BeginAddress)
			high = middle;
		else if (rva >= function->EndAddress)
			low = middle + 1;
		else
			return (RUNTIME_FUNCTION*)function;
	}
	return NULL;
}

PVOID RtlPcToFileHeader(PVOID pc, PVOID* base)
{
	BOOL inside = (BYTE*)pc >= g_phx.ImageBase && (BYTE*)pc < g_phx.ImageBase + g_phx.ImageSize;
	*base = inside ? g_phx.ImageBase : NULL;
	return *base;
}

// Frame-based unwinding to a target is how SEH handlers and longjmp resume; nothing here dispatches SEH, so reaching it is a bug
void RtlUnwindEx(PVOID targetFrame, PVOID targetIp, EXCEPTION_RECORD* record, PVOID returnValue, CONTEXT* context, PVOID historyTable)
{
	(void)record;
	(void)returnValue;
	(void)context;
	(void)historyTable;
	PhxFatal(STATUS_FAIL_FAST_EXCEPTION, "RtlUnwindEx to %p (frame %p) is not supported", targetIp, targetFrame);
}

// Walks the caller's native frames with the unwinder; managed frames have unwind info too, so it crosses them
WORD RtlCaptureStackBackTrace(DWORD skip, DWORD count, PVOID* frames, DWORD* hash)
{
	CONTEXT context;
	RtlCaptureContext(&context);
	WORD captured = 0;
	DWORD total = 0;
	while (captured < count)
	{
		DWORD64 imageBase;
		RUNTIME_FUNCTION* function = RtlLookupFunctionEntry(context.Rip, &imageBase, NULL);
		if (function == NULL)
		{
			if (context.Rip == 0 || context.Rsp == 0)
				break;
			context.Rip = *(DWORD64*)context.Rsp;
			context.Rsp += 8;
		}
		else
		{
			PVOID handlerData;
			DWORD64 establisher;
			RtlVirtualUnwind(0, imageBase, context.Rip, function, &context, &handlerData, &establisher, NULL);
		}
		if (context.Rip == 0)
			break;
		if (skip > 0)
		{
			skip--;
			continue;
		}
		frames[captured++] = (PVOID)context.Rip;
		total += (DWORD)context.Rip;
	}
	if (hash != NULL)
		*hash = total;
	return captured;
}
