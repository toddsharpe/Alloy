using System;
using System.Collections.Generic;
using System.Text;

namespace Alloy.Sdk.Web.Html
{
	public enum HtmlTokenType
	{
		Doctype,
		StartTag,
		EndTag,
		Text,
		Comment,
		EndOfFile,
	}

	// One token from the tokenizer; tag names and attribute names come out lowercased, text and attribute values with references decoded
	public sealed class HtmlToken
	{
		public HtmlTokenType Type;
		public string Name = "";
		public string Data = "";
		public bool SelfClosing;
		public bool ForceQuirks;
		public List<KeyValuePair<string, string>> Attributes;

		public string GetAttribute(string name)
		{
			if (Attributes == null)
				return null;
			foreach (KeyValuePair<string, string> attribute in Attributes)
			{
				if (attribute.Key == name)
					return attribute.Value;
			}
			return null;
		}
	}

	// The WHATWG tokenizer's states that real pages exercise: data, tags and attributes, comments, doctype, and the raw text modes the tree builder switches on
	public sealed class HtmlTokenizer
	{
		public enum Mode
		{
			Data,
			RawText,
			EscapableRawText,
			ScriptData,
			PlainText,
		}

		private readonly string _input;
		private int _at;
		private Mode _mode;
		private string _rawEndTag;
		private readonly StringBuilder _text = new StringBuilder();

		public HtmlTokenizer(string input)
		{
			_input = input ?? "";
		}

		// The tree builder sets this after a start tag whose content is raw (script, style, title, textarea and the like)
		public void SwitchTo(Mode mode, string endTag)
		{
			_mode = mode;
			_rawEndTag = endTag;
		}

		private bool AtEnd => _at >= _input.Length;

		private char Peek(int offset = 0) => _at + offset < _input.Length ? _input[_at + offset] : '\0';

		public HtmlToken Next()
		{
			if (_mode != Mode.Data)
				return NextRaw();
			_text.Clear();
			while (!AtEnd)
			{
				char c = _input[_at];
				if (c == '<')
				{
					HtmlToken tag = TryTag();
					if (tag != null)
					{
						if (_text.Length > 0)
						{
							_at = _tagStart;
							return TextToken();
						}
						return tag;
					}
					_text.Append('<');
					_at++;
					continue;
				}
				if (c == '&')
				{
					int consumed = Entities.Decode(_input, _at, false, _text);
					if (consumed > 0)
					{
						_at += consumed;
						continue;
					}
				}
				if (c == '\r')
				{
					_at++;
					if (Peek() != '\n')
						_text.Append('\n');
					continue;
				}
				_text.Append(c == '\0' ? '�' : c);
				_at++;
			}
			if (_text.Length > 0)
				return TextToken();
			return new HtmlToken { Type = HtmlTokenType.EndOfFile };
		}

		private HtmlToken TextToken() => new HtmlToken { Type = HtmlTokenType.Text, Data = _text.ToString() };

		private int _tagStart;

		// A tag, comment or doctype starting at the '<', or null when the '<' is just text; on success the cursor is past it
		private HtmlToken TryTag()
		{
			_tagStart = _at;
			int save = _at;
			char next = Peek(1);
			HtmlToken token = null;
			if (next == '!')
				token = MarkupDeclaration();
			else if (next == '/')
				token = EndTag();
			else if (char.IsAsciiLetter(next))
				token = StartTag();
			else if (next == '?')
				token = BogusComment(2);
			if (token == null)
				_at = save;
			return token;
		}

		private HtmlToken StartTag()
		{
			_at++;
			HtmlToken token = new HtmlToken { Type = HtmlTokenType.StartTag, Name = ReadTagName() };
			ReadAttributes(token);
			return token;
		}

		private HtmlToken EndTag()
		{
			_at += 2;
			if (Peek() == '>')
			{
				_at++;
				return null;
			}
			if (!char.IsAsciiLetter(Peek()))
				return BogusComment(0);
			HtmlToken token = new HtmlToken { Type = HtmlTokenType.EndTag, Name = ReadTagName() };
			ReadAttributes(token);
			token.Attributes = null;
			token.SelfClosing = false;
			return token;
		}

		private string ReadTagName()
		{
			int start = _at;
			while (!AtEnd && !HtmlText.IsWhitespace(_input[_at]) && _input[_at] != '/' && _input[_at] != '>')
				_at++;
			return _input.Substring(start, _at - start).ToLowerInvariant();
		}

		// Attributes up to the closing '>', tolerating missing values, unquoted values and junk; a trailing '/' marks self-closing
		private void ReadAttributes(HtmlToken token)
		{
			while (true)
			{
				SkipWhitespace();
				if (AtEnd)
					return;
				char c = _input[_at];
				if (c == '>')
				{
					_at++;
					return;
				}
				if (c == '/')
				{
					_at++;
					if (Peek() == '>')
					{
						token.SelfClosing = true;
						_at++;
						return;
					}
					continue;
				}

				int nameStart = _at;
				while (!AtEnd && !HtmlText.IsWhitespace(_input[_at]) && _input[_at] != '/' && _input[_at] != '>' && (_input[_at] != '=' || _at == nameStart))
					_at++;
				string name = _input.Substring(nameStart, _at - nameStart).ToLowerInvariant();
				SkipWhitespace();
				string value = "";
				if (Peek() == '=')
				{
					_at++;
					SkipWhitespace();
					value = ReadAttributeValue();
				}
				if (name.Length == 0)
					continue;
				token.Attributes ??= new List<KeyValuePair<string, string>>();
				if (token.GetAttribute(name) == null)
					token.Attributes.Add(new KeyValuePair<string, string>(name, value));
			}
		}

