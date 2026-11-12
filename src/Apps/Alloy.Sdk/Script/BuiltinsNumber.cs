using System;
using System.Globalization;
using System.Text;

namespace Alloy.Sdk.Script
{
	// Number, Boolean and Math
	public sealed partial class Interpreter
	{
		private void SetupNumber()
		{
			JsFunction constructor = NewConstructor("Number", 1, NumberPrototype,
				(i, t, a) => a.Length == 0 ? 0 : a[0].ToNumber(),
				(i, a, target) => new JsPrimitiveObject(PrototypeFromConstructor(target, NumberPrototype), a.Length == 0 ? 0 : a[0].ToNumber(), "Number"));
			constructor.Put("MAX_SAFE_INTEGER", 9007199254740991.0, false);
			constructor.Put("MIN_SAFE_INTEGER", -9007199254740991.0, false);
			constructor.Put("MAX_VALUE", double.MaxValue, false);
			constructor.Put("MIN_VALUE", double.Epsilon, false);
			constructor.Put("EPSILON", Math.Pow(2, -52), false);
			constructor.Put("POSITIVE_INFINITY", double.PositiveInfinity, false);
			constructor.Put("NEGATIVE_INFINITY", double.NegativeInfinity, false);
			constructor.Put("NaN", double.NaN, false);
			DefineMethod(constructor, "isInteger", 1, (i, t, a) => Arg(a, 0).IsNumber && double.IsFinite(a[0].Number) && Math.Floor(a[0].Number) == a[0].Number);
			DefineMethod(constructor, "isSafeInteger", 1, (i, t, a) => Arg(a, 0).IsNumber && double.IsFinite(a[0].Number) && Math.Floor(a[0].Number) == a[0].Number && Math.Abs(a[0].Number) <= 9007199254740991.0);
			DefineMethod(constructor, "isFinite", 1, (i, t, a) => Arg(a, 0).IsNumber && double.IsFinite(a[0].Number));
			DefineMethod(constructor, "isNaN", 1, (i, t, a) => Arg(a, 0).IsNumber && double.IsNaN(a[0].Number));
			constructor.Put("parseFloat", Global.Get("parseFloat", this), false);
			constructor.Put("parseInt", Global.Get("parseInt", this), false);
			DefineMethod(NumberPrototype, "valueOf", 0, (i, t, a) => ThisNumber(t, "valueOf"));
			DefineMethod(NumberPrototype, "toString", 1, (i, t, a) =>
			{
				double n = ThisNumber(t, "toString");
				int radix = Arg(a, 0).IsUndefined ? 10 : a[0].ToInt32();
				if (radix < 2 || radix > 36)
					throw RangeError("toString() radix must be between 2 and 36");
				return radix == 10 ? JsValue.NumberToString(n) : ToRadix(n, radix);
			});
			DefineMethod(NumberPrototype, "toLocaleString", 0, (i, t, a) =>
			{
				double n = ThisNumber(t, "toLocaleString");
				if (!double.IsFinite(n))
					return JsValue.NumberToString(n);
				return n == Math.Floor(n) && Math.Abs(n) < 1e21 ? n.ToString("#,##0", CultureInfo.InvariantCulture) : n.ToString("#,##0.###", CultureInfo.InvariantCulture);
			});
			DefineMethod(NumberPrototype, "toFixed", 1, (i, t, a) =>
			{
				double n = ThisNumber(t, "toFixed");
				int digits = Arg(a, 0).IsUndefined ? 0 : a[0].ToInt32();
				if (digits < 0 || digits > 100)
					throw RangeError("toFixed() digits argument must be between 0 and 100");
				if (!double.IsFinite(n) || Math.Abs(n) >= 1e21)
					return JsValue.NumberToString(n);
				return ToFixed(n, digits);
			});
			DefineMethod(NumberPrototype, "toPrecision", 1, (i, t, a) =>
			{
				double n = ThisNumber(t, "toPrecision");
				if (Arg(a, 0).IsUndefined || !double.IsFinite(n))
					return JsValue.NumberToString(n);
				int precision = a[0].ToInt32();
				if (precision < 1 || precision > 100)
					throw RangeError("toPrecision() argument must be between 1 and 100");
				if (n == 0)
					return precision == 1 ? "0" : "0." + new string('0', precision - 1);
				int exponent = (int)Math.Floor(Math.Log10(Math.Abs(n)));
				if (exponent < -6 || exponent >= precision)
				{
					string mantissa = (n / Math.Pow(10, exponent)).ToString("F" + (precision - 1), CultureInfo.InvariantCulture);
					if (mantissa.StartsWith("10", StringComparison.Ordinal) || mantissa.StartsWith("-10", StringComparison.Ordinal))
					{
						exponent++;
						mantissa = (n / Math.Pow(10, exponent)).ToString("F" + (precision - 1), CultureInfo.InvariantCulture);
					}
					return mantissa + "e" + (exponent >= 0 ? "+" : "") + exponent.ToString(CultureInfo.InvariantCulture);
				}
				return n.ToString("F" + Math.Max(0, precision - 1 - exponent), CultureInfo.InvariantCulture);
			});
			DefineMethod(NumberPrototype, "toExponential", 1, (i, t, a) =>
			{
				double n = ThisNumber(t, "toExponential");
				if (!double.IsFinite(n))
					return JsValue.NumberToString(n);
				int digits = Arg(a, 0).IsUndefined ? -1 : a[0].ToInt32();
				string text = digits < 0 ? n.ToString("E16", CultureInfo.InvariantCulture) : n.ToString("E" + digits, CultureInfo.InvariantCulture);
				int e = text.IndexOf('E');
				string mantissa = text.Substring(0, e);
				if (digits < 0 && mantissa.Contains('.'))
					mantissa = mantissa.TrimEnd('0').TrimEnd('.');
				int exponent = int.Parse(text.Substring(e + 1), CultureInfo.InvariantCulture);
				return mantissa + "e" + (exponent >= 0 ? "+" : "") + exponent.ToString(CultureInfo.InvariantCulture);
			});
		}

