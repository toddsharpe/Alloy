using System;
using System.Collections.Generic;
using System.Text;

namespace Alloy.Sdk.Web.Html
{
	// Tree construction after the WHATWG algorithm, cut down to what renders real pages: implied html, head and body, implied end tags, scoping, tables, raw text
	public sealed class HtmlParser
	{
		private enum InsertionMode
		{
			Initial,
			BeforeHtml,
			BeforeHead,
			InHead,
			AfterHead,
			InBody,
			AfterBody,
		}

		private static readonly HashSet<string> s_headTags = new HashSet<string>(StringComparer.Ordinal) { "base", "basefont", "bgsound", "link", "meta", "title", "style", "script", "noscript", "template" };
		private static readonly HashSet<string> s_tableSections = new HashSet<string>(StringComparer.Ordinal) { "thead", "tbody", "tfoot" };
		private static readonly HashSet<string> s_listItems = new HashSet<string>(StringComparer.Ordinal) { "ul", "ol", "menu", "dir" };

		private readonly HtmlTokenizer _tokenizer;
		private readonly Document _document = new Document();
		private readonly List<Element> _open = new List<Element>();
		private InsertionMode _mode = InsertionMode.Initial;
		private Element _head;
		private Element _form;
		private StringBuilder _pendingText;
		private Element _pendingTextParent;
		private bool _framesetOk = true;

		private HtmlParser(string html)
		{
			_tokenizer = new HtmlTokenizer(html);
		}

		public static Document Parse(string html)
		{
			HtmlParser parser = new HtmlParser(html);
			parser.Run();
			return parser._document;
		}

		private Element Current => _open.Count > 0 ? _open[_open.Count - 1] : null;

		private void Run()
		{
			while (true)
			{
				HtmlToken token = _tokenizer.Next();
				if (token.Type == HtmlTokenType.EndOfFile)
					break;
				Process(token);
			}
			FlushText();
			if (_document.DocumentElement == null)
				EnsureHtml();
			if (_head == null)
				EnsureHead();
			if (_document.Body == null)
				EnsureBody();
		}

		private void Process(HtmlToken token)
		{
			switch (_mode)
			{
				case InsertionMode.Initial:
					if (token.Type == HtmlTokenType.Doctype)
					{
						_document.QuirksMode = token.ForceQuirks;
						_mode = InsertionMode.BeforeHtml;
						return;
					}
					if (IsIgnorableWhitespace(token))
						return;
					if (token.Type == HtmlTokenType.Comment)
					{
						_document.AppendChild(new Comment(token.Data));
						return;
					}
					_document.QuirksMode = true;
					_mode = InsertionMode.BeforeHtml;
					Process(token);
					return;

				case InsertionMode.BeforeHtml:
					if (token.Type == HtmlTokenType.Comment)
					{
						_document.AppendChild(new Comment(token.Data));
						return;
					}
					if (IsIgnorableWhitespace(token) || token.Type == HtmlTokenType.Doctype)
						return;
					if (token.Type == HtmlTokenType.StartTag && token.Name == "html")
					{
						Element html = CreateElement(token);
						_document.AppendChild(html);
						_open.Add(html);
						_mode = InsertionMode.BeforeHead;
						return;
					}
					EnsureHtml();
					Process(token);
					return;

				case InsertionMode.BeforeHead:
					if (IsIgnorableWhitespace(token) || token.Type == HtmlTokenType.Doctype)
						return;
					if (token.Type == HtmlTokenType.Comment)
					{
						Insert(new Comment(token.Data));
						return;
					}
					if (token.Type == HtmlTokenType.StartTag && token.Name == "html")
					{
						MergeAttributes(_document.DocumentElement, token);
						return;
					}
					if (token.Type == HtmlTokenType.StartTag && token.Name == "head")
					{
						_head = InsertElement(token);
						_mode = InsertionMode.InHead;
						return;
					}
					EnsureHead();
					Process(token);
					return;

				case InsertionMode.InHead:
					InHead(token);
					return;

				case InsertionMode.AfterHead:
					if (IsIgnorableWhitespace(token))
					{
						InsertText(token.Data);
						return;
					}
					if (token.Type == HtmlTokenType.Comment)
					{
						Insert(new Comment(token.Data));
						return;
					}
					if (token.Type == HtmlTokenType.StartTag && token.Name == "body")
					{
						InsertElement(token);
						_framesetOk = false;
						_mode = InsertionMode.InBody;
						return;
					}
					if (token.Type == HtmlTokenType.StartTag && s_headTags.Contains(token.Name))
					{
						// A late head element goes into the head anyway
						_open.Add(_head);
						InHead(token);
						_open.Remove(_head);
						return;
					}
					if (token.Type == HtmlTokenType.EndTag && token.Name != "body" && token.Name != "html" && token.Name != "br")
						return;
					EnsureBody();
					Process(token);
					return;

				case InsertionMode.InBody:
					InBody(token);
					return;

				case InsertionMode.AfterBody:
					if (token.Type == HtmlTokenType.Comment)
					{
						_document.DocumentElement.AppendChild(new Comment(token.Data));
						return;
					}
					if (IsIgnorableWhitespace(token) || (token.Type == HtmlTokenType.EndTag && token.Name == "html"))
						return;
					_mode = InsertionMode.InBody;
					InBody(token);
					return;
			}
		}

