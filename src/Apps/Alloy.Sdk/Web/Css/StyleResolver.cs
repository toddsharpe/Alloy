using System;
using System.Collections.Generic;
using System.Text;
using Alloy.Sdk.Web.Html;

namespace Alloy.Sdk.Web.Css
{
	// The cascade: the user-agent sheet, the page's sheets in order, then style attributes, by importance, specificity and order, into one ComputedStyle per element
	public sealed class StyleResolver
	{
		private struct MatchedDeclaration
		{
			public Declaration Declaration;
			public int Origin;
			public int Specificity;
			public int Order;
		}

		private struct IndexedSelector
		{
			public int Rule;
			public Selector Selector;
			public int Specificity;
			// The tags, ids and classes the element's ancestors must carry for this selector to match at all
			public ulong NeedLow;
			public ulong NeedHigh;
		}

		// Two bits per name in a 128-bit filter, as browsers do for ancestor matching
		private static void AddBits(ref ulong low, ref ulong high, char kind, string name)
		{
			int hash = name.GetHashCode() * 31 + kind;
			int a = hash & 127;
			int b = (hash >> 7) & 127;
			if (a < 64)
				low |= 1UL << a;
			else
				high |= 1UL << (a - 64);
			if (b < 64)
				low |= 1UL << b;
			else
				high |= 1UL << (b - 64);
		}

		private static void ElementBits(Element element, ref ulong low, ref ulong high)
		{
			AddBits(ref low, ref high, 't', element.TagName);
			string id = element.Id;
			if (id != null)
				AddBits(ref low, ref high, 'i', id);
			foreach (string name in element.Classes)
				AddBits(ref low, ref high, 'c', name);
		}

		// What a selector's compounds to the left of the rightmost one demand of the ancestors, following descendant and child combinators
		private static void SelectorNeeds(Selector selector, out ulong low, out ulong high)
		{
			low = 0;
			high = 0;
			for (int i = selector.Compounds.Count - 2; i >= 0; i--)
			{
				Combinator combinator = selector.Compounds[i + 1].Combinator;
				if (combinator != Combinator.Descendant && combinator != Combinator.Child)
					continue;
				foreach (SimpleSelector part in selector.Compounds[i].Parts)
				{
					switch (part.Type)
					{
						case SimpleSelector.Kind.Type:
							AddBits(ref low, ref high, 't', part.Name);
							break;
						case SimpleSelector.Kind.Id:
							AddBits(ref low, ref high, 'i', part.Name);
							break;
						case SimpleSelector.Kind.Class:
							AddBits(ref low, ref high, 'c', part.Name);
							break;
					}
				}
			}
		}

		// Selectors bucketed by the rightmost compound's id, class or type, so an element only tests the rules that could match it
		private sealed class RuleIndex
		{
			public readonly List<StyleRule> Rules;
			private readonly Dictionary<string, List<IndexedSelector>> _byId = new Dictionary<string, List<IndexedSelector>>(StringComparer.Ordinal);
			private readonly Dictionary<string, List<IndexedSelector>> _byClass = new Dictionary<string, List<IndexedSelector>>(StringComparer.Ordinal);
			private readonly Dictionary<string, List<IndexedSelector>> _byTag = new Dictionary<string, List<IndexedSelector>>(StringComparer.Ordinal);
			private readonly Dictionary<string, List<IndexedSelector>> _byAttribute = new Dictionary<string, List<IndexedSelector>>(StringComparer.Ordinal);
			private readonly List<IndexedSelector> _universal = new List<IndexedSelector>();

			public List<IndexedSelector> Universal => _universal;

			public RuleIndex(List<StyleRule> rules)
			{
				Rules = rules;
				for (int r = 0; r < rules.Count; r++)
				{
					foreach (Selector selector in rules[r].Selectors)
					{
						if (selector.PseudoElement != null || selector.Compounds.Count == 0)
							continue;
						IndexedSelector entry = new IndexedSelector { Rule = r, Selector = selector, Specificity = selector.Specificity };
						SelectorNeeds(selector, out entry.NeedLow, out entry.NeedHigh);
						_keys.Clear();
						if (!KeysOf(selector.Compounds[selector.Compounds.Count - 1], _keys))
							continue;
						if (_keys.Count == 0)
							_universal.Add(entry);
						foreach (KeyValuePair<char, string> key in _keys)
						{
							switch (key.Key)
							{
								case 'i':
									Add(_byId, key.Value, entry);
									break;
								case 'c':
									Add(_byClass, key.Value, entry);
									break;
								case 't':
									Add(_byTag, key.Value, entry);
									break;
								case 'a':
									Add(_byAttribute, key.Value, entry);
									break;
								default:
									_universal.Add(entry);
									break;
							}
						}
					}
				}
			}

			private readonly List<KeyValuePair<char, string>> _keys = new List<KeyValuePair<char, string>>();

			// The bucket keys a compound can match under; several when it is :is() or :where() of alternatives, none when only the universal bucket will do; false when it can never match
			private static bool KeysOf(CompoundSelector compound, List<KeyValuePair<char, string>> keys)
			{
				string id = null;
				string cls = null;
				string tag = null;
				string attribute = null;
				List<Selector> alternatives = null;
				foreach (SimpleSelector part in compound.Parts)
				{
					if (part.Type == SimpleSelector.Kind.PseudoClass && part.Inner == null)
					{
						if (part.Name == "root")
							tag ??= "html";
						else if (!Selector.SupportsPseudoClass(part.Name))
							return false;
					}
					if (part.Type == SimpleSelector.Kind.PseudoClass && part.Inner != null && part.Name != "not" && alternatives == null)
						alternatives = part.Inner;
					if (part.Type == SimpleSelector.Kind.Id && id == null)
						id = part.Name;
					else if (part.Type == SimpleSelector.Kind.Class && cls == null)
						cls = part.Name;
					else if (part.Type == SimpleSelector.Kind.Type && tag == null)
						tag = part.Name;
					else if (part.Type == SimpleSelector.Kind.Attribute && attribute == null)
						attribute = part.Name;
				}
				if (id != null)
					keys.Add(new KeyValuePair<char, string>('i', id));
				else if (cls != null)
					keys.Add(new KeyValuePair<char, string>('c', cls));
				else if (tag != null)
					keys.Add(new KeyValuePair<char, string>('t', tag));
				else if (attribute != null)
					keys.Add(new KeyValuePair<char, string>('a', attribute));
				else if (alternatives != null)
				{
					// :is(a, b) matches when any alternative does, so it lives in every alternative's bucket
					foreach (Selector alternative in alternatives)
					{
						int before = keys.Count;
						if (alternative.Compounds.Count == 0 || !KeysOf(alternative.Compounds[alternative.Compounds.Count - 1], keys))
							continue;
						if (keys.Count == before)
							keys.Add(new KeyValuePair<char, string>('*', ""));
					}
				}
				return true;
			}

			private static void Add(Dictionary<string, List<IndexedSelector>> buckets, string key, IndexedSelector entry)
			{
				if (!buckets.TryGetValue(key, out List<IndexedSelector> list))
					buckets[key] = list = new List<IndexedSelector>();
				list.Add(entry);
			}

			public void Candidates(Element element, List<IndexedSelector> into)
			{
				into.AddRange(_universal);
				if (_byTag.TryGetValue(element.TagName, out List<IndexedSelector> byTag))
					into.AddRange(byTag);
				string id = element.Id;
				if (id != null && _byId.TryGetValue(id, out List<IndexedSelector> byId))
					into.AddRange(byId);
				if (_byAttribute.Count > 0)
				{
					foreach (KeyValuePair<string, string> attribute in element.Attributes)
					{
						if (_byAttribute.TryGetValue(attribute.Key, out List<IndexedSelector> byAttribute))
							into.AddRange(byAttribute);
					}
				}
				if (_byClass.Count == 0)
					return;
				foreach (string name in element.Classes)
				{
					if (_byClass.TryGetValue(name, out List<IndexedSelector> byClass))
						into.AddRange(byClass);
				}
			}
		}

		private readonly RuleIndex _userAgent;
		private readonly List<StyleRule> _author = new List<StyleRule>();
		private RuleIndex _authorIndex;
		private readonly Dictionary<Element, ComputedStyle> _styles = new Dictionary<Element, ComputedStyle>();
		private readonly List<MatchedDeclaration> _matched = new List<MatchedDeclaration>();
		private readonly List<IndexedSelector> _candidates = new List<IndexedSelector>();
		private static readonly Comparison<IndexedSelector> s_byRule = (a, b) => a.Rule.CompareTo(b.Rule);

		public StyleResolver(MediaEnvironment media)
		{
			_userAgent = new RuleIndex(CssParser.Parse(UserAgentStylesheet.Css, media).Rules);
		}

		public void AddStylesheet(Stylesheet sheet)
		{
			_author.AddRange(sheet.Rules);
			_authorIndex = null;
		}

		public float RootFontSize { get; private set; } = ComputedStyle.DefaultFontSize;

