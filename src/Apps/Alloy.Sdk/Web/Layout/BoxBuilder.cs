using System;
using System.Collections.Generic;
using System.Text;
using Alloy.Sdk.Web.Css;
using Alloy.Sdk.Web.Html;

namespace Alloy.Sdk.Web.Layout
{
	// Turns the DOM and its computed styles into the layout tree: display none dropped, inline runs wrapped in anonymous blocks, tables normalized, text whitespace processed
	public sealed class BoxBuilder
	{
		private readonly StyleResolver _styles;
		private readonly Func<Element, Alloy.Sdk.Web.Images.Bitmap> _images;

		public BoxBuilder(StyleResolver styles, Func<Element, Alloy.Sdk.Web.Images.Bitmap> images = null)
		{
			_styles = styles;
			_images = images;
		}

		public LayoutBox Build(Document document)
		{
			Element root = document.DocumentElement;
			if (root == null)
				return new LayoutBox(BoxKind.Block, document, new ComputedStyle { Display = Display.Block });
			ComputedStyle style = _styles.Resolve(root);
			if (style.Display == Display.None || style.Display == Display.Inline)
				style.Display = Display.Block;
			LayoutBox box = new LayoutBox(BoxKind.Block, root, style);
			BuildChildren(box, root, style);
			return box;
		}

		private void BuildChildren(LayoutBox parent, Element element, ComputedStyle parentStyle)
		{
			bool preserveWhitespace = parentStyle.WhiteSpace == WhiteSpace.Pre || parentStyle.WhiteSpace == WhiteSpace.PreWrap || parentStyle.WhiteSpace == WhiteSpace.PreLine;
			foreach (Node node in RenderedChildren(element))
			{
				if (node is TextNode text)
				{
					if (text.Data.Length == 0)
						continue;
					bool allWhitespace = IsAllWhitespace(text.Data);
					if (allWhitespace && !preserveWhitespace && parent.IsBlockLevel && parent.Children.Count == 0)
						continue;
					if (allWhitespace && (parent.Kind == BoxKind.Table || parent.Kind == BoxKind.TableRow))
						continue;
					parent.Add(new LayoutBox(BoxKind.Text, text, parentStyle) { Text = text.Data });
					continue;
				}
				if (node is not Element child)
					continue;
				ComputedStyle style = _styles.Resolve(child);
				if (style.Display == Display.None)
					continue;
				LayoutBox box = CreateBox(child, style, parent, _images);
				if (box == null)
					continue;
				parent.Add(box);
				if (!IsLeaf(child.TagName))
					BuildChildren(box, child, style);
			}
			Normalize(parent);
		}

		// What an element shows: its shadow tree when it has one, a slot's assigned light children, otherwise its own children
		private static IEnumerable<Node> RenderedChildren(Element element)
		{
			if (element.ShadowRoot != null)
				return element.ShadowRoot.Children;
			if (element.TagName == "slot")
			{
				Element host = element.ShadowHost;
				if (host != null)
				{
					List<Node> assigned = host.AssignedTo(element.GetAttribute("name"));
					if (assigned.Count > 0)
						return assigned;
				}
			}
			return element.Children;
		}

		// Elements whose DOM children never become boxes: replaced elements and the form controls drawn as widgets
		private static bool IsLeaf(string tag) => tag is "br" or "img" or "input" or "select" or "textarea" or "iframe" or "video" or "canvas" or "embed" or "object" or "svg" or "audio";

