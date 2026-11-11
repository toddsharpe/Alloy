using System;
using System.Buffers.Binary;
using System.Collections.Generic;
using System.IO;
using RandomNumberGenerator = System.Security.Cryptography.RandomNumberGenerator;
using System.Text;
using Alloy.Sdk.Security.Crypto;

namespace Alloy.Sdk.Security.Tls
{
	public sealed class TlsException : IOException
	{
		public TlsException(string message) : base(message)
		{
		}
	}

	// A TLS 1.3 client (RFC 8446) as a stream over another stream: X25519 key exchange, AES-128-GCM or ChaCha20-Poly1305, the server's chain checked against a trust store
	public sealed class TlsClient : Stream
	{
		private const ushort Tls12 = 0x0303;
		private const ushort Tls13 = 0x0304;
		private const ushort AesGcmSha256 = 0x1301;
		private const ushort ChaChaSha256 = 0x1303;
		private const int MaxPlaintext = 16384;

		private readonly Stream _inner;
		private readonly string _host;
		private readonly TrustStore _roots;
		private readonly DateTime _now;
		private readonly HashFunction _hash = HashFunction.Sha256;
		private readonly MemoryStream _transcript = new MemoryStream();
		private readonly byte[] _recordHeader = new byte[5];
		private byte[] _readBuffer = Array.Empty<byte>();
		private int _readStart;
		private int _readEnd;
		private byte[] _handshakeBuffer = Array.Empty<byte>();
		private int _handshakeLength;

		private IAead _readAead;
		private byte[] _readIv;
		private ulong _readSequence;
		private IAead _writeAead;
		private byte[] _writeIv;
		private ulong _writeSequence;
		private ushort _cipherSuite;
		private byte[] _clientHandshakeTraffic;
		private byte[] _serverHandshakeTraffic;
		private byte[] _masterSecret;
		private byte[] _clientApplicationTraffic;
		private byte[] _serverApplicationTraffic;
		private bool _closed;
		private bool _peerClosed;

		public readonly List<Certificate> PeerChain = new List<Certificate>();
		public string CipherSuiteName => _cipherSuite == ChaChaSha256 ? "TLS_CHACHA20_POLY1305_SHA256" : "TLS_AES_128_GCM_SHA256";
		public string Alpn { get; private set; }

		// Validation can be skipped only for tests against hosts whose certificates are not in the store
		public bool SkipVerification;

		public TlsClient(Stream inner, string host, TrustStore roots, DateTime now)
		{
			_inner = inner;
			_host = host;
			_roots = roots;
			_now = now;
		}

		public override bool CanRead => true;

		public override bool CanSeek => false;

		public override bool CanWrite => true;

		public override long Length => throw new NotSupportedException();

		public override long Position { get => throw new NotSupportedException(); set => throw new NotSupportedException(); }

		public override void Flush() => _inner.Flush();

		public override long Seek(long offset, SeekOrigin origin) => throw new NotSupportedException();

		public override void SetLength(long value) => throw new NotSupportedException();

		// Runs the handshake; throws a TlsException with a readable reason when the server or its certificate is not acceptable
		public void Handshake()
		{
			byte[] privateKey = new byte[32];
			RandomNumberGenerator.Fill(privateKey);
			byte[] publicKey = X25519.PublicKey(privateKey);
			byte[] clientHello = BuildClientHello(publicKey, out byte[] clientRandom);
			SendHandshake(clientHello, plaintext: true);

			byte[] serverHello = ReadHandshakeMessage(out byte type);
			if (type != 2)
				throw new TlsException($"expected ServerHello, got handshake type {type}");
			ParseServerHello(serverHello, out byte[] serverShare, out bool retry);
			if (retry)
				throw new TlsException("the server asked for a key share we do not offer");
			byte[] shared = X25519.SharedSecret(privateKey, serverShare);
			DeriveHandshakeSecrets(shared);

			// Encrypted flight: EncryptedExtensions, Certificate, CertificateVerify, Finished
			bool finished = false;
			while (!finished)
			{
				byte[] message = ReadHandshakeMessage(out type);
				switch (type)
				{
					case 8:
						ParseEncryptedExtensions(message);
						break;
					case 11:
						ParseCertificate(message);
						break;
					case 15:
						VerifyCertificateVerify(message);
						break;
					case 20:
						VerifyServerFinished(message);
						finished = true;
						break;
					case 13:
						throw new TlsException("the server asked for a client certificate");
					default:
						throw new TlsException($"unexpected handshake message {type}");
				}
			}
			if (PeerChain.Count == 0)
				throw new TlsException("the server sent no certificate");
			DeriveApplicationSecrets();
			SendClientFinished();
			InstallWriteKeys(_clientApplicationTraffic);
		}

