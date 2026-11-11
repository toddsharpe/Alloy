using System;
using System.Runtime;
using System.Runtime.CompilerServices;
using Internal.Runtime.CompilerHelpers;
using Alloy.Abi;
using Alloy.Arch.X64;

namespace Alloy.Kernel
{
	public static class KernelEnvironment
	{
		// Handed over by the loader in rcx, addressed through the physical window
		public static unsafe BootInfo* BootInfo { get; internal set; }

		// Set once the kernel heap is up; until then `new` is served from the boot arena and nothing is freed
		public static unsafe delegate*<nuint, void*> HeapAllocate;
		public static unsafe delegate*<void*, void> HeapFree;

		// Set once the scheduler runs: the current thread's nonzero id, and a way to let another thread run, for Monitor
		public static unsafe delegate*<uint> CurrentThreadId;
		public static unsafe delegate*<void> Yield;
	}
}

namespace Internal.Runtime.CompilerHelpers
{
	internal static class KernelStartup
	{
		[RuntimeImport("*", "__managed__Main")]
		[MethodImpl(MethodImplOptions.InternalCall)]
		private static extern unsafe int ManagedMain(int argc, char** argv);

		[RuntimeExport("KiSystemStartup")]
		private static unsafe void KiSystemStartup(BootInfo* bootInfo)
		{
			Alloy.Kernel.KernelEnvironment.BootInfo = bootInfo;
			StartupCodeHelpers.InitializeModules();
			ManagedMain(0, null);
			Cpu.HaltForever();
		}
	}
}

namespace System.Runtime
{
	// Bump allocator over a fixed .bss arena until the kernel heap exists (phase 2)
	internal static partial class Allocator
	{
		private static nuint s_bootArenaUsed;

		[RuntimeImport("*", "PhxBootArena")]
		[MethodImpl(MethodImplOptions.InternalCall)]
		private static extern unsafe byte* PhxBootArena(nuint* size);

		private static unsafe partial void* AllocateZeroed(nuint size)
		{
			if (Alloy.Kernel.KernelEnvironment.HeapAllocate != null)
				return Alloy.Kernel.KernelEnvironment.HeapAllocate(size);

			nuint arenaSize;
			byte* arena = PhxBootArena(&arenaSize);
			if (size > arenaSize - s_bootArenaUsed)
				return null;

			// .bss is zeroed by the loader and the arena is never reused, so the memory is already clear
			void* memory = arena + s_bootArenaUsed;
			s_bootArenaUsed += size;
			return memory;
		}

		// Arena memory is never reused; everything else goes back to the kernel heap
		private static unsafe partial void Free(void* memory)
		{
			nuint arenaSize;
			byte* arena = PhxBootArena(&arenaSize);
			if ((byte*)memory >= arena && (byte*)memory < arena + arenaSize)
				return;
			if (Alloy.Kernel.KernelEnvironment.HeapFree != null)
				Alloy.Kernel.KernelEnvironment.HeapFree(memory);
		}
	}
}

namespace System.Threading
{
	public static partial class Monitor
	{
		private static unsafe partial uint CurrentThreadId() =>
			Alloy.Kernel.KernelEnvironment.CurrentThreadId != null ? Alloy.Kernel.KernelEnvironment.CurrentThreadId() : 1;

		private static unsafe partial void Yield()
		{
			if (Alloy.Kernel.KernelEnvironment.Yield == null)
				Environment.FailFast("FAILFAST: a lock contended before the scheduler started");
			Alloy.Kernel.KernelEnvironment.Yield();
		}
	}
}

namespace System
{
	public static partial class Environment
	{
		private static partial void Halt() => Cpu.HaltForever();
	}
}
