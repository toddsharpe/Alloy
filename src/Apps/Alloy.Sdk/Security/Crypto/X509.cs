using System;
using System.Collections.Generic;
using System.Numerics;
using System.Text;

namespace Alloy.Sdk.Security.Crypto
{
	// The parts of an X.509 v3 certificate a TLS client checks: names, validity, the public key, constraints and the signature over the TBS bytes
	public sealed class Certificate
	{
		public byte[] Raw;
		public byte[] TbsCertificate;
		public byte[] Subject;
		public byte[] Issuer;
		public string SubjectName;
		public string IssuerName;
		public string CommonName;
		public DateTime NotBefore;
		public DateTime NotAfter;
		public PublicKey Key;
		public string SignatureAlgorithm;
		public byte[] Signature;
		public readonly List<string> DnsNames = new List<string>();
		public bool IsCa;
		public int PathLength = -1;
		public bool HasBasicConstraints;
		public byte[] SubjectKeyId;
		public byte[] AuthorityKeyId;

		public bool IsSelfIssued => NamesMatch(Subject, SubjectName, Issuer, IssuerName);

		// Issuer and subject names compare by their DER, or by their attributes when the string types differ between the two certificates
		public static bool NamesMatch(byte[] a, string aName, byte[] b, string bName) =>
			a.AsSpan().SequenceEqual(b) || (aName.Length > 0 && string.Equals(aName, bName, StringComparison.OrdinalIgnoreCase));

		public bool IsIssuedBy(Certificate issuer) => NamesMatch(Issuer, IssuerName, issuer.Subject, issuer.SubjectName);

		public override string ToString() => SubjectName;

		// Parses DER; null with a reason when the structure is not one this reader handles
		public static Certificate Parse(ReadOnlySpan<byte> der, out string error)
		{
			error = null;
			Certificate cert = new Certificate { Raw = der.ToArray() };
			Asn1Reader outer = new Asn1Reader(der);
			if (!outer.TryReadSequence(out Asn1Reader top))
				return Fail(out error, "not a certificate");
			int tbsStart = top.Position;
			if (!top.TryReadElement(out byte tbsTag, out ReadOnlySpan<byte> tbsContents, out ReadOnlySpan<byte> tbsElement) || tbsTag != Asn1Reader.Sequence)
				return Fail(out error, "no TBSCertificate");
			cert.TbsCertificate = tbsElement.ToArray();
			if (!top.TryReadSequence(out Asn1Reader signatureAlgorithm) || !signatureAlgorithm.TryReadOid(out string outerAlgorithm))
				return Fail(out error, "no signature algorithm");
			cert.SignatureAlgorithm = outerAlgorithm;
			if (!top.TryReadBitString(out ReadOnlySpan<byte> signature))
				return Fail(out error, "no signature");
			cert.Signature = signature.ToArray();

			Asn1Reader tbs = new Asn1Reader(tbsContents);
			if (tbs.TryReadContext(0, out Asn1Reader versionReader))
				versionReader.TryReadInteger(out _);
			if (!tbs.TryReadInteger(out _))
				return Fail(out error, "no serial number");
			if (!tbs.TryReadSequence(out Asn1Reader innerAlgorithm) || !innerAlgorithm.TryReadOid(out _))
				return Fail(out error, "no inner signature algorithm");
			if (!tbs.TryReadElement(out byte issuerTag, out ReadOnlySpan<byte> issuerContents, out ReadOnlySpan<byte> issuerElement) || issuerTag != Asn1Reader.Sequence)
				return Fail(out error, "no issuer");
			cert.Issuer = issuerElement.ToArray();
			cert.IssuerName = ReadName(issuerContents, out _);
			if (!tbs.TryReadSequence(out Asn1Reader validity) || !validity.TryReadTime(out cert.NotBefore) || !validity.TryReadTime(out cert.NotAfter))
				return Fail(out error, "no validity");
			if (!tbs.TryReadElement(out byte subjectTag, out ReadOnlySpan<byte> subjectContents, out ReadOnlySpan<byte> subjectElement) || subjectTag != Asn1Reader.Sequence)
				return Fail(out error, "no subject");
			cert.Subject = subjectElement.ToArray();
			cert.SubjectName = ReadName(subjectContents, out cert.CommonName);
			if (!tbs.TryReadSequence(out Asn1Reader spki))
				return Fail(out error, "no public key");
			cert.Key = ReadPublicKey(spki, out string keyError);
			if (cert.Key == null)
				return Fail(out error, keyError);
			while (!tbs.AtEnd)
			{
				byte next = tbs.PeekTag();
				if (next == 0xA3)
				{
					tbs.TryReadContext(3, out Asn1Reader extensionsWrapper);
					if (extensionsWrapper.TryReadSequence(out Asn1Reader extensions))
						ReadExtensions(cert, extensions);
				}
				else
					tbs.Skip();
			}
			return cert;
		}

