using System.Runtime.CompilerServices;

namespace System.Threading
{
	// The JIT expands each [Intrinsic] method to a locked instruction; the self-call never runs
	public static class Interlocked
	{
		[Intrinsic]
		public static int CompareExchange(ref int location1, int value, int comparand) => CompareExchange(ref location1, value, comparand);

		[Intrinsic]
		public static long CompareExchange(ref long location1, long value, long comparand) => CompareExchange(ref location1, value, comparand);

		public static nint CompareExchange(ref nint location1, nint value, nint comparand) =>
			(nint)CompareExchange(ref Unsafe.As<nint, long>(ref location1), value, comparand);

		public static uint CompareExchange(ref uint location1, uint value, uint comparand) =>
			(uint)CompareExchange(ref Unsafe.As<uint, int>(ref location1), (int)value, (int)comparand);

		public static ulong CompareExchange(ref ulong location1, ulong value, ulong comparand) =>
			(ulong)CompareExchange(ref Unsafe.As<ulong, long>(ref location1), (long)value, (long)comparand);

		[Intrinsic]
		public static int Exchange(ref int location1, int value) => Exchange(ref location1, value);

		[Intrinsic]
		public static long Exchange(ref long location1, long value) => Exchange(ref location1, value);

		public static nint Exchange(ref nint location1, nint value) => (nint)Exchange(ref Unsafe.As<nint, long>(ref location1), value);

		[Intrinsic]
		public static int ExchangeAdd(ref int location1, int value) => ExchangeAdd(ref location1, value);

		[Intrinsic]
		public static long ExchangeAdd(ref long location1, long value) => ExchangeAdd(ref location1, value);

		public static int Add(ref int location1, int value) => ExchangeAdd(ref location1, value) + value;

		public static long Add(ref long location1, long value) => ExchangeAdd(ref location1, value) + value;

		public static int Increment(ref int location) => Add(ref location, 1);

		public static long Increment(ref long location) => Add(ref location, 1);

		public static int Decrement(ref int location) => Add(ref location, -1);

		public static long Decrement(ref long location) => Add(ref location, -1);

		[Intrinsic]
		public static void MemoryBarrier() => MemoryBarrier();
	}
}
