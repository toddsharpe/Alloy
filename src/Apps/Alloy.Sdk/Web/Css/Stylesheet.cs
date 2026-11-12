using System;
using System.Collections.Generic;

namespace Alloy.Sdk.Web.Css
{
	// One property: value pair from a declaration block, with its tokens kept raw until a property parser wants them
	public sealed class Declaration
	{
		public string Property;
		public List<CssToken> Value;
		public bool Important;

		public override string ToString() => Property + ": " + CssValue.Join(Value) + (Important ? " !important" : "");
	}

	// A style rule: selectors and declarations, with where it came from for the cascade
	public sealed class StyleRule
	{
		public List<Selector> Selectors;
		public readonly List<Declaration> Declarations = new List<Declaration>();
	}

	public sealed class Stylesheet
	{
		public readonly List<StyleRule> Rules = new List<StyleRule>();

		// Stylesheets this one pulls in with @import, as written, for the loader to fetch
		public readonly List<string> Imports = new List<string>();
	}

	// Media conditions a page can ask about: the screen type and the viewport's width and height
	public readonly struct MediaEnvironment
	{
		public readonly int Width;
		public readonly int Height;

		public MediaEnvironment(int width, int height)
		{
			Width = width;
			Height = height;
		}
	}

	// CSS Syntax Level 3 parsing of a stylesheet into rules, with @media evaluated against the environment and other at-rules skipped
	public sealed class CssParser
	{
		private readonly List<CssToken> _tokens;
		private readonly MediaEnvironment _media;
		private int _at;

		private CssParser(List<CssToken> tokens, MediaEnvironment media)
		{
			_tokens = tokens;
			_media = media;
		}

		public static Stylesheet Parse(string css, MediaEnvironment media)
		{
			CssParser parser = new CssParser(CssTokenizer.Tokenize(css), media);
			Stylesheet sheet = new Stylesheet();
			parser.ParseRules(sheet, true);
			return sheet;
		}

		// The declarations of a style attribute
		public static List<Declaration> ParseDeclarations(string css)
		{
			CssParser parser = new CssParser(CssTokenizer.Tokenize(css), default);
			return parser.ParseDeclarationList(parser._tokens.Count);
		}

		private bool AtEnd => _at >= _tokens.Count;

		private void ParseRules(Stylesheet sheet, bool topLevel)
		{
			while (!AtEnd)
			{
				CssToken token = _tokens[_at];
				if (token.Type == CssTokenType.Whitespace || token.Type == CssTokenType.Cdo || token.Type == CssTokenType.Cdc)
				{
					_at++;
					continue;
				}
				if (token.Type == CssTokenType.CloseBrace)
				{
					if (!topLevel)
						return;
					_at++;
					continue;
				}
				if (token.Type == CssTokenType.AtKeyword)
					ParseAtRule(sheet);
				else
					ParseQualifiedRule(sheet);
			}
		}

		private void ParseAtRule(Stylesheet sheet)
		{
			string name = _tokens[_at].Value.ToLowerInvariant();
			_at++;
			int preludeStart = _at;
			while (!AtEnd && _tokens[_at].Type != CssTokenType.OpenBrace && _tokens[_at].Type != CssTokenType.Semicolon)
				_at++;
			List<CssToken> prelude = _tokens.GetRange(preludeStart, _at - preludeStart);
			if (AtEnd || _tokens[_at].Type == CssTokenType.Semicolon)
			{
				if (!AtEnd)
					_at++;
				if (name == "import")
					sheet.Imports.Add(ImportTarget(prelude));
				return;
			}

			_at++;
			if (name == "media" && MediaQuery.Matches(prelude, _media))
			{
				ParseRules(sheet, false);
				if (!AtEnd)
					_at++;
				return;
			}
			if (name == "supports" || name == "document" || name == "layer")
			{
				// Rules inside are kept for supports (the properties used here are plain) and layers (order is close enough)
				ParseRules(sheet, false);
				if (!AtEnd)
					_at++;
				return;
			}
			SkipBlock();
		}

