using System;
using System.Runtime.InteropServices;

namespace Alloy.Abi
{
	// kernel.sym: header, (rva, name offset) pairs sorted by rva, then NUL-terminated ASCII names (tools/BuildImage)
	public readonly ref struct SymbolTable
	{
		public const ulong ExpectedMagic = 0x31534D5953584850; // "PHXSYMS1"
		public const int HeaderSize = 16;
		public const int EntrySize = 8;

		private readonly ReadOnlySpan<byte> _data;
		private readonly int _count;

		private SymbolTable(ReadOnlySpan<byte> data, int count)
		{
			_data = data;
			_count = count;
		}

		public int Count => _count;

		public static bool TryOpen(ReadOnlySpan<byte> data, out SymbolTable table)
		{
			table = default;
			if (!MemoryMarshal.TryRead(data, out ulong magic) || magic != ExpectedMagic)
				return false;
			int count = MemoryMarshal.Read<int>(data.Slice(8));
			if (count < 0 || HeaderSize + (long)count * EntrySize > data.Length)
				return false;
			table = new SymbolTable(data, count);
			return true;
		}

		// The symbol at or below rva; offset is how far past its start rva lies
		public bool TryLookup(uint rva, out ReadOnlySpan<byte> name, out uint offset)
		{
			name = default;
			offset = 0;
			int low = 0;
			int high = _count - 1;
			int found = -1;
			while (low <= high)
			{
				int middle = (low + high) / 2;
				if (Rva(middle) <= rva)
				{
					found = middle;
					low = middle + 1;
				}
				else
				{
					high = middle - 1;
				}
			}
			if (found < 0)
				return false;

			int names = HeaderSize + _count * EntrySize;
			int start = names + MemoryMarshal.Read<int>(_data.Slice(HeaderSize + found * EntrySize + 4));
			if (start < names || start >= _data.Length)
				return false;
			int length = _data.Slice(start).IndexOf((byte)0);
			name = length < 0 ? _data.Slice(start) : _data.Slice(start, length);
			offset = rva - Rva(found);
			return true;
		}

		private uint Rva(int index) => MemoryMarshal.Read<uint>(_data.Slice(HeaderSize + index * EntrySize));
	}
}
