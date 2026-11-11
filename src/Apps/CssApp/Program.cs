using System;
using System.Collections.Generic;
using System.Linq;
using Alloy.Sdk;
using Alloy.Sdk.Web;
using Alloy.Sdk.Web.Css;
using Alloy.Sdk.Web.Html;

namespace Alloy.Apps.CssApp
{
	// Computed styles from the terminal: css <url> fetches a page and its stylesheets and prints what the cascade decided for the first elements
	internal static class Program
	{
		private const int MaxElements = 22;

		private static int Main(string[] args)
		{
			if (args.Length < 1)
			{
				Console.WriteLine("usage: css <url>");
				return 1;
			}
			string url = args[0].Contains("://") ? args[0] : "http://" + args[0];
			HttpResponse response = Http.Get(url);
			if (response.Status == 0)
			{
				Console.WriteLine($"css: {response.Error}");
				return 1;
			}
			Document document = HtmlParser.Parse(response.BodyText());
			MediaEnvironment media = new MediaEnvironment(1024, 768);
			StyleResolver resolver = new StyleResolver(media);

			int rules = 0;
			int sheets = 0;
			foreach (Element element in document.DescendantElements().ToList())
			{
				string css = null;
				if (element.TagName == "style")
					css = element.TextContent;
				else if (element.TagName == "link" && (element.GetAttribute("rel") ?? "").Contains("stylesheet", StringComparison.OrdinalIgnoreCase) && element.HasAttribute("href") && Uri.TryCreate(response.Url, element.GetAttribute("href"), out Uri href))
				{
					HttpResponse sheetResponse = Http.Get(href);
					css = sheetResponse.Ok ? sheetResponse.BodyText() : null;
					Console.WriteLine($"  stylesheet {href}: {(sheetResponse.Ok ? $"{sheetResponse.Body.Length} bytes" : sheetResponse.Error ?? sheetResponse.Status.ToString())}");
				}
				if (css == null)
					continue;
				Stylesheet sheet = CssParser.Parse(css, media);
				resolver.AddStylesheet(sheet);
				rules += sheet.Rules.Count;
				sheets++;
			}

			ulong start = Kernel.TickCount;
			int styled = 0;
			foreach (Element element in document.DescendantElements())
			{
				resolver.Resolve(element);
				styled++;
			}
			ulong elapsed = Kernel.TickCount - start;
			int shown = 0;
			foreach (Element element in document.DescendantElements())
			{
				ComputedStyle style = resolver.Resolve(element);
				if (style.Display == Display.None || element.TagName == "script" || element.TagName == "style")
					continue;
				string name = element.TagName + (element.Id != null ? "#" + element.Id : "") + (element.ClassName.Length > 0 ? "." + element.ClassName.Replace(' ', '.') : "");
				string font = $"{style.FontSize:0.#}{(style.Bold ? "b" : "")}{(style.Italic ? "i" : "")}";
				string extras = (style.BackgroundColor.IsTransparent ? "" : " bg " + style.BackgroundColor) + (style.Margin.ToString() == "0px 0px 0px 0px" ? "" : " m " + style.Margin) + (style.Padding.ToString() == "0px 0px 0px 0px" ? "" : " p " + style.Padding);
				Console.WriteLine($"  {Trim(name, 20),-20} {Trim(style.Display.ToString(), 9),-9} {font,-6} {style.FontFamily,-5} {style.Color}{extras}");
				if (++shown >= MaxElements)
					break;
			}
			Console.WriteLine($"css: {sheets} sheets, {rules} rules, {styled} elements styled in {elapsed} ms");
			return 0;
		}

		private static string Trim(string text, int length) => text.Length <= length ? text : text.Substring(0, length - 3) + "...";
	}
}
