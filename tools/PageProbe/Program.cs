using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Net.Http;
using System.Text;
using Alloy.Sdk.Script;
using Alloy.Sdk.Web;
using Alloy.Sdk.Web.Css;
using Alloy.Sdk.Web.Dom;
using Alloy.Sdk.Web.Html;
using Node = Alloy.Sdk.Web.Html.Node;
using Alloy.Sdk.Web.Layout;

namespace Alloy.Tools.PageProbe
{
	// Loads a page on the host with the SDK's parser, cascade, scripts and layout: pageprobe <url> [-v] [-click <selector>] [-text]
	internal static class Program
	{
		private static readonly HttpClient s_http = new HttpClient(new HttpClientHandler { UseCookies = false, AllowAutoRedirect = true, AutomaticDecompression = System.Net.DecompressionMethods.All }) { Timeout = TimeSpan.FromSeconds(20) };
		private static bool s_verbose;

		private static int Main(string[] args)
		{
			if (args.Length < 1)
			{
				Console.WriteLine("usage: pageprobe <url> [-v] [-click <selector>]... [-text] [-width <px>]");
				return 1;
			}
			string url = args[0].Contains("://") ? args[0] : "https://" + args[0];
			List<string> clicks = new List<string>();
			List<string> styles = new List<string>();
			bool text = false;
			int width = 1200;
			int repeat = 1;
			for (int i = 1; i < args.Length; i++)
			{
				if (args[i] == "-repeat" && i + 1 < args.Length)
				{
					repeat = int.Parse(args[++i]);
					continue;
				}
				if (args[i] == "-v")
					s_verbose = true;
				else if (args[i] == "-click" && i + 1 < args.Length)
					clicks.Add(args[++i]);
				else if (args[i] == "-style" && i + 1 < args.Length)
					styles.Add(args[++i]);
				else if (args[i] == "-text")
					text = true;
				else if (args[i] == "-width" && i + 1 < args.Length)
					width = int.Parse(args[++i]);
			}
			s_http.DefaultRequestHeaders.UserAgent.ParseAdd("Mozilla/5.0 (Alloy; x64) Alloy/0.1");
			s_http.DefaultRequestHeaders.Accept.ParseAdd("text/html,application/xhtml+xml,*/*;q=0.8");

			Stopwatch total = Stopwatch.StartNew();
			ResourceResponse response = Fetch("GET", new Uri(url), null, null, null);
			if (response == null || response.Status == 0)
			{
				Console.WriteLine($"fetch failed: {response?.Error}");
				return 1;
			}
			Uri finalUrl = response.Url ?? new Uri(url);
			string html = ScriptHost.Decode(response.Body);
			Stopwatch parse = Stopwatch.StartNew();
			Document document = HtmlParser.Parse(html);
			parse.Stop();
			Console.WriteLine($"{finalUrl}: {response.Status}, {html.Length} chars, parsed in {parse.ElapsedMilliseconds} ms, {Count(document)} elements");

			List<string> sheets = new List<string>();
			int linked = 0;
			CollectStylesheets(document, finalUrl, sheets, ref linked);
			Console.WriteLine($"stylesheets: {sheets.Count} ({linked} linked)");

			LayoutBox root = null;
			long layouts = 0;
			long layoutMs = 0;
			MediaEnvironment media = new MediaEnvironment(width, 800);
			void Layout()
			{
				Stopwatch sw = Stopwatch.StartNew();
				StyleResolver resolver = new StyleResolver(media);
				foreach (string css in sheets)
					resolver.AddStylesheet(CssParser.Parse(css, media));
				long parsedCss = sw.ElapsedMilliseconds;
				if (s_verbose)
				{
					Stopwatch tokens = Stopwatch.StartNew();
					int tokenCount = 0;
					long cssChars = 0;
					foreach (string css in sheets)
					{
						tokenCount += CssTokenizer.Tokenize(css).Count;
						cssChars += css.Length;
					}
					Console.WriteLine($"  css: {cssChars} chars, {tokenCount} tokens, tokenized alone in {tokens.ElapsedMilliseconds} ms");
					Stopwatch resolve = Stopwatch.StartNew();
					int resolved = 0;
					foreach (Element element in document.DescendantElementsWithShadow())
					{
						resolver.Resolve(element);
						resolved++;
					}
					Console.WriteLine($"  cascade: {resolved} elements resolved in {resolve.ElapsedMilliseconds} ms");
					Console.Write(resolver.DescribeUniversal());
				}
				root = new BoxBuilder(resolver).Build(document);
				long built = sw.ElapsedMilliseconds;
				new LayoutEngine(new FixedFontProvider(), width, 800).Layout(root);
				long laid = sw.ElapsedMilliseconds;
				Painter.Paint(root, width, 800);
				layouts++;
				layoutMs += sw.ElapsedMilliseconds;
				if (s_verbose || sw.ElapsedMilliseconds > 500)
					Console.WriteLine($"  layout pass: css parse {parsedCss} ms, cascade+boxes {built - parsedCss} ms, layout {laid - built} ms, paint {sw.ElapsedMilliseconds - laid} ms");
			}

			List<string> log = new List<string>();
			ScriptHost host = new ScriptHost(document, finalUrl) { Fetch = Fetch, ViewportWidth = width, ViewportHeight = 800 };
			host.Console = (level, line) =>
			{
				log.Add(level + ": " + line);
				if (s_verbose || level == "error")
					Console.WriteLine($"  [{level}] {Trim(line, 300)}");
			};
			host.Navigate = request => Console.WriteLine($"  navigate: {request}");
			host.Alert = message => Console.WriteLine($"  alert: {message}");
			host.EnsureLayout = () =>
			{
				Layout();
				host.Dirty = false;
			};
			host.BoxFor = element => Find(root, element);
			Stopwatch scripts = Stopwatch.StartNew();
			host.RunScripts();
			int before = sheets.Count;
			CollectStylesheets(document, finalUrl, sheets, ref linked);
			host.FireLoad();
			scripts.Stop();
			Console.WriteLine($"scripts: {host.ScriptsRun} run, {host.ScriptErrors} errors, {host.ScriptsSkipped} skipped, {scripts.ElapsedMilliseconds} ms, {sheets.Count - before} stylesheets added by scripts, document now {Count(document)} elements, title \"{document.Title}\"");

			int pumps = 0;
			Stopwatch idle = Stopwatch.StartNew();
			while (host.HasTimers && idle.ElapsedMilliseconds < 3000 && pumps < 200)
			{
				double wait = host.NextTimerDue - host.Interpreter.Clock();
				if (wait > 0)
					System.Threading.Thread.Sleep((int)Math.Min(wait, 100));
				host.Pump();
				pumps++;
			}
			if (pumps > 0)
				Console.WriteLine($"timers: pumped {pumps} times over {idle.ElapsedMilliseconds} ms, {host.ScriptErrors} errors so far");

			foreach (string selector in clicks)
			{
				Element target = host.QueryFirst(document, selector);
				if (target == null)
				{
					Console.WriteLine($"click {selector}: no match");
					continue;
				}
				host.Click(target);
				host.Pump();
				Console.WriteLine($"click {selector}: done, dirty={host.Dirty}, errors={host.ScriptErrors}");
			}

			Layout();
			for (int extra = 1; extra < repeat; extra++)
				Layout();
			Console.WriteLine($"layout: {layouts} runs, {layoutMs} ms total, page height {root.Height:0}, {CountBoxes(root)} boxes, total {total.ElapsedMilliseconds} ms");
			foreach (string selector in styles)
			{
				Element target = host.QueryFirst(document, selector);
				if (target == null)
				{
					Console.WriteLine($"style {selector}: no match");
					continue;
				}
				LayoutBox box = Find(root, target);
				Console.WriteLine($"style {selector}:");
				List<Element> ancestors = new List<Element>();
				for (Element up = target.ParentElement; up != null; up = up.ParentElement)
					ancestors.Insert(0, up);
				foreach (Element ancestor in ancestors)
					Console.WriteLine(" ^ " + Describe(ancestor, Find(root, ancestor)));
				Console.WriteLine("  " + Describe(target, box));
				if (box != null)
				{
					foreach (LayoutBox child in box.Children)
					{
						if (child.Element != null)
							Console.WriteLine("    " + Describe(child.Element, child));
					}
				}
			}
			if (text)
				Console.WriteLine(RenderText(root));
			return host.ScriptErrors > 0 ? 2 : 0;
		}

