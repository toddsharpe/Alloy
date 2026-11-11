// The Win32 layer's one header: the Windows types and prototypes it implements, the kernel interface it stands on, and its own internals
#pragma once

// Freestanding (/X): the basic types, written out
typedef unsigned char BYTE;
typedef unsigned short WORD;
typedef unsigned short wchar_t;
typedef unsigned int DWORD;
typedef unsigned long ULONG;
typedef int BOOL;
typedef long LONG;
typedef long HRESULT;
typedef unsigned __int64 ULONG64;
typedef unsigned __int64 DWORD64;
typedef unsigned __int64 ULONGLONG;
typedef unsigned __int64 DWORDLONG;
typedef unsigned __int64 SIZE_T;
typedef unsigned __int64 ULONG_PTR;
typedef unsigned __int64 KAFFINITY;
typedef unsigned __int64 size_t;
typedef __int64 LONG64;
typedef __int64 LONGLONG;
typedef __int64 LONG_PTR;
typedef __int64 ptrdiff_t;
typedef __int64 intptr_t;
typedef void* PVOID;
typedef void* HANDLE;
typedef void* HMODULE;
typedef char* va_list;

#define NULL ((void*)0)
#define TRUE 1
#define FALSE 0
#define INFINITE 0xFFFFFFFF
#define INVALID_HANDLE_VALUE ((HANDLE)(LONG_PTR)-1)

void __cdecl __va_start(va_list*, ...);
#define va_start(ap, x) ((void)(__va_start(&ap, x)))
#define va_arg(ap, t) ((sizeof(t) > sizeof(__int64) || (sizeof(t) & (sizeof(t) - 1)) != 0) ? **(t**)((ap += sizeof(__int64)) - sizeof(__int64)) : *(t*)((ap += sizeof(__int64)) - sizeof(__int64)))
#define va_end(ap) ((void)(ap = (va_list)0))

// Compiler intrinsics
unsigned __int64 __readgsqword(unsigned long offset);
void __cpuidex(int info[4], int function, int subfunction);
void __cpuid(int info[4], int function);
unsigned __int64 __rdtsc(void);
void __debugbreak(void);
long _InterlockedCompareExchange(long volatile* target, long exchange, long comparand);
__int64 _InterlockedCompareExchange64(__int64 volatile* target, __int64 exchange, __int64 comparand);
void* _InterlockedCompareExchangePointer(void* volatile* target, void* exchange, void* comparand);
long _InterlockedExchange(long volatile* target, long value);
void* _InterlockedExchangePointer(void* volatile* target, void* value);
long _InterlockedIncrement(long volatile* target);
long _InterlockedDecrement(long volatile* target);
long _InterlockedExchangeAdd(long volatile* target, long value);
#pragma intrinsic(__readgsqword, __cpuidex, __cpuid, __rdtsc, __debugbreak)
#pragma intrinsic(_InterlockedCompareExchange, _InterlockedCompareExchange64, _InterlockedCompareExchangePointer, _InterlockedExchange, _InterlockedExchangePointer, _InterlockedIncrement, _InterlockedDecrement, _InterlockedExchangeAdd)

// From libvcruntime
void* memcpy(void* destination, const void* source, size_t count);
void* memmove(void* destination, const void* source, size_t count);
void* memset(void* destination, int value, size_t count);
int memcmp(const void* left, const void* right, size_t count);

// Windows structures the runtime hands us, byte for byte (winnt.h)
typedef struct { ULONGLONG Low; LONGLONG High; } M128A;

typedef struct __declspec(align(16)) _CONTEXT
{
	DWORD64 P1Home, P2Home, P3Home, P4Home, P5Home, P6Home;
	DWORD ContextFlags;
	DWORD MxCsr;
	WORD SegCs, SegDs, SegEs, SegFs, SegGs, SegSs;
	DWORD EFlags;
	DWORD64 Dr0, Dr1, Dr2, Dr3, Dr6, Dr7;
	DWORD64 Rax, Rcx, Rdx, Rbx, Rsp, Rbp, Rsi, Rdi, R8, R9, R10, R11, R12, R13, R14, R15;
	DWORD64 Rip;
	union
	{
		BYTE FltSave[512];
		struct { M128A Header[2]; M128A Legacy[8]; M128A Xmm[16]; } Fx;
	};
	M128A VectorRegister[26];
	DWORD64 VectorControl, DebugControl, LastBranchToRip, LastBranchFromRip, LastExceptionToRip, LastExceptionFromRip;
} CONTEXT;

#define CONTEXT_AMD64 0x100000
#define CONTEXT_CONTROL (CONTEXT_AMD64 | 0x1)
#define CONTEXT_INTEGER (CONTEXT_AMD64 | 0x2)
#define CONTEXT_SEGMENTS (CONTEXT_AMD64 | 0x4)
#define CONTEXT_FLOATING_POINT (CONTEXT_AMD64 | 0x8)
#define CONTEXT_DEBUG_REGISTERS (CONTEXT_AMD64 | 0x10)
#define CONTEXT_FULL (CONTEXT_CONTROL | CONTEXT_INTEGER | CONTEXT_FLOATING_POINT)
#define CONTEXT_ALL (CONTEXT_FULL | CONTEXT_SEGMENTS | CONTEXT_DEBUG_REGISTERS)
#define CONTEXT_XSTATE (CONTEXT_AMD64 | 0x40)

