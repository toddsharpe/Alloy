using Alloy.Abi;

namespace Alloy.Kernel.Ke
{
	// Between a SystemTime and Windows file time, 100 ns units since 1601-01-01 (the host's time sync and the proleptic Gregorian calendar)
	internal static class Calendar
	{
		public const ulong TicksPerSecond = 10_000_000;
		public const ulong TicksPerDay = TicksPerSecond * 86400;

		// Days from 1601-01-01 to 1970-01-01, which the civil date algorithm counts from
		private const long DaysTo1970 = 134774;

		public static SystemTime FromFileTime(ulong ticks)
		{
			long days = (long)(ticks / TicksPerDay);
			ulong inDay = ticks % TicksPerDay;
			CivilFromDays(days - DaysTo1970, out int year, out int month, out int day);
			SystemTime time = default;
			time.Year = (ushort)year;
			time.Month = (ushort)month;
			time.Day = (ushort)day;
			time.Hour = (ushort)(inDay / (TicksPerSecond * 3600));
			time.Minute = (ushort)(inDay / (TicksPerSecond * 60) % 60);
			time.Second = (ushort)(inDay / TicksPerSecond % 60);
			time.Milliseconds = (ushort)(inDay / 10_000 % 1000);
			return time;
		}

		public static ulong ToFileTime(in SystemTime time)
		{
			long days = DaysFromCivil(time.Year, time.Month, time.Day) + DaysTo1970;
			ulong inDay = ((ulong)time.Hour * 3600 + (ulong)time.Minute * 60 + time.Second) * TicksPerSecond + (ulong)time.Milliseconds * 10_000;
			return (ulong)days * TicksPerDay + inDay;
		}

		// Howard Hinnant's days_from_civil: days since 1970-01-01 for a proleptic Gregorian date
		public static long DaysFromCivil(int year, int month, int day)
		{
			year -= month <= 2 ? 1 : 0;
			long era = (year >= 0 ? year : year - 399) / 400;
			long yearOfEra = year - era * 400;
			long dayOfYear = (153 * (month + (month > 2 ? -3 : 9)) + 2) / 5 + day - 1;
			long dayOfEra = yearOfEra * 365 + yearOfEra / 4 - yearOfEra / 100 + dayOfYear;
			return era * 146097 + dayOfEra - 719468;
		}

		// And civil_from_days, the other way
		public static void CivilFromDays(long days, out int year, out int month, out int day)
		{
			days += 719468;
			long era = (days >= 0 ? days : days - 146096) / 146097;
			long dayOfEra = days - era * 146097;
			long yearOfEra = (dayOfEra - dayOfEra / 1460 + dayOfEra / 36524 - dayOfEra / 146096) / 365;
			long dayOfYear = dayOfEra - (365 * yearOfEra + yearOfEra / 4 - yearOfEra / 100);
			long monthIndex = (5 * dayOfYear + 2) / 153;
			day = (int)(dayOfYear - (153 * monthIndex + 2) / 5 + 1);
			month = (int)(monthIndex < 10 ? monthIndex + 3 : monthIndex - 9);
			year = (int)(yearOfEra + era * 400 + (month <= 2 ? 1 : 0));
		}
	}
}
