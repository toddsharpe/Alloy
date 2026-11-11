using System;
using System.Collections.Generic;
using System.Text;
using System.Threading;
using Alloy.Kernel.Ke;

namespace Alloy.Kernel
{
	// Boot-time checks of the language features CoreLib carries beyond the basics: each one would fail to compile or to run without its runtime support
	internal static class FeatureTests
	{
		private interface IArea
		{
			int Area();
			int Doubled() => 2 * Area();
		}

		private sealed class Square : IArea { public int Side; public int Area() => Side * Side; }
		private sealed class Rect : IArea { public int W, H; public int Area() => W * H; }
		private sealed class Tri : IArea { public int B, H; public int Area() => B * H / 2; }
		private sealed class Unit1 : IArea { public int Area() => 1; }
		private sealed class Unit2 : IArea { public int Area() => 2; }
		private sealed class Unit3 : IArea { public int Area() => 3; }
		private sealed class Unit4 : IArea { public int Area() => 4; }
		private sealed class Unit5 : IArea { public int Area() => 5; }
		private struct Dot : IArea { public int Area() => 0; }

		private interface IZero<T> where T : IZero<T>
		{
			static abstract T Zero { get; }
			static abstract T Add(T a, T b);
		}

		private struct Pt : IZero<Pt>
		{
			public int X;
			public static Pt Zero => default;
			public static Pt Add(Pt a, Pt b) => new Pt { X = a.X + b.X };
		}

		private sealed class Counter : IZero<Counter>
		{
			public int N;
			public static Counter Zero => new Counter();
			public static Counter Add(Counter a, Counter b) => new Counter { N = a.N + b.N };
		}

		private sealed class Resource : IDisposable
		{
			public static int Open;
			public Resource() => Open++;
			public void Dispose() => Open--;
		}

		private record Point(int X, int Y);
		private record struct Size(int W, int H);
		private enum Day : byte { Mon, Tue, Wed }
		[Flags] private enum Bits { None = 0, A = 1, B = 2, C = 4 }

		private struct Pair
		{
			public int A;
			public int B;
		}

		private readonly struct Hex : ISpanFormattable
		{
			private readonly uint _value;
			public Hex(uint value) => _value = value;
			public bool TryFormat(Span<char> destination, out int charsWritten, ReadOnlySpan<char> format, IFormatProvider provider) =>
				Alloy.Text.Number.TryFormatHex(_value, destination, out charsWritten, 4);
			public string ToString(string format, IFormatProvider formatProvider) => _value.ToString("X4");
		}

		private static IArea s_shape;
		private static int s_calls;
		private static Func<int, int> s_chain;
		private static event Action<int> s_fired;
		private static readonly object s_gate = new object();
		private static int s_counter;
		private static KEvent s_done;

		public static void Run()
		{
			SelfTest.Check("delegates and lambdas", Delegates());
			SelfTest.Check("multicast delegates and events", Events());
			SelfTest.Check("interface dispatch through cells", Interfaces());
			SelfTest.Check("generic interfaces and using", Enumerables());
			SelfTest.Check("static abstract interface members", StaticAbstract());
			SelfTest.Check("records", Records());
			SelfTest.Check("tuples", Tuples());
			SelfTest.Check("string interpolation", Interpolation());
			SelfTest.Check("lock", Lock());
			SelfTest.Check("typeof and GetType", Types());
			SelfTest.Check("enums", Enums());
			SelfTest.Check("List and Dictionary", Collections());
			SelfTest.Check("strings", Strings());
			SelfTest.Check("Index and Range", Ranges());
			SelfTest.Check("comparers, Nullable and struct equality", Equality());
			SelfTest.Check("arrays", Arrays());
		}

		private static int Twice(int x) => 2 * x;

		private static int AddCall(int x)
		{
			s_calls++;
			return x + 1;
		}

		private static bool Delegates()
		{
			int captured = 10;
			Func<int, int> add = x => x + captured;
			Func<int, int> twice = Twice;
			Func<int, int, int, int, int> sum4 = (a, b, c, d) => a + b + c + d;
			Square square = new Square { Side = 3 };
			Func<int> area = square.Area;
			Predicate<int> even = n => (n & 1) == 0;
			Action<int> set = n => captured = n;
			set(5);
			Func<int, int> same = Twice;
			return Expect(add(1) == 6, "a lambda sees the captured variable") & Expect(twice(4) == 8, "a static method group") & Expect(sum4(1, 2, 3, 4) == 10, "a four-argument lambda") &
				Expect(area() == 9, "an instance method group") & Expect(even(4) && !even(3), "a Predicate") & Expect(captured == 5, "an Action writes the captured variable") &
				Expect(twice == same, "equal delegates to one static method") & Expect(twice != add, "unequal delegates") & Expect(twice.Equals(same), "Equals on delegates") &
				Expect(twice.Target == null, "no target on a static delegate") & Expect(area.Target == square, "the target of an instance delegate");
		}

