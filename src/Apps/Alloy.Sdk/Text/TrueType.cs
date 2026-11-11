using System;
using System.Buffers.Binary;
using System.Collections.Generic;

namespace Alloy.Sdk.Text
{
	// One point of a glyph outline in font units; off-curve points are quadratic control points
	public struct OutlinePoint
	{
		public float X;
		public float Y;
		public bool OnCurve;
	}

	// A glyph's outline: points in font units, and where each contour ends
	public sealed class GlyphOutline
	{
		public static readonly GlyphOutline Empty = new GlyphOutline(new OutlinePoint[0], new int[0], 0, 0, 0, 0);

		public readonly OutlinePoint[] Points;
		public readonly int[] ContourEnds;
		public readonly int XMin;
		public readonly int YMin;
		public readonly int XMax;
		public readonly int YMax;

		public GlyphOutline(OutlinePoint[] points, int[] contourEnds, int xMin, int yMin, int xMax, int yMax)
		{
			Points = points;
			ContourEnds = contourEnds;
			XMin = xMin;
			YMin = yMin;
			XMax = xMax;
			YMax = yMax;
		}
	}

	// A TrueType font file: the tables a renderer needs (head, hhea, hmtx, maxp, cmap, loca, glyf, kern), parsed on demand from the bytes
	public sealed class TrueTypeFont
	{
		private readonly byte[] _data;
		private readonly Dictionary<uint, (int Offset, int Length)> _tables = new Dictionary<uint, (int, int)>();
		private readonly int _loca;
		private readonly int _glyf;
		private readonly int _hmtx;
		private readonly int _cmap;
		private readonly bool _longLoca;
		private readonly int _numberOfHMetrics;
		private readonly Dictionary<int, int> _kerning;
		private readonly Dictionary<int, GlyphOutline> _outlines = new Dictionary<int, GlyphOutline>();
		private readonly Dictionary<int, int> _glyphIds = new Dictionary<int, int>();

		public readonly int UnitsPerEm;
		public readonly int Ascender;
		public readonly int Descender;
		public readonly int LineGap;
		public readonly int GlyphCount;

		private TrueTypeFont(byte[] data)
		{
			_data = data;
			int tableCount = BinaryPrimitives.ReadUInt16BigEndian(data.AsSpan(4));
			for (int i = 0; i < tableCount; i++)
			{
				int record = 12 + 16 * i;
				uint tag = BinaryPrimitives.ReadUInt32BigEndian(data.AsSpan(record));
				int offset = (int)BinaryPrimitives.ReadUInt32BigEndian(data.AsSpan(record + 8));
				int length = (int)BinaryPrimitives.ReadUInt32BigEndian(data.AsSpan(record + 12));
				if (offset >= 0 && length >= 0 && offset + length <= data.Length)
					_tables[tag] = (offset, length);
			}

			int head = Table("head");
			UnitsPerEm = BinaryPrimitives.ReadUInt16BigEndian(data.AsSpan(head + 18));
			_longLoca = BinaryPrimitives.ReadInt16BigEndian(data.AsSpan(head + 50)) != 0;
			int hhea = Table("hhea");
			Ascender = BinaryPrimitives.ReadInt16BigEndian(data.AsSpan(hhea + 4));
			Descender = BinaryPrimitives.ReadInt16BigEndian(data.AsSpan(hhea + 6));
			LineGap = BinaryPrimitives.ReadInt16BigEndian(data.AsSpan(hhea + 8));
			_numberOfHMetrics = BinaryPrimitives.ReadUInt16BigEndian(data.AsSpan(hhea + 34));
			GlyphCount = BinaryPrimitives.ReadUInt16BigEndian(data.AsSpan(Table("maxp") + 4));
			_hmtx = Table("hmtx");
			_loca = Table("loca");
			_glyf = Table("glyf");
			_cmap = FindCmapSubtable();
			_kerning = ReadKerning();
		}

		// False when the bytes aren't a TrueType font with outlines (an OpenType CFF font has none)
		public static bool TryLoad(byte[] data, out TrueTypeFont font)
		{
			font = null;
			if (data == null || data.Length < 12)
				return false;
			uint version = BinaryPrimitives.ReadUInt32BigEndian(data);
			if (version != 0x00010000 && version != 0x74727565)
				return false;
			TrueTypeFont parsed = new TrueTypeFont(data);
			if (parsed._glyf < 0 || parsed._loca < 0 || parsed._cmap < 0 || parsed.UnitsPerEm == 0)
				return false;
			font = parsed;
			return true;
		}

		private int Table(string name)
		{
			uint tag = (uint)name[0] << 24 | (uint)name[1] << 16 | (uint)name[2] << 8 | name[3];
			return _tables.TryGetValue(tag, out (int Offset, int Length) table) ? table.Offset : -1;
		}

