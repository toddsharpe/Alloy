using System.Linq;
using Alloy.Sdk.Web.Css;
using Alloy.Sdk.Web.Html;

namespace Alloy.Tests;

public class CssTests
{
	private static readonly MediaEnvironment Screen = new MediaEnvironment(1024, 768);

	private static Stylesheet Sheet(string css) => CssParser.Parse(css, Screen);

	private static ComputedStyle Style(string html, string css, string tag)
	{
		Document document = HtmlParser.Parse(html);
		StyleResolver resolver = new StyleResolver(Screen);
		resolver.AddStylesheet(Sheet(css));
		return resolver.Resolve(document.DescendantElements().First(e => e.TagName == tag));
	}

	[Fact]
	public void TokenizesTheUsualTokens()
	{
		var tokens = CssTokenizer.Tokenize("a.b#c > d[e=\"f\"] { margin: -1.5em 10% url(x.png) url( \"y.png\" ) #abc !important; } /* c */ @media (min-width:600px)");
		Assert.Contains(tokens, t => t.Type == CssTokenType.Dimension && t.Number == -1.5 && t.Unit == "em");
		Assert.Contains(tokens, t => t.Type == CssTokenType.Percentage && t.Number == 10);
		Assert.Contains(tokens, t => t.Type == CssTokenType.Url && t.Value == "x.png");
		Assert.Contains(tokens, t => t.Type == CssTokenType.Function && t.Value == "url");
		Assert.Contains(tokens, t => t.Type == CssTokenType.Hash && t.Value == "abc" && t.HashIsId);
		Assert.Contains(tokens, t => t.Type == CssTokenType.AtKeyword && t.Value == "media");
		Assert.DoesNotContain(tokens, t => t.Type == CssTokenType.Delim && t.Value == "/");
		Assert.Contains(tokens, t => t.Type == CssTokenType.String && t.Value == "f");
	}

	[Fact]
	public void ParsesRulesDeclarationsAndImportant()
	{
		Stylesheet sheet = Sheet("p, div.x { color: red; margin: 0 auto !important } h1 { font-size: 2em; bogus: 1; }");
		Assert.Equal(2, sheet.Rules.Count);
		Assert.Equal(2, sheet.Rules[0].Selectors.Count);
		Assert.Equal(2, sheet.Rules[0].Declarations.Count);
		Assert.True(sheet.Rules[0].Declarations[1].Important);
		Assert.Equal("0 auto", CssValue.Join(sheet.Rules[0].Declarations[1].Value));
		Assert.Equal("bogus", sheet.Rules[1].Declarations[1].Property);
	}

	[Theory]
	[InlineData("p", 1)]
	[InlineData(".a", 1 << 10)]
	[InlineData("#i", 1 << 20)]
	[InlineData("div p.a:first-child", 2 + (2 << 10))]
	[InlineData("a[href]", 1 + (1 << 10))]
	[InlineData("p:not(.a)", 1 + (1 << 10))]
	public void ComputesSpecificity(string selector, int expected)
	{
		Stylesheet sheet = Sheet(selector + " { color: red }");
		Assert.Equal(expected, sheet.Rules[0].Selectors[0].Specificity);
	}

	[Fact]
	public void MatchesSelectorsAgainstTheTree()
	{
		Document document = HtmlParser.Parse("<div id=a class='x y'><p>one</p><p class=z lang=en-US>two</p><span data-k=\"v-w\">s</span><ul><li>1<li>2<li>3</ul></div>");
		Element p1 = document.DescendantElements().First(e => e.TagName == "p");
		Element p2 = document.DescendantElements().Last(e => e.TagName == "p");
		Element span = document.Body!.FirstElement("span")!;
		Element[] items = document.Body.ElementsByTagName("li").ToArray();

		bool M(string selector, Element element) => Sheet(selector + "{}").Rules[0].Selectors[0].Matches(element);
		var failures = new System.Collections.Generic.List<string>();
		void Expect(bool expected, string selector, Element element)
		{
			if (M(selector, element) != expected)
				failures.Add((expected ? "" : "!") + selector);
		}
		Expect(true, "#a p", p1);
		Expect(true, "div > p.z", p2);
		Expect(false, "div > p.z", p1);
		Expect(true, "p + p", p2);
		Expect(false, "p + p", p1);
		Expect(true, "p ~ span", span);
		Expect(true, "[data-k^=v]", span);
		Expect(true, "[data-k*='-w']", span);
		Expect(true, "[data-k|=v]", span);
		Expect(false, "[data-k=V]", span);
		Expect(true, "[data-k=V-W i]", span);
		Expect(true, "p:first-child", p1);
		Expect(true, "p:last-of-type", p2);
		Expect(true, "li:nth-child(2)", items[1]);
		Expect(true, "li:nth-child(odd)", items[2]);
		Expect(false, "li:nth-child(odd)", items[1]);
		Expect(true, "li:nth-child(2n+1)", items[0]);
		Expect(true, "li:last-child", items[2]);
		Expect(true, "p:not(.z)", p1);
		Expect(false, "p:not(.z)", p2);
		Expect(true, "p:lang(en)", p2);
		Expect(true, "div.x.y", p1.ParentElement!);
		Expect(false, "div.x.w", p1.ParentElement!);
		Expect(true, "*", span);
		Expect(false, "p:hover", p1);
		Assert.Empty(failures);
	}

