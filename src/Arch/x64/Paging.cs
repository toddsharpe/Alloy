using System;

namespace Alloy.Arch.X64
{
	[Flags]
	public enum PageFlags : ulong
	{
		None = 0,
		Present = 0x1,
		Writable = 0x2,
		User = 0x4,
		WriteThrough = 0x8,
		CacheDisable = 0x10,
		Accessed = 0x20,
		Dirty = 0x40,
		Large = 0x80,
		Global = 0x100,
		NoExecute = 0x8000000000000000,
	}

	// Builds 4-level page tables; a table at physical address P is accessed at P + physicalOffset
	public unsafe struct PageTableBuilder
	{
		public const ulong PageSize = 0x1000;
		public const ulong LargePageSize = 0x200000;
		public const ulong HugePageSize = 0x40000000;

		private const ulong AddressMask = 0x000FFFFFFFFFF000;
		private const ulong LeafFlagsMask = 0x80000000000001FF;

		private readonly delegate*<ulong> _allocateTable;
		private readonly ulong _physicalOffset;
		private readonly bool _hugePages;

		// allocateTable returns the physical address of a zeroed page, or 0 when out of memory
		public PageTableBuilder(delegate*<ulong> allocateTable, ulong physicalOffset, bool hugePages)
		{
			_allocateTable = allocateTable;
			_physicalOffset = physicalOffset;
			_hugePages = hugePages;
			Root = allocateTable();
		}

		public ulong Root { get; }

		// Maps a range with the largest pages its alignment allows; fails rather than replace an existing mapping
		public bool Map(ulong virtualAddress, ulong physicalAddress, ulong size, PageFlags flags)
		{
			if (Root == 0 || ((virtualAddress | physicalAddress | size) & (PageSize - 1)) != 0 || !IsCanonical(virtualAddress))
				return false;

			while (size > 0)
			{
				int level = 1;
				ulong step = PageSize;
				if (_hugePages && ((virtualAddress | physicalAddress) & (HugePageSize - 1)) == 0 && size >= HugePageSize)
				{
					level = 3;
					step = HugePageSize;
				}
				else if (((virtualAddress | physicalAddress) & (LargePageSize - 1)) == 0 && size >= LargePageSize)
				{
					level = 2;
					step = LargePageSize;
				}

				if (!MapOne(virtualAddress, physicalAddress, flags, level))
					return false;

				virtualAddress += step;
				physicalAddress += step;
				size -= step;
			}
			return true;
		}

		public bool TryTranslate(ulong virtualAddress, out ulong physicalAddress, out PageFlags flags)
		{
			physicalAddress = 0;
			flags = PageFlags.None;
			ulong table = Root;
			for (int level = 4; level >= 1; level--)
			{
				ulong entry = Table(table)[Index(virtualAddress, level)];
				if ((entry & (ulong)PageFlags.Present) == 0)
					return false;

				if (level == 1 || (entry & (ulong)PageFlags.Large) != 0)
				{
					ulong pageMask = level == 3 ? HugePageSize - 1 : level == 2 ? LargePageSize - 1 : PageSize - 1;
					physicalAddress = (entry & AddressMask & ~pageMask) | (virtualAddress & pageMask);
					flags = (PageFlags)(entry & LeafFlagsMask);
					return true;
				}
				table = entry & AddressMask;
			}
			return false;
		}

		private bool MapOne(ulong virtualAddress, ulong physicalAddress, PageFlags flags, int leafLevel)
		{
			// Intermediate entries allow everything; the leaf decides, and User must be set at every level to reach ring 3
			ulong directoryFlags = (ulong)(PageFlags.Present | PageFlags.Writable | (flags & PageFlags.User));
			ulong table = Root;
			for (int level = 4; level > leafLevel; level--)
			{
				ulong* entry = &Table(table)[Index(virtualAddress, level)];
				if ((*entry & (ulong)PageFlags.Present) == 0)
				{
					ulong next = _allocateTable();
					if (next == 0)
						return false;
					*entry = next | directoryFlags;
				}
				else if ((*entry & (ulong)PageFlags.Large) != 0)
				{
					return false;
				}
				else
				{
					*entry |= directoryFlags;
				}
				table = *entry & AddressMask;
			}

			ulong* leaf = &Table(table)[Index(virtualAddress, leafLevel)];
			if ((*leaf & (ulong)PageFlags.Present) != 0)
				return false;

			ulong large = leafLevel > 1 ? (ulong)PageFlags.Large : 0;
			*leaf = physicalAddress | (ulong)(flags & ~PageFlags.Large) | (ulong)PageFlags.Present | large;
			return true;
		}

		private ulong* Table(ulong physical) => (ulong*)(physical + _physicalOffset);

		private static int Index(ulong virtualAddress, int level) => (int)((virtualAddress >> (12 + 9 * (level - 1))) & 0x1FF);

		private static bool IsCanonical(ulong virtualAddress)
		{
			ulong top = virtualAddress >> 47;
			return top == 0 || top == 0x1FFFF;
		}
	}
}
