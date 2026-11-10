using System;

namespace Alloy.Graphics
{
	// A 32-bit BGRX pixel buffer: the framebuffer, a back buffer or a window
	public unsafe struct Surface
	{
		// Text advances on MetalOS's 10 pixel grid (Draw2D PixelScale)
		public const int CharWidth = 10;
		public const int LineHeight = 10;

		public uint* Pixels;
		public int Width;
		public int Height;
		public int Stride;

		public Surface(uint* pixels, int width, int height, int stride)
		{
			Pixels = pixels;
			Width = width;
			Height = height;
			Stride = stride;
		}

		// One row of pixels, for code that computes pixels itself without touching the pointer
		public readonly Span<uint> Row(int y) => new Span<uint>(Pixels + y * Stride, Width);

		public readonly void Fill(uint color) => FillRectangle(0, 0, Width, Height, color);

		public readonly void FillRectangle(int x, int y, int width, int height, uint color)
		{
			int x0 = Math.Max(x, 0);
			int y0 = Math.Max(y, 0);
			int x1 = Math.Min(x + width, Width);
			int y1 = Math.Min(y + height, Height);
			if (x1 <= x0)
				return;
			for (int row = y0; row < y1; row++)
				new Span<uint>(Pixels + row * Stride + x0, x1 - x0).Fill(color);
		}

		public readonly void DrawBorder(int x, int y, int width, int height, int thickness, uint color)
		{
			FillRectangle(x, y, width, thickness, color);
			FillRectangle(x, y + height - thickness, width, thickness, color);
			FillRectangle(x, y, thickness, height, color);
			FillRectangle(x + width - thickness, y, thickness, height, color);
		}

		public readonly void DrawCharacter(int x, int y, char c, uint color)
		{
			ReadOnlySpan<byte> glyph = Font8x8.Glyph(c);
			for (int row = 0; row < Font8x8.GlyphSize; row++)
			{
				int py = y + row;
				if ((uint)py >= (uint)Height)
					continue;
				byte bits = glyph[row];
				uint* line = Pixels + py * Stride;
				for (int column = 0; column < Font8x8.GlyphSize; column++)
				{
					int px = x + column;
					if ((bits & (1 << column)) != 0 && (uint)px < (uint)Width)
						line[px] = color;
				}
			}
		}

		// Returns where the next character would go; '\n' returns to x and moves down a line
		public readonly void DrawText(ref int x, ref int y, int left, ReadOnlySpan<char> text, uint color)
		{
			foreach (char c in text)
			{
				if (c == '\n')
				{
					x = left;
					y += LineHeight;
				}
				else if (c != '\r')
				{
					DrawCharacter(x, y, c, color);
					x += CharWidth;
				}
			}
		}

		// The font scaled up by an integer factor; returns the x after the last character
		public readonly int DrawText(int x, int y, ReadOnlySpan<char> text, uint color, int scale)
		{
			foreach (char c in text)
			{
				ReadOnlySpan<byte> glyph = Font8x8.Glyph(c);
				for (int row = 0; row < Font8x8.GlyphSize; row++)
				{
					byte bits = glyph[row];
					for (int column = 0; column < Font8x8.GlyphSize; column++)
					{
						if ((bits & (1 << column)) != 0)
							FillRectangle(x + column * scale, y + row * scale, scale, scale, color);
					}
				}
				x += Font8x8.GlyphSize * scale;
			}
			return x;
		}

		public readonly void FillGradient(int x, int y, int width, int height, uint top, uint bottom)
		{
			int span = height > 1 ? height - 1 : 1;
			for (int row = 0; row < height; row++)
				FillRectangle(x, y + row, width, 1, Blend(top, bottom, row, span));
		}

		// A 1px line just inside each edge
		public readonly void DrawOutline(int x, int y, int width, int height, uint color)
		{
			if (width <= 0 || height <= 0)
				return;
			FillRectangle(x, y, width, 1, color);
			FillRectangle(x, y + height - 1, width, 1, color);
			FillRectangle(x, y, 1, height, color);
			FillRectangle(x + width - 1, y, 1, height, color);
		}

		// Light along the top and left, dark along the bottom and right, so the rectangle looks raised (or sunken, swapped)
		public readonly void DrawBevel(int x, int y, int width, int height, uint light, uint dark)
		{
			if (width <= 0 || height <= 0)
				return;
			FillRectangle(x, y, width, 1, light);
			FillRectangle(x, y, 1, height, light);
			FillRectangle(x, y + height - 1, width, 1, dark);
			FillRectangle(x + width - 1, y, 1, height, dark);
		}

		// A raised (or sunken) control: a dark outline around a gradient face with a 1px bevel (MetalOS Draw2D DrawFace)
		public readonly void DrawFace(int x, int y, int width, int height, Theme.Face face)
		{
			if (width < 3 || height < 3)
				return;
			DrawOutline(x, y, width, height, 0x08090A);
			FillGradient(x + 1, y + 1, width - 2, height - 2, face.Top, face.Bottom);
			DrawBevel(x + 1, y + 1, width - 2, height - 2, face.Light, face.Dark);
		}

