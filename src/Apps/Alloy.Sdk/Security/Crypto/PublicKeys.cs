using System;
using System.Numerics;

namespace Alloy.Sdk.Security.Crypto
{
	// A public key that can check a signature: RSA or an elliptic curve point
	public abstract class PublicKey
	{
		public abstract string Kind { get; }

		// Verifies a signature over data with the named scheme ("rsa_pkcs1", "rsa_pss", "ecdsa") and hash
		public abstract bool Verify(string scheme, HashFunction hash, ReadOnlySpan<byte> data, ReadOnlySpan<byte> signature);
	}

	// RSA verification: PKCS#1 v1.5 (RFC 8017 section 8.2) for certificates and PSS (section 8.1) for TLS 1.3
	public sealed class RsaPublicKey : PublicKey
	{
		public readonly BigInteger Modulus;
		public readonly BigInteger Exponent;

		public RsaPublicKey(BigInteger modulus, BigInteger exponent)
		{
			Modulus = modulus;
			Exponent = exponent;
		}

		public override string Kind => "rsa";

		public int Bits => (int)Modulus.GetBitLength();

		public override bool Verify(string scheme, HashFunction hash, ReadOnlySpan<byte> data, ReadOnlySpan<byte> signature)
		{
			int k = (Bits + 7) / 8;
			if (signature.Length != k)
				return false;
			BigInteger s = new BigInteger(signature, isUnsigned: true, isBigEndian: true);
			if (s >= Modulus)
				return false;
			BigInteger m = BigInteger.ModPow(s, Exponent, Modulus);
			byte[] em = new byte[k];
			byte[] raw = m.ToByteArray(isUnsigned: true, isBigEndian: true);
			raw.CopyTo(em, k - raw.Length);
			byte[] digest = hash.Hash(data);
			return scheme switch
			{
				"rsa_pkcs1" => VerifyPkcs1(em, hash, digest),
				"rsa_pss" => VerifyPss(em, hash, digest, Bits - 1),
				_ => false,
			};
		}

		private static bool VerifyPkcs1(byte[] em, HashFunction hash, byte[] digest)
		{
			byte[] prefix = DigestInfoPrefix(hash);
			if (prefix == null)
				return false;
			int tLength = prefix.Length + digest.Length;
			if (em.Length < tLength + 11 || em[0] != 0 || em[1] != 1)
				return false;
			int i = 2;
			while (i < em.Length - tLength - 1)
			{
				if (em[i] != 0xFF)
					return false;
				i++;
			}
			if (em[i] != 0)
				return false;
			i++;
			byte[] expected = new byte[tLength];
			prefix.CopyTo(expected, 0);
			digest.CopyTo(expected, prefix.Length);
			return Hmac.Equal(expected, em.AsSpan(i));
		}

		// The DER DigestInfo header in front of the hash in a PKCS#1 v1.5 signature
		private static byte[] DigestInfoPrefix(HashFunction hash) => hash.Name switch
		{
			"sha256" => new byte[] { 0x30, 0x31, 0x30, 0x0d, 0x06, 0x09, 0x60, 0x86, 0x48, 0x01, 0x65, 0x03, 0x04, 0x02, 0x01, 0x05, 0x00, 0x04, 0x20 },
			"sha384" => new byte[] { 0x30, 0x41, 0x30, 0x0d, 0x06, 0x09, 0x60, 0x86, 0x48, 0x01, 0x65, 0x03, 0x04, 0x02, 0x02, 0x05, 0x00, 0x04, 0x30 },
			"sha512" => new byte[] { 0x30, 0x51, 0x30, 0x0d, 0x06, 0x09, 0x60, 0x86, 0x48, 0x01, 0x65, 0x03, 0x04, 0x02, 0x03, 0x05, 0x00, 0x04, 0x40 },
			_ => null,
		};

