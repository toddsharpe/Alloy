using System;
using Alloy.Abi;

namespace Alloy.Kernel.Mm
{
	// Physical page allocator over the UEFI memory map; pages are reached through the physical window
	internal static class PhysicalMemory
	{
		// Real-mode memory stays reserved for application processor trampolines later
		private const ulong ReservedLow = 0x100000;

		private static BitmapAllocator s_pages;

		public static ulong FreePages => s_pages.FreeCount;

		public static ulong TotalPages => s_pages.Count;

		// Call after the kernel's own GDT/IDT are loaded: boot services memory, where the firmware's lived, becomes free
		public static unsafe void Initialize(BootInfo* info)
		{
			ulong highest = 0;
			for (int i = 0; i < DescriptorCount(info); i++)
			{
				MemoryDescriptor* descriptor = Descriptor(info, i);
				if (IsRam(descriptor->Type))
					highest = Math.Max(highest, descriptor->PhysicalEnd);
			}

			ulong pages = highest >> MemoryLayout.PageShift;
			ulong bitmapPages = MemoryLayout.PagesFor((pages + 7) / 8);
			ulong bitmap = 0;
			for (int i = 0; i < DescriptorCount(info) && bitmap == 0; i++)
			{
				MemoryDescriptor* descriptor = Descriptor(info, i);
				if (descriptor->Type == MemoryType.Conventional && descriptor->PhysicalStart >= ReservedLow && descriptor->NumberOfPages >= bitmapPages)
					bitmap = descriptor->PhysicalStart;
			}
			if (bitmap == 0)
				Environment.FailFast("FAILFAST: no room for the physical page bitmap");

			s_pages = new BitmapAllocator((byte*)BootInfo.ToVirtual(bitmap), pages);
			for (int i = 0; i < DescriptorCount(info); i++)
			{
				MemoryDescriptor* descriptor = Descriptor(info, i);
				if (IsFree(descriptor->Type))
					s_pages.MarkFree(descriptor->PhysicalStart >> MemoryLayout.PageShift, descriptor->NumberOfPages);
			}
			s_pages.MarkUsed(0, ReservedLow >> MemoryLayout.PageShift);
			s_pages.MarkUsed(bitmap >> MemoryLayout.PageShift, bitmapPages);
		}

		// Zeroed, physically contiguous pages; returns 0 when out of memory
		public static unsafe ulong Allocate(ulong pages = 1)
		{
			ulong state = Ke.Scheduler.Lock();
			ulong index = s_pages.Allocate(pages);
			Ke.Scheduler.Unlock(state);
			if (index == ulong.MaxValue)
				return 0;
			ulong physical = index << MemoryLayout.PageShift;
			Window(physical, pages << MemoryLayout.PageShift).Clear();
			return physical;
		}

		public static void Free(ulong physical, ulong pages = 1)
		{
			ulong state = Ke.Scheduler.Lock();
			s_pages.MarkFree(physical >> MemoryLayout.PageShift, pages);
			Ke.Scheduler.Unlock(state);
		}

		public static ulong AllocatePageTable() => Allocate(1);

		// Physical memory through the kernel's window as a span, for pages the hardware or another process shares with the kernel
		public static unsafe Span<byte> Window(ulong physical, ulong bytes)
		{
			if (bytes > 0x7FFFFFFF || physical + bytes > MemoryLayout.PhysicalWindowSize)
				Environment.FailFast("FAILFAST: a physical range outside the window");
			return new Span<byte>((void*)BootInfo.ToVirtual(physical), (int)bytes);
		}

		public static unsafe int DescriptorCount(BootInfo* info) => (int)(info->MemoryMapSize / info->MemoryDescriptorSize);

		public static unsafe MemoryDescriptor* Descriptor(BootInfo* info, int index) =>
			(MemoryDescriptor*)BootInfo.ToVirtual(info->MemoryMap + (ulong)index * info->MemoryDescriptorSize);

		private static bool IsFree(MemoryType type) => type == MemoryType.Conventional || type == MemoryType.BootServicesCode || type == MemoryType.BootServicesData;

		private static bool IsRam(MemoryType type) => type != MemoryType.MemoryMappedIo && type != MemoryType.MemoryMappedIoPortSpace && type != MemoryType.Reserved;
	}
}
