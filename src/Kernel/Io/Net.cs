using System;
using System.Threading;
using Alloy.Abi;
using Alloy.Kernel.Ke;
using Alloy.Kernel.Ob;

namespace Alloy.Kernel.Io
{
	// The kernel owns the NIC and raw Ethernet frames; the TCP/IP stack is NetSvr (MetalOS kernel/Net.cpp)
	internal static unsafe class Net
	{
		private static byte* s_ring;
		private static KEvent s_ready;

		public static ulong Received { get; private set; }

		public static void Initialize()
		{
			KSection section = KSection.Create(NetFrame.RingSize);
			s_ring = (byte*)BootInfo.ToVirtual(section.Physical);
			s_ready = new KEvent(false);
			Endpoints.Register(NetFrame.RxEndpoint, section);
			Endpoints.Register(NetFrame.RxReadyEndpoint, s_ready);
		}

		// The NIC calls this from its interrupt handler; a full ring drops the frame, as MetalOS does when the netstack falls behind
		public static void Receive(byte* frame, int length)
		{
			if (s_ring == null || length <= 0 || length > NetFrame.Mtu)
				return;
			uint* counters = NetFrame.Counters(s_ring);
			uint write = Volatile.Read(ref counters[0]);
			if (write - Volatile.Read(ref counters[1]) >= NetFrame.RxCapacity)
				return;
			NetFrame* record = NetFrame.At(s_ring, write);
			record->Interface = 0;
			record->Length = (ushort)length;
			new ReadOnlySpan<byte>(frame, length).CopyTo(record->Data);
			Interlocked.MemoryBarrier();
			Volatile.Write(ref counters[0], write + 1);
			Received++;
			s_ready.Signal();
		}
	}
}
