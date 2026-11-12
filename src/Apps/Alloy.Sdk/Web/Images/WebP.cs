using System;

namespace Alloy.Sdk.Web.Images
{
	// WebP: the RIFF container around a VP8 key frame (lossy), with the extended header's alpha plane when it is stored raw
	public static class WebP
	{
		public static bool IsWebP(ReadOnlySpan<byte> data) => data.Length >= 12 && data[0] == 'R' && data[1] == 'I' && data[2] == 'F' && data[3] == 'F' && data[8] == 'W' && data[9] == 'E' && data[10] == 'B' && data[11] == 'P';

		public static bool TryDecode(ReadOnlySpan<byte> data, out Bitmap bitmap, out string error)
		{
			bitmap = null;
			error = null;
			if (!IsWebP(data))
			{
				error = "not a WebP file";
				return false;
			}
			int at = 12;
			ReadOnlySpan<byte> alpha = default;
			int alphaWidth = 0;
			int alphaHeight = 0;
			while (at + 8 <= data.Length)
			{
				string tag = ((char)data[at]).ToString() + (char)data[at + 1] + (char)data[at + 2] + (char)data[at + 3];
				int size = data[at + 4] | data[at + 5] << 8 | data[at + 6] << 16 | data[at + 7] << 24;
				int start = at + 8;
				if (size < 0 || start + size > data.Length)
				{
					error = "truncated WebP chunk " + tag;
					return false;
				}
				ReadOnlySpan<byte> payload = data.Slice(start, size);
				switch (tag)
				{
					case "VP8X":
						if (size >= 10)
						{
							alphaWidth = 1 + (payload[4] | payload[5] << 8 | payload[6] << 16);
							alphaHeight = 1 + (payload[7] | payload[8] << 8 | payload[9] << 16);
						}
						break;
					case "ALPH":
						alpha = payload;
						break;
					case "VP8 ":
					{
						if (!Vp8Decoder.TryDecode(payload, out bitmap, out error))
							return false;
						if (alpha.Length > 0 && alphaWidth == bitmap.Width && alphaHeight == bitmap.Height && !ApplyAlpha(bitmap, alpha))
						{
							bitmap = null;
							error = "WebP with losslessly compressed alpha is not supported yet";
							return false;
						}
						return true;
					}
					case "VP8L":
						error = "lossless WebP is not supported yet";
						return false;
				}
				at = start + size + (size & 1);
			}
			error = "WebP without an image chunk";
			return false;
		}

		// The ALPH chunk: a method byte, then raw alpha rows, optionally predicted from neighbours; false when the plane is compressed, which needs the lossless decoder
		private static bool ApplyAlpha(Bitmap bitmap, ReadOnlySpan<byte> chunk)
		{
			int compression = chunk[0] & 3;
			int filter = (chunk[0] >> 2) & 3;
			int width = bitmap.Width;
			int height = bitmap.Height;
			if (compression != 0 || chunk.Length < 1 + width * height)
				return false;
			byte[] alpha = new byte[width * height];
			chunk.Slice(1, width * height).CopyTo(alpha);
			if (filter != 0)
			{
				for (int y = 0; y < height; y++)
				{
					for (int x = 0; x < width; x++)
					{
						int predictor;
						if (x == 0 && y == 0)
							predictor = 0;
						else if (y == 0)
							predictor = alpha[x - 1];
						else if (x == 0)
							predictor = alpha[(y - 1) * width];
						else
						{
							int left = alpha[y * width + x - 1];
							int top = alpha[(y - 1) * width + x];
							int topLeft = alpha[(y - 1) * width + x - 1];
							predictor = filter == 1 ? left : filter == 2 ? top : Math.Clamp(left + top - topLeft, 0, 255);
						}
						alpha[y * width + x] = (byte)(alpha[y * width + x] + predictor);
					}
				}
			}
			uint[] pixels = bitmap.Pixels;
			for (int i = 0; i < pixels.Length; i++)
				pixels[i] = (pixels[i] & 0x00FFFFFF) | (uint)alpha[i] << 24;
			return true;
		}
	}

	// The VP8 boolean entropy decoder of RFC 6386 section 7
	internal sealed class BoolDecoder
	{
		private readonly byte[] _data;
		private int _at;
		private readonly int _end;
		private uint _value;
		private uint _range;
		private int _bitCount;

		public BoolDecoder(byte[] data, int start, int length)
		{
			_data = data;
			_at = start;
			_end = start + length;
			_value = (uint)(Next() << 8 | Next());
			_range = 255;
			_bitCount = 0;
		}

		private int Next() => _at < _end ? _data[_at++] : 0;

		public int Bool(int probability)
		{
			uint split = 1 + (((_range - 1) * (uint)probability) >> 8);
			uint bigSplit = split << 8;
			int result;
			if (_value >= bigSplit)
			{
				result = 1;
				_range -= split;
				_value -= bigSplit;
			}
			else
			{
				result = 0;
				_range = split;
			}
			while (_range < 128)
			{
				_value <<= 1;
				_range <<= 1;
				if (++_bitCount == 8)
				{
					_bitCount = 0;
					_value |= (uint)Next();
				}
			}
			return result;
		}

		public int Literal(int bits)
		{
			int value = 0;
			while (bits-- > 0)
				value = (value << 1) | Bool(128);
			return value;
		}

		public int Signed(int bits)
		{
			int value = Literal(bits);
			return Bool(128) != 0 ? -value : value;
		}

		// A value that may be absent: flag, magnitude, sign
		public int Optional(int bits) => Bool(128) != 0 ? Signed(bits) : 0;

		// The same walk over one row of a three-dimensional table, as the subblock mode probabilities are kept
		public int Tree(sbyte[] tree, byte[,,] probabilities, int first, int second)
		{
			int i = 0;
			while ((i = tree[i + Bool(probabilities[first, second, i >> 1])]) > 0)
			{
			}
			return -i;
		}

		public int Tree(sbyte[] tree, byte[] probabilities, int start = 0)
		{
			int i = start;
			while ((i = tree[i + Bool(probabilities[i >> 1])]) > 0)
			{
			}
			return -i;
		}
	}

