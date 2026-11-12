using System;
using System.Collections.Generic;

namespace Alloy.Sdk.Web.Images
{
	// WebP lossless (VP8L): LZ77 over Huffman-coded ARGB with a colour cache, undone through the predictor, colour, subtract-green and palette transforms
	internal sealed class Vp8LDecoder
	{
		private const int NumLiteralCodes = 256;
		private const int NumLengthCodes = 24;
		private const int NumDistanceCodes = 40;
		private const int NumCodeLengthCodes = 19;
		private static readonly byte[] s_codeLengthOrder = { 17, 18, 0, 1, 2, 3, 4, 5, 16, 6, 7, 8, 9, 10, 11, 12, 13, 14, 15 };
		private static readonly byte[] s_codeToPlane =
		{
			0x18, 0x07, 0x17, 0x19, 0x28, 0x06, 0x27, 0x29, 0x16, 0x1a, 0x26, 0x2a, 0x38, 0x05, 0x37, 0x39, 0x15, 0x1b, 0x36, 0x3a, 0x25, 0x2b, 0x48, 0x04,
			0x47, 0x49, 0x14, 0x1c, 0x35, 0x3b, 0x46, 0x4a, 0x24, 0x2c, 0x58, 0x45, 0x4b, 0x34, 0x3c, 0x03, 0x57, 0x59, 0x13, 0x1d, 0x56, 0x5a, 0x23, 0x2d,
			0x44, 0x4c, 0x55, 0x5b, 0x33, 0x3d, 0x68, 0x02, 0x67, 0x69, 0x12, 0x1e, 0x66, 0x6a, 0x22, 0x2e, 0x54, 0x5c, 0x43, 0x4d, 0x65, 0x6b, 0x32, 0x3e,
			0x78, 0x01, 0x77, 0x79, 0x53, 0x5d, 0x11, 0x1f, 0x64, 0x6c, 0x42, 0x4e, 0x76, 0x7a, 0x21, 0x2f, 0x75, 0x7b, 0x31, 0x3f, 0x63, 0x6d, 0x52, 0x5e,
			0x00, 0x74, 0x7c, 0x41, 0x4f, 0x10, 0x20, 0x62, 0x6e, 0x30, 0x73, 0x7d, 0x51, 0x5f, 0x40, 0x72, 0x7e, 0x61, 0x6f, 0x50, 0x71, 0x7f, 0x60, 0x70,
		};

		private readonly byte[] _data;
		private int _at;
		private ulong _bits;
		private int _bitCount;
		private bool _overrun;

		private Vp8LDecoder(byte[] data)
		{
			_data = data;
		}

		// A VP8L chunk: signature, size, then the image stream
		public static bool TryDecode(ReadOnlySpan<byte> payload, out Bitmap bitmap, out string error)
		{
			bitmap = null;
			error = null;
			try
			{
				Vp8LDecoder decoder = new Vp8LDecoder(payload.ToArray());
				if (decoder.ReadBits(8) != 0x2f)
				{
					error = "VP8L signature missing";
					return false;
				}
				int width = decoder.ReadBits(14) + 1;
				int height = decoder.ReadBits(14) + 1;
				decoder.ReadBits(1);
				if (decoder.ReadBits(3) != 0)
				{
					error = "VP8L version is not 0";
					return false;
				}
				if ((long)width * height > ImageDecoder.MaxPixels)
				{
					error = "VP8L image is too large";
					return false;
				}
				uint[] pixels = decoder.DecodeImageStream(width, height, true);
				if (pixels == null || decoder._overrun)
				{
					error = "corrupt VP8L data";
					return false;
				}
				bitmap = new Bitmap(width, height);
				Array.Copy(pixels, bitmap.Pixels, width * height);
				return true;
			}
			catch (Exception e) when (e is IndexOutOfRangeException || e is ArgumentException || e is OverflowException || e is InvalidOperationException)
			{
				error = "corrupt VP8L data";
				return false;
			}
		}

