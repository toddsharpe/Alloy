using System;
using Alloy.Abi;
using Alloy.Kernel.Ke;
using Alloy.Kernel.Mm;
using Alloy.Text;

namespace Alloy.Kernel.Platform
{
	// The Hyper-V synthetic NIC (MetalOS HyperVNic): NVSP 2 over VMBus, RNDIS inside it, 16 MB receive and send buffers
	internal sealed unsafe class SyntheticNic : VmBusChannel
	{
		private const int RingPages = 10;
		private const int BufferPages = 4096;
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

		// Send-buffer sections: 0 carries setup messages; a data frame holds one until its RNDIS completion
		private const int MaxSendSections = 64;
		private const ulong SectionTag = 0x534E440000000000;
		private const ulong SectionTagMask = 0xFFFFFF0000000000;

		private readonly KEvent _event = new KEvent(false);
		private byte* _response;
		private byte* _receiveBuffer;
		private byte* _sendBuffer;
		private uint _sectionSize;
		private int _sectionCount;
		private ulong _sectionsInUse;
		private bool _macReady;

		public static SyntheticNic Active { get; private set; }

		public readonly byte[] Mac = new byte[6];

		public static bool Start(in VmBusOffer offer)
		{
			SyntheticNic nic = new SyntheticNic();
			nic._response = (byte*)Heap.Allocate(NvspSize);
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
			byte* message = stackalloc byte[NvspSize];
			Nvsp(message, NvspInit, NvspVersion2, NvspVersion2);
			if (!Request(message, NvspInitComplete) || *(uint*)(_response + 12) != NvspSuccess)
				return false;
			Nvsp(message, NvspNdisVersion, 6, 30);
			Send(message, NvspSize, 0, PacketInband, 0);

			ulong receive = PhysicalMemory.Allocate(BufferPages);
			ulong send = PhysicalMemory.Allocate(BufferPages);
			if (receive == 0 || send == 0)
				return false;
			_receiveBuffer = (byte*)BootInfo.ToVirtual(receive);
			_sendBuffer = (byte*)BootInfo.ToVirtual(send);
			Nvsp(message, NvspReceiveBuffer, VmBus.CreateGpadl(relId, receive, BufferPages), ReceiveBufferId);
			if (!Request(message, NvspReceiveBufferComplete))
				return false;
			Nvsp(message, NvspSendBuffer, VmBus.CreateGpadl(relId, send, BufferPages), 0);
			if (!Request(message, NvspSendBufferComplete) || (_sectionSize = *(uint*)(_response + 8)) == 0)
				return false;
			_sectionCount = (int)Math.Min((ulong)BufferPages * MemoryLayout.PageSize / _sectionSize, MaxSendSections);

			uint* rndis = stackalloc uint[8];
			rndis[0] = RndisInit;
			rndis[1] = 24;
			rndis[2] = 1;
			rndis[3] = 1;
			rndis[4] = 0;
			rndis[5] = 0x4000;
			if (!RndisControl(rndis, 24, true) || !_event.Wait(2000))
				return false;

			rndis[0] = RndisSet;
			rndis[1] = 32;
			rndis[2] = 2;
			rndis[3] = OidPacketFilter;
			rndis[4] = 4;
			rndis[5] = 20;
			rndis[6] = 0;
			rndis[7] = FilterDirectedMulticastBroadcast;
			if (!RndisControl(rndis, 32, true) || !_event.Wait(2000))
				return false;

			// The answer comes back as RNDIS_MSG_QUERY_C on the control channel, not as a completion
			rndis[0] = RndisQuery;
			rndis[1] = 28;
			rndis[2] = 3;
			rndis[3] = OidPermanentAddress;
			rndis[4] = 0;
			rndis[5] = 20;
			rndis[6] = 0;
			return RndisControl(rndis, 28, false) && _event.Wait(2000) && _macReady;
		}