	[Fact]
	public void CascadesByOriginImportanceSpecificityAndOrder()
	{
		const string css = "p { color: red } .a { color: green } p { color: blue } #i { color: black } p { color: yellow !important } .later { font-size: 20px } .later { font-size: 30px }";
		Assert.Equal("#ffff00", Style("<p class=a id=i>x</p>", css, "p").Color.ToString());
		Assert.Equal("#000000", Style("<p class=a id=i>x</p>", "p { color: red } .a { color: green } p { color: blue } #i { color: black }", "p").Color.ToString());
		Assert.Equal(30, Style("<p class=later>x</p>", css, "p").FontSize);
		Assert.Equal("#ff0000", Style("<p style=\"color: red\" class=a id=i>x</p>", ".a { color: green } #i { color: black }", "p").Color.ToString());
		Assert.Equal("#00ff00", Style("<p style=\"color: red\">x</p>", "p { color: lime !important }", "p").Color.ToString());
	}

	[Fact]
	public void InheritsAndResolvesFontRelativeLengths()
	{
		const string html = "<html><body><div class=o><p><b>x</b></p></div></body></html>";
		const string css = "html { font-size: 20px } .o { font-size: 1.5em; color: navy; margin: 1em; padding: 2rem } p { margin: 0.5em; line-height: 2 } b { font-size: 50% }";
		ComputedStyle div = Style(html, css, "div");
		Assert.Equal(30, div.FontSize);
		Assert.Equal(30, div.Margin.Top.Value);
		Assert.Equal(40, div.Padding.Left.Value);
		ComputedStyle p = Style(html, css, "p");
		Assert.Equal(30, p.FontSize);
		Assert.Equal(15, p.Margin.Top.Value);
		Assert.Equal(60, p.LineHeight);
		Assert.Equal("#000080", p.Color.ToString());
		ComputedStyle b = Style(html, css, "b");
		Assert.Equal(15, b.FontSize);
		Assert.True(b.Bold);
		Assert.Equal(60, b.LineHeight);
	}

	[Fact]
	public void ExpandsShorthands()
	{
		ComputedStyle style = Style("<div>x</div>", "div { margin: 1px 2px 3px; padding: 4px 5px; border: 2px dashed #f00; font: italic bold 12px/1.5 Georgia, serif; background: url(a.png) #eee no-repeat }", "div");
		Assert.Equal("1px 2px 3px 2px", style.Margin.ToString());
		Assert.Equal("4px 5px 4px 5px", style.Padding.ToString());
		Assert.Equal(2, style.BorderLeft.Width);
		Assert.Equal(BorderStyle.Dashed, style.BorderBottom.Style);
		Assert.Equal("#ff0000", style.BorderTop.Color.ToString());
		Assert.True(style.Italic);
		Assert.True(style.Bold);
		Assert.Equal(12, style.FontSize);
		Assert.Equal(18, style.LineHeight);
		Assert.Equal(FontFamily.Serif, style.FontFamily);
		Assert.Equal("#eeeeee", style.BackgroundColor.ToString());
		Assert.Equal("a.png", style.BackgroundImage);
		Assert.Equal("#eeeeee", Style("<div>x</div>", "div { background: linear-gradient(to right, rgb(1, 2, 3), blue) #eee }", "div").BackgroundColor.ToString());
		Assert.Equal("#ff0000", Style("<div>x</div>", "div { background: rgb(255, 0, 0) }", "div").BackgroundColor.ToString());
	}

	[Fact]
	public void ParsesColorsInEveryForm()
	{
		Assert.Equal("#ff8800", Style("<p>x</p>", "p { color: #f80 }", "p").Color.ToString());
		Assert.Equal("#102030", Style("<p>x</p>", "p { color: rgb(16, 32, 48) }", "p").Color.ToString());
		Assert.Equal("rgba(16, 32, 48, 0.5)", Style("<p>x</p>", "p { color: rgba(16 32 48 / 50%) }", "p").Color.ToString());
		Assert.Equal("#ff0000", Style("<p>x</p>", "p { color: hsl(0, 100%, 50%) }", "p").Color.ToString());
		Assert.Equal("#663399", Style("<p>x</p>", "p { color: RebeccaPurple }", "p").Color.ToString());
		Assert.True(Style("<p>x</p>", "p { background-color: transparent }", "p").BackgroundColor.IsTransparent);
		Assert.Equal(0x80, Style("<p>x</p>", "p { color: #00000080 }", "p").Color.A);
	}

