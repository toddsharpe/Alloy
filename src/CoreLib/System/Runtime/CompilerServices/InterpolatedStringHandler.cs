using System.Runtime.InteropServices;
using Alloy.Text;
using Internal.Runtime;

namespace System.Runtime.CompilerServices
{
	[AttributeUsage(AttributeTargets.Class | AttributeTargets.Struct, Inherited = false)]
	public sealed class InterpolatedStringHandlerAttribute : Attribute
	{
	}

	[AttributeUsage(AttributeTargets.Parameter, Inherited = false)]
	public sealed class InterpolatedStringHandlerArgumentAttribute : Attribute
	{
		public InterpolatedStringHandlerArgumentAttribute(string argument)
		{
			Arguments = new string[] { argument };
		}

		public InterpolatedStringHandlerArgumentAttribute(params string[] arguments)
		{
			Arguments = arguments;
		}

		public string[] Arguments { get; }
	}

	// What $"..." compiles to when the result is a string: a heap buffer that grows as needed and is freed once the string is made
	[InterpolatedStringHandler]
	public ref struct DefaultInterpolatedStringHandler
	{
		private const int MinimumCapacity = 32;

		private char[] _array;
		private Span<char> _chars;
		private int _pos;

		public DefaultInterpolatedStringHandler(int literalLength, int formattedCount)
		{
			int capacity = literalLength + 11 * formattedCount;
			_array = new char[capacity < MinimumCapacity ? MinimumCapacity : capacity];
			_chars = _array;
			_pos = 0;
		}

		public DefaultInterpolatedStringHandler(int literalLength, int formattedCount, IFormatProvider provider) : this(literalLength, formattedCount)
		{
		}

		public string ToStringAndClear()
		{
			string result = _pos == 0 ? string.Empty : new string(_chars.Slice(0, _pos));
			Allocator.FreeObject(_array);
			_array = null;
			_chars = default;
			_pos = 0;
			return result;
		}

		public void AppendLiteral(string value) => AppendFormatted(value.AsSpan());

		public void AppendFormatted(string value) => AppendFormatted(value.AsSpan());

		public void AppendFormatted(string value, int alignment = 0, string format = null) => AppendFormatted(value.AsSpan(), alignment, format);

		public void AppendFormatted(ReadOnlySpan<char> value)
		{
			if (_chars.Length - _pos < value.Length)
				Grow(value.Length);
			value.CopyTo(_chars.Slice(_pos));
			_pos += value.Length;
		}

		public void AppendFormatted(ReadOnlySpan<char> value, int alignment = 0, string format = null)
		{
			int needed = value.Length > Math.Abs(alignment) ? value.Length : Math.Abs(alignment);
			if (_chars.Length - _pos < needed)
				Grow(needed);
			_pos += ValueFormatter.Pad(_chars.Slice(_pos), value, alignment);
		}

		public void AppendFormatted<T>(T value) => AppendFormatted(value, 0, null);

		public void AppendFormatted<T>(T value, string format) => AppendFormatted(value, 0, format);

		public void AppendFormatted<T>(T value, int alignment) => AppendFormatted(value, alignment, null);

		public void AppendFormatted<T>(T value, int alignment, string format)
		{
			while (true)
			{
				int written = ValueFormatter.Format(value, _chars.Slice(_pos), alignment, format.AsSpan());
				if (written >= 0)
				{
					_pos += written;
					return;
				}
				Grow(_chars.Length);
			}
		}

		public void AppendFormatted(object value, int alignment = 0, string format = null) => AppendFormatted<object>(value, alignment, format);

		private void Grow(int additional)
		{
			int capacity = 2 * _chars.Length;
			if (capacity < _pos + additional)
				capacity = _pos + additional;
			char[] array = new char[capacity];
			_chars.Slice(0, _pos).CopyTo(array);
			Allocator.FreeObject(_array);
			_array = array;
			_chars = array;
		}
	}

