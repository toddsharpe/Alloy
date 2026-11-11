using System;
using System.Buffers.Binary;

namespace Alloy.Sdk.Security.Crypto
{
	// A hash function as TLS and the signature schemes need it: one-shot, plus its block and output sizes for HMAC
	public abstract class HashFunction
	{
		public abstract int OutputSize { get; }

		public abstract int BlockSize { get; }

		public abstract string Name { get; }

		public abstract byte[] Hash(ReadOnlySpan<byte> data);

		public static readonly HashFunction Sha256 = new Sha256Function();
		public static readonly HashFunction Sha384 = new Sha384Function();
		public static readonly HashFunction Sha512 = new Sha512Function();

		// The hash a TLS or X.509 algorithm name asks for
		public static HashFunction ByName(string name) => name switch
		{
			"sha256" => Sha256,
			"sha384" => Sha384,
			"sha512" => Sha512,
			_ => null,
		};
	}

	// SHA-256, FIPS 180-4
	public sealed class Sha256Function : HashFunction
	{
		private static readonly uint[] K =
		{
			0x428a2f98, 0x71374491, 0xb5c0fbcf, 0xe9b5dba5, 0x3956c25b, 0x59f111f1, 0x923f82a4, 0xab1c5ed5, 0xd807aa98, 0x12835b01, 0x243185be, 0x550c7dc3, 0x72be5d74, 0x80deb1fe, 0x9bdc06a7, 0xc19bf174,
			0xe49b69c1, 0xefbe4786, 0x0fc19dc6, 0x240ca1cc, 0x2de92c6f, 0x4a7484aa, 0x5cb0a9dc, 0x76f988da, 0x983e5152, 0xa831c66d, 0xb00327c8, 0xbf597fc7, 0xc6e00bf3, 0xd5a79147, 0x06ca6351, 0x14292967,
			0x27b70a85, 0x2e1b2138, 0x4d2c6dfc, 0x53380d13, 0x650a7354, 0x766a0abb, 0x81c2c92e, 0x92722c85, 0xa2bfe8a1, 0xa81a664b, 0xc24b8b70, 0xc76c51a3, 0xd192e819, 0xd6990624, 0xf40e3585, 0x106aa070,
			0x19a4c116, 0x1e376c08, 0x2748774c, 0x34b0bcb5, 0x391c0cb3, 0x4ed8aa4a, 0x5b9cca4f, 0x682e6ff3, 0x748f82ee, 0x78a5636f, 0x84c87814, 0x8cc70208, 0x90befffa, 0xa4506ceb, 0xbef9a3f7, 0xc67178f2,
		};

		public override int OutputSize => 32;

		public override int BlockSize => 64;

		public override string Name => "sha256";

		public override byte[] Hash(ReadOnlySpan<byte> data)
		{
			uint[] h = { 0x6a09e667, 0xbb67ae85, 0x3c6ef372, 0xa54ff53a, 0x510e527f, 0x9b05688c, 0x1f83d9ab, 0x5be0cd19 };
			int paddedLength = (data.Length + 9 + 63) / 64 * 64;
			byte[] padded = new byte[paddedLength];
			data.CopyTo(padded);
			padded[data.Length] = 0x80;
			BinaryPrimitives.WriteUInt64BigEndian(padded.AsSpan(paddedLength - 8), (ulong)data.Length * 8);
			uint[] w = new uint[64];
			for (int block = 0; block < paddedLength; block += 64)
			{
				for (int i = 0; i < 16; i++)
					w[i] = BinaryPrimitives.ReadUInt32BigEndian(padded.AsSpan(block + i * 4));
				for (int i = 16; i < 64; i++)
				{
					uint s0 = Rotr(w[i - 15], 7) ^ Rotr(w[i - 15], 18) ^ (w[i - 15] >> 3);
					uint s1 = Rotr(w[i - 2], 17) ^ Rotr(w[i - 2], 19) ^ (w[i - 2] >> 10);
					w[i] = w[i - 16] + s0 + w[i - 7] + s1;
				}
				uint a = h[0], b = h[1], c = h[2], d = h[3], e = h[4], f = h[5], g = h[6], hh = h[7];
				for (int i = 0; i < 64; i++)
				{
					uint t1 = hh + (Rotr(e, 6) ^ Rotr(e, 11) ^ Rotr(e, 25)) + ((e & f) ^ (~e & g)) + K[i] + w[i];
					uint t2 = (Rotr(a, 2) ^ Rotr(a, 13) ^ Rotr(a, 22)) + ((a & b) ^ (a & c) ^ (b & c));
					hh = g;
					g = f;
					f = e;
					e = d + t1;
					d = c;
					c = b;
					b = a;
					a = t1 + t2;
				}
				h[0] += a;
				h[1] += b;
				h[2] += c;
				h[3] += d;
				h[4] += e;
				h[5] += f;
				h[6] += g;
				h[7] += hh;
			}
			byte[] digest = new byte[32];
			for (int i = 0; i < 8; i++)
				BinaryPrimitives.WriteUInt32BigEndian(digest.AsSpan(i * 4), h[i]);
			return digest;
		}

