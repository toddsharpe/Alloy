namespace Alloy.Graphics
{
	// MetalOS's 8x8 app icons and 5x5 title-bar glyphs (Graphics/Icons.h), drawn at 2x or 4x on the font's pixel grid
	public static class Icons
	{
		public static readonly PixelGlyph Terminal = PixelGlyph.Parse(
			"aaaaaaaa" + "a......a" + "a.b....a" + "a..b...a" + "a.b.bb.a" + "a......a" + "aaaaaaaa" + "........",
			8, Theme.Steel, Theme.Ember, Theme.Bone);

		public static readonly PixelGlyph Calc = PixelGlyph.Parse(
			".aaaaaa." + ".abbbba." + ".abbbba." + ".a....a." + ".ac.c.a." + ".ac.c.a." + ".a....a." + ".aaaaaa.",
			8, Theme.Steel, Theme.Ember, 0xD9D2C3);

		public static readonly PixelGlyph Flame = PixelGlyph.Parse(
			"...a...." + "...aa..." + "..aaa..." + "..aaaa.." + ".aabaaa." + ".aabbaa." + ".aabbaa." + "..aaaa..",
			8, Theme.Ember, Theme.Hot, Theme.Hot);

		public static readonly PixelGlyph Skull = PixelGlyph.Parse(
			".aaaaaa." + "aaaaaaaa" + "abbaabba" + "abbaabba" + "aaaaaaaa" + ".aa..aa." + ".aaaaaa." + ".a.aa.a.",
			8, 0xD9D2C3, Theme.Ember, Theme.Ember);

		public static readonly PixelGlyph Window = PixelGlyph.Parse(
			"aaaaaaaa" + "abbbbbba" + "aaaaaaaa" + "a......a" + "a......a" + "a......a" + "aaaaaaaa" + "........",
			8, Theme.Steel, Theme.Ember, Theme.Ember);

		public static readonly PixelGlyph Globe = PixelGlyph.Parse(
			"..aaaa.." + ".a.bb.a." + "a..bb..a" + "abbbbbba" + "abbbbbba" + "a..bb..a" + ".a.bb.a." + "..aaaa..",
			8, Theme.Steel, Theme.Ember, Theme.Ember);

		public static readonly PixelGlyph Network = PixelGlyph.Parse(
			"........" + ".....a.." + ".....a.." + "...a.a.." + "...a.a.." + ".a.a.a.." + ".a.a.a.." + "........",
			8, Theme.Ember, Theme.Ember, Theme.Ember);

		public static readonly PixelGlyph Minimize = PixelGlyph.Parse("....." + "....." + "....." + "....." + "aaaaa", 5, Theme.Bone, Theme.Bone, Theme.Bone);

		public static readonly PixelGlyph Maximize = PixelGlyph.Parse("aaaaa" + "aaaaa" + "a...a" + "a...a" + "aaaaa", 5, Theme.Bone, Theme.Bone, Theme.Bone);

		public static readonly PixelGlyph Restore = PixelGlyph.Parse(".aaaa" + "....a" + "aaaaa" + "a..a." + "aaaa.", 5, Theme.Bone, Theme.Bone, Theme.Bone);

		public static readonly PixelGlyph Close = PixelGlyph.Parse("a...a" + ".a.a." + "..a.." + ".a.a." + "a...a", 5, Theme.Bone, Theme.Bone, Theme.Bone);
	}
}
