using System;
using System.Collections.Generic;
using Alloy.Sdk.Web.Css;
using Alloy.Sdk.Web.Dom;
using Alloy.Sdk.Web.Html;
using Alloy.Sdk.Web.Images;
using Alloy.Sdk.Web.Layout;

namespace Alloy.Sdk.Web
{
	// A fetched page: its document, the stylesheets it linked and embedded, and the layout and display list for a viewport
	public sealed class Page
	{
		public Uri Url;
		public Document Document;
		public string Error;
		public int Status;

		// Where a redirect the client would not follow points (another scheme, usually https)
		public string Redirect;
		// Decoded images by absolute URL, and how many img elements asked for one
		public readonly Dictionary<string, Bitmap> Images = new Dictionary<string, Bitmap>(StringComparer.Ordinal);
		public int ImageCount;
		public const int MaxImages = 60;
		public const int MaxImageBytes = 8 * 1024 * 1024;
		public const int ImageThreads = 4;
		public readonly List<string> StylesheetUrls = new List<string>();
		public readonly List<string> StylesheetText = new List<string>();
		public LayoutBox Root;
		public DisplayList Display;
		public long FetchMs;
		public long LayoutMs;
		// The page's scripts and what they see; null when scripting is off
		public ScriptHost Scripts;
		public static bool ScriptsEnabled = true;
		public static IFontProvider DefaultFonts;
		public const int DefaultViewportWidth = 1200;
		public const int DefaultViewportHeight = 746;
		private readonly Dictionary<Element, string> _linkSheets = new Dictionary<Element, string>();

		public string Title => Document?.Title ?? Url?.Host ?? "";

		static Page()
		{
			WallClock.Install();
		}

		public static double EpochNow() => WallClock.EpochNow();

		// Fetches a URL and its stylesheets; a failure is reported through Error and an empty document
		public static Page Load(string url, ulong timeoutMs = 15000) => Load(new FormSubmission { Url = Uri.TryCreate(url, UriKind.Absolute, out Uri parsed) ? parsed : null, Method = "GET" }, url, timeoutMs);

		public static Page Load(FormSubmission submission, ulong timeoutMs = 15000) => Load(submission, submission.Url?.ToString() ?? "", timeoutMs);

		private static Page Load(FormSubmission submission, string url, ulong timeoutMs)
		{
			Page page = new Page();
			ulong start = Kernel.TickCount;
			HttpResponse response = submission.Url == null ? Http.Get(url, timeoutMs) : Http.Request(submission.Method, submission.Url, submission.Body, submission.ContentType, timeoutMs);
			page.Status = response.Status;
			page.Url = response.Url ?? (Uri.TryCreate(url, UriKind.Absolute, out Uri parsed) ? parsed : null);
			if (response.Status == 0)
			{
				page.Error = response.Error ?? "request failed";
				page.Document = ErrorDocument($"Can't open {Escape(url)}", Escape(page.Error));
				page.CollectStylesheets(timeoutMs);
				page.FetchMs = (long)(Kernel.TickCount - start);
				return page;
			}
			if (response.Status >= 300 && response.Status < 400 && response.Header("Location") is string location)
			{
				page.Redirect = location;
				page.Error = $"redirects to {location}";
				page.Document = ErrorDocument("This page moved", $"{Escape(url)} redirects to <a href=\"{Escape(location)}\">{Escape(location)}</a>.");
				page.CollectStylesheets(timeoutMs);
				page.FetchMs = (long)(Kernel.TickCount - start);
				return page;
			}
			string type = response.ContentType ?? "";
			if (type.StartsWith("image/", StringComparison.OrdinalIgnoreCase) || (ImageDecoder.Sniff(response.Body) != null && !type.Contains("html", StringComparison.OrdinalIgnoreCase)))
			{
				// A bare image: shown on a page of its own, the way browsers do
				string key = page.Url?.ToString() ?? url;
				if (ImageDecoder.TryDecode(response.Body, out Bitmap bitmap, out string decodeError))
				{
					page.Images[key] = bitmap;
					page.Document = HtmlParser.Parse($"<html><head><title>{Escape(key)} ({bitmap.Width}x{bitmap.Height})</title><style>body {{ margin: 0; background: #202124; text-align: center }} img {{ margin: 24px }}</style></head><body><img src=\"{Escape(key)}\"></body></html>");
				}
				else
				{
					page.Error = decodeError;
					page.Document = ErrorDocument("Can't show this image", $"{Escape(key)}: {Escape(decodeError)}");
				}
				page.CollectStylesheets(timeoutMs);
				page.FetchMs = (long)(Kernel.TickCount - start);
				return page;
			}
			if (type.Contains("text/plain", StringComparison.OrdinalIgnoreCase))
				page.Document = HtmlParser.Parse("<html><body><pre></pre></body></html>");
			else
				page.Document = HtmlParser.Parse(response.BodyText());
			if (type.Contains("text/plain", StringComparison.OrdinalIgnoreCase))
				page.Document.Body.FirstElement("pre").AppendChild(new TextNode(response.BodyText()));
			page.CollectStylesheets(timeoutMs);
			page.RunScripts(timeoutMs);
			page.CollectStylesheets(timeoutMs);
			page.CollectImages(timeoutMs);
			page.FetchMs = (long)(Kernel.TickCount - start);
			page.Scripts?.FireLoad();
			return page;
		}

