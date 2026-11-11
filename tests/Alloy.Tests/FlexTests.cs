using System.Linq;
using Alloy.Sdk.Web.Css;
using Alloy.Sdk.Web.Html;
using Alloy.Sdk.Web.Layout;

namespace Alloy.Tests;

// Flex layout with the fixed-advance font: 16px text is 8px a character, lines are 19px
public class FlexTests
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

	[Fact]
	public void ParsesFlexProperties()
	{
		Document document = HtmlParser.Parse("<div id=c><div id=a></div></div>");
		MediaEnvironment media = new MediaEnvironment(Width, 600);
		StyleResolver resolver = new StyleResolver(media);
		resolver.AddStylesheet(CssParser.Parse("#c { display: flex; flex-flow: column wrap; justify-content: space-between; align-items: center; gap: 10px 20px } #a { flex: 2 0 50px; align-self: flex-end; order: 3 }", media));
		ComputedStyle c = resolver.Resolve(document.DescendantElements().First(e => e.Id == "c"));
		Assert.Equal(Display.Flex, c.Display);
		Assert.Equal(FlexDirection.Column, c.FlexDirection);
		Assert.True(c.FlexWrap);
		Assert.Equal(JustifyContent.SpaceBetween, c.JustifyContent);
		Assert.Equal(AlignItems.Center, c.AlignItems);
		Assert.Equal(10, c.RowGap.Value);
		Assert.Equal(20, c.ColumnGap.Value);
		ComputedStyle a = resolver.Resolve(document.DescendantElements().First(e => e.Id == "a"));
		Assert.Equal(2, a.FlexGrow);
		Assert.Equal(0, a.FlexShrink);
		Assert.Equal(50, a.FlexBasis.Value);
		Assert.Equal(AlignItems.FlexEnd, a.AlignSelf);
		Assert.Equal(3, a.Order);
		ComputedStyle none = resolver.Resolve(HtmlParser.Parse("<div style='flex: none'></div>").Body.FirstElement("div"));
		Assert.Equal(0, none.FlexGrow);
		Assert.Equal(0, none.FlexShrink);
	}

	[Fact]
	public void GrowsItemsToFillTheRow()
	{
		LayoutBox root = Lay("<div id=c><div id=a>a</div><div id=b>b</div><div id=d>d</div></div>", "#c { display: flex } #c > div { flex: 1 }");
		LayoutBox a = Id(root, "a");
		LayoutBox b = Id(root, "b");
		LayoutBox d = Id(root, "d");
		Assert.Equal(800f / 3, a.Width, 1);
		Assert.Equal(800f / 3, b.Width, 1);
		Assert.Equal(0, a.X);
		Assert.Equal(800f / 3, b.X, 1);
		Assert.Equal(1600f / 3, d.X, 1);
		Assert.Equal(19, Id(root, "c").Height);
		LayoutBox weighted = Lay("<div id=c><div id=a>a</div><div id=b>b</div></div>", "#c { display: flex } #a { flex: 1 } #b { flex: 3 }");
		Assert.Equal(200, Id(weighted, "a").Width);
		Assert.Equal(600, Id(weighted, "b").Width);
	}

	[Fact]
	public void SizesItemsByContentAndJustifies()
	{
		LayoutBox root = Lay("<div id=c><div id=a>aaaa</div><div id=b>bb</div></div>", "#c { display: flex; justify-content: space-between }");
		LayoutBox a = Id(root, "a");
		LayoutBox b = Id(root, "b");
		Assert.Equal(32, a.Width);
		Assert.Equal(16, b.Width);
		Assert.Equal(0, a.X);
		Assert.Equal(784, b.X);
		Assert.Equal(392, Id(Lay("<div id=c><div id=a>aa</div></div>", "#c { display: flex; justify-content: center }"), "a").X);
		Assert.Equal(784, Id(Lay("<div id=c><div id=a>aa</div></div>", "#c { display: flex; justify-content: flex-end }"), "a").X);
		LayoutBox around = Lay("<div id=c><div id=a>aa</div><div id=b>bb</div></div>", "#c { display: flex; justify-content: space-around }");
		Assert.Equal(192, Id(around, "a").X);
		Assert.Equal(592, Id(around, "b").X);
		LayoutBox auto = Lay("<div id=c><div id=a>aa</div><div id=b>bb</div></div>", "#c { display: flex } #b { margin-left: auto }");
		Assert.Equal(784, Id(auto, "b").X);
	}

	[Fact]
	public void ShrinksItemsThatOverflow()
	{
		LayoutBox root = Lay("<div id=c><div id=a></div><div id=b></div></div>", "#c { display: flex } #a { width: 600px; height: 10px } #b { width: 600px; height: 10px; flex-shrink: 2 }");
		LayoutBox a = Id(root, "a");
		LayoutBox b = Id(root, "b");
		Assert.Equal(800, a.Width + b.Width, 1);
		Assert.Equal(1400f / 3, a.Width, 1);
		Assert.Equal(1000f / 3, b.Width, 1);
		LayoutBox fixedWidth = Lay("<div id=c><div id=a></div><div id=b></div></div>", "#c { display: flex } #a { width: 600px; height: 10px; flex-shrink: 0 } #b { width: 600px; height: 10px }");
		Assert.Equal(600, Id(fixedWidth, "a").Width);
		Assert.Equal(200, Id(fixedWidth, "b").Width);
	}

	[Fact]
	public void AlignsAndStretchesAcrossTheLine()
	{
		const string html = "<div id=c><div id=a></div><div id=b></div></div>";
		LayoutBox stretch = Lay(html, "#c { display: flex } #a { width: 100px; height: 60px } #b { width: 100px }");
		Assert.Equal(60, Id(stretch, "b").Height);
		Assert.Equal(60, Id(stretch, "c").Height);
		LayoutBox center = Lay(html, "#c { display: flex; align-items: center } #a { width: 100px; height: 60px } #b { width: 100px; height: 20px }");
		Assert.Equal(20, Id(center, "b").Y);
		LayoutBox end = Lay(html, "#c { display: flex; align-items: flex-end } #a { width: 100px; height: 60px } #b { width: 100px; height: 20px; align-self: flex-start }");
		Assert.Equal(0, Id(end, "b").Y);
		LayoutBox tall = Lay(html, "#c { display: flex; height: 100px; align-items: flex-end } #a { width: 100px; height: 60px } #b { width: 100px; height: 20px }");
		Assert.Equal(40, Id(tall, "a").Y);
		Assert.Equal(80, Id(tall, "b").Y);
	}

	[Fact]
	public void WrapsOntoNewLinesWithGaps()
	{
		LayoutBox root = Lay("<div id=c><div id=a></div><div id=b></div><div id=d></div></div>", "#c { display: flex; flex-wrap: wrap; gap: 10px } #c > div { width: 300px; height: 50px }");
		Assert.Equal(0, Id(root, "a").X);
		Assert.Equal(310, Id(root, "b").X);
		Assert.Equal(0, Id(root, "d").X);
		Assert.Equal(60, Id(root, "d").Y);
		Assert.Equal(110, Id(root, "c").Height);
		LayoutBox growing = Lay("<div id=c><div id=a></div><div id=b></div><div id=d></div></div>", "#c { display: flex; flex-wrap: wrap } #c > div { flex: 1 0 300px; height: 50px }");
		Assert.Equal(400, Id(growing, "a").Width);
		Assert.Equal(800, Id(growing, "d").Width);
	}

	[Fact]
	public void LaysOutColumnsAndReversedRows()
	{
		LayoutBox column = Lay("<div id=c><div id=a>a</div><div id=b>b</div></div>", "#c { display: flex; flex-direction: column; height: 200px } #a { flex: 1 } #b { height: 50px }");
		LayoutBox a = Id(column, "a");
		LayoutBox b = Id(column, "b");
		Assert.Equal(800, a.Width);
		Assert.Equal(150, a.Height);
		Assert.Equal(150, b.Y);
		Assert.Equal(200, Id(column, "c").Height);
		LayoutBox natural = Lay("<div id=c><div id=a>a</div><div id=b>b</div></div>", "#c { display: flex; flex-direction: column; align-items: flex-start }");
		Assert.Equal(8, Id(natural, "a").Width);
		Assert.Equal(19, Id(natural, "b").Y);
		Assert.Equal(38, Id(natural, "c").Height);
		LayoutBox reversed = Lay("<div id=c><div id=a>a</div><div id=b>b</div></div>", "#c { display: flex; flex-direction: row-reverse } #c > div { width: 100px }");
		Assert.Equal(700, Id(reversed, "a").X);
		Assert.Equal(600, Id(reversed, "b").X);
		LayoutBox ordered = Lay("<div id=c><div id=a>a</div><div id=b>b</div></div>", "#c { display: flex } #c > div { width: 100px } #a { order: 1 }");
		Assert.Equal(100, Id(ordered, "a").X);
	}

	[Fact]
	public void TreatsTextAndInlinesAsItems()
	{
		LayoutBox root = Lay("<div id=c>plain <span id=s>span</span> <a id=l href=x>link</a></div>", "#c { display: flex; gap: 10px }");
		LayoutBox c = Id(root, "c");
		Assert.Equal(3, c.Children.Count);
		Assert.Equal(BoxKind.AnonymousBlock, c.Children[0].Kind);
		Assert.Equal(BoxKind.Block, Id(root, "s").Kind);
		Assert.Equal(50, Id(root, "s").X);
		Assert.Equal(32, Id(root, "s").Width);
		Assert.Equal(92, Id(root, "l").X);
		Assert.Equal(19, c.Height);
		LayoutBox nested = Lay("<div id=c><div id=a>one two three four five six seven eight nine ten eleven twelve</div><div id=b>short</div></div>", "#c { display: flex; width: 400px } #a { flex: 1 } #b { flex: none }");
		Assert.Equal(40, Id(nested, "b").Width);
		Assert.Equal(360, Id(nested, "a").Width);
		Assert.True(Id(nested, "a").Lines.Count >= 2);
	}
}
