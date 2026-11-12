using System;
using System.Collections.Generic;
using System.Globalization;
using System.Text;

namespace Alloy.Sdk.Script
{
	// Date: a time value in milliseconds since the epoch, taken apart and put together here; local time is UTC on this system
	public sealed partial class Interpreter
	{
		// The wall clock in milliseconds since the epoch; hosts without DateTime.UtcNow set the default before making interpreters
		public static ClockHandler DefaultNow = () => Math.Floor((DateTime.UtcNow - DateTime.UnixEpoch).TotalMilliseconds);
		public ClockHandler Now = DefaultNow;
		private static readonly string[] Days = { "Sun", "Mon", "Tue", "Wed", "Thu", "Fri", "Sat" };
		private static readonly string[] Months = { "Jan", "Feb", "Mar", "Apr", "May", "Jun", "Jul", "Aug", "Sep", "Oct", "Nov", "Dec" };
		private static readonly string[] LongMonths = { "january", "february", "march", "april", "may", "june", "july", "august", "september", "october", "november", "december" };
		private static readonly string[] LongDays = { "sunday", "monday", "tuesday", "wednesday", "thursday", "friday", "saturday" };
		private const double MsPerDay = 86400000;

		// A time value taken apart: the fields the getters return
		private struct Civil
		{
			public long Year;
			public int Month;
			public int Day;
			public int Weekday;
			public int Hour;
			public int Minute;
			public int Second;
			public int Millisecond;
		}

		private delegate JsValue CivilSelector(Civil civil);

