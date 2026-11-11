using System.Buffers.Binary;
using System.Text;
using Alloy.Abi;
using Alloy.Kernel.Io;

namespace Alloy.Tests;

// A FAT32 volume laid out as Microsoft's spec has it, just big enough to count as FAT32: 512-byte sectors, a sector a cluster, 32 reserved sectors and two FATs
internal sealed class FatTestVolume
{
	public const uint Sectors = 70000;
	public const uint Reserved = 32;
	public const uint FatSectors = 545;
	public const uint Root = 2;
	public const uint Eoc = 0x0FFFFFFF;
	public const uint Clusters = Sectors - Reserved - 2 * FatSectors;

	private static readonly int[] LongNameOffsets = [1, 3, 5, 7, 9, 14, 16, 18, 20, 22, 24, 28, 30];

	public readonly MemoryDisk Disk = new(512, Sectors);
	private uint _next = Root + 1;

	public FatTestVolume()
	{
		WriteBootSector(Disk.Sector(0), Sectors, FatSectors);
		SetFat(0, 0x0FFFFFF8);
		SetFat(1, Eoc);
		SetFat(Root, Eoc);
	}

	public static void WriteBootSector(Span<byte> boot, uint sectors, uint fatSectors)
	{
		boot[0] = 0xEB;
		boot[1] = 0x58;
		boot[2] = 0x90;
		Encoding.ASCII.GetBytes("MSWIN4.1").CopyTo(boot.Slice(3));
		BinaryPrimitives.WriteUInt16LittleEndian(boot.Slice(11), 512);
		boot[13] = 1;
		BinaryPrimitives.WriteUInt16LittleEndian(boot.Slice(14), (ushort)Reserved);
		boot[16] = 2;
		boot[21] = 0xF8;
		BinaryPrimitives.WriteUInt32LittleEndian(boot.Slice(32), sectors);
		BinaryPrimitives.WriteUInt32LittleEndian(boot.Slice(36), fatSectors);
		BinaryPrimitives.WriteUInt32LittleEndian(boot.Slice(44), Root);
		BinaryPrimitives.WriteUInt16LittleEndian(boot.Slice(48), 1);
		BinaryPrimitives.WriteUInt16LittleEndian(boot.Slice(50), 6);
		boot[66] = 0x29;
		Encoding.ASCII.GetBytes("FAT32   ").CopyTo(boot.Slice(82));
		boot[510] = 0x55;
		boot[511] = 0xAA;
	}

	public void SetFat(uint cluster, uint value)
	{
		for (uint fat = 0; fat < 2; fat++)
			BinaryPrimitives.WriteUInt32LittleEndian(FatEntry(fat, cluster), value);
	}

	public uint GetFat(uint cluster) => BinaryPrimitives.ReadUInt32LittleEndian(FatEntry(0, cluster));

	// The four bytes of a cluster's entry in one of the FATs
	private Span<byte> FatEntry(uint fat, uint cluster) => Disk.Sector(Reserved + fat * FatSectors + cluster * 4 / 512).Slice((int)(cluster * 4 % 512), 4);

	public void WriteFsInfo(uint freeCount, uint nextFree)
	{
		Span<byte> info = Disk.Sector(1);
		BinaryPrimitives.WriteUInt32LittleEndian(info, 0x41615252);
		BinaryPrimitives.WriteUInt32LittleEndian(info.Slice(484), 0x61417272);
		BinaryPrimitives.WriteUInt32LittleEndian(info.Slice(488), freeCount);
		BinaryPrimitives.WriteUInt32LittleEndian(info.Slice(492), nextFree);
		BinaryPrimitives.WriteUInt32LittleEndian(info.Slice(508), 0xAA550000);
	}

	public uint FsInfoFree() => BinaryPrimitives.ReadUInt32LittleEndian(Disk.Sector(1).Slice(488));

	public Span<byte> FindEntry(uint directory, string shortName) => FindEntry(directory, shortName, out _);

	// The short entry with the 11 padded 8.3 characters along the directory's chain, empty if it isn't there; longName says whether long name entries tied to it by checksum come first
	public Span<byte> FindEntry(uint directory, string shortName, out bool longName)
	{
		byte[] wanted = Encoding.Latin1.GetBytes(shortName);
		bool tied = false;
		byte sum = 0;
		longName = false;
		for (uint cluster = directory; cluster >= 2 && cluster < 0x0FFFFFF8; cluster = GetFat(cluster))
		{
			Span<byte> data = Cluster(cluster);
			for (int offset = 0; offset < 512; offset += 32)
			{
				Span<byte> entry = data.Slice(offset, 32);
				if (entry[0] == 0xE5)
				{
					tied = false;
					continue;
				}
				if (entry[11] == 0x0F)
				{
					tied = true;
					sum = entry[13];
					continue;
				}

				if (entry.Slice(0, 11).SequenceEqual(wanted))
				{
					longName = tied && sum == Checksum(entry);
					return entry;
				}
				tied = false;
			}
		}
		return default;
	}

	public static byte Checksum(ReadOnlySpan<byte> entry)
	{
		byte checksum = 0;
		for (int i = 0; i < 11; i++)
			checksum = (byte)(((checksum & 1) << 7) + (checksum >> 1) + entry[i]);
		return checksum;
	}