		private byte[] BuildClientHello(byte[] publicKey, out byte[] random)
		{
			random = new byte[32];
			RandomNumberGenerator.Fill(random);
			byte[] sessionId = new byte[32];
			RandomNumberGenerator.Fill(sessionId);
			MemoryStream body = new MemoryStream();
			Write16(body, Tls12);
			body.Write(random);
			body.WriteByte(32);
			body.Write(sessionId);
			Write16(body, 4);
			Write16(body, AesGcmSha256);
			Write16(body, ChaChaSha256);
			body.WriteByte(1);
			body.WriteByte(0);

			MemoryStream extensions = new MemoryStream();
			byte[] hostBytes = Encoding.ASCII.GetBytes(_host);
			MemoryStream sni = new MemoryStream();
			Write16(sni, (ushort)(hostBytes.Length + 3));
			sni.WriteByte(0);
			Write16(sni, (ushort)hostBytes.Length);
			sni.Write(hostBytes);
			Extension(extensions, 0, sni.ToArray());
			Extension(extensions, 43, new byte[] { 2, 0x03, 0x04 });
			Extension(extensions, 10, new byte[] { 0, 2, 0x00, 0x1d });
			Extension(extensions, 13, new byte[] { 0, 12, 0x04, 0x03, 0x05, 0x03, 0x08, 0x04, 0x08, 0x05, 0x04, 0x01, 0x05, 0x01 });
			MemoryStream keyShare = new MemoryStream();
			Write16(keyShare, 36);
			Write16(keyShare, 0x001d);
			Write16(keyShare, 32);
			keyShare.Write(publicKey);
			Extension(extensions, 51, keyShare.ToArray());
			Extension(extensions, 45, new byte[] { 1, 1 });
			Extension(extensions, 16, new byte[] { 0, 9, 8, (byte)'h', (byte)'t', (byte)'t', (byte)'p', (byte)'/', (byte)'1', (byte)'.', (byte)'1' });
			byte[] extensionBytes = extensions.ToArray();
			Write16(body, (ushort)extensionBytes.Length);
			body.Write(extensionBytes);
			return HandshakeMessage(1, body.ToArray());
		}

		private static void Extension(MemoryStream stream, ushort type, byte[] data)
		{
			Write16(stream, type);
			Write16(stream, (ushort)data.Length);
			stream.Write(data);
		}

		private static byte[] HandshakeMessage(byte type, byte[] body)
		{
			byte[] message = new byte[4 + body.Length];
			message[0] = type;
			message[1] = (byte)(body.Length >> 16);
			message[2] = (byte)(body.Length >> 8);
			message[3] = (byte)body.Length;
			body.CopyTo(message, 4);
			return message;
		}

		private static void Write16(Stream stream, ushort value)
		{
			stream.WriteByte((byte)(value >> 8));
			stream.WriteByte((byte)value);
		}

		private void ParseServerHello(byte[] message, out byte[] serverShare, out bool retry)
		{
			serverShare = null;
			retry = false;
			ReadOnlySpan<byte> body = message.AsSpan(4);
			if (body.Length < 38)
				throw new TlsException("short ServerHello");
			ReadOnlySpan<byte> random = body.Slice(2, 32);
			retry = random.SequenceEqual(HelloRetryRandom);
			int at = 34;
			int sessionLength = body[at++];
			at += sessionLength;
			_cipherSuite = BinaryPrimitives.ReadUInt16BigEndian(body.Slice(at));
			at += 2;
			if (_cipherSuite != AesGcmSha256 && _cipherSuite != ChaChaSha256)
				throw new TlsException($"the server chose cipher suite 0x{_cipherSuite:x4}, which we did not offer");
			at++;
			int extensionsLength = BinaryPrimitives.ReadUInt16BigEndian(body.Slice(at));
			at += 2;
			int end = Math.Min(body.Length, at + extensionsLength);
			bool sawVersion = false;
			while (at + 4 <= end)
			{
				ushort type = BinaryPrimitives.ReadUInt16BigEndian(body.Slice(at));
				int length = BinaryPrimitives.ReadUInt16BigEndian(body.Slice(at + 2));
				ReadOnlySpan<byte> data = body.Slice(at + 4, length);
				at += 4 + length;
				if (type == 43 && length == 2)
					sawVersion = BinaryPrimitives.ReadUInt16BigEndian(data) == Tls13;
				else if (type == 51 && length >= 4)
				{
					ushort group = BinaryPrimitives.ReadUInt16BigEndian(data);
					int keyLength = BinaryPrimitives.ReadUInt16BigEndian(data.Slice(2));
					if (group != 0x001d || keyLength != 32)
						throw new TlsException("the server's key share is not X25519");
					serverShare = data.Slice(4, 32).ToArray();
				}
			}
			if (!sawVersion)
				throw new TlsException("the server does not speak TLS 1.3");
			if (serverShare == null && !retry)
				throw new TlsException("the server sent no key share");
		}

