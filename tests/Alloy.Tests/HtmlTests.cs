using System.Linq;
using Alloy.Sdk.Web.Html;

namespace Alloy.Tests;

public class HtmlTests
{
	private static Document Parse(string html) => HtmlParser.Parse(html);

	[Fact]
	public void BuildsTheImpliedDocumentAroundBareText()
	{
		Document document = Parse("Hello");
		Assert.NotNull(document.DocumentElement);
		Assert.NotNull(document.Head);
		Assert.NotNull(document.Body);
		Assert.Equal("Hello", document.Body.TextContent);
		Assert.Equal("<html><head></head><body>Hello</body></html>", document.OuterHtml);
	}

	[Fact]
	public void ParsesAFullDocumentWithTitleAndParagraphs()
	{
		Document document = Parse("<!DOCTYPE html><html lang=\"en\"><head><title>A &amp; B</title></head><body><h1>Heading</h1><p>One<p>Two</body></html>");
		Assert.False(document.QuirksMode);
		Assert.Equal("A & B", document.Title);
		Assert.Equal("en", document.DocumentElement!.GetAttribute("lang"));
		Element body = document.Body!;
		Assert.Equal(new[] { "h1", "p", "p" }, body.Children.Select(c => ((Element)c).TagName));
		Assert.Equal("Two", body.Children[2].TextContent);
	}

	[Fact]
	public void DecodesCharacterReferences()
	{
		Assert.Equal("a<b&c d€eéf\"", Entities.DecodeAll("a&lt;b&amp;c&nbsp;d&#8364;e&#xE9;f&quot;"));
		Assert.Equal("– ™ �", Entities.DecodeAll("&#150; &#x2122; &#0;"));
		Assert.Equal("© 2024 &unknown; x&y", Entities.DecodeAll("&copy 2024 &unknown; x&y"));
		Assert.Equal("©right", Entities.DecodeAll("&copyright"));
		Document document = Parse("<p title=\"&lt;t&gt;\">&alpha;&Omega;&hellip;</p>");
		Assert.Equal("<t>", document.Body!.FirstElement("p")!.GetAttribute("title"));
		Assert.Equal("αΩ…", document.Body.TextContent);
	}

	[Fact]
	public void ReadsAttributesInEveryStyle()
	{
		Document document = Parse("<a HREF='x' data-n=5 disabled class=\"a b\" title = \"t\" href=\"dup\">l</a>");
		Element a = document.Body!.FirstElement("a")!;
		Assert.Equal("x", a.GetAttribute("href"));
		Assert.Equal("5", a.GetAttribute("data-n"));
		Assert.Equal("", a.GetAttribute("disabled"));
		Assert.True(a.HasClass("b"));
		Assert.Equal("t", a.GetAttribute("title"));
		Assert.Equal(5, a.Attributes.Count);
	}

	[Fact]
	public void HandlesVoidAndSelfClosingElements()
	{
		Document document = Parse("<p>a<br>b<img src=\"i.png\"/>c<hr><input type=text></p>");
		Element body = document.Body!;
		Assert.Equal(new[] { "p", "hr", "input", "p" }, body.Children.OfType<Element>().Select(e => e.TagName));
		Assert.Empty(body.Children[3].Children);
		Element p = (Element)body.Children[0];
		Assert.Equal(new[] { "a", "br", "b", "img", "c" }, p.Children.Select(n => n is Element e ? e.TagName : ((TextNode)n).Data));
		Assert.Equal("i.png", ((Element)p.Children[3]).GetAttribute("src"));
	}

	[Fact]
	public void ClosesListItemsAndParagraphsImplicitly()
	{
		Document document = Parse("<ul><li>one<li>two<p>para<li>three</ul><dl><dt>t<dd>d<dt>t2</dl>");
		Element ul = document.Body!.FirstElement("ul")!;
		Assert.Equal(3, ul.Children.Count);
		Assert.All(ul.Children, child => Assert.Equal("li", ((Element)child).TagName));
		Assert.Equal("two", ((TextNode)ul.Children[1].Children[0]).Data);
		Assert.Equal("p", ((Element)ul.Children[1].Children[1]).TagName);
		Element dl = document.Body.FirstElement("dl")!;
		Assert.Equal(new[] { "dt", "dd", "dt" }, dl.Children.Select(c => ((Element)c).TagName));
	}

	[Fact]
	public void KeepsScriptAndStyleRaw()
	{
		Document document = Parse("<head><style>p > a { x: '<b>'; }</style><script>if (a < b && c) { s = \"</div>\"; }</script></head><body><textarea>\n&lt;x</textarea><pre>\nkeep</pre>");
		Element head = document.Head!;
		Assert.Equal("p > a { x: '<b>'; }", head.FirstElement("style")!.TextContent);
		Assert.Equal("if (a < b && c) { s = \"</div>\"; }", head.FirstElement("script")!.TextContent);
		Assert.Equal("<x", document.Body!.FirstElement("textarea")!.TextContent);
		Assert.Equal("keep", document.Body.FirstElement("pre")!.TextContent);
	}

