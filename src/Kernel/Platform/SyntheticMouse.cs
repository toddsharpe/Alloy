using System;
using System.Buffers.Binary;
using Alloy.Abi;
using Alloy.Kernel.Io;
using Alloy.Kernel.Ke;

namespace Alloy.Kernel.Platform
{
	// The Hyper-V synthetic HID mouse (MetalOS HyperVMouseDriver): pipe messages wrapping synthhid messages, absolute positions
	internal sealed class SyntheticMouse : VmBusChannel
	{
		private const int RingPages = 10;
		private const uint PipeData = 1;
		private const uint Version = 2 << 16;
		private const uint ProtocolRequest = 0;
		private const uint ProtocolResponse = 1;
		private const uint DeviceInfo = 2;
		private const uint DeviceInfoAck = 3;
		private const uint InputReport = 4;

		private readonly KEvent _response = new KEvent(false);
		private readonly KEvent _deviceInfo = new KEvent(false);
		private bool _approved;

		public static bool Start(in VmBusOffer offer)
		{
			SyntheticMouse mouse = new SyntheticMouse();
			if (!mouse.Open(in offer, RingPages, RingPages))
				return false;
			Span<byte> request = stackalloc byte[20];
			BinaryPrimitives.WriteUInt32LittleEndian(request, PipeData);
			BinaryPrimitives.WriteUInt32LittleEndian(request.Slice(4), 12);
			BinaryPrimitives.WriteUInt32LittleEndian(request.Slice(8), ProtocolRequest);
			BinaryPrimitives.WriteUInt32LittleEndian(request.Slice(12), 4);
			BinaryPrimitives.WriteUInt32LittleEndian(request.Slice(16), Version);
			if (!mouse.Send(request, 1, PacketInband, CompletionRequested))
				return false;
			return mouse._response.Wait(2000) && mouse._approved && mouse._deviceInfo.Wait(2000);
		}

		// Each message is |pipe type, size|synthhid type, size|data|
		protected override void OnPacket(in VmBusPacket packet, ReadOnlySpan<byte> header, ReadOnlySpan<byte> payload)
		{
			if (packet.Type != PacketInband || payload.Length < 16 || BinaryPrimitives.ReadUInt32LittleEndian(payload) != PipeData)
				return;
			uint message = BinaryPrimitives.ReadUInt32LittleEndian(payload.Slice(8));
			if (message == ProtocolResponse && payload.Length >= 21)
			{
				_approved = payload[20] != 0;
				_response.Signal();
			}
			else if (message == DeviceInfo)
			{
				// The host holds input reports back until the device info is acknowledged
				Span<byte> ack = stackalloc byte[17];
				ack.Clear();
				BinaryPrimitives.WriteUInt32LittleEndian(ack, PipeData);
				BinaryPrimitives.WriteUInt32LittleEndian(ack.Slice(4), 9);
				BinaryPrimitives.WriteUInt32LittleEndian(ack.Slice(8), DeviceInfoAck);
				BinaryPrimitives.WriteUInt32LittleEndian(ack.Slice(12), 1);
				Send(ack, 2, PacketInband, CompletionRequested);
				_deviceInfo.Signal();
			}
			else if (message == InputReport && payload.Length >= 23)
			{
				// From inspection (MetalOS): buttons, then x and y from 0 to 32767, then the wheel
				InputEvent input = default;
				input.Type = InputEventType.Mouse;
				input.Buttons = (ushort)(payload[16] & (InputEvent.LeftButton | InputEvent.RightButton));
				input.X = BinaryPrimitives.ReadUInt16LittleEndian(payload.Slice(17));
				input.Y = BinaryPrimitives.ReadUInt16LittleEndian(payload.Slice(19));
				input.Wheel = BinaryPrimitives.ReadInt16LittleEndian(payload.Slice(21));
				Input.Post(in input);
			}
		}
	}
}
