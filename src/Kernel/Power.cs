using Alloy.Abi;
using Alloy.Arch.X64;
using Alloy.Efi;
using Alloy.Kernel.Io;

namespace Alloy.Kernel
{
	// Powering off and restarting through UEFI's ResetSystem, in the runtime services table the loader mapped into the kernel's UEFI window
	internal static class Power
	{
		// What was written reaches the disk first; nothing runs again after the reset
		public static unsafe void Shutdown(bool restart)
		{
			Log.WriteLine(restart ? "Power: restarting" : "Power: powering off");
			if (!Storage.Flush())
				Log.WriteLine("Power: the file system did not flush");

			// The reset tears the COM1 pipe down with whatever the host's reader hasn't collected, so the lines above get a moment to arrive
			Ke.Scheduler.Sleep(250);
			Cpu.DisableInterrupts();
			EfiRuntimeServices* runtime = (EfiRuntimeServices*)(MemoryLayout.KernelUefiRuntime + KernelEnvironment.BootInfo->UefiRuntimeServices);
			runtime->ResetSystem(restart ? EfiResetType.Cold : EfiResetType.Shutdown, 0, 0, null);
			Log.WriteLine("Power: ResetSystem returned");
			Cpu.HaltForever();
		}
	}
}