typedef struct _EXCEPTION_RECORD
{
	DWORD ExceptionCode;
	DWORD ExceptionFlags;
	struct _EXCEPTION_RECORD* ExceptionRecord;
	PVOID ExceptionAddress;
	DWORD NumberParameters;
	ULONG_PTR ExceptionInformation[15];
} EXCEPTION_RECORD;

typedef struct { EXCEPTION_RECORD* ExceptionRecord; CONTEXT* ContextRecord; } EXCEPTION_POINTERS;
typedef LONG (*PVECTORED_EXCEPTION_HANDLER)(EXCEPTION_POINTERS* pointers);
typedef LONG (*PTOP_LEVEL_EXCEPTION_FILTER)(EXCEPTION_POINTERS* pointers);
typedef void (*PEXCEPTION_ROUTINE)(void);

#define EXCEPTION_NONCONTINUABLE 0x1
#define EXCEPTION_CONTINUE_EXECUTION (-1)
#define EXCEPTION_CONTINUE_SEARCH 0
#define EXCEPTION_EXECUTE_HANDLER 1
#define STATUS_BREAKPOINT 0x80000003
#define STATUS_ACCESS_VIOLATION 0xC0000005
#define STATUS_NO_MEMORY 0xC0000017
#define STATUS_STACK_BUFFER_OVERRUN 0xC0000409
#define STATUS_FAIL_FAST_EXCEPTION 0xC0000602

typedef struct { DWORD BeginAddress; DWORD EndAddress; DWORD UnwindData; } RUNTIME_FUNCTION;
typedef struct { M128A* FloatingContext[16]; DWORD64* IntegerContext[16]; } KNONVOLATILE_CONTEXT_POINTERS;

typedef struct
{
	PVOID BaseAddress;
	PVOID AllocationBase;
	DWORD AllocationProtect;
	WORD PartitionId;
	WORD Reserved;
	SIZE_T RegionSize;
	DWORD State;
	DWORD Protect;
	DWORD Type;
	DWORD Reserved2;
} MEMORY_BASIC_INFORMATION;

#define MEM_COMMIT 0x1000
#define MEM_RESERVE 0x2000
#define MEM_DECOMMIT 0x4000
#define MEM_RELEASE 0x8000
#define MEM_FREE 0x10000
#define MEM_RESET 0x80000
#define MEM_TOP_DOWN 0x100000
#define MEM_WRITE_WATCH 0x200000
#define MEM_RESET_UNDO 0x1000000
#define MEM_LARGE_PAGES 0x20000000
#define PAGE_NOACCESS 0x01
#define PAGE_READONLY 0x02
#define PAGE_READWRITE 0x04
#define PAGE_EXECUTE_READ 0x20
#define PAGE_EXECUTE_READWRITE 0x40
#define PAGE_GUARD 0x100

typedef struct
{
	WORD wProcessorArchitecture;
	WORD wReserved;
	DWORD dwPageSize;
	PVOID lpMinimumApplicationAddress;
	PVOID lpMaximumApplicationAddress;
	ULONG_PTR dwActiveProcessorMask;
	DWORD dwNumberOfProcessors;
	DWORD dwProcessorType;
	DWORD dwAllocationGranularity;
	WORD wProcessorLevel;
	WORD wProcessorRevision;
} SYSTEM_INFO;

typedef struct
{
	DWORD dwLength;
	DWORD dwMemoryLoad;
	DWORDLONG ullTotalPhys;
	DWORDLONG ullAvailPhys;
	DWORDLONG ullTotalPageFile;
	DWORDLONG ullAvailPageFile;
	DWORDLONG ullTotalVirtual;
	DWORDLONG ullAvailVirtual;
	DWORDLONG ullAvailExtendedVirtual;
} MEMORYSTATUSEX;

typedef struct
{
	DWORD cb;
	DWORD PageFaultCount;
	SIZE_T PeakWorkingSetSize;
	SIZE_T WorkingSetSize;
	SIZE_T QuotaPeakPagedPoolUsage;
	SIZE_T QuotaPagedPoolUsage;
	SIZE_T QuotaPeakNonPagedPoolUsage;
	SIZE_T QuotaNonPagedPoolUsage;
	SIZE_T PagefileUsage;
	SIZE_T PeakPagefileUsage;
} PROCESS_MEMORY_COUNTERS;

typedef struct { BYTE Level; BYTE Associativity; WORD LineSize; DWORD Size; DWORD Type; } CACHE_DESCRIPTOR;

typedef struct
{
	ULONG_PTR ProcessorMask;
	DWORD Relationship;
	DWORD Padding;
	union
	{
		struct { BYTE Flags; } ProcessorCore;
		struct { DWORD NodeNumber; } NumaNode;
		CACHE_DESCRIPTOR Cache;
		ULONGLONG Reserved[2];
	};
} SYSTEM_LOGICAL_PROCESSOR_INFORMATION;

