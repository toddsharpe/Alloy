using System;
using System.Buffers.Binary;
using Alloy.Abi;
using Alloy.Sdk;

namespace Alloy.Apps.NetSvr
{
	internal enum SocketProtocol
	{
		None,
		Udp,
		Icmp,
		Tcp,
	}

	// One socket; received datagrams wait in a small ring of fixed slots, so nothing is allocated per packet
	internal sealed class Socket
	{
		public const int QueueDepth = 8;

		public SocketProtocol Protocol;
		public ushort Port;
		public int Owner;
		public int Head;
		public int Count;
		public readonly uint[] FromIp = new uint[QueueDepth];
		public readonly ushort[] FromPort = new ushort[QueueDepth];
		public readonly int[] Length = new int[QueueDepth];
		public readonly byte[] Data = new byte[QueueDepth * NetProtocol.MaxPayload];

		// A stream socket's connection, made on first use and kept with the slot since nothing frees it
		public TcpConnection Tcp;

		// The app's read or connect waiting for the connection, answered by the server loop
		public Connection Pending;
		public ulong PendingType;
		public int PendingLength;
		public ulong PendingDeadline;

		public void Enqueue(uint ip, ushort port, ReadOnlySpan<byte> data)
		{
			if (Count == QueueDepth)
				return;
			int slot = (Head + Count++) % QueueDepth;
			FromIp[slot] = ip;
			FromPort[slot] = port;
			Length[slot] = Math.Min(data.Length, NetProtocol.MaxPayload);
			data.Slice(0, Length[slot]).CopyTo(Data.AsSpan(slot * NetProtocol.MaxPayload));
		}
	}

	// MetalOS-NetSvr's stack (Net/*.cpp): Ethernet, ARP with a cache and one frame pending resolution, IPv4, ICMP echo and UDP; addresses are host order
	internal static class Stack
	{
		public const int MaxSockets = 64;
		private const ushort EthIpv4 = 0x0800;
		private const ushort EthArp = 0x0806;
		private const byte ProtoIcmp = 1;
		public const byte ProtoTcp = 6;
		private const byte ProtoUdp = 17;
		private const int ArpEntries = 32;
		private const ulong ArpRetryMs = 1000;
		private const ushort FirstEphemeral = 49152;
		private const uint Broadcast = 0xFFFFFFFF;
		private const int EthernetHeader = 14;
		private const int Ipv4Header = 20;
		private const int ArpSize = 28;

		public static readonly byte[] Mac = new byte[6];
		public static uint Ip;
		public static uint Mask;
		public static uint Gateway;
		public static uint Dns;
		public static readonly Socket[] Sockets = new Socket[MaxSockets];

		private static readonly uint[] s_arpIp = new uint[ArpEntries];
		private static readonly ulong[] s_arpMac = new ulong[ArpEntries];
		private static int s_arpNext;

		// The frame under construction, and the one waiting on ARP
		private static readonly byte[] s_tx = new byte[NetFrame.Mtu];
		private static readonly byte[] s_pending = new byte[NetFrame.Mtu];
		private static int s_pendingLength;
		private static uint s_pendingHop;
		private static ulong s_pendingSent;
		private static ushort s_nextPort = FirstEphemeral;

		public static void Initialize(ReadOnlySpan<byte> mac) => mac.CopyTo(Mac);

		// Where a transport builds its segment, right after the IPv4 header SendIpv4 fills in
		public static Span<byte> TxPayload => s_tx.AsSpan(EthernetHeader + Ipv4Header);

		public static ushort EphemeralPort()
		{
			ushort port = s_nextPort;
			s_nextPort = s_nextPort == 0xFFFF ? FirstEphemeral : (ushort)(s_nextPort + 1);
			return port;
		}

		// A frame from the kernel's ring
		public static void Receive(ReadOnlySpan<byte> frame)
		{
			if (frame.Length < EthernetHeader)
				return;
			ushort type = BinaryPrimitives.ReadUInt16BigEndian(frame.Slice(12));
			if (type == EthArp)
				ReceiveArp(frame.Slice(EthernetHeader));
			else if (type == EthIpv4)
				ReceiveIpv4(frame.Slice(EthernetHeader));
		}

