using System;
using Alloy.Abi;

namespace Alloy.Sdk
{
	public static unsafe class Event
	{
		public static long Create(bool manualReset = false, bool initialState = false) =>
			SystemCall.Invoke(Syscall.CreateEvent, manualReset ? 1UL : 0, initialState ? 1UL : 0);

		public static long Set(long handle) => SystemCall.Invoke(Syscall.SetEvent, (ulong)handle);

		public static long Reset(long handle) => SystemCall.Invoke(Syscall.ResetEvent, (ulong)handle);

		// Index of the first signaled handle, or -Timeout
		public static long WaitAny(ReadOnlySpan<long> handles, ulong timeoutMs = ulong.MaxValue)
		{
			fixed (long* values = handles)
				return SystemCall.Invoke(Syscall.WaitForMultiple, (ulong)values, (ulong)handles.Length, timeoutMs);
		}
	}

	// Sections: pages shared between processes by handle; a mapping lasts until the process exits
	public static unsafe class SharedMemory
	{
		public static long Create(ulong size) => SystemCall.Invoke(Syscall.CreateSharedMemory, size);

		// The section's first length bytes mapped into this process, or empty if it couldn't be; the size is the protocol's to know
		public static Memory<byte> Map(long handle, int length)
		{
			long address = MapAddress(handle);
			return address > 0 ? new MappedMemory<byte>((void*)address, length).Memory : Memory<byte>.Empty;
		}

		// The address of the mapping in this process, or a negative error
		internal static long MapAddress(long handle) => SystemCall.Invoke(Syscall.MapSharedMemory, (ulong)handle);
	}

	// Ports carry 64-byte messages (Abi IpcMessage) and may transfer one handle with each
	public static unsafe class Port
	{
		public static long Create() => SystemCall.Invoke(Syscall.CreatePort);

		public static long Send(long port, in IpcMessage message, long transfer = 0)
		{
			fixed (IpcMessage* payload = &message)
				return SystemCall.Invoke(Syscall.SendMessage, (ulong)port, (ulong)payload, (ulong)transfer);
		}

		// Blocks for the next message; returns the handle it carried (0 if none) or a negative error
		public static long Receive(long port, out IpcMessage message, ulong timeoutMs = ulong.MaxValue)
		{
			fixed (IpcMessage* payload = &message)
				return SystemCall.Invoke(Syscall.ReceiveMessage, (ulong)port, (ulong)payload, timeoutMs);
		}
	}

	// Named handles in the kernel's namespace, how servers publish their ports and sections
	public static unsafe class Endpoint
	{
		public static long Register(string name, long handle)
		{
			byte* bytes = stackalloc byte[256];
			int length = SystemCall.Ascii(name, bytes, 256);
			return SystemCall.Invoke(Syscall.RegisterEndpoint, (ulong)bytes, (ulong)length, (ulong)handle);
		}

		public static long Connect(string name)
		{
			byte* bytes = stackalloc byte[256];
			int length = SystemCall.Ascii(name, bytes, 256);
			return SystemCall.Invoke(Syscall.ConnectEndpoint, (ulong)bytes, (ulong)length);
		}

		// Servers start in parallel with their clients, so a client retries until the name appears
		public static long WaitFor(string name, ulong timeoutMs = 5000)
		{
			ulong start = Kernel.TickCount;
			while (true)
			{
				long handle = Connect(name);
				if (handle > 0 || Kernel.TickCount - start > timeoutMs)
					return handle;
				Kernel.Sleep(5);
			}
		}
	}
}
