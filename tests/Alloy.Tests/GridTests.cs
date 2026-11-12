using System.Linq;
using Alloy.Sdk.Web.Css;
using Alloy.Sdk.Web.Html;
using Alloy.Sdk.Web.Layout;

namespace Alloy.Tests;

// Grid layout with the fixed-advance font: 16px text is 8px a character, lines are 19px
public class GridTests
{
	private const int Width = 800;

	private static LayoutBox Lay(string html, string css = "")
	{
		Document document = HtmlParser.Parse(html);
		MediaEnvironment media = new MediaEnvironment(Width, 600);
		StyleResolver resolver = new StyleResolver(media);
		resolver.AddStylesheet(CssParser.Parse("body { margin: 0 } " + css, media));
		LayoutBox root = new BoxBuilder(resolver).Build(document);
		new LayoutEngine(new FixedFontProvider(), Width, 600).Layout(root);
		return root;
	}

	private static LayoutBox Id(LayoutBox box, string id)
	{
		if (box.Element?.Id == id && box.Kind != BoxKind.Text)
			return box;
		foreach (LayoutBox child in box.Children)
		{
			LayoutBox found = Id(child, id);
			if (found != null)
				return found;
		}
		return null;
	}

	private const string Four = "<div id=g><div id=a>a</div><div id=b>b</div><div id=c>c</div><div id=d>d</div></div>";

	[Fact]
	public void ParsesTrackListsAndPlacement()
	{
		Document document = HtmlParser.Parse("<div id=g><div id=a></div></div>");
		MediaEnvironment media = new MediaEnvironment(Width, 600);
		StyleResolver resolver = new StyleResolver(media);
		resolver.AddStylesheet(CssParser.Parse("#g { display: grid; grid-template-columns: 100px 1fr minmax(50px, 2fr) repeat(2, auto); grid-template-rows: repeat(auto-fill, 40px); gap: 4px 8px; grid-template-areas: 'head head' 'nav main' } #a { grid-column: 2 / span 2; grid-row: span 3; grid-area: main }", media));
		ComputedStyle g = resolver.Resolve(document.DescendantElements().First(e => e.Id == "g"));
		Assert.Equal(Display.Grid, g.Display);
		Assert.Equal(5, g.GridColumns.Before.Count);
		Assert.Equal(GridSizeKind.Fixed, g.GridColumns.Before[0].Max.Kind);
		Assert.Equal(100, g.GridColumns.Before[0].Max.Value);
		Assert.Equal(GridSizeKind.Fr, g.GridColumns.Before[1].Max.Kind);
		Assert.Equal(50, g.GridColumns.Before[2].Min.Value);
		Assert.Equal(2, g.GridColumns.Before[2].Max.Value);
		Assert.Equal(GridSizeKind.Auto, g.GridColumns.Before[4].Max.Kind);
		Assert.True(g.GridRows.HasAutoRepeat);
		Assert.Equal(40, g.GridRows.Repeat[0].Max.Value);
		Assert.Equal(4, g.RowGap.Value);
		Assert.Equal(8, g.ColumnGap.Value);
		Assert.Equal(new[] { "head", "head" }, g.GridAreas[0]);
		Assert.Equal("main", g.GridAreas[1][1]);
		ComputedStyle a = resolver.Resolve(document.DescendantElements().First(e => e.Id == "a"));
		Assert.Equal("main", a.GridColumnStart.Name);
		Assert.Equal("main", a.GridRowEnd.Name);
	}

	[Fact]
	public void SharesSpaceBetweenFractionColumns()
	{
		LayoutBox root = Lay(Four, "#g { display: grid; grid-template-columns: 1fr 1fr 2fr }");
		Assert.Equal(200, Id(root, "a").Width);
		Assert.Equal(200, Id(root, "b").X);
		Assert.Equal(400, Id(root, "c").Width);
		Assert.Equal(400, Id(root, "c").X);
		Assert.Equal(0, Id(root, "d").X);
		Assert.Equal(19, Id(root, "d").Y);
		Assert.Equal(38, Id(root, "g").Height);
		LayoutBox mixed = Lay(Four, "#g { display: grid; grid-template-columns: 100px 1fr 25% } #g > div { height: 10px }");
		Assert.Equal(100, Id(mixed, "a").Width);
		Assert.Equal(500, Id(mixed, "b").Width);
		Assert.Equal(200, Id(mixed, "c").Width);
	}

	[Fact]
	public void SizesAutoColumnsFromContentAndStretchesThem()
	{
		LayoutBox root = Lay("<div id=g><div id=a>aaaa</div><div id=b>bb</div></div>", "#g { display: grid; grid-template-columns: auto auto; width: 400px; justify-items: start }");
		LayoutBox a = Id(root, "a");
		LayoutBox b = Id(root, "b");
		Assert.Equal(32, a.Width);
		Assert.Equal(16, b.Width);
		Assert.Equal(208, b.X);
		LayoutBox stretched = Lay("<div id=g><div id=a>aaaa</div><div id=b>bb</div></div>", "#g { display: grid; grid-template-columns: auto auto; width: 400px }");
		Assert.Equal(400, Id(stretched, "a").Width + Id(stretched, "b").Width);
		LayoutBox minmax = Lay("<div id=g><div id=a>aaaa</div><div id=b>bb</div></div>", "#g { display: grid; grid-template-columns: minmax(100px, auto) 1fr; width: 400px }");
		Assert.Equal(100, Id(minmax, "a").Width);
		Assert.Equal(300, Id(minmax, "b").Width);
	}