		private static string ImportTarget(List<CssToken> prelude)
		{
			foreach (CssToken token in prelude)
			{
				if (token.Type == CssTokenType.String || token.Type == CssTokenType.Url)
					return token.Value;
				if (token.Type == CssTokenType.Function && string.Equals(token.Value, "url", StringComparison.OrdinalIgnoreCase))
					continue;
			}
			return "";
		}

		// Skips a { } block the cursor is just inside of, nested blocks included
		private void SkipBlock()
		{
			int depth = 1;
			while (!AtEnd)
			{
				CssTokenType type = _tokens[_at++].Type;
				if (type == CssTokenType.OpenBrace)
					depth++;
				else if (type == CssTokenType.CloseBrace && --depth == 0)
					return;
			}
		}

		private void ParseQualifiedRule(Stylesheet sheet)
		{
			int preludeStart = _at;
			while (!AtEnd && _tokens[_at].Type != CssTokenType.OpenBrace)
				_at++;
			if (AtEnd)
				return;
			List<CssToken> prelude = _tokens.GetRange(preludeStart, _at - preludeStart);
			_at++;
			int blockEnd = FindBlockEnd();
			List<Selector> selectors = SelectorParser.ParseList(prelude);
			if (selectors != null)
			{
				StyleRule rule = new StyleRule { Selectors = selectors };
				rule.Declarations.AddRange(ParseDeclarationList(blockEnd));
				sheet.Rules.Add(rule);
			}
			_at = Math.Min(blockEnd + 1, _tokens.Count);
		}

		private int FindBlockEnd()
		{
			int depth = 1;
			for (int i = _at; i < _tokens.Count; i++)
			{
				CssTokenType type = _tokens[i].Type;
				if (type == CssTokenType.OpenBrace)
					depth++;
				else if (type == CssTokenType.CloseBrace && --depth == 0)
					return i;
			}
			return _tokens.Count;
		}

		// Declarations up to end: name, colon, value tokens, an optional !important, separated by semicolons; junk is skipped to the next semicolon
		private List<Declaration> ParseDeclarationList(int end)
		{
			List<Declaration> declarations = new List<Declaration>();
			while (_at < end)
			{
				CssToken token = _tokens[_at];
				if (token.Type == CssTokenType.Whitespace || token.Type == CssTokenType.Semicolon)
				{
					_at++;
					continue;
				}
				if (token.Type == CssTokenType.AtKeyword)
				{
					SkipDeclaration(end);
					continue;
				}
				if (token.Type != CssTokenType.Ident)
				{
					SkipDeclaration(end);
					continue;
				}
				string property = token.Value.ToLowerInvariant();
				_at++;
				while (_at < end && _tokens[_at].Type == CssTokenType.Whitespace)
					_at++;
				if (_at >= end || _tokens[_at].Type != CssTokenType.Colon)
				{
					SkipDeclaration(end);
					continue;
				}
				_at++;
				List<CssToken> value = new List<CssToken>();
				int depth = 0;
				while (_at < end)
				{
					CssToken part = _tokens[_at];
					if (part.Type == CssTokenType.Semicolon && depth == 0)
						break;
					if (part.Type == CssTokenType.OpenParen || part.Type == CssTokenType.Function || part.Type == CssTokenType.OpenBracket || part.Type == CssTokenType.OpenBrace)
						depth++;
					else if (part.Type == CssTokenType.CloseParen || part.Type == CssTokenType.CloseBracket || part.Type == CssTokenType.CloseBrace)
						depth--;
					value.Add(part);
					_at++;
				}
				Declaration declaration = new Declaration { Property = property, Value = Trim(value) };
				declaration.Important = StripImportant(declaration.Value);
				if (declaration.Value.Count > 0)
					declarations.Add(declaration);
			}
			return declarations;
		}

