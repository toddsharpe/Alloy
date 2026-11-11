using System;
using System.Buffers.Binary;
using Alloy.Abi;
using Alloy.Kernel.Io;
using Alloy.Kernel.Ke;
using Alloy.Kernel.Mm;
using Alloy.Text;

namespace Alloy.Kernel.Platform
{
	// Hyper-V's synthetic SCSI controller (storvsc, MetalOS HyperVScsiDriver): the boot disk at target 0, LUN 0 as a block device
	internal sealed class SyntheticScsi : BlockDevice
	{
		private const int RingPages = 9;
		private const ulong TimeoutMs = 5000;

		// Transfers go through one physically contiguous buffer, the most the host takes in one request
		private const int BouncePages = 16;
		private const uint BounceSize = BouncePages * (uint)MemoryLayout.PageSize;

		// Protocol versions are major << 8 | minor; Win8's 5.1 is the first with the SRB's Win8 extension
		private const ushort VersionWin10 = 0x0602;
		private const ushort VersionWin8_1 = 0x0600;
		private const ushort VersionWin8 = 0x0501;

		// vstor_operation
		private const uint CompleteIo = 1;
		private const uint ExecuteSrb = 3;
		private const uint BeginInitialization = 7;
		private const uint EndInitialization = 8;
		private const uint QueryProtocolVersion = 9;
		private const uint QueryProperties = 10;
		private const uint RequestCompletionFlag = 1;

		// A 64-byte vstor_packet: the operation, flags and status, then the SRB (vmscsi_request), the channel properties or the version
		private const int PacketSize = 64;
		private const int Operation = 0;
		private const int Flags = 4;
		private const int Status = 8;
		private const int SrbLength = 12;
		private const int SrbStatus = 14;
		private const int ScsiStatus = 15;
		private const int CdbLength = 20;
		private const int SenseLength = 21;
		private const int DataIn = 22;
		private const int TransferLength = 24;
		private const int Cdb = 28;
		private const int SenseData = 28;
		private const int SrbFlags = 52;
		private const int SrbTimeout = 56;
		private const int MaxTransferBytes = 24;
		private const int VersionField = 12;
		private const ushort SrbSize = 52;
		private const int MaxCdb = 16;
		private const byte SenseSize = 20;

		private const uint SrbFlagsDisableSynchTransfer = 0x00000008;
		private const uint SrbFlagsDataIn = 0x00000040;
		private const uint SrbFlagsDataOut = 0x00000080;
		private const byte SrbStatusSuccess = 0x01;
		private const byte SrbStatusAutosenseValid = 0x80;
		private const byte SrbStatusMask = 0x3F;

		// The SAM status byte and the fixed-format sense key that says the device changed under us
		private const byte ScsiStatusGood = 0x00;
		private const byte ScsiStatusCheckCondition = 0x02;
		private const byte SenseKeyUnitAttention = 0x06;

		private const byte DirectionOut = 0;
		private const byte DirectionIn = 1;
		private const byte DirectionNone = 2;

		private const byte Inquiry = 0x12;
		private const byte SynchronizeCache10 = 0x35;
		private const byte Read16 = 0x88;
		private const byte Write16 = 0x8A;
		private const byte ServiceActionIn16 = 0x9E;
		private const byte ReadCapacity16 = 0x10;
		private const uint InquiryLength = 36;
		private const uint CapacityLength = 32;

		// A direct-access block device, in the low bits of INQUIRY's first byte
		private const byte PeripheralDisk = 0x00;

		private readonly Channel _channel;

		// Serializes the disk: an auto-reset event that starts signaled is a lock a waiting thread sleeps on
		private readonly KEvent _lock = new KEvent(false, true);
		private readonly KEvent _event = new KEvent(false);
		private readonly byte[] _response = new byte[PacketSize];
		private bool _waiting;
		private ulong _pending;
		private ulong _nextId = 1;

		private ulong _bouncePhysical;
		private uint _maxTransfer;

		private ushort _version;
		private uint _sectorSize;
		private ulong _sectorCount;
		private bool _ready;

		public static SyntheticScsi Active { get; private set; }

		private SyntheticScsi()
		{
			_channel = new Channel(this);
		}

		public override int SectorSize => (int)_sectorSize;

		public override ulong SectorCount => _sectorCount;

