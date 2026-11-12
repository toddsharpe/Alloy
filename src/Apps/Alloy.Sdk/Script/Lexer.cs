using System;
using System.Collections.Generic;
using System.Globalization;
using System.Text;

namespace Alloy.Sdk.Script
{
	public enum TokenKind
	{
		EndOfFile,
		Identifier,
		Keyword,
		Number,
		String,
		Template,
		Punctuator,
		RegExp,
	}

	// A token with its text, its value for literals, and whether a line break came before it (for semicolon insertion)
	public sealed class Token
	{
		public TokenKind Kind;
		public string Text;
		public string Value;
		public double Number;
		public bool NewlineBefore;
		public int Position;
		public int Line;

		// Template literals: the cooked strings and the expression sources between them
		public List<string> TemplateStrings;
		public List<string> TemplateExpressions;

		public override string ToString() => Kind + " " + Text;
	}

	public sealed class SyntaxError : Exception
	{
		public readonly int Line;

		public SyntaxError(string message, int line) : base(message + " (line " + line + ")")
		{
			Line = line;
		}
	}

	// Tokenizes JavaScript source; regular expression literals are recognized from what came before them
	public sealed class Lexer
	{
		private static readonly HashSet<string> Keywords = new HashSet<string>(StringComparer.Ordinal)
		{
			"var", "let", "const", "function", "return", "if", "else", "for", "while", "do", "break", "continue", "new", "delete", "typeof", "instanceof", "in", "of",
			"this", "null", "undefined", "true", "false", "switch", "case", "default", "throw", "try", "catch", "finally", "class", "extends", "super", "void", "async", "await", "yield", "static", "get", "set",
		};

		private static readonly string[] Punctuators =
		{
			">>>=", "...", "===", "!==", "**=", "<<=", ">>=", ">>>", "&&=", "||=", "??=",
			"=>", "==", "!=", "<=", ">=", "&&", "||", "??", "?.", "++", "--", "+=", "-=", "*=", "/=", "%=", "&=", "|=", "^=", "<<", ">>", "**",
			"{", "}", "(", ")", "[", "]", ";", ",", "<", ">", "+", "-", "*", "/", "%", "&", "|", "^", "!", "~", "?", ":", "=", ".", "#",
		};

		private readonly string _source;
		private int _at;
		private int _line = 1;
		private Token _previous;

		public Lexer(string source)
		{
			_source = source;
		}

		public List<Token> Tokenize()
		{
			List<Token> tokens = new List<Token>();
			while (true)
			{
				Token token = Next();
				tokens.Add(token);
				if (token.Kind == TokenKind.EndOfFile)
					return tokens;
			}
		}

		private Token Next()
		{
			bool newline = SkipTrivia();
			Token token = new Token { NewlineBefore = newline, Position = _at, Line = _line };
			if (_at >= _source.Length)
			{
				token.Kind = TokenKind.EndOfFile;
				token.Text = "";
				_previous = token;
				return token;
			}
			char c = _source[_at];
			if (IsIdentifierStart(c))
				ReadIdentifier(token);
			else if (char.IsDigit(c) || (c == '.' && _at + 1 < _source.Length && char.IsDigit(_source[_at + 1])))
				ReadNumber(token);
			else if (c == '"' || c == '\'')
				ReadString(token, c);
			else if (c == '`')
				ReadTemplate(token);
			else if (c == '/' && RegExpAllowed())
				ReadRegExp(token);
			else
				ReadPunctuator(token);
			_previous = token;
			return token;
		}

		// A slash starts a regular expression when an operand could not come before it
		private bool RegExpAllowed()
		{
			if (_previous == null)
				return true;
			switch (_previous.Kind)
			{
				case TokenKind.Number:
				case TokenKind.String:
				case TokenKind.Template:
				case TokenKind.RegExp:
				case TokenKind.Identifier:
					return false;
				case TokenKind.Keyword:
					return _previous.Text is not ("this" or "super" or "null" or "undefined" or "true" or "false");
				case TokenKind.Punctuator:
					return _previous.Text is not (")" or "]" or "}" or "++" or "--");
				default:
					return true;
			}
		}

		private bool SkipTrivia()
		{
			bool newline = false;
			while (_at < _source.Length)
			{
				char c = _source[_at];
				if (c == '\n')
				{
					newline = true;
					_line++;
					_at++;
				}
				else if (c == ' ' || c == '\t' || c == '\r' || c == '\f' || c == '\v' || c == '\u00A0' || c == '\uFEFF' || c == '\u2028' || c == '\u2029')
					_at++;
				else if (c == '/' && _at + 1 < _source.Length && _source[_at + 1] == '/')
				{
					while (_at < _source.Length && _source[_at] != '\n')
						_at++;
				}
				else if (c == '/' && _at + 1 < _source.Length && _source[_at + 1] == '*')
				{
					int end = _source.IndexOf("*/", _at + 2, StringComparison.Ordinal);
					if (end < 0)
						end = _source.Length - 2;
					for (int i = _at; i < end; i++)
					{
						if (_source[i] == '\n')
						{
							newline = true;
							_line++;
						}
					}
					_at = end + 2;
				}
				else if (c == '<' && _source.AsSpan(_at).StartsWith("<!--"))
				{
					while (_at < _source.Length && _source[_at] != '\n')
						_at++;
				}
				else if (c == '-' && (newline || _previous == null) && _source.AsSpan(_at).StartsWith("-->"))
				{
					while (_at < _source.Length && _source[_at] != '\n')
						_at++;
				}
				else
					break;
			}
			return newline;
		}

