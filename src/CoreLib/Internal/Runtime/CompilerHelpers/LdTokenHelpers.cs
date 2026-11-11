using System;

namespace Internal.Runtime.CompilerHelpers
{
	// What ldtoken compiles to when the compiler can't fold it: the handle, or the Type, for a MethodTable it already knows
	internal static class LdTokenHelpers
	{
		private static unsafe RuntimeTypeHandle GetRuntimeTypeHandle(MethodTable* pEEType) => new RuntimeTypeHandle(pEEType);

		private static unsafe RuntimeType GetRuntimeType(MethodTable* pMT) => Type.GetTypeFromMethodTable(pMT);

		private static unsafe RuntimeMethodHandle GetRuntimeMethodHandle(IntPtr pHandleSignature)
		{
			RuntimeMethodHandle handle;
			*(IntPtr*)&handle = pHandleSignature;
			return handle;
		}

		private static unsafe RuntimeFieldHandle GetRuntimeFieldHandle(IntPtr pHandleSignature)
		{
			RuntimeFieldHandle handle;
			*(IntPtr*)&handle = pHandleSignature;
			return handle;
		}
	}
}
