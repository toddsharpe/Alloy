using System;
using System.Buffers.Binary;

namespace Alloy.Sdk.Security.Crypto
{
	// AES block encryption (FIPS 197) for 128, 192 and 256-bit keys; only the forward cipher, which is all GCM needs
	public sealed class Aes
	{
		private static readonly byte[] SBox = BuildSBox();
		private readonly byte[] _roundKeys;
		private readonly int _rounds;

		public Aes(ReadOnlySpan<byte> key)
		{
			if (key.Length != 16 && key.Length != 24 && key.Length != 32)
				throw new ArgumentException("AES keys are 16, 24 or 32 bytes");
			int nk = key.Length / 4;
			_rounds = nk + 6;
			_roundKeys = new byte[16 * (_rounds + 1)];
			key.CopyTo(_roundKeys);
			byte rcon = 1;
			for (int i = nk; i < 4 * (_rounds + 1); i++)
			{
				byte t0 = _roundKeys[(i - 1) * 4];
				byte t1 = _roundKeys[(i - 1) * 4 + 1];
				byte t2 = _roundKeys[(i - 1) * 4 + 2];
				byte t3 = _roundKeys[(i - 1) * 4 + 3];
				if (i % nk == 0)
				{
					(t0, t1, t2, t3) = ((byte)(SBox[t1] ^ rcon), SBox[t2], SBox[t3], SBox[t0]);
					rcon = Xtime(rcon);
				}
				else if (nk > 6 && i % nk == 4)
					(t0, t1, t2, t3) = (SBox[t0], SBox[t1], SBox[t2], SBox[t3]);
				_roundKeys[i * 4] = (byte)(_roundKeys[(i - nk) * 4] ^ t0);
				_roundKeys[i * 4 + 1] = (byte)(_roundKeys[(i - nk) * 4 + 1] ^ t1);
				_roundKeys[i * 4 + 2] = (byte)(_roundKeys[(i - nk) * 4 + 2] ^ t2);
				_roundKeys[i * 4 + 3] = (byte)(_roundKeys[(i - nk) * 4 + 3] ^ t3);
			}
		}

		public void Encrypt(ReadOnlySpan<byte> input, Span<byte> output)
		{
			Span<byte> s = stackalloc byte[16];
			input.Slice(0, 16).CopyTo(s);
			AddRoundKey(s, 0);
			for (int round = 1; round < _rounds; round++)
			{
				SubBytes(s);
				ShiftRows(s);
				MixColumns(s);
				AddRoundKey(s, round);
			}
			SubBytes(s);
			ShiftRows(s);
			AddRoundKey(s, _rounds);
			s.CopyTo(output);
		}

		private void AddRoundKey(Span<byte> s, int round)
		{
			for (int i = 0; i < 16; i++)
				s[i] ^= _roundKeys[round * 16 + i];
		}

		private static void SubBytes(Span<byte> s)
		{
			for (int i = 0; i < 16; i++)
				s[i] = SBox[s[i]];
		}

		// State is column-major: byte i sits at row i % 4, column i / 4
		private static void ShiftRows(Span<byte> s)
		{
			byte t = s[1];
			s[1] = s[5];
			s[5] = s[9];
			s[9] = s[13];
			s[13] = t;
			(s[2], s[10]) = (s[10], s[2]);
			(s[6], s[14]) = (s[14], s[6]);
			t = s[15];
			s[15] = s[11];
			s[11] = s[7];
			s[7] = s[3];
			s[3] = t;
		}

		private static void MixColumns(Span<byte> s)
		{
			for (int c = 0; c < 4; c++)
			{
				int i = c * 4;
				byte a0 = s[i], a1 = s[i + 1], a2 = s[i + 2], a3 = s[i + 3];
				s[i] = (byte)(Xtime(a0) ^ (Xtime(a1) ^ a1) ^ a2 ^ a3);
				s[i + 1] = (byte)(a0 ^ Xtime(a1) ^ (Xtime(a2) ^ a2) ^ a3);
				s[i + 2] = (byte)(a0 ^ a1 ^ Xtime(a2) ^ (Xtime(a3) ^ a3));
				s[i + 3] = (byte)((Xtime(a0) ^ a0) ^ a1 ^ a2 ^ Xtime(a3));
			}
		}

		private static byte Xtime(byte x) => (byte)((x << 1) ^ ((x & 0x80) != 0 ? 0x1b : 0));

		private static byte[] BuildSBox()
		{
			byte[] box = new byte[256];
			byte p = 1;
			byte q = 1;
			do
			{
				p = (byte)(p ^ (p << 1) ^ ((p & 0x80) != 0 ? 0x1b : 0));
				q ^= (byte)(q << 1);
				q ^= (byte)(q << 2);
				q ^= (byte)(q << 4);
				if ((q & 0x80) != 0)
					q ^= 0x09;
				byte x = (byte)(q ^ RotateLeft(q, 1) ^ RotateLeft(q, 2) ^ RotateLeft(q, 3) ^ RotateLeft(q, 4));
				box[p] = (byte)(x ^ 0x63);
			}
			while (p != 1);
			box[0] = 0x63;
			return box;
		}

		private static byte RotateLeft(byte x, int n) => (byte)((x << n) | (x >> (8 - n)));
	}

	// AES-GCM (NIST SP 800-38D) with 12-byte nonces and 16-byte tags, as TLS uses it
	public sealed class AesGcm : IAead
	{
		private readonly Aes _aes;
		private readonly ulong _hHigh;
		private readonly ulong _hLow;