		private static LayoutBox CreateBox(Element element, ComputedStyle style, LayoutBox parent, Func<Element, Alloy.Sdk.Web.Images.Bitmap> images)
		{
			string tag = element.TagName;
			switch (tag)
			{
				case "br":
					return new LayoutBox(BoxKind.LineBreak, element, style);
				case "img":
				{
					string alt = element.GetAttribute("alt") ?? "";
					Alloy.Sdk.Web.Images.Bitmap bitmap = images?.Invoke(element);
					bool sized = !style.Width.IsAuto || !style.Height.IsAuto;
					if (bitmap == null && !sized && alt.Length > 0)
					{
						// No size and no picture: the alt text stands in, inline, as browsers show it
						LayoutBox text = new LayoutBox(BoxKind.Inline, element, style);
						text.Add(new LayoutBox(BoxKind.Text, element, style) { Text = alt });
						return text;
					}
					LayoutBox image = new LayoutBox(BoxKind.InlineBlock, element, style) { Alt = alt, Image = bitmap, IntrinsicWidth = bitmap?.Width ?? 0, IntrinsicHeight = bitmap?.Height ?? 0 };
					if (style.Display == Display.Block || style.Display == Display.ListItem || style.Display == Display.Flex || style.Display == Display.Grid)
						image.Kind = BoxKind.Block;
					return image;
				}
				case "input":
				{
					string type = (element.GetAttribute("type") ?? "text").ToLowerInvariant();
					if (type == "hidden")
						return null;
					LayoutBox input = new LayoutBox(BoxKind.InlineBlock, element, style);
					if (type == "submit" || type == "button" || type == "reset")
						input.Add(new LayoutBox(BoxKind.Text, element, style) { Text = element.GetAttribute("value") ?? (type == "reset" ? "Reset" : "Submit") });
					else if (type == "checkbox" || type == "radio")
					{
						input.IntrinsicWidth = 13;
						input.IntrinsicHeight = 13;
					}
					else
					{
						int size = int.TryParse(element.GetAttribute("size"), out int s) && s > 0 ? s : 20;
						input.IntrinsicWidth = size * style.FontSize * 0.55f;
						input.IntrinsicHeight = style.FontSize * 1.3f;
						style.WhiteSpace = WhiteSpace.NoWrap;
						style.Overflow = Overflow.Hidden;
						string value = element.GetAttribute("value");
						if (!string.IsNullOrEmpty(value))
							input.Add(new LayoutBox(BoxKind.Text, element, style) { Text = type == "password" ? new string('\u2022', value.Length) : value });
						else if (!string.IsNullOrEmpty(element.GetAttribute("placeholder")))
						{
							ComputedStyle hint = style.Clone();
							hint.Color = Color.FromRgb(0x75, 0x75, 0x75);
							input.Add(new LayoutBox(BoxKind.Text, element, hint) { Text = element.GetAttribute("placeholder") });
						}
					}
					return input;
				}
				case "select":
				{
					LayoutBox select = new LayoutBox(BoxKind.InlineBlock, element, style);
					string first = null;
					foreach (Element option in element.ElementsByTagName("option"))
					{
						if (first == null || option.HasAttribute("selected"))
							first = option.TextContent.Trim();
						if (option.HasAttribute("selected"))
							break;
					}
					select.Add(new LayoutBox(BoxKind.Text, element, style) { Text = (first ?? "") + " ▾" });
					return select;
				}
				case "textarea":
				{
					LayoutBox area = new LayoutBox(BoxKind.InlineBlock, element, style);
					int cols = int.TryParse(element.GetAttribute("cols"), out int c) && c > 0 ? c : 20;
					int rows = int.TryParse(element.GetAttribute("rows"), out int r) && r > 0 ? r : 2;
					area.IntrinsicWidth = cols * style.FontSize * 0.55f;
					area.IntrinsicHeight = rows * style.ResolvedLineHeight;
					style.WhiteSpace = WhiteSpace.PreWrap;
					style.Overflow = Overflow.Hidden;
					string text = element.GetAttribute("value") ?? element.TextContent;
					if (!string.IsNullOrEmpty(text))
						area.Add(new LayoutBox(BoxKind.Text, element, style) { Text = text });
					return area;
				}
				case "iframe":
				case "video":
				case "canvas":
				case "embed":
				case "object":
				case "svg":
				case "audio":
				{
					LayoutBox frame = new LayoutBox(BoxKind.InlineBlock, element, style) { IntrinsicWidth = tag == "audio" ? 0 : 300, IntrinsicHeight = tag == "audio" ? 0 : 150 };
					return frame;
				}
			}
			switch (style.Display)
			{
				case Display.Inline:
					return new LayoutBox(BoxKind.Inline, element, style);
				case Display.InlineBlock:
				case Display.InlineFlex:
				case Display.InlineGrid:
					return new LayoutBox(BoxKind.InlineBlock, element, style);
				case Display.Table:
					return new LayoutBox(BoxKind.Table, element, style);
				case Display.TableRow:
					return new LayoutBox(BoxKind.TableRow, element, style);
				case Display.TableCell:
				{
					LayoutBox cell = new LayoutBox(BoxKind.TableCell, element, style);
					if (int.TryParse(element.GetAttribute("colspan"), out int colspan) && colspan > 1)
						cell.ColumnSpan = Math.Min(colspan, 100);
					if (int.TryParse(element.GetAttribute("rowspan"), out int rowspan) && rowspan > 1)
						cell.RowSpan = Math.Min(rowspan, 100);
					return cell;
				}
				case Display.TableRowGroup:
				case Display.TableHeaderGroup:
				case Display.TableFooterGroup:
					return new LayoutBox(BoxKind.Block, element, style) { Kind = BoxKind.Block };
				case Display.TableColumn:
				case Display.TableColumnGroup:
					return null;
				case Display.TableCaption:
					return new LayoutBox(BoxKind.Block, element, style);
				default:
					return new LayoutBox(BoxKind.Block, element, style);
			}
		}

