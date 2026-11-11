using System;
using System.Buffers.Binary;
using System.Numerics;

namespace Alloy.Sdk.Security.Crypto
{
	// ChaCha20-Poly1305 AEAD, RFC 8439
	public sealed class ChaCha20Poly1305 : IAead
	{
		private readonly byte[] _key;

		public ChaCha20Poly1305(ReadOnlySpan<byte> key)
		{
			if (key.Length != 32)
				throw new ArgumentException("ChaCha20 keys are 32 bytes");
			_key = key.ToArray();
		}

		public int TagSize => 16;

		public byte[] Encrypt(ReadOnlySpan<byte> nonce, ReadOnlySpan<byte> plaintext, ReadOnlySpan<byte> associated)
		{
			byte[] output = new byte[plaintext.Length + 16];
			ChaCha20(_key, nonce, 1, plaintext, output.AsSpan(0, plaintext.Length));
			Tag(nonce, associated, output.AsSpan(0, plaintext.Length)).CopyTo(output.AsSpan(plaintext.Length));
			return output;
		}

		public byte[] Decrypt(ReadOnlySpan<byte> nonce, ReadOnlySpan<byte> ciphertext, ReadOnlySpan<byte> associated)
		{
			if (ciphertext.Length < 16)
				return null;
			ReadOnlySpan<byte> body = ciphertext.Slice(0, ciphertext.Length - 16);
			if (!Hmac.Equal(Tag(nonce, associated, body), ciphertext.Slice(ciphertext.Length - 16)))
				return null;
			byte[] output = new byte[body.Length];
			ChaCha20(_key, nonce, 1, body, output);
			return output;
		}

		private byte[] Tag(ReadOnlySpan<byte> nonce, ReadOnlySpan<byte> associated, ReadOnlySpan<byte> ciphertext)
		{
			byte[] otk = new byte[64];
			ChaCha20(_key, nonce, 0, new byte[64], otk);
			int padA = (16 - associated.Length % 16) % 16;
			int padC = (16 - ciphertext.Length % 16) % 16;
			byte[] message = new byte[associated.Length + padA + ciphertext.Length + padC + 16];
			associated.CopyTo(message);
			ciphertext.CopyTo(message.AsSpan(associated.Length + padA));
			BinaryPrimitives.WriteUInt64LittleEndian(message.AsSpan(message.Length - 16), (ulong)associated.Length);
			BinaryPrimitives.WriteUInt64LittleEndian(message.AsSpan(message.Length - 8), (ulong)ciphertext.Length);
			return Poly1305(otk.AsSpan(0, 32), message);
		}

		// The ChaCha20 stream from a block counter, XORed over the input
		public static void ChaCha20(ReadOnlySpan<byte> key, ReadOnlySpan<byte> nonce, uint counter, ReadOnlySpan<byte> input, Span<byte> output)
		{
			Span<uint> state = stackalloc uint[16];
			Span<uint> working = stackalloc uint[16];
			Span<byte> block = stackalloc byte[64];
			state[0] = 0x61707865;
			state[1] = 0x3320646e;
			state[2] = 0x79622d32;
			state[3] = 0x6b206574;
			for (int i = 0; i < 8; i++)
				state[4 + i] = BinaryPrimitives.ReadUInt32LittleEndian(key.Slice(i * 4));
			state[12] = counter;
			state[13] = BinaryPrimitives.ReadUInt32LittleEndian(nonce);
			state[14] = BinaryPrimitives.ReadUInt32LittleEndian(nonce.Slice(4));
			state[15] = BinaryPrimitives.ReadUInt32LittleEndian(nonce.Slice(8));
			for (int offset = 0; offset < input.Length; offset += 64)
			{
				state.CopyTo(working);
				for (int round = 0; round < 10; round++)
				{
					QuarterRound(working, 0, 4, 8, 12);
					QuarterRound(working, 1, 5, 9, 13);
					QuarterRound(working, 2, 6, 10, 14);
					QuarterRound(working, 3, 7, 11, 15);
					QuarterRound(working, 0, 5, 10, 15);
					QuarterRound(working, 1, 6, 11, 12);
					QuarterRound(working, 2, 7, 8, 13);
					QuarterRound(working, 3, 4, 9, 14);
				}
				for (int i = 0; i < 16; i++)
					BinaryPrimitives.WriteUInt32LittleEndian(block.Slice(i * 4), working[i] + state[i]);
				int take = Math.Min(64, input.Length - offset);
				for (int i = 0; i < take; i++)
					output[offset + i] = (byte)(input[offset + i] ^ block[i]);
				state[12]++;
			}
		}

		private static void QuarterRound(Span<uint> s, int a, int b, int c, int d)
		{
			s[a] += s[b];
			s[d] = Rotl(s[d] ^ s[a], 16);
			s[c] += s[d];
			s[b] = Rotl(s[b] ^ s[c], 12);
			s[a] += s[b];
			s[d] = Rotl(s[d] ^ s[a], 8);
			s[c] += s[d];
			s[b] = Rotl(s[b] ^ s[c], 7);
		}

		private static uint Rotl(uint x, int n) => (x << n) | (x >> (32 - n));

		// Poly1305 over 2^130 - 5, with big integers since the messages are small
		public static byte[] Poly1305(ReadOnlySpan<byte> key, ReadOnlySpan<byte> message)
		{
			BigInteger p = (BigInteger.One << 130) - 5;
			byte[] rBytes = key.Slice(0, 16).ToArray();
			rBytes[3] &= 15;
			rBytes[7] &= 15;
			rBytes[11] &= 15;
			rBytes[15] &= 15;
			rBytes[4] &= 252;
			rBytes[8] &= 252;
			rBytes[12] &= 252;
			BigInteger r = new BigInteger(rBytes, isUnsigned: true, isBigEndian: false);
			BigInteger s = new BigInteger(key.Slice(16, 16), isUnsigned: true, isBigEndian: false);
			BigInteger accumulator = BigInteger.Zero;
			byte[] chunk = new byte[17];
			for (int offset = 0; offset < message.Length; offset += 16)
			{
				int take = Math.Min(16, message.Length - offset);
				Array.Clear(chunk);
				message.Slice(offset, take).CopyTo(chunk);
				chunk[take] = 1;
				BigInteger n = new BigInteger(chunk, isUnsigned: true, isBigEndian: false);
				accumulator = (accumulator + n) * r % p;
			}
			accumulator += s;
			byte[] tag = new byte[16];
			byte[] bytes = accumulator.ToByteArray(isUnsigned: true, isBigEndian: false);
			Array.Copy(bytes, tag, Math.Min(16, bytes.Length));
			return tag;
		}
	}
}
