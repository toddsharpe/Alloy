using System.Runtime;
using System.Runtime.CompilerServices;

namespace System
{
	// The JIT lowers block copies and zeroing to these by name (JitHelper.cs); they forward to rep movsb/stosb
	internal static class SpanHelpers
	{
		internal static unsafe void Memmove(ref byte destination, ref byte source, nuint length) =>
			memmove(Unsafe.AsPointer(ref destination), Unsafe.AsPointer(ref source), length);

		internal static unsafe void Fill(ref byte destination, byte value, nuint length) =>
			memset(Unsafe.AsPointer(ref destination), value, length);

		internal static unsafe void ClearWithoutReferences(ref byte destination, nuint length) =>
			memset(Unsafe.AsPointer(ref destination), 0, length);

		[RuntimeImport("*", "memmove")]
		[MethodImpl(MethodImplOptions.InternalCall)]
		private static extern unsafe void* memmove(void* destination, void* source, nuint length);

		[RuntimeImport("*", "memset")]
		[MethodImpl(MethodImplOptions.InternalCall)]
		private static extern unsafe void* memset(void* destination, int value, nuint length);
	}

	public static class Buffer
	{
		// No GC yet, so copying references needs no barrier bookkeeping
		internal static void BulkMoveWithWriteBarrier(ref byte destination, ref byte source, nuint byteCount) =>
			SpanHelpers.Memmove(ref destination, ref source, byteCount);
	}
}
