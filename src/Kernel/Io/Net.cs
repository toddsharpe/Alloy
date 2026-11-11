using System;
using System.Runtime.InteropServices;
using System.Threading;
using Alloy.Abi;
using Alloy.Kernel.Ke;
using Alloy.Kernel.Mm;
using Alloy.Kernel.Ob;

namespace Alloy.Kernel.Io
{
	// The kernel owns the NIC and raw Ethernet frames; the TCP/IP stack is NetSvr (MetalOS kernel/Net.cpp)
	internal static class Net
	{
		private static ulong s_physical;
		private static KEvent s_ready;

		public static ulong Received { get; private set; }

		public static void Initialize()
		{
			KSection section = KSection.Create(NetFrame.RingSize);
			s_physical = section.Physical;
			s_ready = new KEvent(false);
			Endpoints.Register(NetFrame.RxEndpoint, section);
			Endpoints.Register(NetFrame.RxReadyEndpoint, s_ready);
		}

		// The NIC calls this from its interrupt handler; a full ring drops the frame, as MetalOS does when the netstack falls behind
		public static void Receive(ReadOnlySpan<byte> frame)
		{
			if (s_physical == 0 || frame.Length <= 0 || frame.Length > NetFrame.Mtu)
				return;
			Span<byte> ring = PhysicalMemory.Window(s_physical, NetFrame.RingSize);
			Span<uint> counters = MemoryMarshal.Cast<byte, uint>(ring.Slice(0, NetFrame.HeaderSize));
			uint write = Volatile.Read(ref counters[0]);
			if (write - Volatile.Read(ref counters[1]) >= NetFrame.RxCapacity)
				return;
			ref NetFrame record = ref MemoryMarshal.Cast<byte, NetFrame>(ring.Slice(NetFrame.HeaderSize + (int)(write % NetFrame.RxCapacity) * NetFrame.RecordSize, NetFrame.RecordSize))[0];
			record.Interface = 0;
			record.Length = (ushort)frame.Length;
			frame.CopyTo(record.Data);
			Interlocked.MemoryBarrier();
			Volatile.Write(ref counters[0], write + 1);
			Received++;
			s_ready.Signal();
		}
	}
}
