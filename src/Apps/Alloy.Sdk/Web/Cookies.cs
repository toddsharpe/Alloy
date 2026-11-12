using System;
using System.Collections.Generic;
using System.Globalization;
using System.Text;
using Alloy.Sdk.Script;

namespace Alloy.Sdk.Web
{
	// One cookie as stored: RFC 6265's attributes that matter for sending it back
	public sealed class Cookie
	{
		public string Name;
		public string Value;
		public string Domain;
		public bool HostOnly;
		public string Path;
		// Milliseconds since the epoch, or NaN for a session cookie
		public double Expires = double.NaN;
		public bool Secure;
		public bool HttpOnly;

		public override string ToString() => Name + "=" + Value;
	}

	// The process's cookie jar: what responses set, sent back on later requests to matching hosts and paths
	public static class CookieJar
	{
		private static readonly List<Cookie> s_cookies = new List<Cookie>();
		private static readonly object s_lock = new object();
		public const int MaxCookies = 3000;
		public const int MaxPerHost = 180;
		public static ClockHandler Now = () => Interpreter.DefaultNow();

		public static int Count
		{
			get
			{
				lock (s_lock)
					return s_cookies.Count;
			}
		}

		public static void Clear()
		{
			lock (s_lock)
				s_cookies.Clear();
		}

		// Stores a Set-Cookie header received from url; false when it was malformed or not allowed for that host
		public static bool Store(Uri url, string setCookie, bool fromScript = false)
		{
			Cookie cookie = Parse(url, setCookie);
			if (cookie == null || fromScript && cookie.HttpOnly)
				return false;
			lock (s_lock)
			{
				for (int i = s_cookies.Count - 1; i >= 0; i--)
				{
					Cookie existing = s_cookies[i];
					if (existing.Name == cookie.Name && existing.Domain == cookie.Domain && existing.Path == cookie.Path)
					{
						if (fromScript && existing.HttpOnly)
							return false;
						s_cookies.RemoveAt(i);
					}
				}
				if (!double.IsNaN(cookie.Expires) && cookie.Expires <= Now())
					return true;
				s_cookies.Add(cookie);
				Trim(cookie.Domain);
			}
			return true;
		}

		// The Cookie header for a request to url, or null when nothing applies
		public static string HeaderFor(Uri url, bool forScript = false)
		{
			List<Cookie> matching = Matching(url, forScript);
			if (matching.Count == 0)
				return null;
			StringBuilder header = new StringBuilder();
			foreach (Cookie cookie in matching)
				header.Append(header.Length > 0 ? "; " : "").Append(cookie.Name).Append('=').Append(cookie.Value);
			return header.ToString();
		}

		// Cookies for a URL, longest path first as the RFC asks, then in the order they were set
		public static List<Cookie> Matching(Uri url, bool forScript = false)
		{
			List<Cookie> matching = new List<Cookie>();
			if (url == null)
				return matching;
			double now = Now();
			string host = url.Host.ToLowerInvariant();
			string path = url.AbsolutePath.Length == 0 ? "/" : url.AbsolutePath;
			bool secure = url.Scheme == "https";
			lock (s_lock)
			{
				for (int i = s_cookies.Count - 1; i >= 0; i--)
				{
					Cookie cookie = s_cookies[i];
					if (!double.IsNaN(cookie.Expires) && cookie.Expires <= now)
					{
						s_cookies.RemoveAt(i);
						continue;
					}
				}
				foreach (Cookie cookie in s_cookies)
				{
					if (cookie.Secure && !secure || forScript && cookie.HttpOnly)
						continue;
					if (!DomainMatches(host, cookie) || !PathMatches(path, cookie.Path))
						continue;
					matching.Add(cookie);
				}
			}
			matching.Sort((a, b) => b.Path.Length.CompareTo(a.Path.Length));
			return matching;
		}

		private static bool DomainMatches(string host, Cookie cookie)
		{
			if (cookie.HostOnly)
				return host == cookie.Domain;
			return host == cookie.Domain || host.EndsWith("." + cookie.Domain, StringComparison.Ordinal);
		}

