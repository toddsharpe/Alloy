using System;
using System.Collections.Generic;
using System.Globalization;
using System.Text;
using Alloy.Sdk.Script;
using Alloy.Sdk.Web.Css;
using Alloy.Sdk.Web.Html;
using Node = Alloy.Sdk.Web.Html.Node;
using Alloy.Sdk.Web.Layout;

namespace Alloy.Sdk.Web.Dom
{
	// element.style: the style attribute read and written one declaration at a time
	public sealed class JsStyle : JsObject
	{
		private readonly ScriptHost _host;
		private readonly Element _element;

		public JsStyle(JsObject prototype, ScriptHost host, Element element) : base(prototype)
		{
			Class = "CSSStyleDeclaration";
			_host = host;
			_element = element;
		}

		public Element Element => _element;

		public List<KeyValuePair<string, string>> Declarations => ScriptHost.ParseDeclarations(_element.GetAttribute("style") ?? "");

		public string Get(string property)
		{
			foreach (KeyValuePair<string, string> declaration in Declarations)
			{
				if (declaration.Key == property)
					return declaration.Value;
			}
			return "";
		}

		public void Set(string property, string value)
		{
			List<KeyValuePair<string, string>> declarations = Declarations;
			value = value?.Trim() ?? "";
			int index = declarations.FindIndex(d => d.Key == property);
			if (value.Length == 0)
			{
				if (index < 0)
					return;
				declarations.RemoveAt(index);
			}
			else if (index >= 0)
				declarations[index] = new KeyValuePair<string, string>(property, value);
			else
				declarations.Add(new KeyValuePair<string, string>(property, value));
			Write(declarations);
		}

		public void SetCssText(string text) => Write(ScriptHost.ParseDeclarations(text));

		private void Write(List<KeyValuePair<string, string>> declarations)
		{
			string serialized = ScriptHost.SerializeDeclarations(declarations);
			if (serialized.Length == 0)
				_host.RemoveAttribute(_element, "style");
			else
				_host.SetAttribute(_element, "style", serialized);
		}

		public override JsValue Get(string key, Interpreter interpreter, JsValue receiver)
		{
			if (GetProperty(key) != null || key.StartsWith("@@", StringComparison.Ordinal))
				return base.Get(key, interpreter, receiver);
			if (JsArray.TryIndex(key, out int index))
			{
				List<KeyValuePair<string, string>> declarations = Declarations;
				return index < declarations.Count ? declarations[index].Key : JsValue.Undefined;
			}
			return Get(ScriptHost.ToCssName(key));
		}

		// "transform" in style is how libraries probe for support, so every standard property is present, prefixed or not
		public override bool HasProperty(string key) => base.HasProperty(key) || IsKnownProperty(ScriptHost.ToCssName(key));

		public static bool IsKnownProperty(string name)
		{
			if (name.StartsWith("-webkit-", StringComparison.Ordinal))
				name = name.Substring(8);
			else if (name.StartsWith("-moz-", StringComparison.Ordinal) || name.StartsWith("-ms-", StringComparison.Ordinal))
				name = name.Substring(name.IndexOf('-', 1) + 1);
			return s_knownProperties.Contains(name);
		}