		private static bool Expect(bool condition, string what)
		{
			if (!condition)
				Log.WriteLine(string.Concat("       expected ", what));
			return condition;
		}

		private static bool Events()
		{
			s_calls = 0;
			s_chain = AddCall;
			s_chain += AddCall;
			s_chain += AddCall;
			int last = s_chain(1);
			bool three = s_calls == 3 && last == 2 && s_chain.GetInvocationList().Length == 3;
			s_chain -= AddCall;
			s_chain(1);
			bool two = s_calls == 5 && s_chain.HasSingleTarget == false;
			s_chain -= AddCall;
			s_chain -= AddCall;
			bool empty = s_chain == null;

			int fired = 0;
			Action<int> handler = n => fired += n;
			s_fired += handler;
			s_fired += n => fired += 100 * n;
			s_fired?.Invoke(2);
			s_fired -= handler;
			s_fired?.Invoke(1);
			return three && two && empty && fired == 302;
		}

		private static IArea Make(int pick) => pick switch
		{
			0 => new Square { Side = 3 },
			1 => new Rect { W = 2, H = 5 },
			2 => new Tri { B = 4, H = 3 },
			3 => new Unit1(),
			4 => new Unit2(),
			5 => new Unit3(),
			6 => new Unit4(),
			7 => new Unit5(),
			_ => new Dot(),
		};

		// Nine implementations keep the compiler from guessing the type, so the calls go through dispatch cells and the stub's cache
		private static bool Interfaces()
		{
			int total = 0;
			int doubled = 0;
			for (int round = 0; round < 3; round++)
			{
				for (int pick = 0; pick < 9; pick++)
				{
					s_shape = Make(pick);
					total += s_shape.Area();
					doubled += s_shape.Doubled();
				}
			}
			IArea boxed = new Dot();
			return total == 3 * (9 + 10 + 6 + 1 + 2 + 3 + 4 + 5) && doubled == 2 * total && boxed.Doubled() == 0 && s_shape is Dot;
		}

		private static int Sum(IEnumerable<int> values)
		{
			int sum = 0;
			foreach (int value in values)
				sum += value;
			return sum;
		}

		private static bool Enumerables()
		{
			List<int> list = new List<int> { 1, 2, 3 };
			int[] array = { 4, 5, 6 };
			IList<int> asList = array;
			IReadOnlyList<int> asReadOnly = list;
			bool disposed;
			using (new Resource())
				disposed = Resource.Open == 1;
			disposed &= Resource.Open == 0;
			return Sum(list) == 6 && Sum(array) == 15 && asList.Count == 3 && asList[1] == 5 && asList.IndexOf(6) == 2 && asReadOnly[2] == 3 && disposed;
		}

		private static T Total<T>(ReadOnlySpan<T> values) where T : IZero<T>
		{
			T total = T.Zero;
			for (int i = 0; i < values.Length; i++)
				total = T.Add(total, values[i]);
			return total;
		}

		private static bool StaticAbstract()
		{
			Pt[] points = { new Pt { X = 1 }, new Pt { X = 2 }, new Pt { X = 3 } };
			Counter[] counters = { new Counter { N = 4 }, new Counter { N = 5 } };
			return Total<Pt>(points).X == 6 && Total<Counter>(counters).N == 9;
		}

		private static bool Records()
		{
			Point a = new Point(1, 2);
			Point b = new Point(1, 2);
			Point c = a with { Y = 3 };
			Size s = new Size(4, 5);
			Size t = s with { H = 6 };
			return a == b && a != c && a.Equals(b) && a.GetHashCode() == b.GetHashCode() && c.Y == 3 && c.X == 1 &&
				a.ToString() == "Point { X = 1, Y = 2 }" && s == new Size(4, 5) && s != t && t.ToString() == "Size { W = 4, H = 6 }";
		}

		private static (int Min, int Max) MinMax(ReadOnlySpan<int> values)
		{
			int min = values[0];
			int max = values[0];
			foreach (int value in values)
			{
				if (value < min)
					min = value;
				if (value > max)
					max = value;
			}
			return (min, max);
		}

		private static bool Tuples()
		{
			int[] values = { 5, 2, 9, 1 };
			(int min, int max) = MinMax(values);
			(int, string) pair = (7, "seven");
			var named = (Count: 3, Name: "three");
			return min == 1 && max == 9 && pair.Item1 == 7 && pair.Item2 == "seven" && named.Count == 3 && named.Name == "three" &&
				pair == (7, "seven") && pair != (8, "seven") && pair.Equals((7, "seven")) && (1, 2).ToString() == "(1, 2)";
		}

