using System.Linq;
using Alloy.Sdk.Web.Css;
using Alloy.Sdk.Web.Html;
using Alloy.Sdk.Web.Layout;

namespace Alloy.Tests;

// Layout with a fixed-advance font: 16px text is 8px a character, ascent 13, descent 3, normal line height 19
public class LayoutTests
{
	private const int Width = 800;
	private const int Height = 600;

	internal static LayoutBox Lay(string html, string css = "")
	{
		Document document = HtmlParser.Parse(html);
		MediaEnvironment media = new MediaEnvironment(Width, Height);
		StyleResolver resolver = new StyleResolver(media);
		resolver.AddStylesheet(CssParser.Parse("body { margin: 0 } " + css, media));
		LayoutBox root = new BoxBuilder(resolver).Build(document);
		new LayoutEngine(new FixedFontProvider(), Width, Height).Layout(root);
		return root;
	}

	private static LayoutBox Find(LayoutBox box, string tag)
	{
		if (box.TagName == tag && box.Kind != BoxKind.Text)
			return box;
		foreach (LayoutBox child in box.Children)
		{
			LayoutBox found = Find(child, tag);
			if (found != null)
				return found;
		}
		if (box.Positioned != null)
		{
			foreach (LayoutBox child in box.Positioned)
			{
				LayoutBox found = Find(child, tag);
				if (found != null)
					return found;
			}
		}
		return null;
	}

	internal static LayoutBox FindId(LayoutBox box, string id)
	{
		if (box.Element?.Id == id && box.Kind != BoxKind.Text)
			return box;
		foreach (LayoutBox child in box.Children)
		{
			LayoutBox found = FindId(child, id);
			if (found != null)
				return found;
		}
		return null;
	}

	[Fact]
	public void StacksBlocksAndCollapsesMargins()
	{
		LayoutBox root = Lay("<div id=a>a</div><div id=b>b</div><div id=c><p>c</p></div>", "#a { margin-bottom: 30px; height: 50px } #b { margin-top: 20px; height: 40px } p { margin: 16px 0 }");
		LayoutBox a = FindId(root, "a");
		LayoutBox b = FindId(root, "b");
		LayoutBox c = FindId(root, "c");
		LayoutBox p = Find(root, "p");
		Assert.Equal(0, a.Y);
		Assert.Equal(Width, a.Width);
		Assert.Equal(80, b.Y);
		Assert.Equal(136, c.Y);
		Assert.Equal(136, p.Y);
		Assert.Equal(19, p.Height);
		Assert.Equal(19, c.Height);
		Assert.Equal(171, root.Height);
	}

	[Fact]
	public void SizesBoxesFromWidthPaddingBorderAndAutoMargins()
	{
		LayoutBox root = Lay("<div id=box>x</div><div id=pct>y</div>", "#box { width: 200px; padding: 10px; border: 2px solid black; margin: 0 auto } #pct { width: 50%; margin-left: 10% }");
		LayoutBox box = FindId(root, "box");
		Assert.Equal(224, box.Width);
		Assert.Equal(288, box.X);
		Assert.Equal(43, box.Height);
		Assert.Equal(300, box.ContentX);
		LayoutBox pct = FindId(root, "pct");
		Assert.Equal(400, pct.Width);
		Assert.Equal(80, pct.X);
	}

	[Fact]
	public void WrapsTextIntoLines()
	{
		LayoutBox root = Lay("<p>one two three four five six seven eight nine ten</p>", "p { width: 160px; margin: 0 }");
		LayoutBox p = Find(root, "p");
		Assert.Equal(3, p.Lines.Count);
		Assert.Equal("one two three four", p.Lines[0].Fragments[0].Text);
		Assert.Equal("five six seven eight", p.Lines[1].Fragments[0].Text);
		Assert.Equal("nine ten", p.Lines[2].Fragments[0].Text);
		Assert.Equal(57, p.Height);
		Assert.Equal(14.5, p.Lines[0].Baseline);
		Assert.Equal(19, p.Lines[1].Y);
	}

	[Fact]
	public void AlignsAndIndentsText()
	{
		LayoutBox root = Lay("<p id=c>abcd</p><p id=r>abcd</p><p id=i>abcd efgh</p>", "p { width: 100px } #c { text-align: center } #r { text-align: right } #i { text-indent: 20px }");
		Assert.Equal(34, FindId(root, "c").Lines[0].Fragments[0].X);
		Assert.Equal(68, FindId(root, "r").Lines[0].Fragments[0].X);
		Assert.Equal(20, FindId(root, "i").Lines[0].Fragments[0].X);
	}

