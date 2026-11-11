using System;
using Alloy.Abi;
using Alloy.Sdk;

namespace Alloy.Apps.Tcp
{
	// A stream socket from the terminal: tcp <ip> <port> [text | http [path]]; the text and a blank line go out (or a GET for the path), and what comes back is printed until the peer closes
	internal static class Program
	{
		private const ulong ReadTimeoutMs = 10000;

		private static int Main(string[] args)
		{
			if (args.Length < 2 || !Net.TryParseIp(args[0], out uint ip) || !ushort.TryParse(args[1], out ushort port))
			{
				Console.WriteLine("usage: tcp <ip> <port> [text | http [path]]");
				return 1;
			}

			long socket = Net.Socket(NetProtocol.SocketStream, NetProtocol.ProtocolTcp);
			if (socket < 0)
			{
				Console.WriteLine("tcp: socket create failed");
				return 1;
			}

			ulong start = Kernel.TickCount;
			long connected = Net.Connect(socket, ip, port);
			if (connected < 0)
			{
				Console.WriteLine($"tcp: connect to {Net.FormatIp(ip)}:{port} failed ({connected})");
				Net.Close(socket);
				return 1;
			}
			Console.WriteLine($"tcp: connected to {Net.FormatIp(ip)}:{port} in {Kernel.TickCount - start} ms");

			if (args.Length > 2)
			{
				string text = args[2] == "http"
					? $"GET {(args.Length > 3 ? args[3] : "/")} HTTP/1.0\r\nHost: {Net.FormatIp(ip)}\r\nUser-Agent: Alloy/0.1"
					: string.Join(" ", args.AsSpan(2).ToArray());
				byte[] request = new byte[text.Length + 4];
				for (int i = 0; i < text.Length; i++)
					request[i] = (byte)text[i];
				request[text.Length] = (byte)'\r';
				request[text.Length + 1] = (byte)'\n';
				request[text.Length + 2] = (byte)'\r';
				request[text.Length + 3] = (byte)'\n';
				long sent = Net.Send(socket, request);
				Console.WriteLine($"tcp: sent {sent} bytes");
			}

			// Printed as text, a line at a time, so the terminal shows headers and markup as they arrive
			byte[] buffer = new byte[4096];
			char[] line = new char[200];
			int lineLength = 0;
			long total = 0;
			int lines = 0;
			while (true)
			{
				long got = Net.Receive(socket, buffer, ReadTimeoutMs);
				if (got <= 0)
				{
					Console.WriteLine(got == 0 ? "tcp: peer closed" : $"tcp: receive ended ({got})");
					break;
				}
				total += got;
				for (int i = 0; i < got; i++)
				{
					byte b = buffer[i];
					if (b == (byte)'\n' || lineLength == line.Length)
					{
						if (lines++ < 40)
							Console.WriteLine(new string(line, 0, lineLength));
						lineLength = 0;
						if (b == (byte)'\n')
							continue;
					}
					if (b != (byte)'\r')
						line[lineLength++] = b >= 0x20 && b < 0x7F ? (char)b : '.';
				}
			}
			if (lineLength > 0 && lines++ < 40)
				Console.WriteLine(new string(line, 0, lineLength));
			if (lines > 40)
				Console.WriteLine($"tcp: ... {lines - 40} more lines");
			Console.WriteLine($"tcp: received {total} bytes in {Kernel.TickCount - start} ms");
			Net.Close(socket);
			return 0;
		}
	}
}
