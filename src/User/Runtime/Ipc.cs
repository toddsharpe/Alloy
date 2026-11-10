using System;
using Alloy.Abi;

namespace Alloy.Runtime
{
	public static class Event
	{
		public static long Create(bool manualReset = false, bool initialState = false) =>
			SystemCall.Invoke(Syscall.CreateEvent, manualReset ? 1UL : 0, initialState ? 1UL : 0);

		public static long Set(long handle) => SystemCall.Invoke(Syscall.SetEvent, (ulong)handle);

		public static long Reset(long handle) => SystemCall.Invoke(Syscall.ResetEvent, (ulong)handle);

		// Index of the first signaled handle, or -Timeout
		public static unsafe long WaitAny(ReadOnlySpan<long> handles, ulong timeoutMs = ulong.MaxValue)
		{
			fixed (long* values = handles)
				return SystemCall.Invoke(Syscall.WaitForMultiple, (ulong)values, (ulong)handles.Length, timeoutMs);
		}
	}

	public static class SharedMemory
	{
		public static long Create(ulong size) => SystemCall.Invoke(Syscall.CreateSharedMemory, size);

		// The address of the mapping in this process, or a negative error
		public static long Map(long handle) => SystemCall.Invoke(Syscall.MapSharedMemory, (ulong)handle);
	}

	public static class Port
	{
		public static long Create() => SystemCall.Invoke(Syscall.CreatePort);

		public static unsafe long Send(long port, in IpcMessage message, long transfer = 0)
		{
			fixed (IpcMessage* payload = &message)
				return SystemCall.Invoke(Syscall.SendMessage, (ulong)port, (ulong)payload, (ulong)transfer);
		}

		// Blocks for the next message; returns the handle it carried (0 if none) or a negative error
		public static unsafe long Receive(long port, out IpcMessage message, ulong timeoutMs = ulong.MaxValue)
		{
			fixed (IpcMessage* payload = &message)
				return SystemCall.Invoke(Syscall.ReceiveMessage, (ulong)port, (ulong)payload, timeoutMs);
		}
	}

	public static class Endpoint
	{
		public static unsafe long Register(string name, long handle)
		{
			byte* bytes = stackalloc byte[256];
			int length = Process.Ascii(name, bytes, 256);
			return SystemCall.Invoke(Syscall.RegisterEndpoint, (ulong)bytes, (ulong)length, (ulong)handle);
		}

		public static unsafe long Connect(string name)
		{
			byte* bytes = stackalloc byte[256];
			int length = Process.Ascii(name, bytes, 256);
			return SystemCall.Invoke(Syscall.ConnectEndpoint, (ulong)bytes, (ulong)length);
		}

		// Servers start in parallel with their clients, so a client retries until the name appears
		public static long WaitFor(string name, ulong timeoutMs = 5000)
		{
			ulong start = Process.TickCount;
			while (true)
			{
				long handle = Connect(name);
				if (handle > 0 || Process.TickCount - start > timeoutMs)
					return handle;
				Process.Sleep(5);
			}
		}
	}
}
