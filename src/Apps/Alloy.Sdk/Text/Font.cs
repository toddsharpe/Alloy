using System;
using System.Collections.Generic;
using Alloy.Graphics;

namespace Alloy.Sdk.Text
{
	// One rasterized glyph at one size: an 8-bit coverage bitmap and where it sits relative to the pen
	public sealed class Glyph
	{
		public byte[] Coverage;
		public int Width;
		public int Height;
		public int Left;
		public int Top;
		public float Advance;
		public int Id;
	}

	// A typeface at a pixel size: glyphs are rasterized on first use and kept, text is measured and drawn with them
	public sealed class Font
	{
		private const int Flatten = 6;

		public readonly TrueTypeFont Face;
		public readonly float Size;
		private readonly float _scale;
		private readonly Dictionary<int, Glyph> _glyphs = new Dictionary<int, Glyph>();

		public Font(TrueTypeFont face, float size)
		{
			Face = face;
			Size = size;
			_scale = size / face.UnitsPerEm;
			Ascent = (int)MathF.Ceiling(face.Ascender * _scale);
			Descent = (int)MathF.Ceiling(-face.Descender * _scale);
			LineHeight = (int)MathF.Ceiling((face.Ascender - face.Descender + face.LineGap) * _scale);
		}

		// Pixels above and below the baseline, and the distance between baselines
		public int Ascent { get; }

		public int Descent { get; }

		public int LineHeight { get; }

		public Glyph GetGlyph(int codePoint)
		{
			int id = Face.GlyphId(codePoint);
			if (_glyphs.TryGetValue(id, out Glyph glyph))
				return glyph;
			glyph = Rasterize(id);
			_glyphs[id] = glyph;
			return glyph;
		}

		public float Advance(int left, int right) => Face.Kerning(left, right) * _scale;

		// The width of text drawn on one line, kerning included
		public int Measure(ReadOnlySpan<char> text)
		{
			float x = 0;
			int previous = 0;
			foreach (char c in text)
			{
				Glyph glyph = GetGlyph(c);
				x += Advance(previous, glyph.Id) + glyph.Advance;
				previous = glyph.Id;
			}
			return (int)MathF.Ceiling(x);
		}

		// Draws text with its baseline at y; returns the x after the last glyph
		public int Draw(in Surface surface, int x, int y, ReadOnlySpan<char> text, uint color)
		{
			float pen = x;
			int previous = 0;
			foreach (char c in text)
			{
				Glyph glyph = GetGlyph(c);
				pen += Advance(previous, glyph.Id);
				DrawGlyph(in surface, (int)MathF.Round(pen) + glyph.Left, y - glyph.Top, glyph, color);
				pen += glyph.Advance;
				previous = glyph.Id;
			}
			return (int)MathF.Ceiling(pen);
		}

		// Draws text with the top of its line box at y, which is what layout code works from
		public int DrawTop(in Surface surface, int x, int y, ReadOnlySpan<char> text, uint color) => Draw(in surface, x, y + Ascent, text, color);

		// Blends the glyph's coverage with the color over what is already drawn, clipped to the surface
		public static void DrawGlyph(in Surface surface, int x, int y, Glyph glyph, uint color)
		{
			int x0 = Math.Max(x, 0);
			int y0 = Math.Max(y, 0);
			int x1 = Math.Min(x + glyph.Width, surface.Width);
			int y1 = Math.Min(y + glyph.Height, surface.Height);
			if (x1 <= x0 || y1 <= y0)
				return;
			uint red = (color >> 16) & 0xFF;
			uint green = (color >> 8) & 0xFF;
			uint blue = color & 0xFF;
			for (int row = y0; row < y1; row++)
			{
				Span<uint> line = surface.Row(row);
				int source = (row - y) * glyph.Width;
				for (int column = x0; column < x1; column++)
				{
					uint alpha = glyph.Coverage[source + column - x];
					if (alpha == 0)
						continue;
					if (alpha == 255)
					{
						line[column] = color;
						continue;
					}
					uint under = line[column];
					uint inverse = 255 - alpha;
					uint r = (red * alpha + ((under >> 16) & 0xFF) * inverse + 127) / 255;
					uint g = (green * alpha + ((under >> 8) & 0xFF) * inverse + 127) / 255;
					uint b = (blue * alpha + (under & 0xFF) * inverse + 127) / 255;
					line[column] = r << 16 | g << 8 | b;
				}
			}
		}