		private void SetupDate()
		{
			JsFunction constructor = NewConstructor("Date", 7, DatePrototype, (i, t, a) => DateString(Now()), (i, a, target) => ConstructDate(a, target));
			DefineMethod(constructor, "now", 0, (i, t, a) => Now());
			DefineMethod(constructor, "parse", 1, (i, t, a) => ParseDate(Arg(a, 0).ToJsString()));
			DefineMethod(constructor, "UTC", 7, (i, t, a) => MakeTime(a));
			DefineMethod(DatePrototype, "valueOf", 0, (i, t, a) => ThisTime(t));
			DefineMethod(DatePrototype, "getTime", 0, (i, t, a) => ThisTime(t));
			DefineMethod(DatePrototype, "getTimezoneOffset", 0, (i, t, a) => 0);
			DefineMethod(DatePrototype, SymbolToPrimitive, 1, (i, t, a) => Arg(a, 0).ToJsString() == "number" ? ThisTime(t) : DateString(ThisTime(t)));
			foreach (string prefix in new[] { "get", "getUTC" })
			{
				DefineMethod(DatePrototype, prefix + "FullYear", 0, (i, t, a) => Component(t, c => c.Year));
				DefineMethod(DatePrototype, prefix + "Month", 0, (i, t, a) => Component(t, c => c.Month - 1));
				DefineMethod(DatePrototype, prefix + "Date", 0, (i, t, a) => Component(t, c => c.Day));
				DefineMethod(DatePrototype, prefix + "Day", 0, (i, t, a) => Component(t, c => c.Weekday));
				DefineMethod(DatePrototype, prefix + "Hours", 0, (i, t, a) => Component(t, c => c.Hour));
				DefineMethod(DatePrototype, prefix + "Minutes", 0, (i, t, a) => Component(t, c => c.Minute));
				DefineMethod(DatePrototype, prefix + "Seconds", 0, (i, t, a) => Component(t, c => c.Second));
				DefineMethod(DatePrototype, prefix + "Milliseconds", 0, (i, t, a) => Component(t, c => c.Millisecond));
			}
			DefineMethod(DatePrototype, "getYear", 0, (i, t, a) => Component(t, c => c.Year - 1900));
			foreach (string prefix in new[] { "set", "setUTC" })
			{
				DefineMethod(DatePrototype, prefix + "FullYear", 3, (i, t, a) => SetComponents(t, a, 0));
				DefineMethod(DatePrototype, prefix + "Month", 2, (i, t, a) => SetComponents(t, a, 1));
				DefineMethod(DatePrototype, prefix + "Date", 1, (i, t, a) => SetComponents(t, a, 2));
				DefineMethod(DatePrototype, prefix + "Hours", 4, (i, t, a) => SetComponents(t, a, 3));
				DefineMethod(DatePrototype, prefix + "Minutes", 3, (i, t, a) => SetComponents(t, a, 4));
				DefineMethod(DatePrototype, prefix + "Seconds", 2, (i, t, a) => SetComponents(t, a, 5));
				DefineMethod(DatePrototype, prefix + "Milliseconds", 1, (i, t, a) => SetComponents(t, a, 6));
			}
			DefineMethod(DatePrototype, "setTime", 1, (i, t, a) =>
			{
				JsPrimitiveObject date = ThisDate(t);
				date.Primitive = TimeClip(Arg(a, 0).ToNumber());
				return date.Primitive;
			});
			DefineMethod(DatePrototype, "toISOString", 0, (i, t, a) =>
			{
				double time = ThisTime(t);
				if (double.IsNaN(time))
					throw RangeError("Invalid time value");
				return IsoString(time);
			});
			DefineMethod(DatePrototype, "toJSON", 1, (i, t, a) =>
			{
				double time = ThisTime(t);
				return double.IsNaN(time) ? JsValue.Null : IsoString(time);
			});
			DefineMethod(DatePrototype, "toString", 0, (i, t, a) => DateString(ThisTime(t)));
			DefineMethod(DatePrototype, "toUTCString", 0, (i, t, a) =>
			{
				double time = ThisTime(t);
				if (double.IsNaN(time))
					return "Invalid Date";
				Civil c = Decompose(time);
				return Days[c.Weekday] + ", " + Pad2(c.Day) + " " + Months[c.Month - 1] + " " + Pad4(c.Year) + " " + Pad2(c.Hour) + ":" + Pad2(c.Minute) + ":" + Pad2(c.Second) + " GMT";
			});
			DatePrototype.Put("toGMTString", DatePrototype.Get("toUTCString", this), false);
			DefineMethod(DatePrototype, "toDateString", 0, (i, t, a) =>
			{
				double time = ThisTime(t);
				if (double.IsNaN(time))
					return "Invalid Date";
				Civil c = Decompose(time);
				return Days[c.Weekday] + " " + Months[c.Month - 1] + " " + Pad2(c.Day) + " " + Pad4(c.Year);
			});
			DefineMethod(DatePrototype, "toTimeString", 0, (i, t, a) =>
			{
				double time = ThisTime(t);
				if (double.IsNaN(time))
					return "Invalid Date";
				Civil c = Decompose(time);
				return Pad2(c.Hour) + ":" + Pad2(c.Minute) + ":" + Pad2(c.Second) + " GMT+0000 (Coordinated Universal Time)";
			});
			DefineMethod(DatePrototype, "toLocaleDateString", 0, (i, t, a) =>
			{
				double time = ThisTime(t);
				if (double.IsNaN(time))
					return "Invalid Date";
				Civil c = Decompose(time);
				return c.Month + "/" + c.Day + "/" + c.Year;
			});
			DefineMethod(DatePrototype, "toLocaleTimeString", 0, (i, t, a) =>
			{
				double time = ThisTime(t);
				if (double.IsNaN(time))
					return "Invalid Date";
				return LocaleTime(Decompose(time));
			});
			DefineMethod(DatePrototype, "toLocaleString", 0, (i, t, a) =>
			{
				double time = ThisTime(t);
				if (double.IsNaN(time))
					return "Invalid Date";
				Civil c = Decompose(time);
				return c.Month + "/" + c.Day + "/" + c.Year + ", " + LocaleTime(c);
			});
		}

		private static string LocaleTime(Civil c)
		{
			int hour = c.Hour % 12 == 0 ? 12 : c.Hour % 12;
			return hour + ":" + Pad2(c.Minute) + ":" + Pad2(c.Second) + (c.Hour < 12 ? " AM" : " PM");
		}

