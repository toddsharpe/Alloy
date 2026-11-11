using System;
using System.Collections.Generic;
using System.Globalization;
using System.Text;

namespace Alloy.Sdk.Web.Css
{
	public enum CssTokenType
	{
		Ident,
		Function,
		AtKeyword,
		Hash,
		String,
		Url,
		Number,
		Percentage,
		Dimension,
		Delim,
		Whitespace,
		Colon,
		Semicolon,
		Comma,
		OpenBracket,
		CloseBracket,
		OpenParen,
		CloseParen,
		OpenBrace,
		CloseBrace,
		Cdo,
		Cdc,
		BadString,
		BadUrl,
		EndOfFile,
	}

	// One token of CSS Syntax Level 3; Value holds the ident, string, url, function name, hash or delimiter, Number the numeric value, Unit the dimension's unit
	public readonly struct CssToken
	{
		public readonly CssTokenType Type;
		public readonly string Value;
		public readonly double Number;
		public readonly string Unit;
		public readonly bool IsInteger;
		public readonly bool HashIsId;

		public CssToken(CssTokenType type, string value = "", double number = 0, string unit = "", bool isInteger = false, bool hashIsId = false)
		{
			Type = type;
			Value = value;
			Number = number;
			Unit = unit;
			IsInteger = isInteger;
			HashIsId = hashIsId;
		}

		public bool Is(CssTokenType type) => Type == type;

		public bool IsIdent(string name) => Type == CssTokenType.Ident && string.Equals(Value, name, StringComparison.OrdinalIgnoreCase);

		public bool IsDelim(char c) => Type == CssTokenType.Delim && Value.Length == 1 && Value[0] == c;

		public override string ToString() => Type switch
		{
			CssTokenType.Ident => Value,
			CssTokenType.Function => Value + "(",
			CssTokenType.AtKeyword => "@" + Value,
			CssTokenType.Hash => "#" + Value,
			CssTokenType.String => "\"" + Value + "\"",
			CssTokenType.Url => "url(" + Value + ")",
			CssTokenType.Number => Number.ToString(CultureInfo.InvariantCulture),
			CssTokenType.Percentage => Number.ToString(CultureInfo.InvariantCulture) + "%",
			CssTokenType.Dimension => Number.ToString(CultureInfo.InvariantCulture) + Unit,
			CssTokenType.Delim => Value,
			CssTokenType.Whitespace => " ",
			CssTokenType.Colon => ":",
			CssTokenType.Semicolon => ";",
			CssTokenType.Comma => ",",
			CssTokenType.OpenBracket => "[",
			CssTokenType.CloseBracket => "]",
			CssTokenType.OpenParen => "(",
			CssTokenType.CloseParen => ")",
			CssTokenType.OpenBrace => "{",
			CssTokenType.CloseBrace => "}",
			_ => "",
		};
	}

	// CSS Syntax Level 3 tokenization: comments dropped, escapes decoded, numbers with units, url() in both forms
	public sealed class CssTokenizer
	{
		private readonly string _input;
		private int _at;

		public CssTokenizer(string input)
		{
			_input = input ?? "";
		}

		public static List<CssToken> Tokenize(string input)
		{
			CssTokenizer tokenizer = new CssTokenizer(input);
			List<CssToken> tokens = new List<CssToken>();
			while (true)
			{
				CssToken token = tokenizer.Next();
				if (token.Type == CssTokenType.EndOfFile)
					return tokens;
				tokens.Add(token);
			}
		}

		private char Peek(int offset = 0) => _at + offset < _input.Length ? _input[_at + offset] : '\0';

		private bool AtEnd => _at >= _input.Length;

