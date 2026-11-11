using System;
using System.Collections.Generic;
using System.IO;
using System.IO.Compression;
using System.Text;
using Alloy.Sdk.Security.Crypto;
using Alloy.Sdk.Security.Tls;

namespace Alloy.Sdk.Web
{
	// What came back from a request: the status line, headers (names compared without case) and the decoded body
	public sealed class HttpResponse
	{
		public Uri Url;
		public int Status;
		public string Reason = "";
		public string Error;
		public readonly Dictionary<string, string> Headers = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
		public byte[] Body = Array.Empty<byte>();
		public long ElapsedMs;

		// The TLS cipher suite the connection used, or null over plain http
		public string CipherSuite;

		public bool Ok => Status >= 200 && Status < 300;

		public string Header(string name) => Headers.TryGetValue(name, out string value) ? value : null;

		public string ContentType => Header("Content-Type") ?? "";

		// The charset from the content type, or UTF-8
		public string BodyText()
		{
			string type = ContentType;
			int at = type.IndexOf("charset=", StringComparison.OrdinalIgnoreCase);
			if (at >= 0)
			{
				string charset = type.Substring(at + 8).Trim().Trim('"');
				int end = charset.IndexOf(';');
				if (end >= 0)
					charset = charset.Substring(0, end).Trim();
				if (charset.Equals("iso-8859-1", StringComparison.OrdinalIgnoreCase) || charset.Equals("latin1", StringComparison.OrdinalIgnoreCase) || charset.Equals("windows-1252", StringComparison.OrdinalIgnoreCase))
					return Encoding.Latin1.GetString(Body);
				if (charset.Equals("us-ascii", StringComparison.OrdinalIgnoreCase))
					return Encoding.ASCII.GetString(Body);
			}
			return Encoding.UTF8.GetString(Body);
		}
	}

	// An HTTP/1.1 client over NetSvr's stream sockets: one connection per request, chunked and sized bodies, gzip and deflate, redirects
	public static class Http
	{
		public const string UserAgent = "Alloy/0.1";
		private const int MaxHeaderBytes = 64 * 1024;
		private const int MaxRedirects = 5;
		private const long MaxBody = 64L * 1024 * 1024;

		public static HttpResponse Get(string url, ulong timeoutMs = 15000) => Request("GET", url, null, null, timeoutMs);

		public static HttpResponse Get(Uri url, ulong timeoutMs = 15000) => Request("GET", url, null, null, timeoutMs);

		public static HttpResponse Request(string method, string url, byte[] body, string contentType, ulong timeoutMs = 15000)
		{
			if (!Uri.TryCreate(url, UriKind.Absolute, out Uri uri))
				return Failed(null, "not an absolute URL");
			return Request(method, uri, body, contentType, timeoutMs);
		}

		// Follows redirects between http hosts; a redirect to another scheme comes back as the 3xx itself so the caller can decide
		public static HttpResponse Request(string method, Uri url, byte[] body, string contentType, ulong timeoutMs = 15000)
		{
			HttpResponse response = null;
			for (int hop = 0; hop <= MaxRedirects; hop++)
			{
				response = Exchange(method, url, body, contentType, timeoutMs);
				if (response.Status != 301 && response.Status != 302 && response.Status != 303 && response.Status != 307 && response.Status != 308)
					return response;
				string location = response.Header("Location");
				if (location == null || !Uri.TryCreate(url, location, out Uri next) || (next.Scheme != "http" && next.Scheme != "https"))
					return response;
				if (response.Status == 303 || ((response.Status == 301 || response.Status == 302) && method == "POST"))
				{
					method = "GET";
					body = null;
				}
				url = next;
			}
			return response;
		}