		private static bool Interpolation()
		{
			int x = 42;
			long big = -5;
			byte small = 255;
			string name = "alloy";
			char c = 'k';
			Day day = Day.Wed;
			Hex hex = new Hex(0xBEEF);
			string text = $"{x} {big,4} {small:X2} {name,-7}|{c} {day} {hex} {x:D5} {true} {null}";
			string grown = $"{name}{name}{name}{name}{name}{name}{name}{name}{x}";
			Span<char> buffer = stackalloc char[32];
			bool wrote = buffer.TryWrite($"x={x:X} day={day}", out int written);
			Span<char> tiny = stackalloc char[4];
			bool refused = !tiny.TryWrite($"{name}{name}", out int none);
			return text == "42   -5 FF alloy  |k 2 BEEF 00042 True " && grown == "alloyalloyalloyalloyalloyalloyalloyalloy42" &&
				wrote && buffer.Slice(0, written).SequenceEqual("x=2A day=2") && refused && none == 0;
		}

		private static unsafe bool Lock()
		{
			s_counter = 0;
			s_done = new KEvent(false);
			bool recursive;
			lock (s_gate)
			{
				lock (s_gate)
					recursive = Monitor.IsEntered(s_gate);
			}
			bool released = !Monitor.IsEntered(s_gate);
			Scheduler.Start("locker", &LockLoop, null);
			for (int i = 0; i < 1000; i++)
			{
				lock (s_gate)
				{
					int seen = s_counter;
					if ((i & 15) == 0)
						Scheduler.Yield();
					s_counter = seen + 1;
				}
			}
			bool finished = s_done.Wait(2000);
			return recursive && released && finished && s_counter == 2000;
		}

		private static unsafe void LockLoop(void* argument)
		{
			for (int i = 0; i < 1000; i++)
			{
				lock (s_gate)
				{
					int seen = s_counter;
					if ((i & 7) == 0)
						Scheduler.Yield();
					s_counter = seen + 1;
				}
			}
			s_done.Signal();
			Scheduler.Exit();
		}

		private static bool Types()
		{
			object square = new Square();
			object boxed = 5;
			Type listType = typeof(List<int>);
			return square.GetType() == typeof(Square) && square.GetType() != typeof(Rect) && boxed.GetType() == typeof(int) && typeof(int) != typeof(uint) &&
				listType == typeof(List<int>) && listType != typeof(List<long>) && typeof(int).IsValueType && !typeof(string).IsValueType && typeof(Day).IsEnum &&
				typeof(IArea).IsInterface && typeof(int[]).IsArray && typeof(int[]).GetElementType() == typeof(int) && typeof(IArea).IsAssignableFrom(typeof(Square)) &&
				typeof(Square).BaseType == typeof(object) && ReferenceEquals(square.GetType(), new Square().GetType());
		}

		private static bool Enums()
		{
			Bits bits = Bits.A | Bits.C;
			Day day = Day.Tue;
			object boxed = day;
			return Day.Wed.ToString() == "2" && ((Day)200).ToString("X") == "C8" && bits.HasFlag(Bits.C) && !bits.HasFlag(Bits.B) && boxed.Equals(Day.Tue) &&
				!boxed.Equals(Day.Mon) && EqualityComparer<Day>.Default.Equals(day, Day.Tue) && EqualityComparer<Day>.Default.GetHashCode(day) == EqualityComparer<Day>.Default.GetHashCode(Day.Tue) &&
				Comparer<Day>.Default.Compare(Day.Mon, Day.Wed) < 0 && day.CompareTo(Day.Wed) < 0 && day.GetHashCode() == 1;
		}

		private static bool Collections()
		{
			List<int> list = new List<int>();
			for (int i = 0; i < 100; i++)
				list.Add(99 - i);
			list.Sort();
			bool sorted = true;
			for (int i = 0; i < 100; i++)
				sorted &= list[i] == i;
			list.RemoveAt(0);
			list.Insert(0, -1);
			list.Remove(50);
			int found = list.IndexOf(51);
			List<string> words = new List<string> { "pear", "apple", "fig" };
			words.Sort((a, b) => string.CompareOrdinal(a, b));
			int total = 0;
			foreach (int value in list)
				total += value;

			Dictionary<string, int> ages = new Dictionary<string, int>();
			for (int i = 0; i < 50; i++)
				ages[string.Concat("k", i.ToString())] = i;
			ages["k7"] = 70;
			bool removed = ages.Remove("k3");
			int count = 0;
			foreach (KeyValuePair<string, int> entry in ages)
				count += entry.Key.Length > 0 ? 1 : 0;
			Dictionary<int, Square> byId = new Dictionary<int, Square>();
			byId.Add(1, new Square { Side = 1 });
			byId.TryAdd(1, new Square { Side = 2 });
			return sorted && list.Count == 99 && list[0] == -1 && found == 50 && words[0] == "apple" && words[2] == "pear" && total == 4949 - 50 &&
				ages.Count == 49 && ages["k7"] == 70 && ages.TryGetValue("k10", out int ten) && ten == 10 && !ages.ContainsKey("k3") && removed && count == 49 &&
				byId[1].Side == 1 && byId.Count == 1 && !byId.TryGetValue(2, out _);
		}