		private static readonly byte[] HelloRetryRandom =
		{
			0xCF, 0x21, 0xAD, 0x74, 0xE5, 0x9A, 0x61, 0x11, 0xBE, 0x1D, 0x8C, 0x02, 0x1E, 0x65, 0xB8, 0x91, 0xC2, 0xA2, 0x11, 0x16, 0x7A, 0xBB, 0x8C, 0x5E, 0x07, 0x9E, 0x09, 0xE2, 0xC8, 0xA8, 0x33, 0x9C,
		};

		private void DeriveHandshakeSecrets(byte[] shared)
		{
			byte[] earlySecret = Hkdf.Extract(_hash, Array.Empty<byte>(), new byte[_hash.OutputSize]);
			byte[] derived = Hkdf.DeriveSecret(_hash, earlySecret, "derived", Array.Empty<byte>());
			byte[] handshakeSecret = Hkdf.Extract(_hash, derived, shared);
			byte[] transcript = _transcript.ToArray();
			_clientHandshakeTraffic = Hkdf.DeriveSecret(_hash, handshakeSecret, "c hs traffic", transcript);
			_serverHandshakeTraffic = Hkdf.DeriveSecret(_hash, handshakeSecret, "s hs traffic", transcript);
			_masterSecret = Hkdf.Extract(_hash, Hkdf.DeriveSecret(_hash, handshakeSecret, "derived", Array.Empty<byte>()), new byte[_hash.OutputSize]);
			InstallReadKeys(_serverHandshakeTraffic);
			InstallWriteKeys(_clientHandshakeTraffic);
		}

		private void DeriveApplicationSecrets()
		{
			byte[] transcript = _transcript.ToArray();
			_clientApplicationTraffic = Hkdf.DeriveSecret(_hash, _masterSecret, "c ap traffic", transcript);
			_serverApplicationTraffic = Hkdf.DeriveSecret(_hash, _masterSecret, "s ap traffic", transcript);
		}

		private IAead MakeAead(byte[] key) => _cipherSuite == ChaChaSha256 ? new ChaCha20Poly1305(key) : new AesGcm(key);

		private int KeyLength => _cipherSuite == ChaChaSha256 ? 32 : 16;

		private void InstallReadKeys(byte[] secret)
		{
			_readAead = MakeAead(Hkdf.ExpandLabel(_hash, secret, "key", Array.Empty<byte>(), KeyLength));
			_readIv = Hkdf.ExpandLabel(_hash, secret, "iv", Array.Empty<byte>(), 12);
			_readSequence = 0;
		}

		private void InstallWriteKeys(byte[] secret)
		{
			_writeAead = MakeAead(Hkdf.ExpandLabel(_hash, secret, "key", Array.Empty<byte>(), KeyLength));
			_writeIv = Hkdf.ExpandLabel(_hash, secret, "iv", Array.Empty<byte>(), 12);
			_writeSequence = 0;
		}

		private static byte[] Nonce(byte[] iv, ulong sequence)
		{
			byte[] nonce = (byte[])iv.Clone();
			for (int i = 0; i < 8; i++)
				nonce[11 - i] ^= (byte)(sequence >> (8 * i));
			return nonce;
		}