		// Darkens what is already drawn to keep/256 (shadows, dimmed areas)
		public readonly void Darken(int x, int y, int width, int height, uint keep)
		{
			int x0 = Math.Max(x, 0);
			int y0 = Math.Max(y, 0);
			int x1 = Math.Min(x + width, Width);
			int y1 = Math.Min(y + height, Height);
			for (int row = y0; row < y1; row++)
			{
				uint* line = Pixels + row * Stride;
				for (int column = x0; column < x1; column++)
					line[column] = Shade(line[column], keep);
			}
		}

		// Pixel art at a whole-number scale, its colors kept at keep/256 (256 draws them as they are)
		public readonly void DrawGlyph(int x, int y, PixelGlyph glyph, int scale, uint keep = 256)
		{
			for (int row = 0; row < glyph.Height; row++)
			{
				for (int column = 0; column < glyph.Width; column++)
				{
					int cell = glyph.Cell(column, row);
					if (cell != 0)
						FillRectangle(x + column * scale, y + row * scale, scale, scale, Shade(glyph.Color(cell), keep));
				}
			}
		}

		public static int TextWidth(int length, int scale) => length * 8 * scale;

		// Copies another surface in, clipped to this one
		public readonly void Blit(int x, int y, in Surface source)
		{
			int x0 = Math.Max(x, 0);
			int y0 = Math.Max(y, 0);
			int x1 = Math.Min(x + source.Width, Width);
			int y1 = Math.Min(y + source.Height, Height);
			if (x1 <= x0)
				return;
			for (int row = y0; row < y1; row++)
			{
				uint* from = source.Pixels + (row - y) * source.Stride + (x0 - x);
				new ReadOnlySpan<uint>(from, x1 - x0).CopyTo(new Span<uint>(Pixels + row * Stride + x0, x1 - x0));
			}
		}

		// Like Blit, with every pixel darkened to keep/256
		public readonly void BlitDimmed(int x, int y, in Surface source, uint keep)
		{
			int x0 = Math.Max(x, 0);
			int y0 = Math.Max(y, 0);
			int x1 = Math.Min(x + source.Width, Width);
			int y1 = Math.Min(y + source.Height, Height);
			for (int row = y0; row < y1; row++)
			{
				uint* from = source.Pixels + (row - y) * source.Stride - x;
				uint* to = Pixels + row * Stride;
				for (int column = x0; column < x1; column++)
					to[column] = Shade(from[column], keep);
			}
		}

		// The source stretched to fill the rectangle, nearest pixel (MetalOS WriteFrameScaled)
		public readonly void BlitScaled(int x, int y, int width, int height, in Surface source)
		{
			if (width <= 0 || height <= 0)
				return;
			int x0 = Math.Max(x, 0);
			int y0 = Math.Max(y, 0);
			int x1 = Math.Min(x + width, Width);
			int y1 = Math.Min(y + height, Height);
			ulong stepX = ((ulong)source.Width << 16) / (ulong)width;
			ulong stepY = ((ulong)source.Height << 16) / (ulong)height;
			for (int row = y0; row < y1; row++)
			{
				uint* from = source.Pixels + (int)(((ulong)(row - y) * stepY) >> 16) * source.Stride;
				uint* to = Pixels + row * Stride;
				ulong sx = (ulong)(x0 - x) * stepX;
				for (int column = x0; column < x1; column++, sx += stepX)
					to[column] = from[sx >> 16];
			}
		}

		public static uint Shade(uint color, uint keep) =>
			((((color >> 16) & 0xFF) * keep >> 8) << 16) | ((((color >> 8) & 0xFF) * keep >> 8) << 8) | ((color & 0xFF) * keep >> 8);

		public static uint Blend(uint a, uint b, int numerator, int denominator)
		{
			if (denominator == 0)
				return a;
			uint Channel(int shift) => (uint)((((a >> shift) & 0xFF) * (denominator - numerator) + ((b >> shift) & 0xFF) * numerator) / denominator);
			return (Channel(16) << 16) | (Channel(8) << 8) | Channel(0);
		}

		// Moves rows [top + lines, Height) up to top, then clears the freed rows
		public readonly void ScrollUp(int top, int lines, uint background)
		{
			int rows = Height - top - lines;
			for (int row = 0; row < rows; row++)
			{
				uint* destination = Pixels + (top + row) * Stride;
				uint* source = Pixels + (top + row + lines) * Stride;
				new ReadOnlySpan<uint>(source, Width).CopyTo(new Span<uint>(destination, Width));
			}
			FillRectangle(0, Height - lines, Width, lines, background);
		}
	}
}
