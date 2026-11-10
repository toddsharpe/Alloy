using System;
using System.Text;
using Alloy.Abi;
using Alloy.Sdk;

namespace Alloy.Apps.IpcTest
{
	// Client and server in one image: the client starts a second copy with argument 1 to act as the "echo" server
	internal static class Program
	{
		private const ulong Hello = 1;
		private const ulong Ping = 2;
		private const ulong Done = 3;
		private const int Rounds = 1000;
		private const int PageSize = 4096;

		private static int Main() => Kernel.ProcessArgument == 1 ? Server() : Client();

		private static int Server()
		{
			long requests = Port.Create();
			Endpoint.Register("echo", requests);
			long reply = 0;
			while (true)
			{
				long transferred = Port.Receive(requests, out IpcMessage message);
				if (message.Type == Hello)
				{
					// First message carries the client's reply port, the second its shared memory
					reply = transferred;
					Port.Receive(requests, out message);
				}
				if (message.Type == Ping)
				{
					message.Data[0]++;
					Port.Send(reply, message);
				}
				else if (message.Type == Done)
				{
					Span<byte> shared = SharedMemory.Map(transferred, PageSize).Span;
					int written = Encoding.ASCII.GetBytes("written by the echo server through shared memory", shared);
					shared[written] = 0;
					Port.Send(reply, message);
					return 0;
				}
			}
		}

		private static int Client()
		{
			long server = Kernel.CreateProcess("bin/ipctest.exe", 1);
			long requests = Endpoint.WaitFor("echo");
			long reply = Port.Create();
			IpcMessage message = default;
			message.Type = Hello;
			Port.Send(requests, message, reply);

			ulong start = Kernel.TickCount;
			for (int i = 0; i < Rounds; i++)
			{
				message.Type = Ping;
				message.Data[0] = (ulong)i;
				Port.Send(requests, message);
				Port.Receive(reply, out message);
				if (message.Data[0] != (ulong)i + 1)
				{
					Console.WriteLine("ipctest: FAIL, echo returned the wrong value");
					return 1;
				}
			}
			ulong elapsed = Kernel.TickCount - start;

			long section = SharedMemory.Create(PageSize);
			Memory<byte> shared = SharedMemory.Map(section, PageSize);
			message.Type = Done;
			Port.Send(requests, message, section);
			Port.Receive(reply, out message);
			Kernel.Wait(server);

			ReadOnlySpan<byte> text = shared.Span.Slice(0, 80);
			int length = text.IndexOf((byte)0);
			Console.WriteLine($"ipctest: {Rounds} blocking round trips in {elapsed} ms; shared memory says: {Encoding.ASCII.GetString(length < 0 ? text : text.Slice(0, length))}");
			return 0;
		}
	}
}
