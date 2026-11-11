// Memory: VirtualAlloc and its family over the kernel's address spaces, what the GC asks about the machine, and the heap behind malloc
#include "win32.h"

#define PhxPageSize 0x1000
#define PhxLargeClass 0xFFFF
#define PhxHeaderMagic 0x58484550

// Size classes up to 64 KB; anything larger is its own mapping
static const DWORD s_classSizes[] =
{
	16, 32, 48, 64, 80, 96, 112, 128, 160, 192, 224, 256, 320, 384, 448, 512, 640, 768, 1024, 1280, 1536, 2048, 2560, 3072, 4096, 6144, 8192, 12288, 16384, 24576, 32768, 65536,
};
#define PhxClassCount (sizeof(s_classSizes) / sizeof(s_classSizes[0]))

// 16 bytes ahead of every block, so blocks stay 16-byte aligned
typedef struct { DWORD Class; DWORD Magic; SIZE_T Size; } PhxBlockHeader;
typedef struct PhxFreeBlock { struct PhxFreeBlock* Next; } PhxFreeBlock;

static PhxLock s_heapLock;
static PhxFreeBlock* s_freeLists[PhxClassCount];

PVOID VirtualAlloc(PVOID address, SIZE_T size, DWORD type, DWORD protect)
{
	type &= ~(MEM_TOP_DOWN | MEM_WRITE_WATCH | MEM_LARGE_PAGES | MEM_RESET_UNDO);
	if (address == NULL && (type & (MEM_COMMIT | MEM_RESERVE)) == MEM_COMMIT)
		type |= MEM_RESERVE;
	LONG64 result = PhxSyscall4(PhxSysVirtualAlloc, address, size, type, protect);
	if (result <= 0)
	{
		SetLastError(ERROR_NOT_ENOUGH_MEMORY);
		return NULL;
	}
	return (PVOID)result;
}

PVOID VirtualAllocExNuma(HANDLE process, PVOID address, SIZE_T size, DWORD type, DWORD protect, DWORD node)
{
	(void)process;
	(void)node;
	return VirtualAlloc(address, size, type, protect);
}

BOOL VirtualFree(PVOID address, SIZE_T size, DWORD type)
{
	if (PhxSyscall3(PhxSysVirtualFree, address, size, type) < 0)
	{
		SetLastError(ERROR_INVALID_PARAMETER);
		return FALSE;
	}
	return TRUE;
}

BOOL VirtualProtect(PVOID address, SIZE_T size, DWORD protect, DWORD* oldProtect)
{
	LONG64 result = PhxSyscall3(PhxSysVirtualProtect, address, size, protect);
	if (result <= 0)
	{
		SetLastError(ERROR_INVALID_PARAMETER);
		return FALSE;
	}
	if (oldProtect != NULL)
		*oldProtect = (DWORD)result;
	return TRUE;
}

SIZE_T VirtualQuery(const void* address, MEMORY_BASIC_INFORMATION* buffer, SIZE_T length)
{
	MEMORY_BASIC_INFORMATION info;
	if (PhxSyscall2(PhxSysVirtualQuery, address, &info) < 0)
	{
		SetLastError(ERROR_INVALID_PARAMETER);
		return 0;
	}
	SIZE_T copied = length < sizeof(info) ? length : sizeof(info);
	memcpy(buffer, &info, copied);
	return copied;
}

// Nothing is ever locked, which is what Windows reports too; the pages are released from the working set regardless
BOOL VirtualUnlock(PVOID address, SIZE_T size)
{
	(void)address;
	(void)size;
	SetLastError(158);
	return FALSE;
}

// The GC tracks writes in software, so hardware write watch is never requested
DWORD GetWriteWatch(DWORD flags, PVOID base, SIZE_T size, PVOID* addresses, ULONG_PTR* count, DWORD* granularity)
{
	(void)flags;
	(void)base;
	(void)size;
	(void)addresses;
	(void)count;
	(void)granularity;
	SetLastError(ERROR_INVALID_PARAMETER);
	return (DWORD)-1;
}

DWORD ResetWriteWatch(PVOID base, SIZE_T size)
{
	(void)base;
	(void)size;
	return 0;
}