	[Fact]
	public void AppliesTheUserAgentSheet()
	{
		const string html = "<h1>t</h1><p>p<b>b</b><a href=x>l</a></p><ul><li>i</li></ul><script>s</script><pre>p</pre><table><tr><td>c</td></tr></table>";
		Assert.Equal(Display.Block, Style(html, "", "h1").Display);
		Assert.Equal(32, Style(html, "", "h1").FontSize);
		Assert.True(Style(html, "", "h1").Bold);
		Assert.Equal(16, Style(html, "", "p").Margin.Top.Value);
		Assert.Equal(Display.Inline, Style(html, "", "b").Display);
		Assert.True(Style(html, "", "a").Underline);
		Assert.Equal("#0000ee", Style(html, "", "a").Color.ToString());
		Assert.Equal(Display.ListItem, Style(html, "", "li").Display);
		Assert.Equal(40, Style(html, "", "ul").Padding.Left.Value);
		Assert.Equal(Display.None, Style(html, "", "script").Display);
		Assert.Equal(WhiteSpace.Pre, Style(html, "", "pre").WhiteSpace);
		Assert.Equal(FontFamily.Mono, Style(html, "", "pre").FontFamily);
		Assert.Equal(Display.TableCell, Style(html, "", "td").Display);
		Assert.Equal(8, Style(html, "", "body").Margin.Left.Value);
	}

	[Fact]
	public void EvaluatesMediaQueries()
	{
		Stylesheet sheet = Sheet("@media screen and (min-width: 600px) { p { color: red } } @media (max-width: 500px) { p { color: blue } } @media print { p { color: green } } @media not screen { p { color: pink } } @font-face { font-family: x; src: url(y) } @import url(more.css); p { margin: 0 }");
		Assert.Equal(2, sheet.Rules.Count);
		Assert.Equal("red", sheet.Rules[0].Declarations[0].Value[0].Value);
		Assert.Equal("margin", sheet.Rules[1].Declarations[0].Property);
		Assert.Equal("more.css", sheet.Imports[0]);
	}

	[Fact]
	public void HonoursPresentationalAttributes()
	{
		const string html = "<body bgcolor=\"#ccc\" text=navy><table width=50% border=1 cellpadding=4><tr><td>c</td></tr></table><img width=120 height=80 align=right><font color=red size=5>f</font><p align=center>p</p></body>";
		Assert.Equal("#cccccc", Style(html, "", "body").BackgroundColor.ToString());
		Assert.Equal("#000080", Style(html, "", "body").Color.ToString());
		Assert.Equal(50, Style(html, "", "table").Width.Value);
		Assert.True(Style(html, "", "table").Width.IsPercent);
		Assert.Equal(4, Style(html, "", "td").Padding.Left.Value);
		Assert.Equal(1, Style(html, "", "td").BorderLeft.Effective);
		Assert.Equal(120, Style(html, "", "img").Width.Value);
		Assert.Equal(FloatSide.Right, Style(html, "", "img").Float);
		Assert.Equal("#ff0000", Style(html, "", "font").Color.ToString());
		Assert.Equal(24, Style(html, "", "font").FontSize);
		Assert.Equal(TextAlign.Center, Style(html, "", "p").TextAlign);
	}

	[Fact]
	public void SubstitutesCustomProperties()
	{
		const string html = "<html><body><div class=card><p>x</p></div></body></html>";
		const string css = ":root { --ink: #18231c; --gap: 2em; --pad: var(--gap) } .card { --ink: red; padding: var(--pad); margin: var(--missing, 3px) } p { color: var(--ink); background: light-dark(#eee, #222); font-size: var(--nothing) }";
		ComputedStyle div = Style(html, css, "div");
		Assert.Equal(32, div.Padding.Top.Value);
		Assert.Equal(3, div.Margin.Left.Value);
		ComputedStyle p = Style(html, css, "p");
		Assert.Equal("#ff0000", p.Color.ToString());
		Assert.Equal("#eeeeee", p.BackgroundColor.ToString());
		Assert.Equal(16, p.FontSize);
		Assert.Equal("#18231c", Style(html, css, "body").Custom["--ink"][0].ToString());
	}

	[Fact]
	public void DropsRulesWithUnsupportedSelectorsOnly()
	{
		Stylesheet sheet = Sheet("p::before { content: 'x' } p:hover, q { color: red } ::selection { color: blue } .ok { color: green } a:has(b) { color: red }");
		Assert.Equal(3, sheet.Rules.Count);
		Assert.Equal(2, sheet.Rules[1].Selectors.Count);
		Assert.Equal("ok", sheet.Rules[2].Selectors[0].Compounds[0].Parts[0].Name);
		Assert.Equal("before", sheet.Rules[0].Selectors[0].PseudoElement);
	}
}
