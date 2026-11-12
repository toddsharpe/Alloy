using System;
using System.Collections.Generic;
using Alloy.Graphics;
using Alloy.Sdk.Text;
using Alloy.Sdk.UI;
using Alloy.Sdk.Web.Css;
using Alloy.Sdk.Web.Images;
using Alloy.Sdk.Web.Layout;

namespace Alloy.Sdk.Web.Render
{
	// A layout font over one of the SDK's rasterized TrueType faces
	public sealed class SdkFont : LayoutFont
	{
		public readonly Font Font;

		public SdkFont(Font font)
		{
			Font = font;
		}

		public override float Size => Font.Size;

		public override float Ascent => Font.Ascent;

		public override float Descent => Font.Descent;

		public override float Measure(ReadOnlySpan<char> text) => Font.Measure(text);

		// Draws with the baseline at y, clipped to the rectangle, with letter spacing after each character and an overall alpha
		public void Draw(in Surface surface, int x, int y, string text, uint color, in Rectangle clip, float spacing = 0, int alpha = 255)
		{
			if (alpha <= 0)
				return;
			float pen = x;
			int previous = 0;
			foreach (char c in text)
			{
				Glyph glyph = Font.GetGlyph(c);
				pen += Font.Advance(previous, glyph.Id);
				int left = (int)MathF.Round(pen) + glyph.Left;
				if (left < clip.X + clip.Width && left + glyph.Width > clip.X)
					DrawGlyph(in surface, left, y - glyph.Top, glyph, color, in clip, alpha);
				pen += glyph.Advance + spacing;
				previous = glyph.Id;
			}
		}

		private static void DrawGlyph(in Surface surface, int x, int y, Glyph glyph, uint color, in Rectangle clip, int opacity)
		{
			int x0 = Math.Max(Math.Max(x, 0), clip.X);
			int y0 = Math.Max(Math.Max(y, 0), clip.Y);
			int x1 = Math.Min(Math.Min(x + glyph.Width, surface.Width), clip.X + clip.Width);
			int y1 = Math.Min(Math.Min(y + glyph.Height, surface.Height), clip.Y + clip.Height);
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
					if (opacity < 255)
						alpha = (alpha * (uint)opacity + 127) / 255;
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
	}

	// The built-in 8x8 font, for images without the TrueType files
	public sealed class BitmapFont : LayoutFont
	{
		public override float Size => 8;

		public override float Ascent => 8;

		public override float Descent => 2;

		public override float Measure(ReadOnlySpan<char> text) => text.Length * 8;
	}

	// Maps computed styles to the faces on the image: sans, serif and mono, bold where a bold face exists, at whole pixel sizes
	public sealed class SdkFontProvider : IFontProvider
	{
		private readonly Dictionary<(string, int), LayoutFont> _fonts = new Dictionary<(string, int), LayoutFont>();
		private readonly BitmapFont _bitmap = new BitmapFont();

		public LayoutFont GetFont(ComputedStyle style)
		{
			string face = style.FontFamily switch
			{
				FontFamily.Serif => style.Bold ? Fonts.SerifBold : Fonts.Serif,
				FontFamily.Mono => Fonts.Mono,
				_ => style.Bold ? Fonts.SansBold : Fonts.Sans,
			};
			int size = Math.Clamp((int)MathF.Round(style.FontSize), 6, 200);
			if (_fonts.TryGetValue((face, size), out LayoutFont font))
				return font;
			Font loaded = Fonts.Get(face, size);
			font = loaded != null ? new SdkFont(loaded) : _bitmap;
			_fonts[(face, size)] = font;
			return font;
		}
	}

