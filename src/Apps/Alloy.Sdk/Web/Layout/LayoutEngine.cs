using System;
using System.Collections.Generic;
using System.Text;
using Alloy.Sdk.Web.Css;
using Alloy.Sdk.Web.Html;

namespace Alloy.Sdk.Web.Layout
{
	// The floats placed so far in one block formatting context, in document coordinates
	public sealed class FloatContext
	{
		private struct Entry
		{
			public LayoutBox Box;
			public float Left;
			public float Right;
			public float Top;
			public float Bottom;
			public bool IsLeft;
		}

		private readonly List<Entry> _floats = new List<Entry>();

		public int Count => _floats.Count;

		public void Add(LayoutBox box)
		{
			_floats.Add(new Entry
			{
				Box = box,
				Left = box.X - box.MarginLeft,
				Right = box.X + box.Width + box.MarginRight,
				Top = box.Y - box.MarginTop,
				Bottom = box.Y + box.Height + box.MarginBottom,
				IsLeft = box.Style.Float == FloatSide.Left,
			});
		}

		public void Remove(LayoutBox box)
		{
			for (int i = _floats.Count - 1; i >= 0; i--)
			{
				if (_floats[i].Box == box)
					_floats.RemoveAt(i);
			}
		}

		// The right edge of the left floats crossing the band, bounded below by left; left when there are none
		public float LeftEdge(float left, float top, float bottom)
		{
			float edge = left;
			foreach (Entry entry in _floats)
			{
				if (entry.IsLeft && entry.Top < bottom && entry.Bottom > top)
					edge = Math.Max(edge, entry.Right);
			}
			return edge;
		}

		public float RightEdge(float right, float top, float bottom)
		{
			float edge = right;
			foreach (Entry entry in _floats)
			{
				if (!entry.IsLeft && entry.Top < bottom && entry.Bottom > top)
					edge = Math.Min(edge, entry.Left);
			}
			return edge;
		}

		// The lowest bottom of the floats on a side, for clear
		public float ClearBottom(ClearSide side, float y)
		{
			foreach (Entry entry in _floats)
			{
				if (side == ClearSide.Both || (side == ClearSide.Left && entry.IsLeft) || (side == ClearSide.Right && !entry.IsLeft))
					y = Math.Max(y, entry.Bottom);
			}
			return y;
		}

		// The first float bottom below y among those crossing the band, where a wider line could start; y itself when none cross
		public float NextBottom(float top, float bottom)
		{
			float next = float.MaxValue;
			foreach (Entry entry in _floats)
			{
				if (entry.Top < bottom && entry.Bottom > top && entry.Bottom > top)
					next = Math.Min(next, entry.Bottom);
			}
			return next == float.MaxValue ? top : next;
		}

		public bool Intrudes(float top, float bottom)
		{
			foreach (Entry entry in _floats)
			{
				if (entry.Top < bottom && entry.Bottom > top)
					return true;
			}
			return false;
		}

		public float Bottom(float y)
		{
			foreach (Entry entry in _floats)
				y = Math.Max(y, entry.Bottom);
			return y;
		}

		// Floats must not start above earlier ones
		public float LastTop
		{
			get
			{
				float top = float.MinValue;
				foreach (Entry entry in _floats)
					top = Math.Max(top, entry.Top);
				return top;
			}
		}
	}

	// CSS 2.1 visual formatting, the parts static pages use: block stacking with margin collapsing, inline formatting with line boxes, floats and clear, tables, lists, relative and absolute positioning
	public sealed partial class LayoutEngine
	{
		private const float Spacing = 2;
		private const int MaxItems = 200000;

		private readonly IFontProvider _fonts;
		private readonly float _viewportWidth;
		private readonly float _viewportHeight;
		private LayoutBox _root;

		public LayoutEngine(IFontProvider fonts, float viewportWidth, float viewportHeight)
		{
			_fonts = fonts;
			_viewportWidth = viewportWidth;
			_viewportHeight = viewportHeight;
		}

		// Lays the tree out against the viewport; the root's height is the document height
		public void Layout(LayoutBox root)
		{
			_root = root;
			root.Positioned = null;
			FloatContext floats = new FloatContext();
			LayoutBlock(root, 0, 0, _viewportWidth, _viewportHeight, floats, true);
			float bottom = floats.Bottom(root.Y + root.Height);
			if (bottom > root.Y + root.Height)
				root.Height = bottom - root.Y;
			LayoutPositioned(root, root, root.Width, Math.Max(root.Height, _viewportHeight));
			// The document scrolls as far as anything reaches, even when html itself is only as tall as the viewport
			float reach = ScrollableBottom(root);
			if (reach > root.Y + root.Height)
				root.Height = reach - root.Y;
		}

		// The lowest border-box bottom among the descendants that are not clipped away by an overflow or pinned by position fixed
		private static float ScrollableBottom(LayoutBox box)
		{
			float bottom = box.Y + box.Height;
			if (box.Style != null && box.Style.Overflow != Overflow.Visible && box.Kind != BoxKind.Inline)
				return bottom;
			foreach (LayoutBox child in box.Children)
			{
				if (child.Kind == BoxKind.Text || child.Kind == BoxKind.LineBreak || (child.Style != null && child.Style.Position == Position.Fixed))
					continue;
				bottom = Math.Max(bottom, ScrollableBottom(child));
			}
			if (box.Positioned != null)
			{
				foreach (LayoutBox child in box.Positioned)
				{
					if (child.Style == null || child.Style.Position != Position.Fixed)
						bottom = Math.Max(bottom, ScrollableBottom(child));
				}
			}
			return bottom;
		}

		// Lays out a block-level box with its margin box's top-left at (x, y) inside a containing block of the given content width; the box's own margins are resolved here and left for the caller to place around
		private void LayoutBlock(LayoutBox box, float x, float y, float containingWidth, float containingHeight, FloatContext floats, bool formattingRoot)
		{
			ResolveEdges(box, containingWidth);
			if (box.Kind == BoxKind.Table)
			{
				LayoutTable(box, x, y, containingWidth, containingHeight);
				return;
			}
			ResolveWidth(box, containingWidth, false);
			box.X = x + box.MarginLeft;
			box.Y = y + box.MarginTop;
			LayoutContents(box, containingHeight, floats, formattingRoot);
		}

		// The children of a block whose position and width are settled; sets its height
		private void LayoutContents(LayoutBox box, float containingHeight, FloatContext floats, bool formattingRoot)
		{
			bool ownContext = formattingRoot || box.IsFloat || box.IsAbsolute || box.Kind == BoxKind.InlineBlock || box.Kind == BoxKind.TableCell || box.Style.Overflow != Overflow.Visible || box.Style.IsFlexContainer || box.Style.IsGridContainer;
			if (ownContext)
				floats = new FloatContext();
			float contentHeight;
			if (box.IsReplaced && box.Children.Count == 0)
				contentHeight = 0;
			else if (box.IsReplaced && box.IntrinsicHeight > 0 && box.Style.Height.IsAuto)
			{
				// A text field: its text is laid out inside, but its size is the control's, not the text's
				LayoutInline(box, floats ?? new FloatContext());
				contentHeight = box.IntrinsicHeight;
			}
			else if (box.Style.IsFlexContainer && box.Kind != BoxKind.Inline)
				contentHeight = LayoutFlex(box, containingHeight);
			else if (box.Style.IsGridContainer && box.Kind != BoxKind.Inline)
				contentHeight = LayoutGrid(box, containingHeight);
			else if (HasInlineContent(box))
				contentHeight = LayoutInline(box, floats);
			else
				contentHeight = LayoutBlockChildren(box, floats, ownContext);
			if (ownContext)
				contentHeight = Math.Max(contentHeight, floats.Bottom(box.ContentY + contentHeight) - box.ContentY);
			SetHeight(box, contentHeight, containingHeight);
			if (box.Style.Display == Display.ListItem && box.Style.ListStyleType != ListStyleType.None && !box.Style.ListInside)
				box.Marker = MarkerText(box);
			ApplyTranslate(box);
		}

