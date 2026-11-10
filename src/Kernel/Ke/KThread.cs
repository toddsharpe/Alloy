using System.Runtime;
using System.Runtime.CompilerServices;
using Alloy.Abi;
using Alloy.Kernel.Mm;

namespace Alloy.Kernel.Ke
{
	internal enum ThreadState
	{
		Ready,
		Running,
		Waiting,
		Terminated,
	}

	// A kernel thread: its own stack, the rsp saved by PhxSwitchContext, and a link for whichever queue it is on
	internal sealed unsafe class KThread
	{
		public const ulong StackPages = 8;

		private static int s_nextId;

		public readonly int Id;
		public readonly string Name;
		public ulong SavedRsp;
		public ulong StackPhysical;
		public ThreadState State;
		public KThread Next;
		public KThread WaitNext;
		public ulong WakeTime;
		public KWaitBlock WaitBlocks;
		public int WaitIndex;
		public bool InWait;
		public object Owner;

		private KThread(string name)
		{
			Id = ++s_nextId;
			Name = name;
		}

		public ulong StackTop => BootInfo.ToVirtual(StackPhysical + StackPages * MemoryLayout.PageSize);

		// The thread already running (the boot context) adopts a KThread without a new stack
		public static KThread ForCurrent(string name) => new KThread(name) { State = ThreadState.Running };

		// reserved bytes at the top of the stack stay free: a user thread keeps its user frame there
		public static KThread Create(string name, delegate*<void*, void> entry, void* argument, int reserved = 0)
		{
			KThread thread = new KThread(name);
			thread.StackPhysical = PhysicalMemory.Allocate(StackPages);
			if (thread.StackPhysical == 0)
				System.Environment.FailFast("FAILFAST: out of memory for a thread stack");

			// Layout PhxSwitchContext unwinds: 168 bytes of XMM6-15, r15 r14 r13 r12 rsi rdi rbp rbx, return address
			ulong* returnSlot = (ulong*)((thread.StackTop - (ulong)reserved - 16) & ~15UL);
			*returnSlot = PhxThreadStartAddress();
			ulong* registers = returnSlot - 8;
			registers[2] = (ulong)entry;
			registers[3] = (ulong)argument;
			thread.SavedRsp = (ulong)registers - 168;
			thread.State = ThreadState.Ready;
			return thread;
		}

		public void FreeStack()
		{
			if (StackPhysical != 0)
				PhysicalMemory.Free(StackPhysical, StackPages);
			StackPhysical = 0;
		}

		[RuntimeImport("*", "PhxThreadStartAddress")]
		[MethodImpl(MethodImplOptions.InternalCall)]
		private static extern ulong PhxThreadStartAddress();
	}
}
