using System;
using System.Buffers.Binary;
using Alloy.Abi;
using Alloy.Kernel.Io;
using Alloy.Kernel.Ke;

namespace Alloy.Kernel.Platform
{
	// The Hyper-V synthetic keyboard (MetalOS HyperVKeyboardDriver): scan code set 1 in, virtual keys out to the input ring
	internal sealed class SyntheticKeyboard : VmBusChannel
	{
		private const int RingPages = 10;
		private const uint Version = 1 << 16;
		private const uint ProtocolRequest = 1;
		private const uint ProtocolResponse = 2;
		private const uint KeyEvent = 3;
		private const uint ProtocolAccepted = 1;
		private const uint IsUnicode = 1;
		private const uint IsBreak = 2;
		private const uint IsE0 = 4;
		private const ushort E0LeftWindows = 0x5B;

		private readonly KEvent _response = new KEvent(false);
		private uint _status;

		// http://www.scs.stanford.edu/10wi-cs140/pintos/specs/kbd/scancodes-1.html, as MetalOS has it with the OEM keys corrected
		private static ReadOnlySpan<byte> ScancodeSet1 => new byte[]
		{
			0x00, 0x1B, 0x31, 0x32, 0x33, 0x34, 0x35, 0x36,
			0x37, 0x38, 0x39, 0x30, 0xBD, 0xBB, 0x08, 0x09,
			0x51, 0x57, 0x45, 0x52, 0x54, 0x59, 0x55, 0x49,
			0x4F, 0x50, 0xDB, 0xDD, 0x0D, 0x11, 0x41, 0x53,
			0x44, 0x46, 0x47, 0x48, 0x4A, 0x4B, 0x4C, 0xBA,
			0xDE, 0xC0, 0xA0, 0xDC, 0x5A, 0x58, 0x43, 0x56,
			0x42, 0x4E, 0x4D, 0xBC, 0xBE, 0xBF, 0xA1, 0x6A,
			0x12, 0x20, 0x14, 0x70, 0x71, 0x72, 0x73, 0x74,
			0x75, 0x76, 0x77, 0x78, 0x79, 0x90, 0x91, 0x24,
			0x26, 0x21, 0x6D, 0x25, 0x65, 0x27, 0x6B, 0x23,
			0x28, 0x22, 0x2D, 0x2E, 0x00, 0x00, 0x00, 0x7A,
			0x7B,
		};

		public static bool Start(in VmBusOffer offer)
		{
			SyntheticKeyboard keyboard = new SyntheticKeyboard();
			if (!keyboard.Open(in offer, RingPages, RingPages))
				return false;
			Span<byte> request = stackalloc byte[8];
			BinaryPrimitives.WriteUInt32LittleEndian(request, ProtocolRequest);
			BinaryPrimitives.WriteUInt32LittleEndian(request.Slice(4), Version);
			if (!keyboard.Send(request, 1, PacketInband, CompletionRequested))
				return false;
			return keyboard._response.Wait(2000) && (keyboard._status & ProtocolAccepted) != 0;
		}

		protected override void OnPacket(in VmBusPacket packet, ReadOnlySpan<byte> header, ReadOnlySpan<byte> payload)
		{
			if (packet.Type != PacketInband || payload.Length < 8)
				return;
			uint message = BinaryPrimitives.ReadUInt32LittleEndian(payload);
			if (message == ProtocolResponse)
			{
				_status = BinaryPrimitives.ReadUInt32LittleEndian(payload.Slice(4));
				_response.Signal();
				return;
			}
			if (message != KeyEvent || payload.Length < 12)
				return;

			ushort code = BinaryPrimitives.ReadUInt16LittleEndian(payload.Slice(4));
			uint info = BinaryPrimitives.ReadUInt32LittleEndian(payload.Slice(8));
			ushort key = (info & IsUnicode) != 0 ? FromUnicode(code)
				: (info & IsE0) != 0 && code == E0LeftWindows ? VirtualKey.LeftWindows
				: code < ScancodeSet1.Length ? ScancodeSet1[code] : VirtualKey.None;
			if (key == VirtualKey.None)
				return;
			InputEvent input = default;
			input.Type = InputEventType.Key;
			input.Key = key;
			input.Pressed = (ushort)((info & IsBreak) == 0 ? 1 : 0);
			Input.Post(in input);
		}

		// VM Connect's "Type clipboard text" sends UTF-16 code units; map them to the keys the scan code path produces
		private static ushort FromUnicode(ushort c)
		{
			if (c >= 'a' && c <= 'z')
				return (ushort)(c - 'a' + 'A');
			if ((c >= 'A' && c <= 'Z') || (c >= '0' && c <= '9'))
				return c;
			switch (c)
			{
				case ' ':
					return VirtualKey.Space;
				case '\r':
				case '\n':
					return VirtualKey.Return;
				case '\b':
					return VirtualKey.Back;
				case '\t':
					return VirtualKey.Tab;
				case '-':
					return VirtualKey.OemMinus;
				case '=':
					return VirtualKey.OemPlus;
				case ',':
					return VirtualKey.OemComma;
				case '.':
					return VirtualKey.OemPeriod;
				case '/':
					return VirtualKey.OemSlash;
				case '*':
					return VirtualKey.Multiply;
				case '+':
					return VirtualKey.Add;
				default:
					return VirtualKey.None;
			}
		}
	}
}
