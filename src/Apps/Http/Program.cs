using System;
using Alloy.Sdk;
using Alloy.Sdk.Web;

namespace Alloy.Apps.Http
{
	// A fetch from the terminal: http <url> [file] prints the status, headers and the start of a text body, or saves the body to a file
	internal static class Program
	{
		private const int MaxLines = 30;

		private static int Main(string[] args)
		{
			if (args.Length < 1)
			{
				Console.WriteLine("usage: http <url> [file]");
				return 1;
			}
			string url = args[0].Contains("://") ? args[0] : "http://" + args[0];
			HttpResponse response = Sdk.Web.Http.Get(url);
			if (response.Error != null && response.Status == 0)
			{
				Console.WriteLine($"http: {response.Error}");
				return 1;
			}
			Console.WriteLine($"HTTP {response.Status} {response.Reason}  ({response.Body.Length} bytes in {response.ElapsedMs} ms from {response.Url})");
			foreach (System.Collections.Generic.KeyValuePair<string, string> header in response.Headers)
				Console.WriteLine($"  {header.Key}: {header.Value}");
			if (response.Error != null)
				Console.WriteLine($"http: {response.Error}");

			if (args.Length > 1)
			{
				bool saved = File.WriteAllBytes(args[1], response.Body);
				Console.WriteLine(saved ? $"http: saved {response.Body.Length} bytes to {args[1]}" : $"http: can't write {args[1]}");
				return saved ? 0 : 1;
			}

			string location = response.Header("Location");
			if (response.Status >= 300 && response.Status < 400 && location != null && location.StartsWith("https://", StringComparison.OrdinalIgnoreCase))
			{
				Console.WriteLine("http: the page redirects to https, which isn't supported yet");
				return 1;
			}
			if (response.Body.Length == 0)
				return response.Ok ? 0 : 1;
			string type = response.ContentType;
			bool text = type.StartsWith("text/", StringComparison.OrdinalIgnoreCase) || type.Contains("json", StringComparison.OrdinalIgnoreCase) || type.Contains("xml", StringComparison.OrdinalIgnoreCase);
			if (!text)
			{
				Console.WriteLine($"http: a {type} body, use http <url> <file> to save it");
				return response.Ok ? 0 : 1;
			}
			Console.WriteLine();
			string[] lines = response.BodyText().Split('\n');
			for (int i = 0; i < lines.Length && i < MaxLines; i++)
				Console.WriteLine(lines[i].TrimEnd('\r'));
			if (lines.Length > MaxLines)
				Console.WriteLine($"http: ... {lines.Length - MaxLines} more lines");
			return response.Ok ? 0 : 1;
		}
	}
}
