using System.Runtime;
using System.Runtime.CompilerServices;
using Alloy.Arch.X64;
using Alloy.Kernel.Arch;
using Alloy.Kernel.Mm;
using Alloy.Kernel.Platform;
using Alloy.Kernel.Ps;

namespace Alloy.Kernel.Ke
{
	// Uniprocessor round robin: a ready queue, a sorted timer list, and preemption on every timer tick
	internal static class Scheduler
	{
		public const ulong TickHz = 1000;

		private static KThread s_current;
		private static KThread s_idle;
		private static KThread s_readyHead;
		private static KThread s_readyTail;
		private static KThread s_sleeping;
		private static KThread s_dead;
		private static KEvent s_reap;
		private static bool s_running;

		public static KThread Current => s_current;

		public static ulong Ticks { get; private set; }

		public static int ThreadCount { get; private set; }

		// Milliseconds since the partition started, from the reference TSC rather than the tick count
		public static ulong UptimeMs => HyperV.ReferenceTime() / 10_000;

		public static unsafe void Initialize()
		{
			s_current = KThread.ForCurrent("boot");
			ThreadCount = 1;
			s_idle = KThread.Create("idle", &IdleLoop, null);
			s_reap = new KEvent(false);
			Start("reaper", &ReaperLoop, null);
			HyperV.StartTimer(HyperV.TicksPerSecond / TickHz, &TimerInterrupt);
			s_running = true;
			Cpu.EnableInterrupts();
		}

		public static unsafe KThread Start(string name, delegate*<void*, void> entry, void* argument)
		{
			KThread thread = KThread.Create(name, entry, argument);
			ulong flags = Lock();
			Admit(thread);
			Unlock(flags);
			return thread;
		}

		// Queues a newly created thread; interrupts must be off
		internal static void Admit(KThread thread)
		{
			ThreadCount++;
			MakeReady(thread);
		}

		public static void Yield()
		{
			ulong flags = Lock();
			MakeReady(s_current);
			Switch();
			Unlock(flags);
		}

		public static void Sleep(ulong milliseconds)
		{
			ulong flags = Lock();
			s_current.WakeTime = UptimeMs + milliseconds;
			s_current.State = ThreadState.Waiting;
			AddTimer(s_current);
			Switch();
			Unlock(flags);
		}

		[System.Diagnostics.DoesNotReturn]
		public static void Exit()
		{
			Cpu.DisableInterrupts();
			s_current.State = ThreadState.Terminated;
			s_current.Next = s_dead;
			s_dead = s_current;
			ThreadCount--;
			s_reap.Signal();
			Switch();
			Cpu.HaltForever();
		}

		// Blocks the current thread until a wait block wakes it or the timeout passes; interrupts must be off
		internal static void Block(ulong timeoutMs)
		{
			s_current.State = ThreadState.Waiting;
			if (timeoutMs != ulong.MaxValue)
			{
				s_current.WakeTime = UptimeMs + timeoutMs;
				AddTimer(s_current);
			}
			Switch();
		}

		// Wakes a waiting or sleeping thread so it notices its process is ending; its wait reports a timeout
		public static void Interrupt(KThread thread)
		{
			ulong flags = Lock();
			if (thread.State == ThreadState.Waiting)
			{
				thread.InWait = false;
				thread.WaitIndex = -1;
				Wake(thread);
			}
			Unlock(flags);
		}

		// Loads the kernel's own page tables, before a process's are freed
		public static void UseKernelTables()
		{
			ulong flags = Lock();
			if (s_cr3 != KernelSpace.Root)
			{
				s_cr3 = KernelSpace.Root;
				Cpu.WriteCr3(s_cr3);
			}
			Unlock(flags);
		}

		internal static void Wake(KThread thread)
		{
			RemoveTimer(thread);
			MakeReady(thread);
		}

		internal static ulong Lock()
		{
			ulong flags = Cpu.ReadFlags();
			Cpu.DisableInterrupts();
			return flags;
		}

		internal static void Unlock(ulong flags)
		{
			if ((flags & 0x200) != 0)
				Cpu.EnableInterrupts();
		}

