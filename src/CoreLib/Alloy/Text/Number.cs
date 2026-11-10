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
