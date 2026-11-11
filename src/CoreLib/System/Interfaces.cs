namespace System
{
	// The BCL's variance annotations (IComparable<in T>) are left off: the runtime resolves interfaces by exact match only
	public interface IDisposable
	{
		void Dispose();
	}

	public interface IEquatable<T>
	{
		bool Equals(T other);
	}

	public interface IComparable
	{
		int CompareTo(object obj);
	}

	public interface IComparable<T>
	{
		int CompareTo(T other);
	}

	public interface IFormatProvider
	{
		object GetFormat(Type formatType);
	}

	public interface IFormattable
	{
		string ToString(string format, IFormatProvider formatProvider);
	}

	public interface ISpanFormattable : IFormattable
	{
		bool TryFormat(Span<char> destination, out int charsWritten, ReadOnlySpan<char> format, IFormatProvider provider);
	}

	public interface ICloneable
	{
		object Clone();
	}

	public class EventArgs
	{
		public static readonly EventArgs Empty = new EventArgs();
	}
}

namespace System.Collections
{
	public interface IEqualityComparer
	{
		bool Equals(object x, object y);

		int GetHashCode(object obj);
	}

	public interface IComparer
	{
		int Compare(object x, object y);
	}

	public interface ICollection : IEnumerable
	{
		int Count { get; }
	}
}

namespace System.Collections.Generic
{
	public interface IEnumerator<T> : IDisposable, IEnumerator
	{
		new T Current { get; }
	}

	public interface IEnumerable<T> : IEnumerable
	{
		new IEnumerator<T> GetEnumerator();
	}

	public interface IReadOnlyCollection<T> : IEnumerable<T>
	{
		int Count { get; }
	}

	public interface IReadOnlyList<T> : IReadOnlyCollection<T>
	{
		T this[int index] { get; }
	}

	public interface ICollection<T> : IEnumerable<T>
	{
		int Count { get; }

		bool IsReadOnly { get; }

		void Add(T item);

		void Clear();

		bool Contains(T item);

		void CopyTo(T[] array, int arrayIndex);

		bool Remove(T item);
	}

	public interface IList<T> : ICollection<T>
	{
		T this[int index] { get; set; }

		int IndexOf(T item);

		void Insert(int index, T item);

		void RemoveAt(int index);
	}

	public interface IEqualityComparer<T>
	{
		bool Equals(T x, T y);

		int GetHashCode(T obj);
	}

	public interface IComparer<T>
	{
		int Compare(T x, T y);
	}

	public readonly struct KeyValuePair<TKey, TValue>
	{
		public KeyValuePair(TKey key, TValue value)
		{
			Key = key;
			Value = value;
		}

		public TKey Key { get; }

		public TValue Value { get; }

		public void Deconstruct(out TKey key, out TValue value)
		{
			key = Key;
			value = Value;
		}
	}
}
