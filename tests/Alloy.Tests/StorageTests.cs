using System.Buffers.Binary;
using System.Text;
using Alloy.Kernel.Io;

namespace Alloy.Tests;

public class Crc32Tests
{
	[Fact]
	public void MatchesTheCheckValueWholeOrInParts()
	{
		byte[] data = Encoding.ASCII.GetBytes("123456789");
		Assert.Equal(0xCBF43926u, Crc32.Of(data));

		Crc32 crc = Crc32.Begin();
		crc.Add(data.AsSpan(0, 4));
		crc.Add(data.AsSpan(4));
		Assert.Equal(0xCBF43926u, crc.Value);
		Assert.Equal(0u, Crc32.Of(ReadOnlySpan<byte>.Empty));
	}
}

public class GptTests
{
	private const uint Entries = 128;
	private const uint EntrySize = 128;

	private readonly record struct TestPartition(ulong TypeLow, ulong TypeHigh, ulong First, ulong Last, string Name);

	private static readonly TestPartition[] Two =
	[
		new(Gpt.EfiSystemLow, Gpt.EfiSystemHigh, 2048, 4095, "EFI system partition"),
		new(Gpt.BasicDataLow, Gpt.BasicDataHigh, 4096, 8191, "Alloy"),
	];

	// Writes a primary GPT as DiscUtils lays one out: the header in sector 1, 128 entries from sector 2, both CRCs filled in
	private static void WriteTable(MemoryDisk disk, params TestPartition[] partitions)
	{
		int sectorSize = disk.SectorSize;
		ulong entrySectors = (Entries * EntrySize + (ulong)sectorSize - 1) / (ulong)sectorSize;
		for (ulong s = 0; s < entrySectors; s++)
			disk.Sector(2 + s).Clear();
		for (int i = 0; i < partitions.Length; i++)
		{
			Span<byte> entry = EntryAt(disk, i);
			BinaryPrimitives.WriteUInt64LittleEndian(entry, partitions[i].TypeLow);
			BinaryPrimitives.WriteUInt64LittleEndian(entry.Slice(8), partitions[i].TypeHigh);
			BinaryPrimitives.WriteUInt64LittleEndian(entry.Slice(32), partitions[i].First);
			BinaryPrimitives.WriteUInt64LittleEndian(entry.Slice(40), partitions[i].Last);
			for (int c = 0; c < partitions[i].Name.Length; c++)
				BinaryPrimitives.WriteUInt16LittleEndian(entry.Slice(56 + 2 * c), partitions[i].Name[c]);
		}

		Span<byte> header = disk.Sector(1);
		header.Clear();
		Encoding.ASCII.GetBytes("EFI PART").CopyTo(header);
		BinaryPrimitives.WriteUInt32LittleEndian(header.Slice(8), 0x00010000);
		BinaryPrimitives.WriteUInt32LittleEndian(header.Slice(12), 92);
		BinaryPrimitives.WriteUInt64LittleEndian(header.Slice(24), 1);
		BinaryPrimitives.WriteUInt64LittleEndian(header.Slice(32), disk.SectorCount - 1);
		BinaryPrimitives.WriteUInt64LittleEndian(header.Slice(40), 2 + entrySectors);
		BinaryPrimitives.WriteUInt64LittleEndian(header.Slice(48), disk.SectorCount - 2 - entrySectors);
		BinaryPrimitives.WriteUInt64LittleEndian(header.Slice(72), 2);
		BinaryPrimitives.WriteUInt32LittleEndian(header.Slice(80), Entries);
		BinaryPrimitives.WriteUInt32LittleEndian(header.Slice(84), EntrySize);
		ResealEntries(disk);
	}

	// The entries sit from sector 2 on, contiguous whatever the sector size
	private static Span<byte> EntryAt(MemoryDisk disk, int index)
	{
		int perSector = disk.SectorSize / (int)EntrySize;
		return disk.Sector(2 + (ulong)(index / perSector)).Slice(index % perSector * (int)EntrySize, (int)EntrySize);
	}

	// Puts the header's CRC right again after a test changes a field in it
	private static void ResealHeader(MemoryDisk disk)
	{
		Span<byte> header = disk.Sector(1);
		BinaryPrimitives.WriteUInt32LittleEndian(header.Slice(16), 0);
		BinaryPrimitives.WriteUInt32LittleEndian(header.Slice(16), Crc32.Of(header.Slice(0, 92)));
	}

	// And the entries' CRC, then the header's that covers it
	private static void ResealEntries(MemoryDisk disk)
	{
		Crc32 crc = Crc32.Begin();
		for (int i = 0; i < Entries; i++)
			crc.Add(EntryAt(disk, i));
		BinaryPrimitives.WriteUInt32LittleEndian(disk.Sector(1).Slice(88), crc.Value);
		ResealHeader(disk);
	}

