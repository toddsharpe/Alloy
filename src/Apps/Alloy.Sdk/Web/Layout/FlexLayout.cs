using System;
using System.Collections.Generic;
using Alloy.Sdk.Web.Css;

namespace Alloy.Sdk.Web.Layout
{
	// Flexible box layout (CSS Flexbox Level 1), the parts pages lean on: both directions, wrapping, grow and shrink, justify-content, align-items and gaps
	public sealed partial class LayoutEngine
	{
		private sealed class FlexItem
		{
			public LayoutBox Box;
			public float Basis;
			public float MinMain;
			public float MaxMain = float.MaxValue;
			public float Main;
			public float Cross;
			public float MarginMainStart;
			public float MarginMainEnd;
			public float MarginCrossStart;
			public float MarginCrossEnd;
			public bool AutoMainStart;
			public bool AutoMainEnd;
			public bool AutoCrossStart;
			public bool AutoCrossEnd;
			public bool Frozen;

			public float OuterMain => Main + MarginMainStart + MarginMainEnd;

			public float OuterCross => Cross + MarginCrossStart + MarginCrossEnd;
		}

		// Lays out a flex container's items inside its content box; returns the content height
		private float LayoutFlex(LayoutBox box, float containingHeight)
		{
			ComputedStyle style = box.Style;
			bool row = style.FlexDirection == FlexDirection.Row || style.FlexDirection == FlexDirection.RowReverse;
			bool reverse = style.FlexDirection == FlexDirection.RowReverse || style.FlexDirection == FlexDirection.ColumnReverse;
			float contentWidth = box.ContentWidth;
			float definiteHeight = !style.Height.IsAuto && (!style.Height.IsPercent || containingHeight >= 0) ? Math.Max(0, box.ContentHeightOf(style.Height.Resolve(containingHeight))) : -1;
			float mainSize = row ? contentWidth : definiteHeight;
			float mainGap = (row ? style.ColumnGap : style.RowGap).Resolve(contentWidth);
			float crossGap = (row ? style.RowGap : style.ColumnGap).Resolve(contentWidth);

			List<FlexItem> items = new List<FlexItem>();
			foreach (LayoutBox child in box.Children)
			{
				if (child.IsAbsolute)
				{
					child.StaticX = box.ContentX;
					child.StaticY = box.ContentY;
					Defer(box, child);
					continue;
				}
				if (child.Kind == BoxKind.Text || child.Kind == BoxKind.LineBreak)
					continue;
				items.Add(MeasureItem(child, row, contentWidth, definiteHeight));
			}
			items.Sort((a, b) => a.Box.Style.Order.CompareTo(b.Box.Style.Order));
			if (reverse)
				items.Reverse();

			// Lines: everything on one unless wrapping, then as many items as fit by their hypothetical sizes
			List<List<FlexItem>> lines = new List<List<FlexItem>>();
			List<FlexItem> line = new List<FlexItem>();
			float used = 0;
			foreach (FlexItem item in items)
			{
				float hypothetical = Math.Clamp(item.Basis, item.MinMain, item.MaxMain) + item.MarginMainStart + item.MarginMainEnd;
				if (style.FlexWrap && mainSize >= 0 && line.Count > 0 && used + mainGap + hypothetical > mainSize + 0.01f)
				{
					lines.Add(line);
					line = new List<FlexItem>();
					used = 0;
				}
				used += (line.Count > 0 ? mainGap : 0) + hypothetical;
				line.Add(item);
			}
			if (line.Count > 0)
				lines.Add(line);

			float crossCursor = row ? box.ContentY : box.ContentX;
			float crossStart = crossCursor;
			float containerCross = row ? definiteHeight : contentWidth;
			bool singleLineFills = lines.Count == 1 && containerCross >= 0;
			for (int l = 0; l < lines.Count; l++)
			{
				List<FlexItem> current = lines[l];
				float available = mainSize >= 0 ? mainSize : float.MaxValue;
				ResolveFlexibleLengths(current, available, mainGap);
				float lineCross = 0;
				foreach (FlexItem item in current)
				{
					LayoutItem(item, row, contentWidth, definiteHeight, item.Box.Style.AlignSelf == AlignItems.Auto ? style.AlignItems : item.Box.Style.AlignSelf);
					lineCross = Math.Max(lineCross, item.OuterCross);
				}
				if (singleLineFills)
					lineCross = Math.Max(lineCross, containerCross);
				PlaceLine(box, current, row, reverse, mainSize, mainGap, crossCursor, lineCross);
				crossCursor += lineCross + (l < lines.Count - 1 ? crossGap : 0);
			}
			float crossExtent = crossCursor - crossStart;
			if (row)
				return crossExtent;
			// Column: the content height is the longest line's main extent
			float tallest = 0;
			foreach (List<FlexItem> current in lines)
			{
				float extent = 0;
				for (int i = 0; i < current.Count; i++)
					extent += current[i].OuterMain + (i > 0 ? mainGap : 0);
				tallest = Math.Max(tallest, extent);
			}
			return mainSize >= 0 ? Math.Max(mainSize, tallest) : tallest;
		}

