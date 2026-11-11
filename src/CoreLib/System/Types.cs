using System.Runtime;
using System.Runtime.CompilerServices;
using System.Threading;
using Internal.Runtime;

namespace System
{
	// Contract with the compiler: a flag then the value
	public struct Nullable<T> where T : struct
	{
		private readonly bool hasValue;
		internal T value;

		public Nullable(T value)
		{
			hasValue = true;
			this.value = value;
		}

		public readonly bool HasValue => hasValue;

		public readonly T Value
		{
			get
			{
				if (!hasValue)
					ThrowHelper.FailFast("Nullable object must have a value");
				return value;
			}
		}

		public readonly T GetValueOrDefault() => value;

		public readonly T GetValueOrDefault(T defaultValue) => hasValue ? value : defaultValue;

		public override bool Equals(object other) => hasValue ? value.Equals(other) : other == null;

		public override int GetHashCode() => hasValue ? value.GetHashCode() : 0;

		public override string ToString() => hasValue ? value.ToString() : string.Empty;

		public static implicit operator T?(T value) => new T?(value);

		public static explicit operator T(T? value) => value.Value;
	}

	// Contract with the compiler: one pointer, the type's MethodTable, which is what ldtoken loads
	public struct RuntimeTypeHandle : IEquatable<RuntimeTypeHandle>
	{
		private nint _value;

		internal unsafe RuntimeTypeHandle(MethodTable* methodTable)
		{
			_value = (nint)methodTable;
		}

		public nint Value => _value;

		public bool IsNull => _value == 0;

		internal unsafe MethodTable* ToMethodTable() => (MethodTable*)_value;

		public bool Equals(RuntimeTypeHandle handle) => _value == handle._value;

		public override bool Equals(object obj) => obj is RuntimeTypeHandle handle && _value == handle._value;

		public override int GetHashCode() => (int)_value ^ (int)(_value >> 32);

		public static bool operator ==(RuntimeTypeHandle left, RuntimeTypeHandle right) => left._value == right._value;

		public static bool operator !=(RuntimeTypeHandle left, RuntimeTypeHandle right) => left._value != right._value;
	}

	public struct RuntimeMethodHandle
	{
		private nint _value;
	}

	public struct RuntimeFieldHandle
	{
		private nint _value;
	}

	// No reflection: a Type is its MethodTable, which answers casts and kind questions but has no names or members
	public abstract class Type
	{
		public static readonly Type[] EmptyTypes = new Type[0];

		[Intrinsic]
		public static unsafe Type GetTypeFromHandle(RuntimeTypeHandle handle) => handle.IsNull ? null : GetTypeFromMethodTable(handle.ToMethodTable());

		// One RuntimeType per MethodTable, cached in the type's writable data word
		internal static unsafe RuntimeType GetTypeFromMethodTable(MethodTable* methodTable)
		{
			ref nint slot = ref *(nint*)methodTable->WritableData;
			if (slot != 0)
				return Unsafe.As<nint, RuntimeType>(ref slot);
			RuntimeType created = new RuntimeType(methodTable);
			nint pointer = Unsafe.As<RuntimeType, nint>(ref created);
			if (Interlocked.CompareExchange(ref slot, pointer, 0) != 0)
				Allocator.FreeObject(created);
			return Unsafe.As<nint, RuntimeType>(ref slot);
		}

		public abstract RuntimeTypeHandle TypeHandle { get; }

		internal unsafe MethodTable* MethodTable => TypeHandle.ToMethodTable();

		public unsafe Type BaseType => MethodTable->BaseType == null ? null : GetTypeFromMethodTable(MethodTable->BaseType);

		public unsafe bool IsValueType => MethodTable->IsValueType;

		public unsafe bool IsEnum => MethodTable->IsValueType && MethodTable->BaseType == Internal.Runtime.MethodTable.Of<Enum>();

		public unsafe bool IsPrimitive => MethodTable->IsPrimitive;

		public unsafe bool IsInterface => MethodTable->IsInterface;

		public unsafe bool IsArray => MethodTable->IsArray;

		public unsafe bool IsClass => !MethodTable->IsValueType && !MethodTable->IsInterface && !MethodTable->IsParameterizedType;

		public unsafe bool IsPointer => MethodTable->ElementType == EETypeElementType.Pointer;

		public unsafe bool IsByRef => MethodTable->ElementType == EETypeElementType.ByRef;

		public unsafe bool IsGenericType => MethodTable->IsGeneric;

		public unsafe Type GetElementType() => MethodTable->IsParameterizedType ? GetTypeFromMethodTable(MethodTable->RelatedParameterType) : null;

		public unsafe bool IsAssignableFrom(Type c) => c != null && TypeCast.AreTypesAssignable(c.MethodTable, MethodTable);

		public bool IsAssignableTo(Type targetType) => targetType != null && targetType.IsAssignableFrom(this);

		public bool IsInstanceOfType(object o) => o != null && IsAssignableFrom(o.GetType());

		public bool IsSubclassOf(Type c)
		{
			for (Type type = BaseType; type != null; type = type.BaseType)
			{
				if (type == c)
					return true;
			}
			return false;
		}

		public override bool Equals(object o) => o is Type other && TypeHandle == other.TypeHandle;

		public bool Equals(Type o) => o != null && TypeHandle == o.TypeHandle;

		public override int GetHashCode() => TypeHandle.GetHashCode();

		[Intrinsic]
		public static bool operator ==(Type left, Type right)
		{
			if (ReferenceEquals(left, right))
				return true;
			if (left is null || right is null)
				return false;
			return left.TypeHandle == right.TypeHandle;
		}

		[Intrinsic]
		public static bool operator !=(Type left, Type right) => !(left == right);

		// There are no type names without metadata, so a type prints as its MethodTable address
		public override string ToString()
		{
			Span<char> buffer = stackalloc char[24];
			buffer[0] = 'T';
			buffer[1] = 'y';
			buffer[2] = 'p';
			buffer[3] = 'e';
			buffer[4] = '@';
			Alloy.Text.Number.TryFormatHex((ulong)TypeHandle.Value, buffer.Slice(5), out int written);
			return new string(buffer.Slice(0, 5 + written));
		}
	}

	// Contract with the compiler: the MethodTable pointer is the first field, so ILC can preallocate these for typeof
	internal sealed unsafe class RuntimeType : Type
	{
		private readonly MethodTable* _pUnderlyingEEType;
		private readonly nint _runtimeTypeInfoHandle;

		internal RuntimeType(MethodTable* methodTable)
		{
			_pUnderlyingEEType = methodTable;
		}

		public override RuntimeTypeHandle TypeHandle => new RuntimeTypeHandle(_pUnderlyingEEType);
	}
}
