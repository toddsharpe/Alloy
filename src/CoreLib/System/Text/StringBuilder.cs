using System.Runtime;

namespace System.Text
{
	// A growable char buffer; an outgrown buffer goes back to the heap, the builder itself is the caller's to free
	public sealed class StringBuilder
	{
		private const int DefaultCapacity = 16;

		private char[] _chars;
		private int _length;

		public StringBuilder() : this(DefaultCapacity)
		{
		}

		public StringBuilder(int capacity)
		{
			_chars = new char[capacity < DefaultCapacity ? DefaultCapacity : capacity];
		}

		public StringBuilder(string value) : this(value == null ? DefaultCapacity : value.Length + DefaultCapacity)
		{
			Append(value);
		}

		public int Length
		{
			get => _length;
			set
			{
				if (value < 0)
					ThrowHelper.ThrowArgumentOutOfRange();
				if (value > _length)
				{
					EnsureCapacity(value);
					new Span<char>(_chars, _length, value - _length).Clear();
				}
				_length = value;
			}
		}

		public int Capacity => _chars.Length;

		public char this[int index]
		{
			get
			{
				if ((uint)index >= (uint)_length)
					ThrowHelper.ThrowIndexOutOfRange();
				return _chars[index];
			}
			set
			{
				if ((uint)index >= (uint)_length)
					ThrowHelper.ThrowIndexOutOfRange();
				_chars[index] = value;
			}
		}

		public StringBuilder Append(char value)
		{
			if (_length == _chars.Length)
				EnsureCapacity(_length + 1);
			_chars[_length++] = value;
			return this;
		}

		public StringBuilder Append(char value, int repeatCount)
		{
			if (repeatCount < 0)
				ThrowHelper.ThrowArgumentOutOfRange();
			EnsureCapacity(_length + repeatCount);
			new Span<char>(_chars, _length, repeatCount).Fill(value);
			_length += repeatCount;
			return this;
		}

		public StringBuilder Append(string value) => Append(value.AsSpan());

		public StringBuilder Append(ReadOnlySpan<char> value)
		{
			if (value.Length == 0)
				return this;
			EnsureCapacity(_length + value.Length);
			value.CopyTo(new Span<char>(_chars, _length, value.Length));
			_length += value.Length;
			return this;
		}

		public StringBuilder Append(char[] value) => Append(new ReadOnlySpan<char>(value));

		public StringBuilder Append(object value) => value == null ? this : Append(value.ToString());

		public StringBuilder Append(bool value) => Append(value ? "True" : "False");

		public StringBuilder Append(int value) => Append((long)value);

		public StringBuilder Append(uint value) => Append((ulong)value);

		public StringBuilder Append(long value)
		{
			Span<char> buffer = stackalloc char[20];
			Alloy.Text.Number.TryFormatInt64(value, buffer, out int written);
			return Append(buffer.Slice(0, written));
		}

		public StringBuilder Append(ulong value)
		{
			Span<char> buffer = stackalloc char[20];
			Alloy.Text.Number.TryFormatUInt64(value, buffer, out int written);
			return Append(buffer.Slice(0, written));
		}

		public StringBuilder AppendLine() => Append('\n');

		public StringBuilder AppendLine(string value) => Append(value).Append('\n');

		public StringBuilder Insert(int index, string value)
		{
			if ((uint)index > (uint)_length)
				ThrowHelper.ThrowArgumentOutOfRange();
			ReadOnlySpan<char> text = value.AsSpan();
			if (text.Length == 0)
				return this;
			EnsureCapacity(_length + text.Length);
			new ReadOnlySpan<char>(_chars, index, _length - index).CopyTo(new Span<char>(_chars, index + text.Length, _length - index));
			text.CopyTo(new Span<char>(_chars, index, text.Length));
			_length += text.Length;
			return this;
		}

		public StringBuilder Remove(int startIndex, int length)
		{
			if (startIndex < 0 || length < 0 || startIndex > _length - length)
				ThrowHelper.ThrowArgumentOutOfRange();
			int tail = _length - startIndex - length;
			new ReadOnlySpan<char>(_chars, startIndex + length, tail).CopyTo(new Span<char>(_chars, startIndex, tail));
			_length -= length;
			return this;
		}

		public StringBuilder Clear()
		{
			_length = 0;
			return this;
		}

		public ReadOnlySpan<char> AsSpan() => new ReadOnlySpan<char>(_chars, 0, _length);

		public override string ToString() => _length == 0 ? string.Empty : new string(_chars, 0, _length);

		public string ToString(int startIndex, int length)
		{
			if (startIndex < 0 || length < 0 || startIndex > _length - length)
				ThrowHelper.ThrowArgumentOutOfRange();
			return new string(_chars, startIndex, length);
		}

		public void EnsureCapacity(int capacity)
		{
			if (capacity <= _chars.Length)
				return;
			int size = 2 * _chars.Length;
			if (size < capacity)
				size = capacity;
			char[] chars = new char[size];
			new ReadOnlySpan<char>(_chars, 0, _length).CopyTo(chars);
			Allocator.FreeObject(_chars);
			_chars = chars;
		}
	}
}