		private static string Pad2(int n) => n < 10 ? "0" + n : n.ToString(CultureInfo.InvariantCulture);

		private static string Pad3(int n) => n < 10 ? "00" + n : n < 100 ? "0" + n : n.ToString(CultureInfo.InvariantCulture);

		private static string Pad4(long year)
		{
			if (year < 0)
				return "-" + Pad4(-year).PadLeft(6, '0');
			string text = year.ToString(CultureInfo.InvariantCulture);
			return text.Length < 4 ? text.PadLeft(4, '0') : text;
		}

		private static string IsoString(double time)
		{
			Civil c = Decompose(time);
			string year = c.Year >= 0 && c.Year <= 9999 ? Pad4(c.Year) : (c.Year < 0 ? "-" : "+") + Math.Abs(c.Year).ToString(CultureInfo.InvariantCulture).PadLeft(6, '0');
			return year + "-" + Pad2(c.Month) + "-" + Pad2(c.Day) + "T" + Pad2(c.Hour) + ":" + Pad2(c.Minute) + ":" + Pad2(c.Second) + "." + Pad3(c.Millisecond) + "Z";
		}

		private JsValue ConstructDate(JsValue[] arguments, JsObject newTarget)
		{
			double time;
			if (arguments.Length == 0)
				time = Now();
			else if (arguments.Length == 1)
			{
				JsValue value = arguments[0];
				if (value.IsObject && value.Object is JsPrimitiveObject { Class: "Date" } other)
					time = other.Primitive.Number;
				else
				{
					JsValue primitive = value.ToPrimitive(false);
					time = primitive.IsString ? ParseDate(primitive.String) : TimeClip(primitive.ToNumber());
				}
			}
			else
				time = MakeTime(arguments);
			return new JsPrimitiveObject(PrototypeFromConstructor(newTarget, DatePrototype), time, "Date");
		}

		public JsPrimitiveObject NewDate(double time) => new JsPrimitiveObject(DatePrototype, TimeClip(time), "Date");

		private static double TimeClip(double time) => double.IsNaN(time) || Math.Abs(time) > 8.64e15 ? double.NaN : Math.Truncate(time) + 0.0;

		// Year, month and so on, each defaulting as the spec says, folded into a time value with overflow carried
		private static double MakeTime(JsValue[] arguments)
		{
			double[] parts = { double.NaN, 0, 1, 0, 0, 0, 0 };
			for (int i = 0; i < 7 && i < arguments.Length; i++)
				parts[i] = arguments[i].ToNumber();
			foreach (double part in parts)
			{
				if (!double.IsFinite(part))
					return double.NaN;
			}
			double year = Math.Truncate(parts[0]);
			if (year >= 0 && year <= 99)
				year += 1900;
			return MakeDate(year, Math.Truncate(parts[1]), Math.Truncate(parts[2]), Math.Truncate(parts[3]), Math.Truncate(parts[4]), Math.Truncate(parts[5]), Math.Truncate(parts[6]));
		}

		private static double MakeDate(double year, double month, double day, double hour, double minute, double second, double millisecond)
		{
			double ym = year + Math.Floor(month / 12);
			double mn = month % 12;
			if (mn < 0)
				mn += 12;
			if (Math.Abs(ym) > 400000)
				return double.NaN;
			double days = DaysFromCivil((long)ym, (int)mn + 1) + day - 1;
			double time = ((hour * 60 + minute) * 60 + second) * 1000 + millisecond;
			return TimeClip(days * MsPerDay + time);
		}

		// Days from the epoch to the first of a month, for any year (Howard Hinnant's algorithm)
		private static double DaysFromCivil(long year, int month)
		{
			year -= month <= 2 ? 1 : 0;
			long era = (year >= 0 ? year : year - 399) / 400;
			long yoe = year - era * 400;
			long doy = (153 * (month + (month > 2 ? -3 : 9)) + 2) / 5;
			long doe = yoe * 365 + yoe / 4 - yoe / 100 + doy;
			return era * 146097 + doe - 719468;
		}

