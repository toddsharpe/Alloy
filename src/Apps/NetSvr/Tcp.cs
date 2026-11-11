using System;
using System.Buffers.Binary;
using Alloy.Abi;
using Alloy.Sdk;

namespace Alloy.Apps.NetSvr
{
	internal enum TcpState
	{
		Closed,
		SynSent,
		Established,
		FinWait1,
		FinWait2,
		CloseWait,
		Closing,
		LastAck,
		TimeWait,
	}

	// One connection's control block (RFC 793): the two sequence spaces, a ring each way, and the retransmit timer
	internal sealed class TcpConnection
	{
		public const int ReceiveCapacity = 65536;
		public const int SendCapacity = 32768;

		public TcpState State;
		public uint RemoteIp;
		public ushort RemotePort;
		public ushort PeerMss;

		// SendUnacked is the sequence number of the send ring's first byte; SendNext the next to transmit
		public uint SendUnacked;
		public uint SendNext;
		public uint SendWindow;
		public int SendCount;
		public readonly byte[] SendRing = new byte[SendCapacity];

		public uint RecvNext;
		public int RecvHead;
		public int RecvCount;
		public readonly byte[] RecvRing = new byte[ReceiveCapacity];

		public bool FinReceived;
		public bool FinQueued;
		public bool FinSent;
		public bool Reset;
		public ulong RtoDeadline;
		public ulong Rto;
		public int Retries;
		public ulong TimeWaitUntil;

		public void Open(uint ip, ushort port)
		{
			State = TcpState.SynSent;
			RemoteIp = ip;
			RemotePort = port;
			PeerMss = Tcp.Mss;
			SendUnacked = SendNext = Tcp.InitialSequence();
			SendWindow = Tcp.Mss;
			SendCount = 0;
			RecvNext = 0;
			RecvHead = RecvCount = 0;
			FinReceived = FinQueued = FinSent = Reset = false;
			RtoDeadline = 0;
			Rto = Tcp.InitialRto;
			Retries = 0;
			TimeWaitUntil = 0;
		}

		public int SendUnsent => SendCount - (int)(SendNext - SendUnacked);

		public int ReceiveFree => ReceiveCapacity - RecvCount;

		// Whether a read can return now: bytes, end of stream, or an error
		public bool Readable => RecvCount > 0 || FinReceived || Reset || State == TcpState.Closed;

		public int Read(Span<byte> destination)
		{
			int count = Math.Min(destination.Length, RecvCount);
			int first = Math.Min(count, ReceiveCapacity - RecvHead);
			RecvRing.AsSpan(RecvHead, first).CopyTo(destination);
			RecvRing.AsSpan(0, count - first).CopyTo(destination.Slice(first));
			RecvHead = (RecvHead + count) % ReceiveCapacity;
			RecvCount -= count;
			return count;
		}

		public int Append(ReadOnlySpan<byte> data)
		{
			int count = Math.Min(data.Length, ReceiveFree);
			int tail = (RecvHead + RecvCount) % ReceiveCapacity;
			int first = Math.Min(count, ReceiveCapacity - tail);
			data.Slice(0, first).CopyTo(RecvRing.AsSpan(tail));
			data.Slice(first, count - first).CopyTo(RecvRing);
			RecvCount += count;
			return count;
		}

		public int Queue(ReadOnlySpan<byte> data)
		{
			int count = Math.Min(data.Length, SendCapacity - SendCount);
			int tail = (int)((SendUnacked % SendCapacity + (uint)SendCount) % SendCapacity);
			int first = Math.Min(count, SendCapacity - tail);
			data.Slice(0, first).CopyTo(SendRing.AsSpan(tail));
			data.Slice(first, count - first).CopyTo(SendRing);
			SendCount += count;
			return count;
		}

		// The bytes at a sequence number, in one piece, for a segment under construction
		public void CopyOut(uint sequence, Span<byte> destination)
		{
			int offset = (int)(sequence % SendCapacity);
			int first = Math.Min(destination.Length, SendCapacity - offset);
			SendRing.AsSpan(offset, first).CopyTo(destination);
			SendRing.AsSpan(0, destination.Length - first).CopyTo(destination.Slice(first));
		}
	}