		// The glyph for a code point, 0 (the missing glyph) when the font has none
		public int GlyphId(int codePoint)
		{
			if (_glyphIds.TryGetValue(codePoint, out int cached))
				return cached;
			int id = LookupGlyph(codePoint);
			_glyphIds[codePoint] = id;
			return id;
		}

		public int AdvanceWidth(int glyphId)
		{
			int metric = Math.Min(glyphId, _numberOfHMetrics - 1);
			return metric < 0 ? 0 : BinaryPrimitives.ReadUInt16BigEndian(_data.AsSpan(_hmtx + 4 * metric));
		}

		public int Kerning(int leftGlyph, int rightGlyph) =>
			_kerning != null && _kerning.TryGetValue(leftGlyph << 16 | rightGlyph, out int value) ? value : 0;

		public GlyphOutline Outline(int glyphId)
		{
			if (_outlines.TryGetValue(glyphId, out GlyphOutline cached))
				return cached;
			List<OutlinePoint> points = new List<OutlinePoint>();
			List<int> ends = new List<int>();
			int xMin = 0;
			int yMin = 0;
			int xMax = 0;
			int yMax = 0;
			AppendGlyph(glyphId, points, ends, 1, 0, 0, 1, 0, 0, 0, ref xMin, ref yMin, ref xMax, ref yMax);
			GlyphOutline outline = points.Count == 0 ? GlyphOutline.Empty : new GlyphOutline(points.ToArray(), ends.ToArray(), xMin, yMin, xMax, yMax);
			_outlines[glyphId] = outline;
			return outline;
		}

		private (int Offset, int Length) GlyphLocation(int glyphId)
		{
			if (glyphId < 0 || glyphId >= GlyphCount)
				return (0, 0);
			int start;
			int end;
			if (_longLoca)
			{
				start = (int)BinaryPrimitives.ReadUInt32BigEndian(_data.AsSpan(_loca + 4 * glyphId));
				end = (int)BinaryPrimitives.ReadUInt32BigEndian(_data.AsSpan(_loca + 4 * glyphId + 4));
			}
			else
			{
				start = 2 * BinaryPrimitives.ReadUInt16BigEndian(_data.AsSpan(_loca + 2 * glyphId));
				end = 2 * BinaryPrimitives.ReadUInt16BigEndian(_data.AsSpan(_loca + 2 * glyphId + 2));
			}
			return end > start && _glyf + end <= _data.Length ? (_glyf + start, end - start) : (0, 0);
		}

		// A simple glyph's points, or a composite's components each through its own transform, appended with the running bounds (a composite nests at most a few levels)
		private void AppendGlyph(int glyphId, List<OutlinePoint> points, List<int> ends, float a, float b, float c, float d, float dx, float dy, int depth, ref int xMin, ref int yMin, ref int xMax, ref int yMax)
		{
			(int offset, int length) = GlyphLocation(glyphId);
			if (length < 10 || depth > 4)
				return;
			ReadOnlySpan<byte> glyph = _data.AsSpan(offset, length);
			int contours = BinaryPrimitives.ReadInt16BigEndian(glyph);
			if (points.Count == 0)
			{
				xMin = BinaryPrimitives.ReadInt16BigEndian(glyph.Slice(2));
				yMin = BinaryPrimitives.ReadInt16BigEndian(glyph.Slice(4));
				xMax = BinaryPrimitives.ReadInt16BigEndian(glyph.Slice(6));
				yMax = BinaryPrimitives.ReadInt16BigEndian(glyph.Slice(8));
			}
			if (contours >= 0)
			{
				AppendSimple(glyph, contours, points, ends, a, b, c, d, dx, dy, ref xMin, ref yMin, ref xMax, ref yMax);
				return;
			}

			int at = 10;
			while (at + 4 <= glyph.Length)
			{
				int flags = BinaryPrimitives.ReadUInt16BigEndian(glyph.Slice(at));
				int component = BinaryPrimitives.ReadUInt16BigEndian(glyph.Slice(at + 2));
				at += 4;
				float ox;
				float oy;
				if ((flags & 1) != 0)
				{
					ox = BinaryPrimitives.ReadInt16BigEndian(glyph.Slice(at));
					oy = BinaryPrimitives.ReadInt16BigEndian(glyph.Slice(at + 2));
					at += 4;
				}
				else
				{
					ox = (sbyte)glyph[at];
					oy = (sbyte)glyph[at + 1];
					at += 2;
				}
				float ca = 1;
				float cb = 0;
				float cc = 0;
				float cd = 1;
				if ((flags & 8) != 0)
				{
					ca = cd = F2Dot14(glyph.Slice(at));
					at += 2;
				}
				else if ((flags & 0x40) != 0)
				{
					ca = F2Dot14(glyph.Slice(at));
					cd = F2Dot14(glyph.Slice(at + 2));
					at += 4;
				}
				else if ((flags & 0x80) != 0)
				{
					ca = F2Dot14(glyph.Slice(at));
					cb = F2Dot14(glyph.Slice(at + 2));
					cc = F2Dot14(glyph.Slice(at + 4));
					cd = F2Dot14(glyph.Slice(at + 6));
					at += 8;
				}

				// Point-matching offsets (ARGS_ARE_XY_VALUES clear) are rare; they are treated as zero offsets
				if ((flags & 2) == 0)
				{
					ox = 0;
					oy = 0;
				}
				float na = ca * a + cb * c;
				float nb = ca * b + cb * d;
				float nc = cc * a + cd * c;
				float nd = cc * b + cd * d;
				float ndx = ox * a + oy * c + dx;
				float ndy = ox * b + oy * d + dy;
				AppendGlyph(component, points, ends, na, nb, nc, nd, ndx, ndy, depth + 1, ref xMin, ref yMin, ref xMax, ref yMax);
				if ((flags & 0x20) == 0)
					break;
			}
		}