		private double ThisNumber(JsValue thisValue, string method)
		{
			if (thisValue.IsNumber)
				return thisValue.Number;
			if (thisValue.IsObject && thisValue.Object is JsPrimitiveObject { Primitive: { IsNumber: true } } wrapper)
				return wrapper.Primitive.Number;
			throw TypeError("Number.prototype." + method + " requires that 'this' be a Number");
		}

		// toFixed on the exact value of the double: n / 10^digits closest to it, the larger n on a tie
		private static string ToFixed(double value, int digits)
		{
			bool negative = value < 0 || value == 0 && double.IsNegative(value) && false;
			double x = Math.Abs(value);
			long bits = BitConverter.DoubleToInt64Bits(x);
			int exponent = (int)((bits >> 52) & 0x7FF);
			long mantissa = bits & 0xFFFFFFFFFFFFFL;
			if (exponent == 0)
				exponent++;
			else
				mantissa |= 1L << 52;
			exponent -= 1075;
			System.Numerics.BigInteger scaled = new System.Numerics.BigInteger(mantissa) * System.Numerics.BigInteger.Pow(10, digits);
			System.Numerics.BigInteger n;
			if (exponent >= 0)
				n = scaled << exponent;
			else
			{
				System.Numerics.BigInteger divisor = System.Numerics.BigInteger.One << -exponent;
				n = System.Numerics.BigInteger.DivRem(scaled, divisor, out System.Numerics.BigInteger remainder);
				if (remainder * 2 >= divisor)
					n++;
			}
			string text = n.ToString(CultureInfo.InvariantCulture);
			if (digits > 0)
			{
				text = text.PadLeft(digits + 1, '0');
				text = text.Substring(0, text.Length - digits) + "." + text.Substring(text.Length - digits);
			}
			return (negative && n != 0 ? "-" : "") + text;
		}

		private static string ToRadix(double n, int radix)
		{
			if (double.IsNaN(n))
				return "NaN";
			if (double.IsInfinity(n))
				return n > 0 ? "Infinity" : "-Infinity";
			bool negative = n < 0;
			n = Math.Abs(n);
			double whole = Math.Floor(n);
			double fraction = n - whole;
			const string Digits = "0123456789abcdefghijklmnopqrstuvwxyz";
			StringBuilder builder = new StringBuilder();
			if (whole == 0)
				builder.Append('0');
			while (whole >= 1)
			{
				int digit = (int)(whole % radix);
				builder.Insert(0, Digits[digit]);
				whole = Math.Floor(whole / radix);
			}
			if (fraction > 0)
			{
				builder.Append('.');
				for (int i = 0; i < 52 && fraction > 0; i++)
				{
					fraction *= radix;
					int digit = (int)Math.Floor(fraction);
					builder.Append(Digits[digit]);
					fraction -= digit;
				}
			}
			return (negative ? "-" : "") + builder;
		}

		private void SetupBoolean()
		{
			NewConstructor("Boolean", 1, BooleanPrototype,
				(i, t, a) => Arg(a, 0).Truthy,
				(i, a, target) => new JsPrimitiveObject(PrototypeFromConstructor(target, BooleanPrototype), Arg(a, 0).Truthy, "Boolean"));
			DefineMethod(BooleanPrototype, "valueOf", 0, (i, t, a) => ThisBoolean(t));
			DefineMethod(BooleanPrototype, "toString", 0, (i, t, a) => ThisBoolean(t) ? "true" : "false");
		}

