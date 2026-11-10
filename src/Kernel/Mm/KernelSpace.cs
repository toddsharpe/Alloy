using System;
using Alloy.Abi;
using Alloy.Arch.X64;

namespace Alloy.Kernel.Mm
{
	// The kernel's own page tables: the loader's layout minus the identity map, with the kernel image W^X and global
	internal static unsafe class KernelSpace
	{
		private static PageTableBuilder s_tables;
		private static ulong s_nextVirtual = MemoryLayout.KernelAlloc;

		public static ulong Root => s_tables.Root;

		public static void Initialize(BootInfo* info)
		{
			bool hugePages = (Cpu.CpuId(0x80000001).Edx & (1u << 26)) != 0;
			s_tables = new PageTableBuilder(&PhysicalMemory.AllocatePageTable, MemoryLayout.KernelPhysicalWindow, hugePages);

			Map(MemoryLayout.KernelPhysicalWindow, 0, info->PhysicalWindowSize, PageFlags.Writable | PageFlags.NoExecute | PageFlags.Global);

			ulong imageSize = info->KernelImageSize;
			if (!PeImage.TryParseLoaded(new ReadOnlySpan<byte>((void*)info->KernelVirtual, (int)imageSize), out PeImage pe))
				Environment.FailFast("FAILFAST: cannot parse the running kernel image");
			Map(info->KernelVirtual, info->KernelPhysical, Align(pe.SizeOfHeaders), PageFlags.NoExecute | PageFlags.Global);
			for (int i = 0; i < pe.SectionCount; i++)
			{
				PeSection section = pe.GetSection(i);
				if (section.VirtualSize == 0)
					continue;
				PageFlags flags = PageFlags.Global | (section.IsWritable ? PageFlags.Writable : PageFlags.None) | (section.IsExecutable ? PageFlags.None : PageFlags.NoExecute);
				Map(info->KernelVirtual + section.VirtualAddress, info->KernelPhysical + section.VirtualAddress, Align(section.VirtualSize), flags);
			}

			ulong framebuffer = info->Framebuffer & ~(MemoryLayout.PageSize - 1);
			Map(MemoryLayout.KernelFramebuffer, framebuffer, Align(info->Framebuffer + info->FramebufferSize) - framebuffer, PageFlags.Writable | PageFlags.NoExecute | PageFlags.Global);

			for (int i = 0; i < PhysicalMemory.DescriptorCount(info); i++)
			{
				MemoryDescriptor* descriptor = PhysicalMemory.Descriptor(info, i);
				if ((descriptor->Attribute & MemoryDescriptor.RuntimeAttribute) != 0)
					Map(descriptor->VirtualStart, descriptor->PhysicalStart, descriptor->NumberOfPages * MemoryLayout.PageSize, PageFlags.Writable | PageFlags.Global);
			}

			Cpu.WriteCr3(s_tables.Root);
		}

		// Address space in the KernelAlloc region for mappings that can't live in the physical window; never reused
		public static ulong AllocateVirtual(ulong pages)
		{
			ulong address = s_nextVirtual;
			s_nextVirtual += (pages + 1) * MemoryLayout.PageSize;
			return address;
		}

		public static bool TryTranslate(ulong virtualAddress, out ulong physicalAddress) => s_tables.TryTranslate(virtualAddress, out physicalAddress, out _);

		public static void Map(ulong virtualAddress, ulong physicalAddress, ulong size, PageFlags flags)
		{
			if (!s_tables.Map(virtualAddress, physicalAddress, size, flags))
				Environment.FailFast("FAILFAST: kernel page table mapping failed");
		}

		private static ulong Align(ulong value) => (value + MemoryLayout.PageSize - 1) & ~(MemoryLayout.PageSize - 1);
	}
}