		private static void AppendSimple(ReadOnlySpan<byte> glyph, int contours, List<OutlinePoint> points, List<int> ends, float a, float b, float c, float d, float dx, float dy, ref int xMin, ref int yMin, ref int xMax, ref int yMax)
		{
			int at = 10;
			int first = points.Count;
			int pointCount = 0;
			for (int i = 0; i < contours; i++)
			{
				int end = BinaryPrimitives.ReadUInt16BigEndian(glyph.Slice(at));
				at += 2;
				ends.Add(first + end);
				pointCount = end + 1;
			}
			int instructions = BinaryPrimitives.ReadUInt16BigEndian(glyph.Slice(at));
			at += 2 + instructions;

			Span<byte> flags = pointCount <= 512 ? stackalloc byte[pointCount] : new byte[pointCount];
			for (int i = 0; i < pointCount && at < glyph.Length;)
			{
				byte flag = glyph[at++];
				flags[i++] = flag;
				if ((flag & 8) != 0 && at < glyph.Length)
				{
					int repeat = glyph[at++];
					for (int r = 0; r < repeat && i < pointCount; r++)
						flags[i++] = flag;
				}
			}

			Span<int> xs = pointCount <= 512 ? stackalloc int[pointCount] : new int[pointCount];
			Span<int> ys = pointCount <= 512 ? stackalloc int[pointCount] : new int[pointCount];
			int value = 0;
			for (int i = 0; i < pointCount; i++)
			{
				byte flag = flags[i];
				if ((flag & 2) != 0)
				{
					int delta = at < glyph.Length ? glyph[at++] : 0;
					value += (flag & 16) != 0 ? delta : -delta;
				}
				else if ((flag & 16) == 0 && at + 1 < glyph.Length)
				{
					value += BinaryPrimitives.ReadInt16BigEndian(glyph.Slice(at));
					at += 2;
				}
				xs[i] = value;
			}
			value = 0;
			for (int i = 0; i < pointCount; i++)
			{
				byte flag = flags[i];
				if ((flag & 4) != 0)
				{
					int delta = at < glyph.Length ? glyph[at++] : 0;
					value += (flag & 32) != 0 ? delta : -delta;
				}
				else if ((flag & 32) == 0 && at + 1 < glyph.Length)
				{
					value += BinaryPrimitives.ReadInt16BigEndian(glyph.Slice(at));
					at += 2;
				}
				ys[i] = value;
			}

			for (int i = 0; i < pointCount; i++)
			{
				float x = xs[i] * a + ys[i] * c + dx;
				float y = xs[i] * b + ys[i] * d + dy;
				points.Add(new OutlinePoint { X = x, Y = y, OnCurve = (flags[i] & 1) != 0 });
				xMin = Math.Min(xMin, (int)MathF.Floor(x));
				yMin = Math.Min(yMin, (int)MathF.Floor(y));
				xMax = Math.Max(xMax, (int)MathF.Ceiling(x));
				yMax = Math.Max(yMax, (int)MathF.Ceiling(y));
			}
		}

		private static float F2Dot14(ReadOnlySpan<byte> bytes) => BinaryPrimitives.ReadInt16BigEndian(bytes) / 16384f;