		private static unsafe void TimerInterrupt(InterruptFrame* frame)
		{
			// Acknowledge first: switching away below would otherwise leave the timer in service until this thread runs again
			HyperV.EndOfInterrupt();
			Ticks++;
			if (!s_running)
				return;

			ulong now = UptimeMs;
			while (s_sleeping != null && s_sleeping.WakeTime <= now)
			{
				KThread thread = s_sleeping;
				s_sleeping = thread.Next;
				thread.Next = null;
				// A timed-out waiter keeps its blocks until it runs; InWait = false stops signals from waking it twice
				thread.InWait = false;
				MakeReady(thread);
			}

			if (s_readyHead != null)
			{
				MakeReady(s_current);
				Switch();
			}
		}

		// At the end of a device interrupt whose handler woke a thread: it runs now, as it would after a tick, rather than up to a tick later
		internal static void Preempt()
		{
			if (!s_running || s_readyHead == null)
				return;
			MakeReady(s_current);
			Switch();
		}

		private static void MakeReady(KThread thread)
		{
			if (thread == s_idle)
				return;
			thread.State = ThreadState.Ready;
			thread.Next = null;
			if (s_readyTail == null)
				s_readyHead = thread;
			else
				s_readyTail.Next = thread;
			s_readyTail = thread;
		}

		// Interrupts must be off; resumes whichever thread is next, possibly the current one
		private static unsafe void Switch()
		{
			KThread next = s_readyHead;
			if (next != null)
			{
				s_readyHead = next.Next;
				if (s_readyHead == null)
					s_readyTail = null;
				next.Next = null;
			}
			else
			{
				next = s_current.State == ThreadState.Running ? s_current : s_idle;
			}

			KThread previous = s_current;
			next.State = ThreadState.Running;
			if (next == previous)
				return;
			s_current = next;
			Enter(next);
			fixed (ulong* save = &previous.SavedRsp)
				PhxSwitchContext(save, next.SavedRsp);
		}

		// Per-thread CPU state: the kernel stack for entries from ring 3, and for user threads their address space and GS base
		private static void Enter(KThread next)
		{
			if (next.StackPhysical != 0)
				SystemCalls.SetKernelStack(next.StackTop);
			if (next.Owner is KUserThread user)
			{
				if (s_cr3 != user.Process.Root)
				{
					s_cr3 = user.Process.Root;
					Cpu.WriteCr3(s_cr3);
				}
				Cpu.WriteMsr(SystemCalls.KernelGsBaseMsr, user.Teb);
			}
		}

		private static ulong s_cr3;

		private static void AddTimer(KThread thread)
		{
			if (s_sleeping == null || thread.WakeTime < s_sleeping.WakeTime)
			{
				thread.Next = s_sleeping;
				s_sleeping = thread;
				return;
			}
			KThread at = s_sleeping;
			while (at.Next != null && at.Next.WakeTime <= thread.WakeTime)
				at = at.Next;
			thread.Next = at.Next;
			at.Next = thread;
		}

		private static void RemoveTimer(KThread thread)
		{
			if (s_sleeping == thread)
			{
				s_sleeping = thread.Next;
				thread.Next = null;
				return;
			}
			for (KThread at = s_sleeping; at != null; at = at.Next)
			{
				if (at.Next == thread)
				{
					at.Next = thread.Next;
					thread.Next = null;
					return;
				}
			}
		}

		private static unsafe void IdleLoop(void* argument)
		{
			while (true)
				PhxHalt();
		}

		// Frees dead threads' stacks from a thread context, never from the timer interrupt (MetalOS's reaper)
		private static unsafe void ReaperLoop(void* argument)
		{
			while (true)
			{
				s_reap.Wait();
				ulong flags = Lock();
				KThread dead = s_dead;
				s_dead = null;
				Unlock(flags);
				while (dead != null)
				{
					KThread next = dead.Next;
					dead.FreeStack();
					if (dead.Owner is KUserThread user)
						user.Exited();
					else
						Mm.Heap.Free(dead);
					dead = next;
				}
			}
		}

		[RuntimeImport("*", "PhxSwitchContext")]
		[MethodImpl(MethodImplOptions.InternalCall)]
		private static extern unsafe void PhxSwitchContext(ulong* saveRsp, ulong loadRsp);

		[RuntimeImport("*", "PhxHalt")]
		[MethodImpl(MethodImplOptions.InternalCall)]
		private static extern void PhxHalt();
	}
}
