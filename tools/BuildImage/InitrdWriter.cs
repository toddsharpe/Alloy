using System.Runtime.InteropServices;
using System.Text;
using Alloy.Abi;

namespace Alloy.Tools;

// Writes the initrd format read by Alloy.Abi.InitrdReader
public static class InitrdWriter
{
	public static unsafe byte[] Build(IReadOnlyList<(string Name, byte[] Data)> files)
	{
		HashSet<string> names = new(StringComparer.Ordinal);
		foreach ((string name, _) in files)
		{
			if (name.Length == 0 || name.Length > InitrdEntry.MaxNameLength || name.StartsWith('/') || name.Contains('\\') || !IsAscii(name))
				throw new ArgumentException($"initrd name '{name}' must be 1-{InitrdEntry.MaxNameLength} ASCII characters, '/' separated, no leading slash");
			if (!names.Add(name))
				throw new ArgumentException($"initrd name '{name}' appears twice");
		}

		long tableOffset = sizeof(InitrdHeader);
		long dataOffset = Align(tableOffset + (long)files.Count * sizeof(InitrdEntry));

		InitrdEntry[] entries = new InitrdEntry[files.Count];
		for (int i = 0; i < files.Count; i++)
		{
			entries[i].Offset = (ulong)dataOffset;
			entries[i].Size = (ulong)files[i].Data.Length;
			entries[i].NameLength = (ushort)files[i].Name.Length;
			fixed (byte* name = entries[i].Name)
				Encoding.ASCII.GetBytes(files[i].Name, new Span<byte>(name, InitrdEntry.MaxNameLength));
			dataOffset = Align(dataOffset + files[i].Data.Length);
		}

		byte[] image = new byte[dataOffset];
		InitrdHeader header = new()
		{
			Magic = InitrdHeader.ExpectedMagic,
			Version = InitrdHeader.CurrentVersion,
			EntryCount = (uint)files.Count,
			EntryTableOffset = (ulong)tableOffset,
			ImageSize = (ulong)image.Length,
		};
		MemoryMarshal.Write(image, in header);
		for (int i = 0; i < entries.Length; i++)
		{
			MemoryMarshal.Write(image.AsSpan((int)tableOffset + i * sizeof(InitrdEntry)), in entries[i]);
			files[i].Data.CopyTo(image.AsSpan((int)entries[i].Offset));
		}
		return image;
	}

	private static long Align(long value) => (value + InitrdEntry.DataAlignment - 1) & ~(long)(InitrdEntry.DataAlignment - 1);

	private static bool IsAscii(string s) => s.All(c => c is >= ' ' and < (char)0x7F);
}
