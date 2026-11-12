using System;
using System.Collections.Generic;
using Alloy.Sdk.Web.Css;
using Alloy.Sdk.Web.Html;

namespace Alloy.Sdk.Web.Layout
{
	public enum DisplayItemKind
	{
		// A filled rectangle in an ARGB colour
		Rect,
		// Text drawn with its baseline at Y
		Text,
		// Everything until the matching Unclip is cut to the rectangle
		Clip,
		Unclip,
		// An image: its bitmap scaled into the rectangle, or a placeholder with the alt text while there is none
		Image,
		// Everything until the matching Unfixed is drawn against the viewport, not the scrolled document
		Fixed,
		Unfixed,
	}

	// One drawing operation in document coordinates; a renderer turns the list into pixels wherever it likes
	public readonly struct DisplayItem
	{
		public readonly DisplayItemKind Kind;
		public readonly float X;
		public readonly float Y;
		public readonly float Width;
		public readonly float Height;
		public readonly Color Color;
		public readonly string Text;
		public readonly LayoutFont Font;
		public readonly Alloy.Sdk.Web.Images.Bitmap Image;
		// Extra advance after each character of a text item, the letter-spacing the layout measured with
		public readonly float Spacing;

		public DisplayItem(DisplayItemKind kind, float x, float y, float width, float height, Color color, string text = null, LayoutFont font = null, Alloy.Sdk.Web.Images.Bitmap image = null, float spacing = 0)
		{
			Kind = kind;
			X = x;
			Y = y;
			Width = width;
			Height = height;
			Color = color;
			Text = text;
			Font = font;
			Image = image;
			Spacing = spacing;
		}

		public override string ToString() => Kind switch
		{
			DisplayItemKind.Text => $"text \"{Text}\" at {X},{Y} {Color}",
			DisplayItemKind.Rect => $"rect {X},{Y} {Width}x{Height} {Color}",
			_ => $"{Kind} {X},{Y} {Width}x{Height}",
		};
	}

	public sealed class DisplayList
	{
		public readonly List<DisplayItem> Items = new List<DisplayItem>();
		public float Width;
		public float Height;

		public void Rect(float x, float y, float width, float height, Color color)
		{
			if (width > 0 && height > 0 && color.A > 0)
				Items.Add(new DisplayItem(DisplayItemKind.Rect, x, y, width, height, color));
		}

		public void Text(float x, float baseline, string text, LayoutFont font, Color color, float spacing = 0)
		{
			if (text.Length > 0 && color.A > 0)
				Items.Add(new DisplayItem(DisplayItemKind.Text, x, baseline, 0, 0, color, text, font, null, spacing));
		}
	}

	// Walks the laid-out tree in paint order and records what to draw: canvas, backgrounds and borders, text and decorations, placeholders
	public static class Painter
	{
		public static DisplayList Paint(LayoutBox root, float viewportWidth, float viewportHeight)
		{
			DisplayList list = new DisplayList { Width = Math.Max(viewportWidth, root.Width), Height = Math.Max(viewportHeight, root.Y + root.Height) };
			Color canvas = CanvasColor(root);
			list.Rect(0, 0, list.Width, list.Height, canvas.IsTransparent ? Color.White : canvas);
			PaintBox(list, root, true);
			return list;
		}

		// The page background comes from html, or from body when html has none, and covers the whole canvas
		private static Color CanvasColor(LayoutBox root)
		{
			if (!root.Style.BackgroundColor.IsTransparent)
				return root.Style.BackgroundColor;
			foreach (LayoutBox child in root.Children)
			{
				if (child.TagName == "body")
					return child.Style.BackgroundColor;
			}
			return Color.Transparent;
		}

