using System;
using System.Collections.Generic;
using Alloy.Sdk.Web.Html;

namespace Alloy.Sdk.Web.Css
{
	public enum Combinator
	{
		None,
		Descendant,
		Child,
		NextSibling,
		SubsequentSibling,
	}

	public enum AttributeMatch
	{
		Exists,
		Equals,
		Includes,
		DashMatch,
		Prefix,
		Suffix,
		Substring,
	}

	// One piece of a compound selector: a type, class, id, attribute test or pseudo-class
	public sealed class SimpleSelector
	{
		public enum Kind
		{
			Type,
			Universal,
			Class,
			Id,
			Attribute,
			PseudoClass,
		}

		public Kind Type;
		public string Name;
		public string Value;
		public AttributeMatch Match;
		public bool CaseInsensitive;
		public int NthA;
		public int NthB;
		public Selector Negated;
	}

	// A compound selector (several simple ones on one element) joined to the one before it by a combinator
	public sealed class CompoundSelector
	{
		public readonly List<SimpleSelector> Parts = new List<SimpleSelector>();
		public Combinator Combinator;
	}

	// A complex selector: compounds from left to right, with a specificity for the cascade
	public sealed class Selector
	{
		public readonly List<CompoundSelector> Compounds = new List<CompoundSelector>();
		public string PseudoElement;

		// Ids, then classes/attributes/pseudo-classes, then types, packed so a larger value wins
		public int Specificity
		{
			get
			{
				int ids = 0;
				int classes = 0;
				int types = 0;
				foreach (CompoundSelector compound in Compounds)
				{
					foreach (SimpleSelector part in compound.Parts)
					{
						switch (part.Type)
						{
							case SimpleSelector.Kind.Id:
								ids++;
								break;
							case SimpleSelector.Kind.Class:
							case SimpleSelector.Kind.Attribute:
								classes++;
								break;
							case SimpleSelector.Kind.PseudoClass:
								if (part.Negated != null)
								{
									int inner = part.Negated.Specificity;
									ids += inner >> 20;
									classes += (inner >> 10) & 0x3FF;
									types += inner & 0x3FF;
								}
								else
									classes++;
								break;
							case SimpleSelector.Kind.Type:
								types++;
								break;
						}
					}
				}
				if (PseudoElement != null)
					types++;
				return Math.Min(ids, 1023) << 20 | Math.Min(classes, 1023) << 10 | Math.Min(types, 1023);
			}
		}

		public bool Matches(Element element) => Compounds.Count > 0 && MatchesFrom(element, Compounds.Count - 1);

		// Right to left: the last compound must match the element, each earlier one an element the combinator points at
		private bool MatchesFrom(Element element, int index)
		{
			CompoundSelector compound = Compounds[index];
			if (!MatchesCompound(compound, element))
				return false;
			if (index == 0)
				return true;
			switch (compound.Combinator)
			{
				case Combinator.Child:
					return element.ParentElement != null && MatchesFrom(element.ParentElement, index - 1);
				case Combinator.Descendant:
					for (Element ancestor = element.ParentElement; ancestor != null; ancestor = ancestor.ParentElement)
					{
						if (MatchesFrom(ancestor, index - 1))
							return true;
					}
					return false;
				case Combinator.NextSibling:
				{
					Element previous = PreviousElement(element);
					return previous != null && MatchesFrom(previous, index - 1);
				}
				case Combinator.SubsequentSibling:
					for (Element previous = PreviousElement(element); previous != null; previous = PreviousElement(previous))
					{
						if (MatchesFrom(previous, index - 1))
							return true;
					}
					return false;
			}
			return false;
		}

		private static bool MatchesCompound(CompoundSelector compound, Element element)
		{
			foreach (SimpleSelector part in compound.Parts)
			{
				if (!MatchesSimple(part, element))
					return false;
			}
			return true;
		}