		// An ALPH chunk's compressed plane: a headerless image stream whose green channel is the alpha
		public static bool TryDecodeAlpha(ReadOnlySpan<byte> stream, int width, int height, byte[] alpha)
		{
			try
			{
				Vp8LDecoder decoder = new Vp8LDecoder(stream.ToArray());
				uint[] pixels = decoder.DecodeImageStream(width, height, true);
				if (pixels == null || decoder._overrun)
					return false;
				for (int i = 0; i < width * height; i++)
					alpha[i] = (byte)(pixels[i] >> 8);
				return true;
			}
			catch (Exception e) when (e is IndexOutOfRangeException || e is ArgumentException || e is OverflowException || e is InvalidOperationException)
			{
				return false;
			}
		}

		// Bits come least significant first
		private int ReadBits(int count)
		{
			if (count == 0)
				return 0;
			while (_bitCount < count)
			{
				if (_at < _data.Length)
					_bits |= (ulong)_data[_at++] << _bitCount;
				else if (_bitCount + 8 <= 64)
				{
					_overrun = _overrun || _bitCount < count;
					if (_at > _data.Length + 8)
						throw new InvalidOperationException("past the end");
					_at++;
				}
				_bitCount += 8;
			}
			int value = (int)(_bits & ((1UL << count) - 1));
			_bits >>= count;
			_bitCount -= count;
			return value;
		}

		private int ReadBit() => ReadBits(1);

		// Canonical Huffman decoding by code length, as deflate does it; a code with one symbol takes no bits
		private sealed class HuffmanCode
		{
			private readonly int[] _counts = new int[16];
			private readonly int[] _symbols;
			private readonly int _single = -1;

			public HuffmanCode(int[] lengths, int alphabetSize)
			{
				int present = 0;
				int last = 0;
				for (int i = 0; i < alphabetSize; i++)
				{
					if (lengths[i] > 0)
					{
						_counts[lengths[i]]++;
						present++;
						last = i;
					}
				}
				if (present <= 1)
				{
					_single = present == 1 ? last : 0;
					_symbols = Array.Empty<int>();
					return;
				}
				int[] offsets = new int[17];
				for (int len = 1; len < 16; len++)
					offsets[len + 1] = offsets[len] + _counts[len];
				_symbols = new int[present];
				for (int i = 0; i < alphabetSize; i++)
				{
					if (lengths[i] > 0)
						_symbols[offsets[lengths[i]]++] = i;
				}
			}

			public int Decode(Vp8LDecoder reader)
			{
				if (_single >= 0)
					return _single;
				int code = 0;
				int first = 0;
				int index = 0;
				for (int len = 1; len < 16; len++)
				{
					code |= reader.ReadBit();
					int count = _counts[len];
					if (code - first < count)
						return _symbols[index + (code - first)];
					index += count;
					first += count;
					first <<= 1;
					code <<= 1;
				}
				throw new InvalidOperationException("bad Huffman code");
			}
		}

		private sealed class HuffmanGroup
		{
			public HuffmanCode Green;
			public HuffmanCode Red;
			public HuffmanCode Blue;
			public HuffmanCode Alpha;
			public HuffmanCode Distance;
		}

		private struct Transform
		{
			public int Type;
			public int Bits;
			public int Width;
			public uint[] Data;
		}

		private static int SubSampleSize(int size, int bits) => (size + (1 << bits) - 1) >> bits;