		// Scales the outline to pixels, flattens its curves and accumulates coverage into a bitmap just big enough for its bounds
		private Glyph Rasterize(int id)
		{
			GlyphOutline outline = Face.Outline(id);
			Glyph glyph = new Glyph { Id = id, Advance = Face.AdvanceWidth(id) * _scale };
			if (outline.Points.Length == 0)
			{
				glyph.Coverage = new byte[0];
				return glyph;
			}
			int left = (int)MathF.Floor(outline.XMin * _scale) - 1;
			int top = (int)MathF.Ceiling(outline.YMax * _scale) + 1;
			int right = (int)MathF.Ceiling(outline.XMax * _scale) + 1;
			int bottom = (int)MathF.Floor(outline.YMin * _scale) - 1;
			Rasterizer rasterizer = new Rasterizer(right - left + 1, top - bottom + 1);

			int start = 0;
			for (int contour = 0; contour < outline.ContourEnds.Length; contour++)
			{
				int end = outline.ContourEnds[contour];
				AddContour(rasterizer, outline.Points, start, end, left, top);
				start = end + 1;
			}
			glyph.Coverage = rasterizer.Finish();
			glyph.Width = rasterizer.Width;
			glyph.Height = rasterizer.Height;
			glyph.Left = left;
			glyph.Top = top;
			return glyph;
		}

		// Walks a closed contour: straight segments between on-curve points, quadratics through off-curve ones, with implied midpoints between two off-curve points
		private void AddContour(Rasterizer rasterizer, OutlinePoint[] points, int start, int end, int left, int top)
		{
			int count = end - start + 1;
			if (count < 2)
				return;
			float Px(int i) => points[start + (i % count)].X * _scale - left;
			float Py(int i) => top - points[start + (i % count)].Y * _scale;
			bool On(int i) => points[start + (i % count)].OnCurve;

			// Start on an on-curve point, or on the midpoint of the first two control points if there is none
			int first = -1;
			for (int i = 0; i < count; i++)
			{
				if (On(i))
				{
					first = i;
					break;
				}
			}
			float startX;
			float startY;
			if (first < 0)
			{
				first = 0;
				startX = (Px(0) + Px(1)) / 2;
				startY = (Py(0) + Py(1)) / 2;
			}
			else
			{
				startX = Px(first);
				startY = Py(first);
			}

			float x = startX;
			float y = startY;
			int i2 = first + 1;
			for (int step = 0; step < count; step++, i2++)
			{
				float nx = Px(i2);
				float ny = Py(i2);
				if (On(i2))
				{
					rasterizer.Line(x, y, nx, ny);
					x = nx;
					y = ny;
					continue;
				}
				float ex;
				float ey;
				if (On(i2 + 1))
				{
					ex = Px(i2 + 1);
					ey = Py(i2 + 1);
					step++;
					i2++;
				}
				else
				{
					ex = (nx + Px(i2 + 1)) / 2;
					ey = (ny + Py(i2 + 1)) / 2;
				}
				Quadratic(rasterizer, x, y, nx, ny, ex, ey);
				x = ex;
				y = ey;
			}
			if (x != startX || y != startY)
				rasterizer.Line(x, y, startX, startY);
		}

		private static void Quadratic(Rasterizer rasterizer, float x0, float y0, float cx, float cy, float x1, float y1)
		{
			float px = x0;
			float py = y0;
			for (int i = 1; i <= Flatten; i++)
			{
				float t = i / (float)Flatten;
				float u = 1 - t;
				float x = u * u * x0 + 2 * u * t * cx + t * t * x1;
				float y = u * u * y0 + 2 * u * t * cy + t * t * y1;
				rasterizer.Line(px, py, x, y);
				px = x;
				py = y;
			}
		}
	}

	// The faces the image ships in /fonts, loaded once per process; null when a file is missing, so callers fall back to the 8x8 font
	public static class Fonts
	{
		private static readonly Dictionary<string, TrueTypeFont> s_faces = new Dictionary<string, TrueTypeFont>();
		private static readonly Dictionary<(string, int), Font> s_fonts = new Dictionary<(string, int), Font>();

		public const string Sans = "sans";
		public const string SansBold = "sans-bold";
		public const string Serif = "serif";
		public const string SerifBold = "serif-bold";
		public const string Mono = "mono";

		public static TrueTypeFont Face(string name)
		{
			lock (s_faces)
			{
				if (s_faces.TryGetValue(name, out TrueTypeFont face))
					return face;
				byte[] data = FontFiles.Read(name);
				face = data != null && TrueTypeFont.TryLoad(data, out TrueTypeFont loaded) ? loaded : null;
				s_faces[name] = face;
				return face;
			}
		}

		// A face at a whole pixel size, shared by everyone in the process who asks for it
		public static Font Get(string name, int size)
		{
			lock (s_fonts)
			{
				if (s_fonts.TryGetValue((name, size), out Font font))
					return font;
				TrueTypeFont face = Face(name);
				font = face == null ? null : new Font(face, size);
				s_fonts[(name, size)] = font;
				return font;
			}
		}
	}
}