		private void InHead(HtmlToken token)
		{
			if (IsIgnorableWhitespace(token))
			{
				InsertText(token.Data);
				return;
			}
			if (token.Type == HtmlTokenType.Comment)
			{
				Insert(new Comment(token.Data));
				return;
			}
			if (token.Type == HtmlTokenType.Doctype)
				return;
			if (token.Type == HtmlTokenType.StartTag)
			{
				switch (token.Name)
				{
					case "html":
						MergeAttributes(_document.DocumentElement, token);
						return;
					case "base":
					case "basefont":
					case "bgsound":
					case "link":
					case "meta":
						InsertElement(token);
						PopCurrent();
						return;
					case "title":
						InsertRawText(token, HtmlTokenizer.Mode.EscapableRawText);
						return;
					case "style":
					case "noframes":
						InsertRawText(token, HtmlTokenizer.Mode.RawText);
						return;
					case "script":
						InsertRawText(token, HtmlTokenizer.Mode.ScriptData);
						return;
					case "noscript":
					case "template":
						InsertElement(token);
						return;
					case "head":
						return;
				}
			}
			if (token.Type == HtmlTokenType.EndTag)
			{
				if (token.Name == "head")
				{
					PopCurrent();
					_mode = InsertionMode.AfterHead;
					return;
				}
				if (token.Name == "noscript" || token.Name == "template")
				{
					if (Current?.TagName == token.Name)
						PopCurrent();
					return;
				}
				if (token.Name != "body" && token.Name != "html" && token.Name != "br")
					return;
			}
			if (Current?.TagName == "noscript" || Current?.TagName == "template")
			{
				// Content in noscript renders as if scripting were off, which it is here
				InBody(token);
				return;
			}
			PopCurrent();
			_mode = InsertionMode.AfterHead;
			Process(token);
		}

		private void InBody(HtmlToken token)
		{
			switch (token.Type)
			{
				case HtmlTokenType.Text:
					InsertText(token.Data);
					if (!IsWhitespaceOnly(token.Data))
						_framesetOk = false;
					return;
				case HtmlTokenType.Comment:
					Insert(new Comment(token.Data));
					return;
				case HtmlTokenType.Doctype:
					return;
				case HtmlTokenType.StartTag:
					StartTagInBody(token);
					return;
				case HtmlTokenType.EndTag:
					EndTagInBody(token);
					return;
			}
		}

