using System;
using System.Collections.Generic;
using System.Globalization;
using System.Text;

namespace Alloy.Sdk.Web.Css
{
	public enum LengthUnit
	{
		Pixels,
		Percent,
		Auto,
		// The smaller or larger of a pixel value and a percentage, as min(100%, 80rem) is until the containing size is known
		Min,
		Max,
	}

	// A length as layout needs it: pixels resolved against the font at compute time, a percentage left for the containing block, or auto
	public readonly struct Length
	{
		public readonly float Value;
		public readonly float Percent;
		public readonly LengthUnit Unit;

		public Length(float value, LengthUnit unit)
		{
			Value = value;
			Percent = 0;
			Unit = unit;
		}

		public Length(float pixels, float percent, LengthUnit unit)
		{
			Value = pixels;
			Percent = percent;
			Unit = unit;
		}

		public static readonly Length Zero = new Length(0, LengthUnit.Pixels);
		public static readonly Length Auto = new Length(0, LengthUnit.Auto);

		public static Length Px(float value) => new Length(value, LengthUnit.Pixels);

		public bool IsAuto => Unit == LengthUnit.Auto;

		// True when the containing size decides the value
		public bool IsPercent => Unit == LengthUnit.Percent || Unit == LengthUnit.Min || Unit == LengthUnit.Max;

		// The length in pixels against a containing size; auto counts as the fallback
		public float Resolve(float containing, float auto = 0) => Unit switch
		{
			LengthUnit.Pixels => Value,
			LengthUnit.Percent => containing * Value / 100,
			LengthUnit.Min => Math.Min(Value, containing * Percent / 100),
			LengthUnit.Max => Math.Max(Value, containing * Percent / 100),
			_ => auto,
		};

		public override string ToString() => Unit switch
		{
			LengthUnit.Pixels => Value.ToString("0.##", CultureInfo.InvariantCulture) + "px",
			LengthUnit.Percent => Value.ToString("0.##", CultureInfo.InvariantCulture) + "%",
			LengthUnit.Min => "min(" + Percent.ToString("0.##", CultureInfo.InvariantCulture) + "%, " + Value.ToString("0.##", CultureInfo.InvariantCulture) + "px)",
			LengthUnit.Max => "max(" + Percent.ToString("0.##", CultureInfo.InvariantCulture) + "%, " + Value.ToString("0.##", CultureInfo.InvariantCulture) + "px)",
			_ => "auto",
		};
	}

	// A color as 0xAARRGGBB; alpha 0 is transparent
	public readonly struct Color
	{
		public readonly uint Argb;

		public Color(uint argb)
		{
			Argb = argb;
		}

		public static Color FromRgb(int r, int g, int b, int a = 255) => new Color((uint)(Clamp(a) << 24 | Clamp(r) << 16 | Clamp(g) << 8 | Clamp(b)));

		private static int Clamp(int value) => value < 0 ? 0 : value > 255 ? 255 : value;

		public static readonly Color Transparent = new Color(0);
		public static readonly Color Black = FromRgb(0, 0, 0);
		public static readonly Color White = FromRgb(255, 255, 255);

		public int A => (int)(Argb >> 24);
		public int R => (int)(Argb >> 16) & 0xFF;
		public int G => (int)(Argb >> 8) & 0xFF;
		public int B => (int)Argb & 0xFF;

		public bool IsTransparent => A == 0;

		// The 0xRRGGBB pixel the surfaces use
		public uint Rgb => Argb & 0xFFFFFF;

		// This color drawn over another, by its alpha
		public uint Over(uint underRgb)
		{
			if (A == 255)
				return Rgb;
			if (A == 0)
				return underRgb;
			int inverse = 255 - A;
			int r = (R * A + ((int)(underRgb >> 16) & 0xFF) * inverse + 127) / 255;
			int g = (G * A + ((int)(underRgb >> 8) & 0xFF) * inverse + 127) / 255;
			int b = (B * A + ((int)underRgb & 0xFF) * inverse + 127) / 255;
			return (uint)(r << 16 | g << 8 | b);
		}

		public override string ToString() => A == 255 ? $"#{Rgb:x6}" : $"rgba({R}, {G}, {B}, {(A / 255.0).ToString("0.##", CultureInfo.InvariantCulture)})";
	}

