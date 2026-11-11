using System;
using Alloy.Abi;
using Alloy.Arch.X64;
using Alloy.Kernel.Ke;
using Alloy.Kernel.Mm;

namespace Alloy.Kernel.Ps
{
	internal enum RegionKind
	{
		Private,
		Image,
		Mapped,
	}

	// One reservation: VirtualAlloc's allocation, an image, a stack, a thread block or a shared mapping
	internal sealed class Region
	{
		public ulong Base;
		public ulong Size;
		public uint AllocationProtect;
		public RegionKind Kind;
		public Region Next;
	}

	// A process's user half with Windows' VirtualAlloc semantics: reserve is bookkeeping, commit maps zeroed pages, decommit and release free them
	internal sealed class AddressSpace
	{
		public const ulong UserStart = 0x10000;
		public const ulong UserEnd = 0x0000_7FFF_FFFF_0000;

		private const ulong AddressMask = 0x000FFFFFFFFFF000;

		// Software bits in page table entries: Private pages belong to this process; NoAccess marks a committed page mapped not-present
		private const ulong Private = 1UL << 9;
		private const ulong NoAccess = 1UL << 10;

		private readonly ulong _root;
		private readonly KEvent _lock = new KEvent(false, true);
		private Region _regions;
		private ulong _committedPages;

		public AddressSpace(ulong root)
		{
			_root = root;
		}

		public ulong CommittedBytes => _committedPages * MemoryLayout.PageSize;

		// Reserves address space, at the given address (rounded down to the granularity) or wherever it fits; 0 on failure
		public ulong Reserve(ulong address, ulong size, uint protect, RegionKind kind)
		{
			size = AlignUp(size, MemoryLayout.PageSize);
			if (size == 0 || size > UserEnd)
				return 0;
			Lock();
			ulong reserved = ReserveLocked(address, size, protect, kind);
			Unlock();
			return reserved;
		}

		public bool Commit(ulong address, ulong size, uint protect)
		{
			if (!TryFlags(protect, out ulong flags))
				return false;
			Lock();
			bool committed = CommitLocked(address, size, flags);
			Unlock();
			return committed;
		}

		// VirtualAlloc: reserve, commit or both; returns the base, or 0
		public ulong Allocate(ulong address, ulong size, uint type, uint protect)
		{
			if (!TryFlags(protect, out ulong flags) || size == 0)
				return 0;
			ulong end = AlignUp(address + size, MemoryLayout.PageSize);
			Lock();
			ulong result = 0;
			if ((type & Memory.Reset) != 0)
			{
				result = Find(address) != null ? address & ~(MemoryLayout.PageSize - 1) : 0;
			}
			else if ((type & Memory.Reserve) != 0)
			{
				result = ReserveLocked(address, AlignUp(size + (address & (Memory.AllocationGranularity - 1)), MemoryLayout.PageSize), protect, RegionKind.Private);
				if (result != 0 && (type & Memory.Commit) != 0 && !CommitLocked(result, size, flags))
				{
					ReleaseLocked(result);
					result = 0;
				}
			}
			else if ((type & Memory.Commit) != 0)
			{
				ulong start = address & ~(MemoryLayout.PageSize - 1);
				result = CommitLocked(start, end - start, flags) ? start : 0;
			}
			Unlock();
			return result;
		}

		// VirtualFree: MEM_DECOMMIT a range, or MEM_RELEASE a whole reservation by its base (size 0)
		public bool Free(ulong address, ulong size, uint type)
		{
			Lock();
			bool freed = false;
			if (type == Memory.Release && size == 0)
				freed = ReleaseLocked(address);
			else if (type == Memory.Decommit)
				freed = DecommitLocked(address & ~(MemoryLayout.PageSize - 1), AlignUp(address + size, MemoryLayout.PageSize) - (address & ~(MemoryLayout.PageSize - 1)));
			Unlock();
			return freed;
		}