	// A TCP client (RFC 793, with the MSS option): in-order receive, go-back-N retransmission from one timer, up to eight segments in flight
	internal static class Tcp
	{
		public const ushort Mss = 1460;
		public const ulong InitialRto = 1000;
		private const ulong MaxRto = 8000;
		private const int MaxRetries = 5;
		private const int MaxInFlight = 8;
		private const ulong TimeWaitMs = 1000;
		private const int HeaderSize = 20;

		private const byte Fin = 0x01;
		private const byte Syn = 0x02;
		private const byte Rst = 0x04;
		private const byte Psh = 0x08;
		private const byte Ack = 0x10;

		private static uint s_sequenceSalt = 0x1D4A7F3B;

		// Drawn from the clock and a running salt, so reboots don't reuse a sequence space
		public static uint InitialSequence()
		{
			s_sequenceSalt = s_sequenceSalt * 1664525 + 1013904223;
			return (uint)(Kernel.TickCount * 262144) + s_sequenceSalt;
		}

		public static bool Connect(Socket socket, uint ip, ushort port)
		{
			if (socket.Port == 0)
				socket.Port = Stack.EphemeralPort();
			TcpConnection tcp = socket.Tcp;
			tcp.Open(ip, port);
			return Transmit(socket, Syn, tcp.SendUnacked, ReadOnlySpan<byte>.Empty, true) && Arm(tcp);
		}

		// Queues what fits in the send ring and pushes what the window allows; the rest waits for ACKs
		public static int Send(Socket socket, ReadOnlySpan<byte> data)
		{
			TcpConnection tcp = socket.Tcp;
			if (tcp.State != TcpState.Established && tcp.State != TcpState.CloseWait)
				return -1;
			int queued = tcp.Queue(data);
			Output(socket);
			return queued;
		}

		// A graceful close: the FIN follows whatever is still queued
		public static void Close(Socket socket)
		{
			TcpConnection tcp = socket.Tcp;
			switch (tcp.State)
			{
				case TcpState.SynSent:
					Abort(socket);
					break;
				case TcpState.Established:
					tcp.State = TcpState.FinWait1;
					tcp.FinQueued = true;
					Output(socket);
					break;
				case TcpState.CloseWait:
					tcp.State = TcpState.LastAck;
					tcp.FinQueued = true;
					Output(socket);
					break;
				default:
					break;
			}
		}

		// A reset to the peer and the connection gone at once
		public static void Abort(Socket socket)
		{
			TcpConnection tcp = socket.Tcp;
			if (tcp.State != TcpState.Closed && tcp.State != TcpState.TimeWait)
				Transmit(socket, Rst | Ack, tcp.SendNext, ReadOnlySpan<byte>.Empty, false);
			tcp.State = TcpState.Closed;
			tcp.Reset = true;
			tcp.RtoDeadline = 0;
		}

		// Retransmission and TIME-WAIT expiry, from the server's tick
		public static void Tick(ulong now)
		{
			foreach (Socket socket in Stack.Sockets)
			{
				if (socket.Protocol != SocketProtocol.Tcp)
					continue;
				TcpConnection tcp = socket.Tcp;
				if (tcp.State == TcpState.TimeWait && now >= tcp.TimeWaitUntil)
				{
					tcp.State = TcpState.Closed;
					continue;
				}
				if (tcp.RtoDeadline == 0 || now < tcp.RtoDeadline)
					continue;
				if (++tcp.Retries > MaxRetries)
				{
					tcp.State = TcpState.Closed;
					tcp.Reset = true;
					tcp.RtoDeadline = 0;
					continue;
				}
				tcp.Rto = Math.Min(tcp.Rto * 2, MaxRto);
				Retransmit(socket);
				Arm(tcp);
			}
		}