		private static uint Rotr(uint x, int n) => (x >> n) | (x << (32 - n));
	}

	// SHA-512 and SHA-384 (the same rounds with other initial values, truncated), FIPS 180-4
	public class Sha512Function : HashFunction
	{
		private static readonly ulong[] K =
		{
			0x428a2f98d728ae22, 0x7137449123ef65cd, 0xb5c0fbcfec4d3b2f, 0xe9b5dba58189dbbc, 0x3956c25bf348b538, 0x59f111f1b605d019, 0x923f82a4af194f9b, 0xab1c5ed5da6d8118,
			0xd807aa98a3030242, 0x12835b0145706fbe, 0x243185be4ee4b28c, 0x550c7dc3d5ffb4e2, 0x72be5d74f27b896f, 0x80deb1fe3b1696b1, 0x9bdc06a725c71235, 0xc19bf174cf692694,
			0xe49b69c19ef14ad2, 0xefbe4786384f25e3, 0x0fc19dc68b8cd5b5, 0x240ca1cc77ac9c65, 0x2de92c6f592b0275, 0x4a7484aa6ea6e483, 0x5cb0a9dcbd41fbd4, 0x76f988da831153b5,
			0x983e5152ee66dfab, 0xa831c66d2db43210, 0xb00327c898fb213f, 0xbf597fc7beef0ee4, 0xc6e00bf33da88fc2, 0xd5a79147930aa725, 0x06ca6351e003826f, 0x142929670a0e6e70,
			0x27b70a8546d22ffc, 0x2e1b21385c26c926, 0x4d2c6dfc5ac42aed, 0x53380d139d95b3df, 0x650a73548baf63de, 0x766a0abb3c77b2a8, 0x81c2c92e47edaee6, 0x92722c851482353b,
			0xa2bfe8a14cf10364, 0xa81a664bbc423001, 0xc24b8b70d0f89791, 0xc76c51a30654be30, 0xd192e819d6ef5218, 0xd69906245565a910, 0xf40e35855771202a, 0x106aa07032bbd1b8,
			0x19a4c116b8d2d0c8, 0x1e376c085141ab53, 0x2748774cdf8eeb99, 0x34b0bcb5e19b48a8, 0x391c0cb3c5c95a63, 0x4ed8aa4ae3418acb, 0x5b9cca4f7763e373, 0x682e6ff3d6b2b8a3,
			0x748f82ee5defb2fc, 0x78a5636f43172f60, 0x84c87814a1f0ab72, 0x8cc702081a6439ec, 0x90befffa23631e28, 0xa4506cebde82bde9, 0xbef9a3f7b2c67915, 0xc67178f2e372532b,
			0xca273eceea26619c, 0xd186b8c721c0c207, 0xeada7dd6cde0eb1e, 0xf57d4f7fee6ed178, 0x06f067aa72176fba, 0x0a637dc5a2c898a6, 0x113f9804bef90dae, 0x1b710b35131c471b,
			0x28db77f523047d84, 0x32caab7b40c72493, 0x3c9ebe0a15c9bebc, 0x431d67c49c100d4c, 0x4cc5d4becb3e42b6, 0x597f299cfc657e2a, 0x5fcb6fab3ad6faec, 0x6c44198c4a475817,
		};

		protected virtual ulong[] Initial => new ulong[] { 0x6a09e667f3bcc908, 0xbb67ae8584caa73b, 0x3c6ef372fe94f82b, 0xa54ff53a5f1d36f1, 0x510e527fade682d1, 0x9b05688c2b3e6c1f, 0x1f83d9abfb41bd6b, 0x5be0cd19137e2179 };

		public override int OutputSize => 64;

		public override int BlockSize => 128;

		public override string Name => "sha512";

		public override byte[] Hash(ReadOnlySpan<byte> data)
		{
			ulong[] h = Initial;
			int paddedLength = (data.Length + 17 + 127) / 128 * 128;
			byte[] padded = new byte[paddedLength];
			data.CopyTo(padded);
			padded[data.Length] = 0x80;
			BinaryPrimitives.WriteUInt64BigEndian(padded.AsSpan(paddedLength - 8), (ulong)data.Length * 8);
			ulong[] w = new ulong[80];
			for (int block = 0; block < paddedLength; block += 128)
			{
				for (int i = 0; i < 16; i++)
					w[i] = BinaryPrimitives.ReadUInt64BigEndian(padded.AsSpan(block + i * 8));
				for (int i = 16; i < 80; i++)
				{
					ulong s0 = Rotr(w[i - 15], 1) ^ Rotr(w[i - 15], 8) ^ (w[i - 15] >> 7);
					ulong s1 = Rotr(w[i - 2], 19) ^ Rotr(w[i - 2], 61) ^ (w[i - 2] >> 6);
					w[i] = w[i - 16] + s0 + w[i - 7] + s1;
				}
				ulong a = h[0], b = h[1], c = h[2], d = h[3], e = h[4], f = h[5], g = h[6], hh = h[7];
				for (int i = 0; i < 80; i++)
				{
					ulong t1 = hh + (Rotr(e, 14) ^ Rotr(e, 18) ^ Rotr(e, 41)) + ((e & f) ^ (~e & g)) + K[i] + w[i];
					ulong t2 = (Rotr(a, 28) ^ Rotr(a, 34) ^ Rotr(a, 39)) + ((a & b) ^ (a & c) ^ (b & c));
					hh = g;
					g = f;
					f = e;
					e = d + t1;
					d = c;
					c = b;
					b = a;
					a = t1 + t2;
				}
				h[0] += a;
				h[1] += b;
				h[2] += c;
				h[3] += d;
				h[4] += e;
				h[5] += f;
				h[6] += g;
				h[7] += hh;
			}
			byte[] digest = new byte[OutputSize];
			for (int i = 0; i < OutputSize / 8; i++)
				BinaryPrimitives.WriteUInt64BigEndian(digest.AsSpan(i * 8), h[i]);
			return digest;
		}