		// What sits in the catch-all bucket of the author index, for tuning: the selectors every element must test
		public string DescribeUniversal()
		{
			RuleIndex index = _authorIndex ??= new RuleIndex(_author);
			Dictionary<string, int> counts = new Dictionary<string, int>(StringComparer.Ordinal);
			foreach (IndexedSelector entry in index.Universal)
			{
				System.Text.StringBuilder text = new System.Text.StringBuilder();
				foreach (SimpleSelector part in entry.Selector.Compounds[entry.Selector.Compounds.Count - 1].Parts)
					text.Append(part.Type).Append(':').Append(part.Name).Append(part.Inner != null ? "(" + part.Inner.Count + ")" : "").Append(' ');
				string key = text.ToString();
				counts[key] = counts.TryGetValue(key, out int count) ? count + 1 : 1;
			}
			List<KeyValuePair<string, int>> sorted = new List<KeyValuePair<string, int>>(counts);
			sorted.Sort((a, b) => b.Value.CompareTo(a.Value));
			System.Text.StringBuilder result = new System.Text.StringBuilder();
			result.Append("universal: ").Append(index.Universal.Count).Append('\n');
			for (int i = 0; i < sorted.Count && i < 20; i++)
				result.Append("  x").Append(sorted[i].Value).Append(' ').Append(sorted[i].Key).Append('\n');
			return result.ToString();
		}

		// The style of an element, computing its ancestors' first since most properties inherit
		public ComputedStyle Resolve(Element element)
		{
			if (_styles.TryGetValue(element, out ComputedStyle cached))
				return cached;
			ComputedStyle parent = element.ParentElement == null ? null : Resolve(element.ParentElement);
			ComputedStyle style = Compute(element, parent);
			_styles[element] = style;
			if (element.TagName == "html")
				RootFontSize = style.FontSize;
			return style;
		}

		private ulong _ancestorLow;
		private ulong _ancestorHigh;

		private ComputedStyle Compute(Element element, ComputedStyle parent)
		{
			ComputedStyle style = ComputedStyle.Inherit(parent);
			_ancestorLow = parent?.AncestorBitsLow ?? 0;
			_ancestorHigh = parent?.AncestorBitsHigh ?? 0;
			style.AncestorBitsLow = _ancestorLow;
			style.AncestorBitsHigh = _ancestorHigh;
			ElementBits(element, ref style.AncestorBitsLow, ref style.AncestorBitsHigh);
			_matched.Clear();
			Collect(element, _userAgent, 0);
			Collect(element, _authorIndex ??= new RuleIndex(_author), 1);
			string inline = element.GetAttribute("style");
			if (inline != null)
			{
				int order = _matched.Count;
				foreach (Declaration declaration in CssParser.ParseDeclarations(inline))
					_matched.Add(new MatchedDeclaration { Declaration = declaration, Origin = 2, Specificity = int.MaxValue, Order = order++ });
			}
			string hints = PresentationalHints(element);
			if (hints != null)
			{
				int order = -1000;
				foreach (Declaration declaration in CssParser.ParseDeclarations(hints))
					_matched.Add(new MatchedDeclaration { Declaration = declaration, Origin = 1, Specificity = 0, Order = order++ });
			}
			_matched.Sort(Compare);

			float parentFontSize = parent?.FontSize ?? ComputedStyle.DefaultFontSize;
			PropertyApplier applier = new PropertyApplier(style, parent, parentFontSize, RootFontSize);

			// Custom properties first so var() can see them, then font size since em lengths measure against it, then the rest
			foreach (MatchedDeclaration match in _matched)
			{
				if (match.Declaration.Property.StartsWith("--", StringComparison.Ordinal))
					applier.Apply(match.Declaration);
			}
			foreach (MatchedDeclaration match in _matched)
			{
				if (match.Declaration.Property == "font-size" || match.Declaration.Property == "font")
					applier.Apply(match.Declaration);
			}
			applier.FontSizeSettled();
			foreach (MatchedDeclaration match in _matched)
			{
				if (match.Declaration.Property != "font-size" && !match.Declaration.Property.StartsWith("--", StringComparison.Ordinal))
					applier.Apply(match.Declaration);
			}
			applier.Finish(element);
			return style;
		}

		// The candidates for an element in rule order; a rule matched by several of its selectors counts once, at the highest specificity
		private void Collect(Element element, RuleIndex index, int origin)
		{
			_candidates.Clear();
			index.Candidates(element, _candidates);
			if (_candidates.Count > 1)
				_candidates.Sort(s_byRule);
			int current = -1;
			int best = -1;
			foreach (IndexedSelector candidate in _candidates)
			{
				if (candidate.Rule != current)
				{
					Flush(index, current, best, origin);
					current = candidate.Rule;
					best = -1;
				}
				if ((candidate.NeedLow & _ancestorLow) != candidate.NeedLow || (candidate.NeedHigh & _ancestorHigh) != candidate.NeedHigh)
					continue;
				if (candidate.Selector.Matches(element))
					best = Math.Max(best, candidate.Specificity);
			}
			Flush(index, current, best, origin);
		}

		private void Flush(RuleIndex index, int rule, int best, int origin)
		{
			if (best < 0)
				return;
			foreach (Declaration declaration in index.Rules[rule].Declarations)
				_matched.Add(new MatchedDeclaration { Declaration = declaration, Origin = origin, Specificity = best, Order = _matched.Count });
		}

		// Later entries win: normal before important, user agent before author, lower specificity before higher, earlier before later
		private static int Compare(MatchedDeclaration a, MatchedDeclaration b)
		{
			if (a.Declaration.Important != b.Declaration.Important)
				return a.Declaration.Important ? 1 : -1;
			if (a.Declaration.Important && a.Origin != b.Origin)
				return a.Origin < b.Origin ? 1 : -1;
			if (a.Origin != b.Origin)
				return a.Origin.CompareTo(b.Origin);
			if (a.Specificity != b.Specificity)
				return a.Specificity.CompareTo(b.Specificity);
			return a.Order.CompareTo(b.Order);
		}

		// The old HTML attributes that still style things, as author declarations that any stylesheet rule beats: width, height, align, bgcolor, color, border, cellpadding and the font tag
		private static string PresentationalHints(Element element)
		{
			StringBuilder css = null;
			void Add(string declaration) => (css ??= new StringBuilder()).Append(declaration).Append(';');
			string tag = element.TagName;
			string width = element.GetAttribute("width");
			if (width != null && (tag == "img" || tag == "table" || tag == "td" || tag == "th" || tag == "iframe" || tag == "hr" || tag == "col" || tag == "svg"))
				Add("width: " + ParseHintLength(width));
			string height = element.GetAttribute("height");
			if (height != null && (tag == "img" || tag == "table" || tag == "td" || tag == "th" || tag == "iframe" || tag == "tr" || tag == "svg"))
				Add("height: " + ParseHintLength(height));
			string align = element.GetAttribute("align");
			if (align != null)
			{
				bool box = tag == "table" || tag == "img";
				switch (align.ToLowerInvariant())
				{
					case "center":
					case "middle":
						Add(box ? "margin-left: auto; margin-right: auto" : "text-align: center");
						break;
					case "right":
						Add(box ? "float: right" : "text-align: right");
						break;
					case "left":
						Add(box ? "float: left" : "text-align: left");
						break;
				}
			}
			string bgcolor = element.GetAttribute("bgcolor");
			if (bgcolor != null && TryHintColor(bgcolor, out Color background))
				Add("background-color: " + background);
			if (tag == "font")
			{
				string color = element.GetAttribute("color");
				if (color != null && TryHintColor(color, out Color fontColor))
					Add("color: " + fontColor);
				string size = element.GetAttribute("size");
				if (size != null && int.TryParse(size.TrimStart('+'), out int fontSize))
				{
					if (size.StartsWith("+") || size.StartsWith("-"))
						fontSize += 3;
					string[] sizes = { "10px", "13px", "16px", "18px", "24px", "32px", "48px" };
					Add("font-size: " + sizes[Math.Clamp(fontSize, 1, 7) - 1]);
				}
			}
			if (tag == "body")
			{
				string text = element.GetAttribute("text");
				if (text != null && TryHintColor(text, out Color textColor))
					Add("color: " + textColor);
			}
			if (tag == "table")
			{
				string border = element.GetAttribute("border");
				if (border != null && int.TryParse(border, out int borderWidth) && borderWidth > 0)
					Add("border: " + borderWidth + "px outset gray");
			}
			if (tag == "td" || tag == "th")
			{
				Element table = element.ParentElement;
				while (table != null && table.TagName != "table")
					table = table.ParentElement;
				string padding = table?.GetAttribute("cellpadding");
				if (padding != null && int.TryParse(padding, out int cellPadding))
					Add("padding: " + cellPadding + "px");
				string border = table?.GetAttribute("border");
				if (border != null && int.TryParse(border, out int borderWidth) && borderWidth > 0)
					Add("border: 1px inset gray");
			}
			return css?.ToString();
		}

		// clip: rect(0,0,0,0) and clip-path: inset(50%) are how pages keep text for screen readers only; an empty clip region paints nothing
		internal static bool ClipHidesEverything(List<CssToken> tokens)
		{
			if (tokens.Count == 0 || tokens[0].Type != CssTokenType.Function)
				return false;
			List<double> numbers = new List<double>();
			bool percent = false;
			for (int i = 1; i < tokens.Count && tokens[i].Type != CssTokenType.CloseParen; i++)
			{
				CssToken token = tokens[i];
				if (token.Type == CssTokenType.Number || token.Type == CssTokenType.Dimension || token.Type == CssTokenType.Percentage)
				{
					numbers.Add(token.Number);
					percent |= token.Type == CssTokenType.Percentage;
				}
				else if (token.Type == CssTokenType.Ident || token.Type == CssTokenType.Function)
					return false;
			}
			if (numbers.Count == 0)
				return false;
			switch (tokens[0].Value.ToLowerInvariant())
			{
				case "rect":
					// top right bottom left: nothing shows when the bottom is not below the top or the right edge not past the left
					return numbers.Count == 4 && (numbers[2] <= numbers[0] || numbers[1] <= numbers[3]);
				case "inset":
				{
					double top = numbers[0];
					double right = numbers.Count > 1 ? numbers[1] : top;
					double bottom = numbers.Count > 2 ? numbers[2] : top;
					double left = numbers.Count > 3 ? numbers[3] : right;
					return percent && (top + bottom >= 100 || left + right >= 100);
				}
			}
			return false;
		}