		private void SetHeight(LayoutBox box, float contentHeight, float containingHeight)
		{
			ComputedStyle style = box.Style;
			float height = contentHeight;
			if (box.IsReplaced && (box.Children.Count == 0 || box.IntrinsicHeight > 0))
				height = ReplacedHeight(box, containingHeight);
			else if (!style.Height.IsAuto && (!style.Height.IsPercent || containingHeight >= 0))
				height = Math.Max(0, box.ContentHeightOf(style.Height.Resolve(containingHeight)));
			if (!style.MaxHeight.IsAuto && (!style.MaxHeight.IsPercent || containingHeight >= 0))
				height = Math.Min(height, box.ContentHeightOf(style.MaxHeight.Resolve(containingHeight)));
			if (!style.MinHeight.IsPercent || containingHeight >= 0)
				height = Math.Max(height, box.ContentHeightOf(style.MinHeight.Resolve(containingHeight)));
			box.Height = height + box.VerticalEdges;
		}

		private static float ReplacedHeight(LayoutBox box, float containingHeight)
		{
			ComputedStyle style = box.Style;
			if (!style.Height.IsAuto && (!style.Height.IsPercent || containingHeight >= 0))
				return Math.Max(0, box.ContentHeightOf(style.Height.Resolve(containingHeight)));
			if (box.Children.Count > 0)
				return Math.Max(0, box.IntrinsicHeight);
			float intrinsicWidth = Math.Max(0, box.IntrinsicWidth);
			float intrinsicHeight = Math.Max(0, box.IntrinsicHeight);
			if (intrinsicWidth > 0 && intrinsicHeight > 0 && box.ContentWidth != intrinsicWidth)
				return intrinsicHeight * box.ContentWidth / intrinsicWidth;
			return intrinsicHeight;
		}

		private static bool HasInlineContent(LayoutBox box)
		{
			foreach (LayoutBox child in box.Children)
			{
				if (child.IsOutOfFlow)
					continue;
				return child.IsInlineLevel;
			}
			return box.Style.Display == Display.ListItem && box.Style.ListInside;
		}

		// Margins, padding and borders in pixels; percentages are against the containing block's width, auto margins start at zero
		private static void ResolveEdges(LayoutBox box, float containingWidth)
		{
			ComputedStyle style = box.Style;
			box.MarginTop = style.Margin.Top.Resolve(containingWidth);
			box.MarginRight = style.Margin.Right.Resolve(containingWidth);
			box.MarginBottom = style.Margin.Bottom.Resolve(containingWidth);
			box.MarginLeft = style.Margin.Left.Resolve(containingWidth);
			box.PaddingTop = Math.Max(0, style.Padding.Top.Resolve(containingWidth));
			box.PaddingRight = Math.Max(0, style.Padding.Right.Resolve(containingWidth));
			box.PaddingBottom = Math.Max(0, style.Padding.Bottom.Resolve(containingWidth));
			box.PaddingLeft = Math.Max(0, style.Padding.Left.Resolve(containingWidth));
			box.BorderTop = style.BorderTop.Effective;
			box.BorderRight = style.BorderRight.Effective;
			box.BorderBottom = style.BorderBottom.Effective;
			box.BorderLeft = style.BorderLeft.Effective;
			if (box.Kind == BoxKind.Inline || box.Kind == BoxKind.Text)
			{
				box.MarginTop = box.MarginBottom = 0;
				if (box.Kind == BoxKind.Text)
					box.MarginLeft = box.MarginRight = box.PaddingLeft = box.PaddingRight = box.BorderLeft = box.BorderRight = 0;
			}
		}

		// Block width per CSS 2.1 10.3.3: auto width fills the containing block, auto margins centre a sized box, min and max clamp
		private void ResolveWidth(LayoutBox box, float containingWidth, bool shrinkToFit)
		{
			ComputedStyle style = box.Style;
			bool autoLeft = style.Margin.Left.IsAuto;
			bool autoRight = style.Margin.Right.IsAuto;
			float edges = box.HorizontalEdges;
			float available = containingWidth - box.MarginLeft - box.MarginRight - edges;
			float width;
			if (!style.Width.IsAuto)
				width = Math.Max(0, box.ContentWidthOf(style.Width.Resolve(containingWidth)));
			else if (box.IsReplaced && (box.Children.Count == 0 || box.IntrinsicWidth > 0))
				width = ReplacedWidth(box, available);
			else if (shrinkToFit)
			{
				(float min, float max) = IntrinsicWidths(box);
				width = Math.Min(Math.Max(min, available), max);
			}
			else
			{
				width = Math.Max(0, available);
				autoLeft = autoRight = false;
			}
			width = ClampWidth(box, width, containingWidth);
			box.Width = width + edges;
			if (shrinkToFit || box.IsFloat || box.IsAbsolute || box.Kind == BoxKind.InlineBlock)
				return;
			float slack = containingWidth - box.MarginBoxWidth;
			if (autoLeft && autoRight)
				box.MarginLeft = box.MarginRight = slack / 2;
			else if (autoLeft)
				box.MarginLeft = slack;
			else if (autoRight)
				box.MarginRight = slack;
			else if (slack < 0 && style.Width.IsAuto)
				box.Width = Math.Max(edges, box.Width + slack);
		}

		private static float ClampWidth(LayoutBox box, float width, float containingWidth)
		{
			ComputedStyle style = box.Style;
			if (!style.MaxWidth.IsAuto)
				width = Math.Min(width, Math.Max(0, box.ContentWidthOf(style.MaxWidth.Resolve(containingWidth))));
			width = Math.Max(width, box.ContentWidthOf(style.MinWidth.Resolve(containingWidth)));
			return width;
		}

		private static float ReplacedWidth(LayoutBox box, float available)
		{
			ComputedStyle style = box.Style;
			float intrinsicWidth = Math.Max(0, box.IntrinsicWidth);
			float intrinsicHeight = Math.Max(0, box.IntrinsicHeight);
			if (!style.Height.IsAuto && !style.Height.IsPercent && intrinsicWidth > 0 && intrinsicHeight > 0)
				return box.ContentHeightOf(style.Height.Value) * intrinsicWidth / intrinsicHeight;
			return intrinsicWidth;
		}

