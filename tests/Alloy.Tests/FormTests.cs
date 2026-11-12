using System.Text;
using Alloy.Sdk.Web;
using Alloy.Sdk.Web.Html;

namespace Alloy.Tests;

public class FormTests
{
	private static readonly Uri PageUrl = new Uri("https://example.com/dir/page.html?old=1");

	private static Element Find(Document document, string id) => document.DescendantElements().First(e => e.Id == id);

	[Fact]
	public void EncodesFormData()
	{
		string encoded = Forms.Encode(new[]
		{
			new KeyValuePair<string, string>("q", "alloy os"),
			new KeyValuePair<string, string>("x", "a&b=c/d?"),
			new KeyValuePair<string, string>("u", "café"),
			new KeyValuePair<string, string>("empty", ""),
		});
		Assert.Equal("q=alloy+os&x=a%26b%3Dc%2Fd%3F&u=caf%C3%A9&empty=", encoded);
	}

	[Fact]
	public void SubmitsAGetFormIntoTheQuery()
	{
		Document document = HtmlParser.Parse("<form action='/search' method=get><input name=q value='hello world'><input type=hidden name=lang value=en><input id=go type=submit name=btn value=Go><input type=submit name=other value=No></form>");
		FormSubmission submission = Forms.Submit(Find(document, "go"), PageUrl);
		Assert.NotNull(submission);
		Assert.Equal("GET", submission.Method);
		Assert.Equal("https://example.com/search?q=hello+world&lang=en&btn=Go", submission.Url.ToString());
		Assert.Null(submission.Body);
		FormSubmission fromField = Forms.Submit(document.Body.FirstElement("input"), PageUrl);
		Assert.Equal("https://example.com/search?q=hello+world&lang=en&btn=Go", fromField.Url.ToString());
	}

	[Fact]
	public void PostsIntoTheBodyAndResolvesActions()
	{
		Document document = HtmlParser.Parse("<form method=POST action=submit.cgi><textarea name=t>line one</textarea><select name=s><option value=a>A</option><option value=b selected>B</option></select><button id=b type=submit>Send</button></form>");
		FormSubmission submission = Forms.Submit(Find(document, "b"), PageUrl);
		Assert.Equal("POST", submission.Method);
		Assert.Equal("https://example.com/dir/submit.cgi", submission.Url.ToString());
		Assert.Equal("application/x-www-form-urlencoded", submission.ContentType);
		Assert.Equal("t=line+one&s=b", Encoding.ASCII.GetString(submission.Body));
		Document noAction = HtmlParser.Parse("<form><input name=a value=1></form>");
		Assert.Equal("https://example.com/dir/page.html?a=1", Forms.Submit(noAction.Body.FirstElement("form"), PageUrl).Url.ToString());
	}

	[Fact]
	public void IncludesOnlyCheckedBoxesAndEnabledControls()
	{
		Document document = HtmlParser.Parse("<form><input type=checkbox name=c1 value=yes checked><input type=checkbox name=c2 value=yes><input type=radio name=r value=one><input id=two type=radio name=r value=two><input name=d value=x disabled><input value=skip><input type=checkbox name=plain checked></form>");
		Element two = Find(document, "two");
		Forms.Toggle(two);
		Assert.True(two.HasAttribute("checked"));
		Assert.False(document.DescendantElements().First(e => e.GetAttribute("value") == "one").HasAttribute("checked"));
		FormSubmission submission = Forms.Submit(document.Body.FirstElement("form"), PageUrl);
		Assert.Equal("https://example.com/dir/page.html?c1=yes&r=two&plain=on", submission.Url.ToString());
		Element c1 = document.DescendantElements().First(e => e.GetAttribute("name") == "c1");
		Forms.Toggle(c1);
		Assert.False(c1.HasAttribute("checked"));
	}

	[Fact]
	public void TypesIntoFieldsAndCyclesSelects()
	{
		Document document = HtmlParser.Parse("<form><input id=f name=f placeholder='type here'><select id=s name=s><option>x</option><option>y</option></select><textarea id=t name=t>abc</textarea></form>");
		Element field = Find(document, "f");
		Assert.True(Forms.IsTextField(field));
		Assert.Equal("", Forms.ValueOf(field));
		Forms.SetValue(field, "hi");
		Assert.Equal("hi", Forms.ValueOf(field));
		Element select = Find(document, "s");
		Assert.Equal("x", Forms.ValueOf(select));
		Forms.Advance(select);
		Assert.Equal("y", Forms.ValueOf(select));
		Forms.Advance(select);
		Assert.Equal("x", Forms.ValueOf(select));
		Element area = Find(document, "t");
		Assert.True(Forms.IsTextField(area));
		Assert.Equal("abc", Forms.ValueOf(area));
		Assert.False(Forms.IsTextField(select));
		Assert.Equal("https://example.com/dir/page.html?f=hi&s=x&t=abc", Forms.Submit(field, PageUrl).Url.ToString());
		Assert.Null(Forms.Submit(HtmlParser.Parse("<input name=orphan>").Body.FirstElement("input"), PageUrl));
	}

	[Fact]
	public void LaysOutFieldsWithTheirText()
	{
		Document document = HtmlParser.Parse("<form><input id=f value='some typed text that is far too long for the box' size=10><input id=p placeholder=Search><input id=c type=checkbox checked></form>");
		var media = new Alloy.Sdk.Web.Css.MediaEnvironment(800, 600);
		var resolver = new Alloy.Sdk.Web.Css.StyleResolver(media);
		var root = new Alloy.Sdk.Web.Layout.BoxBuilder(resolver).Build(document);
		new Alloy.Sdk.Web.Layout.LayoutEngine(new Alloy.Sdk.Web.Layout.FixedFontProvider(), 800, 600).Layout(root);
		Alloy.Sdk.Web.Layout.LayoutBox field = FindBox(root, "f");
		Assert.Equal(13.3333f * 10 * 0.55f + 6, field.Width, 1);
		Assert.Single(field.Lines);
		Assert.Equal(13.3333f * 1.3f + 4, field.Height, 1);
		Alloy.Sdk.Web.Layout.LayoutBox placeholder = FindBox(root, "p");
		Assert.Equal("Search", placeholder.Lines[0].Fragments[0].Text);
		Assert.Equal("#757575", placeholder.Lines[0].Fragments[0].Style.Color.ToString());
		Alloy.Sdk.Web.Layout.LayoutBox box = FindBox(root, "c");
		Assert.Equal(15, box.Width);
		Assert.Equal("#1a73e8", box.Style.BackgroundColor.ToString());
	}

	private static Alloy.Sdk.Web.Layout.LayoutBox FindBox(Alloy.Sdk.Web.Layout.LayoutBox box, string id)
	{
		if (box.Element?.Id == id && box.Kind != Alloy.Sdk.Web.Layout.BoxKind.Text)
			return box;
		foreach (var child in box.Children)
		{
			var found = FindBox(child, id);
			if (found != null)
				return found;
		}
		return null!;
	}
}
