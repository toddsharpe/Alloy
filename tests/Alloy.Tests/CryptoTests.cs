using System.Numerics;
using Alloy.Sdk.Security.Crypto;

namespace Alloy.Tests;

public class CryptoTests
{
	private static byte[] Hex(string hex)
	{
		hex = hex.Replace(" ", "").Replace("\n", "");
		byte[] bytes = new byte[hex.Length / 2];
		for (int i = 0; i < bytes.Length; i++)
			bytes[i] = Convert.ToByte(hex.Substring(i * 2, 2), 16);
		return bytes;
	}

	private static string ToHex(ReadOnlySpan<byte> bytes) => Convert.ToHexString(bytes).ToLowerInvariant();

	[Fact]
	public void HashesTheStandardVectors()
	{
		Assert.Equal("ba7816bf8f01cfea414140de5dae2223b00361a396177a9cb410ff61f20015ad", ToHex(HashFunction.Sha256.Hash("abc"u8)));
		Assert.Equal("e3b0c44298fc1c149afbf4c8996fb92427ae41e4649b934ca495991b7852b855", ToHex(HashFunction.Sha256.Hash(Array.Empty<byte>())));
		Assert.Equal("248d6a61d20638b8e5c026930c3e6039a33ce45964ff2167f6ecedd419db06c1", ToHex(HashFunction.Sha256.Hash("abcdbcdecdefdefgefghfghighijhijkijkljklmklmnlmnomnopnopq"u8)));
		Assert.Equal("cb00753f45a35e8bb5a03d699ac65007272c32ab0eded1631a8b605a43ff5bed8086072ba1e7cc2358baeca134c825a7", ToHex(HashFunction.Sha384.Hash("abc"u8)));
		Assert.Equal("ddaf35a193617abacc417349ae20413112e6fa4e89a97ea20a9eeee64b55d39a2192992a274fc1a836ba3c23a3feebbd454d4423643ce80e2a9ac94fa54ca49f", ToHex(HashFunction.Sha512.Hash("abc"u8)));
		byte[] million = new byte[1_000_000];
		Array.Fill(million, (byte)'a');
		Assert.Equal("cdc76e5c9914fb9281a1c7e284d73e67f1809a48a497200e046d39ccc7112cd0", ToHex(HashFunction.Sha256.Hash(million)));
	}

	[Fact]
	public void ComputesHmacAndHkdfVectors()
	{
		// RFC 4231 test case 2
		Assert.Equal("5bdcc146bf60754e6a042426089575c75a003f089d2739839dec58b964ec3843", ToHex(Hmac.Compute(HashFunction.Sha256, "Jefe"u8, "what do ya want for nothing?"u8)));
		Assert.Equal("af45d2e376484031617f78d2b58a6b1b9c7ef464f5a01b47e42ec3736322445e8e2240ca5e69e2c78b3239ecfab21649", ToHex(Hmac.Compute(HashFunction.Sha384, "Jefe"u8, "what do ya want for nothing?"u8)));
		// RFC 5869 test case 1
		byte[] prk = Hkdf.Extract(HashFunction.Sha256, Hex("000102030405060708090a0b0c"), Hex("0b0b0b0b0b0b0b0b0b0b0b0b0b0b0b0b0b0b0b0b0b0b"));
		Assert.Equal("077709362c2e32df0ddc3f0dc47bba6390b6c73bb50f9c3122ec844ad7c2b3e5", ToHex(prk));
		Assert.Equal("3cb25f25faacd57a90434f64d0362f2a2d2d0a90cf1a5a4c5db02d56ecc4c5bf34007208d5b887185865", ToHex(Hkdf.Expand(HashFunction.Sha256, prk, Hex("f0f1f2f3f4f5f6f7f8f9"), 42)));
	}

