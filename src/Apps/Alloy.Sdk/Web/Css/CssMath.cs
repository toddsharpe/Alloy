using System;
using System.Collections.Generic;

namespace Alloy.Sdk.Web.Css
{
	// calc(), min(), max() and clamp() folded into single tokens before a property is applied; lengths and percentages that cannot be combined keep the percentage
	public static class CssMath
	{
		public static bool HasMath(List<CssToken> tokens)
		{
			foreach (CssToken token in tokens)
			{
				if (token.Type == CssTokenType.Function && IsMath(token.Value))
					return true;
			}
			return false;
		}

		private static bool IsMath(string name) => name.ToLowerInvariant() is "calc" or "min" or "max" or "clamp" or "-webkit-calc" or "-moz-calc";

		// A partial sum: pixels, a percentage and a bare number can each be present, as calc(100% - 20px) has both of the first two
		private struct Value
		{
			public double Px;
			public double Percent;
			public double Number;
			public bool HasPx;
			public bool HasPercent;
			public bool HasNumber;
			// 1 or 2 when Px and Percent compete as a min() or max() instead of adding up; such a value cannot be combined further
			public int Mix;

			public bool IsNumber => HasNumber && !HasPx && !HasPercent;

			public static Value operator +(Value a, Value b) => new Value { Px = a.Px + b.Px, Percent = a.Percent + b.Percent, Number = a.Number + b.Number, HasPx = a.HasPx || b.HasPx, HasPercent = a.HasPercent || b.HasPercent, HasNumber = a.HasNumber || b.HasNumber };

			public static Value operator -(Value a, Value b) => a + b.Scale(-1);

			public Value Scale(double factor) => new Value { Px = Px * factor, Percent = Percent * factor, Number = Number * factor, HasPx = HasPx, HasPercent = HasPercent, HasNumber = HasNumber };

			// The single quantity that stands for the value when a comparison is needed: the percentage when there is one, else pixels, else the number
			public double Magnitude => HasPercent ? Percent : HasPx ? Px : Number;
		}

		public static List<CssToken> Collapse(List<CssToken> tokens, double fontSize, double rootFontSize)
		{
			List<CssToken> result = new List<CssToken>(tokens.Count);
			for (int i = 0; i < tokens.Count; i++)
			{
				CssToken token = tokens[i];
				if (token.Type == CssTokenType.Function && IsMath(token.Value))
				{
					int close = MatchingClose(tokens, i);
					if (close > i && TryEvaluateFunction(tokens, i, close, fontSize, rootFontSize, out Value value))
					{
						result.Add(ToToken(value));
						i = close;
						continue;
					}
					// Left as it was, so the declaration fails to parse rather than taking a wrong value
					result.AddRange(tokens.GetRange(i, Math.Max(1, close - i + 1)));
					i = close > i ? close : i;
					continue;
				}
				result.Add(token);
			}
			return result;
		}

		private static CssToken ToToken(Value value)
		{
			if (value.Mix != 0)
				return new CssToken(CssTokenType.Dimension, value.Percent.ToString("R", System.Globalization.CultureInfo.InvariantCulture), value.Px, value.Mix == 1 ? "min" : "max");
			if (value.HasPercent && !value.HasPx)
				return new CssToken(CssTokenType.Percentage, number: value.Percent);
			if (value.HasPx && value.HasPercent)
			{
				// Not expressible as one token: the percentage carries on alone, the pixel offset being the smaller part in practice
				return value.Percent != 0 ? new CssToken(CssTokenType.Percentage, number: value.Percent) : new CssToken(CssTokenType.Dimension, number: value.Px, unit: "px");
			}
			if (value.HasPx)
				return new CssToken(CssTokenType.Dimension, number: value.Px, unit: "px");
			return new CssToken(CssTokenType.Number, number: value.Number, isInteger: Math.Floor(value.Number) == value.Number);
		}

