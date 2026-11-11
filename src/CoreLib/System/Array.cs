using System.Collections;
using System.Collections.Generic;
using System.Runtime;
using System.Runtime.CompilerServices;
using System.Runtime.InteropServices;
using Internal.Runtime;

namespace System
{
	// Contract with the compiler: the element count is the first field, padded to pointer size, then the elements
	public abstract class Array : ICollection
	{
		private int _numComponents;

		public int Length => (int)Unsafe.As<RawArrayData>(this).Length;

		public long LongLength => Length;

		public int Rank => 1;

		int ICollection.Count => Length;

		public int GetLength(int dimension)
		{
			if (dimension != 0)
				ThrowHelper.ThrowArgumentOutOfRange();
			return Length;
		}

		internal unsafe ref byte ElementsReference => ref Unsafe.As<RawArrayData>(this).Data;

		internal unsafe int ElementSize => GetMethodTable()->ComponentSize;

		internal unsafe MethodTable* ElementType => GetMethodTable()->RelatedParameterType;

		// A boxed element; value types are copied into a new box
		public unsafe object GetValue(int index)
		{
			if ((uint)index >= (uint)Length)
				ThrowHelper.ThrowIndexOutOfRange();
			ref byte element = ref Unsafe.AddByteOffset(ref ElementsReference, (nint)index * ElementSize);
			MethodTable* elementType = ElementType;
			if (!elementType->IsValueType)
				return Unsafe.As<byte, object>(ref element);
			return RuntimeExports.RhBox(elementType, ref element);
		}

		public IEnumerator GetEnumerator() => new ObjectEnumerator(this);

		public static T[] Empty<T>() => EmptyArray<T>.Value;

		public static void Clear(Array array)
		{
			if (array == null)
				ThrowHelper.ThrowArgument("a null array");
			SpanHelpers.ClearWithoutReferences(ref array.ElementsReference, (nuint)array.Length * (nuint)array.ElementSize);
		}

		public static void Clear(Array array, int index, int length)
		{
			if (array == null)
				ThrowHelper.ThrowArgument("a null array");
			if (index < 0 || length < 0 || index > array.Length - length)
				ThrowHelper.ThrowArgumentOutOfRange();
			int size = array.ElementSize;
			SpanHelpers.ClearWithoutReferences(ref Unsafe.AddByteOffset(ref array.ElementsReference, (nint)index * size), (nuint)length * (nuint)size);
		}

		public static void Copy(Array sourceArray, Array destinationArray, int length) => Copy(sourceArray, 0, destinationArray, 0, length);

		// Arrays of one element type only; there is no GC, so the bytes move as they are
		public static unsafe void Copy(Array sourceArray, int sourceIndex, Array destinationArray, int destinationIndex, int length)
		{
			if (sourceArray == null || destinationArray == null)
				ThrowHelper.ThrowArgument("a null array");
			if (sourceArray.ElementType != destinationArray.ElementType)
				ThrowHelper.ThrowArrayTypeMismatch();
			if (sourceIndex < 0 || destinationIndex < 0 || length < 0 || sourceIndex > sourceArray.Length - length || destinationIndex > destinationArray.Length - length)
				ThrowHelper.ThrowArgumentOutOfRange();
			int size = sourceArray.ElementSize;
			SpanHelpers.Memmove(ref Unsafe.AddByteOffset(ref destinationArray.ElementsReference, (nint)destinationIndex * size), ref Unsafe.AddByteOffset(ref sourceArray.ElementsReference, (nint)sourceIndex * size), (nuint)length * (nuint)size);
		}

		public static int IndexOf<T>(T[] array, T value)
		{
			if (array == null)
				ThrowHelper.ThrowArgument("a null array");
			EqualityComparer<T> comparer = EqualityComparer<T>.Default;
			for (int i = 0; i < array.Length; i++)
			{
				if (comparer.Equals(array[i], value))
					return i;
			}
			return -1;
		}

		public static bool Exists<T>(T[] array, Predicate<T> match) => FindIndex(array, match) >= 0;

		public static T Find<T>(T[] array, Predicate<T> match)
		{
			int index = FindIndex(array, match);
			return index < 0 ? default : array[index];
		}

