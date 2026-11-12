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

	private static LayoutBox Lay(string html, string css = "")
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

	private static LayoutBox FindId(LayoutBox box, string id)
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
	}
}
