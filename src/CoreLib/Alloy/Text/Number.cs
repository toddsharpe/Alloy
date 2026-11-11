using System;

namespace Alloy.Text
{
	// Integer formatting into caller buffers; also compiled against the BCL by the host tests
	public static class Number
	{
		private const string HexUpper = "0123456789ABCDEF";
		private const string HexLower = "0123456789abcdef";

		public static bool TryFormatUInt64(ulong value, Span<char> destination, out int charsWritten)
		{
			int digits = CountDigits(value);
			if (digits > destination.Length)
			{
				charsWritten = 0;
				return false;
			}

			for (int i = digits - 1; i >= 0; i--)
			{
				destination[i] = (char)('0' + (int)(value % 10));
				value /= 10;
			}
			charsWritten = digits;
			return true;
		}

		public static bool TryFormatInt64(long value, Span<char> destination, out int charsWritten)
		{
			if (value >= 0)
				return TryFormatUInt64((ulong)value, destination, out charsWritten);

			if (destination.Length == 0)
			{
				charsWritten = 0;
				return false;
			}

			// Negate in unsigned space so long.MinValue works
			destination[0] = '-';
			if (!TryFormatUInt64((ulong)(-(value + 1)) + 1, destination.Slice(1), out int written))
			{
				charsWritten = 0;
				return false;
			}
			charsWritten = written + 1;
			return true;
		}

		public static bool TryFormatHex(ulong value, Span<char> destination, out int charsWritten, int minDigits = 1, bool upperCase = true)
		{
			int digits = 1;
			for (ulong v = value >> 4; v != 0; v >>= 4)
				digits++;
			if (digits < minDigits)
				digits = minDigits;

			if (digits > destination.Length)
			{
				charsWritten = 0;
				return false;
			}

			string table = upperCase ? HexUpper : HexLower;
			for (int i = digits - 1; i >= 0; i--)
			{
				destination[i] = table[(int)(value & 0xF)];
				value >>= 4;
			}
			charsWritten = digits;
			return true;
		}

		// Standard numeric formats: G and D (decimal, D with a minimum digit count) and X or x (hex, a negative value shown in its bit width)
		public static bool TryFormatSigned(long value, int bits, Span<char> destination, ReadOnlySpan<char> format, out int charsWritten)
		{
			char kind = format.Length == 0 ? 'G' : format[0];
			int digits = ParseDigits(format);
			if (kind == 'X' || kind == 'x')
			{
				ulong unsigned = bits >= 64 ? (ulong)value : (ulong)value & ((1UL << bits) - 1);
				return TryFormatHex(unsigned, destination, out charsWritten, digits, kind == 'X');
			}
			if (value < 0 && digits > 0)
				return TryFormatPadded(destination, true, (ulong)(-(value + 1)) + 1, digits, out charsWritten);
			if (digits > 0)
				return TryFormatPadded(destination, false, (ulong)value, digits, out charsWritten);
			return TryFormatInt64(value, destination, out charsWritten);
		}

		public static bool TryFormatUnsigned(ulong value, Span<char> destination, ReadOnlySpan<char> format, out int charsWritten)
		{
			char kind = format.Length == 0 ? 'G' : format[0];
			int digits = ParseDigits(format);
			if (kind == 'X' || kind == 'x')
				return TryFormatHex(value, destination, out charsWritten, digits, kind == 'X');
			if (digits > 0)
				return TryFormatPadded(destination, false, value, digits, out charsWritten);
			return TryFormatUInt64(value, destination, out charsWritten);
		}

		public static string ToString(long value)
		{
			Span<char> buffer = stackalloc char[20];
			TryFormatInt64(value, buffer, out int written);
			return new string(buffer.Slice(0, written));
		}

		public static string ToString(ulong value)
		{
			Span<char> buffer = stackalloc char[20];
			TryFormatUInt64(value, buffer, out int written);
			return new string(buffer.Slice(0, written));
		}

		public static string ToString(long value, int bits, ReadOnlySpan<char> format)
		{
			Span<char> buffer = stackalloc char[64];
			TryFormatSigned(value, bits, buffer, format, out int written);
			return new string(buffer.Slice(0, written));
		}

		public static string ToString(ulong value, ReadOnlySpan<char> format)
		{
			Span<char> buffer = stackalloc char[64];
			TryFormatUnsigned(value, buffer, format, out int written);
			return new string(buffer.Slice(0, written));
		}

		// Decimal with an optional sign and surrounding spaces; false on anything else or on overflow
		public static bool TryParseInt64(ReadOnlySpan<char> text, out long value)
		{
			value = 0;
			text = Trim(text);
			bool negative = text.Length > 0 && text[0] == '-';
			if (text.Length > 0 && (text[0] == '-' || text[0] == '+'))
				text = text.Slice(1);
			if (!TryParseUInt64(text, out ulong magnitude))
				return false;
			if (negative ? magnitude > (ulong)long.MaxValue + 1 : magnitude > long.MaxValue)
				return false;
			value = negative ? (long)(0 - magnitude) : (long)magnitude;
			return true;
		}

		public static bool TryParseUInt64(ReadOnlySpan<char> text, out ulong value)
		{
			value = 0;
			text = Trim(text);
			if (text.Length > 0 && text[0] == '+')
				text = text.Slice(1);
			if (text.Length == 0)
				return false;
			for (int i = 0; i < text.Length; i++)
			{
				uint digit = (uint)(text[i] - '0');
				if (digit > 9 || value > (ulong.MaxValue - digit) / 10)
					return false;
				value = value * 10 + digit;
			}
			return true;
		}

		private static ReadOnlySpan<char> Trim(ReadOnlySpan<char> text)
		{
			int start = 0;
			int end = text.Length;
			while (start < end && text[start] == ' ')
				start++;
			while (end > start && text[end - 1] == ' ')
				end--;
			return text.Slice(start, end - start);
		}

		private static int ParseDigits(ReadOnlySpan<char> format)
		{
			int digits = 0;
			for (int i = 1; i < format.Length; i++)
			{
				if (format[i] < '0' || format[i] > '9')
					break;
				digits = digits * 10 + (format[i] - '0');
			}
			return digits;
		}

		private static bool TryFormatPadded(Span<char> destination, bool negative, ulong magnitude, int minDigits, out int charsWritten)
		{
			int digits = CountDigits(magnitude);
			if (digits < minDigits)
				digits = minDigits;
			int length = digits + (negative ? 1 : 0);
			if (length > destination.Length)
			{
				charsWritten = 0;
				return false;
			}
			if (negative)
				destination[0] = '-';
			for (int i = length - 1; i >= length - digits; i--)
			{
				destination[i] = (char)('0' + (int)(magnitude % 10));
				magnitude /= 10;
			}
			charsWritten = length;
			return true;
		}

		private static int CountDigits(ulong value)
		{
			int digits = 1;
			while (value >= 10)
			{
				value /= 10;
				digits++;
			}
			return digits;
		}
	}
}