		// Fixes the children of a box up so layout sees one of the shapes it handles
		private static void Normalize(LayoutBox box)
		{
			if (box.Style.Display is Display.TableRowGroup or Display.TableHeaderGroup or Display.TableFooterGroup)
				return;
			if (box.Kind == BoxKind.Table)
			{
				NormalizeTable(box);
				return;
			}
			if (box.Kind == BoxKind.TableRow)
			{
				WrapLooseCells(box);
				return;
			}
			if ((box.Style.IsFlexContainer || box.Style.IsGridContainer) && box.Kind != BoxKind.Inline)
			{
				BlockifyFlexItems(box);
				return;
			}
			if (box.Kind == BoxKind.Inline)
			{
				// Blocks inside inlines: treated as inline-blocks so the line keeps flowing, which is close enough to the split browsers do
				foreach (LayoutBox child in box.Children)
				{
					if (child.Kind == BoxKind.Block || child.Kind == BoxKind.AnonymousBlock)
						child.Kind = BoxKind.InlineBlock;
				}
				return;
			}
			bool hasBlock = false;
			bool hasInline = false;
			foreach (LayoutBox child in box.Children)
			{
				if (child.IsOutOfFlow)
					continue;
				if (child.Kind == BoxKind.TableRow || child.Kind == BoxKind.TableCell)
					child.Kind = BoxKind.Block;
				if (child.IsBlockLevel)
					hasBlock = true;
				else
					hasInline = true;
			}
			if (!hasBlock || !hasInline)
				return;
			List<LayoutBox> children = new List<LayoutBox>(box.Children);
			box.Children.Clear();
			LayoutBox anonymous = null;
			foreach (LayoutBox child in children)
			{
				if (child.IsBlockLevel && !child.IsOutOfFlow)
				{
					anonymous = null;
					box.Add(child);
					continue;
				}
				if (child.Kind == BoxKind.Text && IsAllWhitespace(child.Text) && anonymous == null)
					continue;
				if (anonymous == null)
				{
					anonymous = new LayoutBox(BoxKind.AnonymousBlock, null, ComputedStyle.Inherit(box.Style));
					anonymous.Style.Display = Display.Block;
					box.Add(anonymous);
				}
				anonymous.Add(child);
			}
			foreach (LayoutBox child in box.Children)
			{
				if (child.Kind == BoxKind.AnonymousBlock)
					TrimTrailingWhitespace(child);
			}
		}

		// Every child of a flex container is an item: inline elements become blocks, runs of text get an anonymous block each, whitespace alone is dropped
		private static void BlockifyFlexItems(LayoutBox box)
		{
			List<LayoutBox> children = new List<LayoutBox>(box.Children);
			box.Children.Clear();
			LayoutBox anonymous = null;
			foreach (LayoutBox child in children)
			{
				if (child.Kind == BoxKind.Text)
				{
					if (IsAllWhitespace(child.Text))
						continue;
					if (anonymous == null)
					{
						anonymous = new LayoutBox(BoxKind.AnonymousBlock, null, ComputedStyle.Inherit(box.Style));
						anonymous.Style.Display = Display.Block;
						box.Add(anonymous);
					}
					anonymous.Add(child);
					continue;
				}
				anonymous = null;
				if (child.Kind == BoxKind.LineBreak)
					continue;
				if (child.Kind == BoxKind.Inline)
				{
					child.Kind = BoxKind.Block;
					child.Style.Display = Display.Block;
				}
				else if (child.Kind == BoxKind.InlineBlock && !child.IsReplaced)
				{
					child.Kind = BoxKind.Block;
					if (child.Style.Display == Display.InlineBlock)
						child.Style.Display = Display.Block;
					else if (child.Style.Display == Display.InlineFlex)
						child.Style.Display = Display.Flex;
					else if (child.Style.Display == Display.InlineGrid)
						child.Style.Display = Display.Grid;
				}
				else if (child.Kind == BoxKind.InlineBlock)
					child.Kind = BoxKind.Block;
				else if (child.Kind == BoxKind.TableRow || child.Kind == BoxKind.TableCell)
					child.Kind = BoxKind.Block;
				if (child.Style.Float != FloatSide.None)
					child.Style.Float = FloatSide.None;
				box.Add(child);
			}
		}

		private static void TrimTrailingWhitespace(LayoutBox anonymous)
		{
			while (anonymous.Children.Count > 0)
			{
				LayoutBox last = anonymous.Children[anonymous.Children.Count - 1];
				if (last.Kind == BoxKind.Text && IsAllWhitespace(last.Text) && last.Style.WhiteSpace == WhiteSpace.Normal)
					anonymous.Children.RemoveAt(anonymous.Children.Count - 1);
				else
					break;
			}
		}