SIZE_T GetLargePageMinimum(void)
{
	return 0;
}

void GetSystemInfo(SYSTEM_INFO* info)
{
	int cpu[4];
	__cpuid(cpu, 1);
	DWORD family = (cpu[0] >> 8) & 0xF;
	DWORD model = ((cpu[0] >> 4) & 0xF) | ((cpu[0] >> 12) & 0xF0);
	DWORD stepping = cpu[0] & 0xF;
	memset(info, 0, sizeof(SYSTEM_INFO));
	info->wProcessorArchitecture = 9;
	info->dwPageSize = PhxPageSize;
	info->lpMinimumApplicationAddress = (PVOID)0x10000;
	info->lpMaximumApplicationAddress = (PVOID)0x00007FFFFFFEFFFF;
	info->dwActiveProcessorMask = 1;
	info->dwNumberOfProcessors = 1;
	info->dwProcessorType = 8664;
	info->dwAllocationGranularity = 0x10000;
	info->wProcessorLevel = (WORD)(family == 0xF ? family + ((cpu[0] >> 20) & 0xFF) : family);
	info->wProcessorRevision = (WORD)((model << 8) | stepping);
}

BOOL GlobalMemoryStatusEx(MEMORYSTATUSEX* status)
{
	PhxMemoryStatus memory;
	if (PhxSyscall1(PhxSysGetMemoryStatus, &memory) < 0)
	{
		SetLastError(ERROR_INVALID_PARAMETER);
		return FALSE;
	}
	ULONG64 totalVirtual = 0x00007FFFFFFEFFFFULL - 0x10000;
	status->dwLength = sizeof(MEMORYSTATUSEX);
	status->dwMemoryLoad = memory.TotalPhysical == 0 ? 0 : (DWORD)((memory.TotalPhysical - memory.AvailablePhysical) * 100 / memory.TotalPhysical);
	status->ullTotalPhys = memory.TotalPhysical;
	status->ullAvailPhys = memory.AvailablePhysical;
	status->ullTotalPageFile = memory.TotalPhysical;
	status->ullAvailPageFile = memory.AvailablePhysical;
	status->ullTotalVirtual = totalVirtual;
	status->ullAvailVirtual = totalVirtual;
	status->ullAvailExtendedVirtual = 0;
	return TRUE;
}

// The working set is what this process has committed, from the kernel's process list
BOOL K32GetProcessMemoryInfo(HANDLE process, PROCESS_MEMORY_COUNTERS* counters, DWORD size)
{
	(void)process;
	if (size < sizeof(PROCESS_MEMORY_COUNTERS))
	{
		SetLastError(ERROR_INSUFFICIENT_BUFFER);
		return FALSE;
	}
	PhxProcessEntry entries[64];
	LONG64 count = PhxSyscall2(PhxSysGetProcesses, entries, 64);
	DWORD id = GetCurrentProcessId();
	ULONG64 committed = 0;
	for (LONG64 i = 0; i < count; i++)
	{
		if (entries[i].Id == id)
			committed = entries[i].VirtualSize;
	}
	memset(counters, 0, sizeof(PROCESS_MEMORY_COUNTERS));
	counters->cb = sizeof(PROCESS_MEMORY_COUNTERS);
	counters->WorkingSetSize = committed;
	counters->PeakWorkingSetSize = committed;
	counters->PagefileUsage = committed;
	counters->PeakPagefileUsage = committed;
	return TRUE;
}

// Decodes one deterministic cache parameters leaf (Intel 4, AMD 0x8000001D) into Windows' descriptors
static int PhxDecodeCacheLeaf(int leaf, CACHE_DESCRIPTOR* caches, int max)
{
	int count = 0;
	for (int subleaf = 0; count < max; subleaf++)
	{
		int info[4];
		__cpuidex(info, leaf, subleaf);
		int type = info[0] & 0x1F;
		if (type == 0)
			break;
		DWORD ways = ((DWORD)info[1] >> 22) + 1;
		DWORD partitions = (((DWORD)info[1] >> 12) & 0x3FF) + 1;
		DWORD line = ((DWORD)info[1] & 0xFFF) + 1;
		DWORD sets = (DWORD)info[2] + 1;
		CACHE_DESCRIPTOR* cache = &caches[count++];
		cache->Level = (BYTE)((info[0] >> 5) & 7);
		cache->Associativity = (info[0] & (1 << 9)) != 0 ? 0xFF : (BYTE)ways;
		cache->LineSize = (WORD)line;
		cache->Size = ways * partitions * line * sets;
		cache->Type = type == 1 ? CacheData : type == 2 ? CacheInstruction : CacheUnified;
	}
	return count;
}

