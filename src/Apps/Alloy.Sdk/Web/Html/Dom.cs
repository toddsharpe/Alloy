using System;
using System.Collections.Generic;
using System.Text;

namespace Alloy.Sdk.Web.Html
{
	public enum NodeType
	{
		Document,
		Element,
		Text,
		Comment,
	}

	// A node in the tree the parser builds; children are owned, and each knows its parent
	public abstract class Node
	{
		public Node Parent { get; internal set; }

		public readonly List<Node> Children = new List<Node>();

		public abstract NodeType Type { get; }

		public Element ParentElement => Parent as Element;

		public Node FirstChild => Children.Count > 0 ? Children[0] : null;

		public Node LastChild => Children.Count > 0 ? Children[Children.Count - 1] : null;

		public void AppendChild(Node child)
		{
			child.Parent?.Children.Remove(child);
			child.Parent = this;
			Children.Add(child);
		}

		public void InsertBefore(Node child, Node reference)
		{
			int index = reference == null ? -1 : Children.IndexOf(reference);
			child.Parent?.Children.Remove(child);
			child.Parent = this;
			if (index < 0)
				Children.Add(child);
			else
				Children.Insert(index, child);
		}

		public void RemoveChild(Node child)
		{
			if (Children.Remove(child))
				child.Parent = null;
		}

		// Every node below this one, depth first, in document order
		public IEnumerable<Node> Descendants()
		{
			Stack<Node> pending = new Stack<Node>();
			for (int i = Children.Count - 1; i >= 0; i--)
				pending.Push(Children[i]);
			while (pending.Count > 0)
			{
				Node node = pending.Pop();
				yield return node;
				for (int i = node.Children.Count - 1; i >= 0; i--)
					pending.Push(node.Children[i]);
			}
		}

		public IEnumerable<Element> DescendantElements()
		{
			foreach (Node node in Descendants())
			{
				if (node is Element element)
					yield return element;
			}
		}

		// Every element below, following shadow roots as well, for the stylesheets and scripts inside them
		public IEnumerable<Element> DescendantElementsWithShadow()
		{
			foreach (Node node in Descendants())
			{
				if (node is not Element element)
					continue;
				yield return element;
				if (element.ShadowRoot == null)
					continue;
				yield return element.ShadowRoot;
				foreach (Element inner in element.ShadowRoot.DescendantElementsWithShadow())
					yield return inner;
			}
		}

		// The text of every text node below, in order, with nothing between them
		public string TextContent
		{
			get
			{
				StringBuilder text = new StringBuilder();
				AppendText(text);
				return text.ToString();
			}
		}

		internal virtual void AppendText(StringBuilder text)
		{
			foreach (Node child in Children)
				child.AppendText(text);
		}

		public abstract void Serialize(StringBuilder html);

		public string OuterHtml
		{
			get
			{
				StringBuilder html = new StringBuilder();
				Serialize(html);
				return html.ToString();
			}
		}

		public string InnerHtml
		{
			get
			{
				StringBuilder html = new StringBuilder();
				foreach (Node child in Children)
					child.Serialize(html);
				return html.ToString();
			}
		}
	}

	public sealed class Document : Node
	{
		public override NodeType Type => NodeType.Document;

		public bool QuirksMode;

		public Element DocumentElement => FindChild(this, "html");

		public Element Head => FindChild(DocumentElement, "head");

		public Element Body => FindChild(DocumentElement, "body");

		// The first title element anywhere, as browsers read it; a late one in the body still counts
		public string Title
		{
			get
			{
				foreach (Element element in DescendantElements())
				{
					if (element.TagName == "title")
						return HtmlText.CollapseWhitespace(element.TextContent).Trim();
				}
				return "";
			}
		}

		private static Element FindChild(Node parent, string tag)
		{
			if (parent == null)
				return null;
			foreach (Node child in parent.Children)
			{
				if (child is Element element && element.TagName == tag)
					return element;
			}
			return null;
		}

		public override void Serialize(StringBuilder html)
		{
			foreach (Node child in Children)
				child.Serialize(html);
		}
	}

	public sealed class Element : Node
	{
		public const string ShadowRootTag = "#shadow-root";

		public readonly string TagName;
		public readonly List<KeyValuePair<string, string>> Attributes = new List<KeyValuePair<string, string>>();
		// A shadow tree a script attached: its Parent is this element, it is not among Children, and layout renders it in place of them
		public Element ShadowRoot;

