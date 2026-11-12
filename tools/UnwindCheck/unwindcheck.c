// Runs Alloy's RtlVirtualUnwind (src/Win32/unwind.c) and Windows' own over every instruction of an image and reports where they disagree
#define _CRT_SECURE_NO_WARNINGS
#include <windows.h>
#include <stdio.h>
#include <stdlib.h>
#include <string.h>

PEXCEPTION_ROUTINE PhxRtlVirtualUnwind(DWORD handlerType, DWORD64 imageBase, DWORD64 pc, PRUNTIME_FUNCTION function, PCONTEXT context, PVOID* handlerData, PDWORD64 establisherFrame, PKNONVOLATILE_CONTEXT_POINTERS pointers);

#define StackBytes (1 << 20)

static BYTE* s_base;
static RUNTIME_FUNCTION* s_table;
static size_t s_functions;

static int CompareFunction(const void* a, const void* b)
{
	DWORD rva = *(const DWORD*)a;
	const RUNTIME_FUNCTION* f = (const RUNTIME_FUNCTION*)b;
	return rva < f->BeginAddress ? -1 : rva >= f->EndAddress ? 1 : 0;
}

// Alloy's unwinder asks the Win32 layer for the entry around a jump target; here that is the loaded image's own table
PRUNTIME_FUNCTION PhxRtlLookupFunctionEntry(DWORD64 pc, PDWORD64 imageBase, PVOID history)
{
	(void)history;
	if (pc < (DWORD64)s_base)
		return NULL;
	DWORD rva = (DWORD)(pc - (DWORD64)s_base);
	*imageBase = (DWORD64)s_base;
	return (PRUNTIME_FUNCTION)bsearch(&rva, s_table, s_functions, sizeof(RUNTIME_FUNCTION), CompareFunction);
}

static void Fill(CONTEXT* c, BYTE* stack, DWORD64 pc)
{
	memset(c, 0, sizeof(*c));
	c->ContextFlags = CONTEXT_FULL;
	c->Rip = pc;
	c->Rsp = (DWORD64)(stack + StackBytes / 2);
	c->Rbp = c->Rsp + 0x200;
	c->Rbx = 0x1000000000000B0B;
	c->Rsi = 0x1000000000000051;
	c->Rdi = 0x1000000000000D1D;
	c->R12 = 0x1000000000000C12;
	c->R13 = 0x1000000000000C13;
	c->R14 = 0x1000000000000C14;
	c->R15 = 0x1000000000000C15;
	for (int i = 0; i < 16; i++)
	{
		(&c->Xmm0)[i].Low = 0x2000000000000000 + i;
		(&c->Xmm0)[i].High = 0x3000000000000000 + i;
	}
}

static int s_frames;

// The establisher frame is compared only when asked: Windows reports it differently inside epilogs that release through the frame pointer, and nothing in Alloy reads it for managed frames
static const char* Differences(const CONTEXT* a, const CONTEXT* b, DWORD64 ea, DWORD64 eb, const KNONVOLATILE_CONTEXT_POINTERS* pa, const KNONVOLATILE_CONTEXT_POINTERS* pb, char* out)
{
	out[0] = 0;
	for (int i = 0; i < 16; i++)
	{
		if (pa->IntegerContext[i] != pb->IntegerContext[i])
			sprintf(out + strlen(out), " p%d %p/%p", i, pa->IntegerContext[i], pb->IntegerContext[i]);
		if (pa->FloatingContext[i] != pb->FloatingContext[i])
			sprintf(out + strlen(out), " px%d %p/%p", i, pa->FloatingContext[i], pb->FloatingContext[i]);
	}
	if (a->Rip != b->Rip) sprintf(out + strlen(out), " rip %llX/%llX", a->Rip, b->Rip);
	if (a->Rsp != b->Rsp) sprintf(out + strlen(out), " rsp %llX/%llX", a->Rsp, b->Rsp);
	if (a->Rbp != b->Rbp) sprintf(out + strlen(out), " rbp %llX/%llX", a->Rbp, b->Rbp);
	if (a->Rbx != b->Rbx) sprintf(out + strlen(out), " rbx %llX/%llX", a->Rbx, b->Rbx);
	if (a->Rsi != b->Rsi) sprintf(out + strlen(out), " rsi %llX/%llX", a->Rsi, b->Rsi);
	if (a->Rdi != b->Rdi) sprintf(out + strlen(out), " rdi %llX/%llX", a->Rdi, b->Rdi);
	if (a->R12 != b->R12) sprintf(out + strlen(out), " r12 %llX/%llX", a->R12, b->R12);
	if (a->R13 != b->R13) sprintf(out + strlen(out), " r13 %llX/%llX", a->R13, b->R13);
	if (a->R14 != b->R14) sprintf(out + strlen(out), " r14 %llX/%llX", a->R14, b->R14);
	if (a->R15 != b->R15) sprintf(out + strlen(out), " r15 %llX/%llX", a->R15, b->R15);
	for (int i = 6; i < 16; i++)
		if (memcmp(&(&a->Xmm0)[i], &(&b->Xmm0)[i], 16) != 0)
			sprintf(out + strlen(out), " xmm%d", i);
	if (s_frames && ea != eb) sprintf(out + strlen(out), " frame %llX/%llX", ea, eb);
	return out;
}

