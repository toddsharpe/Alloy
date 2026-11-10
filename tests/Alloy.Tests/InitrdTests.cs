using System.Text;
using Alloy.Abi;
using Alloy.Tools;

namespace Alloy.Tests;

public class InitrdTests
{
	[Fact]
	public void RoundTripsFilesOnPageBoundaries()
	{
		byte[] hello = Encoding.ASCII.GetBytes("hello from the initrd");
		byte[] big = new byte[10000];
		new Random(1).NextBytes(big);
		byte[] image = InitrdWriter.Build([("etc/motd", hello), ("bin/init.exe", big), ("empty", [])]);

		Assert.True(InitrdReader.TryOpen(image, out InitrdReader reader));
		Assert.Equal(3, reader.Count);
		for (int i = 0; i < reader.Count; i++)
			Assert.Equal(0UL, reader.GetEntry(i).Offset % InitrdEntry.DataAlignment);

		Assert.True(reader.TryFind("etc/motd", out ReadOnlySpan<byte> motd));
		Assert.True(motd.SequenceEqual(hello));
		Assert.True(reader.TryFind("bin/init.exe", out ReadOnlySpan<byte> init));
		Assert.True(init.SequenceEqual(big));
		Assert.True(reader.TryFind("empty", out ReadOnlySpan<byte> empty));
		Assert.Equal(0, empty.Length);
		Assert.False(reader.TryFind("etc/mot", out _));
		Assert.Equal("bin/init.exe", Encoding.ASCII.GetString(reader.GetName(1)));
	}

	[Fact]
	public void RejectsCorruptImages()
	{
		byte[] image = InitrdWriter.Build([("a", [1, 2, 3])]);

		Assert.False(InitrdReader.TryOpen(image.AsSpan(0, 16), out _));

		byte[] badMagic = (byte[])image.Clone();
		badMagic[0] ^= 0xFF;
		Assert.False(InitrdReader.TryOpen(badMagic, out _));

		// An entry whose data runs past the end of the image must not open
		byte[] badSize = (byte[])image.Clone();
		BitConverter.TryWriteBytes(badSize.AsSpan(32 + 8), (ulong)image.Length);
		Assert.False(InitrdReader.TryOpen(badSize, out _));
	}

	[Fact]
	public void RejectsBadNames()
	{
		Assert.Throws<ArgumentException>(() => InitrdWriter.Build([("/abs", [])]));
		Assert.Throws<ArgumentException>(() => InitrdWriter.Build([("dup", []), ("dup", [])]));
		Assert.Throws<ArgumentException>(() => InitrdWriter.Build([(new string('x', InitrdEntry.MaxNameLength + 1), [])]));
	}
}