typedef struct { KAFFINITY Mask; WORD Group; WORD Reserved[3]; } GROUP_AFFINITY;
typedef struct { WORD Group; BYTE Number; BYTE Reserved; } PROCESSOR_NUMBER;
typedef struct { BYTE Flags; BYTE EfficiencyClass; BYTE Reserved[20]; WORD GroupCount; GROUP_AFFINITY GroupMask[1]; } PROCESSOR_RELATIONSHIP;
typedef struct { DWORD NodeNumber; BYTE Reserved[18]; WORD GroupCount; GROUP_AFFINITY GroupMask; } NUMA_NODE_RELATIONSHIP;
typedef struct { BYTE Level; BYTE Associativity; WORD LineSize; DWORD CacheSize; DWORD Type; BYTE Reserved[18]; WORD GroupCount; GROUP_AFFINITY GroupMask; } CACHE_RELATIONSHIP;
typedef struct { BYTE MaximumProcessorCount; BYTE ActiveProcessorCount; BYTE Reserved[38]; KAFFINITY ActiveProcessorMask; } PROCESSOR_GROUP_INFO;
typedef struct { WORD MaximumGroupCount; WORD ActiveGroupCount; BYTE Reserved[20]; PROCESSOR_GROUP_INFO GroupInfo[1]; } GROUP_RELATIONSHIP;

typedef struct
{
	DWORD Relationship;
	DWORD Size;
	union
	{
		PROCESSOR_RELATIONSHIP Processor;
		NUMA_NODE_RELATIONSHIP NumaNode;
		CACHE_RELATIONSHIP Cache;
		GROUP_RELATIONSHIP Group;
	};
} SYSTEM_LOGICAL_PROCESSOR_INFORMATION_EX;

#define RelationProcessorCore 0
#define RelationNumaNode 1
#define RelationCache 2
#define RelationProcessorPackage 3
#define RelationGroup 4
#define RelationAll 0xFFFF
#define CacheUnified 0
#define CacheInstruction 1
#define CacheData 2
#define CacheTrace 3

typedef struct
{
	PVOID DebugInfo;
	LONG LockCount;
	LONG RecursionCount;
	HANDLE OwningThread;
	HANDLE LockSemaphore;
	ULONG_PTR SpinCount;
} CRITICAL_SECTION;

typedef struct _SLIST_ENTRY { struct _SLIST_ENTRY* Next; } SLIST_ENTRY;
typedef struct __declspec(align(16)) { SLIST_ENTRY* Next; ULONGLONG Depth; } SLIST_HEADER;

typedef struct { DWORD dwLowDateTime; DWORD dwHighDateTime; } FILETIME;
typedef struct { WORD wYear, wMonth, wDayOfWeek, wDay, wHour, wMinute, wSecond, wMilliseconds; } SYSTEMTIME;

typedef struct
{
	DWORD cb;
	wchar_t* lpReserved;
	wchar_t* lpDesktop;
	wchar_t* lpTitle;
	DWORD dwX, dwY, dwXSize, dwYSize, dwXCountChars, dwYCountChars, dwFillAttribute, dwFlags;
	WORD wShowWindow;
	WORD cbReserved2;
	BYTE* lpReserved2;
	HANDLE hStdInput, hStdOutput, hStdError;
} STARTUPINFOW;

typedef struct { DWORD LowPart; LONG HighPart; } LUID;
typedef struct { LUID Luid; DWORD Attributes; } LUID_AND_ATTRIBUTES;
typedef struct { DWORD PrivilegeCount; LUID_AND_ATTRIBUTES Privileges[1]; } TOKEN_PRIVILEGES;

typedef struct
{
	DWORD dwOSVersionInfoSize;
	DWORD dwMajorVersion;
	DWORD dwMinorVersion;
	DWORD dwBuildNumber;
	DWORD dwPlatformId;
	wchar_t szCSDVersion[128];
	WORD wServicePackMajor;
	WORD wServicePackMinor;
	WORD wSuiteMask;
	BYTE wProductType;
	BYTE wReserved;
} OSVERSIONINFOEXW;

typedef struct
{
	ULONGLONG StartAddressOfRawData;
	ULONGLONG EndAddressOfRawData;
	ULONGLONG AddressOfIndex;
	ULONGLONG AddressOfCallBacks;
	DWORD SizeOfZeroFill;
	DWORD Characteristics;
} IMAGE_TLS_DIRECTORY64;

typedef void (*PIMAGE_TLS_CALLBACK)(PVOID dllHandle, DWORD reason, PVOID reserved);
typedef DWORD (*LPTHREAD_START_ROUTINE)(PVOID parameter);
typedef void (*PFLS_CALLBACK_FUNCTION)(PVOID data);
typedef void (*_PVFV)(void);
typedef int (*_PIFV)(void);
typedef struct { _PVFV* _first; _PVFV* _last; _PVFV* _end; } _onexit_table_t;

#define DLL_PROCESS_DETACH 0
#define DLL_PROCESS_ATTACH 1
#define DLL_THREAD_ATTACH 2
#define DLL_THREAD_DETACH 3

#define WAIT_OBJECT_0 0
#define WAIT_TIMEOUT 258
#define WAIT_FAILED 0xFFFFFFFF
#define CREATE_SUSPENDED 0x4
#define TLS_OUT_OF_INDEXES 0xFFFFFFFF
#define FLS_OUT_OF_INDEXES 0xFFFFFFFF

#define ERROR_SUCCESS 0
#define ERROR_INVALID_HANDLE 6
#define ERROR_NOT_ENOUGH_MEMORY 8
#define ERROR_NOT_SUPPORTED 50
#define ERROR_INVALID_PARAMETER 87
#define ERROR_CALL_NOT_IMPLEMENTED 120
#define ERROR_INSUFFICIENT_BUFFER 122
#define ERROR_MOD_NOT_FOUND 126
#define ERROR_PROC_NOT_FOUND 127
#define ERROR_ENVVAR_NOT_FOUND 203
#define ERROR_MR_MID_NOT_FOUND 317
#define ERROR_NO_UNICODE_TRANSLATION 1113
#define ERROR_TIMEOUT 1460

