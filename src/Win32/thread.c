// Threads and synchronization: thread start and exit with the image's TLS, Tls and Fls slots, events, waits, critical sections, suspension and contexts, and the one-CPU topology
#include "win32.h"

// libcmt's copy of the image's TLS directory; the kernel doesn't process it, so the layer gives every thread its block
extern const IMAGE_TLS_DIRECTORY64 _tls_used;

static PhxLock s_threadLock;
static ULONG64 s_tlsUsed;
static ULONG64 s_flsUsed[2];
static PFLS_CALLBACK_FUNCTION s_flsCallbacks[128];

void PhxLockAcquire(PhxLock* lock)
{
	int spins = 0;
	while (_InterlockedCompareExchange(&lock->State, 1, 0) != 0)
	{
		if (++spins > 64)
		{
			PhxSyscall0(PhxSysYield);
			spins = 0;
		}
	}
}

void PhxLockRelease(PhxLock* lock)
{
	_InterlockedExchange(&lock->State, 0);
}

static void PhxRunTlsCallbacks(DWORD reason)
{
	PIMAGE_TLS_CALLBACK* callback = (PIMAGE_TLS_CALLBACK*)_tls_used.AddressOfCallBacks;
	for (; callback != NULL && *callback != NULL; callback++)
		(*callback)(g_phx.ImageBase, reason, NULL);
}

// A fresh copy of the .tls image for this thread, behind gs:[0x58] where ILC's thread statics and the runtime's __declspec(thread) data look for it
void PhxAttachThread(PhxTeb* teb)
{
	ULONG64 imageSize = _tls_used.EndAddressOfRawData - _tls_used.StartAddressOfRawData;
	size_t size = (size_t)imageSize + _tls_used.SizeOfZeroFill;
	DWORD alignCode = (_tls_used.Characteristics >> 20) & 0xF;
	size_t alignment = alignCode > 4 ? (size_t)1 << (alignCode - 1) : 16;
	BYTE* allocation = (BYTE*)malloc(size + alignment);
	BYTE* block = (BYTE*)(((ULONG_PTR)allocation + alignment - 1) & ~(ULONG_PTR)(alignment - 1));
	memcpy(block, (const void*)_tls_used.StartAddressOfRawData, (size_t)imageSize);
	memset(block + imageSize, 0, _tls_used.SizeOfZeroFill);

	*(DWORD*)_tls_used.AddressOfIndex = 0;
	teb->TlsAllocation = allocation;
	teb->TlsArray[0] = block;
	teb->ThreadLocalStoragePointer = teb->TlsArray;
	PhxRunTlsCallbacks(DLL_THREAD_ATTACH);
}

// Thread exit as Windows orders it: TLS callbacks, then the Fls callbacks (the runtime's detaches its Thread here), then the block they may have read goes away
void PhxDetachThread(PhxTeb* teb)
{
	PhxRunTlsCallbacks(DLL_THREAD_DETACH);
	for (int round = 0; round < 4; round++)
	{
		BOOL any = FALSE;
		for (DWORD index = 0; index < 128; index++)
		{
			PVOID value = teb->FlsValues[index];
			PFLS_CALLBACK_FUNCTION callback = s_flsCallbacks[index];
			if (value == NULL || callback == NULL)
				continue;
			teb->FlsValues[index] = NULL;
			callback(value);
			any = TRUE;
		}
		if (!any)
			break;
	}
	free(teb->TlsAllocation);
	teb->TlsAllocation = NULL;
}

typedef struct { LPTHREAD_START_ROUTINE StartRoutine; PVOID StartParameter; } PhxThreadStart;

// Every thread the layer creates starts here, on its own stack with rcx = the start block CreateThread allocated, which is this thread's to free
static void PhxThreadEntry(ULONG64 argument)
{
	PhxTeb* teb = PhxCurrentTeb();
	PhxThreadStart* start = (PhxThreadStart*)argument;
	LPTHREAD_START_ROUTINE routine = start->StartRoutine;
	PVOID parameter = start->StartParameter;
	free(start);
	PhxAttachThread(teb);
	DWORD code = routine(parameter);
	PhxDetachThread(teb);
	PhxSyscall1(PhxSysExitThread, code);
	for (;;)
	{
	}
}

