using System;
using System.Buffers.Binary;
using System.IO;
using System.IO.Compression;

namespace Alloy.Sdk.Web.Images
{
	// PNG: every colour type and bit depth, the five filters, Adam7 interlace, palettes with tRNS transparency; gamma and colour profiles are ignored
	public static class Png
	{
		private static readonly int[] s_passX = { 0, 4, 0, 2, 0, 1, 0 };
		private static readonly int[] s_passY = { 0, 0, 4, 0, 2, 0, 1 };
		private static readonly int[] s_passDx = { 8, 8, 4, 4, 2, 2, 1 };
		private static readonly int[] s_passDy = { 8, 8, 8, 4, 4, 2, 2 };

		public static bool TryDecode(ReadOnlySpan<byte> data, out Bitmap bitmap, out string error)
		{
			bitmap = null;
			error = null;
			if (data.Length < 33 || data[0] != 0x89 || data[1] != 'P' || data[2] != 'N' || data[3] != 'G')
			{
				error = "not a PNG";
				return false;
			}
			int width = 0;
			int height = 0;
			int bitDepth = 0;
			int colorType = 0;
			int interlace = 0;
			byte[] palette = null;
			byte[] transparency = null;
			MemoryStream compressed = new MemoryStream();
			int at = 8;
			bool sawHeader = false;
			while (at + 8 <= data.Length)
			{
				int length = BinaryPrimitives.ReadInt32BigEndian(data.Slice(at));
				if (length < 0 || at + 12 + length > data.Length)
				{
					error = "truncated chunk";
					return false;
				}
				ReadOnlySpan<byte> type = data.Slice(at + 4, 4);
				ReadOnlySpan<byte> body = data.Slice(at + 8, length);
				at += 12 + length;
				if (type.SequenceEqual("IHDR"u8))
				{
					if (length < 13)
					{
						error = "bad IHDR";
						return false;
					}
					width = BinaryPrimitives.ReadInt32BigEndian(body);
					height = BinaryPrimitives.ReadInt32BigEndian(body.Slice(4));
					bitDepth = body[8];
					colorType = body[9];
					interlace = body[12];
					sawHeader = true;
					if (width <= 0 || height <= 0 || (long)width * height > ImageDecoder.MaxPixels)
					{
						error = "bad image size";
						return false;
					}
					if (!ValidFormat(colorType, bitDepth) || body[10] != 0 || body[11] != 0 || interlace > 1)
					{
						error = "unsupported PNG format";
						return false;
					}
				}
				else if (type.SequenceEqual("PLTE"u8))
					palette = body.ToArray();
				else if (type.SequenceEqual("tRNS"u8))
					transparency = body.ToArray();
				else if (type.SequenceEqual("IDAT"u8))
					compressed.Write(body);
				else if (type.SequenceEqual("IEND"u8))
					break;
			}
			if (!sawHeader)
			{
				error = "no IHDR";
				return false;
			}
			if (colorType == 3 && palette == null)
			{
				error = "palette image without PLTE";
				return false;
			}
			int channels = Channels(colorType);
			int bitsPerPixel = channels * bitDepth;
			int bytesPerPixel = Math.Max(1, bitsPerPixel / 8);
			long rawLength = 0;
			if (interlace == 0)
				rawLength = (long)height * (1 + RowBytes(width, bitsPerPixel));
			else
			{
				for (int pass = 0; pass < 7; pass++)
				{
					int passWidth = (width - s_passX[pass] + s_passDx[pass] - 1) / s_passDx[pass];
					int passHeight = (height - s_passY[pass] + s_passDy[pass] - 1) / s_passDy[pass];
					if (passWidth > 0 && passHeight > 0)
						rawLength += (long)passHeight * (1 + RowBytes(passWidth, bitsPerPixel));
				}
			}
			byte[] raw = new byte[rawLength];
			try
			{
				compressed.Position = 0;
				using ZLibStream inflate = new ZLibStream(compressed, CompressionMode.Decompress);
				int total = 0;
				while (total < raw.Length)
				{
					int read = inflate.Read(raw, total, raw.Length - total);
					if (read <= 0)
						break;
					total += read;
				}
				if (total < raw.Length)
				{
					error = "image data ends early";
					return false;
				}
			}
			catch (Exception e)
			{
				error = "bad image data: " + e.Message;
				return false;
			}

			bitmap = new Bitmap(width, height);
			int offset = 0;
			if (interlace == 0)
				DecodePass(raw, ref offset, bitmap, 0, 0, 1, 1, width, height, bitDepth, colorType, bytesPerPixel, palette, transparency);
			else
			{
				for (int pass = 0; pass < 7; pass++)
				{
					int passWidth = (width - s_passX[pass] + s_passDx[pass] - 1) / s_passDx[pass];
					int passHeight = (height - s_passY[pass] + s_passDy[pass] - 1) / s_passDy[pass];
					if (passWidth > 0 && passHeight > 0)
						DecodePass(raw, ref offset, bitmap, s_passX[pass], s_passY[pass], s_passDx[pass], s_passDy[pass], passWidth, passHeight, bitDepth, colorType, bytesPerPixel, palette, transparency);
				}
			}
			return true;
		}

		private static bool ValidFormat(int colorType, int bitDepth) => colorType switch
		{
			0 => bitDepth is 1 or 2 or 4 or 8 or 16,
			2 or 4 or 6 => bitDepth is 8 or 16,
			3 => bitDepth is 1 or 2 or 4 or 8,
			_ => false,
		};