	// Formats one value into a span; shared by the string handlers, so every interpolation hole goes through here
	internal static class ValueFormatter
	{
		// The formatted value with its alignment applied, or -1 when destination is too small
		public static unsafe int Format<T>(T value, Span<char> destination, int alignment, ReadOnlySpan<char> format)
		{
			int width = Math.Abs(alignment);
			if (width > destination.Length)
				return -1;

			int written;
			if (typeof(T) == typeof(int))
				written = Number.TryFormatSigned(Unsafe.As<T, int>(ref value), 32, destination, format, out int n) ? n : -1;
			else if (typeof(T) == typeof(uint))
				written = Number.TryFormatUnsigned(Unsafe.As<T, uint>(ref value), destination, format, out int n) ? n : -1;
			else if (typeof(T) == typeof(long))
				written = Number.TryFormatSigned(Unsafe.As<T, long>(ref value), 64, destination, format, out int n) ? n : -1;
			else if (typeof(T) == typeof(ulong))
				written = Number.TryFormatUnsigned(Unsafe.As<T, ulong>(ref value), destination, format, out int n) ? n : -1;
			else if (typeof(T) == typeof(short))
				written = Number.TryFormatSigned(Unsafe.As<T, short>(ref value), 16, destination, format, out int n) ? n : -1;
			else if (typeof(T) == typeof(ushort))
				written = Number.TryFormatUnsigned(Unsafe.As<T, ushort>(ref value), destination, format, out int n) ? n : -1;
			else if (typeof(T) == typeof(byte))
				written = Number.TryFormatUnsigned(Unsafe.As<T, byte>(ref value), destination, format, out int n) ? n : -1;
			else if (typeof(T) == typeof(sbyte))
				written = Number.TryFormatSigned(Unsafe.As<T, sbyte>(ref value), 8, destination, format, out int n) ? n : -1;
			else if (typeof(T) == typeof(nint))
				written = Number.TryFormatSigned(Unsafe.As<T, nint>(ref value), 64, destination, format, out int n) ? n : -1;
			else if (typeof(T) == typeof(nuint))
				written = Number.TryFormatUnsigned(Unsafe.As<T, nuint>(ref value), destination, format, out int n) ? n : -1;
			else if (typeof(T) == typeof(char))
				written = Copy(destination, new ReadOnlySpan<char>(in Unsafe.As<T, char>(ref value)));
			else if (typeof(T) == typeof(bool))
				written = Copy(destination, Unsafe.As<T, bool>(ref value) ? "True" : "False");
			else if (typeof(T) == typeof(string))
				written = Copy(destination, Unsafe.As<T, string>(ref value).AsSpan());
			else if (typeof(T).IsEnum)
				written = FormatEnum(value, destination, format);
			else if (value == null)
				written = 0;
			else if (value is ISpanFormattable spanFormattable)
				written = spanFormattable.TryFormat(destination, out int n, format, null) ? n : -1;
			else if (!format.IsEmpty && value is IFormattable formattable)
				written = Copy(destination, formattable.ToString(new string(format), null));
			else
				written = Copy(destination, value.ToString());

			if (written < 0)
				return -1;
			return Align(destination, written, alignment);
		}

		private static unsafe int FormatEnum<T>(T value, Span<char> destination, ReadOnlySpan<char> format)
		{
			EETypeElementType underlying = MethodTable.Of<T>()->ElementType;
			switch (underlying)
			{
				case EETypeElementType.SByte:
					return Number.TryFormatSigned(Unsafe.As<T, sbyte>(ref value), 8, destination, format, out int n1) ? n1 : -1;
				case EETypeElementType.Byte:
					return Number.TryFormatUnsigned(Unsafe.As<T, byte>(ref value), destination, format, out int n2) ? n2 : -1;
				case EETypeElementType.Int16:
					return Number.TryFormatSigned(Unsafe.As<T, short>(ref value), 16, destination, format, out int n3) ? n3 : -1;
				case EETypeElementType.UInt16:
					return Number.TryFormatUnsigned(Unsafe.As<T, ushort>(ref value), destination, format, out int n4) ? n4 : -1;
				case EETypeElementType.UInt32:
					return Number.TryFormatUnsigned(Unsafe.As<T, uint>(ref value), destination, format, out int n5) ? n5 : -1;
				case EETypeElementType.Int64:
					return Number.TryFormatSigned(Unsafe.As<T, long>(ref value), 64, destination, format, out int n6) ? n6 : -1;
				case EETypeElementType.UInt64:
					return Number.TryFormatUnsigned(Unsafe.As<T, ulong>(ref value), destination, format, out int n7) ? n7 : -1;
				default:
					return Number.TryFormatSigned(Unsafe.As<T, int>(ref value), 32, destination, format, out int n8) ? n8 : -1;
			}
		}