HANDLE CreateThread(PVOID attributes, SIZE_T stackSize, LPTHREAD_START_ROUTINE start, PVOID parameter, DWORD flags, DWORD* threadId)
{
	(void)attributes;
	PhxThreadStart* block = (PhxThreadStart*)malloc(sizeof(PhxThreadStart));
	block->StartRoutine = start;
	block->StartParameter = parameter;
	ULONG64 kernelFlags = (flags & CREATE_SUSPENDED) != 0 ? PhxThreadSuspended : 0;
	LONG64 handle = PhxSyscall4(PhxSysCreateThread, (ULONG64)&PhxThreadEntry, (ULONG64)block, stackSize, kernelFlags);
	if (handle <= 0)
	{
		free(block);
		SetLastError(ERROR_NOT_ENOUGH_MEMORY);
		return NULL;
	}
	if (threadId != NULL)
		*threadId = (DWORD)PhxSyscall1(PhxSysGetThreadId, handle);
	return (HANDLE)handle;
}

HANDLE GetCurrentProcess(void)
{
	return PhxCurrentProcessHandle;
}

HANDLE GetCurrentThread(void)
{
	return PhxCurrentThreadHandle;
}

DWORD GetCurrentProcessId(void)
{
	return (DWORD)PhxCurrentTeb()->ProcessId;
}

DWORD GetCurrentThreadId(void)
{
	return (DWORD)PhxCurrentTeb()->ThreadId;
}

DWORD GetLastError(void)
{
	return PhxCurrentTeb()->LastError;
}

void SetLastError(DWORD error)
{
	PhxCurrentTeb()->LastError = error;
}

// The runtime duplicates GetCurrentThread() to get a handle it can suspend; the kernel has no general duplication
BOOL DuplicateHandle(HANDLE sourceProcess, HANDLE source, HANDLE targetProcess, HANDLE* target, DWORD access, BOOL inherit, DWORD options)
{
	(void)sourceProcess;
	(void)targetProcess;
	(void)access;
	(void)inherit;
	(void)options;
	if (source == PhxCurrentThreadHandle)
	{
		LONG64 handle = PhxSyscall0(PhxSysOpenCurrentThread);
		if (handle <= 0)
		{
			SetLastError(ERROR_NOT_ENOUGH_MEMORY);
			return FALSE;
		}
		*target = (HANDLE)handle;
		return TRUE;
	}
	if (source == PhxCurrentProcessHandle)
	{
		*target = source;
		return TRUE;
	}
	SetLastError(ERROR_NOT_SUPPORTED);
	return FALSE;
}

BOOL CloseHandle(HANDLE handle)
{
	if (handle == PhxCurrentProcessHandle || handle == PhxCurrentThreadHandle || handle == PhxLogHandle || handle == NULL)
		return TRUE;
	if (PhxSyscall1(PhxSysCloseHandle, handle) < 0)
	{
		SetLastError(ERROR_INVALID_HANDLE);
		return FALSE;
	}
	return TRUE;
}

static ULONG64 PhxTimeout(DWORD milliseconds)
{
	return milliseconds == INFINITE ? PhxWaitForever : milliseconds;
}

void Sleep(DWORD milliseconds)
{
	if (milliseconds == 0)
		PhxSyscall0(PhxSysYield);
	else
		PhxSyscall1(PhxSysSleep, milliseconds);
}

DWORD SleepEx(DWORD milliseconds, BOOL alertable)
{
	(void)alertable;
	Sleep(milliseconds);
	return 0;
}

BOOL SwitchToThread(void)
{
	PhxSyscall0(PhxSysYield);
	return TRUE;
}

DWORD SuspendThread(HANDLE thread)
{
	LONG64 previous = PhxSyscall1(PhxSysSuspendThread, thread);
	if (previous < 0)
	{
		SetLastError(ERROR_INVALID_HANDLE);
		return (DWORD)-1;
	}
	return (DWORD)previous;
}

DWORD ResumeThread(HANDLE thread)
{
	LONG64 previous = PhxSyscall1(PhxSysResumeThread, thread);
	if (previous < 0)
	{
		SetLastError(ERROR_INVALID_HANDLE);
		return (DWORD)-1;
	}
	return (DWORD)previous;
}

BOOL GetThreadContext(HANDLE thread, CONTEXT* context)
{
	if (PhxSyscall2(PhxSysGetThreadContext, thread, context) < 0)
	{
		SetLastError(ERROR_INVALID_HANDLE);
		return FALSE;
	}
	return TRUE;
}