		public CssToken Next()
		{
			SkipComments();
			if (AtEnd)
				return new CssToken(CssTokenType.EndOfFile);
			char c = _input[_at];
			if (IsWhitespace(c))
			{
				while (!AtEnd && IsWhitespace(_input[_at]))
					_at++;
				return new CssToken(CssTokenType.Whitespace);
			}
			switch (c)
			{
				case '"':
				case '\'':
					return ReadString(c);
				case '#':
					if (IsNameChar(Peek(1)) || IsValidEscape(1))
					{
						_at++;
						bool isId = WouldStartIdentifier();
						return new CssToken(CssTokenType.Hash, ReadName(), hashIsId: isId);
					}
					_at++;
					return new CssToken(CssTokenType.Delim, "#");
				case '(':
					_at++;
					return new CssToken(CssTokenType.OpenParen);
				case ')':
					_at++;
					return new CssToken(CssTokenType.CloseParen);
				case '[':
					_at++;
					return new CssToken(CssTokenType.OpenBracket);
				case ']':
					_at++;
					return new CssToken(CssTokenType.CloseBracket);
				case '{':
					_at++;
					return new CssToken(CssTokenType.OpenBrace);
				case '}':
					_at++;
					return new CssToken(CssTokenType.CloseBrace);
				case ',':
					_at++;
					return new CssToken(CssTokenType.Comma);
				case ':':
					_at++;
					return new CssToken(CssTokenType.Colon);
				case ';':
					_at++;
					return new CssToken(CssTokenType.Semicolon);
				case '+':
				case '.':
					if (WouldStartNumber())
						return ReadNumeric();
					_at++;
					return new CssToken(CssTokenType.Delim, c.ToString());
				case '-':
					if (WouldStartNumber())
						return ReadNumeric();
					if (Peek(1) == '-' && Peek(2) == '>')
					{
						_at += 3;
						return new CssToken(CssTokenType.Cdc);
					}
					if (WouldStartIdentifier())
						return ReadIdentLike();
					_at++;
					return new CssToken(CssTokenType.Delim, "-");
				case '<':
					if (Peek(1) == '!' && Peek(2) == '-' && Peek(3) == '-')
					{
						_at += 4;
						return new CssToken(CssTokenType.Cdo);
					}
					_at++;
					return new CssToken(CssTokenType.Delim, "<");
				case '@':
					if (WouldStartIdentifier(1))
					{
						_at++;
						return new CssToken(CssTokenType.AtKeyword, ReadName());
					}
					_at++;
					return new CssToken(CssTokenType.Delim, "@");
				case '\\':
					if (IsValidEscape(0))
						return ReadIdentLike();
					_at++;
					return new CssToken(CssTokenType.Delim, "\\");
			}
			if (char.IsAsciiDigit(c))
				return ReadNumeric();
			if (IsNameStart(c))
				return ReadIdentLike();
			_at++;
			return new CssToken(CssTokenType.Delim, c.ToString());
		}

		private void SkipComments()
		{
			while (Peek() == '/' && Peek(1) == '*')
			{
				int end = _input.IndexOf("*/", _at + 2, StringComparison.Ordinal);
				_at = end < 0 ? _input.Length : end + 2;
			}
		}

		private static bool IsWhitespace(char c) => c == ' ' || c == '\t' || c == '\n' || c == '\r' || c == '\f';

		private static bool IsNameStart(char c) => char.IsAsciiLetter(c) || c == '_' || c >= 0x80;

		private static bool IsNameChar(char c) => IsNameStart(c) || char.IsAsciiDigit(c) || c == '-';

		private bool IsValidEscape(int offset) => Peek(offset) == '\\' && Peek(offset + 1) != '\n' && _at + offset + 1 < _input.Length;

		private bool WouldStartIdentifier(int offset = 0)
		{
			char first = Peek(offset);
			if (first == '-')
				return IsNameStart(Peek(offset + 1)) || Peek(offset + 1) == '-' || IsValidEscape(offset + 1);
			return IsNameStart(first) || IsValidEscape(offset);
		}

		private bool WouldStartNumber()
		{
			char first = Peek();
			if (first == '+' || first == '-')
				return char.IsAsciiDigit(Peek(1)) || (Peek(1) == '.' && char.IsAsciiDigit(Peek(2)));
			if (first == '.')
				return char.IsAsciiDigit(Peek(1));
			return char.IsAsciiDigit(first);
		}

		private string ReadName()
		{
			StringBuilder name = new StringBuilder();
			while (!AtEnd)
			{
				char c = _input[_at];
				if (IsNameChar(c))
				{
					name.Append(c);
					_at++;
				}
				else if (IsValidEscape(0))
				{
					_at++;
					name.Append(ReadEscape());
				}
				else
				{
					break;
				}
			}
			return name.ToString();
		}