		// A table's children become rows (row groups flattened, loose cells wrapped), with captions kept in front
		private static void NormalizeTable(LayoutBox table)
		{
			List<LayoutBox> rows = new List<LayoutBox>();
			List<LayoutBox> captions = new List<LayoutBox>();
			LayoutBox looseRow = null;
			foreach (LayoutBox child in table.Children)
			{
				if (child.Kind == BoxKind.TableRow)
				{
					looseRow = null;
					rows.Add(child);
				}
				else if (child.Kind == BoxKind.Block && child.Style.Display is Display.TableRowGroup or Display.TableHeaderGroup or Display.TableFooterGroup)
				{
					looseRow = null;
					foreach (LayoutBox row in child.Children)
					{
						if (row.Kind == BoxKind.TableRow)
							rows.Add(row);
						else if (row.Kind == BoxKind.TableCell)
						{
							LayoutBox wrapper = new LayoutBox(BoxKind.TableRow, null, ComputedStyle.Inherit(table.Style));
							wrapper.Add(row);
							rows.Add(wrapper);
						}
					}
				}
				else if (child.Kind == BoxKind.Block && child.Style.Display == Display.TableCaption)
					captions.Add(child);
				else if (child.Kind == BoxKind.Text && IsAllWhitespace(child.Text))
					continue;
				else
				{
					if (looseRow == null)
					{
						looseRow = new LayoutBox(BoxKind.TableRow, null, ComputedStyle.Inherit(table.Style));
						rows.Add(looseRow);
					}
					if (child.Kind != BoxKind.TableCell)
					{
						LayoutBox cell = new LayoutBox(BoxKind.TableCell, null, ComputedStyle.Inherit(table.Style));
						cell.Add(child);
						looseRow.Add(cell);
					}
					else
						looseRow.Add(child);
				}
			}
			table.Children.Clear();
			foreach (LayoutBox caption in captions)
				table.Add(caption);
			foreach (LayoutBox row in rows)
			{
				WrapLooseCells(row);
				table.Add(row);
			}
		}

		private static void WrapLooseCells(LayoutBox row)
		{
			bool loose = false;
			foreach (LayoutBox child in row.Children)
				loose |= child.Kind != BoxKind.TableCell;
			if (!loose)
				return;
			List<LayoutBox> children = new List<LayoutBox>(row.Children);
			row.Children.Clear();
			LayoutBox cell = null;
			foreach (LayoutBox child in children)
			{
				if (child.Kind == BoxKind.TableCell)
				{
					cell = null;
					row.Add(child);
					continue;
				}
				if (child.Kind == BoxKind.Text && IsAllWhitespace(child.Text))
					continue;
				if (cell == null)
				{
					cell = new LayoutBox(BoxKind.TableCell, null, ComputedStyle.Inherit(row.Style));
					row.Add(cell);
				}
				cell.Add(child);
			}
		}

		public static bool IsAllWhitespace(string text)
		{
			foreach (char c in text)
			{
				if (!HtmlText.IsWhitespace(c))
					return false;
			}
			return true;
		}
	}

	// The marker text for a list item: bullets, numbers, letters and roman numerals
	public static class ListMarkers
	{
		public static string Text(ListStyleType type, int index)
		{
			switch (type)
			{
				case ListStyleType.None:
					return "";
				case ListStyleType.Disc:
					return "•";
				case ListStyleType.Circle:
					return "◦";
				case ListStyleType.Square:
					return "▪";
				case ListStyleType.Decimal:
					return index + ".";
				case ListStyleType.LowerAlpha:
					return Alpha(index, 'a') + ".";
				case ListStyleType.UpperAlpha:
					return Alpha(index, 'A') + ".";
				case ListStyleType.LowerRoman:
					return Roman(index).ToLowerInvariant() + ".";
				case ListStyleType.UpperRoman:
					return Roman(index) + ".";
				default:
					return "•";
			}
		}

		private static string Alpha(int index, char first)
		{
			if (index < 1)
				return index.ToString();
			StringBuilder text = new StringBuilder();
			while (index > 0)
			{
				index--;
				text.Insert(0, (char)(first + index % 26));
				index /= 26;
			}
			return text.ToString();
		}

		private static readonly (int Value, string Symbol)[] s_roman = { (1000, "M"), (900, "CM"), (500, "D"), (400, "CD"), (100, "C"), (90, "XC"), (50, "L"), (40, "XL"), (10, "X"), (9, "IX"), (5, "V"), (4, "IV"), (1, "I") };

		private static string Roman(int index)
		{
			if (index < 1 || index > 3999)
				return index.ToString();
			StringBuilder text = new StringBuilder();
			foreach ((int value, string symbol) in s_roman)
			{
				while (index >= value)
				{
					text.Append(symbol);
					index -= value;
				}
			}
			return text.ToString();
		}
	}
}