	// Parsing of the value tokens the resolver gets per property
	public static class CssValue
	{
		public static string Join(List<CssToken> tokens)
		{
			StringBuilder text = new StringBuilder();
			foreach (CssToken token in tokens)
				text.Append(token.ToString());
			return text.ToString();
		}

		public static List<CssToken> WithoutWhitespace(List<CssToken> tokens)
		{
			List<CssToken> result = new List<CssToken>(tokens.Count);
			foreach (CssToken token in tokens)
			{
				if (token.Type != CssTokenType.Whitespace)
					result.Add(token);
			}
			return result;
		}

		// Splits a value at top-level commas (font families, backgrounds)
		public static List<List<CssToken>> SplitCommas(List<CssToken> tokens)
		{
			List<List<CssToken>> parts = new List<List<CssToken>>();
			List<CssToken> current = new List<CssToken>();
			int depth = 0;
			foreach (CssToken token in tokens)
			{
				if (token.Type == CssTokenType.Function || token.Type == CssTokenType.OpenParen)
					depth++;
				else if (token.Type == CssTokenType.CloseParen)
					depth--;
				if (token.Type == CssTokenType.Comma && depth == 0)
				{
					parts.Add(current);
					current = new List<CssToken>();
					continue;
				}
				if (token.Type != CssTokenType.Whitespace)
					current.Add(token);
			}
			parts.Add(current);
			return parts;
		}

		// Absolute units to pixels at 96 dpi, font-relative ones against the element's font size and the root's
		public static double ToPixels(double value, string unit, double fontSize, double rootFontSize)
		{
			switch (unit)
			{
				case "px":
				case "":
					return value;
				case "em":
					return value * fontSize;
				case "rem":
					return value * rootFontSize;
				case "ex":
					return value * fontSize * 0.5;
				case "ch":
					return value * fontSize * 0.55;
				case "pt":
					return value * 96 / 72;
				case "pc":
					return value * 16;
				case "in":
					return value * 96;
				case "cm":
					return value * 96 / 2.54;
				case "mm":
					return value * 96 / 25.4;
				case "q":
					return value * 96 / 101.6;
				case "vw":
					return value * Viewport.Width / 100;
				case "vh":
					return value * Viewport.Height / 100;
				case "vmin":
					return value * Math.Min(Viewport.Width, Viewport.Height) / 100;
				case "vmax":
					return value * Math.Max(Viewport.Width, Viewport.Height) / 100;
				default:
					return value;
			}
		}

		// The viewport the vw and vh units measure against, set by whoever lays the page out
		public static class Viewport
		{
			public static int Width = 1024;
			public static int Height = 768;
		}

		public static bool TryLength(CssToken token, double fontSize, double rootFontSize, bool allowPercent, bool allowNegative, out Length length)
		{
			length = Length.Zero;
			double pixels;
			if (token.Type == CssTokenType.Dimension && (token.Unit == "min" || token.Unit == "max"))
			{
				// A folded min() or max() of a pixel length and a percentage, which CssMath left for the containing size to settle
				if (!allowPercent || !float.TryParse(token.Value, NumberStyles.Float, CultureInfo.InvariantCulture, out float percent))
					return false;
				length = new Length((float)token.Number, percent, token.Unit == "min" ? LengthUnit.Min : LengthUnit.Max);
				return true;
			}
			if (token.Type == CssTokenType.Dimension)
				pixels = ToPixels(token.Number, token.Unit, fontSize, rootFontSize);
			else if (token.Type == CssTokenType.Number && (token.Number == 0 || true))
				pixels = token.Number;
			else if (token.Type == CssTokenType.Percentage && allowPercent)
			{
				if (token.Number < 0 && !allowNegative)
					return false;
				length = new Length((float)token.Number, LengthUnit.Percent);
				return true;
			}
			else
				return false;
			if (pixels < 0 && !allowNegative)
				return false;
			length = Length.Px((float)pixels);
			return true;
		}

