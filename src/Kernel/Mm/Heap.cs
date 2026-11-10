using System;
using Alloy.Abi;

namespace Alloy.Kernel.Mm
{
	// Kernel heap: page-sized slabs per size class up to 2 KB, whole pages above; memory lives in the physical window
	internal static unsafe class Heap
	{
		private const uint SlabMagic = 0x42414C53;
		private const uint LargeMagic = 0x4752414C;
		private const int HeaderSize = 32;
		private const int ClassCount = 8;
		private const int MinimumShift = 4;
		private const int MaximumSmall = 16 << (ClassCount - 1);

		private struct PageHeader
		{
			public uint Magic;
			public uint ClassOrPages;
			public void* FreeList;
			public PageHeader* Next;
			public uint FreeCount;
			public uint Pad;
		}

		private static PageHeader** s_partial;
		private static ulong s_largePages;

		public static ulong LargePages => s_largePages;

		public static void Initialize()
		{
			ulong table = PhysicalMemory.Allocate(1);
			s_partial = (PageHeader**)BootInfo.ToVirtual(table);
		}

		// Zeroed, 16-byte aligned memory, or null when physical memory runs out; preemptible threads share it, so slab updates run with interrupts off
		public static void* Allocate(nuint size)
		{
			if (size > MaximumSmall)
				return AllocateLarge(size);
			ulong state = Ke.Scheduler.Lock();
			void* block = AllocateSmall(size);
			Ke.Scheduler.Unlock(state);
			return block;
		}

		private static void* AllocateSmall(nuint size)
		{
			int sizeClass = ClassFor(size);
			PageHeader* page = s_partial[sizeClass];
			if (page == null)
			{
				page = NewSlab(sizeClass);
				if (page == null)
					return null;
			}

			void* block = page->FreeList;
			page->FreeList = *(void**)block;
			page->FreeCount--;
			if (page->FreeCount == 0)
			{
				s_partial[sizeClass] = page->Next;
				page->Next = null;
			}
			new Span<byte>(block, 16 << sizeClass).Clear();
			return block;
		}

		public static void Free(void* block)
		{
			if (block == null)
				return;
			ulong state = Ke.Scheduler.Lock();
			FreeLocked(block);
			Ke.Scheduler.Unlock(state);
		}

		private static void FreeLocked(void* block)
		{
			PageHeader* page = (PageHeader*)((ulong)block & ~(MemoryLayout.PageSize - 1));
			if (page->Magic == LargeMagic)
			{
				s_largePages -= page->ClassOrPages;
				page->Magic = 0;
				PhysicalMemory.Free((ulong)page - MemoryLayout.KernelPhysicalWindow, page->ClassOrPages);
				return;
			}
			if (page->Magic != SlabMagic)
				Environment.FailFast("FAILFAST: Heap.Free of a pointer the heap never returned");

			int sizeClass = (int)page->ClassOrPages;
			*(void**)block = page->FreeList;
			page->FreeList = block;
			page->FreeCount++;
			if (page->FreeCount == 1)
			{
				page->Next = s_partial[sizeClass];
				s_partial[sizeClass] = page;
			}
		}

		// Objects start after their header word (Allocator.Place), so the allocation begins 8 bytes before the reference
		public static void Free(object value)
		{
			if (value != null)
				Free((byte*)System.Runtime.CompilerServices.Unsafe.As<object, nint>(ref value) - sizeof(nint));
		}

		private static int ClassFor(nuint size)
		{
			int sizeClass = 0;
			while ((nuint)(16 << sizeClass) < size)
				sizeClass++;
			return sizeClass;
		}

		private static PageHeader* NewSlab(int sizeClass)
		{
			ulong physical = PhysicalMemory.Allocate(1);
			if (physical == 0)
				return null;

			PageHeader* page = (PageHeader*)BootInfo.ToVirtual(physical);
			int blockSize = 16 << sizeClass;
			int first = Math.Max(HeaderSize, blockSize);
			page->Magic = SlabMagic;
			page->ClassOrPages = (uint)sizeClass;
			for (int offset = (int)MemoryLayout.PageSize - blockSize; offset >= first; offset -= blockSize)
			{
				void* block = (byte*)page + offset;
				*(void**)block = page->FreeList;
				page->FreeList = block;
				page->FreeCount++;
			}
			page->Next = s_partial[sizeClass];
			s_partial[sizeClass] = page;
			return page;
		}

		private static void* AllocateLarge(nuint size)
		{
			ulong pages = MemoryLayout.PagesFor((ulong)size + HeaderSize);
			ulong physical = PhysicalMemory.Allocate(pages);
			if (physical == 0)
				return null;
			PageHeader* page = (PageHeader*)BootInfo.ToVirtual(physical);
			page->Magic = LargeMagic;
			page->ClassOrPages = (uint)pages;
			s_largePages += pages;
			return (byte*)page + HeaderSize;
		}
	}
}
