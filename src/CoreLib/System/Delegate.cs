using System.Runtime;
using System.Runtime.CompilerServices;
using System.Threading;
using Internal.Runtime;

namespace System
{
	// The NativeAOT delegate: four fields and the Initialize methods are a contract with ILC, which generates each delegate type's constructor, Invoke and thunks against them
	public abstract class Delegate
	{
		private object _firstParameter;
		private object _helperObject;
		private nint _extraFunctionPointerOrData;
		private IntPtr _functionPointer;

		// A multicast delegate keeps its targets in a Wrapper[] in _helperObject and their count in _extraFunctionPointerOrData
		private struct Wrapper
		{
			public Wrapper(Delegate value) => Value = value;
			public Delegate Value;
		}

		private protected const int MulticastThunk = 0;
		private protected const int ClosedStaticThunk = 1;
		private protected const int OpenStaticThunk = 2;
		private protected const int ClosedInstanceThunkOverGenericMethod = 3;
		private protected const int OpenInstanceThunk = 4;
		private protected const int ObjectArrayThunk = 5;

		// A generic method's pointer has bit 1 set and points at the code address followed by its instantiation argument
		private const nint FatFunctionPointerFlag = 2;

		// ILC overrides this in every delegate type with the thunks it generated; zero means the thunk doesn't exist
		private protected virtual IntPtr GetThunk(int whichThunk) => IntPtr.Zero;

		private void InitializeClosedInstance(object firstParameter, IntPtr functionPointer)
		{
			if (firstParameter == null)
				ThrowHelper.ThrowArgument("a delegate to an instance method of null");
			_functionPointer = functionPointer;
			_firstParameter = firstParameter;
		}

		private void InitializeClosedInstanceSlow(object firstParameter, IntPtr functionPointer)
		{
			if (firstParameter == null)
				ThrowHelper.ThrowArgument("a delegate to an instance method of null");
			if ((functionPointer & FatFunctionPointerFlag) == 0)
			{
				_functionPointer = functionPointer;
				_firstParameter = firstParameter;
			}
			else
			{
				_firstParameter = this;
				_functionPointer = GetThunk(ClosedInstanceThunkOverGenericMethod);
				_extraFunctionPointerOrData = functionPointer;
				_helperObject = firstParameter;
			}
		}

		private void InitializeClosedInstanceWithGVMResolution(object firstParameter, RuntimeMethodHandle tokenOfGenericVirtualMethod) =>
			ThrowHelper.FailFast("FAILFAST: delegates to generic virtual methods are not supported");

		private void InitializeClosedInstanceToInterface(object firstParameter, IntPtr dispatchCell)
		{
			if (firstParameter == null)
				ThrowHelper.ThrowArgument("a delegate to an interface method of null");
			_functionPointer = InterfaceDispatch.Resolve(firstParameter, dispatchCell);
			_firstParameter = firstParameter;
		}

		private void InitializeClosedStaticThunk(object firstParameter, IntPtr functionPointer, IntPtr functionPointerThunk)
		{
			_extraFunctionPointerOrData = functionPointer;
			_helperObject = firstParameter;
			_functionPointer = functionPointerThunk;
			_firstParameter = this;
		}

		private void InitializeOpenStaticThunk(object firstParameter, IntPtr functionPointer, IntPtr functionPointerThunk)
		{
			_firstParameter = this;
			_functionPointer = functionPointerThunk;
			_extraFunctionPointerOrData = functionPointer;
		}

		public object Target
		{
			get
			{
				if (_helperObject is Wrapper[] invocationList)
					return invocationList[(int)_extraFunctionPointerOrData - 1].Value.Target;
				if (_functionPointer == GetThunk(ClosedStaticThunk) || _functionPointer == GetThunk(ClosedInstanceThunkOverGenericMethod))
					return _helperObject;
				return ReferenceEquals(_firstParameter, this) ? null : _firstParameter;
			}
		}

		public bool HasSingleTarget => _helperObject is not Wrapper[];

		public virtual Delegate[] GetInvocationList()
		{
			if (_helperObject is Wrapper[] invocationList)
			{
				int count = (int)_extraFunctionPointerOrData;
				Delegate[] result = new Delegate[count];
				for (int i = 0; i < count; i++)
					result[i] = invocationList[i].Value;
				return result;
			}
			return new Delegate[] { this };
		}

		public static Delegate Combine(Delegate a, Delegate b)
		{
			if (a == null)
				return b;
			return a.CombineImpl(b);
		}

		public static Delegate Remove(Delegate source, Delegate value)
		{
			if (source == null || value == null)
				return source;
			return source.RemoveImpl(value);
		}

