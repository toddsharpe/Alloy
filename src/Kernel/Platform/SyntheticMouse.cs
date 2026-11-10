using System;
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

		public static unsafe bool Start(in VmBusOffer offer)
		{
			SyntheticMouse mouse = new SyntheticMouse();
			if (!mouse.Open(in offer, RingPages, RingPages))
				return false;
			uint* request = stackalloc uint[5];
			request[0] = PipeData;
			request[1] = 12;
			request[2] = ProtocolRequest;
			request[3] = 4;
			request[4] = Version;
			if (!mouse.Send(request, 20, 1, PacketInband, CompletionRequested))
				return false;
			return mouse._response.Wait(2000) && mouse._approved && mouse._deviceInfo.Wait(2000);
		}

		// Each message is |pipe type, size|synthhid type, size|data|
		protected override unsafe void OnPacket(VmBusPacket* packet, byte* payload, uint size)
		{
			if (packet->Type != PacketInband || size < 16 || *(uint*)payload != PipeData)
				return;
			uint message = *(uint*)(payload + 8);
			if (message == ProtocolResponse && size >= 21)
			{
				_approved = payload[20] != 0;
				_response.Signal();
			}
			else if (message == DeviceInfo)
			{
				// The host holds input reports back until the device info is acknowledged
				byte* ack = stackalloc byte[17];
				new Span<byte>(ack, 17).Clear();
				*(uint*)ack = PipeData;
				*(uint*)(ack + 4) = 9;
				*(uint*)(ack + 8) = DeviceInfoAck;
				*(uint*)(ack + 12) = 1;
				Send(ack, 17, 2, PacketInband, CompletionRequested);
				_deviceInfo.Signal();
			}
			else if (message == InputReport && size >= 23)
			{
				// From inspection (MetalOS): buttons, then x and y from 0 to 32767, then the wheel
				InputEvent input = default;
				input.Type = InputEventType.Mouse;
				input.Buttons = (ushort)(payload[16] & (InputEvent.LeftButton | InputEvent.RightButton));
				input.X = *(ushort*)(payload + 17);
				input.Y = *(ushort*)(payload + 19);
				input.Wheel = *(short*)(payload + 21);
				Input.Post(in input);
			}
		}
	}
}
