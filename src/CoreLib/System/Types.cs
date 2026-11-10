namespace System
{
	// Delegates aren't supported yet (no Delegate.Initialize* helpers); use function pointers instead
	public abstract class Delegate
	{
	}

	public abstract class MulticastDelegate : Delegate
	{
	}

	// Contract with the compiler: a flag then the value
	public struct Nullable<T> where T : struct
	{
		private readonly bool hasValue;
		internal T value;

		public Nullable(T value)
		{
			hasValue = true;
			this.value = value;
		}

		public readonly bool HasValue => hasValue;

		public readonly T Value
		{
			get
			{
				if (!hasValue)
					ThrowHelper.FailFast("Nullable object must have a value");
				return value;
			}
		}

		public readonly T GetValueOrDefault() => value;

		public static implicit operator T?(T value) => new T?(value);

		public static explicit operator T(T? value) => value.Value;
	}

	public struct RuntimeTypeHandle
	{
		private nint _value;
	}

	public struct RuntimeMethodHandle
	{
		private nint _value;
	}

	public struct RuntimeFieldHandle
	{
		private nint _value;
	}

	// No reflection; typeof(T) is not available, but some compiler paths require these types to exist
	public abstract class Type
	{
	}

	internal sealed class RuntimeType : Type
	{
	}
}
