using System;
using System.Buffers.Binary;
using Alloy.Abi;
using Alloy.Sdk;

namespace Alloy.Apps.NetSvr
{
	internal sealed class Connection
	{
		public long Reply;
		public long Process;
		public Memory<byte> Buffer;
	}

	// MetalOS-NetSvr: claims the kernel's frame ring and the NIC, runs the IPv4 stack, and serves sockets on "net"
	internal static class Program
	{
		private const int MaxConnections = 16;
		private const ulong TickMs = 10;
		private const ulong ReapMs = 500;

		// Serve's answer when the reply waits on the network: a connect or read the loop completes later
		private const long Deferred = long.MinValue;

		private static readonly Connection[] s_connections = new Connection[MaxConnections];
		private static Memory<byte> s_address;

		private static int Main()
		{
			Span<InterfaceInfo> interfaces = stackalloc InterfaceInfo[1];
			if (Net.GetInterfaces(interfaces) < 1)
			{
				Console.WriteLine("netsvr: no network interface");
				return 1;
			}
			NetFrameRing frames = NetFrameRing.Open();
			if (frames == null)
			{
				Console.WriteLine("netsvr: the kernel's frame ring is missing");
				return 1;
			}
			ReadOnlySpan<byte> mac = interfaces[0].Mac;
			Stack.Initialize(mac);
			for (int i = 0; i < Stack.MaxSockets; i++)
				Stack.Sockets[i] = new Socket();

			// The interface's address in a page anyone can map, so the taskbar shows it (MetalOS "net.addr")
			long address = SharedMemory.Create(4096);
			s_address = SharedMemory.Map(address, 4096);
			Endpoint.Register(NetProtocol.AddressEndpoint, address);

			long port = Port.Create();
			Endpoint.Register(NetProtocol.Endpoint, port);
			Console.WriteLine($"netsvr: up, mac {Net.FormatMac(mac)}");

			ReadOnlySpan<long> waits = stackalloc long[] { port, frames.ReadyEvent };
			ulong lastReap = 0;
			while (true)
			{
				Event.WaitAny(waits, TickMs);
				for (ReadOnlySpan<byte> frame = frames.Peek(); !frame.IsEmpty; frame = frames.Peek())
				{
					Stack.Receive(frame);
					frames.Advance();
				}
				long transferred;
				while ((transferred = Port.Receive(port, out IpcMessage message, 0)) >= 0)
					Handle(in message, transferred);
				ulong now = Kernel.TickCount;
				Tcp.Tick(now);
				Stack.Dispatch();
				CompletePending(now);
				FreeClosedSockets();
				if (now - lastReap >= ReapMs)
				{
					ReapDeadConnections();
					lastReap = now;
				}
			}
		}

		private static void Handle(in IpcMessage message, long transferred)
		{
			if (message.Type == NetProtocol.Connect)
			{
				Accept(in message, transferred);
				return;
			}
			int client = (int)message.Data[6];
			Connection connection = client >= 0 && client < MaxConnections ? s_connections[client] : null;
			if (connection == null)
			{
				if (transferred > 0)
					Kernel.Close(transferred);
				return;
			}
			if (message.Type == NetProtocol.Attach)
			{
				connection.Buffer = transferred > 0 ? SharedMemory.Map(transferred, (int)NetProtocol.BufferSize) : Memory<byte>.Empty;
				Reply(connection, message.Type, !connection.Buffer.IsEmpty ? 0 : Error(SyscallError.InvalidHandle));
				return;
			}

			IpcMessage reply = default;
			reply.Type = message.Type + NetProtocol.Reply;
			long result = Serve(connection, client, in message, ref reply);
			if (result == Deferred)
				return;
			reply.Data[0] = (ulong)result;
			Port.Send(connection.Reply, reply);
		}

		// The connection's reply port comes with Connect, and its process id lets dead apps' sockets be closed
		private static void Accept(in IpcMessage message, long reply)
		{
			IpcMessage answer = default;
			answer.Type = NetProtocol.Connect + NetProtocol.Reply;
			answer.Data[0] = (ulong)Error(SyscallError.Busy);
			for (int i = 0; i < MaxConnections; i++)
			{
				if (s_connections[i] == null)
				{
					s_connections[i] = new Connection { Reply = reply, Process = Kernel.OpenProcess((int)message.Data[0]) };
					answer.Data[0] = (ulong)i;
					break;
				}
			}
			Port.Send(reply, answer);
		}