		// One line of the computed style and geometry that decide how an element lays out
		private static string Describe(Element element, LayoutBox box)
		{
			string name = element.TagName + (element.Id != null ? "#" + element.Id : "") + (element.GetAttribute("class") != null ? "." + Trim(element.GetAttribute("class").Replace(' ', '.'), 60) : "");
			if (box == null)
				return name + " (no box)";
			ComputedStyle style = box.Style;
			return $"{name} {box.Kind} at {box.X:0},{box.Y:0} {box.Width:0}x{box.Height:0} display={style.Display} position={style.Position} float={style.Float} width={style.Width} height={style.Height} font={style.FontSize:0.#}px line={style.LineHeight:0.#} flex={style.FlexDirection}{(style.FlexWrap ? " wrap" : "")} grow={style.FlexGrow} margin={box.MarginTop:0}/{box.MarginRight:0}/{box.MarginBottom:0}/{box.MarginLeft:0} padding={box.PaddingTop:0}/{box.PaddingRight:0}/{box.PaddingBottom:0}/{box.PaddingLeft:0} visible={style.Visible} overflow={style.Overflow}";
		}

		private static ResourceResponse Fetch(string method, Uri url, byte[] body, string contentType, IReadOnlyList<KeyValuePair<string, string>> headers)
		{
			ResourceResponse result = new ResourceResponse { Url = url };
			try
			{
				HttpRequestMessage request = new HttpRequestMessage(new HttpMethod(method), url);
				string cookies = CookieJar.HeaderFor(url);
				if (cookies != null)
					request.Headers.TryAddWithoutValidation("Cookie", cookies);
				if (body != null)
				{
					request.Content = new ByteArrayContent(body);
					if (contentType != null)
						request.Content.Headers.TryAddWithoutValidation("Content-Type", contentType);
				}
				if (headers != null)
				{
					foreach (KeyValuePair<string, string> header in headers)
					{
						if (!request.Headers.TryAddWithoutValidation(header.Key, header.Value) && request.Content != null)
							request.Content.Headers.TryAddWithoutValidation(header.Key, header.Value);
					}
				}
				HttpResponseMessage response = s_http.Send(request);
				result.Status = (int)response.StatusCode;
				result.Reason = response.ReasonPhrase ?? "";
				result.Url = response.RequestMessage?.RequestUri ?? url;
				foreach (KeyValuePair<string, IEnumerable<string>> header in response.Headers)
				{
					if (header.Key.Equals("Set-Cookie", StringComparison.OrdinalIgnoreCase))
					{
						foreach (string value in header.Value)
							CookieJar.Store(result.Url, value);
						continue;
					}
					result.Headers[header.Key] = string.Join(", ", header.Value);
				}
				foreach (KeyValuePair<string, IEnumerable<string>> header in response.Content.Headers)
					result.Headers[header.Key] = string.Join(", ", header.Value);
				using MemoryStream stream = new MemoryStream();
				response.Content.ReadAsStream().CopyTo(stream);
				result.Body = stream.ToArray();
				if (s_verbose)
					Console.WriteLine($"  {method} {url} -> {result.Status} ({result.Body.Length} bytes)");
			}
			catch (Exception e)
			{
				result.Error = e.Message;
				Console.WriteLine($"  {method} {url} failed: {e.Message}");
			}
			return result;
		}