		// A time value back to its civil fields
		private static Civil Decompose(double time)
		{
			double dayNumber = Math.Floor(time / MsPerDay);
			double msInDay = time - dayNumber * MsPerDay;
			long z = (long)dayNumber + 719468;
			long era = (z >= 0 ? z : z - 146096) / 146097;
			long doe = z - era * 146097;
			long yoe = (doe - doe / 1460 + doe / 36524 - doe / 146096) / 365;
			long doy = doe - (365 * yoe + yoe / 4 - yoe / 100);
			long mp = (5 * doy + 2) / 153;
			Civil c = new Civil
			{
				Day = (int)(doy - (153 * mp + 2) / 5 + 1),
				Month = (int)(mp < 10 ? mp + 3 : mp - 9),
			};
			c.Year = yoe + era * 400 + (c.Month <= 2 ? 1 : 0);
			c.Weekday = (int)((((long)dayNumber + 4) % 7 + 7) % 7);
			int ms = (int)msInDay;
			c.Hour = ms / 3600000;
			c.Minute = ms / 60000 % 60;
			c.Second = ms / 1000 % 60;
			c.Millisecond = ms % 1000;
			return c;
		}

		private JsPrimitiveObject ThisDate(JsValue thisValue)
		{
			if (thisValue.IsObject && thisValue.Object is JsPrimitiveObject { Class: "Date" } date)
				return date;
			throw TypeError("this is not a Date object.");
		}

		private double ThisTime(JsValue thisValue) => ThisDate(thisValue).Primitive.Number;

		private JsValue Component(JsValue thisValue, CivilSelector selector)
		{
			double time = ThisTime(thisValue);
			return double.IsNaN(time) ? double.NaN : selector(Decompose(time));
		}

		// Sets the component at the given position and any that follow it in the argument list
		private JsValue SetComponents(JsValue thisValue, JsValue[] arguments, int first)
		{
			JsPrimitiveObject date = ThisDate(thisValue);
			double time = date.Primitive.Number;
			if (double.IsNaN(time))
			{
				if (first != 0)
					return double.NaN;
				time = 0;
			}
			Civil c = Decompose(time);
			double[] parts = { c.Year, c.Month - 1, c.Day, c.Hour, c.Minute, c.Second, c.Millisecond };
			int limit = first switch { 0 => 3, 1 => 2, 2 => 1, 3 => 4, 4 => 3, 5 => 2, _ => 1 };
			for (int i = 0; i < limit && i < arguments.Length; i++)
				parts[first + i] = Math.Truncate(arguments[i].ToNumber());
			foreach (double part in parts)
			{
				if (!double.IsFinite(part))
				{
					date.Primitive = double.NaN;
					return double.NaN;
				}
			}
			date.Primitive = MakeDate(parts[0], parts[1], parts[2], parts[3], parts[4], parts[5], parts[6]);
			return date.Primitive;
		}

		private static string DateString(double time)
		{
			if (double.IsNaN(time))
				return "Invalid Date";
			Civil c = Decompose(time);
			return Days[c.Weekday] + " " + Months[c.Month - 1] + " " + Pad2(c.Day) + " " + Pad4(c.Year) + " " + Pad2(c.Hour) + ":" + Pad2(c.Minute) + ":" + Pad2(c.Second) + " GMT+0000 (Coordinated Universal Time)";
		}

