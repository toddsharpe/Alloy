using System.Runtime.CompilerServices;

namespace Internal.Runtime
{
	// The .NET 10 NativeAOT MethodTable layout (dotnet/runtime MethodTable.cs); revisit on every ILC upgrade
	internal struct MethodTable
	{
		// Upper 16 bits: EETypeFlags. Lower 16 bits: component size (arrays, strings) or EETypeFlagsEx
		private uint _uFlags;
		private uint _uBaseSize;
		// Base type for canonical types; element type for arrays and pointers
		private unsafe MethodTable* _relatedType;
		private ushort _usNumVtableSlots;
		private ushort _usNumInterfaces;
		private uint _uHashCode;
		// Followed by the vtable, then the interface map (_usNumInterfaces full pointers), then optional fields

		private const uint EETypeKindMask = 0x00030000;
		private const uint HasFinalizerFlag = 0x00100000;
		private const uint GenericVarianceFlag = 0x00800000;
		private const uint HasPointersFlag = 0x01000000;
		private const uint IsGenericFlag = 0x02000000;
		private const uint ElementTypeMask = 0x7C000000;
		private const int ElementTypeShift = 26;
		private const uint HasComponentSizeFlag = 0x80000000;
		private const uint IDynamicInterfaceCastableFlag = 0x0008;
		private const uint ValueTypeFieldPaddingMask = 0x00E0;
		private const int ValueTypeFieldPaddingShift = 5;

		private const uint CanonicalKind = 0x00000000;
		private const uint ParameterizedKind = 0x00020000;

		// MethodTable* + length + padding on 64-bit
		internal const uint SzArrayBaseSize = 24;

		[Intrinsic]
		internal static extern unsafe MethodTable* Of<T>();

		// Whether the compiler emitted 32-bit relative pointers in its static data tables (true on 64-bit)
		internal static bool SupportsRelativePointers
		{
			[Intrinsic]
			get => SupportsRelativePointers;
		}

		internal uint BaseSize => _uBaseSize;

		internal bool HasComponentSize => (_uFlags & HasComponentSizeFlag) != 0;

		internal ushort ComponentSize => HasComponentSize ? (ushort)_uFlags : (ushort)0;

		internal EETypeElementType ElementType => (EETypeElementType)((_uFlags & ElementTypeMask) >> ElementTypeShift);

		internal bool IsCanonical => (_uFlags & EETypeKindMask) == CanonicalKind;

		internal bool IsParameterizedType => (_uFlags & EETypeKindMask) == ParameterizedKind;

		internal bool IsArray => ElementType == EETypeElementType.Array || ElementType == EETypeElementType.SzArray;

		internal bool IsSzArray => ElementType == EETypeElementType.SzArray;

		internal bool IsInterface => ElementType == EETypeElementType.Interface;

		internal bool IsValueType => ElementType < EETypeElementType.Class;

		internal bool IsPrimitive => ElementType < EETypeElementType.ValueType;

		internal bool IsNullable => ElementType == EETypeElementType.Nullable;

		internal bool HasFinalizer => (_uFlags & HasFinalizerFlag) != 0;

		internal bool ContainsGCPointers => (_uFlags & HasPointersFlag) != 0;

		internal bool IsGeneric => (_uFlags & IsGenericFlag) != 0;

		internal bool HasGenericVariance => (_uFlags & GenericVarianceFlag) != 0;

		internal bool IsIDynamicInterfaceCastable => !HasComponentSize && (_uFlags & IDynamicInterfaceCastableFlag) != 0;

		// Base type of a canonical type; null for interfaces and System.Object
		internal unsafe MethodTable* BaseType => IsCanonical ? _relatedType : (IsArray ? Of<System.Array>() : null);

		// Element type of an array or pointer
		internal unsafe MethodTable* RelatedParameterType => IsParameterizedType ? _relatedType : null;

		internal ushort NumInterfaces => _usNumInterfaces;

		internal unsafe MethodTable** InterfaceMap =>
			(MethodTable**)((byte*)Unsafe.AsPointer(ref this) + sizeof(MethodTable) + sizeof(void*) * _usNumVtableSlots);

		// Size of a boxed value's payload: the instance size minus the object header, MethodTable pointer and tail padding
		internal unsafe uint ValueTypeSize => _uBaseSize - (uint)(sizeof(nint) * 2) - ((_uFlags & ValueTypeFieldPaddingMask) >> ValueTypeFieldPaddingShift);
	}

	internal enum EETypeElementType
	{
		Unknown = 0x00,
		Void = 0x01,
		Boolean = 0x02,
		Char = 0x03,
		SByte = 0x04,
		Byte = 0x05,
		Int16 = 0x06,
		UInt16 = 0x07,
		Int32 = 0x08,
		UInt32 = 0x09,
		Int64 = 0x0A,
		UInt64 = 0x0B,
		IntPtr = 0x0C,
		UIntPtr = 0x0D,
		Single = 0x0E,
		Double = 0x0F,

		ValueType = 0x10,
		Nullable = 0x12,

		Class = 0x14,
		Interface = 0x15,

		SystemArray = 0x16,

		Array = 0x17,
		SzArray = 0x18,
		ByRef = 0x19,
		Pointer = 0x1A,
		FunctionPointer = 0x1B,
	}
}
