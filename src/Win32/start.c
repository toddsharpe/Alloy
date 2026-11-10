// Process start and exit: the entry point the apps link with (EntryPointSymbol in src/Apps/Directory.Build.props) brings up the layer, MSVC's static CRT and then the NativeAOT runtime
#include "win32.h"

PhxProcess g_phx;

// The linker's image base, libcmt's cookie and CPU dispatch initializers and C initializer tables, and the NativeAOT bootstrapper's main
extern BYTE __ImageBase;
void __security_init_cookie(void);
int __isa_available_init(void);
extern _PIFV __xi_a[];
extern _PIFV __xi_z[];
extern _PVFV __xc_a[];
extern _PVFV __xc_z[];
int wmain(int argc, wchar_t** argv);
void PhxWin32Start(ULONG64 argument);

// Reads the PE headers for the image size and the exception directory RtlLookupFunctionEntry searches
static void PhxLocateImage(void)
{
	BYTE* base = &__ImageBase;
	BYTE* headers = base + *(DWORD*)(base + 0x3C);
	BYTE* optional = headers + 4 + 20;
	DWORD exceptionRva = *(DWORD*)(optional + 112 + 3 * 8);
	DWORD exceptionSize = *(DWORD*)(optional + 112 + 3 * 8 + 4);
	g_phx.ImageBase = base;
	g_phx.ImageSize = *(DWORD*)(optional + 56);
	g_phx.Functions = exceptionRva == 0 ? NULL : (const RUNTIME_FUNCTION*)(base + exceptionRva);
	g_phx.FunctionCount = exceptionSize / sizeof(RUNTIME_FUNCTION);
}

// The kernel left the standard handles, the command line and the image name in the process block
static void PhxReadProcessBlock(PhxTeb* teb)
{
	PhxPeb* peb = (PhxPeb*)teb->ProcessBlock;
	g_phx.Peb = peb;
	g_phx.StdHandles[0] = peb->StdInput == 0 ? PhxLogHandle : (HANDLE)peb->StdInput;
	g_phx.StdHandles[1] = peb->StdOutput == 0 ? PhxLogHandle : (HANDLE)peb->StdOutput;
	g_phx.StdHandles[2] = peb->StdError == 0 ? g_phx.StdHandles[1] : (HANDLE)peb->StdError;
	size_t length = peb->ImageNameLength < sizeof(peb->ImageName) ? (size_t)peb->ImageNameLength : sizeof(peb->ImageName);
	memcpy(g_phx.ImageName, peb->ImageName, length);
	g_phx.ImageName[length] = 0;
}

// Windows' loader keeps a list of loaded modules behind PEB.Ldr, and the runtime reads it directly to name the module in a stack trace: one entry, the image
static void PhxBuildLoaderData(void)
{
	typedef struct { PVOID Flink; PVOID Blink; } PhxListEntry;
	static BYTE s_loaderData[0x58];
	static BYTE s_entry[0x120];
	static wchar_t s_imagePath[0xC0 + 8];

	DWORD length = GetModuleFileNameW(NULL, s_imagePath, sizeof(s_imagePath) / sizeof(wchar_t));
	BYTE* ldr = s_loaderData;
	BYTE* entry = s_entry;
	*(DWORD*)ldr = sizeof(s_loaderData);
	ldr[4] = TRUE;
	for (int list = 0; list < 3; list++)
	{
		PhxListEntry* head = (PhxListEntry*)(ldr + 0x10 + list * 0x10);
		PhxListEntry* link = (PhxListEntry*)(entry + list * 0x10);
		head->Flink = head->Blink = link;
		link->Flink = link->Blink = head;
	}
	*(PVOID*)(entry + 0x30) = g_phx.ImageBase;
	*(PVOID*)(entry + 0x38) = (PVOID)&PhxWin32Start;
	*(DWORD*)(entry + 0x40) = (DWORD)g_phx.ImageSize;
	for (int name = 0; name < 2; name++)
	{
		BYTE* unicodeString = entry + 0x48 + name * 0x10;
		*(WORD*)unicodeString = (WORD)(length * sizeof(wchar_t));
		*(WORD*)(unicodeString + 2) = (WORD)((length + 1) * sizeof(wchar_t));
		*(wchar_t**)(unicodeString + 8) = s_imagePath;
	}
	*(PVOID*)((BYTE*)g_phx.Peb + 0x18) = ldr;
}

// The command line split at spaces into a wide argv, the program's own name first, as wmain expects it
static wchar_t** PhxBuildArguments(int* argc)
{
	const char* line = g_phx.Peb->CommandLine;
	size_t length = g_phx.Peb->CommandLineLength < sizeof(g_phx.Peb->CommandLine) ? (size_t)g_phx.Peb->CommandLineLength : sizeof(g_phx.Peb->CommandLine);
	int count = 0;
	for (size_t i = 0; i < length; i++)
	{
		if (line[i] != ' ' && (i == 0 || line[i - 1] == ' '))
			count++;
	}

	wchar_t** argv = (wchar_t**)malloc((count + 1) * sizeof(wchar_t*) + (length + 1) * sizeof(wchar_t));
	wchar_t* text = (wchar_t*)(argv + count + 1);
	int index = 0;
	for (size_t i = 0; i < length;)
	{
		while (i < length && line[i] == ' ')
			i++;
		if (i == length)
			break;
		argv[index++] = text;
		while (i < length && line[i] != ' ')
			*text++ = (wchar_t)(BYTE)line[i++];
		*text++ = 0;
	}
	argv[index] = NULL;
	*argc = index;
	return argv;
}

// Runs the C initializers libcmt collected into its .CRT sections, as __scrt_common_main_seh would
static void PhxRunInitializers(void)
{
	for (_PIFV* initializer = __xi_a; initializer < __xi_z; initializer++)
	{
		if (*initializer != NULL && (*initializer)() != 0)
			PhxFatal(255, "C initializer failed");
	}
	for (_PVFV* initializer = __xc_a; initializer < __xc_z; initializer++)
	{
		if (*initializer != NULL)
			(*initializer)();
	}
}

// Entered from the kernel on the first thread with rcx = the CreateProcess argument; nothing before this has run in the process
void PhxWin32Start(ULONG64 argument)
{
	PhxTeb* teb = PhxCurrentTeb();
	PhxLocateImage();
	PhxReadProcessBlock(teb);
	g_phx.Argument = argument;
	PhxBuildLoaderData();
	PhxInitializeHeap();
	PhxInitializeTime();
	PhxInitializeExceptions();
	PhxAttachThread(teb);

	__isa_available_init();
	__security_init_cookie();
	PhxRunInitializers();

	int argc;
	wchar_t** argv = PhxBuildArguments(&argc);
	PhxExitProcess(wmain(argc, argv));
}

// The argument the parent passed to CreateProcess, for the SDK (Alloy.Sdk Kernel.ProcessArgument)
ULONG64 PhxGetProcessArgument(void)
{
	return g_phx.Argument;
}

void ExitProcess(DWORD exitCode)
{
	PhxExitProcess((int)exitCode);
}

// Everything that leaves the process goes through here: atexit handlers, the log's last partial line, then the kernel
void PhxExitProcess(int exitCode)
{
	PhxRunExitTables();
	PhxLogFlush();
	PhxSyscall1(PhxSysExitProcess, (ULONG64)(LONG64)exitCode);
	for (;;)
	{
	}
}