	public static uint EntryCluster(ReadOnlySpan<byte> entry) =>
		((uint)BinaryPrimitives.ReadUInt16LittleEndian(entry.Slice(20)) << 16) | BinaryPrimitives.ReadUInt16LittleEndian(entry.Slice(26));

	// Clusters the FAT says are free, to check the free count against
	public uint CountFree()
	{
		uint free = 0;
		for (uint cluster = 2; cluster < Clusters + 2; cluster++)
			free += (GetFat(cluster) & 0x0FFFFFFF) == 0 ? 1u : 0u;
		return free;
	}

	public Span<byte> Cluster(uint cluster) => Disk.Sector(Reserved + 2 * FatSectors + (cluster - 2));

	// A file's data in a chain of clusters (each but the first a cluster apart when fragmented) and its entry; returns its first cluster
	public uint AddFile(uint directory, string shortName, string? longName, ReadOnlySpan<byte> data, bool fragmented = false, byte checksumSkew = 0)
	{
		uint first = 0;
		uint previous = 0;
		for (int at = 0; at < data.Length; at += 512)
		{
			if (fragmented && previous != 0)
				_next++;
			uint cluster = _next++;
			data.Slice(at, Math.Min(512, data.Length - at)).CopyTo(Cluster(cluster));
			if (previous != 0)
				SetFat(previous, cluster);
			else
				first = cluster;
			previous = cluster;
		}
		if (previous != 0)
			SetFat(previous, Eoc);

		AddEntry(directory, shortName, longName, 0x20, first, (uint)data.Length, checksumSkew);
		return first;
	}

	public uint AddDirectory(uint parent, string shortName, string? longName)
	{
		uint cluster = _next++;
		SetFat(cluster, Eoc);
		Cluster(cluster).Clear();
		AddEntry(cluster, ".          ", null, 0x10, cluster, 0);
		AddEntry(cluster, "..         ", null, 0x10, parent == Root ? 0 : parent, 0);
		AddEntry(parent, shortName, longName, 0x10, cluster, 0);
		return cluster;
	}

	// The short entry, after its long name's entries (last part first) when it has one; shortName is the 11 padded 8.3 characters
	public void AddEntry(uint directory, string shortName, string? longName, byte attributes, uint cluster, uint size, byte checksumSkew = 0)
	{
		byte[] entry = new byte[32];
		Encoding.Latin1.GetBytes(shortName).CopyTo(entry, 0);
		entry[11] = attributes;
		BinaryPrimitives.WriteUInt16LittleEndian(entry.AsSpan(20), (ushort)(cluster >> 16));
		BinaryPrimitives.WriteUInt16LittleEndian(entry.AsSpan(26), (ushort)(cluster & 0xFFFF));
		BinaryPrimitives.WriteUInt32LittleEndian(entry.AsSpan(28), size);

		if (longName != null)
		{
			byte checksum = (byte)(Checksum(entry) + checksumSkew);
			int parts = (longName.Length + 12) / 13;
			for (int part = parts; part >= 1; part--)
			{
				byte[] lfn = new byte[32];
				lfn[0] = (byte)(part | (part == parts ? 0x40 : 0));
				lfn[11] = 0x0F;
				lfn[13] = checksum;
				for (int k = 0; k < 13; k++)
				{
					int at = (part - 1) * 13 + k;
					ushort unit = at < longName.Length ? (ushort)(byte)longName[at] : at == longName.Length ? (ushort)0x0000 : (ushort)0xFFFF;
					BinaryPrimitives.WriteUInt16LittleEndian(lfn.AsSpan(LongNameOffsets[k]), unit);
				}
				lfn.CopyTo(FreeSlot(directory));
			}
		}
		entry.CopyTo(FreeSlot(directory));
	}

	// The directory's first unused entry, its chain grown when it's full
	private Span<byte> FreeSlot(uint directory)
	{
		uint cluster = directory;
		while (true)
		{
			Span<byte> data = Cluster(cluster);
			for (int offset = 0; offset < 512; offset += 32)
			{
				if (data[offset] == 0x00)
					return data.Slice(offset, 32);
			}

			uint next = GetFat(cluster);
			if (next >= 0x0FFFFFF8)
			{
				next = _next++;
				SetFat(cluster, next);
				SetFat(next, Eoc);
				Cluster(next).Clear();
			}
			cluster = next;
		}
	}
}

public class Fat32Tests
{
	private static readonly byte[] Data = BuildData();

	private static byte[] BuildData()
	{
		byte[] data = new byte[8192];
		for (int i = 0; i < data.Length; i++)
			data[i] = (byte)(i * 7 + 3);
		return data;
	}

	// Reads the rest of an open file in chunks, as the kernel does
	private static int ReadAll(Fat32 fs, ref FileState file, Span<byte> output, int chunk)
	{
		int total = 0;
		int got;
		while ((got = fs.Read(ref file, output.Slice(total, Math.Min(chunk, output.Length - total)))) != 0)
		{
			file.Position += got;
			total += got;
		}
		return total;
	}

