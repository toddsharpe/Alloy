using System.Runtime.CompilerServices;

namespace System
{
	public static class MemoryExtensions
	{
		// C# 14 lowers every string-to-span conversion to this method, so it must not use that conversion itself
		public static ReadOnlySpan<char> AsSpan(this string text) =>
			(object)text == null ? default : new ReadOnlySpan<char>(ref text.GetRawStringData(), text.Length);

		public static ReadOnlySpan<char> AsSpan(this string text, int start) => ((ReadOnlySpan<char>)text).Slice(start);

		public static ReadOnlySpan<char> AsSpan(this string text, int start, int length) => ((ReadOnlySpan<char>)text).Slice(start, length);

		public static Span<T> AsSpan<T>(this T[] array) => new Span<T>(array);

		public static Span<T> AsSpan<T>(this T[] array, int start) => new Span<T>(array).Slice(start);

		public static Span<T> AsSpan<T>(this T[] array, int start, int length) => new Span<T>(array, start, length);

		public static bool SequenceEqual(this ReadOnlySpan<char> span, ReadOnlySpan<char> other)
		{
			if (span.Length != other.Length)
				return false;
			for (int i = 0; i < span.Length; i++)
			{
				if (span[i] != other[i])
					return false;
			}
			return true;
		}

		public static bool SequenceEqual(this ReadOnlySpan<byte> span, ReadOnlySpan<byte> other)
		{
			if (span.Length != other.Length)
				return false;
			for (int i = 0; i < span.Length; i++)
			{
				if (span[i] != other[i])
					return false;
			}
			return true;
		}

		public static bool SequenceEqual(this Span<byte> span, ReadOnlySpan<byte> other) => SequenceEqual((ReadOnlySpan<byte>)span, other);

		public static bool StartsWith(this ReadOnlySpan<char> span, ReadOnlySpan<char> value) =>
			value.Length <= span.Length && span.Slice(0, value.Length).SequenceEqual(value);

		public static int IndexOf(this ReadOnlySpan<char> span, char value)
		{
			for (int i = 0; i < span.Length; i++)
			{
				if (span[i] == value)
					return i;
			}
			return -1;
		}

		public static int LastIndexOf(this ReadOnlySpan<char> span, char value)
		{
			for (int i = span.Length - 1; i >= 0; i--)
			{
				if (span[i] == value)
					return i;
			}
			return -1;
		}

		public static int IndexOf(this ReadOnlySpan<byte> span, byte value)
		{
			for (int i = 0; i < span.Length; i++)
			{
				if (span[i] == value)
					return i;
			}
			return -1;
		}
	}
}
