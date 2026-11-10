using System;
using System.Runtime;
using System.Runtime.CompilerServices;
using Alloy.Kernel.Diag;
using Alloy.Kernel.Ke;
using Alloy.Kernel.Ps;
using Alloy.Text;

namespace Alloy.Kernel.Arch
{
	// Mirrors what KiInterruptCommon and PhxSyscallEntry push, lowest address first; from ring 3 it is the thread's whole user context
	internal struct InterruptFrame
	{
		public const int Size = 512 + 22 * 8;
		public const ulong SyscallVector = 0x100;

		public unsafe fixed byte FxSave[512];
		public ulong R15;
		public ulong R14;
		public ulong R13;
		public ulong R12;
		public ulong R11;
		public ulong R10;
		public ulong R9;
		public ulong R8;
		public ulong Rdi;
		public ulong Rsi;
		public ulong Rbp;
		public ulong Rbx;
		public ulong Rdx;
		public ulong Rcx;
		public ulong Rax;
		public ulong Vector;
		public ulong ErrorCode;
		public ulong Rip;
		public ulong Cs;
		public ulong Rflags;
		public ulong Rsp;
		public ulong Ss;

		public readonly bool FromUser => (Cs & 3) != 0;

		// Syscall arguments (Abi/Syscalls.cs): rax holds the number, then r10 rdx r8 r9
		public readonly ulong Number => Rax;
		public readonly ulong A0 => R10;
		public readonly ulong A1 => Rdx;
		public readonly ulong A2 => R8;
		public readonly ulong A3 => R9;
	}

	internal static class Interrupts
	{
		public const int Breakpoint = 3;
		public const int PageFault = 14;
		public const int FirstExternal = 32;

		private static unsafe delegate*<InterruptFrame*, void>* s_handlers;

		public static int BreakpointCount { get; private set; }

		public static unsafe void Initialize()
		{
			s_handlers = (delegate*<InterruptFrame*, void>*)Mm.Heap.Allocate(256 * (nuint)sizeof(nint));
		}

		public static unsafe void Register(int vector, delegate*<InterruptFrame*, void> handler) => s_handlers[vector] = handler;

		[RuntimeExport("KiInterruptDispatch")]
		private static unsafe void Dispatch(InterruptFrame* frame)
		{
			int vector = (int)frame->Vector;
			if (vector < FirstExternal)
			{
				HandleException(frame);
				return;
			}

			delegate*<InterruptFrame*, void> handler = s_handlers == null ? null : s_handlers[vector];
			if (handler == null)
				Bugcheck.Fatal(frame, "unexpected interrupt");
			handler(frame);

			if (frame->FromUser && Scheduler.Current.Owner is KUserThread user)
				user.ReturningToUser();
		}

		private static unsafe void HandleException(InterruptFrame* frame)
		{
			// A fault inside a probe or user-copy routine resumes at its recovery point instead of crashing
			nuint count;
			ulong* fixups = PhxFixupTable(&count);
			for (nuint i = 0; i < count; i++)
			{
				if (fixups[i * 2] == frame->Rip)
				{
					frame->Rip = fixups[i * 2 + 1];
					return;
				}
			}

			if (frame->FromUser)
			{
				UserFault(frame);
				return;
			}

			if (frame->Vector == Breakpoint)
			{
				BreakpointCount++;
				Bugcheck.Report(frame, "breakpoint");
				return;
			}

			Bugcheck.Fatal(frame, Bugcheck.ExceptionName((int)frame->Vector));
		}

		// A fault in ring 3 goes to the process's exception dispatcher if it registered one; otherwise it ends that process, never the system
		private static unsafe void UserFault(InterruptFrame* frame)
		{
			KUserThread user = Scheduler.Current.Owner as KUserThread;
			if (user != null && user.Process.ExceptionDispatcher != 0 && ExceptionDelivery.Deliver(user.Process, frame))
				return;
			SpanWriter line = new SpanWriter(stackalloc char[160]);
			line.Append("Process ");
			line.Append(user == null ? "?" : user.Process.Name);
			line.Append(" crashed: ");
			line.Append(Bugcheck.ExceptionName((int)frame->Vector));
			line.Append(" at rip 0x");
			line.AppendHex(frame->Rip, 16);
			if (frame->Vector == PageFault)
			{
				line.Append(", address 0x");
				line.AppendHex(Alloy.Arch.X64.Cpu.ReadCr2(), 16);
			}
			Log.WriteLine(ref line);
			if (user == null)
				Bugcheck.Fatal(frame, "user fault without a process");
			user.Process.Terminate(-1);
			Scheduler.Exit();
		}

		[RuntimeImport("*", "PhxFixupTable")]
		[MethodImpl(MethodImplOptions.InternalCall)]
		private static extern unsafe ulong* PhxFixupTable(nuint* count);
	}
}
