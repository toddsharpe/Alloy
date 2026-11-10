using DiscUtils.Fat;
using DiscUtils.Partitions;
using DiscUtils.Streams;
using Alloy.Tools;

// Packs the initrd, writes a symbol table from a link map, or builds a GPT VHDX whose FAT32 EFI partition holds an ESP directory
if (args.Length == 4 && args[0] == "sym")
	return BuildSymbols(args[1], args[2], args[3]);
if (args.Length >= 2 && args[0] == "initrd")
	return BuildInitrd(args[1], args[2..]);
if (args.Length == 3 && args[0] == "vhdx")
	return BuildVhdx(args[1], args[2]);

Console.Error.WriteLine("usage: BuildImage sym <out.sym> <link.map> <strip-prefix>");
Console.Error.WriteLine("       BuildImage initrd <out.img> [name=path]...");
Console.Error.WriteLine("       BuildImage vhdx <out.vhdx> <esp-dir>");
return 2;

static int BuildSymbols(string output, string map, string stripPrefix)
{
	byte[] table = SymbolWriter.FromLinkMap(File.ReadLines(map), stripPrefix);
	File.WriteAllBytes(output, table);
	Console.WriteLine($"BuildImage: {table.Length} byte symbol table from {Path.GetFileName(map)} -> {output}");
	return 0;
}

static int BuildInitrd(string output, string[] specs)
{
	List<(string, byte[])> files = [];
	foreach (string spec in specs)
	{
		int split = spec.IndexOf('=');
		if (split <= 0)
		{
			Console.Error.WriteLine($"BuildImage: '{spec}' is not name=path");
			return 1;
		}
		string path = spec[(split + 1)..];
		if (!File.Exists(path))
		{
			Console.Error.WriteLine($"BuildImage: missing {path}");
			return 1;
		}
		files.Add((spec[..split], File.ReadAllBytes(path)));
	}

	byte[] image = InitrdWriter.Build(files);
	Directory.CreateDirectory(Path.GetDirectoryName(Path.GetFullPath(output))!);
	File.WriteAllBytes(output, image);
	Console.WriteLine($"BuildImage: initrd with {files.Count} files, {image.Length} bytes -> {output}");
	return 0;
}

static int BuildVhdx(string output, string espDirectory)
{
	// DiscUtils formats FAT32 only from 512 MB; a dynamic VHDX only stores what is written
	const long PartitionSize = 512L * 1024 * 1024;
	const long SectorSize = 512;
	const long FirstSector = 1024 * 1024 / SectorSize;
	const long DiskSize = PartitionSize + 2 * 1024 * 1024;

	if (!Directory.Exists(espDirectory))
	{
		Console.Error.WriteLine($"BuildImage: missing ESP directory {espDirectory}");
		return 1;
	}

	output = Path.GetFullPath(output);
	string[] files = Directory.GetFiles(espDirectory, "*", SearchOption.AllDirectories);

	// Built next to the output and swapped in at the end, so a failed run never leaves a half-written disk
	string temp = output + ".tmp";
	using (FileStream stream = new(temp, FileMode.Create, FileAccess.ReadWrite))
	using (DiscUtils.Vhdx.Disk disk = DiscUtils.Vhdx.Disk.InitializeDynamic(stream, Ownership.None, DiskSize))
	{
		GuidPartitionTable gpt = GuidPartitionTable.Initialize(disk);
		int partition = gpt.Create(FirstSector, FirstSector + PartitionSize / SectorSize - 1, GuidPartitionTypes.EfiSystem, 0, "EFI system partition");

		using FatFileSystem fat = FatFileSystem.FormatPartition(disk, partition, "ALLOY");
		if (fat.FatVariant != FatType.Fat32)
		{
			Console.Error.WriteLine($"BuildImage: formatted {fat.FatVariant}, expected Fat32");
			return 1;
		}

		foreach (string file in files)
		{
			string relative = Path.GetRelativePath(espDirectory, file);
			string directory = Path.GetDirectoryName(relative) ?? "";
			if (directory.Length > 0)
				fat.CreateDirectory(directory);

			using FileStream source = File.OpenRead(file);
			using SparseStream destination = fat.OpenFile(relative, FileMode.Create, FileAccess.Write);
			source.CopyTo(destination);
		}
	}

	File.Move(temp, output, true);
	Console.WriteLine($"BuildImage: {files.Length} files on FAT32 -> {output}");
	return 0;
}
