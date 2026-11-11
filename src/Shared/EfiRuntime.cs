using System;

namespace Alloy.Efi
{
	// The UEFI runtime services and what they take: the loader calls GetTime and SetVirtualAddressMap, the kernel ResetSystem through the table the loader mapped into its UEFI window

	public enum EfiMemoryType : uint
	{
		ReservedMemoryType,
		LoaderCode,
		LoaderData,
		BootServicesCode,
		BootServicesData,
		RuntimeServicesCode,
		RuntimeServicesData,
		ConventionalMemory,
		UnusableMemory,
		AcpiReclaimMemory,
		AcpiMemoryNvs,
		MemoryMappedIo,
		MemoryMappedIoPortSpace,
		PalCode,
		PersistentMemory,
	}

	public enum EfiResetType : uint
	{
		Cold,
		Warm,
		Shutdown,
		PlatformSpecific,
	}

	public struct EfiTableHeader
	{
		public ulong Signature;
		public uint Revision;
		public uint HeaderSize;
		public uint Crc32;
		public uint Reserved;
	}

	public struct EfiMemoryDescriptor
	{
		public EfiMemoryType Type;
		public uint Pad;
		public ulong PhysicalStart;
		public ulong VirtualStart;
		public ulong NumberOfPages;
		public ulong Attribute;
	}

	public struct EfiTime
	{
		public ushort Year;
		public byte Month;
		public byte Day;
		public byte Hour;
		public byte Minute;
		public byte Second;
		public byte Pad1;
		public uint Nanosecond;
		public short TimeZone;
		public byte Daylight;
		public byte Pad2;
	}

	public unsafe struct EfiRuntimeServices
	{
		public EfiTableHeader Hdr;

		public delegate* unmanaged<EfiTime*, void*, ulong> GetTime;
		public void* SetTime;
		public void* GetWakeupTime;
		public void* SetWakeupTime;

		public delegate* unmanaged<nuint, nuint, uint, EfiMemoryDescriptor*, ulong> SetVirtualAddressMap;
		public void* ConvertPointer;

		public void* GetVariable;
		public void* GetNextVariableName;
		public void* SetVariable;

		public void* GetNextHighMonotonicCount;
		public delegate* unmanaged<EfiResetType, ulong, nuint, void*, void> ResetSystem;

		public void* UpdateCapsule;
		public void* QueryCapsuleCapabilities;
		public void* QueryVariableInfo;
	}
}