		private void ParseEncryptedExtensions(byte[] message)
		{
			ReadOnlySpan<byte> body = message.AsSpan(4);
			if (body.Length < 2)
				return;
			int length = BinaryPrimitives.ReadUInt16BigEndian(body);
			int at = 2;
			int end = Math.Min(body.Length, at + length);
			while (at + 4 <= end)
			{
				ushort type = BinaryPrimitives.ReadUInt16BigEndian(body.Slice(at));
				int size = BinaryPrimitives.ReadUInt16BigEndian(body.Slice(at + 2));
				ReadOnlySpan<byte> data = body.Slice(at + 4, size);
				at += 4 + size;
				if (type == 16 && data.Length >= 3)
					Alpn = Encoding.ASCII.GetString(data.Slice(3, Math.Min(data[2], data.Length - 3)));
			}
		}

		private void ParseCertificate(byte[] message)
		{
			ReadOnlySpan<byte> body = message.AsSpan(4);
			int contextLength = body[0];
			int at = 1 + contextLength;
			int listLength = body[at] << 16 | body[at + 1] << 8 | body[at + 2];
			at += 3;
			int end = Math.Min(body.Length, at + listLength);
			while (at + 3 <= end)
			{
				int certLength = body[at] << 16 | body[at + 1] << 8 | body[at + 2];
				at += 3;
				Certificate cert = Certificate.Parse(body.Slice(at, certLength), out string error);
				at += certLength;
				if (at + 2 > end)
					break;
				int extensionsLength = BinaryPrimitives.ReadUInt16BigEndian(body.Slice(at));
				at += 2 + extensionsLength;
				if (cert == null)
				{
					if (PeerChain.Count == 0)
						throw new TlsException("the server's certificate could not be read: " + error);
					continue;
				}
				PeerChain.Add(cert);
			}
			if (PeerChain.Count == 0)
				throw new TlsException("the server sent an empty certificate list");
			if (!SkipVerification && !ChainValidator.Validate(PeerChain, _roots, _host, _now, out string reason))
				throw new TlsException("certificate rejected: " + reason);
		}

		// CertificateVerify: the server's signature over the transcript so far, with the scheme it chose
		private void VerifyCertificateVerify(byte[] message)
		{
			ReadOnlySpan<byte> body = message.AsSpan(4);
			ushort scheme = BinaryPrimitives.ReadUInt16BigEndian(body);
			int length = BinaryPrimitives.ReadUInt16BigEndian(body.Slice(2));
			ReadOnlySpan<byte> signature = body.Slice(4, length);
			byte[] transcriptHash = _hash.Hash(_transcript.ToArray().AsSpan(0, (int)_transcript.Length - message.Length));
			byte[] context = Encoding.ASCII.GetBytes("TLS 1.3, server CertificateVerify");
			byte[] signed = new byte[64 + context.Length + 1 + transcriptHash.Length];
			Array.Fill(signed, (byte)0x20, 0, 64);
			context.CopyTo(signed, 64);
			transcriptHash.CopyTo(signed, 65 + context.Length);
			(string kind, HashFunction hash) = scheme switch
			{
				0x0403 => ("ecdsa", HashFunction.Sha256),
				0x0503 => ("ecdsa", HashFunction.Sha384),
				0x0804 => ("rsa_pss", HashFunction.Sha256),
				0x0805 => ("rsa_pss", HashFunction.Sha384),
				0x0806 => ("rsa_pss", HashFunction.Sha512),
				0x0809 => ("rsa_pss", HashFunction.Sha256),
				0x080a => ("rsa_pss", HashFunction.Sha384),
				0x080b => ("rsa_pss", HashFunction.Sha512),
				_ => (null, null),
			};
			if (kind == null)
				throw new TlsException($"the server signed with scheme 0x{scheme:x4}, which we do not support");
			if (!PeerChain[0].Key.Verify(kind, hash, signed, signature))
				throw new TlsException("the server's CertificateVerify signature does not verify");
		}

		private void VerifyServerFinished(byte[] message)
		{
			byte[] finishedKey = Hkdf.ExpandLabel(_hash, _serverHandshakeTraffic, "finished", Array.Empty<byte>(), _hash.OutputSize);
			byte[] transcriptHash = _hash.Hash(_transcript.ToArray().AsSpan(0, (int)_transcript.Length - message.Length));
			byte[] expected = Hmac.Compute(_hash, finishedKey, transcriptHash);
			if (!Hmac.Equal(expected, message.AsSpan(4)))
				throw new TlsException("the server's Finished does not verify");
			InstallReadKeys(_serverApplicationTraffic ?? DeriveServerApplicationEarly());
		}

