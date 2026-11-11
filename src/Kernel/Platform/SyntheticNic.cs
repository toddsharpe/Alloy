using System;
using System.Buffers.Binary;
using Alloy.Abi;
using Alloy.Kernel.Ke;
using Alloy.Kernel.Mm;
using Alloy.Text;

namespace Alloy.Kernel.Platform
{
	// The Hyper-V synthetic NIC (MetalOS HyperVNic): NVSP 2 over VMBus, RNDIS inside it, 16 MB receive and send buffers
	internal sealed class SyntheticNic : VmBusChannel
	{
		private const int RingPages = 10;
		private const int BufferPages = 4096;
		private const ulong BufferBytes = BufferPages * MemoryLayout.PageSize;
		private const ushort ReceiveBufferId = 0xCAFE;
		private const uint NvspVersion2 = 0x30002;
		private const uint NvspSuccess = 1;
		private const uint ChannelData = 0;
		private const uint ChannelControl = 1;
		private const int NvspSize = 28;

		private const uint NvspInit = 1;
		private const uint NvspInitComplete = 2;
		private const uint NvspNdisVersion = 100;
		private const uint NvspReceiveBuffer = 101;
		private const uint NvspReceiveBufferComplete = 102;
		private const uint NvspSendBuffer = 104;
		private const uint NvspSendBufferComplete = 105;
		private const uint NvspRndisPacket = 107;
		private const uint NvspRndisPacketComplete = 108;

		private const uint RndisPacket = 1;
		private const uint RndisInit = 2;
		private const uint RndisQuery = 4;
		private const uint RndisSet = 5;
		private const uint RndisQueryComplete = 0x80000004;
		private const uint OidPermanentAddress = 0x01010101;
		private const uint OidPacketFilter = 0x0001010E;
		private const uint FilterDirectedMulticastBroadcast = 0x1 | 0x4 | 0x8;

		// An RNDIS data message: type, length, then the data's offset from the field after length and its length
		private const int RndisHeaderSize = 44;

		// Send-buffer sections: 0 carries setup messages; a data frame holds one until its RNDIS completion
		private const int MaxSendSections = 64;
		private const ulong SectionTag = 0x534E440000000000;
		private const ulong SectionTagMask = 0xFFFFFF0000000000;

		private readonly KEvent _event = new KEvent(false);
		private readonly byte[] _response = new byte[NvspSize];
		private ulong _receivePhysical;
		private ulong _sendPhysical;
		private uint _sectionSize;
		private int _sectionCount;
		private ulong _sectionsInUse;
		private bool _macReady;

		public static SyntheticNic Active { get; private set; }

		public readonly byte[] Mac = new byte[6];

		private Span<byte> ReceiveBuffer => PhysicalMemory.Window(_receivePhysical, BufferBytes);

		private Span<byte> SendBuffer => PhysicalMemory.Window(_sendPhysical, BufferBytes);

		public static bool Start(in VmBusOffer offer)
		{
			SyntheticNic nic = new SyntheticNic();
			if (!nic.Open(in offer, RingPages, RingPages) || !nic.Negotiate(offer.RelId))
				return false;
			Active = nic;

			SpanWriter line = new SpanWriter(stackalloc char[64]);
			line.Append("SyntheticNic: MAC ");
			for (int i = 0; i < 6; i++)
			{
				if (i != 0)
					line.Append(':');
				line.AppendHex(nic.Mac[i], 2);
			}
			line.Append(", ");
			line.Append(nic._sectionCount);
			line.Append(" send sections of ");
			line.Append(nic._sectionSize);
			Log.WriteLine(ref line);
			return true;
		}