		private static Certificate Fail(out string error, string reason)
		{
			error = reason;
			return null;
		}

		// A distinguished name as "CN=x, O=y", and its common name
		private static string ReadName(ReadOnlySpan<byte> contents, out string commonName)
		{
			commonName = null;
			StringBuilder text = new StringBuilder();
			Asn1Reader rdns = new Asn1Reader(contents);
			while (rdns.TryReadSet(out Asn1Reader set))
			{
				while (set.TryReadSequence(out Asn1Reader attribute))
				{
					if (!attribute.TryReadOid(out string oid) || !attribute.TryReadString(out string value))
						continue;
					string label = oid switch
					{
						"2.5.4.3" => "CN",
						"2.5.4.6" => "C",
						"2.5.4.7" => "L",
						"2.5.4.8" => "ST",
						"2.5.4.10" => "O",
						"2.5.4.11" => "OU",
						_ => oid,
					};
					if (label == "CN" && commonName == null)
						commonName = value;
					if (text.Length > 0)
						text.Append(", ");
					text.Append(label).Append('=').Append(value);
				}
			}
			return text.ToString();
		}

		private static PublicKey ReadPublicKey(Asn1Reader spki, out string error)
		{
			error = null;
			if (!spki.TryReadSequence(out Asn1Reader algorithm) || !algorithm.TryReadOid(out string oid))
			{
				error = "bad public key algorithm";
				return null;
			}
			if (oid == "1.2.840.113549.1.1.1")
			{
				if (!spki.TryReadBitString(out ReadOnlySpan<byte> keyBits))
				{
					error = "bad RSA key";
					return null;
				}
				Asn1Reader rsa = new Asn1Reader(keyBits);
				if (!rsa.TryReadSequence(out Asn1Reader parts) || !parts.TryReadInteger(out BigInteger modulus) || !parts.TryReadInteger(out BigInteger exponent))
				{
					error = "bad RSA key";
					return null;
				}
				return new RsaPublicKey(modulus, exponent);
			}
			if (oid == "1.2.840.10045.2.1")
			{
				if (!algorithm.TryReadOid(out string curveOid))
				{
					error = "EC key without a named curve";
					return null;
				}
				EllipticCurve curve = curveOid switch
				{
					"1.2.840.10045.3.1.7" => EllipticCurve.P256,
					"1.3.132.0.34" => EllipticCurve.P384,
					_ => null,
				};
				if (curve == null)
				{
					error = "unsupported curve " + curveOid;
					return null;
				}
				if (!spki.TryReadBitString(out ReadOnlySpan<byte> point))
				{
					error = "bad EC key";
					return null;
				}
				EcPublicKey key = EcPublicKey.FromPoint(curve, point);
				if (key == null)
					error = "EC point not on its curve";
				return key;
			}
			error = "unsupported public key algorithm " + oid;
			return null;
		}

