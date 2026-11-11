using Alloy.Text;

namespace System
{
	// Each primitive has a single field of its own type; the compiler and runtime depend on that layout

	public struct Void
	{
	}

	public struct Boolean : IEquatable<bool>, IComparable<bool>, IComparable, ISpanFormattable
	{
		private bool m_value;

		public const string TrueString = "True";
		public const string FalseString = "False";

		public override string ToString() => m_value ? TrueString : FalseString;
		public string ToString(string format, IFormatProvider provider) => ToString();
		public bool TryFormat(Span<char> destination, out int charsWritten, ReadOnlySpan<char> format = default, IFormatProvider provider = null)
		{
			string text = ToString();
			charsWritten = 0;
			if (text.Length > destination.Length)
				return false;
			text.AsSpan().CopyTo(destination);
			charsWritten = text.Length;
			return true;
		}
		public override bool Equals(object obj) => obj is bool other && other == m_value;
		public bool Equals(bool obj) => m_value == obj;
		public override int GetHashCode() => m_value ? 1 : 0;
		public int CompareTo(bool value) => m_value == value ? 0 : (m_value ? 1 : -1);
		public int CompareTo(object obj)
		{
			if (obj == null)
				return 1;
			if (obj is bool other)
				return CompareTo(other);
			ThrowHelper.ThrowArgument("a value of another type");
			return 0;
		}
	}

	public struct Char : IEquatable<char>, IComparable<char>, IComparable, ISpanFormattable
	{
		private char m_value;

		public const char MaxValue = (char)0xFFFF;
		public const char MinValue = (char)0;

		public override string ToString() => new string(m_value, 1);
		public string ToString(string format, IFormatProvider provider) => ToString();
		public bool TryFormat(Span<char> destination, out int charsWritten, ReadOnlySpan<char> format = default, IFormatProvider provider = null)
		{
			charsWritten = 0;
			if (destination.Length == 0)
				return false;
			destination[0] = m_value;
			charsWritten = 1;
			return true;
		}
		public override bool Equals(object obj) => obj is char other && other == m_value;
		public bool Equals(char obj) => m_value == obj;
		public override int GetHashCode() => m_value;
		public int CompareTo(char value) => m_value - value;
		public int CompareTo(object obj)
		{
			if (obj == null)
				return 1;
			if (obj is char other)
				return CompareTo(other);
			ThrowHelper.ThrowArgument("a value of another type");
			return 0;
		}

		// ASCII classification; the images have no Unicode tables
		public static bool IsDigit(char c) => (uint)(c - '0') <= 9;
		public static bool IsLetter(char c) => (uint)((c | 0x20) - 'a') <= 25;
		public static bool IsLetterOrDigit(char c) => IsLetter(c) || IsDigit(c);
		public static bool IsUpper(char c) => (uint)(c - 'A') <= 25;
		public static bool IsLower(char c) => (uint)(c - 'a') <= 25;
		public static bool IsWhiteSpace(char c) => c == ' ' || (uint)(c - '\t') <= 4;
		public static bool IsAsciiHexDigit(char c) => IsDigit(c) || (uint)((c | 0x20) - 'a') <= 5;
		public static char ToUpper(char c) => IsLower(c) ? (char)(c - 32) : c;
		public static char ToLower(char c) => IsUpper(c) ? (char)(c + 32) : c;
		public static char ToUpperInvariant(char c) => ToUpper(c);
		public static char ToLowerInvariant(char c) => ToLower(c);
	}

	public struct SByte : IEquatable<sbyte>, IComparable<sbyte>, IComparable, ISpanFormattable
	{
		private sbyte m_value;

		public const sbyte MaxValue = 0x7F;
		public const sbyte MinValue = -0x80;

		public override string ToString() => Number.ToString(m_value);
		public string ToString(string format) => Number.ToString(m_value, 8, format.AsSpan());
		public string ToString(string format, IFormatProvider provider) => ToString(format);
		public bool TryFormat(Span<char> destination, out int charsWritten, ReadOnlySpan<char> format = default, IFormatProvider provider = null) =>
			Number.TryFormatSigned(m_value, 8, destination, format, out charsWritten);
		public override bool Equals(object obj) => obj is sbyte other && other == m_value;
		public bool Equals(sbyte obj) => m_value == obj;
		public override int GetHashCode() => m_value;
		public int CompareTo(sbyte value) => m_value - value;
		public int CompareTo(object obj)
		{
			if (obj == null)
				return 1;
			if (obj is sbyte other)
				return CompareTo(other);
			ThrowHelper.ThrowArgument("a value of another type");
			return 0;
		}
	}