		private static void PaintBox(DisplayList list, LayoutBox box, bool isRoot)
		{
			ComputedStyle style = box.Style;
			bool pinned = box.IsAbsolute && style.Position == Position.Fixed;
			if (pinned)
				list.Items.Add(new DisplayItem(DisplayItemKind.Fixed, 0, 0, 0, 0, default));
			if (!isRoot && box.Kind != BoxKind.Text)
				PaintBackgroundAndBorders(list, box);
			if (box.Kind == BoxKind.Text)
				return;
			List<LayoutBox> positioned = box.Positioned != null ? OrderedPositioned(box) : null;
			if (positioned != null)
			{
				// A negative z-index sits behind the container's own content but above its background
				foreach (LayoutBox child in positioned)
				{
					if (child.Style.ZIndex < 0)
						PaintBox(list, child, false);
				}
			}
			bool clip = style.Overflow != Overflow.Visible && box.Kind != BoxKind.Inline;
			if (clip)
				list.Items.Add(new DisplayItem(DisplayItemKind.Clip, box.X, box.Y, box.Width, box.Height, default));
			if (box.IsReplaced && box.Children.Count == 0 && box.TagName == "img" && box.Width > 0 && box.Height > 0 && style.Visible)
				list.Items.Add(new DisplayItem(DisplayItemKind.Image, box.ContentX, box.ContentY, box.ContentWidth, box.ContentHeight, style.Color, box.Alt, null, box.Image));
			// An inline svg is rasterized at its box size, with currentColor being the element's colour
			if (box.TagName == "svg" && box.Element != null && box.ContentWidth >= 1 && box.ContentHeight >= 1 && style.Visible)
				list.Items.Add(new DisplayItem(DisplayItemKind.Image, box.ContentX, box.ContentY, box.ContentWidth, box.ContentHeight, style.Color, null, null, Alloy.Sdk.Web.Images.Svg.Cached(box.Element, (int)MathF.Round(box.ContentWidth), (int)MathF.Round(box.ContentHeight), style.Color, style.Rotate)));
			if (box.Marker != null && style.Visible)
				PaintMarker(list, box);
			if (box.Lines != null)
				PaintLines(list, box);
			foreach (LayoutBox child in box.Children)
			{
				if (!child.IsOutOfFlow && child.Kind != BoxKind.Inline && child.Kind != BoxKind.InlineBlock && child.Kind != BoxKind.Text && child.Kind != BoxKind.LineBreak)
					PaintBox(list, child, false);
			}
			foreach (LayoutBox child in box.Children)
			{
				if (child.IsFloat)
					PaintBox(list, child, false);
			}
			if (box.Lines != null)
				PaintAtomics(list, box);
			if (clip)
				list.Items.Add(new DisplayItem(DisplayItemKind.Unclip, 0, 0, 0, 0, default));
			if (positioned != null)
			{
				foreach (LayoutBox child in positioned)
				{
					if (child.Style.ZIndex >= 0)
						PaintBox(list, child, false);
				}
			}
			if (pinned)
				list.Items.Add(new DisplayItem(DisplayItemKind.Unfixed, 0, 0, 0, 0, default));
		}

		// The positioned boxes a container holds, lowest z-index first and document order within a level, as they paint
		internal static List<LayoutBox> OrderedPositioned(LayoutBox box)
		{
			List<LayoutBox> ordered = new List<LayoutBox>(box.Positioned.Count);
			foreach (LayoutBox child in box.Positioned)
			{
				int z = child.Style?.ZIndex ?? 0;
				int at = ordered.Count;
				while (at > 0 && (ordered[at - 1].Style?.ZIndex ?? 0) > z)
					at--;
				ordered.Insert(at, child);
			}
			return ordered;
		}

		private static void PaintBackgroundAndBorders(DisplayList list, LayoutBox box)
		{
			ComputedStyle style = box.Style;
			if (!style.Visible || box.Kind == BoxKind.Inline)
				return;
			if (!style.BackgroundColor.IsTransparent)
				list.Rect(box.X, box.Y, box.Width, box.Height, style.BackgroundColor);
			PaintBorders(list, box.X, box.Y, box.Width, box.Height, style, box.BorderTop, box.BorderRight, box.BorderBottom, box.BorderLeft);
		}

		private static void PaintBorders(DisplayList list, float x, float y, float width, float height, ComputedStyle style, float top, float right, float bottom, float left)
		{
			if (top > 0)
				list.Rect(x, y, width, top, BorderColor(style.BorderTop, style, true));
			if (bottom > 0)
				list.Rect(x, y + height - bottom, width, bottom, BorderColor(style.BorderBottom, style, false));
			if (left > 0)
				list.Rect(x, y, left, height, BorderColor(style.BorderLeft, style, true));
			if (right > 0)
				list.Rect(x + width - right, y, right, height, BorderColor(style.BorderRight, style, false));
		}