int PhxCpuidCacheInfo(CACHE_DESCRIPTOR* caches, int max)
{
	int info[4];
	__cpuid(info, 0);
	DWORD maxLeaf = (DWORD)info[0];
	__cpuid(info, 0x80000000);
	DWORD maxExtended = (DWORD)info[0];
	int count = 0;
	if (maxLeaf >= 4)
		count = PhxDecodeCacheLeaf(4, caches, max);
	if (count == 0 && maxExtended >= 0x8000001D)
	{
		__cpuid(info, 0x80000001);
		if ((info[2] & (1 << 22)) != 0)
			count = PhxDecodeCacheLeaf(0x8000001D, caches, max);
	}
	if (count == 0 && maxExtended >= 0x80000006 && max >= 2)
	{
		__cpuid(info, 0x80000006);
		DWORD l2Kb = (DWORD)info[2] >> 16;
		if (l2Kb != 0)
		{
			caches[count].Level = 2;
			caches[count].Associativity = (BYTE)((info[2] >> 12) & 0xF);
			caches[count].LineSize = (WORD)(info[2] & 0xFF);
			caches[count].Size = l2Kb * 1024;
			caches[count].Type = CacheUnified;
			count++;
		}
		DWORD l3Units = (DWORD)info[3] >> 18;
		if (l3Units != 0)
		{
			caches[count].Level = 3;
			caches[count].Associativity = (BYTE)((info[3] >> 12) & 0xF);
			caches[count].LineSize = (WORD)(info[3] & 0xFF);
			caches[count].Size = l3Units * 512 * 1024;
			caches[count].Type = CacheUnified;
			count++;
		}
	}
	return count;
}

void PhxInitializeHeap(void)
{
	memset(s_freeLists, 0, sizeof(s_freeLists));
	s_heapLock.State = 0;
}

static PVOID PhxAllocateLarge(size_t size)
{
	SIZE_T mapping = (size + sizeof(PhxBlockHeader) + PhxPageSize - 1) & ~(SIZE_T)(PhxPageSize - 1);
	PhxBlockHeader* header = (PhxBlockHeader*)VirtualAlloc(NULL, mapping, MEM_COMMIT | MEM_RESERVE, PAGE_READWRITE);
	if (header == NULL)
		return NULL;
	header->Class = PhxLargeClass;
	header->Magic = PhxHeaderMagic;
	header->Size = mapping;
	return header + 1;
}

// Carves a fresh chunk into blocks of one class and threads them onto its free list; called with the heap lock held
static BOOL PhxRefill(DWORD class)
{
	SIZE_T blockSize = s_classSizes[class] + sizeof(PhxBlockHeader);
	SIZE_T chunkSize = blockSize * 16 > 0x10000 ? blockSize * 16 : 0x10000;
	chunkSize = (chunkSize + PhxPageSize - 1) & ~(SIZE_T)(PhxPageSize - 1);
	BYTE* chunk = (BYTE*)VirtualAlloc(NULL, chunkSize, MEM_COMMIT | MEM_RESERVE, PAGE_READWRITE);
	if (chunk == NULL)
		return FALSE;
	for (SIZE_T offset = 0; offset + blockSize <= chunkSize; offset += blockSize)
	{
		PhxBlockHeader* header = (PhxBlockHeader*)(chunk + offset);
		header->Class = class;
		header->Magic = PhxHeaderMagic;
		header->Size = s_classSizes[class];
		PhxFreeBlock* block = (PhxFreeBlock*)(header + 1);
		block->Next = s_freeLists[class];
		s_freeLists[class] = block;
	}
	return TRUE;
}

