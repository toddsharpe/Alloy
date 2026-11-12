using System;
using System.Collections.Generic;
using Alloy.Sdk.Web.Css;

namespace Alloy.Sdk.Web.Layout
{
	// Grid layout (CSS Grid Level 1), the parts pages lean on: explicit and implicit tracks, fr and auto sizing, repeat(auto-fill), areas, spans, auto-placement, gaps, alignment
	public sealed partial class LayoutEngine
	{
		private sealed class GridItem
		{
			public LayoutBox Box;
			public int Column;
			public int ColumnSpan = 1;
			public int Row;
			public int RowSpan = 1;
			public bool ColumnPlaced;
			public bool RowPlaced;
			public float MinContent;
			public float MaxContent;
		}

		private sealed class GridPlan
		{
			public List<GridItem> Items = new List<GridItem>();
			public List<GridTrack> ColumnTracks;
			public List<GridTrack> RowTracks;
			public int ExplicitColumns;
			public int ExplicitRows;
			public int Columns;
			public int Rows;
			public float[] ColumnSizes;
			public float[] RowSizes;
		}

		// Lays out a grid container's items inside its content box; returns the content height
		private float LayoutGrid(LayoutBox box, float containingHeight)
		{
			ComputedStyle style = box.Style;
			float contentWidth = box.ContentWidth;
			float definiteHeight = !style.Height.IsAuto && (!style.Height.IsPercent || containingHeight >= 0) ? Math.Max(0, box.ContentHeightOf(style.Height.Resolve(containingHeight))) : -1;
			float columnGap = style.ColumnGap.Resolve(contentWidth);
			float rowGap = style.RowGap.Resolve(contentWidth);
			GridPlan plan = PlanGrid(box, contentWidth, columnGap);
			if (plan.Items.Count == 0 && plan.Columns == 0)
				return 0;
			plan.ColumnSizes = SizeTracks(plan.ColumnTracks, plan.Columns, contentWidth, columnGap, style.GridAutoColumns, plan.Items, true, contentWidth);

			// Items are laid out at their column widths to learn their heights, then rows are sized from them
			float[] itemHeights = new float[plan.Items.Count];
			for (int i = 0; i < plan.Items.Count; i++)
			{
				GridItem item = plan.Items[i];
				float width = SpanSize(plan.ColumnSizes, item.Column, item.ColumnSpan, columnGap);
				itemHeights[i] = LayoutGridItem(item.Box, width, definiteHeight, JustifyFor(item.Box, style), false, width);
			}
			plan.RowSizes = SizeRows(plan, rowGap, definiteHeight, contentWidth, style.GridAutoRows, itemHeights);

			float[] columnX = Offsets(plan.ColumnSizes, columnGap, box.ContentX);
			float[] rowY = Offsets(plan.RowSizes, rowGap, box.ContentY);
			for (int i = 0; i < plan.Items.Count; i++)
			{
				GridItem item = plan.Items[i];
				float cellWidth = SpanSize(plan.ColumnSizes, item.Column, item.ColumnSpan, columnGap);
				float cellHeight = SpanSize(plan.RowSizes, item.Row, item.RowSpan, rowGap);
				PlaceGridItem(item.Box, columnX[item.Column], rowY[item.Row], cellWidth, cellHeight, JustifyFor(item.Box, style), AlignFor(item.Box, style), definiteHeight);
			}
			float height = plan.RowSizes.Length == 0 ? 0 : SpanSize(plan.RowSizes, 0, plan.RowSizes.Length, rowGap);
			return definiteHeight >= 0 ? Math.Max(definiteHeight, height) : height;
		}

		private static AlignItems JustifyFor(LayoutBox item, ComputedStyle container) => item.Style.JustifySelf == AlignItems.Auto ? container.JustifyItems : item.Style.JustifySelf;

		private static AlignItems AlignFor(LayoutBox item, ComputedStyle container) => item.Style.AlignSelf == AlignItems.Auto ? container.AlignItems : item.Style.AlignSelf;