		private static long Serve(Connection connection, int client, in IpcMessage message, ref IpcMessage reply)
		{
			switch (message.Type)
			{
				case NetProtocol.Create:
				{
					SocketProtocol protocol = message.Data[1] == NetProtocol.ProtocolUdp || (message.Data[1] == 0 && message.Data[0] == NetProtocol.SocketDatagram) ? SocketProtocol.Udp
						: message.Data[1] == NetProtocol.ProtocolIcmp || (message.Data[1] == 0 && message.Data[0] == NetProtocol.SocketRaw) ? SocketProtocol.Icmp
						: message.Data[1] == NetProtocol.ProtocolTcp || (message.Data[1] == 0 && message.Data[0] == NetProtocol.SocketStream) ? SocketProtocol.Tcp
						: SocketProtocol.None;
					if (protocol == SocketProtocol.None)
						return Error(SyscallError.InvalidArgument);
					for (int i = 0; i < Stack.MaxSockets; i++)
					{
						Socket socket = Stack.Sockets[i];
						if (socket.Protocol == SocketProtocol.None)
						{
							socket.Protocol = protocol;
							socket.Port = 0;
							socket.Owner = client;
							socket.Head = socket.Count = 0;
							socket.Pending = null;
							if (protocol == SocketProtocol.Tcp)
							{
								socket.Tcp ??= new TcpConnection();
								socket.Tcp.State = TcpState.Closed;
								socket.Tcp.Reset = false;
							}
							return i;
						}
					}
					return Error(SyscallError.Busy);
				}

				case NetProtocol.GetInterfaceIp:
					reply.Data[1] = Stack.Ip;
					reply.Data[2] = Stack.Mask;
					return 0;

				case NetProtocol.SetInterfaceIp:
					Stack.Ip = (uint)message.Data[1];
					Stack.Mask = (uint)message.Data[2];
					BinaryPrimitives.WriteUInt32LittleEndian(s_address.Span, Stack.Ip);
					Kernel.PublishValue("NetworkAddressIPv4", Net.FormatIp(Stack.Ip));
					Kernel.PublishValue("NetworkSubnetIPv4", Net.FormatIp(Stack.Mask));
					Kernel.PublishValue("NetworkDhcpIPv4", "1");
					return 0;

				case NetProtocol.GetGateway:
					reply.Data[1] = Stack.Gateway;
					return 0;

				case NetProtocol.SetGateway:
					Stack.Gateway = (uint)message.Data[1];
					Kernel.PublishValue("NetworkGatewayIPv4", Net.FormatIp(Stack.Gateway));
					return 0;
			}

			Socket target = message.Data[0] < Stack.MaxSockets ? Stack.Sockets[(int)message.Data[0]] : null;
			if (target == null || target.Protocol == SocketProtocol.None || target.Owner != client)
				return message.Type == NetProtocol.Close ? 0 : Error(SyscallError.InvalidHandle);
			switch (message.Type)
			{
				case NetProtocol.Bind:
					target.Port = (ushort)message.Data[1];
					return 0;

				case NetProtocol.SendTo:
				{
					if (connection.Buffer.IsEmpty || target.Protocol == SocketProtocol.Tcp)
						return Error(SyscallError.InvalidPointer);
					int length = (int)Math.Min(message.Data[3], NetProtocol.MaxPayload);
					ReadOnlySpan<byte> data = connection.Buffer.Span.Slice(0, length);
					bool sent = target.Protocol == SocketProtocol.Udp
						? Stack.SendUdp(target, (uint)message.Data[1], (ushort)message.Data[2], data)
						: Stack.SendIcmp(target, (uint)message.Data[1], data);
					return sent ? 0 : Error(SyscallError.Busy);
				}

				case NetProtocol.RecvFrom:
				{
					// Empty answers Timeout at once; the client retries until its own timeout, as MetalOS's does
					if (target.Count == 0 || connection.Buffer.IsEmpty || target.Protocol == SocketProtocol.Tcp)
						return Error(SyscallError.Timeout);
					int slot = target.Head;
					int length = Math.Min(target.Length[slot], (int)Math.Min(message.Data[1], NetProtocol.MaxPayload));
					target.Data.AsSpan(slot * NetProtocol.MaxPayload, length).CopyTo(connection.Buffer.Span);
					reply.Data[1] = target.FromIp[slot];
					reply.Data[2] = target.FromPort[slot];
					target.Head = (target.Head + 1) % Socket.QueueDepth;
					target.Count--;
					return length;
				}

				case NetProtocol.StreamConnect:
				{
					if (target.Protocol != SocketProtocol.Tcp || target.Tcp.State != TcpState.Closed || target.Pending != null || Stack.Ip == 0)
						return Error(SyscallError.InvalidArgument);
					if (!Tcp.Connect(target, (uint)message.Data[1], (ushort)message.Data[2]))
						return Error(SyscallError.Busy);
					Defer(target, connection, message.Type, 0, message.Data[3]);
					return Deferred;
				}

				case NetProtocol.StreamSend:
				{
					if (target.Protocol != SocketProtocol.Tcp || connection.Buffer.IsEmpty)
						return Error(SyscallError.InvalidArgument);
					if (target.Tcp.Reset)
						return Error(SyscallError.Closed);
					int length = (int)Math.Min(message.Data[1], NetProtocol.BufferSize);
					int queued = Tcp.Send(target, connection.Buffer.Span.Slice(0, length));
					return queued < 0 ? Error(SyscallError.Closed) : queued;
				}

				case NetProtocol.StreamRecv:
				{
					if (target.Protocol != SocketProtocol.Tcp || connection.Buffer.IsEmpty || target.Pending != null)
						return Error(SyscallError.InvalidArgument);
					int length = (int)Math.Min(message.Data[1], NetProtocol.BufferSize);
					if (target.Tcp.Readable)
						return Read(target, connection, length);
					if (message.Data[3] == 0)
						return Error(SyscallError.Timeout);
					Defer(target, connection, message.Type, length, message.Data[3]);
					return Deferred;
				}

				case NetProtocol.Close:
					if (target.Protocol == SocketProtocol.Tcp)
					{
						// The slot stays taken while the FIN exchange runs; FreeClosedSockets reclaims it
						Tcp.Close(target);
						target.Owner = -1;
						target.Pending = null;
						return 0;
					}
					target.Protocol = SocketProtocol.None;
					target.Port = 0;
					return 0;

				default:
					return Error(SyscallError.NotImplemented);
			}
		}