		// The image stream: at level 0 the transforms, then the colour cache size, the Huffman groups and the pixels; nested streams carry the transform and group images
		private uint[] DecodeImageStream(int width, int height, bool level0)
		{
			int decodeWidth = width;
			List<Transform> transforms = new List<Transform>();
			if (level0)
			{
				int seen = 0;
				while (ReadBit() != 0)
				{
					Transform transform = new Transform { Type = ReadBits(2), Width = decodeWidth };
					if ((seen & (1 << transform.Type)) != 0)
						return null;
					seen |= 1 << transform.Type;
					switch (transform.Type)
					{
						case 0:
						case 1:
							transform.Bits = ReadBits(3) + 2;
							transform.Data = DecodeImageStream(SubSampleSize(decodeWidth, transform.Bits), SubSampleSize(height, transform.Bits), false);
							break;
						case 3:
						{
							int colours = ReadBits(8) + 1;
							int bits = colours > 16 ? 0 : colours > 4 ? 1 : colours > 2 ? 2 : 3;
							transform.Bits = bits;
							uint[] palette = DecodeImageStream(colours, 1, false);
							if (palette == null)
								return null;
							transform.Data = ExpandPalette(palette, colours, bits);
							decodeWidth = SubSampleSize(decodeWidth, bits);
							break;
						}
					}
					if (transform.Type != 2 && transform.Data == null)
						return null;
					transforms.Add(transform);
				}
			}
			int cacheBits = 0;
			if (ReadBit() != 0)
			{
				cacheBits = ReadBits(4);
				if (cacheBits < 1 || cacheBits > 11)
					return null;
			}
			uint[] metaImage = null;
			int metaBits = 0;
			int groupCount = 1;
			if (level0 && ReadBit() != 0)
			{
				metaBits = ReadBits(3) + 2;
				metaImage = DecodeImageStream(SubSampleSize(decodeWidth, metaBits), SubSampleSize(height, metaBits), false);
				if (metaImage == null)
					return null;
				for (int i = 0; i < metaImage.Length; i++)
				{
					int group = (int)((metaImage[i] >> 8) & 0xffff);
					metaImage[i] = (uint)group;
					if (group >= groupCount)
						groupCount = group + 1;
				}
			}
			HuffmanGroup[] groups = new HuffmanGroup[groupCount];
			int greenAlphabet = NumLiteralCodes + NumLengthCodes + (cacheBits > 0 ? 1 << cacheBits : 0);
			int[] lengths = new int[greenAlphabet];
			for (int g = 0; g < groupCount; g++)
			{
				groups[g] = new HuffmanGroup
				{
					Green = ReadHuffmanCode(greenAlphabet, lengths),
					Red = ReadHuffmanCode(NumLiteralCodes, lengths),
					Blue = ReadHuffmanCode(NumLiteralCodes, lengths),
					Alpha = ReadHuffmanCode(NumLiteralCodes, lengths),
					Distance = ReadHuffmanCode(NumDistanceCodes, lengths),
				};
			}
			uint[] pixels = DecodePixels(decodeWidth, height, groups, metaImage, metaBits, cacheBits);
			if (pixels == null)
				return null;
			for (int t = transforms.Count - 1; t >= 0; t--)
				pixels = ApplyInverse(transforms[t], pixels, height);
			return pixels;
		}

		private HuffmanCode ReadHuffmanCode(int alphabetSize, int[] lengths)
		{
			Array.Clear(lengths, 0, alphabetSize);
			if (ReadBit() != 0)
			{
				// One or two symbols listed outright
				int count = ReadBit() + 1;
				int firstBits = ReadBit() == 0 ? 1 : 8;
				int symbol = ReadBits(firstBits);
				if (symbol >= alphabetSize)
					throw new InvalidOperationException("symbol out of range");
				lengths[symbol] = 1;
				if (count == 2)
				{
					symbol = ReadBits(8);
					if (symbol >= alphabetSize)
						throw new InvalidOperationException("symbol out of range");
					lengths[symbol] = 1;
				}
				return new HuffmanCode(lengths, alphabetSize);
			}
			int[] codeLengthLengths = new int[NumCodeLengthCodes];
			int codes = ReadBits(4) + 4;
			for (int i = 0; i < codes; i++)
				codeLengthLengths[s_codeLengthOrder[i]] = ReadBits(3);
			HuffmanCode lengthCode = new HuffmanCode(codeLengthLengths, NumCodeLengthCodes);
			int maxSymbol = alphabetSize;
			if (ReadBit() != 0)
			{
				int lengthBits = 2 + 2 * ReadBits(3);
				maxSymbol = 2 + ReadBits(lengthBits);
				if (maxSymbol > alphabetSize)
					throw new InvalidOperationException("bad symbol count");
			}
			int previous = 8;
			int at = 0;
			while (at < alphabetSize)
			{
				if (maxSymbol-- == 0)
					break;
				int length = lengthCode.Decode(this);
				if (length < 16)
				{
					lengths[at++] = length;
					if (length != 0)
						previous = length;
				}
				else
				{
					int repeat = length == 16 ? 3 + ReadBits(2) : length == 17 ? 3 + ReadBits(3) : 11 + ReadBits(7);
					int value = length == 16 ? previous : 0;
					if (at + repeat > alphabetSize)
						throw new InvalidOperationException("repeat past the alphabet");
					while (repeat-- > 0)
						lengths[at++] = value;
				}
			}
			return new HuffmanCode(lengths, alphabetSize);
		}