	[Fact]
	public void DerivesTls13SecretsLikeTheRfcTrace()
	{
		// RFC 8448 section 3: the simple 1-RTT handshake's early and handshake secrets
		byte[] earlySecret = Hkdf.Extract(HashFunction.Sha256, Array.Empty<byte>(), new byte[32]);
		Assert.Equal("33ad0a1c607ec03b09e6cd9893680ce210adf300aa1f2660e1b22e10f170f92a", ToHex(earlySecret));
		byte[] derived = Hkdf.DeriveSecret(HashFunction.Sha256, earlySecret, "derived", Array.Empty<byte>());
		Assert.Equal("6f2615a108c702c5678f54fc9dbab69716c076189c48250cebeac3576c3611ba", ToHex(derived));
		byte[] shared = Hex("8bd4054fb55b9d63fdfbacf9f04b9f0d35e6d63f537563efd46272900f89492d");
		byte[] handshakeSecret = Hkdf.Extract(HashFunction.Sha256, derived, shared);
		Assert.Equal("1dc826e93606aa6fdc0aadc12f741b01046aa6b99f691ed221a9f0ca043fbeac", ToHex(handshakeSecret));
	}

	[Fact]
	public void AgreesOnX25519Vectors()
	{
		// RFC 7748 section 5.2 and 6.1
		Assert.Equal("c3da55379de9c6908e94ea4df28d084f32eccf03491c71f754b4075577a28552",
			ToHex(X25519.ScalarMultiply(Hex("a546e36bf0527c9d3b16154b82465edd62144c0ac1fc5a18506a2244ba449ac4"), Hex("e6db6867583030db3594c1a424b15f7c726624ec26b3353b10a903a6d0ab1c4c"))));
		byte[] alicePrivate = Hex("77076d0a7318a57d3c16c17251b26645df4c2f87ebc0992ab177fba51db92c2a");
		byte[] bobPrivate = Hex("5dab087e624a8a4b79e17f8b83800ee66f3bb1292618b6fd1c2f8b27ff88e0eb");
		byte[] alicePublic = X25519.PublicKey(alicePrivate);
		byte[] bobPublic = X25519.PublicKey(bobPrivate);
		Assert.Equal("8520f0098930a754748b7ddcb43ef75a0dbf3a0d26381af4eba4a98eaa9b4e6a", ToHex(alicePublic));
		Assert.Equal("de9edb7d7b7dc1b4d35b61c2ece435373f8343c85b78674dadfc7e146f882b4f", ToHex(bobPublic));
		Assert.Equal("4a5d9d5ba4ce2de1728e3bf480350f25e07e21c947d19e3376f09b3c1e161742", ToHex(X25519.SharedSecret(alicePrivate, bobPublic)));
		Assert.Equal(ToHex(X25519.SharedSecret(alicePrivate, bobPublic)), ToHex(X25519.SharedSecret(bobPrivate, alicePublic)));
	}

