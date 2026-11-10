using System;
using System.Runtime.InteropServices;

namespace Alloy.Abi
{
	// PE32+ images from link.exe; every read is bounds checked since user images will come from the initrd
	public readonly ref struct PeImage
	{
		public const ushort MachineAmd64 = 0x8664;
		public const ushort SubsystemNative = 1;
		public const ushort SubsystemEfiApplication = 10;

		public const int DirectoryImport = 1;
		public const int DirectoryException = 3;
		public const int DirectoryBaseRelocation = 5;

		private const ushort DosMagic = 0x5A4D;
		private const uint NtSignature = 0x00004550;
		private const ushort OptionalMagic64 = 0x20B;
		private const int FileHeaderSize = 20;
		private const int SectionHeaderSize = 40;
		private const int DataDirectoryOffset = 112;

		private readonly ReadOnlySpan<byte> _file;
		private readonly int _optionalHeader;
		private readonly int _sectionTable;

		private PeImage(ReadOnlySpan<byte> file, int optionalHeader, int sectionTable, int sectionCount, uint directoryCount)
		{
			_file = file;
			_optionalHeader = optionalHeader;
			_sectionTable = sectionTable;
			SectionCount = sectionCount;
			DirectoryCount = directoryCount;
		}

		public int SectionCount { get; }

		public uint DirectoryCount { get; }

		public ushort Machine => Read<ushort>(_file, _optionalHeader - FileHeaderSize);

		public uint EntryPoint => Read<uint>(_file, _optionalHeader + 16);

		public ulong ImageBase => Read<ulong>(_file, _optionalHeader + 24);

		public uint SectionAlignment => Read<uint>(_file, _optionalHeader + 32);

		public uint SizeOfImage => Read<uint>(_file, _optionalHeader + 56);

		public uint SizeOfHeaders => Read<uint>(_file, _optionalHeader + 60);

		public ulong SizeOfStackReserve => Read<ulong>(_file, _optionalHeader + 72);

		public ushort Subsystem => Read<ushort>(_file, _optionalHeader + 68);

		public static bool TryParse(ReadOnlySpan<byte> file, out PeImage image) => TryParse(file, false, out image);

		// Parses an image already laid out at its RVAs (SizeOfImage bytes), so raw file offsets aren't checked
		public static bool TryParseLoaded(ReadOnlySpan<byte> image, out PeImage pe) => TryParse(image, true, out pe);

		private static bool TryParse(ReadOnlySpan<byte> file, bool loaded, out PeImage image)
		{
			image = default;
			if (file.Length < 0x40 || Read<ushort>(file, 0) != DosMagic)
				return false;

			int nt = Read<int>(file, 0x3C);
			if (nt < 0 || nt > file.Length - 4 - FileHeaderSize || Read<uint>(file, nt) != NtSignature)
				return false;

			int fileHeader = nt + 4;
			int sectionCount = Read<ushort>(file, fileHeader + 2);
			int optionalSize = Read<ushort>(file, fileHeader + 16);
			int optionalHeader = fileHeader + FileHeaderSize;
			if (optionalSize < DataDirectoryOffset || optionalHeader > file.Length - optionalSize || Read<ushort>(file, optionalHeader) != OptionalMagic64)
				return false;

			uint directoryCount = Read<uint>(file, optionalHeader + 108);
			if (directoryCount > 16 || DataDirectoryOffset + directoryCount * 8 > (uint)optionalSize)
				return false;

			int sectionTable = optionalHeader + optionalSize;
			if (sectionTable > file.Length - sectionCount * SectionHeaderSize)
				return false;

			PeImage candidate = new PeImage(file, optionalHeader, sectionTable, sectionCount, directoryCount);
			if (candidate.SizeOfHeaders > candidate.SizeOfImage || candidate.SizeOfHeaders > (uint)file.Length || candidate.EntryPoint >= candidate.SizeOfImage)
				return false;

			for (int i = 0; i < sectionCount; i++)
			{
				PeSection section = candidate.GetSection(i);
				if ((ulong)section.VirtualAddress + section.VirtualSize > candidate.SizeOfImage)
					return false;
				if (!loaded && (ulong)section.PointerToRawData + section.SizeOfRawData > (ulong)file.Length)
					return false;
			}

			image = candidate;
			return true;
		}

		public PeSection GetSection(int index)
		{
			if ((uint)index >= (uint)SectionCount)
				return default;
			return MemoryMarshal.Read<PeSection>(_file.Slice(_sectionTable + index * SectionHeaderSize, SectionHeaderSize));
		}

		public PeDataDirectory GetDirectory(int index)
		{
			if ((uint)index >= DirectoryCount)
				return default;
			return MemoryMarshal.Read<PeDataDirectory>(_file.Slice(_optionalHeader + DataDirectoryOffset + index * 8, 8));
		}

		// Lays the image out at its RVAs in memory (SizeOfImage bytes); everything not backed by the file is zeroed
		public bool TryLoad(Span<byte> memory)
		{
			if ((uint)memory.Length < SizeOfImage)
				return false;

			memory.Slice(0, (int)SizeOfImage).Clear();
			_file.Slice(0, (int)SizeOfHeaders).CopyTo(memory);
			for (int i = 0; i < SectionCount; i++)
			{
				PeSection section = GetSection(i);
				uint length = Math.Min(section.SizeOfRawData, section.VirtualSize);
				_file.Slice((int)section.PointerToRawData, (int)length).CopyTo(memory.Slice((int)section.VirtualAddress));
			}
			return true;
		}

		// Applies base relocations to a loaded image moving from fromBase to toBase; returns the count, or -1 if malformed
		public static int Relocate(Span<byte> memory, PeDataDirectory relocations, ulong fromBase, ulong toBase)
		{
			const int TypeAbsolute = 0;
			const int TypeDir64 = 10;

			if ((ulong)relocations.VirtualAddress + relocations.Size > (ulong)memory.Length)
				return -1;

			ulong delta = toBase - fromBase;
			int applied = 0;
			int offset = (int)relocations.VirtualAddress;
			int end = offset + (int)relocations.Size;
			while (offset + 8 <= end)
			{
				uint pageRva = Read<uint>(memory, offset);
				uint blockSize = Read<uint>(memory, offset + 4);
				if (blockSize < 8 || blockSize > (uint)(end - offset))
					return -1;

				for (int entry = offset + 8; entry + 2 <= offset + (int)blockSize; entry += 2)
				{
					ushort value = Read<ushort>(memory, entry);
					int type = value >> 12;
					if (type == TypeAbsolute)
						continue;
					if (type != TypeDir64)
						return -1;

					ulong target = (ulong)pageRva + (uint)(value & 0xFFF);
					if (target + 8 > (ulong)memory.Length)
						return -1;
					Span<byte> slot = memory.Slice((int)target, 8);
					MemoryMarshal.Write(slot, MemoryMarshal.Read<ulong>(slot) + delta);
					applied++;
				}
				offset += (int)blockSize;
			}
			return applied;
		}

		private static T Read<T>(ReadOnlySpan<byte> data, int offset) where T : unmanaged
		{
			if (offset < 0 || offset > data.Length)
				return default;
			MemoryMarshal.TryRead(data.Slice(offset), out T value);
			return value;
		}
	}

	public struct PeSection
	{
		public const uint MemoryExecute = 0x20000000;
		public const uint MemoryRead = 0x40000000;
		public const uint MemoryWrite = 0x80000000;
		public const uint MemoryDiscardable = 0x02000000;

		public ulong Name;
		public uint VirtualSize;
		public uint VirtualAddress;
		public uint SizeOfRawData;
		public uint PointerToRawData;
		public uint PointerToRelocations;
		public uint PointerToLinenumbers;
		public ushort NumberOfRelocations;
		public ushort NumberOfLinenumbers;
		public uint Characteristics;

		public readonly bool IsExecutable => (Characteristics & MemoryExecute) != 0;

		public readonly bool IsWritable => (Characteristics & MemoryWrite) != 0;
	}

	public struct PeDataDirectory
	{
		public uint VirtualAddress;
		public uint Size;
	}
}