		private void StartTagInBody(HtmlToken token)
		{
			string name = token.Name;
			switch (name)
			{
				case "html":
					MergeAttributes(_document.DocumentElement, token);
					return;
				case "body":
					MergeAttributes(_document.Body, token);
					return;
				case "base":
				case "basefont":
				case "bgsound":
				case "link":
				case "meta":
				case "title":
				case "style":
				case "script":
				case "noscript":
				case "template":
					InHead(token);
					return;
				case "frameset":
				case "frame":
					return;
			}

			if (HtmlTags.ClosesParagraph(name))
			{
				CloseParagraph();
				if (HtmlTags.IsHeading(name) && Current != null && HtmlTags.IsHeading(Current.TagName))
					PopCurrent();
				if (name == "form")
				{
					if (_form != null)
						return;
					_form = InsertElement(token);
					return;
				}
				if (name == "hr")
				{
					InsertElement(token);
					PopCurrent();
					_framesetOk = false;
					return;
				}
				if (name == "pre" || name == "listing")
				{
					InsertElement(token);
					_skipLeadingNewline = true;
					_framesetOk = false;
					return;
				}
				if (name == "xmp" || name == "plaintext")
				{
					InsertRawText(token, name == "xmp" ? HtmlTokenizer.Mode.RawText : HtmlTokenizer.Mode.PlainText);
					return;
				}
				InsertElement(token);
				return;
			}

			switch (name)
			{
				case "li":
					CloseListItem("li");
					CloseParagraph();
					InsertElement(token);
					return;
				case "dd":
				case "dt":
					CloseListItem("dd");
					CloseListItem("dt");
					CloseParagraph();
					InsertElement(token);
					return;
				case "a":
				{
					// An a inside an a closes the first, the common case the adoption agency handles
					Element openAnchor = FindInScope("a");
					if (openAnchor != null)
						PopTo(openAnchor);
					InsertElement(token);
					return;
				}
				case "nobr":
					if (FindInScope("nobr") != null)
						PopTo(FindInScope("nobr"));
					InsertElement(token);
					return;
				case "button":
					if (FindInScope("button") != null)
						PopTo(FindInScope("button"));
					InsertElement(token);
					_framesetOk = false;
					return;
				case "area":
				case "br":
				case "embed":
				case "img":
				case "keygen":
				case "wbr":
				case "input":
				case "param":
				case "source":
				case "track":
				case "col":
					if (name == "col" && !InTableScope("colgroup"))
					{
						Element colgroup = new Element("colgroup");
						Insert(colgroup);
						_open.Add(colgroup);
					}
					InsertElement(token);
					PopCurrent();
					if (name != "col")
						_framesetOk = false;
					return;
				case "textarea":
					_skipLeadingNewline = true;
					InsertRawText(token, HtmlTokenizer.Mode.EscapableRawText);
					return;
				case "iframe":
				case "noembed":
					InsertRawText(token, HtmlTokenizer.Mode.RawText);
					return;
				case "select":
					InsertElement(token);
					_framesetOk = false;
					return;
				case "option":
				case "optgroup":
					if (Current?.TagName == "option")
						PopCurrent();
					if (name == "optgroup" && Current?.TagName == "optgroup")
						PopCurrent();
					InsertElement(token);
					return;
				case "rb":
				case "rtc":
				case "rp":
				case "rt":
					GenerateImpliedEndTags(name == "rt" || name == "rp" ? "rtc" : null);
					InsertElement(token);
					return;
				case "caption":
				case "colgroup":
				case "thead":
				case "tbody":
				case "tfoot":
					CloseTableContext("table");
					InsertElement(token);
					return;
				case "tr":
					CloseTableContext("tbody");
					if (!InTableScope("tbody") && !InTableScope("thead") && !InTableScope("tfoot"))
					{
						Element tbody = new Element("tbody");
						Insert(tbody);
						_open.Add(tbody);
					}
					InsertElement(token);
					return;
				case "td":
				case "th":
					CloseTableContext("tr");
					if (!InTableScope("tr"))
					{
						if (!InTableScope("tbody") && !InTableScope("thead") && !InTableScope("tfoot"))
						{
							Element tbody = new Element("tbody");
							Insert(tbody);
							_open.Add(tbody);
						}
						Element tr = new Element("tr");
						Insert(tr);
						_open.Add(tr);
					}
					InsertElement(token);
					return;
				case "image":
					token.Name = "img";
					StartTagInBody(token);
					return;
				case "math":
				case "svg":
					InsertElement(token);
					if (token.SelfClosing)
						PopCurrent();
					return;
			}

			InsertElement(token);
			if (token.SelfClosing && HtmlTags.IsVoid(name))
				PopCurrent();
		}