		// Stacks the in-flow block children, collapsing adjacent vertical margins, placing floats and keeping absolutes for later; returns the content height
		private float LayoutBlockChildren(LayoutBox box, FloatContext floats, bool ownContext)
		{
			float x = box.ContentX;
			float width = box.ContentWidth;
			float y = box.ContentY;
			bool collapseTop = CollapsesTopThrough(box) && !ownContext;
			bool collapseBottom = CollapsesBottomThrough(box) && !ownContext;
			float pending = 0;
			bool first = true;
			float height = -1;
			if (!box.Style.Height.IsAuto && !box.Style.Height.IsPercent)
				height = box.ContentHeightOf(box.Style.Height.Value);
			foreach (LayoutBox child in box.Children)
			{
				if (child.IsAbsolute)
				{
					child.StaticX = x;
					child.StaticY = y + pending;
					Defer(box, child);
					continue;
				}
				if (child.IsFloat)
				{
					PlaceFloat(child, floats, x, width, y + pending, height);
					continue;
				}
				if (child.Kind == BoxKind.Text || child.Kind == BoxKind.LineBreak)
					continue;
				ResolveEdges(child, width);
				if (child.Style.Clear != ClearSide.None)
				{
					float cleared = floats.ClearBottom(child.Style.Clear, y + pending);
					if (cleared > y + pending)
					{
						y = cleared;
						pending = 0;
					}
				}
				float top = EffectiveTopMargin(child, width);
				if (!(first && collapseTop))
					y += Collapse(pending, top);
				float childTop = y;
				LayoutBlock(child, x, y - child.MarginTop, width, height, floats, false);
				if (child.Style.Position == Position.Relative)
					OffsetRelative(child, width, height);
				y = childTop + child.Height;
				pending = EffectiveBottomMargin(child, width);
				first = false;
			}
			if (!first && !collapseBottom)
				y += pending;
			if (first)
				pending = 0;
			box.LeakedBottomMargin = collapseBottom ? pending : 0;
			return Math.Max(0, y - box.ContentY);
		}

		// Two adjoining vertical margins become one: the larger of positives, the more negative of negatives, or their sum when mixed
		private static float Collapse(float a, float b)
		{
			if (a >= 0 && b >= 0)
				return Math.Max(a, b);
			if (a <= 0 && b <= 0)
				return Math.Min(a, b);
			return a + b;
		}

		private static bool CollapsesTopThrough(LayoutBox box) =>
			box.BorderTop == 0 && box.PaddingTop == 0 && box.Kind != BoxKind.TableCell && box.Kind != BoxKind.InlineBlock && !box.IsFloat && !box.IsAbsolute && box.Style.Overflow == Overflow.Visible;

		private static bool CollapsesBottomThrough(LayoutBox box) =>
			box.BorderBottom == 0 && box.PaddingBottom == 0 && box.Style.Height.IsAuto && box.Style.MinHeight.Value == 0 && box.Kind != BoxKind.TableCell && box.Kind != BoxKind.InlineBlock && !box.IsFloat && !box.IsAbsolute && box.Style.Overflow == Overflow.Visible;

		// A block's top margin as its parent sees it: its own, collapsed with those of its first children that have nothing between them and it
		private float EffectiveTopMargin(LayoutBox box, float containingWidth)
		{
			float margin = box.Style.Margin.Top.Resolve(containingWidth);
			int depth = 0;
			LayoutBox current = box;
			while (depth++ < 32)
			{
				if (current.Kind == BoxKind.Table || current.IsReplaced || HasInlineContent(current))
					break;
				ResolveEdges(current, containingWidth);
				if (!CollapsesTopThrough(current) || current.Kind == BoxKind.InlineBlock)
					break;
				LayoutBox firstChild = FirstInFlowBlock(current);
				if (firstChild == null)
					break;
				float childWidth = Math.Max(0, containingWidth - current.HorizontalEdges);
				margin = Collapse(margin, firstChild.Style.Margin.Top.Resolve(childWidth));
				current = firstChild;
				containingWidth = childWidth;
			}
			return margin;
		}

		private float EffectiveBottomMargin(LayoutBox box, float containingWidth)
		{
			float margin = box.Style.Margin.Bottom.Resolve(containingWidth);
			if (box.LeakedBottomMargin != 0)
				margin = Collapse(margin, box.LeakedBottomMargin);
			return margin;
		}

		private static LayoutBox FirstInFlowBlock(LayoutBox box)
		{
			foreach (LayoutBox child in box.Children)
			{
				if (child.IsOutOfFlow)
					continue;
				return child.IsBlockLevel ? child : null;
			}
			return null;
		}

		private static void Defer(LayoutBox box, LayoutBox child)
		{
			LayoutBox container = box;
			while (container.Parent != null && container.Style.Position == Position.Static)
				container = container.Parent;
			if (child.Style.Position == Position.Fixed)
			{
				while (container.Parent != null)
					container = container.Parent;
			}
			(container.Positioned ??= new List<LayoutBox>()).Add(child);
		}

		private void OffsetRelative(LayoutBox box, float containingWidth, float containingHeight)
		{
			float dx = box.Style.Left.IsAuto ? 0 : box.Style.Left.Resolve(containingWidth);
			float dy = box.Style.Top.IsAuto ? 0 : (box.Style.Top.IsPercent && containingHeight < 0 ? 0 : box.Style.Top.Resolve(containingHeight));
			box.Translate(dx, dy);
		}

		// A transform's translation moves the finished box and everything in it, without touching the flow around it
		private static void ApplyTranslate(LayoutBox box)
		{
			ComputedStyle style = box.Style;
			if (style == null || (style.TranslateX.Value == 0 && style.TranslateY.Value == 0))
				return;
			box.Translate(style.TranslateX.Resolve(box.Width), style.TranslateY.Resolve(box.Height));
		}

		// Absolutely positioned boxes deferred to this container: shrink-to-fit at their offsets, or where they would have been
		private void LayoutPositioned(LayoutBox container, LayoutBox root, float containingWidth, float containingHeight)
		{
			if (container.Positioned == null)
				return;
			List<LayoutBox> positioned = container.Positioned;
			container.Positioned = null;
			foreach (LayoutBox box in positioned)
			{
				float width = container == root ? _viewportWidth : container.ContentWidth;
				float height = container == root ? Math.Max(_viewportHeight, container.Height) : container.ContentHeight;
				float originX = container == root ? 0 : container.ContentX;
				float originY = container == root ? 0 : container.ContentY;
				ResolveEdges(box, width);
				ResolveWidth(box, width, true);
				float x = box.Style.Left.IsAuto ? box.StaticX : originX + box.Style.Left.Resolve(width);
				float y = box.Style.Top.IsAuto ? box.StaticY : originY + box.Style.Top.Resolve(height);
				box.X = x + box.MarginLeft;
				box.Y = y + box.MarginTop;
				if (box.Kind == BoxKind.Table)
					LayoutTable(box, x, y, width, height);
				else
					LayoutContents(box, height, null, true);
				LayoutPositioned(box, root, box.ContentWidth, box.ContentHeight);
			}
			foreach (LayoutBox child in container.Children)
				LayoutPositionedIn(child, root);
		}

		private void LayoutPositionedIn(LayoutBox box, LayoutBox root)
		{
			if (box.Positioned != null)
				LayoutPositioned(box, root, box.ContentWidth, box.ContentHeight);
			foreach (LayoutBox child in box.Children)
				LayoutPositionedIn(child, root);
		}

