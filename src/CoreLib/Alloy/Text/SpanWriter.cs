using System;

namespace Alloy.Text
{
	// Builds text in a caller buffer so logging never allocates; overflow is dropped and sets Truncated
	public ref struct SpanWriter
	{
		private readonly Span<char> _buffer;
		private int _length;

		public SpanWriter(Span<char> buffer)
		{
			_buffer = buffer;
			_length = 0;
			Truncated = false;
		}

		public readonly int Length => _length;

		public bool Truncated { readonly get; private set; }

		public readonly ReadOnlySpan<char> Written => _buffer.Slice(0, _length);

		public void Append(char value)
		{
			if (_length < _buffer.Length)
				_buffer[_length++] = value;
			else
				Truncated = true;
		}

		public void Append(string value) => Append(value.AsSpan());

		public void Append(ReadOnlySpan<char> value)
		{
			int count = value.Length;
			if (count > _buffer.Length - _length)
			{
				count = _buffer.Length - _length;
				Truncated = true;
			}
			value.Slice(0, count).CopyTo(_buffer.Slice(_length));
			_length += count;
		}

		public void Append(long value)
		{
			if (Number.TryFormatInt64(value, _buffer.Slice(_length), out int written))
				_length += written;
			else
				Truncated = true;
		}

		public void Append(ulong value)
		{
			if (Number.TryFormatUInt64(value, _buffer.Slice(_length), out int written))
				_length += written;
			else
				Truncated = true;
		}

		public void Append(int value) => Append((long)value);

		public void Append(uint value) => Append((ulong)value);

		// Hex without a prefix, zero-padded to minDigits
		public void AppendHex(ulong value, int minDigits = 1)
		{
			if (Number.TryFormatHex(value, _buffer.Slice(_length), out int written, minDigits))
				_length += written;
			else
				Truncated = true;
		}

		public override readonly string ToString() => new string(Written);
	}
}
