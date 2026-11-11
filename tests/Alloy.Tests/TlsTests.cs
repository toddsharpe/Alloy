using System.IO;
using System.Net.Sockets;
using System.Text;
using Alloy.Sdk.Security.Crypto;
using Alloy.Sdk.Security.Tls;

namespace Alloy.Tests;

public class TlsTests
{
	private static TrustStore Roots() => TrustStore.FromPem(ImageTestsFixtures.Text("ca-bundle.crt"));

	[Fact]
	public void ParsesCertificates()
	{
		Certificate leaf = CryptoTests.Load("letsencrypt.org.0.der");
		Assert.Equal("letsencrypt.org", leaf.CommonName);
		Assert.Contains("www.letsencrypt.org", leaf.DnsNames);
		Assert.Contains("lencr.org", leaf.DnsNames);
		Assert.Equal(new DateTime(2026, 11, 3, 13, 44, 34, DateTimeKind.Utc), leaf.NotBefore);
		Assert.Equal(new DateTime(2027, 2, 1, 13, 44, 33, DateTimeKind.Utc), leaf.NotAfter);
		Assert.Equal("1.2.840.10045.4.3.3", leaf.SignatureAlgorithm);
		Assert.False(leaf.IsCa);
		Assert.Equal("C=US, O=Let's Encrypt, CN=YE2", leaf.IssuerName);
		Certificate intermediate = CryptoTests.Load("letsencrypt.org.1.der");
		Assert.True(intermediate.IsCa);
		Assert.Equal(0, intermediate.PathLength);
		Assert.True(intermediate.Subject.AsSpan().SequenceEqual(leaf.Issuer));
		Certificate root = CryptoTests.Load("letsencrypt.org.4.der");
		Assert.True(root.IsSelfIssued);
		Assert.Equal("ISRG Root X1", root.CommonName);
		Assert.True(leaf.MatchesHost("LetsEncrypt.org"));
		Assert.True(leaf.MatchesHost("www.letsencrypt.org"));
		Assert.False(leaf.MatchesHost("evil.letsencrypt.org.example"));
	}

	[Fact]
	public void LoadsTheRootBundle()
	{
		TrustStore roots = Roots();
		Assert.True(roots.Roots.Count > 100, $"only {roots.Roots.Count} roots");
		Assert.Contains(roots.Roots, r => r.CommonName == "ISRG Root X1");
		Assert.Contains(roots.Roots, r => r.CommonName == "ISRG Root X2");
		Assert.Contains(roots.Roots, r => r.CommonName == "DigiCert Global Root G2");
		int unsupported = 0;
		foreach (Certificate root in roots.Roots)
		{
			if (!Certificate.TryDescribeSignature(root.SignatureAlgorithm, out _, out _))
				unsupported++;
			else
				Assert.True(root.IsSignedBy(root), root.SubjectName);
		}
		// Old roots self-sign with SHA-1 or RSA-PSS, which only matters for self-signatures, which are never checked
		Assert.True(unsupported < roots.Roots.Count / 4, $"{unsupported} roots use signature algorithms we cannot check");
	}

	[Fact]
	public void ValidatesChainsAgainstTheBundle()
	{
		TrustStore roots = Roots();
		List<Certificate> chain = new List<Certificate>();
		for (int i = 0; i < 5; i++)
			chain.Add(CryptoTests.Load($"letsencrypt.org.{i}.der"));
		DateTime now = new DateTime(2026, 12, 1, 0, 0, 0, DateTimeKind.Utc);
		string links = string.Join("; ", Enumerable.Range(0, 4).Select(i => $"{i}: issued={chain[i].IsIssuedBy(chain[i + 1])} signed={chain[i].IsSignedBy(chain[i + 1])} ca={chain[i + 1].IsCa} path={chain[i + 1].PathLength}"));
		Assert.True(ChainValidator.Validate(chain, roots, "letsencrypt.org", now, out string? error), error + " | " + links);
		Assert.True(ChainValidator.Validate(chain.GetRange(0, 3), roots, "www.letsencrypt.org", now, out error), error);
		Assert.False(ChainValidator.Validate(chain, roots, "example.com", now, out error));
		Assert.Contains("not example.com", error);
		Assert.False(ChainValidator.Validate(chain, roots, "letsencrypt.org", new DateTime(2030, 1, 1, 0, 0, 0, DateTimeKind.Utc), out error));
		Assert.Contains("expired", error);
		Assert.False(ChainValidator.Validate(chain.GetRange(0, 1), roots, "letsencrypt.org", now, out error));
		Assert.Contains("no trusted issuer", error);
		Assert.False(ChainValidator.Validate(chain, new TrustStore(), "letsencrypt.org", now, out error));
		List<Certificate> wrongIssuer = new List<Certificate> { CryptoTests.Load("www.cloudflare.com.0.der"), chain[1] };
		Assert.False(ChainValidator.Validate(wrongIssuer, roots, "www.cloudflare.com", now, out error));
	}

	// Talks TLS 1.3 to a real server when the network allows; passes quietly when it does not
	[Theory]
	[InlineData("letsencrypt.org")]
	[InlineData("www.cloudflare.com")]
	[InlineData("example.com")]
	public void HandshakesWithRealServers(string host)
	{
		TcpClient tcp;
		try
		{
			tcp = new TcpClient();
			tcp.ReceiveTimeout = 10000;
			tcp.SendTimeout = 10000;
			if (!tcp.ConnectAsync(host, 443).Wait(10000))
				return;
		}
		catch (Exception)
		{
			return;
		}
		using (tcp)
		{
			NetworkStream network = tcp.GetStream();
			TlsClient tls = new TlsClient(network, host, Roots(), DateTime.UtcNow);
			tls.Handshake();
			Assert.NotEmpty(tls.PeerChain);
			Assert.True(tls.PeerChain[0].MatchesHost(host));
			byte[] request = Encoding.ASCII.GetBytes($"HEAD / HTTP/1.1\r\nHost: {host}\r\nConnection: close\r\n\r\n");
			tls.Write(request);
			MemoryStream response = new MemoryStream();
			byte[] buffer = new byte[4096];
			int read;
			while ((read = tls.Read(buffer, 0, buffer.Length)) > 0)
			{
				response.Write(buffer, 0, read);
				if (response.Length > 64 * 1024)
					break;
			}
			string text = Encoding.ASCII.GetString(response.ToArray());
			Assert.StartsWith("HTTP/1.1 ", text);
		}
	}
}