		// NVSP init and NDIS version, the receive and send buffers, then RNDIS init, the packet filter and the MAC
		private bool Negotiate(uint relId)
		{
			Span<byte> message = stackalloc byte[NvspSize];
			Nvsp(message, NvspInit, NvspVersion2, NvspVersion2);
			if (!Request(message, NvspInitComplete) || BinaryPrimitives.ReadUInt32LittleEndian(_response.AsSpan(12)) != NvspSuccess)
				return false;
			Nvsp(message, NvspNdisVersion, 6, 30);
			Send(message, 0, PacketInband, 0);

			_receivePhysical = PhysicalMemory.Allocate(BufferPages);
			_sendPhysical = PhysicalMemory.Allocate(BufferPages);
			if (_receivePhysical == 0 || _sendPhysical == 0)
				return false;
			Nvsp(message, NvspReceiveBuffer, VmBus.CreateGpadl(relId, _receivePhysical, BufferPages), ReceiveBufferId);
			if (!Request(message, NvspReceiveBufferComplete))
				return false;
			Nvsp(message, NvspSendBuffer, VmBus.CreateGpadl(relId, _sendPhysical, BufferPages), 0);
			if (!Request(message, NvspSendBufferComplete) || (_sectionSize = BinaryPrimitives.ReadUInt32LittleEndian(_response.AsSpan(8))) == 0)
				return false;
			_sectionCount = (int)Math.Min(BufferBytes / _sectionSize, MaxSendSections);

			Span<uint> rndis = stackalloc uint[8];
			rndis[0] = RndisInit;
			rndis[1] = 24;
			rndis[2] = 1;
			rndis[3] = 1;
			rndis[4] = 0;
			rndis[5] = 0x4000;
			if (!RndisControl(rndis.Slice(0, 6), true) || !_event.Wait(2000))
				return false;

			rndis[0] = RndisSet;
			rndis[1] = 32;
			rndis[2] = 2;
			rndis[3] = OidPacketFilter;
			rndis[4] = 4;
			rndis[5] = 20;
			rndis[6] = 0;
			rndis[7] = FilterDirectedMulticastBroadcast;
			if (!RndisControl(rndis, true) || !_event.Wait(2000))
				return false;

			// The answer comes back as RNDIS_MSG_QUERY_C on the control channel, not as a completion
			rndis[0] = RndisQuery;
			rndis[1] = 28;
			rndis[2] = 3;
			rndis[3] = OidPermanentAddress;
			rndis[4] = 0;
			rndis[5] = 20;
			rndis[6] = 0;
			return RndisControl(rndis.Slice(0, 7), false) && _event.Wait(2000) && _macReady;
		}

		// Copies one Ethernet frame into a free send section and hands it to the host; false when none is free
		public bool SendFrame(ReadOnlySpan<byte> frame)
		{
			if (frame.Length <= 0 || frame.Length > NetFrame.Mtu || RndisHeaderSize + frame.Length > _sectionSize)
				return false;
			int section = AcquireSection();
			if (section == 0)
				return false;
			Span<byte> slot = SendBuffer.Slice(section * (int)_sectionSize, (int)_sectionSize);
			slot.Slice(0, RndisHeaderSize).Clear();
			BinaryPrimitives.WriteUInt32LittleEndian(slot, RndisPacket);
			BinaryPrimitives.WriteUInt32LittleEndian(slot.Slice(4), (uint)(RndisHeaderSize + frame.Length));
			BinaryPrimitives.WriteUInt32LittleEndian(slot.Slice(8), RndisHeaderSize - 8);
			BinaryPrimitives.WriteUInt32LittleEndian(slot.Slice(12), (uint)frame.Length);
			frame.CopyTo(slot.Slice(RndisHeaderSize));
			if (SendRndis(ChannelData, section, (uint)(RndisHeaderSize + frame.Length), true))
				return true;
			ReleaseSection(SectionTag | (uint)section);
			return false;
		}

		private static void Nvsp(Span<byte> message, uint type, uint first, uint second)
		{
			message.Clear();
			BinaryPrimitives.WriteUInt32LittleEndian(message, type);
			BinaryPrimitives.WriteUInt32LittleEndian(message.Slice(4), first);
			BinaryPrimitives.WriteUInt32LittleEndian(message.Slice(8), second);
		}

		private bool Request(ReadOnlySpan<byte> message, uint answer)
		{
			_event.Reset();
			return Send(message, 1, PacketInband, CompletionRequested) && _event.Wait(2000) && BinaryPrimitives.ReadUInt32LittleEndian(_response) == answer;
		}

		// Setup messages go through section 0
		private bool RndisControl(ReadOnlySpan<uint> rndis, bool completion)
		{
			_event.Reset();
			Span<byte> section = SendBuffer.Slice(0, (int)_sectionSize);
			for (int i = 0; i < rndis.Length; i++)
				BinaryPrimitives.WriteUInt32LittleEndian(section.Slice(4 * i), rndis[i]);
			return SendRndis(ChannelControl, 0, (uint)(4 * rndis.Length), completion);
		}

