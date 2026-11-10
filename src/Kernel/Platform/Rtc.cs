using Alloy.Abi;
using Alloy.Arch.X64;

namespace Alloy.Kernel.Platform
{
	// The CMOS real-time clock, which Hyper-V keeps in host local time; MetalOS reads the same clock through EFI GetTime
	internal static class Rtc
	{
		private const ushort IndexPort = 0x70;
		private const ushort DataPort = 0x71;

		public static SystemTime Read()
		{
			// Two passes that agree can't have straddled an update
			SystemTime first, second;
			do
			{
				first = ReadOnce();
				second = ReadOnce();
			}
			while (first.Second != second.Second || first.Minute != second.Minute || first.Hour != second.Hour || first.Day != second.Day);

			byte status = Register(0x0B);
			bool bcd = (status & 4) == 0;
			bool clock12 = (status & 2) == 0;
			bool pm = (second.Hour & 0x80) != 0;
			second.Hour &= 0x7F;
			if (bcd)
			{
				second.Second = FromBcd(second.Second);
				second.Minute = FromBcd(second.Minute);
				second.Hour = FromBcd(second.Hour);
				second.Day = FromBcd(second.Day);
				second.Month = FromBcd(second.Month);
				second.Year = FromBcd(second.Year);
			}
			if (clock12)
				second.Hour = (ushort)(second.Hour % 12 + (pm ? 12 : 0));
			second.Year += 2000;
			return second;
		}

		private static SystemTime ReadOnce()
		{
			while ((Register(0x0A) & 0x80) != 0)
				Cpu.Pause();
			SystemTime time = default;
			time.Second = Register(0x00);
			time.Minute = Register(0x02);
			time.Hour = Register(0x04);
			time.Day = Register(0x07);
			time.Month = Register(0x08);
			time.Year = Register(0x09);
			return time;
		}

		private static byte Register(byte index)
		{
			Cpu.OutByte(IndexPort, index);
			return Cpu.InByte(DataPort);
		}

		private static ushort FromBcd(ushort value) => (ushort)((value & 0x0F) + (value >> 4) * 10);
	}
}
