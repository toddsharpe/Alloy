using System;
using System.Buffers.Binary;
using System.Collections.Generic;
using Alloy.Abi;

namespace Alloy.Sdk
{
	// One resource record from an answer section: an address for A, a target name for CNAME
	public struct DnsAnswer
	{
		public const ushort TypeA = 1;
		public const ushort TypeCname = 5;

		public ushort Type;
		public uint Ttl;
		public uint Address;
		public string Target;
	}

	// A stub resolver (RFC 1035) over UDP to the server DHCP named, with a per-process cache that honours TTLs
	public static class Dns
	{
		private const ushort Port = 53;
		private const int MaxMessage = 512;
		private const int MaxName = 253;
		private const ulong QueryTimeoutMs = 2000;
		private const int Attempts = 3;
		private const uint MinCacheSeconds = 10;
		private const uint MaxCacheSeconds = 3600;
		private const uint FallbackServer = 0x08080808;

		private static readonly Dictionary<string, (uint Address, ulong Expires)> s_cache = new Dictionary<string, (uint, ulong)>();
		private static readonly object s_lock = new object();
		private static ushort s_nextId = 0x4A11;

		// The server to ask: the one DHCP gave, else the gateway, else a public resolver
		public static uint Server
		{
			get
			{
				uint server = Net.DnsServer;
				if (server != 0)
					return server;
				uint gateway = Net.Gateway;
				return gateway != 0 ? gateway : FallbackServer;
			}
		}

		// A host's IPv4 address, from a dotted quad, the cache, or a query; false when none of those gives one
		public static bool TryResolve(string host, out uint address)
		{
			if (Net.TryParseIp(host, out address))
				return true;
			address = 0;
			if (string.IsNullOrEmpty(host) || host.Length > MaxName)
				return false;
			string key = host.ToLowerInvariant();
			ulong now = Kernel.TickCount;
			lock (s_lock)
			{
				if (s_cache.TryGetValue(key, out (uint Address, ulong Expires) cached) && cached.Expires > now)
				{
					address = cached.Address;
					return true;
				}
			}

			DnsAnswer[] answers = new DnsAnswer[8];
			int count = Query(host, answers);
			for (int i = 0; i < count; i++)
			{
				if (answers[i].Type != DnsAnswer.TypeA)
					continue;
				address = answers[i].Address;
				uint ttl = Math.Max(MinCacheSeconds, Math.Min(answers[i].Ttl, MaxCacheSeconds));
				lock (s_lock)
					s_cache[key] = (address, now + 1000UL * ttl);
				return true;
			}
			return false;
		}

		// Asks for the host's A records and returns every answer the server sent, CNAMEs included; 0 when nothing came back
		public static int Query(string host, Span<DnsAnswer> answers)
		{
			long socket = Net.Socket(NetProtocol.SocketDatagram, NetProtocol.ProtocolUdp);
			if (socket < 0)
				return 0;
			Span<byte> message = stackalloc byte[MaxMessage];
			Span<byte> reply = stackalloc byte[MaxMessage];
			int count = 0;
			uint server = Server;
			for (int attempt = 0; attempt < Attempts && count == 0; attempt++)
			{
				ushort id = s_nextId++;
				int length = BuildQuery(message, id, host);
				if (length < 0)
					break;
				if (Net.SendTo(socket, message.Slice(0, length), server, Port) < 0)
					break;
				ulong deadline = Kernel.TickCount + QueryTimeoutMs;
				while (count == 0)
				{
					ulong now = Kernel.TickCount;
					if (now >= deadline)
						break;
					long got = Net.ReceiveFrom(socket, reply, out _, out ushort fromPort, deadline - now);
					if (got < 12 || fromPort != Port || BinaryPrimitives.ReadUInt16BigEndian(reply) != id)
						continue;
					count = ParseAnswers(reply.Slice(0, (int)got), answers);
					break;
				}

				// A server that never answers gives way to the public one for the last try
				if (count == 0 && attempt == Attempts - 2)
					server = FallbackServer;
			}
			Net.Close(socket);
			return count;
		}