	[Fact]
	public void MixesFontsOnOneLineAndBreaksOnBr()
	{
		LayoutBox root = Lay("<p>small <b style='font-size: 32px'>big</b> after<br>next<br><br>last</p>", "p { margin: 0 }");
		LayoutBox p = Find(root, "p");
		Assert.Equal(4, p.Lines.Count);
		LineBox first = p.Lines[0];
		Assert.Equal(38, first.Height);
		Assert.Equal(29, first.Baseline);
		LineFragment big = first.Fragments.First(f => f.Text == "big");
		Assert.Equal(48, big.Width);
		Assert.Equal(0, big.Y);
		LineFragment small = first.Fragments[0];
		Assert.Equal(29, small.Y + small.Baseline);
		Assert.Equal(19, p.Lines[2].Height);
		Assert.Equal("last", p.Lines[3].Fragments[0].Text);
	}

	[Fact]
	public void PreservesWhitespaceInPre()
	{
		LayoutBox root = Lay("<pre>  two  spaces\nsecond line\n\nfourth</pre>", "pre { font-size: 16px }");
		LayoutBox pre = Find(root, "pre");
		Assert.Equal(4, pre.Lines.Count);
		Assert.Equal("  two  spaces", pre.Lines[0].Fragments[0].Text);
		Assert.Equal(104, pre.Lines[0].Width);
		Assert.Equal(0, pre.Lines[2].Fragments.Count);
		Assert.Equal("fourth", pre.Lines[3].Fragments[0].Text);
	}

	[Fact]
	public void PlacesFloatsAndShortensLines()
	{
		LayoutBox root = Lay("<div id=f>float</div><p>a b c d e f g h i j k l m n o p q r s t u v w x y z</p><div id=cl>after</div>", "#f { float: left; width: 100px; height: 100px; margin-right: 10px } p { margin: 0 } #cl { clear: both }");
		LayoutBox f = FindId(root, "f");
		LayoutBox p = Find(root, "p");
		Assert.Equal(0, f.X);
		Assert.Equal(0, f.Y);
		Assert.Equal(110, p.Lines[0].X);
		Assert.Equal(0, p.Y);
		Assert.True(p.Lines[0].Width <= 690);
		Assert.Equal(100, FindId(root, "cl").Y);
		Assert.Equal(119, root.Height);
	}

	[Fact]
	public void FloatsRightAndClearsLeft()
	{
		LayoutBox root = Lay("<div id=r>r</div><div id=l>l</div><p>text</p>", "#r { float: right; width: 50px; height: 30px } #l { float: left; width: 60px; height: 20px } p { margin: 0 }");
		Assert.Equal(750, FindId(root, "r").X);
		Assert.Equal(0, FindId(root, "l").X);
		LineBox line = Find(root, "p").Lines[0];
		Assert.Equal(60, line.X);
	}

	[Fact]
	public void LaysOutInlineBlocksOnTheBaseline()
	{
		LayoutBox root = Lay("<p>x <span id=ib>in<br>two</span> y</p>", "p { margin: 0 } #ib { display: inline-block; width: 60px; padding: 2px; background: red }");
		LayoutBox p = Find(root, "p");
		LayoutBox ib = FindId(root, "ib");
		Assert.Single(p.Lines);
		Assert.Equal(64, ib.Width);
		Assert.Equal(42, ib.Height);
		Assert.Equal(16, ib.X);
		Assert.Equal(0, ib.Y);
		Assert.Equal(35.5, p.Lines[0].Baseline);
		Assert.Equal(42, p.Lines[0].Height);
	}

	[Fact]
	public void IndentsListsAndNumbersMarkers()
	{
		LayoutBox root = Lay("<ul><li>a</li><li>b</li></ul><ol start=3><li>c</li><li value=10>d</li></ol>", "ul, ol { margin: 0 }");
		LayoutBox[] items = root.Children.SelectMany(b => b.Children).SelectMany(l => l.Children).Where(i => i.TagName == "li").ToArray();
		Assert.Equal(4, items.Length);
		Assert.Equal(40, items[0].X);
		Assert.Equal("•", items[0].Marker);
		Assert.Equal("3.", items[2].Marker);
		Assert.Equal("10.", items[3].Marker);
		Assert.Equal(19, items[1].Y);
	}

