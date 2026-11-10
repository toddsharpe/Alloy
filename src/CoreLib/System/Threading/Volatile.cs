using System.Runtime.CompilerServices;

namespace System.Threading
{
	// Reads and writes through a volatile field, so the compiler neither caches nor reorders them
	public static class Volatile
	{
		private struct VolatileByte
		{
			public volatile byte Value;
		}

		private struct VolatileInt32
		{
			public volatile int Value;
		}

		private struct VolatileUInt32
		{
			public volatile uint Value;
		}

		public static byte Read(ref byte location) => Unsafe.As<byte, VolatileByte>(ref location).Value;

		public static void Write(ref byte location, byte value) => Unsafe.As<byte, VolatileByte>(ref location).Value = value;

		public static int Read(ref int location) => Unsafe.As<int, VolatileInt32>(ref location).Value;

		public static void Write(ref int location, int value) => Unsafe.As<int, VolatileInt32>(ref location).Value = value;

		public static uint Read(ref uint location) => Unsafe.As<uint, VolatileUInt32>(ref location).Value;

		public static void Write(ref uint location, uint value) => Unsafe.As<uint, VolatileUInt32>(ref location).Value = value;
	}
}