		// The frame waiting on ARP goes out once its next hop resolves, or is dropped when the request goes unanswered
		public static void Dispatch()
		{
			if (s_pendingLength == 0)
				return;
			if (Lookup(s_pendingHop, out ulong mac))
			{
				WriteMac(s_pending, mac);
				Transmit(s_pending, s_pendingLength);
				s_pendingLength = 0;
			}
			else if (Kernel.TickCount - s_pendingSent >= ArpRetryMs)
			{
				s_pendingLength = 0;
			}
		}

		public static bool SendUdp(Socket socket, uint ip, ushort port, ReadOnlySpan<byte> data)
		{
			if (data.Length > NetProtocol.MaxPayload - 8)
				return false;
			if (socket.Port == 0)
				socket.Port = EphemeralPort();
			Span<byte> udp = s_tx.AsSpan(EthernetHeader + Ipv4Header);
			BinaryPrimitives.WriteUInt16BigEndian(udp, socket.Port);
			BinaryPrimitives.WriteUInt16BigEndian(udp.Slice(2), port);
			BinaryPrimitives.WriteUInt16BigEndian(udp.Slice(4), (ushort)(8 + data.Length));
			BinaryPrimitives.WriteUInt16BigEndian(udp.Slice(6), 0);
			data.CopyTo(udp.Slice(8));
			return SendIpv4(ip, ProtoUdp, 8 + data.Length);
		}

		// The app builds the echo message; the socket's port becomes its id and the checksum is filled in here
		public static bool SendIcmp(Socket socket, uint ip, ReadOnlySpan<byte> data)
		{
			if (data.Length < 8 || data.Length > NetProtocol.MaxPayload)
				return false;
			if (socket.Port == 0)
				socket.Port = EphemeralPort();
			Span<byte> icmp = s_tx.AsSpan(EthernetHeader + Ipv4Header, data.Length);
			data.CopyTo(icmp);
			BinaryPrimitives.WriteUInt16BigEndian(icmp.Slice(4), socket.Port);
			BinaryPrimitives.WriteUInt16BigEndian(icmp.Slice(2), 0);
			BinaryPrimitives.WriteUInt16BigEndian(icmp.Slice(2), Checksum(icmp));
			return SendIpv4(ip, ProtoIcmp, data.Length);
		}

		private static void ReceiveArp(ReadOnlySpan<byte> arp)
		{
			if (arp.Length < ArpSize || BinaryPrimitives.ReadUInt16BigEndian(arp) != 1 || BinaryPrimitives.ReadUInt16BigEndian(arp.Slice(2)) != EthIpv4)
				return;
			ushort op = BinaryPrimitives.ReadUInt16BigEndian(arp.Slice(6));
			ulong senderMac = ReadMac(arp.Slice(8));
			uint senderIp = BinaryPrimitives.ReadUInt32BigEndian(arp.Slice(14));
			uint targetIp = BinaryPrimitives.ReadUInt32BigEndian(arp.Slice(24));
			if (senderIp != 0)
				Learn(senderIp, senderMac);
			if (op == 1 && Ip != 0 && targetIp == Ip)
			{
				WriteArp(s_tx.AsSpan(EthernetHeader), 2, senderMac, senderIp);
				Frame(s_tx, senderMac, EthArp);
				Transmit(s_tx, EthernetHeader + ArpSize);
			}
		}

