using System;
using System.Threading;
using Alloy.Abi;
using Alloy.Arch.X64;
using Alloy.Kernel.Mm;

namespace Alloy.Kernel.Platform
{
	// The page at the start of each ring (hv_ring_buffer); the ring's data follows it
	internal struct VmBusRingHeader
	{
		public volatile uint WriteIndex;
		public volatile uint ReadIndex;
		public volatile uint InterruptMask;
		public volatile uint PendingSendSize;
		public unsafe fixed uint Reserved[12];
		public uint FeatureBits;
	}

	// vmpacket_descriptor: offset and length in 8-byte units, both from the packet's start
	internal struct VmBusPacket
	{
		public ushort Type;
		public ushort Offset8;
		public ushort Length8;
		public ushort Flags;
		public ulong TransactionId;
	}

	// One open channel (MetalOS HyperVChannel + HyperVRingBuffer): an outbound and an inbound ring in one GPADL
	internal abstract class VmBusChannel
	{
		public const ushort PacketInband = 6;
		public const ushort PacketTransferPages = 7;
		public const ushort PacketGpaDirect = 9;
		public const ushort PacketCompletion = 11;
		public const ushort CompletionRequested = 1;
		public const int GpaDirectMaxPages = 32;
		private const uint TrailerSize = 8;
		private const int GpaRangeHeaderSize = 16;

		private VmBusOffer _offer;
		private unsafe VmBusRingHeader* _outbound;
		private unsafe VmBusRingHeader* _inbound;
		private uint _outboundSize;
		private uint _inboundSize;
		private uint _readIndex;

		// Each ring is mapped header, data, then the data again, so a packet that wraps still reads and writes in one piece
		protected unsafe bool Open(in VmBusOffer offer, int sendPages, int receivePages)
		{
			_offer = offer;
			ulong physical = PhysicalMemory.Allocate((ulong)(sendPages + receivePages));
			if (physical == 0)
				return false;
			_outbound = MapRing(physical, sendPages);
			_inbound = MapRing(physical + ((ulong)sendPages << MemoryLayout.PageShift), receivePages);
			_outboundSize = (uint)(sendPages - 1) << MemoryLayout.PageShift;
			_inboundSize = (uint)(receivePages - 1) << MemoryLayout.PageShift;
			_outbound->FeatureBits = 1;
			_inbound->FeatureBits = 1;

			uint gpadl = VmBus.CreateGpadl(offer.RelId, physical, sendPages + receivePages);
			return gpadl != 0 && VmBus.OpenChannel(this, offer.RelId, gpadl, (uint)sendPages);
		}

		// Called from the VMBus interrupt for each packet; payload and size exclude the descriptor and anything between it and the payload
		protected abstract unsafe void OnPacket(VmBusPacket* packet, byte* payload, uint size);

		// False when the host hasn't freed enough room; one byte always stays free so a full ring never looks empty
		protected unsafe bool Send(void* data, uint length, ulong requestId, ushort type, ushort flags)
		{
			VmBusPacket packet;
			packet.Type = type;
			packet.Offset8 = (ushort)(sizeof(VmBusPacket) >> 3);
			packet.Length8 = (ushort)(Align8((uint)sizeof(VmBusPacket) + length) >> 3);
			packet.Flags = flags;
			packet.TransactionId = requestId;
			return Write(new ReadOnlySpan<byte>(&packet, sizeof(VmBusPacket)), new ReadOnlySpan<byte>(data, (int)length));
		}