#define FILE_TYPE_UNKNOWN 0
#define FILE_TYPE_CHAR 2
#define FILE_TYPE_PIPE 3
#define STD_INPUT_HANDLE ((DWORD)-10)
#define STD_OUTPUT_HANDLE ((DWORD)-11)
#define STD_ERROR_HANDLE ((DWORD)-12)

// Every Win32 function the layer provides; imports.c turns this into the __imp_ table and GetProcAddress's name table
#define PHX_WIN32_FUNCTIONS(X) \
	X(AddVectoredExceptionHandler) X(AdjustTokenPrivileges) X(BCryptGenRandom) X(CloseHandle) X(CoGetApartmentType) X(CoInitializeEx) X(CoUninitialize) X(CoWaitForMultipleHandles) \
	X(CreateEventExW) X(CreateEventW) X(CreateMemoryResourceNotification) X(CreateThread) X(DebugBreak) X(DeleteCriticalSection) X(DeregisterEventSource) X(DuplicateHandle) \
	X(EncodePointer) X(EnterCriticalSection) X(ExitProcess) X(FlsAlloc) X(FlsFree) X(FlsGetValue) X(FlsSetValue) X(FlushInstructionCache) X(FlushProcessWriteBuffers) X(FormatMessageW) X(FreeLibrary) \
	X(GetCommandLineW) X(GetConsoleMode) X(GetConsoleOutputCP) X(GetCurrentProcess) X(GetCurrentProcessId) X(GetCurrentProcessorNumber) X(GetCurrentProcessorNumberEx) X(GetCurrentThread) X(GetCurrentThreadId) \
	X(GetEnabledXStateFeatures) X(GetEnvironmentVariableW) X(GetFileType) X(GetLargePageMinimum) X(GetLastError) X(GetLogicalProcessorInformation) X(GetLogicalProcessorInformationEx) \
	X(GetModuleFileNameW) X(GetModuleHandleExW) X(GetModuleHandleW) X(GetNumaHighestNodeNumber) X(GetNumaNodeProcessorMaskEx) X(GetNumaProcessorNodeEx) X(GetProcAddress) \
	X(GetProcessAffinityMask) X(GetProcessGroupAffinity) X(GetProcessHeap) X(GetStartupInfoW) X(GetStdHandle) X(GetSystemInfo) X(GetSystemTimeAsFileTime) X(GetThreadContext) X(GetThreadIdealProcessorEx) \
	X(GetThreadPriority) X(GetTickCount64) X(GetWriteWatch) X(GlobalMemoryStatusEx) X(HeapAlloc) X(HeapCreate) X(HeapDestroy) X(HeapFree) X(HeapReAlloc) X(HeapSize) X(InitializeContext) X(InitializeCriticalSection) X(InitializeCriticalSectionAndSpinCount) \
	X(InitializeCriticalSectionEx) X(InterlockedFlushSList) X(InterlockedPushEntrySList) X(IsDebuggerPresent) X(IsProcessInJob) X(IsProcessorFeaturePresent) X(K32GetProcessMemoryInfo) \
	X(LeaveCriticalSection) X(LoadLibraryExW) X(LocalFree) X(LocateXStateFeature) X(LookupPrivilegeValueW) X(MultiByteToWideChar) X(OpenProcessToken) X(QueryInformationJobObject) \
	X(QueryPerformanceCounter) X(QueryPerformanceFrequency) X(RaiseException) X(RaiseFailFastException) X(ReadConsoleW) X(ReadFile) X(RegisterEventSourceW) X(ReportEventW) X(ResetEvent) X(ResetWriteWatch) \
	X(ResumeThread) X(RtlCaptureContext) X(RtlCaptureStackBackTrace) X(RtlLookupFunctionEntry) X(RtlPcToFileHeader) X(RtlRestoreContext) X(RtlUnwindEx) X(RtlVirtualUnwind) \
	X(SetEvent) X(SetLastError) X(SetThreadAffinityMask) X(SetThreadContext) X(SetThreadGroupAffinity) X(SetThreadIdealProcessorEx) X(SetThreadPriority) X(SetUnhandledExceptionFilter) \
	X(SetThreadErrorMode) X(SetXStateFeaturesMask) X(Sleep) X(SleepEx) X(SuspendThread) X(SwitchToThread) X(SystemTimeToFileTime) X(TerminateProcess) X(TlsAlloc) X(TlsFree) X(TlsGetValue) X(TlsSetValue) X(TryEnterCriticalSection) X(TzSpecificLocalTimeToSystemTime) \
	X(UnhandledExceptionFilter) X(VerSetConditionMask) X(VerifyVersionInfoW) X(VirtualAlloc) X(VirtualAllocExNuma) X(VirtualFree) X(VirtualProtect) X(VirtualQuery) X(VirtualUnlock) \
	X(WaitForMultipleObjectsEx) X(WaitForSingleObject) X(WaitForSingleObjectEx) X(WideCharToMultiByte) X(WriteConsoleW) X(WriteFile)