	[Fact]
	public void SizesTableColumnsFromContent()
	{
		LayoutBox root = Lay("<table><tr><td>a</td><td>longer cell text</td></tr><tr><td>bb</td><td>c</td></tr></table>", "td { padding: 0 }");
		LayoutBox table = Find(root, "table");
		LayoutBox[] cells = table.Children.Where(r => r.Kind == BoxKind.TableRow).SelectMany(r => r.Children).ToArray();
		Assert.Equal(4, cells.Length);
		Assert.Equal(16, cells[0].Width);
		Assert.Equal(128, cells[1].Width);
		Assert.Equal(2, cells[0].X);
		Assert.Equal(20, cells[1].X);
		Assert.Equal(16, cells[2].Width);
		Assert.Equal(23, cells[2].Y);
		Assert.Equal(150, table.Width);
		Assert.Equal(44, table.Height);
	}

	[Fact]
	public void StretchesAndWrapsTablesToTheirWidth()
	{
		LayoutBox root = Lay("<table width=100%><tr><td>short</td><td>a much longer piece of text that will need to wrap around</td></tr></table>", "td { padding: 0 }");
		LayoutBox table = Find(root, "table");
		Assert.Equal(Width, table.Width);
		LayoutBox[] cells = table.Children.Where(r => r.Kind == BoxKind.TableRow).SelectMany(r => r.Children).ToArray();
		Assert.Equal(794, cells[0].Width + cells[1].Width);
		Assert.True(cells[1].Width > cells[0].Width * 5);
		LayoutBox narrow = Find(Lay("<table style='width: 200px'><tr><td>one two three four five six</td></tr></table>", "td { padding: 0 }"), "td");
		Assert.True(narrow.Lines.Count >= 2);
		Assert.Equal(196, narrow.Width);
	}

	[Fact]
	public void SpansColumnsAndRows()
	{
		LayoutBox root = Lay("<table><tr><td colspan=2>wide</td></tr><tr><td>a</td><td>b</td></tr><tr><td rowspan=2>tall</td><td>c</td></tr><tr><td>d</td></tr></table>", "td { padding: 0 }");
		LayoutBox table = Find(root, "table");
		LayoutBox[] cells = table.Children.Where(r => r.Kind == BoxKind.TableRow).SelectMany(r => r.Children).ToArray();
		Assert.Equal(6, cells.Length);
		Assert.Equal(cells[1].Width + cells[2].Width + 2, cells[0].Width);
		Assert.Equal(cells[4].X, cells[5].X);
		Assert.Equal(40, cells[3].Height);
	}

	[Fact]
	public void PositionsRelativeAndAbsoluteBoxes()
	{
		LayoutBox root = Lay("<div id=rel>r</div><div id=cb><div id=abs>a</div>text</div><div id=fix>f</div>", "#rel { position: relative; left: 10px; top: 5px } #cb { position: relative; margin-top: 20px; height: 100px } #abs { position: absolute; left: 30px; top: 40px; width: 50px } #fix { position: fixed; left: 700px; top: 0 }");
		Assert.Equal(10, FindId(root, "rel").X);
		Assert.Equal(5, FindId(root, "rel").Y);
		LayoutBox abs = FindId(root, "abs");
		Assert.Equal(30, abs.X);
		Assert.Equal(79, abs.Y);
		Assert.Equal(50, abs.Width);
		Assert.Equal(700, FindId(root, "fix").X);
		Assert.Equal(0, FindId(root, "fix").Y);
	}

	[Fact]
	public void SkipsHiddenContentAndKeepsInvisibleSpace()
	{
		LayoutBox root = Lay("<div id=n>gone</div><div id=h>hidden</div><p>text</p>", "#n { display: none } #h { visibility: hidden; height: 30px }");
		Assert.Null(FindId(root, "n"));
		Assert.Equal(30, FindId(root, "h").Height);
		Assert.Equal(46, Find(root, "p").Y);
		DisplayList list = Painter.Paint(root, Width, Height);
		Assert.DoesNotContain(list.Items, i => i.Text == "hidden");
		Assert.Contains(list.Items, i => i.Text == "text");
	}