		// Inset and outset borders get the light and dark shades browsers give them; everything else draws solid
		private static Color BorderColor(BorderEdge edge, ComputedStyle style, bool topOrLeft)
		{
			Color color = edge.Color.A == 0 && edge.Style != BorderStyle.None ? style.Color : edge.Color;
			bool lighten = edge.Style == BorderStyle.Inset || edge.Style == BorderStyle.Groove ? !topOrLeft : edge.Style == BorderStyle.Outset || edge.Style == BorderStyle.Ridge ? topOrLeft : false;
			bool darken = edge.Style == BorderStyle.Inset || edge.Style == BorderStyle.Groove ? topOrLeft : edge.Style == BorderStyle.Outset || edge.Style == BorderStyle.Ridge ? !topOrLeft : false;
			if (lighten)
				return Color.FromRgb(Math.Min(255, color.R / 2 + 128), Math.Min(255, color.G / 2 + 128), Math.Min(255, color.B / 2 + 128), color.A);
			if (darken)
				return Color.FromRgb(color.R / 2, color.G / 2, color.B / 2, color.A);
			return color;
		}

		private static void PaintMarker(DisplayList list, LayoutBox box)
		{
			LineBox line = FirstLine(box);
			LineFragment first = line != null && line.Fragments.Count > 0 ? line.Fragments[0] : null;
			LayoutFont font = first?.Font;
			if (font == null)
			{
				foreach (DisplayItem item in list.Items)
				{
					if (item.Font != null)
						font = item.Font;
				}
			}
			if (font == null)
				return;
			float width = font.Measure(box.Marker);
			float gap = font.Size * 0.5f;
			float baseline = line != null ? line.Y + line.Baseline : box.ContentY + font.Ascent;
			list.Text(box.ContentX - gap - width, baseline, box.Marker, font, box.Style.Color);
		}

		private static LineBox FirstLine(LayoutBox box)
		{
			if (box.Lines != null && box.Lines.Count > 0)
				return box.Lines[0];
			foreach (LayoutBox child in box.Children)
			{
				if (child.IsOutOfFlow)
					continue;
				LineBox line = FirstLine(child);
				if (line != null)
					return line;
			}
			return null;
		}

		private static void PaintLines(DisplayList list, LayoutBox box)
		{
			foreach (LineBox line in box.Lines)
			{
				foreach (LineFragment fragment in line.Fragments)
				{
					if (fragment.IsAtomic)
						continue;
					ComputedStyle style = fragment.Style ?? box.Style;
					if (!style.Visible)
						continue;
					PaintInlineBackground(list, fragment, line);
					if (fragment.Text.Length == 0)
						continue;
					float baseline = fragment.Y + fragment.Baseline;
					list.Text(fragment.X, baseline, fragment.Text, fragment.Font, style.Color, style.LetterSpacing);
					float thickness = Math.Max(1, MathF.Round(fragment.Font.Size / 14));
					if (style.Underline)
						list.Rect(fragment.X, baseline + Math.Max(1, fragment.Font.Descent / 2), fragment.Width, thickness, style.Color);
					if (style.LineThrough)
						list.Rect(fragment.X, baseline - fragment.Font.XHeight / 2, fragment.Width, thickness, style.Color);
				}
			}
		}

		// An inline element's background and top and bottom borders, drawn behind each of its fragments
		private static void PaintInlineBackground(DisplayList list, LineFragment fragment, LineBox line)
		{
			LayoutBox inline = fragment.Inline;
			if (inline == null)
				return;
			ComputedStyle style = inline.Style;
			if (style.BackgroundColor.IsTransparent && inline.BorderTop == 0 && inline.BorderBottom == 0 && inline.BorderLeft == 0 && inline.BorderRight == 0)
				return;
			LayoutFont font = fragment.Font;
			float top = fragment.Y + (fragment.Height - (font.Ascent + font.Descent)) / 2 - inline.PaddingTop - inline.BorderTop;
			float height = font.Ascent + font.Descent + inline.PaddingTop + inline.PaddingBottom + inline.BorderTop + inline.BorderBottom;
			bool opening = fragment.Box == inline && fragment.Text.Length == 0 && IsOpeningEdge(fragment, line);
			bool closing = fragment.Box == inline && fragment.Text.Length == 0 && !opening;
			float x = fragment.X;
			float width = fragment.Width;
			if (opening)
				x += inline.MarginLeft;
			if (!style.BackgroundColor.IsTransparent)
				list.Rect(x, top, Math.Max(0, width - (opening ? inline.MarginLeft : 0) - (closing ? inline.MarginRight : 0)), height, style.BackgroundColor);
			PaintBorders(list, x, top, Math.Max(0, width - (opening ? inline.MarginLeft : 0) - (closing ? inline.MarginRight : 0)), height, style, inline.BorderTop, closing ? inline.BorderRight : 0, inline.BorderBottom, opening ? inline.BorderLeft : 0);
		}