		// Runs the page's scripts, with the network for their fetches and a layout on demand for their measurements
		private void RunScripts(ulong timeoutMs)
		{
			if (!ScriptsEnabled || Document == null)
				return;
			Scripts = new ScriptHost(Document, Url);
			Scripts.Fetch = (method, url, body, contentType) => ToResource(Http.Request(method, url, body, contentType, timeoutMs));
			Scripts.BoxFor = BoxFor;
			Scripts.EnsureLayout = () => Layout(DefaultViewportWidth, DefaultViewportHeight, DefaultFonts ?? new FixedFontProvider());
			Scripts.ViewportWidth = DefaultViewportWidth;
			Scripts.ViewportHeight = DefaultViewportHeight;
			Scripts.Console = (level, text) => Console.WriteLine("script " + level + ": " + text);
			Scripts.RunScripts();
		}

		private static ResourceResponse ToResource(HttpResponse response) => new ResourceResponse
		{
			Status = response.Status,
			Reason = response.Reason,
			Url = response.Url,
			Headers = response.Headers,
			Body = response.Body,
			Error = response.Error,
		};

		// A page from markup already in hand, for errors and tests
		public static Page FromHtml(string html, Uri url = null)
		{
			Page page = new Page { Url = url, Document = HtmlParser.Parse(html) };
			page.RefreshStylesheets();
			return page;
		}

		// Fetches the linked stylesheets not fetched yet, then rebuilds the list layout reads
		private void CollectStylesheets(ulong timeoutMs)
		{
			foreach (Element element in new List<Element>(Document.DescendantElements()))
			{
				if (element.TagName != "link" || _linkSheets.ContainsKey(element) || StylesheetUrls.Count >= 12)
					continue;
				string rel = element.GetAttribute("rel") ?? "";
				if (!rel.Contains("stylesheet", StringComparison.OrdinalIgnoreCase) || rel.Contains("alternate", StringComparison.OrdinalIgnoreCase))
					continue;
				if (!MediaAttributeApplies(element))
					continue;
				string href = element.GetAttribute("href");
				if (string.IsNullOrEmpty(href) || Url == null || !Uri.TryCreate(Url, href, out Uri target))
					continue;
				if (target.Scheme != "http" && target.Scheme != "https")
					continue;
				StylesheetUrls.Add(target.ToString());
				HttpResponse response = Http.Get(target, timeoutMs);
				_linkSheets[element] = response.Ok ? response.BodyText() : "";
			}
			RefreshStylesheets();
		}

		// The stylesheets in document order: style elements as they stand now, link elements as fetched
		private void RefreshStylesheets()
		{
			StylesheetText.Clear();
			foreach (Element element in Document.DescendantElements())
			{
				if (element.TagName == "style")
				{
					if (MediaAttributeApplies(element))
						StylesheetText.Add(element.TextContent);
				}
				else if (_linkSheets.TryGetValue(element, out string css))
					StylesheetText.Add(css);
			}
		}

		// Fetches and decodes the pictures the img elements point at, each URL once, up to the limits, on a few threads at once
		private void CollectImages(ulong timeoutMs)
		{
			List<string> keys = new List<string>();
			foreach (Element element in Document.DescendantElements())
			{
				if (element.TagName != "img")
					continue;
				ImageCount++;
				if (keys.Count >= MaxImages)
					continue;
				string key = ImageKey(element);
				if (key == null || Images.ContainsKey(key))
					continue;
				Images[key] = null;
				keys.Add(key);
			}
			if (keys.Count == 0)
				return;
			int next = 0;
			void Worker()
			{
				while (true)
				{
					string key;
					lock (keys)
					{
						if (next >= keys.Count)
							return;
						key = keys[next++];
					}
					HttpResponse response = Http.Get(key, timeoutMs);
					if (!response.Ok || response.Body.Length == 0 || response.Body.Length > MaxImageBytes)
						continue;
					if (ImageDecoder.TryDecode(response.Body, out Bitmap bitmap, out _))
					{
						lock (Images)
							Images[key] = bitmap;
					}
				}
			}
			int threads = Math.Min(ImageThreads, keys.Count);
			System.Threading.Thread[] workers = new System.Threading.Thread[threads - 1];
			for (int i = 0; i < workers.Length; i++)
			{
				workers[i] = new System.Threading.Thread(Worker) { IsBackground = true };
				workers[i].Start();
			}
			Worker();
			foreach (System.Threading.Thread worker in workers)
				worker.Join();
		}