	// A VP8 key frame decoded to 0xFFRRGGBB pixels: RFC 6386 intra prediction, DCT and WHT residuals, and the loop filter
	internal sealed class Vp8Decoder
	{
		private const int DcPred = 0, VPred = 1, HPred = 2, TmPred = 3, BPred = 4;
		private const int BDc = 0, BTm = 1, BVe = 2, BHe = 3, BRd = 4, BVr = 5, BLd = 6, BVl = 7, BHd = 8, BHu = 9;

		private static readonly sbyte[] s_kfYModeTree = { -BPred, 2, 4, 6, -DcPred, -VPred, -HPred, -TmPred };
		private static readonly byte[] s_kfYModeProbs = { 145, 156, 163, 128 };
		private static readonly sbyte[] s_uvModeTree = { -DcPred, 2, -VPred, 4, -HPred, -TmPred };
		private static readonly byte[] s_kfUvModeProbs = { 142, 114, 183 };
		private static readonly sbyte[] s_bModeTree = { -BDc, 2, -BTm, 4, -BVe, 6, 8, 12, -BHe, 10, -BRd, -BVr, -BLd, 14, -BVl, 16, -BHd, -BHu };
		private static readonly sbyte[] s_segmentTree = { 2, 4, 0, -1, -2, -3 };
		private static readonly byte[] s_zigzag = { 0, 1, 4, 8, 5, 2, 3, 6, 9, 12, 13, 10, 7, 11, 14, 15 };
		private static readonly byte[] s_bands = { 0, 1, 2, 3, 6, 4, 5, 6, 6, 6, 6, 6, 6, 6, 6, 7, 0 };
		private static readonly byte[] s_cat3 = { 173, 148, 140 };
		private static readonly byte[] s_cat4 = { 176, 155, 140, 135 };
		private static readonly byte[] s_cat5 = { 180, 157, 141, 134, 130 };
		private static readonly byte[] s_cat6 = { 254, 254, 243, 230, 196, 177, 153, 140, 133, 130, 129 };
		private static readonly byte[][] s_cats = { s_cat3, s_cat4, s_cat5, s_cat6 };

		private static readonly byte[] s_dcQuant =
		{
			4, 5, 6, 7, 8, 9, 10, 10, 11, 12, 13, 14, 15, 16, 17, 17, 18, 19, 20, 20, 21, 21, 22, 22, 23, 23, 24, 25, 25, 26, 27, 28,
			29, 30, 31, 32, 33, 34, 35, 36, 37, 37, 38, 39, 40, 41, 42, 43, 44, 45, 46, 46, 47, 48, 49, 50, 51, 52, 53, 54, 55, 56, 57, 58,
			59, 60, 61, 62, 63, 64, 65, 66, 67, 68, 69, 70, 71, 72, 73, 74, 75, 76, 76, 77, 78, 79, 80, 81, 82, 83, 84, 85, 86, 87, 88, 89,
			91, 93, 95, 96, 98, 100, 101, 102, 104, 106, 108, 110, 112, 114, 116, 118, 122, 124, 126, 128, 130, 132, 134, 136, 138, 140, 143, 145, 148, 151, 154, 157,
		};

		private static readonly ushort[] s_acQuant =
		{
			4, 5, 6, 7, 8, 9, 10, 11, 12, 13, 14, 15, 16, 17, 18, 19, 20, 21, 22, 23, 24, 25, 26, 27, 28, 29, 30, 31, 32, 33, 34, 35,
			36, 37, 38, 39, 40, 41, 42, 43, 44, 45, 46, 47, 48, 49, 50, 51, 52, 53, 54, 55, 56, 57, 58, 60, 62, 64, 66, 68, 70, 72, 74, 76,
			78, 80, 82, 84, 86, 88, 90, 92, 94, 96, 98, 100, 102, 104, 106, 108, 110, 112, 114, 116, 119, 122, 125, 128, 131, 134, 137, 140, 143, 146, 149, 152,
			155, 158, 161, 164, 167, 170, 173, 177, 181, 185, 189, 193, 197, 201, 205, 209, 213, 217, 221, 225, 229, 234, 239, 245, 249, 254, 259, 264, 269, 274, 279, 284,
		};

		private readonly byte[] _data;
		private int _width;
		private int _height;
		private int _mbWidth;
		private int _mbHeight;
		private byte[] _y;
		private byte[] _u;
		private byte[] _v;
		private int _yStride;
		private int _uvStride;

		private bool _segmentation;
		private bool _updateSegmentMap;
		private bool _segmentAbsolute;
		private readonly int[] _segmentQuant = new int[4];
		private readonly int[] _segmentFilter = new int[4];
		private readonly byte[] _segmentProbs = { 255, 255, 255 };
		private bool _simpleFilter;
		private int _filterLevel;
		private int _sharpness;
		private bool _filterAdjust;
		private readonly int[] _refDeltas = new int[4];
		private readonly int[] _modeDeltas = new int[4];
		private bool _skipEnabled;
		private int _skipProb;
		private readonly byte[,,,] _coeffProbs = new byte[4, 8, 3, 11];
		private readonly int[,] _quant = new int[4, 6];

		private byte[] _mbSegments;
		private byte[] _mbSkipsInner;
		private byte[] _mbFilterLevel;
		private byte[] _aboveModes;
		private readonly byte[] _leftModes = new byte[4];
		private byte[] _aboveNz;
		private byte[] _aboveNzDc;
		private int _leftNz;
		private int _leftNzDc;
		private readonly short[] _coeffs = new short[25 * 16];
		private readonly byte[] _block = new byte[16 * 16];

		private Vp8Decoder(byte[] data)
		{
			_data = data;
		}

		public static bool TryDecode(ReadOnlySpan<byte> payload, out Bitmap bitmap, out string error)
		{
			bitmap = null;
			error = null;
			try
			{
				Vp8Decoder decoder = new Vp8Decoder(payload.ToArray());
				return decoder.Decode(out bitmap, out error);
			}
			catch (Exception e) when (e is IndexOutOfRangeException || e is ArgumentException || e is OverflowException)
			{
				error = "corrupt VP8 data";
				return false;
			}
		}

