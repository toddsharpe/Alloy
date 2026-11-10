using System.Runtime.CompilerServices;
using System.Runtime.InteropServices;
using Internal.Runtime;

namespace System
{
	// Contract with the compiler: one field (the MethodTable pointer), and the finalizer is the first virtual slot
	public class Object
	{
		internal unsafe MethodTable* m_pMethodTable;

		public Object()
		{
		}

		~Object()
		{
		}

		public virtual bool Equals(object obj) => this == obj;

		// The heap never moves objects, so the address is a stable identity hash
		public virtual int GetHashCode()
		{
			object self = this;
			nint address = Unsafe.As<object, nint>(ref self);
			return (int)address ^ (int)(address >> 32);
		}

		public virtual string ToString() => "System.Object";

		public static bool ReferenceEquals(object objA, object objB) => objA == objB;

		internal unsafe MethodTable* GetMethodTable() => m_pMethodTable;

		[StructLayout(LayoutKind.Sequential)]
		private class RawData
		{
			public byte Data;
		}

		// The data after the MethodTable pointer; for arrays and strings this starts with the length
		internal ref byte GetRawData() => ref Unsafe.As<RawData>(this).Data;

		internal unsafe uint GetRawObjectDataSize() => m_pMethodTable->BaseSize - (uint)sizeof(ObjHeader) - (uint)sizeof(MethodTable*);
	}

	// Precedes every object; unused until there is locking or hashing state to keep
	internal struct ObjHeader
	{
		private nint _value;
	}
}