		// The absolute http URL of an img's src, or null when it is missing, relative to nothing, or another scheme
		private string ImageKey(Element element)
		{
			string src = element.GetAttribute("src");
			if (string.IsNullOrWhiteSpace(src))
				return null;
			Uri target = Resolve(src.Trim());
			if (target == null || (target.Scheme != "http" && target.Scheme != "https"))
				return null;
			return target.ToString();
		}

		// The picture for an img element, if it was fetched and decoded
		public Bitmap ImageFor(Element element)
		{
			string key = ImageKey(element);
			return key != null && Images.TryGetValue(key, out Bitmap bitmap) ? bitmap : null;
		}

		private static bool MediaAttributeApplies(Element element)
		{
			string media = element.GetAttribute("media");
			if (string.IsNullOrWhiteSpace(media))
				return true;
			return MediaQuery.Matches(CssTokenizer.Tokenize(media), new MediaEnvironment(CssValue.Viewport.Width, CssValue.Viewport.Height));
		}

		// Styles, lays out and paints the document for a viewport; call again after a resize
		public void Layout(int width, int height, IFontProvider fonts)
		{
			ulong start = Kernel.TickCount;
			if (Scripts != null && Scripts.Dirty)
				RefreshStylesheets();
			CssValue.Viewport.Width = width;
			CssValue.Viewport.Height = height;
			MediaEnvironment media = new MediaEnvironment(width, height);
			StyleResolver resolver = new StyleResolver(media);
			foreach (string css in StylesheetText)
				resolver.AddStylesheet(CssParser.Parse(css, media));
			Root = new BoxBuilder(resolver, ImageFor).Build(Document);
			new LayoutEngine(fonts, width, height).Layout(Root);
			Display = Painter.Paint(Root, width, height);
			LayoutMs = (long)(Kernel.TickCount - start);
			if (Scripts != null)
				Scripts.Dirty = false;
		}

		// The form control under a document point (text field, button, checkbox, radio or select), if any
		public Element ControlAt(float x, float y)
		{
			if (Root == null)
				return null;
			LayoutBox hit = Painter.HitTest(Root, x, y);
			for (Node node = hit?.Node; node != null; node = node.Parent)
			{
				if (node is Element element && (element.TagName == "input" || element.TagName == "textarea" || element.TagName == "button" || element.TagName == "select"))
					return element;
				if (node is Element label && label.TagName == "label")
				{
					string target = label.GetAttribute("for");
					if (target != null)
					{
						foreach (Element candidate in Document.DescendantElements())
						{
							if (candidate.Id == target)
								return candidate;
						}
					}
					foreach (Element inner in label.DescendantElements())
					{
						if (inner.TagName == "input" || inner.TagName == "select" || inner.TagName == "textarea")
							return inner;
					}
				}
			}
			return null;
		}

		// The box laid out for an element, for drawing focus around it
		public LayoutBox BoxFor(Element element) => Root == null ? null : Find(Root, element);

		private static LayoutBox Find(LayoutBox box, Element element)
		{
			if (box.Node == element && box.Kind != BoxKind.Text)
				return box;
			foreach (LayoutBox child in box.Children)
			{
				LayoutBox found = Find(child, element);
				if (found != null)
					return found;
			}
			if (box.Positioned != null)
			{
				foreach (LayoutBox child in box.Positioned)
				{
					LayoutBox found = Find(child, element);
					if (found != null)
						return found;
				}
			}
			return null;
		}

		// The link under a document point, if any
		public Element LinkAt(float x, float y)
		{
			if (Root == null)
				return null;
			LayoutBox hit = Painter.HitTest(Root, x, y);
			return hit == null ? null : Painter.LinkFor(hit.Node);
		}

		public Uri Resolve(string href) => Url != null && Uri.TryCreate(Url, href, out Uri target) ? target : (Uri.TryCreate(href, UriKind.Absolute, out Uri absolute) ? absolute : null);

		private static Document ErrorDocument(string heading, string detail) =>
			HtmlParser.Parse($"<html><head><style>body {{ font-family: sans-serif; margin: 48px; color: #333 }} h1 {{ font-size: 24px }} p {{ max-width: 40em; line-height: 1.5 }}</style></head><body><h1>{heading}</h1><p>{detail}</p></body></html>");

		private static string Escape(string text) => text.Replace("&", "&amp;").Replace("<", "&lt;").Replace(">", "&gt;");
	}
}