		private static int MatchingClose(List<CssToken> tokens, int open)
		{
			int depth = 0;
			for (int i = open; i < tokens.Count; i++)
			{
				if (tokens[i].Type == CssTokenType.Function || tokens[i].Type == CssTokenType.OpenParen)
					depth++;
				else if (tokens[i].Type == CssTokenType.CloseParen && --depth == 0)
					return i;
			}
			return -1;
		}

		// The function token at open, its arguments up to the matching close paren
		private static bool TryEvaluateFunction(List<CssToken> tokens, int open, int close, double fontSize, double rootFontSize, out Value value)
		{
			value = default;
			string name = tokens[open].Value.ToLowerInvariant();
			List<Value> arguments = new List<Value>();
			int start = open + 1;
			for (int i = open + 1; i <= close; i++)
			{
				if (i == close || (tokens[i].Type == CssTokenType.Comma && Depth(tokens, open + 1, i) == 0))
				{
					Parser parser = new Parser(tokens, start, i, fontSize, rootFontSize);
					if (!parser.TryParse(out Value argument))
						return false;
					arguments.Add(argument);
					start = i + 1;
				}
			}
			if (arguments.Count == 0)
				return false;
			switch (name)
			{
				case "calc":
				case "-webkit-calc":
				case "-moz-calc":
					value = arguments[0];
					return arguments.Count == 1;
				case "min":
					return TryPick(arguments, true, out value);
				case "max":
					return TryPick(arguments, false, out value);
				case "clamp":
					if (arguments.Count != 3)
						return false;
					if (TryPick(new List<Value> { arguments[1], arguments[2] }, true, out Value upper) && upper.Mix == 0 && TryPick(new List<Value> { arguments[0], upper }, false, out value) && value.Mix == 0)
						return true;
					value = Pick(new List<Value> { arguments[0], Pick(new List<Value> { arguments[1], arguments[2] }, true) }, false);
					return true;
			}
			return false;
		}

		private static int Depth(List<CssToken> tokens, int from, int to)
		{
			int depth = 0;
			for (int i = from; i < to; i++)
			{
				if (tokens[i].Type == CssTokenType.Function || tokens[i].Type == CssTokenType.OpenParen)
					depth++;
				else if (tokens[i].Type == CssTokenType.CloseParen)
					depth--;
			}
			return depth;
		}

		// The smallest or largest argument; pixel lengths and percentages compete among their own kind, and when one of each is left the containing size decides at layout
		private static bool TryPick(List<Value> arguments, bool smallest, out Value value)
		{
			value = default;
			Value px = default;
			Value percent = default;
			bool anyPx = false;
			bool anyPercent = false;
			foreach (Value argument in arguments)
			{
				if (argument.Mix != 0)
					return false;
				if (argument.HasPx && !argument.HasPercent)
				{
					if (!anyPx || (smallest ? argument.Px < px.Px : argument.Px > px.Px))
						px = argument;
					anyPx = true;
				}
				else if (argument.HasPercent)
				{
					if (!anyPercent || (smallest ? argument.Percent < percent.Percent : argument.Percent > percent.Percent))
						percent = argument;
					anyPercent = true;
				}
				else
				{
					value = Pick(arguments, smallest);
					return true;
				}
			}
			if (anyPx && anyPercent)
				value = new Value { Px = px.Px, Percent = percent.Percent, HasPx = true, HasPercent = true, Mix = smallest ? 1 : 2 };
			else
				value = anyPx ? px : percent;
			return true;
		}

		// The smallest or largest argument by magnitude; when pixels and percentages are mixed only the pixel ones compete, since the percentage's basis is unknown here
		private static Value Pick(List<Value> arguments, bool smallest)
		{
			bool anyPx = false;
			foreach (Value argument in arguments)
				anyPx |= argument.HasPx && !argument.HasPercent;
			Value best = default;
			bool found = false;
			foreach (Value argument in arguments)
			{
				if (anyPx && (!argument.HasPx || argument.HasPercent))
					continue;
				if (!found || (smallest ? argument.Magnitude < best.Magnitude : argument.Magnitude > best.Magnitude))
				{
					best = argument;
					found = true;
				}
			}
			return best;
		}

