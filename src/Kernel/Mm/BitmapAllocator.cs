namespace Alloy.Kernel.Mm
{
	// One bit per page, set when used; hardware-free so the host tests run it directly
	public struct BitmapAllocator
	{
		private readonly unsafe byte* _bits;
		private readonly ulong _count;
		private ulong _hint;

		public unsafe BitmapAllocator(byte* bits, ulong count)
		{
			_bits = bits;
			_count = count;
			_hint = 0;
			FreeCount = 0;
			new System.Span<byte>(bits, (int)((count + 7) / 8)).Fill(0xFF);
		}

		public ulong Count => _count;

		public ulong FreeCount { get; private set; }

		public readonly unsafe bool IsUsed(ulong index) => index >= _count || (_bits[index >> 3] & (1 << (int)(index & 7))) != 0;

		public unsafe void MarkFree(ulong start, ulong count)
		{
			for (ulong i = start; i < start + count && i < _count; i++)
			{
				if (IsUsed(i))
				{
					_bits[i >> 3] &= (byte)~(1 << (int)(i & 7));
					FreeCount++;
				}
			}
		}

		public unsafe void MarkUsed(ulong start, ulong count)
		{
			for (ulong i = start; i < start + count && i < _count; i++)
			{
				if (!IsUsed(i))
				{
					_bits[i >> 3] |= (byte)(1 << (int)(i & 7));
					FreeCount--;
				}
			}
		}

		// First fit from a moving hint, wrapping once; returns the first index or ulong.MaxValue
		public ulong Allocate(ulong count)
		{
			if (count == 0 || count > FreeCount)
				return ulong.MaxValue;

			for (int pass = 0; pass < 2; pass++)
			{
				ulong start = pass == 0 ? _hint : 0;
				ulong run = 0;
				for (ulong i = start; i < _count; i++)
				{
					run = IsUsed(i) ? 0 : run + 1;
					if (run == count)
					{
						ulong first = i + 1 - count;
						MarkUsed(first, count);
						_hint = i + 1;
						return first;
					}
				}
			}
			return ulong.MaxValue;
		}
	}
}