		private static Length ParseHintLength(string text)
		{
			text = text.Trim();
			if (text.EndsWith("%") && float.TryParse(text.AsSpan(0, text.Length - 1), System.Globalization.NumberStyles.Float, System.Globalization.CultureInfo.InvariantCulture, out float percent))
				return new Length(percent, LengthUnit.Percent);
			if (text.EndsWith("px"))
				text = text.Substring(0, text.Length - 2);
			return float.TryParse(text, System.Globalization.NumberStyles.Float, System.Globalization.CultureInfo.InvariantCulture, out float pixels) ? Length.Px(pixels) : Length.Auto;
		}

		private static bool TryHintColor(string text, out Color color)
		{
			text = text.Trim();
			if (text.StartsWith("#"))
				return CssValue.TryHex(text.Substring(1), out color);
			if (NamedColors.TryGet(text.ToLowerInvariant(), out color))
				return true;
			return CssValue.TryHex(text, out color);
		}
	}

	// Applies one declaration to a style: the property grammar subset layout understands, shorthands expanded, keywords inherit and initial honoured
	public sealed class PropertyApplier
	{
		private readonly ComputedStyle _style;
		private readonly ComputedStyle _parent;
		private readonly float _parentFontSize;
		private readonly float _rootFontSize;
		private float _fontSize;

		public PropertyApplier(ComputedStyle style, ComputedStyle parent, float parentFontSize, float rootFontSize)
		{
			_style = style;
			_parent = parent;
			_parentFontSize = parentFontSize;
			_rootFontSize = rootFontSize;
			_fontSize = style.FontSize;
		}

		public void FontSizeSettled() => _fontSize = _style.FontSize;