		private static int Copy(Span<char> destination, ReadOnlySpan<char> text)
		{
			if (text.Length > destination.Length)
				return -1;
			text.CopyTo(destination);
			return text.Length;
		}

		// Text already at the start of destination, padded to the alignment: positive right-aligns, negative left-aligns
		private static int Align(Span<char> destination, int written, int alignment)
		{
			int width = Math.Abs(alignment);
			if (written >= width)
				return written;
			if (alignment > 0)
			{
				destination.Slice(0, written).CopyTo(destination.Slice(width - written));
				destination.Slice(0, width - written).Fill(' ');
			}
			else
			{
				destination.Slice(written, width - written).Fill(' ');
			}
			return width;
		}

		public static int Pad(Span<char> destination, ReadOnlySpan<char> text, int alignment)
		{
			text.CopyTo(destination);
			return Align(destination, text.Length, alignment);
		}
	}
}

namespace System
{
	using System.Runtime.CompilerServices;

	public static partial class MemoryExtensions
	{
		// $"..." written straight into a span, no heap at all; false when it doesn't fit
		public static bool TryWrite(this Span<char> destination, [InterpolatedStringHandlerArgument("destination")] ref TryWriteInterpolatedStringHandler handler, out int charsWritten)
		{
			if (handler._success)
			{
				charsWritten = handler._pos;
				return true;
			}
			charsWritten = 0;
			return false;
		}

		[InterpolatedStringHandler]
		public ref struct TryWriteInterpolatedStringHandler
		{
			private readonly Span<char> _destination;
			internal int _pos;
			internal bool _success;

			public TryWriteInterpolatedStringHandler(int literalLength, int formattedCount, Span<char> destination, out bool shouldAppend)
			{
				_destination = destination;
				_pos = 0;
				_success = shouldAppend = destination.Length >= literalLength;
			}

			public bool AppendLiteral(string value) => AppendFormatted(value.AsSpan());

			public bool AppendFormatted(string value) => AppendFormatted(value.AsSpan());

			public bool AppendFormatted(string value, int alignment = 0, string format = null) => AppendFormatted(value.AsSpan(), alignment, format);

			public bool AppendFormatted(ReadOnlySpan<char> value)
			{
				if (_destination.Length - _pos < value.Length)
					return Fail();
				value.CopyTo(_destination.Slice(_pos));
				_pos += value.Length;
				return true;
			}

			public bool AppendFormatted(ReadOnlySpan<char> value, int alignment = 0, string format = null)
			{
				int needed = value.Length > Math.Abs(alignment) ? value.Length : Math.Abs(alignment);
				if (_destination.Length - _pos < needed)
					return Fail();
				_pos += ValueFormatter.Pad(_destination.Slice(_pos), value, alignment);
				return true;
			}

			public bool AppendFormatted<T>(T value) => AppendFormatted(value, 0, null);

			public bool AppendFormatted<T>(T value, string format) => AppendFormatted(value, 0, format);

			public bool AppendFormatted<T>(T value, int alignment) => AppendFormatted(value, alignment, null);

			public bool AppendFormatted<T>(T value, int alignment, string format)
			{
				int written = ValueFormatter.Format(value, _destination.Slice(_pos), alignment, format.AsSpan());
				if (written < 0)
					return Fail();
				_pos += written;
				return true;
			}

			public bool AppendFormatted(object value, int alignment = 0, string format = null) => AppendFormatted<object>(value, alignment, format);

			private bool Fail()
			{
				_success = false;
				_pos = 0;
				return false;
			}
		}
	}
}