	[Fact]
	public void AutoPlacesWithGapsAndImplicitRows()
	{
		LayoutBox root = Lay("<div id=g><div id=a>a</div><div id=b>b</div><div id=c>c</div><div id=d>d</div><div id=e>e</div></div>", "#g { display: grid; grid-template-columns: repeat(2, 1fr); gap: 10px 20px; grid-auto-rows: 30px }");
		Assert.Equal(390, Id(root, "a").Width);
		Assert.Equal(410, Id(root, "b").X);
		Assert.Equal(40, Id(root, "c").Y);
		Assert.Equal(80, Id(root, "e").Y);
		Assert.Equal(0, Id(root, "e").X);
		Assert.Equal(30, Id(root, "e").Height);
		Assert.Equal(110, Id(root, "g").Height);
	}

	[Fact]
	public void HonoursSpansAndExplicitLines()
	{
		LayoutBox root = Lay(Four, "#g { display: grid; grid-template-columns: repeat(4, 100px); grid-auto-rows: 20px } #a { grid-column: 1 / 3 } #b { grid-column: span 2 } #c { grid-column: 4; grid-row: 2 } #d { grid-row: 2 / 4; grid-column: -2 }");
		Assert.Equal(200, Id(root, "a").Width);
		Assert.Equal(200, Id(root, "b").X);
		Assert.Equal(200, Id(root, "b").Width);
		Assert.Equal(300, Id(root, "c").X);
		Assert.Equal(20, Id(root, "c").Y);
		LayoutBox d = Id(root, "d");
		Assert.Equal(300, d.X);
		Assert.Equal(20, d.Y);
		Assert.Equal(40, d.Height);
		LayoutBox negative = Lay("<div id=g><div id=a>a</div></div>", "#g { display: grid; grid-template-columns: repeat(3, 100px) } #a { grid-column: 2 / -1 }");
		Assert.Equal(100, Id(negative, "a").X);
		Assert.Equal(200, Id(negative, "a").Width);
	}

	[Fact]
	public void PlacesByTemplateAreas()
	{
		LayoutBox root = Lay("<div id=g><div id=h>h</div><div id=n>n</div><div id=m>m</div><div id=f>f</div></div>",
			"#g { display: grid; grid-template-columns: 200px 1fr; grid-template-areas: 'head head' 'nav main' 'foot foot'; grid-template-rows: 50px auto 30px } #h { grid-area: head } #n { grid-area: nav } #m { grid-area: main } #f { grid-area: foot }");
		LayoutBox h = Id(root, "h");
		Assert.Equal(800, h.Width);
		Assert.Equal(50, h.Height);
		LayoutBox n = Id(root, "n");
		Assert.Equal(200, n.Width);
		Assert.Equal(50, n.Y);
		LayoutBox m = Id(root, "m");
		Assert.Equal(200, m.X);
		Assert.Equal(600, m.Width);
		LayoutBox f = Id(root, "f");
		Assert.Equal(69, f.Y);
		Assert.Equal(30, f.Height);
		Assert.Equal(99, Id(root, "g").Height);
		LayoutBox shorthand = Lay("<div id=g><div id=h>h</div><div id=m>m</div></div>", "#g { display: grid; grid-template: 'a a' 40px 'b c' 60px / 100px 1fr } #h { grid-area: a } #m { grid-area: c }");
		Assert.Equal(40, Id(shorthand, "h").Height);
		Assert.Equal(100, Id(shorthand, "m").X);
		Assert.Equal(40, Id(shorthand, "m").Y);
		Assert.Equal(60, Id(shorthand, "m").Height);
	}

	[Fact]
	public void FillsWithRepeatAutoFillAndAlignsItems()
	{
		LayoutBox root = Lay("<div id=g><div id=a>a</div><div id=b>b</div><div id=c>c</div><div id=d>d</div><div id=e>e</div></div>", "#g { display: grid; grid-template-columns: repeat(auto-fill, minmax(180px, 1fr)); gap: 10px }");
		Assert.Equal(192.5f, Id(root, "a").Width, 1);
		Assert.Equal(0, Id(root, "d").Y);
		Assert.Equal(29, Id(root, "e").Y);
		Assert.Equal(0, Id(root, "e").X);
		LayoutBox aligned = Lay("<div id=g><div id=a>a</div><div id=b>b</div></div>", "#g { display: grid; grid-template-columns: 200px 200px; grid-template-rows: 100px; justify-items: center; align-items: end } #b { justify-self: end; align-self: start }");
		Assert.Equal(96, Id(aligned, "a").X);
		Assert.Equal(81, Id(aligned, "a").Y);
		Assert.Equal(392, Id(aligned, "b").X);
		Assert.Equal(0, Id(aligned, "b").Y);
		Assert.Equal(19, Id(aligned, "b").Height);
		LayoutBox stretched = Lay("<div id=g><div id=a>a</div></div>", "#g { display: grid; grid-template-columns: 200px; grid-template-rows: 100px }");
		Assert.Equal(100, Id(stretched, "a").Height);
		Assert.Equal(200, Id(stretched, "a").Width);
	}

	[Fact]
	public void LaysOutTextAndNestedBlocksInCells()
	{
		LayoutBox root = Lay("<div id=g>loose text<div id=a>one two three four five six seven eight nine ten</div></div>", "#g { display: grid; grid-template-columns: 100px 1fr; width: 300px }");
		LayoutBox g = Id(root, "g");
		Assert.Equal(2, g.Children.Count);
		Assert.Equal(BoxKind.AnonymousBlock, g.Children[0].Kind);
		LayoutBox a = Id(root, "a");
		Assert.Equal(100, a.X);
		Assert.Equal(200, a.Width);
		Assert.True(a.Lines.Count >= 2);
		Assert.Equal(a.Height, g.Height);
	}
}
