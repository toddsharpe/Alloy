using System;
using System.Runtime.InteropServices;
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
		public const int Size = 16;

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

		// Called from the VMBus interrupt for each packet: its descriptor, whatever sits between the descriptor and the payload, and the payload
		protected abstract void OnPacket(in VmBusPacket packet, ReadOnlySpan<byte> header, ReadOnlySpan<byte> payload);

		// False when the host hasn't freed enough room; one byte always stays free so a full ring never looks empty
		protected bool Send(ReadOnlySpan<byte> data, ulong requestId, ushort type, ushort flags)
		{
			VmBusPacket packet;
			packet.Type = type;
			packet.Offset8 = VmBusPacket.Size >> 3;
			packet.Length8 = (ushort)(Align8((uint)(VmBusPacket.Size + data.Length)) >> 3);
			packet.Flags = flags;
			packet.TransactionId = requestId;
			Span<byte> header = stackalloc byte[VmBusPacket.Size];
			MemoryMarshal.Write(header, in packet);
			return Write(header, data);
		}

		// A packet whose data the host reads or writes in guest memory: byteCount bytes of physically contiguous pages at physical, completion requested (one gpa_range after the descriptor)
		protected bool SendGpaDirect(ReadOnlySpan<byte> data, ulong requestId, ulong physical, uint byteCount)
		{
			uint offset = (uint)(physical & (MemoryLayout.PageSize - 1));
			int pages = (int)MemoryLayout.PagesFor(offset + byteCount);
			if (pages == 0 || pages > GpaDirectMaxPages)
				return false;

			int headerSize = VmBusPacket.Size + GpaRangeHeaderSize + 8 * pages;
			Span<byte> header = stackalloc byte[VmBusPacket.Size + GpaRangeHeaderSize + 8 * GpaDirectMaxPages];
			header = header.Slice(0, headerSize);
			VmBusPacket packet;
			packet.Type = PacketGpaDirect;
			packet.Offset8 = (ushort)(headerSize >> 3);
			packet.Length8 = (ushort)(Align8((uint)(headerSize + data.Length)) >> 3);
			packet.Flags = CompletionRequested;
			packet.TransactionId = requestId;
			MemoryMarshal.Write(header, in packet);

			// Reserved, the range count, then the range's byte count, offset into its first page and page numbers
			Span<uint> range = MemoryMarshal.Cast<byte, uint>(header.Slice(VmBusPacket.Size, GpaRangeHeaderSize));
			range[0] = 0;
			range[1] = 1;
			range[2] = byteCount;
			range[3] = offset;
			Span<ulong> pfns = MemoryMarshal.Cast<byte, ulong>(header.Slice(VmBusPacket.Size + GpaRangeHeaderSize));
			for (int i = 0; i < pages; i++)
				pfns[i] = (physical >> MemoryLayout.PageShift) + (ulong)i;
			return Write(header, data);
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

			// The double mapping makes the slot contiguous even when it wraps the ring's end
			Span<byte> slot = new Span<byte>((byte*)_outbound + MemoryLayout.PageSize + write, (int)(aligned + TrailerSize));
			header.CopyTo(slot);
			payload.CopyTo(slot.Slice(header.Length));
			slot.Slice((int)packetLength, (int)(aligned - packetLength)).Clear();
			MemoryMarshal.Write(slot.Slice((int)aligned), (ulong)write << 32);

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
					// The descriptor's offset and length bound the packet; a host that breaks them has corrupted the ring
					byte* at = (byte*)_inbound + MemoryLayout.PageSize + _readIndex;
					VmBusPacket packet = MemoryMarshal.Read<VmBusPacket>(new ReadOnlySpan<byte>(at, VmBusPacket.Size));
					uint offset = (uint)packet.Offset8 << 3;
					uint length = (uint)packet.Length8 << 3;
					if (offset < VmBusPacket.Size || length < offset || length > _inboundSize)
						Environment.FailFast("FAILFAST: a VMBus packet descriptor outside its ring");
					ReadOnlySpan<byte> whole = new ReadOnlySpan<byte>(at, (int)length);
					OnPacket(in packet, whole.Slice(VmBusPacket.Size, (int)offset - VmBusPacket.Size), whole.Slice((int)offset));
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