		// Sums of products over lengths, percentages and numbers, with nested parentheses and math functions
		private sealed class Parser
		{
			private readonly List<CssToken> _tokens;
			private readonly int _end;
			private readonly double _fontSize;
			private readonly double _rootFontSize;
			private int _at;

			public Parser(List<CssToken> tokens, int start, int end, double fontSize, double rootFontSize)
			{
				_tokens = tokens;
				_at = start;
				_end = end;
				_fontSize = fontSize;
				_rootFontSize = rootFontSize;
			}

			public bool TryParse(out Value value)
			{
				value = default;
				if (!Sum(out value))
					return false;
				SkipSpace();
				return _at >= _end;
			}

			private void SkipSpace()
			{
				while (_at < _end && _tokens[_at].Type == CssTokenType.Whitespace)
					_at++;
			}

			private bool Sum(out Value value)
			{
				if (!Product(out value))
					return false;
				while (true)
				{
					SkipSpace();
					if (_at >= _end || !(_tokens[_at].IsDelim('+') || _tokens[_at].IsDelim('-')))
						return true;
					bool minus = _tokens[_at].IsDelim('-');
					_at++;
					if (!Product(out Value right) || value.Mix != 0 || right.Mix != 0)
						return false;
					value = minus ? value - right : value + right;
				}
			}

			private bool Product(out Value value)
			{
				if (!Factor(out value))
					return false;
				while (true)
				{
					SkipSpace();
					if (_at >= _end || !(_tokens[_at].IsDelim('*') || _tokens[_at].IsDelim('/')))
						return true;
					bool divide = _tokens[_at].IsDelim('/');
					_at++;
					if (!Factor(out Value right) || value.Mix != 0 || right.Mix != 0)
						return false;
					if (divide)
					{
						if (!right.IsNumber || right.Number == 0)
							return false;
						value = value.Scale(1 / right.Number);
					}
					else if (right.IsNumber)
						value = value.Scale(right.Number);
					else if (value.IsNumber)
						value = right.Scale(value.Number);
					else
						return false;
				}
			}

			private bool Factor(out Value value)
			{
				value = default;
				SkipSpace();
				if (_at >= _end)
					return false;
				CssToken token = _tokens[_at];
				switch (token.Type)
				{
					case CssTokenType.Number:
						value = new Value { Number = token.Number, HasNumber = true };
						_at++;
						return true;
					case CssTokenType.Percentage:
						value = new Value { Percent = token.Number, HasPercent = true };
						_at++;
						return true;
					case CssTokenType.Dimension:
						// Container query units stand in for percentages of the containing block, the nearest thing without container tracking
						if (token.Unit is "cqw" or "cqi" or "cqh" or "cqb" or "cqmin" or "cqmax")
							value = new Value { Percent = token.Number, HasPercent = true };
						else
							value = new Value { Px = CssValue.ToPixels(token.Number, token.Unit, _fontSize, _rootFontSize), HasPx = true };
						_at++;
						return true;
					case CssTokenType.OpenParen:
					{
						_at++;
						if (!Sum(out value))
							return false;
						SkipSpace();
						if (_at >= _end || _tokens[_at].Type != CssTokenType.CloseParen)
							return false;
						_at++;
						return true;
					}
					case CssTokenType.Function:
					{
						int close = MatchingClose(_tokens, _at);
						if (close < 0 || close >= _end || !IsMath(token.Value) || !TryEvaluateFunction(_tokens, _at, close, _fontSize, _rootFontSize, out value))
							return false;
						_at = close + 1;
						return true;
					}
					case CssTokenType.Ident:
						if (token.IsIdent("e"))
							value = new Value { Number = Math.E, HasNumber = true };
						else if (token.IsIdent("pi"))
							value = new Value { Number = Math.PI, HasNumber = true };
						else if (token.IsIdent("infinity"))
							value = new Value { Number = double.PositiveInfinity, HasNumber = true };
						else
							return false;
						_at++;
						return true;
					default:
						return false;
				}
			}
		}
	}
}