	[Fact]
	public void ReadsTheBootAndDataPartitionsWhole()
	{
		MemoryDisk disk = new(512, 16384);
		WriteTable(disk, Two);

		Assert.True(Gpt.Read(disk, out GptTable table));
		Assert.Equal(2, table.Count);
		Assert.True(table.Partitions[0].IsType(Gpt.EfiSystemLow, Gpt.EfiSystemHigh));
		Assert.Equal(2048UL, table.Partitions[0].FirstSector);
		Assert.Equal(2048UL, table.Partitions[0].SectorCount);
		Assert.True(table.Partitions[0].NameIs("EFI system partition"));
		Assert.True(table.Partitions[1].IsType(Gpt.BasicDataLow, Gpt.BasicDataHigh));
		Assert.Equal(4096UL, table.Partitions[1].FirstSector);
		Assert.Equal(4096UL, table.Partitions[1].SectorCount);
		Assert.True(table.Partitions[1].NameIs("Alloy"));
	}

	[Fact]
	public void FourKilobyteSectorsHoldThirtyTwoEntriesEach()
	{
		MemoryDisk disk = new(4096, 4096);
		WriteTable(disk, new TestPartition(Gpt.EfiSystemLow, Gpt.EfiSystemHigh, 256, 1023, "ESP"));

		Assert.True(Gpt.Read(disk, out GptTable table));
		Assert.Equal(1, table.Count);
		Assert.Equal(768UL, table.Partitions[0].SectorCount);
	}

	[Fact]
	public void RefusesAMissingSignatureOrAFailedCrc()
	{
		MemoryDisk disk = new(512, 16384);
		Assert.False(Gpt.Read(disk, out GptTable table));

		WriteTable(disk, Two);
		disk.Sector(1)[40] ^= 1;
		Assert.False(Gpt.Read(disk, out table));

		WriteTable(disk, Two);
		disk.Sector(2)[32] ^= 1;
		Assert.False(Gpt.Read(disk, out table));
		Assert.Equal(0, table.Count);
	}

	[Fact]
	public void RefusesUnsoundFieldsUnderSoundCrcs()
	{
		MemoryDisk disk = new(512, 16384);

		// An entry past the usable sectors
		WriteTable(disk, Two);
		BinaryPrimitives.WriteUInt64LittleEndian(EntryAt(disk, 1).Slice(40), disk.SectorCount - 1);
		ResealEntries(disk);
		Assert.False(Gpt.Read(disk, out _));

		// A header that isn't at sector 1
		WriteTable(disk, Two);
		BinaryPrimitives.WriteUInt64LittleEndian(disk.Sector(1).Slice(24), 2);
		ResealHeader(disk);
		Assert.False(Gpt.Read(disk, out _));

		// An entry size that splits sectors
		WriteTable(disk, Two);
		BinaryPrimitives.WriteUInt32LittleEndian(disk.Sector(1).Slice(84), 384);
		ResealHeader(disk);
		Assert.False(Gpt.Read(disk, out _));
	}

	[Fact]
	public void SkipsPartitionsPastTheEighth()
	{
		MemoryDisk disk = new(512, 16384);
		TestPartition[] ten = new TestPartition[10];
		for (int i = 0; i < 10; i++)
			ten[i] = new TestPartition(Gpt.BasicDataLow, Gpt.BasicDataHigh, 2048 + (ulong)i * 1024, 2048 + (ulong)i * 1024 + 1023, "Data");
		WriteTable(disk, ten);

		Assert.True(Gpt.Read(disk, out GptTable table));
		Assert.Equal(Gpt.MaxPartitions, table.Count);
		Assert.Equal(2048UL + 7 * 1024, table.Partitions[7].FirstSector);
	}

	[Fact]
	public void APartitionIsAWindowOntoItsSectors()
	{
		MemoryDisk disk = new(512, 64);
		for (ulong s = 0; s < disk.SectorCount; s++)
			disk.Sector(s)[0] = (byte)s;

		Partition partition = new(disk, 10, 20);
		Assert.Equal(20UL, partition.SectorCount);
		byte[] sector = new byte[512];
		Assert.True(partition.Read(0, 1, sector));
		Assert.Equal(10, sector[0]);
		Assert.True(partition.Read(19, 1, sector));
		Assert.Equal(29, sector[0]);
		Assert.False(partition.Read(20, 1, sector));
		Assert.False(partition.Read(19, 2, sector));

		sector[0] = 0xAA;
		Assert.True(partition.Write(5, 1, sector));
		Assert.Equal(0xAA, disk.Sector(15)[0]);
		Assert.False(partition.Write(20, 1, sector));
	}
}
