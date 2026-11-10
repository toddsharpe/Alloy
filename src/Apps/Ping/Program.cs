using System;
using Alloy.Abi;
using Alloy.Sdk;

namespace Alloy.Apps.Ping
{
	// MetalOS's ping.exe over an ICMP socket: we build the echo request, NetSvr fills in its id and checksum and hands back the replies to that id
	internal static class Program
	{
		private const string Payload = "Alloy ping";

		private static int Main(string[] args)
		{
			// The target is the first argument if it is a dotted address, else the gateway of interface 0
			uint target;
			if (args.Length < 1 || !Net.TryParseIp(args[0], out target))
			{
				if (!Net.GetInterfaceIp(out uint ip, out uint mask) || ip == 0)
				{
					Console.WriteLine("ping: no configured interface (run dhcp.exe first)");
					return 1;
				}
				target = Net.GatewayOrGuess(ip, mask);
			}
			Console.WriteLine($"ping {Net.FormatIp(target)}");

			long socket = Net.Socket(NetProtocol.SocketRaw, NetProtocol.ProtocolIcmp);
			if (socket < 0)
			{
				Console.WriteLine("  socket create failed");
				return 1;
			}

			Span<byte> message = stackalloc byte[8 + Payload.Length];
			Span<byte> reply = stackalloc byte[256];
			int received = 0;
			for (ushort sequence = 1; sequence <= 4; sequence++)
			{
				message.Clear();
				for (int i = 0; i < Payload.Length; i++)
					message[8 + i] = (byte)Payload[i];
				message[0] = 8;
				message[6] = (byte)(sequence >> 8);
				message[7] = (byte)sequence;
				ulong start = Kernel.TickCount;
				if (Net.SendTo(socket, message, target, 0) < 0)
				{
					Console.WriteLine($"  seq={sequence} send failed");
					Kernel.Sleep(500);
					continue;
				}

				// Every reply is to this socket's id; one to another sequence (a late one) is skipped
				bool replied = false;
				uint from = 0;
				while (!replied)
				{
					ulong waited = Kernel.TickCount - start;
					if (waited >= 1000)
						break;
					long got = Net.ReceiveFrom(socket, reply, out from, out _, 1000 - waited);
					if (got < 0)
						break;
					replied = got >= 8 && reply[0] == 0 && reply[6] == (byte)(sequence >> 8) && reply[7] == (byte)sequence;
				}

				if (replied)
				{
					Console.WriteLine($"  reply from {Net.FormatIp(from)}: seq={sequence} time={Kernel.TickCount - start}ms");
					received++;
				}
				else
				{
					Console.WriteLine($"  seq={sequence} timeout");
				}
				Kernel.Sleep(500);
			}

			Console.WriteLine($"ping: {received}/4 replies");
			Net.Close(socket);
			return 0;
		}
	}
}
