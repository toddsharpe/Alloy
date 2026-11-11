using System;
using System.Runtime;
using System.Runtime.CompilerServices;
using Internal.Runtime.CompilerHelpers;
using Alloy.Abi;

namespace Alloy.Runtime
{
	// The raw kernel interface; Alloy.Runtime wraps it in typed calls
	public static class SystemCall
	{
		[RuntimeImport("*", "PhxSyscall")]
		[MethodImpl(MethodImplOptions.InternalCall)]
		private static extern long PhxSyscall(ulong number, ulong a0, ulong a1, ulong a2, ulong a3, ulong a4, ulong a5);

		public static long Invoke(Syscall number, ulong a0 = 0, ulong a1 = 0, ulong a2 = 0, ulong a3 = 0, ulong a4 = 0, ulong a5 = 0) =>
			PhxSyscall((ulong)number, a0, a1, a2, a3, a4, a5);

		// Prints one line to the kernel log (COM1 and the console); non-ASCII becomes '?'
		public static unsafe void DebugPrint(ReadOnlySpan<char> text)
		{
			byte* bytes = stackalloc byte[200];
			int count = Math.Min(text.Length, 200);
			for (int i = 0; i < count; i++)
				bytes[i] = text[i] < 0x80 ? (byte)text[i] : (byte)'?';
			Invoke(Syscall.DebugPrint, (ulong)bytes, (ulong)count);
		}

		public static void ExitProcess(int code)
		{
			Invoke(Syscall.ExitProcess, (ulong)code);
			while (true)
			{
			}
		}
	}
}

namespace Internal.Runtime.CompilerHelpers
{
	internal static class UserStartup
	{
		// The thread environment block gs points at; the kernel fills it per thread (Abi Teb)
		[RuntimeImport("*", "PhxTeb")]
		[MethodImpl(MethodImplOptions.InternalCall)]
		internal static extern unsafe byte* PhxTeb();

		[RuntimeImport("*", "__managed__Main")]
		[MethodImpl(MethodImplOptions.InternalCall)]
		private static extern unsafe int ManagedMain(int argc, char** argv);

		// The process entry point: rcx carries the argument CreateProcess passed
		[RuntimeExport("PhxUserStart")]
		private static unsafe void PhxUserStart(ulong argument)
		{
			Environment.ProcessArgument = argument;
			Environment.SetFailFastWriter(&WriteFailFast);
			StartupCodeHelpers.InitializeModules();
			Environment.ReadParameters(PhxTeb());
			Alloy.Runtime.SystemCall.ExitProcess(ManagedMain(0, null));
		}

		private static void WriteFailFast(string message) => Alloy.Runtime.SystemCall.DebugPrint(message);
	}
}

namespace System.Runtime
{
	// Bump allocation from 1 MB VirtualAlloc chunks; nothing is freed until the process exits
	internal static partial class Allocator
	{
		private const nuint ChunkSize = 1024 * 1024;

		private static unsafe byte* s_chunk;
		private static nuint s_used;
		private static nuint s_size;

		private static unsafe partial void* AllocateZeroed(nuint size)
		{
			if (size > ChunkSize / 4)
				return Reserve(size);

			if (s_chunk == null || s_used + size > s_size)
			{
				s_chunk = (byte*)Reserve(ChunkSize);
				if (s_chunk == null)
					return null;
				s_used = 0;
				s_size = ChunkSize;
			}
			void* memory = s_chunk + s_used;
			s_used += (size + 15) & ~(nuint)15;
			return memory;
		}

		private static unsafe partial void Free(void* memory)
		{
		}

		// Fresh pages from the kernel are already zero
		private static unsafe void* Reserve(nuint size)
		{
			long address = Alloy.Runtime.SystemCall.Invoke(Syscall.VirtualAlloc, 0, size, Memory.Commit | Memory.Reserve, Memory.ReadWrite);
			return address <= 0 ? null : (void*)address;
		}
	}
}

namespace System.Threading
{
	public static partial class Monitor
	{
		// The kernel's thread id from the TEB, offset by one so no thread is ever id 0
		private static unsafe partial uint CurrentThreadId() => *(uint*)(UserStartup.PhxTeb() + Teb.ThreadId) + 1;

		private static unsafe partial void Yield() => Alloy.Runtime.SystemCall.Invoke(Syscall.Yield);
	}
}

namespace System
{
	public static partial class Environment
	{
		public static ulong ProcessArgument { get; internal set; }

		// The parameters the kernel left in the process block TEB+0x60 points at (Abi Peb)
		public static string CommandLine { get; private set; } = "";

		public static ulong StdInput { get; private set; }

		public static ulong StdOutput { get; private set; }

		public static ulong StdError { get; private set; }

		internal static unsafe void ReadParameters(byte* teb)
		{
			byte* peb = *(byte**)(teb + Teb.ProcessBlock);
			StdInput = *(ulong*)(peb + Peb.StdInput);
			StdOutput = *(ulong*)(peb + Peb.StdOutput);
			StdError = *(ulong*)(peb + Peb.StdError);
			int length = (int)Math.Min(*(ulong*)(peb + Peb.CommandLineLength), Peb.MaxCommandLine);
			if (length == 0)
				return;
			char* line = stackalloc char[length];
			for (int i = 0; i < length; i++)
				line[i] = (char)peb[Peb.CommandLine + i];
			CommandLine = new string(new ReadOnlySpan<char>(line, length));
		}

		private static partial void Halt() => Alloy.Runtime.SystemCall.ExitProcess(-1);
	}
}
