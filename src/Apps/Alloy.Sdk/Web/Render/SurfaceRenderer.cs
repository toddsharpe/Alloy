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

		// Draws with the baseline at y, clipped to the rectangle, with letter spacing after each character
		public void Draw(in Surface surface, int x, int y, string text, uint color, in Rectangle clip, float spacing = 0)
		{
			float pen = x;
			int previous = 0;
			foreach (char c in text)
			{
				Glyph glyph = Font.GetGlyph(c);
				pen += Font.Advance(previous, glyph.Id);
				int left = (int)MathF.Round(pen) + glyph.Left;
				if (left < clip.X + clip.Width && left + glyph.Width > clip.X)
					DrawGlyph(in surface, left, y - glyph.Top, glyph, color, in clip);
				pen += glyph.Advance + spacing;
				previous = glyph.Id;
			}
		}

		private static void DrawGlyph(in Surface surface, int x, int y, Glyph glyph, uint color, in Rectangle clip)
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
		public static void Draw(in Surface surface, DisplayList list, int scrollX, int scrollY, Rectangle viewport)
		{
			Stack<Rectangle> clips = new Stack<Rectangle>();
			Rectangle clip = Intersect(viewport, new Rectangle(0, 0, surface.Width, surface.Height));
			foreach (DisplayItem item in list.Items)
			{
				switch (item.Kind)
				{
					case DisplayItemKind.Clip:
						clips.Push(clip);
						clip = Intersect(clip, ToRectangle(item, scrollX, scrollY, viewport));
						break;
					case DisplayItemKind.Unclip:
						if (clips.Count > 0)
							clip = clips.Pop();
						break;
					case DisplayItemKind.Rect:
						FillRect(in surface, Intersect(clip, ToRectangle(item, scrollX, scrollY, viewport)), item.Color);
						break;
					case DisplayItemKind.Image:
					{
						Rectangle box = ToRectangle(item, scrollX, scrollY, viewport);
						if (item.Image != null)
						{
							DrawBitmap(in surface, item.Image, box, Intersect(clip, box));
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
							sdk.Draw(in surface, x, baseline, item.Text, item.Color.Rgb, in clip, item.Spacing);
						else if (baseline - 8 >= clip.Y && baseline <= clip.Y + clip.Height)
							surface.DrawText(x, baseline - 8, item.Text, item.Color.Rgb, 1);
						break;
					}
				}
			}
		}

		// Nearest-neighbour scaling into the box, blending pixels with alpha over what is there
		private static void DrawBitmap(in Surface surface, Bitmap bitmap, Rectangle box, Rectangle clip)
		{
			if (clip.Width <= 0 || clip.Height <= 0 || box.Width <= 0 || box.Height <= 0)
				return;
			bool opaque = bitmap.IsOpaque;
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