// The prototypes, in the order of the list above
PVOID AddVectoredExceptionHandler(ULONG first, PVECTORED_EXCEPTION_HANDLER handler);
BOOL AdjustTokenPrivileges(HANDLE token, BOOL disableAll, TOKEN_PRIVILEGES* newState, DWORD bufferLength, TOKEN_PRIVILEGES* previousState, DWORD* returnLength);
LONG BCryptGenRandom(PVOID algorithm, BYTE* buffer, ULONG count, ULONG flags);
BOOL CloseHandle(HANDLE handle);
HRESULT CoGetApartmentType(int* type, int* qualifier);
HRESULT CoInitializeEx(PVOID reserved, DWORD model);
void CoUninitialize(void);
HRESULT CoWaitForMultipleHandles(DWORD flags, DWORD timeout, ULONG count, HANDLE* handles, DWORD* index);
HANDLE CreateEventExW(PVOID attributes, const wchar_t* name, DWORD flags, DWORD access);
HANDLE CreateEventW(PVOID attributes, BOOL manualReset, BOOL initialState, const wchar_t* name);
HANDLE CreateMemoryResourceNotification(int type);
HANDLE CreateThread(PVOID attributes, SIZE_T stackSize, LPTHREAD_START_ROUTINE start, PVOID parameter, DWORD flags, DWORD* threadId);
void DebugBreak(void);
void DeleteCriticalSection(CRITICAL_SECTION* section);
BOOL DeregisterEventSource(HANDLE source);
BOOL DuplicateHandle(HANDLE sourceProcess, HANDLE source, HANDLE targetProcess, HANDLE* target, DWORD access, BOOL inherit, DWORD options);
PVOID EncodePointer(PVOID pointer);
void EnterCriticalSection(CRITICAL_SECTION* section);
__declspec(noreturn) void ExitProcess(DWORD exitCode);
DWORD FlsAlloc(PFLS_CALLBACK_FUNCTION callback);
BOOL FlsFree(DWORD index);
PVOID FlsGetValue(DWORD index);
BOOL FlsSetValue(DWORD index, PVOID value);
BOOL FlushInstructionCache(HANDLE process, const void* address, SIZE_T size);
void FlushProcessWriteBuffers(void);
DWORD FormatMessageW(DWORD flags, const void* source, DWORD messageId, DWORD languageId, wchar_t* buffer, DWORD size, va_list* arguments);
BOOL FreeLibrary(HMODULE module);
wchar_t* GetCommandLineW(void);
BOOL GetConsoleMode(HANDLE console, DWORD* mode);
DWORD GetConsoleOutputCP(void);
HANDLE GetCurrentProcess(void);
DWORD GetCurrentProcessId(void);
DWORD GetCurrentProcessorNumber(void);
void GetCurrentProcessorNumberEx(PROCESSOR_NUMBER* number);
HANDLE GetCurrentThread(void);
DWORD GetCurrentThreadId(void);
DWORD64 GetEnabledXStateFeatures(void);
DWORD GetEnvironmentVariableW(const wchar_t* name, wchar_t* buffer, DWORD size);
DWORD GetFileType(HANDLE file);
SIZE_T GetLargePageMinimum(void);
DWORD GetLastError(void);
BOOL GetLogicalProcessorInformation(SYSTEM_LOGICAL_PROCESSOR_INFORMATION* buffer, DWORD* length);
BOOL GetLogicalProcessorInformationEx(DWORD relationship, SYSTEM_LOGICAL_PROCESSOR_INFORMATION_EX* buffer, DWORD* length);
DWORD GetModuleFileNameW(HMODULE module, wchar_t* buffer, DWORD size);
BOOL GetModuleHandleExW(DWORD flags, const wchar_t* name, HMODULE* module);
HMODULE GetModuleHandleW(const wchar_t* name);
BOOL GetNumaHighestNodeNumber(ULONG* number);
BOOL GetNumaNodeProcessorMaskEx(WORD node, GROUP_AFFINITY* mask);
BOOL GetNumaProcessorNodeEx(PROCESSOR_NUMBER* processor, WORD* node);
PVOID GetProcAddress(HMODULE module, const char* name);
BOOL GetProcessAffinityMask(HANDLE process, ULONG_PTR* processMask, ULONG_PTR* systemMask);
BOOL GetProcessGroupAffinity(HANDLE process, WORD* count, WORD* groups);
void GetStartupInfoW(STARTUPINFOW* info);
HANDLE GetStdHandle(DWORD which);
void GetSystemInfo(SYSTEM_INFO* info);
void GetSystemTimeAsFileTime(FILETIME* time);
BOOL GetThreadContext(HANDLE thread, CONTEXT* context);
BOOL GetThreadIdealProcessorEx(HANDLE thread, PROCESSOR_NUMBER* number);
int GetThreadPriority(HANDLE thread);
ULONGLONG GetTickCount64(void);
DWORD GetWriteWatch(DWORD flags, PVOID base, SIZE_T size, PVOID* addresses, ULONG_PTR* count, DWORD* granularity);
BOOL GlobalMemoryStatusEx(MEMORYSTATUSEX* status);
BOOL InitializeContext(PVOID buffer, DWORD flags, CONTEXT** context, DWORD* length);
void InitializeCriticalSection(CRITICAL_SECTION* section);
BOOL InitializeCriticalSectionAndSpinCount(CRITICAL_SECTION* section, DWORD spinCount);
BOOL InitializeCriticalSectionEx(CRITICAL_SECTION* section, DWORD spinCount, DWORD flags);
SLIST_ENTRY* InterlockedFlushSList(SLIST_HEADER* head);
SLIST_ENTRY* InterlockedPushEntrySList(SLIST_HEADER* head, SLIST_ENTRY* entry);
BOOL IsDebuggerPresent(void);
BOOL IsProcessInJob(HANDLE process, HANDLE job, BOOL* result);
BOOL IsProcessorFeaturePresent(DWORD feature);
BOOL K32GetProcessMemoryInfo(HANDLE process, PROCESS_MEMORY_COUNTERS* counters, DWORD size);
void LeaveCriticalSection(CRITICAL_SECTION* section);
HMODULE LoadLibraryExW(const wchar_t* name, HANDLE file, DWORD flags);
HANDLE LocalFree(HANDLE memory);
PVOID LocateXStateFeature(CONTEXT* context, DWORD feature, DWORD* length);
BOOL LookupPrivilegeValueW(const wchar_t* system, const wchar_t* name, LUID* luid);
int MultiByteToWideChar(DWORD codePage, DWORD flags, const char* bytes, int byteCount, wchar_t* chars, int charCount);
BOOL OpenProcessToken(HANDLE process, DWORD access, HANDLE* token);
BOOL QueryInformationJobObject(HANDLE job, int information, PVOID buffer, DWORD length, DWORD* returned);
BOOL QueryPerformanceCounter(LONGLONG* counter);
BOOL QueryPerformanceFrequency(LONGLONG* frequency);
void RaiseException(DWORD code, DWORD flags, DWORD count, const ULONG_PTR* arguments);
void RaiseFailFastException(EXCEPTION_RECORD* record, CONTEXT* context, DWORD flags);
HANDLE RegisterEventSourceW(const wchar_t* server, const wchar_t* source);
BOOL ReportEventW(HANDLE log, WORD type, WORD category, DWORD eventId, PVOID sid, WORD stringCount, DWORD dataSize, const wchar_t** strings, PVOID data);
BOOL ResetEvent(HANDLE event);
DWORD ResetWriteWatch(PVOID base, SIZE_T size);
DWORD ResumeThread(HANDLE thread);
void RtlCaptureContext(CONTEXT* context);
WORD RtlCaptureStackBackTrace(DWORD skip, DWORD count, PVOID* frames, DWORD* hash);
RUNTIME_FUNCTION* RtlLookupFunctionEntry(DWORD64 pc, DWORD64* imageBase, PVOID historyTable);
PVOID RtlPcToFileHeader(PVOID pc, PVOID* base);
void RtlRestoreContext(CONTEXT* context, EXCEPTION_RECORD* record);
void RtlUnwindEx(PVOID targetFrame, PVOID targetIp, EXCEPTION_RECORD* record, PVOID returnValue, CONTEXT* context, PVOID historyTable);
PEXCEPTION_ROUTINE RtlVirtualUnwind(DWORD handlerType, DWORD64 imageBase, DWORD64 pc, RUNTIME_FUNCTION* function, CONTEXT* context, PVOID* handlerData, DWORD64* establisherFrame, KNONVOLATILE_CONTEXT_POINTERS* pointers);
BOOL SetEvent(HANDLE event);
void SetLastError(DWORD error);
ULONG_PTR SetThreadAffinityMask(HANDLE thread, ULONG_PTR mask);
BOOL SetThreadContext(HANDLE thread, const CONTEXT* context);
BOOL SetThreadGroupAffinity(HANDLE thread, const GROUP_AFFINITY* affinity, GROUP_AFFINITY* previous);
BOOL SetThreadIdealProcessorEx(HANDLE thread, PROCESSOR_NUMBER* ideal, PROCESSOR_NUMBER* previous);
BOOL SetThreadPriority(HANDLE thread, int priority);
PTOP_LEVEL_EXCEPTION_FILTER SetUnhandledExceptionFilter(PTOP_LEVEL_EXCEPTION_FILTER filter);
BOOL SetXStateFeaturesMask(CONTEXT* context, DWORD64 mask);
BOOL SetThreadErrorMode(DWORD newMode, DWORD* oldMode);
BOOL SystemTimeToFileTime(const SYSTEMTIME* systemTime, FILETIME* fileTime);
BOOL TzSpecificLocalTimeToSystemTime(PVOID timeZone, const SYSTEMTIME* localTime, SYSTEMTIME* universalTime);
void Sleep(DWORD milliseconds);
DWORD SleepEx(DWORD milliseconds, BOOL alertable);
DWORD SuspendThread(HANDLE thread);
BOOL SwitchToThread(void);
BOOL TerminateProcess(HANDLE process, DWORD exitCode);
DWORD TlsAlloc(void);
BOOL TlsFree(DWORD index);
PVOID TlsGetValue(DWORD index);
BOOL TlsSetValue(DWORD index, PVOID value);
BOOL TryEnterCriticalSection(CRITICAL_SECTION* section);
LONG UnhandledExceptionFilter(EXCEPTION_POINTERS* pointers);
ULONGLONG VerSetConditionMask(ULONGLONG mask, DWORD type, BYTE condition);
BOOL VerifyVersionInfoW(OSVERSIONINFOEXW* info, DWORD typeMask, DWORDLONG conditionMask);
PVOID VirtualAlloc(PVOID address, SIZE_T size, DWORD type, DWORD protect);
PVOID VirtualAllocExNuma(HANDLE process, PVOID address, SIZE_T size, DWORD type, DWORD protect, DWORD node);
BOOL VirtualFree(PVOID address, SIZE_T size, DWORD type);
BOOL VirtualProtect(PVOID address, SIZE_T size, DWORD protect, DWORD* oldProtect);
SIZE_T VirtualQuery(const void* address, MEMORY_BASIC_INFORMATION* buffer, SIZE_T length);
BOOL VirtualUnlock(PVOID address, SIZE_T size);
DWORD WaitForMultipleObjectsEx(DWORD count, const HANDLE* handles, BOOL waitAll, DWORD milliseconds, BOOL alertable);
DWORD WaitForSingleObject(HANDLE handle, DWORD milliseconds);
DWORD WaitForSingleObjectEx(HANDLE handle, DWORD milliseconds, BOOL alertable);
int WideCharToMultiByte(DWORD codePage, DWORD flags, const wchar_t* chars, int charCount, char* bytes, int byteCount, const char* defaultChar, BOOL* usedDefault);
BOOL WriteConsoleW(HANDLE console, const void* buffer, DWORD count, DWORD* written, PVOID reserved);
BOOL WriteFile(HANDLE file, const void* buffer, DWORD count, DWORD* written, PVOID overlapped);
BOOL ReadFile(HANDLE file, void* buffer, DWORD count, DWORD* read, PVOID overlapped);
BOOL ReadConsoleW(HANDLE console, void* buffer, DWORD count, DWORD* read, PVOID control);
HANDLE GetProcessHeap(void);
HANDLE HeapCreate(DWORD options, SIZE_T initialSize, SIZE_T maximumSize);
BOOL HeapDestroy(HANDLE heap);
PVOID HeapAlloc(HANDLE heap, DWORD flags, SIZE_T size);
BOOL HeapFree(HANDLE heap, DWORD flags, PVOID block);
SIZE_T HeapSize(HANDLE heap, DWORD flags, const void* block);
PVOID HeapReAlloc(HANDLE heap, DWORD flags, PVOID block, SIZE_T size);