	public struct Byte : IEquatable<byte>, IComparable<byte>, IComparable, ISpanFormattable
	{
		private byte m_value;

		public const byte MaxValue = 0xFF;
		public const byte MinValue = 0;

		public override string ToString() => Number.ToString((ulong)m_value);
		public string ToString(string format) => Number.ToString((ulong)m_value, format.AsSpan());
		public string ToString(string format, IFormatProvider provider) => ToString(format);
		public bool TryFormat(Span<char> destination, out int charsWritten, ReadOnlySpan<char> format = default, IFormatProvider provider = null) =>
			Number.TryFormatUnsigned(m_value, destination, format, out charsWritten);
		public override bool Equals(object obj) => obj is byte other && other == m_value;
		public bool Equals(byte obj) => m_value == obj;
		public override int GetHashCode() => m_value;
		public int CompareTo(byte value) => m_value - value;
		public int CompareTo(object obj)
		{
			if (obj == null)
				return 1;
			if (obj is byte other)
				return CompareTo(other);
			ThrowHelper.ThrowArgument("a value of another type");
			return 0;
		}
		public static bool TryParse(ReadOnlySpan<char> s, out byte result)
		{
			bool parsed = Number.TryParseUInt64(s, out ulong value) && value <= MaxValue;
			result = parsed ? (byte)value : (byte)0;
			return parsed;
		}
	}

	public struct Int16 : IEquatable<short>, IComparable<short>, IComparable, ISpanFormattable
	{
		private short m_value;

		public const short MaxValue = 0x7FFF;
		public const short MinValue = -0x8000;

		public override string ToString() => Number.ToString(m_value);
		public string ToString(string format) => Number.ToString(m_value, 16, format.AsSpan());
		public string ToString(string format, IFormatProvider provider) => ToString(format);
		public bool TryFormat(Span<char> destination, out int charsWritten, ReadOnlySpan<char> format = default, IFormatProvider provider = null) =>
			Number.TryFormatSigned(m_value, 16, destination, format, out charsWritten);
		public override bool Equals(object obj) => obj is short other && other == m_value;
		public bool Equals(short obj) => m_value == obj;
		public override int GetHashCode() => m_value;
		public int CompareTo(short value) => m_value - value;
		public int CompareTo(object obj)
		{
			if (obj == null)
				return 1;
			if (obj is short other)
				return CompareTo(other);
			ThrowHelper.ThrowArgument("a value of another type");
			return 0;
		}
	}

	public struct UInt16 : IEquatable<ushort>, IComparable<ushort>, IComparable, ISpanFormattable
	{
		private ushort m_value;

		public const ushort MaxValue = 0xFFFF;
		public const ushort MinValue = 0;

		public override string ToString() => Number.ToString((ulong)m_value);
		public string ToString(string format) => Number.ToString((ulong)m_value, format.AsSpan());
		public string ToString(string format, IFormatProvider provider) => ToString(format);
		public bool TryFormat(Span<char> destination, out int charsWritten, ReadOnlySpan<char> format = default, IFormatProvider provider = null) =>
			Number.TryFormatUnsigned(m_value, destination, format, out charsWritten);
		public override bool Equals(object obj) => obj is ushort other && other == m_value;
		public bool Equals(ushort obj) => m_value == obj;
		public override int GetHashCode() => m_value;
		public int CompareTo(ushort value) => m_value - value;
		public int CompareTo(object obj)
		{
			if (obj == null)
				return 1;
			if (obj is ushort other)
				return CompareTo(other);
			ThrowHelper.ThrowArgument("a value of another type");
			return 0;
		}
		public static bool TryParse(ReadOnlySpan<char> s, out ushort result)
		{
			bool parsed = Number.TryParseUInt64(s, out ulong value) && value <= MaxValue;
			result = parsed ? (ushort)value : (ushort)0;
			return parsed;
		}
	}

	public struct Int32 : IEquatable<int>, IComparable<int>, IComparable, ISpanFormattable
	{
		private int m_value;

		public const int MaxValue = 0x7FFFFFFF;
		public const int MinValue = unchecked((int)0x80000000);