	[Fact]
	public void EncryptsAesBlocksAndGcm()
	{
		// FIPS 197 appendix C.1
		Aes aes = new Aes(Hex("000102030405060708090a0b0c0d0e0f"));
		byte[] block = new byte[16];
		aes.Encrypt(Hex("00112233445566778899aabbccddeeff"), block);
		Assert.Equal("69c4e0d86a7b0430d8cdb78070b4c55a", ToHex(block));
		Aes aes256 = new Aes(Hex("000102030405060708090a0b0c0d0e0f101112131415161718191a1b1c1d1e1f"));
		aes256.Encrypt(Hex("00112233445566778899aabbccddeeff"), block);
		Assert.Equal("8ea2b7ca516745bfeafc49904b496089", ToHex(block));
		// GCM test cases 1, 2 and 4 from the GCM specification
		AesGcm gcm = new AesGcm(new byte[16]);
		Assert.Equal("58e2fccefa7e3061367f1d57a4e7455a", ToHex(gcm.Encrypt(new byte[12], Array.Empty<byte>(), Array.Empty<byte>())));
		Assert.Equal("0388dace60b6a392f328c2b971b2fe78ab6e47d42cec13bdf53a67b21257bddf", ToHex(gcm.Encrypt(new byte[12], new byte[16], Array.Empty<byte>())));
		AesGcm gcm4 = new AesGcm(Hex("feffe9928665731c6d6a8f9467308308"));
		byte[] plaintext = Hex("d9313225f88406e5a55909c5aff5269a86a7a9531534f7da2e4c303d8a318a721c3c0c95956809532fcf0e2449a6b525b16aedf5aa0de657ba637b39");
		byte[] aad = Hex("feedfacedeadbeeffeedfacedeadbeefabaddad2");
		byte[] sealed_ = gcm4.Encrypt(Hex("cafebabefacedbaddecaf888"), plaintext, aad);
		Assert.Equal("42831ec2217774244b7221b784d0d49ce3aa212f2c02a4e035c17e2329aca12e21d514b25466931c7d8f6a5aac84aa051ba30b396a0aac973d58e091", ToHex(sealed_.AsSpan(0, plaintext.Length)));
		Assert.Equal("5bc94fbc3221a5db94fae95ae7121a47", ToHex(sealed_.AsSpan(plaintext.Length)));
		Assert.Equal(ToHex(plaintext), ToHex(gcm4.Decrypt(Hex("cafebabefacedbaddecaf888"), sealed_, aad)!));
		sealed_[3] ^= 1;
		Assert.Null(gcm4.Decrypt(Hex("cafebabefacedbaddecaf888"), sealed_, aad));
	}

	[Fact]
	public void SealsWithChaCha20Poly1305()
	{
		// RFC 8439 section 2.8.2
		byte[] key = Hex("808182838485868788898a8b8c8d8e8f909192939495969798999a9b9c9d9e9f");
		byte[] nonce = Hex("070000004041424344454647");
		byte[] aad = Hex("50515253c0c1c2c3c4c5c6c7");
		byte[] plaintext = System.Text.Encoding.ASCII.GetBytes("Ladies and Gentlemen of the class of '99: If I could offer you only one tip for the future, sunscreen would be it.");
		ChaCha20Poly1305 aead = new ChaCha20Poly1305(key);
		byte[] sealed_ = aead.Encrypt(nonce, plaintext, aad);
		Assert.Equal("d31a8d34648e60db7b86afbc53ef7ec2a4aded51296e08fea9e2b5a736ee62d63dbea45e8ca9671282fafb69da92728b1a71de0a9e060b2905d6a5b67ecd3b3692ddbd7f2d778b8c9803aee328091b58fab324e4fad675945585808b4831d7bc3ff4def08e4b7a9de576d26586cec64b6116", ToHex(sealed_.AsSpan(0, plaintext.Length)));
		Assert.Equal("1ae10b594f09e26a7e902ecbd0600691", ToHex(sealed_.AsSpan(plaintext.Length)));
		Assert.Equal(ToHex(plaintext), ToHex(aead.Decrypt(nonce, sealed_, aad)!));
		// RFC 8439 section 2.5.2 Poly1305
		Assert.Equal("a8061dc1305136c6c22b8baf0c0127a9", ToHex(ChaCha20Poly1305.Poly1305(Hex("85d6be7857556d337f4452fe42d506a80103808afb0db2fd4abff6af4149f51b"), "Cryptographic Forum Research Group"u8)));
	}