		// A packet whose data the host reads or writes in guest memory: byteCount bytes of physically contiguous pages at physical, completion requested (one gpa_range after the descriptor)
		protected unsafe bool SendGpaDirect(void* data, uint length, ulong requestId, ulong physical, uint byteCount)
		{
			uint offset = (uint)(physical & (MemoryLayout.PageSize - 1));
			int pages = (int)MemoryLayout.PagesFor(offset + byteCount);
			if (pages == 0 || pages > GpaDirectMaxPages)
				return false;

			int headerSize = sizeof(VmBusPacket) + GpaRangeHeaderSize + 8 * pages;
			byte* header = stackalloc byte[sizeof(VmBusPacket) + GpaRangeHeaderSize + 8 * GpaDirectMaxPages];
			VmBusPacket* packet = (VmBusPacket*)header;
			packet->Type = PacketGpaDirect;
			packet->Offset8 = (ushort)(headerSize >> 3);
			packet->Length8 = (ushort)(Align8((uint)headerSize + length) >> 3);
			packet->Flags = CompletionRequested;
			packet->TransactionId = requestId;

			// Reserved, the range count, then the range's byte count, offset into its first page and page numbers
			uint* range = (uint*)(header + sizeof(VmBusPacket));
			range[0] = 0;
			range[1] = 1;
			range[2] = byteCount;
			range[3] = offset;
			ulong* pfns = (ulong*)(header + sizeof(VmBusPacket) + GpaRangeHeaderSize);
			for (int i = 0; i < pages; i++)
				pfns[i] = (physical >> MemoryLayout.PageShift) + (ulong)i;
			return Write(new ReadOnlySpan<byte>(header, headerSize), new ReadOnlySpan<byte>(data, (int)length));
		}

		private static uint Align8(uint length) => (length + 7) & ~7u;

		// The descriptor and whatever follows it, then the payload, padded to 8 bytes and followed by the write index the host expects
		private unsafe bool Write(ReadOnlySpan<byte> header, ReadOnlySpan<byte> payload)
		{
			uint packetLength = (uint)(header.Length + payload.Length);
			uint aligned = Align8(packetLength);
			ulong state = Ke.Scheduler.Lock();
			uint write = _outbound->WriteIndex;
			uint read = _outbound->ReadIndex;
			uint available = write >= read ? _outboundSize - (write - read) : read - write;
			if (available <= aligned + TrailerSize)
			{
				Ke.Scheduler.Unlock(state);
				return false;
			}

			byte* at = (byte*)_outbound + MemoryLayout.PageSize + write;
			header.CopyTo(new Span<byte>(at, header.Length));
			payload.CopyTo(new Span<byte>(at + header.Length, payload.Length));
			new Span<byte>(at + packetLength, (int)(aligned - packetLength)).Clear();
			*(ulong*)(at + aligned) = (ulong)write << 32;

			Interlocked.MemoryBarrier();
			_outbound->WriteIndex = (write + aligned + TrailerSize) % _outboundSize;
			Interlocked.MemoryBarrier();
			if (_outbound->InterruptMask == 0 && write == _outbound->ReadIndex)
				Signal();
			Ke.Scheduler.Unlock(state);
			return true;
		}

		// Drains the inbound ring, then looks again after publishing the read index: the host skips its interrupt while the ring is non-empty
		internal unsafe void OnInterrupt()
		{
			do
			{
				while (_readIndex != _inbound->WriteIndex)
				{
					VmBusPacket* packet = (VmBusPacket*)((byte*)_inbound + MemoryLayout.PageSize + _readIndex);
					uint offset = (uint)packet->Offset8 << 3;
					uint length = (uint)packet->Length8 << 3;
					OnPacket(packet, (byte*)packet + offset, length - offset);
					_readIndex = (_readIndex + length + TrailerSize) % _inboundSize;
				}
				Interlocked.MemoryBarrier();
				_inbound->ReadIndex = _readIndex;
				Interlocked.MemoryBarrier();
				if (_inbound->PendingSendSize != 0)
					Signal();
			}
			while (_readIndex != _inbound->WriteIndex);
		}

		private void Signal() => HyperV.SignalEvent(_offer.ConnectionId, 0);

		private static unsafe VmBusRingHeader* MapRing(ulong physical, int pages)
		{
			ulong address = KernelSpace.AllocateVirtual((ulong)(2 * pages - 1));
			const PageFlags Flags = PageFlags.Writable | PageFlags.Global | PageFlags.NoExecute;
			KernelSpace.Map(address, physical, MemoryLayout.PageSize, Flags);
			for (int i = 1; i < pages; i++)
			{
				ulong page = physical + ((ulong)i << MemoryLayout.PageShift);
				KernelSpace.Map(address + ((ulong)i << MemoryLayout.PageShift), page, MemoryLayout.PageSize, Flags);
				KernelSpace.Map(address + ((ulong)(pages - 1 + i) << MemoryLayout.PageShift), page, MemoryLayout.PageSize, Flags);
			}
			return (VmBusRingHeader*)address;
		}
	}
}