		public override string ToString() => Number.ToString(m_value);
		public string ToString(string format) => Number.ToString(m_value, 32, format.AsSpan());
		public string ToString(string format, IFormatProvider provider) => ToString(format);
		public bool TryFormat(Span<char> destination, out int charsWritten, ReadOnlySpan<char> format = default, IFormatProvider provider = null) =>
			Number.TryFormatSigned(m_value, 32, destination, format, out charsWritten);
		public override bool Equals(object obj) => obj is int other && other == m_value;
		public bool Equals(int obj) => m_value == obj;
		public override int GetHashCode() => m_value;
		public int CompareTo(int value) => m_value < value ? -1 : (m_value > value ? 1 : 0);
		public int CompareTo(object obj)
		{
			if (obj == null)
				return 1;
			if (obj is int other)
				return CompareTo(other);
			ThrowHelper.ThrowArgument("a value of another type");
			return 0;
		}
		public static bool TryParse(ReadOnlySpan<char> s, out int result)
		{
			bool parsed = Number.TryParseInt64(s, out long value) && value >= MinValue && value <= MaxValue;
			result = parsed ? (int)value : 0;
			return parsed;
		}
		public static bool TryParse(string s, out int result) => TryParse(s.AsSpan(), out result);
		public static int Parse(ReadOnlySpan<char> s)
		{
			if (!TryParse(s, out int result))
				ThrowHelper.ThrowArgument("not a number");
			return result;
		}
		public static int Parse(string s) => Parse(s.AsSpan());
	}

	public struct UInt32 : IEquatable<uint>, IComparable<uint>, IComparable, ISpanFormattable
	{
		private uint m_value;

		public const uint MaxValue = 0xFFFFFFFF;
		public const uint MinValue = 0;

		public override string ToString() => Number.ToString((ulong)m_value);
		public string ToString(string format) => Number.ToString((ulong)m_value, format.AsSpan());
		public string ToString(string format, IFormatProvider provider) => ToString(format);
		public bool TryFormat(Span<char> destination, out int charsWritten, ReadOnlySpan<char> format = default, IFormatProvider provider = null) =>
			Number.TryFormatUnsigned(m_value, destination, format, out charsWritten);
		public override bool Equals(object obj) => obj is uint other && other == m_value;
		public bool Equals(uint obj) => m_value == obj;
		public override int GetHashCode() => (int)m_value;
		public int CompareTo(uint value) => m_value < value ? -1 : (m_value > value ? 1 : 0);
		public int CompareTo(object obj)
		{
			if (obj == null)
				return 1;
			if (obj is uint other)
				return CompareTo(other);
			ThrowHelper.ThrowArgument("a value of another type");
			return 0;
		}
		public static bool TryParse(ReadOnlySpan<char> s, out uint result)
		{
			bool parsed = Number.TryParseUInt64(s, out ulong value) && value <= MaxValue;
			result = parsed ? (uint)value : 0;
			return parsed;
		}
		public static bool TryParse(string s, out uint result) => TryParse(s.AsSpan(), out result);
	}

	public struct Int64 : IEquatable<long>, IComparable<long>, IComparable, ISpanFormattable
	{
		private long m_value;

		public const long MaxValue = 0x7FFFFFFFFFFFFFFF;
		public const long MinValue = unchecked((long)0x8000000000000000);

		public override string ToString() => Number.ToString(m_value);
		public string ToString(string format) => Number.ToString(m_value, 64, format.AsSpan());
		public string ToString(string format, IFormatProvider provider) => ToString(format);
		public bool TryFormat(Span<char> destination, out int charsWritten, ReadOnlySpan<char> format = default, IFormatProvider provider = null) =>
			Number.TryFormatSigned(m_value, 64, destination, format, out charsWritten);
		public override bool Equals(object obj) => obj is long other && other == m_value;
		public bool Equals(long obj) => m_value == obj;
		public override int GetHashCode() => (int)m_value ^ (int)(m_value >> 32);
		public int CompareTo(long value) => m_value < value ? -1 : (m_value > value ? 1 : 0);
		public int CompareTo(object obj)
		{
			if (obj == null)
				return 1;
			if (obj is long other)
				return CompareTo(other);
			ThrowHelper.ThrowArgument("a value of another type");
			return 0;
		}
		public static bool TryParse(ReadOnlySpan<char> s, out long result) => Number.TryParseInt64(s, out result);
		public static bool TryParse(string s, out long result) => TryParse(s.AsSpan(), out result);
	}

	public struct UInt64 : IEquatable<ulong>, IComparable<ulong>, IComparable, ISpanFormattable
	{
		private ulong m_value;