		private static void Defer(Socket socket, Connection connection, ulong type, int length, ulong timeoutMs)
		{
			socket.Pending = connection;
			socket.PendingType = type;
			socket.PendingLength = length;
			socket.PendingDeadline = timeoutMs == ulong.MaxValue ? ulong.MaxValue : Kernel.TickCount + timeoutMs;
		}

		// Bytes waiting on the connection, 0 once the peer has closed, or Closed after a reset
		private static long Read(Socket socket, Connection connection, int length)
		{
			TcpConnection tcp = socket.Tcp;
			if (tcp.RecvCount > 0)
				return tcp.Read(connection.Buffer.Span.Slice(0, length));
			if (tcp.FinReceived)
				return 0;
			return Error(SyscallError.Closed);
		}

		// Connects and reads that were waiting on the network: answered once the connection can, or with Timeout at their deadline
		private static void CompletePending(ulong now)
		{
			foreach (Socket socket in Stack.Sockets)
			{
				if (socket.Pending == null)
					continue;
				TcpConnection tcp = socket.Tcp;
				long result;
				if (socket.PendingType == NetProtocol.StreamConnect)
				{
					if (tcp.State == TcpState.Established)
						result = 0;
					else if (tcp.State == TcpState.Closed)
						result = Error(SyscallError.Closed);
					else if (now >= socket.PendingDeadline)
					{
						Tcp.Abort(socket);
						result = Error(SyscallError.Timeout);
					}
					else
						continue;
				}
				else
				{
					if (tcp.Readable)
						result = Read(socket, socket.Pending, socket.PendingLength);
					else if (now >= socket.PendingDeadline)
						result = Error(SyscallError.Timeout);
					else
						continue;
				}
				Connection connection = socket.Pending;
				socket.Pending = null;
				Reply(connection, socket.PendingType, result);
			}
		}

		// A closed stream socket's slot goes back once its connection has finished closing
		private static void FreeClosedSockets()
		{
			foreach (Socket socket in Stack.Sockets)
			{
				if (socket.Protocol == SocketProtocol.Tcp && socket.Owner < 0 && socket.Tcp.State == TcpState.Closed)
				{
					socket.Protocol = SocketProtocol.None;
					socket.Port = 0;
				}
			}
		}

		private static void Reply(Connection connection, ulong type, long result)
		{
			IpcMessage reply = default;
			reply.Type = type + NetProtocol.Reply;
			reply.Data[0] = (ulong)result;
			Port.Send(connection.Reply, reply);
		}

		// Closes the sockets and connection of an app that has exited, cleanly or by crashing
		private static void ReapDeadConnections()
		{
			for (int i = 0; i < MaxConnections; i++)
			{
				Connection connection = s_connections[i];
				if (connection == null || (connection.Process > 0 && Kernel.Wait(connection.Process, 0) < 0))
					continue;
				foreach (Socket socket in Stack.Sockets)
				{
					if (socket.Owner != i || socket.Protocol == SocketProtocol.None)
						continue;
					if (socket.Protocol == SocketProtocol.Tcp)
						Tcp.Abort(socket);
					socket.Protocol = SocketProtocol.None;
					socket.Pending = null;
				}
				if (connection.Process > 0)
					Kernel.Close(connection.Process);
				Kernel.Close(connection.Reply);
				s_connections[i] = null;
			}
		}

		private static long Error(SyscallError error) => -(long)error;
	}
}