	[Fact]
	public void PaintsBackgroundsBordersTextAndDecorations()
	{
		LayoutBox root = Lay("<div id=b><a href=x>link</a> <span id=s>mark</span></div>", "#b { background: #eee; border: 3px solid red; padding: 5px; width: 200px } #s { background: yellow }");
		DisplayList list = Painter.Paint(root, Width, Height);
		DisplayItem canvas = list.Items[0];
		Assert.Equal(DisplayItemKind.Rect, canvas.Kind);
		Assert.Equal("#ffffff", canvas.Color.ToString());
		Assert.Contains(list.Items, i => i.Kind == DisplayItemKind.Rect && i.Color.ToString() == "#eeeeee" && i.Width == 216);
		Assert.Equal(4, list.Items.Count(i => i.Kind == DisplayItemKind.Rect && i.Color.ToString() == "#ff0000"));
		DisplayItem link = list.Items.First(i => i.Kind == DisplayItemKind.Text && i.Text == "link");
		Assert.Equal("#0000ee", link.Color.ToString());
		Assert.Equal(8, link.X);
		Assert.Equal(22.5, link.Y);
		Assert.Contains(list.Items, i => i.Kind == DisplayItemKind.Rect && i.Color.ToString() == "#0000ee" && i.Width == 32);
		Assert.Contains(list.Items, i => i.Kind == DisplayItemKind.Rect && i.Color.ToString() == "#ffff00");
	}

	[Fact]
	public void FindsLinksUnderPoints()
	{
		LayoutBox root = Lay("<p>plain <a href='/go'>link text</a> more</p><div id=d style='height: 50px'></div>", "p { margin: 0 }");
		Element link = Painter.LinkFor(Painter.HitTest(root, 60, 5)?.Node);
		Assert.NotNull(link);
		Assert.Equal("/go", link.GetAttribute("href"));
		Assert.Null(Painter.LinkFor(Painter.HitTest(root, 10, 5)?.Node));
		Assert.Equal("d", Painter.HitTest(root, 100, 40)?.Element?.Id);
	}

	[Fact]
	public void KeepsTablesNarrowWhenInlinesHoldBlocks()
	{
		LayoutBox root = Lay("<table width=200><tr><td><font color=green>intro text <p>On deeper inspection this paragraph is far longer than the two hundred pixel table it sits in</p></font></td></tr></table>", "td { padding: 0 }");
		LayoutBox table = Find(root, "table");
		Assert.Equal(200, table.Width);
		LayoutBox p = Find(root, "p");
		Assert.True(p.Width <= 196);
		Assert.True(p.Lines.Count >= 4);
	}

	[Fact]
	public void SizesAndPaintsImagesFromTheirBitmaps()
	{
		Alloy.Sdk.Web.Images.Bitmap picture = new Alloy.Sdk.Web.Images.Bitmap(40, 30);
		Document document = HtmlParser.Parse("<p><img id=a src=a.png alt=A> <img id=b src=a.png width=80> <img id=c src=missing.png alt=Missing></p>");
		StyleResolver resolver = new StyleResolver(new MediaEnvironment(Width, Height));
		resolver.AddStylesheet(CssParser.Parse("body, p { margin: 0 }", new MediaEnvironment(Width, Height)));
		LayoutBox root = new BoxBuilder(resolver, e => e.GetAttribute("src") == "a.png" ? picture : null).Build(document);
		new LayoutEngine(new FixedFontProvider(), Width, Height).Layout(root);
		LayoutBox a = FindId(root, "a");
		LayoutBox b = FindId(root, "b");
		Assert.Equal(40, a.Width);
		Assert.Equal(30, a.Height);
		Assert.Equal(80, b.Width);
		Assert.Equal(60, b.Height);
		Assert.Equal(a.Y + a.Height, b.Y + b.Height);
		Assert.Equal(0, b.Y);
		Assert.Equal(BoxKind.Inline, FindId(root, "c").Kind);
		DisplayList list = Painter.Paint(root, Width, Height);
		Assert.Equal(2, list.Items.Count(i => i.Kind == DisplayItemKind.Image && i.Image == picture));
		Assert.Contains(list.Items, i => i.Kind == DisplayItemKind.Text && i.Text == "Missing");
	}

