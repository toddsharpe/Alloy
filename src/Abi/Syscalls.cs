using System;

namespace Alloy.Abi
{
	// The one definition of the syscall table, grouped like MetalOS's; rax = number, args in r10 rdx r8 r9 then [rsp+0x30], [rsp+0x38]
	public enum Syscall : ulong
	{
		GetTickCount = 0x100,
		DebugPrint = 0x101,
		GetSystemTime = 0x102,
		QueryTime = 0x103,
		GetMemoryStatus = 0x104,

		ExitProcess = 0x200,
		CreateProcess = 0x201,
		GetProcessId = 0x202,
		OpenProcess = 0x203,
		GetProcesses = 0x204,
		SetExceptionDispatcher = 0x205,
		GetExitCode = 0x206,

		Sleep = 0x300,
		Yield = 0x301,
		CreateThread = 0x302,
		ExitThread = 0x303,
		OpenCurrentThread = 0x304,
		SuspendThread = 0x305,
		ResumeThread = 0x306,
		GetThreadContext = 0x307,
		SetThreadContext = 0x308,
		GetThreadId = 0x309,

		OpenFile = 0x400,
		ReadFile = 0x401,
		SeekFile = 0x402,
		GetFileSize = 0x403,
		WriteFile = 0x404,
		CreatePipe = 0x405,
		PeekPipe = 0x406,
		DeleteFile = 0x407,
		MoveFile = 0x408,
		CreateDirectory = 0x409,
		ListDirectory = 0x40A,

		CloseHandle = 0x500,

		WaitForObject = 0x600,
		CreateEvent = 0x601,
		SetEvent = 0x602,
		ResetEvent = 0x603,
		WaitForMultiple = 0x604,

		VirtualAlloc = 0x700,
		VirtualFree = 0x701,
		VirtualProtect = 0x702,
		VirtualQuery = 0x703,

		CreateSharedMemory = 0x800,
		MapSharedMemory = 0x801,

		MapFramebuffer = 0x900,
		FlushFramebuffer = 0x901,

		NetSend = 0xA00,
		GetInterfaces = 0xA01,

		CreatePort = 0xB00,
		SendMessage = 0xB01,
		ReceiveMessage = 0xB02,
		RegisterEndpoint = 0xB03,
		ConnectEndpoint = 0xB04,
	}

	// A syscall returns a value >= 0 on success, or the negated error
	public enum SyscallError : long
	{
		InvalidPointer = 1,
		InvalidArgument = 2,
		InvalidHandle = 3,
		NotFound = 4,
		OutOfMemory = 5,
		NotImplemented = 6,
		Timeout = 7,
		Busy = 8,
		Closed = 9,
	}

	// CreateProcess's optional fourth argument: handles of the caller's that become the child's standard streams (0 for none)
	public struct StartInfo
	{
		public ulong StdInput;
		public ulong StdOutput;
		public ulong StdError;
	}

	// One row of GetProcesses (MetalOS ProcessListEntry)
	public struct ProcessEntry
	{
		public const int MaxName = 32;

		public uint Id;
		public uint Threads;
		public ulong VirtualSize;
		public NameBytes Name;
	}

	// The process name as ASCII, NUL padded
	[System.Runtime.CompilerServices.InlineArray(ProcessEntry.MaxName)]
	public struct NameBytes
	{
		private byte _element0;
	}

	// Wall-clock time from the RTC, in the host's local time
	public struct SystemTime
	{
		public ushort Year;
		public ushort Month;
		public ushort Day;
		public ushort Hour;
		public ushort Minute;
		public ushort Second;
		public ushort Milliseconds;
		public ushort Reserved;
	}

	// GetMemoryStatus fills this
	public struct MemoryStatus
	{
		public ulong TotalPhysical;
		public ulong AvailablePhysical;
	}

	// CreateThread's flags
	public static class ThreadFlags
	{
		public const ulong Suspended = 0x4;
		public const long StillActive = 259;
	}

	public enum SeekOrigin
	{
		Begin,
		Current,
		End,
	}

	// OpenFile's access: Append writes, each at the end however the position moved
	[Flags]
	public enum FileAccess
	{
		None = 0,
		Read = 1 << 0,
		Write = 1 << 1,
		ReadWrite = Read | Write,
		Append = Write | (1 << 2),
		ReadAppend = ReadWrite | Append,
	}

	// What OpenFile does when the file is there, or isn't: fopen's r, w and a
	public enum FileCreate
	{
		OpenExisting,
		CreateAlways,
		OpenAlways,
	}

	// One name in a directory, from ListDirectory
	public struct DirectoryEntry
	{
		public const int MaxName = 256;

		public FileNameBytes Name;
		public ulong Size;
		public uint Directory;
		public uint Reserved;
	}

	// A file name as ASCII, NUL padded
	[System.Runtime.CompilerServices.InlineArray(DirectoryEntry.MaxName)]
	public struct FileNameBytes
	{
		private byte _element0;
	}

	// ListDirectory's third argument: where the entries go and how many fit, and the index to list from, moved past those returned
	public struct DirectoryListing
	{
		public ulong Entries;
		public ulong Count;
		public ulong Index;
	}
}