		private uint[] DecodePixels(int width, int height, HuffmanGroup[] groups, uint[] metaImage, int metaBits, int cacheBits)
		{
			int total = width * height;
			uint[] pixels = new uint[total];
			uint[] cache = cacheBits > 0 ? new uint[1 << cacheBits] : null;
			int cacheShift = 32 - cacheBits;
			int metaWidth = metaBits > 0 ? SubSampleSize(width, metaBits) : 0;
			int lengthLimit = NumLiteralCodes + NumLengthCodes;
			int cacheLimit = lengthLimit + (cacheBits > 0 ? 1 << cacheBits : 0);
			int at = 0;
			int lastCached = 0;
			while (at < total)
			{
				if (_overrun)
					return null;
				int x = at % width;
				int y = at / width;
				HuffmanGroup group = metaImage == null ? groups[0] : groups[metaImage[(y >> metaBits) * metaWidth + (x >> metaBits)]];
				int code = group.Green.Decode(this);
				if (code < NumLiteralCodes)
				{
					int red = group.Red.Decode(this);
					int blue = group.Blue.Decode(this);
					int alpha = group.Alpha.Decode(this);
					pixels[at++] = (uint)alpha << 24 | (uint)red << 16 | (uint)code << 8 | (uint)blue;
				}
				else if (code < lengthLimit)
				{
					int length = CopyDistance(code - NumLiteralCodes);
					int distanceSymbol = group.Distance.Decode(this);
					int distance = PlaneCodeToDistance(width, CopyDistance(distanceSymbol));
					if (distance > at || at + length > total)
						return null;
					for (int i = 0; i < length; i++)
					{
						pixels[at] = pixels[at - distance];
						at++;
					}
				}
				else if (code < cacheLimit)
				{
					while (lastCached < at)
						cache[(0x1e35a7bd * pixels[lastCached++]) >> cacheShift] = pixels[lastCached - 1];
					pixels[at++] = cache[code - lengthLimit];
				}
				else
					return null;
				if (cache != null)
				{
					while (lastCached < at)
						cache[(0x1e35a7bd * pixels[lastCached++]) >> cacheShift] = pixels[lastCached - 1];
				}
			}
			return pixels;
		}

		private int CopyDistance(int symbol)
		{
			if (symbol < 4)
				return symbol + 1;
			int extraBits = (symbol - 2) >> 1;
			int offset = (2 + (symbol & 1)) << extraBits;
			return offset + ReadBits(extraBits) + 1;
		}

		private static int PlaneCodeToDistance(int width, int planeCode)
		{
			if (planeCode > 120)
				return planeCode - 120;
			int code = s_codeToPlane[planeCode - 1];
			int yOffset = code >> 4;
			int xOffset = 8 - (code & 0xf);
			int distance = yOffset * width + xOffset;
			return distance >= 1 ? distance : 1;
		}

		// A palette of up to 256 colours delta-coded, padded to the size the packed index width needs
		private static uint[] ExpandPalette(uint[] palette, int colours, int bits)
		{
			int finalColours = 1 << (8 >> bits);
			uint[] expanded = new uint[Math.Max(finalColours, colours)];
			expanded[0] = palette[0];
			for (int i = 1; i < colours; i++)
			{
				uint previous = expanded[i - 1];
				uint delta = palette[i];
				uint a = ((delta >> 24) + (previous >> 24)) & 0xff;
				uint r = (((delta >> 16) & 0xff) + ((previous >> 16) & 0xff)) & 0xff;
				uint g = (((delta >> 8) & 0xff) + ((previous >> 8) & 0xff)) & 0xff;
				uint b = ((delta & 0xff) + (previous & 0xff)) & 0xff;
				expanded[i] = a << 24 | r << 16 | g << 8 | b;
			}
			return expanded;
		}

