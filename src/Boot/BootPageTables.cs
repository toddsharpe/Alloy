using System;
using Alloy.Abi;
using Alloy.Arch.X64;

namespace Alloy.Boot
{
	// The page tables the kernel starts on, built in fresh loader memory rather than by editing the firmware's
	internal static class BootPageTables
	{
		private const ulong MinimumWindow = 4UL << 30;

		private static PageTableBuilder s_tables;

		public static unsafe void Build(BootInfo* info, KernelImage kernel, MemoryMap map)
		{
			bool hugePages = (Cpu.CpuId(0x80000001).Edx & (1u << 26)) != 0;
			s_tables = new PageTableBuilder(&Uefi.AllocatePageTable, 0, hugePages);

			ulong top = AlignUp(Math.Max(map.HighestRamAddress(), MinimumWindow), PageTableBuilder.HugePageSize);
			if (top > MemoryLayout.PhysicalWindowSize)
				Environment.FailFast("FAILFAST: physical memory exceeds the kernel's physical window");

			// Identity map: the trampoline runs from loader memory after the switch; the kernel drops it in phase 2
			Map(0, 0, top, PageFlags.Writable);
			Map(MemoryLayout.KernelPhysicalWindow, 0, top, PageFlags.Writable | PageFlags.NoExecute);
			MapKernel(kernel);

			ulong framebuffer = info->Framebuffer & ~(MemoryLayout.PageSize - 1);
			ulong framebufferEnd = AlignUp(info->Framebuffer + info->FramebufferSize, MemoryLayout.PageSize);
			Map(MemoryLayout.KernelFramebuffer, framebuffer, framebufferEnd - framebuffer, PageFlags.Writable | PageFlags.NoExecute);

			// Runtime code and data can share ranges on older firmware, so these stay writable and executable
			for (int i = 0; i < map.Count; i++)
			{
				MemoryDescriptor* descriptor = map[i];
				if ((descriptor->Attribute & MemoryDescriptor.RuntimeAttribute) != 0)
					Map(MemoryLayout.KernelUefiRuntime + descriptor->PhysicalStart, descriptor->PhysicalStart, descriptor->NumberOfPages * MemoryLayout.PageSize, PageFlags.Writable);
			}

			info->PageTableRoot = s_tables.Root;
			info->PhysicalWindowSize = top;
		}

		public static bool IsMapped(ulong virtualAddress, ulong physicalAddress, ulong pages)
		{
			for (ulong page = 0; page < pages; page++)
			{
				ulong offset = page * MemoryLayout.PageSize;
				if (!s_tables.TryTranslate(virtualAddress + offset, out ulong mapped, out _) || mapped != physicalAddress + offset)
					return false;
			}
			return true;
		}

		// Headers read-only, then each section with exactly the access it declares; a writable code section is refused
		private static void MapKernel(KernelImage kernel)
		{
			PeImage.TryParse(kernel.FileBytes, out PeImage pe);
			Map(kernel.ImageBase, kernel.Physical, AlignUp(pe.SizeOfHeaders, MemoryLayout.PageSize), PageFlags.NoExecute);
			for (int i = 0; i < pe.SectionCount; i++)
			{
				PeSection section = pe.GetSection(i);
				if (section.VirtualSize == 0)
					continue;
				if (section.IsWritable && section.IsExecutable)
					Environment.FailFast("FAILFAST: kernel.exe has a writable code section");

				PageFlags flags = (section.IsWritable ? PageFlags.Writable : PageFlags.None) | (section.IsExecutable ? PageFlags.None : PageFlags.NoExecute);
				Map(kernel.ImageBase + section.VirtualAddress, kernel.Physical + section.VirtualAddress, AlignUp(section.VirtualSize, MemoryLayout.PageSize), flags);
			}
		}

		private static void Map(ulong virtualAddress, ulong physicalAddress, ulong size, PageFlags flags)
		{
			if (!s_tables.Map(virtualAddress, physicalAddress, size, flags))
				Environment.FailFast("FAILFAST: boot page table mapping failed (overlap or out of memory)");
		}

		private static ulong AlignUp(ulong value, ulong alignment) => (value + alignment - 1) & ~(alignment - 1);
	}
}
