using System;
using Alloy.Kernel.Mm;

namespace Alloy.Kernel.Platform
{
	// One of Hyper-V's integration components (Linux hv_util): a VMBus channel of ICMSG frames, versions negotiated once, then each request answered in place
	internal abstract class IntegrationComponent : VmBusChannel
	{
		protected const int RingPages = 5;

		// A frame is a vmbuspipe_hdr (flags, size), then an icmsg_hdr (framework version, type, message version, size, status, ictype, flags), then the message
		protected const int HeaderSize = 28;
		private const int FrameMessageType = 12;
		private const int FrameMessageSize = 18;
		private const int FrameStatus = 20;
		private const int FrameFlags = 25;
		private const ushort NegotiateSize = 0x10;

		protected const ushort TypeNegotiate = 0;
		protected const ushort TypeHeartbeat = 1;
		protected const ushort TypeKvp = 2;
		protected const ushort TypeShutdown = 3;
		protected const ushort TypeTimeSync = 4;

		private const byte FlagTransaction = 1;
		private const byte FlagResponse = 4;

		// HRESULTs as the host reads them in the frame's status
		protected const uint StatusOk = 0;
		protected const uint StatusContinue = 0x80070103;
		protected const uint StatusFail = 0x80004005;

		// An ic_version is a major then a minor, each 16 bits, so a version compares as one little-endian word
		private static ReadOnlySpan<uint> FrameworkVersions => new uint[] { Version(3, 0), Version(1, 0) };

		private readonly unsafe byte* _frame;
		private readonly int _frameSize;

		protected unsafe IntegrationComponent(int frameSize = 4096)
		{
			_frameSize = frameSize;
			_frame = (byte*)Heap.Allocate((nuint)frameSize);
		}

		// The message version the host and this component agreed on, zero before that
		protected uint NegotiatedVersion { get; private set; }

		protected static uint Version(ushort major, ushort minor) => major | ((uint)minor << 16);

		// The message versions this component speaks, most preferred first
		protected abstract ReadOnlySpan<uint> MessageVersions { get; }

		// The component's own messages; the status goes back in the frame
		protected abstract unsafe uint OnMessage(ushort type, byte* body, int length);

		protected bool OpenChannel(in VmBusOffer offer) => Open(in offer, RingPages, RingPages);

		protected override unsafe void OnPacket(VmBusPacket* packet, byte* payload, uint size)
		{
			if (packet->Type != PacketInband || size < HeaderSize || size > (uint)_frameSize)
				return;

			new ReadOnlySpan<byte>(payload, (int)size).CopyTo(new Span<byte>(_frame, (int)size));
			ushort type = *(ushort*)(_frame + FrameMessageType);
			uint status = type == TypeNegotiate ? (Negotiate((int)size) ? StatusOk : StatusFail) : OnMessage(type, _frame + HeaderSize, (int)size - HeaderSize);
			if (type == TypeNegotiate)
				*(ushort*)(_frame + FrameMessageSize) = NegotiateSize;
			*(uint*)(_frame + FrameStatus) = status;
			_frame[FrameFlags] = FlagTransaction | FlagResponse;
			Send(_frame, size, packet->TransactionId, PacketInband, 0);
		}

		// The host lists the framework and message versions it speaks; the answer is one of each, the first of ours it has, or none
		private unsafe bool Negotiate(int size)
		{
			byte* body = _frame + HeaderSize;
			int frameworkCount = *(ushort*)body;
			int messageCount = *(ushort*)(body + 2);
			uint* offered = (uint*)(body + 8);
			if (HeaderSize + 8 + 4 * (frameworkCount + messageCount) > size)
				return false;

			uint framework = Pick(FrameworkVersions, offered, frameworkCount);
			uint message = Pick(MessageVersions, offered + frameworkCount, messageCount);
			bool agreed = framework != 0 && message != 0;
			*(ushort*)body = (ushort)(agreed ? 1 : 0);
			*(ushort*)(body + 2) = (ushort)(agreed ? 1 : 0);
			offered[0] = framework;
			offered[1] = message;
			if (agreed)
				NegotiatedVersion = message;
			return agreed;
		}

		private static unsafe uint Pick(ReadOnlySpan<uint> preferred, uint* offered, int count)
		{
			for (int i = 0; i < preferred.Length; i++)
			{
				for (int j = 0; j < count; j++)
				{
					if (offered[j] == preferred[i])
						return preferred[i];
				}
			}
			return 0;
		}
	}
}
