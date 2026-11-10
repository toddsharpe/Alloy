namespace Internal.Runtime
{
	// Each ILC object points to one of these from .modules$I (dotnet/runtime ModuleHeaders.h)
	internal struct ReadyToRunHeader
	{
		public const uint ExpectedSignature = 0x00525452; // 'RTR'
		public const ushort ExpectedMajorVersion = 16;

		public uint Signature;
		public ushort MajorVersion;
		public ushort MinorVersion;
		public uint Flags;
		public ushort NumberOfSections;
		public byte EntrySize;
		public byte EntryType;
		// ModuleInfoRow[NumberOfSections] follows
	}

	internal struct ModuleInfoRow
	{
		private const int HasEndPointerFlag = 0x1;

		public int SectionId;
		public int Flags;
		public unsafe void* Start;
		public unsafe void* End;

		public unsafe int Length => (Flags & HasEndPointerFlag) != 0 ? (int)((byte*)End - (byte*)Start) : sizeof(void*);
	}

	internal enum ReadyToRunSectionType
	{
		GCStaticRegion = 201,
		ThreadStaticRegion = 202,
		TypeManagerIndirection = 204,
		EagerCctor = 205,
		FrozenObjectRegion = 206,
		DehydratedData = 207,
		ThreadStaticOffsetRegion = 208,
		ImportAddressTables = 212,
		ModuleInitializerList = 213,
	}

	internal static class GCStaticRegionConstants
	{
		// Low bits of each GC static region slot in the image, before the startup code replaces it with the base object
		public const nint Uninitialized = 0x1;
		public const nint HasPreInitializedData = 0x2;
		public const nint Mask = Uninitialized | HasPreInitializedData;
	}
}