		private static HttpResponse Exchange(string method, Uri url, byte[] body, string contentType, ulong timeoutMs)
		{
			HttpResponse response = new HttpResponse { Url = url };
			ulong started = Kernel.TickCount;
			if (url.Scheme != "http" && url.Scheme != "https")
				return Failed(response, $"{url.Scheme} is not supported, only http and https");
			if (!Dns.TryResolve(url.Host, out uint address))
				return Failed(response, $"can't resolve {url.Host}");

			long socket = Alloy.Sdk.Net.Socket(Abi.NetProtocol.SocketStream, Abi.NetProtocol.ProtocolTcp);
			if (socket < 0)
				return Failed(response, "no socket");
			try
			{
				long connected = Alloy.Sdk.Net.Connect(socket, address, (ushort)url.Port, timeoutMs);
				if (connected < 0)
					return Failed(response, connected == SystemCall.Error(Abi.SyscallError.Timeout) ? $"connect to {url.Host} timed out" : $"connect to {url.Host} refused");

				NetStream network = new NetStream(socket, timeoutMs);
				Stream stream = network;
				if (url.Scheme == "https")
				{
					TlsClient tls = new TlsClient(network, url.Host, Tls.Roots, Tls.Now);
					try
					{
						tls.Handshake();
					}
					catch (TlsException e)
					{
						return Failed(response, network.Error != null ? network.Error : e.Message);
					}
					catch (IOException e)
					{
						return Failed(response, e.Message);
					}
					response.CipherSuite = tls.CipherSuiteName;
					stream = tls;
				}
				byte[] request = BuildRequest(method, url, body, contentType);
				try
				{
					stream.Write(request);
				}
				catch (IOException e)
				{
					return Failed(response, e.Message);
				}

				SocketReader reader = new SocketReader(stream, network);
				if (!ReadHead(reader, response))
					return response;
				byte[] raw = ReadBody(reader, response, method == "HEAD");
				if (raw == null)
					return response;
				response.Body = Decode(raw, response.Header("Content-Encoding"), response);
			}
			finally
			{
				Alloy.Sdk.Net.Close(socket);
				response.ElapsedMs = (long)(Kernel.TickCount - started);
			}
			return response;
		}

		private static byte[] BuildRequest(string method, Uri url, byte[] body, string contentType)
		{
			StringBuilder head = new StringBuilder();
			head.Append(method).Append(' ').Append(url.PathAndQuery.Length == 0 ? "/" : url.PathAndQuery).Append(" HTTP/1.1\r\n");
			head.Append("Host: ").Append(url.IsDefaultPort ? url.Host : $"{url.Host}:{url.Port}").Append("\r\n");
			head.Append("User-Agent: ").Append(UserAgent).Append("\r\n");
			head.Append("Accept: text/html,application/xhtml+xml,image/*,*/*;q=0.8\r\n");
			head.Append("Accept-Encoding: gzip, deflate\r\n");
			head.Append("Connection: close\r\n");
			if (body != null)
			{
				head.Append("Content-Type: ").Append(contentType ?? "application/octet-stream").Append("\r\n");
				head.Append("Content-Length: ").Append(body.Length).Append("\r\n");
			}
			head.Append("\r\n");
			byte[] headBytes = Encoding.ASCII.GetBytes(head.ToString());
			if (body == null || body.Length == 0)
				return headBytes;
			byte[] request = new byte[headBytes.Length + body.Length];
			headBytes.CopyTo(request, 0);
			body.CopyTo(request, headBytes.Length);
			return request;
		}

		// The status line and headers, up to the blank line; folded header lines and repeated names are joined with commas
		private static bool ReadHead(SocketReader reader, HttpResponse response)
		{
			string statusLine = reader.ReadLine(MaxHeaderBytes);
			if (statusLine == null)
			{
				Failed(response, reader.Error ?? "no response");
				return false;
			}
			if (!statusLine.StartsWith("HTTP/1.", StringComparison.Ordinal) || statusLine.Length < 12 || !int.TryParse(statusLine.AsSpan(9, 3), out int status))
			{
				Failed(response, $"not an HTTP response: {Truncate(statusLine, 40)}");
				return false;
			}
			response.Status = status;
			response.Reason = statusLine.Length > 13 ? statusLine.Substring(13).Trim() : "";

			string lastName = null;
			while (true)
			{
				string line = reader.ReadLine(MaxHeaderBytes);
				if (line == null)
				{
					Failed(response, reader.Error ?? "the headers were cut short");
					return false;
				}
				if (line.Length == 0)
					return true;
				if ((line[0] == ' ' || line[0] == '\t') && lastName != null)
				{
					response.Headers[lastName] += " " + line.Trim();
					continue;
				}
				int colon = line.IndexOf(':');
				if (colon <= 0)
					continue;
				string name = line.Substring(0, colon).Trim();
				string value = line.Substring(colon + 1).Trim();
				response.Headers[name] = response.Headers.TryGetValue(name, out string existing) ? existing + ", " + value : value;
				lastName = name;
			}
		}

