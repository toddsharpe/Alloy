using Alloy.Text;

namespace System
{
	// Each primitive has a single field of its own type; the compiler and runtime depend on that layout

	public abstract class ValueType
	{
	}

	public abstract class Enum : ValueType
	{
	}

	public struct Void
	{
	}

	public struct Boolean
	{
		private bool m_value;

		public override string ToString() => m_value ? "True" : "False";
		public override int GetHashCode() => m_value ? 1 : 0;
	}

	public struct Char
	{
		private char m_value;

		public const char MaxValue = (char)0xFFFF;
		public const char MinValue = (char)0;

		public override string ToString() => new string(m_value, 1);
		public override int GetHashCode() => m_value;
	}

	public struct SByte
	{
		private sbyte m_value;

		public const sbyte MaxValue = 0x7F;
		public const sbyte MinValue = -0x80;

		public override string ToString() => Number.ToString(m_value);
		public override int GetHashCode() => m_value;
	}

	public struct Byte
	{
		private byte m_value;

		public const byte MaxValue = 0xFF;
		public const byte MinValue = 0;

		public override string ToString() => Number.ToString((ulong)m_value);
		public bool TryFormat(Span<char> destination, out int charsWritten) => Number.TryFormatUInt64(m_value, destination, out charsWritten);
		public override int GetHashCode() => m_value;
	}

	public struct Int16
	{
		private short m_value;

		public const short MaxValue = 0x7FFF;
		public const short MinValue = -0x8000;

		public override string ToString() => Number.ToString(m_value);
		public override int GetHashCode() => m_value;
	}

	public struct UInt16
	{
		private ushort m_value;

		public const ushort MaxValue = 0xFFFF;
		public const ushort MinValue = 0;

		public override string ToString() => Number.ToString((ulong)m_value);
		public override int GetHashCode() => m_value;
	}

	public struct Int32
	{
		private int m_value;

		public const int MaxValue = 0x7FFFFFFF;
		public const int MinValue = unchecked((int)0x80000000);

		public override string ToString() => Number.ToString(m_value);
		public bool TryFormat(Span<char> destination, out int charsWritten) => Number.TryFormatInt64(m_value, destination, out charsWritten);
		public override bool Equals(object obj) => obj is int other && other == m_value;
		public override int GetHashCode() => m_value;
	}

	public struct UInt32
	{
		private uint m_value;

		public const uint MaxValue = 0xFFFFFFFF;
		public const uint MinValue = 0;

		public override string ToString() => Number.ToString((ulong)m_value);
		public bool TryFormat(Span<char> destination, out int charsWritten) => Number.TryFormatUInt64(m_value, destination, out charsWritten);
		public override int GetHashCode() => (int)m_value;
	}

	public struct Int64
	{
		private long m_value;

		public const long MaxValue = 0x7FFFFFFFFFFFFFFF;
		public const long MinValue = unchecked((long)0x8000000000000000);

		public override string ToString() => Number.ToString(m_value);
		public bool TryFormat(Span<char> destination, out int charsWritten) => Number.TryFormatInt64(m_value, destination, out charsWritten);
		public override int GetHashCode() => (int)m_value ^ (int)(m_value >> 32);
	}

	public struct UInt64
	{
		private ulong m_value;

		public const ulong MaxValue = 0xFFFFFFFFFFFFFFFF;
		public const ulong MinValue = 0;

		public override string ToString() => Number.ToString(m_value);
		public bool TryFormat(Span<char> destination, out int charsWritten) => Number.TryFormatUInt64(m_value, destination, out charsWritten);
		public override int GetHashCode() => (int)m_value ^ (int)(m_value >> 32);
	}

	public struct Single
	{
		private float m_value;
	}

	public struct Double
	{
		private double m_value;
	}

	// RuntimeFeature.NumericIntPtr makes nint/nuint and IntPtr/UIntPtr the same type, so arithmetic is built in
	public unsafe struct IntPtr
	{
		private void* _value;

		public static readonly nint Zero;
		public static int Size => sizeof(nint);
		public static nint MaxValue => unchecked((nint)0x7FFFFFFFFFFFFFFF);
		public static nint MinValue => unchecked((nint)0x8000000000000000);

		public override string ToString() => Number.ToString((long)(nint)_value);
		public override int GetHashCode() => (int)(nint)_value ^ (int)((nint)_value >> 32);
	}

	public unsafe struct UIntPtr
	{
		private void* _value;

		public static readonly nuint Zero;
		public static int Size => sizeof(nuint);
		public static nuint MaxValue => unchecked((nuint)0xFFFFFFFFFFFFFFFF);
		public static nuint MinValue => 0;

		public override string ToString() => Number.ToString((ulong)(nuint)_value);
		public override int GetHashCode() => (int)(nuint)_value ^ (int)((nuint)_value >> 32);
	}
}