		private bool Decode(out Bitmap bitmap, out string error)
		{
			bitmap = null;
			error = null;
			if (_data.Length < 10)
			{
				error = "VP8 frame too short";
				return false;
			}
			int tag = _data[0] | _data[1] << 8 | _data[2] << 16;
			bool keyFrame = (tag & 1) == 0;
			int firstPartSize = tag >> 5;
			if (!keyFrame)
			{
				error = "VP8 inter frames are not stills";
				return false;
			}
			if (_data[3] != 0x9d || _data[4] != 0x01 || _data[5] != 0x2a)
			{
				error = "VP8 start code missing";
				return false;
			}
			_width = (_data[6] | _data[7] << 8) & 0x3fff;
			_height = (_data[8] | _data[9] << 8) & 0x3fff;
			if (_width == 0 || _height == 0 || (long)_width * _height > ImageDecoder.MaxPixels)
			{
				error = "VP8 frame has an unusable size";
				return false;
			}
			if (10 + firstPartSize > _data.Length)
			{
				error = "VP8 first partition is truncated";
				return false;
			}
			_mbWidth = (_width + 15) >> 4;
			_mbHeight = (_height + 15) >> 4;
			_yStride = _mbWidth * 16;
			_uvStride = _mbWidth * 8;
			_y = new byte[_yStride * _mbHeight * 16];
			_u = new byte[_uvStride * _mbHeight * 8];
			_v = new byte[_uvStride * _mbHeight * 8];
			BoolDecoder header = new BoolDecoder(_data, 10, firstPartSize);
			ParseHeader(header);
			int partitionsStart = 10 + firstPartSize;
			int partitionCount = _partitionCount;
			int sizesLength = 3 * (partitionCount - 1);
			if (partitionsStart + sizesLength > _data.Length)
			{
				error = "VP8 partition table is truncated";
				return false;
			}
			BoolDecoder[] partitions = new BoolDecoder[partitionCount];
			int dataStart = partitionsStart + sizesLength;
			for (int p = 0; p < partitionCount; p++)
			{
				int size;
				if (p < partitionCount - 1)
				{
					int o = partitionsStart + 3 * p;
					size = _data[o] | _data[o + 1] << 8 | _data[o + 2] << 16;
				}
				else
					size = _data.Length - dataStart;
				size = Math.Max(0, Math.Min(size, _data.Length - dataStart));
				partitions[p] = new BoolDecoder(_data, dataStart, size);
				dataStart += size;
			}
			_mbSegments = new byte[_mbWidth * _mbHeight];
			_mbSkipsInner = new byte[_mbWidth * _mbHeight];
			_mbFilterLevel = new byte[_mbWidth * _mbHeight];
			_aboveModes = new byte[_mbWidth * 4];
			_aboveNz = new byte[_mbWidth];
			_aboveNzDc = new byte[_mbWidth];
			for (int mbY = 0; mbY < _mbHeight; mbY++)
			{
				BoolDecoder tokens = partitions[mbY % partitionCount];
				Array.Clear(_leftModes, 0, 4);
				_leftNz = 0;
				_leftNzDc = 0;
				for (int mbX = 0; mbX < _mbWidth; mbX++)
					DecodeMacroblock(header, tokens, mbX, mbY);
			}
			if (_filterLevel > 0)
				LoopFilter();
			bitmap = ToBitmap();
			return true;
		}

		private int _partitionCount = 1;

		private void ParseHeader(BoolDecoder b)
		{
			b.Literal(1);
			b.Literal(1);
			_segmentation = b.Bool(128) != 0;
			if (_segmentation)
			{
				_updateSegmentMap = b.Bool(128) != 0;
				bool updateData = b.Bool(128) != 0;
				if (updateData)
				{
					_segmentAbsolute = b.Bool(128) != 0;
					for (int i = 0; i < 4; i++)
						_segmentQuant[i] = b.Optional(7);
					for (int i = 0; i < 4; i++)
						_segmentFilter[i] = b.Optional(6);
				}
				if (_updateSegmentMap)
				{
					for (int i = 0; i < 3; i++)
						_segmentProbs[i] = (byte)(b.Bool(128) != 0 ? b.Literal(8) : 255);
				}
			}
			_simpleFilter = b.Bool(128) != 0;
			_filterLevel = b.Literal(6);
			_sharpness = b.Literal(3);
			_filterAdjust = b.Bool(128) != 0;
			if (_filterAdjust)
			{
				if (b.Bool(128) != 0)
				{
					for (int i = 0; i < 4; i++)
					{
						if (b.Bool(128) != 0)
							_refDeltas[i] = b.Signed(6);
					}
					for (int i = 0; i < 4; i++)
					{
						if (b.Bool(128) != 0)
							_modeDeltas[i] = b.Signed(6);
					}
				}
			}
			_partitionCount = 1 << b.Literal(2);
			int baseQ = b.Literal(7);
			int y1Dc = b.Optional(4);
			int y2Dc = b.Optional(4);
			int y2Ac = b.Optional(4);
			int uvDc = b.Optional(4);
			int uvAc = b.Optional(4);
			for (int s = 0; s < 4; s++)
			{
				int q = baseQ;
				if (_segmentation)
					q = _segmentAbsolute ? _segmentQuant[s] : baseQ + _segmentQuant[s];
				_quant[s, 0] = s_dcQuant[Clamp127(q + y1Dc)];
				_quant[s, 1] = s_acQuant[Clamp127(q)];
				_quant[s, 2] = s_dcQuant[Clamp127(q + y2Dc)] * 2;
				_quant[s, 3] = Math.Max(8, s_acQuant[Clamp127(q + y2Ac)] * 155 / 100);
				_quant[s, 4] = Math.Min(132, (int)s_dcQuant[Clamp127(q + uvDc)]);
				_quant[s, 5] = s_acQuant[Clamp127(q + uvAc)];
			}
			b.Literal(1);
			for (int i = 0; i < 4; i++)
				for (int j = 0; j < 8; j++)
					for (int k = 0; k < 3; k++)
						for (int l = 0; l < 11; l++)
							_coeffProbs[i, j, k, l] = b.Bool(Vp8Tables.CoeffUpdateProbs[i, j, k, l]) != 0 ? (byte)b.Literal(8) : Vp8Tables.DefaultCoeffProbs[i, j, k, l];
			_skipEnabled = b.Bool(128) != 0;
			_skipProb = _skipEnabled ? b.Literal(8) : 0;
		}