		private unsafe Delegate NewMulticastDelegate(Wrapper[] invocationList, int invocationCount, bool thisIsMulticastAlready = false)
		{
			Delegate result = Unsafe.As<Delegate>(Allocator.NewObject(GetMethodTable()));
			result._functionPointer = thisIsMulticastAlready ? _functionPointer : GetThunk(MulticastThunk);
			result._firstParameter = result;
			result._helperObject = invocationList;
			result._extraFunctionPointerOrData = invocationCount;
			return result;
		}

		private static bool TrySetSlot(Wrapper[] list, int index, Delegate value)
		{
			if (list[index].Value == null && Interlocked.CompareExchange(ref list[index].Value, value, null) == null)
				return true;
			Delegate present = list[index].Value;
			return present != null && ReferenceEquals(present._firstParameter, value._firstParameter) && ReferenceEquals(present._helperObject, value._helperObject) &&
				present._extraFunctionPointerOrData == value._extraFunctionPointerOrData && present._functionPointer == value._functionPointer;
		}

		protected virtual unsafe Delegate CombineImpl(Delegate follow)
		{
			if (follow == null)
				return this;
			if (GetMethodTable() != follow.GetMethodTable())
				ThrowHelper.ThrowArgument("delegates of different types");

			int followCount = 1;
			Wrapper[] followList = follow._helperObject as Wrapper[];
			if (followList != null)
				followCount = (int)follow._extraFunctionPointerOrData;

			if (_helperObject is not Wrapper[] invocationList)
			{
				int count = 1 + followCount;
				Wrapper[] list = new Wrapper[count];
				list[0] = new Wrapper(this);
				if (followList == null)
					list[1] = new Wrapper(follow);
				else
					new ReadOnlySpan<Wrapper>(followList, 0, followCount).CopyTo(new Span<Wrapper>(list, 1, followCount));
				return NewMulticastDelegate(list, count);
			}
			else
			{
				int invocationCount = (int)_extraFunctionPointerOrData;
				int count = invocationCount + followCount;
				Wrapper[] list = null;
				if (count <= invocationList.Length)
				{
					list = invocationList;
					if (followList == null)
					{
						if (!TrySetSlot(list, invocationCount, follow))
							list = null;
					}
					else
					{
						for (int i = 0; i < followCount; i++)
						{
							if (!TrySetSlot(list, invocationCount + i, followList[i].Value))
							{
								list = null;
								break;
							}
						}
					}
				}
				if (list == null)
				{
					int capacity = invocationList.Length;
					while (capacity < count)
						capacity *= 2;
					list = new Wrapper[capacity];
					new ReadOnlySpan<Wrapper>(invocationList, 0, invocationCount).CopyTo(list);
					if (followList == null)
						list[invocationCount] = new Wrapper(follow);
					else
						new ReadOnlySpan<Wrapper>(followList, 0, followCount).CopyTo(new Span<Wrapper>(list, invocationCount, followCount));
				}
				return NewMulticastDelegate(list, count, true);
			}
		}

		private static Wrapper[] DeleteFromInvocationList(Wrapper[] invocationList, int invocationCount, int deleteIndex, int deleteCount)
		{
			int capacity = invocationList.Length;
			while (capacity / 2 >= invocationCount - deleteCount)
				capacity /= 2;
			Wrapper[] list = new Wrapper[capacity];
			new ReadOnlySpan<Wrapper>(invocationList, 0, deleteIndex).CopyTo(list);
			int tail = invocationCount - deleteIndex - deleteCount;
			new ReadOnlySpan<Wrapper>(invocationList, deleteIndex + deleteCount, tail).CopyTo(new Span<Wrapper>(list, deleteIndex, tail));
			return list;
		}

		private static bool EqualInvocationLists(Wrapper[] a, Wrapper[] b, int start, int count)
		{
			for (int i = 0; i < count; i++)
			{
				if (!a[start + i].Value.Equals(b[i].Value))
					return false;
			}
			return true;
		}

		// Removes the last occurrence of value's targets from this delegate's list
		protected virtual Delegate RemoveImpl(Delegate value)
		{
			if (value == null)
				return this;
			if (value._helperObject is not Wrapper[] valueList)
			{
				if (_helperObject is not Wrapper[] invocationList)
					return Equals(value) ? null : this;

				int invocationCount = (int)_extraFunctionPointerOrData;
				for (int i = invocationCount; --i >= 0;)
				{
					if (value.Equals(invocationList[i].Value))
					{
						if (invocationCount == 2)
							return invocationList[1 - i].Value;
						return NewMulticastDelegate(DeleteFromInvocationList(invocationList, invocationCount, i, 1), invocationCount - 1, true);
					}
				}
			}
			else if (_helperObject is Wrapper[] invocationList)
			{
				int invocationCount = (int)_extraFunctionPointerOrData;
				int valueCount = (int)value._extraFunctionPointerOrData;
				for (int i = invocationCount - valueCount; i >= 0; i--)
				{
					if (EqualInvocationLists(invocationList, valueList, i, valueCount))
					{
						if (invocationCount - valueCount == 0)
							return null;
						if (invocationCount - valueCount == 1)
							return invocationList[i != 0 ? 0 : invocationCount - 1].Value;
						return NewMulticastDelegate(DeleteFromInvocationList(invocationList, invocationCount, i, valueCount), invocationCount - valueCount, true);
					}
				}
			}
			return this;
		}