		public Element(string tagName)
		{
			TagName = tagName;
		}

		public override NodeType Type => NodeType.Element;

		public string GetAttribute(string name)
		{
			foreach (KeyValuePair<string, string> attribute in Attributes)
			{
				if (attribute.Key == name)
					return attribute.Value;
			}
			return null;
		}

		public bool HasAttribute(string name) => GetAttribute(name) != null;

		// The first attribute wins, as in browsers; a later duplicate is dropped
		public void SetAttribute(string name, string value)
		{
			for (int i = 0; i < Attributes.Count; i++)
			{
				if (Attributes[i].Key == name)
				{
					Attributes[i] = new KeyValuePair<string, string>(name, value);
					return;
				}
			}
			Attributes.Add(new KeyValuePair<string, string>(name, value));
		}

		public string Id => GetAttribute("id");

		public Element AttachShadow()
		{
			ShadowRoot = new Element(ShadowRootTag) { Parent = this };
			return ShadowRoot;
		}

		// The host whose shadow tree contains this element, or null in the light tree
		public Element ShadowHost
		{
			get
			{
				for (Node node = Parent; node != null; node = node.Parent)
				{
					if (node is Element element && element.TagName == ShadowRootTag)
						return element.Parent as Element;
				}
				return null;
			}
		}

		// The light children a slot of this host shows: those naming the slot, or the unnamed ones for the default slot
		public List<Node> AssignedTo(string slotName)
		{
			List<Node> assigned = new List<Node>();
			foreach (Node child in Children)
			{
				string wanted = child is Element element ? element.GetAttribute("slot") ?? "" : "";
				if (wanted == (slotName ?? ""))
					assigned.Add(child);
			}
			return assigned;
		}

		public string ClassName => GetAttribute("class") ?? "";

		private string _classSource;
		private string[] _classes = Array.Empty<string>();

		// The class names, split once per value of the attribute; selector matching asks for them constantly
		public string[] Classes
		{
			get
			{
				string current = GetAttribute("class");
				if (!ReferenceEquals(current, _classSource))
				{
					_classSource = current;
					_classes = current == null ? Array.Empty<string>() : current.Split(s_classSeparators, StringSplitOptions.RemoveEmptyEntries);
				}
				return _classes;
			}
		}

		private static readonly char[] s_classSeparators = { ' ', '\t', '\n', '\r', '\f' };

		public bool HasClass(string name)
		{
			foreach (string candidate in Classes)
			{
				if (candidate == name)
					return true;
			}
			return false;
		}

		public Element FirstElement(string tag)
		{
			foreach (Element element in DescendantElements())
			{
				if (element.TagName == tag)
					return element;
			}
			return null;
		}

		public IEnumerable<Element> ElementsByTagName(string tag)
		{
			foreach (Element element in DescendantElements())
			{
				if (element.TagName == tag)
					yield return element;
			}
		}

		public override void Serialize(StringBuilder html)
		{
			html.Append('<').Append(TagName);
			foreach (KeyValuePair<string, string> attribute in Attributes)
			{
				html.Append(' ').Append(attribute.Key);
				if (attribute.Value.Length > 0)
				{
					html.Append("=\"");
					HtmlText.EscapeAttribute(attribute.Value, html);
					html.Append('"');
				}
			}
			html.Append('>');
			if (HtmlTags.IsVoid(TagName))
				return;
			bool raw = HtmlTags.IsRawText(TagName);
			foreach (Node child in Children)
			{
				if (raw && child is TextNode text)
					html.Append(text.Data);
				else
					child.Serialize(html);
			}
			html.Append("</").Append(TagName).Append('>');
		}
	}

	public sealed class TextNode : Node
	{
		public string Data;

		public TextNode(string data)
		{
			Data = data;
		}

		public override NodeType Type => NodeType.Text;

		internal override void AppendText(StringBuilder text) => text.Append(Data);

		public override void Serialize(StringBuilder html) => HtmlText.EscapeText(Data, html);
	}

	public sealed class Comment : Node
	{
		public readonly string Data;

		public Comment(string data)
		{
			Data = data;
		}

		public override NodeType Type => NodeType.Comment;

		public override void Serialize(StringBuilder html) => html.Append("<!--").Append(Data).Append("-->");
	}

