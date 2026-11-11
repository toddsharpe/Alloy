using System.Runtime;
using System.Runtime.CompilerServices;

namespace System.Collections.Generic
{
	// A growable array; an outgrown buffer goes back to the heap, the list itself is the caller's to free
	public class List<T> : IList<T>, IReadOnlyList<T>
	{
		private const int DefaultCapacity = 4;

		private T[] _items;
		private int _size;
		private int _version;

		public List()
		{
			_items = Array.Empty<T>();
		}

		public List(int capacity)
		{
			if (capacity < 0)
				ThrowHelper.ThrowArgumentOutOfRange();
			_items = capacity == 0 ? Array.Empty<T>() : new T[capacity];
		}

		public List(ReadOnlySpan<T> items)
		{
			_items = items.Length == 0 ? Array.Empty<T>() : items.ToArray();
			_size = items.Length;
		}

		public int Count => _size;

		public int Capacity
		{
			get => _items.Length;
			set
			{
				if (value < _size)
					ThrowHelper.ThrowArgumentOutOfRange();
				if (value == _items.Length)
					return;
				T[] items = value == 0 ? Array.Empty<T>() : new T[value];
				new ReadOnlySpan<T>(_items, 0, _size).CopyTo(items);
				if (_items.Length != 0)
					Allocator.FreeObject(_items);
				_items = items;
			}
		}

		bool ICollection<T>.IsReadOnly => false;

		public T this[int index]
		{
			get
			{
				if ((uint)index >= (uint)_size)
					ThrowHelper.ThrowArgumentOutOfRange();
				return _items[index];
			}
			set
			{
				if ((uint)index >= (uint)_size)
					ThrowHelper.ThrowArgumentOutOfRange();
				_items[index] = value;
				_version++;
			}
		}

		public void Add(T item)
		{
			if (_size == _items.Length)
				Grow(_size + 1);
			_items[_size++] = item;
			_version++;
		}

		public void AddRange(ReadOnlySpan<T> items)
		{
			if (items.Length == 0)
				return;
			if (_items.Length - _size < items.Length)
				Grow(_size + items.Length);
			items.CopyTo(new Span<T>(_items, _size, items.Length));
			_size += items.Length;
			_version++;
		}

		public void Insert(int index, T item)
		{
			if ((uint)index > (uint)_size)
				ThrowHelper.ThrowArgumentOutOfRange();
			if (_size == _items.Length)
				Grow(_size + 1);
			if (index < _size)
				new ReadOnlySpan<T>(_items, index, _size - index).CopyTo(new Span<T>(_items, index + 1, _size - index));
			_items[index] = item;
			_size++;
			_version++;
		}

		public void RemoveAt(int index)
		{
			if ((uint)index >= (uint)_size)
				ThrowHelper.ThrowArgumentOutOfRange();
			_size--;
			if (index < _size)
				new ReadOnlySpan<T>(_items, index + 1, _size - index).CopyTo(new Span<T>(_items, index, _size - index));
			_items[_size] = default;
			_version++;
		}

		public bool Remove(T item)
		{
			int index = IndexOf(item);
			if (index < 0)
				return false;
			RemoveAt(index);
			return true;
		}

		public void RemoveRange(int index, int count)
		{
			if (index < 0 || count < 0 || _size - index < count)
				ThrowHelper.ThrowArgumentOutOfRange();
			if (count == 0)
				return;
			int tail = _size - index - count;
			if (tail > 0)
				new ReadOnlySpan<T>(_items, index + count, tail).CopyTo(new Span<T>(_items, index, tail));
			_size -= count;
			new Span<T>(_items, _size, count).Clear();
			_version++;
		}

		public void Clear()
		{
			new Span<T>(_items, 0, _size).Clear();
			_size = 0;
			_version++;
		}

		public bool Contains(T item) => IndexOf(item) >= 0;

		public int IndexOf(T item)
		{
			EqualityComparer<T> comparer = EqualityComparer<T>.Default;
			for (int i = 0; i < _size; i++)
			{
				if (comparer.Equals(_items[i], item))
					return i;
			}
			return -1;
		}

		public void CopyTo(T[] array, int arrayIndex) => new ReadOnlySpan<T>(_items, 0, _size).CopyTo(new Span<T>(array).Slice(arrayIndex));

		public T[] ToArray() => new ReadOnlySpan<T>(_items, 0, _size).ToArray();