		private static bool IsIdentifierStart(char c) => char.IsLetter(c) || c == '_' || c == '$' || c == '\\' || c > 0x7F && char.IsLetterOrDigit(c);

		private static bool IsIdentifierPart(char c) => char.IsLetterOrDigit(c) || c == '_' || c == '$' || c == '\u200C' || c == '\u200D' || c == '\\';

		private void ReadIdentifier(Token token)
		{
			StringBuilder name = new StringBuilder();
			while (_at < _source.Length && IsIdentifierPart(_source[_at]))
			{
				if (_source[_at] == '\\' && _at + 1 < _source.Length && _source[_at + 1] == 'u')
				{
					_at += 2;
					name.Append(ReadUnicodeEscape());
					continue;
				}
				name.Append(_source[_at++]);
			}
			token.Text = name.ToString();
			token.Kind = Keywords.Contains(token.Text) ? TokenKind.Keyword : TokenKind.Identifier;
		}

		private void ReadNumber(Token token)
		{
			int start = _at;
			token.Kind = TokenKind.Number;
			if (_source[_at] == '0' && _at + 1 < _source.Length && (_source[_at + 1] == 'x' || _source[_at + 1] == 'X'))
			{
				_at += 2;
				int digits = _at;
				while (_at < _source.Length && (Uri.IsHexDigit(_source[_at]) || _source[_at] == '_'))
					_at++;
				token.Number = (double)long.Parse(_source.Substring(digits, _at - digits).Replace("_", ""), NumberStyles.HexNumber, CultureInfo.InvariantCulture);
			}
			else if (_source[_at] == '0' && _at + 1 < _source.Length && (_source[_at + 1] == 'b' || _source[_at + 1] == 'B'))
			{
				_at += 2;
				int digits = _at;
				while (_at < _source.Length && (_source[_at] == '0' || _source[_at] == '1' || _source[_at] == '_'))
					_at++;
				token.Number = Convert.ToInt64(_source.Substring(digits, _at - digits).Replace("_", ""), 2);
			}
			else if (_source[_at] == '0' && _at + 1 < _source.Length && (_source[_at + 1] == 'o' || _source[_at + 1] == 'O'))
			{
				_at += 2;
				int digits = _at;
				while (_at < _source.Length && _source[_at] >= '0' && _source[_at] <= '7')
					_at++;
				token.Number = Convert.ToInt64(_source.Substring(digits, _at - digits), 8);
			}
			else
			{
				while (_at < _source.Length && (char.IsDigit(_source[_at]) || _source[_at] == '_'))
					_at++;
				if (_at < _source.Length && _source[_at] == '.')
				{
					_at++;
					while (_at < _source.Length && (char.IsDigit(_source[_at]) || _source[_at] == '_'))
						_at++;
				}
				if (_at < _source.Length && (_source[_at] == 'e' || _source[_at] == 'E'))
				{
					int save = _at;
					_at++;
					if (_at < _source.Length && (_source[_at] == '+' || _source[_at] == '-'))
						_at++;
					if (_at < _source.Length && char.IsDigit(_source[_at]))
					{
						while (_at < _source.Length && char.IsDigit(_source[_at]))
							_at++;
					}
					else
						_at = save;
				}
				string text = _source.Substring(start, _at - start).Replace("_", "");
				token.Number = double.Parse(text, NumberStyles.Float, CultureInfo.InvariantCulture);
			}
			if (_at < _source.Length && _source[_at] == 'n')
				_at++;
			token.Text = _source.Substring(start, _at - start);
		}

		private void ReadString(Token token, char quote)
		{
			int start = _at;
			_at++;
			StringBuilder value = new StringBuilder();
			while (_at < _source.Length && _source[_at] != quote)
			{
				char c = _source[_at];
				if (c == '\\')
				{
					_at++;
					value.Append(ReadEscape());
					continue;
				}
				if (c == '\n')
					throw new SyntaxError("unterminated string", _line);
				value.Append(c);
				_at++;
			}
			if (_at >= _source.Length)
				throw new SyntaxError("unterminated string", _line);
			_at++;
			token.Kind = TokenKind.String;
			token.Value = value.ToString();
			token.Text = _source.Substring(start, _at - start);
		}