	// Tag classes the parser and serializer need to know about
	public static class HtmlTags
	{
		private static readonly HashSet<string> s_void = new HashSet<string>(StringComparer.Ordinal)
		{
			"area", "base", "br", "col", "embed", "hr", "img", "input", "link", "meta", "param", "source", "track", "wbr",
		};

		private static readonly HashSet<string> s_rawText = new HashSet<string>(StringComparer.Ordinal)
		{
			"script", "style", "xmp", "iframe", "noembed", "noframes", "plaintext",
		};

		private static readonly HashSet<string> s_escapableRawText = new HashSet<string>(StringComparer.Ordinal) { "textarea", "title" };

		// Elements whose start tag closes an open paragraph (the "closes a p" set in the body insertion mode)
		private static readonly HashSet<string> s_closesParagraph = new HashSet<string>(StringComparer.Ordinal)
		{
			"address", "article", "aside", "blockquote", "center", "details", "dialog", "dir", "div", "dl", "fieldset", "figcaption", "figure", "footer",
			"header", "hgroup", "main", "menu", "nav", "ol", "p", "search", "section", "summary", "ul", "h1", "h2", "h3", "h4", "h5", "h6", "pre", "listing",
			"form", "table", "hr", "xmp", "plaintext",
		};

		private static readonly HashSet<string> s_headings = new HashSet<string>(StringComparer.Ordinal) { "h1", "h2", "h3", "h4", "h5", "h6" };

		private static readonly HashSet<string> s_formatting = new HashSet<string>(StringComparer.Ordinal)
		{
			"a", "b", "big", "code", "em", "font", "i", "nobr", "s", "small", "strike", "strong", "tt", "u",
		};

		// Elements that stay open for an end tag only; an end tag for something inside never pops past one of these
		private static readonly HashSet<string> s_scope = new HashSet<string>(StringComparer.Ordinal)
		{
			"applet", "caption", "html", "table", "td", "th", "marquee", "object", "template",
		};

		private static readonly HashSet<string> s_impliedEnd = new HashSet<string>(StringComparer.Ordinal)
		{
			"dd", "dt", "li", "optgroup", "option", "p", "rb", "rp", "rt", "rtc",
		};

		public static bool IsVoid(string tag) => s_void.Contains(tag);

		public static bool IsRawText(string tag) => s_rawText.Contains(tag);

		public static bool IsEscapableRawText(string tag) => s_escapableRawText.Contains(tag);

		public static bool ClosesParagraph(string tag) => s_closesParagraph.Contains(tag);

		public static bool IsHeading(string tag) => s_headings.Contains(tag);

		public static bool IsFormatting(string tag) => s_formatting.Contains(tag);

		public static bool IsScopeBoundary(string tag) => s_scope.Contains(tag);

		public static bool HasImpliedEnd(string tag) => s_impliedEnd.Contains(tag);
	}

	// Text helpers shared by the DOM and the layout code
	public static class HtmlText
	{
		public static bool IsWhitespace(char c) => c == ' ' || c == '\t' || c == '\n' || c == '\r' || c == '\f';

		// Runs of whitespace become one space, which is how inline text renders outside pre
		public static string CollapseWhitespace(string text)
		{
			StringBuilder result = new StringBuilder(text.Length);
			bool pendingSpace = false;
			foreach (char c in text)
			{
				if (IsWhitespace(c))
				{
					pendingSpace = true;
					continue;
				}
				if (pendingSpace && result.Length > 0)
					result.Append(' ');
				pendingSpace = false;
				result.Append(c);
			}
			return result.ToString();
		}

		public static void EscapeText(string text, StringBuilder html)
		{
			foreach (char c in text)
			{
				switch (c)
				{
					case '&':
						html.Append("&amp;");
						break;
					case '<':
						html.Append("&lt;");
						break;
					case '>':
						html.Append("&gt;");
						break;
					case ' ':
						html.Append("&nbsp;");
						break;
					default:
						html.Append(c);
						break;
				}
			}
		}

		public static void EscapeAttribute(string text, StringBuilder html)
		{
			foreach (char c in text)
			{
				switch (c)
				{
					case '&':
						html.Append("&amp;");
						break;
					case '"':
						html.Append("&quot;");
						break;
					case ' ':
						html.Append("&nbsp;");
						break;
					default:
						html.Append(c);
						break;
				}
			}
		}
	}
}
