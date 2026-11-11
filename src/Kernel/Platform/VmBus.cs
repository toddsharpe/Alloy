using System;
using System.Threading;
using Alloy.Kernel.Arch;
using Alloy.Kernel.Ke;
using Alloy.Kernel.Mm;
using Alloy.Text;

namespace Alloy.Kernel.Platform
{
	// A channel the host offered (vmbus_channel_offer_channel), with the interface GUID as two little-endian halves
	internal struct VmBusOffer
	{
		public ulong TypeLow;
		public ulong TypeHigh;
		public uint RelId;
		public uint ConnectionId;
		public bool DedicatedInterrupt;
		public ushort MmioMegabytes;
	}

	// The VMBus client (MetalOS VmBusDriver): connects over SINT 2, collects offers, creates GPADLs and opens channels
	internal static class VmBus
	{
		public const int Vector = 0x90;
		private const int MessageSint = 2;
		private const uint ContactConnectionId = 4;
		private const uint VersionWin10V52 = (5 << 16) | 2;
		private const uint ChannelMessage = 1;
		private const ulong ReplyTimeoutMs = 2000;
		private const int MaxOffers = 32;
		private const int MaxChannels = 128;
		private const int MaxHeaderPfns = 26;
		private const int MaxBodyPfns = 28;

		private const uint OfferChannel = 1;
		private const uint RescindOffer = 2;
		private const uint RequestOffers = 3;
		private const uint AllOffersDelivered = 4;
		private const uint OpenChannelRequest = 5;
		private const uint OpenChannelResult = 6;
		private const uint GpadlHeader = 8;
		private const uint GpadlBody = 9;
		private const uint GpadlCreated = 10;
		private const uint InitiateContact = 14;
		private const uint VersionResponse = 15;

		public const ulong KeyboardLow = 0x48EA2B17F912AD6D;
		public const ulong KeyboardHigh = 0x84761CA627F965BD;
		public const ulong MouseLow = 0x4CC05B4ACFA8B69E;
		public const ulong MouseHigh = 0x5AF9F3A1A18B8BB9;
		public const ulong VideoLow = 0x4AACE377DA0A7802;
		public const ulong VideoHigh = 0xF87310EB5805778E;
		public const ulong NetworkLow = 0x46C5DF3EF8615163;
		public const ulong NetworkHigh = 0x0EED65F9D2F23F91;
		public const ulong ScsiLow = 0x4D2904A1BA6163D9;
		public const ulong ScsiHigh = 0x7FDCB1FFE27205B6;
		public const ulong ShutdownLow = 0x493452130E0B6031;
		public const ulong ShutdownHigh = 0xDB39ED0CD9388B81;

		private static uint s_connectionId;
		private static unsafe VmBusOffer* s_offers;
		private static int s_offerCount;
		private static VmBusChannel[] s_channels;
		private static int s_nextGpadl;

		// Setup runs on one thread, so one reply slot is enough: the message type awaited and a copy of its payload
		private static KEvent s_reply;
		private static uint s_awaiting;
		private static unsafe byte* s_replyPayload;

		public static int OfferCount => s_offerCount;

		public static unsafe bool Initialize()
		{
			s_offers = (VmBusOffer*)Heap.Allocate((nuint)(MaxOffers * sizeof(VmBusOffer)));
			s_channels = new VmBusChannel[MaxChannels];
			s_replyPayload = (byte*)Heap.Allocate(HyperV.MessagePayloadSize);
			s_reply = new KEvent(false);

			HyperV.InitializeSynic();
			Interrupts.Register(Vector, &OnInterrupt);
			HyperV.EnableSint(MessageSint, Vector);

			// The monitor pages are only handed over; channels here signal with HvSignalEvent instead
			byte* contact = stackalloc byte[40];
			new Span<byte>(contact, 40).Clear();
			*(uint*)contact = InitiateContact;
			*(uint*)(contact + 8) = VersionWin10V52;
			contact[16] = MessageSint;
			*(ulong*)(contact + 24) = PhysicalMemory.Allocate(1);
			*(ulong*)(contact + 32) = PhysicalMemory.Allocate(1);
			if (!Request(ContactConnectionId, contact, 40, VersionResponse) || s_replyPayload[8] == 0)
			{
				Log.WriteLine("VmBus: the host refused protocol 5.2");
				return false;
			}
			s_connectionId = *(uint*)(s_replyPayload + 12);

			ulong header = RequestOffers;
			if (!Request(s_connectionId, &header, 8, AllOffersDelivered))
			{
				Log.WriteLine("VmBus: no ALLOFFERS_DELIVERED from the host");
				return false;
			}

			for (int i = 0; i < s_offerCount; i++)
				LogOffer(in s_offers[i]);
			return true;
		}