		private static ulong Rotr(ulong x, int n) => (x >> n) | (x << (64 - n));
	}

	public sealed class Sha384Function : Sha512Function
	{
		protected override ulong[] Initial => new ulong[] { 0xcbbb9d5dc1059ed8, 0x629a292a367cd507, 0x9159015a3070dd17, 0x152fecd8f70e5939, 0x67332667ffc00b31, 0x8eb44a8768581511, 0xdb0c2e0d64f98fa7, 0x47b5481dbefa4fa4 };

		public override int OutputSize => 48;

		public override string Name => "sha384";
	}

	// HMAC (RFC 2104) and HKDF (RFC 5869) over any of the hashes, with TLS 1.3's labelled expansion
	public static class Hmac
	{
		public static byte[] Compute(HashFunction hash, ReadOnlySpan<byte> key, ReadOnlySpan<byte> data)
		{
			byte[] block = new byte[hash.BlockSize];
			if (key.Length > hash.BlockSize)
				hash.Hash(key).CopyTo(block, 0);
			else
				key.CopyTo(block);
			byte[] inner = new byte[hash.BlockSize + data.Length];
			byte[] outer = new byte[hash.BlockSize + hash.OutputSize];
			for (int i = 0; i < hash.BlockSize; i++)
			{
				inner[i] = (byte)(block[i] ^ 0x36);
				outer[i] = (byte)(block[i] ^ 0x5c);
			}
			data.CopyTo(inner.AsSpan(hash.BlockSize));
			hash.Hash(inner).CopyTo(outer, hash.BlockSize);
			return hash.Hash(outer);
		}

		public static bool Equal(ReadOnlySpan<byte> a, ReadOnlySpan<byte> b)
		{
			if (a.Length != b.Length)
				return false;
			int difference = 0;
			for (int i = 0; i < a.Length; i++)
				difference |= a[i] ^ b[i];
			return difference == 0;
		}
	}

	public static class Hkdf
	{
		public static byte[] Extract(HashFunction hash, ReadOnlySpan<byte> salt, ReadOnlySpan<byte> keyMaterial)
		{
			if (salt.Length == 0)
				salt = new byte[hash.OutputSize];
			return Hmac.Compute(hash, salt, keyMaterial);
		}

		public static byte[] Expand(HashFunction hash, ReadOnlySpan<byte> prk, ReadOnlySpan<byte> info, int length)
		{
			byte[] output = new byte[length];
			byte[] previous = Array.Empty<byte>();
			int written = 0;
			for (byte counter = 1; written < length; counter++)
			{
				byte[] input = new byte[previous.Length + info.Length + 1];
				previous.CopyTo(input, 0);
				info.CopyTo(input.AsSpan(previous.Length));
				input[input.Length - 1] = counter;
				previous = Hmac.Compute(hash, prk, input);
				int take = Math.Min(previous.Length, length - written);
				Array.Copy(previous, 0, output, written, take);
				written += take;
			}
			return output;
		}

		// HKDF-Expand-Label from RFC 8446 section 7.1
		public static byte[] ExpandLabel(HashFunction hash, ReadOnlySpan<byte> secret, string label, ReadOnlySpan<byte> context, int length)
		{
			byte[] fullLabel = System.Text.Encoding.ASCII.GetBytes("tls13 " + label);
			byte[] info = new byte[2 + 1 + fullLabel.Length + 1 + context.Length];
			info[0] = (byte)(length >> 8);
			info[1] = (byte)length;
			info[2] = (byte)fullLabel.Length;
			fullLabel.CopyTo(info, 3);
			info[3 + fullLabel.Length] = (byte)context.Length;
			context.CopyTo(info.AsSpan(4 + fullLabel.Length));
			return Expand(hash, secret, info, length);
		}

		public static byte[] DeriveSecret(HashFunction hash, ReadOnlySpan<byte> secret, string label, ReadOnlySpan<byte> transcript) =>
			ExpandLabel(hash, secret, label, hash.Hash(transcript), hash.OutputSize);
	}
}
