using System;

namespace Alloy.Sdk.Web.Images
{
	// A decoded image: 0xAARRGGBB pixels, straight (not premultiplied) alpha, rows top to bottom
	public sealed class Bitmap
	{
		public readonly int Width;
		public readonly int Height;
		public readonly uint[] Pixels;

		public Bitmap(int width, int height)
		{
			Width = width;
			Height = height;
			Pixels = new uint[width * height];
		}

		public uint this[int x, int y]
		{
			get => Pixels[y * Width + x];
			set => Pixels[y * Width + x] = value;
		}

		// True when every pixel is opaque, so drawing can skip blending
		public bool IsOpaque
		{
			get
			{
				foreach (uint pixel in Pixels)
				{
					if (pixel >> 24 != 0xFF)
						return false;
				}
				return true;
			}
		}
	}

	// The image formats the browser decodes, told apart by their signatures
	public static class ImageDecoder
	{
		public const int MaxPixels = 64 * 1024 * 1024;

		public static bool TryDecode(ReadOnlySpan<byte> data, out Bitmap bitmap, out string error)
		{
			bitmap = null;
			error = null;
			if (data.Length >= 8 && data[0] == 0x89 && data[1] == 'P' && data[2] == 'N' && data[3] == 'G')
				return Png.TryDecode(data, out bitmap, out error);
			if (data.Length >= 6 && data[0] == 'G' && data[1] == 'I' && data[2] == 'F' && data[3] == '8')
				return Gif.TryDecode(data, out bitmap, out error);
			if (data.Length >= 3 && data[0] == 0xFF && data[1] == 0xD8 && data[2] == 0xFF)
				return Jpeg.TryDecode(data, out bitmap, out error);
			if (WebP.IsWebP(data))
				return WebP.TryDecode(data, out bitmap, out error);
			error = "unknown image format";
			return false;
		}

		public static string Sniff(ReadOnlySpan<byte> data)
		{
			if (data.Length >= 8 && data[0] == 0x89 && data[1] == 'P' && data[2] == 'N' && data[3] == 'G')
				return "png";
			if (data.Length >= 6 && data[0] == 'G' && data[1] == 'I' && data[2] == 'F' && data[3] == '8')
				return "gif";
			if (data.Length >= 3 && data[0] == 0xFF && data[1] == 0xD8 && data[2] == 0xFF)
				return "jpeg";
			if (WebP.IsWebP(data))
				return "webp";
			return null;
		}
	}
}
