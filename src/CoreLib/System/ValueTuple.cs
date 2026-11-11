using System.Collections.Generic;
using System.Text;

namespace System
{
	// The compiler's tuple types; element names live only in TupleElementNamesAttribute
	public struct ValueTuple : IEquatable<ValueTuple>
	{
		public bool Equals(ValueTuple other) => true;

		public override bool Equals(object obj) => obj is ValueTuple;

		public override int GetHashCode() => 0;

		public override string ToString() => "()";

		public static ValueTuple<T1> Create<T1>(T1 item1) => new ValueTuple<T1>(item1);

		public static ValueTuple<T1, T2> Create<T1, T2>(T1 item1, T2 item2) => new ValueTuple<T1, T2>(item1, item2);

		public static ValueTuple<T1, T2, T3> Create<T1, T2, T3>(T1 item1, T2 item2, T3 item3) => new ValueTuple<T1, T2, T3>(item1, item2, item3);

		public static ValueTuple<T1, T2, T3, T4> Create<T1, T2, T3, T4>(T1 item1, T2 item2, T3 item3, T4 item4) => new ValueTuple<T1, T2, T3, T4>(item1, item2, item3, item4);

		internal static int Combine(int h1, int h2) => ((h1 << 5) + h1) ^ h2;

		internal static void Append<T>(StringBuilder builder, T value)
		{
			if (value != null)
				builder.Append(value.ToString());
		}
	}

	public struct ValueTuple<T1> : IEquatable<ValueTuple<T1>>
	{
		public T1 Item1;

		public ValueTuple(T1 item1)
		{
			Item1 = item1;
		}

		public bool Equals(ValueTuple<T1> other) => EqualityComparer<T1>.Default.Equals(Item1, other.Item1);

		public override bool Equals(object obj) => obj is ValueTuple<T1> other && Equals(other);

		public override int GetHashCode() => Item1 == null ? 0 : Item1.GetHashCode();

		public override string ToString()
		{
			StringBuilder builder = new StringBuilder();
			builder.Append('(');
			ValueTuple.Append(builder, Item1);
			builder.Append(')');
			return builder.ToString();
		}
	}

	public struct ValueTuple<T1, T2> : IEquatable<ValueTuple<T1, T2>>
	{
		public T1 Item1;
		public T2 Item2;

		public ValueTuple(T1 item1, T2 item2)
		{
			Item1 = item1;
			Item2 = item2;
		}

		public bool Equals(ValueTuple<T1, T2> other) =>
			EqualityComparer<T1>.Default.Equals(Item1, other.Item1) && EqualityComparer<T2>.Default.Equals(Item2, other.Item2);

		public override bool Equals(object obj) => obj is ValueTuple<T1, T2> other && Equals(other);

		public override int GetHashCode() => ValueTuple.Combine(Item1 == null ? 0 : Item1.GetHashCode(), Item2 == null ? 0 : Item2.GetHashCode());

		public override string ToString()
		{
			StringBuilder builder = new StringBuilder();
			builder.Append('(');
			ValueTuple.Append(builder, Item1);
			builder.Append(", ");
			ValueTuple.Append(builder, Item2);
			builder.Append(')');
			return builder.ToString();
		}
	}

	public struct ValueTuple<T1, T2, T3> : IEquatable<ValueTuple<T1, T2, T3>>
	{
		public T1 Item1;
		public T2 Item2;
		public T3 Item3;

		public ValueTuple(T1 item1, T2 item2, T3 item3)
		{
			Item1 = item1;
			Item2 = item2;
			Item3 = item3;
		}

		public bool Equals(ValueTuple<T1, T2, T3> other) =>
			EqualityComparer<T1>.Default.Equals(Item1, other.Item1) && EqualityComparer<T2>.Default.Equals(Item2, other.Item2) && EqualityComparer<T3>.Default.Equals(Item3, other.Item3);

		public override bool Equals(object obj) => obj is ValueTuple<T1, T2, T3> other && Equals(other);

		public override int GetHashCode() =>
			ValueTuple.Combine(ValueTuple.Combine(Item1 == null ? 0 : Item1.GetHashCode(), Item2 == null ? 0 : Item2.GetHashCode()), Item3 == null ? 0 : Item3.GetHashCode());