		// Expands the templates, resolves named lines and areas, and places every item (explicit positions first, then the auto-placement cursor)
		private GridPlan PlanGrid(LayoutBox box, float contentWidth, float columnGap)
		{
			ComputedStyle style = box.Style;
			GridPlan plan = new GridPlan();
			GridTemplate columns = style.GridColumns ?? new GridTemplate();
			GridTemplate rows = style.GridRows ?? new GridTemplate();
			int repeatCount = columns.HasAutoRepeat ? AutoRepeatCount(columns, contentWidth, columnGap) : 0;
			plan.ColumnTracks = columns.Expand(repeatCount);
			plan.RowTracks = rows.Expand(rows.HasAutoRepeat ? 1 : 0);
			if (style.GridAreas != null)
			{
				int areaColumns = 0;
				foreach (string[] row in style.GridAreas)
					areaColumns = Math.Max(areaColumns, row.Length);
				while (plan.ColumnTracks.Count < areaColumns)
					plan.ColumnTracks.Add(style.GridAutoColumns);
				while (plan.RowTracks.Count < style.GridAreas.Length)
					plan.RowTracks.Add(style.GridAutoRows);
			}
			plan.ExplicitColumns = plan.ColumnTracks.Count;
			plan.ExplicitRows = plan.RowTracks.Count;

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
				ResolveEdges(child, contentWidth);
				GridItem item = new GridItem { Box = child };
				(float min, float max) = OuterIntrinsicWidths(child);
				item.MinContent = min;
				item.MaxContent = max;
				ResolvePlacement(style, item, plan, columns, rows);
				plan.Items.Add(item);
			}
			plan.Items.Sort((a, b) => a.Box.Style.Order.CompareTo(b.Box.Style.Order));
			AutoPlace(plan, style.GridAutoFlow == GridAutoFlow.Column);
			while (plan.ColumnTracks.Count < plan.Columns)
				plan.ColumnTracks.Add(style.GridAutoColumns);
			while (plan.RowTracks.Count < plan.Rows)
				plan.RowTracks.Add(style.GridAutoRows);
			return plan;
		}

		// How many times an auto-fill repeat fits: by the repeat's minimum widths, at least once
		private static int AutoRepeatCount(GridTemplate template, float contentWidth, float gap)
		{
			float fixedBefore = 0;
			foreach (GridTrack track in template.Before)
				fixedBefore += TrackMinimum(track, contentWidth) + gap;
			foreach (GridTrack track in template.After)
				fixedBefore += TrackMinimum(track, contentWidth) + gap;
			float repeatWidth = 0;
			foreach (GridTrack track in template.Repeat)
				repeatWidth += TrackMinimum(track, contentWidth) + gap;
			if (repeatWidth <= 0)
				return 1;
			float available = contentWidth - fixedBefore + gap;
			return Math.Max(1, (int)Math.Floor((available + 0.01f) / repeatWidth));
		}

		private static float TrackMinimum(GridTrack track, float contentWidth)
		{
			if (track.Min.IsDefinite)
				return track.Min.Resolve(contentWidth);
			if (track.Max.IsDefinite)
				return track.Max.Resolve(contentWidth);
			return 0;
		}

		// An item's lines from its grid-row/grid-column/grid-area values: numbers, negative numbers, spans, area names and named lines
		private static void ResolvePlacement(ComputedStyle container, GridItem item, GridPlan plan, GridTemplate columns, GridTemplate rows)
		{
			ComputedStyle style = item.Box.Style;
			ResolveAxis(style.GridColumnStart, style.GridColumnEnd, plan.ExplicitColumns, container.GridAreas, columns, true, out item.Column, out item.ColumnSpan, out item.ColumnPlaced);
			ResolveAxis(style.GridRowStart, style.GridRowEnd, plan.ExplicitRows, container.GridAreas, rows, false, out item.Row, out item.RowSpan, out item.RowPlaced);
		}

