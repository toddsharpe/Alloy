using System;

namespace Alloy.Sdk.Web
{
	// What people type into an address bar, turned into a URL
	public static class Address
	{
		// A full URL for typed text: a bare host gets http://, schemes and hosts are lowercased, junk is null
		public static string Normalize(string text)
		{
			if (text == null)
				return null;
			text = text.Trim();
			if (text.Length == 0)
				return null;
			if (text.StartsWith("//", StringComparison.Ordinal))
				text = "http:" + text;
			else if (text.StartsWith("https//", StringComparison.OrdinalIgnoreCase) || text.StartsWith("http//", StringComparison.OrdinalIgnoreCase))
				text = text.Insert(text.IndexOf('/'), ":");
			else if (!text.Contains("://", StringComparison.Ordinal))
				text = "http://" + text;
			if (!Uri.TryCreate(text, UriKind.Absolute, out Uri uri))
				return null;
			if (uri.Scheme != "http" && uri.Scheme != "https")
				return null;
			if (string.IsNullOrEmpty(uri.Host))
				return null;
			return uri.ToString();
		}

		public static bool IsSecure(string url) => url != null && url.StartsWith("https://", StringComparison.OrdinalIgnoreCase);

		// A URL as the address bar shows it: without the trailing slash a bare host picks up
		public static string Display(Uri url)
		{
			if (url == null)
				return "";
			string text = url.ToString();
			if (url.AbsolutePath == "/" && string.IsNullOrEmpty(url.Query) && string.IsNullOrEmpty(url.Fragment) && text.EndsWith('/'))
				text = text.Substring(0, text.Length - 1);
			return text;
		}
	}
}