		// A float: shrink-to-fit, then as high as the current line and as far to its side as earlier floats allow
		private void PlaceFloat(LayoutBox box, FloatContext floats, float x, float width, float y, float containingHeight)
		{
			ResolveEdges(box, width);
			if (box.Kind == BoxKind.Table)
				LayoutTable(box, x, y, width, containingHeight);
			else
			{
				ResolveWidth(box, width, true);
				box.X = x + box.MarginLeft;
				box.Y = y + box.MarginTop;
				LayoutContents(box, containingHeight, null, true);
			}
			if (box.Style.Clear != ClearSide.None)
				y = floats.ClearBottom(box.Style.Clear, y);
			y = Math.Max(y, floats.LastTop);
			float need = box.MarginBoxWidth;
			float height = Math.Max(1, box.MarginBoxHeight);
			int guard = 0;
			while (guard++ < 64)
			{
				float left = floats.LeftEdge(x, y, y + height);
				float right = floats.RightEdge(x + width, y, y + height);
				if (right - left >= need || !floats.Intrudes(y, y + height))
				{
					float targetX = box.Style.Float == FloatSide.Left ? left : right - need;
					box.Translate(targetX + box.MarginLeft - box.X, y + box.MarginTop - box.Y);
					floats.Add(box);
					return;
				}
				float next = floats.NextBottom(y, y + height);
				if (next <= y)
					break;
				y = next;
			}
			box.Translate(x + box.MarginLeft - box.X, y + box.MarginTop - box.Y);
			floats.Add(box);
		}

		private string MarkerText(LayoutBox item)
		{
			Element element = item.Element;
			int index = 1;
			if (element != null)
			{
				Element parent = element.ParentElement;
				if (parent != null)
				{
					index = int.TryParse(parent.GetAttribute("start"), out int start) ? start : 1;
					bool reversed = parent.HasAttribute("reversed");
					foreach (Node sibling in parent.Children)
					{
						if (sibling is not Element candidate)
							continue;
						if (int.TryParse(candidate.GetAttribute("value"), out int value))
							index = value;
						if (candidate == element)
							break;
						if (candidate.TagName == "li")
							index += reversed ? -1 : 1;
					}
				}
			}
			return ListMarkers.Text(item.Style.ListStyleType, index);
		}

		// Inline formatting: the inline content of a block broken into lines around the floats; returns the content height
		private float LayoutInline(LayoutBox box, FloatContext floats)
		{
			List<InlineItem> items = new List<InlineItem>();
			InlineCollector collector = new InlineCollector(this, items, box);
			if (box.Style.Display == Display.ListItem && box.Style.ListInside && box.Style.ListStyleType != ListStyleType.None)
				collector.AddMarker(MarkerText(box) + " ");
			collector.Collect(box);
			LineBuilder builder = new LineBuilder(this, box, floats);
			foreach (InlineItem item in items)
				builder.Add(item);
			builder.Finish();
			box.Lines = builder.Lines;
			return builder.Y - box.ContentY;
		}

		internal LayoutFont FontFor(ComputedStyle style) => _fonts.GetFont(style);

		// Atomic inlines are laid out as blocks before the line knows where they go
		internal void LayoutAtomic(LayoutBox box, float containingWidth, float containingHeight)
		{
			ResolveEdges(box, containingWidth);
			if (box.Kind == BoxKind.Table)
			{
				LayoutTable(box, 0, 0, containingWidth, containingHeight);
				return;
			}
			ResolveWidth(box, containingWidth, true);
			box.X = box.MarginLeft;
			box.Y = box.MarginTop;
			LayoutContents(box, containingHeight, null, true);
			box.Baseline = FindBaseline(box);
		}

		// The last line's baseline from the top of the border box, or -1 when the box has no lines
		private static float FindBaseline(LayoutBox box)
		{
			if (box.Style.Overflow != Overflow.Visible && box.Kind == BoxKind.InlineBlock)
				return -1;
			if (box.Lines != null && box.Lines.Count > 0)
			{
				LineBox last = box.Lines[box.Lines.Count - 1];
				return last.Y + last.Baseline - box.Y;
			}
			for (int i = box.Children.Count - 1; i >= 0; i--)
			{
				LayoutBox child = box.Children[i];
				if (child.IsOutOfFlow || child.Kind == BoxKind.Text)
					continue;
				float baseline = FindBaseline(child);
				if (baseline >= 0)
					return child.Y - box.Y + baseline;
			}
			return -1;
		}

		// Min-content and max-content widths of a box's content, not counting its own edges
		internal (float Min, float Max) IntrinsicWidths(LayoutBox box)
		{
			if (box.MinContent >= 0)
				return (box.MinContent, box.MaxContent);
			float min = 0;
			float max = 0;
			if (box.IsReplaced && (box.Children.Count == 0 || box.IntrinsicWidth > 0))
			{
				float width = box.Style.Width.IsAuto || box.Style.Width.IsPercent ? Math.Max(0, box.IntrinsicWidth) : box.ContentWidthOf(box.Style.Width.Value);
				min = max = width;
				// A percentage width on a picture resolves against nothing while its container is being sized, so it asks for no minimum
				if (box.Style.Width.IsPercent || (!box.Style.MaxWidth.IsAuto && box.Style.MaxWidth.IsPercent))
					min = 0;
			}
			else if (box.Kind == BoxKind.Table)
			{
				TableGrid grid = BuildGrid(box);
				(min, max) = (grid.MinWidth, grid.MaxWidth);
			}
			else if (box.Style.IsFlexContainer && box.Kind != BoxKind.Inline)
				(min, max) = FlexIntrinsicWidths(box);
			else if (box.Style.IsGridContainer && box.Kind != BoxKind.Inline)
				(min, max) = GridIntrinsicWidths(box);
			else if (HasInlineContent(box))
			{
				List<InlineItem> items = new List<InlineItem>();
				new InlineCollector(this, items, box, true).Collect(box);
				float line = 0;
				float pendingSpace = 0;
				bool nowrap = box.Style.WhiteSpace == WhiteSpace.NoWrap || box.Style.WhiteSpace == WhiteSpace.Pre;
				float unbreakable = 0;
				foreach (InlineItem item in items)
				{
					switch (item.Kind)
					{
						case InlineItemKind.Word:
						case InlineItemKind.Atomic:
						case InlineItemKind.Edge:
							line += pendingSpace + item.Width;
							unbreakable += pendingSpace * (nowrap || (item.Style != null && item.Style.WhiteSpace is WhiteSpace.NoWrap or WhiteSpace.Pre) ? 1 : 0) + (item.Kind == InlineItemKind.Atomic ? item.MinWidth : item.Width);
							pendingSpace = 0;
							break;
						case InlineItemKind.Space:
							if (item.Preserved)
								line += item.Width;
							else
								pendingSpace = item.Width;
							if (!(nowrap || (item.Style != null && item.Style.WhiteSpace is WhiteSpace.NoWrap or WhiteSpace.Pre)))
							{
								min = Math.Max(min, unbreakable);
								unbreakable = 0;
							}
							else
								unbreakable += item.Preserved ? item.Width : 0;
							break;
						case InlineItemKind.Newline:
							max = Math.Max(max, line);
							min = Math.Max(min, unbreakable);
							line = 0;
							pendingSpace = 0;
							unbreakable = 0;
							break;
					}
				}
				max = Math.Max(max, line);
				min = Math.Max(min, unbreakable);
				min = Math.Min(min, max);
			}
			else
			{
				foreach (LayoutBox child in box.Children)
				{
					if (child.IsAbsolute || child.Kind == BoxKind.Text || child.Kind == BoxKind.LineBreak)
						continue;
					(float childMin, float childMax) = OuterIntrinsicWidths(child);
					min = Math.Max(min, childMin);
					max = Math.Max(max, childMax);
				}
			}
			if (!box.Style.MaxWidth.IsAuto && !box.Style.MaxWidth.IsPercent)
			{
				max = Math.Min(max, box.ContentWidthOf(box.Style.MaxWidth.Value));
				min = Math.Min(min, max);
			}
			if (!box.Style.MinWidth.IsPercent && box.Style.MinWidth.Value > 0)
			{
				min = Math.Max(min, box.ContentWidthOf(box.Style.MinWidth.Value));
				max = Math.Max(max, min);
			}
			box.MinContent = min;
			box.MaxContent = max;
			return (min, max);
		}