		public void Apply(Declaration declaration)
		{
			List<CssToken> value = declaration.Value;
			if (HasVar(value))
			{
				value = SubstituteVars(value, 0);
				if (value == null)
					return;
			}
			if (!declaration.Property.StartsWith("--", StringComparison.Ordinal) && CssMath.HasMath(value))
				value = CssMath.Collapse(value, _fontSize, _rootFontSize);
			if (declaration.Property.StartsWith("--", StringComparison.Ordinal))
			{
				if (_style.Custom == null || _style.Custom == _parent?.Custom)
					_style.Custom = _style.Custom == null ? new Dictionary<string, List<CssToken>>() : new Dictionary<string, List<CssToken>>(_style.Custom);
				_style.Custom[declaration.Property] = value;
				return;
			}
			List<CssToken> tokens = CssValue.WithoutWhitespace(value);
			if (tokens.Count == 0)
				return;
			if (tokens.Count == 1 && tokens[0].Type == CssTokenType.Ident)
			{
				string keyword = tokens[0].Value.ToLowerInvariant();
				if (keyword == "inherit")
				{
					InheritProperty(declaration.Property);
					return;
				}
				if (keyword == "initial" || keyword == "unset" || keyword == "revert")
				{
					ResetProperty(declaration.Property, keyword != "initial");
					return;
				}
			}
			switch (declaration.Property)
			{
				case "display":
					ApplyDisplay(tokens[0]);
					break;
				case "position":
					_style.Position = Keyword(tokens[0], "relative", Position.Relative, "absolute", Position.Absolute, "fixed", Position.Fixed, Position.Static);
					break;
				case "float":
					_style.Float = Keyword(tokens[0], "left", FloatSide.Left, "right", FloatSide.Right, "none", FloatSide.None, FloatSide.None);
					break;
				case "clear":
					_style.Clear = Keyword(tokens[0], "left", ClearSide.Left, "right", ClearSide.Right, "both", ClearSide.Both, ClearSide.None);
					break;
				case "overflow":
				case "overflow-x":
				case "overflow-y":
					_style.Overflow = Keyword(tokens[0], "hidden", Overflow.Hidden, "scroll", Overflow.Scroll, "auto", Overflow.Auto, Overflow.Visible);
					break;
				case "visibility":
					_style.Visible = !tokens[0].IsIdent("hidden") && !tokens[0].IsIdent("collapse");
					break;
				case "box-sizing":
					_style.BorderBox = tokens[0].IsIdent("border-box");
					break;
				case "clip":
				case "clip-path":
					if (StyleResolver.ClipHidesEverything(tokens))
						_style.Visible = false;
					break;
				case "margin":
					ApplyEdges(tokens, ref _style.Margin, true, true);
					break;
				case "margin-top":
					ApplyEdge(tokens[0], ref _style.Margin.Top, true, true);
					break;
				case "margin-right":
					ApplyEdge(tokens[0], ref _style.Margin.Right, true, true);
					break;
				case "margin-bottom":
					ApplyEdge(tokens[0], ref _style.Margin.Bottom, true, true);
					break;
				case "margin-left":
					ApplyEdge(tokens[0], ref _style.Margin.Left, true, true);
					break;
				case "margin-inline":
					ApplyEdge(tokens[0], ref _style.Margin.Left, true, true);
					ApplyEdge(tokens[tokens.Count > 1 ? 1 : 0], ref _style.Margin.Right, true, true);
					break;
				case "margin-block":
					ApplyEdge(tokens[0], ref _style.Margin.Top, true, true);
					ApplyEdge(tokens[tokens.Count > 1 ? 1 : 0], ref _style.Margin.Bottom, true, true);
					break;
				case "padding":
					ApplyEdges(tokens, ref _style.Padding, false, false);
					break;
				case "padding-top":
					ApplyEdge(tokens[0], ref _style.Padding.Top, false, false);
					break;
				case "padding-right":
					ApplyEdge(tokens[0], ref _style.Padding.Right, false, false);
					break;
				case "padding-bottom":
					ApplyEdge(tokens[0], ref _style.Padding.Bottom, false, false);
					break;
				case "padding-left":
					ApplyEdge(tokens[0], ref _style.Padding.Left, false, false);
					break;
				case "padding-inline":
					ApplyEdge(tokens[0], ref _style.Padding.Left, false, false);
					ApplyEdge(tokens[tokens.Count > 1 ? 1 : 0], ref _style.Padding.Right, false, false);
					break;
				case "padding-block":
					ApplyEdge(tokens[0], ref _style.Padding.Top, false, false);
					ApplyEdge(tokens[tokens.Count > 1 ? 1 : 0], ref _style.Padding.Bottom, false, false);
					break;
				case "width":
				case "inline-size":
					ApplySize(tokens[0], ref _style.Width, true);
					break;
				case "height":
				case "block-size":
					ApplySize(tokens[0], ref _style.Height, true);
					break;
				case "min-width":
				case "min-inline-size":
					ApplySize(tokens[0], ref _style.MinWidth, false);
					break;
				case "max-width":
				case "max-inline-size":
					ApplySize(tokens[0], ref _style.MaxWidth, true);
					break;
				case "min-height":
				case "min-block-size":
					ApplySize(tokens[0], ref _style.MinHeight, false);
					break;
				case "max-height":
				case "max-block-size":
					ApplySize(tokens[0], ref _style.MaxHeight, true);
					break;
				case "top":
					ApplySize(tokens[0], ref _style.Top, true);
					break;
				case "left":
					ApplySize(tokens[0], ref _style.Left, true);
					break;
				case "border":
					ApplyBorderShorthand(tokens, -1);
					break;
				case "border-top":
					ApplyBorderShorthand(tokens, 0);
					break;
				case "border-right":
					ApplyBorderShorthand(tokens, 1);
					break;
				case "border-bottom":
					ApplyBorderShorthand(tokens, 2);
					break;
				case "border-left":
					ApplyBorderShorthand(tokens, 3);
					break;
				case "border-width":
					ApplyBorderWidths(tokens);
					break;
				case "border-style":
					ApplyBorderStyles(tokens);
					break;
				case "border-color":
					ApplyBorderColors(tokens);
					break;
				case "border-top-width":
					_style.BorderTop.Width = BorderWidth(tokens[0]);
					break;
				case "border-right-width":
					_style.BorderRight.Width = BorderWidth(tokens[0]);
					break;
				case "border-bottom-width":
					_style.BorderBottom.Width = BorderWidth(tokens[0]);
					break;
				case "border-left-width":
					_style.BorderLeft.Width = BorderWidth(tokens[0]);
					break;
				case "border-top-style":
					_style.BorderTop.Style = ParseBorderStyle(tokens[0]);
					break;
				case "border-right-style":
					_style.BorderRight.Style = ParseBorderStyle(tokens[0]);
					break;
				case "border-bottom-style":
					_style.BorderBottom.Style = ParseBorderStyle(tokens[0]);
					break;
				case "border-left-style":
					_style.BorderLeft.Style = ParseBorderStyle(tokens[0]);
					break;
				case "border-top-color":
					if (CssValue.TryColor(tokens, 0, out Color topColor, out _))
						_style.BorderTop.Color = topColor;
					break;
				case "border-right-color":
					if (CssValue.TryColor(tokens, 0, out Color rightColor, out _))
						_style.BorderRight.Color = rightColor;
					break;
				case "border-bottom-color":
					if (CssValue.TryColor(tokens, 0, out Color bottomColor, out _))
						_style.BorderBottom.Color = bottomColor;
					break;
				case "border-left-color":
					if (CssValue.TryColor(tokens, 0, out Color leftColor, out _))
						_style.BorderLeft.Color = leftColor;
					break;
				case "color":
					if (tokens[0].IsIdent("currentcolor"))
						break;
					if (CssValue.TryColor(tokens, 0, out Color color, out _))
						_style.Color = color;
					break;
				case "background-color":
					if (CssValue.TryColor(tokens, 0, out Color backgroundColor, out _))
						_style.BackgroundColor = backgroundColor;
					break;
				case "background":
					ApplyBackground(tokens);
					break;
				case "background-image":
					_style.BackgroundImage = tokens[0].Type == CssTokenType.Url ? tokens[0].Value : null;
					if (tokens[0].Type == CssTokenType.Function && tokens[0].Value.EndsWith("gradient", StringComparison.OrdinalIgnoreCase))
						SkipFunction(tokens, 0, true);
					break;
				case "font-size":
					ApplyFontSize(tokens[0]);
					break;
				case "font-weight":
					ApplyFontWeight(tokens[0]);
					break;
				case "font-style":
					_style.Italic = tokens[0].IsIdent("italic") || tokens[0].IsIdent("oblique");
					break;
				case "font-family":
					_style.FontFamily = ParseFamily(tokens);
					break;
				case "font":
					ApplyFontShorthand(tokens);
					break;
				case "line-height":
					ApplyLineHeight(tokens[0]);
					break;
				case "text-align":
					_style.TextAlign = Keyword(tokens[0], "right", TextAlign.Right, "center", TextAlign.Center, "justify", TextAlign.Justify, tokens[0].IsIdent("end") ? TextAlign.Right : TextAlign.Left);
					break;
				case "text-decoration":
				case "text-decoration-line":
					ApplyTextDecoration(tokens);
					break;
				case "text-indent":
					if (CssValue.TryLength(tokens[0], _fontSize, _rootFontSize, true, true, out Length indent))
						_style.TextIndent = indent;
					break;
				case "text-transform":
					_style.TextTransform = Keyword(tokens[0], "uppercase", TextTransform.Uppercase, "lowercase", TextTransform.Lowercase, "capitalize", TextTransform.Capitalize, TextTransform.None);
					break;
				case "white-space":
					_style.WhiteSpace = Keyword(tokens[0], "pre", WhiteSpace.Pre, "nowrap", WhiteSpace.NoWrap, "pre-wrap", WhiteSpace.PreWrap, tokens[0].IsIdent("pre-line") ? WhiteSpace.PreLine : WhiteSpace.Normal);
					break;
				case "vertical-align":
					_style.VerticalAlign = Keyword(tokens[0], "top", VerticalAlign.Top, "middle", VerticalAlign.Middle, "bottom", VerticalAlign.Bottom,
						tokens[0].IsIdent("sub") ? VerticalAlign.Sub : tokens[0].IsIdent("super") ? VerticalAlign.Super : tokens[0].IsIdent("text-top") ? VerticalAlign.TextTop : tokens[0].IsIdent("text-bottom") ? VerticalAlign.TextBottom : VerticalAlign.Baseline);
					break;
				case "list-style-type":
					_style.ListStyleType = ParseListStyle(tokens[0]);
					break;
				case "list-style-position":
					_style.ListInside = tokens[0].IsIdent("inside");
					break;
				case "list-style":
					foreach (CssToken token in tokens)
					{
						if (token.IsIdent("inside"))
							_style.ListInside = true;
						else if (token.IsIdent("outside"))
							_style.ListInside = false;
						else if (token.Type == CssTokenType.Ident)
							_style.ListStyleType = ParseListStyle(token);
					}
					break;
				case "letter-spacing":
					if (CssValue.TryLength(tokens[0], _fontSize, _rootFontSize, false, true, out Length spacing))
						_style.LetterSpacing = spacing.Value;
					else if (tokens[0].IsIdent("normal"))
						_style.LetterSpacing = 0;
					break;
				case "opacity":
					if (tokens[0].Type == CssTokenType.Number)
						_style.Opacity = (float)Math.Clamp(tokens[0].Number, 0, 1);
					break;
				case "flex-direction":
					_style.FlexDirection = Keyword(tokens[0], "row-reverse", FlexDirection.RowReverse, "column", FlexDirection.Column, "column-reverse", FlexDirection.ColumnReverse, FlexDirection.Row);
					break;
				case "flex-wrap":
					_style.FlexWrap = tokens[0].IsIdent("wrap") || tokens[0].IsIdent("wrap-reverse");
					break;
				case "flex-flow":
					foreach (CssToken token in tokens)
					{
						if (token.IsIdent("wrap") || token.IsIdent("wrap-reverse"))
							_style.FlexWrap = true;
						else if (token.IsIdent("nowrap"))
							_style.FlexWrap = false;
						else if (token.Type == CssTokenType.Ident)
							_style.FlexDirection = Keyword(token, "row-reverse", FlexDirection.RowReverse, "column", FlexDirection.Column, "column-reverse", FlexDirection.ColumnReverse, FlexDirection.Row);
					}
					break;
				case "justify-content":
					_style.JustifyContent = ParseJustify(tokens[0]);
					break;
				case "align-items":
					_style.AlignItems = ParseAlign(tokens[0], AlignItems.Stretch);
					break;
				case "align-self":
					_style.AlignSelf = ParseAlign(tokens[0], AlignItems.Auto);
					break;
				case "flex-grow":
					if (tokens[0].Type == CssTokenType.Number)
						_style.FlexGrow = (float)Math.Max(0, tokens[0].Number);
					break;
				case "flex-shrink":
					if (tokens[0].Type == CssTokenType.Number)
						_style.FlexShrink = (float)Math.Max(0, tokens[0].Number);
					break;
				case "flex-basis":
					ApplyFlexBasis(tokens[0]);
					break;
				case "flex":
					ApplyFlexShorthand(tokens);
					break;
				case "gap":
					if (TryEdge(tokens[0], false, false, out Length rowGap))
					{
						_style.RowGap = rowGap;
						_style.ColumnGap = tokens.Count > 1 && TryEdge(tokens[1], false, false, out Length columnGap) ? columnGap : rowGap;
					}
					break;
				case "row-gap":
					if (TryEdge(tokens[0], false, false, out Length onlyRowGap))
						_style.RowGap = onlyRowGap;
					break;
				case "column-gap":
					if (TryEdge(tokens[0], false, false, out Length onlyColumnGap))
						_style.ColumnGap = onlyColumnGap;
					break;
				case "order":
					if (tokens[0].Type == CssTokenType.Number)
						_style.Order = (int)tokens[0].Number;
					break;
				case "grid-template-columns":
					_style.GridColumns = ParseTrackList(tokens, 0, tokens.Count);
					break;
				case "grid-template-rows":
					_style.GridRows = ParseTrackList(tokens, 0, tokens.Count);
					break;
				case "grid-auto-rows":
					_style.GridAutoRows = ParseTrackList(tokens, 0, tokens.Count) is { } autoRows && autoRows.Before.Count > 0 ? autoRows.Before[0] : new GridTrack(GridSize.AutoSize);
					break;
				case "grid-auto-columns":
					_style.GridAutoColumns = ParseTrackList(tokens, 0, tokens.Count) is { } autoColumns && autoColumns.Before.Count > 0 ? autoColumns.Before[0] : new GridTrack(GridSize.AutoSize);
					break;
				case "grid-auto-flow":
					_style.GridAutoFlow = tokens[0].IsIdent("column") ? GridAutoFlow.Column : GridAutoFlow.Row;
					break;
				case "grid-template-areas":
					_style.GridAreas = ParseAreas(tokens);
					break;
				case "grid-template":
				case "grid":
					ApplyGridTemplate(tokens);
					break;
				case "grid-column":
					ApplyGridPlacement(tokens, ref _style.GridColumnStart, ref _style.GridColumnEnd);
					break;
				case "grid-row":
					ApplyGridPlacement(tokens, ref _style.GridRowStart, ref _style.GridRowEnd);
					break;
				case "grid-column-start":
					_style.GridColumnStart = ParseGridLine(tokens, 0, tokens.Count);
					break;
				case "grid-column-end":
					_style.GridColumnEnd = ParseGridLine(tokens, 0, tokens.Count);
					break;
				case "grid-row-start":
					_style.GridRowStart = ParseGridLine(tokens, 0, tokens.Count);
					break;
				case "grid-row-end":
					_style.GridRowEnd = ParseGridLine(tokens, 0, tokens.Count);
					break;
				case "grid-area":
					ApplyGridArea(tokens);
					break;
				case "grid-gap":
					if (TryEdge(tokens[0], false, false, out Length gridRowGap))
					{
						_style.RowGap = gridRowGap;
						_style.ColumnGap = tokens.Count > 1 && TryEdge(tokens[1], false, false, out Length gridColumnGap) ? gridColumnGap : gridRowGap;
					}
					break;
				case "grid-row-gap":
					if (TryEdge(tokens[0], false, false, out Length onlyGridRowGap))
						_style.RowGap = onlyGridRowGap;
					break;
				case "grid-column-gap":
					if (TryEdge(tokens[0], false, false, out Length onlyGridColumnGap))
						_style.ColumnGap = onlyGridColumnGap;
					break;
				case "justify-items":
					_style.JustifyItems = ParseAlign(tokens[0], AlignItems.Stretch);
					break;
				case "justify-self":
					_style.JustifySelf = ParseAlign(tokens[0], AlignItems.Auto);
					break;
				case "place-items":
					_style.AlignItems = ParseAlign(tokens[0], AlignItems.Stretch);
					_style.JustifyItems = ParseAlign(tokens.Count > 1 ? tokens[1] : tokens[0], AlignItems.Stretch);
					break;
				case "place-self":
					_style.AlignSelf = ParseAlign(tokens[0], AlignItems.Auto);
					_style.JustifySelf = ParseAlign(tokens.Count > 1 ? tokens[1] : tokens[0], AlignItems.Auto);
					break;
			}
		}