		private string ReadAttributeValue()
		{
			char quote = Peek();
			int start;
			int end;
			if (quote == '"' || quote == '\'')
			{
				_at++;
				start = _at;
				int close = _input.IndexOf(quote, _at);
				end = close < 0 ? _input.Length : close;
				_at = close < 0 ? _input.Length : close + 1;
			}
			else
			{
				start = _at;
				while (!AtEnd && !HtmlText.IsWhitespace(_input[_at]) && _input[_at] != '>')
					_at++;
				end = _at;
			}
			string raw = _input.Substring(start, end - start);
			return Entities.DecodeAll(raw.IndexOf('\r') >= 0 ? raw.Replace("\r\n", "\n").Replace('\r', '\n') : raw, true);
		}

		private void SkipWhitespace()
		{
			while (!AtEnd && HtmlText.IsWhitespace(_input[_at]))
				_at++;
		}

		// <!-- comments -->, <!DOCTYPE ...>, and anything else after <! as a bogus comment
		private HtmlToken MarkupDeclaration()
		{
			if (string.CompareOrdinal(_input, _at + 2, "--", 0, 2) == 0)
			{
				int start = _at + 4;
				int end = _input.IndexOf("-->", start, StringComparison.Ordinal);
				if (end < 0)
				{
					int shortEnd = _input.IndexOf("--!>", start, StringComparison.Ordinal);
					end = shortEnd < 0 ? _input.Length : shortEnd;
					_at = shortEnd < 0 ? _input.Length : shortEnd + 4;
				}
				else
				{
					_at = end + 3;
				}
				return new HtmlToken { Type = HtmlTokenType.Comment, Data = _input.Substring(start, Math.Max(0, end - start)) };
			}
			if (_at + 9 <= _input.Length && string.Compare(_input, _at + 2, "DOCTYPE", 0, 7, StringComparison.OrdinalIgnoreCase) == 0)
			{
				int start = _at + 9;
				int end = _input.IndexOf('>', start);
				if (end < 0)
					end = _input.Length;
				string body = _input.Substring(start, end - start).Trim();
				_at = Math.Min(end + 1, _input.Length);
				int space = 0;
				while (space < body.Length && !HtmlText.IsWhitespace(body[space]))
					space++;
				string name = body.Substring(0, space).ToLowerInvariant();
				string rest = body.Substring(space).Trim();

				// Anything beyond a plain "html" (a public identifier, a system identifier, another name) means quirks, close enough to the spec's long list
				bool quirks = name != "html" || (rest.Length > 0 && !rest.Equals("SYSTEM \"about:legacy-compat\"", StringComparison.OrdinalIgnoreCase) && !rest.StartsWith("PUBLIC \"-//W3C//DTD HTML 4.01//", StringComparison.OrdinalIgnoreCase) && !rest.StartsWith("PUBLIC \"-//W3C//DTD XHTML 1.0 Strict//", StringComparison.OrdinalIgnoreCase) && !rest.StartsWith("PUBLIC \"-//W3C//DTD XHTML 1.1//", StringComparison.OrdinalIgnoreCase));
				return new HtmlToken { Type = HtmlTokenType.Doctype, Name = name, ForceQuirks = quirks };
			}
			return BogusComment(2);
		}

		private HtmlToken BogusComment(int skip)
		{
			int start = _at + skip;
			int end = _input.IndexOf('>', start);
			if (end < 0)
				end = _input.Length;
			_at = Math.Min(end + 1, _input.Length);
			return new HtmlToken { Type = HtmlTokenType.Comment, Data = _input.Substring(start, end - start) };
		}

		// Raw content up to the matching end tag: no tags or (except for escapable raw text) references inside
		private HtmlToken NextRaw()
		{
			if (AtEnd)
			{
				_mode = Mode.Data;
				return new HtmlToken { Type = HtmlTokenType.EndOfFile };
			}
			int end = _mode == Mode.PlainText ? _input.Length : FindRawEnd();
			string raw = _input.Substring(_at, end - _at);
			if (raw.IndexOf('\r') >= 0)
				raw = raw.Replace("\r\n", "\n").Replace('\r', '\n');
			if (_mode == Mode.EscapableRawText)
				raw = Entities.DecodeAll(raw, false);
			_at = end;
			if (end >= _input.Length)
			{
				_mode = Mode.Data;
				return raw.Length > 0 ? new HtmlToken { Type = HtmlTokenType.Text, Data = raw } : new HtmlToken { Type = HtmlTokenType.EndOfFile };
			}
			_mode = Mode.Data;
			if (raw.Length > 0)
				return new HtmlToken { Type = HtmlTokenType.Text, Data = raw };
			return Next();
		}

		// Where "</name" followed by whitespace, '/' or '>' starts; the end of the input when it never comes
		private int FindRawEnd()
		{
			int search = _at;
			while (true)
			{
				int candidate = _input.IndexOf("</", search, StringComparison.Ordinal);
				if (candidate < 0)
					return _input.Length;
				int after = candidate + 2 + _rawEndTag.Length;
				if (string.Compare(_input, candidate + 2, _rawEndTag, 0, _rawEndTag.Length, StringComparison.OrdinalIgnoreCase) == 0 &&
					(after >= _input.Length || HtmlText.IsWhitespace(_input[after]) || _input[after] == '/' || _input[after] == '>'))
					return candidate;
				search = candidate + 2;
			}
		}
	}
}
