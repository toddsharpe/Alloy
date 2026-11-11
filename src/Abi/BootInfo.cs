namespace Alloy.Abi
{
	// Handed from the loader to KiSystemStartup in rcx, through the physical window; every address field is physical
	public struct BootInfo
	{
		public const ulong ExpectedMagic = 0x31544F4F42584850; // "PHXBOOT1"
		public const uint CurrentVersion = 2;

		public ulong Magic;
		public uint Version;
		public uint Size;

		// The final UEFI memory map, after ExitBootServices and SetVirtualAddressMap
		public ulong MemoryMap;
		public ulong MemoryMapSize;
		public ulong MemoryDescriptorSize;
		public uint MemoryDescriptorVersion;
		public uint Reserved0;

		public ulong KernelPhysical;
		public ulong KernelVirtual;
		public ulong KernelImageSize;

		// Optional files; zero when absent
		public ulong Symbols;
		public ulong SymbolsSize;
		public ulong CommandLine;
		public ulong CommandLineSize;

		public ulong Framebuffer;
		public ulong FramebufferSize;
		public uint FramebufferWidth;
		public uint FramebufferHeight;
		public uint FramebufferPixelsPerScanLine;
		public uint Reserved1;

		public ulong AcpiRsdp;
		public ulong UefiRuntimeServices;

		public ulong PageTableRoot;
		public ulong PhysicalWindowSize;
		public ulong BootStack;
		public ulong BootStackSize;

		// Wall clock read from UEFI just before ExitBootServices
		public ushort BootYear;
		public byte BootMonth;
		public byte BootDay;
		public byte BootHour;
		public byte BootMinute;
		public byte BootSecond;
		public byte Reserved2;

		public readonly unsafe bool IsValid => Magic == ExpectedMagic && Version == CurrentVersion && Size == sizeof(BootInfo);

		public static ulong ToVirtual(ulong physical) => MemoryLayout.KernelPhysicalWindow + physical;
	}

	// Layout of an EFI_MEMORY_DESCRIPTOR; the map's stride is MemoryDescriptorSize, which can be larger than this
	public struct MemoryDescriptor
	{
		public const ulong RuntimeAttribute = 0x8000000000000000;

		public MemoryType Type;
		public uint Pad;
		public ulong PhysicalStart;
		public ulong VirtualStart;
		public ulong NumberOfPages;
		public ulong Attribute;

		public readonly ulong PhysicalEnd => PhysicalStart + (NumberOfPages << MemoryLayout.PageShift);
	}

	public enum MemoryType : uint
	{
		Reserved,
		LoaderCode,
		LoaderData,
		BootServicesCode,
		BootServicesData,
		RuntimeServicesCode,
		RuntimeServicesData,
		Conventional,
		Unusable,
		AcpiReclaim,
		AcpiNvs,
		MemoryMappedIo,
		MemoryMappedIoPortSpace,
		PalCode,
		Persistent,
	}
}
