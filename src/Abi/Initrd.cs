using System;
using System.Runtime.InteropServices;

namespace Alloy.Abi
{
	// initrd.img: header, entry table, then each file page-aligned so the kernel can map it directly
	public struct InitrdHeader
	{
		public const ulong ExpectedMagic = 0x5254494E49584850; // "PHXINITR"
		public const uint CurrentVersion = 1;

		public ulong Magic;
		public uint Version;
		public uint EntryCount;
		public ulong EntryTableOffset;
		public ulong ImageSize;
	}

	public struct InitrdEntry
	{
		public const int MaxNameLength = 104;
		public const int DataAlignment = 4096;
		// Offset of Name within the record
		public const int NameOffset = 24;

		public ulong Offset;
		public ulong Size;
		public ushort NameLength;
		public ushort Reserved0;
		public uint Reserved1;
		// ASCII, '/' separated, no leading slash
		public unsafe fixed byte Name[MaxNameLength];
	}

	public readonly ref struct InitrdReader
	{
		private readonly ReadOnlySpan<byte> _image;
		private readonly int _count;
		private readonly int _tableOffset;

		private InitrdReader(ReadOnlySpan<byte> image, int count, int tableOffset)
		{
			_image = image;
			_count = count;
			_tableOffset = tableOffset;
		}

		public int Count => _count;

		// Validates the header and every entry's bounds up front, so the accessors never index out of the image
		public static unsafe bool TryOpen(ReadOnlySpan<byte> image, out InitrdReader reader)
		{
			reader = default;
			if (!MemoryMarshal.TryRead(image, out InitrdHeader header))
				return false;
			if (header.Magic != InitrdHeader.ExpectedMagic || header.Version != InitrdHeader.CurrentVersion || header.ImageSize > (ulong)image.Length)
				return false;

			ulong tableEnd = header.EntryTableOffset + (ulong)header.EntryCount * (ulong)sizeof(InitrdEntry);
			if (header.EntryTableOffset < (ulong)sizeof(InitrdHeader) || tableEnd > header.ImageSize)
				return false;

			InitrdReader candidate = new InitrdReader(image.Slice(0, (int)header.ImageSize), (int)header.EntryCount, (int)header.EntryTableOffset);
			for (int i = 0; i < candidate._count; i++)
			{
				InitrdEntry entry = candidate.GetEntry(i);
				if (entry.NameLength == 0 || entry.NameLength > InitrdEntry.MaxNameLength)
					return false;
				if (entry.Offset > header.ImageSize || entry.Size > header.ImageSize - entry.Offset)
					return false;
			}

			reader = candidate;
			return true;
		}

		public unsafe InitrdEntry GetEntry(int index)
		{
			if ((uint)index >= (uint)_count)
				return default;
			return MemoryMarshal.Read<InitrdEntry>(_image.Slice(_tableOffset + index * sizeof(InitrdEntry)));
		}

		public unsafe ReadOnlySpan<byte> GetName(int index)
		{
			if ((uint)index >= (uint)_count)
				return default;
			int nameOffset = _tableOffset + index * sizeof(InitrdEntry) + InitrdEntry.NameOffset;
			return _image.Slice(nameOffset, GetEntry(index).NameLength);
		}

		public ReadOnlySpan<byte> GetData(int index)
		{
			if ((uint)index >= (uint)_count)
				return default;
			InitrdEntry entry = GetEntry(index);
			return _image.Slice((int)entry.Offset, (int)entry.Size);
		}

		public bool TryFind(ReadOnlySpan<char> name, out ReadOnlySpan<byte> data)
		{
			for (int i = 0; i < _count; i++)
			{
				if (NameEquals(GetName(i), name))
				{
					data = GetData(i);
					return true;
				}
			}
			data = default;
			return false;
		}

		private static bool NameEquals(ReadOnlySpan<byte> ascii, ReadOnlySpan<char> name)
		{
			if (ascii.Length != name.Length)
				return false;
			for (int i = 0; i < ascii.Length; i++)
			{
				if (ascii[i] != name[i])
					return false;
			}
			return true;
		}
	}
}
