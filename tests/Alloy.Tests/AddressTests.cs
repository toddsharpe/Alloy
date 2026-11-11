using Alloy.Sdk.Web;

namespace Alloy.Tests;

public class AddressTests
{
	[Theory]
	[InlineData("example.com", "http://example.com/")]
	[InlineData("  info.cern.ch ", "http://info.cern.ch/")]
	[InlineData("HTTP://Example.com/A/B?q=1", "http://example.com/A/B?q=1")]
	[InlineData("localhost:8080/p", "http://localhost:8080/p")]
	[InlineData("//host/x", "http://host/x")]
	[InlineData("https://secure.example", "https://secure.example/")]
	[InlineData("192.168.1.1", "http://192.168.1.1/")]
	public void NormalizesTypedAddresses(string typed, string expected)
	{
		Assert.Equal(expected, Address.Normalize(typed));
	}

	[Theory]
	[InlineData("")]
	[InlineData("   ")]
	[InlineData("ftp://files.example")]
	[InlineData("not a url at all")]
	[InlineData(null)]
	public void RejectsWhatIsNotAnAddress(string typed)
	{
		Assert.Null(Address.Normalize(typed));
	}

	[Fact]
	public void DisplaysWithoutTheBareTrailingSlash()
	{
		Assert.Equal("http://example.com", Address.Display(new System.Uri("http://example.com/")));
		Assert.Equal("http://example.com/a/", Address.Display(new System.Uri("http://example.com/a/")));
		Assert.Equal("http://example.com/?q=1", Address.Display(new System.Uri("http://example.com/?q=1")));
		Assert.True(Address.IsSecure("https://x.y"));
		Assert.False(Address.IsSecure("http://x.y"));
	}
}
