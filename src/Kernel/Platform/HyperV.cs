using System;
using System.Runtime;
using System.Runtime.CompilerServices;
using System.Threading;
using Alloy.Abi;
using Alloy.Arch.X64;
using Alloy.Kernel.Arch;
using Alloy.Kernel.Mm;

namespace Alloy.Kernel.Platform
{
	// Hyper-V enlightenments (TLFS): guest identity, hypercall page, reference TSC and synthetic timer 0 in direct mode
	internal static unsafe class HyperV
	{
		public const int TimerVector = 0x80;
		public const ulong TicksPerSecond = 10_000_000;

		private const uint MsrGuestOsId = 0x40000000;
		private const uint MsrHypercall = 0x40000001;
		private const uint MsrTimeRefCount = 0x40000020;
		private const uint MsrReferenceTsc = 0x40000021;
		private const uint MsrEoi = 0x40000070;
		private const uint MsrStimer0Config = 0x400000B0;
		private const uint MsrStimer0Count = 0x400000B1;
		private const uint MsrScontrol = 0x40000080;
		private const uint MsrSiefp = 0x40000082;
		private const uint MsrSimp = 0x40000083;
		private const uint MsrEom = 0x40000084;
		private const uint MsrSint0 = 0x40000090;

		private const ulong CallPostMessage = 0x5C;
		private const ulong CallSignalEvent = 0x5D;
		private const ulong FastCall = 1UL << 16;

		public const int MessageSlotSize = 256;
		public const int MessagePayloadSize = 240;
		public const int EventFlagsSize = 256;
		public const ushort StatusSuccess = 0;
		public const ushort StatusInsufficientBuffers = 0x13;

		// Open source, Linux type, as MetalOS identifies itself (TLFS 3.13)
		private const ulong GuestOsId = 0x8101000000010002;

		private struct ReferenceTscPage
		{
			public uint Sequence;
			public uint Reserved;
			public ulong Scale;
			public long Offset;
		}

		private static ReferenceTscPage* s_tsc;
		private static void* s_hypercallPage;
		private static ulong s_postPage;
		private static byte* s_messages;
		private static byte* s_events;

		public static bool Present { get; private set; }

		public static void Initialize()
		{
			CpuIdResult vendor = Cpu.CpuId(0x40000000);
			CpuIdResult iface = Cpu.CpuId(0x40000001);
			Present = vendor.Ebx == 0x7263694D && vendor.Ecx == 0x666F736F && vendor.Edx == 0x76482074 && iface.Eax == 0x31237648;
			if (!Present)
				Environment.FailFast("FAILFAST: Alloy needs Hyper-V (CPUID 0x40000000 is not \"Microsoft Hv\" / \"Hv#1\")");

			Cpu.WriteMsr(MsrGuestOsId, GuestOsId);

			// The hypervisor writes the hypercall code into this page; it must be executable, and the physical window is not
			ulong hypercall = PhysicalMemory.Allocate(1);
			s_hypercallPage = (void*)KernelSpace.AllocateVirtual(1);
			KernelSpace.Map((ulong)s_hypercallPage, hypercall, MemoryLayout.PageSize, PageFlags.Global);
			Cpu.WriteMsr(MsrHypercall, hypercall | 1);

			ulong tsc = PhysicalMemory.Allocate(1);
			s_tsc = (ReferenceTscPage*)BootInfo.ToVirtual(tsc);
			Cpu.WriteMsr(MsrReferenceTsc, tsc | 1);
		}

		// Time since the partition started, in 100 ns units
		public static ulong ReferenceTime()
		{
			while (true)
			{
				uint sequence = s_tsc->Sequence;
				if (sequence == 0)
					return Cpu.ReadMsr(MsrTimeRefCount);
				ulong tsc = Cpu.ReadTsc();
				ulong scale = s_tsc->Scale;
				long offset = s_tsc->Offset;
				if (s_tsc->Sequence != sequence)
					continue;
				return (ulong)((long)MultiplyHigh(tsc, scale) + offset);
			}
		}

		public static void StartTimer(ulong periodTicks, delegate*<InterruptFrame*, void> handler)
		{
			Interrupts.Register(TimerVector, handler);
			Cpu.WriteMsr(MsrStimer0Count, periodTicks);
			const ulong Enable = 1, Periodic = 1 << 1, AutoEnable = 1 << 3, DirectMode = 1 << 12;
			Cpu.WriteMsr(MsrStimer0Config, Enable | Periodic | AutoEnable | ((ulong)TimerVector << 4) | DirectMode);
		}