		// After the backslash: the escape's character(s), with line continuations producing nothing
		private string ReadEscape()
		{
			if (_at >= _source.Length)
				return "";
			char c = _source[_at++];
			switch (c)
			{
				case 'n':
					return "\n";
				case 't':
					return "\t";
				case 'r':
					return "\r";
				case 'b':
					return "\b";
				case 'f':
					return "\f";
				case 'v':
					return "\v";
				case '0' when _at >= _source.Length || !char.IsDigit(_source[_at]):
					return "\0";
				case 'x':
				{
					string hex = _source.Substring(_at, Math.Min(2, _source.Length - _at));
					_at += hex.Length;
					return ((char)int.Parse(hex, NumberStyles.HexNumber, CultureInfo.InvariantCulture)).ToString();
				}
				case 'u':
					return ReadUnicodeEscape();
				case '\r':
					if (_at < _source.Length && _source[_at] == '\n')
						_at++;
					_line++;
					return "";
				case '\n':
					_line++;
					return "";
				default:
					return c.ToString();
			}
		}

		private string ReadUnicodeEscape()
		{
			if (_at < _source.Length && _source[_at] == '{')
			{
				int close = _source.IndexOf('}', _at);
				string hex = _source.Substring(_at + 1, close - _at - 1);
				_at = close + 1;
				return char.ConvertFromUtf32(int.Parse(hex, NumberStyles.HexNumber, CultureInfo.InvariantCulture));
			}
			string four = _source.Substring(_at, Math.Min(4, _source.Length - _at));
			_at += four.Length;
			return ((char)int.Parse(four, NumberStyles.HexNumber, CultureInfo.InvariantCulture)).ToString();
		}

		// Template literals: cooked string pieces and the raw source of each ${...} for the parser to parse later
		private void ReadTemplate(Token token)
		{
			int start = _at;
			_at++;
			token.Kind = TokenKind.Template;
			token.TemplateStrings = new List<string>();
			token.TemplateExpressions = new List<string>();
			StringBuilder piece = new StringBuilder();
			while (true)
			{
				if (_at >= _source.Length)
					throw new SyntaxError("unterminated template", _line);
				char c = _source[_at];
				if (c == '`')
				{
					_at++;
					break;
				}
				if (c == '\\')
				{
					_at++;
					piece.Append(ReadEscape());
					continue;
				}
				if (c == '$' && _at + 1 < _source.Length && _source[_at + 1] == '{')
				{
					token.TemplateStrings.Add(piece.ToString());
					piece.Clear();
					_at += 2;
					int depth = 1;
					int expressionStart = _at;
					while (_at < _source.Length && depth > 0)
					{
						char d = _source[_at];
						if (d == '{')
							depth++;
						else if (d == '}')
							depth--;
						else if (d == '\'' || d == '"' || d == '`')
							SkipNested(d);
						if (depth > 0)
							_at++;
					}
					token.TemplateExpressions.Add(_source.Substring(expressionStart, _at - expressionStart));
					_at++;
					continue;
				}
				if (c == '\n')
					_line++;
				piece.Append(c);
				_at++;
			}
			token.TemplateStrings.Add(piece.ToString());
			token.Text = _source.Substring(start, _at - start);
		}

		// Inside a template expression, a nested string or template is skipped whole so its braces do not count
		private void SkipNested(char quote)
		{
			_at++;
			while (_at < _source.Length && _source[_at] != quote)
			{
				if (_source[_at] == '\\')
					_at++;
				else if (quote == '`' && _source[_at] == '$' && _at + 1 < _source.Length && _source[_at + 1] == '{')
				{
					int depth = 1;
					_at += 2;
					while (_at < _source.Length && depth > 0)
					{
						if (_source[_at] == '{')
							depth++;
						else if (_source[_at] == '}')
							depth--;
						_at++;
					}
					continue;
				}
				_at++;
			}
		}

		private void ReadRegExp(Token token)
		{
			int start = _at;
			_at++;
			bool inClass = false;
			while (_at < _source.Length)
			{
				char c = _source[_at];
				if (c == '\\')
				{
					_at += 2;
					continue;
				}
				if (c == '[')
					inClass = true;
				else if (c == ']')
					inClass = false;
				else if (c == '/' && !inClass)
					break;
				else if (c == '\n')
					throw new SyntaxError("unterminated regular expression", _line);
				_at++;
			}
			if (_at >= _source.Length)
				throw new SyntaxError("unterminated regular expression", _line);
			string pattern = _source.Substring(start + 1, _at - start - 1);
			_at++;
			int flagsStart = _at;
			while (_at < _source.Length && char.IsLetter(_source[_at]))
				_at++;
			token.Kind = TokenKind.RegExp;
			token.Value = pattern;
			token.Text = _source.Substring(flagsStart, _at - flagsStart);
		}

		private void ReadPunctuator(Token token)
		{
			foreach (string punctuator in Punctuators)
			{
				if (string.CompareOrdinal(_source, _at, punctuator, 0, punctuator.Length) == 0)
				{
					// "?." followed by a digit is a conditional with a decimal, not optional chaining
					if (punctuator == "?." && _at + 2 < _source.Length && char.IsDigit(_source[_at + 2]))
						continue;
					token.Kind = TokenKind.Punctuator;
					token.Text = punctuator;
					_at += punctuator.Length;
					return;
				}
			}
			throw new SyntaxError($"unexpected character '{_source[_at]}'", _line);
		}
	}
}