		// A child's contribution to its parent's intrinsic widths: a fixed width counts as is, otherwise its content, plus its edges and margins
		internal (float Min, float Max) OuterIntrinsicWidths(LayoutBox box)
		{
			ResolveEdges(box, 0);
			float edges = box.HorizontalEdges + Math.Max(0, box.MarginLeft) + Math.Max(0, box.MarginRight);
			if (!box.Style.Width.IsAuto && !box.Style.Width.IsPercent)
			{
				float width = ClampWidth(box, box.ContentWidthOf(box.Style.Width.Value), 0) + edges;
				return (width, width);
			}
			(float min, float max) = IntrinsicWidths(box);
			return (min + edges, max + edges);
		}

		// Tables: columns sized from their cells' content between min and max, rows as tall as their tallest cell
		private sealed class TableGrid
		{
			public int Columns;
			public float[] MinColumns;
			public float[] MaxColumns;
			public List<LayoutBox> Rows = new List<LayoutBox>();
			public List<LayoutBox> Captions = new List<LayoutBox>();
			public List<(LayoutBox Cell, int Row, int Column)> Cells = new List<(LayoutBox, int, int)>();
			public float Spacing;

			public float MinWidth
			{
				get
				{
					float width = Spacing * (Columns + 1);
					foreach (float column in MinColumns)
						width += column;
					return width;
				}
			}

			public float MaxWidth
			{
				get
				{
					float width = Spacing * (Columns + 1);
					foreach (float column in MaxColumns)
						width += column;
					return width;
				}
			}
		}

		private TableGrid BuildGrid(LayoutBox table)
		{
			TableGrid grid = new TableGrid { Spacing = Spacing };
			if (table.Element != null && table.Element.GetAttribute("cellspacing") is string spacing && float.TryParse(spacing, out float cellSpacing))
				grid.Spacing = Math.Max(0, cellSpacing);
			foreach (LayoutBox child in table.Children)
			{
				if (child.Kind == BoxKind.TableRow)
					grid.Rows.Add(child);
				else if (child.Kind == BoxKind.Block)
					grid.Captions.Add(child);
			}
			List<HashSet<int>> occupied = new List<HashSet<int>>();
			int columns = 0;
			for (int r = 0; r < grid.Rows.Count; r++)
			{
				while (occupied.Count <= r)
					occupied.Add(new HashSet<int>());
				int column = 0;
				foreach (LayoutBox cell in grid.Rows[r].Children)
				{
					if (cell.Kind != BoxKind.TableCell)
						continue;
					while (occupied[r].Contains(column))
						column++;
					grid.Cells.Add((cell, r, column));
					for (int span = 1; span < cell.RowSpan; span++)
					{
						while (occupied.Count <= r + span)
							occupied.Add(new HashSet<int>());
						for (int c = 0; c < cell.ColumnSpan; c++)
							occupied[r + span].Add(column + c);
					}
					column += cell.ColumnSpan;
				}
				columns = Math.Max(columns, column);
			}
			grid.Columns = columns;
			grid.MinColumns = new float[columns];
			grid.MaxColumns = new float[columns];
			foreach ((LayoutBox cell, int row, int column) in grid.Cells)
			{
				if (cell.ColumnSpan != 1)
					continue;
				(float min, float max) = CellIntrinsicWidths(cell);
				grid.MinColumns[column] = Math.Max(grid.MinColumns[column], min);
				grid.MaxColumns[column] = Math.Max(grid.MaxColumns[column], max);
			}
			foreach ((LayoutBox cell, int row, int column) in grid.Cells)
			{
				if (cell.ColumnSpan == 1)
					continue;
				int last = Math.Min(columns, column + cell.ColumnSpan);
				(float min, float max) = CellIntrinsicWidths(cell);
				float haveMin = 0;
				float haveMax = 0;
				for (int c = column; c < last; c++)
				{
					haveMin += grid.MinColumns[c];
					haveMax += grid.MaxColumns[c];
				}
				float spanSpacing = grid.Spacing * (last - column - 1);
				if (min - spanSpacing > haveMin)
				{
					float extra = (min - spanSpacing - haveMin) / (last - column);
					for (int c = column; c < last; c++)
						grid.MinColumns[c] += extra;
				}
				if (max - spanSpacing > haveMax)
				{
					float extra = (max - spanSpacing - haveMax) / (last - column);
					for (int c = column; c < last; c++)
						grid.MaxColumns[c] += extra;
				}
			}
			for (int c = 0; c < columns; c++)
				grid.MaxColumns[c] = Math.Max(grid.MaxColumns[c], grid.MinColumns[c]);
			return grid;
		}

		private (float Min, float Max) CellIntrinsicWidths(LayoutBox cell)
		{
			ResolveEdges(cell, 0);
			(float min, float max) = IntrinsicWidths(cell);
			float edges = cell.HorizontalEdges;
			if (!cell.Style.Width.IsAuto && !cell.Style.Width.IsPercent)
			{
				float width = cell.Style.Width.Value + edges;
				return (Math.Max(min + edges, width), Math.Max(max + edges, width));
			}
			return (min + edges, max + edges);
		}