		private static bool MatchesSimple(SimpleSelector part, Element element)
		{
			switch (part.Type)
			{
				case SimpleSelector.Kind.Universal:
					return true;
				case SimpleSelector.Kind.Type:
					return element.TagName == part.Name;
				case SimpleSelector.Kind.Id:
					return element.Id == part.Name;
				case SimpleSelector.Kind.Class:
					return element.HasClass(part.Name);
				case SimpleSelector.Kind.Attribute:
					return MatchesAttribute(part, element);
				case SimpleSelector.Kind.PseudoClass:
					return MatchesPseudo(part, element);
			}
			return false;
		}

		private static bool MatchesAttribute(SimpleSelector part, Element element)
		{
			string value = element.GetAttribute(part.Name);
			if (value == null)
				return false;
			StringComparison comparison = part.CaseInsensitive ? StringComparison.OrdinalIgnoreCase : StringComparison.Ordinal;
			switch (part.Match)
			{
				case AttributeMatch.Exists:
					return true;
				case AttributeMatch.Equals:
					return string.Equals(value, part.Value, comparison);
				case AttributeMatch.Includes:
					foreach (string word in value.Split(' ', StringSplitOptions.RemoveEmptyEntries))
					{
						if (string.Equals(word, part.Value, comparison))
							return true;
					}
					return false;
				case AttributeMatch.DashMatch:
					return string.Equals(value, part.Value, comparison) || value.StartsWith(part.Value + "-", comparison);
				case AttributeMatch.Prefix:
					return part.Value.Length > 0 && value.StartsWith(part.Value, comparison);
				case AttributeMatch.Suffix:
					return part.Value.Length > 0 && value.EndsWith(part.Value, comparison);
				case AttributeMatch.Substring:
					return part.Value.Length > 0 && value.Contains(part.Value, comparison);
			}
			return false;
		}

		// The pseudo-classes a static page can satisfy; hover, focus and the rest never match
		private static bool MatchesPseudo(SimpleSelector part, Element element)
		{
			switch (part.Name)
			{
				case "not":
					return part.Negated != null && !part.Negated.Matches(element);
				case "root":
					return element.ParentElement == null && element.TagName == "html";
				case "link":
				case "any-link":
					return (element.TagName == "a" || element.TagName == "area") && element.HasAttribute("href");
				case "empty":
					foreach (Node child in element.Children)
					{
						if (child is Element || (child is TextNode text && text.Data.Length > 0))
							return false;
					}
					return true;
				case "first-child":
					return PreviousElement(element) == null && element.ParentElement != null;
				case "last-child":
					return NextElement(element) == null && element.ParentElement != null;
				case "only-child":
					return element.ParentElement != null && PreviousElement(element) == null && NextElement(element) == null;
				case "first-of-type":
					return PreviousOfType(element) == null && element.ParentElement != null;
				case "last-of-type":
					return NextOfType(element) == null && element.ParentElement != null;
				case "nth-child":
					return MatchesNth(part, Index(element, false, false));
				case "nth-last-child":
					return MatchesNth(part, Index(element, true, false));
				case "nth-of-type":
					return MatchesNth(part, Index(element, false, true));
				case "nth-last-of-type":
					return MatchesNth(part, Index(element, true, true));
				case "checked":
					return element.HasAttribute("checked") || element.HasAttribute("selected");
				case "disabled":
					return element.HasAttribute("disabled");
				case "enabled":
					return (element.TagName == "input" || element.TagName == "button" || element.TagName == "select" || element.TagName == "textarea") && !element.HasAttribute("disabled");
				case "lang":
					for (Element at = element; at != null; at = at.ParentElement)
					{
						string lang = at.GetAttribute("lang");
						if (lang != null)
							return lang.Equals(part.Value, StringComparison.OrdinalIgnoreCase) || lang.StartsWith(part.Value + "-", StringComparison.OrdinalIgnoreCase);
					}
					return false;
				default:
					return false;
			}
		}