	// Draws a display list onto a surface, scrolled and clipped to a viewport rectangle
	public static class SurfaceRenderer
	{
		public static void Draw(in Surface surface, DisplayList list, int pageScrollX, int pageScrollY, Rectangle viewport)
		{
			Stack<Rectangle> clips = new Stack<Rectangle>();
			Rectangle clip = Intersect(viewport, new Rectangle(0, 0, surface.Width, surface.Height));
			int pinned = 0;
			foreach (DisplayItem item in list.Items)
			{
				// Inside a fixed box the document scroll does not apply
				int scrollX = pinned > 0 ? 0 : pageScrollX;
				int scrollY = pinned > 0 ? 0 : pageScrollY;
				switch (item.Kind)
				{
					case DisplayItemKind.Fixed:
						pinned++;
						break;
					case DisplayItemKind.Unfixed:
						pinned--;
						break;
					case DisplayItemKind.Clip:
						clips.Push(clip);
						clip = Intersect(clip, ToRectangle(item, scrollX, scrollY, viewport));
						break;
					case DisplayItemKind.Unclip:
						if (clips.Count > 0)
							clip = clips.Pop();
						break;
					case DisplayItemKind.Rect:
					{
						Rectangle box = ToRectangle(item, scrollX, scrollY, viewport);
						Color color = Faded(item.Color, item.Opacity);
						if (item.Radius.Any || item.Stroke > 0)
							FillRounded(in surface, box, Intersect(clip, box), color, item.Radius, item.Stroke);
						else
							FillRect(in surface, Intersect(clip, box), color);
						break;
					}
					case DisplayItemKind.Image:
					{
						Rectangle box = ToRectangle(item, scrollX, scrollY, viewport);
						if (item.Image != null)
						{
							DrawBitmap(in surface, item.Image, box, Intersect(clip, box), item.Radius, item.Opacity);
							break;
						}
						FillRect(in surface, Intersect(clip, box), Color.FromRgb(0xDD, 0xDD, 0xDD));
						Rectangle inner = Intersect(clip, box);
						if (inner.Width > 2 && inner.Height > 2)
						{
							FillRect(in surface, Intersect(clip, new Rectangle(box.X, box.Y, box.Width, 1)), Color.FromRgb(0x99, 0x99, 0x99));
							FillRect(in surface, Intersect(clip, new Rectangle(box.X, box.Y + box.Height - 1, box.Width, 1)), Color.FromRgb(0x99, 0x99, 0x99));
							FillRect(in surface, Intersect(clip, new Rectangle(box.X, box.Y, 1, box.Height)), Color.FromRgb(0x99, 0x99, 0x99));
							FillRect(in surface, Intersect(clip, new Rectangle(box.X + box.Width - 1, box.Y, 1, box.Height)), Color.FromRgb(0x99, 0x99, 0x99));
						}
						if (!string.IsNullOrEmpty(item.Text) && box.Width > 24 && box.Height > 12)
							surface.DrawText(box.X + 4, box.Y + 4, item.Text.Length > box.Width / 8 - 1 ? item.Text.Substring(0, Math.Max(0, box.Width / 8 - 1)) : item.Text, item.Color.Rgb, 1);
						break;
					}
					case DisplayItemKind.Text:
					{
						int x = viewport.X + (int)MathF.Round(item.X) - scrollX;
						int baseline = viewport.Y + (int)MathF.Round(item.Y) - scrollY;
						if (baseline + 4 < clip.Y - item.Font.Size || baseline - item.Font.Size * 2 > clip.Y + clip.Height)
							break;
						if (item.Font is SdkFont sdk)
							sdk.Draw(in surface, x, baseline, item.Text, item.Color.Rgb, in clip, item.Spacing, (int)(item.Color.A * item.Opacity + 0.5f));
						else if (baseline - 8 >= clip.Y && baseline <= clip.Y + clip.Height)
							surface.DrawText(x, baseline - 8, item.Text, item.Color.Rgb, 1);
						break;
					}
				}
			}
		}

		private static Color Faded(Color color, float opacity) => opacity >= 1 ? color : Color.FromRgb(color.R, color.G, color.B, (int)(color.A * opacity + 0.5f));

