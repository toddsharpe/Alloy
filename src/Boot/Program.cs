using System;
using System.Runtime;
using System.Runtime.CompilerServices;
using Alloy.Abi;
using Alloy.Arch.X64;
using Alloy.Efi;
using Alloy.Text;

namespace Alloy.Boot
{
	internal static class Program
	{
		private const ulong BootStackSize = 64 * 1024;
		private const uint EferMsr = 0xC0000080;
		private const ulong EferNoExecuteEnable = 1 << 11;

		private static unsafe void Main()
		{
			Log.Initialize();
			Log.WriteLine("Alloy Boot (phase 1)");
			PrintPlatform();
			if (!SelfTest.Run())
			{
				Log.WriteLine("Alloy Boot: SELFTEST FAILED");
				Cpu.HaltForever();
			}

			BootInfo* info = (BootInfo*)Uefi.AllocateZeroedPages(1);
			info->Magic = BootInfo.ExpectedMagic;
			info->Version = BootInfo.CurrentVersion;
			info->Size = (uint)sizeof(BootInfo);

			EfiFileProtocol* root = Uefi.OpenBootVolume();
			KernelImage kernel = KernelImage.Load(root, "\\EFI\\ALLOY\\kernel.exe");
			info->KernelPhysical = kernel.Physical;
			info->KernelVirtual = kernel.ImageBase;
			info->KernelImageSize = kernel.SizeOfImage;
			Uefi.ReadFile(root, "\\EFI\\ALLOY\\kernel.sym", true, out info->Symbols, out info->SymbolsSize);
			Uefi.ReadFile(root, "\\EFI\\ALLOY\\cmdline.txt", true, out info->CommandLine, out info->CommandLineSize);

			ReadFramebuffer(info);
			info->AcpiRsdp = (ulong)Uefi.FindConfigurationTable(EfiGuids.Acpi20Table);
			info->UefiRuntimeServices = (ulong)EfiApplication.SystemTable->RuntimeServices;
			info->BootStack = Uefi.AllocateZeroedPages(MemoryLayout.PagesFor(BootStackSize));
			info->BootStackSize = BootStackSize;
			ReadTime(info);

			MemoryMap map = MemoryMap.Allocate();
			map.Refresh();
			BootPageTables.Build(info, kernel, map);
			Describe(info, kernel);

			// The tables mark data NX, which faults as a reserved bit unless EFER.NXE is on
			Cpu.WriteMsr(EferMsr, Cpu.ReadMsr(EferMsr) | EferNoExecuteEnable);

			map.ExitBootServices();
			EfiApplication.MarkBootServicesExited();

			// Nothing below may allocate: the pool is gone, so only stack buffers and the serial port remain
			map.SetVirtualAddressMap(info);
			info->MemoryMap = map.Buffer;
			info->MemoryMapSize = map.Size;
			info->MemoryDescriptorSize = map.DescriptorSize;
			info->MemoryDescriptorVersion = map.DescriptorVersion;

			SpanWriter line = new SpanWriter(stackalloc char[96]);
			line.Append("Entering kernel at 0x");
			line.AppendHex(kernel.EntryPoint, 16);
			Log.WriteLine(ref line);

			ulong stackTop = BootInfo.ToVirtual(info->BootStack + info->BootStackSize);
			PhxEnterKernel(info->PageTableRoot, stackTop, kernel.EntryPoint, BootInfo.ToVirtual((ulong)info));
		}

		// Loads cr3 and rsp then calls the kernel; never returns (Trampoline.asm)
		[RuntimeImport("*", "PhxEnterKernel")]
		[MethodImpl(MethodImplOptions.InternalCall)]
		private static extern void PhxEnterKernel(ulong pageTableRoot, ulong stackTop, ulong entryPoint, ulong bootInfo);

