namespace Alloy.Abi
{
	// Virtual address layout; the whole kernel lives in PML4 slot 256 so every process shares it
	public static class MemoryLayout
	{
		public const ulong PageSize = 0x1000;
		public const int PageShift = 12;
		public const ulong LargePageSize = 0x200000;
		public const ulong HugePageSize = 0x40000000;

		public const ulong UserStart = 0x0000_0000_0001_0000;
		public const ulong UserEnd = 0x0000_7FFF_FFFF_FFFF;

		public const ulong KernelStart = 0xFFFF_8000_0000_0000;
		public const ulong KernelBase = 0xFFFF_8000_0100_0000;
		public const ulong KernelSymbols = 0xFFFF_8000_1000_0000;
		public const ulong KernelHeap = 0xFFFF_8000_2000_0000;
		public const ulong KernelAlloc = 0xFFFF_8001_0000_0000;
		public const ulong KernelFramebuffer = 0xFFFF_8002_0000_0000;
		public const ulong KernelPfnDatabase = 0xFFFF_8003_0000_0000;
		public const ulong KernelUefiRuntime = 0xFFFF_8004_0000_0000;
		public const ulong KernelPhysicalWindow = 0xFFFF_8010_0000_0000;
		public const ulong KernelEnd = 0xFFFF_8080_0000_0000;

		public const ulong PhysicalWindowSize = KernelEnd - KernelPhysicalWindow;

		public static ulong PagesFor(ulong bytes) => (bytes + PageSize - 1) >> PageShift;
	}
}