		// EMSA-PSS-VERIFY with MGF1 over the same hash and a salt as long as the hash, which is what TLS 1.3 mandates
		private static bool VerifyPss(byte[] em, HashFunction hash, byte[] digest, int emBits)
		{
			int hLen = hash.OutputSize;
			int sLen = hLen;
			int emLen = (emBits + 7) / 8;
			if (em.Length > emLen)
				em = em.AsSpan(em.Length - emLen).ToArray();
			if (emLen < hLen + sLen + 2 || em[emLen - 1] != 0xbc)
				return false;
			byte[] maskedDb = em.AsSpan(0, emLen - hLen - 1).ToArray();
			byte[] h = em.AsSpan(emLen - hLen - 1, hLen).ToArray();
			int unusedBits = 8 * emLen - emBits;
			if (unusedBits > 0 && (maskedDb[0] >> (8 - unusedBits)) != 0)
				return false;
			byte[] dbMask = Mgf1(hash, h, maskedDb.Length);
			byte[] db = new byte[maskedDb.Length];
			for (int i = 0; i < db.Length; i++)
				db[i] = (byte)(maskedDb[i] ^ dbMask[i]);
			if (unusedBits > 0)
				db[0] &= (byte)(0xFF >> unusedBits);
			int padLength = emLen - hLen - sLen - 2;
			for (int i = 0; i < padLength; i++)
			{
				if (db[i] != 0)
					return false;
			}
			if (db[padLength] != 1)
				return false;
			byte[] salt = db.AsSpan(padLength + 1, sLen).ToArray();
			byte[] mPrime = new byte[8 + hLen + sLen];
			digest.CopyTo(mPrime, 8);
			salt.CopyTo(mPrime, 8 + hLen);
			return Hmac.Equal(hash.Hash(mPrime), h);
		}

		private static byte[] Mgf1(HashFunction hash, byte[] seed, int length)
		{
			byte[] mask = new byte[length];
			byte[] input = new byte[seed.Length + 4];
			seed.CopyTo(input, 0);
			int written = 0;
			for (uint counter = 0; written < length; counter++)
			{
				input[seed.Length] = (byte)(counter >> 24);
				input[seed.Length + 1] = (byte)(counter >> 16);
				input[seed.Length + 2] = (byte)(counter >> 8);
				input[seed.Length + 3] = (byte)counter;
				byte[] block = hash.Hash(input);
				int take = Math.Min(block.Length, length - written);
				Array.Copy(block, 0, mask, written, take);
				written += take;
			}
			return mask;
		}
	}

	// A short-Weierstrass prime curve: P-256 and P-384 as X.509 and TLS use them
	public sealed class EllipticCurve
	{
		public readonly string Name;
		public readonly BigInteger P;
		public readonly BigInteger A;
		public readonly BigInteger B;
		public readonly BigInteger Gx;
		public readonly BigInteger Gy;
		public readonly BigInteger N;
		public readonly int ByteLength;

		private EllipticCurve(string name, string p, string a, string b, string gx, string gy, string n)
		{
			Name = name;
			P = Parse(p);
			A = Parse(a);
			B = Parse(b);
			Gx = Parse(gx);
			Gy = Parse(gy);
			N = Parse(n);
			ByteLength = ((int)P.GetBitLength() + 7) / 8;
		}

		private static BigInteger Parse(string hex) => BigInteger.Parse("0" + hex, System.Globalization.NumberStyles.HexNumber);

		public static readonly EllipticCurve P256 = new EllipticCurve("secp256r1",
			"FFFFFFFF00000001000000000000000000000000FFFFFFFFFFFFFFFFFFFFFFFF",
			"FFFFFFFF00000001000000000000000000000000FFFFFFFFFFFFFFFFFFFFFFFC",
			"5AC635D8AA3A93E7B3EBBD55769886BC651D06B0CC53B0F63BCE3C3E27D2604B",
			"6B17D1F2E12C4247F8BCE6E563A440F277037D812DEB33A0F4A13945D898C296",
			"4FE342E2FE1A7F9B8EE7EB4A7C0F9E162BCE33576B315ECECBB6406837BF51F5",
			"FFFFFFFF00000000FFFFFFFFFFFFFFFFBCE6FAADA7179E84F3B9CAC2FC632551");