		private static bool Strings()
		{
			string s = "  Hello, Alloy  ";
			string t = s.Trim();
			string[] parts = "a,b,,c".Split(',');
			object o = 12;
			string five = "a" + "b" + "c" + "d" + "e" + 'f' + o;
			StringBuilder builder = new StringBuilder();
			builder.Append("n=").Append(42).Append(' ').Append(true).Append((object)"x");
			return t == "Hello, Alloy" && t.Substring(7) == "Alloy" && t.Substring(0, 5) == "Hello" && t.IndexOf(',') == 5 && t.IndexOf("Alloy") == 7 && t.Contains("llo") &&
				t.StartsWith("Hello") && t.EndsWith("loy") && t.ToUpperInvariant() == "HELLO, ALLOY" && t.ToLowerInvariant() == "hello, alloy" && parts.Length == 4 && parts[2] == "" && parts[3] == "c" &&
				five == "abcdef12" && string.Join("-", parts) == "a-b--c" && string.CompareOrdinal("abc", "abd") < 0 && "abc".CompareTo("abc") == 0 && t.Replace('l', 'L') == "HeLLo, ALLoy" &&
				"7".PadLeft(3, '0') == "007" && builder.ToString() == "n=42 Truex" && int.TryParse("-123", out int parsed) && parsed == -123 && !int.TryParse("12x", out _) &&
				string.IsNullOrWhiteSpace("  ") && !string.IsNullOrWhiteSpace(" a") && 255.ToString("X2") == "FF" && (-1).ToString("X") == "FFFFFFFF" && 7.ToString("D3") == "007";
		}

		private static bool Ranges()
		{
			int[] values = { 1, 2, 3, 4, 5 };
			int[] middle = values[1..4];
			int[] tail = values[^2..];
			ReadOnlySpan<int> span = values;
			ReadOnlySpan<int> head = span[..2];
			string text = "kernel";
			Index last = ^1;
			return values[^1] == 5 && values[last] == 5 && middle.Length == 3 && middle[0] == 2 && middle[2] == 4 && tail.Length == 2 && tail[0] == 4 && head.Length == 2 &&
				text[2..] == "rnel" && text[..^2] == "kern" && new Range(1, 3).GetOffsetAndLength(5) == (1, 2);
		}

		private static bool Equality()
		{
			int? none = null;
			int? some = 3;
			Pair p = new Pair { A = 1, B = 2 };
			Pair q = new Pair { A = 1, B = 2 };
			object boxedP = p;
			return EqualityComparer<int>.Default.Equals(3, 3) && !EqualityComparer<int>.Default.Equals(3, 4) && EqualityComparer<string>.Default.Equals("a", "a") &&
				Comparer<string>.Default.Compare("a", "b") < 0 && Comparer<int>.Default.Compare(2, 1) > 0 && EqualityComparer<int?>.Default.Equals(none, null) &&
				!none.HasValue && some.HasValue && some.Value == 3 && some == 3 && none != 3 && some.ToString() == "3" && none.GetValueOrDefault(9) == 9 &&
				boxedP.Equals(q) && p.Equals(q) && p.GetHashCode() == q.GetHashCode() && EqualityComparer<Pair>.Default.Equals(p, q);
		}

		private static bool Arrays()
		{
			int[] values = { 3, 1, 2 };
			Array.Sort(values);
			int[] grown = values;
			Array.Resize(ref grown, 5);
			int[] copy = new int[3];
			Array.Copy(values, copy, 3);
			IEnumerable<int> asEnumerable = values;
			int sum = 0;
			foreach (int value in asEnumerable)
				sum += value;
			return Array.Empty<int>().Length == 0 && ReferenceEquals(Array.Empty<int>(), Array.Empty<int>()) && values[0] == 1 && values[2] == 3 && grown.Length == 5 && grown[2] == 3 && grown[4] == 0 &&
				Array.IndexOf(copy, 2) == 1 && Array.IndexOf(copy, 9) < 0 && sum == 6 && Array.Exists(values, v => v == 2) && Array.Find(values, v => v > 1) == 2;
		}
	}
}