		// The Unicode subtable to use: a full-repertoire format 12 first, then the BMP format 4
		private int FindCmapSubtable()
		{
			int cmap = Table("cmap");
			if (cmap < 0)
				return -1;
			int count = BinaryPrimitives.ReadUInt16BigEndian(_data.AsSpan(cmap + 2));
			int format4 = -1;
			int format12 = -1;
			for (int i = 0; i < count; i++)
			{
				int record = cmap + 4 + 8 * i;
				int platform = BinaryPrimitives.ReadUInt16BigEndian(_data.AsSpan(record));
				int encoding = BinaryPrimitives.ReadUInt16BigEndian(_data.AsSpan(record + 2));
				int offset = cmap + (int)BinaryPrimitives.ReadUInt32BigEndian(_data.AsSpan(record + 4));
				if (offset + 4 > _data.Length || (platform != 0 && platform != 3))
					continue;
				int format = BinaryPrimitives.ReadUInt16BigEndian(_data.AsSpan(offset));
				if (format == 12 && (platform == 0 || encoding == 10))
					format12 = offset;
				else if (format == 4 && (platform == 0 || encoding == 1))
					format4 = offset;
			}
			return format12 >= 0 ? format12 : format4;
		}

		private int LookupGlyph(int codePoint)
		{
			ReadOnlySpan<byte> data = _data;
			int format = BinaryPrimitives.ReadUInt16BigEndian(data.Slice(_cmap));
			if (format == 12)
			{
				int groups = (int)BinaryPrimitives.ReadUInt32BigEndian(data.Slice(_cmap + 12));
				int low = 0;
				int high = groups - 1;
				while (low <= high)
				{
					int middle = (low + high) / 2;
					int group = _cmap + 16 + 12 * middle;
					uint start = BinaryPrimitives.ReadUInt32BigEndian(data.Slice(group));
					uint end = BinaryPrimitives.ReadUInt32BigEndian(data.Slice(group + 4));
					if (codePoint < start)
						high = middle - 1;
					else if (codePoint > end)
						low = middle + 1;
					else
						return (int)(BinaryPrimitives.ReadUInt32BigEndian(data.Slice(group + 8)) + ((uint)codePoint - start));
				}
				return 0;
			}

			if (codePoint > 0xFFFF)
				return 0;
			int segments = BinaryPrimitives.ReadUInt16BigEndian(data.Slice(_cmap + 6)) / 2;
			int endCodes = _cmap + 14;
			int startCodes = endCodes + 2 * segments + 2;
			int deltas = startCodes + 2 * segments;
			int rangeOffsets = deltas + 2 * segments;
			for (int i = 0; i < segments; i++)
			{
				int end = BinaryPrimitives.ReadUInt16BigEndian(data.Slice(endCodes + 2 * i));
				if (codePoint > end)
					continue;
				int start = BinaryPrimitives.ReadUInt16BigEndian(data.Slice(startCodes + 2 * i));
				if (codePoint < start)
					return 0;
				int delta = BinaryPrimitives.ReadInt16BigEndian(data.Slice(deltas + 2 * i));
				int rangeOffset = BinaryPrimitives.ReadUInt16BigEndian(data.Slice(rangeOffsets + 2 * i));
				if (rangeOffset == 0)
					return (codePoint + delta) & 0xFFFF;
				int address = rangeOffsets + 2 * i + rangeOffset + 2 * (codePoint - start);
				if (address + 2 > data.Length)
					return 0;
				int glyph = BinaryPrimitives.ReadUInt16BigEndian(data.Slice(address));
				return glyph == 0 ? 0 : (glyph + delta) & 0xFFFF;
			}
			return 0;
		}

		// The classic kern table's format 0 pairs; fonts that kern only through GPOS get none
		private Dictionary<int, int> ReadKerning()
		{
			int kern = Table("kern");
			if (kern < 0 || kern + 4 > _data.Length)
				return null;
			int tables = BinaryPrimitives.ReadUInt16BigEndian(_data.AsSpan(kern + 2));
			int at = kern + 4;
			Dictionary<int, int> pairs = null;
			for (int t = 0; t < tables && at + 6 <= _data.Length; t++)
			{
				int length = BinaryPrimitives.ReadUInt16BigEndian(_data.AsSpan(at + 2));
				int coverage = BinaryPrimitives.ReadUInt16BigEndian(_data.AsSpan(at + 4));
				if ((coverage >> 8) == 0 && (coverage & 1) != 0 && at + 14 <= _data.Length)
				{
					int count = BinaryPrimitives.ReadUInt16BigEndian(_data.AsSpan(at + 6));
					pairs ??= new Dictionary<int, int>(count);
					for (int i = 0; i < count && at + 14 + 6 * i + 6 <= _data.Length; i++)
					{
						int pair = at + 14 + 6 * i;
						int left = BinaryPrimitives.ReadUInt16BigEndian(_data.AsSpan(pair));
						int right = BinaryPrimitives.ReadUInt16BigEndian(_data.AsSpan(pair + 2));
						pairs[left << 16 | right] = BinaryPrimitives.ReadInt16BigEndian(_data.AsSpan(pair + 4));
					}
				}
				at += Math.Max(length, 6);
			}
			return pairs;
		}
	}
}
