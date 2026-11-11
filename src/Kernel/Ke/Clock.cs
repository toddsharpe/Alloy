using Alloy.Abi;
using Alloy.Kernel.Platform;
using Alloy.Text;

namespace Alloy.Kernel.Ke
{
	// The wall clock: the host's UTC from its time sync, carried forward on the reference counter, shown in the host's zone; the CMOS RTC until the first sync
	internal static class Clock
	{
		private static bool s_synced;
		private static ulong s_hostTime;
		private static ulong s_reference;
		private static long s_zoneOffsetSeconds;

		public static bool Synced => s_synced;

		public static SystemTime Now()
		{
			if (!s_synced)
				return Rtc.Read();

			ulong utc = s_hostTime + (HyperV.ReferenceTime() - s_reference);
			return Calendar.FromFileTime((ulong)((long)utc + s_zoneOffsetSeconds * (long)Calendar.TicksPerSecond));
		}

		// The host's UTC as a file time, and the reference counter's value when the host took it
		public static void SetHostTime(ulong hostTime, ulong reference)
		{
			ulong flags = Scheduler.Lock();
			bool first = !s_synced;
			if (first)
				s_zoneOffsetSeconds = ZoneOffset(hostTime + (HyperV.ReferenceTime() - reference));
			s_hostTime = hostTime;
			s_reference = reference;
			s_synced = true;
			Scheduler.Unlock(flags);

			if (first)
			{
				SpanWriter line = new SpanWriter(stackalloc char[96]);
				line.Append("Clock: synced with the host, zone offset ");
				line.Append(s_zoneOffsetSeconds < 0 ? '-' : '+');
				long magnitude = s_zoneOffsetSeconds < 0 ? -s_zoneOffsetSeconds : s_zoneOffsetSeconds;
				AppendTwoDigits(ref line, (int)(magnitude / 3600));
				line.Append(':');
				AppendTwoDigits(ref line, (int)(magnitude / 60 % 60));
				Log.WriteLine(ref line);
			}
		}

		// The RTC runs in the host's local time while the host sends UTC, so their difference, to the nearest quarter hour, is the zone's offset
		private static long ZoneOffset(ulong utc)
		{
			SystemTime local = Rtc.Read();
			long difference = (long)(Calendar.ToFileTime(in local) / Calendar.TicksPerSecond) - (long)(utc / Calendar.TicksPerSecond);
			long quarters = (difference + (difference < 0 ? -450 : 450)) / 900;
			return quarters * 900;
		}

		private static void AppendTwoDigits(ref SpanWriter line, int value)
		{
			line.Append((char)('0' + value / 10 % 10));
			line.Append((char)('0' + value % 10));
		}
	}
}