		private static bool MatchesNth(SimpleSelector part, int index)
		{
			if (index <= 0)
				return false;
			if (part.NthA == 0)
				return index == part.NthB;
			int n = (index - part.NthB) / part.NthA;
			return n >= 0 && part.NthA * n + part.NthB == index;
		}

		// The element's 1-based position among its element siblings, from the front or the back, counting every type or its own
		private static int Index(Element element, bool fromEnd, bool ofType)
		{
			Element parent = element.ParentElement;
			if (parent == null)
				return 0;
			int index = 0;
			int count = parent.Children.Count;
			for (int i = 0; i < count; i++)
			{
				Node child = parent.Children[fromEnd ? count - 1 - i : i];
				if (child is not Element sibling || (ofType && sibling.TagName != element.TagName))
					continue;
				index++;
				if (sibling == element)
					return index;
			}
			return 0;
		}

		internal static Element PreviousElement(Element element)
		{
			Element parent = element.ParentElement;
			if (parent == null)
				return null;
			int at = parent.Children.IndexOf(element);
			for (int i = at - 1; i >= 0; i--)
			{
				if (parent.Children[i] is Element sibling)
					return sibling;
			}
			return null;
		}

		internal static Element NextElement(Element element)
		{
			Element parent = element.ParentElement;
			if (parent == null)
				return null;
			int at = parent.Children.IndexOf(element);
			for (int i = at + 1; i < parent.Children.Count; i++)
			{
				if (parent.Children[i] is Element sibling)
					return sibling;
			}
			return null;
		}

		private static Element PreviousOfType(Element element)
		{
			for (Element previous = PreviousElement(element); previous != null; previous = PreviousElement(previous))
			{
				if (previous.TagName == element.TagName)
					return previous;
			}
			return null;
		}

		private static Element NextOfType(Element element)
		{
			for (Element next = NextElement(element); next != null; next = NextElement(next))
			{
				if (next.TagName == element.TagName)
					return next;
			}
			return null;
		}
	}

	// Turns a selector's tokens into Selector objects; a selector that uses anything unsupported is dropped with its rule, as browsers drop unknown selectors
	public static class SelectorParser
	{
		public static List<Selector> ParseList(List<CssToken> tokens)
		{
			List<Selector> selectors = new List<Selector>();
			int at = 0;
			while (at < tokens.Count)
			{
				Selector selector = ParseOne(tokens, ref at);
				if (selector == null)
					return null;
				selectors.Add(selector);
				if (at < tokens.Count && tokens[at].Type == CssTokenType.Comma)
					at++;
			}
			return selectors.Count == 0 ? null : selectors;
		}

		private static Selector ParseOne(List<CssToken> tokens, ref int at)
		{
			Selector selector = new Selector();
			Combinator pending = Combinator.None;
			SkipWhitespace(tokens, ref at);
			while (at < tokens.Count && tokens[at].Type != CssTokenType.Comma)
			{
				CompoundSelector compound = new CompoundSelector { Combinator = pending };
				if (!ParseCompound(tokens, ref at, compound, selector))
					return null;
				if (compound.Parts.Count == 0)
					return null;
				selector.Compounds.Add(compound);

				bool sawWhitespace = false;
				while (at < tokens.Count && tokens[at].Type == CssTokenType.Whitespace)
				{
					sawWhitespace = true;
					at++;
				}
				if (at >= tokens.Count || tokens[at].Type == CssTokenType.Comma)
					break;
				CssToken token = tokens[at];
				if (token.IsDelim('>'))
					pending = Combinator.Child;
				else if (token.IsDelim('+'))
					pending = Combinator.NextSibling;
				else if (token.IsDelim('~'))
					pending = Combinator.SubsequentSibling;
				else if (sawWhitespace)
				{
					pending = Combinator.Descendant;
					continue;
				}
				else
					return null;
				at++;
				SkipWhitespace(tokens, ref at);
			}
			return selector.Compounds.Count == 0 || selector.Compounds[0].Combinator != Combinator.None ? null : selector;
		}