		public static int FindIndex<T>(T[] array, Predicate<T> match)
		{
			if (array == null || match == null)
				ThrowHelper.ThrowArgument("a null array or match");
			for (int i = 0; i < array.Length; i++)
			{
				if (match(array[i]))
					return i;
			}
			return -1;
		}

		// A new array of the new size with the old contents; the old array is left to the caller, who may still hold it
		public static void Resize<T>(ref T[] array, int newSize)
		{
			if (newSize < 0)
				ThrowHelper.ThrowArgumentOutOfRange();
			T[] old = array;
			if (old == null)
			{
				array = new T[newSize];
				return;
			}
			if (old.Length == newSize)
				return;
			T[] grown = new T[newSize];
			new ReadOnlySpan<T>(old, 0, Math.Min(old.Length, newSize)).CopyTo(grown);
			array = grown;
		}

		public static void Fill<T>(T[] array, T value) => new Span<T>(array).Fill(value);

		public static void Reverse<T>(T[] array) => new Span<T>(array).Reverse();

		public static void Sort<T>(T[] array) => Sorting.HeapSort(new Span<T>(array), Comparer<T>.Default);

		public static void Sort<T>(T[] array, IComparer<T> comparer) => Sorting.HeapSort(new Span<T>(array), comparer ?? Comparer<T>.Default);

		public static void Sort<T>(T[] array, Comparison<T> comparison) => Sorting.HeapSort(new Span<T>(array), comparison);

		private static class EmptyArray<T>
		{
			public static readonly T[] Value = new T[0];
		}

		// Handed out through IEnumerable; Dispose is not on the non-generic interface, so foreach over a plain Array leaks it
		private sealed class ObjectEnumerator : IEnumerator
		{
			private readonly Array _array;
			private int _index = -1;

			public ObjectEnumerator(Array array)
			{
				_array = array;
			}

			public object Current => _array.GetValue(_index);

			public bool MoveNext()
			{
				if (_index + 1 >= _array.Length)
					return false;
				_index++;
				return true;
			}

			public void Reset() => _index = -1;
		}
	}

	// The compiler gives every T[] this type's interfaces and methods; `this` is really the array (Unsafe.As), as in NativeAOT
	public class Array<T> : Array, IList<T>, IReadOnlyList<T>
	{
		private T[] Self => Unsafe.As<T[]>(this);

		public int Count => Self.Length;

		public bool IsReadOnly => false;

		public T this[int index]
		{
			get => Self[index];
			set => Self[index] = value;
		}

		public int IndexOf(T item) => Array.IndexOf(Self, item);

		public bool Contains(T item) => Array.IndexOf(Self, item) >= 0;

		public void CopyTo(T[] array, int arrayIndex) => new ReadOnlySpan<T>(Self).CopyTo(new Span<T>(array).Slice(arrayIndex));

		public void Add(T item) => ThrowHelper.FailFast("FAILFAST: an array has a fixed size");

		public void Insert(int index, T item) => ThrowHelper.FailFast("FAILFAST: an array has a fixed size");

		public bool Remove(T item)
		{
			ThrowHelper.FailFast("FAILFAST: an array has a fixed size");
			return false;
		}

		public void RemoveAt(int index) => ThrowHelper.FailFast("FAILFAST: an array has a fixed size");

		public void Clear() => ThrowHelper.FailFast("FAILFAST: an array has a fixed size");

		public new IEnumerator<T> GetEnumerator() => new Enumerator(Self);

		IEnumerator IEnumerable.GetEnumerator() => new Enumerator(Self);

		// Dispose, which foreach always calls through IEnumerator<T>, gives the enumerator back to the heap
		private sealed class Enumerator : IEnumerator<T>
		{
			private readonly T[] _array;
			private int _index = -1;

			public Enumerator(T[] array)
			{
				_array = array;
			}

			public T Current => _array[_index];

			object IEnumerator.Current => Current;

			public bool MoveNext()
			{
				if (_index + 1 >= _array.Length)
					return false;
				_index++;
				return true;
			}

			public void Reset() => _index = -1;

			public void Dispose() => Allocator.FreeObject(this);
		}
	}

	[StructLayout(LayoutKind.Sequential)]
	internal class RawArrayData
	{
		public uint Length;
		public uint Padding;
		public byte Data;
	}
}