// C runtime pieces libcmt and the runtime import from the UCRT (crt.c)
void* malloc(size_t size);
void* calloc(size_t count, size_t size);
void free(void* block);
size_t strlen(const char* text);
int strcmp(const char* left, const char* right);
int strncmp(const char* left, const char* right, size_t count);
__declspec(noreturn) void abort(void);
int PhxFormatV(char* buffer, size_t capacity, const char* format, va_list arguments);

// The kernel interface (Abi/Syscalls.cs): rax = number, then r10 rdx r8 r9 and two stack slots; negative results are errors
enum PhxSyscallNumber
{
	PhxSysGetTickCount = 0x100,
	PhxSysDebugPrint = 0x101,
	PhxSysGetSystemTime = 0x102,
	PhxSysQueryTime = 0x103,
	PhxSysGetMemoryStatus = 0x104,
	PhxSysExitProcess = 0x200,
	PhxSysGetProcessId = 0x202,
	PhxSysGetProcesses = 0x204,
	PhxSysSetExceptionDispatcher = 0x205,
	PhxSysGetExitCode = 0x206,
	PhxSysSleep = 0x300,
	PhxSysYield = 0x301,
	PhxSysCreateThread = 0x302,
	PhxSysExitThread = 0x303,
	PhxSysOpenCurrentThread = 0x304,
	PhxSysSuspendThread = 0x305,
	PhxSysResumeThread = 0x306,
	PhxSysGetThreadContext = 0x307,
	PhxSysSetThreadContext = 0x308,
	PhxSysGetThreadId = 0x309,
	PhxSysReadFile = 0x401,
	PhxSysWriteFile = 0x404,
	PhxSysCloseHandle = 0x500,
	PhxSysWaitForObject = 0x600,
	PhxSysCreateEvent = 0x601,
	PhxSysSetEvent = 0x602,
	PhxSysResetEvent = 0x603,
	PhxSysWaitForMultiple = 0x604,
	PhxSysVirtualAlloc = 0x700,
	PhxSysVirtualFree = 0x701,
	PhxSysVirtualProtect = 0x702,
	PhxSysVirtualQuery = 0x703,
};

