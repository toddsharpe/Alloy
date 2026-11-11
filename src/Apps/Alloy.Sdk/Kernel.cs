using System;
using Alloy.Abi;

namespace Alloy.Sdk
{
	// Processes, time, memory and handles: the kernel calls that aren't IPC, networking or windows
	public static class Kernel
	{
		public static int ProcessId => (int)SystemCall.Invoke(Syscall.GetProcessId);

		// The value the parent passed to CreateProcess, in rcx at process start
		public static ulong ProcessArgument => SystemCall.PhxGetProcessArgument();

		// Milliseconds since boot
		public static ulong TickCount => (ulong)SystemCall.Invoke(Syscall.GetTickCount);

		// Wall-clock time from the RTC, in the host's local time
		public static unsafe SystemTime Time
		{
			get
			{
				SystemTime time;
				SystemCall.Invoke(Syscall.GetSystemTime, (ulong)&time);
				return time;
			}
		}

		public static void Sleep(ulong milliseconds) => SystemCall.Invoke(Syscall.Sleep, milliseconds);

		public static void Yield() => SystemCall.Invoke(Syscall.Yield);

		// Starts the program the command line's first word names on the Alloy partition (bin/ and .exe are tried); a handle to the process, or a negative error
		public static long CreateProcess(string commandLine, ulong argument = 0) => CreateProcess(commandLine, argument, null);

		public static unsafe long CreateProcess(string commandLine, ulong argument, StartInfo? startInfo)
		{
			byte* bytes = stackalloc byte[256];
			int length = SystemCall.Ascii(commandLine, bytes, 255);
			StartInfo info = startInfo.GetValueOrDefault();
			return SystemCall.Invoke(Syscall.CreateProcess, (ulong)bytes, (ulong)length, argument, startInfo.HasValue ? (ulong)&info : 0);
		}

		// A handle to a running process by id, waitable until it exits; negative once it has gone
		public static long OpenProcess(int id) => SystemCall.Invoke(Syscall.OpenProcess, (ulong)id);

		// 0 once the object is signaled, or -Timeout
		public static long Wait(long handle, ulong timeoutMs = ulong.MaxValue) => SystemCall.Invoke(Syscall.WaitForObject, (ulong)handle, timeoutMs);

		public static bool Close(long handle) => SystemCall.Invoke(Syscall.CloseHandle, (ulong)handle) == 0;

		// A process's or thread's exit code, or ThreadFlags.StillActive while it runs
		public static long ExitCode(long handle) => SystemCall.Invoke(Syscall.GetExitCode, (ulong)handle);

		// The running processes, up to the span's length; returns how many
		public static unsafe int ListProcesses(Span<ProcessEntry> entries)
		{
			fixed (ProcessEntry* pointer = entries)
				return (int)SystemCall.Invoke(Syscall.GetProcesses, (ulong)pointer, (ulong)entries.Length);
		}

		// Hands one Ethernet frame to the NIC; false when it couldn't take it right now
		public static unsafe bool NetSend(ReadOnlySpan<byte> frame)
		{
			fixed (byte* pointer = frame)
				return SystemCall.Invoke(Syscall.NetSend, (ulong)pointer, (ulong)frame.Length) == 0;
		}

		// A key and value for the host's key-value pair exchange, where Hyper-V Manager reads the guest's address and name from; false if they don't fit
		public static unsafe bool PublishValue(string key, string value)
		{
			byte* keyBytes = stackalloc byte[256];
			byte* valueBytes = stackalloc byte[256];
			int keyLength = SystemCall.Ascii(key, keyBytes, 255);
			int valueLength = SystemCall.Ascii(value, valueBytes, 255);
			return SystemCall.Invoke(Syscall.PublishValue, (ulong)keyBytes, (ulong)keyLength, (ulong)valueBytes, (ulong)valueLength) == 0;
		}

		public static string Name(in ProcessEntry entry)
		{
			ReadOnlySpan<byte> name = entry.Name;
			int length = name.IndexOf((byte)0);
			return System.Text.Encoding.ASCII.GetString(length < 0 ? name : name.Slice(0, length));
		}
	}

	// Anonymous pipes: a 4 KB kernel ring with a read end and a write end, the terminal's way of collecting a program's output
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

		// Blocks while the pipe is empty and still has a writer; 0 is end of file
		public static unsafe long Read(long read, Span<byte> buffer)
		{
			fixed (byte* pointer = buffer)
				return SystemCall.Invoke(Syscall.ReadFile, (ulong)read, (ulong)pointer, (ulong)buffer.Length);
		}

		public static unsafe long Write(long write, ReadOnlySpan<byte> data)
		{
			fixed (byte* pointer = data)
				return SystemCall.Invoke(Syscall.WriteFile, (ulong)write, (ulong)pointer, (ulong)data.Length);
		}
	}
}
