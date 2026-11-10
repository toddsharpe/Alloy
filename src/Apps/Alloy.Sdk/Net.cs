using System;
using Alloy.Abi;

namespace Alloy.Sdk
{
	// Sockets and interface configuration through NetSvr (MetalOS-NET.dll); calls are synchronous request and reply, addresses host order
	public static unsafe class Net
	{
		private static long s_server;
		private static long s_reply;
		private static ulong s_client;
		private static byte* s_buffer;

		// Interface enumeration is a kernel syscall; the address configuration belongs to NetSvr
		public static int GetInterfaces(Span<InterfaceInfo> interfaces)
		{
			fixed (InterfaceInfo* pointer = interfaces)
				return (int)SystemCall.Invoke(Syscall.GetInterfaces, (ulong)pointer, (ulong)interfaces.Length);
		}

		public static long Socket(ulong type, ulong protocol) => Call(NetProtocol.Create, type, protocol, out _);

		public static long Bind(long socket, ushort port) => Call(NetProtocol.Bind, (ulong)socket, port, out _);

		public static long SendTo(long socket, ReadOnlySpan<byte> data, uint ip, ushort port)
		{
			if (!Connect() || data.Length > NetProtocol.MaxPayload)
				return SystemCall.Error(SyscallError.InvalidArgument);
			data.CopyTo(new Span<byte>(s_buffer, NetProtocol.MaxPayload));
			return Call(NetProtocol.SendTo, (ulong)socket, ip, out _, port, (ulong)data.Length);
		}

		// The next datagram's length, retrying every few milliseconds until one arrives or the timeout passes
		public static long ReceiveFrom(long socket, Span<byte> buffer, out uint ip, out ushort port, ulong timeoutMs)
		{
			ulong start = Kernel.TickCount;
			while (true)
			{
				long result = Call(NetProtocol.RecvFrom, (ulong)socket, (ulong)buffer.Length, out IpcMessage reply);
				if (result >= 0)
				{
					new ReadOnlySpan<byte>(s_buffer, (int)result).CopyTo(buffer);
					ip = (uint)reply.Data[1];
					port = (ushort)reply.Data[2];
					return result;
				}
				if (result != SystemCall.Error(SyscallError.Timeout) || Kernel.TickCount - start >= timeoutMs)
				{
					ip = 0;
					port = 0;
					return result;
				}
				Kernel.Sleep(5);
			}
		}

		public static long Close(long socket) => Call(NetProtocol.Close, (ulong)socket, 0, out _);

		public static bool GetInterfaceIp(out uint ip, out uint mask)
		{
			bool got = Call(NetProtocol.GetInterfaceIp, 0, 0, out IpcMessage reply) >= 0;
			ip = got ? (uint)reply.Data[1] : 0;
			mask = got ? (uint)reply.Data[2] : 0;
			return got;
		}

		public static bool SetInterfaceIp(uint ip, uint mask) => Call(NetProtocol.SetInterfaceIp, 0, ip, out _, mask) >= 0;

		public static uint Gateway => Call(NetProtocol.GetGateway, 0, 0, out IpcMessage reply) >= 0 ? (uint)reply.Data[1] : 0;

		public static bool SetGateway(uint gateway) => Call(NetProtocol.SetGateway, 0, gateway, out _) >= 0;

		// The DHCP gateway, or a network|.1 guess when there is none (MetalOS NetUtil GatewayOrGuess)
		public static uint GatewayOrGuess(uint ip, uint mask)
		{
			uint gateway = Gateway;
			return gateway != 0 ? gateway : (ip & mask) | 1;
		}

		public static string FormatIp(uint ip) => $"{ip >> 24}.{(ip >> 16) & 0xFF}.{(ip >> 8) & 0xFF}.{ip & 0xFF}";

		public static string FormatMac(ReadOnlySpan<byte> mac) => $"{mac[0]:X2}:{mac[1]:X2}:{mac[2]:X2}:{mac[3]:X2}:{mac[4]:X2}:{mac[5]:X2}";

		// Dotted quad to host order; false if it isn't one
		public static bool TryParseIp(string text, out uint ip)
		{
			ip = 0;
			uint octet = 0;
			int octets = 0;
			bool digits = false;
			for (int i = 0; i <= text.Length; i++)
			{
				char c = i < text.Length ? text[i] : '.';
				if (c >= '0' && c <= '9')
				{
					octet = octet * 10 + (uint)(c - '0');
					digits = true;
					continue;
				}
				if (c != '.' || !digits || octet > 255 || octets == 4)
					return false;
				ip = ip << 8 | octet;
				octets++;
				octet = 0;
				digits = false;
			}
			return octets == 4;
		}

		private static long Call(ulong type, ulong a0, ulong a1, out IpcMessage reply, ulong a2 = 0, ulong a3 = 0)
		{
			reply = default;
			if (!Connect())
				return SystemCall.Error(SyscallError.NotFound);
			IpcMessage message = default;
			message.Type = type;
			message.Data[0] = a0;
			message.Data[1] = a1;
			message.Data[2] = a2;
			message.Data[3] = a3;
			message.Data[6] = s_client;
			Port.Send(s_server, message);
			while (Port.Receive(s_reply, out reply) >= 0)
			{
				if (reply.Type == type + NetProtocol.Reply)
					return (long)reply.Data[0];
			}
			return SystemCall.Error(SyscallError.InvalidHandle);
		}

		// Once per process: the reply port with Connect, then the shared page datagrams travel in
		private static bool Connect()
		{
			if (s_buffer != null)
				return true;
			s_server = Endpoint.WaitFor(NetProtocol.Endpoint, 2000);
			if (s_server <= 0)
				return false;
			s_reply = Port.Create();
			IpcMessage message = default;
			message.Type = NetProtocol.Connect;
			message.Data[0] = (ulong)Kernel.ProcessId;
			Port.Send(s_server, message, s_reply);
			if (Port.Receive(s_reply, out IpcMessage reply) < 0 || (long)reply.Data[0] < 0)
				return false;
			s_client = reply.Data[0];

			long section = SharedMemory.Create(NetProtocol.BufferSize);
			byte* buffer = (byte*)SharedMemory.MapAddress(section);
			message = default;
			message.Type = NetProtocol.Attach;
			message.Data[6] = s_client;
			Port.Send(s_server, message, section);
			Kernel.Close(section);
			if (Port.Receive(s_reply, out reply) < 0 || (long)reply.Data[0] < 0)
				return false;
			s_buffer = buffer;
			return true;
		}
	}
}