		private static int Clamp127(int value) => value < 0 ? 0 : value > 127 ? 127 : value;

		private void DecodeMacroblock(BoolDecoder header, BoolDecoder tokens, int mbX, int mbY)
		{
			int index = mbY * _mbWidth + mbX;
			int segment = 0;
			if (_updateSegmentMap)
				segment = header.Tree(s_segmentTree, _segmentProbs);
			_mbSegments[index] = (byte)segment;
			bool skip = _skipEnabled && header.Bool(_skipProb) != 0;
			int yMode = header.Tree(s_kfYModeTree, s_kfYModeProbs);
			byte[] subModes = new byte[16];
			if (yMode == BPred)
			{
				for (int i = 0; i < 16; i++)
				{
					int above = i < 4 ? _aboveModes[mbX * 4 + i] : subModes[i - 4];
					int left = (i & 3) == 0 ? _leftModes[i >> 2] : subModes[i - 1];
					subModes[i] = (byte)header.Tree(s_bModeTree, Vp8Tables.BModeProbs, above, left);
				}
			}
			else
			{
				byte implied = (byte)(yMode == DcPred ? BDc : yMode == VPred ? BVe : yMode == HPred ? BHe : BTm);
				for (int i = 0; i < 16; i++)
					subModes[i] = implied;
			}
			for (int i = 0; i < 4; i++)
			{
				_aboveModes[mbX * 4 + i] = subModes[12 + i];
				_leftModes[i] = subModes[i * 4 + 3];
			}
			int uvMode = header.Tree(s_uvModeTree, s_kfUvModeProbs);

			Array.Clear(_coeffs, 0, _coeffs.Length);
			bool hasCoefficients = false;
			if (!skip)
				hasCoefficients = DecodeResiduals(tokens, mbX, segment, yMode != BPred);
			else
			{
				_aboveNz[mbX] = 0;
				_leftNz = 0;
				if (yMode != BPred)
				{
					_aboveNzDc[mbX] = 0;
					_leftNzDc = 0;
				}
			}
			_mbSkipsInner[index] = (byte)(!hasCoefficients && yMode != BPred ? 1 : 0);
			int level = _filterLevel;
			if (_segmentation)
				level = _segmentAbsolute ? _segmentFilter[segment] : level + _segmentFilter[segment];
			if (_filterAdjust)
			{
				level += _refDeltas[0];
				if (yMode == BPred)
					level += _modeDeltas[0];
			}
			_mbFilterLevel[index] = (byte)Math.Clamp(level, 0, 63);

			Reconstruct(mbX, mbY, yMode, subModes, uvMode);
		}

		// The residual tokens of one macroblock: the Y2 block when the luma mode has one, then 16 Y, 4 U and 4 V blocks
		private bool DecodeResiduals(BoolDecoder b, int mbX, int segment, bool hasY2)
		{
			bool any = false;
			int firstCoefficient = 0;
			int yType = 3;
			if (hasY2)
			{
				int ctx = _aboveNzDc[mbX] + _leftNzDc;
				int count = DecodeBlock(b, 1, ctx, _quant[segment, 2], _quant[segment, 3], 0, 24 * 16);
				int nz = count > 0 ? 1 : 0;
				_aboveNzDc[mbX] = (byte)nz;
				_leftNzDc = nz;
				if (count > 0)
				{
					any = true;
					if (count > 1)
						InverseWalsh();
					else
					{
						short dc = (short)((_coeffs[24 * 16] + 3) >> 3);
						for (int i = 0; i < 16; i++)
							_coeffs[i * 16] = dc;
					}
				}
				firstCoefficient = 1;
				yType = 0;
			}
			// Each block's context counts the non-zero blocks above and left of it, inside this macroblock or in the neighbours at its edges
			int aboveBits = _aboveNz[mbX];
			int leftBits = _leftNz;
			Span<int> above = stackalloc int[4];
			Span<int> left = stackalloc int[4];
			for (int i = 0; i < 4; i++)
			{
				above[i] = (aboveBits >> i) & 1;
				left[i] = (leftBits >> i) & 1;
			}
			for (int y = 0; y < 4; y++)
			{
				for (int x = 0; x < 4; x++)
				{
					int count = DecodeBlock(b, yType, above[x] + left[y], _quant[segment, 0], _quant[segment, 1], firstCoefficient, (y * 4 + x) * 16);
					int nz = count > firstCoefficient ? 1 : 0;
					any |= nz != 0;
					above[x] = nz;
					left[y] = nz;
				}
			}
			int newAbove = above[0] | above[1] << 1 | above[2] << 2 | above[3] << 3;
			int newLeft = left[0] | left[1] << 1 | left[2] << 2 | left[3] << 3;
			for (int plane = 0; plane < 2; plane++)
			{
				int shift = 4 + plane * 2;
				for (int i = 0; i < 2; i++)
				{
					above[i] = (aboveBits >> (shift + i)) & 1;
					left[i] = (leftBits >> (shift + i)) & 1;
				}
				for (int y = 0; y < 2; y++)
				{
					for (int x = 0; x < 2; x++)
					{
						int count = DecodeBlock(b, 2, above[x] + left[y], _quant[segment, 4], _quant[segment, 5], 0, (16 + plane * 4 + y * 2 + x) * 16);
						int nz = count > 0 ? 1 : 0;
						any |= nz != 0;
						above[x] = nz;
						left[y] = nz;
					}
				}
				newAbove |= above[0] << shift | above[1] << (shift + 1);
				newLeft |= left[0] << shift | left[1] << (shift + 1);
			}
			_aboveNz[mbX] = (byte)newAbove;
			_leftNz = newLeft;
			return any;
		}

