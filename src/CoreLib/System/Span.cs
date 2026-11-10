using System.Runtime.CompilerServices;
using System.Runtime.InteropServices;

namespace System
{
	public readonly ref struct Span<T>
	{
		internal readonly ref T _reference;
		private readonly int _length;

		public Span(T[] array)
		{
			if (array == null)
			{
				this = default;
				return;
			}
			_reference = ref MemoryMarshal.GetArrayDataReference(array);
			_length = array.Length;
		}

		public Span(T[] array, int start, int length)
		{
			if (array == null)
			{
				if (start != 0 || length != 0)
					ThrowHelper.ThrowArgumentOutOfRange();
				this = default;
				return;
			}
			if ((ulong)(uint)start + (ulong)(uint)length > (ulong)(uint)array.Length)
				ThrowHelper.ThrowArgumentOutOfRange();
			_reference = ref Unsafe.Add(ref MemoryMarshal.GetArrayDataReference(array), (nint)(uint)start);
			_length = length;
		}

		public unsafe Span(void* pointer, int length)
		{
			if (length < 0)
				ThrowHelper.ThrowArgumentOutOfRange();
			_reference = ref Unsafe.AsRef<T>(pointer);
			_length = length;
		}

		public Span(ref T reference)
		{
			_reference = ref reference;
			_length = 1;
		}

		internal Span(ref T reference, int length)
		{
			_reference = ref reference;
			_length = length;
		}

		public ref T this[int index]
		{
			[Intrinsic]
			get
			{
				if ((uint)index >= (uint)_length)
					ThrowHelper.ThrowIndexOutOfRange();
				return ref Unsafe.Add(ref _reference, (nint)(uint)index);
			}
		}

		public int Length => _length;

		public bool IsEmpty => _length == 0;

		public static Span<T> Empty => default;

		public ref T GetPinnableReference()
		{
			if (_length == 0)
				return ref Unsafe.NullRef<T>();
			return ref _reference;
		}

		public Span<T> Slice(int start)
		{
			if ((uint)start > (uint)_length)
				ThrowHelper.ThrowArgumentOutOfRange();
			return new Span<T>(ref Unsafe.Add(ref _reference, (nint)(uint)start), _length - start);
		}

		public Span<T> Slice(int start, int length)
		{
			if ((ulong)(uint)start + (ulong)(uint)length > (ulong)(uint)_length)
				ThrowHelper.ThrowArgumentOutOfRange();
			return new Span<T>(ref Unsafe.Add(ref _reference, (nint)(uint)start), length);
		}

		public void Clear()
		{
			if (RuntimeHelpers.IsReferenceOrContainsReferences<T>())
			{
				for (int i = 0; i < _length; i++)
					Unsafe.Add(ref _reference, i) = default;
			}
			else
			{
				SpanHelpers.ClearWithoutReferences(ref Unsafe.As<T, byte>(ref _reference), (nuint)(uint)_length * (nuint)Unsafe.SizeOf<T>());
			}
		}

		public void Fill(T value)
		{
			for (int i = 0; i < _length; i++)
				Unsafe.Add(ref _reference, i) = value;
		}

		public void CopyTo(Span<T> destination)
		{
			if (!TryCopyTo(destination))
				ThrowHelper.ThrowArgument("Destination too short");
		}

		public bool TryCopyTo(Span<T> destination)
		{
			if ((uint)_length > (uint)destination.Length)
				return false;
			((ReadOnlySpan<T>)this).CopyTo(destination);
			return true;
		}

		public T[] ToArray()
		{
			T[] array = new T[_length];
			CopyTo(array);
			return array;
		}

		public Enumerator GetEnumerator() => new Enumerator(this);

		public static implicit operator Span<T>(T[] array) => new Span<T>(array);

		public static implicit operator ReadOnlySpan<T>(Span<T> span) => new ReadOnlySpan<T>(ref span._reference, span._length);

		public ref struct Enumerator
		{
			private readonly Span<T> _span;
			private int _index;

			internal Enumerator(Span<T> span)
			{
				_span = span;
				_index = -1;
			}

			public bool MoveNext()
			{
				int index = _index + 1;
				if (index < _span.Length)
				{
					_index = index;
					return true;
				}
				return false;
			}

			public ref T Current => ref _span[_index];
		}
	}

