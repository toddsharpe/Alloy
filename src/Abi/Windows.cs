namespace Alloy.Abi
{
	// Windows x64 CONTEXT (winnt.h), byte for byte: the kernel fills it for exceptions and GetThreadContext, the Win32 layer reads it
	public unsafe struct WindowsContext
	{
		public const int Size = 0x4D0;
		public const uint Amd64 = 0x100000;
		public const uint Control = Amd64 | 0x1;
		public const uint Integer = Amd64 | 0x2;
		public const uint Segments = Amd64 | 0x4;
		public const uint FloatingPoint = Amd64 | 0x8;
		public const uint DebugRegisters = Amd64 | 0x10;
		public const uint Full = Control | Integer | FloatingPoint;
		public const uint All = Full | Segments | DebugRegisters;
		public const uint ExceptionActive = 0x8000000;
		public const uint ServiceActive = 0x10000000;
		public const uint ExceptionRequest = 0x40000000;
		public const uint ExceptionReporting = 0x80000000;

		public ulong P1Home;
		public ulong P2Home;
		public ulong P3Home;
		public ulong P4Home;
		public ulong P5Home;
		public ulong P6Home;
		public uint ContextFlags;
		public uint MxCsr;
		public ushort SegCs;
		public ushort SegDs;
		public ushort SegEs;
		public ushort SegFs;
		public ushort SegGs;
		public ushort SegSs;
		public uint EFlags;
		public ulong Dr0;
		public ulong Dr1;
		public ulong Dr2;
		public ulong Dr3;
		public ulong Dr6;
		public ulong Dr7;
		public ulong Rax;
		public ulong Rcx;
		public ulong Rdx;
		public ulong Rbx;
		public ulong Rsp;
		public ulong Rbp;
		public ulong Rsi;
		public ulong Rdi;
		public ulong R8;
		public ulong R9;
		public ulong R10;
		public ulong R11;
		public ulong R12;
		public ulong R13;
		public ulong R14;
		public ulong R15;
		public ulong Rip;
		public fixed byte FltSave[512];
		public fixed byte VectorRegister[26 * 16];
		public ulong VectorControl;
		public ulong DebugControl;
		public ulong LastBranchToRip;
		public ulong LastBranchFromRip;
		public ulong LastExceptionToRip;
		public ulong LastExceptionFromRip;
	}

	// Windows EXCEPTION_RECORD (winnt.h)
	public unsafe struct WindowsExceptionRecord
	{
		public const int Size = 0x98;
		public const uint AccessViolation = 0xC0000005;
		public const uint Breakpoint = 0x80000003;
		public const uint SingleStep = 0x80000004;
		public const uint IntegerDivideByZero = 0xC0000094;
		public const uint IntegerOverflow = 0xC0000095;
		public const uint ArrayBoundsExceeded = 0xC000008C;
		public const uint IllegalInstruction = 0xC000001D;
		public const uint PrivilegedInstruction = 0xC0000096;
		public const uint FloatMultipleTraps = 0xC00002B5;

		public uint ExceptionCode;
		public uint ExceptionFlags;
		public ulong ExceptionRecord;
		public ulong ExceptionAddress;
		public uint NumberParameters;
		public uint Reserved;
		public fixed ulong ExceptionInformation[15];
	}

	// Windows MEMORY_BASIC_INFORMATION (winnt.h)
	public struct MemoryBasicInformation
	{
		public ulong BaseAddress;
		public ulong AllocationBase;
		public uint AllocationProtect;
		public ushort PartitionId;
		public ushort Reserved;
		public ulong RegionSize;
		public uint State;
		public uint Protect;
		public uint Type;
		public uint Reserved2;
	}

	// VirtualAlloc's allocation types and page protections, with Windows' values
	public static class Memory
	{
		public const uint Commit = 0x1000;
		public const uint Reserve = 0x2000;
		public const uint Decommit = 0x4000;
		public const uint Release = 0x8000;
		public const uint Free = 0x10000;
		public const uint Private = 0x20000;
		public const uint Mapped = 0x40000;
		public const uint Reset = 0x80000;
		public const uint TopDown = 0x100000;
		public const uint WriteWatch = 0x200000;
		public const uint ResetUndo = 0x1000000;
		public const uint Image = 0x1000000;
		public const uint LargePages = 0x20000000;

		public const uint NoAccess = 0x01;
		public const uint ReadOnly = 0x02;
		public const uint ReadWrite = 0x04;
		public const uint WriteCopy = 0x08;
		public const uint Execute = 0x10;
		public const uint ExecuteRead = 0x20;
		public const uint ExecuteReadWrite = 0x40;
		public const uint ExecuteWriteCopy = 0x80;
		public const uint Guard = 0x100;
		public const uint NoCache = 0x200;

		// Reservations are aligned to this, as on Windows
		public const ulong AllocationGranularity = 0x10000;
	}

	// Where native code finds things in the thread block behind gs (Windows' TEB offsets); Alloy's own fields start at 0x100
	public static class Teb
	{
		public const int Pages = 2;
		public const int StackBase = 0x08;
		public const int StackLimit = 0x10;
		public const int Self = 0x30;
		public const int ProcessId = 0x40;
		public const int ThreadId = 0x48;
		public const int ThreadLocalStorage = 0x58;
		public const int ProcessBlock = 0x60;
		public const int LastError = 0x68;
		public const int DeallocationStack = 0x1478;
		public const int UserReserved = 0x100;
	}

	// The process block TEB+0x60 points at; ImageBaseAddress matches Windows' PEB, the rest is Alloy's
	public static class Peb
	{
		public const int ImageBase = 0x10;
		public const int StdInput = 0x400;
		public const int StdOutput = 0x408;
		public const int StdError = 0x410;
		public const int CommandLineLength = 0x418;
		public const int ImageNameLength = 0x420;
		public const int ImageName = 0x440;
		public const int MaxImageName = 0xC0;
		public const int CommandLine = 0x500;
		public const int MaxCommandLine = 0xB00;
	}
}