#define PhxErrorTimeout (-7)
#define PhxErrorClosed (-9)
#define PhxThreadSuspended 0x4
#define PhxWaitForever ((ULONG64)-1)

LONG64 PhxSyscall(ULONG64 number, ULONG64 a0, ULONG64 a1, ULONG64 a2, ULONG64 a3, ULONG64 a4, ULONG64 a5);
#define PhxSyscall0(number) PhxSyscall(number, 0, 0, 0, 0, 0, 0)
#define PhxSyscall1(number, a0) PhxSyscall(number, (ULONG64)(a0), 0, 0, 0, 0, 0)
#define PhxSyscall2(number, a0, a1) PhxSyscall(number, (ULONG64)(a0), (ULONG64)(a1), 0, 0, 0, 0)
#define PhxSyscall3(number, a0, a1, a2) PhxSyscall(number, (ULONG64)(a0), (ULONG64)(a1), (ULONG64)(a2), 0, 0, 0)
#define PhxSyscall4(number, a0, a1, a2, a3) PhxSyscall(number, (ULONG64)(a0), (ULONG64)(a1), (ULONG64)(a2), (ULONG64)(a3), 0, 0)

// The kernel's SystemTime, MemoryStatus and ProcessEntry (Abi/Syscalls.cs)
typedef struct { WORD Year, Month, Day, Hour, Minute, Second, Milliseconds, Reserved; } PhxSystemTime;
typedef struct { ULONG64 TotalPhysical; ULONG64 AvailablePhysical; } PhxMemoryStatus;
typedef struct { DWORD Id; DWORD Threads; ULONG64 VirtualSize; char Name[32]; } PhxProcessEntry;