		// ISO 8601 first, then the textual forms browsers accept: month names, weekday names, slashes, times with AM/PM and zone suffixes
		public static double ParseDate(string text)
		{
			text = text.Trim();
			if (text.Length == 0)
				return double.NaN;
			if (TryParseIso(text, out double iso))
				return iso;
			int paren = text.IndexOf('(');
			if (paren > 0)
				text = text.Substring(0, paren);
			long year = long.MinValue;
			int month = -1;
			int day = -1;
			int hour = 0;
			int minute = 0;
			int second = 0;
			int millisecond = 0;
			int offset = 0;
			bool pm = false;
			bool am = false;
			bool hasTime = false;
			foreach (string raw in text.Split(new[] { ' ', ',', '\t' }, StringSplitOptions.RemoveEmptyEntries))
			{
				string token = raw.ToLowerInvariant().TrimEnd('.');
				if (token.Length == 0)
					continue;
				if (token.Contains(':'))
				{
					string[] parts = token.Split(':');
					if (parts.Length < 2 || parts.Length > 3)
						return double.NaN;
					if (!int.TryParse(parts[0], NumberStyles.None, CultureInfo.InvariantCulture, out hour) || !int.TryParse(parts[1], NumberStyles.None, CultureInfo.InvariantCulture, out minute))
						return double.NaN;
					if (parts.Length == 3)
					{
						int dot = parts[2].IndexOf('.');
						string secondText = dot < 0 ? parts[2] : parts[2].Substring(0, dot);
						if (!int.TryParse(secondText, NumberStyles.None, CultureInfo.InvariantCulture, out second))
							return double.NaN;
						if (dot >= 0 && !int.TryParse(parts[2].Substring(dot + 1).PadRight(3, '0').Substring(0, 3), NumberStyles.None, CultureInfo.InvariantCulture, out millisecond))
							return double.NaN;
					}
					hasTime = true;
					continue;
				}
				if (token == "am" || token == "a.m")
				{
					am = true;
					continue;
				}
				if (token == "pm" || token == "p.m")
				{
					pm = true;
					continue;
				}
				if (token == "gmt" || token == "utc" || token == "ut" || token == "z")
					continue;
				if ((token.StartsWith("gmt", StringComparison.Ordinal) || token.StartsWith("utc", StringComparison.Ordinal)) && token.Length > 3 && (token[3] == '+' || token[3] == '-'))
					token = token.Substring(3);
				if ((token[0] == '+' || token[0] == '-') && token.Length >= 3 && char.IsDigit(token[1]) && year != long.MinValue)
				{
					string digits = token.Substring(1).Replace(":", "");
					if (!int.TryParse(digits, NumberStyles.None, CultureInfo.InvariantCulture, out int zone))
						return double.NaN;
					int zoneHours = digits.Length <= 2 ? zone : zone / 100;
					int zoneMinutes = digits.Length <= 2 ? 0 : zone % 100;
					offset = (token[0] == '-' ? -1 : 1) * (zoneHours * 60 + zoneMinutes);
					continue;
				}
				int monthIndex = MonthIndex(token);
				if (monthIndex >= 0)
				{
					month = monthIndex;
					continue;
				}
				if (DayIndex(token) >= 0)
					continue;
				if (token.Contains('/') || token.Contains('-') && char.IsDigit(token[0]))
				{
					string[] parts = token.Split('/', '-');
					if (parts.Length != 3)
						return double.NaN;
					int[] numbers = new int[3];
					for (int i = 0; i < 3; i++)
					{
						if (!int.TryParse(parts[i], NumberStyles.None, CultureInfo.InvariantCulture, out numbers[i]))
							return double.NaN;
					}
					if (parts[0].Length == 4)
					{
						year = numbers[0];
						month = numbers[1] - 1;
						day = numbers[2];
					}
					else
					{
						month = numbers[0] - 1;
						day = numbers[1];
						year = numbers[2];
						if (parts[2].Length <= 2)
							year += year < 50 ? 2000 : 1900;
					}
					continue;
				}
				string number = token.TrimEnd('s', 't', 'h', 'n', 'd', 'r');
				if (!int.TryParse(number, NumberStyles.None, CultureInfo.InvariantCulture, out int value))
					return double.NaN;
				if (raw.Length >= 3 && (raw.Length > 4 || value > 31 || day >= 0 && year == long.MinValue))
					year = value;
				else if (day < 0)
					day = value;
				else if (year == long.MinValue)
					year = value < 50 ? value + 2000 : value < 100 ? value + 1900 : value;
				else
					return double.NaN;
			}
			if (year == long.MinValue)
				return double.NaN;
			if (month < 0)
				month = 0;
			if (day < 0)
				day = 1;
			if (pm && hour < 12)
				hour += 12;
			if (am && hour == 12)
				hour = 0;
			if (month > 11 || day < 1 || day > 31 || hour > 24 || minute > 59 || second > 59)
				return double.NaN;
			_ = hasTime;
			return MakeDate(year, month, day, hour, minute, second, millisecond) - offset * 60000.0;
		}