		// Chunked, sized, or everything until the server closes, in that order of preference
		private static byte[] ReadBody(SocketReader reader, HttpResponse response, bool headOnly)
		{
			if (headOnly || response.Status == 204 || response.Status == 304 || (response.Status >= 100 && response.Status < 200))
				return Array.Empty<byte>();
			string transfer = response.Header("Transfer-Encoding");
			if (transfer != null && transfer.IndexOf("chunked", StringComparison.OrdinalIgnoreCase) >= 0)
				return ReadChunked(reader, response);
			string lengthHeader = response.Header("Content-Length");
			if (lengthHeader != null && long.TryParse(lengthHeader.Split(',')[0].Trim(), out long length))
			{
				if (length < 0 || length > MaxBody)
				{
					Failed(response, $"a body of {length} bytes is too large");
					return null;
				}
				byte[] body = new byte[length];
				int got = reader.ReadFully(body);
				if (got < length)
				{
					Failed(response, $"the body stopped after {got} of {length} bytes");
					return null;
				}
				return body;
			}
			using MemoryStream all = new MemoryStream();
			byte[] buffer = new byte[16384];
			while (all.Length < MaxBody)
			{
				int got = reader.Read(buffer);
				if (got <= 0)
					break;
				all.Write(buffer, 0, got);
			}
			return all.ToArray();
		}

		private static byte[] ReadChunked(SocketReader reader, HttpResponse response)
		{
			using MemoryStream all = new MemoryStream();
			while (true)
			{
				string sizeLine = reader.ReadLine(1024);
				if (sizeLine == null)
				{
					Failed(response, reader.Error ?? "a chunk size was cut short");
					return null;
				}
				int semicolon = sizeLine.IndexOf(';');
				if (semicolon >= 0)
					sizeLine = sizeLine.Substring(0, semicolon);
				if (!long.TryParse(sizeLine.Trim(), System.Globalization.NumberStyles.HexNumber, null, out long size) || size < 0 || all.Length + size > MaxBody)
				{
					Failed(response, $"a bad chunk size: {Truncate(sizeLine, 20)}");
					return null;
				}
				if (size == 0)
					break;
				byte[] chunk = new byte[size];
				if (reader.ReadFully(chunk) < size)
				{
					Failed(response, "a chunk was cut short");
					return null;
				}
				all.Write(chunk, 0, chunk.Length);
				if (reader.ReadLine(16) == null)
				{
					Failed(response, "a chunk's end was cut short");
					return null;
				}
			}

			// Trailers, if any, end with a blank line
			while (true)
			{
				string trailer = reader.ReadLine(MaxHeaderBytes);
				if (string.IsNullOrEmpty(trailer))
					break;
			}
			return all.ToArray();
		}

		private static byte[] Decode(byte[] raw, string encoding, HttpResponse response)
		{
			if (encoding == null || raw.Length == 0)
				return raw;
			bool gzip = encoding.IndexOf("gzip", StringComparison.OrdinalIgnoreCase) >= 0;
			bool deflate = !gzip && encoding.IndexOf("deflate", StringComparison.OrdinalIgnoreCase) >= 0;
			if (!gzip && !deflate)
				return raw;
			try
			{
				using MemoryStream packed = new MemoryStream(raw);
				using MemoryStream unpacked = new MemoryStream();
				if (gzip)
				{
					using GZipStream stream = new GZipStream(packed, CompressionMode.Decompress);
					stream.CopyTo(unpacked);
				}
				else
				{
					// Some servers send a zlib stream for deflate; its two-byte header gives it away
					bool zlib = raw.Length > 1 && (raw[0] & 0x0F) == 8 && ((raw[0] << 8) | raw[1]) % 31 == 0;
					using Stream stream = zlib ? new ZLibStream(packed, CompressionMode.Decompress) : new DeflateStream(packed, CompressionMode.Decompress);
					stream.CopyTo(unpacked);
				}
				response.Headers.Remove("Content-Encoding");
				return unpacked.ToArray();
			}
			catch (Exception e)
			{
				Failed(response, $"the {encoding} body didn't decode: {e.Message}");
				return raw;
			}
		}