		private static readonly HashSet<string> s_knownProperties = new HashSet<string>(StringComparer.Ordinal)
		{
			"align-content", "align-items", "align-self", "all", "animation", "animation-delay", "animation-direction", "animation-duration", "animation-fill-mode", "animation-iteration-count", "animation-name", "animation-play-state", "animation-timing-function", "appearance", "aspect-ratio",
			"backdrop-filter", "backface-visibility", "background", "background-attachment", "background-blend-mode", "background-clip", "background-color", "background-image", "background-origin", "background-position", "background-position-x", "background-position-y", "background-repeat", "background-size",
			"block-size", "border", "border-bottom", "border-bottom-color", "border-bottom-left-radius", "border-bottom-right-radius", "border-bottom-style", "border-bottom-width", "border-collapse", "border-color", "border-image", "border-left", "border-left-color", "border-left-style", "border-left-width",
			"border-radius", "border-right", "border-right-color", "border-right-style", "border-right-width", "border-spacing", "border-style", "border-top", "border-top-color", "border-top-left-radius", "border-top-right-radius", "border-top-style", "border-top-width", "border-width", "bottom",
			"box-shadow", "box-sizing", "break-after", "break-before", "break-inside", "caption-side", "caret-color", "clear", "clip", "clip-path", "color", "color-scheme", "column-count", "column-gap", "column-rule", "column-span", "column-width", "columns", "contain", "container", "container-name", "container-type", "content", "counter-increment", "counter-reset", "cursor",
			"direction", "display", "empty-cells", "filter", "flex", "flex-basis", "flex-direction", "flex-flow", "flex-grow", "flex-shrink", "flex-wrap", "float", "font", "font-family", "font-feature-settings", "font-kerning", "font-size", "font-size-adjust", "font-stretch", "font-style", "font-variant", "font-variant-numeric", "font-variation-settings", "font-weight",
			"gap", "grid", "grid-area", "grid-auto-columns", "grid-auto-flow", "grid-auto-rows", "grid-column", "grid-column-end", "grid-column-gap", "grid-column-start", "grid-gap", "grid-row", "grid-row-end", "grid-row-gap", "grid-row-start", "grid-template", "grid-template-areas", "grid-template-columns", "grid-template-rows",
			"height", "hyphens", "image-rendering", "inline-size", "inset", "inset-block", "inset-inline", "isolation", "justify-content", "justify-items", "justify-self", "left", "letter-spacing", "line-break", "line-height", "list-style", "list-style-image", "list-style-position", "list-style-type",
			"margin", "margin-block", "margin-block-end", "margin-block-start", "margin-bottom", "margin-inline", "margin-inline-end", "margin-inline-start", "margin-left", "margin-right", "margin-top", "mask", "mask-image", "max-block-size", "max-height", "max-inline-size", "max-width", "min-block-size", "min-height", "min-inline-size", "min-width", "mix-blend-mode",
			"object-fit", "object-position", "offset", "opacity", "order", "orphans", "outline", "outline-color", "outline-offset", "outline-style", "outline-width", "overflow", "overflow-anchor", "overflow-wrap", "overflow-x", "overflow-y", "overscroll-behavior",
			"padding", "padding-block", "padding-block-end", "padding-block-start", "padding-bottom", "padding-inline", "padding-inline-end", "padding-inline-start", "padding-left", "padding-right", "padding-top", "page-break-after", "page-break-before", "page-break-inside", "perspective", "perspective-origin", "place-content", "place-items", "place-self", "pointer-events", "position",
			"quotes", "resize", "right", "rotate", "row-gap", "scale", "scroll-behavior", "scroll-margin", "scroll-padding", "scroll-snap-align", "scroll-snap-type", "scrollbar-color", "scrollbar-gutter", "scrollbar-width", "shape-outside", "tab-size", "table-layout", "text-align", "text-align-last", "text-decoration", "text-decoration-color", "text-decoration-line", "text-decoration-style", "text-decoration-thickness",
			"text-indent", "text-overflow", "text-rendering", "text-shadow", "text-size-adjust", "text-transform", "text-underline-offset", "text-wrap", "top", "touch-action", "transform", "transform-box", "transform-origin", "transform-style", "transition", "transition-delay", "transition-duration", "transition-property", "transition-timing-function", "translate",
			"unicode-bidi", "user-select", "vertical-align", "visibility", "white-space", "widows", "width", "will-change", "word-break", "word-spacing", "word-wrap", "writing-mode", "z-index", "zoom",
		};

		public override void Set(string key, JsValue value, Interpreter interpreter, JsValue receiver)
		{
			if (key == "cssText")
			{
				SetCssText(value.ToJsString());
				return;
			}
			if (GetProperty(key) != null || key.StartsWith("@@", StringComparison.Ordinal))
			{
				base.Set(key, value, interpreter, receiver);
				return;
			}
			Set(ScriptHost.ToCssName(key), value.IsNullish ? "" : value.ToJsString());
		}
	}

	// getComputedStyle(element): what the cascade and layout decided, read back as CSS text
	public sealed class JsComputedStyle : JsObject
	{
		private readonly ScriptHost _host;
		private readonly Element _element;

