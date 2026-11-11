namespace System
{
	// A position from the start or the end: ^1 is the last element
	public readonly struct Index : IEquatable<Index>
	{
		private readonly int _value;

		public Index(int value, bool fromEnd = false)
		{
			if (value < 0)
				ThrowHelper.ThrowArgumentOutOfRange();
			_value = fromEnd ? ~value : value;
		}

		private Index(int value)
		{
			_value = value;
		}

		public static Index Start => new Index(0);

		public static Index End => new Index(~0);

		public static Index FromStart(int value)
		{
			if (value < 0)
				ThrowHelper.ThrowArgumentOutOfRange();
			return new Index(value);
		}

		public static Index FromEnd(int value)
		{
			if (value < 0)
				ThrowHelper.ThrowArgumentOutOfRange();
			return new Index(~value);
		}

		public int Value => _value < 0 ? ~_value : _value;

		public bool IsFromEnd => _value < 0;

		public int GetOffset(int length) => _value < 0 ? length + _value + 1 : _value;

		public override bool Equals(object value) => value is Index index && _value == index._value;

		public bool Equals(Index other) => _value == other._value;

		public override int GetHashCode() => _value;

		public static implicit operator Index(int value) => FromStart(value);

		public override string ToString() => IsFromEnd ? string.Concat("^", Value.ToString()) : Value.ToString();
	}

	public readonly struct Range : IEquatable<Range>
	{
		public Index Start { get; }

		public Index End { get; }

		public Range(Index start, Index end)
		{
			Start = start;
			End = end;
		}

		public override bool Equals(object value) => value is Range range && range.Start.Equals(Start) && range.End.Equals(End);

		public bool Equals(Range other) => other.Start.Equals(Start) && other.End.Equals(End);

		public override int GetHashCode() => Start.GetHashCode() * 31 + End.GetHashCode();

		public override string ToString() => string.Concat(Start.ToString(), "..", End.ToString());

		public static Range StartAt(Index start) => new Range(start, Index.End);

		public static Range EndAt(Index end) => new Range(Index.Start, end);

		public static Range All => new Range(Index.Start, Index.End);

		public (int Offset, int Length) GetOffsetAndLength(int length)
		{
			int start = Start.GetOffset(length);
			int end = End.GetOffset(length);
			if ((uint)end > (uint)length || (uint)start > (uint)end)
				ThrowHelper.ThrowArgumentOutOfRange();
			return (start, end - start);
		}
	}
}