		// The live elements; adding to the list may move them, so the span is for a loop, not for keeping
		public Span<T> AsSpan() => new Span<T>(_items, 0, _size);

		public void Reverse() => AsSpan().Reverse();

		public void Sort() => Sort(Comparer<T>.Default);

		public void Sort(IComparer<T> comparer) => Sorting.HeapSort(AsSpan(), comparer);

		public void Sort(Comparison<T> comparison) => Sorting.HeapSort(AsSpan(), comparison);

		public T Find(Predicate<T> match)
		{
			for (int i = 0; i < _size; i++)
			{
				if (match(_items[i]))
					return _items[i];
			}
			return default;
		}

		public int FindIndex(Predicate<T> match)
		{
			for (int i = 0; i < _size; i++)
			{
				if (match(_items[i]))
					return i;
			}
			return -1;
		}

		public bool Exists(Predicate<T> match) => FindIndex(match) >= 0;

		public int RemoveAll(Predicate<T> match)
		{
			int kept = 0;
			for (int i = 0; i < _size; i++)
			{
				if (!match(_items[i]))
					_items[kept++] = _items[i];
			}
			int removed = _size - kept;
			new Span<T>(_items, kept, removed).Clear();
			_size = kept;
			_version++;
			return removed;
		}

		private void Grow(int needed)
		{
			int capacity = _items.Length == 0 ? DefaultCapacity : 2 * _items.Length;
			if (capacity < needed)
				capacity = needed;
			Capacity = capacity;
		}

		public Enumerator GetEnumerator() => new Enumerator(this);

		IEnumerator<T> IEnumerable<T>.GetEnumerator() => new ObjectEnumerator(this);

		IEnumerator IEnumerable.GetEnumerator() => new ObjectEnumerator(this);

		public struct Enumerator : IEnumerator<T>
		{
			private readonly List<T> _list;
			private readonly int _version;
			private int _index;

			internal Enumerator(List<T> list)
			{
				_list = list;
				_version = list._version;
				_index = -1;
			}

			public T Current => _list._items[_index];

			object IEnumerator.Current => Current;

			public bool MoveNext()
			{
				if (_version != _list._version)
					ThrowHelper.FailFast("FAILFAST: a List<T> changed while being enumerated");
				if (_index + 1 >= _list._size)
					return false;
				_index++;
				return true;
			}

			public void Reset() => _index = -1;

			public void Dispose()
			{
			}
		}

		// Handed out through IEnumerable<T>; Dispose, which foreach always calls, gives it back to the heap
		private sealed class ObjectEnumerator : IEnumerator<T>
		{
			private Enumerator _enumerator;

			public ObjectEnumerator(List<T> list)
			{
				_enumerator = new Enumerator(list);
			}

			public T Current => _enumerator.Current;

			object IEnumerator.Current => _enumerator.Current;

			public bool MoveNext() => _enumerator.MoveNext();

			public void Reset() => _enumerator.Reset();

			public void Dispose() => Allocator.FreeObject(this);
		}
	}

	// In-place heap sort: no recursion and no allocation, which suits a kernel better than introsort
	internal static class Sorting
	{
		public static void HeapSort<T>(Span<T> items, IComparer<T> comparer)
		{
			for (int i = items.Length / 2 - 1; i >= 0; i--)
				SiftDown(items, i, items.Length, comparer);
			for (int end = items.Length - 1; end > 0; end--)
			{
				(items[0], items[end]) = (items[end], items[0]);
				SiftDown(items, 0, end, comparer);
			}
		}

		public static void HeapSort<T>(Span<T> items, Comparison<T> comparison) => HeapSort(items, new ComparisonComparer<T>(comparison));

		private static void SiftDown<T>(Span<T> items, int root, int count, IComparer<T> comparer)
		{
			while (true)
			{
				int child = 2 * root + 1;
				if (child >= count)
					return;
				if (child + 1 < count && comparer.Compare(items[child], items[child + 1]) < 0)
					child++;
				if (comparer.Compare(items[root], items[child]) >= 0)
					return;
				(items[root], items[child]) = (items[child], items[root]);
				root = child;
			}
		}

		private sealed class ComparisonComparer<T> : IComparer<T>
		{
			private readonly Comparison<T> _comparison;

			public ComparisonComparer(Comparison<T> comparison)
			{
				_comparison = comparison;
			}

			public int Compare(T x, T y) => _comparison(x, y);
		}
	}
}