		// The item's flex base size and its min and max along the main axis
		private FlexItem MeasureItem(LayoutBox child, bool row, float contentWidth, float definiteHeight)
		{
			ResolveEdges(child, contentWidth);
			ComputedStyle style = child.Style;
			FlexItem item = new FlexItem { Box = child };
			if (row)
			{
				item.MarginMainStart = child.MarginLeft;
				item.MarginMainEnd = child.MarginRight;
				item.MarginCrossStart = child.MarginTop;
				item.MarginCrossEnd = child.MarginBottom;
				item.AutoMainStart = style.Margin.Left.IsAuto;
				item.AutoMainEnd = style.Margin.Right.IsAuto;
				item.AutoCrossStart = style.Margin.Top.IsAuto;
				item.AutoCrossEnd = style.Margin.Bottom.IsAuto;
			}
			else
			{
				item.MarginMainStart = child.MarginTop;
				item.MarginMainEnd = child.MarginBottom;
				item.MarginCrossStart = child.MarginLeft;
				item.MarginCrossEnd = child.MarginRight;
				item.AutoMainStart = style.Margin.Top.IsAuto;
				item.AutoMainEnd = style.Margin.Bottom.IsAuto;
				item.AutoCrossStart = style.Margin.Left.IsAuto;
				item.AutoCrossEnd = style.Margin.Right.IsAuto;
			}
			float edges = row ? child.HorizontalEdges : child.VerticalEdges;
			(float minContent, float maxContent) = IntrinsicWidths(child);
			Length basis = style.FlexBasis;
			Length size = row ? style.Width : style.Height;
			float containing = row ? contentWidth : definiteHeight;
			if (!basis.IsAuto && (!basis.IsPercent || containing >= 0))
				item.Basis = Math.Max(0, row ? child.ContentWidthOf(basis.Resolve(containing)) : child.ContentHeightOf(basis.Resolve(containing))) + edges;
			else if (!size.IsAuto && (!size.IsPercent || containing >= 0))
				item.Basis = Math.Max(0, row ? child.ContentWidthOf(size.Resolve(containing)) : child.ContentHeightOf(size.Resolve(containing))) + edges;
			else if (row)
				item.Basis = Math.Min(maxContent, Math.Max(0, contentWidth - item.MarginMainStart - item.MarginMainEnd - edges)) + edges;
			else
				item.Basis = ContentHeightAtWidth(child, contentWidth, definiteHeight);
			if (row)
			{
				item.MinMain = Math.Max(child.ContentWidthOf(style.MinWidth.Resolve(contentWidth)), style.Overflow == Overflow.Visible ? Math.Min(minContent, item.Basis - edges) : 0) + edges;
				if (!style.MaxWidth.IsAuto)
					item.MaxMain = Math.Max(0, child.ContentWidthOf(style.MaxWidth.Resolve(contentWidth))) + edges;
			}
			else
			{
				item.MinMain = (style.MinHeight.IsPercent && definiteHeight < 0 ? 0 : child.ContentHeightOf(style.MinHeight.Resolve(definiteHeight))) + edges;
				if (!style.MaxHeight.IsAuto && (!style.MaxHeight.IsPercent || definiteHeight >= 0))
					item.MaxMain = Math.Max(0, child.ContentHeightOf(style.MaxHeight.Resolve(definiteHeight))) + edges;
			}
			item.MaxMain = Math.Max(item.MaxMain, item.MinMain);
			return item;
		}

		// A column item's natural height: laid out at the container's width
		private float ContentHeightAtWidth(LayoutBox child, float contentWidth, float definiteHeight)
		{
			ResolveEdges(child, contentWidth);
			ResolveWidth(child, contentWidth, false);
			child.X = child.MarginLeft;
			child.Y = child.MarginTop;
			LayoutContents(child, definiteHeight, null, true);
			return child.Height;
		}

