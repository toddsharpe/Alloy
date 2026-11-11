using System;
using System.Buffers.Binary;

namespace Alloy.Sdk.Security.Crypto
{
	// AES block encryption (FIPS 197) for 128, 192 and 256-bit keys with the usual T-tables; only the forward cipher, which is all GCM needs
	public sealed class Aes
	{
		private static readonly byte[] SBox = BuildSBox();
		private static readonly uint[] Te0 = new uint[256];
		private static readonly uint[] Te1 = new uint[256];
		private static readonly uint[] Te2 = new uint[256];
		private static readonly uint[] Te3 = new uint[256];
		private readonly uint[] _roundKeys;
		private readonly int _rounds;

		static Aes()
		{
			for (int i = 0; i < 256; i++)
			{
				uint s = SBox[i];
				uint s2 = Xtime((byte)s);
				uint s3 = s2 ^ s;
				Te0[i] = s2 << 24 | s << 16 | s << 8 | s3;
				Te1[i] = s3 << 24 | s2 << 16 | s << 8 | s;
				Te2[i] = s << 24 | s3 << 16 | s2 << 8 | s;
				Te3[i] = s << 24 | s << 16 | s3 << 8 | s2;
			}
		}

		public Aes(ReadOnlySpan<byte> key)
		{
			if (key.Length != 16 && key.Length != 24 && key.Length != 32)
				throw new ArgumentException("AES keys are 16, 24 or 32 bytes");
			int nk = key.Length / 4;
			_rounds = nk + 6;
			_roundKeys = new uint[4 * (_rounds + 1)];
			for (int i = 0; i < nk; i++)
				_roundKeys[i] = BinaryPrimitives.ReadUInt32BigEndian(key.Slice(i * 4));
			uint rcon = 1;
			for (int i = nk; i < _roundKeys.Length; i++)
			{
				uint t = _roundKeys[i - 1];
				if (i % nk == 0)
				{
					t = SubWord(t << 8 | t >> 24) ^ rcon << 24;
					rcon = Xtime((byte)rcon);
				}
				else if (nk > 6 && i % nk == 4)
					t = SubWord(t);
				_roundKeys[i] = _roundKeys[i - nk] ^ t;
			}
		}

		private static uint SubWord(uint w) => (uint)(SBox[w >> 24] << 24 | SBox[(w >> 16) & 0xFF] << 16 | SBox[(w >> 8) & 0xFF] << 8 | SBox[w & 0xFF]);