		// Changes the protection of committed pages; returns the first page's old protection, or 0 if any page isn't committed
		public unsafe uint Protect(ulong address, ulong size, uint protect)
		{
			if (!TryFlags(protect, out ulong flags))
				return 0;
			ulong start = address & ~(MemoryLayout.PageSize - 1);
			ulong end = AlignUp(address + size, MemoryLayout.PageSize);
			Lock();
			uint old = 0;
			Region region = Find(start);
			if (region != null && end <= region.Base + region.Size)
			{
				for (ulong page = start; page < end; page += MemoryLayout.PageSize)
				{
					ulong* entry = Entry(page, false);
					if (entry == null || (*entry & ((ulong)PageFlags.Present | NoAccess)) == 0)
					{
						old = 0;
						break;
					}
					if (page == start)
						old = ProtectOf(*entry);
					*entry = Leaf(*entry & AddressMask, flags, *entry & Private);
					Cpu.InvalidatePage((void*)page);
				}
			}
			Unlock();
			return old;
		}

		// VirtualQuery: the run of pages from address that share a state and protection, within one allocation
		public bool Query(ulong address, out MemoryBasicInformation info)
		{
			ulong page = address & ~(MemoryLayout.PageSize - 1);
			Lock();
			Region region = Find(page);
			info = default;
			info.BaseAddress = page;
			if (region == null)
			{
				Region next = _regions;
				while (next != null && next.Base <= page)
					next = next.Next;
				info.State = Memory.Free;
				info.Protect = Memory.NoAccess;
				info.RegionSize = (next != null ? next.Base : UserEnd) - page;
				Unlock();
				return page < UserEnd;
			}
			info.AllocationBase = region.Base;
			info.AllocationProtect = region.AllocationProtect;
			info.Type = region.Kind == RegionKind.Image ? Memory.Image : region.Kind == RegionKind.Mapped ? Memory.Mapped : Memory.Private;
			StateOf(page, out uint state, out uint protect);
			info.State = state;
			info.Protect = state == Memory.Commit ? protect : 0;
			ulong end = page + MemoryLayout.PageSize;
			while (end < region.Base + region.Size)
			{
				StateOf(end, out uint nextState, out uint nextProtect);
				if (nextState != state || (state == Memory.Commit && nextProtect != protect))
					break;
				end += MemoryLayout.PageSize;
			}
			info.RegionSize = end - page;
			Unlock();
			return true;
		}

		// Maps physical pages the caller supplies into a reservation; private ones are freed with the process
		public unsafe bool MapPages(ulong address, ulong physical, ulong pages, PageFlags flags, bool owned)
		{
			Lock();
			bool mapped = true;
			for (ulong i = 0; i < pages && mapped; i++)
			{
				ulong* entry = Entry(address + i * MemoryLayout.PageSize, true);
				mapped = entry != null;
				if (mapped)
				{
					if (owned && (*entry & Private) == 0)
						_committedPages++;
					*entry = (physical + i * MemoryLayout.PageSize) | (ulong)(flags | PageFlags.Present | PageFlags.User) | (owned ? Private : 0);
				}
			}
			Unlock();
			return mapped;
		}

		// The physical page behind a committed user address
		public unsafe bool TryTranslate(ulong address, out ulong physical)
		{
			Lock();
			ulong* entry = Entry(address, false);
			bool mapped = entry != null && (*entry & ((ulong)PageFlags.Present | NoAccess)) != 0;
			physical = mapped ? (*entry & AddressMask) | (address & (MemoryLayout.PageSize - 1)) : 0;
			Unlock();
			return mapped;
		}

		// Frees every page this process owns, then its page tables; the caller has switched to the kernel's tables
		public unsafe void Destroy()
		{
			ulong* pml4 = Table(_root);
			for (int i = 0; i < 256; i++)
			{
				if ((pml4[i] & (ulong)PageFlags.Present) != 0)
					FreeTable(pml4[i] & AddressMask, 3);
				pml4[i] = 0;
			}
			for (Region region = _regions; region != null;)
			{
				Region next = region.Next;
				Heap.Free(region);
				region = next;
			}
			_regions = null;
			_committedPages = 0;
		}