		// Named colors, #rgb, #rrggbb, #rgba, #rrggbbaa, rgb(), rgba(), hsl(), hsla(), transparent
		public static bool TryColor(List<CssToken> tokens, int at, out Color color, out int consumed)
		{
			color = Color.Transparent;
			consumed = 0;
			if (at >= tokens.Count)
				return false;
			CssToken token = tokens[at];
			if (token.Type == CssTokenType.Hash)
			{
				consumed = 1;
				return TryHex(token.Value, out color);
			}
			if (token.Type == CssTokenType.Ident)
			{
				consumed = 1;
				string name = token.Value.ToLowerInvariant();
				if (name == "transparent")
					return true;
				return NamedColors.TryGet(name, out color);
			}
			if (token.Type == CssTokenType.Function && string.Equals(token.Value, "light-dark", StringComparison.OrdinalIgnoreCase))
			{
				bool light = TryColor(tokens, at + 1, out color, out int inner);
				int close = at + 1;
				int nested = 1;
				while (close < tokens.Count && nested > 0)
				{
					if (tokens[close].Type == CssTokenType.Function || tokens[close].Type == CssTokenType.OpenParen)
						nested++;
					else if (tokens[close].Type == CssTokenType.CloseParen)
						nested--;
					close++;
				}
				consumed = close - at;
				return light;
			}
			if (token.Type == CssTokenType.Function)
			{
				int close = at + 1;
				List<CssToken> arguments = new List<CssToken>();
				while (close < tokens.Count && tokens[close].Type != CssTokenType.CloseParen)
				{
					if (tokens[close].Type != CssTokenType.Whitespace && tokens[close].Type != CssTokenType.Comma && !tokens[close].IsDelim('/'))
						arguments.Add(tokens[close]);
					close++;
				}
				consumed = close - at + 1;
				string function = token.Value.ToLowerInvariant();
				if ((function == "rgb" || function == "rgba") && arguments.Count >= 3)
				{
					int r = Channel(arguments[0]);
					int g = Channel(arguments[1]);
					int b = Channel(arguments[2]);
					int a = arguments.Count > 3 ? Alpha(arguments[3]) : 255;
					color = Color.FromRgb(r, g, b, a);
					return true;
				}
				if ((function == "hsl" || function == "hsla") && arguments.Count >= 3)
				{
					double h = arguments[0].Number % 360;
					if (h < 0)
						h += 360;
					double s = Math.Clamp(arguments[1].Number / 100, 0, 1);
					double l = Math.Clamp(arguments[2].Number / 100, 0, 1);
					int a = arguments.Count > 3 ? Alpha(arguments[3]) : 255;
					HslToRgb(h, s, l, out int r, out int g, out int b);
					color = Color.FromRgb(r, g, b, a);
					return true;
				}
			}
			return false;
		}

		private static int Channel(CssToken token) => token.Type == CssTokenType.Percentage ? (int)Math.Round(token.Number * 2.55) : (int)Math.Round(token.Number);

		private static int Alpha(CssToken token) => token.Type == CssTokenType.Percentage ? (int)Math.Round(token.Number * 2.55) : (int)Math.Round(Math.Clamp(token.Number, 0, 1) * 255);

		private static void HslToRgb(double h, double s, double l, out int r, out int g, out int b)
		{
			double c = (1 - Math.Abs(2 * l - 1)) * s;
			double x = c * (1 - Math.Abs(h / 60 % 2 - 1));
			double m = l - c / 2;
			(double r1, double g1, double b1) = h < 60 ? (c, x, 0.0) : h < 120 ? (x, c, 0.0) : h < 180 ? (0.0, c, x) : h < 240 ? (0.0, x, c) : h < 300 ? (x, 0.0, c) : (c, 0.0, x);
			r = (int)Math.Round((r1 + m) * 255);
			g = (int)Math.Round((g1 + m) * 255);
			b = (int)Math.Round((b1 + m) * 255);
		}

