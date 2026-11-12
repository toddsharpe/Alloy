using System;
using System.Collections.Generic;

namespace Alloy.Sdk.Web.Css
{
	public enum Display
	{
		Inline,
		Block,
		InlineBlock,
		ListItem,
		None,
		Table,
		TableRow,
		TableCell,
		TableRowGroup,
		TableHeaderGroup,
		TableFooterGroup,
		TableCaption,
		TableColumn,
		TableColumnGroup,
		Flex,
		InlineFlex,
		Grid,
		InlineGrid,
	}

	public enum FlexDirection
	{
		Row,
		RowReverse,
		Column,
		ColumnReverse,
	}

	public enum JustifyContent
	{
		FlexStart,
		FlexEnd,
		Center,
		SpaceBetween,
		SpaceAround,
		SpaceEvenly,
	}

	public enum AlignItems
	{
		Stretch,
		FlexStart,
		FlexEnd,
		Center,
		Baseline,
		Auto,
	}

	public enum FloatSide
	{
		None,
		Left,
		Right,
	}

	public enum ClearSide
	{
		None,
		Left,
		Right,
		Both,
	}

	public enum TextAlign
	{
		Left,
		Right,
		Center,
		Justify,
	}

	public enum WhiteSpace
	{
		Normal,
		Pre,
		NoWrap,
		PreWrap,
		PreLine,
	}

	public enum ListStyleType
	{
		Disc,
		Circle,
		Square,
		Decimal,
		LowerAlpha,
		UpperAlpha,
		LowerRoman,
		UpperRoman,
		None,
	}

	public enum FontFamily
	{
		Sans,
		Serif,
		Mono,
	}

	public enum TextTransform
	{
		None,
		Uppercase,
		Lowercase,
		Capitalize,
	}

	public enum VerticalAlign
	{
		Baseline,
		Top,
		Middle,
		Bottom,
		Sub,
		Super,
		TextTop,
		TextBottom,
	}

	public enum BorderStyle
	{
		None,
		Solid,
		Dotted,
		Dashed,
		Double,
		Inset,
		Outset,
		Groove,
		Ridge,
		Hidden,
	}

	public enum Position
	{
		Static,
		Relative,
		Absolute,
		Fixed,
	}

	public enum Overflow
	{
		Visible,
		Hidden,
		Scroll,
		Auto,
	}

	// Four sides of a box property, in CSS order
	public struct Edges
	{
		public Length Top;
		public Length Right;
		public Length Bottom;
		public Length Left;

		public Edges(Length all)
		{
			Top = Right = Bottom = Left = all;
		}

		public override string ToString() => $"{Top} {Right} {Bottom} {Left}";
	}

	public struct BorderEdge
	{
		public float Width;
		public BorderStyle Style;
		public Color Color;

		// The width layout uses: a border with no style takes no room
		public float Effective => Style == BorderStyle.None || Style == BorderStyle.Hidden ? 0 : Width;
	}

	// Every property layout and paint read, resolved as far as the cascade can: font-relative lengths in pixels, percentages kept
	public sealed class ComputedStyle
	{
		public const float DefaultFontSize = 16;

		// A bloom filter of this element's and its ancestors' tags, ids and classes, for rejecting descendant selectors cheaply
		internal ulong AncestorBitsLow;
		internal ulong AncestorBitsHigh;

		public Display Display = Display.Inline;
		public Position Position = Position.Static;
		public FloatSide Float = FloatSide.None;
		public ClearSide Clear = ClearSide.None;
		public Overflow Overflow = Overflow.Visible;
		public bool Visible = true;
		// box-sizing: border-box, where width and height include the padding and borders
		public bool BorderBox;
		// aspect-ratio as width over height, or 0 for auto
		public float AspectRatio;
		// z-index for positioned boxes; auto counts as 0 and keeps document order
		public int ZIndex;
		// The translation part of transform, applied after layout; percentages are of the box's own size
		public Length TranslateX = Length.Zero;
		public Length TranslateY = Length.Zero;
		// The rotation part of transform in degrees, honoured when an svg is rasterized
		public float Rotate;

		public Edges Margin = new Edges(Length.Zero);
		public Edges Padding = new Edges(Length.Zero);
		public BorderEdge BorderTop;
		public BorderEdge BorderRight;
		public BorderEdge BorderBottom;
		public BorderEdge BorderLeft;
		public Length Width = Length.Auto;
		public Length Height = Length.Auto;
		public Length MinWidth = Length.Zero;
		public Length MaxWidth = Length.Auto;
		public Length MinHeight = Length.Zero;
		public Length MaxHeight = Length.Auto;
		public Length Top = Length.Auto;
		public Length Left = Length.Auto;
		public Length Right = Length.Auto;
		public Length Bottom = Length.Auto;