		public static unsafe bool TryFind(ulong typeLow, ulong typeHigh, out VmBusOffer offer)
		{
			for (int i = 0; i < s_offerCount; i++)
			{
				if (s_offers[i].TypeLow == typeLow && s_offers[i].TypeHigh == typeHigh)
				{
					offer = s_offers[i];
					return true;
				}
			}
			offer = default;
			return false;
		}

		// Describes physically contiguous pages to the host: a GPADL_HEADER with the first page numbers, GPADL_BODY for the rest
		public static unsafe uint CreateGpadl(uint relId, ulong physical, int pages)
		{
			uint gpadl = (uint)Interlocked.Increment(ref s_nextGpadl);
			byte* message = stackalloc byte[HyperV.MessagePayloadSize];
			int headerPfns = pages < MaxHeaderPfns ? pages : MaxHeaderPfns;
			new Span<byte>(message, HyperV.MessagePayloadSize).Clear();
			*(uint*)message = GpadlHeader;
			*(uint*)(message + 8) = relId;
			*(uint*)(message + 12) = gpadl;
			*(ushort*)(message + 16) = (ushort)(8 + 8 * pages);
			*(ushort*)(message + 18) = 1;
			*(uint*)(message + 20) = (uint)pages << 12;
			for (int i = 0; i < headerPfns; i++)
				*(ulong*)(message + 28 + 8 * i) = (physical >> 12) + (ulong)i;

			ulong flags = Scheduler.Lock();
			Arm(GpadlCreated);
			bool posted = HyperV.PostMessageRetry(s_connectionId, ChannelMessage, message, 28 + 8 * headerPfns);
			Scheduler.Unlock(flags);

			uint number = 0;
			for (int sent = headerPfns; posted && sent < pages;)
			{
				int batch = pages - sent < MaxBodyPfns ? pages - sent : MaxBodyPfns;
				new Span<byte>(message, HyperV.MessagePayloadSize).Clear();
				*(uint*)message = GpadlBody;
				*(uint*)(message + 8) = number++;
				*(uint*)(message + 12) = gpadl;
				for (int i = 0; i < batch; i++)
					*(ulong*)(message + 16 + 8 * i) = (physical >> 12) + (ulong)(sent + i);
				posted = HyperV.PostMessageRetry(s_connectionId, ChannelMessage, message, 16 + 8 * batch);
				sent += batch;
			}

			if (!posted || !s_reply.Wait(ReplyTimeoutMs))
				return 0;
			return *(uint*)(s_replyPayload + 12) == gpadl && *(uint*)(s_replyPayload + 16) == 0 ? gpadl : 0;
		}

		// The channel is registered before OPENCHANNEL: the host may signal it the moment it is open (MetalOS HyperVChannel)
		public static unsafe bool OpenChannel(VmBusChannel channel, uint relId, uint gpadl, uint downstreamPageOffset)
		{
			if (relId >= MaxChannels)
				return false;
			s_channels[relId] = channel;

			byte* message = stackalloc byte[148];
			new Span<byte>(message, 148).Clear();
			*(uint*)message = OpenChannelRequest;
			*(uint*)(message + 8) = relId;
			*(uint*)(message + 12) = relId;
			*(uint*)(message + 16) = gpadl;
			*(uint*)(message + 24) = downstreamPageOffset;
			if (Request(s_connectionId, message, 148, OpenChannelResult) && *(uint*)(s_replyPayload + 16) == 0)
				return true;
			s_channels[relId] = null;
			return false;
		}

		private static void Arm(uint messageType)
		{
			s_reply.Reset();
			s_awaiting = messageType;
		}

		// Arms before posting, since the answer can arrive before the wait starts
		private static unsafe bool Request(uint connectionId, void* message, int size, uint replyType)
		{
			ulong flags = Scheduler.Lock();
			Arm(replyType);
			bool posted = HyperV.PostMessageRetry(connectionId, ChannelMessage, message, size);
			Scheduler.Unlock(flags);
			return posted && s_reply.Wait(ReplyTimeoutMs);
		}

