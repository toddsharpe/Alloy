using System.Runtime.CompilerServices;

namespace System.Threading
{
	// What lock compiles to: a thin lock in the word before each object, with the owner's thread id above a recursion count
	public static partial class Monitor
	{
		private const int OwnerShift = 32;
		private const long CountMask = 0xFFFFFFFF;

		public static void Enter(object obj)
		{
			bool lockTaken = false;
			Enter(obj, ref lockTaken);
		}

		// Spins through Yield while another thread holds the lock, so this must not be used from an interrupt handler
		public static void Enter(object obj, ref bool lockTaken)
		{
			if (obj == null)
				ThrowHelper.ThrowArgument("a null lock object");
			ref long header = ref HeaderOf(obj);
			long owner = (long)CurrentThreadId() << OwnerShift;
			while (true)
			{
				long current = header;
				if (current == 0)
				{
					if (Interlocked.CompareExchange(ref header, owner | 1, 0) == 0)
						break;
					continue;
				}
				if ((current & ~CountMask) == owner)
				{
					header = current + 1;
					break;
				}
				Yield();
			}
			lockTaken = true;
		}

		public static bool TryEnter(object obj)
		{
			if (obj == null)
				ThrowHelper.ThrowArgument("a null lock object");
			ref long header = ref HeaderOf(obj);
			long owner = (long)CurrentThreadId() << OwnerShift;
			long current = header;
			if (current == 0)
				return Interlocked.CompareExchange(ref header, owner | 1, 0) == 0;
			if ((current & ~CountMask) != owner)
				return false;
			header = current + 1;
			return true;
		}

		public static void TryEnter(object obj, ref bool lockTaken) => lockTaken = TryEnter(obj);

		public static void Exit(object obj)
		{
			if (obj == null)
				ThrowHelper.ThrowArgument("a null lock object");
			ref long header = ref HeaderOf(obj);
			long current = header;
			if ((current & ~CountMask) != (long)CurrentThreadId() << OwnerShift)
				ThrowHelper.FailFast("FAILFAST: Monitor.Exit by a thread that doesn't hold the lock");
			if ((current & CountMask) == 1)
				Interlocked.Exchange(ref header, 0);
			else
				header = current - 1;
		}

		public static bool IsEntered(object obj)
		{
			if (obj == null)
				ThrowHelper.ThrowArgument("a null lock object");
			return (HeaderOf(obj) & ~CountMask) == (long)CurrentThreadId() << OwnerShift;
		}

		// The ObjHeader word sits just below the MethodTable pointer
		private static unsafe ref long HeaderOf(object obj) => ref *(long*)((byte*)Unsafe.As<object, nint>(ref obj) - sizeof(nint));

		// A nonzero id for the running thread, from the platform
		private static unsafe partial uint CurrentThreadId();

		// Lets the lock's owner run; from the platform
		private static unsafe partial void Yield();
	}
}
