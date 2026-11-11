using System;
using System.Collections.Generic;
using System.IO;
using Alloy.Sdk.Security.Tls;

namespace Alloy.Sdk.Web
{
	// One open connection to a host: the socket, the plain or TLS stream over it, and the reader that keeps any bytes read ahead
	internal sealed class HttpConnection
	{
		public readonly string Key;
		public readonly long Socket;
		public readonly NetStream Network;
		public readonly Stream Stream;
		public readonly SocketReader Reader;
		public readonly string CipherSuite;
		public ulong LastUsed;
		public bool Reused;

		private HttpConnection(string key, long socket, NetStream network, Stream stream, string cipherSuite)
		{
			Key = key;
			Socket = socket;
			Network = network;
			Stream = stream;
			Reader = new SocketReader(stream, network);
			CipherSuite = cipherSuite;
		}

		public static string KeyFor(Uri url) => $"{url.Scheme}://{url.Host.ToLowerInvariant()}:{url.Port}";

		// Connects, and for https runs the TLS handshake; null with the reason when either fails
		public static HttpConnection Open(Uri url, uint address, ulong timeoutMs, out string error)
		{
			error = null;
			long socket = Net.Socket(Abi.NetProtocol.SocketStream, Abi.NetProtocol.ProtocolTcp);
			if (socket < 0)
			{
				error = "no socket";
				return null;
			}
			long connected = Net.Connect(socket, address, (ushort)url.Port, timeoutMs);
			if (connected < 0)
			{
				Net.Close(socket);
				error = connected == SystemCall.Error(Abi.SyscallError.Timeout) ? $"connect to {url.Host} timed out" : $"connect to {url.Host} refused";
				return null;
			}
			NetStream network = new NetStream(socket, timeoutMs);
			if (url.Scheme != "https")
				return new HttpConnection(KeyFor(url), socket, network, network, null);
			TlsClient tls = new TlsClient(network, url.Host, Tls.Roots, Tls.Now);
			try
			{
				tls.Handshake();
			}
			catch (IOException e)
			{
				Net.Close(socket);
				error = network.Error ?? e.Message;
				return null;
			}
			return new HttpConnection(KeyFor(url), socket, network, tls, tls.CipherSuiteName);
		}

		public void Close()
		{
			try
			{
				Stream.Dispose();
			}
			catch (Exception)
			{
			}
			Net.Close(Socket);
		}
	}

	// Idle keep-alive connections by host, so a page's stylesheets and images reuse one TCP connection and one TLS handshake per host
	internal static class ConnectionPool
	{
		private const int MaxIdlePerHost = 4;
		private const ulong IdleMs = 20000;
		private static readonly Dictionary<string, List<HttpConnection>> s_idle = new Dictionary<string, List<HttpConnection>>();

		public static HttpConnection Take(string key)
		{
			lock (s_idle)
			{
				if (!s_idle.TryGetValue(key, out List<HttpConnection> list))
					return null;
				ulong now = Kernel.TickCount;
				while (list.Count > 0)
				{
					HttpConnection connection = list[list.Count - 1];
					list.RemoveAt(list.Count - 1);
					if (now - connection.LastUsed > IdleMs)
					{
						connection.Close();
						continue;
					}
					connection.Reused = true;
					return connection;
				}
				return null;
			}
		}

		public static void Return(HttpConnection connection)
		{
			connection.LastUsed = Kernel.TickCount;
			lock (s_idle)
			{
				if (!s_idle.TryGetValue(connection.Key, out List<HttpConnection> list))
					s_idle[connection.Key] = list = new List<HttpConnection>();
				if (list.Count >= MaxIdlePerHost)
				{
					connection.Close();
					return;
				}
				list.Add(connection);
			}
		}

		// Closes everything idle, for a browser going away or a network that changed
		public static void Clear()
		{
			lock (s_idle)
			{
				foreach (List<HttpConnection> list in s_idle.Values)
				{
					foreach (HttpConnection connection in list)
						connection.Close();
				}
				s_idle.Clear();
			}
		}
	}
}