		private static void ReadExtensions(Certificate cert, Asn1Reader extensions)
		{
			while (extensions.TryReadSequence(out Asn1Reader extension))
			{
				if (!extension.TryReadOid(out string oid))
					continue;
				if (extension.PeekTag() == Asn1Reader.Boolean)
					extension.TryReadBoolean(out _);
				if (!extension.TryReadOctetString(out ReadOnlySpan<byte> value))
					continue;
				Asn1Reader inner = new Asn1Reader(value);
				switch (oid)
				{
					case "2.5.29.17":
						if (inner.TryReadSequence(out Asn1Reader names))
						{
							while (!names.AtEnd)
							{
								byte tag = names.PeekTag();
								if (!names.TryReadElement(out _, out ReadOnlySpan<byte> name, out _))
									break;
								if (tag == 0x82)
									cert.DnsNames.Add(Encoding.ASCII.GetString(name).ToLowerInvariant());
							}
						}
						break;
					case "2.5.29.19":
						cert.HasBasicConstraints = true;
						if (inner.TryReadSequence(out Asn1Reader constraints))
						{
							if (constraints.PeekTag() == Asn1Reader.Boolean && constraints.TryReadBoolean(out bool ca))
								cert.IsCa = ca;
							if (constraints.PeekTag() == Asn1Reader.Integer && constraints.TryReadInteger(out BigInteger pathLength))
								cert.PathLength = (int)pathLength;
						}
						break;
					case "2.5.29.14":
						if (inner.TryReadOctetString(out ReadOnlySpan<byte> ski))
							cert.SubjectKeyId = ski.ToArray();
						break;
					case "2.5.29.35":
						if (inner.TryReadSequence(out Asn1Reader aki) && aki.PeekTag() == 0x80 && aki.TryReadElement(out _, out ReadOnlySpan<byte> keyId, out _))
							cert.AuthorityKeyId = keyId.ToArray();
						break;
				}
			}
		}

		// The scheme and hash a signature algorithm OID stands for
		public static bool TryDescribeSignature(string oid, out string scheme, out HashFunction hash)
		{
			switch (oid)
			{
				case "1.2.840.113549.1.1.11":
					scheme = "rsa_pkcs1";
					hash = HashFunction.Sha256;
					return true;
				case "1.2.840.113549.1.1.12":
					scheme = "rsa_pkcs1";
					hash = HashFunction.Sha384;
					return true;
				case "1.2.840.113549.1.1.13":
					scheme = "rsa_pkcs1";
					hash = HashFunction.Sha512;
					return true;
				case "1.2.840.10045.4.3.2":
					scheme = "ecdsa";
					hash = HashFunction.Sha256;
					return true;
				case "1.2.840.10045.4.3.3":
					scheme = "ecdsa";
					hash = HashFunction.Sha384;
					return true;
				case "1.2.840.10045.4.3.4":
					scheme = "ecdsa";
					hash = HashFunction.Sha512;
					return true;
				default:
					scheme = null;
					hash = null;
					return false;
			}
		}

		// Whether this certificate's signature was made by the issuer's key
		public bool IsSignedBy(Certificate issuer)
		{
			if (!TryDescribeSignature(SignatureAlgorithm, out string scheme, out HashFunction hash))
				return false;
			return issuer.Key.Verify(scheme, hash, TbsCertificate, Signature);
		}

		// RFC 6125 host matching against the subject alternative names, one wildcard label at the left allowed
		public bool MatchesHost(string host)
		{
			host = host.ToLowerInvariant().TrimEnd('.');
			IEnumerable<string> names = DnsNames.Count > 0 ? DnsNames : (CommonName != null ? new[] { CommonName.ToLowerInvariant() } : Array.Empty<string>());
			foreach (string name in names)
			{
				if (name == host)
					return true;
				if (name.StartsWith("*.", StringComparison.Ordinal))
				{
					int dot = host.IndexOf('.');
					if (dot > 0 && host.Substring(dot + 1) == name.Substring(2) && host.IndexOf('.', 0, dot) < 0)
						return true;
				}
			}
			return false;
		}
	}

	// The root certificates trusted, read from a PEM bundle
	public sealed class TrustStore
	{
		public readonly List<Certificate> Roots = new List<Certificate>();