		public AesGcm(ReadOnlySpan<byte> key)
		{
			_aes = new Aes(key);
			Span<byte> h = stackalloc byte[16];
			_aes.Encrypt(new byte[16], h);
			_hHigh = BinaryPrimitives.ReadUInt64BigEndian(h);
			_hLow = BinaryPrimitives.ReadUInt64BigEndian(h.Slice(8));
		}

		public int TagSize => 16;

		public byte[] Encrypt(ReadOnlySpan<byte> nonce, ReadOnlySpan<byte> plaintext, ReadOnlySpan<byte> associated)
		{
			byte[] output = new byte[plaintext.Length + 16];
			Counter(nonce, plaintext, output.AsSpan(0, plaintext.Length));
			Tag(nonce, associated, output.AsSpan(0, plaintext.Length)).CopyTo(output.AsSpan(plaintext.Length));
			return output;
		}

		public byte[] Decrypt(ReadOnlySpan<byte> nonce, ReadOnlySpan<byte> ciphertext, ReadOnlySpan<byte> associated)
		{
			if (ciphertext.Length < 16)
				return null;
			ReadOnlySpan<byte> body = ciphertext.Slice(0, ciphertext.Length - 16);
			byte[] expected = Tag(nonce, associated, body);
			if (!Hmac.Equal(expected, ciphertext.Slice(ciphertext.Length - 16)))
				return null;
			byte[] output = new byte[body.Length];
			Counter(nonce, body, output);
			return output;
		}

		// CTR mode from counter block 2 upwards; block 1 is the tag mask
		private void Counter(ReadOnlySpan<byte> nonce, ReadOnlySpan<byte> input, Span<byte> output)
		{
			Span<byte> block = stackalloc byte[16];
			Span<byte> stream = stackalloc byte[16];
			nonce.Slice(0, 12).CopyTo(block);
			uint counter = 2;
			for (int offset = 0; offset < input.Length; offset += 16)
			{
				BinaryPrimitives.WriteUInt32BigEndian(block.Slice(12), counter++);
				_aes.Encrypt(block, stream);
				int take = Math.Min(16, input.Length - offset);
				for (int i = 0; i < take; i++)
					output[offset + i] = (byte)(input[offset + i] ^ stream[i]);
			}
		}

		private byte[] Tag(ReadOnlySpan<byte> nonce, ReadOnlySpan<byte> associated, ReadOnlySpan<byte> ciphertext)
		{
			ulong high = 0;
			ulong low = 0;
			Ghash(ref high, ref low, associated);
			Ghash(ref high, ref low, ciphertext);
			Span<byte> lengths = stackalloc byte[16];
			BinaryPrimitives.WriteUInt64BigEndian(lengths, (ulong)associated.Length * 8);
			BinaryPrimitives.WriteUInt64BigEndian(lengths.Slice(8), (ulong)ciphertext.Length * 8);
			Ghash(ref high, ref low, lengths);
			Span<byte> block = stackalloc byte[16];
			Span<byte> mask = stackalloc byte[16];
			nonce.Slice(0, 12).CopyTo(block);
			BinaryPrimitives.WriteUInt32BigEndian(block.Slice(12), 1);
			_aes.Encrypt(block, mask);
			byte[] tag = new byte[16];
			BinaryPrimitives.WriteUInt64BigEndian(tag, high ^ BinaryPrimitives.ReadUInt64BigEndian(mask));
			BinaryPrimitives.WriteUInt64BigEndian(tag.AsSpan(8), low ^ BinaryPrimitives.ReadUInt64BigEndian(mask.Slice(8)));
			return tag;
		}

		private void Ghash(ref ulong high, ref ulong low, ReadOnlySpan<byte> data)
		{
			Span<byte> block = stackalloc byte[16];
			for (int offset = 0; offset < data.Length; offset += 16)
			{
				block.Clear();
				data.Slice(offset, Math.Min(16, data.Length - offset)).CopyTo(block);
				high ^= BinaryPrimitives.ReadUInt64BigEndian(block);
				low ^= BinaryPrimitives.ReadUInt64BigEndian(block.Slice(8));
				Multiply(ref high, ref low);
			}
		}

		// Multiplication in GF(2^128) with GCM's bit order: x times H, bit by bit
		private void Multiply(ref ulong xHigh, ref ulong xLow)
		{
			ulong zHigh = 0;
			ulong zLow = 0;
			ulong vHigh = _hHigh;
			ulong vLow = _hLow;
			for (int i = 0; i < 128; i++)
			{
				bool bit = i < 64 ? ((xHigh >> (63 - i)) & 1) != 0 : ((xLow >> (127 - i)) & 1) != 0;
				if (bit)
				{
					zHigh ^= vHigh;
					zLow ^= vLow;
				}
				bool lsb = (vLow & 1) != 0;
				vLow = (vLow >> 1) | (vHigh << 63);
				vHigh >>= 1;
				if (lsb)
					vHigh ^= 0xe100000000000000;
			}
			xHigh = zHigh;
			xLow = zLow;
		}
	}

	// An authenticated cipher as the TLS record layer uses one
	public interface IAead
	{
		int TagSize { get; }

		byte[] Encrypt(ReadOnlySpan<byte> nonce, ReadOnlySpan<byte> plaintext, ReadOnlySpan<byte> associated);

		// Null when the tag does not match
		byte[] Decrypt(ReadOnlySpan<byte> nonce, ReadOnlySpan<byte> ciphertext, ReadOnlySpan<byte> associated);
	}
}
