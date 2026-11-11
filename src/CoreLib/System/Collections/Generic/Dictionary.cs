using System.Runtime;

namespace System.Collections.Generic
{
	// Chained hashing over two arrays (the BCL's shape); outgrown arrays go back to the heap
	public class Dictionary<TKey, TValue> : IEnumerable<KeyValuePair<TKey, TValue>>, IReadOnlyCollection<KeyValuePair<TKey, TValue>>
	{
		private struct Entry
		{
			public int HashCode;
			public int Next;
			public TKey Key;
			public TValue Value;
		}

		private int[] _buckets;
		private Entry[] _entries;
		private int _count;
		private int _freeList;
		private int _freeCount;
		private int _version;
		private readonly IEqualityComparer<TKey> _comparer;

		public Dictionary() : this(0, null)
		{
		}

		public Dictionary(int capacity) : this(capacity, null)
		{
		}

		public Dictionary(IEqualityComparer<TKey> comparer) : this(0, comparer)
		{
		}

		public Dictionary(int capacity, IEqualityComparer<TKey> comparer)
		{
			if (capacity < 0)
				ThrowHelper.ThrowArgumentOutOfRange();
			_comparer = comparer ?? EqualityComparer<TKey>.Default;
			_freeList = -1;
			if (capacity > 0)
				Initialize(capacity);
		}

		public int Count => _count - _freeCount;

		public IEqualityComparer<TKey> Comparer => _comparer;

		public TValue this[TKey key]
		{
			get
			{
				int index = FindEntry(key);
				if (index < 0)
					ThrowHelper.FailFast("FAILFAST: the key is not in the dictionary");
				return _entries[index].Value;
			}
			set => TryInsert(key, value, true);
		}

		public void Add(TKey key, TValue value)
		{
			if (!TryInsert(key, value, false))
				ThrowHelper.ThrowArgument("the key is already in the dictionary");
		}

		public bool TryAdd(TKey key, TValue value) => TryInsert(key, value, false);

		public bool ContainsKey(TKey key) => FindEntry(key) >= 0;

		public bool TryGetValue(TKey key, out TValue value)
		{
			int index = FindEntry(key);
			if (index < 0)
			{
				value = default;
				return false;
			}
			value = _entries[index].Value;
			return true;
		}

		public bool Remove(TKey key) => Remove(key, out _);

		public bool Remove(TKey key, out TValue value)
		{
			value = default;
			if (_buckets == null)
				return false;
			int hashCode = Hash(key);
			ref int bucket = ref _buckets[hashCode & (_buckets.Length - 1)];
			int last = -1;
			for (int i = bucket - 1; i >= 0;)
			{
				ref Entry entry = ref _entries[i];
				if (entry.HashCode == hashCode && _comparer.Equals(entry.Key, key))
				{
					if (last < 0)
						bucket = entry.Next + 1;
					else
						_entries[last].Next = entry.Next;
					value = entry.Value;
					entry.HashCode = -1;
					entry.Next = _freeList;
					entry.Key = default;
					entry.Value = default;
					_freeList = i;
					_freeCount++;
					_version++;
					return true;
				}
				last = i;
				i = entry.Next;
			}
			return false;
		}

		public void Clear()
		{
			if (_count == 0)
				return;
			new Span<int>(_buckets).Clear();
			new Span<Entry>(_entries, 0, _count).Clear();
			_count = 0;
			_freeList = -1;
			_freeCount = 0;
			_version++;
		}

		public KeyCollection Keys => new KeyCollection(this);

		public ValueCollection Values => new ValueCollection(this);

		private int Hash(TKey key)
		{
			if (key == null)
				ThrowHelper.ThrowArgument("a null key");
			return _comparer.GetHashCode(key) & 0x7FFFFFFF;
		}