	// Writes in chunks, moving the position as the kernel does
	private static int WriteAll(Fat32 fs, ref FileState file, ReadOnlySpan<byte> data, int chunk)
	{
		int total = 0;
		while (total < data.Length)
		{
			int got = fs.Write(ref file, data.Slice(total, Math.Min(chunk, data.Length - total)));
			if (got == 0)
				break;
			file.Position += got;
			total += got;
		}
		return total;
	}

	// Closes what the test had open, as closing its handle would
	private static void Close(Fat32 fs, ref FileState file)
	{
		if (file.Access != FileAccess.None)
			fs.Close(ref file);
		file = default;
	}

	private static bool Open(Fat32 fs, ref FileState file, string path, FileAccess access, FileCreate create)
	{
		Close(fs, ref file);
		return fs.Open(ref file, path, access, create);
	}

	// The whole of a file, read from its start
	private static bool Same(Fat32 fs, string path, ReadOnlySpan<byte> expected)
	{
		byte[] output = new byte[8192];
		FileState file = default;
		if (!Open(fs, ref file, path, FileAccess.Read, FileCreate.OpenExisting))
			return false;

		bool same = file.Length == expected.Length && ReadAll(fs, ref file, output, 777) == expected.Length && output.AsSpan(0, expected.Length).SequenceEqual(expected);
		Close(fs, ref file);
		return same;
	}

	private static string NameOf(in DirectoryEntry entry)
	{
		ReadOnlySpan<byte> name = entry.Name;
		int length = name.IndexOf((byte)0);
		return Encoding.ASCII.GetString(length < 0 ? name : name.Slice(0, length));
	}

	private static bool Listed(DirectoryEntry[] entries, int count, int index, string name, uint size, bool directory) =>
		index < count && NameOf(in entries[index]) == name && entries[index].Size == size && (entries[index].Directory != 0) == directory;

	// The whole volume is built before it's mounted, so the cache never holds a stale sector
	private static FatTestVolume ReadVolume()
	{
		FatTestVolume volume = new();
		volume.AddFile(FatTestVolume.Root, "README  TXT", null, Data.AsSpan(0, 100));
		volume.AddFile(FatTestVolume.Root, "ALLOYW~1EXE", "Alloy-WMSvr.exe", Data.AsSpan(0, 3000));
		uint efi = volume.AddDirectory(FatTestVolume.Root, "EFI        ", null);
		uint boot = volume.AddDirectory(efi, "BOOT       ", null);
		volume.AddFile(boot, "TERM    EXE", "term.exe", Data.AsSpan(0, 5000), true);
		volume.AddFile(boot, "EMPTY   TXT", null, Data.AsSpan(0, 0));
		volume.AddFile(FatTestVolume.Root, "\u00E5ONE    TXT", null, Data.AsSpan(0, 10));
		volume.AddFile(FatTestVolume.Root, "BADSUM  TXT", "Bad Checksum.txt", Data.AsSpan(0, 10), false, 1);
		uint cut = volume.AddFile(FatTestVolume.Root, "SHORT   BIN", null, Data.AsSpan(0, 2048));
		volume.SetFat(cut + 1, FatTestVolume.Eoc);
		uint many = volume.AddDirectory(FatTestVolume.Root, "MANY       ", null);
		for (int i = 0; i < 40; i++)
			volume.AddFile(many, $"FILE{i:D4}DAT", $"File number {i:D2}.dat", Data.AsSpan(i, 1));

		// Short names Windows gave lower case through byte 12 rather than a long name, and a volume label
		volume.AddFile(FatTestVolume.Root, "CASED   TXT", null, Data.AsSpan(0, 5));
		volume.FindEntry(FatTestVolume.Root, "CASED   TXT")[12] = 0x18;
		volume.AddFile(FatTestVolume.Root, "MIXED   TXT", null, Data.AsSpan(0, 6));
		volume.FindEntry(FatTestVolume.Root, "MIXED   TXT")[12] = 0x08;
		volume.AddEntry(FatTestVolume.Root, "ALLOY      ", null, 0x08, 0, 0);
		return volume;
	}

	private static Fat32 Mount(FatTestVolume volume)
	{
		Fat32 fs = new();
		Assert.True(fs.Mount(volume.Disk, new SectorCache()));
		return fs;
	}

	[Fact]
	public void ABlankOrSmallVolumeIsNotFat32()
	{
		MemoryDisk blank = new(512, 1000);
		Fat32 none = new();
		Assert.False(none.Mount(blank, new SectorCache()));

		MemoryDisk small = new(512, 20000);
		FatTestVolume.WriteBootSector(small.Sector(0), 20000, 160);
		Assert.False(none.Mount(small, new SectorCache()));
	}

	[Fact]
	public void OpensAShortNameInAnyCaseFromTheRoot()
	{
		Fat32 fs = Mount(ReadVolume());
		byte[] output = new byte[8192];
		FileState file = default;
		Assert.True(Open(fs, ref file, "readme.txt", FileAccess.Read, FileCreate.OpenExisting));
		Assert.Equal(100, file.Length);
		Assert.Equal(100, ReadAll(fs, ref file, output, 4096));
		Assert.True(output.AsSpan(0, 100).SequenceEqual(Data.AsSpan(0, 100)));
		Assert.True(Open(fs, ref file, "/README.TXT", FileAccess.Read, FileCreate.OpenExisting));
		Assert.True(Open(fs, ref file, "/./readme.txt", FileAccess.Read, FileCreate.OpenExisting));
	}

