using System.Runtime.CompilerServices;

namespace System
{
	public static partial class MemoryExtensions
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

		public static bool SequenceEqual<T>(this ReadOnlySpan<T> span, ReadOnlySpan<T> other) where T : IEquatable<T>
		{
			if (span.Length != other.Length)
				return false;
			for (int i = 0; i < span.Length; i++)
			{
				if (!span[i].Equals(other[i]))
					return false;
			}
			return true;
		}

		public static bool SequenceEqual<T>(this Span<T> span, ReadOnlySpan<T> other) where T : IEquatable<T> => SequenceEqual((ReadOnlySpan<T>)span, other);

		public static bool StartsWith(this ReadOnlySpan<char> span, ReadOnlySpan<char> value) =>
			value.Length <= span.Length && span.Slice(0, value.Length).SequenceEqual(value);

		public static bool EndsWith(this ReadOnlySpan<char> span, ReadOnlySpan<char> value) =>
			value.Length <= span.Length && span.Slice(span.Length - value.Length).SequenceEqual(value);

		public static bool StartsWith<T>(this ReadOnlySpan<T> span, ReadOnlySpan<T> value) where T : IEquatable<T> =>
			value.Length <= span.Length && span.Slice(0, value.Length).SequenceEqual(value);

		public static bool EndsWith<T>(this ReadOnlySpan<T> span, ReadOnlySpan<T> value) where T : IEquatable<T> =>
			value.Length <= span.Length && span.Slice(span.Length - value.Length).SequenceEqual(value);

		public static int IndexOf(this ReadOnlySpan<char> span, char value)
		{
			for (int i = 0; i < span.Length; i++)
			{
				if (span[i] == value)
					return i;
			}
			return -1;
		}

		public static int IndexOf(this ReadOnlySpan<char> span, ReadOnlySpan<char> value)
		{
			if (value.Length == 0)
				return 0;
			for (int i = 0; i + value.Length <= span.Length; i++)
			{
				if (span.Slice(i, value.Length).SequenceEqual(value))
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

		public static int IndexOf<T>(this ReadOnlySpan<T> span, T value) where T : IEquatable<T>
		{
			for (int i = 0; i < span.Length; i++)
			{
				if (span[i].Equals(value))
					return i;
			}
			return -1;
		}

		public static int IndexOf<T>(this Span<T> span, T value) where T : IEquatable<T> => IndexOf((ReadOnlySpan<T>)span, value);

		public static int LastIndexOf<T>(this ReadOnlySpan<T> span, T value) where T : IEquatable<T>
		{
			for (int i = span.Length - 1; i >= 0; i--)
			{
				if (span[i].Equals(value))
					return i;
			}
			return -1;
		}

		public static bool Contains<T>(this ReadOnlySpan<T> span, T value) where T : IEquatable<T> => IndexOf(span, value) >= 0;

		public static bool Contains<T>(this Span<T> span, T value) where T : IEquatable<T> => IndexOf((ReadOnlySpan<T>)span, value) >= 0;

		public static bool Contains(this ReadOnlySpan<char> span, char value) => IndexOf(span, value) >= 0;

		public static void Reverse<T>(this Span<T> span)
		{
			for (int i = 0, j = span.Length - 1; i < j; i++, j--)
				(span[i], span[j]) = (span[j], span[i]);
		}

		public static ReadOnlySpan<char> Trim(this ReadOnlySpan<char> span) => span.TrimStart().TrimEnd();

		public static ReadOnlySpan<char> TrimStart(this ReadOnlySpan<char> span)
		{
			int start = 0;
			while (start < span.Length && char.IsWhiteSpace(span[start]))
				start++;
			return span.Slice(start);
		}

		public static ReadOnlySpan<char> TrimEnd(this ReadOnlySpan<char> span)
		{
			int end = span.Length;
			while (end > 0 && char.IsWhiteSpace(span[end - 1]))
				end--;
			return span.Slice(0, end);
		}

		public static ReadOnlySpan<char> Trim(this ReadOnlySpan<char> span, char trimChar)
		{
			int start = 0;
			int end = span.Length;
			while (start < end && span[start] == trimChar)
				start++;
			while (end > start && span[end - 1] == trimChar)
				end--;
			return span.Slice(start, end - start);
		}

		public static bool IsWhiteSpace(this ReadOnlySpan<char> span)
		{
			for (int i = 0; i < span.Length; i++)
			{
				if (!char.IsWhiteSpace(span[i]))
					return false;
			}
			return true;
		}
	}
}