	[Fact]
	public void VerifiesRsaAndEcdsaSignaturesFromRealCertificates()
	{
		Certificate root = Load("letsencrypt.org.4.der");
		Certificate crossSigned = Load("letsencrypt.org.3.der");
		Certificate x2Intermediate = Load("letsencrypt.org.2.der");
		Certificate issuingCa = Load("letsencrypt.org.1.der");
		Certificate leaf = Load("letsencrypt.org.0.der");
		Assert.Equal("rsa", root.Key.Kind);
		Assert.Equal(4096, ((RsaPublicKey)root.Key).Bits);
		Assert.True(root.IsSignedBy(root));
		Assert.True(crossSigned.IsSignedBy(root));
		Assert.False(crossSigned.IsSignedBy(crossSigned));
		Assert.Equal("ec", issuingCa.Key.Kind);
		Assert.True(x2Intermediate.IsSignedBy(crossSigned));
		Assert.True(issuingCa.IsSignedBy(x2Intermediate));
		Assert.True(leaf.IsSignedBy(issuingCa));
		Assert.False(leaf.IsSignedBy(root));
		byte[] tampered = (byte[])leaf.TbsCertificate.Clone();
		tampered[tampered.Length / 2] ^= 1;
		Assert.False(issuingCa.Key.Verify("ecdsa", HashFunction.Sha384, tampered, leaf.Signature));
	}

	[Fact]
	public void VerifiesRsaPss()
	{
		// A PSS signature made with .NET on the host over "alloy" with a 2048-bit key, salt length 32
		RsaPublicKey key = new RsaPublicKey(
			BigInteger.Parse("0" + PssModulus, System.Globalization.NumberStyles.HexNumber), 65537);
		Assert.True(key.Verify("rsa_pss", HashFunction.Sha256, "alloy"u8, Hex(PssSignature)));
		Assert.False(key.Verify("rsa_pss", HashFunction.Sha256, "alloy!"u8, Hex(PssSignature)));
		Assert.False(key.Verify("rsa_pkcs1", HashFunction.Sha256, "alloy"u8, Hex(PssSignature)));
	}

	private const string PssModulus = "BD66F2E48E69D422CFC81A28FE2979DB707AADF55FD685317AAC98D658180153BB81E73576FB18540EDCBEE87200922B8D98512A9EC90C9F1B73A4A715557EE3D7E9AB20009BBB86D782DB5AFB936D712D8075A0D9F68DF4DFCF6F069142E8154B92D17E529B9F46CEBDB13D266158B94AC9E6DBE87D49BA6DF41B837164BBC0BC6D0A4853409D1BDF155360A558A5580BEEA94BC3454D38D3F6A534B050A4535681FB346B9A10F636B0A00861DB3394B90B892A384C1A97B811FF52247CD2DB9CC2EC34BDE641D2DB7CA839AD35A62CB8FB3ECE32F1CB16EF680405459E188BA8CFC1374E4D0CC40EA969C17F325084C59B23CB726FF33ECA050A89065E2AC1";
	private const string PssSignature = "842F8B25FF8BE28F052E71A4E6A99A37D06F558A1E448C7913F78C26304F52AF4934ADAB8A8510D423FA2AAD91C49E873B42306D6A5C9D004EC9D3AA11A6FF25B1C70438E7794FEB8B547FA2F9399FD0DC6A9B3E223742C0AB411B807D5509BDD7A225B90E795B89B1FF1F80816649A5501A2778F3B3BA892F42FEC12D223881B5F75876842B1ADADFBED370C29FBCD905DD6E497FD9C82E8A5A8FD15B9339FA0B1793272AA2F04D26B7607D0696073D8F579F4126C22067F5B49D0D3795C099E4045B25A56739B00AE64615102E0A787B8666A33CE8F9DE56CEF8C76719020C1572B09B112A9DA333F15E92C180E7A149378E75DF041E563F281C838BA70388";

	internal static Certificate Load(string name)
	{
		Certificate cert = Certificate.Parse(ImageTestsFixtures.Bytes(name), out string? error);
		Assert.True(cert != null, error);
		return cert!;
	}
}

internal static class ImageTestsFixtures
{
	public static byte[] Bytes(string name)
	{
		using var stream = typeof(ImageTestsFixtures).Assembly.GetManifestResourceStream("Alloy.Tests.Fixtures." + name)!;
		byte[] data = new byte[stream.Length];
		int read = 0;
		while (read < data.Length)
			read += stream.Read(data, read, data.Length - read);
		return data;
	}

	public static string Text(string name) => System.Text.Encoding.ASCII.GetString(Bytes(name));
}