	[Fact]
	public void HandlesHeadingsParagraphsAndTheFirstWebsite()
	{
		const string html = "<html><head><title>t</title></head><body><header><h1>World Wide Web</h1></header><p>The WorldWideWeb (W3) is a wide-area <a href=a>hypermedia</a> information retrieval initiative aiming to give universal access to a large universe of documents.</p><ul><li><a href=b>What's out there?</a></li><li>Help</li></ul></body></html>";
		LayoutBox root = Lay(html, "body { margin: 8px }");
		LayoutBox h1 = Find(root, "h1");
		Assert.Equal(32, h1.Style.FontSize);
		Assert.Equal(8, h1.X);
		Assert.Equal(21.44f, h1.Y, 2);
		Assert.Equal(38, h1.Height);
		LayoutBox p = Find(root, "p");
		Assert.True(p.Lines.Count >= 2);
		Assert.Equal(784, p.Width);
		LayoutBox ul = Find(root, "ul");
		Assert.Equal(48, ul.Children[0].X);
		Assert.True(root.Height > ul.Y + ul.Height - 1);
	}

	[Fact]
	public void SizesInlineSvgFromItsAttributesOrViewBox()
	{
		LayoutBox sized = Find(Lay("<p><svg width=\"16\" height=\"16\" viewBox=\"0 0 16 16\"></svg></p>"), "svg");
		Assert.Equal(16, sized.Width, 0.5f);
		Assert.Equal(16, sized.Height, 0.5f);
		LayoutBox boxed = Find(Lay("<p><svg viewBox=\"0 0 24 12\"></svg></p>"), "svg");
		Assert.Equal(24, boxed.Width, 0.5f);
		Assert.Equal(12, boxed.Height, 0.5f);
		LayoutBox styled = Find(Lay("<p><svg viewBox=\"0 0 24 12\" class=\"i\"></svg></p>", ".i { width: 48px }"), "svg");
		Assert.Equal(48, styled.Width, 0.5f);
		Assert.Equal(24, styled.Height, 0.5f);
		LayoutBox bare = Find(Lay("<p><svg></svg></p>"), "svg");
		Assert.Equal(300, bare.Width, 0.5f);
		Assert.Equal(150, bare.Height, 0.5f);
	}

	[Fact]
	public void AnInlineWrapperHoldingOnlyBlocksBecomesABlock()
	{
		LayoutBox root = Lay("<body style=\"margin:0\"><my-partial><div id=\"a\">header</div></my-partial><span>x <div id=\"b\">mixed</div> y</span></body>", "body { width: 800px }");
		LayoutBox a = FindId(root, "a");
		Assert.Equal(800, a.Width, 0.5f);
		LayoutBox wrapper = Find(root, "my-partial");
		Assert.Equal(BoxKind.Block, wrapper.Kind);
		LayoutBox b = FindId(root, "b");
		Assert.Equal(BoxKind.InlineBlock, b.Kind);
		// Whitespace around the blocks and nesting, as server-rendered partials have them, add no lines and keep the full width
		root = Lay("<body style=\"margin:0\"><my-partial>\n  <div>\n<div><header id=\"h\" style=\"width:100%\">header</header></div></div>\n</my-partial><my-partial>\n<div id=\"empty\"></div>\n</my-partial><div id=\"after\">after</div></body>", "body { width: 800px; line-height: 21px }");
		LayoutBox h = FindId(root, "h");
		Assert.Equal(800, h.Width, 0.5f);
		Assert.Equal(0, FindId(root, "empty").Height, 0.5f);
		Assert.Equal(21, FindId(root, "after").Y, 0.5f);
	}

	[Fact]
	public void BorderBoxSizingIncludesPaddingAndBorders()
	{
		LayoutBox root = Lay("<body style=\"margin:0\"><div id=\"a\" style=\"width:100px;height:50px;padding:10px;border:5px solid black;box-sizing:border-box\">a</div><div id=\"c\" style=\"width:100px;padding:10px;border:5px solid black\">c</div><div style=\"display:flex;width:600px\"><div id=\"f\" style=\"flex-basis:200px;padding:20px;box-sizing:border-box\">f</div><div id=\"g\" style=\"width:50%;max-width:150px;padding:20px;box-sizing:border-box\">g</div></div><div style=\"display:flex;width:600px\"><div id=\"slot\"><button id=\"b\" style=\"display:grid;grid-template-columns:minmax(0,1fr);width:100%;padding:1px 12px;border:1px solid black;margin:0\"><span style=\"display:flex;gap:8px;width:100%\"><span>Search</span><svg width=\"16\" height=\"16\"></svg></span></button></div><div>auth</div></div></body>");
		LayoutBox a = FindId(root, "a");
		Assert.Equal(100, a.Width, 0.5f);
		Assert.Equal(70, a.ContentWidth, 0.5f);
		Assert.Equal(50, a.Height, 0.5f);
		LayoutBox c = FindId(root, "c");
		Assert.Equal(130, c.Width, 0.5f);
		Assert.Equal(200, FindId(root, "f").Width, 0.5f);
		Assert.Equal(150, FindId(root, "g").Width, 0.5f);
		// A button is border-box by default, so a grid button at 100% fits the flex slot sized from its content
		LayoutBox slot = FindId(root, "slot");
		LayoutBox button = FindId(root, "b");
		Assert.Equal(slot.Width, button.Width, 0.5f);
		Assert.True(slot.Width > 50, "slot width " + slot.Width);
	}