		public Color Color = Color.Black;
		public Color BackgroundColor = Color.Transparent;
		public string BackgroundImage;

		public float FontSize = DefaultFontSize;
		public bool Bold;
		public bool Italic;
		public FontFamily FontFamily = FontFamily.Sans;
		public float LineHeight = -1;
		public TextAlign TextAlign = TextAlign.Left;
		public bool Underline;
		public bool LineThrough;
		public Length TextIndent = Length.Zero;
		public WhiteSpace WhiteSpace = WhiteSpace.Normal;
		public TextTransform TextTransform = TextTransform.None;
		public VerticalAlign VerticalAlign = VerticalAlign.Baseline;
		public ListStyleType ListStyleType = ListStyleType.Disc;
		public bool ListInside;
		public float LetterSpacing;
		public float Opacity = 1;

		// Flex containers and items
		public FlexDirection FlexDirection = FlexDirection.Row;
		public bool FlexWrap;
		public JustifyContent JustifyContent = JustifyContent.FlexStart;
		public AlignItems AlignItems = AlignItems.Stretch;
		public AlignItems AlignSelf = AlignItems.Auto;
		public float FlexGrow;
		public float FlexShrink = 1;
		public Length FlexBasis = Length.Auto;
		public Length RowGap = Length.Zero;
		public Length ColumnGap = Length.Zero;
		public int Order;

		// Grid containers and items
		public GridTemplate GridColumns;
		public GridTemplate GridRows;
		public GridTrack GridAutoRows = new GridTrack(GridSize.AutoSize);
		public GridTrack GridAutoColumns = new GridTrack(GridSize.AutoSize);
		public string[][] GridAreas;
		public GridAutoFlow GridAutoFlow = GridAutoFlow.Row;
		public GridLine GridColumnStart;
		public GridLine GridColumnEnd;
		public GridLine GridRowStart;
		public GridLine GridRowEnd;
		public AlignItems JustifyItems = AlignItems.Stretch;
		public AlignItems JustifySelf = AlignItems.Auto;

		// Custom properties (--name) in scope, shared with the parent until the element declares its own
		public Dictionary<string, List<CssToken>> Custom;

		public float BorderWidth(int side) => side switch { 0 => BorderTop.Effective, 1 => BorderRight.Effective, 2 => BorderBottom.Effective, _ => BorderLeft.Effective };

		// Line height in pixels for this font size: "normal" is a fraction of the size, as browsers do
		public float ResolvedLineHeight => LineHeight < 0 ? MathF.Round(FontSize * 1.2f) : LineHeight;

		public bool IsBlockLevel => Display == Display.Block || Display == Display.ListItem || Display == Display.Table || Display == Display.TableCaption || Display == Display.Flex || Display == Display.Grid;

		public bool IsFlexContainer => Display == Display.Flex || Display == Display.InlineFlex;

		public bool IsGridContainer => Display == Display.Grid || Display == Display.InlineGrid;

		public bool IsInlineLevel => Display == Display.Inline || Display == Display.InlineBlock;

		public ComputedStyle Clone() => (ComputedStyle)MemberwiseClone();

		// The inherited properties, copied from the parent before the element's own declarations apply
		public static ComputedStyle Inherit(ComputedStyle parent)
		{
			ComputedStyle style = new ComputedStyle();
			if (parent == null)
				return style;
			style.Color = parent.Color;
			style.FontSize = parent.FontSize;
			style.Bold = parent.Bold;
			style.Italic = parent.Italic;
			style.FontFamily = parent.FontFamily;
			style.LineHeight = parent.LineHeight;
			style.TextAlign = parent.TextAlign;
			style.TextIndent = parent.TextIndent;
			style.WhiteSpace = parent.WhiteSpace;
			style.TextTransform = parent.TextTransform;
			style.ListStyleType = parent.ListStyleType;
			style.ListInside = parent.ListInside;
			style.LetterSpacing = parent.LetterSpacing;
			style.Visible = parent.Visible;
			style.Underline = parent.Underline;
			style.LineThrough = parent.LineThrough;
			style.Custom = parent.Custom;
			return style;
		}

		public override string ToString() => $"display {Display}, font {FontSize}px{(Bold ? " bold" : "")}{(Italic ? " italic" : "")} {FontFamily}, color {Color}, background {BackgroundColor}, margin {Margin}, padding {Padding}";
	}
}