		private static void ResolveAxis(GridLine start, GridLine end, int explicitCount, string[][] areas, GridTemplate template, bool columnAxis, out int index, out int span, out bool placed)
		{
			int startLine = LineNumber(start, explicitCount, areas, template, columnAxis, false);
			int endLine = LineNumber(end, explicitCount, areas, template, columnAxis, true);
			span = 1;
			if (startLine > 0 && endLine > 0)
			{
				if (endLine < startLine)
					(startLine, endLine) = (endLine, startLine);
				if (endLine == startLine)
					endLine = startLine + 1;
				index = startLine - 1;
				span = endLine - startLine;
				placed = true;
				return;
			}
			if (startLine > 0)
			{
				index = startLine - 1;
				span = end.IsSpan ? end.Span : 1;
				placed = true;
				return;
			}
			if (endLine > 0)
			{
				span = start.IsSpan ? start.Span : 1;
				index = Math.Max(0, endLine - 1 - span);
				placed = true;
				return;
			}
			span = start.IsSpan ? start.Span : end.IsSpan ? end.Span : 1;
			index = 0;
			placed = false;
		}

		// A line as a 1-based number; 0 when it is auto or a span
		private static int LineNumber(GridLine line, int explicitCount, string[][] areas, GridTemplate template, bool columnAxis, bool isEnd)
		{
			if (line.IsAuto || line.IsSpan)
				return 0;
			if (line.Name != null)
			{
				if (areas != null)
				{
					int first = int.MaxValue;
					int last = 0;
					for (int r = 0; r < areas.Length; r++)
					{
						for (int c = 0; c < areas[r].Length; c++)
						{
							if (!string.Equals(areas[r][c], line.Name, StringComparison.OrdinalIgnoreCase))
								continue;
							int position = columnAxis ? c : r;
							first = Math.Min(first, position);
							last = Math.Max(last, position);
						}
					}
					if (first != int.MaxValue)
						return isEnd ? last + 2 : first + 1;
				}
				string implicitName = line.Name;
				if (template.LineNames.TryGetValue(implicitName, out List<int> named) && named.Count > 0)
					return named[0] > 0 ? named[0] : explicitCount + 2 + named[0];
				if (template.LineNames.TryGetValue(line.Name + (isEnd ? "-end" : "-start"), out List<int> edge) && edge.Count > 0)
					return edge[0] > 0 ? edge[0] : explicitCount + 2 + edge[0];
				return 0;
			}
			if (line.Line < 0)
				return Math.Max(1, explicitCount + 2 + line.Line);
			return line.Line;
		}