		// A segment from the IPv4 layer, already matched to this host
		public static void Receive(uint source, uint destination, ReadOnlySpan<byte> segment)
		{
			if (segment.Length < HeaderSize || Checksum(source, destination, segment) != 0)
				return;
			int headerLength = (segment[12] >> 4) * 4;
			if (headerLength < HeaderSize || headerLength > segment.Length)
				return;
			ushort sourcePort = BinaryPrimitives.ReadUInt16BigEndian(segment);
			ushort destinationPort = BinaryPrimitives.ReadUInt16BigEndian(segment.Slice(2));
			Socket socket = Find(destinationPort, source, sourcePort);
			if (socket == null)
			{
				RefuseUnknown(source, sourcePort, destinationPort, segment);
				return;
			}

			TcpConnection tcp = socket.Tcp;
			uint sequence = BinaryPrimitives.ReadUInt32BigEndian(segment.Slice(4));
			uint acknowledgment = BinaryPrimitives.ReadUInt32BigEndian(segment.Slice(8));
			byte flags = segment[13];
			uint window = BinaryPrimitives.ReadUInt16BigEndian(segment.Slice(14));
			ReadOnlySpan<byte> options = segment.Slice(HeaderSize, headerLength - HeaderSize);
			ReadOnlySpan<byte> data = segment.Slice(headerLength);

			if (tcp.State == TcpState.SynSent)
			{
				if ((flags & Rst) != 0)
				{
					tcp.State = TcpState.Closed;
					tcp.Reset = true;
					tcp.RtoDeadline = 0;
					return;
				}
				if ((flags & (Syn | Ack)) != (Syn | Ack) || acknowledgment != tcp.SendNext + 1)
					return;
				tcp.SendNext++;
				tcp.SendUnacked = acknowledgment;
				tcp.RecvNext = sequence + 1;
				tcp.SendWindow = window;
				tcp.PeerMss = ParseMss(options);
				tcp.State = TcpState.Established;
				tcp.RtoDeadline = 0;
				tcp.Retries = 0;
				tcp.Rto = InitialRto;
				SendAck(socket);
				return;
			}

			if ((flags & Rst) != 0)
			{
				tcp.State = TcpState.Closed;
				tcp.Reset = true;
				tcp.RtoDeadline = 0;
				return;
			}

			// Only the segment that starts at RecvNext moves the receive side; an overlap is trimmed, anything else just gets an ACK
			int trim = (int)(tcp.RecvNext - sequence);
			if (trim < 0 || trim > data.Length + ((flags & Fin) != 0 ? 1 : 0))
			{
				SendAck(socket);
				return;
			}
			if (trim > data.Length)
			{
				SendAck(socket);
				return;
			}
			data = data.Slice(trim);

			if ((flags & Ack) != 0)
				Acknowledge(socket, acknowledgment, window);
			if (tcp.State == TcpState.Closed)
				return;

			bool receiving = tcp.State == TcpState.Established || tcp.State == TcpState.FinWait1 || tcp.State == TcpState.FinWait2;
			if (data.Length > 0 && receiving)
			{
				int taken = tcp.Append(data);
				tcp.RecvNext += (uint)taken;
				if (taken < data.Length)
				{
					SendAck(socket);
					return;
				}
			}

			if ((flags & Fin) != 0 && receiving && !tcp.FinReceived)
			{
				tcp.RecvNext++;
				tcp.FinReceived = true;
				if (tcp.State == TcpState.Established)
					tcp.State = TcpState.CloseWait;
				else if (tcp.State == TcpState.FinWait1)
					tcp.State = TcpState.Closing;
				else
					EnterTimeWait(tcp);
			}

			if (data.Length > 0 || (flags & Fin) != 0)
				SendAck(socket);
			Output(socket);
		}

		private static void Acknowledge(Socket socket, uint acknowledgment, uint window)
		{
			TcpConnection tcp = socket.Tcp;
			int acked = (int)(acknowledgment - tcp.SendUnacked);
			int outstanding = (int)(tcp.SendNext - tcp.SendUnacked);
			if (acked < 0 || acked > outstanding)
				return;
			tcp.SendWindow = window;
			if (acked == 0)
				return;

			// The FIN takes one sequence number past the data
			int finBytes = tcp.FinSent && acknowledgment == tcp.SendNext ? 1 : 0;
			tcp.SendCount -= acked - finBytes;
			tcp.SendUnacked = acknowledgment;
			tcp.Retries = 0;
			tcp.Rto = InitialRto;
			if (tcp.SendUnacked == tcp.SendNext)
				tcp.RtoDeadline = 0;
			else
				Arm(tcp);

			if (finBytes == 0)
				return;
			if (tcp.State == TcpState.FinWait1)
				tcp.State = TcpState.FinWait2;
			else if (tcp.State == TcpState.Closing)
				EnterTimeWait(tcp);
			else if (tcp.State == TcpState.LastAck)
				tcp.State = TcpState.Closed;
		}