	[Fact]
	public void OpensALongNameInAnyCaseAndItsShortAlias()
	{
		Fat32 fs = Mount(ReadVolume());
		byte[] output = new byte[8192];
		FileState file = default;
		Assert.True(Open(fs, ref file, "alloy-wmsvr.EXE", FileAccess.Read, FileCreate.OpenExisting));
		Assert.Equal(3000, file.Length);
		Assert.Equal(3000, ReadAll(fs, ref file, output, 4096));
		Assert.True(output.AsSpan(0, 3000).SequenceEqual(Data.AsSpan(0, 3000)));
		Assert.True(Open(fs, ref file, "ALLOYW~1.EXE", FileAccess.Read, FileCreate.OpenExisting));
	}

	[Fact]
	public void AFileInConsecutiveClustersReadsWholeSectorsInOneRequest()
	{
		FatTestVolume volume = ReadVolume();
		Fat32 fs = Mount(volume);
		byte[] output = new byte[8192];
		FileState file = default;
		Assert.True(Open(fs, ref file, "/alloy-wmsvr.exe", FileAccess.Read, FileCreate.OpenExisting));
		int before = volume.Disk.Reads;
		Assert.Equal(2560, fs.Read(ref file, output.AsSpan(0, 2560)));
		Assert.Equal(before + 1, volume.Disk.Reads);
		Assert.True(output.AsSpan(0, 2560).SequenceEqual(Data.AsSpan(0, 2560)));
	}

	[Theory]
	[InlineData(1)]
	[InlineData(7)]
	[InlineData(511)]
	[InlineData(512)]
	[InlineData(513)]
	[InlineData(4096)]
	public void AFragmentedFileThroughNestedDirectoriesReadsTheSameInAnyChunk(int chunk)
	{
		Fat32 fs = Mount(ReadVolume());
		byte[] output = new byte[8192];
		FileState file = default;
		Assert.True(Open(fs, ref file, "\\EFI\\BOOT\\term.exe", FileAccess.Read, FileCreate.OpenExisting));
		Assert.Equal(5000, ReadAll(fs, ref file, output, chunk));
		Assert.True(output.AsSpan(0, 5000).SequenceEqual(Data.AsSpan(0, 5000)));
	}

	[Fact]
	public void APositionMovedForwardOrBackReadsFromThere()
	{
		Fat32 fs = Mount(ReadVolume());
		byte[] output = new byte[8192];
		FileState file = default;
		Assert.True(Open(fs, ref file, "/efi/boot/TERM.EXE", FileAccess.Read, FileCreate.OpenExisting));
		file.Position = 4000;
		Assert.Equal(600, fs.Read(ref file, output.AsSpan(0, 600)));
		Assert.True(output.AsSpan(0, 600).SequenceEqual(Data.AsSpan(4000, 600)));
		file.Position = 100;
		Assert.Equal(50, fs.Read(ref file, output.AsSpan(0, 50)));
		Assert.True(output.AsSpan(0, 50).SequenceEqual(Data.AsSpan(100, 50)));
	}

	[Fact]
	public void APathNotFromTheRootStartsInTheDefaultDirectory()
	{
		Fat32 fs = Mount(ReadVolume());
		FileState file = default;
		Assert.False(Open(fs, ref file, "term.exe", FileAccess.Read, FileCreate.OpenExisting));
		Assert.True(fs.SetDirectory("/efi/boot"));
		Assert.True(Open(fs, ref file, "term.exe", FileAccess.Read, FileCreate.OpenExisting));
		Assert.True(Open(fs, ref file, "./TERM.EXE", FileAccess.Read, FileCreate.OpenExisting));
		Assert.True(Open(fs, ref file, "../BOOT/term.exe", FileAccess.Read, FileCreate.OpenExisting));
		Assert.True(Open(fs, ref file, "..\\..\\readme.txt", FileAccess.Read, FileCreate.OpenExisting));
		Assert.True(Open(fs, ref file, "/readme.txt", FileAccess.Read, FileCreate.OpenExisting));
		Assert.False(fs.SetDirectory("/readme.txt"));
	}

	[Fact]
	public void MissingDirectoryOrDeletedNamesAreNotFiles()
	{
		Fat32 fs = Mount(ReadVolume());
		FileState file = default;
		Assert.False(Open(fs, ref file, "/missing.txt", FileAccess.Read, FileCreate.OpenExisting));
		Assert.False(Open(fs, ref file, "/EFI", FileAccess.Read, FileCreate.OpenExisting));
		Assert.False(Open(fs, ref file, "/readme.txt/x", FileAccess.Read, FileCreate.OpenExisting));
		Assert.False(Open(fs, ref file, "/gone.txt", FileAccess.Read, FileCreate.OpenExisting));
	}

