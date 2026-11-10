namespace Alloy.Graphics
{
	// Pixel art up to 8x8, two bits a cell: 0 is clear, 1 to 3 pick a palette color (MetalOS PixelGlyph and Wm::IconBits)
	public sealed unsafe class PixelGlyph
	{
		public readonly int Width;
		public readonly int Height;
		public readonly ulong Bits;
		public readonly ulong MoreBits;
		public readonly uint Color1;
		public readonly uint Color2;
		public readonly uint Color3;

		public PixelGlyph(int width, int height, ulong bits, ulong moreBits, uint color1, uint color2, uint color3)
		{
			Width = width;
			Height = height;
			Bits = bits;
			MoreBits = moreBits;
			Color1 = color1;
			Color2 = color2;
			Color3 = color3;
		}

		// Rows written as in MetalOS's Icons.h: '.' clear, 'a' to 'c' the palette colors
		public static PixelGlyph Parse(string rows, int width, uint color1, uint color2, uint color3)
		{
			ulong bits = 0, moreBits = 0;
			for (int i = 0; i < rows.Length; i++)
			{
				char c = rows[i];
				ulong cell = c >= 'a' && c <= 'c' ? (ulong)(c - 'a' + 1) : 0;
				int index = i / width * 8 + i % width;
				if (index < 32)
					bits |= cell << (2 * index);
				else
					moreBits |= cell << (2 * (index - 32));
			}
			return new PixelGlyph(width, rows.Length / width, bits, moreBits, color1, color2, color3);
		}

		public int Cell(int x, int y)
		{
			int index = y * 8 + x;
			return (int)((index < 32 ? Bits >> (2 * index) : MoreBits >> (2 * (index - 32))) & 3);
		}

		public uint Color(int cell) => cell == 1 ? Color1 : cell == 2 ? Color2 : Color3;

		// Row y as MetalOS's IconBits row: two bits a column, column 0 lowest
		public ushort Row(int y) => (ushort)(y < 4 ? Bits >> (16 * y) : MoreBits >> (16 * (y - 4)));
	}
}
