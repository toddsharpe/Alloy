using System.Runtime.InteropServices;
using Alloy.Abi;
using Alloy.Arch.X64;

namespace Alloy.Tests;

// Runs the page table builder against a fake physical memory buffer; tests in a class run sequentially, so the statics are safe
public unsafe class PagingTests : IDisposable
{
	private const ulong MemorySize = 256 * PageTableBuilder.PageSize;

	private static byte* s_memory;
	private static ulong s_next;
	private static ulong s_limit;

	public PagingTests()
	{
		s_memory = (byte*)NativeMemory.AllocZeroed((nuint)MemorySize);
		s_next = PageTableBuilder.PageSize;
		s_limit = MemorySize;
	}

	public void Dispose() => NativeMemory.Free(s_memory);

	// Physical 0 means failure, so allocation starts at the second page
	private static ulong AllocateTable()
	{
		if (s_next >= s_limit)
			return 0;
		ulong page = s_next;
		s_next += PageTableBuilder.PageSize;
		return page;
	}

	private static PageTableBuilder Create(bool hugePages) => new(&AllocateTable, (ulong)s_memory, hugePages);

	[Fact]
	public void TranslatesSmallPagesWithFlags()
	{
		PageTableBuilder tables = Create(hugePages: true);
		Assert.True(tables.Map(MemoryLayout.KernelBase, 0x200000, 3 * PageTableBuilder.PageSize, PageFlags.Writable | PageFlags.NoExecute));

		Assert.True(tables.TryTranslate(MemoryLayout.KernelBase + 0x1234, out ulong physical, out PageFlags flags));
		Assert.Equal(0x201234UL, physical);
		Assert.True(flags.HasFlag(PageFlags.Writable | PageFlags.NoExecute | PageFlags.Present));
		Assert.False(flags.HasFlag(PageFlags.Large));
		Assert.False(tables.TryTranslate(MemoryLayout.KernelBase + 3 * PageTableBuilder.PageSize, out _, out _));
	}

	[Fact]
	public void PicksTheLargestPagesAlignmentAllows()
	{
		PageTableBuilder huge = Create(hugePages: true);
		Assert.True(huge.Map(MemoryLayout.KernelPhysicalWindow, 0, 2 * PageTableBuilder.HugePageSize, PageFlags.Writable));
		Assert.True(huge.TryTranslate(MemoryLayout.KernelPhysicalWindow + PageTableBuilder.HugePageSize + 0x12345, out ulong physical, out PageFlags flags));
		Assert.Equal(PageTableBuilder.HugePageSize + 0x12345, physical);
		Assert.True(flags.HasFlag(PageFlags.Large));

		PageTableBuilder large = Create(hugePages: false);
		Assert.True(large.Map(MemoryLayout.KernelPhysicalWindow, 0, PageTableBuilder.HugePageSize, PageFlags.Writable));
		Assert.True(large.TryTranslate(MemoryLayout.KernelPhysicalWindow + 0x3FFFFFFF, out physical, out flags));
		Assert.Equal(0x3FFFFFFFUL, physical);
		Assert.True(flags.HasFlag(PageFlags.Large));
	}

	[Fact]
	public void RefusesOverlapsAndBadAddresses()
	{
		PageTableBuilder tables = Create(hugePages: false);
		Assert.True(tables.Map(MemoryLayout.KernelFramebuffer, 0xF8000000, PageTableBuilder.LargePageSize, PageFlags.Writable));
		Assert.False(tables.Map(MemoryLayout.KernelFramebuffer, 0xF8000000, PageTableBuilder.PageSize, PageFlags.Writable));
		Assert.False(tables.Map(MemoryLayout.KernelFramebuffer + PageTableBuilder.PageSize, 0, PageTableBuilder.PageSize, PageFlags.Writable));

		Assert.False(tables.Map(0x0000_8000_0000_0000, 0, PageTableBuilder.PageSize, PageFlags.Writable));
		Assert.False(tables.Map(MemoryLayout.KernelBase + 1, 0, PageTableBuilder.PageSize, PageFlags.Writable));
	}

	[Fact]
	public void KeepsTheKernelInOnePml4Slot()
	{
		PageTableBuilder tables = Create(hugePages: true);
		Assert.True(tables.Map(MemoryLayout.KernelBase, 0x100000, PageTableBuilder.PageSize, PageFlags.None));
		Assert.True(tables.Map(MemoryLayout.KernelPhysicalWindow, 0, PageTableBuilder.HugePageSize, PageFlags.Writable));
		Assert.True(tables.Map(MemoryLayout.KernelUefiRuntime + 0x7F000000, 0x7F000000, PageTableBuilder.PageSize, PageFlags.Writable));

		ulong* pml4 = (ulong*)(s_memory + tables.Root);
		for (int i = 0; i < 512; i++)
			Assert.Equal(i == 256, (pml4[i] & (ulong)PageFlags.Present) != 0);
	}

	[Fact]
	public void ReportsExhaustion()
	{
		PageTableBuilder tables = Create(hugePages: false);
		s_limit = s_next;
		Assert.False(tables.Map(MemoryLayout.KernelBase, 0, PageTableBuilder.PageSize, PageFlags.None));
	}
}