		// A wait interrupted by process termination returns early, so keep waiting until the lock is really ours
		private void Lock()
		{
			while (!_lock.Wait())
			{
			}
		}

		private void Unlock() => _lock.Signal();

		private ulong ReserveLocked(ulong address, ulong size, uint protect, RegionKind kind)
		{
			ulong start;
			if (address != 0)
			{
				start = address & ~(Memory.AllocationGranularity - 1);
				if (start < UserStart || start + size > UserEnd || !IsFree(start, size))
					return 0;
			}
			else
			{
				start = FindGap(size);
				if (start == 0)
					return 0;
			}
			Region region = new Region { Base = start, Size = size, AllocationProtect = protect, Kind = kind };
			if (_regions == null || _regions.Base > start)
			{
				region.Next = _regions;
				_regions = region;
			}
			else
			{
				Region before = _regions;
				while (before.Next != null && before.Next.Base < start)
					before = before.Next;
				region.Next = before.Next;
				before.Next = region;
			}
			return start;
		}

		private unsafe bool CommitLocked(ulong address, ulong size, ulong flags)
		{
			ulong start = address & ~(MemoryLayout.PageSize - 1);
			ulong end = AlignUp(address + size, MemoryLayout.PageSize);
			Region region = Find(start);
			if (region == null || end > region.Base + region.Size || region.Kind == RegionKind.Mapped)
				return false;
			for (ulong page = start; page < end; page += MemoryLayout.PageSize)
			{
				ulong* entry = Entry(page, true);
				if (entry == null)
					return false;
				if ((*entry & ((ulong)PageFlags.Present | NoAccess)) != 0)
				{
					*entry = Leaf(*entry & AddressMask, flags, *entry & Private);
					Cpu.InvalidatePage((void*)page);
					continue;
				}
				ulong physical = PhysicalMemory.Allocate(1);
				if (physical == 0)
					return false;
				*entry = Leaf(physical, flags, Private);
				_committedPages++;
			}
			return true;
		}

		private bool DecommitLocked(ulong start, ulong size)
		{
			Region region = Find(start);
			if (region == null || start + size > region.Base + region.Size)
				return false;
			for (ulong page = start; page < start + size; page += MemoryLayout.PageSize)
				Unmap(page);
			return true;
		}

		private bool ReleaseLocked(ulong address)
		{
			Region region = Find(address);
			if (region == null || region.Base != address)
				return false;
			for (ulong page = region.Base; page < region.Base + region.Size; page += MemoryLayout.PageSize)
				Unmap(page);
			if (_regions == region)
			{
				_regions = region.Next;
			}
			else
			{
				Region before = _regions;
				while (before.Next != region)
					before = before.Next;
				before.Next = region.Next;
			}
			Heap.Free(region);
			return true;
		}

		private unsafe void Unmap(ulong page)
		{
			ulong* entry = Entry(page, false);
			if (entry == null || *entry == 0)
				return;
			if ((*entry & Private) != 0)
			{
				PhysicalMemory.Free(*entry & AddressMask, 1);
				_committedPages--;
			}
			*entry = 0;
			Cpu.InvalidatePage((void*)page);
		}

		private Region Find(ulong address)
		{
			for (Region region = _regions; region != null && region.Base <= address; region = region.Next)
			{
				if (address < region.Base + region.Size)
					return region;
			}
			return null;
		}

		private bool IsFree(ulong start, ulong size)
		{
			for (Region region = _regions; region != null; region = region.Next)
			{
				if (start < region.Base + region.Size && region.Base < start + size)
					return false;
			}
			return true;
		}

