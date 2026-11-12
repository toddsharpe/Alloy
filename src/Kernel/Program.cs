using System;
using Alloy.Abi;
using Alloy.Arch.X64;
using Alloy.Kernel.Arch;
using Alloy.Kernel.Diag;
using Alloy.Kernel.Io;
using Alloy.Kernel.Ke;
using Alloy.Kernel.Mm;
using Alloy.Kernel.Platform;
using Alloy.Kernel.Ps;
using Alloy.Text;

namespace Alloy.Kernel
{
	internal static class Program
	{
		private const ulong Cr0WriteProtect = 1 << 16;
		private const ulong Cr4OsXsave = 1 << 18;
		private const ulong Cr4Smep = 1 << 20;
		private const ulong Cr4Smap = 1 << 21;
		private const ulong Xcr0X87Sse = 0x3;
		private const ulong Xcr0Avx = 0x4;

		// A static, so the compiler can't turn the deliberate fault into a null-reference throw helper
		private static ulong s_faultAddress;
		private static bool s_avx;

		private static unsafe void Main()
		{
			EnableExtendedState();
			Log.Initialize();
			BootInfo* info = KernelEnvironment.BootInfo;
			if (!info->IsValid)
				Environment.FailFast("FAILFAST: BootInfo is missing or from a different loader version");

			Console.Initialize(info);
			Bugcheck.Initialize(info);
			StackWalker.Initialize(info->KernelVirtual);
			Log.WriteLine("Alloy Kernel");

			SpanWriter line = new SpanWriter(stackalloc char[128]);
			line.Append("Kernel 0x");
			line.AppendHex(info->KernelVirtual, 16);
			line.Append(", ");
			line.Append(info->FramebufferWidth);
			line.Append('x');
			line.Append(info->FramebufferHeight);
			line.Append(", symbols ");
			line.Append(info->SymbolsSize / 1024);
			line.Append(" KB");
			Log.WriteLine(ref line);

			Descriptors.Initialize();
			Log.WriteLine("Arch: GDT, TSS and IDT loaded");

			PhysicalMemory.Initialize(info);
			KernelSpace.Initialize(info);
			Heap.Initialize();
			KernelEnvironment.HeapAllocate = &Heap.Allocate;
			KernelEnvironment.HeapFree = &FreeBlock;
			Interrupts.Initialize();
			EnableProtection();

			line = new SpanWriter(stackalloc char[128]);
			line.Append("Mm: ");
			line.Append(PhysicalMemory.FreePages * MemoryLayout.PageSize >> 20);
			line.Append(" MB free of ");
			line.Append(PhysicalMemory.TotalPages * MemoryLayout.PageSize >> 20);
			line.Append(" MB, kernel tables at 0x");
			line.AppendHex(KernelSpace.Root, 8);
			Log.WriteLine(ref line);

			HyperV.Initialize();
			Interrupts.Register(0xFF, &SpuriousInterrupt);
			SystemCalls.Initialize();
			Scheduler.Initialize();
			KernelEnvironment.CurrentThreadId = &CurrentThreadId;
			KernelEnvironment.Yield = &Scheduler.Yield;
			line = new SpanWriter(stackalloc char[96]);
			line.Append("Ke: Hyper-V reference time ");
			line.Append(Scheduler.UptimeMs);
			line.Append(" ms, ");
			line.Append(Scheduler.TickHz);
			line.Append(" Hz STIMER0 tick");
			Log.WriteLine(ref line);

			bool passed = SelfTest.Run();
			if (HasArgument(info, "bugcheck-test"))
			{
				Log.WriteLine("Command line asked for a bugcheck: writing through a null pointer");
				*(int*)s_faultAddress = 1;
			}

			Log.WriteLine(passed ? "Alloy Kernel: ready" : "Alloy Kernel: SELFTEST FAILED");
			Scheduler.Start("status", &StatusLoop, null);
			StartDevices();
			Storage.Mount(SyntheticScsi.Active);

			if (HasArgument(info, "usertest"))
				RunUserTest("bin/ktest.exe");
			if (KProcess.Create("bin/init.exe", 0, "/") == null)
				Environment.FailFast("FAILFAST: cannot start bin/init.exe from the Alloy partition");
			while (true)
				Scheduler.Sleep(1000);
		}

		// boot_test's -KernelArgs usertest: a ring-3 test program runs to completion before init starts
		private static void RunUserTest(string path)
		{
			KProcess test = KProcess.Create(path, 0, "/");
			if (test == null)
				Log.WriteLine("usertest: could not start the test program");
			else
				test.Wait();
		}

		private static unsafe void SpuriousInterrupt(InterruptFrame* frame)
		{
		}