		private int FindEntry(TKey key)
		{
			if (_buckets == null)
			{
				Hash(key);
				return -1;
			}
			int hashCode = Hash(key);
			for (int i = _buckets[hashCode & (_buckets.Length - 1)] - 1; i >= 0; i = _entries[i].Next)
			{
				if (_entries[i].HashCode == hashCode && _comparer.Equals(_entries[i].Key, key))
					return i;
			}
			return -1;
		}

		private bool TryInsert(TKey key, TValue value, bool overwrite)
		{
			if (_buckets == null)
				Initialize(0);
			int hashCode = Hash(key);
			ref int bucket = ref _buckets[hashCode & (_buckets.Length - 1)];
			for (int i = bucket - 1; i >= 0; i = _entries[i].Next)
			{
				if (_entries[i].HashCode == hashCode && _comparer.Equals(_entries[i].Key, key))
				{
					if (!overwrite)
						return false;
					_entries[i].Value = value;
					_version++;
					return true;
				}
			}

			int index;
			if (_freeCount > 0)
			{
				index = _freeList;
				_freeList = _entries[index].Next;
				_freeCount--;
			}
			else
			{
				if (_count == _entries.Length)
				{
					Resize(2 * _entries.Length);
					bucket = ref _buckets[hashCode & (_buckets.Length - 1)];
				}
				index = _count++;
			}

			ref Entry entry = ref _entries[index];
			entry.HashCode = hashCode;
			entry.Next = bucket - 1;
			entry.Key = key;
			entry.Value = value;
			bucket = index + 1;
			_version++;
			return true;
		}

		private void Initialize(int capacity)
		{
			int size = 4;
			while (size < capacity)
				size *= 2;
			_buckets = new int[size];
			_entries = new Entry[size];
		}

		private void Resize(int size)
		{
			Entry[] entries = new Entry[size];
			new ReadOnlySpan<Entry>(_entries, 0, _count).CopyTo(entries);
			int[] buckets = new int[size];
			for (int i = 0; i < _count; i++)
			{
				if (entries[i].HashCode >= 0)
				{
					ref int bucket = ref buckets[entries[i].HashCode & (size - 1)];
					entries[i].Next = bucket - 1;
					bucket = i + 1;
				}
			}
			Allocator.FreeObject(_buckets);
			Allocator.FreeObject(_entries);
			_buckets = buckets;
			_entries = entries;
		}

		public Enumerator GetEnumerator() => new Enumerator(this);

		IEnumerator<KeyValuePair<TKey, TValue>> IEnumerable<KeyValuePair<TKey, TValue>>.GetEnumerator() => new ObjectEnumerator(this);

		IEnumerator IEnumerable.GetEnumerator() => new ObjectEnumerator(this);

		// Walks the entry array in insertion order, skipping freed slots
		private bool MoveNext(ref int index, int version)
		{
			if (version != _version)
				ThrowHelper.FailFast("FAILFAST: a Dictionary changed while being enumerated");
			while (++index < _count)
			{
				if (_entries[index].HashCode >= 0)
					return true;
			}
			return false;
		}

		public struct Enumerator : IEnumerator<KeyValuePair<TKey, TValue>>
		{
			private readonly Dictionary<TKey, TValue> _dictionary;
			private readonly int _version;
			private int _index;

			internal Enumerator(Dictionary<TKey, TValue> dictionary)
			{
				_dictionary = dictionary;
				_version = dictionary._version;
				_index = -1;
			}

			public KeyValuePair<TKey, TValue> Current => new KeyValuePair<TKey, TValue>(_dictionary._entries[_index].Key, _dictionary._entries[_index].Value);

			object IEnumerator.Current => Current;

			public bool MoveNext() => _dictionary.MoveNext(ref _index, _version);

			public void Reset() => _index = -1;

			public void Dispose()
			{
			}
		}

		private sealed class ObjectEnumerator : IEnumerator<KeyValuePair<TKey, TValue>>
		{
			private Enumerator _enumerator;

			public ObjectEnumerator(Dictionary<TKey, TValue> dictionary)
			{
				_enumerator = new Enumerator(dictionary);
			}