		// Sends as much queued data as the peer's window and the in-flight cap allow, then the FIN once everything is out
		private static void Output(Socket socket)
		{
			TcpConnection tcp = socket.Tcp;
			if (tcp.State == TcpState.Closed || tcp.State == TcpState.SynSent || tcp.State == TcpState.TimeWait)
				return;
			Span<byte> buffer = stackalloc byte[Mss];
			while (tcp.SendUnsent > 0)
			{
				int inFlight = (int)(tcp.SendNext - tcp.SendUnacked);
				int window = (int)tcp.SendWindow - inFlight;
				if (window <= 0 || inFlight >= MaxInFlight * Mss)
					break;
				int length = Math.Min(Math.Min(tcp.SendUnsent, window), Math.Min(tcp.PeerMss, Mss));
				tcp.CopyOut(tcp.SendNext, buffer.Slice(0, length));
				if (!Transmit(socket, Psh | Ack, tcp.SendNext, buffer.Slice(0, length), false))
					break;
				tcp.SendNext += (uint)length;
				Arm(tcp);
			}
			if (tcp.FinQueued && !tcp.FinSent && tcp.SendUnsent == 0)
			{
				if (Transmit(socket, Fin | Ack, tcp.SendNext, ReadOnlySpan<byte>.Empty, false))
				{
					tcp.FinSent = true;
					tcp.SendNext++;
					Arm(tcp);
				}
			}
		}

		// Everything from the oldest unacknowledged byte again, SYN and FIN included
		private static void Retransmit(Socket socket)
		{
			TcpConnection tcp = socket.Tcp;
			if (tcp.State == TcpState.SynSent)
			{
				Transmit(socket, Syn, tcp.SendUnacked, ReadOnlySpan<byte>.Empty, true);
				return;
			}
			Span<byte> buffer = stackalloc byte[Mss];
			uint sequence = tcp.SendUnacked;
			int remaining = tcp.SendCount - tcp.SendUnsent;
			int segments = 0;
			while (remaining > 0 && segments++ < MaxInFlight)
			{
				int length = Math.Min(remaining, Math.Min(tcp.PeerMss, Mss));
				tcp.CopyOut(sequence, buffer.Slice(0, length));
				if (!Transmit(socket, Psh | Ack, sequence, buffer.Slice(0, length), false))
					return;
				sequence += (uint)length;
				remaining -= length;
			}
			if (tcp.FinSent && remaining == 0)
				Transmit(socket, Fin | Ack, sequence, ReadOnlySpan<byte>.Empty, false);
		}

		private static void SendAck(Socket socket) => Transmit(socket, Ack, socket.Tcp.SendNext, ReadOnlySpan<byte>.Empty, false);

		private static void EnterTimeWait(TcpConnection tcp)
		{
			tcp.State = TcpState.TimeWait;
			tcp.RtoDeadline = 0;
			tcp.TimeWaitUntil = Kernel.TickCount + TimeWaitMs;
		}

		private static bool Arm(TcpConnection tcp)
		{
			tcp.RtoDeadline = Kernel.TickCount + tcp.Rto;
			return true;
		}

		// A segment to a port nobody listens on gets a reset, as a real stack answers
		private static void RefuseUnknown(uint source, ushort sourcePort, ushort destinationPort, ReadOnlySpan<byte> segment)
		{
			byte flags = segment[13];
			if ((flags & Rst) != 0 || Stack.Ip == 0)
				return;
			uint sequence = BinaryPrimitives.ReadUInt32BigEndian(segment.Slice(4));
			uint acknowledgment = BinaryPrimitives.ReadUInt32BigEndian(segment.Slice(8));
			Span<byte> header = Stack.TxPayload.Slice(0, HeaderSize);
			if ((flags & Ack) != 0)
				WriteHeader(header, destinationPort, sourcePort, acknowledgment, 0, Rst, 0);
			else
				WriteHeader(header, destinationPort, sourcePort, 0, sequence + (uint)(segment.Length - (segment[12] >> 4) * 4) + ((flags & Syn) != 0 ? 1u : 0), Rst | Ack, 0);
			BinaryPrimitives.WriteUInt16BigEndian(header.Slice(16), Checksum(Stack.Ip, source, header));
			Stack.SendIpv4(source, Stack.ProtoTcp, HeaderSize);
		}

