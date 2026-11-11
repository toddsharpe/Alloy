using Alloy.Kernel.Io;

namespace Alloy.Tests;

public class PathsTests
{
	private static string Resolve(string directory, string path)
	{
		Span<char> result = stackalloc char[256];
		int length = Paths.Resolve(directory, path, result);
		return length < 0 ? null! : new string(result.Slice(0, length));
	}

	[Theory]
	[InlineData("/", "docs", "/docs")]
	[InlineData("/", "/docs/notes.txt", "/docs/notes.txt")]
	[InlineData("/docs", "notes.txt", "/docs/notes.txt")]
	[InlineData("/docs", "/bin/ps.exe", "/bin/ps.exe")]
	[InlineData("/docs/sub", "..", "/docs")]
	[InlineData("/docs/sub", "../..", "/")]
	[InlineData("/docs", "../../..", "/")]
	[InlineData("/docs", ".", "/docs")]
	[InlineData("/docs", "./sub/./x", "/docs/sub/x")]
	[InlineData("/docs", "sub\\x", "/docs/sub/x")]
	[InlineData("/docs", "", "/docs")]
	[InlineData("/", "", "/")]
	[InlineData("/docs/", "a//b/", "/docs/a/b")]
	[InlineData("/", "..", "/")]
	public void MakesPathsAbsoluteAndTidy(string directory, string path, string expected)
	{
		Assert.Equal(expected, Resolve(directory, path));
	}

	[Fact]
	public void RefusesAPathTooLongOnceResolved()
	{
		string name = new string('x', 200);
		Assert.NotNull(Resolve("/", name));
		Assert.Null(Resolve("/" + name, name));
	}
}