			public KeyValuePair<TKey, TValue> Current => _enumerator.Current;

			object IEnumerator.Current => _enumerator.Current;

			public bool MoveNext() => _enumerator.MoveNext();

			public void Reset() => _enumerator.Reset();

			public void Dispose() => Allocator.FreeObject(this);
		}

		public readonly struct KeyCollection : IEnumerable<TKey>, IReadOnlyCollection<TKey>
		{
			private readonly Dictionary<TKey, TValue> _dictionary;

			internal KeyCollection(Dictionary<TKey, TValue> dictionary)
			{
				_dictionary = dictionary;
			}

			public int Count => _dictionary.Count;

			public bool Contains(TKey key) => _dictionary.ContainsKey(key);

			public Enumerator GetEnumerator() => new Enumerator(_dictionary);

			IEnumerator<TKey> IEnumerable<TKey>.GetEnumerator() => new ObjectEnumerator(_dictionary);

			IEnumerator IEnumerable.GetEnumerator() => new ObjectEnumerator(_dictionary);

			public struct Enumerator : IEnumerator<TKey>
			{
				private readonly Dictionary<TKey, TValue> _dictionary;
				private readonly int _version;
				private int _index;

				internal Enumerator(Dictionary<TKey, TValue> dictionary)
				{
					_dictionary = dictionary;
					_version = dictionary._version;
					_index = -1;
				}

				public TKey Current => _dictionary._entries[_index].Key;

				object IEnumerator.Current => Current;

				public bool MoveNext() => _dictionary.MoveNext(ref _index, _version);

				public void Reset() => _index = -1;

				public void Dispose()
				{
				}
			}

			private sealed class ObjectEnumerator : IEnumerator<TKey>
			{
				private Enumerator _enumerator;

				public ObjectEnumerator(Dictionary<TKey, TValue> dictionary)
				{
					_enumerator = new Enumerator(dictionary);
				}

				public TKey Current => _enumerator.Current;

				object IEnumerator.Current => _enumerator.Current;

				public bool MoveNext() => _enumerator.MoveNext();

				public void Reset() => _enumerator.Reset();

				public void Dispose() => Allocator.FreeObject(this);
			}
		}

		public readonly struct ValueCollection : IEnumerable<TValue>, IReadOnlyCollection<TValue>
		{
			private readonly Dictionary<TKey, TValue> _dictionary;

			internal ValueCollection(Dictionary<TKey, TValue> dictionary)
			{
				_dictionary = dictionary;
			}

			public int Count => _dictionary.Count;

			public Enumerator GetEnumerator() => new Enumerator(_dictionary);

			IEnumerator<TValue> IEnumerable<TValue>.GetEnumerator() => new ObjectEnumerator(_dictionary);

			IEnumerator IEnumerable.GetEnumerator() => new ObjectEnumerator(_dictionary);

			public struct Enumerator : IEnumerator<TValue>
			{
				private readonly Dictionary<TKey, TValue> _dictionary;
				private readonly int _version;
				private int _index;

				internal Enumerator(Dictionary<TKey, TValue> dictionary)
				{
					_dictionary = dictionary;
					_version = dictionary._version;
					_index = -1;
				}

				public TValue Current => _dictionary._entries[_index].Value;

				object IEnumerator.Current => Current;

				public bool MoveNext() => _dictionary.MoveNext(ref _index, _version);

				public void Reset() => _index = -1;

				public void Dispose()
				{
				}
			}

			private sealed class ObjectEnumerator : IEnumerator<TValue>
			{
				private Enumerator _enumerator;

				public ObjectEnumerator(Dictionary<TKey, TValue> dictionary)
				{
					_enumerator = new Enumerator(dictionary);
				}

				public TValue Current => _enumerator.Current;

				object IEnumerator.Current => _enumerator.Current;

				public bool MoveNext() => _enumerator.MoveNext();

				public void Reset() => _enumerator.Reset();

				public void Dispose() => Allocator.FreeObject(this);
			}
		}
	}
}