		// Auto-placement: fixed items are marked first, the rest take the next free cells in order, growing the implicit axis
		private static void AutoPlace(GridPlan plan, bool columnFlow)
		{
			HashSet<(int, int)> occupied = new HashSet<(int, int)>();
			int columns = Math.Max(1, plan.ExplicitColumns);
			int rows = Math.Max(1, plan.ExplicitRows);
			foreach (GridItem item in plan.Items)
			{
				if (item.ColumnPlaced && item.RowPlaced)
				{
					Occupy(occupied, item);
					columns = Math.Max(columns, item.Column + item.ColumnSpan);
					rows = Math.Max(rows, item.Row + item.RowSpan);
				}
			}
			if (!columnFlow)
			{
				foreach (GridItem item in plan.Items)
				{
					if (item.RowPlaced && !item.ColumnPlaced)
					{
						item.Column = 0;
						while (Collides(occupied, item))
							item.Column++;
						Occupy(occupied, item);
						columns = Math.Max(columns, item.Column + item.ColumnSpan);
						rows = Math.Max(rows, item.Row + item.RowSpan);
					}
				}
			}
			int cursorColumn = 0;
			int cursorRow = 0;
			foreach (GridItem item in plan.Items)
			{
				if (item.ColumnPlaced && item.RowPlaced)
					continue;
				if (!columnFlow && item.RowPlaced)
					continue;
				if (columnFlow)
				{
					if (item.ColumnPlaced)
					{
						item.Row = 0;
						while (Collides(occupied, item))
							item.Row++;
					}
					else
					{
						item.Column = cursorColumn;
						item.Row = cursorRow;
						while (true)
						{
							if (item.Row + item.RowSpan > rows && item.Row > 0)
							{
								item.Row = 0;
								item.Column++;
							}
							if (!Collides(occupied, item))
								break;
							item.Row++;
						}
						cursorColumn = item.Column;
						cursorRow = item.Row;
					}
				}
				else
				{
					if (item.ColumnPlaced)
					{
						item.Row = item.Column < cursorColumn ? cursorRow + 1 : cursorRow;
						while (Collides(occupied, item))
							item.Row++;
						cursorColumn = item.Column;
						cursorRow = item.Row;
					}
					else
					{
						item.Column = cursorColumn;
						item.Row = cursorRow;
						while (true)
						{
							if (item.Column + item.ColumnSpan > columns && item.Column > 0)
							{
								item.Column = 0;
								item.Row++;
							}
							if (item.Column + item.ColumnSpan > columns && item.Column == 0)
							{
								if (!Collides(occupied, item))
									break;
								item.Row++;
								continue;
							}
							if (!Collides(occupied, item))
								break;
							item.Column++;
						}
						cursorColumn = item.Column;
						cursorRow = item.Row;
					}
				}
				Occupy(occupied, item);
				columns = Math.Max(columns, item.Column + item.ColumnSpan);
				rows = Math.Max(rows, item.Row + item.RowSpan);
			}
			plan.Columns = plan.Items.Count == 0 ? plan.ExplicitColumns : columns;
			plan.Rows = plan.Items.Count == 0 ? plan.ExplicitRows : rows;
		}

		private static bool Collides(HashSet<(int, int)> occupied, GridItem item)
		{
			for (int c = item.Column; c < item.Column + item.ColumnSpan; c++)
			{
				for (int r = item.Row; r < item.Row + item.RowSpan; r++)
				{
					if (occupied.Contains((c, r)))
						return true;
				}
			}
			return false;
		}

		private static void Occupy(HashSet<(int, int)> occupied, GridItem item)
		{
			for (int c = item.Column; c < item.Column + item.ColumnSpan; c++)
			{
				for (int r = item.Row; r < item.Row + item.RowSpan; r++)
					occupied.Add((c, r));
			}
		}