		// Grows or shrinks the items of one line to fill it (CSS Flexbox 9.7), freezing items that hit their min or max
		private static void ResolveFlexibleLengths(List<FlexItem> line, float available, float gap)
		{
			foreach (FlexItem item in line)
			{
				item.Main = Math.Clamp(item.Basis, item.MinMain, item.MaxMain);
				item.Frozen = false;
			}
			if (available == float.MaxValue)
				return;
			float outerSum = 0;
			foreach (FlexItem item in line)
				outerSum += item.OuterMain;
			outerSum += gap * (line.Count - 1);
			bool growing = outerSum < available;
			foreach (FlexItem item in line)
			{
				float factor = growing ? item.Box.Style.FlexGrow : item.Box.Style.FlexShrink;
				if (factor == 0 || (growing && item.Basis > item.Main) || (!growing && item.Basis < item.Main))
					item.Frozen = true;
			}
			for (int pass = 0; pass < 8; pass++)
			{
				float free = available - gap * (line.Count - 1);
				float totalFactor = 0;
				float scaledShrink = 0;
				foreach (FlexItem item in line)
				{
					free -= item.Frozen ? item.OuterMain : item.Basis + item.MarginMainStart + item.MarginMainEnd;
					if (!item.Frozen)
					{
						totalFactor += growing ? item.Box.Style.FlexGrow : item.Box.Style.FlexShrink;
						scaledShrink += item.Box.Style.FlexShrink * item.Basis;
					}
				}
				if (totalFactor <= 0)
					break;
				if (growing && totalFactor < 1)
					free *= totalFactor;
				bool clamped = false;
				foreach (FlexItem item in line)
				{
					if (item.Frozen)
						continue;
					float target = growing
						? item.Basis + free * item.Box.Style.FlexGrow / totalFactor
						: item.Basis + (scaledShrink > 0 ? free * (item.Box.Style.FlexShrink * item.Basis) / scaledShrink : 0);
					float limited = Math.Clamp(target, item.MinMain, item.MaxMain);
					if (Math.Abs(limited - target) > 0.01f)
					{
						item.Main = limited;
						item.Frozen = true;
						clamped = true;
					}
					else
						item.Main = target;
				}
				if (!clamped)
					break;
			}
		}

		// Lays the item out with its main size settled and reads its cross size
		private void LayoutItem(FlexItem item, bool row, float contentWidth, float definiteHeight, AlignItems align)
		{
			LayoutBox child = item.Box;
			ResolveEdges(child, contentWidth);
			if (row)
			{
				child.Width = Math.Max(child.HorizontalEdges, item.Main);
				child.X = child.MarginLeft;
				child.Y = child.MarginTop;
				LayoutContents(child, definiteHeight, null, true);
				item.Cross = child.Height;
			}
			else
			{
				// A column item with no width of its own fills the line when stretched and fits its content otherwise
				bool shrinkToFit = align != AlignItems.Stretch && child.Style.Width.IsAuto;
				ResolveWidth(child, contentWidth, shrinkToFit);
				child.X = child.MarginLeft;
				child.Y = child.MarginTop;
				LayoutContents(child, definiteHeight, null, true);
				child.Height = Math.Max(child.VerticalEdges, item.Main);
				item.Cross = child.Width;
			}
		}

