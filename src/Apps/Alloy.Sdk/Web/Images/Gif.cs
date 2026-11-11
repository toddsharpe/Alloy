using System;
using System.Buffers.Binary;

namespace Alloy.Sdk.Web.Images
{
	// GIF87a and GIF89a: the first frame, with its local or global palette, LZW decoding, interlace and the transparent index
	public static class Gif
	{
		public static bool TryDecode(ReadOnlySpan<byte> data, out Bitmap bitmap, out string error)
		{
			bitmap = null;
			error = null;
			if (data.Length < 13 || data[0] != 'G' || data[1] != 'I' || data[2] != 'F')
			{
				error = "not a GIF";
				return false;
			}
			int width = BinaryPrimitives.ReadUInt16LittleEndian(data.Slice(6));
			int height = BinaryPrimitives.ReadUInt16LittleEndian(data.Slice(8));
			int flags = data[10];
			int background = data[11];
			int at = 13;
			byte[] globalPalette = null;
			if ((flags & 0x80) != 0)
			{
				int size = 3 << ((flags & 7) + 1);
				if (at + size > data.Length)
				{
					error = "truncated palette";
					return false;
				}
				globalPalette = data.Slice(at, size).ToArray();
				at += size;
			}
			if (width <= 0 || height <= 0 || (long)width * height > ImageDecoder.MaxPixels)
			{
				error = "bad image size";
				return false;
			}
			int transparent = -1;
			while (at < data.Length)
			{
				byte block = data[at++];
				if (block == 0x3B)
					break;
				if (block == 0x21)
				{
					if (at >= data.Length)
						break;
					byte label = data[at++];
					if (label == 0xF9 && at + 5 < data.Length && data[at] == 4)
					{
						if ((data[at + 1] & 1) != 0)
							transparent = data[at + 4];
					}
					at = SkipSubBlocks(data, at);
					continue;
				}
				if (block != 0x2C)
				{
					error = "unexpected block";
					return false;
				}
				if (at + 9 > data.Length)
				{
					error = "truncated image descriptor";
					return false;
				}
				int left = BinaryPrimitives.ReadUInt16LittleEndian(data.Slice(at));
				int top = BinaryPrimitives.ReadUInt16LittleEndian(data.Slice(at + 2));
				int frameWidth = BinaryPrimitives.ReadUInt16LittleEndian(data.Slice(at + 4));
				int frameHeight = BinaryPrimitives.ReadUInt16LittleEndian(data.Slice(at + 6));
				int frameFlags = data[at + 8];
				at += 9;
				byte[] palette = globalPalette;
				if ((frameFlags & 0x80) != 0)
				{
					int size = 3 << ((frameFlags & 7) + 1);
					if (at + size > data.Length)
					{
						error = "truncated local palette";
						return false;
					}
					palette = data.Slice(at, size).ToArray();
					at += size;
				}
				if (palette == null)
				{
					error = "no palette";
					return false;
				}
				if (at >= data.Length)
				{
					error = "truncated image";
					return false;
				}
				int minimumCodeSize = data[at++];
				if (minimumCodeSize < 2 || minimumCodeSize > 11)
				{
					error = "bad LZW code size";
					return false;
				}
				byte[] indices = new byte[frameWidth * frameHeight];
				if (!Lzw.Decode(data, ref at, minimumCodeSize, indices, out error))
					return false;
				bitmap = new Bitmap(width, height);
				bool interlaced = (frameFlags & 0x40) != 0;
				int source = 0;
				for (int row = 0; row < frameHeight; row++)
				{
					int y = top + (interlaced ? InterlacedRow(row, frameHeight) : row);
					for (int column = 0; column < frameWidth; column++, source++)
					{
						int x = left + column;
						if (x >= width || y >= height)
							continue;
						int index = indices[source];
						if (index == transparent || index * 3 + 2 >= palette.Length)
							continue;
						bitmap[x, y] = 0xFF000000 | (uint)(palette[index * 3] << 16 | palette[index * 3 + 1] << 8 | palette[index * 3 + 2]);
					}
				}
				return true;
			}
			error = "no image in GIF";
			return false;
		}