		public JsComputedStyle(JsObject prototype, ScriptHost host, Element element) : base(prototype)
		{
			Class = "CSSStyleDeclaration";
			_host = host;
			_element = element;
		}

		public string Get(string property) => _host.ComputedValue(_element, property);

		public override JsValue Get(string key, Interpreter interpreter, JsValue receiver)
		{
			if (GetProperty(key) != null || key.StartsWith("@@", StringComparison.Ordinal))
				return base.Get(key, interpreter, receiver);
			return Get(ScriptHost.ToCssName(key));
		}

		public override void Set(string key, JsValue value, Interpreter interpreter, JsValue receiver)
		{
		}
	}

	// classList and the other DOMTokenLists: a space-separated attribute as a set
	public sealed class JsTokenList : JsObject
	{
		private readonly ScriptHost _host;
		public readonly Element Element;
		public readonly string Attribute;

		public JsTokenList(JsObject prototype, ScriptHost host, Element element, string attribute) : base(prototype)
		{
			Class = "DOMTokenList";
			_host = host;
			Element = element;
			Attribute = attribute;
		}

		public List<string> Tokens
		{
			get
			{
				List<string> tokens = new List<string>();
				foreach (string token in (Element.GetAttribute(Attribute) ?? "").Split(new[] { ' ', '\t', '\n', '\r', '\f' }, StringSplitOptions.RemoveEmptyEntries))
				{
					if (!tokens.Contains(token))
						tokens.Add(token);
				}
				return tokens;
			}
		}

		public void Write(List<string> tokens)
		{
			string value = string.Join(" ", tokens);
			if (value.Length == 0 && !Element.HasAttribute(Attribute))
				return;
			_host.SetAttribute(Element, Attribute, value);
		}

		public override JsValue Get(string key, Interpreter interpreter, JsValue receiver)
		{
			if (key == "length")
				return Tokens.Count;
			if (key == "value")
				return Element.GetAttribute(Attribute) ?? "";
			if (JsArray.TryIndex(key, out int index))
			{
				List<string> tokens = Tokens;
				return index < tokens.Count ? tokens[index] : JsValue.Undefined;
			}
			return base.Get(key, interpreter, receiver);
		}

		public override void Set(string key, JsValue value, Interpreter interpreter, JsValue receiver)
		{
			if (key == "value")
			{
				_host.SetAttribute(Element, Attribute, value.ToJsString());
				return;
			}
			base.Set(key, value, interpreter, receiver);
		}
	}

	// element.dataset: the data-* attributes with camelCase names
	public sealed class JsDataset : JsObject
	{
		private readonly ScriptHost _host;
		private readonly Element _element;

		public JsDataset(JsObject prototype, ScriptHost host, Element element) : base(prototype)
		{
			Class = "DOMStringMap";
			_host = host;
			_element = element;
		}

		private static string AttributeName(string key)
		{
			StringBuilder name = new StringBuilder("data-");
			foreach (char c in key)
			{
				if (char.IsUpper(c))
					name.Append('-').Append(char.ToLowerInvariant(c));
				else
					name.Append(c);
			}
			return name.ToString();
		}

		public static string PropertyName(string attribute)
		{
			StringBuilder name = new StringBuilder();
			bool upper = false;
			foreach (char c in attribute.Substring(5))
			{
				if (c == '-')
				{
					upper = true;
					continue;
				}
				name.Append(upper ? char.ToUpperInvariant(c) : c);
				upper = false;
			}
			return name.ToString();
		}

		public override JsProperty GetOwnProperty(string key)
		{
			if (key.StartsWith("@@", StringComparison.Ordinal))
				return base.GetOwnProperty(key);
			string value = _element.GetAttribute(AttributeName(key));
			return value == null ? null : new JsProperty { Value = value };
		}

		public override JsValue Get(string key, Interpreter interpreter, JsValue receiver)
		{
			if (key.StartsWith("@@", StringComparison.Ordinal))
				return base.Get(key, interpreter, receiver);
			string value = _element.GetAttribute(AttributeName(key));
			return value == null ? base.Get(key, interpreter, receiver) : value;
		}