		// Track sizing (CSS Grid 11): fixed tracks as written, content tracks from their items, then free space to fr tracks or stretched autos
		private float[] SizeTracks(List<GridTrack> tracks, int count, float available, float gap, GridTrack implicitTrack, List<GridItem> items, bool columns, float contentWidth)
		{
			float[] sizes = new float[count];
			float[] limits = new float[count];
			bool[] flexible = new bool[count];
			float[] flex = new float[count];
			bool[] content = new bool[count];
			for (int i = 0; i < count; i++)
			{
				GridTrack track = i < tracks.Count ? tracks[i] : implicitTrack;
				GridSize min = track.Min;
				GridSize max = track.Max;
				if (min.IsDefinite)
					sizes[i] = min.Resolve(contentWidth);
				else
					content[i] = true;
				if (max.IsFlexible)
				{
					flexible[i] = true;
					flex[i] = max.Value;
					limits[i] = float.MaxValue;
				}
				else if (max.IsDefinite)
					limits[i] = max.Resolve(contentWidth);
				else
					limits[i] = float.MaxValue;
				if (min.Kind == GridSizeKind.Fixed || min.Kind == GridSizeKind.Percent)
					content[i] = false;
			}
			// Content-sized tracks take the largest min-content of the items that sit in them alone; auto tracks may grow to max-content
			float[] wanted = new float[count];
			foreach (GridItem item in items)
			{
				int start = columns ? item.Column : item.Row;
				int span = columns ? item.ColumnSpan : item.RowSpan;
				if (span != 1 || start >= count)
					continue;
				GridTrack track = start < tracks.Count ? tracks[start] : implicitTrack;
				float minimum = track.Min.Kind == GridSizeKind.MaxContent ? item.MaxContent : item.MinContent;
				if (content[start])
					sizes[start] = Math.Max(sizes[start], Math.Min(minimum, limits[start]));
				float preferred = track.Max.Kind == GridSizeKind.MinContent ? item.MinContent : item.MaxContent;
				if (!flexible[start] && !track.Max.IsDefinite)
					wanted[start] = Math.Max(wanted[start], preferred);
			}
			foreach (GridItem item in items)
			{
				int start = columns ? item.Column : item.Row;
				int span = columns ? item.ColumnSpan : item.RowSpan;
				if (span <= 1 || start + span > count)
					continue;
				float have = gap * (span - 1);
				int growable = 0;
				for (int i = start; i < start + span; i++)
				{
					have += sizes[i];
					if (content[i] && !flexible[i])
						growable++;
				}
				if (item.MinContent > have && growable > 0)
				{
					float extra = (item.MinContent - have) / growable;
					for (int i = start; i < start + span; i++)
					{
						if (content[i] && !flexible[i])
							sizes[i] = Math.Min(limits[i], sizes[i] + extra);
					}
				}
			}
			// fr tracks are not counted yet: they are sized from the space the others leave, each keeping at least its base size
			float used = gap * Math.Max(0, count - 1);
			float totalFlex = 0;
			for (int i = 0; i < count; i++)
			{
				if (flexible[i])
					totalFlex += flex[i];
				else
					used += sizes[i];
			}
			float free = available - used;
			if (free > 0 && totalFlex > 0)
			{
				for (int pass = 0; pass < 4 && totalFlex > 0; pass++)
				{
					float perFr = free / totalFlex;
					bool settled = true;
					for (int i = 0; i < count; i++)
					{
						if (!flexible[i] || flex[i] <= 0)
							continue;
						if (perFr * flex[i] < sizes[i])
						{
							free -= sizes[i];
							totalFlex -= flex[i];
							flex[i] = 0;
							settled = false;
						}
					}
					if (settled)
					{
						for (int i = 0; i < count; i++)
						{
							if (flexible[i] && flex[i] > 0)
								sizes[i] = perFr * flex[i];
						}
						break;
					}
				}
			}
			else if (free > 0)
			{
				// No fr tracks: auto tracks grow toward their max-content, then whatever is left stretches them equally
				int autos = 0;
				for (int i = 0; i < count; i++)
				{
					if (content[i] && limits[i] == float.MaxValue)
						autos++;
				}
				if (autos > 0)
				{
					for (int pass = 0; pass < 2 && free > 0.01f; pass++)
					{
						int hungry = 0;
						for (int i = 0; i < count; i++)
						{
							if (content[i] && limits[i] == float.MaxValue && wanted[i] > sizes[i])
								hungry++;
						}
						if (hungry == 0)
							break;
						float share = free / hungry;
						for (int i = 0; i < count; i++)
						{
							if (!(content[i] && limits[i] == float.MaxValue && wanted[i] > sizes[i]))
								continue;
							float grow = Math.Min(share, wanted[i] - sizes[i]);
							sizes[i] += grow;
							free -= grow;
						}
					}
					if (free > 0.01f)
					{
						float share = free / autos;
						for (int i = 0; i < count; i++)
						{
							if (content[i] && limits[i] == float.MaxValue)
								sizes[i] += share;
						}
					}
				}
			}
			return sizes;
		}