		private static int Channels(int colorType) => colorType switch { 0 => 1, 2 => 3, 3 => 1, 4 => 2, _ => 4 };

		private static int RowBytes(int width, int bitsPerPixel) => (int)(((long)width * bitsPerPixel + 7) / 8);

		// Unfilters one pass's rows and writes its pixels into their places in the image
		private static void DecodePass(byte[] raw, ref int offset, Bitmap bitmap, int startX, int startY, int dx, int dy, int passWidth, int passHeight, int bitDepth, int colorType, int bytesPerPixel, byte[] palette, byte[] transparency)
		{
			int bitsPerPixel = Channels(colorType) * bitDepth;
			int rowBytes = RowBytes(passWidth, bitsPerPixel);
			byte[] previous = new byte[rowBytes];
			byte[] current = new byte[rowBytes];
			for (int row = 0; row < passHeight; row++)
			{
				int filter = raw[offset++];
				Array.Copy(raw, offset, current, 0, rowBytes);
				offset += rowBytes;
				Unfilter(filter, current, previous, bytesPerPixel);
				int y = startY + row * dy;
				for (int column = 0; column < passWidth; column++)
					bitmap[startX + column * dx, y] = Pixel(current, column, bitDepth, colorType, palette, transparency);
				(previous, current) = (current, previous);
			}
		}

		private static void Unfilter(int filter, byte[] row, byte[] previous, int bpp)
		{
			switch (filter)
			{
				case 1:
					for (int i = bpp; i < row.Length; i++)
						row[i] += row[i - bpp];
					break;
				case 2:
					for (int i = 0; i < row.Length; i++)
						row[i] += previous[i];
					break;
				case 3:
					for (int i = 0; i < row.Length; i++)
						row[i] += (byte)(((i >= bpp ? row[i - bpp] : 0) + previous[i]) >> 1);
					break;
				case 4:
					for (int i = 0; i < row.Length; i++)
					{
						int a = i >= bpp ? row[i - bpp] : 0;
						int b = previous[i];
						int c = i >= bpp ? previous[i - bpp] : 0;
						int p = a + b - c;
						int pa = Math.Abs(p - a);
						int pb = Math.Abs(p - b);
						int pc = Math.Abs(p - c);
						row[i] += (byte)(pa <= pb && pa <= pc ? a : pb <= pc ? b : c);
					}
					break;
			}
		}

		// One pixel of an unfiltered row as ARGB; 16-bit samples keep their high byte
		private static uint Pixel(byte[] row, int x, int bitDepth, int colorType, byte[] palette, byte[] transparency)
		{
			switch (colorType)
			{
				case 0:
				{
					int value = Sample(row, x, bitDepth);
					int gray = bitDepth switch { 16 => value >> 8, 8 => value, _ => value * 255 / ((1 << bitDepth) - 1) };
					bool transparent = transparency != null && transparency.Length >= 2 && value == BinaryPrimitives.ReadUInt16BigEndian(transparency);
					return (transparent ? 0u : 0xFF000000) | (uint)(gray << 16 | gray << 8 | gray);
				}
				case 2:
				{
					int step = bitDepth == 16 ? 2 : 1;
					int at = x * 3 * step;
					int r = row[at];
					int g = row[at + step];
					int b = row[at + 2 * step];
					bool transparent = false;
					if (transparency != null && transparency.Length >= 6)
					{
						int r16 = bitDepth == 16 ? row[at] << 8 | row[at + 1] : r;
						int g16 = bitDepth == 16 ? row[at + 2] << 8 | row[at + 3] : g;
						int b16 = bitDepth == 16 ? row[at + 4] << 8 | row[at + 5] : b;
						transparent = r16 == BinaryPrimitives.ReadUInt16BigEndian(transparency) && g16 == BinaryPrimitives.ReadUInt16BigEndian(transparency.AsSpan(2)) && b16 == BinaryPrimitives.ReadUInt16BigEndian(transparency.AsSpan(4));
					}
					return (transparent ? 0u : 0xFF000000) | (uint)(r << 16 | g << 8 | b);
				}
				case 3:
				{
					int index = Sample(row, x, bitDepth);
					int at = index * 3;
					if (at + 2 >= palette.Length)
						return 0xFF000000;
					uint alpha = transparency != null && index < transparency.Length ? transparency[index] : 255u;
					return alpha << 24 | (uint)(palette[at] << 16 | palette[at + 1] << 8 | palette[at + 2]);
				}
				case 4:
				{
					int step = bitDepth == 16 ? 2 : 1;
					int at = x * 2 * step;
					int gray = row[at];
					int alpha = row[at + step];
					return (uint)(alpha << 24 | gray << 16 | gray << 8 | gray);
				}
				default:
				{
					int step = bitDepth == 16 ? 2 : 1;
					int at = x * 4 * step;
					return (uint)(row[at + 3 * step] << 24 | row[at] << 16 | row[at + step] << 8 | row[at + 2 * step]);
				}
			}
		}

		private static int Sample(byte[] row, int x, int bitDepth)
		{
			switch (bitDepth)
			{
				case 16:
					return row[x * 2] << 8 | row[x * 2 + 1];
				case 8:
					return row[x];
				default:
				{
					int perByte = 8 / bitDepth;
					int b = row[x / perByte];
					int shift = 8 - bitDepth * (x % perByte + 1);
					return (b >> shift) & ((1 << bitDepth) - 1);
				}
			}
		}
	}
}