		private static bool ParseCompound(List<CssToken> tokens, ref int at, CompoundSelector compound, Selector selector)
		{
			while (at < tokens.Count)
			{
				CssToken token = tokens[at];
				if (token.Type == CssTokenType.Ident)
				{
					if (compound.Parts.Count > 0)
						return false;
					compound.Parts.Add(new SimpleSelector { Type = SimpleSelector.Kind.Type, Name = token.Value.ToLowerInvariant() });
					at++;
				}
				else if (token.IsDelim('*'))
				{
					if (compound.Parts.Count > 0)
						return false;
					compound.Parts.Add(new SimpleSelector { Type = SimpleSelector.Kind.Universal });
					at++;
				}
				else if (token.Type == CssTokenType.Hash)
				{
					compound.Parts.Add(new SimpleSelector { Type = SimpleSelector.Kind.Id, Name = token.Value });
					at++;
				}
				else if (token.IsDelim('.'))
				{
					if (at + 1 >= tokens.Count || tokens[at + 1].Type != CssTokenType.Ident)
						return false;
					compound.Parts.Add(new SimpleSelector { Type = SimpleSelector.Kind.Class, Name = tokens[at + 1].Value });
					at += 2;
				}
				else if (token.Type == CssTokenType.OpenBracket)
				{
					SimpleSelector attribute = ParseAttribute(tokens, ref at);
					if (attribute == null)
						return false;
					compound.Parts.Add(attribute);
				}
				else if (token.Type == CssTokenType.Colon)
				{
					at++;
					if (at < tokens.Count && tokens[at].Type == CssTokenType.Colon)
					{
						at++;
						if (at >= tokens.Count || tokens[at].Type != CssTokenType.Ident)
							return false;
						selector.PseudoElement = tokens[at].Value.ToLowerInvariant();
						at++;
						continue;
					}
					if (at >= tokens.Count)
						return false;
					SimpleSelector pseudo = ParsePseudoClass(tokens, ref at, selector);
					if (pseudo == null)
						return false;
					compound.Parts.Add(pseudo);
				}
				else
				{
					return true;
				}
			}
			return true;
		}

		private static SimpleSelector ParseAttribute(List<CssToken> tokens, ref int at)
		{
			at++;
			SkipWhitespace(tokens, ref at);
			if (at >= tokens.Count || tokens[at].Type != CssTokenType.Ident)
				return null;
			SimpleSelector attribute = new SimpleSelector { Type = SimpleSelector.Kind.Attribute, Name = tokens[at].Value.ToLowerInvariant(), Match = AttributeMatch.Exists };
			at++;
			SkipWhitespace(tokens, ref at);
			if (at < tokens.Count && tokens[at].Type == CssTokenType.CloseBracket)
			{
				at++;
				return attribute;
			}
			if (at >= tokens.Count)
				return null;
			CssToken op = tokens[at];
			if (op.IsDelim('='))
				attribute.Match = AttributeMatch.Equals;
			else if (op.Type == CssTokenType.Delim && at + 1 < tokens.Count && tokens[at + 1].IsDelim('='))
			{
				switch (op.Value)
				{
					case "~":
						attribute.Match = AttributeMatch.Includes;
						break;
					case "|":
						attribute.Match = AttributeMatch.DashMatch;
						break;
					case "^":
						attribute.Match = AttributeMatch.Prefix;
						break;
					case "$":
						attribute.Match = AttributeMatch.Suffix;
						break;
					case "*":
						attribute.Match = AttributeMatch.Substring;
						break;
					default:
						return null;
				}
				at++;
			}
			else
				return null;
			at++;
			SkipWhitespace(tokens, ref at);
			if (at >= tokens.Count || (tokens[at].Type != CssTokenType.Ident && tokens[at].Type != CssTokenType.String))
				return null;
			attribute.Value = tokens[at].Value;
			at++;
			SkipWhitespace(tokens, ref at);
			if (at < tokens.Count && tokens[at].IsIdent("i"))
			{
				attribute.CaseInsensitive = true;
				at++;
				SkipWhitespace(tokens, ref at);
			}
			if (at >= tokens.Count || tokens[at].Type != CssTokenType.CloseBracket)
				return null;
			at++;
			return attribute;
		}