		public void Encrypt(ReadOnlySpan<byte> input, Span<byte> output)
		{
			uint[] rk = _roundKeys;
			uint s0 = BinaryPrimitives.ReadUInt32BigEndian(input) ^ rk[0];
			uint s1 = BinaryPrimitives.ReadUInt32BigEndian(input.Slice(4)) ^ rk[1];
			uint s2 = BinaryPrimitives.ReadUInt32BigEndian(input.Slice(8)) ^ rk[2];
			uint s3 = BinaryPrimitives.ReadUInt32BigEndian(input.Slice(12)) ^ rk[3];
			int k = 4;
			for (int round = 1; round < _rounds; round++, k += 4)
			{
				uint t0 = Te0[s0 >> 24] ^ Te1[(s1 >> 16) & 0xFF] ^ Te2[(s2 >> 8) & 0xFF] ^ Te3[s3 & 0xFF] ^ rk[k];
				uint t1 = Te0[s1 >> 24] ^ Te1[(s2 >> 16) & 0xFF] ^ Te2[(s3 >> 8) & 0xFF] ^ Te3[s0 & 0xFF] ^ rk[k + 1];
				uint t2 = Te0[s2 >> 24] ^ Te1[(s3 >> 16) & 0xFF] ^ Te2[(s0 >> 8) & 0xFF] ^ Te3[s1 & 0xFF] ^ rk[k + 2];
				uint t3 = Te0[s3 >> 24] ^ Te1[(s0 >> 16) & 0xFF] ^ Te2[(s1 >> 8) & 0xFF] ^ Te3[s2 & 0xFF] ^ rk[k + 3];
				s0 = t0;
				s1 = t1;
				s2 = t2;
				s3 = t3;
			}
			// The last round has no MixColumns: SubBytes and ShiftRows only
			uint r0 = (uint)(SBox[s0 >> 24] << 24 | SBox[(s1 >> 16) & 0xFF] << 16 | SBox[(s2 >> 8) & 0xFF] << 8 | SBox[s3 & 0xFF]) ^ rk[k];
			uint r1 = (uint)(SBox[s1 >> 24] << 24 | SBox[(s2 >> 16) & 0xFF] << 16 | SBox[(s3 >> 8) & 0xFF] << 8 | SBox[s0 & 0xFF]) ^ rk[k + 1];
			uint r2 = (uint)(SBox[s2 >> 24] << 24 | SBox[(s3 >> 16) & 0xFF] << 16 | SBox[(s0 >> 8) & 0xFF] << 8 | SBox[s1 & 0xFF]) ^ rk[k + 2];
			uint r3 = (uint)(SBox[s3 >> 24] << 24 | SBox[(s0 >> 16) & 0xFF] << 16 | SBox[(s1 >> 8) & 0xFF] << 8 | SBox[s2 & 0xFF]) ^ rk[k + 3];
			BinaryPrimitives.WriteUInt32BigEndian(output, r0);
			BinaryPrimitives.WriteUInt32BigEndian(output.Slice(4), r1);
			BinaryPrimitives.WriteUInt32BigEndian(output.Slice(8), r2);
			BinaryPrimitives.WriteUInt32BigEndian(output.Slice(12), r3);
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

	// AES-GCM (NIST SP 800-38D) with 12-byte nonces and 16-byte tags, as TLS uses it; GHASH runs four bits at a time from a table of H's multiples
	public sealed class AesGcm : IAead
	{
		private static readonly ulong[] Reduction =
		{
			0x0000, 0x1c20, 0x3840, 0x2460, 0x7080, 0x6ca0, 0x48c0, 0x54e0, 0xe100, 0xfd20, 0xd940, 0xc560, 0x9180, 0x8da0, 0xa9c0, 0xb5e0,
		};

		private readonly Aes _aes;
		private readonly ulong[] _tableHigh = new ulong[16];
		private readonly ulong[] _tableLow = new ulong[16];

		public AesGcm(ReadOnlySpan<byte> key)
		{
			_aes = new Aes(key);
			Span<byte> h = stackalloc byte[16];
			_aes.Encrypt(new byte[16], h);
			ulong hHigh = BinaryPrimitives.ReadUInt64BigEndian(h);
			ulong hLow = BinaryPrimitives.ReadUInt64BigEndian(h.Slice(8));
			// Table entry i holds i times H in GCM's bit order, built by doubling from the top bit down
			_tableHigh[8] = hHigh;
			_tableLow[8] = hLow;
			for (int i = 4; i > 0; i >>= 1)
			{
				ulong high = _tableHigh[i * 2];
				ulong low = _tableLow[i * 2];
				bool lsb = (low & 1) != 0;
				low = (low >> 1) | (high << 63);
				high >>= 1;
				if (lsb)
					high ^= 0xe100000000000000;
				_tableHigh[i] = high;
				_tableLow[i] = low;
			}
			for (int i = 2; i < 16; i <<= 1)
			{
				for (int j = 1; j < i; j++)
				{
					_tableHigh[i + j] = _tableHigh[i] ^ _tableHigh[j];
					_tableLow[i + j] = _tableLow[i] ^ _tableLow[j];
				}
			}
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
			int full = data.Length & ~15;
			for (int offset = 0; offset < full; offset += 16)
			{
				high ^= BinaryPrimitives.ReadUInt64BigEndian(data.Slice(offset));
				low ^= BinaryPrimitives.ReadUInt64BigEndian(data.Slice(offset + 8));
				Multiply(ref high, ref low);
			}
			if (full < data.Length)
			{
				Span<byte> block = stackalloc byte[16];
				block.Clear();
				data.Slice(full).CopyTo(block);
				high ^= BinaryPrimitives.ReadUInt64BigEndian(block);
				low ^= BinaryPrimitives.ReadUInt64BigEndian(block.Slice(8));
				Multiply(ref high, ref low);
			}
		}

		// Shoup's 4-bit method: x times H, nibbles from the least significant up, the product shifted four bits with a reduction table between them
		private void Multiply(ref ulong xHigh, ref ulong xLow)
		{
			ulong zHigh = 0;
			ulong zLow = 0;
			bool first = true;
			for (int word = 0; word < 2; word++)
			{
				ulong x = word == 0 ? xLow : xHigh;
				for (int shift = 0; shift < 64; shift += 4)
				{
					if (!first)
					{
						int rem = (int)(zLow & 15);
						zLow = (zLow >> 4) | (zHigh << 60);
						zHigh = (zHigh >> 4) ^ (Reduction[rem] << 48);
					}
					first = false;
					int nibble = (int)(x >> shift) & 15;
					zHigh ^= _tableHigh[nibble];
					zLow ^= _tableLow[nibble];
				}
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