		private static int MonthIndex(string token)
		{
			for (int i = 0; i < 12; i++)
			{
				if (token.Length >= 3 && LongMonths[i].StartsWith(token, StringComparison.Ordinal))
					return i;
			}
			return -1;
		}

		private static int DayIndex(string token)
		{
			for (int i = 0; i < 7; i++)
			{
				if (token.Length >= 3 && LongDays[i].StartsWith(token, StringComparison.Ordinal))
					return i;
			}
			return -1;
		}

		private static bool TryParseIso(string text, out double time)
		{
			time = double.NaN;
			int at = 0;
			int sign = 1;
			if (at < text.Length && (text[at] == '+' || text[at] == '-'))
			{
				sign = text[at] == '-' ? -1 : 1;
				at++;
			}
			if (!ReadDigits(text, ref at, at == 0 ? 4 : 6, out int year))
				return false;
			int month = 1;
			int day = 1;
			int hour = 0;
			int minute = 0;
			int second = 0;
			int millisecond = 0;
			int offset = 0;
			if (at < text.Length && text[at] == '-')
			{
				at++;
				if (!ReadDigits(text, ref at, 2, out month))
					return false;
				if (at < text.Length && text[at] == '-')
				{
					at++;
					if (!ReadDigits(text, ref at, 2, out day))
						return false;
				}
			}
			if (at < text.Length && (text[at] == 'T' || text[at] == 't' || text[at] == ' '))
			{
				at++;
				if (!ReadDigits(text, ref at, 2, out hour) || at >= text.Length || text[at] != ':')
					return false;
				at++;
				if (!ReadDigits(text, ref at, 2, out minute))
					return false;
				if (at < text.Length && text[at] == ':')
				{
					at++;
					if (!ReadDigits(text, ref at, 2, out second))
						return false;
					if (at < text.Length && (text[at] == '.' || text[at] == ','))
					{
						at++;
						int start = at;
						while (at < text.Length && char.IsDigit(text[at]))
							at++;
						if (at == start)
							return false;
						millisecond = int.Parse(text.Substring(start, Math.Min(3, at - start)).PadRight(3, '0'), CultureInfo.InvariantCulture);
					}
				}
				if (at < text.Length && (text[at] == 'Z' || text[at] == 'z'))
					at++;
				else if (at < text.Length && (text[at] == '+' || text[at] == '-'))
				{
					int zoneSign = text[at] == '-' ? -1 : 1;
					at++;
					if (!ReadDigits(text, ref at, 2, out int zoneHour))
						return false;
					if (at < text.Length && text[at] == ':')
						at++;
					if (!ReadDigits(text, ref at, 2, out int zoneMinute))
						return false;
					offset = zoneSign * (zoneHour * 60 + zoneMinute);
				}
			}
			if (at != text.Length)
				return false;
			if (month < 1 || month > 12 || day < 1 || day > 31 || hour > 24 || minute > 59 || second > 59)
				return false;
			time = MakeDate(sign * year, month - 1, day, hour, minute, second, millisecond) - offset * 60000.0;
			return true;
		}

		private static bool ReadDigits(string text, ref int at, int count, out int value)
		{
			value = 0;
			if (at + count > text.Length)
				return false;
			for (int i = 0; i < count; i++)
			{
				char c = text[at + i];
				if (c < '0' || c > '9')
					return false;
				value = value * 10 + (c - '0');
			}
			at += count;
			return true;
		}
	}
}
