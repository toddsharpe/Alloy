using System;
using System.Threading;
using Alloy.Arch.X64;
using Alloy.Text;

namespace Alloy.Boot
{
	// Exercises every CoreLib runtime contract on real firmware, so an ILC upgrade that breaks one fails the boot test
	internal static class SelfTest
	{
		private static int s_passed;
		private static int s_failed;

		public static bool Run()
		{
			Check("allocation and virtual calls", Allocation());
			Check("string concat and equality", Strings());
			Check("number formatting", Formatting());
			Check("value and reference arrays", Arrays());
			Check("class and interface casts", Casts());
			Check("boxing", Boxing());
			Check("lazy static constructor", LazyStatics.Value == 7 && LazyStatics.RunCount == 1);
			Check("preinitialized statics", PreinitializedStatics());
			Check("GC statics", GcStatics());
			Check("large struct copy", StructCopy());
			Check("spans", Spans());
			Check("interlocked", InterlockedOps());

			SpanWriter line = new SpanWriter(stackalloc char[64]);
			line.Append("selftest: ");
			line.Append(s_passed);
			line.Append('/');
			line.Append(s_passed + s_failed);
			line.Append(" passed");
			Log.WriteLine(ref line);
			return s_failed == 0;
		}

		private static void Check(string name, bool passed)
		{
			if (passed)
				s_passed++;
			else
				s_failed++;
			Log.WriteLine(string.Concat(passed ? "  ok   " : "  FAIL ", name));
		}

		private abstract class Shape
		{
			public abstract int Area();
		}

		private sealed class Square : Shape, IMarker
		{
			private readonly int _side;

			public Square(int side)
			{
				_side = side;
			}

			public override int Area() => _side * _side;
		}

		private sealed class Rectangle : Shape
		{
			private readonly int _width;
			private readonly int _height;

			public Rectangle(int width, int height)
			{
				_width = width;
				_height = height;
			}

			public override int Area() => _width * _height;
		}

		private interface IMarker
		{
		}

		private static bool Allocation()
		{
			Shape[] shapes = new Shape[] { new Square(3), new Rectangle(2, 5) };
			int total = 0;
			foreach (Shape shape in shapes)
				total += shape.Area();
			return total == 19;
		}

		private static bool Strings()
		{
			string name = string.Concat("Al", "loy");
			return name == "Alloy" && name.Length == 5 && name[2] == 'l' && name != "MetalOS" && new string('x', 3) == "xxx";
		}

		private static bool Formatting()
		{
			SpanWriter writer = new SpanWriter(stackalloc char[64]);
			writer.Append(-42);
			writer.Append(' ');
			writer.AppendHex(0xDEADBEEF, 10);
			return 12345.ToString() == "12345" && long.MinValue.ToString() == "-9223372036854775808" && writer.Written.SequenceEqual("-42 00DEADBEEF");
		}

		private static bool Arrays()
		{
			int[] values = new int[16];
			for (int i = 0; i < values.Length; i++)
				values[i] = i;
			int sum = 0;
			foreach (int value in values)
				sum += value;

			// A covariant store into a string[] seen as object[] goes through TypeCast.StelemRef
			object[] strings = new string[2];
			strings[0] = "a";
			strings[1] = null;
			return sum == 120 && strings.Length == 2 && (string)strings[0] == "a";
		}

		private static bool Casts()
		{
			object square = new Square(1);
			object rectangle = new Rectangle(1, 1);
			return square is Shape && square is Square && !(square is Rectangle) && square is IMarker && !(rectangle is IMarker) &&
				(rectangle as Shape) != null && (square as string) == null;
		}

		private static bool Boxing()
		{
			object boxed = 42;
			object boxedLong = 0x1_0000_0000L;
			return boxed is int && (int)boxed == 42 && !(boxed is long) && (long)boxedLong == 0x1_0000_0000L && boxed.ToString() == "42";
		}

		private static class LazyStatics
		{
			public static readonly int Value = Compute();
			public static int RunCount;

			// Reads the TSC so the compiler can't preinitialize this; it has to run a real static constructor
			private static int Compute()
			{
				RunCount++;
				return Cpu.ReadTsc() != 0 ? 7 : 8;
			}
		}

		private static class PreinitStatics
		{
			public static readonly int[] Table = { 1, 2, 3, 4 };
			public static readonly string Greeting = "hello";
		}

		private static bool PreinitializedStatics() => PreinitStatics.Table.Length == 4 && PreinitStatics.Table[3] == 4 && PreinitStatics.Greeting == "hello";

		private static class GcStaticHolder
		{
			public static string Name;
			public static Shape Shape;
		}

		private static bool GcStatics()
		{
			GcStaticHolder.Name = string.Concat("gc", "static");
			GcStaticHolder.Shape = new Rectangle(3, 4);
			return GcStaticHolder.Name == "gcstatic" && GcStaticHolder.Shape.Area() == 12;
		}

		private struct Large
		{
			public long A, B, C, D, E, F, G, H;
		}

		private static bool StructCopy()
		{
			Large source = new Large { A = 1, B = 2, C = 3, D = 4, E = 5, F = 6, G = 7, H = 8 };
			Large copy = source;
			Large[] array = new Large[4];
			array[2] = copy;
			return array[2].H == 8 && array[2].A == 1 && array[1].H == 0;
		}

		private static bool Spans()
		{
			Span<byte> buffer = stackalloc byte[32];
			buffer.Fill(0xAB);
			buffer.Slice(8, 8).Clear();
			Span<byte> other = new byte[32];
			buffer.CopyTo(other);
			return other[0] == 0xAB && other[8] == 0 && other[15] == 0 && other[16] == 0xAB && other.SequenceEqual(buffer);
		}

		private static bool InterlockedOps()
		{
			int value = 5;
			int previous = Interlocked.CompareExchange(ref value, 9, 5);
			long counter = 0;
			Interlocked.Increment(ref counter);
			return previous == 5 && value == 9 && counter == 1;
		}
	}
}