		private static bool HasVar(List<CssToken> tokens)
		{
			foreach (CssToken token in tokens)
			{
				if (token.Type == CssTokenType.Function && string.Equals(token.Value, "var", StringComparison.OrdinalIgnoreCase))
					return true;
			}
			return false;
		}

		// Replaces each var(--name, fallback) with the custom property's tokens, or the fallback; null when one is unknown and has no fallback
		private List<CssToken> SubstituteVars(List<CssToken> tokens, int depth)
		{
			if (depth > 8)
				return null;
			List<CssToken> result = new List<CssToken>(tokens.Count);
			for (int i = 0; i < tokens.Count; i++)
			{
				CssToken token = tokens[i];
				if (token.Type != CssTokenType.Function || !string.Equals(token.Value, "var", StringComparison.OrdinalIgnoreCase))
				{
					result.Add(token);
					continue;
				}
				int close = i + 1;
				int nested = 1;
				int comma = -1;
				while (close < tokens.Count)
				{
					CssTokenType type = tokens[close].Type;
					if (type == CssTokenType.Function || type == CssTokenType.OpenParen)
						nested++;
					else if (type == CssTokenType.CloseParen && --nested == 0)
						break;
					else if (type == CssTokenType.Comma && nested == 1 && comma < 0)
						comma = close;
					close++;
				}
				int nameAt = i + 1;
				while (nameAt < close && tokens[nameAt].Type == CssTokenType.Whitespace)
					nameAt++;
				if (nameAt >= close || tokens[nameAt].Type != CssTokenType.Ident)
					return null;
				List<CssToken> replacement = null;
				if (_style.Custom != null && _style.Custom.TryGetValue(tokens[nameAt].Value, out List<CssToken> custom))
					replacement = custom;
				else if (comma >= 0)
					replacement = tokens.GetRange(comma + 1, close - comma - 1);
				if (replacement == null)
					return null;
				if (HasVar(replacement))
				{
					replacement = SubstituteVars(replacement, depth + 1);
					if (replacement == null)
						return null;
				}
				result.AddRange(replacement);
				i = close;
			}
			return result;
		}

		// Left over from before the cascade knew about the element: whatever still needs the element itself
		public void Finish(Element element)
		{
			if (element.TagName == "html" || element.TagName == "body")
				_style.Position = Position.Static;
		}

		private void InheritProperty(string property)
		{
			if (_parent == null)
				return;
			switch (property)
			{
				case "color":
					_style.Color = _parent.Color;
					break;
				case "background-color":
				case "background":
					_style.BackgroundColor = _parent.BackgroundColor;
					break;
				case "font-size":
					_style.FontSize = _parent.FontSize;
					break;
				case "font-weight":
					_style.Bold = _parent.Bold;
					break;
				case "font-family":
					_style.FontFamily = _parent.FontFamily;
					break;
				case "text-align":
					_style.TextAlign = _parent.TextAlign;
					break;
				case "display":
					_style.Display = _parent.Display;
					break;
				case "line-height":
					_style.LineHeight = _parent.LineHeight;
					break;
				case "white-space":
					_style.WhiteSpace = _parent.WhiteSpace;
					break;
				case "text-decoration":
					_style.Underline = _parent.Underline;
					_style.LineThrough = _parent.LineThrough;
					break;
			}
		}

		private void ResetProperty(string property, bool unset)
		{
			ComputedStyle initial = unset ? ComputedStyle.Inherit(_parent) : new ComputedStyle();
			switch (property)
			{
				case "color":
					_style.Color = initial.Color;
					break;
				case "background-color":
				case "background":
					_style.BackgroundColor = Color.Transparent;
					_style.BackgroundImage = null;
					break;
				case "font-size":
					_style.FontSize = initial.FontSize;
					break;
				case "font-weight":
					_style.Bold = initial.Bold;
					break;
				case "font-style":
					_style.Italic = initial.Italic;
					break;
				case "font-family":
					_style.FontFamily = initial.FontFamily;
					break;
				case "text-align":
					_style.TextAlign = initial.TextAlign;
					break;
				case "line-height":
					_style.LineHeight = initial.LineHeight;
					break;
				case "margin":
					_style.Margin = new Edges(Length.Zero);
					break;
				case "padding":
					_style.Padding = new Edges(Length.Zero);
					break;
				case "border":
					_style.BorderTop = _style.BorderRight = _style.BorderBottom = _style.BorderLeft = default;
					break;
				case "display":
					_style.Display = Display.Inline;
					break;
				case "width":
					_style.Width = Length.Auto;
					break;
				case "height":
					_style.Height = Length.Auto;
					break;
				case "text-decoration":
					_style.Underline = initial.Underline;
					_style.LineThrough = initial.LineThrough;
					break;
				case "white-space":
					_style.WhiteSpace = initial.WhiteSpace;
					break;
				case "list-style-type":
				case "list-style":
					_style.ListStyleType = initial.ListStyleType;
					break;
			}
		}

		private void ApplyDisplay(CssToken token)
		{
			if (token.Type != CssTokenType.Ident)
				return;
			switch (token.Value.ToLowerInvariant())
			{
				case "block":
				case "flow-root":
					_style.Display = Display.Block;
					break;
				case "flex":
					_style.Display = Display.Flex;
					break;
				case "inline-flex":
					_style.Display = Display.InlineFlex;
					break;
				case "grid":
					_style.Display = Display.Grid;
					break;
				case "inline-grid":
					_style.Display = Display.InlineGrid;
					break;
				case "inline":
				case "contents":
					_style.Display = Display.Inline;
					break;
				case "inline-block":
					_style.Display = Display.InlineBlock;
					break;
				case "list-item":
					_style.Display = Display.ListItem;
					break;
				case "none":
					_style.Display = Display.None;
					break;
				case "table":
				case "inline-table":
					_style.Display = Display.Table;
					break;
				case "table-row":
					_style.Display = Display.TableRow;
					break;
				case "table-cell":
					_style.Display = Display.TableCell;
					break;
				case "table-row-group":
					_style.Display = Display.TableRowGroup;
					break;
				case "table-header-group":
					_style.Display = Display.TableHeaderGroup;
					break;
				case "table-footer-group":
					_style.Display = Display.TableFooterGroup;
					break;
				case "table-caption":
					_style.Display = Display.TableCaption;
					break;
				case "table-column":
					_style.Display = Display.TableColumn;
					break;
				case "table-column-group":
					_style.Display = Display.TableColumnGroup;
					break;
			}
		}

		private static T Keyword<T>(CssToken token, string a, T va, string b, T vb, string c, T vc, T fallback) =>
			token.IsIdent(a) ? va : token.IsIdent(b) ? vb : token.IsIdent(c) ? vc : fallback;

		// One to four values in CSS order: all; vertical horizontal; top horizontal bottom; top right bottom left
		private void ApplyEdges(List<CssToken> tokens, ref Edges edges, bool allowAuto, bool allowNegative)
		{
			Length[] values = new Length[Math.Min(tokens.Count, 4)];
			for (int i = 0; i < values.Length; i++)
			{
				if (!TryEdge(tokens[i], allowAuto, allowNegative, out values[i]))
					return;
			}
			switch (values.Length)
			{
				case 1:
					edges = new Edges(values[0]);
					break;
				case 2:
					edges.Top = edges.Bottom = values[0];
					edges.Left = edges.Right = values[1];
					break;
				case 3:
					edges.Top = values[0];
					edges.Left = edges.Right = values[1];
					edges.Bottom = values[2];
					break;
				case 4:
					edges.Top = values[0];
					edges.Right = values[1];
					edges.Bottom = values[2];
					edges.Left = values[3];
					break;
			}
		}

		private void ApplyEdge(CssToken token, ref Length edge, bool allowAuto, bool allowNegative)
		{
			if (TryEdge(token, allowAuto, allowNegative, out Length value))
				edge = value;
		}

		private bool TryEdge(CssToken token, bool allowAuto, bool allowNegative, out Length length)
		{
			if (token.IsIdent("auto") && allowAuto)
			{
				length = Length.Auto;
				return true;
			}
			return CssValue.TryLength(token, _fontSize, _rootFontSize, true, allowNegative, out length);
		}

		private void ApplySize(CssToken token, ref Length size, bool allowAuto)
		{
			if (token.IsIdent("auto") || token.IsIdent("none") || token.IsIdent("max-content") || token.IsIdent("min-content") || token.IsIdent("fit-content"))
			{
				if (allowAuto)
					size = Length.Auto;
				return;
			}
			if (CssValue.TryLength(token, _fontSize, _rootFontSize, true, false, out Length value))
				size = value;
		}

		private float BorderWidth(CssToken token)
		{
			if (token.IsIdent("thin"))
				return 1;
			if (token.IsIdent("medium"))
				return 3;
			if (token.IsIdent("thick"))
				return 5;
			return CssValue.TryLength(token, _fontSize, _rootFontSize, false, false, out Length length) ? length.Value : 3;
		}