		private Span<byte> Bounce => PhysicalMemory.Window(_bouncePhysical, BounceSize);

		// True with a disk to serve at target 0, LUN 0
		public static bool Start(in VmBusOffer offer)
		{
			SyntheticScsi scsi = new SyntheticScsi();
			if (!scsi._channel.OpenRings(in offer, RingPages) || !scsi.Negotiate())
				return false;

			scsi._bouncePhysical = PhysicalMemory.Allocate(BouncePages);
			if (scsi._bouncePhysical == 0 || !scsi.Probe())
				return false;

			scsi._ready = true;
			Active = scsi;
			return true;
		}

		public override bool Read(ulong sector, int count, Span<byte> buffer)
		{
			if (!InRange(sector, count, buffer.Length))
				return false;

			_lock.Wait();
			int chunk = (int)(_maxTransfer / _sectorSize);
			bool done = true;
			for (int at = 0; done && at < count;)
			{
				int sectors = Math.Min(chunk, count - at);
				done = Transfer(Read16, sector + (ulong)at, (uint)sectors, DirectionIn);
				if (done)
				{
					Bounce.Slice(0, sectors * (int)_sectorSize).CopyTo(buffer.Slice(at * (int)_sectorSize));
					at += sectors;
				}
			}
			_lock.Signal();
			return done;
		}

		public override bool Write(ulong sector, int count, ReadOnlySpan<byte> buffer)
		{
			if (!InRange(sector, count, buffer.Length))
				return false;

			_lock.Wait();
			int chunk = (int)(_maxTransfer / _sectorSize);
			bool done = true;
			for (int at = 0; done && at < count;)
			{
				int sectors = Math.Min(chunk, count - at);
				buffer.Slice(at * (int)_sectorSize, sectors * (int)_sectorSize).CopyTo(Bounce);
				done = Transfer(Write16, sector + (ulong)at, (uint)sectors, DirectionOut);
				at += sectors;
			}
			_lock.Signal();
			return done;
		}

		public override bool Flush()
		{
			if (!_ready)
				return false;

			_lock.Wait();
			Span<byte> cdb = stackalloc byte[10];
			cdb.Clear();
			cdb[0] = SynchronizeCache10;
			bool done = Command(cdb, DirectionNone, 0);
			_lock.Signal();
			return done;
		}

		private bool InRange(ulong sector, int count, int bufferLength) =>
			_ready && count >= 0 && (ulong)count <= _sectorCount && sector <= _sectorCount - (ulong)count && (long)bufferLength >= (long)count * _sectorSize;

		// Begin, a protocol version both sides speak, the channel's properties, end
		private bool Negotiate()
		{
			if (!Operate(BeginInitialization))
			{
				Log.WriteLine("SyntheticScsi: no answer to BEGIN_INITIALIZATION");
				return false;
			}

			// Newest first; a version the host rejects completes with a nonzero status
			Span<byte> packet = stackalloc byte[PacketSize];
			for (int i = 0; i < 3 && _version == 0; i++)
			{
				ushort version = i == 0 ? VersionWin10 : i == 1 ? VersionWin8_1 : VersionWin8;
				packet.Clear();
				BinaryPrimitives.WriteUInt32LittleEndian(packet.Slice(Operation), QueryProtocolVersion);
				BinaryPrimitives.WriteUInt32LittleEndian(packet.Slice(Flags), RequestCompletionFlag);
				BinaryPrimitives.WriteUInt16LittleEndian(packet.Slice(VersionField), version);
				if (!Execute(packet, 0))
				{
					Log.WriteLine("SyntheticScsi: no answer to QUERY_PROTOCOL_VERSION");
					return false;
				}
				if (Response(Operation) == CompleteIo && Response(Status) == 0)
					_version = version;
			}
			if (_version == 0)
			{
				Log.WriteLine("SyntheticScsi: host rejected protocol versions 6.2 to 5.1");
				return false;
			}

			packet.Clear();
			BinaryPrimitives.WriteUInt32LittleEndian(packet.Slice(Operation), QueryProperties);
			BinaryPrimitives.WriteUInt32LittleEndian(packet.Slice(Flags), RequestCompletionFlag);
			if (!Execute(packet, 0) || Response(Operation) != CompleteIo || Response(Status) != 0)
			{
				Log.WriteLine("SyntheticScsi: no answer to QUERY_PROPERTIES");
				return false;
			}

			// The host's limit on one request, or the bounce buffer's where that is smaller or the host names none
			uint hostMax = Response(MaxTransferBytes);
			_maxTransfer = hostMax == 0 || hostMax > BounceSize ? BounceSize : hostMax;

			if (!Operate(EndInitialization))
			{
				Log.WriteLine("SyntheticScsi: no answer to END_INITIALIZATION");
				return false;
			}
			return true;
		}

