namespace Alloy.Efi
{
	public static class EfiGuids
	{
		public static readonly EfiGuid LoadedImageProtocol = new EfiGuid(0x5B1B31A1, 0x9562, 0x11D2, 0x8E, 0x3F, 0x00, 0xA0, 0xC9, 0x69, 0x72, 0x3B);
		public static readonly EfiGuid SimpleFileSystemProtocol = new EfiGuid(0x964E5B22, 0x6459, 0x11D2, 0x8E, 0x39, 0x00, 0xA0, 0xC9, 0x69, 0x72, 0x3B);
		public static readonly EfiGuid FileInfo = new EfiGuid(0x09576E92, 0x6D3F, 0x11D2, 0x8E, 0x39, 0x00, 0xA0, 0xC9, 0x69, 0x72, 0x3B);
		public static readonly EfiGuid GraphicsOutputProtocol = new EfiGuid(0x9042A9DE, 0x23DC, 0x4A38, 0x96, 0xFB, 0x7A, 0xDE, 0xD0, 0x80, 0x51, 0x6A);
		public static readonly EfiGuid Acpi20Table = new EfiGuid(0x8868E871, 0xE4F1, 0x11D3, 0xBC, 0x22, 0x00, 0x80, 0xC7, 0x3C, 0x88, 0x81);
	}

	public struct EfiLoadedImageProtocol
	{
		public uint Revision;
		public unsafe void* ParentHandle;
		public unsafe EfiSystemTable* SystemTable;
		public unsafe void* DeviceHandle;
		public unsafe void* FilePath;
		public unsafe void* Reserved;
		public uint LoadOptionsSize;
		public unsafe void* LoadOptions;
		public unsafe void* ImageBase;
		public ulong ImageSize;
		public EfiMemoryType ImageCodeType;
		public EfiMemoryType ImageDataType;
		public unsafe void* Unload;
	}

	public struct EfiSimpleFileSystemProtocol
	{
		public ulong Revision;
		public unsafe delegate* unmanaged<EfiSimpleFileSystemProtocol*, EfiFileProtocol**, ulong> OpenVolume;
	}

	public unsafe struct EfiFileProtocol
	{
		public const ulong ModeRead = 0x1;

		public ulong Revision;
		public delegate* unmanaged<EfiFileProtocol*, EfiFileProtocol**, char*, ulong, ulong, ulong> Open;
		public delegate* unmanaged<EfiFileProtocol*, ulong> Close;
		public void* Delete;
		public delegate* unmanaged<EfiFileProtocol*, nuint*, void*, ulong> Read;
		public void* Write;
		public void* GetPosition;
		public void* SetPosition;
		public delegate* unmanaged<EfiFileProtocol*, EfiGuid*, nuint*, void*, ulong> GetInfo;
		public void* SetInfo;
		public void* Flush;
	}

	public struct EfiFileInfo
	{
		public ulong Size;
		public ulong FileSize;
		public ulong PhysicalSize;
		public EfiTime CreateTime;
		public EfiTime LastAccessTime;
		public EfiTime ModificationTime;
		public ulong Attribute;
	}

	public enum EfiPixelFormat : uint
	{
		RedGreenBlueReserved8,
		BlueGreenRedReserved8,
		BitMask,
		BltOnly,
	}

	public struct EfiGraphicsOutputModeInformation
	{
		public uint Version;
		public uint HorizontalResolution;
		public uint VerticalResolution;
		public EfiPixelFormat PixelFormat;
		public uint RedMask;
		public uint GreenMask;
		public uint BlueMask;
		public uint ReservedMask;
		public uint PixelsPerScanLine;
	}

	public struct EfiGraphicsOutputMode
	{
		public uint MaxMode;
		public uint Mode;
		public unsafe EfiGraphicsOutputModeInformation* Info;
		public nuint SizeOfInfo;
		public ulong FrameBufferBase;
		public nuint FrameBufferSize;
	}

	public unsafe struct EfiGraphicsOutputProtocol
	{
		public void* QueryMode;
		public void* SetMode;
		public void* Blt;
		public EfiGraphicsOutputMode* Mode;
	}
}
