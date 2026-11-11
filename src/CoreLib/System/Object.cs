using System.Runtime;
using System.Runtime.CompilerServices;
using System.Runtime.InteropServices;
using Internal.Runtime;

namespace System
{
	// Contract with the compiler: one field (the MethodTable pointer), and the finalizer is the first virtual slot
	public class Object
	{
		internal unsafe MethodTable* m_pEEType;

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

		[Intrinsic]
		public unsafe Type GetType() => Type.GetTypeFromMethodTable(m_pEEType);

		// A shallow copy: the same MethodTable and a byte-for-byte copy of the fields, or of the elements for an array
		protected internal unsafe object MemberwiseClone()
		{
			MethodTable* type = m_pEEType;
			object clone;
			nuint size;
			if (type->HasComponentSize)
			{
				uint length = Unsafe.As<RawArrayData>(this).Length;
				clone = Allocator.NewArray(type, (int)length);
				size = type->BaseSize - (uint)(2 * sizeof(nint)) + (nuint)length * type->ComponentSize;
			}
			else
			{
				clone = Allocator.NewObject(type);
				size = type->BaseSize - (uint)(2 * sizeof(nint));
			}
			SpanHelpers.Memmove(ref clone.GetRawData(), ref GetRawData(), size);
			return clone;
		}

		public static bool ReferenceEquals(object objA, object objB) => objA == objB;

		public static bool Equals(object objA, object objB) => objA == objB || (objA != null && objB != null && objA.Equals(objB));

		internal unsafe MethodTable* GetMethodTable() => m_pEEType;

		[StructLayout(LayoutKind.Sequential)]
		private class RawData
		{
			public byte Data;
		}

		// The data after the MethodTable pointer; for arrays and strings this starts with the length
		internal ref byte GetRawData() => ref Unsafe.As<RawData>(this).Data;

		internal unsafe uint GetRawObjectDataSize() => m_pEEType->BaseSize - (uint)sizeof(ObjHeader) - (uint)sizeof(MethodTable*);
	}

	// Precedes every object; Monitor keeps its thin lock here
	internal struct ObjHeader
	{
		private nint _value;
	}
}
