using System.Runtime;
using System.Runtime.CompilerServices;
using System.Runtime.InteropServices;
using Alloy.Abi;
using Alloy.Runtime;

namespace Alloy.User.KTest
{
	// Checks the kernel interfaces the Win32 layer stands on: VirtualAlloc semantics, threads, suspension, contexts and exception dispatch
	internal static unsafe class Program
	{
		private static int s_failed;
		private static volatile int s_workerArgument;
		private static volatile int s_redirected;
		private static int s_exceptions;
		private static uint s_code;
		private static ulong s_address;
		private static ulong s_information0;
		private static ulong s_information1;

		private static int Main()
		{
			Console.WriteLine("ktest: kernel interfaces for the Win32 layer");
			TestMemory();
			TestThreads();
			TestSuspension();
			TestExceptions();
			Console.WriteLine(s_failed == 0 ? "ktest: all passed" : "ktest: some checks failed");

			// Left blocked on purpose: exiting must still end the process, as it must for the runtime's finalizer thread
			Call(Syscall.CreateThread, (ulong)(delegate* unmanaged<ulong, void>)&Blocker, (ulong)Event.Create(), 0, 0);
			Process.Sleep(10);
			return s_failed;
		}

		private static void Check(bool passed, string name)
		{
			Console.WriteLine(string.Concat(passed ? "  ok   " : "  FAIL ", name));
			if (!passed)
				s_failed++;
		}

		private static long Call(Syscall number, ulong a0 = 0, ulong a1 = 0, ulong a2 = 0, ulong a3 = 0) => SystemCall.Invoke(number, a0, a1, a2, a3);

		private static MemoryBasicInformation Query(ulong address)
		{
			MemoryBasicInformation info;
			Call(Syscall.VirtualQuery, address, (ulong)&info);
			return info;
		}

		private static void TestMemory()
		{
			const ulong Gigabyte = 1UL << 30;
			const ulong Megabyte = 1UL << 20;
			ulong reserved = (ulong)Call(Syscall.VirtualAlloc, 0, Gigabyte, Memory.Reserve, Memory.NoAccess);
			MemoryBasicInformation info = Query(reserved);
			Check((long)reserved > 0 && (reserved & 0xFFFF) == 0 && info.State == Memory.Reserve && info.RegionSize == Gigabyte, "reserving 1 GB costs only address space");

			ulong committed = (ulong)Call(Syscall.VirtualAlloc, reserved + Megabyte, Megabyte, Memory.Commit, Memory.ReadWrite);
			ulong* words = (ulong*)committed;
			for (ulong i = 0; i < Megabyte / 8; i += 512)
				words[i] = i * 3;
			bool readBack = committed == reserved + Megabyte;
			for (ulong i = 0; i < Megabyte / 8 && readBack; i += 512)
				readBack = words[i] == i * 3;
			info = Query(committed);
			MemoryBasicInformation below = Query(reserved);
			Check(readBack && info.State == Memory.Commit && info.Protect == Memory.ReadWrite && info.RegionSize == Megabyte && info.AllocationBase == reserved
				&& below.State == Memory.Reserve && below.RegionSize == Megabyte, "committing inside a reservation, and VirtualQuery's runs");

			long old = Call(Syscall.VirtualProtect, committed, 4096, Memory.ReadOnly);
			info = Query(committed);
			Check(old == Memory.ReadWrite && info.Protect == Memory.ReadOnly && info.RegionSize == 4096, "VirtualProtect changes one page");

			Call(Syscall.VirtualFree, committed, Megabyte, Memory.Decommit);
			Check(Query(committed).State == Memory.Reserve, "decommitted pages go back to reserved");
			Call(Syscall.VirtualFree, reserved, 0, Memory.Release);
			Check(Query(reserved).State == Memory.Free, "releasing frees the whole reservation");

			MemoryStatus status;
			Call(Syscall.GetMemoryStatus, (ulong)&status);
			Check(status.TotalPhysical >= 1UL << 31 && status.AvailablePhysical < status.TotalPhysical, "memory status reports the machine");
		}

		private static void TestThreads()
		{
			long thread = Call(Syscall.CreateThread, (ulong)(delegate* unmanaged<ulong, void>)&Worker, 5, 0, ThreadFlags.Suspended);
			Process.Sleep(20);
			Check(thread > 0 && s_workerArgument == 0 && Call(Syscall.GetExitCode, (ulong)thread) == ThreadFlags.StillActive, "a thread created suspended doesn't run");
			long previous = Call(Syscall.ResumeThread, (ulong)thread);
			Process.Wait(thread, 1000);
			Check(previous == 1 && s_workerArgument == 5 && Call(Syscall.GetExitCode, (ulong)thread) == 42, "resumed, it gets its argument and exits with its code");
			Process.Close(thread);
		}