	[Fact]
	public void AnEmptyFileAndAFileAtItsEndReadNothing()
	{
		Fat32 fs = Mount(ReadVolume());
		byte[] output = new byte[8192];
		FileState file = default;
		Assert.True(fs.SetDirectory("/efi/boot"));
		Assert.True(Open(fs, ref file, "empty.txt", FileAccess.Read, FileCreate.OpenExisting));
		Assert.Equal(0, file.Length);
		Assert.Equal(0, fs.Read(ref file, output.AsSpan(0, 10)));

		Assert.True(Open(fs, ref file, "/readme.txt", FileAccess.Read, FileCreate.OpenExisting));
		file.Position = 100;
		Assert.Equal(0, fs.Read(ref file, output.AsSpan(0, 10)));
	}

	[Fact]
	public void ALongNameWithTheWrongChecksumIsOnlyItsShortName()
	{
		Fat32 fs = Mount(ReadVolume());
		FileState file = default;
		Assert.False(Open(fs, ref file, "/Bad Checksum.txt", FileAccess.Read, FileCreate.OpenExisting));
		Assert.True(Open(fs, ref file, "/BADSUM.TXT", FileAccess.Read, FileCreate.OpenExisting));
	}

	[Fact]
	public void ADirectoryOfManyClustersIsSearchedToItsLastEntry()
	{
		Fat32 fs = Mount(ReadVolume());
		byte[] output = new byte[8192];
		FileState file = default;
		Assert.True(Open(fs, ref file, "/many/File number 39.dat", FileAccess.Read, FileCreate.OpenExisting));
		Assert.Equal(1, fs.Read(ref file, output.AsSpan(0, 1)));
		Assert.Equal(Data[39], output[0]);
		Assert.True(Open(fs, ref file, "/MANY/FILE0000.DAT", FileAccess.Read, FileCreate.OpenExisting));
	}

	[Fact]
	public void AChainThatEndsBeforeTheSizeGivesWhatItHas()
	{
		Fat32 fs = Mount(ReadVolume());
		byte[] output = new byte[8192];
		FileState file = default;
		Assert.True(Open(fs, ref file, "/short.bin", FileAccess.Read, FileCreate.OpenExisting));
		Assert.Equal(2048, file.Length);
		Assert.Equal(1024, ReadAll(fs, ref file, output, 4096));
	}

	[Fact]
	public void AListingGivesEachNameInOrderWithoutDotsDeletedNamesOrTheLabel()
	{
		Fat32 fs = Mount(ReadVolume());
		DirectoryEntry[] entries = new DirectoryEntry[16];
		int count = fs.List("/", 0, entries);
		Assert.Equal(8, count);
		Assert.True(Listed(entries, count, 0, "README.TXT", 100, false));
		Assert.True(Listed(entries, count, 1, "Alloy-WMSvr.exe", 3000, false));
		Assert.True(Listed(entries, count, 2, "EFI", 0, true));
		Assert.True(Listed(entries, count, 3, "BADSUM.TXT", 10, false));
		Assert.True(Listed(entries, count, 4, "SHORT.BIN", 2048, false));
		Assert.True(Listed(entries, count, 5, "MANY", 0, true));
		Assert.True(Listed(entries, count, 6, "cased.txt", 5, false));
		Assert.True(Listed(entries, count, 7, "mixed.TXT", 6, false));

		count = fs.List("\\efi/boot", 0, entries);
		Assert.Equal(2, count);
		Assert.True(Listed(entries, count, 0, "term.exe", 5000, false));
		Assert.True(Listed(entries, count, 1, "EMPTY.TXT", 0, false));
	}

	[Fact]
	public void AListingFillsWhatItIsGivenFromAnIndexAndOnlyADirectoryLists()
	{
		Fat32 fs = Mount(ReadVolume());
		DirectoryEntry[] entries = new DirectoryEntry[3];
		int count = fs.List("/many", 0, entries);
		Assert.Equal(3, count);
		Assert.True(Listed(entries, count, 2, "File number 02.dat", 1, false));
		count = fs.List("/many", 38, entries);
		Assert.Equal(2, count);
		Assert.True(Listed(entries, count, 1, "File number 39.dat", 1, false));
		Assert.Equal(-1, fs.List("/readme.txt", 0, entries));
		Assert.Equal(-1, fs.List("/missing", 0, entries));
	}

	[Fact]
	public void AVolumeWithoutFsInfoHasNoFreeCount()
	{
		Fat32 fs = Mount(ReadVolume());
		Assert.Equal(Fat32.Unknown, fs.FreeClusters);
	}