	[Fact]
	public void HandlesCommentsDoctypesAndBogusMarkup()
	{
		Document document = Parse("<!-- lead --><!DOCTYPE HTML PUBLIC \"-//W3C//DTD HTML 4.01 Transitional//EN\"><p>a<!-- in -->b<? pi ?></p><!>");
		Assert.True(document.QuirksMode);
		Assert.IsType<Comment>(document.Children[0]);
		Element p = document.Body!.FirstElement("p")!;
		Assert.Equal(new[] { NodeType.Text, NodeType.Comment, NodeType.Text, NodeType.Comment }, p.Children.Select(c => c.Type));
		Assert.Equal("ab", p.TextContent);
	}

	[Fact]
	public void BuildsTablesWithImpliedSectionsAndRows()
	{
		Document document = Parse("<table><tr><td>a<td>b<tr><th>c</table><table><td>d</table>");
		Element[] tables = document.Body!.ElementsByTagName("table").ToArray();
		Element tbody = (Element)tables[0].Children[0];
		Assert.Equal("tbody", tbody.TagName);
		Assert.Equal(2, tbody.Children.Count);
		Assert.Equal(new[] { "td", "td" }, tbody.Children[0].Children.Select(c => ((Element)c).TagName));
		Assert.Equal("th", ((Element)tbody.Children[1].Children[0]).TagName);
		Assert.Equal("d", tables[1].TextContent);
		Assert.Equal("tbody", ((Element)tables[1].Children[0]).TagName);
	}

	[Fact]
	public void ClosesMisnestedAndUnclosedElements()
	{
		Document document = Parse("<div><b>bold <i>both</b> italic?</i> plain</div><span>open");
		Element div = document.Body!.FirstElement("div")!;
		Assert.Equal("bold both italic? plain", div.TextContent);
		Assert.Equal("b", ((Element)div.Children[0]).TagName);
		Assert.Equal("open", document.Body.FirstElement("span")!.TextContent);
		Assert.Equal("<body><div><b>bold <i>both</i></b> italic? plain</div><span>open</span></body>", document.Body.OuterHtml);
	}

	[Fact]
	public void HeadElementsAfterBodyStayWhereTheyAreButStillCount()
	{
		Document document = Parse("<body><p>x</p><title>late</title><meta charset=utf-8>");
		Assert.Equal("late", document.Title);
		Assert.NotNull(document.Body!.FirstElement("meta"));
		Assert.Equal("xlate", document.Body.TextContent);
	}

	[Fact]
	public void NormalizesNewlinesAndCase()
	{
		Document document = Parse("<DIV ID=Main>a\r\nb\rc</Div>");
		Element div = (Element)document.Body!.Children[0];
		Assert.Equal("div", div.TagName);
		Assert.Equal("Main", div.Id);
		Assert.Equal("a\nb\nc", div.TextContent);
	}

	[Fact]
	public void HandlesTheFirstWebsite()
	{
		const string page = "<html><head></head><body><header>\n<title>http://info.cern.ch</title>\n</header>\n\n<h1>http://info.cern.ch - home of the first website</h1>\n<p>From here you can:</p>\n<ul>\n<li><a href=\"http://info.cern.ch/hypertext/WWW/TheProject.html\">Browse the first website</a></li>\n<li><a href=\"http://line-mode.cern.ch/www/hypertext/WWW/TheProject.html\">Browse the first website using the line-mode browser simulator</a></li>\n<li><a href=\"http://home.web.cern.ch/topics/birth-web\">Learn about the birth of the web</a></li>\n<li><a href=\"http://home.web.cern.ch/about\">Learn about CERN, the physics laboratory where the web was born</a></li>\n</ul>\n</body></html>";
		Document document = Parse(page);
		Assert.Equal(4, document.Body!.ElementsByTagName("a").Count());
		Assert.Equal("http://info.cern.ch - home of the first website", document.Body.FirstElement("h1")!.TextContent);
		Assert.Equal("Learn about CERN, the physics laboratory where the web was born", document.Body.ElementsByTagName("a").Last().TextContent);
		Assert.Equal("http://info.cern.ch", HtmlText.CollapseWhitespace(document.Body.FirstElement("title")!.TextContent));
	}

	[Fact]
	public void SurvivesGarbage()
	{
		Document document = Parse("<<p>>a</b></p></html></body>text<a href=\"unterminated>more<div</div>");
		Assert.NotNull(document.Body);
		Assert.Contains("a", document.Body!.TextContent);
		Assert.Contains("text", document.Body.TextContent);
	}
}
