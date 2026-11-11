using System.Runtime.CompilerServices;

namespace System.Collections.Generic
{
	// Create is an ILC intrinsic, like EqualityComparer<T>.Create; the class names below are the contract
	public abstract class Comparer<T> : IComparer, IComparer<T>
	{
		[Intrinsic]
		private static Comparer<T> Create() => new ObjectComparer<T>();

		public static Comparer<T> Default { [Intrinsic] get; } = Create();

		public abstract int Compare(T x, T y);

		int IComparer.Compare(object x, object y)
		{
			if (x == null)
				return y == null ? 0 : -1;
			if (y == null)
				return 1;
			if (x is T first && y is T second)
				return Compare(first, second);
			ThrowHelper.ThrowArgument("values of another type");
			return 0;
		}
	}

	public sealed class GenericComparer<T> : Comparer<T> where T : IComparable<T>
	{
		public override int Compare(T x, T y)
		{
			if (x != null)
				return y != null ? x.CompareTo(y) : 1;
			return y != null ? -1 : 0;
		}

		public override bool Equals(object obj) => obj != null && GetType() == obj.GetType();

		public override int GetHashCode() => GetType().GetHashCode();
	}

	public sealed class NullableComparer<T> : Comparer<T?> where T : struct
	{
		public override int Compare(T? x, T? y)
		{
			if (x.HasValue)
				return y.HasValue ? Comparer<T>.Default.Compare(x.value, y.value) : 1;
			return y.HasValue ? -1 : 0;
		}

		public override bool Equals(object obj) => obj != null && GetType() == obj.GetType();

		public override int GetHashCode() => GetType().GetHashCode();
	}

	// Compares the underlying values as signed numbers; a ulong enum above long.MaxValue sorts below the rest
	public sealed class EnumComparer<T> : Comparer<T> where T : struct, Enum
	{
		public override int Compare(T x, T y) => EnumHelpers.ToInt64(x).CompareTo(EnumHelpers.ToInt64(y));

		public override bool Equals(object obj) => obj != null && GetType() == obj.GetType();

		public override int GetHashCode() => GetType().GetHashCode();
	}

	public sealed class ObjectComparer<T> : Comparer<T>
	{
		public override int Compare(T x, T y)
		{
			if (x == null)
				return y == null ? 0 : -1;
			if (y == null)
				return 1;
			if (x is IComparable comparable)
				return comparable.CompareTo(y);
			ThrowHelper.FailFast("FAILFAST: Comparer<T>.Default needs T to implement IComparable");
			return 0;
		}

		public override bool Equals(object obj) => obj != null && GetType() == obj.GetType();

		public override int GetHashCode() => GetType().GetHashCode();
	}
}
