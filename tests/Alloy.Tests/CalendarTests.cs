using Alloy.Abi;
using Alloy.Kernel.Ke;

namespace Alloy.Tests;

public class CalendarTests
{
	[Theory]
	[InlineData(1601, 1, 1, 0, 0, 0, 0)]
	[InlineData(1970, 1, 1, 0, 0, 0, 0)]
	[InlineData(2000, 2, 29, 23, 59, 59, 999)]
	[InlineData(2026, 11, 11, 13, 34, 40, 500)]
	[InlineData(2100, 3, 1, 12, 0, 0, 0)]
	public void MatchesDateTimeInBothDirections(int year, int month, int day, int hour, int minute, int second, int millisecond)
	{
		DateTime expected = new(year, month, day, hour, minute, second, millisecond, DateTimeKind.Utc);
		SystemTime time = new() { Year = (ushort)year, Month = (ushort)month, Day = (ushort)day, Hour = (ushort)hour, Minute = (ushort)minute, Second = (ushort)second, Milliseconds = (ushort)millisecond };

		ulong ticks = Calendar.ToFileTime(in time);
		Assert.Equal((ulong)expected.ToFileTimeUtc(), ticks);

		SystemTime back = Calendar.FromFileTime(ticks);
		Assert.Equal((year, month, day, hour, minute, second, millisecond), ((int)back.Year, (int)back.Month, (int)back.Day, (int)back.Hour, (int)back.Minute, (int)back.Second, (int)back.Milliseconds));
	}

	[Fact]
	public void CountsDaysAcrossLeapYearsAndCenturies()
	{
		Assert.Equal(0, Calendar.DaysFromCivil(1970, 1, 1));
		Assert.Equal(-134774, Calendar.DaysFromCivil(1601, 1, 1));
		Assert.Equal(11016, Calendar.DaysFromCivil(2000, 2, 29));
		Assert.Equal(11017, Calendar.DaysFromCivil(2000, 3, 1));

		Calendar.CivilFromDays(11017, out int year, out int month, out int day);
		Assert.Equal((2000, 3, 1), (year, month, day));
		Calendar.CivilFromDays(-134774, out year, out month, out day);
		Assert.Equal((1601, 1, 1), (year, month, day));
	}
}
