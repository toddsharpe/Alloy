using System;

namespace Alloy.Kernel.Io
{
	// CRC-32 as GPT, zip and Ethernet use it (reflected polynomial 0xEDB88320, inverted at both ends), added up in parts
	internal struct Crc32
	{
		private uint _state;

		public static Crc32 Begin() => new Crc32 { _state = 0xFFFFFFFF };

		public void Add(ReadOnlySpan<byte> data)
		{
			for (int i = 0; i < data.Length; i++)
			{
				_state ^= data[i];
				for (int bit = 0; bit < 8; bit++)
					_state = (_state >> 1) ^ (0xEDB88320 & (0 - (_state & 1)));
			}
		}

		public uint Value => ~_state;

		public static uint Of(ReadOnlySpan<byte> data)
		{
			Crc32 crc = Begin();
			crc.Add(data);
			return crc.Value;
		}
	}
}