		private static void TestSuspension()
		{
			ulong* counter = (ulong*)Process.Allocate(4096);
			ulong spin = PhxTestSpinAddress();
			long spinner = Call(Syscall.CreateThread, spin, (ulong)counter, 0, 0);
			Process.Sleep(20);
			long previous = Call(Syscall.SuspendThread, (ulong)spinner);
			ulong before = *counter;
			Process.Sleep(20);
			Check(before > 0 && previous == 0 && *counter == before, "a suspended thread stops running");

			WindowsContext context;
			context.ContextFlags = WindowsContext.Full | WindowsContext.ExceptionRequest;
			long got = Call(Syscall.GetThreadContext, (ulong)spinner, (ulong)&context);
			Check(got == 0 && (context.ContextFlags & WindowsContext.ExceptionReporting) != 0 && (context.ContextFlags & WindowsContext.ServiceActive) == 0
				&& context.Rip >= spin && context.Rip < spin + 16 && context.Rcx == (ulong)counter, "GetThreadContext sees it stopped in ring 3");

			context.ContextFlags = WindowsContext.Control;
			context.Rip = (ulong)(delegate* unmanaged<void>)&Redirected;
			context.Rsp = (context.Rsp & ~15UL) - 8;
			long set = Call(Syscall.SetThreadContext, (ulong)spinner, (ulong)&context);
			Call(Syscall.ResumeThread, (ulong)spinner);
			Process.Wait(spinner, 1000);
			Check(set == 0 && s_redirected == 1, "SetThreadContext redirects it when resumed");
			Process.Close(spinner);

			long sleeper = Call(Syscall.CreateThread, (ulong)(delegate* unmanaged<ulong, void>)&Sleeper, 0, 0, 0);
			Process.Sleep(10);
			Call(Syscall.SuspendThread, (ulong)sleeper);
			context.ContextFlags = WindowsContext.Control | WindowsContext.ExceptionRequest;
			Call(Syscall.GetThreadContext, (ulong)sleeper, (ulong)&context);
			Call(Syscall.ResumeThread, (ulong)sleeper);
			Process.Wait(sleeper, 1000);
			Check((context.ContextFlags & WindowsContext.ServiceActive) != 0, "a thread blocked in a syscall reports SERVICE_ACTIVE");
			Process.Close(sleeper);
		}

		private static void TestExceptions()
		{
			Call(Syscall.SetExceptionDispatcher, (ulong)(delegate* unmanaged<WindowsExceptionRecord*, WindowsContext*, void>)&Dispatcher);
			PhxTestBreak();
			Check(s_code == WindowsExceptionRecord.Breakpoint && s_address == PhxTestBreakAddress(), "a breakpoint reaches the dispatcher at its own address and resumes");
			PhxTestWrite((int*)0x10);
			Check(s_code == WindowsExceptionRecord.AccessViolation && s_information0 == 1 && s_information1 == 0x10, "a write to 0x10 is an access violation saying where and how");
			uint quotient = PhxTestDivide(0);
			Check(s_code == WindowsExceptionRecord.IntegerDivideByZero, "dividing by zero is reported as such");
			Check(s_exceptions == 3 && PhxTestDivide(4) == 25, "each fault was dispatched once and the program carries on");
		}

		[UnmanagedCallersOnly]
		private static void Worker(ulong argument)
		{
			s_workerArgument = (int)argument;
			SystemCall.Invoke(Syscall.ExitThread, 42);
		}

		[UnmanagedCallersOnly]
		private static void Redirected()
		{
			s_redirected = 1;
			SystemCall.Invoke(Syscall.ExitThread, 0);
		}

		[UnmanagedCallersOnly]
		private static void Blocker(ulong neverSignaled)
		{
			Process.Wait((long)neverSignaled);
		}

		[UnmanagedCallersOnly]
		private static void Sleeper(ulong argument)
		{
			Process.Sleep(50);
			SystemCall.Invoke(Syscall.ExitThread, 0);
		}

		// Kernel-delivered faults land here; the breakpoint resumes after itself, the others return from the helper that faulted
		[UnmanagedCallersOnly]
		private static void Dispatcher(WindowsExceptionRecord* record, WindowsContext* context)
		{
			s_exceptions++;
			s_code = record->ExceptionCode;
			s_address = record->ExceptionAddress;
			s_information0 = record->ExceptionInformation[0];
			s_information1 = record->ExceptionInformation[1];
			if (record->ExceptionCode == WindowsExceptionRecord.Breakpoint)
			{
				context->Rip++;
			}
			else
			{
				context->Rip = *(ulong*)context->Rsp;
				context->Rsp += 8;
			}
			PhxTestRestore(context);
		}

		[RuntimeImport("*", "PhxTestBreak")]
		[MethodImpl(MethodImplOptions.InternalCall)]
		private static extern void PhxTestBreak();

		[RuntimeImport("*", "PhxTestBreakAddress")]
		[MethodImpl(MethodImplOptions.InternalCall)]
		private static extern ulong PhxTestBreakAddress();

		[RuntimeImport("*", "PhxTestWrite")]
		[MethodImpl(MethodImplOptions.InternalCall)]
		private static extern void PhxTestWrite(int* address);

		[RuntimeImport("*", "PhxTestDivide")]
		[MethodImpl(MethodImplOptions.InternalCall)]
		private static extern uint PhxTestDivide(uint divisor);

		[RuntimeImport("*", "PhxTestSpinAddress")]
		[MethodImpl(MethodImplOptions.InternalCall)]
		private static extern ulong PhxTestSpinAddress();

		[RuntimeImport("*", "PhxTestRestore")]
		[MethodImpl(MethodImplOptions.InternalCall)]
		private static extern void PhxTestRestore(WindowsContext* context);
	}
}
