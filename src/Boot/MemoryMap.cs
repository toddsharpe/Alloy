using System;
using Alloy.Abi;
using Alloy.Efi;
using Alloy.Text;

namespace Alloy.Boot
{
	// The UEFI memory map in a buffer sized once up front, so the final read before ExitBootServices needs no allocation
	internal unsafe struct MemoryMap
	{
		private const int SlackDescriptors = 64;

		public ulong Buffer;
		public ulong Capacity;
		public ulong Size;
		public ulong DescriptorSize;
		public uint DescriptorVersion;
		public nuint Key;

		public readonly int Count => DescriptorSize == 0 ? 0 : (int)(Size / DescriptorSize);

		public readonly MemoryDescriptor* this[int index] => (MemoryDescriptor*)(Buffer + (ulong)index * DescriptorSize);

		public static MemoryMap Allocate()
		{
			nuint size = 0;
			nuint key;
			nuint descriptorSize;
			uint version;
			ulong status = EfiApplication.SystemTable->BootServices->GetMemoryMap(&size, null, &key, &descriptorSize, &version);
			if (status != EfiStatus.BufferTooSmall)
				Uefi.Check(status, "GetMemoryMap size");

			MemoryMap map = default;
			map.Capacity = MemoryLayout.PagesFor(size + SlackDescriptors * descriptorSize) * MemoryLayout.PageSize;
			map.Buffer = Uefi.AllocatePages(map.Capacity / MemoryLayout.PageSize);
			return map;
		}

		public void Refresh()
		{
			nuint size = (nuint)Capacity;
			nuint key;
			nuint descriptorSize;
			uint version;
			Uefi.Check(EfiApplication.SystemTable->BootServices->GetMemoryMap(&size, (EfiMemoryDescriptor*)Buffer, &key, &descriptorSize, &version), "GetMemoryMap");
			Size = size;
			Key = key;
			DescriptorSize = descriptorSize;
			DescriptorVersion = version;
		}

		// End of the highest RAM-backed range; MMIO is left out so a high device window can't inflate the physical window
		public readonly ulong HighestRamAddress()
		{
			ulong highest = 0;
			for (int i = 0; i < Count; i++)
			{
				MemoryDescriptor* descriptor = this[i];
				if (descriptor->Type != MemoryType.MemoryMappedIo && descriptor->Type != MemoryType.MemoryMappedIoPortSpace && descriptor->Type != MemoryType.Reserved)
					highest = Math.Max(highest, descriptor->PhysicalEnd);
			}
			return highest;
		}

		// The key goes stale whenever firmware touches the map, so on a mismatch read it again and retry once
		public void ExitBootServices()
		{
			EfiBootServices* bootServices = EfiApplication.SystemTable->BootServices;
			Refresh();
			ulong status = bootServices->ExitBootServices(EfiApplication.ImageHandle, Key);
			if (status == EfiStatus.InvalidParameter)
			{
				Refresh();
				status = bootServices->ExitBootServices(EfiApplication.ImageHandle, Key);
			}
			Uefi.Check(status, "ExitBootServices");
		}

		// Moves runtime services into the kernel's UEFI window, where BootPageTables already mapped them
		public void SetVirtualAddressMap(BootInfo* info)
		{
			for (int i = 0; i < Count; i++)
			{
				MemoryDescriptor* descriptor = this[i];
				if ((descriptor->Attribute & MemoryDescriptor.RuntimeAttribute) == 0)
					continue;

				descriptor->VirtualStart = MemoryLayout.KernelUefiRuntime + descriptor->PhysicalStart;
				if (!BootPageTables.IsMapped(descriptor->VirtualStart, descriptor->PhysicalStart, descriptor->NumberOfPages))
				{
					SpanWriter line = new SpanWriter(stackalloc char[96]);
					line.Append("FAILFAST: runtime range 0x");
					line.AppendHex(descriptor->PhysicalStart, 16);
					line.Append(" appeared after the page tables were built");
					Log.WriteLine(ref line);
					Environment.FailFast("FAILFAST: unmapped UEFI runtime range");
				}
			}

			EfiRuntimeServices* runtime = EfiApplication.SystemTable->RuntimeServices;
			Uefi.Check(runtime->SetVirtualAddressMap((nuint)Size, (nuint)DescriptorSize, DescriptorVersion, (EfiMemoryDescriptor*)Buffer), "SetVirtualAddressMap");
		}
	}
}