BOOL SetThreadContext(HANDLE thread, const CONTEXT* context)
{
	if (PhxSyscall2(PhxSysSetThreadContext, thread, context) < 0)
	{
		SetLastError(ERROR_INVALID_HANDLE);
		return FALSE;
	}
	return TRUE;
}

// InitializeContext sizes and aligns a CONTEXT buffer; without XSAVE in the kernel there is never extended state to add
BOOL InitializeContext(PVOID buffer, DWORD flags, CONTEXT** context, DWORD* length)
{
	DWORD needed = sizeof(CONTEXT) + 16;
	if (buffer == NULL || *length < needed)
	{
		*length = needed;
		SetLastError(ERROR_INSUFFICIENT_BUFFER);
		return FALSE;
	}
	CONTEXT* aligned = (CONTEXT*)(((ULONG_PTR)buffer + 15) & ~(ULONG_PTR)15);
	memset(aligned, 0, sizeof(CONTEXT));
	aligned->ContextFlags = flags & ~CONTEXT_XSTATE;
	*context = aligned;
	return TRUE;
}

DWORD64 GetEnabledXStateFeatures(void)
{
	return 0x3;
}

BOOL SetXStateFeaturesMask(CONTEXT* context, DWORD64 mask)
{
	(void)context;
	(void)mask;
	return TRUE;
}

PVOID LocateXStateFeature(CONTEXT* context, DWORD feature, DWORD* length)
{
	if (feature == 1)
	{
		if (length != NULL)
			*length = 16 * 16;
		return context->Fx.Xmm;
	}
	if (feature == 0)
	{
		if (length != NULL)
			*length = 512;
		return context->FltSave;
	}
	return NULL;
}

int GetThreadPriority(HANDLE thread)
{
	(void)thread;
	return 0;
}

BOOL SetThreadPriority(HANDLE thread, int priority)
{
	(void)thread;
	(void)priority;
	return TRUE;
}

// Tls slots live in the TEB at Windows' offset; a 64-bit mask says which are taken
DWORD TlsAlloc(void)
{
	PhxLockAcquire(&s_threadLock);
	DWORD index = TLS_OUT_OF_INDEXES;
	for (DWORD i = 0; i < 64; i++)
	{
		if ((s_tlsUsed & (1ULL << i)) == 0)
		{
			s_tlsUsed |= 1ULL << i;
			index = i;
			break;
		}
	}
	PhxLockRelease(&s_threadLock);
	if (index != TLS_OUT_OF_INDEXES)
		PhxCurrentTeb()->TlsSlots[index] = NULL;
	return index;
}

BOOL TlsFree(DWORD index)
{
	if (index >= 64)
		return FALSE;
	PhxLockAcquire(&s_threadLock);
	s_tlsUsed &= ~(1ULL << index);
	PhxLockRelease(&s_threadLock);
	return TRUE;
}

PVOID TlsGetValue(DWORD index)
{
	PhxTeb* teb = PhxCurrentTeb();
	if (index >= 64)
	{
		teb->LastError = ERROR_INVALID_PARAMETER;
		return NULL;
	}
	teb->LastError = ERROR_SUCCESS;
	return teb->TlsSlots[index];
}

BOOL TlsSetValue(DWORD index, PVOID value)
{
	if (index >= 64)
	{
		SetLastError(ERROR_INVALID_PARAMETER);
		return FALSE;
	}
	PhxCurrentTeb()->TlsSlots[index] = value;
	return TRUE;
}

// Fls slots are Tls slots with a callback at thread exit, which the runtime uses to notice a thread leaving
DWORD FlsAlloc(PFLS_CALLBACK_FUNCTION callback)
{
	PhxLockAcquire(&s_threadLock);
	DWORD index = FLS_OUT_OF_INDEXES;
	for (DWORD i = 0; i < 128; i++)
	{
		if ((s_flsUsed[i / 64] & (1ULL << (i % 64))) == 0)
		{
			s_flsUsed[i / 64] |= 1ULL << (i % 64);
			s_flsCallbacks[i] = callback;
			index = i;
			break;
		}
	}
	PhxLockRelease(&s_threadLock);
	if (index != FLS_OUT_OF_INDEXES)
		PhxCurrentTeb()->FlsValues[index] = NULL;
	return index;
}

