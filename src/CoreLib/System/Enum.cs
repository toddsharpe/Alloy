using System.Runtime.CompilerServices;
using Alloy.Text;
using Internal.Runtime;

namespace System
{
	// Equality and hashing over the raw bytes, which is right for the plain-data structs the images use; structs holding references need their own
	public abstract class ValueType
	{
		public override unsafe bool Equals(object obj)
		{
			if (obj == null || obj.GetMethodTable() != GetMethodTable())
				return false;
			MethodTable* type = GetMethodTable();
			if (type->ContainsGCPointers)
				ThrowHelper.FailFast("FAILFAST: Equals on a struct with references needs an override");
			uint size = type->ValueTypeSize;
			return new ReadOnlySpan<byte>(ref GetRawData(), (int)size).SequenceEqual(new ReadOnlySpan<byte>(ref obj.GetRawData(), (int)size));
		}

		public override unsafe int GetHashCode()
		{
			MethodTable* type = GetMethodTable();
			if (type->ContainsGCPointers)
				ThrowHelper.FailFast("FAILFAST: GetHashCode on a struct with references needs an override");
			ReadOnlySpan<byte> bytes = new ReadOnlySpan<byte>(ref GetRawData(), (int)type->ValueTypeSize);
			uint hash = 2166136261;
			for (int i = 0; i < bytes.Length; i++)
				hash = (hash ^ bytes[i]) * 16777619;
			return (int)hash;
		}

		public override string ToString() => "ValueType";
	}

	// Without metadata an enum has no names, so it prints as its number; the underlying type comes from the MethodTable
	public abstract class Enum : ValueType, IComparable
	{
		private unsafe ulong RawValue(out bool signed)
		{
			EETypeElementType underlying = GetMethodTable()->ElementType;
			ref byte data = ref GetRawData();
			switch (underlying)
			{
				case EETypeElementType.SByte:
					signed = true;
					return (ulong)(long)Unsafe.As<byte, sbyte>(ref data);
				case EETypeElementType.Byte:
					signed = false;
					return data;
				case EETypeElementType.Int16:
					signed = true;
					return (ulong)(long)Unsafe.As<byte, short>(ref data);
				case EETypeElementType.UInt16:
					signed = false;
					return Unsafe.As<byte, ushort>(ref data);
				case EETypeElementType.UInt32:
					signed = false;
					return Unsafe.As<byte, uint>(ref data);
				case EETypeElementType.Int64:
					signed = true;
					return (ulong)Unsafe.As<byte, long>(ref data);
				case EETypeElementType.UInt64:
					signed = false;
					return Unsafe.As<byte, ulong>(ref data);
				default:
					signed = true;
					return (ulong)(long)Unsafe.As<byte, int>(ref data);
			}
		}

		private unsafe int Bits()
		{
			switch (GetMethodTable()->ElementType)
			{
				case EETypeElementType.SByte:
				case EETypeElementType.Byte:
					return 8;
				case EETypeElementType.Int16:
				case EETypeElementType.UInt16:
					return 16;
				case EETypeElementType.Int64:
				case EETypeElementType.UInt64:
					return 64;
				default:
					return 32;
			}
		}

		public override string ToString() => ToString(null);

		public string ToString(string format)
		{
			ulong raw = RawValue(out bool signed);
			Span<char> buffer = stackalloc char[24];
			int written;
			if (signed)
				Number.TryFormatSigned((long)raw, Bits(), buffer, format.AsSpan(), out written);
			else
				Number.TryFormatUnsigned(raw, buffer, format.AsSpan(), out written);
			return new string(buffer.Slice(0, written));
		}

		public unsafe bool HasFlag(Enum flag)
		{
			if (flag == null || flag.GetMethodTable() != GetMethodTable())
				ThrowHelper.ThrowArgument("a flag of another enum type");
			ulong bits = flag.RawValue(out _);
			return (RawValue(out _) & bits) == bits;
		}

		public override unsafe bool Equals(object obj) => obj != null && obj.GetMethodTable() == GetMethodTable() && RawValue(out _) == Unsafe.As<Enum>(obj).RawValue(out _);

		public override int GetHashCode() => RawValue(out _).GetHashCode();

		public unsafe int CompareTo(object target)
		{
			if (target == null)
				return 1;
			if (target.GetMethodTable() != GetMethodTable())
				ThrowHelper.ThrowArgument("a value of another enum type");
			ulong mine = RawValue(out bool signed);
			ulong other = Unsafe.As<Enum>(target).RawValue(out _);
			if (signed)
				return ((long)mine).CompareTo((long)other);
			return mine.CompareTo(other);
		}
	}
}
