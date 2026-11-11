using DiscUtils.Fat;
using DiscUtils.Partitions;
using DiscUtils.Streams;
using Alloy.Tools;

// Writes a symbol table from a link map, or builds a GPT VHDX: a FAT32 EFI system partition from an ESP directory and a FAT32 Alloy partition from an OS directory
if (args.Length == 4 && args[0] == "sym")
	return BuildSymbols(args[1], args[2], args[3]);
if (args.Length == 4 && args[0] == "vhdx")
	return BuildVhdx(args[1], args[2], args[3]);

Console.Error.WriteLine("usage: BuildImage sym <out.sym> <link.map> <strip-prefix>");
Console.Error.WriteLine("       BuildImage vhdx <out.vhdx> <esp-dir> <os-dir>");
return 2;

static int BuildSymbols(string output, string map, string stripPrefix)
{
	byte[] table = SymbolWriter.FromLinkMap(File.ReadLines(map), stripPrefix);
	File.WriteAllBytes(output, table);
	Console.WriteLine($"BuildImage: {table.Length} byte symbol table from {Path.GetFileName(map)} -> {output}");
	return 0;
}

static int BuildVhdx(string output, string espDirectory, string osDirectory)
{
	// DiscUtils formats FAT32 only from 512 MB; a dynamic VHDX only stores what is written
	const long EspSize = 512L * 1024 * 1024;
	const long OsSize = 1024L * 1024 * 1024;
	const long SectorSize = 512;
	const long FirstSector = 1024 * 1024 / SectorSize;
	const long OsFirstSector = FirstSector + EspSize / SectorSize;
	const long DiskSize = EspSize + OsSize + 2 * 1024 * 1024;

	foreach (string directory in new[] { espDirectory, osDirectory })
	{
		if (!Directory.Exists(directory))
		{
			Console.Error.WriteLine($"BuildImage: missing directory {directory}");
			return 1;
		}
	}

	output = Path.GetFullPath(output);

	// Built next to the output and swapped in at the end, so a failed run never leaves a half-written disk
	string temp = output + ".tmp";
	using (FileStream stream = new(temp, FileMode.Create, FileAccess.ReadWrite))
	using (DiscUtils.Vhdx.Disk disk = DiscUtils.Vhdx.Disk.InitializeDynamic(stream, Ownership.None, DiskSize))
	{
		GuidPartitionTable gpt = GuidPartitionTable.Initialize(disk);
		int esp = gpt.Create(FirstSector, OsFirstSector - 1, GuidPartitionTypes.EfiSystem, 0, "EFI system partition");
		int os = gpt.Create(OsFirstSector, OsFirstSector + OsSize / SectorSize - 1, GuidPartitionTypes.WindowsBasicData, 0, "Alloy");
		if (!Populate(disk, esp, "SYSTEM", espDirectory) || !Populate(disk, os, "ALLOY", osDirectory))
			return 1;
	}

	File.Move(temp, output, true);
	Console.WriteLine($"BuildImage: {output}");
	return 0;
}

// Formats the partition FAT32 and copies the directory's tree onto it
static bool Populate(DiscUtils.Vhdx.Disk disk, int partition, string label, string directory)
{
	using FatFileSystem fat = FatFileSystem.FormatPartition(disk, partition, label);
	if (fat.FatVariant != FatType.Fat32)
	{
		Console.Error.WriteLine($"BuildImage: {label} formatted {fat.FatVariant}, expected Fat32");
		return false;
	}

	string[] files = Directory.GetFiles(directory, "*", SearchOption.AllDirectories);
	foreach (string file in files)
	{
		string relative = Path.GetRelativePath(directory, file);
		string parent = Path.GetDirectoryName(relative) ?? "";
		if (parent.Length > 0)
			fat.CreateDirectory(parent);

		using FileStream source = File.OpenRead(file);
		using SparseStream destination = fat.OpenFile(relative, FileMode.Create, FileAccess.Write);
		source.CopyTo(destination);
	}

	Console.WriteLine($"BuildImage: {label}: {files.Length} files on FAT32 ({fat.ClusterSize}-byte clusters)");
	return true;
}