		public override string ToString()
		{
			StringBuilder builder = new StringBuilder();
			builder.Append('(');
			ValueTuple.Append(builder, Item1);
			builder.Append(", ");
			ValueTuple.Append(builder, Item2);
			builder.Append(", ");
			ValueTuple.Append(builder, Item3);
			builder.Append(')');
			return builder.ToString();
		}
	}

	public struct ValueTuple<T1, T2, T3, T4> : IEquatable<ValueTuple<T1, T2, T3, T4>>
	{
		public T1 Item1;
		public T2 Item2;
		public T3 Item3;
		public T4 Item4;

		public ValueTuple(T1 item1, T2 item2, T3 item3, T4 item4)
		{
			Item1 = item1;
			Item2 = item2;
			Item3 = item3;
			Item4 = item4;
		}

		public bool Equals(ValueTuple<T1, T2, T3, T4> other) =>
			EqualityComparer<T1>.Default.Equals(Item1, other.Item1) && EqualityComparer<T2>.Default.Equals(Item2, other.Item2) &&
			EqualityComparer<T3>.Default.Equals(Item3, other.Item3) && EqualityComparer<T4>.Default.Equals(Item4, other.Item4);

		public override bool Equals(object obj) => obj is ValueTuple<T1, T2, T3, T4> other && Equals(other);

		public override int GetHashCode() =>
			ValueTuple.Combine(ValueTuple.Combine(ValueTuple.Combine(Item1 == null ? 0 : Item1.GetHashCode(), Item2 == null ? 0 : Item2.GetHashCode()), Item3 == null ? 0 : Item3.GetHashCode()), Item4 == null ? 0 : Item4.GetHashCode());

		public override string ToString()
		{
			StringBuilder builder = new StringBuilder();
			builder.Append('(');
			ValueTuple.Append(builder, Item1);
			builder.Append(", ");
			ValueTuple.Append(builder, Item2);
			builder.Append(", ");
			ValueTuple.Append(builder, Item3);
			builder.Append(", ");
			ValueTuple.Append(builder, Item4);
			builder.Append(')');
			return builder.ToString();
		}
	}

	public struct ValueTuple<T1, T2, T3, T4, T5> : IEquatable<ValueTuple<T1, T2, T3, T4, T5>>
	{
		public T1 Item1;
		public T2 Item2;
		public T3 Item3;
		public T4 Item4;
		public T5 Item5;

		public ValueTuple(T1 item1, T2 item2, T3 item3, T4 item4, T5 item5)
		{
			Item1 = item1;
			Item2 = item2;
			Item3 = item3;
			Item4 = item4;
			Item5 = item5;
		}

		public bool Equals(ValueTuple<T1, T2, T3, T4, T5> other) =>
			EqualityComparer<T1>.Default.Equals(Item1, other.Item1) && EqualityComparer<T2>.Default.Equals(Item2, other.Item2) &&
			EqualityComparer<T3>.Default.Equals(Item3, other.Item3) && EqualityComparer<T4>.Default.Equals(Item4, other.Item4) &&
			EqualityComparer<T5>.Default.Equals(Item5, other.Item5);

		public override bool Equals(object obj) => obj is ValueTuple<T1, T2, T3, T4, T5> other && Equals(other);

		public override int GetHashCode() =>
			ValueTuple.Combine(ValueTuple.Combine(ValueTuple.Combine(ValueTuple.Combine(Item1 == null ? 0 : Item1.GetHashCode(), Item2 == null ? 0 : Item2.GetHashCode()), Item3 == null ? 0 : Item3.GetHashCode()), Item4 == null ? 0 : Item4.GetHashCode()), Item5 == null ? 0 : Item5.GetHashCode());

		public override string ToString()
		{
			StringBuilder builder = new StringBuilder();
			builder.Append('(');
			ValueTuple.Append(builder, Item1);
			builder.Append(", ");
			ValueTuple.Append(builder, Item2);
			builder.Append(", ");
			ValueTuple.Append(builder, Item3);
			builder.Append(", ");
			ValueTuple.Append(builder, Item4);
			builder.Append(", ");
			ValueTuple.Append(builder, Item5);
			builder.Append(')');
			return builder.ToString();
		}
	}

	public struct ValueTuple<T1, T2, T3, T4, T5, T6> : IEquatable<ValueTuple<T1, T2, T3, T4, T5, T6>>
	{
		public T1 Item1;
		public T2 Item2;
		public T3 Item3;
		public T4 Item4;
		public T5 Item5;
		public T6 Item6;