	[Fact]
	public void TheDocumentReachesAsFarAsItsContent()
	{
		LayoutBox root = Lay("<body><div style=\"height:3000px\">tall</div><div style=\"position:fixed;top:0;height:9000px\">pinned</div></body>", "html { height: 100% } body { height: 100% }");
		Assert.Equal(3000, root.Height, 0.5f);
		LayoutBox clipped = Lay("<body><div style=\"height:100px;overflow:hidden\"><div style=\"height:5000px\">hidden</div></div></body>", "html { height: 100% }");
		Assert.Equal(600, clipped.Height, 0.5f);
	}

	[Fact]
	public void PaintsAnInlineBackgroundThatOpensWithAnAtomic()
	{
		LayoutBox root = Lay("<body><p><span style=\"background:red;padding:2px\"><img width=\"10\" height=\"10\"> after</span><span style=\"border:1px solid blue\"><input></span></p></body>");
		DisplayList list = Painter.Paint(root, Width, Height);
		Assert.Contains(list.Items, item => item.Kind == DisplayItemKind.Rect && item.Color.ToString() == "#ff0000");
	}

	[Fact]
	public void APercentageWidePictureLetsItsFlexItemShrink()
	{
		Document document = HtmlParser.Parse("<body style=\"margin:0\"><div style=\"display:flex;width:300px\"><div id=\"card\"><div><img id=\"big\" src=\"x.png\" style=\"width:100%;height:auto\"></div></div><div id=\"side\" style=\"width:100px;flex:none\">s</div></div></body>");
		MediaEnvironment media = new MediaEnvironment(Width, Height);
		StyleResolver resolver = new StyleResolver(media);
		resolver.AddStylesheet(CssParser.Parse("body { margin: 0 }", media));
		Alloy.Sdk.Web.Images.Bitmap picture = new Alloy.Sdk.Web.Images.Bitmap(1000, 500);
		LayoutBox root = new BoxBuilder(resolver, element => element.TagName == "img" ? picture : null).Build(document);
		new LayoutEngine(new FixedFontProvider(), Width, Height).Layout(root);
		LayoutBox card = FindId(root, "card");
		Assert.True(card.Width <= 200.5f, "card width " + card.Width);
		LayoutBox big = FindId(root, "big");
		Assert.True(big.Width <= 200.5f, "image width " + big.Width);
		Assert.Equal(big.Width / 2, big.Height, 1f);
	}

	[Fact]
	public void TranslationsMoveABoxWithoutMovingItsNeighbours()
	{
		LayoutBox root = Lay("<body><div id=\"a\" style=\"height:50px;transform:translate(20px, -50%)\">a</div><div id=\"b\" style=\"height:50px;transform:translateY(10px)\">b</div><div id=\"c\">c</div></body>");
		LayoutBox a = FindId(root, "a");
		LayoutBox b = FindId(root, "b");
		LayoutBox c = FindId(root, "c");
		Assert.Equal(20, a.X, 0.5f);
		Assert.Equal(-25, a.Y, 0.5f);
		Assert.Equal(60, b.Y, 0.5f);
		Assert.Equal(100, c.Y, 0.5f);
	}

	[Fact]
	public void AspectRatioSizesAnEmptyBoxFromItsWidth()
	{
		LayoutBox root = Lay("<body style=\"margin:0\"><div id=\"a\" style=\"width:400px;aspect-ratio:2 / 1\"></div><div id=\"b\" style=\"width:100px;aspect-ratio:1;height:30px\"></div><div id=\"c\" style=\"width:100px;aspect-ratio:4;line-height:20px\">one<br>two<br>three</div></body>");
		Assert.Equal(200, FindId(root, "a").Height, 0.5f);
		Assert.Equal(30, FindId(root, "b").Height, 0.5f);
		Assert.Equal(60, FindId(root, "c").Height, 0.5f);
	}

