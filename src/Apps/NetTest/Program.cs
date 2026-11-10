using System;
using System.Text;
using Alloy.Abi;
using Alloy.Sdk;

namespace Alloy.Apps.NetTest
{
	// MetalOS's nettest.exe: a UDP datagram to the gateway's discard port
	internal static class Program
	{
		private const string Message = "hello from Alloy";

		private static int Main()
		{
			Console.WriteLine("NetTest: UDP datagram socket demo");
			Span<InterfaceInfo> interfaces = stackalloc InterfaceInfo[1];
			if (Net.GetInterfaces(interfaces) < 1)
			{
				Console.WriteLine("  no interfaces");
				return 1;
			}
			Net.GetInterfaceIp(out uint ip, out uint mask);
			Console.WriteLine($"  if0 {Net.FormatMac(interfaces[0].Mac)}  ip {Net.FormatIp(ip)} / {Net.FormatIp(mask)}");
			if (ip == 0)
			{
				Console.WriteLine("  interface has no IP - run dhcp.exe first");
				return 1;
			}

			uint gateway = Net.GatewayOrGuess(ip, mask);
			Console.WriteLine($"  gateway {Net.FormatIp(gateway)}");

			long socket = Net.Socket(NetProtocol.SocketDatagram, NetProtocol.ProtocolUdp);
			if (socket < 0)
			{
				Console.WriteLine("  SocketCreate failed");
				return 1;
			}
			Console.WriteLine($"  bind     = {Net.Bind(socket, 12345)}");

			// The first unicast send may wait on ARP, so retry a few times
			byte[] data = Encoding.ASCII.GetBytes(Message);
			for (int attempt = 0; attempt < 5; attempt++)
			{
				long result = Net.SendTo(socket, data, gateway, 9);
				Console.WriteLine($"  sendto[{attempt}] = {result}");
				if (result == 0)
					break;
				Kernel.Sleep(300);
			}
			Net.Close(socket);
			return 0;
		}
	}
}