		private static int SkipSubBlocks(ReadOnlySpan<byte> data, int at)
		{
			while (at < data.Length)
			{
				int size = data[at++];
				if (size == 0)
					break;
				at += size;
			}
			return at;
		}

		// The row an interlaced frame's nth stored row lands on: every 8th from 0, then from 4, every 4th from 2, every 2nd from 1
		private static int InterlacedRow(int n, int height)
		{
			int pass0 = (height + 7) / 8;
			int pass1 = (height + 3) / 8;
			int pass2 = (height + 1) / 4;
			if (n < pass0)
				return n * 8;
			n -= pass0;
			if (n < pass1)
				return 4 + n * 8;
			n -= pass1;
			if (n < pass2)
				return 2 + n * 4;
			n -= pass2;
			return 1 + n * 2;
		}
	}

	// The GIF flavour of LZW: variable code width from the minimum size up to 12 bits, clear and end codes, data in sub-blocks
	internal static class Lzw
	{
		public static bool Decode(ReadOnlySpan<byte> data, ref int at, int minimumCodeSize, byte[] output, out string error)
		{
			error = null;
			int clear = 1 << minimumCodeSize;
			int end = clear + 1;
			int[] prefix = new int[4096];
			byte[] suffix = new byte[4096];
			int[] length = new int[4096];
			for (int i = 0; i < clear; i++)
			{
				prefix[i] = -1;
				suffix[i] = (byte)i;
				length[i] = 1;
			}
			int codeSize = minimumCodeSize + 1;
			int next = end + 1;
			int previous = -1;
			int written = 0;
			uint bits = 0;
			int bitCount = 0;
			int blockRemaining = 0;
			byte[] stack = new byte[4097];
			while (written < output.Length)
			{
				while (bitCount < codeSize)
				{
					if (blockRemaining == 0)
					{
						if (at >= data.Length)
							return Finish(ref error, written, output.Length);
						blockRemaining = data[at++];
						if (blockRemaining == 0)
							return Finish(ref error, written, output.Length);
					}
					if (at >= data.Length)
						return Finish(ref error, written, output.Length);
					bits |= (uint)data[at++] << bitCount;
					bitCount += 8;
					blockRemaining--;
				}
				int code = (int)(bits & ((1u << codeSize) - 1));
				bits >>= codeSize;
				bitCount -= codeSize;
				if (code == clear)
				{
					codeSize = minimumCodeSize + 1;
					next = end + 1;
					previous = -1;
					continue;
				}
				if (code == end)
					break;
				int entry = code;
				byte first;
				if (code < next)
					first = FirstByte(prefix, suffix, code);
				else if (code == next && previous >= 0)
				{
					first = FirstByte(prefix, suffix, previous);
					entry = previous;
				}
				else
				{
					error = "bad LZW code";
					return false;
				}
				int count = 0;
				if (code == next && previous >= 0)
					stack[count++] = first;
				int walk = entry;
				while (walk >= 0 && count < stack.Length)
				{
					stack[count++] = suffix[walk];
					walk = prefix[walk];
				}
				for (int i = count - 1; i >= 0 && written < output.Length; i--)
					output[written++] = stack[i];
				if (previous >= 0 && next < 4096)
				{
					prefix[next] = previous;
					suffix[next] = first;
					length[next] = length[previous] + 1;
					next++;
					if (next == (1 << codeSize) && codeSize < 12)
						codeSize++;
				}
				previous = code;
			}
			// Step past whatever sub-blocks remain so the caller lands on the next block
			while (blockRemaining > 0 && at < data.Length)
			{
				at++;
				blockRemaining--;
			}
			while (at < data.Length)
			{
				int size = data[at++];
				if (size == 0)
					break;
				at += size;
			}
			return true;
		}

		private static byte FirstByte(int[] prefix, byte[] suffix, int code)
		{
			while (prefix[code] >= 0)
				code = prefix[code];
			return suffix[code];
		}

		private static bool Finish(ref string error, int written, int expected)
		{
			if (written < expected)
				error = "image data ends early";
			return written >= expected || written > 0;
		}
	}
}