		private static BorderStyle ParseBorderStyle(CssToken token)
		{
			if (token.Type != CssTokenType.Ident)
				return BorderStyle.None;
			return token.Value.ToLowerInvariant() switch
			{
				"solid" => BorderStyle.Solid,
				"dotted" => BorderStyle.Dotted,
				"dashed" => BorderStyle.Dashed,
				"double" => BorderStyle.Double,
				"inset" => BorderStyle.Inset,
				"outset" => BorderStyle.Outset,
				"groove" => BorderStyle.Groove,
				"ridge" => BorderStyle.Ridge,
				"hidden" => BorderStyle.Hidden,
				_ => BorderStyle.None,
			};
		}

		// border: width style color in any order; side -1 means all four
		private void ApplyBorderShorthand(List<CssToken> tokens, int side)
		{
			BorderEdge edge = new BorderEdge { Width = 3, Style = BorderStyle.None, Color = _style.Color };
			for (int i = 0; i < tokens.Count; i++)
			{
				CssToken token = tokens[i];
				if (token.Type == CssTokenType.Ident && ParseBorderStyle(token) != BorderStyle.None || token.IsIdent("none") || token.IsIdent("hidden"))
					edge.Style = ParseBorderStyle(token);
				else if (token.Type == CssTokenType.Dimension || token.Type == CssTokenType.Number || token.IsIdent("thin") || token.IsIdent("medium") || token.IsIdent("thick"))
					edge.Width = BorderWidth(token);
				else if (CssValue.TryColor(tokens, i, out Color color, out int consumed))
				{
					edge.Color = color;
					i += consumed - 1;
				}
			}
			SetBorder(side, edge);
		}

		private void SetBorder(int side, BorderEdge edge)
		{
			if (side < 0 || side == 0)
				_style.BorderTop = edge;
			if (side < 0 || side == 1)
				_style.BorderRight = edge;
			if (side < 0 || side == 2)
				_style.BorderBottom = edge;
			if (side < 0 || side == 3)
				_style.BorderLeft = edge;
		}

		private void ApplyBorderWidths(List<CssToken> tokens)
		{
			float[] widths = new float[4];
			int count = Math.Min(tokens.Count, 4);
			for (int i = 0; i < count; i++)
				widths[i] = BorderWidth(tokens[i]);
			(float top, float right, float bottom, float left) = Spread(widths, count);
			_style.BorderTop.Width = top;
			_style.BorderRight.Width = right;
			_style.BorderBottom.Width = bottom;
			_style.BorderLeft.Width = left;
		}

		private void ApplyBorderStyles(List<CssToken> tokens)
		{
			BorderStyle[] styles = new BorderStyle[4];
			int count = Math.Min(tokens.Count, 4);
			for (int i = 0; i < count; i++)
				styles[i] = ParseBorderStyle(tokens[i]);
			(BorderStyle top, BorderStyle right, BorderStyle bottom, BorderStyle left) = Spread(styles, count);
			_style.BorderTop.Style = top;
			_style.BorderRight.Style = right;
			_style.BorderBottom.Style = bottom;
			_style.BorderLeft.Style = left;
		}

		private void ApplyBorderColors(List<CssToken> tokens)
		{
			Color[] colors = new Color[4];
			int count = 0;
			for (int i = 0; i < tokens.Count && count < 4; count++)
			{
				if (!CssValue.TryColor(tokens, i, out colors[count], out int consumed))
					return;
				i += consumed;
			}
			if (count == 0)
				return;
			(Color top, Color right, Color bottom, Color left) = Spread(colors, count);
			_style.BorderTop.Color = top;
			_style.BorderRight.Color = right;
			_style.BorderBottom.Color = bottom;
			_style.BorderLeft.Color = left;
		}

		private static (T, T, T, T) Spread<T>(T[] values, int count) => count switch
		{
			1 => (values[0], values[0], values[0], values[0]),
			2 => (values[0], values[1], values[0], values[1]),
			3 => (values[0], values[1], values[2], values[1]),
			_ => (values[0], values[1], values[2], values[3]),
		};

		// background: the color and url() are taken, positions, repeats and gradients are not
		private void ApplyBackground(List<CssToken> tokens)
		{
			_style.BackgroundColor = Color.Transparent;
			_style.BackgroundImage = null;
			foreach (List<CssToken> layer in CssValue.SplitCommas(tokens))
			{
				for (int i = 0; i < layer.Count; i++)
				{
					CssToken token = layer[i];
					if (token.Type == CssTokenType.Url)
					{
						_style.BackgroundImage ??= token.Value;
						continue;
					}
					if (token.Type == CssTokenType.Function && !IsColorFunction(token.Value))
					{
						i = SkipFunction(layer, i, token.Value.EndsWith("gradient", StringComparison.OrdinalIgnoreCase));
						continue;
					}
					if (CssValue.TryColor(layer, i, out Color color, out int consumed))
					{
						_style.BackgroundColor = color;
						i += consumed - 1;
					}
				}
			}
		}

		private static bool IsColorFunction(string name) => name.ToLowerInvariant() is "rgb" or "rgba" or "hsl" or "hsla" or "light-dark";

		// Steps past a function's arguments; a gradient's first colour stop stands in for the gradient when nothing else paints the background
		private int SkipFunction(List<CssToken> tokens, int at, bool gradient)
		{
			int nested = 1;
			int i = at;
			bool found = !_style.BackgroundColor.IsTransparent || !gradient;
			while (++i < tokens.Count && nested > 0)
			{
				CssTokenType type = tokens[i].Type;
				if (!found && nested == 1 && CssValue.TryColor(tokens, i, out Color stop, out int consumed))
				{
					_style.BackgroundColor = stop;
					found = true;
					i += consumed - 1;
					continue;
				}
				if (type == CssTokenType.Function || type == CssTokenType.OpenParen)
					nested++;
				else if (type == CssTokenType.CloseParen)
					nested--;
			}
			return i - 1;
		}

		// A track list: sizes, minmax(), repeat() with a count or auto-fill/auto-fit, and [line names]
		private GridTemplate ParseTrackList(List<CssToken> tokens, int start, int end)
		{
			GridTemplate template = new GridTemplate();
			if (end - start == 1 && tokens[start].IsIdent("none"))
				return template;
			List<GridTrack> target = template.Before;
			int i = start;
			while (i < end)
			{
				CssToken token = tokens[i];
				if (token.Type == CssTokenType.OpenBracket)
				{
					int close = i + 1;
					while (close < end && tokens[close].Type != CssTokenType.CloseBracket)
					{
						if (tokens[close].Type == CssTokenType.Ident)
						{
							int line = target == template.After && template.HasAutoRepeat ? -(template.After.Count + 1) : target.Count + 1;
							if (!template.LineNames.TryGetValue(tokens[close].Value, out List<int> lines))
								template.LineNames[tokens[close].Value] = lines = new List<int>();
							lines.Add(line);
						}
						close++;
					}
					i = close + 1;
					continue;
				}
				if (token.Type == CssTokenType.Function && string.Equals(token.Value, "repeat", StringComparison.OrdinalIgnoreCase))
				{
					int close = MatchingParen(tokens, i, end);
					int comma = i + 1;
					while (comma < close && tokens[comma].Type != CssTokenType.Comma)
						comma++;
					List<GridTrack> inner = new List<GridTrack>();
					GridTemplate repeated = ParseTrackList(tokens, comma + 1, close);
					inner.AddRange(repeated.Before);
					CssToken count = tokens[i + 1];
					if (count.IsIdent("auto-fill") || count.IsIdent("auto-fit"))
					{
						template.HasAutoRepeat = true;
						template.AutoFit = count.IsIdent("auto-fit");
						template.Repeat.AddRange(inner);
						target = template.After;
					}
					else
					{
						int times = count.Type == CssTokenType.Number ? Math.Clamp((int)count.Number, 1, 1000) : 1;
						for (int n = 0; n < times; n++)
							target.AddRange(inner);
					}
					i = close + 1;
					continue;
				}
				if (token.Type == CssTokenType.Function && string.Equals(token.Value, "minmax", StringComparison.OrdinalIgnoreCase))
				{
					int close = MatchingParen(tokens, i, end);
					int comma = i + 1;
					while (comma < close && tokens[comma].Type != CssTokenType.Comma)
						comma++;
					GridSize min = comma > i + 1 ? ParseGridSize(tokens[i + 1]) : GridSize.AutoSize;
					GridSize max = comma + 1 < close ? ParseGridSize(tokens[comma + 1]) : GridSize.AutoSize;
					target.Add(new GridTrack(min, max));
					i = close + 1;
					continue;
				}
				if (token.Type == CssTokenType.Function && string.Equals(token.Value, "fit-content", StringComparison.OrdinalIgnoreCase))
				{
					int close = MatchingParen(tokens, i, end);
					GridSize limit = close > i + 1 ? ParseGridSize(tokens[i + 1]) : GridSize.AutoSize;
					target.Add(new GridTrack(GridSize.AutoSize, limit));
					i = close + 1;
					continue;
				}
				if (token.Type == CssTokenType.Function)
				{
					i = MatchingParen(tokens, i, end) + 1;
					continue;
				}
				target.Add(new GridTrack(ParseGridSize(token)));
				i++;
			}
			return template;
		}

		private static int MatchingParen(List<CssToken> tokens, int open, int end)
		{
			int depth = 0;
			for (int i = open; i < end; i++)
			{
				if (tokens[i].Type == CssTokenType.Function || tokens[i].Type == CssTokenType.OpenParen)
					depth++;
				else if (tokens[i].Type == CssTokenType.CloseParen && --depth == 0)
					return i;
			}
			return end;
		}

