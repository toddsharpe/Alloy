using System;
using Alloy.Sdk.Script;

namespace Alloy.Sdk.Web
{
	// The kernel's clock as scripts and cookies want it: milliseconds since the epoch, with the RTC taken as UTC
	public static class WallClock
	{
		public static double EpochNow()
		{
			Abi.SystemTime time = Kernel.Time;
			if (time.Year < 2000)
				return 0;
			try
			{
				DateTime now = new DateTime(time.Year, time.Month, time.Day, time.Hour, time.Minute, time.Second, time.Milliseconds, DateTimeKind.Utc);
				return Math.Floor((now - DateTime.UnixEpoch).TotalMilliseconds);
			}
			catch (ArgumentOutOfRangeException)
			{
				return 0;
			}
		}

		// Points the engine and the cookie jar at the kernel clock; the host tests keep DateTime.UtcNow
		public static void Install()
		{
			Interpreter.DefaultNow = EpochNow;
			CookieJar.Now = EpochNow;
		}
	}
}