		private bool SendRndis(uint channel, int section, uint length, bool completion)
		{
			Span<byte> message = stackalloc byte[NvspSize];
			message.Clear();
			BinaryPrimitives.WriteUInt32LittleEndian(message, NvspRndisPacket);
			BinaryPrimitives.WriteUInt32LittleEndian(message.Slice(4), channel);
			BinaryPrimitives.WriteUInt32LittleEndian(message.Slice(8), (uint)section);
			BinaryPrimitives.WriteUInt32LittleEndian(message.Slice(12), length);
			ulong transaction = section == 0 ? 0 : SectionTag | (uint)section;
			return Send(message, transaction, PacketInband, completion ? CompletionRequested : (ushort)0);
		}

		private int AcquireSection()
		{
			ulong flags = Scheduler.Lock();
			int section = 0;
			for (int i = 1; i < _sectionCount; i++)
			{
				if ((_sectionsInUse & (1UL << i)) == 0)
				{
					_sectionsInUse |= 1UL << i;
					section = i;
					break;
				}
			}
			Scheduler.Unlock(flags);
			return section;
		}

		private void ReleaseSection(ulong transaction)
		{
			if ((transaction & SectionTagMask) != SectionTag)
				return;
			int section = (int)(transaction & ~SectionTagMask);
			ulong flags = Scheduler.Lock();
			if (section > 0 && section < _sectionCount)
				_sectionsInUse &= ~(1UL << section);
			Scheduler.Unlock(flags);
		}

		protected override void OnPacket(in VmBusPacket packet, ReadOnlySpan<byte> header, ReadOnlySpan<byte> payload)
		{
			if (payload.Length < 8)
				return;
			uint type = BinaryPrimitives.ReadUInt32LittleEndian(payload);
			if (packet.Type == PacketCompletion)
			{
				if (type == NvspRndisPacketComplete)
					ReleaseSection(packet.TransactionId);
				if (type == NvspInitComplete || type == NvspReceiveBufferComplete || type == NvspSendBufferComplete || type == NvspRndisPacketComplete)
				{
					payload.Slice(0, Math.Min(payload.Length, NvspSize)).CopyTo(_response);
					if (type != NvspRndisPacketComplete || packet.TransactionId == 0)
						_event.Signal();
				}
				return;
			}
			if (packet.Type != PacketTransferPages || type != NvspRndisPacket || header.Length < 8)
				return;

			// The transfer-page header follows the descriptor: a range count, then ranges of the receive buffer, each holding one RNDIS message
			uint ranges = BinaryPrimitives.ReadUInt32LittleEndian(header.Slice(4));
			uint channel = BinaryPrimitives.ReadUInt32LittleEndian(payload.Slice(4));
			ReadOnlySpan<byte> receive = ReceiveBuffer;
			for (int r = 0; r < ranges && 8 + 8 * r + 8 <= header.Length; r++)
			{
				int count = (int)BinaryPrimitives.ReadUInt32LittleEndian(header.Slice(8 + 8 * r));
				int offset = (int)BinaryPrimitives.ReadUInt32LittleEndian(header.Slice(8 + 8 * r + 4));
				if (count < 8 || offset < 0 || (long)offset + count > receive.Length)
					continue;
				ReadOnlySpan<byte> rndis = receive.Slice(offset, count);
				uint message = BinaryPrimitives.ReadUInt32LittleEndian(rndis);
				if (channel == ChannelControl && message == RndisQueryComplete && rndis.Length >= 24)
				{
					// The information buffer's length and its offset from the request id field
					int length = (int)BinaryPrimitives.ReadUInt32LittleEndian(rndis.Slice(16));
					int at = 8 + (int)BinaryPrimitives.ReadUInt32LittleEndian(rndis.Slice(20));
					ReadOnlySpan<byte> info = rndis.Slice(at, Math.Min(length, rndis.Length - at));
					for (int i = 0; i < 6 && i < info.Length; i++)
						Mac[i] = info[i];
					_macReady = true;
					_event.Signal();
				}
				else if (channel == ChannelData && message == RndisPacket && rndis.Length >= 16)
				{
					int at = 8 + (int)BinaryPrimitives.ReadUInt32LittleEndian(rndis.Slice(8));
					int length = (int)BinaryPrimitives.ReadUInt32LittleEndian(rndis.Slice(12));
					if (at >= 0 && length >= 0 && (long)at + length <= rndis.Length)
						Io.Net.Receive(rndis.Slice(at, length));
				}
			}

			Span<byte> complete = stackalloc byte[NvspSize];
			Nvsp(complete, NvspRndisPacketComplete, NvspSuccess, 0);
			Send(complete, packet.TransactionId, PacketCompletion, 0);
		}
	}
}