		public const ulong MaxValue = 0xFFFFFFFFFFFFFFFF;
		public const ulong MinValue = 0;

		public override string ToString() => Number.ToString(m_value);
		public string ToString(string format) => Number.ToString(m_value, format.AsSpan());
		public string ToString(string format, IFormatProvider provider) => ToString(format);
		public bool TryFormat(Span<char> destination, out int charsWritten, ReadOnlySpan<char> format = default, IFormatProvider provider = null) =>
			Number.TryFormatUnsigned(m_value, destination, format, out charsWritten);
		public override bool Equals(object obj) => obj is ulong other && other == m_value;
		public bool Equals(ulong obj) => m_value == obj;
		public override int GetHashCode() => (int)m_value ^ (int)(m_value >> 32);
		public int CompareTo(ulong value) => m_value < value ? -1 : (m_value > value ? 1 : 0);
		public int CompareTo(object obj)
		{
			if (obj == null)
				return 1;
			if (obj is ulong other)
				return CompareTo(other);
			ThrowHelper.ThrowArgument("a value of another type");
			return 0;
		}
		public static bool TryParse(ReadOnlySpan<char> s, out ulong result) => Number.TryParseUInt64(s, out result);
		public static bool TryParse(string s, out ulong result) => TryParse(s.AsSpan(), out result);
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
	public struct IntPtr : IEquatable<nint>, IComparable<nint>, IComparable, ISpanFormattable
	{
		private unsafe void* _value;

		public static readonly nint Zero;
		public static unsafe int Size => sizeof(nint);
		public static nint MaxValue => unchecked((nint)0x7FFFFFFFFFFFFFFF);
		public static nint MinValue => unchecked((nint)0x8000000000000000);

		public override unsafe string ToString() => Number.ToString((long)(nint)_value);
		public unsafe string ToString(string format) => Number.ToString((long)(nint)_value, 64, format.AsSpan());
		public string ToString(string format, IFormatProvider provider) => ToString(format);
		public unsafe bool TryFormat(Span<char> destination, out int charsWritten, ReadOnlySpan<char> format = default, IFormatProvider provider = null) =>
			Number.TryFormatSigned((long)(nint)_value, 64, destination, format, out charsWritten);
		public override unsafe bool Equals(object obj) => obj is nint other && other == (nint)_value;
		public unsafe bool Equals(nint other) => (nint)_value == other;
		public override unsafe int GetHashCode() => (int)(nint)_value ^ (int)((nint)_value >> 32);
		public unsafe int CompareTo(nint value) => (nint)_value < value ? -1 : ((nint)_value > value ? 1 : 0);
		public int CompareTo(object obj)
		{
			if (obj == null)
				return 1;
			if (obj is nint other)
				return CompareTo(other);
			ThrowHelper.ThrowArgument("a value of another type");
			return 0;
		}
	}

	public struct UIntPtr : IEquatable<nuint>, IComparable<nuint>, IComparable, ISpanFormattable
	{
		private unsafe void* _value;

		public static readonly nuint Zero;
		public static unsafe int Size => sizeof(nuint);
		public static nuint MaxValue => unchecked((nuint)0xFFFFFFFFFFFFFFFF);
		public static nuint MinValue => 0;

		public override unsafe string ToString() => Number.ToString((ulong)(nuint)_value);
		public unsafe string ToString(string format) => Number.ToString((ulong)(nuint)_value, format.AsSpan());
		public string ToString(string format, IFormatProvider provider) => ToString(format);
		public unsafe bool TryFormat(Span<char> destination, out int charsWritten, ReadOnlySpan<char> format = default, IFormatProvider provider = null) =>
			Number.TryFormatUnsigned((ulong)(nuint)_value, destination, format, out charsWritten);
		public override unsafe bool Equals(object obj) => obj is nuint other && other == (nuint)_value;
		public unsafe bool Equals(nuint other) => (nuint)_value == other;
		public override unsafe int GetHashCode() => (int)(nuint)_value ^ (int)((nuint)_value >> 32);
		public unsafe int CompareTo(nuint value) => (nuint)_value < value ? -1 : ((nuint)_value > value ? 1 : 0);
		public int CompareTo(object obj)
		{
			if (obj == null)
				return 1;
			if (obj is nuint other)
				return CompareTo(other);
			ThrowHelper.ThrowArgument("a value of another type");
			return 0;
		}
	}
}
