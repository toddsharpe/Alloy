using Alloy.Sdk.Web.Css;
using Alloy.Sdk.Web.Html;
using Alloy.Sdk.Web.Images;
using Alloy.Sdk.Web.Layout;

namespace Alloy.Tests;

public class SvgTests
{
	private static Bitmap Render(string svg, int width = 16, int height = 16, uint current = 0xFF000000)
	{
		Document document = HtmlParser.Parse("<body>" + svg + "</body>");
		Element element = document.DescendantElements().First(e => e.TagName == "svg");
		return Svg.Render(element, width, height, new Color(current));
	}

	private static int Alpha(Bitmap bitmap, int x, int y) => (int)(bitmap[x, y] >> 24);

	[Fact]
	public void FillsBasicShapes()
	{
		Bitmap rect = Render("<svg viewBox=\"0 0 16 16\"><rect width=\"8\" height=\"16\" fill=\"#ff0000\"/><circle cx=\"12\" cy=\"8\" r=\"3\" fill=\"#00ff00\"/></svg>");
		Assert.Equal(0xFFFF0000u, rect[4, 8]);
		Assert.Equal(0xFF00FF00u, rect[12, 8]);
		Assert.Equal(0, Alpha(rect, 12, 1));
	}

	[Fact]
	public void CurrentColorIsTheElementsColour()
	{
		Bitmap bitmap = Render("<svg viewBox=\"0 0 16 16\" fill=\"currentColor\"><path d=\"M0 0h16v16H0z\"/></svg>", 16, 16, 0xFF0000FF);
		Assert.Equal(0xFF0000FFu, bitmap[8, 8]);
	}

	[Fact]
	public void ParsesCompactPathsWithArcs()
	{
		Bitmap bars = Render("<svg viewBox=\"0 0 16 16\"><path d=\"M1 2.75A.75.75 0 0 1 1.75 2h12.5a.75.75 0 0 1 0 1.5H1.75A.75.75 0 0 1 1 2.75Zm0 5A.75.75 0 0 1 1.75 7h12.5a.75.75 0 0 1 0 1.5H1.75A.75.75 0 0 1 1 7.75ZM1.75 12h12.5a.75.75 0 0 1 0 1.5H1.75a.75.75 0 0 1 0-1.5Z\"/></svg>");
		Assert.True(Alpha(bars, 8, 2) > 200);
		Assert.True(Alpha(bars, 8, 7) > 200);
		Assert.True(Alpha(bars, 8, 12) > 200);
		Assert.Equal(0, Alpha(bars, 8, 5));
		Assert.Equal(0, Alpha(bars, 8, 10));
	}

	[Fact]
	public void EvenOddLeavesAHole()
	{
		Bitmap ring = Render("<svg viewBox=\"0 0 16 16\"><path fill-rule=\"evenodd\" d=\"M0 0h16v16H0z M4 4h8v8H4z\"/></svg>");
		Assert.Equal(255, Alpha(ring, 2, 2));
		Assert.Equal(0, Alpha(ring, 8, 8));
		Bitmap solid = Render("<svg viewBox=\"0 0 16 16\"><path d=\"M0 0h16v16H0z M4 4h8v8H4z\"/></svg>");
		Assert.Equal(255, Alpha(solid, 8, 8));
	}

	[Fact]
	public void StrokesLinesWithWidth()
	{
		Bitmap line = Render("<svg viewBox=\"0 0 16 16\"><line x1=\"0\" y1=\"8\" x2=\"16\" y2=\"8\" stroke=\"#000\" stroke-width=\"2\"/></svg>");
		Assert.True(Alpha(line, 8, 8) > 200);
		Assert.Equal(0, Alpha(line, 8, 2));
		Bitmap outline = Render("<svg viewBox=\"0 0 24 24\" fill=\"none\" stroke=\"currentColor\" stroke-width=\"2\"><circle cx=\"12\" cy=\"12\" r=\"10\"/></svg>", 24, 24);
		Assert.True(Alpha(outline, 12, 2) > 100);
		Assert.Equal(0, Alpha(outline, 12, 12));
	}

	[Fact]
	public void UseAndTransformsPlaceShapes()
	{
		Bitmap bitmap = Render("<svg viewBox=\"0 0 16 16\"><defs><path id=\"p\" d=\"M0 0h4v4H0z\"/></defs><use href=\"#p\" transform=\"translate(12 12)\"/><g transform=\"translate(8 0) scale(2)\"><rect width=\"2\" height=\"2\" fill=\"#00f\"/></g></svg>");
		Assert.Equal(255, Alpha(bitmap, 14, 14));
		Assert.Equal(0, Alpha(bitmap, 2, 2));
		Assert.Equal(0xFF0000FFu, bitmap[10, 2]);
	}

	[Fact]
	public void ScalesTheViewBoxToTheBoxAndCentresIt()
	{
		Bitmap doubled = Render("<svg viewBox=\"0 0 16 16\"><rect width=\"8\" height=\"8\"/></svg>", 32, 32);
		Assert.Equal(255, Alpha(doubled, 15, 15));
		Assert.Equal(0, Alpha(doubled, 17, 17));
		Bitmap wide = Render("<svg viewBox=\"0 0 16 16\"><rect width=\"16\" height=\"16\"/></svg>", 48, 16);
		Assert.Equal(0, Alpha(wide, 8, 8));
		Assert.Equal(255, Alpha(wide, 24, 8));
	}

	[Fact]
	public void SelfClosedShapesAreSiblings()
	{
		Document document = HtmlParser.Parse("<body><svg viewBox=\"0 0 16 16\"><rect width=\"8\" height=\"8\"/><rect x=\"8\" width=\"8\" height=\"8\"/></svg></body>");
		Element svg = document.DescendantElements().First(e => e.TagName == "svg");
		Assert.Equal(2, svg.Children.Count);
	}

	[Fact]
	public void InlineSvgPaintsAsAnImage()
	{
		LayoutBox root = LayoutTests.Lay("<body style=\"margin:0;color:#ff0000\"><svg width=\"16\" height=\"16\" viewBox=\"0 0 16 16\" fill=\"currentColor\"><rect width=\"16\" height=\"16\"/></svg></body>");
		DisplayList list = Painter.Paint(root, 800, 600);
		DisplayItem item = list.Items.Find(i => i.Kind == DisplayItemKind.Image);
		Assert.NotNull(item.Image);
		Assert.Equal(16, item.Image.Width);
		Assert.Equal(0xFFFF0000u, item.Image[8, 8]);
	}
}