		private static bool PathMatches(string requestPath, string cookiePath)
		{
			if (requestPath == cookiePath)
				return true;
			if (!requestPath.StartsWith(cookiePath, StringComparison.Ordinal))
				return false;
			return cookiePath.EndsWith("/", StringComparison.Ordinal) || requestPath[cookiePath.Length] == '/';
		}

		// Set-Cookie syntax: name=value, then attributes separated by semicolons
		public static Cookie Parse(Uri url, string text)
		{
			if (url == null || string.IsNullOrWhiteSpace(text))
				return null;
			string[] parts = text.Split(';');
			int equals = parts[0].IndexOf('=');
			if (equals <= 0)
				return null;
			string name = parts[0].Substring(0, equals).Trim();
			string value = parts[0].Substring(equals + 1).Trim();
			if (name.Length == 0 || name.IndexOfAny(new[] { ' ', '\t', '\r', '\n', ',' }) >= 0)
				return null;
			if (value.Length >= 2 && value[0] == '"' && value[value.Length - 1] == '"')
				value = value.Substring(1, value.Length - 2);
			string host = url.Host.ToLowerInvariant();
			Cookie cookie = new Cookie { Name = name, Value = value, Domain = host, HostOnly = true, Path = DefaultPath(url) };
			double maxAge = double.NaN;
			double expires = double.NaN;
			for (int i = 1; i < parts.Length; i++)
			{
				string attribute = parts[i].Trim();
				int split = attribute.IndexOf('=');
				string key = (split < 0 ? attribute : attribute.Substring(0, split)).Trim().ToLowerInvariant();
				string argument = split < 0 ? "" : attribute.Substring(split + 1).Trim();
				switch (key)
				{
					case "expires":
						expires = Interpreter.ParseDate(argument);
						break;
					case "max-age":
						if (double.TryParse(argument, NumberStyles.Float, CultureInfo.InvariantCulture, out double seconds))
							maxAge = seconds;
						break;
					case "domain":
					{
						string domain = argument.TrimStart('.').ToLowerInvariant();
						if (domain.Length == 0)
							break;
						if (host != domain && !host.EndsWith("." + domain, StringComparison.Ordinal))
							return null;
						if (IsPublicSuffix(domain))
							break;
						cookie.Domain = domain;
						cookie.HostOnly = false;
						break;
					}
					case "path":
						if (argument.StartsWith("/", StringComparison.Ordinal))
							cookie.Path = argument;
						break;
					case "secure":
						cookie.Secure = true;
						break;
					case "httponly":
						cookie.HttpOnly = true;
						break;
				}
			}
			if (!double.IsNaN(maxAge))
				cookie.Expires = Now() + maxAge * 1000;
			else if (!double.IsNaN(expires))
				cookie.Expires = expires;
			if (cookie.Secure && url.Scheme != "https")
				return null;
			return cookie;
		}

		// A cookie cannot cover a whole top-level domain or the common two-level ones
		private static bool IsPublicSuffix(string domain)
		{
			int dots = 0;
			foreach (char c in domain)
				dots += c == '.' ? 1 : 0;
			if (dots == 0)
				return true;
			return dots == 1 && (domain.EndsWith(".co.uk", StringComparison.Ordinal) || domain.EndsWith(".com.au", StringComparison.Ordinal) || domain.EndsWith(".co.jp", StringComparison.Ordinal) || domain.EndsWith(".github.io", StringComparison.Ordinal));
		}

		// The request path up to its last slash, as the RFC's default-path algorithm gives it
		private static string DefaultPath(Uri url)
		{
			string path = url.AbsolutePath;
			if (path.Length == 0 || path[0] != '/')
				return "/";
			int slash = path.LastIndexOf('/');
			return slash <= 0 ? "/" : path.Substring(0, slash);
		}

		// Keeps the jar bounded: the oldest cookies of a busy domain, then the oldest overall, go first
		private static void Trim(string domain)
		{
			int perHost = 0;
			for (int i = s_cookies.Count - 1; i >= 0; i--)
			{
				if (s_cookies[i].Domain == domain && ++perHost > MaxPerHost)
					s_cookies.RemoveAt(i);
			}
			while (s_cookies.Count > MaxCookies)
				s_cookies.RemoveAt(0);
		}
	}
}