		private void LayoutTable(LayoutBox table, float x, float y, float containingWidth, float containingHeight)
		{
			TableGrid grid = BuildGrid(table);
			ComputedStyle style = table.Style;
			float edges = table.HorizontalEdges;
			float available = containingWidth - table.MarginLeft - table.MarginRight - edges;
			float gridMin = grid.MinWidth;
			float gridMax = grid.MaxWidth;
			float target;
			bool fixedWidth = !style.Width.IsAuto;
			if (fixedWidth)
				target = Math.Max(gridMin, Math.Max(0, style.Width.Resolve(containingWidth)));
			else
				target = Math.Max(gridMin, Math.Min(available, gridMax));
			target = Math.Max(gridMin, ClampWidth(table, target, containingWidth));
			float[] columns = new float[grid.Columns];
			float spacingTotal = grid.Spacing * (grid.Columns + 1);
			float cells = target - spacingTotal;
			float sumMin = gridMin - spacingTotal;
			float sumMax = gridMax - spacingTotal;
			for (int c = 0; c < grid.Columns; c++)
			{
				if (cells >= sumMax)
					columns[c] = grid.MaxColumns[c] + (sumMax > 0 ? (cells - sumMax) * grid.MaxColumns[c] / sumMax : (cells - sumMax) / Math.Max(1, grid.Columns));
				else if (cells <= sumMin || sumMax <= sumMin)
					columns[c] = grid.MinColumns[c];
				else
					columns[c] = grid.MinColumns[c] + (grid.MaxColumns[c] - grid.MinColumns[c]) * (cells - sumMin) / (sumMax - sumMin);
			}
			table.Width = target + edges;
			if (style.Margin.Left.IsAuto && style.Margin.Right.IsAuto)
				table.MarginLeft = table.MarginRight = Math.Max(0, (containingWidth - table.Width) / 2);
			else if (style.Margin.Left.IsAuto)
				table.MarginLeft = Math.Max(0, containingWidth - table.Width - table.MarginRight);
			table.X = x + table.MarginLeft;
			table.Y = y + table.MarginTop;

			float cursorY = table.ContentY;
			foreach (LayoutBox caption in grid.Captions)
			{
				ResolveEdges(caption, target);
				LayoutBlock(caption, table.ContentX, cursorY, target, -1, new FloatContext(), true);
				cursorY = caption.Y + caption.Height + caption.MarginBottom;
			}
			float[] columnX = new float[grid.Columns + 1];
			columnX[0] = table.ContentX + grid.Spacing;
			for (int c = 0; c < grid.Columns; c++)
				columnX[c + 1] = columnX[c] + columns[c] + grid.Spacing;
			float[] rowHeights = new float[grid.Rows.Count];
			float[] rowY = new float[grid.Rows.Count + 1];
			cursorY += grid.Spacing;
			foreach ((LayoutBox cell, int row, int column) in grid.Cells)
			{
				int last = Math.Min(grid.Columns, column + cell.ColumnSpan);
				float width = columnX[last] - columnX[column] - grid.Spacing;
				ResolveEdges(cell, width);
				cell.MarginTop = cell.MarginBottom = cell.MarginLeft = cell.MarginRight = 0;
				cell.Width = Math.Max(cell.HorizontalEdges, width);
				cell.X = columnX[column];
				cell.Y = 0;
				LayoutContents(cell, -1, null, true);
				if (!cell.Style.Height.IsAuto && !cell.Style.Height.IsPercent)
					cell.Height = Math.Max(cell.Height, cell.Style.Height.Value + cell.VerticalEdges);
				if (cell.RowSpan == 1)
					rowHeights[row] = Math.Max(rowHeights[row], cell.Height);
			}
			for (int r = 0; r < grid.Rows.Count; r++)
			{
				LayoutBox row = grid.Rows[r];
				if (!row.Style.Height.IsAuto && !row.Style.Height.IsPercent)
					rowHeights[r] = Math.Max(rowHeights[r], row.Style.Height.Value);
			}
			foreach ((LayoutBox cell, int row, int column) in grid.Cells)
			{
				if (cell.RowSpan <= 1)
					continue;
				int last = Math.Min(grid.Rows.Count, row + cell.RowSpan);
				float have = -grid.Spacing;
				for (int r = row; r < last; r++)
					have += rowHeights[r] + grid.Spacing;
				if (cell.Height > have)
					rowHeights[last - 1] += cell.Height - have;
			}
			rowY[0] = cursorY;
			for (int r = 0; r < grid.Rows.Count; r++)
			{
				LayoutBox row = grid.Rows[r];
				row.X = table.ContentX + grid.Spacing;
				row.Y = rowY[r];
				row.Width = Math.Max(0, target - 2 * grid.Spacing);
				row.Height = rowHeights[r];
				rowY[r + 1] = rowY[r] + rowHeights[r] + grid.Spacing;
			}
			foreach ((LayoutBox cell, int row, int column) in grid.Cells)
			{
				int last = Math.Min(grid.Rows.Count, row + cell.RowSpan);
				float height = rowY[last] - rowY[row] - grid.Spacing;
				float contentHeight = cell.Height;
				cell.Translate(0, rowY[row]);
				float slack = height - contentHeight;
				cell.Height = Math.Max(height, contentHeight);
				if (slack > 0)
				{
					float shift = cell.Style.VerticalAlign switch
					{
						VerticalAlign.Top => 0,
						VerticalAlign.Bottom => slack,
						VerticalAlign.Baseline => 0,
						_ => slack / 2,
					};
					if (shift > 0)
					{
						foreach (LayoutBox child in cell.Children)
							child.Translate(0, shift);
						if (cell.Lines != null)
						{
							foreach (LineBox line in cell.Lines)
							{
								line.Y += shift;
								foreach (LineFragment fragment in line.Fragments)
									fragment.Y += shift;
							}
						}
					}
				}
			}
			float bottom = grid.Rows.Count > 0 ? rowY[grid.Rows.Count] : cursorY;
			float content = bottom - table.ContentY;
			SetHeight(table, content, containingHeight);
			if (table.Height < content + table.VerticalEdges)
				table.Height = content + table.VerticalEdges;
		}

		// The pieces of inline content in order, flattened from the inline tree
		internal enum InlineItemKind
		{
			Word,
			Space,
			Newline,
			Atomic,
			Edge,
			Float,
			Positioned,
		}

		internal sealed class InlineItem
		{
			public InlineItemKind Kind;
			public LayoutBox Box;
			public LayoutBox Inline;
			public string Text;
			public LayoutFont Font;
			public ComputedStyle Style;
			public float Width;
			// An atomic item's narrowest width, for min-content measuring; Width is its widest
			public float MinWidth;
			public bool Preserved;
			public bool NoWrap;
			public bool IsMarker;
			public bool Opening;
		}

		internal sealed class InlineCollector
		{
			private readonly LayoutEngine _engine;
			private readonly List<InlineItem> _items;
			private readonly LayoutBox _block;
			private readonly bool _measuring;
			private LayoutBox _inline;
			private bool _lastWasSpace = true;

			public InlineCollector(LayoutEngine engine, List<InlineItem> items, LayoutBox block, bool measuring = false)
			{
				_engine = engine;
				_items = items;
				_block = block;
				_measuring = measuring;
			}

			public void AddMarker(string text)
			{
				LayoutFont font = _engine.FontFor(_block.Style);
				_items.Add(new InlineItem { Kind = InlineItemKind.Word, Box = _block, Text = text, Font = font, Style = _block.Style, Width = font.Measure(text), IsMarker = true });
				_lastWasSpace = true;
			}

			public void Collect(LayoutBox box)
			{
				foreach (LayoutBox child in box.Children)
				{
					if (_items.Count > MaxItems)
						return;
					if (child.IsAbsolute)
					{
						_items.Add(new InlineItem { Kind = InlineItemKind.Positioned, Box = child });
						continue;
					}
					if (child.IsFloat)
					{
						_items.Add(new InlineItem { Kind = InlineItemKind.Float, Box = child });
						continue;
					}
					switch (child.Kind)
					{
						case BoxKind.Text:
							CollectText(child);
							break;
						case BoxKind.LineBreak:
							_items.Add(new InlineItem { Kind = InlineItemKind.Newline, Box = child, Style = child.Style });
							_lastWasSpace = true;
							break;
						case BoxKind.Inline:
						{
							ResolveEdges(child, _block.ContentWidth);
							LayoutBox outer = _inline;
							_inline = child;
							_items.Add(new InlineItem { Kind = InlineItemKind.Edge, Box = child, Inline = child, Style = child.Style, Width = child.MarginLeft + child.BorderLeft + child.PaddingLeft, Opening = true });
							Collect(child);
							_items.Add(new InlineItem { Kind = InlineItemKind.Edge, Box = child, Inline = child, Style = child.Style, Width = child.MarginRight + child.BorderRight + child.PaddingRight });
							_inline = outer;
							break;
						}
						default:
						{
							if (!_measuring)
								_engine.LayoutAtomic(child, _block.ContentWidth, -1);
							(float min, float max) = _measuring ? _engine.OuterIntrinsicWidths(child) : (child.MarginBoxWidth, child.MarginBoxWidth);
							_items.Add(new InlineItem { Kind = InlineItemKind.Atomic, Box = child, Inline = _inline, Style = child.Style, Width = max, MinWidth = min });
							_lastWasSpace = false;
							break;
						}
					}
				}
			}