		private void SkipDeclaration(int end)
		{
			int depth = 0;
			while (_at < end)
			{
				CssToken token = _tokens[_at++];
				if (token.Type == CssTokenType.OpenParen || token.Type == CssTokenType.Function || token.Type == CssTokenType.OpenBrace || token.Type == CssTokenType.OpenBracket)
					depth++;
				else if (token.Type == CssTokenType.CloseParen || token.Type == CssTokenType.CloseBrace || token.Type == CssTokenType.CloseBracket)
					depth--;
				else if (token.Type == CssTokenType.Semicolon && depth <= 0)
					return;
			}
		}

		private static List<CssToken> Trim(List<CssToken> tokens)
		{
			int start = 0;
			int end = tokens.Count;
			while (start < end && tokens[start].Type == CssTokenType.Whitespace)
				start++;
			while (end > start && tokens[end - 1].Type == CssTokenType.Whitespace)
				end--;
			return tokens.GetRange(start, end - start);
		}

		private static bool StripImportant(List<CssToken> value)
		{
			int end = value.Count;
			while (end > 0 && value[end - 1].Type == CssTokenType.Whitespace)
				end--;
			if (end >= 2 && value[end - 1].IsIdent("important"))
			{
				int bang = end - 2;
				while (bang > 0 && value[bang].Type == CssTokenType.Whitespace)
					bang--;
				if (value[bang].IsDelim('!'))
				{
					value.RemoveRange(bang, value.Count - bang);
					while (value.Count > 0 && value[value.Count - 1].Type == CssTokenType.Whitespace)
						value.RemoveAt(value.Count - 1);
					return true;
				}
			}
			return false;
		}
	}

	// @media evaluation for a screen: media types, and, not, only, and the width and height range features
	public static class MediaQuery
	{
		public static bool Matches(List<CssToken> prelude, MediaEnvironment media)
		{
			List<List<CssToken>> queries = Split(prelude);
			if (queries.Count == 0)
				return true;
			foreach (List<CssToken> query in queries)
			{
				if (MatchesOne(query, media))
					return true;
			}
			return false;
		}

		private static List<List<CssToken>> Split(List<CssToken> tokens)
		{
			List<List<CssToken>> parts = new List<List<CssToken>>();
			List<CssToken> current = new List<CssToken>();
			int depth = 0;
			foreach (CssToken token in tokens)
			{
				if (token.Type == CssTokenType.OpenParen || token.Type == CssTokenType.Function)
					depth++;
				else if (token.Type == CssTokenType.CloseParen)
					depth--;
				if (token.Type == CssTokenType.Comma && depth == 0)
				{
					parts.Add(current);
					current = new List<CssToken>();
					continue;
				}
				if (token.Type != CssTokenType.Whitespace || current.Count > 0)
					current.Add(token);
			}
			if (current.Count > 0)
				parts.Add(current);
			return parts;
		}

		private static bool MatchesOne(List<CssToken> query, MediaEnvironment media)
		{
			bool negate = false;
			bool result = true;
			int at = 0;
			while (at < query.Count)
			{
				CssToken token = query[at];
				if (token.Type == CssTokenType.Whitespace)
				{
					at++;
					continue;
				}
				if (token.IsIdent("not"))
				{
					negate = true;
					at++;
					continue;
				}
				if (token.IsIdent("only") || token.IsIdent("and"))
				{
					at++;
					continue;
				}
				if (token.Type == CssTokenType.Ident)
				{
					string type = token.Value.ToLowerInvariant();
					result &= type == "screen" || type == "all";
					at++;
					continue;
				}
				if (token.Type == CssTokenType.OpenParen)
				{
					int close = at + 1;
					int depth = 1;
					while (close < query.Count && depth > 0)
					{
						if (query[close].Type == CssTokenType.OpenParen || query[close].Type == CssTokenType.Function)
							depth++;
						else if (query[close].Type == CssTokenType.CloseParen)
							depth--;
						close++;
					}
					result &= Feature(query.GetRange(at + 1, Math.Max(0, close - at - 2)), media);
					at = close;
					continue;
				}
				at++;
			}
			return negate ? !result : result;
		}

