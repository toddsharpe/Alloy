using Alloy.Text;

namespace Alloy.Tests;

public class NumberTests
{
	[Theory]
	[InlineData(0L, "0")]
	[InlineData(7L, "7")]
	[InlineData(-42L, "-42")]
	[InlineData(1234567890L, "1234567890")]
	[InlineData(long.MaxValue, "9223372036854775807")]
	[InlineData(long.MinValue, "-9223372036854775808")]
	public void FormatsSignedLikeTheBcl(long value, string expected)
	{
		Assert.Equal(expected, Number.ToString(value));
		Assert.Equal(value.ToString(System.Globalization.CultureInfo.InvariantCulture), Number.ToString(value));
	}

	[Fact]
	public void FormatsUnsignedMax()
	{
		Assert.Equal("18446744073709551615", Number.ToString(ulong.MaxValue));
	}

	[Fact]
	public void ReportsShortBuffer()
	{
		Span<char> buffer = stackalloc char[3];
		Assert.False(Number.TryFormatInt64(-1000, buffer, out int written));
		Assert.Equal(0, written);
		Assert.True(Number.TryFormatInt64(-99, buffer, out written));
		Assert.Equal("-99", new string(buffer[..written]));
	}

	[Theory]
	[InlineData(0x0UL, 1, true, "0")]
	[InlineData(0xDEADBEEFUL, 1, true, "DEADBEEF")]
	[InlineData(0xDEADBEEFUL, 16, false, "00000000deadbeef")]
	[InlineData(0xFFFF800001000000UL, 1, true, "FFFF800001000000")]
	public void FormatsHex(ulong value, int minDigits, bool upper, string expected)
	{
		Span<char> buffer = stackalloc char[32];
		Assert.True(Number.TryFormatHex(value, buffer, out int written, minDigits, upper));
		Assert.Equal(expected, new string(buffer[..written]));
	}
}

public class SpanWriterTests
{
	[Fact]
	public void BuildsMixedLines()
	{
		SpanWriter writer = new(stackalloc char[64]);
		writer.Append("KernelMain @ 0x");
		writer.AppendHex(0xFFFF800001000000, 16);
		writer.Append(' ');
		writer.Append(-5);
		Assert.Equal("KernelMain @ 0xFFFF800001000000 -5", writer.ToString());
		Assert.False(writer.Truncated);
	}

	[Fact]
	public void TruncatesInsteadOfOverflowing()
	{
		SpanWriter writer = new(stackalloc char[8]);
		writer.Append("0123456789");
		Assert.True(writer.Truncated);
		Assert.Equal("01234567", writer.ToString());
		writer.Append(12345);
		Assert.Equal(8, writer.Length);
	}
}