		// Copies one Ethernet frame into a free send section and hands it to the host; false when none is free
		public bool SendFrame(byte* frame, int length)
		{
			if (length <= 0 || length > NetFrame.Mtu || 44 + length > _sectionSize)
				return false;
			int section = AcquireSection();
			if (section == 0)
				return false;
			uint* rndis = (uint*)(_sendBuffer + (ulong)section * _sectionSize);
			new Span<byte>(rndis, 44).Clear();
			rndis[0] = RndisPacket;
			rndis[1] = (uint)(44 + length);
			rndis[2] = 36;
			rndis[3] = (uint)length;
			new ReadOnlySpan<byte>(frame, length).CopyTo(new Span<byte>((byte*)rndis + 44, length));
			if (SendRndis(ChannelData, section, (uint)(44 + length), true))
				return true;
			ReleaseSection(SectionTag | (uint)section);
			return false;
		}

		private static void Nvsp(byte* message, uint type, uint first, uint second)
		{
			new Span<byte>(message, NvspSize).Clear();
			*(uint*)message = type;
			*(uint*)(message + 4) = first;
			*(uint*)(message + 8) = second;
		}

		private bool Request(byte* message, uint answer)
		{
			_event.Reset();
			return Send(message, NvspSize, 1, PacketInband, CompletionRequested) && _event.Wait(2000) && *(uint*)_response == answer;
		}

		// Setup messages go through section 0
		private bool RndisControl(void* rndis, uint length, bool completion)
		{
			_event.Reset();
			new ReadOnlySpan<byte>(rndis, (int)length).CopyTo(new Span<byte>(_sendBuffer, (int)_sectionSize));
			return SendRndis(ChannelControl, 0, length, completion);
		}

		private bool SendRndis(uint channel, int section, uint length, bool completion)
		{
			byte* message = stackalloc byte[NvspSize];
			new Span<byte>(message, NvspSize).Clear();
			*(uint*)message = NvspRndisPacket;
			*(uint*)(message + 4) = channel;
			*(uint*)(message + 8) = (uint)section;
			*(uint*)(message + 12) = length;
			ulong transaction = section == 0 ? 0 : SectionTag | (uint)section;
			return Send(message, NvspSize, transaction, PacketInband, completion ? CompletionRequested : (ushort)0);
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

		protected override void OnPacket(VmBusPacket* packet, byte* payload, uint size)
		{
			if (size < 8)
				return;
			uint type = *(uint*)payload;
			if (packet->Type == PacketCompletion)
			{
				if (type == NvspRndisPacketComplete)
					ReleaseSection(packet->TransactionId);
				if (type == NvspInitComplete || type == NvspReceiveBufferComplete || type == NvspSendBufferComplete || type == NvspRndisPacketComplete)
				{
					new ReadOnlySpan<byte>(payload, NvspSize).CopyTo(new Span<byte>(_response, NvspSize));
					if (type != NvspRndisPacketComplete || packet->TransactionId == 0)
						_event.Signal();
				}
				return;
			}
			if (packet->Type != PacketTransferPages || type != NvspRndisPacket)
				return;

			// The transfer-page header follows the descriptor: ranges of the receive buffer, each holding one RNDIS message
			byte* transfer = (byte*)packet + sizeof(VmBusPacket);
			uint ranges = *(uint*)(transfer + 4);
			uint channel = *(uint*)(payload + 4);
			for (uint r = 0; r < ranges; r++)
			{
				uint* rndis = (uint*)(_receiveBuffer + *(uint*)(transfer + 8 + 8 * r + 4));
				if (channel == ChannelControl && rndis[0] == RndisQueryComplete)
				{
					byte* info = (byte*)(rndis + 2) + rndis[5];
					for (int i = 0; i < 6 && i < (int)rndis[4]; i++)
						Mac[i] = info[i];
					_macReady = true;
					_event.Signal();
				}
				else if (channel == ChannelData && rndis[0] == RndisPacket)
				{
					byte* frame = (byte*)(rndis + 2) + rndis[2];
					Io.Net.Receive(frame, (int)rndis[3]);
				}
			}

			byte* complete = stackalloc byte[NvspSize];
			Nvsp(complete, NvspRndisPacketComplete, NvspSuccess, 0);
			Send(complete, NvspSize, packet->TransactionId, PacketCompletion, 0);
		}
	}
}