	[Fact]
	public void PositionedBoxesPaintInZIndexOrder()
	{
		LayoutBox root = Lay("<body><div style=\"position:relative;height:100px\"><div id=\"top\" style=\"position:absolute;left:0;top:0;width:50px;height:50px;background:#ff0000;z-index:5\"></div><div id=\"mid\" style=\"position:absolute;left:10px;top:10px;width:50px;height:50px;background:#00ff00\"></div><div id=\"low\" style=\"position:absolute;left:20px;top:20px;width:50px;height:50px;background:#0000ff;z-index:-1\"></div></div></body>");
		DisplayList list = Painter.Paint(root, Width, Height);
		int red = list.Items.FindIndex(item => item.Kind == DisplayItemKind.Rect && item.Color.ToString() == "#ff0000");
		int green = list.Items.FindIndex(item => item.Kind == DisplayItemKind.Rect && item.Color.ToString() == "#00ff00");
		int blue = list.Items.FindIndex(item => item.Kind == DisplayItemKind.Rect && item.Color.ToString() == "#0000ff");
		Assert.True(blue < green && green < red, $"paint order blue {blue} green {green} red {red}; rects: {string.Join(" ", list.Items.ConvertAll(i => i.Kind + ":" + i.Color + "@" + i.X + "," + i.Y + " " + i.Width + "x" + i.Height))}; top box {FindId(root, "top")?.Width}x{FindId(root, "top")?.Height} positioned {root.Positioned?.Count}");
		Assert.Same(FindId(root, "top"), Painter.HitTest(root, 25, 25));
	}

	[Fact]
	public void PositionedBoxesPaintWithoutAnyAtTheRoot()
	{
		LayoutBox root = Lay("<body><div style=\"position:relative;height:100px\"><div id=\"a\" style=\"position:absolute;left:5px;top:5px;width:50px;height:50px;background:#ff0000\"></div></div></body>");
		LayoutBox a = FindId(root, "a");
		Assert.Equal(50, a.Width);
		Assert.Equal(5, a.Y);
		DisplayList list = Painter.Paint(root, Width, Height);
		Assert.Contains(list.Items, item => item.Kind == DisplayItemKind.Rect && item.Color.ToString() == "#ff0000");
		Assert.Same(a, Painter.HitTest(root, 30, 30));
	}

	[Fact]
	public void PlacesAbsoluteBoxesByRightAndBottomOffsets()
	{
		LayoutBox root = Lay("<body><div style=\"position:relative;width:200px;height:100px\"><div id=\"a\" style=\"position:absolute;right:10px;bottom:20px;width:50px;height:30px\"></div><div id=\"b\" style=\"position:absolute;inset:10px 20px 30px 40px;width:50px;height:30px\"></div><div id=\"c\" style=\"position:absolute;inset-block-start:50%;inset-inline-start:16px;width:10px;height:10px\"></div></div><div id=\"r\" style=\"position:relative;right:10px;bottom:5px;height:20px\">x</div></body>");
		Assert.Equal(140, FindId(root, "a").X);
		Assert.Equal(50, FindId(root, "a").Y);
		Assert.Equal(40, FindId(root, "b").X);
		Assert.Equal(10, FindId(root, "b").Y);
		Assert.Equal(16, FindId(root, "c").X);
		Assert.Equal(50, FindId(root, "c").Y);
		Assert.Equal(-10, FindId(root, "r").X);
		Assert.Equal(95, FindId(root, "r").Y);
	}

	[Fact]
	public void AbsoluteChildrenOfMovedFlexItemsKeepTheirStaticPosition()
	{
		LayoutBox root = Lay("<body><div style=\"display:flex;justify-content:flex-end;width:400px\"><div style=\"width:100px;height:50px\"></div><div id=\"rel\" style=\"position:relative;width:100px;height:50px\"><div id=\"a\" style=\"position:absolute;width:10px;height:10px\"></div></div></div></body>");
		Assert.Equal(300, FindId(root, "rel").X);
		Assert.Equal(300, FindId(root, "a").X);
		Assert.Equal(0, FindId(root, "a").Y);
	}

	[Fact]
	public void TextItemsCarryTheirLetterSpacing()
	{
		LayoutBox root = Lay("<body><p style=\"letter-spacing:-2px\">tight</p><p>plain</p></body>");
		DisplayList list = Painter.Paint(root, Width, Height);
		Assert.Equal(-2, list.Items.Find(item => item.Kind == DisplayItemKind.Text && item.Text == "tight").Spacing);
		Assert.Equal(0, list.Items.Find(item => item.Kind == DisplayItemKind.Text && item.Text == "plain").Spacing);
	}

