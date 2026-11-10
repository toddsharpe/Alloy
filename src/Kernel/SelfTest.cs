using System;
using System.Runtime;
using System.Runtime.CompilerServices;
using Alloy.Abi;
using Alloy.Kernel.Arch;
using Alloy.Kernel.Mm;
using Alloy.Text;

namespace Alloy.Kernel
{
	// Boot-time checks of the phase 2 machinery: heap, exception dispatch, fault fixups and the dropped identity map
	internal static class SelfTest
	{
		private static int s_passed;
		private static int s_failed;

		public static bool Run()
		{
			Check("heap allocate, free and reuse", HeapRoundTrip());
			Check("objects come from the heap", HeapBacksNew());
			Check("breakpoint is reported and resumed", Breakpoint());
			Check("page fault in a probe is recovered", ProbeRecovers());
			Check("null page is unmapped", !Probe(0) && !Probe(0x1000));
			Check("timer ticks and sleep is accurate", SleepAccuracy());
			Check("threads ping-pong 1000 events", PingPong());
			Check("a busy thread is preempted", Preemption());
			Check("wait times out", WaitTimeout());
			Check("exited threads are reaped", Reaped());

			SpanWriter line = new SpanWriter(stackalloc char[64]);
			line.Append("selftest: ");
			line.Append(s_passed);
			line.Append('/');
			line.Append(s_passed + s_failed);
			line.Append(" passed");
			Log.WriteLine(ref line);
			return s_failed == 0;
		}

		private static void Check(string name, bool passed)
		{
			if (passed)
				s_passed++;
			else
				s_failed++;
			Log.WriteLine(string.Concat(passed ? "  ok   " : "  FAIL ", name));
		}

		// Runs one allocation pattern twice: the second pass must be served entirely from slabs the first one freed
		private static bool HeapRoundTrip()
		{
			if (!HeapPattern())
				return false;
			ulong freeAfterFirst = PhysicalMemory.FreePages;
			return HeapPattern() && PhysicalMemory.FreePages == freeAfterFirst && Heap.LargePages == 0;
		}

		private static unsafe bool HeapPattern()
		{
			const int Count = 256;
			byte** blocks = stackalloc byte*[Count];
			for (int i = 0; i < Count; i++)
			{
				nuint size = (nuint)(8 + (i * 37) % 6000);
				blocks[i] = (byte*)Heap.Allocate(size);
				if (blocks[i] == null || ((ulong)blocks[i] & 15) != 0 || blocks[i][size - 1] != 0)
					return false;
				new Span<byte>(blocks[i], (int)size).Fill((byte)i);
			}
			for (int i = 0; i < Count; i++)
			{
				nuint size = (nuint)(8 + (i * 37) % 6000);
				if (blocks[i][0] != (byte)i || blocks[i][size - 1] != (byte)i)
					return false;
			}
			for (int i = 0; i < Count; i++)
				Heap.Free(blocks[i]);
			return true;
		}

		private static bool HeapBacksNew()
		{
			int[] values = new int[2000];
			values[1999] = 7;
			ulong address = (ulong)Unsafe.As<int[], nint>(ref values);
			bool inWindow = address >= MemoryLayout.KernelPhysicalWindow && address < MemoryLayout.KernelEnd;
			Heap.Free(values);
			return inWindow;
		}

		private static bool Breakpoint()
		{
			int before = Interrupts.BreakpointCount;
			PhxBreakpoint();
			return Interrupts.BreakpointCount == before + 1;
		}

		private static bool SleepAccuracy()
		{
			ulong ticks = Ke.Scheduler.Ticks;
			ulong start = Ke.Scheduler.UptimeMs;
			Ke.Scheduler.Sleep(100);
			ulong elapsed = Ke.Scheduler.UptimeMs - start;
			return elapsed >= 100 && elapsed < 120 && Ke.Scheduler.Ticks - ticks >= 90;
		}

		private static Ke.KEvent s_ping;
		private static Ke.KEvent s_pong;
		private static int s_rounds;

		private static unsafe bool PingPong()
		{
			s_ping = new Ke.KEvent(false);
			s_pong = new Ke.KEvent(false);
			s_rounds = 0;
			Ke.Scheduler.Start("pong", &PongLoop, null);
			for (int i = 0; i < 1000; i++)
			{
				s_ping.Signal();
				if (!s_pong.Wait(1000))
					return false;
			}
			return s_rounds == 1000;
		}

		private static unsafe void PongLoop(void* argument)
		{
			for (int i = 0; i < 1000; i++)
			{
				s_ping.Wait();
				s_rounds++;
				s_pong.Signal();
			}
			Ke.Scheduler.Exit();
		}

		private static volatile bool s_spin;
		private static volatile int s_spins;

		private static unsafe bool Preemption()
		{
			s_spin = true;
			Ke.Scheduler.Start("spinner", &SpinLoop, null);
			Ke.Scheduler.Sleep(50);
			bool spun = s_spins > 0;
			s_spin = false;
			Ke.Scheduler.Sleep(10);
			return spun;
		}

		// Never yields: the sleeping main thread only gets back if the timer preempts this loop
		private static unsafe void SpinLoop(void* argument)
		{
			while (s_spin)
				s_spins++;
			Ke.Scheduler.Exit();
		}

		private static bool WaitTimeout()
		{
			Ke.KEvent never = new Ke.KEvent(true);
			ulong start = Ke.Scheduler.UptimeMs;
			bool signaled = never.Wait(30);
			ulong elapsed = Ke.Scheduler.UptimeMs - start;
			return !signaled && elapsed >= 30 && elapsed < 50;
		}

		private static bool Reaped()
		{
			ulong free = PhysicalMemory.FreePages;
			Ke.Scheduler.Sleep(20);
			return Ke.Scheduler.ThreadCount == 2 && PhysicalMemory.FreePages >= free;
		}

		private static bool ProbeRecovers() => Probe(MemoryLayout.KernelBase) && !Probe(0xFFFF_8000_0000_0000);

		private static unsafe bool Probe(ulong address)
		{
			byte value;
			return PhxProbeRead((byte*)address, &value) != 0;
		}

		[RuntimeImport("*", "PhxBreakpoint")]
		[MethodImpl(MethodImplOptions.InternalCall)]
		private static extern void PhxBreakpoint();

		[RuntimeImport("*", "PhxProbeRead")]
		[MethodImpl(MethodImplOptions.InternalCall)]
		private static extern unsafe int PhxProbeRead(byte* address, byte* value);
	}
}