		// One 4x4 block's coefficients into _coeffs at offset, dequantized and in raster order; returns the index past the last token read
		private int DecodeBlock(BoolDecoder b, int type, int ctx, int dcQuant, int acQuant, int first, int offset)
		{
			int n = first;
			int band = s_bands[n];
			int c = ctx;
			if (b.Bool(_coeffProbs[type, band, c, 0]) == 0)
				return n;
			while (true)
			{
				while (b.Bool(_coeffProbs[type, band, c, 1]) == 0)
				{
					if (++n == 16)
						return 16;
					band = s_bands[n];
					c = 0;
				}
				int value;
				int nextCtx;
				if (b.Bool(_coeffProbs[type, band, c, 2]) == 0)
				{
					value = 1;
					nextCtx = 1;
				}
				else
				{
					value = LargeValue(b, type, band, c);
					nextCtx = 2;
				}
				if (b.Bool(128) != 0)
					value = -value;
				_coeffs[offset + s_zigzag[n]] = (short)(value * (n > 0 ? acQuant : dcQuant));
				if (++n == 16)
					return 16;
				band = s_bands[n];
				c = nextCtx;
				if (b.Bool(_coeffProbs[type, band, c, 0]) == 0)
					return n;
			}
		}

		private int LargeValue(BoolDecoder b, int type, int band, int c)
		{
			if (b.Bool(_coeffProbs[type, band, c, 3]) == 0)
			{
				if (b.Bool(_coeffProbs[type, band, c, 4]) == 0)
					return 2;
				return 3 + b.Bool(_coeffProbs[type, band, c, 5]);
			}
			if (b.Bool(_coeffProbs[type, band, c, 6]) == 0)
			{
				if (b.Bool(_coeffProbs[type, band, c, 7]) == 0)
					return 5 + b.Bool(159);
				int v = 7 + 2 * b.Bool(165);
				return v + b.Bool(145);
			}
			int bit1 = b.Bool(_coeffProbs[type, band, c, 8]);
			int bit0 = b.Bool(_coeffProbs[type, band, c, 9 + bit1]);
			int cat = 2 * bit1 + bit0;
			int extra = 0;
			foreach (byte probability in s_cats[cat])
				extra = extra + extra + b.Bool(probability);
			return extra + 3 + (8 << cat);
		}

		// The Y2 block's inverse Walsh-Hadamard transform, whose outputs are the DC terms of the 16 Y blocks
		private void InverseWalsh()
		{
			int o = 24 * 16;
			int[] tmp = new int[16];
			for (int i = 0; i < 4; i++)
			{
				int a0 = _coeffs[o + i] + _coeffs[o + 12 + i];
				int a1 = _coeffs[o + 4 + i] + _coeffs[o + 8 + i];
				int a2 = _coeffs[o + 4 + i] - _coeffs[o + 8 + i];
				int a3 = _coeffs[o + i] - _coeffs[o + 12 + i];
				tmp[i] = a0 + a1;
				tmp[8 + i] = a0 - a1;
				tmp[4 + i] = a3 + a2;
				tmp[12 + i] = a3 - a2;
			}
			for (int i = 0; i < 4; i++)
			{
				int dc = tmp[i * 4] + 3;
				int a0 = dc + tmp[i * 4 + 3];
				int a1 = tmp[i * 4 + 1] + tmp[i * 4 + 2];
				int a2 = tmp[i * 4 + 1] - tmp[i * 4 + 2];
				int a3 = dc - tmp[i * 4 + 3];
				_coeffs[(i * 4) * 16] = (short)((a0 + a1) >> 3);
				_coeffs[(i * 4 + 1) * 16] = (short)((a3 + a2) >> 3);
				_coeffs[(i * 4 + 2) * 16] = (short)((a0 - a1) >> 3);
				_coeffs[(i * 4 + 3) * 16] = (short)((a3 - a2) >> 3);
			}
		}

		// Prediction plus residual for the three planes of one macroblock, written straight into the unfiltered frame
		private void Reconstruct(int mbX, int mbY, int yMode, byte[] subModes, int uvMode)
		{
			int yBase = mbY * 16 * _yStride + mbX * 16;
			if (yMode == BPred)
			{
				for (int i = 0; i < 16; i++)
				{
					int bx = (i & 3) * 4;
					int by = (i >> 2) * 4;
					PredictSubblock(_y, _yStride, yBase + by * _yStride + bx, mbX, mbY, bx, by, subModes[i]);
					AddResidual(_y, _yStride, yBase + by * _yStride + bx, i * 16);
				}
			}
			else
			{
				PredictBlock(_y, _yStride, yBase, 16, mbX, mbY, yMode);
				for (int i = 0; i < 16; i++)
				{
					int bx = (i & 3) * 4;
					int by = (i >> 2) * 4;
					AddResidual(_y, _yStride, yBase + by * _yStride + bx, i * 16);
				}
			}
			int uvBase = mbY * 8 * _uvStride + mbX * 8;
			PredictBlock(_u, _uvStride, uvBase, 8, mbX, mbY, uvMode);
			PredictBlock(_v, _uvStride, uvBase, 8, mbX, mbY, uvMode);
			for (int i = 0; i < 4; i++)
			{
				int bx = (i & 1) * 4;
				int by = (i >> 1) * 4;
				AddResidual(_u, _uvStride, uvBase + by * _uvStride + bx, (16 + i) * 16);
				AddResidual(_v, _uvStride, uvBase + by * _uvStride + bx, (20 + i) * 16);
			}
		}

		// 16x16 luma or 8x8 chroma prediction from the row above and the column to the left, with 127 above the frame and 129 left of it
		private static void PredictBlock(byte[] plane, int stride, int at, int size, int mbX, int mbY, int mode)
		{
			bool hasAbove = mbY > 0;
			bool hasLeft = mbX > 0;
			switch (mode)
			{
				case DcPred:
				{
					int sum = 0;
					int count = 0;
					if (hasAbove)
					{
						for (int i = 0; i < size; i++)
							sum += plane[at - stride + i];
						count += size;
					}
					if (hasLeft)
					{
						for (int i = 0; i < size; i++)
							sum += plane[at + i * stride - 1];
						count += size;
					}
					int shift = size == 16 ? 4 : 3;
					int value = count == 0 ? 128 : count == size ? (sum + (size >> 1)) >> shift : (sum + size) >> (shift + 1);
					Fill(plane, stride, at, size, (byte)value);
					break;
				}
				case VPred:
					for (int y = 0; y < size; y++)
						for (int x = 0; x < size; x++)
							plane[at + y * stride + x] = hasAbove ? plane[at - stride + x] : (byte)127;
					break;
				case HPred:
					for (int y = 0; y < size; y++)
					{
						byte left = hasLeft ? plane[at + y * stride - 1] : (byte)129;
						for (int x = 0; x < size; x++)
							plane[at + y * stride + x] = left;
					}
					break;
				default:
				{
					int corner = !hasAbove ? 127 : !hasLeft ? 129 : plane[at - stride - 1];
					for (int y = 0; y < size; y++)
					{
						int left = hasLeft ? plane[at + y * stride - 1] : 129;
						for (int x = 0; x < size; x++)
						{
							int above = hasAbove ? plane[at - stride + x] : 127;
							plane[at + y * stride + x] = Clip(left + above - corner);
						}
					}
					break;
				}
			}
		}

