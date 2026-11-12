using System;
using Alloy.Sdk.Web;

namespace Alloy.Tests;

// The cookie jar: what Set-Cookie stores and what later requests send back
public class CookieTests
{
	private static readonly Uri Page = new Uri("https://shop.example.com/cart/view?x=1");

	[Fact]
	public void SendsCookiesBackToMatchingHostsAndPaths()
	{
		CookieJar.Clear();
		Assert.True(CookieJar.Store(Page, "session=abc; Path=/; HttpOnly"));
		Assert.True(CookieJar.Store(Page, "cart=3"));
		Assert.True(CookieJar.Store(Page, "pref=dark; Domain=example.com; Path=/"));
		Assert.True(CookieJar.Store(Page, "tls=1; Secure"));
		Assert.Equal("cart=3; tls=1; session=abc; pref=dark", CookieJar.HeaderFor(new Uri("https://shop.example.com/cart/pay")));
		Assert.Equal("session=abc; pref=dark", CookieJar.HeaderFor(new Uri("https://shop.example.com/other")));
		Assert.Equal("session=abc; pref=dark", CookieJar.HeaderFor(new Uri("http://shop.example.com/other")));
		Assert.Equal("pref=dark", CookieJar.HeaderFor(new Uri("https://www.example.com/")));
		Assert.Null(CookieJar.HeaderFor(new Uri("https://example.org/")));
		Assert.Equal("cart=3; tls=1; pref=dark", CookieJar.HeaderFor(new Uri("https://shop.example.com/cart/x"), forScript: true));
	}

	[Fact]
	public void ExpiresAndDeletes()
	{
		CookieJar.Clear();
		double now = 1_700_000_000_000;
		CookieJar.Now = () => now;
		Assert.True(CookieJar.Store(Page, "short=1; Max-Age=60; Path=/"));
		Assert.True(CookieJar.Store(Page, "dated=2; Expires=Wed, 15 Nov 2023 00:00:00 GMT; Path=/"));
		Assert.True(CookieJar.Store(Page, "old=3; Expires=Mon, 01 Jan 2001 00:00:00 GMT; Path=/"));
		Assert.Equal("short=1; dated=2", CookieJar.HeaderFor(Page));
		now += 61_000;
		Assert.Equal("dated=2", CookieJar.HeaderFor(Page));
		Assert.True(CookieJar.Store(Page, "dated=; Max-Age=0; Path=/"));
		Assert.Null(CookieJar.HeaderFor(Page));
		CookieJar.Store(Page, "keep=1; Path=/");
		CookieJar.Store(Page, "keep=2; Path=/");
		Assert.Equal("keep=2", CookieJar.HeaderFor(Page));
		CookieJar.Now = () => Alloy.Sdk.Script.Interpreter.DefaultNow();
	}

	[Fact]
	public void RejectsWhatTheRfcForbids()
	{
		CookieJar.Clear();
		Assert.False(CookieJar.Store(Page, "bad"));
		Assert.False(CookieJar.Store(Page, "foreign=1; Domain=other.com"));
		Assert.True(CookieJar.Store(Page, "tld=1; Domain=com"));
		Assert.Null(CookieJar.HeaderFor(new Uri("https://x.com/")));
		Assert.Equal("tld=1", CookieJar.HeaderFor(Page));
		Assert.False(CookieJar.Store(new Uri("http://shop.example.com/"), "s=1; Secure"));
		Assert.True(CookieJar.Store(Page, "locked=1; HttpOnly; Path=/"));
		Assert.False(CookieJar.Store(Page, "locked=2; Path=/", fromScript: true));
		Assert.Equal("tld=1; locked=1", CookieJar.HeaderFor(Page));
		Assert.Equal("quoted", CookieJar.Parse(Page, "q=\"quoted\"").Value);
		Assert.Equal("/cart", CookieJar.Parse(Page, "p=1").Path);
	}
}