			// Words and spaces per white-space: collapsing runs, preserving them, or breaking on newlines
			private void CollectText(LayoutBox box)
			{
				ComputedStyle style = box.Style;
				LayoutFont font = _engine.FontFor(style);
				string text = Transform(box.Text, style.TextTransform);
				WhiteSpace mode = style.WhiteSpace;
				bool preserveSpaces = mode == WhiteSpace.Pre || mode == WhiteSpace.PreWrap;
				bool preserveNewlines = mode != WhiteSpace.Normal && mode != WhiteSpace.NoWrap;
				bool noWrap = mode == WhiteSpace.NoWrap || mode == WhiteSpace.Pre;
				int i = 0;
				StringBuilder word = new StringBuilder();
				while (i < text.Length)
				{
					char c = text[i];
					if (c == '\n' && preserveNewlines)
					{
						FlushWord(word, box, font, style, noWrap);
						_items.Add(new InlineItem { Kind = InlineItemKind.Newline, Box = box, Style = style });
						_lastWasSpace = true;
						i++;
						continue;
					}
					if (c == '\r')
					{
						i++;
						continue;
					}
					if (HtmlText.IsWhitespace(c))
					{
						FlushWord(word, box, font, style, noWrap);
						if (preserveSpaces)
						{
							int count = c == '\t' ? 8 : 1;
							string spaces = new string(' ', count);
							_items.Add(new InlineItem { Kind = InlineItemKind.Space, Box = box, Inline = _inline, Text = spaces, Font = font, Style = style, Width = font.Measure(spaces), Preserved = true, NoWrap = noWrap });
							_lastWasSpace = false;
						}
						else if (!_lastWasSpace)
						{
							_items.Add(new InlineItem { Kind = InlineItemKind.Space, Box = box, Inline = _inline, Text = " ", Font = font, Style = style, Width = font.SpaceWidth + style.LetterSpacing, NoWrap = noWrap });
							_lastWasSpace = true;
						}
						i++;
						continue;
					}
					word.Append(c);
					i++;
				}
				FlushWord(word, box, font, style, noWrap);
			}

			private void FlushWord(StringBuilder word, LayoutBox box, LayoutFont font, ComputedStyle style, bool noWrap)
			{
				if (word.Length == 0)
					return;
				string text = word.ToString();
				word.Clear();
				float width = font.Measure(text) + style.LetterSpacing * text.Length;
				_items.Add(new InlineItem { Kind = InlineItemKind.Word, Box = box, Inline = _inline, Text = text, Font = font, Style = style, Width = width, NoWrap = noWrap });
				_lastWasSpace = false;
			}

			private static string Transform(string text, TextTransform transform)
			{
				switch (transform)
				{
					case TextTransform.Uppercase:
						return text.ToUpperInvariant();
					case TextTransform.Lowercase:
						return text.ToLowerInvariant();
					case TextTransform.Capitalize:
					{
						char[] chars = text.ToCharArray();
						bool start = true;
						for (int i = 0; i < chars.Length; i++)
						{
							if (char.IsLetter(chars[i]))
							{
								if (start)
									chars[i] = char.ToUpperInvariant(chars[i]);
								start = false;
							}
							else
								start = HtmlText.IsWhitespace(chars[i]) || chars[i] == '-';
						}
						return new string(chars);
					}
					default:
						return text;
				}
			}
		}

		// Builds the line boxes of one block: greedy breaking at spaces, floats shortening lines, alignment, strut and baseline placement
		internal sealed class LineBuilder
		{
			private readonly LayoutEngine _engine;
			private readonly LayoutBox _block;
			private readonly FloatContext _floats;
			private readonly LayoutFont _strutFont;
			private readonly float _strutLineHeight;
			private readonly List<LineFragment> _fragments = new List<LineFragment>();
			private LineFragment _run;
			private InlineItem _pendingSpace;
			private float _used;
			private float _lineLeft;
			private float _lineRight;
			private bool _firstLine = true;
			private bool _lineHasContent;

			public readonly List<LineBox> Lines = new List<LineBox>();
			public float Y;

			public LineBuilder(LayoutEngine engine, LayoutBox block, FloatContext floats)
			{
				_engine = engine;
				_block = block;
				_floats = floats;
				_strutFont = engine.FontFor(block.Style);
				_strutLineHeight = block.Style.ResolvedLineHeight;
				Y = block.ContentY;
				StartLine();
			}

			private void StartLine()
			{
				_fragments.Clear();
				_run = null;
				_pendingSpace = null;
				_used = 0;
				_lineHasContent = false;
				float indent = _firstLine ? _block.Style.TextIndent.Resolve(_block.ContentWidth) : 0;
				_lineLeft = _floats.LeftEdge(_block.ContentX, Y, Y + _strutLineHeight) + indent;
				_lineRight = _floats.RightEdge(_block.ContentX + _block.ContentWidth, Y, Y + _strutLineHeight);
			}

			private float Available => _lineRight - _lineLeft;

			public void Add(InlineItem item)
			{
				switch (item.Kind)
				{
					case InlineItemKind.Space:
						if (item.Preserved)
						{
							if (!item.NoWrap && _lineHasContent && _used + item.Width > Available)
								BreakLine(false);
							AppendText(item, item.Text, item.Width);
						}
						else if (_lineHasContent)
							_pendingSpace = item;
						break;
					case InlineItemKind.Word:
						PlaceWidth(item, item.Width, item.NoWrap);
						AppendText(item, item.Text, item.Width);
						break;
					case InlineItemKind.Edge:
						if (item.Width > 0 || item.Inline.Style.BackgroundColor.A > 0 || item.Inline.BorderTop > 0 || item.Inline.BorderBottom > 0)
						{
							if (item.Opening)
								PlaceWidth(item, item.Width, true);
							_used += item.Width;
							_fragments.Add(new LineFragment { Box = item.Inline, Inline = item.Inline, Style = item.Style, Font = _engine.FontFor(item.Style), Text = "", Width = item.Width });
							_run = null;
						}
						break;
					case InlineItemKind.Atomic:
						PlaceWidth(item, item.Width, false);
						_fragments.Add(new LineFragment { Box = item.Box, Inline = item.Inline, Style = item.Style, Width = item.Width, Height = item.Box.MarginBoxHeight, IsAtomic = true });
						_used += item.Width;
						_run = null;
						_lineHasContent = true;
						break;
					case InlineItemKind.Newline:
						BreakLine(true);
						break;
					case InlineItemKind.Float:
						PlaceFloatInLine(item);
						break;
					case InlineItemKind.Positioned:
						item.Box.StaticX = _lineLeft + _used;
						item.Box.StaticY = Y;
						Defer(_block, item.Box);
						break;
				}
			}

			// Makes room for something of the width: a pending space goes first, and a line that would overflow is broken unless it is empty or unbreakable
			private void PlaceWidth(InlineItem item, float width, bool noWrap)
			{
				float spaceWidth = _pendingSpace != null ? _pendingSpace.Width : 0;
				if (_lineHasContent && !noWrap && _used + spaceWidth + width > Available + 0.01f)
					BreakLine(false);
				else if (!_lineHasContent && width > Available && _floats.Intrudes(Y, Y + _strutLineHeight))
					MoveBelowFloats(width);
				if (_pendingSpace != null && _lineHasContent)
				{
					InlineItem space = _pendingSpace;
					_pendingSpace = null;
					AppendText(space, " ", space.Width);
				}
				_pendingSpace = null;
			}

