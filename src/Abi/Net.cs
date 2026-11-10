namespace Alloy.Abi
{
	// Kernel <-> NetSvr raw-frame device (MetalOS NetDevice): frames come up a shared ring, NetSend sends one
	public struct NetFrame
	{
		public const int Mtu = 1514;
		public const string RxEndpoint = "net.rx";
		public const string RxReadyEndpoint = "net.rx.ready";
		public const uint RxCapacity = 256;
		public const int RecordSize = 1520;
		public const int HeaderSize = 64;
		public const ulong RingSize = HeaderSize + RxCapacity * RecordSize;

		public ushort Interface;
		public ushort Length;
		public FrameBytes Data;

		// The ring's header holds the free-running write and read counters, like the input ring
		public static unsafe uint* Counters(byte* ring) => (uint*)ring;

		public static unsafe NetFrame* At(byte* ring, uint counter) => (NetFrame*)(ring + HeaderSize + counter % RxCapacity * RecordSize);
	}

	// One frame's bytes, up to the MTU
	[System.Runtime.CompilerServices.InlineArray(NetFrame.Mtu)]
	public struct FrameBytes
	{
		private byte _element0;
	}

	// Filled by GetInterfaces; the MAC is in wire order
	public struct InterfaceInfo
	{
		public uint Index;
		public uint Present;
		public MacBytes Mac;
		public ushort Reserved;
	}

	[System.Runtime.CompilerServices.InlineArray(6)]
	public struct MacBytes
	{
		private byte _element0;
	}

	// The app <-> NetSvr socket protocol (MetalOS NetIpc) over ports; datagram payloads travel in a shared page the app hands over at connect
	public static class NetProtocol
	{
		public const string Endpoint = "net";
		public const string AddressEndpoint = "net.addr";
		public const int MaxPayload = 1500;
		public const ulong BufferSize = 4096;

		// Requests; Data[6] carries the client id the server assigned on Connect
		public const ulong Connect = 1;
		public const ulong Attach = 2;
		public const ulong Create = 3;
		public const ulong Bind = 4;
		public const ulong SendTo = 5;
		public const ulong RecvFrom = 6;
		public const ulong Close = 7;
		public const ulong GetInterfaceIp = 8;
		public const ulong SetInterfaceIp = 9;
		public const ulong GetGateway = 10;
		public const ulong SetGateway = 11;

		// Every reply has Type = the request's + Reply; Data[0] is the result, >= 0 or a negated SyscallError
		public const ulong Reply = 100;

		public const ulong SocketDatagram = 2;
		public const ulong SocketRaw = 3;
		public const ulong ProtocolIcmp = 1;
		public const ulong ProtocolUdp = 17;
	}
}
