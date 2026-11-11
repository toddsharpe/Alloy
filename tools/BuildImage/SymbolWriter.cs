using System.Globalization;
using System.IO;
using System.Runtime.InteropServices;
using System.Text;
using System.Text.RegularExpressions;
using Alloy.Abi;

namespace Alloy.Tools;

// Turns a link.exe map into the kernel.sym format read by Alloy.Abi.SymbolTable
public static partial class SymbolWriter
{
	[GeneratedRegex(@"^\s*[0-9a-fA-F]{4}:[0-9a-fA-F]{8}\s+(\S+)\s+([0-9a-fA-F]{16})\s")]
	private static partial Regex SymbolLine();

	[GeneratedRegex(@"Preferred load address is ([0-9a-fA-F]+)")]
	private static partial Regex LoadAddress();

	public static byte[] FromLinkMap(IEnumerable<string> lines, string stripPrefix)
	{
		ulong imageBase = 0;
		SortedDictionary<uint, string> symbols = [];
		foreach (string line in lines)
		{
			Match load = LoadAddress().Match(line);
			if (load.Success)
			{
				imageBase = ulong.Parse(load.Groups[1].Value, NumberStyles.HexNumber);
				continue;
			}

			Match symbol = SymbolLine().Match(line);
			if (!symbol.Success || imageBase == 0)
				continue;
			ulong address = ulong.Parse(symbol.Groups[2].Value, NumberStyles.HexNumber);
			if (address < imageBase || address - imageBase > uint.MaxValue)
				continue;

			string name = symbol.Groups[1].Value;
			if (name.StartsWith(stripPrefix, StringComparison.Ordinal))
				name = name[stripPrefix.Length..];
			symbols.TryAdd((uint)(address - imageBase), name);
		}
		return Build(symbols);
	}

	public static byte[] Build(SortedDictionary<uint, string> symbols)
	{
		MemoryStream names = new();
		List<(uint Rva, uint Name)> entries = [];
		foreach ((uint rva, string name) in symbols)
		{
			entries.Add((rva, (uint)names.Length));
			names.Write(Encoding.ASCII.GetBytes(name));
			names.WriteByte(0);
		}

		byte[] output = new byte[SymbolTable.HeaderSize + entries.Count * SymbolTable.EntrySize + names.Length];
		MemoryMarshal.Write(output, SymbolTable.ExpectedMagic);
		MemoryMarshal.Write(output.AsSpan(8), entries.Count);
		for (int i = 0; i < entries.Count; i++)
		{
			MemoryMarshal.Write(output.AsSpan(SymbolTable.HeaderSize + i * SymbolTable.EntrySize), entries[i].Rva);
			MemoryMarshal.Write(output.AsSpan(SymbolTable.HeaderSize + i * SymbolTable.EntrySize + 4), entries[i].Name);
		}
		names.ToArray().CopyTo(output, SymbolTable.HeaderSize + entries.Count * SymbolTable.EntrySize);
		return output;
	}
}