		public ValueTuple(T1 item1, T2 item2, T3 item3, T4 item4, T5 item5, T6 item6)
		{
			Item1 = item1;
			Item2 = item2;
			Item3 = item3;
			Item4 = item4;
			Item5 = item5;
			Item6 = item6;
		}

		public bool Equals(ValueTuple<T1, T2, T3, T4, T5, T6> other) =>
			EqualityComparer<T1>.Default.Equals(Item1, other.Item1) && EqualityComparer<T2>.Default.Equals(Item2, other.Item2) &&
			EqualityComparer<T3>.Default.Equals(Item3, other.Item3) && EqualityComparer<T4>.Default.Equals(Item4, other.Item4) &&
			EqualityComparer<T5>.Default.Equals(Item5, other.Item5) && EqualityComparer<T6>.Default.Equals(Item6, other.Item6);

		public override bool Equals(object obj) => obj is ValueTuple<T1, T2, T3, T4, T5, T6> other && Equals(other);

		public override int GetHashCode() =>
			ValueTuple.Combine(ValueTuple.Combine(ValueTuple.Combine(ValueTuple.Combine(ValueTuple.Combine(Item1 == null ? 0 : Item1.GetHashCode(), Item2 == null ? 0 : Item2.GetHashCode()), Item3 == null ? 0 : Item3.GetHashCode()), Item4 == null ? 0 : Item4.GetHashCode()), Item5 == null ? 0 : Item5.GetHashCode()), Item6 == null ? 0 : Item6.GetHashCode());

		public override string ToString()
		{
			StringBuilder builder = new StringBuilder();
			builder.Append('(');
			ValueTuple.Append(builder, Item1);
			builder.Append(", ");
			ValueTuple.Append(builder, Item2);
			builder.Append(", ");
			ValueTuple.Append(builder, Item3);
			builder.Append(", ");
			ValueTuple.Append(builder, Item4);
			builder.Append(", ");
			ValueTuple.Append(builder, Item5);
			builder.Append(", ");
			ValueTuple.Append(builder, Item6);
			builder.Append(')');
			return builder.ToString();
		}
	}

	public struct ValueTuple<T1, T2, T3, T4, T5, T6, T7> : IEquatable<ValueTuple<T1, T2, T3, T4, T5, T6, T7>>
	{
		public T1 Item1;
		public T2 Item2;
		public T3 Item3;
		public T4 Item4;
		public T5 Item5;
		public T6 Item6;
		public T7 Item7;

		public ValueTuple(T1 item1, T2 item2, T3 item3, T4 item4, T5 item5, T6 item6, T7 item7)
		{
			Item1 = item1;
			Item2 = item2;
			Item3 = item3;
			Item4 = item4;
			Item5 = item5;
			Item6 = item6;
			Item7 = item7;
		}

		public bool Equals(ValueTuple<T1, T2, T3, T4, T5, T6, T7> other) =>
			EqualityComparer<T1>.Default.Equals(Item1, other.Item1) && EqualityComparer<T2>.Default.Equals(Item2, other.Item2) &&
			EqualityComparer<T3>.Default.Equals(Item3, other.Item3) && EqualityComparer<T4>.Default.Equals(Item4, other.Item4) &&
			EqualityComparer<T5>.Default.Equals(Item5, other.Item5) && EqualityComparer<T6>.Default.Equals(Item6, other.Item6) &&
			EqualityComparer<T7>.Default.Equals(Item7, other.Item7);

		public override bool Equals(object obj) => obj is ValueTuple<T1, T2, T3, T4, T5, T6, T7> other && Equals(other);

		public override int GetHashCode() =>
			ValueTuple.Combine(ValueTuple.Combine(ValueTuple.Combine(ValueTuple.Combine(ValueTuple.Combine(ValueTuple.Combine(Item1 == null ? 0 : Item1.GetHashCode(), Item2 == null ? 0 : Item2.GetHashCode()), Item3 == null ? 0 : Item3.GetHashCode()), Item4 == null ? 0 : Item4.GetHashCode()), Item5 == null ? 0 : Item5.GetHashCode()), Item6 == null ? 0 : Item6.GetHashCode()), Item7 == null ? 0 : Item7.GetHashCode());

