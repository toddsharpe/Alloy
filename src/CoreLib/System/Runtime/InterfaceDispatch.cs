using System.Runtime.CompilerServices;
using System.Runtime.InteropServices;
using System.Threading;
using Internal.Runtime;

namespace System.Runtime
{
	// Interface calls the compiler couldn't devirtualize go through a per-call-site cell: Runtime.asm checks the cell's cache, and misses land here
	internal static unsafe class InterfaceDispatch
	{
		private const nint FlagsMask = 3;
		private const nint CachePointerIsInterfacePointer = 1;
		private const nint CachePointerIsIndirectedInterfaceRelativePointer = 2;
		private const nint CachePointerIsInterfaceRelativePointer = 3;
		private const nint MaxVTableOffsetPlusOne = 0x1000;
		private const ushort ReabstractionSlot = 0xFFFF;
		private const ushort DiamondSlot = 0xFFFE;

		// A cell is the stub to call, then the interface it dispatches (with flags in the low bits) until a cache replaces that word
		private struct Cell
		{
			public nint Stub;
			public nint Cache;
		}

		// Layout is shared with Runtime.asm: the interface and slot, then four (MethodTable, target) entries from offset 24
		[StructLayout(LayoutKind.Sequential)]
		private sealed class Cache
		{
			public MethodTable* Interface;
			public uint Slot;
			public uint Count;
			public MethodTable* Type0;
			public nint Target0;
			public MethodTable* Type1;
			public nint Target1;
			public MethodTable* Type2;
			public nint Target2;
			public MethodTable* Type3;
			public nint Target3;
		}

		// The slow path of the dispatch stub and of delegates to interface methods: the implementation of the cell's method for the instance's type
		[RuntimeExport("PhxResolveInterfaceDispatch")]
		internal static nint Resolve(object instance, nint cellAddress)
		{
			if (instance == null)
				ThrowHelper.FailFast("FAILFAST: null reference (interface call)");

			Cell* cell = (Cell*)cellAddress;
			MethodTable* type = instance.GetMethodTable();
			nint word = cell->Cache;
			Cache cache = null;
			MethodTable* interfaceType;
			uint slot;
			if ((word & FlagsMask) == 0)
			{
				// A cell that names a vtable offset directly, or one already carrying a cache (kernel addresses are negative, hence unsigned)
				if ((nuint)word < (nuint)MaxVTableOffsetPlusOne)
					return *(nint*)((byte*)type + word);
				cache = Unsafe.As<nint, Cache>(ref word);
				interfaceType = cache.Interface;
				slot = cache.Slot;
			}
			else
			{
				Decode(cell, out interfaceType, out slot);
			}

			nint target = FindImplementation(type, interfaceType, slot);
			if (target == 0)
				ThrowHelper.FailFast("FAILFAST: no implementation for an interface method (variant or dynamic interfaces are not supported)");

			if (cache == null)
			{
				Cache created = new Cache { Interface = interfaceType, Slot = slot };
				nint pointer = Unsafe.As<Cache, nint>(ref created);
				nint seen = Interlocked.CompareExchange(ref cell->Cache, pointer, word);
				if (seen == word)
				{
					cache = created;
				}
				else
				{
					Allocator.FreeObject(created);
					if ((seen & FlagsMask) == 0 && (nuint)seen >= (nuint)MaxVTableOffsetPlusOne)
						cache = Unsafe.As<nint, Cache>(ref seen);
				}
			}
			if (cache != null)
				Remember(cache, type, target);
			return target;
		}

		// The target is written before the type, so the stub never sees a type with a stale target
		private static void Remember(Cache cache, MethodTable* type, nint target)
		{
			switch (cache.Count++ & 3)
			{
				case 0:
					cache.Type0 = null;
					cache.Target0 = target;
					cache.Type0 = type;
					break;
				case 1:
					cache.Type1 = null;
					cache.Target1 = target;
					cache.Type1 = type;
					break;
				case 2:
					cache.Type2 = null;
					cache.Target2 = target;
					cache.Type2 = type;
					break;
				default:
					cache.Type3 = null;
					cache.Target3 = target;
					cache.Type3 = type;
					break;
			}
		}

		// Cells for one interface slot sit together, closed by a cell with no stub whose second word holds the cell kind and the slot
		private static void Decode(Cell* cell, out MethodTable* interfaceType, out uint slot)
		{
			nint word = cell->Cache;
			Cell* sentinel = cell;
			while (sentinel->Stub != 0)
				sentinel++;
			nint kindAndSlot = sentinel->Cache;
			if ((kindAndSlot >> 16) != 0)
				ThrowHelper.FailFast("FAILFAST: a metadata token dispatch cell");
			slot = (ushort)kindAndSlot;

			switch (word & FlagsMask)
			{
				case CachePointerIsInterfacePointer:
					interfaceType = (MethodTable*)(word & ~FlagsMask);
					break;
				case CachePointerIsInterfaceRelativePointer:
					interfaceType = (MethodTable*)(((nint)(&cell->Cache) + (int)word) & ~FlagsMask);
					break;
				default:
					interfaceType = *(MethodTable**)(((nint)(&cell->Cache) + (int)word) & ~FlagsMask);
					break;
			}
		}

		// Walks the type and its bases for an entry in a dispatch map, exact implementations first and then default interface methods
		internal static nint FindImplementation(MethodTable* type, MethodTable* interfaceType, uint slot)
		{
			for (int pass = 0; pass < 2; pass++)
			{
				for (MethodTable* current = type; current != null; current = current->BaseType)
				{
					if (!FindSlot(current, interfaceType, slot, pass == 1, out ushort implementation))
						continue;
					if (implementation < current->NumVtableSlots)
						return type->VTable[implementation];
					if (implementation == ReabstractionSlot || implementation == DiamondSlot)
						ThrowHelper.FailFast("FAILFAST: an interface method with no usable default implementation");
					return current->GetSealedVirtualSlot((ushort)(implementation - current->NumVtableSlots));
				}
			}
			return 0;
		}

		private static bool FindSlot(MethodTable* type, MethodTable* interfaceType, uint slot, bool defaults, out ushort implementation)
		{
			implementation = 0;
			if (!interfaceType->IsInterface)
			{
				implementation = (ushort)slot;
				return type == interfaceType;
			}
			DispatchMap* map = type->DispatchMap;
			if (map == null)
				return false;

			int start = defaults ? (int)map->NumStandardEntries : 0;
			int end = defaults ? (int)(map->NumStandardEntries + map->NumDefaultEntries) : (int)map->NumStandardEntries;
			DispatchMapEntry* entries = map->Entries;
			MethodTable** interfaces = type->InterfaceMap;
			for (int i = start; i < end; i++)
			{
				if (entries[i].InterfaceMethodSlot == slot && interfaces[entries[i].InterfaceIndex] == interfaceType)
				{
					implementation = entries[i].ImplMethodSlot;
					return true;
				}
			}
			return false;
		}
	}
}