void* malloc(size_t size)
{
	if (size == 0)
		size = 1;
	if (size > s_classSizes[PhxClassCount - 1])
		return PhxAllocateLarge(size);
	DWORD class = 0;
	while (s_classSizes[class] < size)
		class++;

	PhxLockAcquire(&s_heapLock);
	if (s_freeLists[class] == NULL && !PhxRefill(class))
	{
		PhxLockRelease(&s_heapLock);
		return NULL;
	}
	PhxFreeBlock* block = s_freeLists[class];
	s_freeLists[class] = block->Next;
	PhxLockRelease(&s_heapLock);
	return block;
}

void* calloc(size_t count, size_t size)
{
	if (size != 0 && count > (size_t)-1 / size)
		return NULL;
	void* block = malloc(count * size);
	if (block != NULL)
		memset(block, 0, count * size);
	return block;
}

void free(void* block)
{
	if (block == NULL)
		return;
	PhxBlockHeader* header = (PhxBlockHeader*)block - 1;
	if (header->Magic != PhxHeaderMagic)
		PhxFatal(STATUS_STACK_BUFFER_OVERRUN, "free: %p is not a heap block", block);
	if (header->Class == PhxLargeClass)
	{
		VirtualFree(header, 0, MEM_RELEASE);
		return;
	}
	PhxLockAcquire(&s_heapLock);
	PhxFreeBlock* freed = (PhxFreeBlock*)block;
	freed->Next = s_freeLists[header->Class];
	s_freeLists[header->Class] = freed;
	PhxLockRelease(&s_heapLock);
}

// No new handler is ever installed, so operator new's retry loop ends at once
int _callnewh(size_t size)
{
	(void)size;
	return 0;
}

// Win32 heaps over malloc: every heap handle is the one process heap, so blocks can move between them freely
#define PhxProcessHeap ((HANDLE)(LONG_PTR)0x50484850)
#define HEAP_ZERO_MEMORY 0x8

HANDLE GetProcessHeap(void)
{
	return PhxProcessHeap;
}

HANDLE HeapCreate(DWORD options, SIZE_T initialSize, SIZE_T maximumSize)
{
	(void)options;
	(void)initialSize;
	(void)maximumSize;
	return PhxProcessHeap;
}

BOOL HeapDestroy(HANDLE heap)
{
	(void)heap;
	return TRUE;
}

PVOID HeapAlloc(HANDLE heap, DWORD flags, SIZE_T size)
{
	(void)heap;
	void* block = malloc(size);
	if (block != NULL && (flags & HEAP_ZERO_MEMORY) != 0)
		memset(block, 0, size);
	return block;
}

BOOL HeapFree(HANDLE heap, DWORD flags, PVOID block)
{
	(void)heap;
	(void)flags;
	free(block);
	return TRUE;
}

SIZE_T HeapSize(HANDLE heap, DWORD flags, const void* block)
{
	(void)heap;
	(void)flags;
	if (block == NULL)
		return (SIZE_T)-1;
	const PhxBlockHeader* header = (const PhxBlockHeader*)block - 1;
	return header->Class == PhxLargeClass ? header->Size - sizeof(PhxBlockHeader) : header->Size;
}

PVOID HeapReAlloc(HANDLE heap, DWORD flags, PVOID block, SIZE_T size)
{
	if (block == NULL)
		return HeapAlloc(heap, flags, size);
	SIZE_T old = HeapSize(heap, 0, block);
	void* moved = HeapAlloc(heap, flags, size);
	if (moved == NULL)
		return NULL;
	memcpy(moved, block, old < size ? old : size);
	free(block);
	return moved;
}

// The CRT's aligned allocation: the real block's address sits just below the aligned one so _aligned_free can find it
void* _aligned_malloc(size_t size, size_t alignment)
{
	if (alignment < 16)
		alignment = 16;
	BYTE* raw = (BYTE*)malloc(size + alignment + sizeof(void*));
	if (raw == NULL)
		return NULL;
	BYTE* aligned = (BYTE*)(((ULONG_PTR)raw + sizeof(void*) + alignment - 1) & ~(ULONG_PTR)(alignment - 1));
	((void**)aligned)[-1] = raw;
	return aligned;
}

void _aligned_free(void* block)
{
	if (block != NULL)
		free(((void**)block)[-1]);
}
