using System;
using Alloy.Arch.X64;

namespace Alloy.Devices
{
	// Polled 16550; on Hyper-V the host drops output while no reader is attached to the COM pipe
	public readonly struct Uart16550
	{
		public const ushort Com1Port = 0x3F8;
		public const ushort Com2Port = 0x2F8;

		private const int Data = 0;
		private const int InterruptEnable = 1;
		private const int FifoControl = 2;
		private const int LineControl = 3;
		private const int ModemControl = 4;
		private const int LineStatus = 5;

		private const byte LineStatusTransmitEmpty = 0x20;

		private readonly ushort _port;

		public Uart16550(ushort port)
		{
			_port = port;
		}

		public static Uart16550 Com1 => new Uart16550(Com1Port);

		// 115200 baud, 8 data bits, no parity, one stop bit, FIFOs on, no interrupts
		public void Initialize()
		{
			Cpu.OutByte((ushort)(_port + InterruptEnable), 0x00);
			Cpu.OutByte((ushort)(_port + LineControl), 0x80);
			Cpu.OutByte((ushort)(_port + Data), 0x01);
			Cpu.OutByte((ushort)(_port + InterruptEnable), 0x00);
			Cpu.OutByte((ushort)(_port + LineControl), 0x03);
			Cpu.OutByte((ushort)(_port + FifoControl), 0xC7);
			Cpu.OutByte((ushort)(_port + ModemControl), 0x03);
		}

		public void Write(char value)
		{
			// The host side is a pipe of bytes; anything outside ASCII becomes '?'
			byte b = value < 0x80 ? (byte)value : (byte)'?';
			while ((Cpu.InByte((ushort)(_port + LineStatus)) & LineStatusTransmitEmpty) == 0)
				Cpu.Pause();
			Cpu.OutByte(_port, b);
		}

		public void Write(ReadOnlySpan<char> text)
		{
			foreach (char c in text)
			{
				if (c == '\n')
					Write('\r');
				Write(c);
			}
		}
	}
}