		private static unsafe void OnInterrupt(InterruptFrame* frame)
		{
			// Each set bit is a channel id the host signaled; exchanging a whole word takes its bits atomically
			uint* events = HyperV.EventFlags(MessageSint);
			for (int word = 0; word < MaxChannels / 32; word++)
			{
				if (Volatile.Read(ref events[word]) == 0)
					continue;
				uint pending = (uint)Interlocked.Exchange(ref *(int*)&events[word], 0);
				for (int bit = 0; pending != 0; bit++, pending >>= 1)
				{
					if ((pending & 1) != 0)
						s_channels[word * 32 + bit]?.OnInterrupt();
				}
			}

			byte* slot = HyperV.MessageSlot(MessageSint);
			if (Volatile.Read(ref *(uint*)slot) == ChannelMessage)
			{
				byte* payload = stackalloc byte[HyperV.MessagePayloadSize];
				new ReadOnlySpan<byte>(slot + 16, HyperV.MessagePayloadSize).CopyTo(new Span<byte>(payload, HyperV.MessagePayloadSize));
				HyperV.CompleteMessage(MessageSint);
				OnMessage(payload);
			}
			else if (Volatile.Read(ref *(uint*)slot) != 0)
			{
				HyperV.CompleteMessage(MessageSint);
			}

			HyperV.EndOfInterrupt();

			// A channel callback may have woken a thread (a disk request's completion), which runs now rather than at the next timer tick
			Scheduler.Preempt();
		}

		private static unsafe void OnMessage(byte* payload)
		{
			uint type = *(uint*)payload;
			if (type == OfferChannel)
			{
				if (s_offerCount < MaxOffers && *(ushort*)(payload + 8 + 172) == 0)
				{
					VmBusOffer* offer = &s_offers[s_offerCount++];
					offer->TypeLow = *(ulong*)(payload + 8);
					offer->TypeHigh = *(ulong*)(payload + 16);
					offer->RelId = *(uint*)(payload + 184);
					offer->DedicatedInterrupt = (payload[190] & 1) != 0;
					offer->ConnectionId = *(uint*)(payload + 192);
					offer->MmioMegabytes = *(ushort*)(payload + 58);
				}
				return;
			}
			if (type == RescindOffer)
			{
				SpanWriter line = new SpanWriter(stackalloc char[64]);
				line.Append("VmBus: host rescinded channel ");
				line.Append(*(uint*)(payload + 8));
				Log.WriteLine(ref line);
				return;
			}
			if (type == s_awaiting)
			{
				new ReadOnlySpan<byte>(payload, HyperV.MessagePayloadSize).CopyTo(new Span<byte>(s_replyPayload, HyperV.MessagePayloadSize));
				s_awaiting = 0;
				s_reply.Signal();
			}
		}

		private static void LogOffer(in VmBusOffer offer)
		{
			SpanWriter line = new SpanWriter(stackalloc char[96]);
			line.Append("VmBus: channel ");
			line.Append(offer.RelId);
			line.Append(" {");
			line.AppendHex((uint)offer.TypeLow, 8);
			line.Append('-');
			line.AppendHex((ushort)(offer.TypeLow >> 32), 4);
			line.Append('-');
			line.AppendHex((ushort)(offer.TypeLow >> 48), 4);
			line.Append('-');
			for (int i = 0; i < 8; i++)
			{
				if (i == 2)
					line.Append('-');
				line.AppendHex((byte)(offer.TypeHigh >> (8 * i)), 2);
			}
			line.Append('}');
			if (offer.TypeLow == KeyboardLow && offer.TypeHigh == KeyboardHigh)
				line.Append(" keyboard");
			else if (offer.TypeLow == MouseLow && offer.TypeHigh == MouseHigh)
				line.Append(" mouse");
			else if (offer.TypeLow == VideoLow && offer.TypeHigh == VideoHigh)
				line.Append(" video");
			else if (offer.TypeLow == NetworkLow && offer.TypeHigh == NetworkHigh)
				line.Append(" network");
			else if (offer.TypeLow == ScsiLow && offer.TypeHigh == ScsiHigh)
				line.Append(" scsi");
			else if (offer.TypeLow == ShutdownLow && offer.TypeHigh == ShutdownHigh)
				line.Append(" shutdown");
			Log.WriteLine(ref line);
		}
	}
}
