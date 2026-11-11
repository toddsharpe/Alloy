using System.Reflection;
using Alloy.Sdk.Web.Images;

namespace Alloy.Tests;

public class ImageTests
{
	private static byte[] Fixture(string name)
	{
		Assembly assembly = typeof(ImageTests).Assembly;
		using var stream = assembly.GetManifestResourceStream("Alloy.Tests.Fixtures." + name)!;
		byte[] data = new byte[stream.Length];
		int read = 0;
		while (read < data.Length)
			read += stream.Read(data, read, data.Length - read);
		return data;
	}

	private static Bitmap Decode(string name)
	{
		Assert.True(ImageDecoder.TryDecode(Fixture(name), out Bitmap? bitmap, out string? error), error);
		return bitmap!;
	}

	private static (int A, int R, int G, int B) Argb(uint pixel) => ((int)(pixel >> 24), (int)(pixel >> 16) & 0xFF, (int)(pixel >> 8) & 0xFF, (int)pixel & 0xFF);

	[Fact]
	public void DecodesInterlacedRgbaPngWithEveryFilter()
	{
		Bitmap bitmap = Decode("rgba_interlaced.png");
		Assert.Equal(5, bitmap.Width);
		Assert.Equal(7, bitmap.Height);
		for (int y = 0; y < 7; y++)
		{
			for (int x = 0; x < 5; x++)
				Assert.Equal((255 - y * 30, x * 40, y * 30, 200), Argb(bitmap[x, y]));
		}
	}

	[Fact]
	public void DecodesPalettePngWithTransparency()
	{
		Bitmap bitmap = Decode("palette.png");
		Assert.Equal(6, bitmap.Width);
		Assert.Equal(2, bitmap.Height);
		Assert.Equal((0, 255, 0, 0), Argb(bitmap[0, 0]));
		Assert.Equal((255, 0, 255, 0), Argb(bitmap[1, 0]));
		Assert.Equal((255, 0, 0, 255), Argb(bitmap[2, 0]));
		Assert.Equal((255, 255, 255, 0), Argb(bitmap[3, 0]));
		Assert.Equal((0, 255, 0, 0), Argb(bitmap[4, 0]));
		Assert.Equal((255, 0, 255, 0), Argb(bitmap[0, 1]));
		Assert.False(bitmap.IsOpaque);
	}

	[Fact]
	public void DecodesSixteenBitGrayAndGrayAlpha()
	{
		Bitmap gray = Decode("gray16.png");
		Assert.Equal((255, 0, 0, 0), Argb(gray[0, 0]));
		Assert.Equal((255, 78, 78, 78), Argb(gray[1, 0]));
		Assert.Equal((255, 175, 175, 175), Argb(gray[2, 1]));
		Bitmap alpha = Decode("grayalpha.png");
		Assert.Equal(4, alpha.Width);
		Assert.Equal((255, 0, 0, 0), Argb(alpha[0, 0]));
		Assert.Equal((75, 240, 240, 240), Argb(alpha[3, 0]));
	}

	[Fact]
	public void DecodesARealPng()
	{
		Bitmap bitmap = Decode("pig.png");
		Assert.Equal(100, bitmap.Width);
		Assert.Equal(100, bitmap.Height);
		Assert.True(bitmap.IsOpaque);
		int distinct = bitmap.Pixels.Distinct().Count();
		Assert.True(distinct > 100, $"only {distinct} colours");
	}

	[Fact]
	public void DecodesGifsWithTransparencyAndInterlace()
	{
		Bitmap tiny = Decode("tiny.gif");
		Assert.Equal(4, tiny.Width);
		Assert.Equal(3, tiny.Height);
		Assert.Equal((0, 0, 0, 0), Argb(tiny[0, 0]));
		Assert.Equal((255, 255, 0, 0), Argb(tiny[1, 0]));
		Assert.Equal((255, 0, 255, 0), Argb(tiny[2, 0]));
		Assert.Equal((255, 0, 0, 255), Argb(tiny[3, 0]));
		Assert.Equal((255, 0, 0, 255), Argb(tiny[0, 1]));
		Assert.Equal((255, 0, 255, 0), Argb(tiny[3, 2]));
		Bitmap interlaced = Decode("interlaced.gif");
		Assert.Equal(9, interlaced.Height);
		for (int y = 0; y < 9; y++)
		{
			for (int x = 0; x < 4; x++)
			{
				int index = (x + y) % 4;
				(int A, int R, int G, int B) expected = index switch { 0 => (255, 0, 0, 0), 1 => (255, 255, 0, 0), 2 => (255, 0, 255, 0), _ => (255, 0, 0, 255) };
				Assert.Equal(expected, Argb(interlaced[x, y]));
			}
		}
	}

	[Fact]
	public void DecodesABaselineJpeg()
	{
		Bitmap bitmap = Decode("jackal.jpg");
		Assert.Equal(239, bitmap.Width);
		Assert.Equal(178, bitmap.Height);
		Assert.True(bitmap.IsOpaque);
		long sum = 0;
		foreach (uint pixel in bitmap.Pixels)
			sum += ((pixel >> 16) & 0xFF) + ((pixel >> 8) & 0xFF) + (pixel & 0xFF);
		double mean = sum / (3.0 * bitmap.Pixels.Length);
		Assert.InRange(mean, 40, 215);
		int distinct = bitmap.Pixels.Distinct().Count();
		Assert.True(distinct > 1000, $"only {distinct} colours");
		(int A, int R, int G, int B) corner = Argb(bitmap[0, 0]);
		(int A, int R, int G, int B) middle = Argb(bitmap[119, 89]);
		Assert.NotEqual(corner, middle);
	}

	[Fact]
	public void RejectsJunk()
	{
		Assert.False(ImageDecoder.TryDecode(new byte[] { 1, 2, 3, 4, 5, 6, 7, 8, 9 }, out _, out string? error));
		Assert.Equal("unknown image format", error);
		Assert.False(ImageDecoder.TryDecode(new byte[] { 0x89, (byte)'P', (byte)'N', (byte)'G', 13, 10, 26, 10, 0, 0 }, out _, out _));
		Assert.False(ImageDecoder.TryDecode(new byte[] { 0xFF, 0xD8, 0xFF, 0xD9 }, out _, out _));
		Assert.Equal("png", ImageDecoder.Sniff(Fixture("pig.png")));
		Assert.Equal("jpeg", ImageDecoder.Sniff(Fixture("jackal.jpg")));
		Assert.Equal("gif", ImageDecoder.Sniff(Fixture("tiny.gif")));
	}
}