BOOL FlsFree(DWORD index)
{
	if (index >= 128)
		return FALSE;
	PhxLockAcquire(&s_threadLock);
	s_flsUsed[index / 64] &= ~(1ULL << (index % 64));
	s_flsCallbacks[index] = NULL;
	PhxLockRelease(&s_threadLock);
	return TRUE;
}

PVOID FlsGetValue(DWORD index)
{
	PhxTeb* teb = PhxCurrentTeb();
	if (index >= 128)
	{
		teb->LastError = ERROR_INVALID_PARAMETER;
		return NULL;
	}
	teb->LastError = ERROR_SUCCESS;
	return teb->FlsValues[index];
}

BOOL FlsSetValue(DWORD index, PVOID value)
{
	if (index >= 128)
	{
		SetLastError(ERROR_INVALID_PARAMETER);
		return FALSE;
	}
	PhxCurrentTeb()->FlsValues[index] = value;
	return TRUE;
}

HANDLE CreateEventW(PVOID attributes, BOOL manualReset, BOOL initialState, const wchar_t* name)
{
	(void)attributes;
	(void)name;
	LONG64 handle = PhxSyscall2(PhxSysCreateEvent, manualReset != 0, initialState != 0);
	if (handle <= 0)
	{
		SetLastError(ERROR_NOT_ENOUGH_MEMORY);
		return NULL;
	}
	return (HANDLE)handle;
}

HANDLE CreateEventExW(PVOID attributes, const wchar_t* name, DWORD flags, DWORD access)
{
	(void)access;
	return CreateEventW(attributes, (flags & 1) != 0, (flags & 2) != 0, name);
}

BOOL SetEvent(HANDLE event)
{
	if (PhxSyscall1(PhxSysSetEvent, event) < 0)
	{
		SetLastError(ERROR_INVALID_HANDLE);
		return FALSE;
	}
	return TRUE;
}

BOOL ResetEvent(HANDLE event)
{
	if (PhxSyscall1(PhxSysResetEvent, event) < 0)
	{
		SetLastError(ERROR_INVALID_HANDLE);
		return FALSE;
	}
	return TRUE;
}

// Low-memory notifications never fire: a manual-reset event nobody sets
HANDLE CreateMemoryResourceNotification(int type)
{
	(void)type;
	return CreateEventW(NULL, TRUE, FALSE, NULL);
}

static BOOL PhxIsPseudoHandle(HANDLE handle)
{
	return handle == PhxCurrentProcessHandle || handle == PhxCurrentThreadHandle || handle == PhxLogHandle || handle == NULL;
}

DWORD WaitForSingleObject(HANDLE handle, DWORD milliseconds)
{
	if (PhxIsPseudoHandle(handle))
	{
		SetLastError(ERROR_INVALID_HANDLE);
		return WAIT_FAILED;
	}
	LONG64 result = PhxSyscall2(PhxSysWaitForObject, handle, PhxTimeout(milliseconds));
	if (result == 0)
		return WAIT_OBJECT_0;
	if (result == PhxErrorTimeout)
		return WAIT_TIMEOUT;
	SetLastError(ERROR_INVALID_HANDLE);
	return WAIT_FAILED;
}

DWORD WaitForSingleObjectEx(HANDLE handle, DWORD milliseconds, BOOL alertable)
{
	(void)alertable;
	return WaitForSingleObject(handle, milliseconds);
}

// The kernel waits for any of up to 16 objects; waiting for all is a wait on each in turn
DWORD WaitForMultipleObjectsEx(DWORD count, const HANDLE* handles, BOOL waitAll, DWORD milliseconds, BOOL alertable)
{
	(void)alertable;
	if (count == 0 || count > 16)
	{
		SetLastError(ERROR_INVALID_PARAMETER);
		return WAIT_FAILED;
	}
	for (DWORD i = 0; i < count; i++)
	{
		if (PhxIsPseudoHandle(handles[i]))
		{
			SetLastError(ERROR_INVALID_HANDLE);
			return WAIT_FAILED;
		}
	}
	if (count == 1 || waitAll)
	{
		for (DWORD i = 0; i < count; i++)
		{
			DWORD result = WaitForSingleObject(handles[i], milliseconds);
			if (result != WAIT_OBJECT_0)
				return result;
		}
		return WAIT_OBJECT_0;
	}
	LONG64 result = PhxSyscall3(PhxSysWaitForMultiple, handles, count, PhxTimeout(milliseconds));
	if (result >= 0)
		return WAIT_OBJECT_0 + (DWORD)result;
	if (result == PhxErrorTimeout)
		return WAIT_TIMEOUT;
	SetLastError(ERROR_INVALID_HANDLE);
	return WAIT_FAILED;
}