		// Coverage of a pixel by a rounded rectangle: one inside, falling off across the arc at each rounded corner
		private static float CornerCoverage(float x, float y, Rectangle rect, Corners radius)
		{
			float left = rect.X;
			float top = rect.Y;
			float right = rect.X + rect.Width;
			float bottom = rect.Y + rect.Height;
			if (radius.TopLeft > 0 && x < left + radius.TopLeft && y < top + radius.TopLeft)
				return Disk(x, y, left + radius.TopLeft, top + radius.TopLeft, radius.TopLeft);
			if (radius.TopRight > 0 && x > right - radius.TopRight && y < top + radius.TopRight)
				return Disk(x, y, right - radius.TopRight, top + radius.TopRight, radius.TopRight);
			if (radius.BottomRight > 0 && x > right - radius.BottomRight && y > bottom - radius.BottomRight)
				return Disk(x, y, right - radius.BottomRight, bottom - radius.BottomRight, radius.BottomRight);
			if (radius.BottomLeft > 0 && x < left + radius.BottomLeft && y > bottom - radius.BottomLeft)
				return Disk(x, y, left + radius.BottomLeft, bottom - radius.BottomLeft, radius.BottomLeft);
			return 1;
		}

		private static float Disk(float x, float y, float cx, float cy, float r)
		{
			float dx = x - cx;
			float dy = y - cy;
			return Math.Clamp(r + 0.5f - MathF.Sqrt(dx * dx + dy * dy), 0, 1);
		}

		// A rounded rectangle, or with a stroke the ring between it and the same shape inset by the stroke; rows clear of the corners fill as spans
		private static void FillRounded(in Surface surface, Rectangle box, Rectangle clip, Color color, Corners radius, float stroke)
		{
			if (clip.Width <= 0 || clip.Height <= 0 || color.A == 0)
				return;
			Rectangle inner = new Rectangle(box.X + (int)stroke, box.Y + (int)stroke, Math.Max(0, box.Width - 2 * (int)stroke), Math.Max(0, box.Height - 2 * (int)stroke));
			Corners innerRadius = radius.Inset(stroke);
			int bandTop = box.Y + (int)MathF.Ceiling(Math.Max(radius.MaxTop, stroke > 0 ? stroke : 0));
			int bandBottom = box.Y + box.Height - (int)MathF.Ceiling(Math.Max(radius.MaxBottom, stroke > 0 ? stroke : 0));
			for (int y = clip.Y; y < clip.Y + clip.Height; y++)
			{
				if (y >= bandTop && y < bandBottom)
				{
					// No corner reaches this row: the whole row, or just the two sides of a ring
					if (stroke <= 0)
						FillRect(in surface, new Rectangle(clip.X, y, clip.Width, 1), color);
					else
					{
						FillRect(in surface, Intersect(clip, new Rectangle(box.X, y, (int)stroke, 1)), color);
						FillRect(in surface, Intersect(clip, new Rectangle(box.X + box.Width - (int)stroke, y, (int)stroke, 1)), color);
					}
					continue;
				}
				Span<uint> row = surface.Row(y);
				for (int x = clip.X; x < clip.X + clip.Width; x++)
				{
					float px = x + 0.5f;
					float py = y + 0.5f;
					float coverage = CornerCoverage(px, py, box, radius);
					if (stroke > 0 && px > inner.X && px < inner.X + inner.Width && py > inner.Y && py < inner.Y + inner.Height)
						coverage -= CornerCoverage(px, py, inner, innerRadius);
					if (coverage <= 0)
						continue;
					int alpha = (int)(color.A * coverage + 0.5f);
					if (alpha <= 0)
						continue;
					row[x] = alpha >= 255 ? color.Rgb : Color.FromRgb(color.R, color.G, color.B, alpha).Over(row[x]);
				}
			}
		}