		private GridSize ParseGridSize(CssToken token)
		{
			if (token.Type == CssTokenType.Dimension && token.Unit == "fr")
				return new GridSize(GridSizeKind.Fr, (float)Math.Max(0, token.Number));
			if (token.Type == CssTokenType.Percentage)
				return new GridSize(GridSizeKind.Percent, (float)token.Number);
			if (token.IsIdent("min-content"))
				return new GridSize(GridSizeKind.MinContent);
			if (token.IsIdent("max-content"))
				return new GridSize(GridSizeKind.MaxContent);
			if (CssValue.TryLength(token, _fontSize, _rootFontSize, false, false, out Length length))
				return new GridSize(GridSizeKind.Fixed, length.Value);
			return GridSize.AutoSize;
		}

		// grid-template-areas: one string per row, names separated by spaces, a dot for an empty cell
		private static string[][] ParseAreas(List<CssToken> tokens)
		{
			List<string[]> rows = new List<string[]>();
			foreach (CssToken token in tokens)
			{
				if (token.Type != CssTokenType.String)
					continue;
				string[] names = token.Value.Split(new[] { ' ', '\t' }, StringSplitOptions.RemoveEmptyEntries);
				for (int i = 0; i < names.Length; i++)
				{
					if (names[i].StartsWith(".", StringComparison.Ordinal))
						names[i] = null;
				}
				rows.Add(names);
			}
			return rows.Count == 0 ? null : rows.ToArray();
		}

		// grid-template and grid: "areas" rows with optional sizes then "/ columns", or "rows / columns"
		private void ApplyGridTemplate(List<CssToken> tokens)
		{
			if (tokens.Count == 1 && tokens[0].IsIdent("none"))
			{
				_style.GridColumns = null;
				_style.GridRows = null;
				_style.GridAreas = null;
				return;
			}
			int slash = -1;
			int depth = 0;
			for (int i = 0; i < tokens.Count; i++)
			{
				if (tokens[i].Type == CssTokenType.Function || tokens[i].Type == CssTokenType.OpenParen)
					depth++;
				else if (tokens[i].Type == CssTokenType.CloseParen)
					depth--;
				else if (depth == 0 && tokens[i].IsDelim('/'))
				{
					slash = i;
					break;
				}
			}
			int rowsEnd = slash < 0 ? tokens.Count : slash;
			bool hasAreas = false;
			foreach (CssToken token in tokens)
				hasAreas |= token.Type == CssTokenType.String;
			if (hasAreas)
			{
				List<string[]> areas = new List<string[]>();
				GridTemplate rows = new GridTemplate();
				for (int i = 0; i < rowsEnd; i++)
				{
					if (tokens[i].Type == CssTokenType.String)
					{
						string[] names = tokens[i].Value.Split(new[] { ' ', '\t' }, StringSplitOptions.RemoveEmptyEntries);
						for (int n = 0; n < names.Length; n++)
						{
							if (names[n].StartsWith(".", StringComparison.Ordinal))
								names[n] = null;
						}
						areas.Add(names);
						GridSize size = i + 1 < rowsEnd && tokens[i + 1].Type != CssTokenType.String && tokens[i + 1].Type != CssTokenType.OpenBracket ? ParseGridSize(tokens[i + 1]) : GridSize.AutoSize;
						rows.Before.Add(new GridTrack(size));
					}
				}
				_style.GridAreas = areas.ToArray();
				_style.GridRows = rows;
			}
			else
				_style.GridRows = ParseTrackList(tokens, 0, rowsEnd);
			if (slash >= 0)
				_style.GridColumns = ParseTrackList(tokens, slash + 1, tokens.Count);
		}

		// grid-column and grid-row: start [/ end]; one value that is a plain line leaves the end auto, a name applies to both
		private void ApplyGridPlacement(List<CssToken> tokens, ref GridLine start, ref GridLine end)
		{
			int slash = tokens.FindIndex(t => t.IsDelim('/'));
			if (slash < 0)
			{
				start = ParseGridLine(tokens, 0, tokens.Count);
				end = start.Name != null && start.Line == 0 && start.Span == 0 ? start : GridLine.Auto;
				return;
			}
			start = ParseGridLine(tokens, 0, slash);
			end = ParseGridLine(tokens, slash + 1, tokens.Count);
		}

		// grid-area: a name, or row-start / column-start / row-end / column-end
		private void ApplyGridArea(List<CssToken> tokens)
		{
			List<int> slashes = new List<int>();
			for (int i = 0; i < tokens.Count; i++)
			{
				if (tokens[i].IsDelim('/'))
					slashes.Add(i);
			}
			if (slashes.Count == 0)
			{
				GridLine line = ParseGridLine(tokens, 0, tokens.Count);
				_style.GridRowStart = _style.GridRowEnd = _style.GridColumnStart = _style.GridColumnEnd = line;
				return;
			}
			List<GridLine> parts = new List<GridLine>();
			int from = 0;
			foreach (int slash in slashes)
			{
				parts.Add(ParseGridLine(tokens, from, slash));
				from = slash + 1;
			}
			parts.Add(ParseGridLine(tokens, from, tokens.Count));
			_style.GridRowStart = parts[0];
			_style.GridColumnStart = parts.Count > 1 ? parts[1] : GridLine.Auto;
			_style.GridRowEnd = parts.Count > 2 ? parts[2] : GridLine.Auto;
			_style.GridColumnEnd = parts.Count > 3 ? parts[3] : GridLine.Auto;
		}

		private static GridLine ParseGridLine(List<CssToken> tokens, int start, int end)
		{
			if (start >= end || tokens[start].IsIdent("auto"))
				return GridLine.Auto;
			bool span = false;
			int number = 0;
			string name = null;
			for (int i = start; i < end; i++)
			{
				CssToken token = tokens[i];
				if (token.IsIdent("span"))
					span = true;
				else if (token.Type == CssTokenType.Number)
					number = (int)token.Number;
				else if (token.Type == CssTokenType.Ident)
					name = token.Value;
			}
			if (span)
				return new GridLine(0, Math.Max(1, number == 0 ? 1 : number), name);
			return new GridLine(number, 0, name);
		}

		private static JustifyContent ParseJustify(CssToken token)
		{
			if (token.Type != CssTokenType.Ident)
				return JustifyContent.FlexStart;
			return token.Value.ToLowerInvariant() switch
			{
				"flex-end" or "end" or "right" => JustifyContent.FlexEnd,
				"center" => JustifyContent.Center,
				"space-between" => JustifyContent.SpaceBetween,
				"space-around" => JustifyContent.SpaceAround,
				"space-evenly" => JustifyContent.SpaceEvenly,
				_ => JustifyContent.FlexStart,
			};
		}

		private static AlignItems ParseAlign(CssToken token, AlignItems fallback)
		{
			if (token.Type != CssTokenType.Ident)
				return fallback;
			return token.Value.ToLowerInvariant() switch
			{
				"stretch" or "normal" => AlignItems.Stretch,
				"flex-start" or "start" or "self-start" => AlignItems.FlexStart,
				"flex-end" or "end" or "self-end" => AlignItems.FlexEnd,
				"center" => AlignItems.Center,
				"baseline" => AlignItems.Baseline,
				"auto" => AlignItems.Auto,
				_ => fallback,
			};
		}

		private void ApplyFlexBasis(CssToken token)
		{
			if (token.IsIdent("auto") || token.IsIdent("content"))
				_style.FlexBasis = Length.Auto;
			else if (CssValue.TryLength(token, _fontSize, _rootFontSize, true, false, out Length basis))
				_style.FlexBasis = basis;
		}

		// flex: none | auto | [grow [shrink]] [basis], with the usual defaults for the one-value forms
		private void ApplyFlexShorthand(List<CssToken> tokens)
		{
			if (tokens.Count == 1 && tokens[0].IsIdent("none"))
			{
				_style.FlexGrow = 0;
				_style.FlexShrink = 0;
				_style.FlexBasis = Length.Auto;
				return;
			}
			if (tokens.Count == 1 && tokens[0].IsIdent("auto"))
			{
				_style.FlexGrow = 1;
				_style.FlexShrink = 1;
				_style.FlexBasis = Length.Auto;
				return;
			}
			_style.FlexGrow = 1;
			_style.FlexShrink = 1;
			_style.FlexBasis = Length.Zero;
			int numbers = 0;
			foreach (CssToken token in tokens)
			{
				if (token.Type == CssTokenType.Number && !token.IsDelim('/'))
				{
					if (numbers == 0)
						_style.FlexGrow = (float)Math.Max(0, token.Number);
					else if (numbers == 1)
						_style.FlexShrink = (float)Math.Max(0, token.Number);
					numbers++;
				}
				else
					ApplyFlexBasis(token);
			}
		}

		private void ApplyFontSize(CssToken token)
		{
			if (token.Type == CssTokenType.Ident)
			{
				float size = token.Value.ToLowerInvariant() switch
				{
					"xx-small" => 9,
					"x-small" => 10,
					"small" => 13,
					"medium" => 16,
					"large" => 18,
					"x-large" => 24,
					"xx-large" => 32,
					"xxx-large" => 48,
					"smaller" => _parentFontSize / 1.2f,
					"larger" => _parentFontSize * 1.2f,
					_ => -1,
				};
				if (size > 0)
					_style.FontSize = MathF.Round(size * 4) / 4;
				return;
			}
			if (token.Type == CssTokenType.Percentage)
			{
				_style.FontSize = MathF.Max(1, (float)(_parentFontSize * token.Number / 100));
				return;
			}
			if (token.Type == CssTokenType.Dimension || token.Type == CssTokenType.Number)
			{
				double pixels = CssValue.ToPixels(token.Number, token.Type == CssTokenType.Dimension ? token.Unit : "px", _parentFontSize, _rootFontSize);
				if (pixels > 0)
					_style.FontSize = (float)pixels;
			}
		}