		private static void ReceiveIpv4(ReadOnlySpan<byte> ip)
		{
			if (ip.Length < Ipv4Header || ip[0] != 0x45 || Checksum(ip.Slice(0, Ipv4Header)) != 0)
				return;
			int total = BinaryPrimitives.ReadUInt16BigEndian(ip.Slice(2));
			if (total < Ipv4Header || total > ip.Length || (BinaryPrimitives.ReadUInt16BigEndian(ip.Slice(6)) & 0x3FFF) != 0)
				return;
			uint source = BinaryPrimitives.ReadUInt32BigEndian(ip.Slice(12));
			uint destination = BinaryPrimitives.ReadUInt32BigEndian(ip.Slice(16));

			// Before DHCP finishes a server may unicast to the offered address; the NIC already filtered by MAC
			bool accept = destination == Ip || destination == Broadcast || Ip == 0 || (Mask != 0 && destination == (Ip | ~Mask));
			if (!accept)
				return;
			ReadOnlySpan<byte> payload = ip.Slice(Ipv4Header, total - Ipv4Header);
			if (ip[9] == ProtoIcmp)
				ReceiveIcmp(source, payload);
			else if (ip[9] == ProtoUdp)
				ReceiveUdp(source, payload);
			else if (ip[9] == ProtoTcp)
				Tcp.Receive(source, destination, payload);
		}

		private static void ReceiveIcmp(uint source, ReadOnlySpan<byte> icmp)
		{
			if (icmp.Length < 8 || Checksum(icmp) != 0)
				return;
			if (icmp[0] == 8 && Ip != 0)
			{
				// Echo request: reflect id, sequence and payload
				Span<byte> reply = s_tx.AsSpan(EthernetHeader + Ipv4Header, icmp.Length);
				icmp.CopyTo(reply);
				reply[0] = 0;
				BinaryPrimitives.WriteUInt16BigEndian(reply.Slice(2), 0);
				BinaryPrimitives.WriteUInt16BigEndian(reply.Slice(2), Checksum(reply));
				SendIpv4(source, ProtoIcmp, icmp.Length);
			}
			else if (icmp[0] == 0)
			{
				// An echo reply goes whole to the ICMP socket whose port is its id
				Socket socket = Find(SocketProtocol.Icmp, BinaryPrimitives.ReadUInt16BigEndian(icmp.Slice(4)));
				socket?.Enqueue(source, 0, icmp);
			}
		}

		private static void ReceiveUdp(uint source, ReadOnlySpan<byte> udp)
		{
			if (udp.Length < 8)
				return;
			int size = Math.Min(BinaryPrimitives.ReadUInt16BigEndian(udp.Slice(4)), udp.Length) - 8;
			if (size < 0)
				return;
			Socket socket = Find(SocketProtocol.Udp, BinaryPrimitives.ReadUInt16BigEndian(udp.Slice(2)));
			socket?.Enqueue(source, BinaryPrimitives.ReadUInt16BigEndian(udp), udp.Slice(8, size));
		}

		private static Socket Find(SocketProtocol protocol, ushort port)
		{
			foreach (Socket socket in Sockets)
			{
				if (socket != null && socket.Protocol == protocol && socket.Port == port)
					return socket;
			}
			return null;
		}

		// The IPv4 header goes in front of a payload already at s_tx + 34
		public static bool SendIpv4(uint destination, byte protocol, int length)
		{
			Span<byte> ip = s_tx.AsSpan(EthernetHeader, Ipv4Header);
			ip[0] = 0x45;
			ip[1] = 0;
			BinaryPrimitives.WriteUInt16BigEndian(ip.Slice(2), (ushort)(Ipv4Header + length));
			BinaryPrimitives.WriteUInt16BigEndian(ip.Slice(4), 0);
			BinaryPrimitives.WriteUInt16BigEndian(ip.Slice(6), 0);
			ip[8] = 64;
			ip[9] = protocol;
			BinaryPrimitives.WriteUInt16BigEndian(ip.Slice(10), 0);
			BinaryPrimitives.WriteUInt32BigEndian(ip.Slice(12), Ip);
			BinaryPrimitives.WriteUInt32BigEndian(ip.Slice(16), destination);
			BinaryPrimitives.WriteUInt16BigEndian(ip.Slice(10), Checksum(ip));
			return SendEthernet(destination, EthIpv4, EthernetHeader + Ipv4Header + length);
		}

