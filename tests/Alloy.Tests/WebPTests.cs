using System;
using Alloy.Sdk.Web.Images;

namespace Alloy.Tests;

// WebP decoding against files written by libwebp: a flat colour, a gradient and a lossy picture with an alpha plane
public class WebPTests
{
	private const string Red16 = "UklGRjoAAABXRUJQVlA4IC4AAACwAQCdASoQABAAAUAmJaACdLoABDAAAP7x3I/4DdfFtMv/vYL/3YL/3YL/WwAA";
	private const string Grad64 = "UklGRtgAAABXRUJQVlA4IMwAAABwBwCdASpAADAAPn0wkkekoyGqt+gBUA+JbADL174An5H+SnN47cmCin+BKv74h6RegzMDWAZ0+4v7XLsK54gj9tUyAAD++nCFV1Mg9n//vGDfSo26z3e9Tb+P+Ib3fEAHJcuxgyihzwNgxZ5LDrLN8OdxbGu9Erd9x11/vHRa7uO1e2J8fkwR9QrBbFxkvXn5Cy/tPvf9Q8ODfxxh0GrV5bVcn9MIrv2OIM4C3ENbXCCTFXxat8mj1HH15P6YQMUAkt9cufUMR3QAAAA=";
	private const string Duck = "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";

	private static Bitmap Decode(string base64)
	{
		Assert.True(ImageDecoder.TryDecode(Convert.FromBase64String(base64), out Bitmap bitmap, out string error), error);
		return bitmap;
	}

	private static (int R, int G, int B) Rgb(uint pixel) => ((int)(pixel >> 16) & 255, (int)(pixel >> 8) & 255, (int)pixel & 255);

	private static (int R, int G, int B) Mean(Bitmap bitmap)
	{
		long r = 0, g = 0, b = 0;
		foreach (uint pixel in bitmap.Pixels)
		{
			r += (pixel >> 16) & 255;
			g += (pixel >> 8) & 255;
			b += pixel & 255;
		}
		int n = bitmap.Pixels.Length;
		return ((int)(r / n), (int)(g / n), (int)(b / n));
	}

	private static void Near((int R, int G, int B) expected, (int R, int G, int B) actual, int tolerance)
	{
		Assert.True(Math.Abs(expected.R - actual.R) <= tolerance && Math.Abs(expected.G - actual.G) <= tolerance && Math.Abs(expected.B - actual.B) <= tolerance, $"expected {expected}, got {actual}");
	}

	[Fact]
	public void DecodesAFlatColour()
	{
		Bitmap bitmap = Decode(Red16);
		Assert.Equal(16, bitmap.Width);
		Assert.Equal(16, bitmap.Height);
		Assert.Equal("webp", ImageDecoder.Sniff(Convert.FromBase64String(Red16)));
		Near((200, 29, 30), Rgb(bitmap[0, 0]), 1);
		Near((200, 29, 30), Rgb(bitmap[15, 15]), 1);
		Assert.True(bitmap.IsOpaque);
	}

	[Fact]
	public void DecodesAGradientWithSubblockModesAndTheLoopFilter()
	{
		Bitmap bitmap = Decode(Grad64);
		Assert.Equal(64, bitmap.Width);
		Assert.Equal(48, bitmap.Height);
		Near((125, 117, 128), Mean(bitmap), 2);
		Near((3, 0, 255), Rgb(bitmap[0, 0]), 4);
		Near((244, 235, 0), Rgb(bitmap[63, 47]), 4);
	}

	[Fact]
	public void DeclinesAnExtendedFileWhoseAlphaIsCompressed()
	{
		// The duck's alpha plane is stored losslessly, which waits for the VP8L decoder; a raw alpha plane applies as is
		Assert.False(ImageDecoder.TryDecode(Convert.FromBase64String(Duck), out _, out string error));
		Assert.Contains("alpha", error);
		byte[] red = Convert.FromBase64String(Red16);
		byte[] vp8 = red.AsSpan(12).ToArray();
		byte[] alph = new byte[1 + 16 * 16];
		for (int i = 1; i < alph.Length; i++)
			alph[i] = (byte)((i - 1) % 16 < 8 ? 255 : 0);
		byte[] file = BuildExtended(16, 16, alph, vp8);
		Bitmap bitmap = Decode(Convert.ToBase64String(file));
		Assert.Equal(255u, bitmap[0, 0] >> 24);
		Assert.Equal(0u, bitmap[15, 15] >> 24);
		Near((200, 29, 30), Rgb(bitmap[0, 0]), 1);
		Assert.False(bitmap.IsOpaque);
	}

	// A VP8X container around an ALPH chunk and an existing VP8 chunk, for a width and height
	private static byte[] BuildExtended(int width, int height, byte[] alph, byte[] vp8Chunk)
	{
		System.IO.MemoryStream stream = new System.IO.MemoryStream();
		void Chunk(string tag, byte[] payload)
		{
			stream.Write(System.Text.Encoding.ASCII.GetBytes(tag), 0, 4);
			stream.Write(BitConverter.GetBytes(payload.Length), 0, 4);
			stream.Write(payload, 0, payload.Length);
			if ((payload.Length & 1) != 0)
				stream.WriteByte(0);
		}
		byte[] vp8x = new byte[10];
		vp8x[0] = 0x10;
		vp8x[4] = (byte)(width - 1);
		vp8x[7] = (byte)(height - 1);
		Chunk("VP8X", vp8x);
		Chunk("ALPH", alph);
		stream.Write(vp8Chunk, 0, vp8Chunk.Length);
		byte[] body = stream.ToArray();
		System.IO.MemoryStream file = new System.IO.MemoryStream();
		file.Write(System.Text.Encoding.ASCII.GetBytes("RIFF"), 0, 4);
		file.Write(BitConverter.GetBytes(body.Length + 4), 0, 4);
		file.Write(System.Text.Encoding.ASCII.GetBytes("WEBP"), 0, 4);
		file.Write(body, 0, body.Length);
		return file.ToArray();
	}

	[Fact]
	public void RejectsTruncatedAndLosslessFiles()
	{
		byte[] duck = Convert.FromBase64String(Duck);
		Assert.False(ImageDecoder.TryDecode(duck.AsSpan(0, 40), out _, out string error));
		Assert.NotNull(error);
		byte[] lossless = Convert.FromBase64String(Red16);
		lossless[12] = (byte)'V';
		lossless[13] = (byte)'P';
		lossless[14] = (byte)'8';
		lossless[15] = (byte)'L';
		Assert.False(ImageDecoder.TryDecode(lossless, out _, out error));
		Assert.Contains("lossless", error);
	}
}
