using System;
using System.Numerics;

namespace Alloy.Sdk.Security.Crypto
{
	// X25519 key agreement, RFC 7748, with big-integer field arithmetic (a handshake needs one of these, so speed is not the point)
	public static class X25519
	{
		private static readonly BigInteger P = (BigInteger.One << 255) - 19;
		private static readonly BigInteger A24 = 121665;

		public static byte[] BasePoint
		{
			get
			{
				byte[] u = new byte[32];
				u[0] = 9;
				return u;
			}
		}

		public static byte[] PublicKey(ReadOnlySpan<byte> privateKey) => ScalarMultiply(privateKey, BasePoint);

		public static byte[] SharedSecret(ReadOnlySpan<byte> privateKey, ReadOnlySpan<byte> peerPublicKey) => ScalarMultiply(privateKey, peerPublicKey);

		public static byte[] ScalarMultiply(ReadOnlySpan<byte> scalarBytes, ReadOnlySpan<byte> uBytes)
		{
			byte[] k = scalarBytes.Slice(0, 32).ToArray();
			k[0] &= 248;
			k[31] &= 127;
			k[31] |= 64;
			byte[] uClamped = uBytes.Slice(0, 32).ToArray();
			uClamped[31] &= 127;
			BigInteger scalar = new BigInteger(k, isUnsigned: true, isBigEndian: false);
			BigInteger u = new BigInteger(uClamped, isUnsigned: true, isBigEndian: false) % P;

			BigInteger x2 = BigInteger.One;
			BigInteger z2 = BigInteger.Zero;
			BigInteger x3 = u;
			BigInteger z3 = BigInteger.One;
			int swap = 0;
			for (int t = 254; t >= 0; t--)
			{
				int bit = (int)((scalar >> t) & 1);
				swap ^= bit;
				if (swap == 1)
				{
					(x2, x3) = (x3, x2);
					(z2, z3) = (z3, z2);
				}
				swap = bit;
				BigInteger a = Mod(x2 + z2);
				BigInteger aa = Mod(a * a);
				BigInteger b = Mod(x2 - z2);
				BigInteger bb = Mod(b * b);
				BigInteger e = Mod(aa - bb);
				BigInteger c = Mod(x3 + z3);
				BigInteger d = Mod(x3 - z3);
				BigInteger da = Mod(d * a);
				BigInteger cb = Mod(c * b);
				BigInteger daPlusCb = Mod(da + cb);
				BigInteger daMinusCb = Mod(da - cb);
				x3 = Mod(daPlusCb * daPlusCb);
				z3 = Mod(u * Mod(daMinusCb * daMinusCb));
				x2 = Mod(aa * bb);
				z2 = Mod(e * Mod(aa + A24 * e));
			}
			if (swap == 1)
			{
				(x2, x3) = (x3, x2);
				(z2, z3) = (z3, z2);
			}
			BigInteger result = Mod(x2 * BigInteger.ModPow(z2, P - 2, P));
			byte[] output = new byte[32];
			byte[] bytes = result.ToByteArray(isUnsigned: true, isBigEndian: false);
			Array.Copy(bytes, output, Math.Min(32, bytes.Length));
			return output;
		}

		private static BigInteger Mod(BigInteger value)
		{
			value %= P;
			return value.Sign < 0 ? value + P : value;
		}
	}
}
