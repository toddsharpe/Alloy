using System;
using System.Collections.Generic;
using Alloy.Sdk.Web.Css;
using Alloy.Sdk.Web.Html;

namespace Alloy.Sdk.Web.Layout
{
	public enum BoxKind
	{
		// A block container: lays its children out vertically, or as lines when they are inline
		Block,
		// An anonymous block wrapping a run of inline children that sit beside block siblings
		AnonymousBlock,
		// An inline element: a span in the line, carrying style for its descendants
		Inline,
		// An atomic inline: inline-block or a replaced element, laid out as a block and placed in a line
		InlineBlock,
		// A run of text from one text node, whitespace already processed
		Text,
		// A br
		LineBreak,
		// A table, its rows and its cells; row groups are flattened away
		Table,
		TableRow,
		TableCell,
	}

	// A box in the layout tree: an element's or an anonymous one, with the geometry layout gives it in document pixels
	public sealed class LayoutBox
	{
		public BoxKind Kind;
		public Node Node;
		public ComputedStyle Style;
		public LayoutBox Parent;
		public readonly List<LayoutBox> Children = new List<LayoutBox>();

		// Text boxes
		public string Text;

		// Border box position and size, in document coordinates
		public float X;
		public float Y;
		public float Width;
		public float Height;

		// Resolved edges in pixels
		public float MarginTop;
		public float MarginRight;
		public float MarginBottom;
		public float MarginLeft;
		public float PaddingTop;
		public float PaddingRight;
		public float PaddingBottom;
		public float PaddingLeft;
		public float BorderTop;
		public float BorderRight;
		public float BorderBottom;
		public float BorderLeft;

		// The lines of a block container with inline content
		public List<LineBox> Lines;

		// A list item's marker text, drawn before its first line
		public string Marker;

		// Replaced elements: the size the element itself asks for, before CSS
		public float IntrinsicWidth = -1;
		public float IntrinsicHeight = -1;
		public string Alt;

		// Table cells
		public int ColumnSpan = 1;
		public int RowSpan = 1;

		// The baseline of the first line (atomic inlines sit on it), from the top of the border box; -1 when there is none
		public float Baseline = -1;

		// Out-of-flow descendants a block positions after its own layout
		public List<LayoutBox> Positioned;

		// Intrinsic content widths once measured; -1 until then
		public float MinContent = -1;
		public float MaxContent = -1;

		// A last child's bottom margin that collapsed through this box's bottom, for the parent to collapse with this box's own
		public float LeakedBottomMargin;

		// Where an absolutely positioned box would have been in the flow, for auto offsets
		public float StaticX;
		public float StaticY;

		public Element Element => Node as Element;

		public string TagName => (Node as Element)?.TagName;

		public float ContentX => X + BorderLeft + PaddingLeft;

		public float ContentY => Y + BorderTop + PaddingTop;

		public float ContentWidth => Width - BorderLeft - PaddingLeft - BorderRight - PaddingRight;

		public float ContentHeight => Height - BorderTop - PaddingTop - BorderBottom - PaddingBottom;

		public float MarginBoxWidth => Width + MarginLeft + MarginRight;

		public float MarginBoxHeight => Height + MarginTop + MarginBottom;

		public float HorizontalEdges => BorderLeft + PaddingLeft + BorderRight + PaddingRight;

		public float VerticalEdges => BorderTop + PaddingTop + BorderBottom + PaddingBottom;

		public bool IsBlockLevel => Kind == BoxKind.Block || Kind == BoxKind.AnonymousBlock || Kind == BoxKind.Table;

		public bool IsInlineLevel => Kind == BoxKind.Inline || Kind == BoxKind.InlineBlock || Kind == BoxKind.Text || Kind == BoxKind.LineBreak;

		public bool IsReplaced => IntrinsicWidth >= 0 || IntrinsicHeight >= 0 || TagName == "img";

		public bool IsFloat => Style != null && Style.Float != FloatSide.None && Kind != BoxKind.Inline && Kind != BoxKind.Text;

		public bool IsOutOfFlow => IsFloat || IsAbsolute;

		public bool IsAbsolute => Style != null && (Style.Position == Position.Absolute || Style.Position == Position.Fixed) && Kind != BoxKind.Text && Kind != BoxKind.Inline;

		public bool ContainsPoint(float x, float y) => x >= X && x < X + Width && y >= Y && y < Y + Height;

		public LayoutBox(BoxKind kind, Node node, ComputedStyle style)
		{
			Kind = kind;
			Node = node;
			Style = style;
		}

		public void Add(LayoutBox child)
		{
			child.Parent = this;
			Children.Add(child);
		}

		// Moves this box and everything in it
		public void Translate(float dx, float dy)
		{
			if (dx == 0 && dy == 0)
				return;
			X += dx;
			Y += dy;
			if (Lines != null)
			{
				foreach (LineBox line in Lines)
				{
					line.X += dx;
					line.Y += dy;
					foreach (LineFragment fragment in line.Fragments)
					{
						fragment.X += dx;
						fragment.Y += dy;
					}
				}
			}
			foreach (LayoutBox child in Children)
				child.Translate(dx, dy);
			if (Positioned != null)
			{
				foreach (LayoutBox child in Positioned)
					child.Translate(dx, dy);
			}
		}

		// The nearest element at or above this box, for hit tests on anonymous and text boxes
		public Element NearestElement()
		{
			LayoutBox box = this;
			while (box != null)
			{
				if (box.Node is Element element)
					return element;
				box = box.Parent;
			}
			return null;
		}

		public override string ToString() => $"{Kind} {TagName ?? (Text != null ? "\"" + (Text.Length > 20 ? Text.Substring(0, 20) + "..." : Text) + "\"" : "")} at {X},{Y} {Width}x{Height}";
	}

	// One line of inline content in a block container
	public sealed class LineBox
	{
		public float X;
		public float Y;
		public float Width;
		public float Height;

		// From the top of the line
		public float Baseline;
		public readonly List<LineFragment> Fragments = new List<LineFragment>();

		public float Bottom => Y + Height;
	}

	// A piece of a line: a run of text in one font, an atomic inline box, or a list marker
	public sealed class LineFragment
	{
		// The text box or inline-level box this came from; the inline element whose style it draws with is Style's owner
		public LayoutBox Box;
		public LayoutBox Inline;
		public string Text;
		public LayoutFont Font;
		public ComputedStyle Style;
		public float X;
		public float Y;
		public float Width;
		public float Height;

		// The text baseline from the top of the fragment
		public float Baseline;
		public bool IsAtomic;
		public bool IsMarker;

		// How many spaces a justified line can stretch in this fragment
		public int Spaces;

		public override string ToString() => IsAtomic ? $"[{Box}]" : $"\"{Text}\" at {X},{Y} {Width}x{Height}";
	}
}
