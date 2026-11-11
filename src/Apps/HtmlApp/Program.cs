using System;
using System.Collections.Generic;
using System.Linq;
using Alloy.Sdk;
using Alloy.Sdk.Web;
using Alloy.Sdk.Web.Html;

namespace Alloy.Apps.HtmlApp
{
	// An outline of a page from the terminal: html <url> fetches it, parses it and prints the title, headings, link count and the first links
	internal static class Program
	{
		private const int MaxListed = 12;

		private static int Main(string[] args)
		{
			if (args.Length < 1)
			{
				Console.WriteLine("usage: html <url>");
				return 1;
			}
			string url = args[0].Contains("://") ? args[0] : "http://" + args[0];
			HttpResponse response = Http.Get(url);
			if (response.Status == 0)
			{
				Console.WriteLine($"html: {response.Error}");
				return 1;
			}
			ulong start = Kernel.TickCount;
			Document document = HtmlParser.Parse(response.BodyText());
			ulong parsed = Kernel.TickCount - start;

			int nodes = document.Descendants().Count();
			int elements = document.DescendantElements().Count();
			Console.WriteLine($"html: {response.Status} {response.Body.Length} bytes, {elements} elements in {nodes} nodes, parsed in {parsed} ms{(document.QuirksMode ? ", quirks mode" : "")}");
			Console.WriteLine($"  title: {document.Title}");
			foreach (Element heading in document.DescendantElements().Where(e => HtmlTags.IsHeading(e.TagName)).Take(MaxListed))
				Console.WriteLine($"  {heading.TagName}: {Trim(HtmlText.CollapseWhitespace(heading.TextContent), 70)}");
			List<Element> links = document.DescendantElements().Where(e => e.TagName == "a" && e.HasAttribute("href")).ToList();
			Console.WriteLine($"  {links.Count} links, {document.DescendantElements().Count(e => e.TagName == "img")} images, {document.DescendantElements().Count(e => e.TagName == "script")} scripts");
			foreach (Element link in links.Take(MaxListed))
			{
				string text = Trim(HtmlText.CollapseWhitespace(link.TextContent), 40);
				Uri.TryCreate(response.Url, link.GetAttribute("href"), out Uri target);
				Console.WriteLine($"    {text,-40} {Trim(target?.ToString() ?? link.GetAttribute("href"), 60)}");
			}
			return 0;
		}

		private static string Trim(string text, int length) => text.Length <= length ? text : text.Substring(0, length - 3) + "...";
	}
}