	[Fact]
	public void AutoMarginsNeverPullAWideBoxLeft()
	{
		LayoutBox root = Lay("<body style=\"margin:0;width:400px\"><div id=\"wide\" style=\"width:500px;margin:0 auto;height:10px\"></div><div id=\"fits\" style=\"width:200px;margin:0 auto;height:10px\"></div><div id=\"capped\" style=\"width:min(100%, 300px);height:10px\"></div></body>");
		Assert.Equal(0, FindId(root, "wide").X);
		Assert.Equal(100, FindId(root, "fits").X);
		Assert.Equal(300, FindId(root, "capped").Width);
	}

	[Fact]
	public void FixedBoxesPaintPinnedAndHitTestAgainstTheViewport()
	{
		LayoutBox root = Lay("<body style=\"margin:0\"><div style=\"height:3000px\">tall</div><div id=\"bar\" style=\"position:fixed;top:0;left:0;width:100px;height:50px;background:#ff0000\"></div></body>");
		DisplayList list = Painter.Paint(root, Width, Height);
		int start = list.Items.FindIndex(item => item.Kind == DisplayItemKind.Fixed);
		int rect = list.Items.FindIndex(item => item.Kind == DisplayItemKind.Rect && item.Color.ToString() == "#ff0000");
		int end = list.Items.FindIndex(item => item.Kind == DisplayItemKind.Unfixed);
		Assert.True(start >= 0 && start < rect && rect < end, $"fixed {start} rect {rect} unfixed {end}");
		LayoutBox bar = FindId(root, "bar");
		Assert.Same(bar, Painter.HitTest(root, 10, 1010, 0, 1000));
		Assert.NotSame(bar, Painter.HitTest(root, 10, 1010));
	}

	[Fact]
	public void RoundedBoxesPaintWithCornersAndRings()
	{
		LayoutBox root = Lay("<body style=\"margin:0\"><div id=\"a\" style=\"width:100px;height:50px;border-radius:10px;background:#ff0000\"></div><div id=\"b\" style=\"width:100px;height:50px;border:2px solid #000000;border-radius:6px\"></div><div id=\"c\" style=\"width:40px;height:40px;border-radius:50%;background:#00ff00\"></div></body>");
		DisplayList list = Painter.Paint(root, Width, Height);
		DisplayItem red = list.Items.Find(item => item.Kind == DisplayItemKind.Rect && item.Color.ToString() == "#ff0000");
		Assert.Equal(10, red.Radius.TopLeft);
		Assert.Equal(0, red.Stroke);
		DisplayItem ring = list.Items.Find(item => item.Kind == DisplayItemKind.Rect && item.Color.ToString() == "#000000" && item.Stroke > 0);
		Assert.Equal(2, ring.Stroke);
		Assert.Equal(6, ring.Radius.BottomRight);
		DisplayItem green = list.Items.Find(item => item.Kind == DisplayItemKind.Rect && item.Color.ToString() == "#00ff00");
		Assert.Equal(20, green.Radius.TopLeft);
	}

	[Fact]
	public void OpacityFadesABoxAndWhatIsInsideIt()
	{
		LayoutBox root = Lay("<body style=\"margin:0\"><div style=\"opacity:0.5;background:#ff0000;width:100px;height:50px\"><p style=\"opacity:0.5\">faded</p></div><div style=\"opacity:0;background:#0000ff;width:10px;height:10px\"></div><p>solid</p></body>");
		DisplayList list = Painter.Paint(root, Width, Height);
		DisplayItem red = list.Items.Find(item => item.Kind == DisplayItemKind.Rect && item.Color.Rgb == 0xff0000);
		Assert.Equal(0.5f, red.Opacity, 0.01f);
		Assert.Equal(0.25f, list.Items.Find(item => item.Kind == DisplayItemKind.Text && item.Text == "faded").Opacity, 0.01f);
		Assert.Equal(1f, list.Items.Find(item => item.Kind == DisplayItemKind.Text && item.Text == "solid").Opacity);
		Assert.DoesNotContain(list.Items, item => item.Kind == DisplayItemKind.Rect && item.Color.Rgb == 0x0000ff);
	}
}