		// A standard query with recursion desired: the header, then the name as length-prefixed labels, type A, class IN
		private static int BuildQuery(Span<byte> message, ushort id, string host)
		{
			message.Clear();
			BinaryPrimitives.WriteUInt16BigEndian(message, id);
			BinaryPrimitives.WriteUInt16BigEndian(message.Slice(2), 0x0100);
			BinaryPrimitives.WriteUInt16BigEndian(message.Slice(4), 1);
			int at = 12;
			int labelStart = at++;
			for (int i = 0; i <= host.Length; i++)
			{
				if (i == host.Length || host[i] == '.')
				{
					int labelLength = at - labelStart - 1;
					if (labelLength == 0 || labelLength > 63)
						return -1;
					message[labelStart] = (byte)labelLength;
					labelStart = at++;
					continue;
				}
				if (at >= MaxMessage - 8)
					return -1;
				message[at++] = (byte)host[i];
			}
			message[labelStart] = 0;
			BinaryPrimitives.WriteUInt16BigEndian(message.Slice(at), DnsAnswer.TypeA);
			BinaryPrimitives.WriteUInt16BigEndian(message.Slice(at + 2), 1);
			return at + 4;
		}

		// The answer section: each record's name may point back into the message (RFC 1035 4.1.4)
		private static int ParseAnswers(ReadOnlySpan<byte> reply, Span<DnsAnswer> answers)
		{
			ushort flags = BinaryPrimitives.ReadUInt16BigEndian(reply.Slice(2));
			if ((flags & 0x8000) == 0 || (flags & 0x000F) != 0)
				return 0;
			int questions = BinaryPrimitives.ReadUInt16BigEndian(reply.Slice(4));
			int records = BinaryPrimitives.ReadUInt16BigEndian(reply.Slice(6));
			int at = 12;
			for (int i = 0; i < questions; i++)
			{
				at = SkipName(reply, at);
				if (at < 0 || at + 4 > reply.Length)
					return 0;
				at += 4;
			}

			int count = 0;
			for (int i = 0; i < records && count < answers.Length; i++)
			{
				at = SkipName(reply, at);
				if (at < 0 || at + 10 > reply.Length)
					break;
				ushort type = BinaryPrimitives.ReadUInt16BigEndian(reply.Slice(at));
				uint ttl = BinaryPrimitives.ReadUInt32BigEndian(reply.Slice(at + 4));
				int dataLength = BinaryPrimitives.ReadUInt16BigEndian(reply.Slice(at + 8));
				int data = at + 10;
				if (data + dataLength > reply.Length)
					break;
				if (type == DnsAnswer.TypeA && dataLength == 4)
					answers[count++] = new DnsAnswer { Type = type, Ttl = ttl, Address = BinaryPrimitives.ReadUInt32BigEndian(reply.Slice(data)) };
				else if (type == DnsAnswer.TypeCname)
					answers[count++] = new DnsAnswer { Type = type, Ttl = ttl, Target = ReadName(reply, data) };
				at = data + dataLength;
			}
			return count;
		}

		// The offset after a name, whether it is labels, a pointer, or labels ending in a pointer; -1 if malformed
		private static int SkipName(ReadOnlySpan<byte> reply, int at)
		{
			while (at < reply.Length)
			{
				int length = reply[at];
				if (length == 0)
					return at + 1;
				if ((length & 0xC0) == 0xC0)
					return at + 2 <= reply.Length ? at + 2 : -1;
				at += 1 + length;
			}
			return -1;
		}

		// A name as dotted text, following at most a few pointers so a loop can't hang the resolver
		private static string ReadName(ReadOnlySpan<byte> reply, int at)
		{
			Span<char> name = stackalloc char[MaxName + 1];
			int length = 0;
			for (int hops = 0; hops < 16 && at < reply.Length; hops++)
			{
				int labelLength = reply[at];
				if (labelLength == 0)
					break;
				if ((labelLength & 0xC0) == 0xC0)
				{
					if (at + 1 >= reply.Length)
						break;
					at = ((labelLength & 0x3F) << 8) | reply[at + 1];
					continue;
				}
				if (at + 1 + labelLength > reply.Length || length + labelLength + 1 > name.Length)
					break;
				if (length > 0)
					name[length++] = '.';
				for (int i = 0; i < labelLength; i++)
					name[length++] = (char)reply[at + 1 + i];
				at += 1 + labelLength;
			}
			return new string(name.Slice(0, length));
		}
	}
}