int wmain(int argc, wchar_t** argv)
{
	if (argc < 3)
	{
		fprintf(stderr, "usage: unwindcheck <image.exe> <instruction addresses, hex per line, at the preferred base> [max reports] [frames]\n");
		return 2;
	}
	int maxReports = argc > 3 ? _wtoi(argv[3]) : 40;
	s_frames = argc > 4;
	HMODULE module = LoadLibraryExW(argv[1], NULL, DONT_RESOLVE_DLL_REFERENCES);
	if (module == NULL)
	{
		fprintf(stderr, "LoadLibraryEx failed: %lu\n", GetLastError());
		return 2;
	}
	BYTE* base = (BYTE*)module;
	IMAGE_NT_HEADERS64* nt = (IMAGE_NT_HEADERS64*)(base + ((IMAGE_DOS_HEADER*)base)->e_lfanew);
	// The loader rewrites ImageBase in memory to where it put the image, so the preferred base comes from the file
	DWORD64 preferred = nt->OptionalHeader.ImageBase;
	FILE* file = _wfopen(argv[1], L"rb");
	if (file != NULL)
	{
		static BYTE header[4096];
		if (fread(header, 1, sizeof(header), file) > 0x200)
			preferred = ((IMAGE_NT_HEADERS64*)(header + ((IMAGE_DOS_HEADER*)header)->e_lfanew))->OptionalHeader.ImageBase;
		fclose(file);
	}
	IMAGE_DATA_DIRECTORY directory = nt->OptionalHeader.DataDirectory[IMAGE_DIRECTORY_ENTRY_EXCEPTION];
	RUNTIME_FUNCTION* table = (RUNTIME_FUNCTION*)(base + directory.VirtualAddress);
	size_t functions = directory.Size / sizeof(RUNTIME_FUNCTION);
	s_base = base;
	s_table = table;
	s_functions = functions;
	printf("%zu functions, image at %p (preferred %llX)\n", functions, base, preferred);

	FILE* list = _wfopen(argv[2], L"r");
	if (list == NULL)
	{
		fprintf(stderr, "cannot open the instruction list\n");
		return 2;
	}
	BYTE* stack = (BYTE*)VirtualAlloc(NULL, StackBytes, MEM_COMMIT | MEM_RESERVE, PAGE_READWRITE);
	for (size_t i = 0; i < StackBytes / 8; i++)
		((DWORD64*)stack)[i] = 0x4000000000000000 + i * 8;

	char line[64];
	char text[8192];
	long long checked = 0, mismatches = 0, reported = 0;
	RUNTIME_FUNCTION* lastReported = NULL;
	while (fgets(line, sizeof(line), list) != NULL)
	{
		DWORD64 address = strtoull(line, NULL, 16);
		if (address < preferred)
			continue;
		DWORD rva = (DWORD)(address - preferred);
		RUNTIME_FUNCTION* function = (RUNTIME_FUNCTION*)bsearch(&rva, table, functions, sizeof(RUNTIME_FUNCTION), CompareFunction);
		if (function == NULL)
			continue;
		DWORD64 pc = (DWORD64)base + rva;
		CONTEXT a, b;
		Fill(&a, stack, pc);
		b = a;
		PVOID data;
		DWORD64 frameA = 0, frameB = 0;
		KNONVOLATILE_CONTEXT_POINTERS pointersA, pointersB;
		memset(&pointersA, 0, sizeof(pointersA));
		memset(&pointersB, 0, sizeof(pointersB));
		RtlVirtualUnwind(UNW_FLAG_NHANDLER, (DWORD64)base, pc, function, &a, &data, &frameA, &pointersA);
		PhxRtlVirtualUnwind(UNW_FLAG_NHANDLER, (DWORD64)base, pc, function, &b, &data, &frameB, &pointersB);
		checked++;
		Differences(&a, &b, frameA, frameB, &pointersA, &pointersB, text);
		if (text[0] == 0)
			continue;
		mismatches++;
		if ((function != lastReported || maxReports < 0) && reported < abs(maxReports))
		{
			lastReported = function;
			reported++;
			printf("%llX (function %llX-%llX, +%lX):%s\n", address, preferred + function->BeginAddress, preferred + function->EndAddress, rva - function->BeginAddress, text);
		}
	}
	printf("%lld instructions checked, %lld mismatches\n", checked, mismatches);
	return mismatches == 0 ? 0 : 1;
}