		public override string ToString()
		{
			StringBuilder builder = new StringBuilder();
			builder.Append('(');
			ValueTuple.Append(builder, Item1);
			builder.Append(", ");
			ValueTuple.Append(builder, Item2);
			builder.Append(", ");
			ValueTuple.Append(builder, Item3);
			builder.Append(", ");
			ValueTuple.Append(builder, Item4);
			builder.Append(", ");
			ValueTuple.Append(builder, Item5);
			builder.Append(", ");
			ValueTuple.Append(builder, Item6);
			builder.Append(", ");
			ValueTuple.Append(builder, Item7);
			builder.Append(')');
			return builder.ToString();
		}
	}

	// Tuples past seven elements nest the rest in TRest, as the compiler lays them out
	public struct ValueTuple<T1, T2, T3, T4, T5, T6, T7, TRest> : IEquatable<ValueTuple<T1, T2, T3, T4, T5, T6, T7, TRest>> where TRest : struct
	{
		public T1 Item1;
		public T2 Item2;
		public T3 Item3;
		public T4 Item4;
		public T5 Item5;
		public T6 Item6;
		public T7 Item7;
		public TRest Rest;

		public ValueTuple(T1 item1, T2 item2, T3 item3, T4 item4, T5 item5, T6 item6, T7 item7, TRest rest)
		{
			Item1 = item1;
			Item2 = item2;
			Item3 = item3;
			Item4 = item4;
			Item5 = item5;
			Item6 = item6;
			Item7 = item7;
			Rest = rest;
		}

		public bool Equals(ValueTuple<T1, T2, T3, T4, T5, T6, T7, TRest> other) =>
			EqualityComparer<T1>.Default.Equals(Item1, other.Item1) && EqualityComparer<T2>.Default.Equals(Item2, other.Item2) &&
			EqualityComparer<T3>.Default.Equals(Item3, other.Item3) && EqualityComparer<T4>.Default.Equals(Item4, other.Item4) &&
			EqualityComparer<T5>.Default.Equals(Item5, other.Item5) && EqualityComparer<T6>.Default.Equals(Item6, other.Item6) &&
			EqualityComparer<T7>.Default.Equals(Item7, other.Item7) && EqualityComparer<TRest>.Default.Equals(Rest, other.Rest);

		public override bool Equals(object obj) => obj is ValueTuple<T1, T2, T3, T4, T5, T6, T7, TRest> other && Equals(other);

		public override int GetHashCode() =>
			ValueTuple.Combine(ValueTuple.Combine(ValueTuple.Combine(ValueTuple.Combine(ValueTuple.Combine(ValueTuple.Combine(ValueTuple.Combine(Item1 == null ? 0 : Item1.GetHashCode(), Item2 == null ? 0 : Item2.GetHashCode()), Item3 == null ? 0 : Item3.GetHashCode()), Item4 == null ? 0 : Item4.GetHashCode()), Item5 == null ? 0 : Item5.GetHashCode()), Item6 == null ? 0 : Item6.GetHashCode()), Item7 == null ? 0 : Item7.GetHashCode()), Rest.GetHashCode());

		public override string ToString()
		{
			StringBuilder builder = new StringBuilder();
			builder.Append('(');
			ValueTuple.Append(builder, Item1);
			builder.Append(", ");
			ValueTuple.Append(builder, Item2);
			builder.Append(", ");
			ValueTuple.Append(builder, Item3);
			builder.Append(", ");
			ValueTuple.Append(builder, Item4);
			builder.Append(", ");
			ValueTuple.Append(builder, Item5);
			builder.Append(", ");
			ValueTuple.Append(builder, Item6);
			builder.Append(", ");
			ValueTuple.Append(builder, Item7);
			builder.Append(", ");
			string rest = Rest.ToString();
			builder.Append(rest.Length > 1 ? rest.Substring(1, rest.Length - 2) : rest);
			builder.Append(')');
			return builder.ToString();
		}
	}
}

namespace System.Runtime.CompilerServices
{
	[AttributeUsage(AttributeTargets.Field | AttributeTargets.Parameter | AttributeTargets.Property | AttributeTargets.ReturnValue | AttributeTargets.Class | AttributeTargets.Struct | AttributeTargets.Event)]
	public sealed class TupleElementNamesAttribute : Attribute
	{
		public TupleElementNamesAttribute(string[] transformNames)
		{
			TransformNames = transformNames;
		}

		public string[] TransformNames { get; }
	}
}