		// Keyboard, mouse, NIC and the boot disk arrive over VMBus; without the first three the system still boots, without the disk it has no files
		private static void StartDevices()
		{
			Input.Initialize();
			Net.Initialize();
			if (!VmBus.Initialize())
				return;
			bool keyboard = VmBus.TryFind(VmBus.KeyboardLow, VmBus.KeyboardHigh, out VmBusOffer offer) && SyntheticKeyboard.Start(in offer);
			bool mouse = VmBus.TryFind(VmBus.MouseLow, VmBus.MouseHigh, out offer) && SyntheticMouse.Start(in offer);
			bool nic = VmBus.TryFind(VmBus.NetworkLow, VmBus.NetworkHigh, out offer) && SyntheticNic.Start(in offer);
			bool disk = VmBus.TryFind(VmBus.ScsiLow, VmBus.ScsiHigh, out offer) && SyntheticScsi.Start(in offer);

			SpanWriter line = new SpanWriter(stackalloc char[128]);
			line.Append("VmBus: ");
			line.Append(VmBus.OfferCount);
			line.Append(" channels offered, keyboard ");
			line.Append(keyboard ? "ready" : "missing");
			line.Append(", mouse ");
			line.Append(mouse ? "ready" : "missing");
			line.Append(", network ");
			line.Append(nic ? "ready" : "missing");
			line.Append(", disk ");
			line.Append(disk ? "ready" : "missing");
			Log.WriteLine(ref line);
			StartIntegrationComponents();
		}

		// The host's integration components, each optional: without one Hyper-V Manager just can't ask for that service
		private static void StartIntegrationComponents()
		{
			bool shutdown = VmBus.TryFind(VmBus.ShutdownLow, VmBus.ShutdownHigh, out VmBusOffer offer) && ShutdownComponent.Start(in offer);
			bool heartbeat = VmBus.TryFind(VmBus.HeartbeatLow, VmBus.HeartbeatHigh, out offer) && HeartbeatComponent.Start(in offer);
			bool timeSync = VmBus.TryFind(VmBus.TimeSyncLow, VmBus.TimeSyncHigh, out offer) && TimeSyncComponent.Start(in offer);
			bool kvp = VmBus.TryFind(VmBus.KvpLow, VmBus.KvpHigh, out offer) && KvpComponent.Start(in offer);

			SpanWriter line = new SpanWriter(stackalloc char[128]);
			line.Append("VmBus: integration components: shutdown ");
			line.Append(shutdown ? "ready" : "missing");
			line.Append(", heartbeat ");
			line.Append(heartbeat ? "ready" : "missing");
			line.Append(", time sync ");
			line.Append(timeSync ? "ready" : "missing");
			line.Append(", kvp ");
			line.Append(kvp ? "ready" : "missing");
			Log.WriteLine(ref line);
		}

		// Redraws uptime, ticks and threads in the title bar ten times a second, so a screenshot shows the scheduler running
		private static unsafe void FreeBlock(void* block) => Heap.Free(block);

		// Thread ids start at 0, and Monitor needs a nonzero owner
		private static uint CurrentThreadId() => (uint)Scheduler.Current.Id + 1;

		private static unsafe void StatusLoop(void* argument)
		{
			Span<char> buffer = stackalloc char[80];
			while (true)
			{
				SpanWriter line = new SpanWriter(buffer);
				line.Append("uptime ");
				line.Append(Scheduler.UptimeMs / 1000);
				line.Append('.');
				line.Append(Scheduler.UptimeMs / 100 % 10);
				line.Append("s  ticks ");
				line.Append(Scheduler.Ticks);
				line.Append("  threads ");
				line.Append(Scheduler.ThreadCount);
				Diag.Console.Status(line.Written);
				Scheduler.Sleep(100);
			}
		}

		// XSAVE with x87, SSE and AVX state, before the first interrupt stub runs xsave64 (Arch.asm), so frames carry the upper halves of the ymm registers ring 3 uses; wider state stays off
		private static void EnableExtendedState()
		{
			CpuIdResult basic = Cpu.CpuId(1);
			if ((basic.Ecx & (1u << 26)) == 0)
				Cpu.HaltForever();
			s_avx = (basic.Ecx & (1u << 28)) != 0;
			Cpu.WriteCr4(Cpu.ReadCr4() | Cr4OsXsave);
			Cpu.WriteXcr0(s_avx ? Xcr0X87Sse | Xcr0Avx : Xcr0X87Sse);
			if (Cpu.CpuId(0xD).Ebx > InterruptFrame.XSaveSize)
				Cpu.HaltForever();
		}

		// Write protection always; SMEP and SMAP when the CPU has them, so ring 0 can't run or touch user pages by accident
		private static void EnableProtection()
		{
			Cpu.WriteCr0(Cpu.ReadCr0() | Cr0WriteProtect);
			CpuIdResult features = Cpu.CpuId(7);
			ulong cr4 = Cpu.ReadCr4();
			bool smep = (features.Ebx & (1u << 7)) != 0;
			bool smap = (features.Ebx & (1u << 20)) != 0;
			if (smep)
				cr4 |= Cr4Smep;
			if (smap)
				cr4 |= Cr4Smap;
			Cpu.WriteCr4(cr4);

			SpanWriter line = new SpanWriter(stackalloc char[96]);
			line.Append("Protection: WP, NX");
			line.Append(smep ? ", SMEP" : "");
			line.Append(smap ? ", SMAP" : "");
			line.Append(s_avx ? "; XSAVE with AVX" : "; XSAVE");
			Log.WriteLine(ref line);
		}

		private static unsafe bool HasArgument(BootInfo* info, string argument)
		{
			if (info->CommandLine == 0)
				return false;
			ReadOnlySpan<byte> commandLine = PhysicalMemory.Window(info->CommandLine, info->CommandLineSize);
			for (int start = 0; start + argument.Length <= commandLine.Length; start++)
			{
				int i = 0;
				while (i < argument.Length && commandLine[start + i] == argument[i])
					i++;
				if (i == argument.Length)
					return true;
			}
			return false;
		}
	}
}