		public static bool TryHex(string hex, out Color color)
		{
			color = Color.Transparent;
			if (hex.Length != 3 && hex.Length != 4 && hex.Length != 6 && hex.Length != 8)
				return false;
			foreach (char c in hex)
			{
				if (!char.IsAsciiHexDigit(c))
					return false;
			}
			if (hex.Length <= 4)
			{
				StringBuilder doubled = new StringBuilder();
				foreach (char c in hex)
					doubled.Append(c).Append(c);
				hex = doubled.ToString();
			}
			uint value = uint.Parse(hex, NumberStyles.HexNumber, CultureInfo.InvariantCulture);
			color = hex.Length == 6 ? new Color(0xFF000000 | value) : new Color(value >> 8 | (value & 0xFF) << 24);
			return true;
		}
	}

	// The CSS named colors
	public static class NamedColors
	{
		private static readonly Dictionary<string, uint> s_colors = new Dictionary<string, uint>(StringComparer.Ordinal)
		{
			["aliceblue"] = 0xF0F8FF, ["antiquewhite"] = 0xFAEBD7, ["aqua"] = 0x00FFFF, ["aquamarine"] = 0x7FFFD4, ["azure"] = 0xF0FFFF, ["beige"] = 0xF5F5DC,
			["bisque"] = 0xFFE4C4, ["black"] = 0x000000, ["blanchedalmond"] = 0xFFEBCD, ["blue"] = 0x0000FF, ["blueviolet"] = 0x8A2BE2, ["brown"] = 0xA52A2A,
			["burlywood"] = 0xDEB887, ["cadetblue"] = 0x5F9EA0, ["chartreuse"] = 0x7FFF00, ["chocolate"] = 0xD2691E, ["coral"] = 0xFF7F50, ["cornflowerblue"] = 0x6495ED,
			["cornsilk"] = 0xFFF8DC, ["crimson"] = 0xDC143C, ["cyan"] = 0x00FFFF, ["darkblue"] = 0x00008B, ["darkcyan"] = 0x008B8B, ["darkgoldenrod"] = 0xB8860B,
			["darkgray"] = 0xA9A9A9, ["darkgreen"] = 0x006400, ["darkgrey"] = 0xA9A9A9, ["darkkhaki"] = 0xBDB76B, ["darkmagenta"] = 0x8B008B, ["darkolivegreen"] = 0x556B2F,
			["darkorange"] = 0xFF8C00, ["darkorchid"] = 0x9932CC, ["darkred"] = 0x8B0000, ["darksalmon"] = 0xE9967A, ["darkseagreen"] = 0x8FBC8F, ["darkslateblue"] = 0x483D8B,
			["darkslategray"] = 0x2F4F4F, ["darkslategrey"] = 0x2F4F4F, ["darkturquoise"] = 0x00CED1, ["darkviolet"] = 0x9400D3, ["deeppink"] = 0xFF1493, ["deepskyblue"] = 0x00BFFF,
			["dimgray"] = 0x696969, ["dimgrey"] = 0x696969, ["dodgerblue"] = 0x1E90FF, ["firebrick"] = 0xB22222, ["floralwhite"] = 0xFFFAF0, ["forestgreen"] = 0x228B22,
			["fuchsia"] = 0xFF00FF, ["gainsboro"] = 0xDCDCDC, ["ghostwhite"] = 0xF8F8FF, ["gold"] = 0xFFD700, ["goldenrod"] = 0xDAA520, ["gray"] = 0x808080,
			["green"] = 0x008000, ["greenyellow"] = 0xADFF2F, ["grey"] = 0x808080, ["honeydew"] = 0xF0FFF0, ["hotpink"] = 0xFF69B4, ["indianred"] = 0xCD5C5C,
			["indigo"] = 0x4B0082, ["ivory"] = 0xFFFFF0, ["khaki"] = 0xF0E68C, ["lavender"] = 0xE6E6FA, ["lavenderblush"] = 0xFFF0F5, ["lawngreen"] = 0x7CFC00,
			["lemonchiffon"] = 0xFFFACD, ["lightblue"] = 0xADD8E6, ["lightcoral"] = 0xF08080, ["lightcyan"] = 0xE0FFFF, ["lightgoldenrodyellow"] = 0xFAFAD2, ["lightgray"] = 0xD3D3D3,
			["lightgreen"] = 0x90EE90, ["lightgrey"] = 0xD3D3D3, ["lightpink"] = 0xFFB6C1, ["lightsalmon"] = 0xFFA07A, ["lightseagreen"] = 0x20B2AA, ["lightskyblue"] = 0x87CEFA,
			["lightslategray"] = 0x778899, ["lightslategrey"] = 0x778899, ["lightsteelblue"] = 0xB0C4DE, ["lightyellow"] = 0xFFFFE0, ["lime"] = 0x00FF00, ["limegreen"] = 0x32CD32,
			["linen"] = 0xFAF0E6, ["magenta"] = 0xFF00FF, ["maroon"] = 0x800000, ["mediumaquamarine"] = 0x66CDAA, ["mediumblue"] = 0x0000CD, ["mediumorchid"] = 0xBA55D3,
			["mediumpurple"] = 0x9370DB, ["mediumseagreen"] = 0x3CB371, ["mediumslateblue"] = 0x7B68EE, ["mediumspringgreen"] = 0x00FA9A, ["mediumturquoise"] = 0x48D1CC, ["mediumvioletred"] = 0xC71585,
			["midnightblue"] = 0x191970, ["mintcream"] = 0xF5FFFA, ["mistyrose"] = 0xFFE4E1, ["moccasin"] = 0xFFE4B5, ["navajowhite"] = 0xFFDEAD, ["navy"] = 0x000080,
			["oldlace"] = 0xFDF5E6, ["olive"] = 0x808000, ["olivedrab"] = 0x6B8E23, ["orange"] = 0xFFA500, ["orangered"] = 0xFF4500, ["orchid"] = 0xDA70D6,
			["palegoldenrod"] = 0xEEE8AA, ["palegreen"] = 0x98FB98, ["paleturquoise"] = 0xAFEEEE, ["palevioletred"] = 0xDB7093, ["papayawhip"] = 0xFFEFD5, ["peachpuff"] = 0xFFDAB9,
			["peru"] = 0xCD853F, ["pink"] = 0xFFC0CB, ["plum"] = 0xDDA0DD, ["powderblue"] = 0xB0E0E6, ["purple"] = 0x800080, ["rebeccapurple"] = 0x663399,
			["red"] = 0xFF0000, ["rosybrown"] = 0xBC8F8F, ["royalblue"] = 0x4169E1, ["saddlebrown"] = 0x8B4513, ["salmon"] = 0xFA8072, ["sandybrown"] = 0xF4A460,
			["seagreen"] = 0x2E8B57, ["seashell"] = 0xFFF5EE, ["sienna"] = 0xA0522D, ["silver"] = 0xC0C0C0, ["skyblue"] = 0x87CEEB, ["slateblue"] = 0x6A5ACD,
			["slategray"] = 0x708090, ["slategrey"] = 0x708090, ["snow"] = 0xFFFAFA, ["springgreen"] = 0x00FF7F, ["steelblue"] = 0x4682B4, ["tan"] = 0xD2B48C,
			["teal"] = 0x008080, ["thistle"] = 0xD8BFD8, ["tomato"] = 0xFF6347, ["turquoise"] = 0x40E0D0, ["violet"] = 0xEE82EE, ["wheat"] = 0xF5DEB3,
			["white"] = 0xFFFFFF, ["whitesmoke"] = 0xF5F5F5, ["yellow"] = 0xFFFF00, ["yellowgreen"] = 0x9ACD32,
		};

		public static bool TryGet(string name, out Color color)
		{
			if (s_colors.TryGetValue(name, out uint rgb))
			{
				color = new Color(0xFF000000 | rgb);
				return true;
			}
			color = Color.Transparent;
			return false;
		}
	}
}