		private static SimpleSelector ParsePseudoClass(List<CssToken> tokens, ref int at, Selector selector)
		{
			CssToken token = tokens[at];
			if (token.Type == CssTokenType.Ident)
			{
				at++;
				string name = token.Value.ToLowerInvariant();

				// The old single-colon pseudo-elements
				if (name == "before" || name == "after" || name == "first-line" || name == "first-letter")
				{
					selector.PseudoElement = name;
					return new SimpleSelector { Type = SimpleSelector.Kind.Universal };
				}
				return new SimpleSelector { Type = SimpleSelector.Kind.PseudoClass, Name = name };
			}
			if (token.Type != CssTokenType.Function)
				return null;
			string function = token.Value.ToLowerInvariant();
			at++;
			int start = at;
			int depth = 1;
			while (at < tokens.Count)
			{
				if (tokens[at].Type == CssTokenType.Function || tokens[at].Type == CssTokenType.OpenParen)
					depth++;
				else if (tokens[at].Type == CssTokenType.CloseParen && --depth == 0)
					break;
				at++;
			}
			if (at >= tokens.Count)
				return null;
			List<CssToken> arguments = tokens.GetRange(start, at - start);
			at++;
			switch (function)
			{
				case "not":
				{
					List<Selector> negated = ParseList(arguments);
					if (negated == null || negated.Count != 1)
						return null;
					return new SimpleSelector { Type = SimpleSelector.Kind.PseudoClass, Name = "not", Negated = negated[0] };
				}
				case "nth-child":
				case "nth-last-child":
				case "nth-of-type":
				case "nth-last-of-type":
				{
					SimpleSelector nth = new SimpleSelector { Type = SimpleSelector.Kind.PseudoClass, Name = function };
					return ParseNth(arguments, nth) ? nth : null;
				}
				case "lang":
				{
					int i = 0;
					SkipWhitespace(arguments, ref i);
					if (i >= arguments.Count || (arguments[i].Type != CssTokenType.Ident && arguments[i].Type != CssTokenType.String))
						return null;
					return new SimpleSelector { Type = SimpleSelector.Kind.PseudoClass, Name = "lang", Value = arguments[i].Value };
				}
				default:
					return null;
			}
		}

		// an+b in its spellings: odd, even, 3, n, 2n+1, -n+3, +n
		private static bool ParseNth(List<CssToken> arguments, SimpleSelector nth)
		{
			string text = "";
			foreach (CssToken token in arguments)
				text += token.ToString();
			text = text.Replace(" ", "").ToLowerInvariant();
			if (text == "odd")
			{
				nth.NthA = 2;
				nth.NthB = 1;
				return true;
			}
			if (text == "even")
			{
				nth.NthA = 2;
				nth.NthB = 0;
				return true;
			}
			int n = text.IndexOf('n');
			if (n < 0)
			{
				if (!int.TryParse(text, out int b))
					return false;
				nth.NthA = 0;
				nth.NthB = b;
				return true;
			}
			string aText = text.Substring(0, n);
			string bText = text.Substring(n + 1);
			int a = aText == "" || aText == "+" ? 1 : aText == "-" ? -1 : int.TryParse(aText, out int parsedA) ? parsedA : int.MinValue;
			int bValue = bText == "" ? 0 : int.TryParse(bText, out int parsedB) ? parsedB : int.MinValue;
			if (a == int.MinValue || bValue == int.MinValue)
				return false;
			nth.NthA = a;
			nth.NthB = bValue;
			return true;
		}

		private static void SkipWhitespace(List<CssToken> tokens, ref int at)
		{
			while (at < tokens.Count && tokens[at].Type == CssTokenType.Whitespace)
				at++;
		}
	}
}