		// The server's application keys are needed as soon as its Finished arrives, so derive them from the transcript at that point
		private byte[] DeriveServerApplicationEarly()
		{
			DeriveApplicationSecrets();
			return _serverApplicationTraffic;
		}

		private void SendClientFinished()
		{
			byte[] finishedKey = Hkdf.ExpandLabel(_hash, _clientHandshakeTraffic, "finished", Array.Empty<byte>(), _hash.OutputSize);
			byte[] verify = Hmac.Compute(_hash, finishedKey, _hash.Hash(_transcript.ToArray()));
			SendHandshake(HandshakeMessage(20, verify), plaintext: false);
		}

		// Records: plaintext before the keys exist, otherwise sealed with the inner content type appended
		private void SendHandshake(byte[] message, bool plaintext)
		{
			_transcript.Write(message);
			if (plaintext)
				WriteRecord(22, message);
			else
				WriteEncrypted(22, message);
		}

		private void WriteRecord(byte type, ReadOnlySpan<byte> payload)
		{
			byte[] record = new byte[5 + payload.Length];
			record[0] = type;
			record[1] = 0x03;
			record[2] = type == 22 && _writeAead == null ? (byte)0x01 : (byte)0x03;
			record[3] = (byte)(payload.Length >> 8);
			record[4] = (byte)payload.Length;
			payload.CopyTo(record.AsSpan(5));
			_inner.Write(record);
			_inner.Flush();
		}

		private void WriteEncrypted(byte type, ReadOnlySpan<byte> content)
		{
			byte[] inner = new byte[content.Length + 1];
			content.CopyTo(inner);
			inner[content.Length] = type;
			byte[] header = { 23, 0x03, 0x03, (byte)((inner.Length + _writeAead.TagSize) >> 8), (byte)(inner.Length + _writeAead.TagSize) };
			byte[] sealedData = _writeAead.Encrypt(Nonce(_writeIv, _writeSequence++), inner, header);
			byte[] record = new byte[5 + sealedData.Length];
			header.CopyTo(record, 0);
			sealedData.CopyTo(record, 5);
			_inner.Write(record);
			_inner.Flush();
		}

		// The next complete handshake message, across records as needed, added to the transcript
		private byte[] ReadHandshakeMessage(out byte type)
		{
			while (true)
			{
				if (_handshakeLength >= 4)
				{
					int length = _handshakeBuffer[1] << 16 | _handshakeBuffer[2] << 8 | _handshakeBuffer[3];
					if (_handshakeLength >= 4 + length)
					{
						byte[] message = _handshakeBuffer.AsSpan(0, 4 + length).ToArray();
						Array.Copy(_handshakeBuffer, 4 + length, _handshakeBuffer, 0, _handshakeLength - 4 - length);
						_handshakeLength -= 4 + length;
						type = message[0];
						_transcript.Write(message);
						return message;
					}
				}
				(byte recordType, byte[] payload) = ReadRecord();
				if (recordType == 22)
				{
					if (_handshakeBuffer.Length < _handshakeLength + payload.Length)
						Array.Resize(ref _handshakeBuffer, Math.Max(_handshakeLength + payload.Length, _handshakeBuffer.Length * 2));
					payload.CopyTo(_handshakeBuffer, _handshakeLength);
					_handshakeLength += payload.Length;
				}
				else if (recordType == 21)
					throw new TlsException(DescribeAlert(payload));
				else if (recordType == 20)
					continue;
				else
					throw new TlsException($"unexpected record type {recordType} during the handshake");
			}
		}

		private static string DescribeAlert(byte[] payload)
		{
			if (payload.Length < 2)
				return "the server sent an alert";
			string name = payload[1] switch
			{
				0 => "close_notify",
				10 => "unexpected_message",
				20 => "bad_record_mac",
				40 => "handshake_failure",
				42 => "bad_certificate",
				47 => "illegal_parameter",
				48 => "unknown_ca",
				50 => "decode_error",
				51 => "decrypt_error",
				70 => "protocol_version",
				80 => "internal_error",
				109 => "missing_extension",
				110 => "unsupported_extension",
				112 => "unrecognized_name",
				120 => "no_application_protocol",
				_ => "alert " + payload[1],
			};
			return "the server sent " + name;
		}