		private static void Fill(byte[] plane, int stride, int at, int size, byte value)
		{
			for (int y = 0; y < size; y++)
				for (int x = 0; x < size; x++)
					plane[at + y * stride + x] = value;
		}

		// The ten 4x4 luma predictors, from the above row A (eight pixels, the last four possibly borrowed from the macroblock's top-right), the left column L and the corner P
		private void PredictSubblock(byte[] plane, int stride, int at, int mbX, int mbY, int bx, int by, int mode)
		{
			Span<int> a = stackalloc int[8];
			Span<int> l = stackalloc int[4];
			bool topRow = mbY == 0 && by == 0;
			bool leftColumn = mbX == 0 && bx == 0;
			for (int i = 0; i < 4; i++)
			{
				a[i] = topRow ? 127 : plane[at - stride + i];
				l[i] = leftColumn ? 129 : plane[at + i * stride - 1];
			}
			if (topRow)
			{
				for (int i = 4; i < 8; i++)
					a[i] = 127;
			}
			else if (bx == 12)
			{
				// The rightmost subblocks all borrow the pixels above and right of the macroblock; past the frame edge the last above pixel repeats
				int mbRowAbove = mbY * 16 * stride - stride + mbX * 16;
				if (mbY == 0)
				{
					for (int i = 4; i < 8; i++)
						a[i] = 127;
				}
				else if (mbX == _mbWidth - 1)
				{
					for (int i = 4; i < 8; i++)
						a[i] = plane[mbRowAbove + 15];
				}
				else
				{
					for (int i = 4; i < 8; i++)
						a[i] = plane[mbRowAbove + 16 + i - 4];
				}
			}
			else
			{
				for (int i = 4; i < 8; i++)
					a[i] = plane[at - stride + i];
			}
			int p = topRow ? 127 : leftColumn ? 129 : plane[at - stride - 1];
			Span<int> e = stackalloc int[9];
			e[0] = l[3];
			e[1] = l[2];
			e[2] = l[1];
			e[3] = l[0];
			e[4] = p;
			e[5] = a[0];
			e[6] = a[1];
			e[7] = a[2];
			e[8] = a[3];
			Span<byte> b = stackalloc byte[16];
			switch (mode)
			{
				case BDc:
				{
					int sum = 4;
					for (int i = 0; i < 4; i++)
						sum += a[i] + l[i];
					b.Fill((byte)(sum >> 3));
					break;
				}
				case BTm:
					for (int r = 0; r < 4; r++)
						for (int c = 0; c < 4; c++)
							b[r * 4 + c] = Clip(l[r] + a[c] - p);
					break;
				case BVe:
					for (int c = 0; c < 4; c++)
					{
						byte v = Avg3(c == 0 ? p : a[c - 1], a[c], a[c + 1]);
						for (int r = 0; r < 4; r++)
							b[r * 4 + c] = v;
					}
					break;
				case BHe:
				{
					byte r0 = Avg3(p, l[0], l[1]);
					byte r1 = Avg3(l[0], l[1], l[2]);
					byte r2 = Avg3(l[1], l[2], l[3]);
					byte r3 = Avg3(l[2], l[3], l[3]);
					for (int c = 0; c < 4; c++)
					{
						b[c] = r0;
						b[4 + c] = r1;
						b[8 + c] = r2;
						b[12 + c] = r3;
					}
					break;
				}
				case BLd:
					for (int r = 0; r < 4; r++)
						for (int c = 0; c < 4; c++)
							b[r * 4 + c] = r + c == 6 ? Avg3(a[6], a[7], a[7]) : Avg3(a[r + c], a[r + c + 1], a[r + c + 2]);
					break;
				case BRd:
					for (int r = 0; r < 4; r++)
						for (int c = 0; c < 4; c++)
							b[r * 4 + c] = Avg3(e[c - r + 3], e[c - r + 4], e[c - r + 5]);
					break;
				case BVr:
					b[12] = Avg3(e[1], e[2], e[3]);
					b[8] = Avg3(e[2], e[3], e[4]);
					b[13] = b[4] = Avg3(e[3], e[4], e[5]);
					b[9] = b[0] = Avg2(e[4], e[5]);
					b[14] = b[5] = Avg3(e[4], e[5], e[6]);
					b[10] = b[1] = Avg2(e[5], e[6]);
					b[15] = b[6] = Avg3(e[5], e[6], e[7]);
					b[11] = b[2] = Avg2(e[6], e[7]);
					b[7] = Avg3(e[6], e[7], e[8]);
					b[3] = Avg2(e[7], e[8]);
					break;
				case BVl:
					b[0] = Avg2(a[0], a[1]);
					b[4] = Avg3(a[0], a[1], a[2]);
					b[8] = b[1] = Avg2(a[1], a[2]);
					b[5] = b[12] = Avg3(a[1], a[2], a[3]);
					b[9] = b[2] = Avg2(a[2], a[3]);
					b[13] = b[6] = Avg3(a[2], a[3], a[4]);
					b[10] = b[3] = Avg2(a[3], a[4]);
					b[14] = b[7] = Avg3(a[3], a[4], a[5]);
					b[11] = Avg3(a[4], a[5], a[6]);
					b[15] = Avg3(a[5], a[6], a[7]);
					break;
				case BHd:
					b[12] = Avg2(e[0], e[1]);
					b[13] = Avg3(e[0], e[1], e[2]);
					b[8] = b[14] = Avg2(e[1], e[2]);
					b[9] = b[15] = Avg3(e[1], e[2], e[3]);
					b[10] = b[4] = Avg2(e[2], e[3]);
					b[11] = b[5] = Avg3(e[2], e[3], e[4]);
					b[6] = b[0] = Avg2(e[3], e[4]);
					b[7] = b[1] = Avg3(e[3], e[4], e[5]);
					b[2] = Avg3(e[4], e[5], e[6]);
					b[3] = Avg3(e[5], e[6], e[7]);
					break;
				default:
					b[0] = Avg2(l[0], l[1]);
					b[1] = Avg3(l[0], l[1], l[2]);
					b[2] = b[4] = Avg2(l[1], l[2]);
					b[3] = b[5] = Avg3(l[1], l[2], l[3]);
					b[6] = b[8] = Avg2(l[2], l[3]);
					b[7] = b[9] = Avg3(l[2], l[3], l[3]);
					b[10] = b[11] = b[12] = b[13] = b[14] = b[15] = (byte)l[3];
					break;
			}
			for (int r = 0; r < 4; r++)
				for (int c = 0; c < 4; c++)
					plane[at + r * stride + c] = b[r * 4 + c];
		}

