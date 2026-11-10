using System;
using System.Runtime;
using System.Runtime.CompilerServices;
using Internal.Runtime.CompilerHelpers;
using Alloy.Abi;
using Alloy.Arch.X64;

namespace Alloy.Kernel
{
	public static unsafe class KernelEnvironment
	{
		// Handed over by the loader in rcx, addressed through the physical window
		public static BootInfo* BootInfo { get; internal set; }

		// Set once the kernel heap is up; until then `new` is served from the boot arena
		public static delegate*<nuint, void*> HeapAllocate;
	}
}

namespace Internal.Runtime.CompilerHelpers
{
	internal static unsafe class KernelStartup
	{
		[RuntimeImport("*", "__managed__Main")]
		[MethodImpl(MethodImplOptions.InternalCall)]
		private static extern int ManagedMain(int argc, char** argv);

		[RuntimeExport("KiSystemStartup")]
		private static void KiSystemStartup(BootInfo* bootInfo)
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
	internal static unsafe partial class Allocator
	{
		private static nuint s_bootArenaUsed;

		[RuntimeImport("*", "PhxBootArena")]
		[MethodImpl(MethodImplOptions.InternalCall)]
		private static extern byte* PhxBootArena(nuint* size);

		private static partial void* AllocateZeroed(nuint size)
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
	}
}

namespace System
{
	public static partial class Environment
	{
		private static partial void Halt() => Cpu.HaltForever();
	}
}