		public static TrustStore FromPem(string pem)
		{
			TrustStore store = new TrustStore();
			int at = 0;
			while (true)
			{
				int begin = pem.IndexOf("-----BEGIN CERTIFICATE-----", at, StringComparison.Ordinal);
				if (begin < 0)
					break;
				begin += "-----BEGIN CERTIFICATE-----".Length;
				int end = pem.IndexOf("-----END CERTIFICATE-----", begin, StringComparison.Ordinal);
				if (end < 0)
					break;
				at = end + "-----END CERTIFICATE-----".Length;
				string base64 = pem.Substring(begin, end - begin).Replace("\r", "").Replace("\n", "").Replace(" ", "");
				byte[] der;
				try
				{
					der = Convert.FromBase64String(base64);
				}
				catch (FormatException)
				{
					continue;
				}
				Certificate cert = Certificate.Parse(der, out _);
				if (cert != null)
					store.Roots.Add(cert);
			}
			return store;
		}

		// The root a certificate names as its issuer, verified to have signed it; several roots can share a name
		public Certificate FindIssuer(Certificate cert)
		{
			foreach (Certificate root in Roots)
			{
				if (cert.IsIssuedBy(root) && cert.IsSignedBy(root))
					return root;
			}
			return null;
		}

		public bool HasSubject(Certificate cert)
		{
			foreach (Certificate root in Roots)
			{
				if (cert.IsIssuedBy(root))
					return true;
			}
			return false;
		}

		public bool Contains(Certificate cert)
		{
			foreach (Certificate root in Roots)
			{
				if (root.Raw.AsSpan().SequenceEqual(cert.Raw))
					return true;
			}
			return false;
		}
	}

	// Builds and checks a path from a server's certificate to a trusted root: issuer names, signatures, validity, CA flags, the host name
	public static class ChainValidator
	{
		public const int MaxDepth = 8;

		// A date as yyyy-mm-dd without the date formatter, which would drag time zones and the registry into the image
		private static string Date(DateTime d) => $"{d.Year}-{d.Month:00}-{d.Day:00}";

		public static bool Validate(IReadOnlyList<Certificate> presented, TrustStore roots, string host, DateTime now, out string error)
		{
			error = null;
			if (presented.Count == 0)
			{
				error = "no certificate";
				return false;
			}
			Certificate leaf = presented[0];
			if (!leaf.MatchesHost(host))
			{
				error = $"the certificate is for {(leaf.DnsNames.Count > 0 ? string.Join(", ", leaf.DnsNames) : leaf.CommonName)}, not {host}";
				return false;
			}
			Certificate current = leaf;
			HashSet<Certificate> used = new HashSet<Certificate>();
			for (int depth = 0; depth < MaxDepth; depth++)
			{
				if (now < current.NotBefore || now > current.NotAfter)
				{
					error = $"{current.CommonName ?? current.SubjectName} is {(now < current.NotBefore ? "not yet valid" : "expired")} (valid {Date(current.NotBefore)} to {Date(current.NotAfter)})";
					return false;
				}
				if (depth > 0 && (!current.IsCa || (current.PathLength >= 0 && depth - 1 > current.PathLength)))
				{
					error = $"{current.CommonName ?? current.SubjectName} is not a certificate authority";
					return false;
				}
				// A trusted root with the issuer's name ends the path, whether or not the server also sent a cross-signed version
				if (roots.FindIssuer(current) != null)
					return true;
				if (roots.Contains(current))
					return true;
				bool trusted = roots.HasSubject(current);
				Certificate issuer = null;
				foreach (Certificate candidate in presented)
				{
					if (candidate == current || used.Contains(candidate) || !current.IsIssuedBy(candidate))
						continue;
					if (current.IsSignedBy(candidate))
					{
						issuer = candidate;
						break;
					}
				}
				if (issuer == null)
				{
					error = trusted ? $"the signature from {current.IssuerName} does not verify" : $"no trusted issuer for {current.CommonName ?? current.SubjectName} (issued by {current.IssuerName})";
					return false;
				}
				used.Add(current);
				current = issuer;
			}
			error = "the certificate chain is too long";
			return false;
		}
	}
}