// The thread block behind gs: Windows' offsets for what native code reads directly (Abi/Windows.cs Teb), the layer's own fields from 0x100
typedef struct PhxTeb
{
	BYTE Reserved0[0x08];
	PVOID StackBase;
	PVOID StackLimit;
	BYTE Reserved1[0x30 - 0x18];
	struct PhxTeb* Self;
	BYTE Reserved2[0x40 - 0x38];
	ULONG64 ProcessId;
	ULONG64 ThreadId;
	BYTE Reserved3[0x58 - 0x50];
	PVOID* ThreadLocalStoragePointer;
	BYTE* ProcessBlock;
	DWORD LastError;
	BYTE Reserved4[0x100 - 0x6C];
	PVOID TlsArray[4];
	PVOID TlsAllocation;
	LPTHREAD_START_ROUTINE StartRoutine;
	PVOID StartParameter;
	LONG ComInitialized;
	LONG Reserved5;
	BYTE Reserved6[0x200 - 0x140];
	PVOID FlsValues[128];
	BYTE Reserved7[0x1478 - 0x600];
	PVOID DeallocationStack;
	PVOID TlsSlots[64];
} PhxTeb;

#define PhxOffsetOf(type, field) ((size_t)&(((type*)0)->field))
typedef char PhxTebCheck1[PhxOffsetOf(PhxTeb, ThreadLocalStoragePointer) == 0x58 ? 1 : -1];
typedef char PhxTebCheck2[PhxOffsetOf(PhxTeb, FlsValues) == 0x200 ? 1 : -1];
typedef char PhxTebCheck3[PhxOffsetOf(PhxTeb, TlsSlots) == 0x1480 ? 1 : -1];
typedef char PhxContextCheck[sizeof(CONTEXT) == 0x4D0 ? 1 : -1];
typedef char PhxRecordCheck[sizeof(EXCEPTION_RECORD) == 0x98 ? 1 : -1];

// The process block TEB+0x60 points at (Abi/Windows.cs Peb)
typedef struct
{
	BYTE Reserved0[0x10];
	PVOID ImageBase;
	BYTE Reserved1[0x400 - 0x18];
	ULONG64 StdInput;
	ULONG64 StdOutput;
	ULONG64 StdError;
	ULONG64 CommandLineLength;
	ULONG64 ImageNameLength;
	BYTE Reserved2[0x440 - 0x428];
	char ImageName[0xC0];
	char CommandLine[0xB00];
} PhxPeb;

static __forceinline PhxTeb* PhxCurrentTeb(void)
{
	return (PhxTeb*)__readgsqword(0x30);
}

// Pseudo handles: Windows' values for the current process and thread, and one for output that goes to the kernel log
#define PhxCurrentProcessHandle ((HANDLE)(LONG_PTR)-1)
#define PhxCurrentThreadHandle ((HANDLE)(LONG_PTR)-2)
#define PhxLogHandle ((HANDLE)(ULONG_PTR)0x4C4F47)
#define PhxImageModule ((HMODULE)g_phx.ImageBase)
#define PhxSystemModule ((HMODULE)&g_phx)

// Process-wide state (start.c)
typedef struct
{
	BYTE* ImageBase;
	SIZE_T ImageSize;
	const RUNTIME_FUNCTION* Functions;
	DWORD FunctionCount;
	PhxPeb* Peb;
	HANDLE StdHandles[3];
	ULONG64 FileTimeBase;
	ULONG64 Argument;
	char ImageName[0xC0 + 1];
} PhxProcess;

extern PhxProcess g_phx;

// A spinlock for the layer's own tables; the holder may be preempted, so waiters yield
typedef struct { volatile long State; } PhxLock;
void PhxLockAcquire(PhxLock* lock);
void PhxLockRelease(PhxLock* lock);

// Threads (thread.c)
void PhxAttachThread(PhxTeb* teb);
void PhxDetachThread(PhxTeb* teb);

// The kernel log, line buffered (io.c)
void PhxLogWrite(const char* bytes, size_t count);
void PhxLogFlush(void);
void PhxLogFormat(const char* format, ...);

// Exceptions (exception.c)
void PhxInitializeExceptions(void);
__declspec(noreturn) void PhxFatal(DWORD exitCode, const char* format, ...);
__declspec(noreturn) void PhxExitProcess(int exitCode);

// Time, the heap and the CPU (misc.c, memory.c)
void PhxInitializeTime(void);
ULONG64 PhxReferenceTime(void);
void PhxInitializeHeap(void);
int PhxCpuidCacheInfo(CACHE_DESCRIPTOR* caches, int max);

// The C runtime's exit tables (crt.c)
void PhxRunExitTables(void);