		// The disk at target 0, LUN 0: a direct-access device, its capacity, and a look at its partition table
		private bool Probe()
		{
			Span<byte> cdb = stackalloc byte[MaxCdb];
			cdb.Clear();
			cdb[0] = Inquiry;
			cdb[4] = (byte)InquiryLength;
			if (!Command(cdb.Slice(0, 6), DirectionIn, InquiryLength) || (Bounce[0] & 0x1F) != PeripheralDisk)
			{
				Log.WriteLine("SyntheticScsi: no disk at target 0, LUN 0");
				return false;
			}

			// The last sector and the sector size, big-endian
			cdb.Clear();
			cdb[0] = ServiceActionIn16;
			cdb[1] = ReadCapacity16;
			cdb[13] = (byte)CapacityLength;
			if (!Command(cdb, DirectionIn, CapacityLength))
			{
				Log.WriteLine("SyntheticScsi: READ CAPACITY failed");
				return false;
			}

			ReadOnlySpan<byte> capacity = Bounce;
			ulong last = 0;
			for (int i = 0; i < 8; i++)
				last = (last << 8) | capacity[i];
			_sectorCount = last + 1;
			_sectorSize = ((uint)capacity[8] << 24) | ((uint)capacity[9] << 16) | ((uint)capacity[10] << 8) | capacity[11];
			if (_sectorSize == 0 || BounceSize % _sectorSize != 0 || _maxTransfer < _sectorSize)
			{
				Log.WriteLine("SyntheticScsi: the sector size doesn't fit the transfer size");
				return false;
			}

			// The boot disk is GPT, whose header in sector 1 starts with its signature
			bool gpt = Transfer(Read16, 1, 1, DirectionIn) && IsGptSignature(Bounce);
			SpanWriter line = new SpanWriter(stackalloc char[128]);
			line.Append("SyntheticScsi: protocol ");
			line.Append((uint)(_version >> 8));
			line.Append('.');
			line.Append((uint)(_version & 0xFF));
			line.Append(", disk 0: ");
			line.Append((_sectorCount * _sectorSize) >> 20);
			line.Append(" MB in ");
			line.Append(_sectorSize);
			line.Append("-byte sectors");
			line.Append(gpt ? ", GPT" : "");
			Log.WriteLine(ref line);
			return true;
		}

		private static bool IsGptSignature(ReadOnlySpan<byte> sector) =>
			sector[0] == 'E' && sector[1] == 'F' && sector[2] == 'I' && sector[3] == ' ' && sector[4] == 'P' && sector[5] == 'A' && sector[6] == 'R' && sector[7] == 'T';

		// Sends the packet and waits for its completion in the response, with dmaBytes of the bounce buffer as its data; false on no answer
		private bool Execute(ReadOnlySpan<byte> packet, uint dmaBytes)
		{
			ulong flags = Scheduler.Lock();
			_event.Reset();
			_pending = _nextId++;
			_response.AsSpan().Clear();
			_waiting = true;
			Scheduler.Unlock(flags);

			bool sent = dmaBytes == 0 ?
				_channel.Post(packet, _pending) :
				_channel.PostGpaDirect(packet, _pending, _bouncePhysical, dmaBytes);
			bool answered = sent && _event.Wait(TimeoutMs);

			flags = Scheduler.Lock();
			_waiting = false;
			Scheduler.Unlock(flags);
			return answered;
		}

		private bool Operate(uint operation)
		{
			Span<byte> packet = stackalloc byte[PacketSize];
			packet.Clear();
			BinaryPrimitives.WriteUInt32LittleEndian(packet.Slice(Operation), operation);
			BinaryPrimitives.WriteUInt32LittleEndian(packet.Slice(Flags), RequestCompletionFlag);
			return Execute(packet, 0) && Response(Operation) == CompleteIo && Response(Status) == 0;
		}