		private void EndTagInBody(HtmlToken token)
		{
			string name = token.Name;
			switch (name)
			{
				case "body":
				case "html":
					if (_document.Body != null)
						_mode = InsertionMode.AfterBody;
					return;
				case "p":
					if (FindInScope("p") == null)
					{
						InsertElement(new HtmlToken { Type = HtmlTokenType.StartTag, Name = "p" });
					}
					CloseParagraph();
					return;
				case "br":
					StartTagInBody(new HtmlToken { Type = HtmlTokenType.StartTag, Name = "br" });
					return;
				case "form":
					_form = null;
					break;
				case "li":
				case "dd":
				case "dt":
				{
					Element item = FindInListScope(name);
					if (item == null)
						return;
					GenerateImpliedEndTags(name);
					PopTo(item);
					return;
				}
				case "h1":
				case "h2":
				case "h3":
				case "h4":
				case "h5":
				case "h6":
				{
					Element heading = FindHeadingInScope();
					if (heading == null)
						return;
					GenerateImpliedEndTags(null);
					PopTo(heading);
					return;
				}
				case "table":
				{
					Element table = FindInScope("table");
					if (table != null)
						PopTo(table);
					return;
				}
			}

			Element target = FindInScope(name);
			if (target == null)
				return;
			GenerateImpliedEndTags(name);
			PopTo(target);
		}

		// A start tag's content handed to the tokenizer raw; the next text token becomes the element's only child
		private void InsertRawText(HtmlToken token, HtmlTokenizer.Mode mode)
		{
			Element element = InsertElement(token);
			_tokenizer.SwitchTo(mode, token.Name);
			HtmlToken content = _tokenizer.Next();
			if (content.Type == HtmlTokenType.Text)
			{
				string data = content.Data;
				if (_skipLeadingNewline && data.Length > 0 && data[0] == '\n')
					data = data.Substring(1);
				if (data.Length > 0)
					element.AppendChild(new TextNode(data));
			}
			_skipLeadingNewline = false;
			if (Current == element)
				PopCurrent();
		}

		private bool _skipLeadingNewline;

		private Element CreateElement(HtmlToken token)
		{
			Element element = new Element(token.Name);
			if (token.Attributes != null)
			{
				foreach (KeyValuePair<string, string> attribute in token.Attributes)
					element.Attributes.Add(attribute);
			}
			return element;
		}

		private Element InsertElement(HtmlToken token)
		{
			Element element = CreateElement(token);
			Insert(element);
			_open.Add(element);
			return element;
		}

		private void Insert(Node node)
		{
			FlushText();
			(Current ?? (Node)_document).AppendChild(node);
		}

		// Adjacent text tokens merge into one text node; a leading newline after pre or textarea is dropped
		private void InsertText(string data)
		{
			if (_skipLeadingNewline)
			{
				_skipLeadingNewline = false;
				if (data.Length > 0 && data[0] == '\n')
					data = data.Substring(1);
				if (data.Length == 0)
					return;
			}
			Node parent = Current ?? (Node)_document;
			if (_pendingText != null && _pendingTextParent != parent)
				FlushText();
			_pendingText ??= new StringBuilder();
			_pendingTextParent = Current;
			_pendingText.Append(data);
		}

		private void FlushText()
		{
			if (_pendingText == null || _pendingText.Length == 0)
			{
				_pendingText = null;
				return;
			}
			Node parent = _pendingTextParent ?? (Node)_document;
			string text = _pendingText.ToString();
			_pendingText = null;
			if (parent == _document || parent == _document.DocumentElement)
			{
				if (IsWhitespaceOnly(text))
					return;
				parent = _document.Body ?? parent;
			}
			if (parent.LastChild is TextNode last)
				last.Data += text;
			else
				parent.AppendChild(new TextNode(text));
		}

		private void PopCurrent()
		{
			FlushText();
			if (_open.Count > 0)
				_open.RemoveAt(_open.Count - 1);
		}

		private void PopTo(Element element)
		{
			FlushText();
			int index = _open.IndexOf(element);
			if (index >= 0)
				_open.RemoveRange(index, _open.Count - index);
		}

		private void CloseParagraph()
		{
			Element p = FindInButtonScope("p");
			if (p == null)
				return;
			GenerateImpliedEndTags("p");
			PopTo(p);
		}

		// An open li (or dd/dt) between the top and the nearest list closes when another starts
		private void CloseListItem(string name)
		{
			for (int i = _open.Count - 1; i >= 0; i--)
			{
				Element element = _open[i];
				if (element.TagName == name)
				{
					GenerateImpliedEndTags(name);
					PopTo(element);
					return;
				}
				if (HtmlTags.IsScopeBoundary(element.TagName) || (name == "li" && s_listItems.Contains(element.TagName)) || ((name == "dd" || name == "dt") && element.TagName == "dl"))
					return;
				if (HtmlTags.ClosesParagraph(element.TagName) && element.TagName != "p" && element.TagName != "div" && element.TagName != "address")
					return;
			}
		}