// Critical sections: an owner and a count, with a kernel event created on first contention so waiters block instead of spinning
void InitializeCriticalSection(CRITICAL_SECTION* section)
{
	memset(section, 0, sizeof(CRITICAL_SECTION));
}

BOOL InitializeCriticalSectionAndSpinCount(CRITICAL_SECTION* section, DWORD spinCount)
{
	InitializeCriticalSection(section);
	section->SpinCount = spinCount;
	return TRUE;
}

BOOL InitializeCriticalSectionEx(CRITICAL_SECTION* section, DWORD spinCount, DWORD flags)
{
	(void)flags;
	return InitializeCriticalSectionAndSpinCount(section, spinCount);
}

void DeleteCriticalSection(CRITICAL_SECTION* section)
{
	if (section->LockSemaphore != NULL)
		CloseHandle(section->LockSemaphore);
	memset(section, 0, sizeof(CRITICAL_SECTION));
}

static HANDLE PhxSectionEvent(CRITICAL_SECTION* section)
{
	HANDLE event = section->LockSemaphore;
	if (event == NULL)
	{
		HANDLE created = CreateEventW(NULL, FALSE, FALSE, NULL);
		event = _InterlockedCompareExchangePointer(&section->LockSemaphore, created, NULL);
		if (event == NULL)
			event = created;
		else
			CloseHandle(created);
	}
	return event;
}

BOOL TryEnterCriticalSection(CRITICAL_SECTION* section)
{
	HANDLE me = (HANDLE)PhxCurrentTeb()->ThreadId;
	if (section->OwningThread == me)
	{
		section->RecursionCount++;
		return TRUE;
	}
	if (_InterlockedCompareExchange(&section->LockCount, 1, 0) != 0)
		return FALSE;
	section->OwningThread = me;
	section->RecursionCount = 1;
	return TRUE;
}

void EnterCriticalSection(CRITICAL_SECTION* section)
{
	HANDLE me = (HANDLE)PhxCurrentTeb()->ThreadId;
	if (section->OwningThread == me)
	{
		section->RecursionCount++;
		return;
	}
	ULONG_PTR spins = section->SpinCount;
	while (_InterlockedCompareExchange(&section->LockCount, 1, 0) != 0)
	{
		if (spins > 0)
		{
			spins--;
			continue;
		}
		// A short timeout covers a wakeup that slipped in before the wait started
		PhxSyscall2(PhxSysWaitForObject, PhxSectionEvent(section), 10);
	}
	section->OwningThread = me;
	section->RecursionCount = 1;
}

void LeaveCriticalSection(CRITICAL_SECTION* section)
{
	if (--section->RecursionCount > 0)
		return;
	section->OwningThread = NULL;
	_InterlockedExchange(&section->LockCount, 0);
	if (section->LockSemaphore != NULL)
		PhxSyscall1(PhxSysSetEvent, section->LockSemaphore);
}

// Interlocked singly linked lists, pushed and flushed only, so the header is just the head pointer and a depth
SLIST_ENTRY* InterlockedPushEntrySList(SLIST_HEADER* head, SLIST_ENTRY* entry)
{
	SLIST_ENTRY* old;
	do
	{
		old = head->Next;
		entry->Next = old;
	} while (_InterlockedCompareExchangePointer((void* volatile*)&head->Next, entry, old) != old);
	_InterlockedExchangeAdd((long volatile*)&head->Depth, 1);
	return old;
}

SLIST_ENTRY* InterlockedFlushSList(SLIST_HEADER* head)
{
	SLIST_ENTRY* old = (SLIST_ENTRY*)_InterlockedExchangePointer((void* volatile*)&head->Next, NULL);
	_InterlockedExchange((long volatile*)&head->Depth, 0);
	return old;
}

