using System;
using System.Collections.Generic;
using System.Text;

namespace Alloy.Sdk.Web.Html
{
	// Character references: the HTML 4 named set plus the common HTML5 additions, numeric references, and the Windows-1252 fixups browsers apply
	public static class Entities
	{
		private static readonly string[] s_latin1 =
		{
			"nbsp", "iexcl", "cent", "pound", "curren", "yen", "brvbar", "sect", "uml", "copy", "ordf", "laquo", "not", "shy", "reg", "macr",
			"deg", "plusmn", "sup2", "sup3", "acute", "micro", "para", "middot", "cedil", "sup1", "ordm", "raquo", "frac14", "frac12", "frac34", "iquest",
			"Agrave", "Aacute", "Acirc", "Atilde", "Auml", "Aring", "AElig", "Ccedil", "Egrave", "Eacute", "Ecirc", "Euml", "Igrave", "Iacute", "Icirc", "Iuml",
			"ETH", "Ntilde", "Ograve", "Oacute", "Ocirc", "Otilde", "Ouml", "times", "Oslash", "Ugrave", "Uacute", "Ucirc", "Uuml", "Yacute", "THORN", "szlig",
			"agrave", "aacute", "acirc", "atilde", "auml", "aring", "aelig", "ccedil", "egrave", "eacute", "ecirc", "euml", "igrave", "iacute", "icirc", "iuml",
			"eth", "ntilde", "ograve", "oacute", "ocirc", "otilde", "ouml", "divide", "oslash", "ugrave", "uacute", "ucirc", "uuml", "yacute", "thorn", "yuml",
		};

		private static readonly (string Name, int Code)[] s_others =
		{
			("quot", 34), ("amp", 38), ("apos", 39), ("lt", 60), ("gt", 62), ("OElig", 338), ("oelig", 339), ("Scaron", 352), ("scaron", 353), ("Yuml", 376),
			("fnof", 402), ("circ", 710), ("tilde", 732), ("ensp", 8194), ("emsp", 8195), ("thinsp", 8201), ("zwnj", 8204), ("zwj", 8205), ("lrm", 8206), ("rlm", 8207),
			("ndash", 8211), ("mdash", 8212), ("lsquo", 8216), ("rsquo", 8217), ("sbquo", 8218), ("ldquo", 8220), ("rdquo", 8221), ("bdquo", 8222), ("dagger", 8224),
			("Dagger", 8225), ("bull", 8226), ("hellip", 8230), ("permil", 8240), ("prime", 8242), ("Prime", 8243), ("lsaquo", 8249), ("rsaquo", 8250), ("oline", 8254),
			("frasl", 8260), ("euro", 8364), ("image", 8465), ("weierp", 8472), ("real", 8476), ("trade", 8482), ("alefsym", 8501), ("larr", 8592), ("uarr", 8593),
			("rarr", 8594), ("darr", 8595), ("harr", 8596), ("crarr", 8629), ("lArr", 8656), ("uArr", 8657), ("rArr", 8658), ("dArr", 8659), ("hArr", 8660),
			("forall", 8704), ("part", 8706), ("exist", 8707), ("empty", 8709), ("nabla", 8711), ("isin", 8712), ("notin", 8713), ("ni", 8715), ("prod", 8719),
			("sum", 8721), ("minus", 8722), ("lowast", 8727), ("radic", 8730), ("prop", 8733), ("infin", 8734), ("ang", 8736), ("and", 8743), ("or", 8744),
			("cap", 8745), ("cup", 8746), ("int", 8747), ("there4", 8756), ("sim", 8764), ("cong", 8773), ("asymp", 8776), ("ne", 8800), ("equiv", 8801),
			("le", 8804), ("ge", 8805), ("sub", 8834), ("sup", 8835), ("nsub", 8836), ("sube", 8838), ("supe", 8839), ("oplus", 8853), ("otimes", 8855),
			("perp", 8869), ("sdot", 8901), ("lceil", 8968), ("rceil", 8969), ("lfloor", 8970), ("rfloor", 8971), ("lang", 9001), ("rang", 9002), ("loz", 9674),
			("spades", 9824), ("clubs", 9827), ("hearts", 9829), ("diams", 9830), ("thetasym", 977), ("upsih", 978), ("piv", 982), ("sigmaf", 962),
			("check", 10003), ("cross", 10007), ("star", 9734), ("starf", 9733), ("phone", 9742), ("female", 9792), ("male", 9794), ("larrhk", 8617),
			("hookleftarrow", 8617), ("hyphen", 8208), ("dash", 8208), ("horbar", 8213), ("Vert", 8214), ("nldr", 8229), ("caret", 8257), ("lsquor", 8218),
			("rsquor", 8217), ("ldquor", 8222), ("rdquor", 8221), ("NewLine", 10), ("Tab", 9), ("excl", 33), ("num", 35), ("dollar", 36), ("percnt", 37),
			("lpar", 40), ("rpar", 41), ("ast", 42), ("plus", 43), ("comma", 44), ("period", 46), ("sol", 47), ("colon", 58), ("semi", 59), ("equals", 61),
			("quest", 63), ("commat", 64), ("lsqb", 91), ("bsol", 92), ("rsqb", 93), ("Hat", 94), ("lowbar", 95), ("grave", 96), ("lcub", 123), ("verbar", 124),
			("rcub", 125), ("copysr", 8471), ("trade", 8482), ("numero", 8470), ("ohm", 937), ("mho", 8487), ("angst", 197), ("micro", 181), ("half", 189),
			("deg", 176), ("laquo", 171), ("raquo", 187), ("times", 215), ("divide", 247), ("middot", 183), ("centerdot", 183), ("bullet", 8226),
			("mldr", 8230), ("nbsp", 160), ("NonBreakingSpace", 160), ("ensp", 8194), ("emsp", 8195), ("emsp13", 8196), ("emsp14", 8197), ("numsp", 8199),
			("puncsp", 8200), ("hairsp", 8202), ("ZeroWidthSpace", 8203), ("MediumSpace", 8287), ("leftarrow", 8592), ("rightarrow", 8594), ("uparrow", 8593),
			("downarrow", 8595), ("leftrightarrow", 8596), ("Rightarrow", 8658), ("Leftarrow", 8656), ("infin", 8734), ("approx", 8776), ("neq", 8800),
			("leq", 8804), ("geq", 8805), ("setminus", 8726), ("pm", 177), ("PlusMinus", 177), ("Dot", 168), ("die", 168), ("DoubleDot", 168),
		};

