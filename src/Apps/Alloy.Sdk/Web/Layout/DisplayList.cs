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

		public DisplayItem(DisplayItemKind kind, float x, float y, float width, float height, Color color, string text = null, LayoutFont font = null, Alloy.Sdk.Web.Images.Bitmap image = null)
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

		public void Text(float x, float baseline, string text, LayoutFont font, Color color)
		{
			if (text.Length > 0 && color.A > 0)
				Items.Add(new DisplayItem(DisplayItemKind.Text, x, baseline, 0, 0, color, text, font));
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
			if (!isRoot && box.Kind != BoxKind.Text)
				PaintBackgroundAndBorders(list, box);
			if (box.Kind == BoxKind.Text)
				return;
			bool clip = style.Overflow != Overflow.Visible && box.Kind != BoxKind.Inline;
			if (clip)
				list.Items.Add(new DisplayItem(DisplayItemKind.Clip, box.X, box.Y, box.Width, box.Height, default));
			if (box.IsReplaced && box.Children.Count == 0 && box.TagName == "img" && box.Width > 0 && box.Height > 0 && style.Visible)
				list.Items.Add(new DisplayItem(DisplayItemKind.Image, box.ContentX, box.ContentY, box.ContentWidth, box.ContentHeight, style.Color, box.Alt, null, box.Image));
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
			if (box.Positioned != null)
			{
				foreach (LayoutBox child in box.Positioned)
					PaintBox(list, child, false);
			}
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
					list.Text(fragment.X, baseline, fragment.Text, fragment.Font, style.Color);
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
				if (other.Inline == fragment.Inline && other.Text.Length > 0)
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
		public static LayoutBox HitTest(LayoutBox box, float x, float y)
		{
			if (box.Positioned != null)
			{
				for (int i = box.Positioned.Count - 1; i >= 0; i--)
				{
					LayoutBox hit = HitTest(box.Positioned[i], x, y);
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
							LayoutBox inner = HitTest(fragment.Box, x, y);
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
				if (child.Kind == BoxKind.Text || child.Kind == BoxKind.Inline || child.Kind == BoxKind.LineBreak || child.Kind == BoxKind.InlineBlock)
					continue;
				LayoutBox hit = HitTest(child, x, y);
				if (hit != null)
					return hit;
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
