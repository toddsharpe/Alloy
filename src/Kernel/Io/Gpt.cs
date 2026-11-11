using System;
using System.Runtime.CompilerServices;

namespace Alloy.Kernel.Io
{
	// One partition from the table: its type GUID as the two little-endian halves the disk stores, its run of sectors and its name
	internal struct GptPartition
	{
		public ulong TypeLow;
		public ulong TypeHigh;
		public ulong FirstSector;
		public ulong SectorCount;
		public int NameLength;
		public GptNameBytes Name;

		public bool IsType(ulong low, ulong high) => TypeLow == low && TypeHigh == high;

		// The UTF-16 name as ASCII, with '?' for the rest
		public readonly bool NameIs(ReadOnlySpan<char> name)
		{
			if (name.Length != NameLength)
				return false;
			ReadOnlySpan<byte> bytes = Name;
			for (int i = 0; i < NameLength; i++)
			{
				if (bytes[i] != name[i])
					return false;
			}
			return true;
		}
	}

	[InlineArray(Gpt.MaxNameLength)]
	internal struct GptNameBytes
	{
		private byte _element0;
	}

	[InlineArray(Gpt.MaxPartitions)]
	internal struct GptPartitions
	{
		private GptPartition _element0;
	}

	// The partitions kept from a table; more than MaxPartitions are skipped
	internal struct GptTable
	{
		public int Count;
		public GptPartitions Partitions;
	}

	// The GUID partition table: the header in sector 1 and its array of entries, each checked by its CRC
	internal static class Gpt
	{
		public const int MaxPartitions = 8;
		public const int MaxNameLength = 36;

		// Type GUIDs in the halves the disk stores them as (the first three fields little-endian)
		public const ulong EfiSystemLow = 0xC12A7328 | (0xF81FUL << 32) | (0x11D2UL << 48);
		public const ulong EfiSystemHigh = 0xBA | (0x4BUL << 8) | (0x00UL << 16) | (0xA0UL << 24) | (0xC9UL << 32) | (0x3EUL << 40) | (0xC9UL << 48) | (0x3BUL << 56);
		public const ulong BasicDataLow = 0xEBD0A0A2 | (0xB9E5UL << 32) | (0x4433UL << 48);
		public const ulong BasicDataHigh = 0x87 | (0xC0UL << 8) | (0x68UL << 16) | (0xB6UL << 24) | (0xB7UL << 32) | (0x26UL << 40) | (0x99UL << 48) | (0xC7UL << 56);

		private const int MinSectorSize = 512;
		private const int MaxSectorSize = 4096;
		private const uint HeaderSize = 92;
		private const int HeaderCrcOffset = 16;
		private const uint MinEntrySize = 128;

		// A corrupt entry count can't send the read across the disk
		private const ulong MaxEntriesBytes = 1024 * 1024;

		private const int EntryNameOffset = 56;

		// False when the disk has no GPT, or its header or entries don't check out; only the primary table is read
		public static bool Read(BlockDevice disk, out GptTable table)
		{
			table = default;
			int sectorSize = disk.SectorSize;
			if (sectorSize < MinSectorSize || sectorSize > MaxSectorSize || disk.SectorCount < 3)
				return false;

			Span<byte> sector = stackalloc byte[MaxSectorSize];
			sector = sector.Slice(0, sectorSize);
			if (!disk.Read(1, 1, sector) || !IsSignature(sector))
				return false;

			// The header's CRC is over its own size with the CRC field taken as zero
			uint headerSize = Read32(sector, 12);
			uint headerCrc = Read32(sector, HeaderCrcOffset);
			if (headerSize < HeaderSize || headerSize > (uint)sectorSize)
				return false;
			sector.Slice(HeaderCrcOffset, 4).Clear();
			if (Crc32.Of(sector.Slice(0, (int)headerSize)) != headerCrc)
				return false;

			ulong myLba = Read64(sector, 24);
			ulong firstUsable = Read64(sector, 40);
			ulong lastUsable = Read64(sector, 48);
			ulong entriesLba = Read64(sector, 72);
			uint entryCount = Read32(sector, 80);
			uint entrySize = Read32(sector, 84);
			uint entriesCrc = Read32(sector, 88);
			ulong entriesBytes = (ulong)entryCount * entrySize;
			if (myLba != 1 || firstUsable > lastUsable || lastUsable >= disk.SectorCount ||
				entrySize < MinEntrySize || (uint)sectorSize % entrySize != 0 || entriesBytes > MaxEntriesBytes)
				return false;

			ulong entrySectors = (entriesBytes + (ulong)sectorSize - 1) / (ulong)sectorSize;
			if (entriesLba < 2 || entrySectors > disk.SectorCount || entriesLba > disk.SectorCount - entrySectors)
				return false;

			// Each entry goes into the CRC as it's read; one that's used must lie in the usable sectors
			Crc32 crc = Crc32.Begin();
			bool sound = true;
			uint index = 0;
			for (ulong at = 0; at < entrySectors; at++)
			{
				if (!disk.Read(entriesLba + at, 1, sector))
					return false;

				for (int offset = 0; offset < sectorSize && index < entryCount; offset += (int)entrySize, index++)
				{
					ReadOnlySpan<byte> entry = sector.Slice(offset, (int)entrySize);
					crc.Add(entry);

					ulong typeLow = Read64(entry, 0);
					ulong typeHigh = Read64(entry, 8);
					if (typeLow == 0 && typeHigh == 0)
						continue;

					ulong first = Read64(entry, 32);
					ulong last = Read64(entry, 40);
					if (first < firstUsable || last > lastUsable || first > last)
					{
						sound = false;
						continue;
					}

					if (table.Count == MaxPartitions)
						continue;

					ref GptPartition partition = ref table.Partitions[table.Count++];
					partition.TypeLow = typeLow;
					partition.TypeHigh = typeHigh;
					partition.FirstSector = first;
					partition.SectorCount = last - first + 1;
					Span<byte> name = partition.Name;
					for (int i = 0; i < MaxNameLength; i++)
					{
						ushort unit = Read16(entry, EntryNameOffset + 2 * i);
						if (unit == 0)
							break;
						name[i] = unit >= 0x20 && unit < 0x7F ? (byte)unit : (byte)'?';
						partition.NameLength = i + 1;
					}
				}
			}

			if (!sound || crc.Value != entriesCrc)
			{
				table = default;
				return false;
			}
			return true;
		}

		// "EFI PART", the header's first eight bytes
		private static bool IsSignature(ReadOnlySpan<byte> sector) =>
			sector[0] == (byte)'E' && sector[1] == (byte)'F' && sector[2] == (byte)'I' && sector[3] == (byte)' ' && sector[4] == (byte)'P' && sector[5] == (byte)'A' && sector[6] == (byte)'R' && sector[7] == (byte)'T';

		private static ushort Read16(ReadOnlySpan<byte> bytes, int offset) => (ushort)(bytes[offset] | (bytes[offset + 1] << 8));

		private static uint Read32(ReadOnlySpan<byte> bytes, int offset) => (uint)(bytes[offset] | (bytes[offset + 1] << 8) | (bytes[offset + 2] << 16) | (bytes[offset + 3] << 24));

		private static ulong Read64(ReadOnlySpan<byte> bytes, int offset) => Read32(bytes, offset) | ((ulong)Read32(bytes, offset + 4) << 32);
	}
}
