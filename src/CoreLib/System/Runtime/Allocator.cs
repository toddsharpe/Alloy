using System.Runtime.CompilerServices;
using Internal.Runtime;

namespace System.Runtime
{
	// The platform supplies zeroed memory; with no GC nothing is reclaimed
	internal static partial class Allocator
	{
		// Returns zeroed, 8-byte aligned memory, or null when exhausted
		private static unsafe partial void* AllocateZeroed(nuint size);

		internal static unsafe object NewObject(MethodTable* pMT)
		{
			// BaseSize counts the object header that precedes the MethodTable pointer
			return Place(pMT, pMT->BaseSize);
		}

		internal static unsafe object NewArray(MethodTable* pMT, int length)
		{
			if (length < 0)
				ThrowHelper.ThrowArgumentOutOfRange();
			ulong size = pMT->BaseSize + (ulong)(uint)length * pMT->ComponentSize;
			if (size > int.MaxValue)
				ThrowHelper.ThrowOutOfMemory();

			object array = Place(pMT, (nuint)size);
			Unsafe.As<RawArrayData>(array).Length = (uint)length;
			return array;
		}

		private static unsafe object Place(MethodTable* pMT, nuint size)
		{
			size = (size + 7) & ~(nuint)7;
			byte* memory = (byte*)AllocateZeroed(size);
			if (memory == null)
				ThrowHelper.ThrowOutOfMemory();

			// The object starts after its header
			nint obj = (nint)(memory + sizeof(ObjHeader));
			*(MethodTable**)obj = pMT;
			return Unsafe.As<nint, object>(ref obj);
		}

		// Allocation entry points the compiler calls (JitHelper.cs, AllocFast.asm in dotnet/runtime)
		[RuntimeExport("RhpNewFast")]
		private static unsafe object RhpNewFast(MethodTable* pMT) => NewObject(pMT);

		// Finalizers never run: there is no GC to discover unreachable objects
		[RuntimeExport("RhpNewFinalizable")]
		private static unsafe object RhpNewFinalizable(MethodTable* pMT) => NewObject(pMT);

		[RuntimeExport("RhNewObject")]
		private static unsafe object RhNewObject(MethodTable* pMT) => NewObject(pMT);

		[RuntimeExport("RhpNewArrayFast")]
		private static unsafe object RhpNewArrayFast(MethodTable* pMT, int length) => NewArray(pMT, length);

		[RuntimeExport("RhpNewPtrArrayFast")]
		private static unsafe object RhpNewPtrArrayFast(MethodTable* pMT, int length) => NewArray(pMT, length);

		[RuntimeExport("RhNewArray")]
		private static unsafe object RhNewArray(MethodTable* pMT, int length) => NewArray(pMT, length);
	}
}