		private float[] SizeRows(GridPlan plan, float gap, float definiteHeight, float contentWidth, GridTrack implicitTrack, float[] itemHeights)
		{
			int count = plan.Rows;
			float[] sizes = new float[count];
			bool[] flexible = new bool[count];
			float[] flex = new float[count];
			for (int i = 0; i < count; i++)
			{
				GridTrack track = i < plan.RowTracks.Count ? plan.RowTracks[i] : implicitTrack;
				if (track.Min.IsDefinite)
					sizes[i] = track.Min.Kind == GridSizeKind.Percent && definiteHeight < 0 ? 0 : track.Min.Resolve(definiteHeight);
				if (track.Max.IsFlexible)
				{
					flexible[i] = true;
					flex[i] = track.Max.Value;
				}
				else if (track.Max.IsDefinite && !(track.Max.Kind == GridSizeKind.Percent && definiteHeight < 0))
					sizes[i] = Math.Max(sizes[i], track.Max.Resolve(definiteHeight));
			}
			for (int n = 0; n < plan.Items.Count; n++)
			{
				GridItem item = plan.Items[n];
				GridTrack track = item.Row < plan.RowTracks.Count ? plan.RowTracks[item.Row] : implicitTrack;
				bool contentSized = !(track.Min.IsDefinite && track.Max.IsDefinite) && !(track.Max.IsDefinite && !track.Max.IsFlexible && definiteHeight >= 0);
				if (item.RowSpan == 1)
				{
					if (contentSized || (flexible[item.Row] && definiteHeight < 0))
						sizes[item.Row] = Math.Max(sizes[item.Row], itemHeights[n]);
					continue;
				}
				float have = gap * (item.RowSpan - 1);
				int growable = 0;
				for (int i = item.Row; i < item.Row + item.RowSpan && i < count; i++)
				{
					have += sizes[i];
					GridTrack spanned = i < plan.RowTracks.Count ? plan.RowTracks[i] : implicitTrack;
					if (!spanned.Max.IsDefinite)
						growable++;
				}
				if (itemHeights[n] > have && growable > 0)
				{
					float extra = (itemHeights[n] - have) / growable;
					for (int i = item.Row; i < item.Row + item.RowSpan && i < count; i++)
					{
						GridTrack spanned = i < plan.RowTracks.Count ? plan.RowTracks[i] : implicitTrack;
						if (!spanned.Max.IsDefinite)
							sizes[i] += extra;
					}
				}
			}
			if (definiteHeight >= 0)
			{
				float used = gap * Math.Max(0, count - 1);
				float totalFlex = 0;
				for (int i = 0; i < count; i++)
				{
					used += flexible[i] ? 0 : sizes[i];
					if (flexible[i])
						totalFlex += flex[i];
				}
				float free = definiteHeight - used;
				if (totalFlex > 0 && free > 0)
				{
					for (int i = 0; i < count; i++)
					{
						if (flexible[i])
							sizes[i] = Math.Max(sizes[i], free * flex[i] / totalFlex);
					}
				}
			}
			return sizes;
		}

		private static float SpanSize(float[] sizes, int start, int span, float gap)
		{
			float total = 0;
			for (int i = start; i < start + span && i < sizes.Length; i++)
				total += sizes[i];
			return total + gap * Math.Max(0, Math.Min(span, sizes.Length - start) - 1);
		}

		private static float[] Offsets(float[] sizes, float gap, float origin)
		{
			float[] offsets = new float[sizes.Length + 1];
			offsets[0] = origin;
			for (int i = 0; i < sizes.Length; i++)
				offsets[i + 1] = offsets[i] + sizes[i] + gap;
			return offsets;
		}

		// Lays an item out inside a cell of the given width: stretched to it unless it has a width or asks for another justification; returns its outer height
		private float LayoutGridItem(LayoutBox child, float cellWidth, float definiteHeight, AlignItems justify, bool placed, float contentWidth)
		{
			ResolveEdges(child, contentWidth);
			bool autoMargins = child.Style.Margin.Left.IsAuto || child.Style.Margin.Right.IsAuto;
			bool stretch = justify == AlignItems.Stretch && child.Style.Width.IsAuto && !autoMargins;
			if (stretch)
			{
				child.Width = Math.Max(child.HorizontalEdges, cellWidth - child.MarginLeft - child.MarginRight);
				child.Width = Math.Max(child.HorizontalEdges, ClampWidth(child, child.Width - child.HorizontalEdges, contentWidth) + child.HorizontalEdges);
			}
			else
				ResolveWidth(child, cellWidth, true);
			child.X = child.MarginLeft;
			child.Y = child.MarginTop;
			LayoutContents(child, definiteHeight, null, true);
			return child.MarginBoxHeight;
		}