		public override void Set(string key, JsValue value, Interpreter interpreter, JsValue receiver) => _host.SetAttribute(_element, AttributeName(key), value.ToJsString());

		public override bool Delete(string key)
		{
			_host.RemoveAttribute(_element, AttributeName(key));
			return true;
		}
	}

	public sealed partial class ScriptHost
	{
		private JsObject _stylePrototype;
		private JsObject _tokenListPrototype;

		// camelCase to kebab-case, with the vendor and float special cases
		public static string ToCssName(string property)
		{
			if (property == "cssFloat" || property == "styleFloat")
				return "float";
			if (property.IndexOf('-') >= 0)
				return property;
			StringBuilder name = new StringBuilder(property.Length + 4);
			for (int i = 0; i < property.Length; i++)
			{
				char c = property[i];
				if (char.IsUpper(c))
				{
					if (i > 0 || property.StartsWith("webkit", StringComparison.Ordinal) || property.StartsWith("moz", StringComparison.Ordinal) || property.StartsWith("ms", StringComparison.Ordinal))
						name.Append('-');
					name.Append(char.ToLowerInvariant(c));
				}
				else
					name.Append(c);
			}
			string result = name.ToString();
			if (result.StartsWith("webkit-", StringComparison.Ordinal) || result.StartsWith("moz-", StringComparison.Ordinal) || result.StartsWith("ms-", StringComparison.Ordinal))
				result = "-" + result;
			return result;
		}

		// "color: red; width: 10px !important" into pairs, splitting only at semicolons outside quotes and parentheses
		public static List<KeyValuePair<string, string>> ParseDeclarations(string text)
		{
			List<KeyValuePair<string, string>> declarations = new List<KeyValuePair<string, string>>();
			int depth = 0;
			char quote = '\0';
			int start = 0;
			for (int i = 0; i <= text.Length; i++)
			{
				char c = i < text.Length ? text[i] : ';';
				if (quote != '\0')
				{
					if (c == quote)
						quote = '\0';
					continue;
				}
				if (c == '"' || c == '\'')
					quote = c;
				else if (c == '(')
					depth++;
				else if (c == ')')
					depth = Math.Max(0, depth - 1);
				else if (c == ';' && depth == 0)
				{
					string piece = text.Substring(start, i - start);
					start = i + 1;
					int colon = piece.IndexOf(':');
					if (colon <= 0)
						continue;
					string name = piece.Substring(0, colon).Trim().ToLowerInvariant();
					string value = piece.Substring(colon + 1).Trim();
					if (value.EndsWith("!important", StringComparison.OrdinalIgnoreCase))
						value = value.Substring(0, value.Length - 10).TrimEnd();
					if (name.Length == 0 || value.Length == 0)
						continue;
					int existing = declarations.FindIndex(d => d.Key == name);
					if (existing >= 0)
						declarations[existing] = new KeyValuePair<string, string>(name, value);
					else
						declarations.Add(new KeyValuePair<string, string>(name, value));
				}
			}
			return declarations;
		}

		public static string SerializeDeclarations(List<KeyValuePair<string, string>> declarations)
		{
			StringBuilder text = new StringBuilder();
			foreach (KeyValuePair<string, string> declaration in declarations)
				text.Append(declaration.Key).Append(": ").Append(declaration.Value).Append("; ");
			return text.ToString().TrimEnd();
		}

		internal JsStyle StyleOf(Element element)
		{
			if (!_styles.TryGetValue(element, out JsStyle style))
				_styles[element] = style = new JsStyle(_stylePrototype, this, element);
			return style;
		}

		internal JsTokenList TokenList(Element element, string attribute) => new JsTokenList(_tokenListPrototype, this, element, attribute);