// One CPU, one group, one NUMA node; the kernel schedules everything on it
DWORD GetCurrentProcessorNumber(void)
{
	return 0;
}

void GetCurrentProcessorNumberEx(PROCESSOR_NUMBER* number)
{
	number->Group = 0;
	number->Number = 0;
	number->Reserved = 0;
}

BOOL GetProcessAffinityMask(HANDLE process, ULONG_PTR* processMask, ULONG_PTR* systemMask)
{
	(void)process;
	*processMask = 1;
	*systemMask = 1;
	return TRUE;
}

BOOL GetProcessGroupAffinity(HANDLE process, WORD* count, WORD* groups)
{
	(void)process;
	if (*count < 1 || groups == NULL)
	{
		*count = 1;
		SetLastError(ERROR_INSUFFICIENT_BUFFER);
		return FALSE;
	}
	*count = 1;
	groups[0] = 0;
	return TRUE;
}

ULONG_PTR SetThreadAffinityMask(HANDLE thread, ULONG_PTR mask)
{
	(void)thread;
	(void)mask;
	return 1;
}

BOOL SetThreadGroupAffinity(HANDLE thread, const GROUP_AFFINITY* affinity, GROUP_AFFINITY* previous)
{
	(void)thread;
	(void)affinity;
	if (previous != NULL)
	{
		memset(previous, 0, sizeof(GROUP_AFFINITY));
		previous->Mask = 1;
	}
	return TRUE;
}

BOOL SetThreadIdealProcessorEx(HANDLE thread, PROCESSOR_NUMBER* ideal, PROCESSOR_NUMBER* previous)
{
	(void)thread;
	(void)ideal;
	if (previous != NULL)
		GetCurrentProcessorNumberEx(previous);
	return TRUE;
}

BOOL GetThreadIdealProcessorEx(HANDLE thread, PROCESSOR_NUMBER* number)
{
	(void)thread;
	GetCurrentProcessorNumberEx(number);
	return TRUE;
}

BOOL GetNumaHighestNodeNumber(ULONG* number)
{
	*number = 0;
	return TRUE;
}

BOOL GetNumaNodeProcessorMaskEx(WORD node, GROUP_AFFINITY* mask)
{
	memset(mask, 0, sizeof(GROUP_AFFINITY));
	if (node != 0)
	{
		SetLastError(ERROR_INVALID_PARAMETER);
		return FALSE;
	}
	mask->Mask = 1;
	return TRUE;
}

BOOL GetNumaProcessorNodeEx(PROCESSOR_NUMBER* processor, WORD* node)
{
	(void)processor;
	*node = 0;
	return TRUE;
}

static void PhxFillCache(CACHE_DESCRIPTOR* cache, const CACHE_DESCRIPTOR* source)
{
	*cache = *source;
}

// The GC sizes gen0 from the largest cache here, so the entries come from CPUID rather than a guess
BOOL GetLogicalProcessorInformation(SYSTEM_LOGICAL_PROCESSOR_INFORMATION* buffer, DWORD* length)
{
	CACHE_DESCRIPTOR caches[8];
	int cacheCount = PhxCpuidCacheInfo(caches, 8);
	DWORD count = 3 + (DWORD)cacheCount;
	DWORD needed = count * sizeof(SYSTEM_LOGICAL_PROCESSOR_INFORMATION);
	if (buffer == NULL || *length < needed)
	{
		*length = needed;
		SetLastError(ERROR_INSUFFICIENT_BUFFER);
		return FALSE;
	}
	memset(buffer, 0, needed);
	buffer[0].ProcessorMask = 1;
	buffer[0].Relationship = RelationProcessorCore;
	buffer[1].ProcessorMask = 1;
	buffer[1].Relationship = RelationNumaNode;
	buffer[2].ProcessorMask = 1;
	buffer[2].Relationship = RelationProcessorPackage;
	for (int i = 0; i < cacheCount; i++)
	{
		buffer[3 + i].ProcessorMask = 1;
		buffer[3 + i].Relationship = RelationCache;
		PhxFillCache(&buffer[3 + i].Cache, &caches[i]);
	}
	*length = needed;
	return TRUE;
}