		// Positions a line's items along the main axis per justify-content and auto margins, and across per align-items
		private void PlaceLine(LayoutBox box, List<FlexItem> line, bool row, bool reverse, float mainSize, float gap, float crossStart, float lineCross)
		{
			ComputedStyle style = box.Style;
			float used = gap * (line.Count - 1);
			int autoMargins = 0;
			foreach (FlexItem item in line)
			{
				used += item.OuterMain;
				autoMargins += (item.AutoMainStart ? 1 : 0) + (item.AutoMainEnd ? 1 : 0);
			}
			float free = mainSize >= 0 ? Math.Max(0, mainSize - used) : 0;
			float autoShare = autoMargins > 0 ? free / autoMargins : 0;
			float offset = 0;
			float between = 0;
			if (autoMargins == 0)
			{
				// In a reversed direction the main start is the far edge, so start and end swap sides
				switch (style.JustifyContent)
				{
					case JustifyContent.FlexStart:
						offset = reverse ? free : 0;
						break;
					case JustifyContent.FlexEnd:
						offset = reverse ? 0 : free;
						break;
					case JustifyContent.Center:
						offset = free / 2;
						break;
					case JustifyContent.SpaceBetween:
						between = line.Count > 1 ? free / (line.Count - 1) : 0;
						break;
					case JustifyContent.SpaceAround:
						between = free / line.Count;
						offset = between / 2;
						break;
					case JustifyContent.SpaceEvenly:
						between = free / (line.Count + 1);
						offset = between;
						break;
				}
			}
			float mainCursor = (row ? box.ContentX : box.ContentY) + offset;
			foreach (FlexItem item in line)
			{
				float marginStart = item.MarginMainStart + (item.AutoMainStart ? autoShare : 0);
				float marginEnd = item.MarginMainEnd + (item.AutoMainEnd ? autoShare : 0);
				AlignItems align = item.Box.Style.AlignSelf == AlignItems.Auto ? style.AlignItems : item.Box.Style.AlignSelf;
				float crossFree = lineCross - item.OuterCross;
				float crossOffset = 0;
				if (item.AutoCrossStart && item.AutoCrossEnd)
					crossOffset = crossFree / 2;
				else if (item.AutoCrossStart)
					crossOffset = crossFree;
				else if (!item.AutoCrossEnd)
				{
					switch (align)
					{
						case AlignItems.FlexEnd:
							crossOffset = crossFree;
							break;
						case AlignItems.Center:
							crossOffset = crossFree / 2;
							break;
						case AlignItems.Stretch:
							if (row ? item.Box.Style.Height.IsAuto : item.Box.Style.Width.IsAuto)
								StretchItem(item, row, lineCross);
							break;
					}
				}
				LayoutBox child = item.Box;
				float targetX = row ? mainCursor + marginStart : crossStart + item.MarginCrossStart + crossOffset;
				float targetY = row ? crossStart + item.MarginCrossStart + crossOffset : mainCursor + marginStart;
				child.Translate(targetX - child.X, targetY - child.Y);
				if (child.Style.Position == Position.Relative)
					OffsetRelative(child, box.ContentWidth, -1);
				mainCursor += marginStart + item.Main + marginEnd + gap + between;
			}
		}

		// Stretch: the item fills the line's cross size, which for a row means its height and for a column a wider layout
		private void StretchItem(FlexItem item, bool row, float lineCross)
		{
			LayoutBox child = item.Box;
			float target = Math.Max(0, lineCross - item.MarginCrossStart - item.MarginCrossEnd);
			if (row)
			{
				if (!child.Style.MaxHeight.IsAuto && !child.Style.MaxHeight.IsPercent)
					target = Math.Min(target, child.ContentHeightOf(child.Style.MaxHeight.Value) + child.VerticalEdges);
				child.Height = Math.Max(child.Height, target);
				item.Cross = child.Height;
				return;
			}
			if (Math.Abs(child.Width - target) < 0.5f)
				return;
			float main = item.Main;
			child.Width = Math.Max(child.HorizontalEdges, target);
			float x = child.X;
			float y = child.Y;
			child.Translate(child.MarginLeft - x, child.MarginTop - y);
			LayoutContents(child, -1, null, true);
			child.Height = Math.Max(child.VerticalEdges, main);
			child.Translate(x - child.X, y - child.Y);
			item.Cross = child.Width;
		}

		// A flex container's intrinsic widths: a row sums its items (wrapping lets the minimum be the widest item), a column takes the widest
		private (float Min, float Max) FlexIntrinsicWidths(LayoutBox box)
		{
			ComputedStyle style = box.Style;
			bool row = style.FlexDirection == FlexDirection.Row || style.FlexDirection == FlexDirection.RowReverse;
			float gap = (row ? style.ColumnGap : style.RowGap).Resolve(0);
			float min = 0;
			float max = 0;
			float sumMin = 0;
			int count = 0;
			foreach (LayoutBox child in box.Children)
			{
				if (child.IsAbsolute || child.Kind == BoxKind.Text || child.Kind == BoxKind.LineBreak)
					continue;
				(float childMin, float childMax) = OuterIntrinsicWidths(child);
				if (!child.Style.FlexBasis.IsAuto && !child.Style.FlexBasis.IsPercent)
					childMax = Math.Max(childMin, child.ContentWidthOf(child.Style.FlexBasis.Value) + child.HorizontalEdges + Math.Max(0, child.MarginLeft) + Math.Max(0, child.MarginRight));
				min = Math.Max(min, childMin);
				sumMin += childMin;
				if (row)
					max += childMax;
				else
					max = Math.Max(max, childMax);
				count++;
			}
			if (row)
			{
				float gaps = gap * Math.Max(0, count - 1);
				max += gaps;
				min = style.FlexWrap ? min : sumMin + gaps;
			}
			return (Math.Min(min, max), max);
		}
	}
}