		private bool ThisBoolean(JsValue thisValue)
		{
			if (thisValue.IsBoolean)
				return thisValue.Boolean;
			if (thisValue.IsObject && thisValue.Object is JsPrimitiveObject { Primitive: { IsBoolean: true } } wrapper)
				return wrapper.Primitive.Boolean;
			throw TypeError("Boolean.prototype.valueOf requires that 'this' be a Boolean");
		}

		private void SetupMath()
		{
			JsObject math = NewObject();
			math.Class = "Math";
			Global.Put("Math", math, false);
			math.Put(SymbolToStringTag, "Math", false);
			math.Put("PI", Math.PI, false);
			math.Put("E", Math.E, false);
			math.Put("LN2", Math.Log(2), false);
			math.Put("LN10", Math.Log(10), false);
			math.Put("LOG2E", 1 / Math.Log(2), false);
			math.Put("LOG10E", 1 / Math.Log(10), false);
			math.Put("SQRT2", Math.Sqrt(2), false);
			math.Put("SQRT1_2", Math.Sqrt(0.5), false);
			Unary(math, "abs", Math.Abs);
			Unary(math, "floor", Math.Floor);
			Unary(math, "ceil", Math.Ceiling);
			Unary(math, "trunc", Math.Truncate);
			Unary(math, "round", n => double.IsFinite(n) ? Math.Floor(n + 0.5) : n);
			Unary(math, "sign", n => double.IsNaN(n) ? double.NaN : Math.Sign(n));
			Unary(math, "sqrt", Math.Sqrt);
			Unary(math, "cbrt", Math.Cbrt);
			Unary(math, "exp", Math.Exp);
			Unary(math, "expm1", n => Math.Exp(n) - 1);
			Unary(math, "log", Math.Log);
			Unary(math, "log2", Math.Log2);
			Unary(math, "log10", Math.Log10);
			Unary(math, "log1p", n => Math.Log(1 + n));
			Unary(math, "sin", Math.Sin);
			Unary(math, "cos", Math.Cos);
			Unary(math, "tan", Math.Tan);
			Unary(math, "asin", Math.Asin);
			Unary(math, "acos", Math.Acos);
			Unary(math, "atan", Math.Atan);
			Unary(math, "sinh", Math.Sinh);
			Unary(math, "cosh", Math.Cosh);
			Unary(math, "tanh", Math.Tanh);
			Unary(math, "asinh", Math.Asinh);
			Unary(math, "acosh", Math.Acosh);
			Unary(math, "atanh", Math.Atanh);
			Unary(math, "fround", n => (float)n);
			Unary(math, "clz32", n => System.Numerics.BitOperations.LeadingZeroCount(JsValue.FromNumber(n).ToUInt32()));
			DefineMethod(math, "atan2", 2, (i, t, a) => Math.Atan2(Arg(a, 0).ToNumber(), Arg(a, 1).ToNumber()));
			DefineMethod(math, "pow", 2, (i, t, a) => BinaryOperation("**", Arg(a, 0).ToNumber(), Arg(a, 1).ToNumber()));
			DefineMethod(math, "imul", 2, (i, t, a) => (double)(Arg(a, 0).ToInt32() * Arg(a, 1).ToInt32()));
			DefineMethod(math, "hypot", 2, (i, t, a) =>
			{
				double sum = 0;
				foreach (JsValue v in a)
					sum += v.ToNumber() * v.ToNumber();
				return Math.Sqrt(sum);
			});
			DefineMethod(math, "max", 2, (i, t, a) =>
			{
				double result = double.NegativeInfinity;
				foreach (JsValue v in a)
				{
					double n = v.ToNumber();
					if (double.IsNaN(n))
						return double.NaN;
					result = Math.Max(result, n);
				}
				return result;
			});
			DefineMethod(math, "min", 2, (i, t, a) =>
			{
				double result = double.PositiveInfinity;
				foreach (JsValue v in a)
				{
					double n = v.ToNumber();
					if (double.IsNaN(n))
						return double.NaN;
					result = Math.Min(result, n);
				}
				return result;
			});
			Random random = new Random();
			DefineMethod(math, "random", 0, (i, t, a) => random.NextDouble());
		}

		private delegate double MathFunction(double x);

		private void Unary(JsObject math, string name, MathFunction function) => DefineMethod(math, name, 1, (i, t, a) => function(Arg(a, 0).ToNumber()));
	}
}