		public static readonly EllipticCurve P384 = new EllipticCurve("secp384r1",
			"FFFFFFFFFFFFFFFFFFFFFFFFFFFFFFFFFFFFFFFFFFFFFFFFFFFFFFFFFFFFFFFEFFFFFFFF0000000000000000FFFFFFFF",
			"FFFFFFFFFFFFFFFFFFFFFFFFFFFFFFFFFFFFFFFFFFFFFFFFFFFFFFFFFFFFFFFEFFFFFFFF0000000000000000FFFFFFFC",
			"B3312FA7E23EE7E4988E056BE3F82D19181D9C6EFE8141120314088F5013875AC656398D8A2ED19D2A85C8EDD3EC2AEF",
			"AA87CA22BE8B05378EB1C71EF320AD746E1D3B628BA79B9859F741E082542A385502F25DBF55296C3A545E3872760AB7",
			"3617DE4A96262C6F5D9E98BF9292DC29F8F41DBD289A147CE9DA3113B5F0B8C00A60B1CE1D7E819D7A431D7C90EA0E5F",
			"FFFFFFFFFFFFFFFFFFFFFFFFFFFFFFFFFFFFFFFFFFFFFFFFC7634D81F4372DDF581A0DB248B0A77AECEC196ACCC52973");

		public static EllipticCurve ByName(string name) => name switch
		{
			"secp256r1" or "prime256v1" or "P-256" => P256,
			"secp384r1" or "P-384" => P384,
			_ => null,
		};

		// Points in Jacobian coordinates; (0, 1, 0) is infinity
		private readonly struct Point
		{
			public readonly BigInteger X;
			public readonly BigInteger Y;
			public readonly BigInteger Z;

			public Point(BigInteger x, BigInteger y, BigInteger z)
			{
				X = x;
				Y = y;
				Z = z;
			}

			public bool IsInfinity => Z.IsZero;
		}

		private BigInteger Mod(BigInteger v)
		{
			v %= P;
			return v.Sign < 0 ? v + P : v;
		}

		private Point Double(Point p)
		{
			if (p.IsInfinity || p.Y.IsZero)
				return new Point(0, 1, 0);
			BigInteger xx = Mod(p.X * p.X);
			BigInteger yy = Mod(p.Y * p.Y);
			BigInteger yyyy = Mod(yy * yy);
			BigInteger zz = Mod(p.Z * p.Z);
			BigInteger s = Mod(4 * p.X * yy);
			BigInteger m = Mod(3 * xx + A * Mod(zz * zz));
			BigInteger x3 = Mod(m * m - 2 * s);
			BigInteger y3 = Mod(m * (s - x3) - 8 * yyyy);
			BigInteger z3 = Mod(2 * p.Y * p.Z);
			return new Point(x3, y3, z3);
		}

		private Point Add(Point p, Point q)
		{
			if (p.IsInfinity)
				return q;
			if (q.IsInfinity)
				return p;
			BigInteger z1z1 = Mod(p.Z * p.Z);
			BigInteger z2z2 = Mod(q.Z * q.Z);
			BigInteger u1 = Mod(p.X * z2z2);
			BigInteger u2 = Mod(q.X * z1z1);
			BigInteger s1 = Mod(p.Y * q.Z * z2z2);
			BigInteger s2 = Mod(q.Y * p.Z * z1z1);
			if (u1 == u2)
				return s1 == s2 ? Double(p) : new Point(0, 1, 0);
			BigInteger h = Mod(u2 - u1);
			BigInteger r = Mod(s2 - s1);
			BigInteger hh = Mod(h * h);
			BigInteger hhh = Mod(hh * h);
			BigInteger v = Mod(u1 * hh);
			BigInteger x3 = Mod(r * r - hhh - 2 * v);
			BigInteger y3 = Mod(r * (v - x3) - s1 * hhh);
			BigInteger z3 = Mod(p.Z * q.Z * h);
			return new Point(x3, y3, z3);
		}

