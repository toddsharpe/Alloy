using System;
using Alloy.Abi;

namespace Alloy.Runtime
{
	// Typed wrappers over SystemCall; errors come back as negative values (Abi/Syscalls.cs)
	public static class Process
	{
		public static int Id => (int)SystemCall.Invoke(Syscall.GetProcessId);

		public static ulong TickCount => (ulong)SystemCall.Invoke(Syscall.GetTickCount);

		public static void Sleep(ulong milliseconds) => SystemCall.Invoke(Syscall.Sleep, milliseconds);

		public static void Yield() => SystemCall.Invoke(Syscall.Yield);

		public static void Exit(int code) => SystemCall.ExitProcess(code);

		// Starts an initrd image named by the command line's first word (bin/ and .exe are tried); returns a handle, or a negative error
		public static unsafe long Create(string commandLine, ulong argument = 0) => Create(commandLine, argument, null);

		public static unsafe long Create(string commandLine, ulong argument, StartInfo* startInfo)
		{
			byte* bytes = stackalloc byte[256];
			int length = Ascii(commandLine, bytes, 255);
			return SystemCall.Invoke(Syscall.CreateProcess, (ulong)bytes, (ulong)length, argument, (ulong)startInfo);
		}

		public static string CommandLine => Environment.CommandLine;

		// The command line split at spaces; the program's own name is first
		public static string[] Arguments
		{
			get
			{
				string line = Environment.CommandLine;
				int count = 0;
				for (int i = 0; i < line.Length; i++)
				{
					if (line[i] != ' ' && (i == 0 || line[i - 1] == ' '))
						count++;
				}
				string[] arguments = new string[count];
				int index = 0;
				for (int i = 0; i < line.Length;)
				{
					while (i < line.Length && line[i] == ' ')
						i++;
					int start = i;
					while (i < line.Length && line[i] != ' ')
						i++;
					if (i > start)
						arguments[index++] = new string(line.AsSpan(start, i - start));
				}
				return arguments;
			}
		}

		// Up to max running processes (MetalOS GetProcesses); returns how many
		public static unsafe int List(ProcessEntry* entries, int max) => (int)SystemCall.Invoke(Syscall.GetProcesses, (ulong)entries, (ulong)max);

		public static unsafe long CreateThread(delegate* unmanaged<ulong, void> entry, ulong argument) =>
			SystemCall.Invoke(Syscall.CreateThread, (ulong)entry, argument, 0, 0);

		public static void ExitThread() => SystemCall.Invoke(Syscall.ExitThread);

		// A handle to a running process by id, waitable until it exits; negative once it has gone
		public static long Open(int id) => SystemCall.Invoke(Syscall.OpenProcess, (ulong)id);

		// 0 once signaled, or -Timeout
		public static long Wait(long handle, ulong timeoutMs = ulong.MaxValue) => SystemCall.Invoke(Syscall.WaitForObject, (ulong)handle, timeoutMs);

		public static long Close(long handle) => SystemCall.Invoke(Syscall.CloseHandle, (ulong)handle);

		public static unsafe void* Allocate(ulong size) => (void*)SystemCall.Invoke(Syscall.VirtualAlloc, 0, size, Memory.Commit | Memory.Reserve, Memory.ReadWrite);

		internal static unsafe int Ascii(string text, byte* destination, int capacity)
		{
			int length = Math.Min(text.Length, capacity);
			for (int i = 0; i < length; i++)
				destination[i] = (byte)text[i];
			return length;
		}
	}

	public static class File
	{
		public static unsafe long Open(string path)
		{
			byte* bytes = stackalloc byte[256];
			int length = Process.Ascii(path, bytes, 256);
			return SystemCall.Invoke(Syscall.OpenFile, (ulong)bytes, (ulong)length);
		}

		public static unsafe long Read(long handle, void* buffer, ulong count) => SystemCall.Invoke(Syscall.ReadFile, (ulong)handle, (ulong)buffer, count);

		public static long Seek(long handle, long offset, SeekOrigin origin) => SystemCall.Invoke(Syscall.SeekFile, (ulong)handle, (ulong)offset, (ulong)origin);

		public static long Size(long handle) => SystemCall.Invoke(Syscall.GetFileSize, (ulong)handle);

		public static unsafe long Write(long handle, void* buffer, ulong count) => SystemCall.Invoke(Syscall.WriteFile, (ulong)handle, (ulong)buffer, count);

		public static long Close(long handle) => Process.Close(handle);
	}

	public static class Pipe
	{
		public static unsafe bool Create(out long read, out long write)
		{
			long* ends = stackalloc long[2];
			bool created = SystemCall.Invoke(Syscall.CreatePipe, (ulong)ends) == 0;
			read = created ? ends[0] : 0;
			write = created ? ends[1] : 0;
			return created;
		}

		// Bytes waiting in a read end, or negative once the writer has gone and the pipe is empty
		public static long Available(long read) => SystemCall.Invoke(Syscall.PeekPipe, (ulong)read);
	}

	public static class Clock
	{
		// Wall-clock time from the RTC, in the host's local time
		public static unsafe SystemTime Now
		{
			get
			{
				SystemTime time = default;
				SystemCall.Invoke(Syscall.GetSystemTime, (ulong)&time);
				return time;
			}
		}
	}

	// Standard output when the process has one (a pipe from term), otherwise whole lines to the kernel log
	public static class Console
	{
		private static unsafe char* s_line;
		private static int s_length;

		public static void WriteLine(ReadOnlySpan<char> text)
		{
			Write(text);
			Write("\n");
		}

		public static void Write(ReadOnlySpan<char> text) => Write(Environment.StdOutput, text);

		public static void Error(ReadOnlySpan<char> text) => Write(Environment.StdError != 0 ? Environment.StdError : Environment.StdOutput, text);

		private static unsafe void Write(ulong handle, ReadOnlySpan<char> text)
		{
			if (handle != 0)
			{
				byte* bytes = stackalloc byte[256];
				for (int start = 0; start < text.Length; start += 256)
				{
					int count = Math.Min(text.Length - start, 256);
					for (int i = 0; i < count; i++)
						bytes[i] = text[start + i] < 0x80 ? (byte)text[start + i] : (byte)'?';
					File.Write((long)handle, bytes, (ulong)count);
				}
				return;
			}
			if (s_line == null)
				s_line = (char*)Process.Allocate(200 * sizeof(char));
			foreach (char c in text)
			{
				if (c == '\n' || s_length == 200)
				{
					SystemCall.DebugPrint(new ReadOnlySpan<char>(s_line, s_length));
					s_length = 0;
					if (c == '\n')
						continue;
				}
				s_line[s_length++] = c;
			}
		}
	}
}
