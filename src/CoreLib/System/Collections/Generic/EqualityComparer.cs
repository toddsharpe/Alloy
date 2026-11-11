using System.Runtime.CompilerServices;

namespace System.Collections.Generic
{
	// Create is an ILC intrinsic: for each T the compiler picks one of the comparer classes below by name, so their names and namespace are a contract
	public abstract class EqualityComparer<T> : IEqualityComparer, IEqualityComparer<T>
	{
		[Intrinsic]
		private static EqualityComparer<T> Create()
		{
			// Only shared generic code gets here; reference types answer through their own Equals and GetHashCode
			if (typeof(T) == typeof(string))
				return Unsafe.As<EqualityComparer<T>>(new StringEqualityComparer());
			return new ObjectEqualityComparer<T>();
		}

		public static EqualityComparer<T> Default { [Intrinsic] get; } = Create();

		public abstract bool Equals(T x, T y);

		public abstract int GetHashCode(T obj);

		int IEqualityComparer.GetHashCode(object obj)
		{
			if (obj == null)
				return 0;
			if (obj is T value)
				return GetHashCode(value);
			ThrowHelper.ThrowArgument("a value of another type");
			return 0;
		}

		bool IEqualityComparer.Equals(object x, object y)
		{
			if (x == y)
				return true;
			if (x == null || y == null)
				return false;
			if (x is T first && y is T second)
				return Equals(first, second);
			ThrowHelper.ThrowArgument("values of another type");
			return false;
		}
	}

	// Binds Equals to IEquatable<T>.Equals(T) instead of Object.Equals(Object)
	public sealed class GenericEqualityComparer<T> : EqualityComparer<T> where T : IEquatable<T>
	{
		public override bool Equals(T x, T y)
		{
			if (x != null)
				return y != null && x.Equals(y);
			return y == null;
		}

		public override int GetHashCode(T obj) => obj == null ? 0 : obj.GetHashCode();

		public override bool Equals(object obj) => obj != null && GetType() == obj.GetType();

		public override int GetHashCode() => GetType().GetHashCode();
	}

	public sealed class NullableEqualityComparer<T> : EqualityComparer<T?> where T : struct
	{
		public override bool Equals(T? x, T? y)
		{
			if (x.HasValue)
				return y.HasValue && EqualityComparer<T>.Default.Equals(x.value, y.value);
			return !y.HasValue;
		}

		public override int GetHashCode(T? obj) => obj.GetHashCode();

		public override bool Equals(object obj) => obj != null && GetType() == obj.GetType();

		public override int GetHashCode() => GetType().GetHashCode();
	}

	public sealed class ObjectEqualityComparer<T> : EqualityComparer<T>
	{
		public override bool Equals(T x, T y)
		{
			if (x != null)
				return y != null && x.Equals(y);
			return y == null;
		}

		public override int GetHashCode(T obj) => obj == null ? 0 : obj.GetHashCode();

		public override bool Equals(object obj) => obj != null && GetType() == obj.GetType();

		public override int GetHashCode() => GetType().GetHashCode();
	}

	public sealed class EnumEqualityComparer<T> : EqualityComparer<T> where T : struct, Enum
	{
		public override bool Equals(T x, T y) => EnumHelpers.ToUInt64(x) == EnumHelpers.ToUInt64(y);

		public override int GetHashCode(T obj) => EnumHelpers.ToUInt64(obj).GetHashCode();

		public override bool Equals(object obj) => obj != null && GetType() == obj.GetType();

		public override int GetHashCode() => GetType().GetHashCode();
	}

	internal sealed class StringEqualityComparer : EqualityComparer<string>
	{
		public override bool Equals(string x, string y) => string.Equals(x, y);

		public override int GetHashCode(string obj) => obj == null ? 0 : obj.GetHashCode();

		public override bool Equals(object obj) => obj != null && GetType() == obj.GetType();

		public override int GetHashCode() => GetType().GetHashCode();
	}

	// Reads an enum's underlying value without knowing its type, for the enum comparers and formatting
	internal static class EnumHelpers
	{
		public static ulong ToUInt64<T>(T value) where T : struct
		{
			switch (Unsafe.SizeOf<T>())
			{
				case 1:
					return Unsafe.As<T, byte>(ref value);
				case 2:
					return Unsafe.As<T, ushort>(ref value);
				case 4:
					return Unsafe.As<T, uint>(ref value);
				default:
					return Unsafe.As<T, ulong>(ref value);
			}
		}

		public static long ToInt64<T>(T value) where T : struct
		{
			switch (Unsafe.SizeOf<T>())
			{
				case 1:
					return Unsafe.As<T, sbyte>(ref value);
				case 2:
					return Unsafe.As<T, short>(ref value);
				case 4:
					return Unsafe.As<T, int>(ref value);
				default:
					return Unsafe.As<T, long>(ref value);
			}
		}
	}
}