		// Before a new row, cell or section, whatever of that level is open closes, down to the outermost one above the table
		private void CloseTableContext(string upTo)
		{
			string[] closes = upTo == "table" ? new[] { "caption", "colgroup", "thead", "tbody", "tfoot", "tr", "td", "th" }
				: upTo == "tbody" ? new[] { "tr", "td", "th" } : new[] { "td", "th" };
			Element outermost = null;
			for (int i = _open.Count - 1; i >= 0; i--)
			{
				Element element = _open[i];
				if (element.TagName == "table")
					break;
				if (Array.IndexOf(closes, element.TagName) >= 0)
					outermost = element;
			}
			if (outermost == null)
				return;
			GenerateImpliedEndTags(null);
			PopTo(outermost);
		}

		private void GenerateImpliedEndTags(string except)
		{
			while (Current != null && HtmlTags.HasImpliedEnd(Current.TagName) && Current.TagName != except)
				PopCurrent();
		}

		private Element FindInScope(string name)
		{
			for (int i = _open.Count - 1; i >= 0; i--)
			{
				Element element = _open[i];
				if (element.TagName == name)
					return element;
				if (HtmlTags.IsScopeBoundary(element.TagName))
					return null;
			}
			return null;
		}

		private Element FindInButtonScope(string name)
		{
			for (int i = _open.Count - 1; i >= 0; i--)
			{
				Element element = _open[i];
				if (element.TagName == name)
					return element;
				if (HtmlTags.IsScopeBoundary(element.TagName) || element.TagName == "button")
					return null;
			}
			return null;
		}

		private Element FindInListScope(string name)
		{
			for (int i = _open.Count - 1; i >= 0; i--)
			{
				Element element = _open[i];
				if (element.TagName == name)
					return element;
				if (HtmlTags.IsScopeBoundary(element.TagName) || s_listItems.Contains(element.TagName))
					return null;
			}
			return null;
		}

		private Element FindHeadingInScope()
		{
			for (int i = _open.Count - 1; i >= 0; i--)
			{
				Element element = _open[i];
				if (HtmlTags.IsHeading(element.TagName))
					return element;
				if (HtmlTags.IsScopeBoundary(element.TagName))
					return null;
			}
			return null;
		}

		private bool InTableScope(string name)
		{
			for (int i = _open.Count - 1; i >= 0; i--)
			{
				Element element = _open[i];
				if (element.TagName == name)
					return true;
				if (element.TagName == "table" || element.TagName == "html" || element.TagName == "template")
					return false;
			}
			return false;
		}

		private void EnsureHtml()
		{
			Element html = new Element("html");
			_document.AppendChild(html);
			_open.Add(html);
			_mode = InsertionMode.BeforeHead;
		}

		private void EnsureHead()
		{
			if (_document.DocumentElement == null)
				EnsureHtml();
			_head = new Element("head");
			_document.DocumentElement.InsertBefore(_head, _document.DocumentElement.FirstChild);
			_mode = InsertionMode.AfterHead;
		}

		private void EnsureBody()
		{
			if (_document.DocumentElement == null)
				EnsureHtml();
			if (_head == null)
				EnsureHead();
			if (_document.Body != null)
			{
				_mode = InsertionMode.InBody;
				return;
			}
			Element body = new Element("body");
			_document.DocumentElement.AppendChild(body);
			PopTo(_document.DocumentElement);
			_open.Clear();
			_open.Add(_document.DocumentElement);
			_open.Add(body);
			_mode = InsertionMode.InBody;
		}

		private static void MergeAttributes(Element element, HtmlToken token)
		{
			if (element == null || token.Attributes == null)
				return;
			foreach (KeyValuePair<string, string> attribute in token.Attributes)
			{
				if (!element.HasAttribute(attribute.Key))
					element.Attributes.Add(attribute);
			}
		}

		private static bool IsIgnorableWhitespace(HtmlToken token) => token.Type == HtmlTokenType.Text && IsWhitespaceOnly(token.Data);

		private static bool IsWhitespaceOnly(string text)
		{
			foreach (char c in text)
			{
				if (!HtmlText.IsWhitespace(c))
					return false;
			}
			return true;
		}
	}
}
