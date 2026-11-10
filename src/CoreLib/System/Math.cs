namespace System
{
	public static class Math
	{
		public static int Min(int a, int b) => a < b ? a : b;
		public static uint Min(uint a, uint b) => a < b ? a : b;
		public static long Min(long a, long b) => a < b ? a : b;
		public static ulong Min(ulong a, ulong b) => a < b ? a : b;
		public static nuint Min(nuint a, nuint b) => a < b ? a : b;

		public static int Max(int a, int b) => a > b ? a : b;
		public static uint Max(uint a, uint b) => a > b ? a : b;
		public static long Max(long a, long b) => a > b ? a : b;
		public static ulong Max(ulong a, ulong b) => a > b ? a : b;
		public static nuint Max(nuint a, nuint b) => a > b ? a : b;

		public static int Abs(int value)
		{
			if (value == int.MinValue)
				ThrowHelper.FailFast("FAILFAST: Math.Abs overflow");
			return value < 0 ? -value : value;
		}

		public static long Abs(long value)
		{
			if (value == long.MinValue)
				ThrowHelper.FailFast("FAILFAST: Math.Abs overflow");
			return value < 0 ? -value : value;
		}

		// Checked floating point conversions; the JIT calls these by name (JitHelper.cs)
		private static int ConvertToInt32Checked(double value)
		{
			if (!(value > -2147483649.0 && value < 2147483648.0))
				ThrowHelper.FailFast("FAILFAST: overflow");
			return (int)value;
		}

		private static uint ConvertToUInt32Checked(double value)
		{
			if (!(value > -1.0 && value < 4294967296.0))
				ThrowHelper.FailFast("FAILFAST: overflow");
			return (uint)value;
		}

		private static long ConvertToInt64Checked(double value)
		{
			if (!(value >= -9223372036854775808.0 && value < 9223372036854775808.0))
				ThrowHelper.FailFast("FAILFAST: overflow");
			return (long)value;
		}

		private static ulong ConvertToUInt64Checked(double value)
		{
			if (!(value > -1.0 && value < 18446744073709551616.0))
				ThrowHelper.FailFast("FAILFAST: overflow");
			return (ulong)value;
		}
	}
}