		private Point Multiply(Point p, BigInteger k)
		{
			Point result = new Point(0, 1, 0);
			Point addend = p;
			for (int i = 0; i < k.GetBitLength(); i++)
			{
				if (!((k >> i) & 1).IsZero)
					result = Add(result, addend);
				addend = Double(addend);
			}
			return result;
		}

		private (BigInteger X, BigInteger Y) ToAffine(Point p)
		{
			BigInteger zInverse = BigInteger.ModPow(p.Z, P - 2, P);
			BigInteger zz = Mod(zInverse * zInverse);
			return (Mod(p.X * zz), Mod(p.Y * zz * zInverse));
		}

		public bool IsOnCurve(BigInteger x, BigInteger y) => Mod(y * y) == Mod(x * x * x + A * x + B);

		// ECDSA verification (FIPS 186-4): the hash truncated to the group order's bits, u1 G + u2 Q compared with r
		public bool VerifySignature(BigInteger qx, BigInteger qy, BigInteger r, BigInteger s, ReadOnlySpan<byte> digest)
		{
			if (r.Sign <= 0 || r >= N || s.Sign <= 0 || s >= N || !IsOnCurve(qx, qy))
				return false;
			BigInteger e = new BigInteger(digest, isUnsigned: true, isBigEndian: true);
			int excess = digest.Length * 8 - (int)N.GetBitLength();
			if (excess > 0)
				e >>= excess;
			BigInteger w = BigInteger.ModPow(s, N - 2, N);
			BigInteger u1 = e * w % N;
			BigInteger u2 = r * w % N;
			Point g = new Point(Gx, Gy, 1);
			Point q = new Point(qx, qy, 1);
			Point sum = Add(Multiply(g, u1), Multiply(q, u2));
			if (sum.IsInfinity)
				return false;
			(BigInteger x, _) = ToAffine(sum);
			return x % N == r;
		}
	}

	public sealed class EcPublicKey : PublicKey
	{
		public readonly EllipticCurve Curve;
		public readonly BigInteger X;
		public readonly BigInteger Y;

		public EcPublicKey(EllipticCurve curve, BigInteger x, BigInteger y)
		{
			Curve = curve;
			X = x;
			Y = y;
		}

		public override string Kind => "ec";

		// From the uncompressed point encoding 04 || X || Y
		public static EcPublicKey FromPoint(EllipticCurve curve, ReadOnlySpan<byte> point)
		{
			if (point.Length != 1 + 2 * curve.ByteLength || point[0] != 4)
				return null;
			BigInteger x = new BigInteger(point.Slice(1, curve.ByteLength), isUnsigned: true, isBigEndian: true);
			BigInteger y = new BigInteger(point.Slice(1 + curve.ByteLength), isUnsigned: true, isBigEndian: true);
			return curve.IsOnCurve(x, y) ? new EcPublicKey(curve, x, y) : null;
		}

		// The signature is a DER SEQUENCE of two INTEGERs, r and s
		public override bool Verify(string scheme, HashFunction hash, ReadOnlySpan<byte> data, ReadOnlySpan<byte> signature)
		{
			if (scheme != "ecdsa")
				return false;
			Asn1Reader reader = new Asn1Reader(signature);
			if (!reader.TryReadSequence(out Asn1Reader body) || !body.TryReadInteger(out BigInteger r) || !body.TryReadInteger(out BigInteger s))
				return false;
			return Curve.VerifySignature(X, Y, r, s, hash.Hash(data));
		}
	}
}