		// Nearest-neighbour scaling into the box, blending pixels with alpha over what is there, inside rounded corners and at an opacity
		private static void DrawBitmap(in Surface surface, Bitmap bitmap, Rectangle box, Rectangle clip, Corners radius = default, float opacity = 1)
		{
			if (clip.Width <= 0 || clip.Height <= 0 || box.Width <= 0 || box.Height <= 0)
				return;
			bool opaque = bitmap.IsOpaque && !radius.Any && opacity >= 1;
			bool shaped = radius.Any || opacity < 1;
			long stepX = ((long)bitmap.Width << 16) / box.Width;
			long stepY = ((long)bitmap.Height << 16) / box.Height;
			for (int y = clip.Y; y < clip.Y + clip.Height; y++)
			{
				int sourceY = (int)(((long)(y - box.Y) * stepY) >> 16);
				if (sourceY >= bitmap.Height)
					sourceY = bitmap.Height - 1;
				Span<uint> row = surface.Row(y);
				int sourceRow = sourceY * bitmap.Width;
				long sx = (long)(clip.X - box.X) * stepX;
				for (int x = clip.X; x < clip.X + clip.Width; x++, sx += stepX)
				{
					int sourceX = (int)(sx >> 16);
					if (sourceX >= bitmap.Width)
						sourceX = bitmap.Width - 1;
					uint pixel = bitmap.Pixels[sourceRow + sourceX];
					if (shaped)
					{
						float factor = opacity * (radius.Any ? CornerCoverage(x + 0.5f, y + 0.5f, box, radius) : 1);
						if (factor <= 0)
							continue;
						uint alpha = (uint)((pixel >> 24) * factor + 0.5f);
						if (alpha == 0)
							continue;
						pixel = alpha << 24 | (pixel & 0xFFFFFF);
					}
					if (opaque || pixel >= 0xFF000000)
						row[x] = pixel & 0xFFFFFF;
					else if (pixel >= 0x01000000)
						row[x] = new Color(pixel).Over(row[x]);
				}
			}
		}

		private static Rectangle ToRectangle(in DisplayItem item, int scrollX, int scrollY, Rectangle viewport)
		{
			int x0 = (int)MathF.Round(item.X);
			int y0 = (int)MathF.Round(item.Y);
			int x1 = (int)MathF.Round(item.X + item.Width);
			int y1 = (int)MathF.Round(item.Y + item.Height);
			return new Rectangle(viewport.X + x0 - scrollX, viewport.Y + y0 - scrollY, Math.Max(x1 - x0, item.Width > 0 ? 1 : 0), Math.Max(y1 - y0, item.Height > 0 ? 1 : 0));
		}

		private static Rectangle Intersect(Rectangle a, Rectangle b)
		{
			int x0 = Math.Max(a.X, b.X);
			int y0 = Math.Max(a.Y, b.Y);
			int x1 = Math.Min(a.X + a.Width, b.X + b.Width);
			int y1 = Math.Min(a.Y + a.Height, b.Y + b.Height);
			return new Rectangle(x0, y0, Math.Max(0, x1 - x0), Math.Max(0, y1 - y0));
		}

		// A translucent rectangle over what is drawn, for selections and other overlays
		public static void FillBlend(in Surface surface, Rectangle rect, Color color) => FillRect(in surface, rect, color);

		private static void FillRect(in Surface surface, Rectangle rect, Color color)
		{
			if (rect.Width <= 0 || rect.Height <= 0 || color.A == 0)
				return;
			if (color.A == 255)
			{
				surface.FillRectangle(rect.X, rect.Y, rect.Width, rect.Height, color.Rgb);
				return;
			}
			for (int y = rect.Y; y < rect.Y + rect.Height; y++)
			{
				Span<uint> row = surface.Row(y);
				for (int x = rect.X; x < rect.X + rect.Width; x++)
					row[x] = color.Over(row[x]);
			}
		}
	}
}