		private static uint[] ApplyInverse(Transform transform, uint[] pixels, int height)
		{
			switch (transform.Type)
			{
				case 0:
					InversePredictor(transform, pixels, height);
					return pixels;
				case 1:
					InverseColor(transform, pixels, height);
					return pixels;
				case 2:
					for (int i = 0; i < pixels.Length; i++)
					{
						uint argb = pixels[i];
						uint green = (argb >> 8) & 0xff;
						uint redBlue = argb & 0x00ff00ffu;
						redBlue += (green << 16) | green;
						redBlue &= 0x00ff00ffu;
						pixels[i] = (argb & 0xff00ff00u) | redBlue;
					}
					return pixels;
				default:
					return InversePalette(transform, pixels, height);
			}
		}

		private static void InversePredictor(Transform transform, uint[] pixels, int height)
		{
			int width = transform.Width;
			int tilesPerRow = SubSampleSize(width, transform.Bits);
			pixels[0] = AddPixels(pixels[0], 0xff000000u);
			for (int x = 1; x < width; x++)
				pixels[x] = AddPixels(pixels[x], pixels[x - 1]);
			for (int y = 1; y < height; y++)
			{
				int row = y * width;
				pixels[row] = AddPixels(pixels[row], pixels[row - width]);
				int tileRow = (y >> transform.Bits) * tilesPerRow;
				for (int x = 1; x < width; x++)
				{
					int mode = (int)((transform.Data[tileRow + (x >> transform.Bits)] >> 8) & 0xf);
					int at = row + x;
					uint left = pixels[at - 1];
					uint top = pixels[at - width];
					uint topLeft = pixels[at - width - 1];
					uint topRight = x + 1 < width ? pixels[at - width + 1] : pixels[row];
					uint predicted;
					switch (mode)
					{
						case 0: predicted = 0xff000000u; break;
						case 1: predicted = left; break;
						case 2: predicted = top; break;
						case 3: predicted = topRight; break;
						case 4: predicted = topLeft; break;
						case 5: predicted = Average2(Average2(left, topRight), top); break;
						case 6: predicted = Average2(left, topLeft); break;
						case 7: predicted = Average2(left, top); break;
						case 8: predicted = Average2(topLeft, top); break;
						case 9: predicted = Average2(top, topRight); break;
						case 10: predicted = Average2(Average2(left, topLeft), Average2(top, topRight)); break;
						case 11: predicted = Select(top, left, topLeft); break;
						case 12: predicted = ClampedAddSubtractFull(left, top, topLeft); break;
						default: predicted = ClampedAddSubtractHalf(left, top, topLeft); break;
					}
					pixels[at] = AddPixels(pixels[at], predicted);
				}
			}
		}

		// The top-right of the last pixel in a row is the first pixel of the current row, as the spec's wrapped addressing has it
		private static uint AddPixels(uint a, uint b)
		{
			uint alphaGreen = (a & 0xff00ff00u) + (b & 0xff00ff00u);
			uint redBlue = (a & 0x00ff00ffu) + (b & 0x00ff00ffu);
			return (alphaGreen & 0xff00ff00u) | (redBlue & 0x00ff00ffu);
		}

		private static uint Average2(uint a, uint b) => (((a ^ b) & 0xfefefefeu) >> 1) + (a & b);

		private static uint Select(uint a, uint b, uint c)
		{
			int paMinusPb = Sub3((int)(a >> 24), (int)(b >> 24), (int)(c >> 24)) + Sub3((int)(a >> 16) & 0xff, (int)(b >> 16) & 0xff, (int)(c >> 16) & 0xff) + Sub3((int)(a >> 8) & 0xff, (int)(b >> 8) & 0xff, (int)(c >> 8) & 0xff) + Sub3((int)a & 0xff, (int)b & 0xff, (int)c & 0xff);
			return paMinusPb <= 0 ? a : b;
		}

