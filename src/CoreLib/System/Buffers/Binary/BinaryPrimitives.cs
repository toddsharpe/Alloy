using System.Runtime.InteropServices;

namespace System.Buffers.Binary
{
	// The little-endian readers and writers of the BCL's BinaryPrimitives; x64 is little-endian, so each is an unaligned load or store behind a length check
	public static class BinaryPrimitives
	{
		public static ushort ReadUInt16LittleEndian(ReadOnlySpan<byte> source) => MemoryMarshal.Read<ushort>(source);

		public static short ReadInt16LittleEndian(ReadOnlySpan<byte> source) => MemoryMarshal.Read<short>(source);

		public static uint ReadUInt32LittleEndian(ReadOnlySpan<byte> source) => MemoryMarshal.Read<uint>(source);

		public static int ReadInt32LittleEndian(ReadOnlySpan<byte> source) => MemoryMarshal.Read<int>(source);

		public static ulong ReadUInt64LittleEndian(ReadOnlySpan<byte> source) => MemoryMarshal.Read<ulong>(source);

		public static long ReadInt64LittleEndian(ReadOnlySpan<byte> source) => MemoryMarshal.Read<long>(source);

		public static void WriteUInt16LittleEndian(Span<byte> destination, ushort value) => MemoryMarshal.Write(destination, in value);

		public static void WriteInt16LittleEndian(Span<byte> destination, short value) => MemoryMarshal.Write(destination, in value);

		public static void WriteUInt32LittleEndian(Span<byte> destination, uint value) => MemoryMarshal.Write(destination, in value);

		public static void WriteInt32LittleEndian(Span<byte> destination, int value) => MemoryMarshal.Write(destination, in value);

		public static void WriteUInt64LittleEndian(Span<byte> destination, ulong value) => MemoryMarshal.Write(destination, in value);

		public static void WriteInt64LittleEndian(Span<byte> destination, long value) => MemoryMarshal.Write(destination, in value);
	}
}