		private void SetupStyleBindings()
		{
			_stylePrototype = NewPrototype(Interpreter.ObjectPrototype, "CSSStyleDeclaration");
			JsObject proto = _stylePrototype;
			Interpreter.DefineGetter(proto, "cssText", (i, t, a) => t.Object is JsStyle style ? SerializeDeclarations(style.Declarations) : "", (i, t, a) =>
			{
				if (t.Object is JsStyle style)
					style.SetCssText(Interpreter.Arg(a, 0).ToJsString());
				return JsValue.Undefined;
			});
			Interpreter.DefineGetter(proto, "length", (i, t, a) => t.Object is JsStyle style ? style.Declarations.Count : 0);
			Interpreter.DefineGetter(proto, "parentRule", (i, t, a) => JsValue.Null);
			Interpreter.DefineMethod(proto, "getPropertyValue", 1, (i, t, a) =>
			{
				string name = Interpreter.Arg(a, 0).ToJsString().Trim().ToLowerInvariant();
				return t.Object is JsStyle style ? style.Get(name) : t.Object is JsComputedStyle computed ? computed.Get(name) : "";
			});
			Interpreter.DefineMethod(proto, "getPropertyPriority", 1, (i, t, a) => "");
			Interpreter.DefineMethod(proto, "setProperty", 2, (i, t, a) =>
			{
				if (t.Object is JsStyle style)
					style.Set(Interpreter.Arg(a, 0).ToJsString().Trim().ToLowerInvariant(), Interpreter.Arg(a, 1).IsNullish ? "" : Interpreter.Arg(a, 1).ToJsString());
				return JsValue.Undefined;
			});
			Interpreter.DefineMethod(proto, "removeProperty", 1, (i, t, a) =>
			{
				if (t.Object is not JsStyle style)
					return "";
				string name = Interpreter.Arg(a, 0).ToJsString().Trim().ToLowerInvariant();
				string old = style.Get(name);
				style.Set(name, "");
				return old;
			});
			Interpreter.DefineMethod(proto, "item", 1, (i, t, a) =>
			{
				if (t.Object is not JsStyle style)
					return "";
				int index = Interpreter.Arg(a, 0).ToInt32();
				List<KeyValuePair<string, string>> declarations = style.Declarations;
				return index >= 0 && index < declarations.Count ? declarations[index].Key : "";
			});

			_tokenListPrototype = NewPrototype(Interpreter.ObjectPrototype, "DOMTokenList");
			JsObject tokens = _tokenListPrototype;
			Interpreter.DefineMethod(tokens, "contains", 1, (i, t, a) => ThisTokens(t).Tokens.Contains(Interpreter.Arg(a, 0).ToJsString()));
			Interpreter.DefineMethod(tokens, "add", 1, (i, t, a) =>
			{
				JsTokenList list = ThisTokens(t);
				List<string> current = list.Tokens;
				bool changed = false;
				foreach (JsValue token in a)
				{
					string text = token.ToJsString();
					if (text.Length == 0)
						throw DomException("SyntaxError", "The token provided must not be empty.");
					if (!current.Contains(text))
					{
						current.Add(text);
						changed = true;
					}
				}
				if (changed)
					list.Write(current);
				return JsValue.Undefined;
			});
			Interpreter.DefineMethod(tokens, "remove", 1, (i, t, a) =>
			{
				JsTokenList list = ThisTokens(t);
				List<string> current = list.Tokens;
				bool changed = false;
				foreach (JsValue token in a)
					changed |= current.Remove(token.ToJsString());
				if (changed)
					list.Write(current);
				return JsValue.Undefined;
			});
			Interpreter.DefineMethod(tokens, "toggle", 1, (i, t, a) =>
			{
				JsTokenList list = ThisTokens(t);
				List<string> current = list.Tokens;
				string token = Interpreter.Arg(a, 0).ToJsString();
				bool present = current.Contains(token);
				bool wanted = a.Length > 1 && !a[1].IsUndefined ? a[1].Truthy : !present;
				if (wanted && !present)
					current.Add(token);
				else if (!wanted && present)
					current.Remove(token);
				else
					return wanted;
				list.Write(current);
				return wanted;
			});
			Interpreter.DefineMethod(tokens, "replace", 2, (i, t, a) =>
			{
				JsTokenList list = ThisTokens(t);
				List<string> current = list.Tokens;
				int index = current.IndexOf(Interpreter.Arg(a, 0).ToJsString());
				if (index < 0)
					return false;
				current[index] = Interpreter.Arg(a, 1).ToJsString();
				list.Write(current);
				return true;
			});
			Interpreter.DefineMethod(tokens, "item", 1, (i, t, a) =>
			{
				List<string> current = ThisTokens(t).Tokens;
				int index = Interpreter.Arg(a, 0).ToInt32();
				return index >= 0 && index < current.Count ? current[index] : JsValue.Null;
			});
			Interpreter.DefineMethod(tokens, "supports", 1, (i, t, a) => true);
			Interpreter.DefineMethod(tokens, "toString", 0, (i, t, a) => ThisTokens(t).Element.GetAttribute(ThisTokens(t).Attribute) ?? "");
			Interpreter.DefineMethod(tokens, "forEach", 1, (i, t, a) =>
			{
				JsFunction callback = Interpreter.RequireCallable(Interpreter.Arg(a, 0), "forEach callback");
				List<string> current = ThisTokens(t).Tokens;
				for (int n = 0; n < current.Count; n++)
					Interpreter.Call(callback, Interpreter.Arg(a, 1), new JsValue[] { current[n], n, t });
				return JsValue.Undefined;
			});
			Interpreter.DefineMethod(tokens, "values", 0, (i, t, a) => Interpreter.Call(Interpreter.ArrayValues, Interpreter.NewStringArray(ThisTokens(t).Tokens), Array.Empty<JsValue>()));
			Interpreter.DefineMethod(tokens, "keys", 0, (i, t, a) => Interpreter.Call(Interpreter.ArrayPrototype.Get("keys", Interpreter), Interpreter.NewStringArray(ThisTokens(t).Tokens), Array.Empty<JsValue>()));
			Interpreter.DefineMethod(tokens, "entries", 0, (i, t, a) => Interpreter.Call(Interpreter.ArrayPrototype.Get("entries", Interpreter), Interpreter.NewStringArray(ThisTokens(t).Tokens), Array.Empty<JsValue>()));
			tokens.Put(Interpreter.SymbolIterator, tokens.Get("values", Interpreter), false);
		}