		private static readonly string[] s_greekUpper =
		{
			"Alpha", "Beta", "Gamma", "Delta", "Epsilon", "Zeta", "Eta", "Theta", "Iota", "Kappa", "Lambda", "Mu", "Nu", "Xi", "Omicron", "Pi", "Rho", "",
			"Sigma", "Tau", "Upsilon", "Phi", "Chi", "Psi", "Omega",
		};

		private static readonly string[] s_greekLower =
		{
			"alpha", "beta", "gamma", "delta", "epsilon", "zeta", "eta", "theta", "iota", "kappa", "lambda", "mu", "nu", "xi", "omicron", "pi", "rho", "",
			"sigma", "tau", "upsilon", "phi", "chi", "psi", "omega",
		};

		// Code points 0x80 to 0x9F in a numeric reference mean the Windows-1252 characters, as browsers read them
		private static readonly int[] s_windows1252 =
		{
			8364, 0x81, 8218, 402, 8222, 8230, 8224, 8225, 710, 8240, 352, 8249, 338, 0x8D, 381, 0x8F,
			0x90, 8216, 8217, 8220, 8221, 8226, 8211, 8212, 732, 8482, 353, 8250, 339, 0x9D, 382, 376,
		};

		private static readonly Dictionary<string, string> s_names = Build();

		// Names that browsers still accept without the semicolon in text (the legacy HTML 4 Latin-1 set and the basics)
		private static readonly HashSet<string> s_legacy = BuildLegacy();

		private static Dictionary<string, string> Build()
		{
			Dictionary<string, string> names = new Dictionary<string, string>(StringComparer.Ordinal);
			for (int i = 0; i < s_latin1.Length; i++)
				names[s_latin1[i]] = ((char)(160 + i)).ToString();
			foreach ((string name, int code) in s_others)
				names[name] = char.ConvertFromUtf32(code);
			for (int i = 0; i < s_greekUpper.Length; i++)
			{
				if (s_greekUpper[i].Length > 0)
					names[s_greekUpper[i]] = ((char)(913 + i)).ToString();
				if (s_greekLower[i].Length > 0)
					names[s_greekLower[i]] = ((char)(945 + i)).ToString();
			}
			return names;
		}