BOOL GetLogicalProcessorInformationEx(DWORD relationship, SYSTEM_LOGICAL_PROCESSOR_INFORMATION_EX* buffer, DWORD* length)
{
	CACHE_DESCRIPTOR caches[8];
	int cacheCount = PhxCpuidCacheInfo(caches, 8);
	BOOL all = relationship == RelationAll;
	DWORD needed = 0;
	if (all || relationship == RelationProcessorCore || relationship == RelationProcessorPackage)
		needed += (all ? 2 : 1) * (8 + sizeof(PROCESSOR_RELATIONSHIP));
	if (all || relationship == RelationNumaNode)
		needed += 8 + sizeof(NUMA_NODE_RELATIONSHIP);
	if (all || relationship == RelationCache)
		needed += (DWORD)cacheCount * (8 + sizeof(CACHE_RELATIONSHIP));
	if (all || relationship == RelationGroup)
		needed += 8 + sizeof(GROUP_RELATIONSHIP);
	if (needed == 0)
	{
		SetLastError(ERROR_INVALID_PARAMETER);
		return FALSE;
	}
	if (buffer == NULL || *length < needed)
	{
		*length = needed;
		SetLastError(ERROR_INSUFFICIENT_BUFFER);
		return FALSE;
	}

	memset(buffer, 0, needed);
	BYTE* at = (BYTE*)buffer;
	static const DWORD processorKinds[2] = { RelationProcessorCore, RelationProcessorPackage };
	for (int i = 0; i < 2; i++)
	{
		if (!all && relationship != processorKinds[i])
			continue;
		SYSTEM_LOGICAL_PROCESSOR_INFORMATION_EX* entry = (SYSTEM_LOGICAL_PROCESSOR_INFORMATION_EX*)at;
		entry->Relationship = processorKinds[i];
		entry->Size = 8 + sizeof(PROCESSOR_RELATIONSHIP);
		entry->Processor.GroupCount = 1;
		entry->Processor.GroupMask[0].Mask = 1;
		at += entry->Size;
	}
	if (all || relationship == RelationNumaNode)
	{
		SYSTEM_LOGICAL_PROCESSOR_INFORMATION_EX* entry = (SYSTEM_LOGICAL_PROCESSOR_INFORMATION_EX*)at;
		entry->Relationship = RelationNumaNode;
		entry->Size = 8 + sizeof(NUMA_NODE_RELATIONSHIP);
		entry->NumaNode.GroupCount = 1;
		entry->NumaNode.GroupMask.Mask = 1;
		at += entry->Size;
	}
	if (all || relationship == RelationCache)
	{
		for (int i = 0; i < cacheCount; i++)
		{
			SYSTEM_LOGICAL_PROCESSOR_INFORMATION_EX* entry = (SYSTEM_LOGICAL_PROCESSOR_INFORMATION_EX*)at;
			entry->Relationship = RelationCache;
			entry->Size = 8 + sizeof(CACHE_RELATIONSHIP);
			entry->Cache.Level = caches[i].Level;
			entry->Cache.Associativity = caches[i].Associativity;
			entry->Cache.LineSize = caches[i].LineSize;
			entry->Cache.CacheSize = caches[i].Size;
			entry->Cache.Type = caches[i].Type;
			entry->Cache.GroupCount = 1;
			entry->Cache.GroupMask.Mask = 1;
			at += entry->Size;
		}
	}
	if (all || relationship == RelationGroup)
	{
		SYSTEM_LOGICAL_PROCESSOR_INFORMATION_EX* entry = (SYSTEM_LOGICAL_PROCESSOR_INFORMATION_EX*)at;
		entry->Relationship = RelationGroup;
		entry->Size = 8 + sizeof(GROUP_RELATIONSHIP);
		entry->Group.MaximumGroupCount = 1;
		entry->Group.ActiveGroupCount = 1;
		entry->Group.GroupInfo[0].MaximumProcessorCount = 1;
		entry->Group.GroupInfo[0].ActiveProcessorCount = 1;
		entry->Group.GroupInfo[0].ActiveProcessorMask = 1;
		at += entry->Size;
	}
	*length = needed;
	return TRUE;
}

// One CPU: there is no other core whose write buffers could be stale
void FlushProcessWriteBuffers(void)
{
	long barrier = 0;
	_InterlockedExchange(&barrier, 1);
}

BOOL FlushInstructionCache(HANDLE process, const void* address, SIZE_T size)
{
	(void)process;
	(void)address;
	(void)size;
	return TRUE;
}