		public override unsafe bool Equals(object obj)
		{
			if (obj == null)
				return false;
			if (ReferenceEquals(this, obj))
				return true;
			if (GetMethodTable() != obj.GetMethodTable())
				return false;
			Delegate other = Unsafe.As<Delegate>(obj);

			if (_helperObject is Wrapper[] invocationList)
			{
				if (other._extraFunctionPointerOrData != _extraFunctionPointerOrData || other._helperObject is not Wrapper[] otherList)
					return false;
				return EqualInvocationLists(invocationList, otherList, 0, (int)_extraFunctionPointerOrData);
			}

			if (!ReferenceEquals(_helperObject, other._helperObject) || !FunctionPointersEqual(_extraFunctionPointerOrData, other._extraFunctionPointerOrData) || !FunctionPointersEqual(_functionPointer, other._functionPointer))
				return false;

			// Thunk-based delegates point _firstParameter at themselves, so those compare by kind rather than identity
			if (ReferenceEquals(_firstParameter, this))
				return ReferenceEquals(other._firstParameter, other);
			return ReferenceEquals(_firstParameter, other._firstParameter);
		}

		public override int GetHashCode()
		{
			if (_helperObject is Wrapper[] invocationList)
			{
				int multicastHash = 0;
				for (int i = 0; i < (int)_extraFunctionPointerOrData; i++)
					multicastHash = multicastHash * 33 + invocationList[i].Value.GetHashCode();
				return multicastHash;
			}
			int hash = (_helperObject == null ? 0 : _helperObject.GetHashCode()) + 7 * _extraFunctionPointerOrData.GetHashCode() + 13 * _functionPointer.GetHashCode();
			if (!ReferenceEquals(_firstParameter, this))
				hash += 17 * (_firstParameter == null ? 0 : _firstParameter.GetHashCode());
			return hash;
		}

		// Two generic method pointers are the same method when their code and instantiation arguments match
		private static unsafe bool FunctionPointersEqual(nint a, nint b)
		{
			if (a == b)
				return true;
			if ((a & FatFunctionPointerFlag) == 0 || (b & FatFunctionPointerFlag) == 0)
				return false;
			nint* first = (nint*)(a & ~FatFunctionPointerFlag);
			nint* second = (nint*)(b & ~FatFunctionPointerFlag);
			return first[0] == second[0] && first[1] == second[1];
		}

		public static bool operator ==(Delegate d1, Delegate d2)
		{
			if (d2 is null)
				return d1 is null;
			return ReferenceEquals(d1, d2) || d2.Equals(d1);
		}

		public static bool operator !=(Delegate d1, Delegate d2) => !(d1 == d2);
	}

	public abstract class MulticastDelegate : Delegate
	{
		public static bool operator ==(MulticastDelegate d1, MulticastDelegate d2)
		{
			if (d2 is null)
				return d1 is null;
			return ReferenceEquals(d1, d2) || d2.Equals(d1);
		}

		public static bool operator !=(MulticastDelegate d1, MulticastDelegate d2) => !(d1 == d2);

		public override bool Equals(object obj) => base.Equals(obj);

		public override int GetHashCode() => base.GetHashCode();
	}

	public delegate void Action();
	public delegate void Action<T>(T obj);
	public delegate void Action<T1, T2>(T1 arg1, T2 arg2);
	public delegate void Action<T1, T2, T3>(T1 arg1, T2 arg2, T3 arg3);
	public delegate void Action<T1, T2, T3, T4>(T1 arg1, T2 arg2, T3 arg3, T4 arg4);
	public delegate TResult Func<TResult>();
	public delegate TResult Func<T, TResult>(T arg);
	public delegate TResult Func<T1, T2, TResult>(T1 arg1, T2 arg2);
	public delegate TResult Func<T1, T2, T3, TResult>(T1 arg1, T2 arg2, T3 arg3);
	public delegate TResult Func<T1, T2, T3, T4, TResult>(T1 arg1, T2 arg2, T3 arg3, T4 arg4);
	public delegate bool Predicate<T>(T obj);
	public delegate int Comparison<T>(T x, T y);
	public delegate TOutput Converter<TInput, TOutput>(TInput input);
	public delegate void EventHandler(object sender, EventArgs e);
	public delegate void EventHandler<TEventArgs>(object sender, TEventArgs e);
}