		// First fit above UserStart, aligned to the allocation granularity
		private ulong FindGap(ulong size)
		{
			ulong candidate = UserStart;
			for (Region region = _regions; region != null; region = region.Next)
			{
				if (candidate + size <= region.Base)
					return candidate;
				candidate = Math.Max(candidate, AlignUp(region.Base + region.Size, Memory.AllocationGranularity));
			}
			return candidate + size <= UserEnd ? candidate : 0;
		}

		private unsafe void StateOf(ulong page, out uint state, out uint protect)
		{
			ulong* entry = Entry(page, false);
			if (entry == null || (*entry & ((ulong)PageFlags.Present | NoAccess)) == 0)
			{
				state = Memory.Reserve;
				protect = 0;
				return;
			}
			state = Memory.Commit;
			protect = ProtectOf(*entry);
		}

		private static uint ProtectOf(ulong entry)
		{
			if ((entry & (ulong)PageFlags.Present) == 0)
				return Memory.NoAccess;
			bool write = (entry & (ulong)PageFlags.Writable) != 0;
			bool execute = (entry & (ulong)PageFlags.NoExecute) == 0;
			return execute ? (write ? Memory.ExecuteReadWrite : Memory.ExecuteRead) : (write ? Memory.ReadWrite : Memory.ReadOnly);
		}

		private static ulong Leaf(ulong physical, ulong flags, ulong owned) => flags == NoAccess ? physical | NoAccess | owned : physical | flags | owned;

		// Page table bits for a Windows protection; PAGE_NOACCESS commits the page but leaves it not present
		private static bool TryFlags(uint protect, out ulong flags)
		{
			const ulong User = (ulong)(PageFlags.Present | PageFlags.User);
			const ulong NoExecute = (ulong)PageFlags.NoExecute;
			const ulong Writable = (ulong)PageFlags.Writable;
			switch (protect & ~(Memory.Guard | Memory.NoCache))
			{
				case Memory.NoAccess:
					flags = NoAccess;
					return true;
				case Memory.ReadOnly:
					flags = User | NoExecute;
					return true;
				case Memory.ReadWrite:
				case Memory.WriteCopy:
					flags = User | Writable | NoExecute;
					return true;
				case Memory.Execute:
				case Memory.ExecuteRead:
					flags = User;
					return true;
				case Memory.ExecuteReadWrite:
				case Memory.ExecuteWriteCopy:
					flags = User | Writable;
					return true;
				default:
					flags = 0;
					return false;
			}
		}

		// The leaf entry for a page, creating the tables above it when asked; user tables stay 4 KB pages
		private unsafe ulong* Entry(ulong address, bool create)
		{
			ulong table = _root;
			for (int level = 4; level > 1; level--)
			{
				ulong* entry = &Table(table)[(address >> (12 + 9 * (level - 1))) & 0x1FF];
				if ((*entry & (ulong)PageFlags.Present) == 0)
				{
					if (!create)
						return null;
					ulong next = PhysicalMemory.Allocate(1);
					if (next == 0)
						return null;
					*entry = next | (ulong)(PageFlags.Present | PageFlags.Writable | PageFlags.User);
				}
				table = *entry & AddressMask;
			}
			return &Table(table)[(address >> 12) & 0x1FF];
		}

		private unsafe void FreeTable(ulong table, int level)
		{
			ulong* entries = Table(table);
			for (int i = 0; i < 512; i++)
			{
				ulong entry = entries[i];
				if (level > 1 && (entry & (ulong)PageFlags.Present) != 0)
					FreeTable(entry & AddressMask, level - 1);
				else if (level == 1 && (entry & Private) != 0)
					PhysicalMemory.Free(entry & AddressMask, 1);
			}
			PhysicalMemory.Free(table, 1);
		}

		private static unsafe ulong* Table(ulong physical) => (ulong*)BootInfo.ToVirtual(physical);

		private static ulong AlignUp(ulong value, ulong alignment) => (value + alignment - 1) & ~(alignment - 1);
	}
}