	public readonly ref struct ReadOnlySpan<T>
	{
		internal readonly ref T _reference;
		private readonly int _length;

		public ReadOnlySpan(T[] array)
		{
			if (array == null)
			{
				this = default;
				return;
			}
			_reference = ref MemoryMarshal.GetArrayDataReference(array);
			_length = array.Length;
		}

		public ReadOnlySpan(T[] array, int start, int length)
		{
			if (array == null)
			{
				if (start != 0 || length != 0)
					ThrowHelper.ThrowArgumentOutOfRange();
				this = default;
				return;
			}
			if ((ulong)(uint)start + (ulong)(uint)length > (ulong)(uint)array.Length)
				ThrowHelper.ThrowArgumentOutOfRange();
			_reference = ref Unsafe.Add(ref MemoryMarshal.GetArrayDataReference(array), (nint)(uint)start);
			_length = length;
		}

		public unsafe ReadOnlySpan(void* pointer, int length)
		{
			if (length < 0)
				ThrowHelper.ThrowArgumentOutOfRange();
			_reference = ref Unsafe.AsRef<T>(pointer);
			_length = length;
		}

		public ReadOnlySpan(ref readonly T reference)
		{
			_reference = ref Unsafe.AsRef(in reference);
			_length = 1;
		}

		internal ReadOnlySpan(ref T reference, int length)
		{
			_reference = ref reference;
			_length = length;
		}

		public ref readonly T this[int index]
		{
			[Intrinsic]
			get
			{
				if ((uint)index >= (uint)_length)
					ThrowHelper.ThrowIndexOutOfRange();
				return ref Unsafe.Add(ref _reference, (nint)(uint)index);
			}
		}

		public int Length => _length;

		public bool IsEmpty => _length == 0;

		public static ReadOnlySpan<T> Empty => default;

		public ref readonly T GetPinnableReference()
		{
			if (_length == 0)
				return ref Unsafe.NullRef<T>();
			return ref _reference;
		}

		public ReadOnlySpan<T> Slice(int start)
		{
			if ((uint)start > (uint)_length)
				ThrowHelper.ThrowArgumentOutOfRange();
			return new ReadOnlySpan<T>(ref Unsafe.Add(ref _reference, (nint)(uint)start), _length - start);
		}

		public ReadOnlySpan<T> Slice(int start, int length)
		{
			if ((ulong)(uint)start + (ulong)(uint)length > (ulong)(uint)_length)
				ThrowHelper.ThrowArgumentOutOfRange();
			return new ReadOnlySpan<T>(ref Unsafe.Add(ref _reference, (nint)(uint)start), length);
		}

		public void CopyTo(Span<T> destination)
		{
			if (!TryCopyTo(destination))
				ThrowHelper.ThrowArgument("Destination too short");
		}

		public bool TryCopyTo(Span<T> destination)
		{
			if ((uint)_length > (uint)destination.Length)
				return false;

			if (RuntimeHelpers.IsReferenceOrContainsReferences<T>())
			{
				// Element by element so every store goes through the write barrier, overlap-safe in both directions
				if (Unsafe.IsAddressLessThan(ref destination._reference, ref _reference))
				{
					for (int i = 0; i < _length; i++)
						Unsafe.Add(ref destination._reference, i) = Unsafe.Add(ref _reference, i);
				}
				else
				{
					for (int i = _length - 1; i >= 0; i--)
						Unsafe.Add(ref destination._reference, i) = Unsafe.Add(ref _reference, i);
				}
			}
			else
			{
				SpanHelpers.Memmove(ref Unsafe.As<T, byte>(ref destination._reference), ref Unsafe.As<T, byte>(ref _reference), (nuint)(uint)_length * (nuint)Unsafe.SizeOf<T>());
			}
			return true;
		}

		public T[] ToArray()
		{
			T[] array = new T[_length];
			CopyTo(array);
			return array;
		}

		public Enumerator GetEnumerator() => new Enumerator(this);

		public static implicit operator ReadOnlySpan<T>(T[] array) => new ReadOnlySpan<T>(array);

		public ref struct Enumerator
		{
			private readonly ReadOnlySpan<T> _span;
			private int _index;

			internal Enumerator(ReadOnlySpan<T> span)
			{
				_span = span;
				_index = -1;
			}

			public bool MoveNext()
			{
				int index = _index + 1;
				if (index < _span.Length)
				{
					_index = index;
					return true;
				}
				return false;
			}

			public ref readonly T Current => ref _span[_index];
		}
	}
}