		private static int Sub3(int a, int b, int c) => Math.Abs(b - c) - Math.Abs(a - c);

		private static uint Clip255(int value) => (uint)(value < 0 ? 0 : value > 255 ? 255 : value);

		private static uint ClampedAddSubtractFull(uint c0, uint c1, uint c2)
		{
			uint a = Clip255((int)(c0 >> 24) + (int)(c1 >> 24) - (int)(c2 >> 24));
			uint r = Clip255((int)((c0 >> 16) & 0xff) + (int)((c1 >> 16) & 0xff) - (int)((c2 >> 16) & 0xff));
			uint g = Clip255((int)((c0 >> 8) & 0xff) + (int)((c1 >> 8) & 0xff) - (int)((c2 >> 8) & 0xff));
			uint b = Clip255((int)(c0 & 0xff) + (int)(c1 & 0xff) - (int)(c2 & 0xff));
			return a << 24 | r << 16 | g << 8 | b;
		}

		private static uint ClampedAddSubtractHalf(uint c0, uint c1, uint c2)
		{
			uint average = Average2(c0, c1);
			uint a = Clip255((int)(average >> 24) + ((int)(average >> 24) - (int)(c2 >> 24)) / 2);
			uint r = Clip255((int)((average >> 16) & 0xff) + ((int)((average >> 16) & 0xff) - (int)((c2 >> 16) & 0xff)) / 2);
			uint g = Clip255((int)((average >> 8) & 0xff) + ((int)((average >> 8) & 0xff) - (int)((c2 >> 8) & 0xff)) / 2);
			uint b = Clip255((int)(average & 0xff) + ((int)(average & 0xff) - (int)(c2 & 0xff)) / 2);
			return a << 24 | r << 16 | g << 8 | b;
		}

		private static void InverseColor(Transform transform, uint[] pixels, int height)
		{
			int width = transform.Width;
			int tilesPerRow = SubSampleSize(width, transform.Bits);
			for (int y = 0; y < height; y++)
			{
				int tileRow = (y >> transform.Bits) * tilesPerRow;
				for (int x = 0; x < width; x++)
				{
					uint code = transform.Data[tileRow + (x >> transform.Bits)];
					sbyte greenToRed = (sbyte)(code & 0xff);
					sbyte greenToBlue = (sbyte)((code >> 8) & 0xff);
					sbyte redToBlue = (sbyte)((code >> 16) & 0xff);
					uint argb = pixels[y * width + x];
					sbyte green = (sbyte)(argb >> 8);
					int red = (int)((argb >> 16) & 0xff);
					int blue = (int)(argb & 0xff);
					red = (red + ((greenToRed * green) >> 5)) & 0xff;
					blue += (greenToBlue * green) >> 5;
					blue += (redToBlue * (sbyte)red) >> 5;
					blue &= 0xff;
					pixels[y * width + x] = (argb & 0xff00ff00u) | (uint)red << 16 | (uint)blue;
				}
			}
		}

		// Packed palette indices in the green channel, several per pixel when the palette is small, back to colours
		private static uint[] InversePalette(Transform transform, uint[] packed, int height)
		{
			int width = transform.Width;
			int bits = transform.Bits;
			uint[] palette = transform.Data;
			uint[] pixels = new uint[width * height];
			if (bits == 0)
			{
				for (int i = 0; i < pixels.Length; i++)
				{
					int index = (int)((packed[i] >> 8) & 0xff);
					pixels[i] = index < palette.Length ? palette[index] : 0;
				}
				return pixels;
			}
			int packedWidth = SubSampleSize(width, bits);
			int perByte = 1 << bits;
			int bitsPerPixel = 8 >> bits;
			uint mask = (1u << bitsPerPixel) - 1;
			for (int y = 0; y < height; y++)
			{
				for (int x = 0; x < width; x++)
				{
					uint value = (packed[y * packedWidth + (x >> bits)] >> 8) & 0xff;
					int index = (int)((value >> ((x & (perByte - 1)) * bitsPerPixel)) & mask);
					pixels[y * width + x] = index < palette.Length ? palette[index] : 0;
				}
			}
			return pixels;
		}
	}
}
