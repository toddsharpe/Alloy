using System;
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
		private unsafe byte* _response;
		private bool _waiting;
		private ulong _pending;
		private ulong _nextId = 1;

		private ulong _bouncePhysical;
		private unsafe byte* _bounce;
		private uint _maxTransfer;

		private ushort _version;
		private uint _sectorSize;
		private ulong _sectorCount;
		private bool _ready;

		public static SyntheticScsi Active { get; private set; }

		private unsafe SyntheticScsi()
		{
			_channel = new Channel(this);
			_response = (byte*)Heap.Allocate(PacketSize);
		}

		public override int SectorSize => (int)_sectorSize;

		public override ulong SectorCount => _sectorCount;

		// True with a disk to serve at target 0, LUN 0
		public static unsafe bool Start(in VmBusOffer offer)
		{
			SyntheticScsi scsi = new SyntheticScsi();
			if (!scsi._channel.OpenRings(in offer, RingPages) || !scsi.Negotiate())
				return false;

			scsi._bouncePhysical = PhysicalMemory.Allocate(BouncePages);
			if (scsi._bouncePhysical == 0)
				return false;
			scsi._bounce = (byte*)BootInfo.ToVirtual(scsi._bouncePhysical);
			if (!scsi.Probe())
				return false;

			scsi._ready = true;
			Active = scsi;
			return true;
		}

		public override unsafe bool Read(ulong sector, int count, Span<byte> buffer)
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
					new ReadOnlySpan<byte>(_bounce, sectors * (int)_sectorSize).CopyTo(buffer.Slice(at * (int)_sectorSize));
					at += sectors;
				}
			}
			_lock.Signal();
			return done;
		}

		public override unsafe bool Write(ulong sector, int count, ReadOnlySpan<byte> buffer)
		{
			if (!InRange(sector, count, buffer.Length))
				return false;

			_lock.Wait();
			int chunk = (int)(_maxTransfer / _sectorSize);
			bool done = true;
			for (int at = 0; done && at < count;)
			{
				int sectors = Math.Min(chunk, count - at);
				buffer.Slice(at * (int)_sectorSize, sectors * (int)_sectorSize).CopyTo(new Span<byte>(_bounce, sectors * (int)_sectorSize));
				done = Transfer(Write16, sector + (ulong)at, (uint)sectors, DirectionOut);
				at += sectors;
			}
			_lock.Signal();
			return done;
		}

		public override unsafe bool Flush()
		{
			if (!_ready)
				return false;

			_lock.Wait();
			byte* cdb = stackalloc byte[10];
			new Span<byte>(cdb, 10).Clear();
			cdb[0] = SynchronizeCache10;
			bool done = Command(new ReadOnlySpan<byte>(cdb, 10), DirectionNone, 0);
			_lock.Signal();
			return done;
		}

		private bool InRange(ulong sector, int count, int bufferLength) =>
			_ready && count >= 0 && (ulong)count <= _sectorCount && sector <= _sectorCount - (ulong)count && (long)bufferLength >= (long)count * _sectorSize;

		// Begin, a protocol version both sides speak, the channel's properties, end
		private unsafe bool Negotiate()
		{
			if (!Operate(BeginInitialization))
			{
				Log.WriteLine("SyntheticScsi: no answer to BEGIN_INITIALIZATION");
				return false;
			}

			// Newest first; a version the host rejects completes with a nonzero status
			byte* packet = stackalloc byte[PacketSize];
			for (int i = 0; i < 3 && _version == 0; i++)
			{
				ushort version = i == 0 ? VersionWin10 : i == 1 ? VersionWin8_1 : VersionWin8;
				new Span<byte>(packet, PacketSize).Clear();
				Put32(packet, Operation, QueryProtocolVersion);
				Put32(packet, Flags, RequestCompletionFlag);
				Put16(packet, VersionField, version);
				if (!Execute(packet, 0))
				{
					Log.WriteLine("SyntheticScsi: no answer to QUERY_PROTOCOL_VERSION");
					return false;
				}
				if (Get32(_response, Operation) == CompleteIo && Get32(_response, Status) == 0)
					_version = version;
			}
			if (_version == 0)
			{
				Log.WriteLine("SyntheticScsi: host rejected protocol versions 6.2 to 5.1");
				return false;
			}

			new Span<byte>(packet, PacketSize).Clear();
			Put32(packet, Operation, QueryProperties);
			Put32(packet, Flags, RequestCompletionFlag);
			if (!Execute(packet, 0) || Get32(_response, Operation) != CompleteIo || Get32(_response, Status) != 0)
			{
				Log.WriteLine("SyntheticScsi: no answer to QUERY_PROPERTIES");
				return false;
			}

			// The host's limit on one request, or the bounce buffer's where that is smaller or the host names none
			uint hostMax = Get32(_response, MaxTransferBytes);
			_maxTransfer = hostMax == 0 || hostMax > BounceSize ? BounceSize : hostMax;

			if (!Operate(EndInitialization))
			{
				Log.WriteLine("SyntheticScsi: no answer to END_INITIALIZATION");
				return false;
			}
			return true;
		}

		// The disk at target 0, LUN 0: a direct-access device, its capacity, and a look at its partition table
		private unsafe bool Probe()
		{
			byte* cdb = stackalloc byte[MaxCdb];
			new Span<byte>(cdb, MaxCdb).Clear();
			cdb[0] = Inquiry;
			cdb[4] = (byte)InquiryLength;
			if (!Command(new ReadOnlySpan<byte>(cdb, 6), DirectionIn, InquiryLength) || (_bounce[0] & 0x1F) != PeripheralDisk)
			{
				Log.WriteLine("SyntheticScsi: no disk at target 0, LUN 0");
				return false;
			}

			// The last sector and the sector size, big-endian
			new Span<byte>(cdb, MaxCdb).Clear();
			cdb[0] = ServiceActionIn16;
			cdb[1] = ReadCapacity16;
			cdb[13] = (byte)CapacityLength;
			if (!Command(new ReadOnlySpan<byte>(cdb, MaxCdb), DirectionIn, CapacityLength))
			{
				Log.WriteLine("SyntheticScsi: READ CAPACITY failed");
				return false;
			}

			ulong last = 0;
			for (int i = 0; i < 8; i++)
				last = (last << 8) | _bounce[i];
			_sectorCount = last + 1;
			_sectorSize = ((uint)_bounce[8] << 24) | ((uint)_bounce[9] << 16) | ((uint)_bounce[10] << 8) | _bounce[11];
			if (_sectorSize == 0 || BounceSize % _sectorSize != 0 || _maxTransfer < _sectorSize)
			{
				Log.WriteLine("SyntheticScsi: the sector size doesn't fit the transfer size");
				return false;
			}

			// The boot disk is GPT, whose header in sector 1 starts with its signature
			bool gpt = Transfer(Read16, 1, 1, DirectionIn) && _bounce[0] == 'E' && _bounce[1] == 'F' && _bounce[2] == 'I' && _bounce[3] == ' ' && _bounce[4] == 'P' && _bounce[5] == 'A' && _bounce[6] == 'R' && _bounce[7] == 'T';
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

		// Sends the packet and waits for its completion in the response, with dmaBytes of the bounce buffer as its data; false on no answer
		private unsafe bool Execute(byte* packet, uint dmaBytes)
		{
			ulong flags = Scheduler.Lock();
			_event.Reset();
			_pending = _nextId++;
			new Span<byte>(_response, PacketSize).Clear();
			_waiting = true;
			Scheduler.Unlock(flags);

			bool sent = dmaBytes == 0 ?
				_channel.Post(packet, PacketSize, _pending) :
				_channel.PostGpaDirect(packet, PacketSize, _pending, _bouncePhysical, dmaBytes);
			bool answered = sent && _event.Wait(TimeoutMs);

			flags = Scheduler.Lock();
			_waiting = false;
			Scheduler.Unlock(flags);
			return answered;
		}

		private unsafe bool Operate(uint operation)
		{
			byte* packet = stackalloc byte[PacketSize];
			new Span<byte>(packet, PacketSize).Clear();
			Put32(packet, Operation, operation);
			Put32(packet, Flags, RequestCompletionFlag);
			return Execute(packet, 0) && Get32(_response, Operation) == CompleteIo && Get32(_response, Status) == 0;
		}

		// One SCSI command to the disk, its data in the bounce buffer; false unless it succeeds
		private unsafe bool Command(ReadOnlySpan<byte> cdb, byte direction, uint bytes)
		{
			if (cdb.Length > MaxCdb || bytes > BounceSize)
				return false;

			// A disk reports a unit attention once after it changes or first comes up, so the command goes again
			byte* packet = stackalloc byte[PacketSize];
			for (int attempt = 0; attempt < 3; attempt++)
			{
				new Span<byte>(packet, PacketSize).Clear();
				Put32(packet, Operation, ExecuteSrb);
				Put32(packet, Flags, RequestCompletionFlag);
				Put16(packet, SrbLength, SrbSize);
				packet[CdbLength] = (byte)cdb.Length;
				packet[SenseLength] = SenseSize;
				packet[DataIn] = direction;
				Put32(packet, TransferLength, bytes);
				cdb.CopyTo(new Span<byte>(packet + Cdb, MaxCdb));
				Put32(packet, SrbFlags, SrbFlagsDisableSynchTransfer | (direction == DirectionIn ? SrbFlagsDataIn : direction == DirectionOut ? SrbFlagsDataOut : 0));
				Put32(packet, SrbTimeout, 60);

				if (!Execute(packet, bytes))
					return false;

				if (Get32(_response, Status) == 0 && (_response[SrbStatus] & SrbStatusMask) == SrbStatusSuccess && _response[ScsiStatus] == ScsiStatusGood)
					return true;

				bool unitAttention = _response[ScsiStatus] == ScsiStatusCheckCondition && (_response[SrbStatus] & SrbStatusAutosenseValid) != 0 &&
					(_response[SenseData + 2] & 0x0F) == SenseKeyUnitAttention;
				if (!unitAttention)
					return false;
			}
			return false;
		}

		// READ(16) or WRITE(16): count sectors at sector, big-endian, through the bounce buffer
		private unsafe bool Transfer(byte opcode, ulong sector, uint count, byte direction)
		{
			byte* cdb = stackalloc byte[MaxCdb];
			new Span<byte>(cdb, MaxCdb).Clear();
			cdb[0] = opcode;
			for (int i = 0; i < 8; i++)
				cdb[2 + i] = (byte)(sector >> (56 - 8 * i));
			for (int i = 0; i < 4; i++)
				cdb[10 + i] = (byte)(count >> (24 - 8 * i));

			uint bytes = count * _sectorSize;
			return Command(new ReadOnlySpan<byte>(cdb, MaxCdb), direction, bytes) && Get32(_response, TransferLength) == bytes;
		}

		private unsafe void OnPacket(VmBusPacket* packet, byte* payload, uint size)
		{
			// Only the completion a request is waiting for counts; the host's bus notifications and answers that came too late are dropped
			if (packet->Type != VmBusChannel.PacketCompletion || !_waiting || packet->TransactionId != _pending)
				return;

			new ReadOnlySpan<byte>(payload, (int)Math.Min(size, (uint)PacketSize)).CopyTo(new Span<byte>(_response, PacketSize));
			_waiting = false;
			_event.Signal();
		}

		private static unsafe void Put16(byte* packet, int offset, ushort value) => *(ushort*)(packet + offset) = value;

		private static unsafe void Put32(byte* packet, int offset, uint value) => *(uint*)(packet + offset) = value;

		private static unsafe uint Get32(byte* packet, int offset) => *(uint*)(packet + offset);

		// The controller's VMBus channel; its packets go to the driver
		private sealed class Channel : VmBusChannel
		{
			private readonly SyntheticScsi _owner;

			public Channel(SyntheticScsi owner)
			{
				_owner = owner;
			}

			public bool OpenRings(in VmBusOffer offer, int pages) => Open(in offer, pages, pages);

			public unsafe bool Post(void* data, uint length, ulong requestId) => Send(data, length, requestId, PacketInband, CompletionRequested);

			public unsafe bool PostGpaDirect(void* data, uint length, ulong requestId, ulong physical, uint byteCount) => SendGpaDirect(data, length, requestId, physical, byteCount);

			protected override unsafe void OnPacket(VmBusPacket* packet, byte* payload, uint size) => _owner.OnPacket(packet, payload, size);
		}
	}
}