		// Broadcasts go straight out; anything else needs its next hop's MAC, which may wait one ARP round trip
		private static bool SendEthernet(uint destination, ushort type, int length)
		{
			if (destination == Broadcast || (Mask != 0 && destination == (Ip | ~Mask)))
			{
				Frame(s_tx, 0xFFFFFFFFFFFF, type);
				return Transmit(s_tx, length);
			}
			uint hop = Gateway != 0 && (destination & Mask) != (Ip & Mask) ? Gateway : destination;
			if (Lookup(hop, out ulong mac))
			{
				Frame(s_tx, mac, type);
				return Transmit(s_tx, length);
			}
			if (s_pendingLength != 0 && Kernel.TickCount - s_pendingSent < ArpRetryMs)
				return false;
			Frame(s_tx, 0, type);
			s_tx.AsSpan(0, length).CopyTo(s_pending);
			s_pendingLength = length;
			s_pendingHop = hop;
			s_pendingSent = Kernel.TickCount;

			WriteArp(s_tx.AsSpan(EthernetHeader), 1, 0, hop);
			Frame(s_tx, 0xFFFFFFFFFFFF, EthArp);
			return Transmit(s_tx, EthernetHeader + ArpSize);
		}

		private static void WriteArp(Span<byte> arp, ushort op, ulong targetMac, uint targetIp)
		{
			BinaryPrimitives.WriteUInt16BigEndian(arp, 1);
			BinaryPrimitives.WriteUInt16BigEndian(arp.Slice(2), EthIpv4);
			arp[4] = 6;
			arp[5] = 4;
			BinaryPrimitives.WriteUInt16BigEndian(arp.Slice(6), op);
			Mac.CopyTo(arp.Slice(8));
			BinaryPrimitives.WriteUInt32BigEndian(arp.Slice(14), Ip);
			WriteMac(arp.Slice(18), targetMac);
			BinaryPrimitives.WriteUInt32BigEndian(arp.Slice(24), targetIp);
		}

		private static void Frame(Span<byte> frame, ulong destination, ushort type)
		{
			WriteMac(frame, destination);
			Mac.CopyTo(frame.Slice(6));
			BinaryPrimitives.WriteUInt16BigEndian(frame.Slice(12), type);
		}

		private static bool Transmit(byte[] frame, int length)
		{
			// Ethernet's minimum frame is 60 bytes before the FCS; short ones are padded with zeros
			if (length < 60)
			{
				frame.AsSpan(length, 60 - length).Clear();
				length = 60;
			}
			return Kernel.NetSend(frame.AsSpan(0, length));
		}

		private static void Learn(uint ip, ulong mac)
		{
			for (int i = 0; i < ArpEntries; i++)
			{
				if (s_arpIp[i] == ip)
				{
					s_arpMac[i] = mac;
					return;
				}
			}
			s_arpIp[s_arpNext] = ip;
			s_arpMac[s_arpNext] = mac;
			s_arpNext = (s_arpNext + 1) % ArpEntries;
		}

		private static bool Lookup(uint ip, out ulong mac)
		{
			for (int i = 0; i < ArpEntries; i++)
			{
				if (s_arpIp[i] == ip && ip != 0)
				{
					mac = s_arpMac[i];
					return true;
				}
			}
			mac = 0;
			return false;
		}

		// The Internet checksum: the ones' complement of the ones' complement sum of 16-bit words
		public static ushort Checksum(ReadOnlySpan<byte> data)
		{
			uint sum = 0;
			int i = 0;
			for (; i + 1 < data.Length; i += 2)
				sum += BinaryPrimitives.ReadUInt16BigEndian(data.Slice(i));
			if (i < data.Length)
				sum += (uint)data[i] << 8;
			while ((sum >> 16) != 0)
				sum = (sum & 0xFFFF) + (sum >> 16);
			return (ushort)~sum;
		}

		private static ulong ReadMac(ReadOnlySpan<byte> p) => (ulong)p[0] << 40 | (ulong)p[1] << 32 | (ulong)p[2] << 24 | (ulong)p[3] << 16 | (ulong)p[4] << 8 | p[5];

		private static void WriteMac(Span<byte> p, ulong mac)
		{
			for (int i = 0; i < 6; i++)
				p[i] = (byte)(mac >> (40 - 8 * i));
		}
	}
}