	[Fact]
	public void WritesFilesAndDirectories()
	{
		// FSInfo's hint points at a cluster in use, to be passed over
		FatTestVolume volume = new();
		volume.AddFile(FatTestVolume.Root, "OLD     TXT", null, Data.AsSpan(0, 1000));
		volume.AddEntry(FatTestVolume.Root, "LOCKED  TXT", null, 0x21, 0, 0);
		volume.WriteFsInfo(FatTestVolume.Clusters - 3, 3);

		Fat32 fs = Mount(volume);
		Assert.Equal(FatTestVolume.Clusters - 3, fs.FreeClusters);
		FileState file = default;

		// Only a write open makes a file, and only with a disposition that allows it
		Assert.False(Open(fs, ref file, "/new.bin", FileAccess.ReadWrite, FileCreate.OpenExisting));
		Assert.False(Open(fs, ref file, "/new.bin", FileAccess.Read, FileCreate.CreateAlways));
		Assert.False(Open(fs, ref file, "/new.bin", FileAccess.Read, FileCreate.OpenAlways));
		Assert.True(Open(fs, ref file, "/new.bin", FileAccess.ReadWrite, FileCreate.CreateAlways));
		Assert.Equal(0, file.Length);

		// A file written in pieces reads back the same, in one run of new clusters, and so does the volume mounted again
		Assert.Equal(5000, WriteAll(fs, ref file, Data.AsSpan(0, 5000), 700));
		Assert.Equal(5000, file.Length);
		Assert.Equal(10u, file.Run);
		Assert.Equal(FatTestVolume.Clusters - 13, fs.FreeClusters);
		Assert.True(Same(fs, "new.bin", Data.AsSpan(0, 5000)));

		Fat32 again = new();
		Assert.True(again.Mount(volume.Disk, new SectorCache()));
		Assert.Equal(FatTestVolume.Clusters - 13, again.FreeClusters);
		Assert.True(Same(again, "/NEW.BIN", Data.AsSpan(0, 5000)));

		// A read open doesn't write, and nor does a read-only file open for writing
		Assert.True(Open(fs, ref file, "/new.bin", FileAccess.Read, FileCreate.OpenExisting));
		Assert.Equal(0, fs.Write(ref file, Data.AsSpan(0, 10)));
		Assert.True(Open(fs, ref file, "/locked.txt", FileAccess.Read, FileCreate.OpenExisting));
		Assert.False(Open(fs, ref file, "/locked.txt", FileAccess.Write, FileCreate.OpenExisting));
		Assert.False(Open(fs, ref file, "/locked.txt", FileAccess.Write, FileCreate.CreateAlways));
		Assert.True(Open(fs, ref file, "/locked.txt", FileAccess.Read, FileCreate.OpenExisting));
		Assert.False(fs.Delete("/locked.txt"));
		Close(fs, ref file);
		Assert.False(fs.Delete("/locked.txt"));

		// Writing over the middle changes only those bytes; past the end the file grows, the gap reading as zeros
		Assert.True(Open(fs, ref file, "/new.bin", FileAccess.ReadWrite, FileCreate.OpenAlways));
		Assert.Equal(5000, file.Length);
		file.Position = 1000;
		Assert.Equal(1500, WriteAll(fs, ref file, Data.AsSpan(100, 1500), 1500));
		file.Position = 7000;
		Assert.Equal(10, WriteAll(fs, ref file, Data.AsSpan(0, 10), 10));
		Assert.Equal(7010, file.Length);

		byte[] expect = new byte[8192];
		Data.AsSpan(0, 5000).CopyTo(expect);
		Data.AsSpan(100, 1500).CopyTo(expect.AsSpan(1000));
		expect.AsSpan(5000, 2000).Clear();
		Data.AsSpan(0, 10).CopyTo(expect.AsSpan(7000));
		Assert.True(Same(fs, "/new.bin", expect.AsSpan(0, 7010)));

		// Opening to create over a file empties it, its clusters freed
		uint free = fs.FreeClusters;
		Assert.True(Open(fs, ref file, "/new.bin", FileAccess.Write, FileCreate.CreateAlways));
		Assert.Equal(0, file.Length);
		Assert.Equal(free + 14, fs.FreeClusters);
		Assert.True(Same(fs, "/new.bin", ReadOnlySpan<byte>.Empty));

		// Any number read a file but one writes, and a file anyone has open isn't emptied, deleted or renamed until the last closes it
		FileState reader = default;
		FileState other = default;
		FileState writer = default;
		Assert.True(Open(fs, ref reader, "/old.txt", FileAccess.Read, FileCreate.OpenExisting));
		Assert.True(Open(fs, ref other, "/OLD.TXT", FileAccess.Read, FileCreate.OpenExisting));
		Assert.True(Open(fs, ref writer, "/old.txt", FileAccess.ReadWrite, FileCreate.OpenAlways));
		Assert.False(Open(fs, ref file, "/old.txt", FileAccess.Write, FileCreate.OpenExisting));
		Assert.False(Open(fs, ref file, "/old.txt", FileAccess.Write, FileCreate.CreateAlways));
		Assert.False(fs.Delete("/old.txt"));
		Assert.False(fs.Rename("/old.txt", "/older.txt"));

		Close(fs, ref writer);
		Assert.True(Open(fs, ref writer, "/old.txt", FileAccess.Write, FileCreate.OpenExisting));
		Assert.False(Open(fs, ref file, "/old.txt", FileAccess.Write, FileCreate.CreateAlways));
		Close(fs, ref writer);
		Close(fs, ref reader);
		Assert.False(fs.Delete("/old.txt"));
		Close(fs, ref other);
		Assert.True(fs.Rename("/old.txt", "/older.txt"));
		Assert.True(fs.Rename("/older.txt", "/old.txt"));
		Assert.True(Same(fs, "/old.txt", Data.AsSpan(0, 1000)));

		// Only so many files are open at once, however many opens each has; closing one makes room
		FileState[] held = new FileState[Fat32.MaxOpen];
		for (int i = 0; i < Fat32.MaxOpen; i++)
			Assert.True(Open(fs, ref held[i], $"/held{i:D2}.txt", FileAccess.Write, FileCreate.CreateAlways));
		Assert.False(Open(fs, ref file, "/old.txt", FileAccess.Read, FileCreate.OpenExisting));
		Assert.False(Open(fs, ref file, "/held64.txt", FileAccess.Write, FileCreate.CreateAlways));
		Assert.True(Open(fs, ref file, "/held00.txt", FileAccess.Read, FileCreate.OpenExisting));
		Close(fs, ref file);
		Close(fs, ref held[0]);
		Assert.True(Open(fs, ref file, "/old.txt", FileAccess.Read, FileCreate.OpenExisting));
		Close(fs, ref file);
		for (int i = 0; i < Fat32.MaxOpen; i++)
		{
			Close(fs, ref held[i]);
			Assert.True(fs.Delete($"/held{i:D2}.txt"));
		}
		Assert.False(Open(fs, ref file, "/held64.txt", FileAccess.Read, FileCreate.OpenExisting));

		// A long name gets an alias with a numeric tail, the next along when that's taken; a name that is its 8.3 alias exactly needs no long name
		Assert.True(Open(fs, ref file, "/Long File Name.txt", FileAccess.Write, FileCreate.CreateAlways));
		Assert.True(Open(fs, ref file, "/Long File Name 2.txt", FileAccess.Write, FileCreate.CreateAlways));
		Assert.True(Open(fs, ref file, "/LONGFI~1.TXT", FileAccess.Read, FileCreate.OpenExisting));
		Assert.True(Open(fs, ref file, "/longfi~2.txt", FileAccess.Read, FileCreate.OpenExisting));
		Assert.True(Open(fs, ref file, "/LONG FILE NAME 2.TXT", FileAccess.Read, FileCreate.OpenExisting));

		Assert.True(Open(fs, ref file, "/UPPER.TXT", FileAccess.Write, FileCreate.CreateAlways));
		Assert.False(volume.FindEntry(FatTestVolume.Root, "UPPER   TXT", out bool longName).IsEmpty);
		Assert.False(longName);
		Assert.True(Open(fs, ref file, "/lower.txt", FileAccess.Write, FileCreate.CreateAlways));
		Assert.False(volume.FindEntry(FatTestVolume.Root, "LOWER   TXT", out longName).IsEmpty);
		Assert.True(longName);
		Assert.True(Open(fs, ref file, "/.hidden", FileAccess.Write, FileCreate.CreateAlways));
		Assert.False(volume.FindEntry(FatTestVolume.Root, "HIDDEN~1   ").IsEmpty);
		Assert.True(Open(fs, ref file, "/a+b.tar.gz", FileAccess.Write, FileCreate.CreateAlways));
		Assert.False(volume.FindEntry(FatTestVolume.Root, "A_BTAR~1GZ ").IsEmpty);

		// A name of 255 characters fits, one longer doesn't, and nor does one with a reserved character, a trailing dot or a missing directory
		string name = "/" + new string('x', 251) + ".txt";
		Assert.True(Open(fs, ref file, name, FileAccess.Write, FileCreate.CreateAlways));
		Assert.Equal(600, WriteAll(fs, ref file, Data.AsSpan(0, 600), 600));
		Assert.True(Same(fs, name, Data.AsSpan(0, 600)));
		Assert.True(Same(fs, "/XXXXXX~1.TXT", Data.AsSpan(0, 600)));

		Assert.False(Open(fs, ref file, "/" + new string('x', 252) + ".txt", FileAccess.Write, FileCreate.CreateAlways));
		Assert.False(Open(fs, ref file, "/bad?.txt", FileAccess.Write, FileCreate.CreateAlways));
		Assert.False(Open(fs, ref file, "/trailing.", FileAccess.Write, FileCreate.CreateAlways));
		Assert.False(Open(fs, ref file, "/missing/x.txt", FileAccess.Write, FileCreate.CreateAlways));
		Assert.False(Open(fs, ref file, "/old.txt/x.txt", FileAccess.Write, FileCreate.CreateAlways));
		Assert.False(Open(fs, ref file, "/", FileAccess.Write, FileCreate.OpenAlways));
		Assert.False(fs.CreateDirectory("/"));
		Assert.False(fs.CreateDirectory("/old.txt"));

		// A directory made holds files, and goes only once empty; the root, the default directory and ".." never go
		Assert.True(fs.CreateDirectory("/Projects"));
		Assert.False(fs.CreateDirectory("/projects"));
		Assert.False(Open(fs, ref file, "/projects", FileAccess.Write, FileCreate.OpenAlways));
		Assert.True(fs.CreateDirectory("/projects/Sub"));
		Assert.True(Open(fs, ref file, "/projects/sub/notes.txt", FileAccess.Write, FileCreate.CreateAlways));
		Assert.Equal(100, WriteAll(fs, ref file, Data.AsSpan(0, 100), 100));

		Assert.True(fs.SetDirectory("/projects/sub"));
		Assert.True(Same(fs, "notes.txt", Data.AsSpan(0, 100)));
		Assert.True(Same(fs, "../sub/notes.txt", Data.AsSpan(0, 100)));
		Assert.True(Same(fs, "../../old.txt", Data.AsSpan(0, 1000)));
		DirectoryEntry[] entries = new DirectoryEntry[16];
		int count = fs.List("..", 0, entries);
		Assert.Equal(1, count);
		Assert.True(Listed(entries, count, 0, "Sub", 0, true));
		Assert.False(fs.Delete("/projects"));
		Assert.False(fs.Delete("/projects/sub"));
		Assert.False(fs.Delete(".."));
		Assert.False(fs.Delete("/"));
		Assert.True(fs.SetDirectory("/"));

		Span<byte> projects = volume.FindEntry(FatTestVolume.Root, "PROJECTS   ");
		Assert.False(projects.IsEmpty);
		uint cluster = FatTestVolume.EntryCluster(projects);
		Assert.False(fs.Delete("/projects/sub/notes.txt"));
		Close(fs, ref file);
		Assert.True(fs.Delete("/projects/sub/notes.txt"));
		Assert.False(Open(fs, ref file, "/projects/sub/notes.txt", FileAccess.Read, FileCreate.OpenExisting));
		Assert.True(fs.Delete("/projects/sub"));
		Assert.True(fs.Delete("/projects"));
		Assert.Equal(0xE5, projects[0]);
		Assert.Equal(0u, volume.GetFat(cluster));
		Assert.False(fs.Delete("/projects"));

		// A rename within a directory or to another keeps the data, a directory moved gets its new parent in "..", and nothing moves onto a name taken or into itself
		Assert.True(fs.CreateDirectory("/a"));
		Assert.True(fs.CreateDirectory("/a/b"));
		Assert.True(Open(fs, ref file, "/a/data.bin", FileAccess.Write, FileCreate.CreateAlways));
		Assert.Equal(3000, WriteAll(fs, ref file, Data.AsSpan(0, 3000), 3000));
		Assert.False(fs.Rename("/a/data.bin", "/a/Renamed Data.bin"));
		Close(fs, ref file);

		Assert.True(fs.Rename("/a/data.bin", "/a/Renamed Data.bin"));
		Assert.False(Open(fs, ref file, "/a/data.bin", FileAccess.Read, FileCreate.OpenExisting));
		Assert.True(Same(fs, "/a/renamed data.bin", Data.AsSpan(0, 3000)));
		Assert.True(fs.Rename("/a/renamed data.bin", "/moved.bin"));
		Assert.True(Same(fs, "/moved.bin", Data.AsSpan(0, 3000)));

		Assert.False(fs.Rename("/moved.bin", "/a/b/../../old.txt"));
		Assert.False(fs.Rename("/moved.bin", "/bad|name"));
		Assert.False(fs.Rename("/missing", "/x"));
		Assert.False(fs.Rename("/", "/x"));
		Assert.False(fs.Rename("/a", "/a/a"));
		Assert.False(fs.Rename("/a", "/a/b/a"));

		Assert.True(fs.Rename("/a/b", "/b"));
		Assert.True(Same(fs, "/b/../moved.bin", Data.AsSpan(0, 3000)));
		Assert.True(fs.Rename("/b", "/a/c"));
		Assert.True(fs.SetDirectory("/a/c"));
		Assert.True(Same(fs, "../../moved.bin", Data.AsSpan(0, 3000)));
		Assert.True(fs.SetDirectory("/"));

		// A directory grows a cluster at a time as it fills, and a deleted name's entries are used again
		Assert.True(fs.CreateDirectory("/many"));
		for (int i = 0; i < 40; i++)
		{
			Assert.True(Open(fs, ref file, $"/many/File number {i:D2}.dat", FileAccess.Write, FileCreate.CreateAlways));
			Assert.Equal(1, WriteAll(fs, ref file, Data.AsSpan(i, 1), 1));
		}
		Assert.True(Same(fs, "/many/file number 39.dat", Data.AsSpan(39, 1)));
		Assert.True(Same(fs, "/many/file number 00.dat", Data.AsSpan(0, 1)));

		free = fs.FreeClusters;
		Assert.True(fs.Delete("/many/File number 07.dat"));
		Assert.True(Open(fs, ref file, "/many/File number 77.dat", FileAccess.Write, FileCreate.CreateAlways));
		Assert.Equal(free + 1, fs.FreeClusters);
		Assert.True(Same(fs, "/many/file number 08.dat", Data.AsSpan(8, 1)));
		Close(fs, ref file);

		// Every change reached both FATs, and FSInfo's free count is the FAT's
		Assert.True(fs.Flush());
		for (uint s = 0; s < FatTestVolume.FatSectors; s++)
			Assert.True(volume.Disk.Sector(FatTestVolume.Reserved + s).SequenceEqual(volume.Disk.Sector(FatTestVolume.Reserved + FatTestVolume.FatSectors + s)));
		Assert.Equal(fs.FreeClusters, volume.FsInfoFree());
		Assert.Equal(fs.FreeClusters, volume.CountFree());
	}
}