		private static bool IsOpeningEdge(LineFragment fragment, LineBox line)
		{
			foreach (LineFragment other in line.Fragments)
			{
				if (other == fragment)
					return true;
				if (other.Inline == fragment.Inline && !other.IsAtomic && other.Text != null && other.Text.Length > 0)
					return false;
			}
			return true;
		}

		private static void PaintAtomics(DisplayList list, LayoutBox box)
		{
			foreach (LineBox line in box.Lines)
			{
				foreach (LineFragment fragment in line.Fragments)
				{
					if (fragment.IsAtomic)
						PaintBox(list, fragment.Box, false);
				}
			}
		}

		// The deepest box under a document point, text fragments first, so a click finds the link it landed on
		public static LayoutBox HitTest(LayoutBox box, float x, float y) => HitTest(box, x, y, 0, 0);

		// With the scroll the document point includes, so a fixed box is tested where it shows rather than where it was laid out
		public static LayoutBox HitTest(LayoutBox box, float x, float y, float scrollX, float scrollY)
		{
			if ((scrollX != 0 || scrollY != 0) && box.IsAbsolute && box.Style.Position == Position.Fixed)
			{
				x -= scrollX;
				y -= scrollY;
				scrollX = 0;
				scrollY = 0;
			}
			List<LayoutBox> ordered = box.Positioned != null ? OrderedPositioned(box) : null;
			if (ordered != null)
			{
				for (int i = ordered.Count - 1; i >= 0; i--)
				{
					if (ordered[i].Style.ZIndex < 0)
						break;
					LayoutBox hit = HitTest(ordered[i], x, y, scrollX, scrollY);
					if (hit != null)
						return hit;
				}
			}
			if (box.Lines != null)
			{
				foreach (LineBox line in box.Lines)
				{
					if (y < line.Y || y >= line.Y + line.Height)
						continue;
					foreach (LineFragment fragment in line.Fragments)
					{
						if (x < fragment.X || x >= fragment.X + fragment.Width)
							continue;
						if (fragment.IsAtomic)
						{
							LayoutBox inner = HitTest(fragment.Box, x, y, scrollX, scrollY);
							return inner ?? fragment.Box;
						}
						if (fragment.Text.Length > 0 && y >= fragment.Y && y < fragment.Y + fragment.Height)
							return fragment.Box;
					}
				}
			}
			for (int i = box.Children.Count - 1; i >= 0; i--)
			{
				LayoutBox child = box.Children[i];
				if (child.Kind == BoxKind.Text || child.Kind == BoxKind.Inline || child.Kind == BoxKind.LineBreak || child.Kind == BoxKind.InlineBlock || child.IsAbsolute)
					continue;
				LayoutBox hit = HitTest(child, x, y, scrollX, scrollY);
				if (hit != null)
					return hit;
			}
			if (ordered != null)
			{
				for (int i = ordered.Count - 1; i >= 0; i--)
				{
					if (ordered[i].Style.ZIndex >= 0)
						continue;
					LayoutBox hit = HitTest(ordered[i], x, y, scrollX, scrollY);
					if (hit != null)
						return hit;
				}
			}
			return box.ContainsPoint(x, y) ? box : null;
		}

		// The link an element is inside, if any
		public static Element LinkFor(Node node)
		{
			while (node != null)
			{
				if (node is Element element && element.TagName == "a" && element.HasAttribute("href"))
					return element;
				node = node.Parent;
			}
			return null;
		}
	}
}