		// One SCSI command to the disk, its data in the bounce buffer; false unless it succeeds
		private bool Command(ReadOnlySpan<byte> cdb, byte direction, uint bytes)
		{
			if (cdb.Length > MaxCdb || bytes > BounceSize)
				return false;

			// A disk reports a unit attention once after it changes or first comes up, so the command goes again
			Span<byte> packet = stackalloc byte[PacketSize];
			for (int attempt = 0; attempt < 3; attempt++)
			{
				packet.Clear();
				BinaryPrimitives.WriteUInt32LittleEndian(packet.Slice(Operation), ExecuteSrb);
				BinaryPrimitives.WriteUInt32LittleEndian(packet.Slice(Flags), RequestCompletionFlag);
				BinaryPrimitives.WriteUInt16LittleEndian(packet.Slice(SrbLength), SrbSize);
				packet[CdbLength] = (byte)cdb.Length;
				packet[SenseLength] = SenseSize;
				packet[DataIn] = direction;
				BinaryPrimitives.WriteUInt32LittleEndian(packet.Slice(TransferLength), bytes);
				cdb.CopyTo(packet.Slice(Cdb, MaxCdb));
				BinaryPrimitives.WriteUInt32LittleEndian(packet.Slice(SrbFlags), SrbFlagsDisableSynchTransfer | (direction == DirectionIn ? SrbFlagsDataIn : direction == DirectionOut ? SrbFlagsDataOut : 0));
				BinaryPrimitives.WriteUInt32LittleEndian(packet.Slice(SrbTimeout), 60);

				if (!Execute(packet, bytes))
					return false;

				if (Response(Status) == 0 && (_response[SrbStatus] & SrbStatusMask) == SrbStatusSuccess && _response[ScsiStatus] == ScsiStatusGood)
					return true;

				bool unitAttention = _response[ScsiStatus] == ScsiStatusCheckCondition && (_response[SrbStatus] & SrbStatusAutosenseValid) != 0 &&
					(_response[SenseData + 2] & 0x0F) == SenseKeyUnitAttention;
				if (!unitAttention)
					return false;
			}
			return false;
		}

		// READ(16) or WRITE(16): count sectors at sector, big-endian, through the bounce buffer
		private bool Transfer(byte opcode, ulong sector, uint count, byte direction)
		{
			Span<byte> cdb = stackalloc byte[MaxCdb];
			cdb.Clear();
			cdb[0] = opcode;
			for (int i = 0; i < 8; i++)
				cdb[2 + i] = (byte)(sector >> (56 - 8 * i));
			for (int i = 0; i < 4; i++)
				cdb[10 + i] = (byte)(count >> (24 - 8 * i));

			uint bytes = count * _sectorSize;
			return Command(cdb, direction, bytes) && Response(TransferLength) == bytes;
		}

		private uint Response(int offset) => BinaryPrimitives.ReadUInt32LittleEndian(_response.AsSpan(offset));

		private void OnPacket(in VmBusPacket packet, ReadOnlySpan<byte> payload)
		{
			// Only the completion a request is waiting for counts; the host's bus notifications and answers that came too late are dropped
			if (packet.Type != VmBusChannel.PacketCompletion || !_waiting || packet.TransactionId != _pending)
				return;

			payload.Slice(0, Math.Min(payload.Length, PacketSize)).CopyTo(_response);
			_waiting = false;
			_event.Signal();
		}

		// The controller's VMBus channel; its packets go to the driver
		private sealed class Channel : VmBusChannel
		{
			private readonly SyntheticScsi _owner;

			public Channel(SyntheticScsi owner)
			{
				_owner = owner;
			}

			public bool OpenRings(in VmBusOffer offer, int pages) => Open(in offer, pages, pages);

			public bool Post(ReadOnlySpan<byte> data, ulong requestId) => Send(data, requestId, PacketInband, CompletionRequested);

			public bool PostGpaDirect(ReadOnlySpan<byte> data, ulong requestId, ulong physical, uint byteCount) => SendGpaDirect(data, requestId, physical, byteCount);

			protected override void OnPacket(in VmBusPacket packet, ReadOnlySpan<byte> header, ReadOnlySpan<byte> payload) => _owner.OnPacket(in packet, payload);
		}
	}
}