		private JsTokenList ThisTokens(JsValue thisValue)
		{
			if (thisValue.IsObject && thisValue.Object is JsTokenList list)
				return list;
			throw Interpreter.TypeError("Illegal invocation");
		}

		// Computed values, as text, from the style the cascade gave the element and the box layout gave it

		internal string ComputedValue(Element element, string property)
		{
			LayoutBox box = LayoutBoxOf(element);
			ComputedStyle style = box?.Style;
			if (style == null)
			{
				switch (property)
				{
					case "display":
						return "none";
					case "visibility":
						return "hidden";
					case "opacity":
						return "1";
					case "position":
						return "static";
					default:
						return property.EndsWith("color", StringComparison.Ordinal) ? "rgba(0, 0, 0, 0)" : property.EndsWith("width", StringComparison.Ordinal) || property.EndsWith("height", StringComparison.Ordinal) || property.StartsWith("margin", StringComparison.Ordinal) || property.StartsWith("padding", StringComparison.Ordinal) ? "0px" : "";
				}
			}
			switch (property)
			{
				case "display":
					return Kebab(style.Display.ToString());
				case "position":
					return Kebab(style.Position.ToString());
				case "float":
					return Kebab(style.Float.ToString());
				case "clear":
					return Kebab(style.Clear.ToString());
				case "overflow":
				case "overflow-x":
				case "overflow-y":
					return Kebab(style.Overflow.ToString());
				case "visibility":
					return style.Visible ? "visible" : "hidden";
				case "width":
					return Px(box.ContentWidth);
				case "height":
					return Px(box.ContentHeight);
				case "top":
					return style.Top.Unit == LengthUnit.Auto ? "auto" : Px(style.Top.Value);
				case "left":
					return style.Left.Unit == LengthUnit.Auto ? "auto" : Px(style.Left.Value);
				case "right":
				case "bottom":
					return "auto";
				case "min-width":
				case "min-height":
					return "0px";
				case "max-width":
				case "max-height":
					return "none";
				case "color":
					return ColorText(style.Color);
				case "background-color":
					return ColorText(style.BackgroundColor);
				case "background-image":
					return style.BackgroundImage == null ? "none" : "url(\"" + style.BackgroundImage + "\")";
				case "font-size":
					return Px(style.FontSize);
				case "font-weight":
					return style.Bold ? "700" : "400";
				case "font-style":
					return style.Italic ? "italic" : "normal";
				case "font-family":
					return style.FontFamily == FontFamily.Mono ? "monospace" : style.FontFamily == FontFamily.Serif ? "serif" : "sans-serif";
				case "line-height":
					return Px(style.ResolvedLineHeight);
				case "text-align":
					return Kebab(style.TextAlign.ToString());
				case "text-decoration":
				case "text-decoration-line":
					return style.Underline ? "underline" : style.LineThrough ? "line-through" : "none";
				case "text-transform":
					return Kebab(style.TextTransform.ToString());
				case "white-space":
					return Kebab(style.WhiteSpace.ToString());
				case "vertical-align":
					return Kebab(style.VerticalAlign.ToString());
				case "letter-spacing":
					return style.LetterSpacing == 0 ? "normal" : Px(style.LetterSpacing);
				case "opacity":
					return style.Opacity.ToString(CultureInfo.InvariantCulture);
				case "margin-top":
					return Px(box.MarginTop);
				case "margin-right":
					return Px(box.MarginRight);
				case "margin-bottom":
					return Px(box.MarginBottom);
				case "margin-left":
					return Px(box.MarginLeft);
				case "padding-top":
					return Px(box.PaddingTop);
				case "padding-right":
					return Px(box.PaddingRight);
				case "padding-bottom":
					return Px(box.PaddingBottom);
				case "padding-left":
					return Px(box.PaddingLeft);
				case "border-top-width":
					return Px(box.BorderTop);
				case "border-right-width":
					return Px(box.BorderRight);
				case "border-bottom-width":
					return Px(box.BorderBottom);
				case "border-left-width":
					return Px(box.BorderLeft);
				case "border-top-style":
					return Kebab(style.BorderTop.Style.ToString());
				case "border-top-color":
					return ColorText(style.BorderTop.Color);
				case "flex-direction":
					return Kebab(style.FlexDirection.ToString());
				case "flex-wrap":
					return style.FlexWrap ? "wrap" : "nowrap";
				case "justify-content":
					return Kebab(style.JustifyContent.ToString());
				case "align-items":
					return Kebab(style.AlignItems.ToString());
				case "flex-grow":
					return style.FlexGrow.ToString(CultureInfo.InvariantCulture);
				case "flex-shrink":
					return style.FlexShrink.ToString(CultureInfo.InvariantCulture);
				case "z-index":
					return "auto";
				case "transform":
				case "animation-name":
				case "transition-property":
				case "filter":
				case "box-shadow":
				case "text-shadow":
					return "none";
				case "transition-duration":
				case "animation-duration":
				case "transition-delay":
				case "animation-delay":
					return "0s";
				case "pointer-events":
				case "cursor":
					return "auto";
				case "box-sizing":
					return "content-box";
				case "direction":
					return "ltr";
				case "list-style-type":
					return Kebab(style.ListStyleType.ToString());
				case "content":
					return "normal";
				default:
					return "";
			}
		}

		private static string Px(float value) => (Math.Abs(value - MathF.Round(value)) < 0.01f ? ((int)MathF.Round(value)).ToString(CultureInfo.InvariantCulture) : value.ToString("0.##", CultureInfo.InvariantCulture)) + "px";

		private static string Kebab(string name)
		{
			StringBuilder text = new StringBuilder();
			for (int i = 0; i < name.Length; i++)
			{
				if (char.IsUpper(name[i]))
				{
					if (i > 0)
						text.Append('-');
					text.Append(char.ToLowerInvariant(name[i]));
				}
				else
					text.Append(name[i]);
			}
			return text.ToString();
		}

		private static string ColorText(Color color)
		{
			uint argb = color.Argb;
			int a = (int)(argb >> 24);
			int r = (int)(argb >> 16) & 255;
			int g = (int)(argb >> 8) & 255;
			int b = (int)argb & 255;
			if (a == 255)
				return "rgb(" + r + ", " + g + ", " + b + ")";
			return "rgba(" + r + ", " + g + ", " + b + ", " + (a / 255.0).ToString("0.##", CultureInfo.InvariantCulture) + ")";
		}
	}
}