		// Positions a laid-out item in its cell per justify-self and align-self, stretching its height when asked
		private void PlaceGridItem(LayoutBox child, float cellX, float cellY, float cellWidth, float cellHeight, AlignItems justify, AlignItems align, float definiteHeight)
		{
			float freeX = cellWidth - child.MarginBoxWidth;
			float dx = 0;
			if (child.Style.Margin.Left.IsAuto && child.Style.Margin.Right.IsAuto)
				dx = Math.Max(0, freeX / 2);
			else if (child.Style.Margin.Left.IsAuto)
				dx = Math.Max(0, freeX);
			else
			{
				switch (justify)
				{
					case AlignItems.Center:
						dx = freeX / 2;
						break;
					case AlignItems.FlexEnd:
						dx = freeX;
						break;
				}
			}
			bool stretchHeight = align == AlignItems.Stretch && child.Style.Height.IsAuto && !child.Style.Margin.Top.IsAuto && !child.Style.Margin.Bottom.IsAuto;
			if (stretchHeight)
				child.Height = Math.Max(child.Height, cellHeight - child.MarginTop - child.MarginBottom);
			float freeY = cellHeight - child.MarginBoxHeight;
			float dy = 0;
			if (child.Style.Margin.Top.IsAuto && child.Style.Margin.Bottom.IsAuto)
				dy = Math.Max(0, freeY / 2);
			else if (child.Style.Margin.Top.IsAuto)
				dy = Math.Max(0, freeY);
			else
			{
				switch (align)
				{
					case AlignItems.Center:
						dy = freeY / 2;
						break;
					case AlignItems.FlexEnd:
						dy = freeY;
						break;
				}
			}
			child.Translate(cellX + child.MarginLeft + dx - child.X, cellY + child.MarginTop + dy - child.Y);
			if (child.Style.Position == Position.Relative)
				OffsetRelative(child, cellWidth, definiteHeight);
		}

		// A grid's intrinsic widths: fixed columns as written, content columns from their items, fr columns from their items' max-content
		private (float Min, float Max) GridIntrinsicWidths(LayoutBox box)
		{
			ComputedStyle style = box.Style;
			float gap = style.ColumnGap.Resolve(0);
			GridPlan plan = PlanGrid(box, 0, gap);
			if (plan.Columns == 0)
				return (0, 0);
			float[] minColumns = new float[plan.Columns];
			float[] maxColumns = new float[plan.Columns];
			for (int i = 0; i < plan.Columns; i++)
			{
				GridTrack track = i < plan.ColumnTracks.Count ? plan.ColumnTracks[i] : style.GridAutoColumns;
				if (track.Min.Kind == GridSizeKind.Fixed)
					minColumns[i] = track.Min.Value;
				if (track.Max.Kind == GridSizeKind.Fixed)
					maxColumns[i] = track.Max.Value;
			}
			foreach (GridItem item in plan.Items)
			{
				if (item.ColumnSpan != 1 || item.Column >= plan.Columns)
					continue;
				GridTrack track = item.Column < plan.ColumnTracks.Count ? plan.ColumnTracks[item.Column] : style.GridAutoColumns;
				if (track.Min.Kind != GridSizeKind.Fixed)
					minColumns[item.Column] = Math.Max(minColumns[item.Column], item.MinContent);
				if (track.Max.Kind != GridSizeKind.Fixed)
					maxColumns[item.Column] = Math.Max(maxColumns[item.Column], item.MaxContent);
			}
			float min = gap * (plan.Columns - 1);
			float max = min;
			for (int i = 0; i < plan.Columns; i++)
			{
				min += minColumns[i];
				max += Math.Max(minColumns[i], maxColumns[i]);
			}
			return (min, max);
		}
	}
}
