using System;

namespace Alloy.Efi
{
	// UEFI 2.x core tables; unused function pointers are void* placeholders that keep the layout

	public static class EfiStatus
	{
		public const ulong Success = 0;
		private const ulong Error = 0x8000000000000000;
		public const ulong LoadError = Error | 1;
		public const ulong InvalidParameter = Error | 2;
		public const ulong Unsupported = Error | 3;
		public const ulong BadBufferSize = Error | 4;
		public const ulong BufferTooSmall = Error | 5;
		public const ulong NotReady = Error | 6;
		public const ulong DeviceError = Error | 7;
		public const ulong OutOfResources = Error | 9;
		public const ulong NotFound = Error | 14;

		public static bool IsError(ulong status) => (status & Error) != 0;
	}

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

	public enum EfiAllocateType : uint
	{
		AnyPages,
		MaxAddress,
		Address,
	}

	public enum EfiResetType : uint
	{
		Cold,
		Warm,
		Shutdown,
		PlatformSpecific,
	}

	public struct EfiGuid
	{
		public uint Data1;
		public ushort Data2;
		public ushort Data3;
		public ulong Data4;

		public EfiGuid(uint data1, ushort data2, ushort data3, byte d0, byte d1, byte d2, byte d3, byte d4, byte d5, byte d6, byte d7)
		{
			Data1 = data1;
			Data2 = data2;
			Data3 = data3;
			Data4 = d0 | (ulong)d1 << 8 | (ulong)d2 << 16 | (ulong)d3 << 24 | (ulong)d4 << 32 | (ulong)d5 << 40 | (ulong)d6 << 48 | (ulong)d7 << 56;
		}

		public readonly bool Equals(in EfiGuid other) => Data1 == other.Data1 && Data2 == other.Data2 && Data3 == other.Data3 && Data4 == other.Data4;
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

	public struct EfiInputKey
	{
		public ushort ScanCode;
		public char UnicodeChar;
	}

	public struct EfiConfigurationTable
	{
		public EfiGuid VendorGuid;
		public unsafe void* VendorTable;
	}

	public struct EfiSimpleTextOutputMode
	{
		public int MaxMode;
		public int Mode;
		public int Attribute;
		public int CursorColumn;
		public int CursorRow;
		public byte CursorVisible;
	}

	public unsafe struct EfiSimpleTextOutputProtocol
	{
		public delegate* unmanaged<EfiSimpleTextOutputProtocol*, byte, ulong> Reset;
		public delegate* unmanaged<EfiSimpleTextOutputProtocol*, char*, ulong> OutputString;
		public void* TestString;
		public delegate* unmanaged<EfiSimpleTextOutputProtocol*, nuint, nuint*, nuint*, ulong> QueryMode;
		public delegate* unmanaged<EfiSimpleTextOutputProtocol*, nuint, ulong> SetMode;
		public delegate* unmanaged<EfiSimpleTextOutputProtocol*, nuint, ulong> SetAttribute;
		public delegate* unmanaged<EfiSimpleTextOutputProtocol*, ulong> ClearScreen;
		public delegate* unmanaged<EfiSimpleTextOutputProtocol*, nuint, nuint, ulong> SetCursorPosition;
		public delegate* unmanaged<EfiSimpleTextOutputProtocol*, byte, ulong> EnableCursor;
		public EfiSimpleTextOutputMode* Mode;
	}

	public unsafe struct EfiSimpleTextInputProtocol
	{
		public delegate* unmanaged<EfiSimpleTextInputProtocol*, byte, ulong> Reset;
		public delegate* unmanaged<EfiSimpleTextInputProtocol*, EfiInputKey*, ulong> ReadKeyStroke;
		public void* WaitForKey;
	}

	public unsafe struct EfiBootServices
	{
		public EfiTableHeader Hdr;

		public void* RaiseTpl;
		public void* RestoreTpl;

		public delegate* unmanaged<EfiAllocateType, EfiMemoryType, nuint, ulong*, ulong> AllocatePages;
		public delegate* unmanaged<ulong, nuint, ulong> FreePages;
		public delegate* unmanaged<nuint*, EfiMemoryDescriptor*, nuint*, nuint*, uint*, ulong> GetMemoryMap;
		public delegate* unmanaged<EfiMemoryType, nuint, void**, ulong> AllocatePool;
		public delegate* unmanaged<void*, ulong> FreePool;

		public void* CreateEvent;
		public void* SetTimer;
		public void* WaitForEvent;
		public void* SignalEvent;
		public void* CloseEvent;
		public void* CheckEvent;

		public void* InstallProtocolInterface;
		public void* ReinstallProtocolInterface;
		public void* UninstallProtocolInterface;
		public delegate* unmanaged<void*, EfiGuid*, void**, ulong> HandleProtocol;
		public void* Reserved;
		public void* RegisterProtocolNotify;
		public void* LocateHandle;
		public void* LocateDevicePath;
		public void* InstallConfigurationTable;

		public void* LoadImage;
		public void* StartImage;
		public void* Exit;
		public void* UnloadImage;
		public delegate* unmanaged<void*, nuint, ulong> ExitBootServices;

		public void* GetNextMonotonicCount;
		public delegate* unmanaged<nuint, ulong> Stall;
		public delegate* unmanaged<nuint, ulong, nuint, char*, ulong> SetWatchdogTimer;

		public void* ConnectController;
		public void* DisconnectController;

		public delegate* unmanaged<void*, EfiGuid*, void**, void*, void*, uint, ulong> OpenProtocol;
		public void* CloseProtocol;
		public void* OpenProtocolInformation;

		public void* ProtocolsPerHandle;
		public void* LocateHandleBuffer;
		public delegate* unmanaged<EfiGuid*, void*, void**, ulong> LocateProtocol;
		public void* InstallMultipleProtocolInterfaces;
		public void* UninstallMultipleProtocolInterfaces;

		public void* CalculateCrc32;

		public void* CopyMem;
		public void* SetMem;
		public void* CreateEventEx;
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

	public unsafe struct EfiSystemTable
	{
		public EfiTableHeader Hdr;
		public char* FirmwareVendor;
		public uint FirmwareRevision;
		public void* ConsoleInHandle;
		public EfiSimpleTextInputProtocol* ConIn;
		public void* ConsoleOutHandle;
		public EfiSimpleTextOutputProtocol* ConOut;
		public void* StandardErrorHandle;
		public EfiSimpleTextOutputProtocol* StdErr;
		public EfiRuntimeServices* RuntimeServices;
		public EfiBootServices* BootServices;
		public nuint NumberOfTableEntries;
		public EfiConfigurationTable* ConfigurationTable;
	}
}