		// (min-width: 600px), (max-width: 900px), (width: 1024px), (orientation: landscape), (prefers-color-scheme: ...) and unknown features (which fail)
		private static bool Feature(List<CssToken> tokens, MediaEnvironment media)
		{
			List<CssToken> parts = new List<CssToken>();
			foreach (CssToken token in tokens)
			{
				if (token.Type != CssTokenType.Whitespace)
					parts.Add(token);
			}
			if (parts.Count == 0)
				return false;
			if (parts.Count > 2 && (parts[0].Type != CssTokenType.Ident || parts[1].Type != CssTokenType.Colon))
				return Range(parts, media);
			if (parts[0].Type != CssTokenType.Ident)
				return false;
			string name = parts[0].Value.ToLowerInvariant();
			if (parts.Count == 1)
				return name == "color" || name == "width" || name == "height";
			if (parts.Count < 3 || parts[1].Type != CssTokenType.Colon)
				return false;
			CssToken value = parts[2];
			switch (name)
			{
				case "min-width":
					return media.Width >= Pixels(value);
				case "max-width":
					return media.Width <= Pixels(value);
				case "width":
					return media.Width == Pixels(value);
				case "min-height":
					return media.Height >= Pixels(value);
				case "max-height":
					return media.Height <= Pixels(value);
				case "height":
					return media.Height == Pixels(value);
				case "orientation":
					return value.IsIdent(media.Width >= media.Height ? "landscape" : "portrait");
				case "prefers-color-scheme":
					return value.IsIdent("dark");
				case "prefers-reduced-motion":
					return value.IsIdent("no-preference");
				case "hover":
					return value.IsIdent("hover");
				case "pointer":
					return value.IsIdent("fine");
				case "min-resolution":
					return value.Number <= 96 || (value.Type == CssTokenType.Dimension && value.Unit == "dppx" && value.Number <= 1);
				case "max-resolution":
					return true;
				case "min-device-width":
					return media.Width >= Pixels(value);
				case "max-device-width":
					return media.Width <= Pixels(value);
				default:
					return false;
			}
		}

		// The range syntax: (width >= 1012px), (width < 768px), (400px <= height < 900px)
		private static bool Range(List<CssToken> parts, MediaEnvironment media)
		{
			double? left = null;
			string leftOp = null;
			double? right = null;
			string rightOp = null;
			double? feature = null;
			string op = null;
			foreach (CssToken part in parts)
			{
				if (part.Type == CssTokenType.Delim && (part.Value == "<" || part.Value == ">" || part.Value == "="))
				{
					op = op != null && part.Value == "=" ? op + "=" : part.Value;
					continue;
				}
				if (part.Type == CssTokenType.Ident)
				{
					string name = part.Value.ToLowerInvariant();
					if (name == "width")
						feature = media.Width;
					else if (name == "height")
						feature = media.Height;
					else
						return false;
					leftOp = op;
					op = null;
					continue;
				}
				if (part.Type == CssTokenType.Dimension || part.Type == CssTokenType.Number)
				{
					if (feature == null)
						left = Pixels(part);
					else
					{
						right = Pixels(part);
						rightOp = op;
					}
					op = null;
					continue;
				}
				return false;
			}
			if (feature == null)
				return false;
			bool result = true;
			if (left != null)
				result &= Compare(left.Value, leftOp, feature.Value);
			if (right != null)
				result &= Compare(feature.Value, rightOp, right.Value);
			return result && (left != null || right != null);
		}

		private static bool Compare(double a, string op, double b) => op switch
		{
			"<" => a < b,
			"<=" => a <= b,
			">" => a > b,
			">=" => a >= b,
			"=" => a == b,
			_ => false,
		};

		private static double Pixels(CssToken token)
		{
			if (token.Type == CssTokenType.Dimension)
				return CssValue.ToPixels(token.Number, token.Unit, 16, 16);
			return token.Number;
		}
	}
}