		private static bool Transmit(Socket socket, byte flags, uint sequence, ReadOnlySpan<byte> data, bool withMss)
		{
			TcpConnection tcp = socket.Tcp;
			int headerLength = HeaderSize + (withMss ? 4 : 0);
			Span<byte> segment = Stack.TxPayload.Slice(0, headerLength + data.Length);
			WriteHeader(segment, socket.Port, tcp.RemotePort, sequence, tcp.RecvNext, flags, (ushort)Math.Min(tcp.ReceiveFree, 65535));
			segment[12] = (byte)(headerLength / 4 << 4);
			if (withMss)
			{
				segment[20] = 2;
				segment[21] = 4;
				BinaryPrimitives.WriteUInt16BigEndian(segment.Slice(22), Mss);
			}
			data.CopyTo(segment.Slice(headerLength));
			BinaryPrimitives.WriteUInt16BigEndian(segment.Slice(16), Checksum(Stack.Ip, tcp.RemoteIp, segment));
			return Stack.SendIpv4(tcp.RemoteIp, Stack.ProtoTcp, segment.Length);
		}

		private static void WriteHeader(Span<byte> header, ushort sourcePort, ushort destinationPort, uint sequence, uint acknowledgment, byte flags, ushort window)
		{
			BinaryPrimitives.WriteUInt16BigEndian(header, sourcePort);
			BinaryPrimitives.WriteUInt16BigEndian(header.Slice(2), destinationPort);
			BinaryPrimitives.WriteUInt32BigEndian(header.Slice(4), sequence);
			BinaryPrimitives.WriteUInt32BigEndian(header.Slice(8), acknowledgment);
			header[12] = HeaderSize / 4 << 4;
			header[13] = flags;
			BinaryPrimitives.WriteUInt16BigEndian(header.Slice(14), window);
			BinaryPrimitives.WriteUInt16BigEndian(header.Slice(16), 0);
			BinaryPrimitives.WriteUInt16BigEndian(header.Slice(18), 0);
		}

		private static ushort ParseMss(ReadOnlySpan<byte> options)
		{
			for (int i = 0; i < options.Length;)
			{
				byte kind = options[i];
				if (kind == 0)
					break;
				if (kind == 1)
				{
					i++;
					continue;
				}
				if (i + 1 >= options.Length || options[i + 1] < 2)
					break;
				if (kind == 2 && options[i + 1] == 4 && i + 3 < options.Length)
				{
					ushort mss = BinaryPrimitives.ReadUInt16BigEndian(options.Slice(i + 2));
					return mss == 0 ? Mss : Math.Min(mss, Mss);
				}
				i += options[i + 1];
			}
			return Mss;
		}

		// A connection by its local port and peer; a SYN-SENT socket is matched the same way since Connect set its peer
		private static Socket Find(ushort localPort, uint remoteIp, ushort remotePort)
		{
			foreach (Socket socket in Stack.Sockets)
			{
				if (socket.Protocol == SocketProtocol.Tcp && socket.Port == localPort && socket.Tcp.State != TcpState.Closed && socket.Tcp.RemoteIp == remoteIp && socket.Tcp.RemotePort == remotePort)
					return socket;
			}
			return null;
		}

		// The Internet checksum over the pseudo header (addresses, protocol, length) and the segment
		private static ushort Checksum(uint source, uint destination, ReadOnlySpan<byte> segment)
		{
			uint sum = (source >> 16) + (source & 0xFFFF) + (destination >> 16) + (destination & 0xFFFF) + Stack.ProtoTcp + (uint)segment.Length;
			int i = 0;
			for (; i + 1 < segment.Length; i += 2)
				sum += BinaryPrimitives.ReadUInt16BigEndian(segment.Slice(i));
			if (i < segment.Length)
				sum += (uint)segment[i] << 8;
			while ((sum >> 16) != 0)
				sum = (sum & 0xFFFF) + (sum >> 16);
			return (ushort)~sum;
		}
	}
}