		// One record from the wire, decrypted and unpadded when keys are installed; the inner content type is returned
		private (byte Type, byte[] Payload) ReadRecord()
		{
			ReadExactly(_recordHeader);
			byte type = _recordHeader[0];
			int length = BinaryPrimitives.ReadUInt16BigEndian(_recordHeader.AsSpan(3));
			if (length > MaxPlaintext + 256)
				throw new TlsException("oversized record");
			byte[] payload = new byte[length];
			ReadExactly(payload);
			if (_readAead == null || type != 23)
				return (type, payload);
			byte[] plain = _readAead.Decrypt(Nonce(_readIv, _readSequence++), payload, _recordHeader) ?? throw new TlsException("a record failed to authenticate");
			int end = plain.Length - 1;
			while (end >= 0 && plain[end] == 0)
				end--;
			if (end < 0)
				throw new TlsException("a record had no content type");
			return (plain[end], plain.AsSpan(0, end).ToArray());
		}

		private void ReadExactly(byte[] buffer)
		{
			int total = 0;
			while (total < buffer.Length)
			{
				int read = _inner.Read(buffer, total, buffer.Length - total);
				if (read <= 0)
					throw new TlsException(total == 0 && _readAead != null && buffer == _recordHeader ? "the connection closed" : "the connection closed mid-record");
				total += read;
			}
		}

		// Application data: records are decrypted into a buffer; post-handshake messages (tickets, key updates) are handled on the way
		public override int Read(byte[] buffer, int offset, int count) => Read(buffer.AsSpan(offset, count));

		public override int Read(Span<byte> buffer)
		{
			while (_readStart == _readEnd)
			{
				if (_peerClosed)
					return 0;
				(byte type, byte[] payload) record;
				try
				{
					record = ReadRecord();
				}
				catch (TlsException e) when (e.Message == "the connection closed")
				{
					_peerClosed = true;
					return 0;
				}
				switch (record.type)
				{
					case 23:
						_readBuffer = record.payload;
						_readStart = 0;
						_readEnd = record.payload.Length;
						break;
					case 22:
						HandlePostHandshake(record.payload);
						break;
					case 21:
						_peerClosed = true;
						if (record.payload.Length >= 2 && record.payload[1] == 0)
							return 0;
						throw new TlsException(DescribeAlert(record.payload));
					default:
						break;
				}
			}
			int take = Math.Min(buffer.Length, _readEnd - _readStart);
			_readBuffer.AsSpan(_readStart, take).CopyTo(buffer);
			_readStart += take;
			return take;
		}

		private void HandlePostHandshake(byte[] payload)
		{
			int at = 0;
			while (at + 4 <= payload.Length)
			{
				byte type = payload[at];
				int length = payload[at + 1] << 16 | payload[at + 2] << 8 | payload[at + 3];
				if (at + 4 + length > payload.Length)
					break;
				if (type == 24 && length >= 1)
				{
					// KeyUpdate: the server's keys roll forward; if it asks, ours do too
					_serverApplicationTraffic = Hkdf.ExpandLabel(_hash, _serverApplicationTraffic, "traffic upd", Array.Empty<byte>(), _hash.OutputSize);
					InstallReadKeys(_serverApplicationTraffic);
					if (payload[at + 4] == 1)
					{
						WriteEncrypted(22, new byte[] { 24, 0, 0, 1, 0 });
						_clientApplicationTraffic = Hkdf.ExpandLabel(_hash, _clientApplicationTraffic, "traffic upd", Array.Empty<byte>(), _hash.OutputSize);
						InstallWriteKeys(_clientApplicationTraffic);
					}
				}
				at += 4 + length;
			}
		}

		public override void Write(byte[] buffer, int offset, int count) => Write(buffer.AsSpan(offset, count));

		public override void Write(ReadOnlySpan<byte> buffer)
		{
			if (_closed)
				throw new TlsException("the connection is closed");
			for (int offset = 0; offset < buffer.Length; offset += MaxPlaintext)
				WriteEncrypted(23, buffer.Slice(offset, Math.Min(MaxPlaintext, buffer.Length - offset)));
		}

		protected override void Dispose(bool disposing)
		{
			if (!_closed)
			{
				_closed = true;
				try
				{
					if (_writeAead != null)
						WriteEncrypted(21, new byte[] { 1, 0 });
				}
				catch (Exception)
				{
				}
			}
			base.Dispose(disposing);
		}
	}
}