		private static HttpResponse Failed(HttpResponse response, string error)
		{
			response ??= new HttpResponse();
			response.Error = error;
			return response;
		}

		private static string Truncate(string text, int length) => text.Length <= length ? text : text.Substring(0, length) + "...";
	}

	// What the HTTP client needs for https: the trust store from the image's certificate bundle, loaded once, and the clock
	public static class Tls
	{
		public const string BundlePath = "/certs/ca-bundle.crt";
		private static TrustStore s_roots;

		public static TrustStore Roots
		{
			get
			{
				if (s_roots == null)
				{
					byte[] pem = File.ReadAllBytes(BundlePath);
					s_roots = pem == null ? new TrustStore() : TrustStore.FromPem(Encoding.ASCII.GetString(pem));
				}
				return s_roots;
			}
		}

		// The kernel's clock; before it is set the bundle's own era is assumed so validity checks do not all fail
		public static DateTime Now
		{
			get
			{
				Abi.SystemTime time = Kernel.Time;
				if (time.Year < 2000)
					return new DateTime(2026, 11, 11, 0, 0, 0, DateTimeKind.Utc);
				return new DateTime(time.Year, Math.Clamp((int)time.Month, 1, 12), Math.Clamp((int)time.Day, 1, 28), Math.Clamp((int)time.Hour, 0, 23), Math.Clamp((int)time.Minute, 0, 59), Math.Clamp((int)time.Second, 0, 59), DateTimeKind.Utc);
			}
		}
	}

	// Buffered reads over a connection stream (plain or TLS); lines end at CRLF (a bare LF is accepted)
	internal sealed class SocketReader
	{
		private readonly Stream _stream;
		private readonly NetStream _network;
		private readonly byte[] _buffer = new byte[16384];
		private int _start;
		private int _end;
		private bool _closed;

		public string Error { get; private set; }

		public SocketReader(Stream stream, NetStream network)
		{
			_stream = stream;
			_network = network;
		}

		private bool Fill()
		{
			if (_closed)
				return false;
			if (_start == _end)
				_start = _end = 0;
			if (_end == _buffer.Length)
			{
				Array.Copy(_buffer, _start, _buffer, 0, _end - _start);
				_end -= _start;
				_start = 0;
			}
			int got;
			try
			{
				got = _stream.Read(_buffer, _end, _buffer.Length - _end);
			}
			catch (IOException e)
			{
				_closed = true;
				Error = _network.Error ?? e.Message;
				return false;
			}
			if (got <= 0)
			{
				_closed = true;
				Error = _network.Error;
				return false;
			}
			_end += got;
			return true;
		}

		public string ReadLine(int maxLength)
		{
			int scanned = 0;
			while (true)
			{
				for (int i = _start + scanned; i < _end; i++)
				{
					if (_buffer[i] != (byte)'\n')
						continue;
					int length = i - _start;
					if (length > 0 && _buffer[i - 1] == (byte)'\r')
						length--;
					string line = Encoding.Latin1.GetString(_buffer, _start, length);
					_start = i + 1;
					return line;
				}
				scanned = _end - _start;
				if (scanned > maxLength)
				{
					Error = "a line was too long";
					return null;
				}
				if (!Fill())
					return null;
			}
		}

		public int Read(Span<byte> destination)
		{
			if (_start == _end && !Fill())
				return 0;
			int count = Math.Min(destination.Length, _end - _start);
			_buffer.AsSpan(_start, count).CopyTo(destination);
			_start += count;
			return count;
		}

		public int ReadFully(Span<byte> destination)
		{
			int total = 0;
			while (total < destination.Length)
			{
				int got = Read(destination.Slice(total));
				if (got <= 0)
					break;
				total += got;
			}
			return total;
		}
	}
}