		private static HashSet<string> BuildLegacy()
		{
			HashSet<string> legacy = new HashSet<string>(s_latin1, StringComparer.Ordinal);
			legacy.Add("quot");
			legacy.Add("amp");
			legacy.Add("lt");
			legacy.Add("gt");
			return legacy;
		}

		public static bool TryLookup(string name, out string value) => s_names.TryGetValue(name, out value);

		// Decodes the reference starting at text[at] (the '&'); returns the characters and how much was consumed, or 0 to keep the '&' as is
		public static int Decode(ReadOnlySpan<char> text, int at, bool inAttribute, StringBuilder output)
		{
			if (at + 1 >= text.Length)
				return 0;
			if (text[at + 1] == '#')
				return DecodeNumeric(text, at, output);

			int end = at + 1;
			while (end < text.Length && end - at <= 32 && char.IsAsciiLetterOrDigit(text[end]))
				end++;
			if (end == at + 1)
				return 0;
			string name = text.Slice(at + 1, end - at - 1).ToString();
			bool terminated = end < text.Length && text[end] == ';';
			if (s_names.TryGetValue(name, out string value))
			{
				if (terminated)
				{
					output.Append(value);
					return end - at + 1;
				}
				if (!s_legacy.Contains(name))
					return 0;
				if (inAttribute && end < text.Length && (text[end] == '=' || char.IsAsciiLetterOrDigit(text[end])))
					return 0;
				output.Append(value);
				return end - at;
			}

			// A legacy name may be a prefix of what was written (&copyright) in text
			if (!inAttribute)
			{
				for (int length = Math.Min(name.Length - 1, 8); length >= 2; length--)
				{
					string prefix = name.Substring(0, length);
					if (s_legacy.Contains(prefix) && s_names.TryGetValue(prefix, out string prefixValue))
					{
						output.Append(prefixValue);
						return length + 1;
					}
				}
			}
			return 0;
		}

		private static int DecodeNumeric(ReadOnlySpan<char> text, int at, StringBuilder output)
		{
			int cursor = at + 2;
			bool hex = cursor < text.Length && (text[cursor] == 'x' || text[cursor] == 'X');
			if (hex)
				cursor++;
			int start = cursor;
			long code = 0;
			while (cursor < text.Length && cursor - start < 9)
			{
				char c = text[cursor];
				int digit = hex ? HexValue(c) : (c >= '0' && c <= '9' ? c - '0' : -1);
				if (digit < 0)
					break;
				code = code * (hex ? 16 : 10) + digit;
				cursor++;
			}
			if (cursor == start)
				return 0;
			if (cursor < text.Length && text[cursor] == ';')
				cursor++;
			if (code == 0 || code > 0x10FFFF || (code >= 0xD800 && code <= 0xDFFF))
				code = 0xFFFD;
			else if (code >= 0x80 && code <= 0x9F)
				code = s_windows1252[code - 0x80];
			output.Append(char.ConvertFromUtf32((int)code));
			return cursor - at;
		}

		private static int HexValue(char c)
		{
			if (c >= '0' && c <= '9')
				return c - '0';
			if (c >= 'a' && c <= 'f')
				return c - 'a' + 10;
			if (c >= 'A' && c <= 'F')
				return c - 'A' + 10;
			return -1;
		}

		// Text with its references replaced; what isn't a reference stays as written
		public static string DecodeAll(string text, bool inAttribute = false)
		{
			if (text.IndexOf('&') < 0)
				return text;
			StringBuilder output = new StringBuilder(text.Length);
			ReadOnlySpan<char> span = text;
			for (int i = 0; i < span.Length;)
			{
				if (span[i] == '&')
				{
					int consumed = Decode(span, i, inAttribute, output);
					if (consumed > 0)
					{
						i += consumed;
						continue;
					}
				}
				output.Append(span[i++]);
			}
			return output.ToString();
		}
	}
}