		private static byte Avg2(int x, int y) => (byte)((x + y + 1) >> 1);

		private static byte Avg3(int x, int y, int z) => (byte)((x + 2 * y + z + 2) >> 2);

		private static byte Clip(int value) => (byte)(value < 0 ? 0 : value > 255 ? 255 : value);

		// The inverse DCT of one block added onto its prediction; a block with only a DC term takes the shortcut
		private void AddResidual(byte[] plane, int stride, int at, int offset)
		{
			bool anyAc = false;
			for (int i = 1; i < 16; i++)
			{
				if (_coeffs[offset + i] != 0)
				{
					anyAc = true;
					break;
				}
			}
			if (!anyAc)
			{
				int dc = _coeffs[offset];
				if (dc == 0)
					return;
				int delta = (dc + 4) >> 3;
				for (int y = 0; y < 4; y++)
					for (int x = 0; x < 4; x++)
						plane[at + y * stride + x] = Clip(plane[at + y * stride + x] + delta);
				return;
			}
			Span<int> tmp = stackalloc int[16];
			for (int i = 0; i < 4; i++)
			{
				int i0 = _coeffs[offset + i];
				int i1 = _coeffs[offset + 4 + i];
				int i2 = _coeffs[offset + 8 + i];
				int i3 = _coeffs[offset + 12 + i];
				int a = i0 + i2;
				int bb = i0 - i2;
				int c = Mul2(i1) - Mul1(i3);
				int d = Mul1(i1) + Mul2(i3);
				tmp[i * 4] = a + d;
				tmp[i * 4 + 1] = bb + c;
				tmp[i * 4 + 2] = bb - c;
				tmp[i * 4 + 3] = a - d;
			}
			for (int i = 0; i < 4; i++)
			{
				int dc = tmp[i] + 4;
				int a = dc + tmp[8 + i];
				int bb = dc - tmp[8 + i];
				int c = Mul2(tmp[4 + i]) - Mul1(tmp[12 + i]);
				int d = Mul1(tmp[4 + i]) + Mul2(tmp[12 + i]);
				int row = at + i * stride;
				plane[row] = Clip(plane[row] + ((a + d) >> 3));
				plane[row + 1] = Clip(plane[row + 1] + ((bb + c) >> 3));
				plane[row + 2] = Clip(plane[row + 2] + ((bb - c) >> 3));
				plane[row + 3] = Clip(plane[row + 3] + ((a - d) >> 3));
			}
		}

		private static int Mul1(int a) => ((a * 20091) >> 16) + a;

		private static int Mul2(int a) => (a * 35468) >> 16;

		// The in-loop deblocking filter over the finished frame, macroblock by macroblock in raster order as the encoder did
		private void LoopFilter()
		{
			for (int mbY = 0; mbY < _mbHeight; mbY++)
			{
				for (int mbX = 0; mbX < _mbWidth; mbX++)
				{
					int index = mbY * _mbWidth + mbX;
					int level = _mbFilterLevel[index];
					if (level == 0)
						continue;
					int interior = level;
					if (_sharpness > 0)
					{
						interior >>= _sharpness > 4 ? 2 : 1;
						if (interior > 9 - _sharpness)
							interior = 9 - _sharpness;
					}
					if (interior < 1)
						interior = 1;
					int hevThreshold = level >= 40 ? 2 : level >= 15 ? 1 : 0;
					int mbLimit = ((level + 2) * 2) + interior;
					int subLimit = (level * 2) + interior;
					bool inner = _mbSkipsInner[index] == 0;
					int yAt = mbY * 16 * _yStride + mbX * 16;
					int uvAt = mbY * 8 * _uvStride + mbX * 8;
					if (_simpleFilter)
					{
						if (mbX > 0)
							SimpleEdge(_y, yAt, 1, _yStride, mbLimit);
						if (inner)
						{
							for (int x = 4; x < 16; x += 4)
								SimpleEdge(_y, yAt + x, 1, _yStride, subLimit);
						}
						if (mbY > 0)
							SimpleEdge(_y, yAt, _yStride, 1, mbLimit);
						if (inner)
						{
							for (int y = 4; y < 16; y += 4)
								SimpleEdge(_y, yAt + y * _yStride, _yStride, 1, subLimit);
						}
						continue;
					}
					if (mbX > 0)
					{
						NormalEdge(_y, yAt, 1, _yStride, 16, mbLimit, interior, hevThreshold, true);
						NormalEdge(_u, uvAt, 1, _uvStride, 8, mbLimit, interior, hevThreshold, true);
						NormalEdge(_v, uvAt, 1, _uvStride, 8, mbLimit, interior, hevThreshold, true);
					}
					if (inner)
					{
						for (int x = 4; x < 16; x += 4)
							NormalEdge(_y, yAt + x, 1, _yStride, 16, subLimit, interior, hevThreshold, false);
						NormalEdge(_u, uvAt + 4, 1, _uvStride, 8, subLimit, interior, hevThreshold, false);
						NormalEdge(_v, uvAt + 4, 1, _uvStride, 8, subLimit, interior, hevThreshold, false);
					}
					if (mbY > 0)
					{
						NormalEdge(_y, yAt, _yStride, 1, 16, mbLimit, interior, hevThreshold, true);
						NormalEdge(_u, uvAt, _uvStride, 1, 8, mbLimit, interior, hevThreshold, true);
						NormalEdge(_v, uvAt, _uvStride, 1, 8, mbLimit, interior, hevThreshold, true);
					}
					if (inner)
					{
						for (int y = 4; y < 16; y += 4)
							NormalEdge(_y, yAt + y * _yStride, _yStride, 1, 16, subLimit, interior, hevThreshold, false);
						NormalEdge(_u, uvAt + 4 * _uvStride, _uvStride, 1, 8, subLimit, interior, hevThreshold, false);
						NormalEdge(_v, uvAt + 4 * _uvStride, _uvStride, 1, 8, subLimit, interior, hevThreshold, false);
					}
				}
			}
		}