		private static unsafe void PrintPlatform()
		{
			EfiSystemTable* st = EfiApplication.SystemTable;
			SpanWriter line = new SpanWriter(stackalloc char[160]);
			line.Append("Firmware: ");
			line.Append(new ReadOnlySpan<char>(st->FirmwareVendor, Strlen(st->FirmwareVendor)));
			line.Append(" rev 0x");
			line.AppendHex(st->FirmwareRevision, 8);
			line.Append(", UEFI ");
			line.Append(st->Hdr.Revision >> 16);
			line.Append('.');
			line.Append((st->Hdr.Revision & 0xFFFF) / 10);
			Log.WriteLine(ref line);

			line = new SpanWriter(stackalloc char[160]);
			line.Append("CPU: ");
			AppendCpuVendor(ref line);
			Log.WriteLine(ref line);
		}

		// GOP hangs off the console handle on Hyper-V's firmware (MetalOS EfiDevice.cpp)
		private static unsafe void ReadFramebuffer(BootInfo* info)
		{
			EfiGraphicsOutputProtocol* gop = Uefi.OpenProtocol<EfiGraphicsOutputProtocol>(EfiApplication.SystemTable->ConsoleOutHandle, EfiGuids.GraphicsOutputProtocol, "GraphicsOutput");
			EfiGraphicsOutputModeInformation* mode = gop->Mode->Info;
			if (mode->PixelFormat != EfiPixelFormat.BlueGreenRedReserved8)
				Environment.FailFast("FAILFAST: framebuffer is not 32-bit BGRX");

			info->Framebuffer = gop->Mode->FrameBufferBase;
			info->FramebufferSize = gop->Mode->FrameBufferSize;
			info->FramebufferWidth = mode->HorizontalResolution;
			info->FramebufferHeight = mode->VerticalResolution;
			info->FramebufferPixelsPerScanLine = mode->PixelsPerScanLine;
		}

		private static unsafe void ReadTime(BootInfo* info)
		{
			EfiTime time;
			if (EfiStatus.IsError(EfiApplication.SystemTable->RuntimeServices->GetTime(&time, null)))
				return;
			info->BootYear = time.Year;
			info->BootMonth = time.Month;
			info->BootDay = time.Day;
			info->BootHour = time.Hour;
			info->BootMinute = time.Minute;
			info->BootSecond = time.Second;
		}

		private static unsafe void Describe(BootInfo* info, KernelImage kernel)
		{
			SpanWriter line = new SpanWriter(stackalloc char[192]);
			line.Append("Kernel 0x");
			line.AppendHex(kernel.ImageBase, 16);
			line.Append(" (");
			line.Append(kernel.SizeOfImage / 1024);
			line.Append(" KB), symbols ");
			line.Append(info->SymbolsSize / 1024);
			line.Append(" KB, framebuffer ");
			line.Append(info->FramebufferWidth);
			line.Append('x');
			line.Append(info->FramebufferHeight);
			line.Append(", window ");
			line.Append(info->PhysicalWindowSize >> 30);
			line.Append(" GB");
			Log.WriteLine(ref line);
		}

		private static unsafe int Strlen(char* s)
		{
			int length = 0;
			while (s[length] != '\0')
				length++;
			return length;
		}

		private static unsafe void AppendCpuVendor(ref SpanWriter writer)
		{
			CpuIdResult id = Cpu.CpuId(0);
			uint* registers = stackalloc uint[] { id.Ebx, id.Edx, id.Ecx };
			for (int i = 0; i < 12; i++)
				writer.Append((char)((byte*)registers)[i]);

			// Hypervisor vendor leaf, present when CPUID.1:ECX bit 31 is set (Hyper-V reports "Microsoft Hv")
			if ((Cpu.CpuId(1).Ecx & (1u << 31)) != 0)
			{
				CpuIdResult hv = Cpu.CpuId(0x40000000);
				uint* hvRegisters = stackalloc uint[] { hv.Ebx, hv.Ecx, hv.Edx };
				writer.Append(", hypervisor ");
				for (int i = 0; i < 12; i++)
					writer.Append((char)((byte*)hvRegisters)[i]);
			}
		}
	}
}
