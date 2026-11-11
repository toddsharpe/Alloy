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

		// References are plain words here: no GC means no write barrier to keep
		public static object CompareExchange(ref object location1, object value, object comparand)
		{
			nint result = CompareExchange(ref Unsafe.As<object, nint>(ref location1), Unsafe.As<object, nint>(ref value), Unsafe.As<object, nint>(ref comparand));
			return Unsafe.As<nint, object>(ref result);
		}

		public static T CompareExchange<T>(ref T location1, T value, T comparand) where T : class
		{
			nint result = CompareExchange(ref Unsafe.As<T, nint>(ref location1), Unsafe.As<T, nint>(ref value), Unsafe.As<T, nint>(ref comparand));
			return Unsafe.As<nint, T>(ref result);
		}

		[Intrinsic]
		public static int Exchange(ref int location1, int value) => Exchange(ref location1, value);

		[Intrinsic]
		public static long Exchange(ref long location1, long value) => Exchange(ref location1, value);

		public static nint Exchange(ref nint location1, nint value) => (nint)Exchange(ref Unsafe.As<nint, long>(ref location1), value);

		public static uint Exchange(ref uint location1, uint value) => (uint)Exchange(ref Unsafe.As<uint, int>(ref location1), (int)value);

		public static ulong Exchange(ref ulong location1, ulong value) => (ulong)Exchange(ref Unsafe.As<ulong, long>(ref location1), (long)value);

		public static object Exchange(ref object location1, object value)
		{
			nint result = Exchange(ref Unsafe.As<object, nint>(ref location1), Unsafe.As<object, nint>(ref value));
			return Unsafe.As<nint, object>(ref result);
		}

		public static T Exchange<T>(ref T location1, T value) where T : class
		{
			nint result = Exchange(ref Unsafe.As<T, nint>(ref location1), Unsafe.As<T, nint>(ref value));
			return Unsafe.As<nint, T>(ref result);
		}

		[Intrinsic]
		public static int ExchangeAdd(ref int location1, int value) => ExchangeAdd(ref location1, value);

		[Intrinsic]
		public static long ExchangeAdd(ref long location1, long value) => ExchangeAdd(ref location1, value);

		public static int Add(ref int location1, int value) => ExchangeAdd(ref location1, value) + value;

		public static long Add(ref long location1, long value) => ExchangeAdd(ref location1, value) + value;

		public static uint Add(ref uint location1, uint value) => (uint)Add(ref Unsafe.As<uint, int>(ref location1), (int)value);

		public static ulong Add(ref ulong location1, ulong value) => (ulong)Add(ref Unsafe.As<ulong, long>(ref location1), (long)value);

		public static int Increment(ref int location) => Add(ref location, 1);

		public static long Increment(ref long location) => Add(ref location, 1);

		public static uint Increment(ref uint location) => Add(ref location, 1);

		public static ulong Increment(ref ulong location) => Add(ref location, 1);

		public static int Decrement(ref int location) => Add(ref location, -1);

		public static long Decrement(ref long location) => Add(ref location, -1);

		public static int Or(ref int location1, int value)
		{
			int current = location1;
			while (true)
			{
				int seen = CompareExchange(ref location1, current | value, current);
				if (seen == current)
					return seen;
				current = seen;
			}
		}

		public static int And(ref int location1, int value)
		{
			int current = location1;
			while (true)
			{
				int seen = CompareExchange(ref location1, current & value, current);
				if (seen == current)
					return seen;
				current = seen;
			}
		}

		[Intrinsic]
		public static void MemoryBarrier() => MemoryBarrier();
	}
}