		// An edge between pixels at - step and at, walked along the other axis; step crosses the edge, along runs down it
		private static void SimpleEdge(byte[] plane, int at, int step, int along, int limit)
		{
			for (int i = 0; i < 16; i++)
			{
				int pos = at + i * along;
				int p1 = plane[pos - 2 * step];
				int p0 = plane[pos - step];
				int q0 = plane[pos];
				int q1 = plane[pos + step];
				if (Math.Abs(p0 - q0) * 2 + (Math.Abs(p1 - q1) >> 1) > limit)
					continue;
				CommonAdjust(plane, pos, step, true);
			}
		}

		private static void NormalEdge(byte[] plane, int at, int step, int along, int length, int edgeLimit, int interior, int hevThreshold, bool macroblockEdge)
		{
			for (int i = 0; i < length; i++)
			{
				int pos = at + i * along;
				int p3 = plane[pos - 4 * step];
				int p2 = plane[pos - 3 * step];
				int p1 = plane[pos - 2 * step];
				int p0 = plane[pos - step];
				int q0 = plane[pos];
				int q1 = plane[pos + step];
				int q2 = plane[pos + 2 * step];
				int q3 = plane[pos + 3 * step];
				if (Math.Abs(p0 - q0) * 2 + (Math.Abs(p1 - q1) >> 1) > edgeLimit)
					continue;
				if (Math.Abs(p3 - p2) > interior || Math.Abs(p2 - p1) > interior || Math.Abs(p1 - p0) > interior || Math.Abs(q1 - q0) > interior || Math.Abs(q2 - q1) > interior || Math.Abs(q3 - q2) > interior)
					continue;
				bool hev = Math.Abs(p1 - p0) > hevThreshold || Math.Abs(q1 - q0) > hevThreshold;
				if (macroblockEdge)
				{
					if (hev)
					{
						CommonAdjust(plane, pos, step, true);
						continue;
					}
					int sp2 = p2 - 128, sp1 = p1 - 128, sp0 = p0 - 128, sq0 = q0 - 128, sq1 = q1 - 128, sq2 = q2 - 128;
					int w = Clamp128(Clamp128(sp1 - sq1) + 3 * (sq0 - sp0));
					int a = Clamp128((27 * w + 63) >> 7);
					plane[pos] = (byte)(Clamp128(sq0 - a) + 128);
					plane[pos - step] = (byte)(Clamp128(sp0 + a) + 128);
					a = Clamp128((18 * w + 63) >> 7);
					plane[pos + step] = (byte)(Clamp128(sq1 - a) + 128);
					plane[pos - 2 * step] = (byte)(Clamp128(sp1 + a) + 128);
					a = Clamp128((9 * w + 63) >> 7);
					plane[pos + 2 * step] = (byte)(Clamp128(sq2 - a) + 128);
					plane[pos - 3 * step] = (byte)(Clamp128(sp2 + a) + 128);
				}
				else
				{
					int a = (CommonAdjust(plane, pos, step, hev) + 1) >> 1;
					if (!hev)
					{
						plane[pos + step] = (byte)(Clamp128(q1 - 128 - a) + 128);
						plane[pos - 2 * step] = (byte)(Clamp128(p1 - 128 + a) + 128);
					}
				}
			}
		}

		// Moves p0 and q0 toward each other; returns the filter value the subblock filter spreads to the next pixels
		private static int CommonAdjust(byte[] plane, int pos, int step, bool useOuterTaps)
		{
			int p1 = plane[pos - 2 * step] - 128;
			int p0 = plane[pos - step] - 128;
			int q0 = plane[pos] - 128;
			int q1 = plane[pos + step] - 128;
			int a = Clamp128((useOuterTaps ? Clamp128(p1 - q1) : 0) + 3 * (q0 - p0));
			int f1 = Clamp128(a + 4) >> 3;
			int f2 = Clamp128(a + 3) >> 3;
			plane[pos] = (byte)(Clamp128(q0 - f1) + 128);
			plane[pos - step] = (byte)(Clamp128(p0 + f2) + 128);
			return f1;
		}

		private static int Clamp128(int value) => value < -128 ? -128 : value > 127 ? 127 : value;

		// BT.601 limited-range YUV to RGB, each chroma sample shared by its two by two luma pixels
		private Bitmap ToBitmap()
		{
			Bitmap bitmap = new Bitmap(_width, _height);
			uint[] pixels = bitmap.Pixels;
			for (int y = 0; y < _height; y++)
			{
				int yRow = y * _yStride;
				int uvRow = (y >> 1) * _uvStride;
				for (int x = 0; x < _width; x++)
				{
					int c = 298 * (_y[yRow + x] - 16);
					int d = _u[uvRow + (x >> 1)] - 128;
					int e = _v[uvRow + (x >> 1)] - 128;
					byte r = Clip((c + 409 * e + 128) >> 8);
					byte g = Clip((c - 100 * d - 208 * e + 128) >> 8);
					byte b = Clip((c + 516 * d + 128) >> 8);
					pixels[y * _width + x] = 0xFF000000u | (uint)r << 16 | (uint)g << 8 | b;
				}
			}
			return bitmap;
		}
	}
}