		private static void CollectStylesheets(Document document, Uri url, List<string> sheets, ref int linked)
		{
			int index = 0;
			foreach (Element element in document.DescendantElementsWithShadow())
			{
				if (element.TagName == "style")
				{
					if (index >= sheets.Count)
						sheets.Add(element.TextContent);
					index++;
					continue;
				}
				if (element.TagName != "link")
					continue;
				string rel = element.GetAttribute("rel") ?? "";
				if (!rel.Contains("stylesheet", StringComparison.OrdinalIgnoreCase) || rel.Contains("alternate", StringComparison.OrdinalIgnoreCase))
					continue;
				string href = element.GetAttribute("href");
				if (string.IsNullOrEmpty(href) || !Uri.TryCreate(url, href, out Uri target))
					continue;
				if (index >= sheets.Count)
				{
					ResourceResponse response = Fetch("GET", target, null, null, null);
					sheets.Add(response.Ok ? ScriptHost.Decode(response.Body) : "");
					linked++;
				}
				index++;
			}
		}

		private static int Count(Node node)
		{
			int count = 0;
			foreach (Element _ in node.DescendantElements())
				count++;
			return count;
		}

		private static int CountBoxes(LayoutBox box)
		{
			int count = 1;
			foreach (LayoutBox child in box.Children)
				count += CountBoxes(child);
			return count;
		}

		private static LayoutBox Find(LayoutBox box, Element element)
		{
			if (box == null)
				return null;
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

		// The laid-out text, one line box per line, so a page's visible words can be checked without pixels
		private static string RenderText(LayoutBox root)
		{
			StringBuilder text = new StringBuilder();
			Walk(root, text);
			return text.ToString();
		}

		private static void Walk(LayoutBox box, StringBuilder text)
		{
			if (box.Lines != null)
			{
				foreach (LineBox line in box.Lines)
				{
					StringBuilder lineText = new StringBuilder();
					foreach (LineFragment fragment in line.Fragments)
					{
						if (fragment.Text != null)
							lineText.Append(fragment.Text);
					}
					if (lineText.Length > 0)
						text.Append($"{line.Y,6:0} ").Append(lineText.ToString().Trim()).Append('\n');
				}
			}
			foreach (LayoutBox child in box.Children)
				Walk(child, text);
			if (box.Positioned != null)
			{
				foreach (LayoutBox child in box.Positioned)
					Walk(child, text);
			}
		}

		private static string Trim(string text, int max) => text.Length > max ? text.Substring(0, max) + "..." : text;
	}
}