		private void ApplyFontWeight(CssToken token)
		{
			if (token.Type == CssTokenType.Number)
				_style.Bold = token.Number >= 600;
			else if (token.IsIdent("bold") || token.IsIdent("bolder"))
				_style.Bold = true;
			else if (token.IsIdent("normal") || token.IsIdent("lighter"))
				_style.Bold = false;
		}

		// The first family that maps to a face the image has; generic names and the common Windows and web names
		private static FontFamily ParseFamily(List<CssToken> tokens)
		{
			foreach (List<CssToken> part in CssValue.SplitCommas(tokens))
			{
				string name = string.Join(" ", part.ConvertAll(t => t.Value)).ToLowerInvariant();
				switch (name)
				{
					case "serif":
					case "times":
					case "times new roman":
					case "georgia":
					case "garamond":
					case "cambria":
					case "book antiqua":
					case "palatino":
					case "palatino linotype":
					case "baskerville":
						return FontFamily.Serif;
					case "monospace":
					case "courier":
					case "courier new":
					case "consolas":
					case "menlo":
					case "monaco":
					case "lucida console":
					case "source code pro":
					case "fira code":
					case "ui-monospace":
						return FontFamily.Mono;
					case "sans-serif":
					case "arial":
					case "helvetica":
					case "helvetica neue":
					case "verdana":
					case "tahoma":
					case "segoe ui":
					case "roboto":
					case "open sans":
					case "system-ui":
					case "-apple-system":
					case "blinkmacsystemfont":
					case "ubuntu":
					case "noto sans":
					case "inter":
					case "lato":
						return FontFamily.Sans;
				}
			}
			return FontFamily.Sans;
		}

		// font: [style] [weight] size[/line-height] family
		private void ApplyFontShorthand(List<CssToken> tokens)
		{
			int i = 0;
			_style.Italic = false;
			_style.Bold = false;
			while (i < tokens.Count)
			{
				CssToken token = tokens[i];
				if (token.IsIdent("italic") || token.IsIdent("oblique"))
					_style.Italic = true;
				else if (token.IsIdent("bold") || token.IsIdent("bolder") || (token.Type == CssTokenType.Number && token.Number >= 100 && token.Number <= 900 && i + 1 < tokens.Count && tokens[i + 1].Type != CssTokenType.Comma))
					_style.Bold = token.Type == CssTokenType.Number ? token.Number >= 600 : true;
				else if (token.IsIdent("normal") || token.IsIdent("small-caps") || token.IsIdent("lighter"))
				{
				}
				else
					break;
				i++;
			}
			if (i >= tokens.Count)
				return;
			ApplyFontSize(tokens[i]);
			_fontSize = _style.FontSize;
			i++;
			if (i < tokens.Count && tokens[i].IsDelim('/'))
			{
				i++;
				if (i < tokens.Count)
				{
					ApplyLineHeight(tokens[i]);
					i++;
				}
			}
			if (i < tokens.Count)
				_style.FontFamily = ParseFamily(tokens.GetRange(i, tokens.Count - i));
		}

		private void ApplyLineHeight(CssToken token)
		{
			if (token.IsIdent("normal"))
				_style.LineHeight = -1;
			else if (token.Type == CssTokenType.Number)
				_style.LineHeight = (float)(token.Number * _fontSize);
			else if (token.Type == CssTokenType.Percentage)
				_style.LineHeight = (float)(token.Number / 100 * _fontSize);
			else if (CssValue.TryLength(token, _fontSize, _rootFontSize, false, false, out Length length))
				_style.LineHeight = length.Value;
		}

		private void ApplyTextDecoration(List<CssToken> tokens)
		{
			bool underline = false;
			bool lineThrough = false;
			bool none = false;
			foreach (CssToken token in tokens)
			{
				if (token.IsIdent("underline"))
					underline = true;
				else if (token.IsIdent("line-through"))
					lineThrough = true;
				else if (token.IsIdent("none"))
					none = true;
			}
			if (none || underline || lineThrough)
			{
				_style.Underline = underline;
				_style.LineThrough = lineThrough;
			}
		}

		private static ListStyleType ParseListStyle(CssToken token)
		{
			if (token.Type != CssTokenType.Ident)
				return ListStyleType.Disc;
			return token.Value.ToLowerInvariant() switch
			{
				"circle" => ListStyleType.Circle,
				"square" => ListStyleType.Square,
				"decimal" => ListStyleType.Decimal,
				"decimal-leading-zero" => ListStyleType.Decimal,
				"lower-alpha" => ListStyleType.LowerAlpha,
				"lower-latin" => ListStyleType.LowerAlpha,
				"upper-alpha" => ListStyleType.UpperAlpha,
				"upper-latin" => ListStyleType.UpperAlpha,
				"lower-roman" => ListStyleType.LowerRoman,
				"upper-roman" => ListStyleType.UpperRoman,
				"none" => ListStyleType.None,
				_ => ListStyleType.Disc,
			};
		}
	}

	// What an element looks like before the page says anything, after the HTML standard's rendering section
	public static class UserAgentStylesheet
	{
		public const string Css = @"
html, body, div, p, h1, h2, h3, h4, h5, h6, ul, ol, dl, dd, dt, li, blockquote, pre, address, form, fieldset, hr, table, caption,
article, aside, footer, header, hgroup, main, nav, section, figure, figcaption, details, summary, menu, dir, center, noscript, legend, search, dialog, optgroup { display: block; }
head, script, style, title, meta, link, base, template, noframes, param, area, datalist, [hidden] { display: none; }
li { display: list-item; }
table { display: table; border-spacing: 2px; box-sizing: border-box; text-indent: 0; }
button, select { box-sizing: border-box; }
caption { display: table-caption; text-align: center; }
thead { display: table-header-group; }
tbody { display: table-row-group; }
tfoot { display: table-footer-group; }
tr { display: table-row; }
td, th { display: table-cell; padding: 1px; }
th { font-weight: bold; text-align: center; }
col { display: table-column; }
colgroup { display: table-column-group; }
img, input, button, select, textarea, video, canvas, iframe, embed, object { display: inline-block; }
body { margin: 8px; }
p, dl, blockquote, figure { margin: 1em 0; }
blockquote, figure { margin-left: 40px; margin-right: 40px; }
address, cite, dfn, em, i, var { font-style: italic; }
b, strong { font-weight: bold; }
h1 { font-size: 2em; margin: 0.67em 0; font-weight: bold; }
h2 { font-size: 1.5em; margin: 0.83em 0; font-weight: bold; }
h3 { font-size: 1.17em; margin: 1em 0; font-weight: bold; }
h4 { font-size: 1em; margin: 1.33em 0; font-weight: bold; }
h5 { font-size: 0.83em; margin: 1.67em 0; font-weight: bold; }
h6 { font-size: 0.67em; margin: 2.33em 0; font-weight: bold; }
article h1, aside h1, nav h1, section h1 { font-size: 1.5em; margin: 0.83em 0; }
ul, ol, menu, dir { margin: 1em 0; padding-left: 40px; }
ul, menu, dir { list-style-type: disc; }
ol { list-style-type: decimal; }
ul ul, ol ul { list-style-type: circle; }
ul ul ul, ol ul ul, ol ol ul, ul ol ul { list-style-type: square; }
ul ul, ul ol, ol ul, ol ol { margin-top: 0; margin-bottom: 0; }
dd { margin-left: 40px; }
pre, code, kbd, samp, tt, xmp, listing, plaintext { font-family: monospace; }
pre, xmp, listing, plaintext { white-space: pre; margin: 1em 0; }
hr { display: block; margin: 0.5em auto; border: 1px inset gray; height: 0; color: gray; }
a:link, a[href] { color: #0000ee; text-decoration: underline; }
u, ins { text-decoration: underline; }
s, strike, del { text-decoration: line-through; }
small { font-size: smaller; }
big { font-size: larger; }
sub { vertical-align: sub; font-size: smaller; }
sup { vertical-align: super; font-size: smaller; }
center { text-align: center; }
mark { background-color: yellow; color: black; }
abbr[title], acronym[title] { text-decoration: underline; }
textarea, input, button, select { font-family: sans-serif; font-size: 13.3333px; }
button, input[type=button], input[type=submit], input[type=reset] { display: inline-block; padding: 1px 6px; border: 2px outset buttonface; background-color: #efefef; color: black; text-align: center; }
input:not([type]), input[type=text], input[type=search], input[type=email], input[type=url], input[type=password], input[type=number], input[type=tel], textarea { border: 1px inset #767676; padding: 1px 2px; background-color: white; color: black; }
input[type=hidden] { display: none; }
input[type=checkbox], input[type=radio] { border: 1px solid #767676; background-color: white; margin: 3px 3px 3px 4px; vertical-align: middle; }
input[type=checkbox][checked], input[type=radio][checked] { background-color: #1a73e8; border-color: #1a73e8; }
fieldset { margin: 0 2px; padding: 0.35em 0.75em 0.625em; border: 2px groove threedface; }
legend { padding: 0 2px; }
summary { display: block; }
details:not([open]) > :not(summary) { display: none; }
br { display: inline; }
nobr { white-space: nowrap; }
";
	}
}
