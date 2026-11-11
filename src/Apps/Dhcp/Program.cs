using System;
using System.Buffers.Binary;
using Alloy.Abi;
using Alloy.Sdk;

namespace Alloy.Apps.Dhcp
{
	// MetalOS's dhcp.exe: a minimal DHCP client (DISCOVER, OFFER, REQUEST, ACK) that applies the lease to interface 0
	internal static class Program
	{
		private const byte OpRequest = 1;
		private const byte HTypeEthernet = 1;
		private const byte HLenEthernet = 6;
		private const ushort FlagBroadcast = 0x8000;
		private const uint MagicCookie = 0x63825363;
		private const ushort ServerPort = 67;
		private const ushort ClientPort = 68;
		private const int HeaderSize = 240;
		private const int MinPacket = 300;
		private const int MaxPacket = 576;
		private const uint TransactionId = 0x50485831;

		private const byte Discover = 1;
		private const byte Offer = 2;
		private const byte Request = 3;
		private const byte Ack = 5;

		private const byte OptionSubnet = 1;
		private const byte OptionRouter = 3;
		private const byte OptionDns = 6;
		private const byte OptionRequestIp = 50;
		private const byte OptionMessageType = 53;
		private const byte OptionServerId = 54;
		private const byte OptionParameters = 55;
		private const byte OptionEnd = 255;

		private static int Main()
		{
			Console.WriteLine("dhcp: configuring interface 0");
			Span<InterfaceInfo> interfaces = stackalloc InterfaceInfo[1];
			if (Net.GetInterfaces(interfaces) < 1)
			{
				Console.WriteLine("  no interfaces found");
				return 1;
			}
			ReadOnlySpan<byte> mac = interfaces[0].Mac;
			Console.WriteLine($"  if0 mac {Net.FormatMac(mac)}");

			long socket = Net.Socket(NetProtocol.SocketDatagram, NetProtocol.ProtocolUdp);
			if (socket < 0)
			{
				Console.WriteLine("  socket create failed");
				return 1;
			}
			Net.Bind(socket, ClientPort);

			Span<byte> packet = stackalloc byte[MaxPacket];
			Span<byte> reply = stackalloc byte[MaxPacket];
			int length = Build(packet, mac, Discover, 0, 0);
			if (Net.SendTo(socket, packet.Slice(0, length), 0xFFFFFFFF, ServerPort) < 0)
				return Fail(socket, "  DISCOVER send failed");
			int replyLength = Await(socket, Offer, reply);
			if (replyLength < 0)
				return Fail(socket, "  no OFFER received");
			uint leased = BinaryPrimitives.ReadUInt32BigEndian(reply.Slice(16));
			Console.WriteLine($"  OFFER {Net.FormatIp(leased)}");

			// REQUEST echoes the offered address and the server id back
			ReadOnlySpan<byte> server = FindOption(reply.Slice(0, replyLength), OptionServerId);
			length = Build(packet, mac, Request, leased, server.Length == 4 ? BinaryPrimitives.ReadUInt32BigEndian(server) : 0);
			if (Net.SendTo(socket, packet.Slice(0, length), 0xFFFFFFFF, ServerPort) < 0)
				return Fail(socket, "  REQUEST send failed");
			replyLength = Await(socket, Ack, reply);
			if (replyLength < 0)
				return Fail(socket, "  no ACK received");

			uint address = BinaryPrimitives.ReadUInt32BigEndian(reply.Slice(16));
			ReadOnlySpan<byte> subnet = FindOption(reply.Slice(0, replyLength), OptionSubnet);
			uint mask = subnet.Length == 4 ? BinaryPrimitives.ReadUInt32BigEndian(subnet) : 0xFFFFFF00;
			if (!Net.SetInterfaceIp(address, mask))
				Console.WriteLine("  SetInterfaceIp failed");
			else
				Console.WriteLine($"  configured {Net.FormatIp(address)}");

			ReadOnlySpan<byte> router = FindOption(reply.Slice(0, replyLength), OptionRouter);
			if (router.Length >= 4)
			{
				uint gateway = BinaryPrimitives.ReadUInt32BigEndian(router);
				Net.SetGateway(gateway);
				Console.WriteLine($"  gateway {Net.FormatIp(gateway)}");
			}
			ReadOnlySpan<byte> dns = FindOption(reply.Slice(0, replyLength), OptionDns);
			if (dns.Length >= 4)
			{
				uint resolver = BinaryPrimitives.ReadUInt32BigEndian(dns);
				Net.SetDnsServer(resolver);
				Console.WriteLine($"  dns {Net.FormatIp(resolver)}");
			}
			Net.Close(socket);
			return 0;
		}

		private static int Fail(long socket, string message)
		{
			Console.WriteLine(message);
			Net.Close(socket);
			return 1;
		}

		// A DHCP message padded to the BOOTP minimum; REQUEST adds the requested address and server id
		private static int Build(Span<byte> packet, ReadOnlySpan<byte> mac, byte type, uint requestIp, uint serverId)
		{
			packet.Clear();
			packet[0] = OpRequest;
			packet[1] = HTypeEthernet;
			packet[2] = HLenEthernet;
			BinaryPrimitives.WriteUInt32BigEndian(packet.Slice(4), TransactionId);
			packet[10] = FlagBroadcast >> 8;
			mac.CopyTo(packet.Slice(28));
			BinaryPrimitives.WriteUInt32BigEndian(packet.Slice(236), MagicCookie);

			int at = HeaderSize;
			packet[at++] = OptionMessageType;
			packet[at++] = 1;
			packet[at++] = type;
			if (requestIp != 0)
			{
				packet[at++] = OptionRequestIp;
				packet[at++] = 4;
				BinaryPrimitives.WriteUInt32BigEndian(packet.Slice(at), requestIp);
				at += 4;
			}
			if (serverId != 0)
			{
				packet[at++] = OptionServerId;
				packet[at++] = 4;
				BinaryPrimitives.WriteUInt32BigEndian(packet.Slice(at), serverId);
				at += 4;
			}
			packet[at++] = OptionParameters;
			packet[at++] = 3;
			packet[at++] = OptionSubnet;
			packet[at++] = OptionRouter;
			packet[at++] = OptionDns;
			packet[at++] = OptionEnd;
			return Math.Max(at, MinPacket);
		}

		// A reply of the expected type for our transaction, or -1
		private static int Await(long socket, byte type, Span<byte> message)
		{
			for (int attempt = 0; attempt < 4; attempt++)
			{
				long got = Net.ReceiveFrom(socket, message, out _, out _, 3000);
				if (got < HeaderSize || BinaryPrimitives.ReadUInt32BigEndian(message.Slice(4)) != TransactionId)
					continue;
				ReadOnlySpan<byte> found = FindOption(message.Slice(0, (int)got), OptionMessageType);
				if (found.Length == 1 && found[0] == type)
					return (int)got;
			}
			return -1;
		}

		// The value of an option, which follow the fixed header as type, length, value; empty when absent
		private static ReadOnlySpan<byte> FindOption(ReadOnlySpan<byte> message, byte code)
		{
			int i = HeaderSize;
			while (i < message.Length)
			{
				byte c = message[i++];
				if (c == OptionEnd)
					break;
				if (c == 0)
					continue;
				if (i >= message.Length)
					break;
				int size = message[i++];
				if (i + size > message.Length)
					break;
				if (c == code)
					return message.Slice(i, size);
				i += size;
			}
			return ReadOnlySpan<byte>.Empty;
		}
	}
}