			private void MoveBelowFloats(float width)
			{
				int guard = 0;
				while (guard++ < 64 && width > Available && _floats.Intrudes(Y, Y + _strutLineHeight))
				{
					float next = _floats.NextBottom(Y, Y + _strutLineHeight);
					if (next <= Y)
						break;
					Y = next;
					StartLine();
				}
			}

			private void AppendText(InlineItem item, string text, float width)
			{
				if (_run != null && _run.Box == item.Box && _run.Font == item.Font && _run.Inline == item.Inline && !item.IsMarker && !_run.IsMarker)
				{
					// The run is measured whole so its width matches how it will be drawn, kerning and rounding included
					_run.Text += text;
					float before = _run.Width;
					_run.Width = item.Font.Measure(_run.Text) + (item.Style?.LetterSpacing ?? 0) * _run.Text.Length;
					width = _run.Width - before;
				}
				else
				{
					_run = new LineFragment { Box = item.Box, Inline = item.Inline, Text = text, Font = item.Font, Style = item.Style, Width = width, IsMarker = item.IsMarker };
					_fragments.Add(_run);
				}
				_used += width;
				_lineHasContent = true;
			}

			private void PlaceFloatInLine(InlineItem item)
			{
				LayoutBox box = item.Box;
				_engine.PlaceFloat(box, _floats, _block.ContentX, _block.ContentWidth, Y, -1);
				float lineBottom = Y + _strutLineHeight;
				bool crosses = box.Y - box.MarginTop < lineBottom && box.Y + box.Height + box.MarginBottom > Y;
				if (!crosses)
					return;
				float left = _floats.LeftEdge(_block.ContentX, Y, lineBottom);
				float right = _floats.RightEdge(_block.ContentX + _block.ContentWidth, Y, lineBottom);
				if (right - left < _used)
				{
					// No room beside what this line already holds: it goes below the line instead
					_floats.Remove(box);
					_engine.PlaceFloat(box, _floats, _block.ContentX, _block.ContentWidth, lineBottom, -1);
					return;
				}
				_lineLeft = left + (_firstLine ? _block.Style.TextIndent.Resolve(_block.ContentWidth) : 0);
				_lineRight = right;
			}

			private void BreakLine(bool forced)
			{
				FinishLine(forced);
				_firstLine = false;
				StartLine();
			}

			public void Finish()
			{
				if (_lineHasContent)
					FinishLine(false);
				else if (Lines.Count == 0 && _fragments.Count > 0)
					FinishLine(false);
			}

			// Positions the fragments vertically around the baseline, aligns the line horizontally and records it
			private void FinishLine(bool forced)
			{
				if (!_lineHasContent && !forced)
				{
					_fragments.Clear();
					return;
				}
				float strutAscent = _strutFont.Ascent;
				float strutDescent = _strutFont.Descent;
				float strutLeading = (_strutLineHeight - (strutAscent + strutDescent)) / 2;
				float above = strutAscent + strutLeading;
				float below = strutDescent + strutLeading;
				float tallest = 0;
				foreach (LineFragment fragment in _fragments)
				{
					ComputeExtents(fragment, out float fragmentAbove, out float fragmentBelow, out bool edgeAligned);
					if (edgeAligned)
						tallest = Math.Max(tallest, fragmentAbove + fragmentBelow);
					else
					{
						above = Math.Max(above, fragmentAbove);
						below = Math.Max(below, fragmentBelow);
					}
				}
				float lineHeight = Math.Max(above + below, tallest);
				float baseline = above;
				float used = _used;
				if (_run != null && _run.Text.EndsWith(' ') && _run.Style.WhiteSpace != WhiteSpace.Pre && _run.Style.WhiteSpace != WhiteSpace.PreWrap)
				{
					float trailing = _run.Font.SpaceWidth;
					_run.Text = _run.Text.TrimEnd(' ');
					_run.Width -= trailing;
					used -= trailing;
				}
				float slack = Available - used;
				float offset = 0;
				if (slack > 0)
				{
					offset = _block.Style.TextAlign switch
					{
						TextAlign.Center => slack / 2,
						TextAlign.Right => slack,
						_ => 0,
					};
				}
				LineBox line = new LineBox { X = _lineLeft, Y = Y, Width = Math.Max(used, 0), Height = lineHeight, Baseline = baseline };
				float x = _lineLeft + offset;
				foreach (LineFragment fragment in _fragments)
				{
					ComputeExtents(fragment, out float fragmentAbove, out float fragmentBelow, out bool edgeAligned);
					fragment.X = x;
					if (fragment.IsAtomic)
					{
						LayoutBox box = fragment.Box;
						float top;
						if (fragment.Style.VerticalAlign == VerticalAlign.Top)
							top = Y;
						else if (fragment.Style.VerticalAlign == VerticalAlign.Bottom)
							top = Y + lineHeight - fragment.Height;
						else
							top = Y + baseline - fragmentAbove;
						fragment.Y = top;
						box.Translate(x + box.MarginLeft - box.X, top + box.MarginTop - box.Y);
					}
					else
					{
						fragment.Height = fragmentAbove + fragmentBelow;
						fragment.Baseline = fragmentAbove;
						if (edgeAligned)
							fragment.Y = fragment.Style.VerticalAlign == VerticalAlign.Top ? Y : Y + lineHeight - fragment.Height;
						else
							fragment.Y = Y + baseline - fragmentAbove;
					}
					x += fragment.Width;
					line.Fragments.Add(fragment);
				}
				Lines.Add(line);
				Y += lineHeight;
			}

			// How far a fragment reaches above and below the baseline, after its line height and vertical alignment
			private void ComputeExtents(LineFragment fragment, out float above, out float below, out bool edgeAligned)
			{
				VerticalAlign align = fragment.Style?.VerticalAlign ?? VerticalAlign.Baseline;
				edgeAligned = align == VerticalAlign.Top || align == VerticalAlign.Bottom;
				if (fragment.IsAtomic)
				{
					LayoutBox box = fragment.Box;
					float height = box.MarginBoxHeight;
					float baseline = box.Baseline >= 0 ? box.Baseline + box.MarginTop : height;
					if (align == VerticalAlign.Middle)
						baseline = height / 2 + _strutFont.XHeight / 2;
					else if (align == VerticalAlign.TextBottom)
						baseline = height - _strutFont.Descent;
					else if (align == VerticalAlign.TextTop)
						baseline = _strutFont.Ascent;
					above = baseline;
					below = height - baseline;
					return;
				}
				LayoutFont font = fragment.Font ?? _strutFont;
				float lineHeight = fragment.Style != null ? fragment.Style.ResolvedLineHeight : _strutLineHeight;
				float leading = (lineHeight - (font.Ascent + font.Descent)) / 2;
				float shift = 0;
				switch (align)
				{
					case VerticalAlign.Sub:
						shift = font.Size * 0.25f;
						break;
					case VerticalAlign.Super:
						shift = -font.Size * 0.4f;
						break;
					case VerticalAlign.Middle:
						shift = -_strutFont.XHeight / 2 + (font.Ascent - font.Descent) / 2 - font.Ascent / 2 + font.Descent / 2;
						break;
				}
				above = font.Ascent + leading - shift;
				below = font.Descent + leading + shift;
			}
		}
	}
}