		// After the backslash: up to six hex digits (and one optional whitespace) or the next character itself
		private string ReadEscape()
		{
			if (AtEnd)
				return "�";
			char c = _input[_at];
			if (!char.IsAsciiHexDigit(c))
			{
				_at++;
				return c.ToString();
			}
			int code = 0;
			int digits = 0;
			while (!AtEnd && digits < 6 && char.IsAsciiHexDigit(_input[_at]))
			{
				code = code * 16 + Convert.ToInt32(_input[_at].ToString(), 16);
				_at++;
				digits++;
			}
			if (!AtEnd && IsWhitespace(_input[_at]))
				_at++;
			if (code == 0 || code > 0x10FFFF || (code >= 0xD800 && code <= 0xDFFF))
				return "�";
			return char.ConvertFromUtf32(code);
		}

		private CssToken ReadIdentLike()
		{
			string name = ReadName();
			if (Peek() == '(')
			{
				_at++;
				if (string.Equals(name, "url", StringComparison.OrdinalIgnoreCase))
				{
					int save = _at;
					while (!AtEnd && IsWhitespace(_input[_at]))
						_at++;
					if (Peek() == '"' || Peek() == '\'')
					{
						_at = save;
						return new CssToken(CssTokenType.Function, name);
					}
					return ReadUrl();
				}
				return new CssToken(CssTokenType.Function, name);
			}
			return new CssToken(CssTokenType.Ident, name);
		}

		private CssToken ReadUrl()
		{
			StringBuilder url = new StringBuilder();
			while (true)
			{
				if (AtEnd)
					return new CssToken(CssTokenType.Url, url.ToString());
				char c = _input[_at];
				if (c == ')')
				{
					_at++;
					return new CssToken(CssTokenType.Url, url.ToString());
				}
				if (IsWhitespace(c))
				{
					while (!AtEnd && IsWhitespace(_input[_at]))
						_at++;
					if (AtEnd || _input[_at] == ')')
						continue;
					return BadUrl(url);
				}
				if (c == '"' || c == '\'' || c == '(' || c <= 0x08 || c == 0x0B || (c >= 0x0E && c <= 0x1F) || c == 0x7F)
					return BadUrl(url);
				if (c == '\\')
				{
					if (IsValidEscape(0))
					{
						_at++;
						url.Append(ReadEscape());
						continue;
					}
					return BadUrl(url);
				}
				url.Append(c);
				_at++;
			}
		}

		private CssToken BadUrl(StringBuilder url)
		{
			while (!AtEnd && _input[_at] != ')')
			{
				if (IsValidEscape(0))
					_at++;
				_at++;
			}
			if (!AtEnd)
				_at++;
			return new CssToken(CssTokenType.BadUrl, url.ToString());
		}

		private CssToken ReadString(char quote)
		{
			_at++;
			StringBuilder value = new StringBuilder();
			while (!AtEnd)
			{
				char c = _input[_at];
				if (c == quote)
				{
					_at++;
					return new CssToken(CssTokenType.String, value.ToString());
				}
				if (c == '\n')
					return new CssToken(CssTokenType.BadString, value.ToString());
				if (c == '\\')
				{
					_at++;
					if (AtEnd)
						break;
					if (_input[_at] == '\n')
					{
						_at++;
						continue;
					}
					value.Append(ReadEscape());
					continue;
				}
				value.Append(c);
				_at++;
			}
			return new CssToken(CssTokenType.String, value.ToString());
		}

		private CssToken ReadNumeric()
		{
			int start = _at;
			bool isInteger = true;
			if (Peek() == '+' || Peek() == '-')
				_at++;
			while (char.IsAsciiDigit(Peek()))
				_at++;
			if (Peek() == '.' && char.IsAsciiDigit(Peek(1)))
			{
				isInteger = false;
				_at++;
				while (char.IsAsciiDigit(Peek()))
					_at++;
			}
			if ((Peek() == 'e' || Peek() == 'E') && (char.IsAsciiDigit(Peek(1)) || ((Peek(1) == '+' || Peek(1) == '-') && char.IsAsciiDigit(Peek(2)))))
			{
				isInteger = false;
				_at += 2;
				while (char.IsAsciiDigit(Peek()))
					_at++;
			}
			double number = double.TryParse(_input.AsSpan(start, _at - start), NumberStyles.Float, CultureInfo.InvariantCulture, out double parsed) ? parsed : 0;
			if (WouldStartIdentifier())
				return new CssToken(CssTokenType.Dimension, number: number, unit: ReadName().ToLowerInvariant(), isInteger: isInteger);
			if (Peek() == '%')
			{
				_at++;
				return new CssToken(CssTokenType.Percentage, number: number, isInteger: isInteger);
			}
			return new CssToken(CssTokenType.Number, number: number, isInteger: isInteger);
		}
	}
}