		public static void EndOfInterrupt() => Cpu.WriteMsr(MsrEoi, 0);

		// The synthetic interrupt controller: one 256-byte message slot and one 256-byte event flag block per SINT (TLFS 11)
		public static void InitializeSynic()
		{
			ulong events = PhysicalMemory.Allocate(1);
			ulong messages = PhysicalMemory.Allocate(1);
			s_postPage = PhysicalMemory.Allocate(1);
			s_events = (byte*)BootInfo.ToVirtual(events);
			s_messages = (byte*)BootInfo.ToVirtual(messages);
			Cpu.WriteMsr(MsrSiefp, events | 1);
			Cpu.WriteMsr(MsrSimp, messages | 1);
			Cpu.WriteMsr(MsrScontrol, Cpu.ReadMsr(MsrScontrol) | 1);
		}

		// Unmasks a SINT onto a vector; the handler ends with EndOfInterrupt since AutoEOI stays off
		public static void EnableSint(int sint, int vector)
		{
			const ulong Masked = 1 << 16, AutoEoi = 1 << 17;
			uint msr = MsrSint0 + (uint)sint;
			Cpu.WriteMsr(msr, (Cpu.ReadMsr(msr) & ~(Masked | AutoEoi | 0xFF)) | (uint)vector);
		}

		public static byte* MessageSlot(int sint) => s_messages + sint * MessageSlotSize;

		public static uint* EventFlags(int sint) => (uint*)(s_events + sint * EventFlagsSize);

		// Frees the slot after reading it; the hypervisor then delivers the next queued message, if it flagged one
		public static void CompleteMessage(int sint)
		{
			byte* slot = MessageSlot(sint);
			Volatile.Write(ref *(uint*)slot, 0);
			Interlocked.MemoryBarrier();
			if ((Volatile.Read(ref slot[5]) & 1) != 0)
				Cpu.WriteMsr(MsrEom, 0);
		}

		// HvPostMessage through the one page-aligned input page, so interrupts stay off while it is in use
		public static ushort PostMessage(uint connectionId, uint messageType, void* payload, int size)
		{
			if (size > MessagePayloadSize)
				return StatusInsufficientBuffers;
			ulong flags = Ke.Scheduler.Lock();
			byte* input = (byte*)BootInfo.ToVirtual(s_postPage);
			*(uint*)input = connectionId;
			*(uint*)(input + 4) = 0;
			*(uint*)(input + 8) = messageType;
			*(uint*)(input + 12) = (uint)size;
			new ReadOnlySpan<byte>(payload, size).CopyTo(new Span<byte>(input + 16, MessagePayloadSize));
			ushort status = (ushort)Hypercall(CallPostMessage, s_postPage, 0);
			Ke.Scheduler.Unlock(flags);
			return status;
		}

		// Retries while the host's message buffers are full, as MetalOS's PostChannelMessage does
		public static bool PostMessageRetry(uint connectionId, uint messageType, void* payload, int size)
		{
			while (true)
			{
				ushort status = PostMessage(connectionId, messageType, payload, size);
				if (status != StatusInsufficientBuffers)
					return status == StatusSuccess;
				Cpu.Pause();
			}
		}

		// HvSignalEvent as a fast call: the connection id and flag number travel in a register
		public static ushort SignalEvent(uint connectionId, ushort flag) => (ushort)Hypercall(CallSignalEvent | FastCall, connectionId | ((ulong)flag << 32), 0);

		public static ulong Hypercall(ulong control, ulong input, ulong output) => PhxHypercall(control, input, output, s_hypercallPage);

		private static ulong MultiplyHigh(ulong a, ulong b)
		{
			ulong aLow = (uint)a, aHigh = a >> 32, bLow = (uint)b, bHigh = b >> 32;
			ulong low = aLow * bLow;
			ulong middle1 = aHigh * bLow + (low >> 32);
			ulong middle2 = aLow * bHigh + (uint)middle1;
			return aHigh * bHigh + (middle1 >> 32) + (middle2 >> 32);
		}

		[RuntimeImport("*", "PhxHypercall")]
		[MethodImpl(MethodImplOptions.InternalCall)]
		private static extern ulong PhxHypercall(ulong control, ulong input, ulong output, void* page);
	}
}
