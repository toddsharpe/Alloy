using System;
using System.Buffers.Binary;

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

		private readonly byte[] _frame;

		protected IntegrationComponent(int frameSize = 4096)
		{
			_frame = new byte[frameSize];
		}

		// The message version the host and this component agreed on, zero before that
		protected uint NegotiatedVersion { get; private set; }

		protected static uint Version(ushort major, ushort minor) => major | ((uint)minor << 16);

		// The message versions this component speaks, most preferred first
		protected abstract ReadOnlySpan<uint> MessageVersions { get; }

		// The component's own messages, answered in place in the body; the status goes back in the frame
		protected abstract uint OnMessage(ushort type, Span<byte> body);

		protected bool OpenChannel(in VmBusOffer offer) => Open(in offer, RingPages, RingPages);

		protected override void OnPacket(in VmBusPacket packet, ReadOnlySpan<byte> header, ReadOnlySpan<byte> payload)
		{
			if (packet.Type != PacketInband || payload.Length < HeaderSize || payload.Length > _frame.Length)
				return;

			Span<byte> frame = _frame.AsSpan(0, payload.Length);
			payload.CopyTo(frame);
			ushort type = BinaryPrimitives.ReadUInt16LittleEndian(frame.Slice(FrameMessageType));
			uint status = type == TypeNegotiate ? (Negotiate(frame.Slice(HeaderSize)) ? StatusOk : StatusFail) : OnMessage(type, frame.Slice(HeaderSize));
			if (type == TypeNegotiate)
				BinaryPrimitives.WriteUInt16LittleEndian(frame.Slice(FrameMessageSize), NegotiateSize);
			BinaryPrimitives.WriteUInt32LittleEndian(frame.Slice(FrameStatus), status);
			frame[FrameFlags] = FlagTransaction | FlagResponse;
			Send(frame, packet.TransactionId, PacketInband, 0);
		}

		// The host lists the framework and message versions it speaks; the answer is one of each, the first of ours it has, or none
		private bool Negotiate(Span<byte> body)
		{
			if (body.Length < 8)
				return false;
			int frameworkCount = BinaryPrimitives.ReadUInt16LittleEndian(body);
			int messageCount = BinaryPrimitives.ReadUInt16LittleEndian(body.Slice(2));
			if (8 + 4 * (frameworkCount + messageCount) > body.Length)
				return false;

			Span<byte> offered = body.Slice(8);
			uint framework = Pick(FrameworkVersions, offered.Slice(0, 4 * frameworkCount));
			uint message = Pick(MessageVersions, offered.Slice(4 * frameworkCount, 4 * messageCount));
			bool agreed = framework != 0 && message != 0;
			BinaryPrimitives.WriteUInt16LittleEndian(body, (ushort)(agreed ? 1 : 0));
			BinaryPrimitives.WriteUInt16LittleEndian(body.Slice(2), (ushort)(agreed ? 1 : 0));
			BinaryPrimitives.WriteUInt32LittleEndian(offered, framework);
			BinaryPrimitives.WriteUInt32LittleEndian(offered.Slice(4), message);
			if (agreed)
				NegotiatedVersion = message;
			return agreed;
		}

		private static uint Pick(ReadOnlySpan<uint> preferred, ReadOnlySpan<byte> offered)
		{
			for (int i = 0; i < preferred.Length; i++)
			{
				for (int j = 0; j + 4 <= offered.Length; j += 4)
				{
					if (BinaryPrimitives.ReadUInt32LittleEndian(offered.Slice(j)) == preferred[i])
						return preferred[i];
				}
			}
			return 0;
		}
	}
}
