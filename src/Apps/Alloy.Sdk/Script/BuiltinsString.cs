using System;
using System.Collections.Generic;
using System.Globalization;
using System.Text;
using System.Text.RegularExpressions;

namespace Alloy.Sdk.Script
{
	// String and RegExp
	public sealed partial class Interpreter
	{
		private void SetupString()
		{
			JsFunction constructor = NewConstructor("String", 1, StringPrototype,
				(i, t, a) => a.Length == 0 ? "" : a[0].IsSymbol ? a[0].Symbol.ToString() : a[0].ToJsString(),
				(i, a, target) => new JsPrimitiveObject(PrototypeFromConstructor(target, StringPrototype), a.Length == 0 ? "" : a[0].ToJsString(), "String"));
			DefineMethod(constructor, "fromCharCode", 1, (i, t, a) =>
			{
				StringBuilder builder = new StringBuilder(a.Length);
				foreach (JsValue code in a)
					builder.Append((char)(code.ToUInt32() & 0xFFFF));
				return builder.ToString();
			});
			DefineMethod(constructor, "fromCodePoint", 1, (i, t, a) =>
			{
				StringBuilder builder = new StringBuilder(a.Length);
				foreach (JsValue code in a)
				{
					double n = code.ToNumber();
					if (n < 0 || n > 0x10FFFF || n != Math.Floor(n))
						throw RangeError("Invalid code point " + code.ToJsString());
					builder.Append(char.ConvertFromUtf32((int)n));
				}
				return builder.ToString();
			});
			DefineMethod(constructor, "raw", 1, (i, t, a) =>
			{
				JsValue raw = GetMember(Arg(a, 0), "raw");
				JsValue[] strings = ArgumentsFromArrayLike(raw);
				StringBuilder builder = new StringBuilder();
				for (int n = 0; n < strings.Length; n++)
				{
					builder.Append(strings[n].ToJsString());
					if (n + 1 < strings.Length && n + 1 < a.Length)
						builder.Append(a[n + 1].ToJsString());
				}
				return builder.ToString();
			});
			DefineMethod(StringPrototype, "toString", 0, (i, t, a) => ThisString(t, "toString"));
			DefineMethod(StringPrototype, "valueOf", 0, (i, t, a) => ThisString(t, "valueOf"));
			DefineMethod(StringPrototype, "charAt", 1, (i, t, a) =>
			{
				string s = ThisString(t, "charAt");
				double n = Math.Truncate(Arg(a, 0).ToNumber());
				return n >= 0 && n < s.Length ? s[(int)n].ToString() : "";
			});
			DefineMethod(StringPrototype, "charCodeAt", 1, (i, t, a) =>
			{
				string s = ThisString(t, "charCodeAt");
				double n = Math.Truncate(Arg(a, 0).ToNumber());
				return n >= 0 && n < s.Length ? s[(int)n] : double.NaN;
			});
			DefineMethod(StringPrototype, "codePointAt", 1, (i, t, a) =>
			{
				string s = ThisString(t, "codePointAt");
				double n = Math.Truncate(Arg(a, 0).ToNumber());
				if (!(n >= 0 && n < s.Length))
					return JsValue.Undefined;
				int index = (int)n;
				return char.IsHighSurrogate(s[index]) && index + 1 < s.Length && char.IsLowSurrogate(s[index + 1]) ? char.ConvertToUtf32(s[index], s[index + 1]) : s[index];
			});
			DefineMethod(StringPrototype, "at", 1, (i, t, a) =>
			{
				string s = ThisString(t, "at");
				double n = Math.Truncate(Arg(a, 0).ToNumber());
				if (n < 0)
					n += s.Length;
				return n >= 0 && n < s.Length ? s[(int)n].ToString() : JsValue.Undefined;
			});
			DefineMethod(StringPrototype, "indexOf", 1, (i, t, a) =>
			{
				string s = ThisString(t, "indexOf");
				int start = Math.Min(s.Length, Math.Max(0, (int)Math.Truncate(Arg(a, 1).IsUndefined ? 0 : Arg(a, 1).ToNumber())));
				return s.IndexOf(Arg(a, 0).ToJsString(), start, StringComparison.Ordinal);
			});
			DefineMethod(StringPrototype, "lastIndexOf", 1, (i, t, a) =>
			{
				string s = ThisString(t, "lastIndexOf");
				string search = Arg(a, 0).ToJsString();
				double from = Arg(a, 1).ToNumber();
				int start = double.IsNaN(from) ? s.Length : (int)Math.Min(s.Length, Math.Max(0, from));
				if (search.Length == 0)
					return start;
				int last = Math.Min(start, s.Length - search.Length);
				if (last < 0)
					return -1;
				return s.LastIndexOf(search, last + search.Length - 1, StringComparison.Ordinal);
			});
			DefineMethod(StringPrototype, "includes", 1, (i, t, a) =>
			{
				string s = ThisString(t, "includes");
				int start = Math.Min(s.Length, Math.Max(0, (int)Math.Truncate(Arg(a, 1).IsUndefined ? 0 : Arg(a, 1).ToNumber())));
				return s.IndexOf(SearchString(Arg(a, 0), "includes"), start, StringComparison.Ordinal) >= 0;
			});
			DefineMethod(StringPrototype, "startsWith", 1, (i, t, a) =>
			{
				string s = ThisString(t, "startsWith");
				string search = SearchString(Arg(a, 0), "startsWith");
				int start = Math.Min(s.Length, Math.Max(0, (int)Math.Truncate(Arg(a, 1).IsUndefined ? 0 : Arg(a, 1).ToNumber())));
				return start + search.Length <= s.Length && string.CompareOrdinal(s, start, search, 0, search.Length) == 0;
			});
			DefineMethod(StringPrototype, "endsWith", 1, (i, t, a) =>
			{
				string s = ThisString(t, "endsWith");
				string search = SearchString(Arg(a, 0), "endsWith");
				int end = Arg(a, 1).IsUndefined ? s.Length : Math.Min(s.Length, Math.Max(0, (int)Math.Truncate(a[1].ToNumber())));
				int start = end - search.Length;
				return start >= 0 && string.CompareOrdinal(s, start, search, 0, search.Length) == 0;
			});
			DefineMethod(StringPrototype, "slice", 2, (i, t, a) =>
			{
				string s = ThisString(t, "slice");
				int start = RelativeIndex(Arg(a, 0), s.Length, 0);
				int end = RelativeIndex(Arg(a, 1), s.Length, s.Length);
				return end > start ? s.Substring(start, end - start) : "";
			});
			DefineMethod(StringPrototype, "substring", 2, (i, t, a) =>
			{
				string s = ThisString(t, "substring");
				int start = ClampIndex(Arg(a, 0), s.Length, 0);
				int end = ClampIndex(Arg(a, 1), s.Length, s.Length);
				if (start > end)
					(start, end) = (end, start);
				return s.Substring(start, end - start);
			});
			DefineMethod(StringPrototype, "substr", 2, (i, t, a) =>
			{
				string s = ThisString(t, "substr");
				int start = RelativeIndex(Arg(a, 0), s.Length, 0);
				int length = Arg(a, 1).IsUndefined ? s.Length - start : (int)Math.Max(0, Math.Min(s.Length - start, a[1].ToNumber()));
				return s.Substring(start, length);
			});
			DefineMethod(StringPrototype, "toUpperCase", 0, (i, t, a) => ThisString(t, "toUpperCase").ToUpperInvariant());
			DefineMethod(StringPrototype, "toLowerCase", 0, (i, t, a) => ThisString(t, "toLowerCase").ToLowerInvariant());
			DefineMethod(StringPrototype, "toLocaleUpperCase", 0, (i, t, a) => ThisString(t, "toLocaleUpperCase").ToUpperInvariant());
			DefineMethod(StringPrototype, "toLocaleLowerCase", 0, (i, t, a) => ThisString(t, "toLocaleLowerCase").ToLowerInvariant());
			DefineMethod(StringPrototype, "trim", 0, (i, t, a) => ThisString(t, "trim").Trim(Whitespace));
			DefineMethod(StringPrototype, "trimStart", 0, (i, t, a) => ThisString(t, "trimStart").TrimStart(Whitespace));
			DefineMethod(StringPrototype, "trimEnd", 0, (i, t, a) => ThisString(t, "trimEnd").TrimEnd(Whitespace));
			StringPrototype.Put("trimLeft", StringPrototype.Get("trimStart", this), false);
			StringPrototype.Put("trimRight", StringPrototype.Get("trimEnd", this), false);
			DefineMethod(StringPrototype, "padStart", 2, (i, t, a) => Pad(ThisString(t, "padStart"), a, true));
			DefineMethod(StringPrototype, "padEnd", 2, (i, t, a) => Pad(ThisString(t, "padEnd"), a, false));
			DefineMethod(StringPrototype, "repeat", 1, (i, t, a) =>
			{
				string s = ThisString(t, "repeat");
				double count = Arg(a, 0).ToNumber();
				if (count < 0 || double.IsInfinity(count) || count * s.Length > 100_000_000)
					throw RangeError("Invalid count value: " + Arg(a, 0).ToJsString());
				StringBuilder builder = new StringBuilder();
				for (int n = 0; n < (int)count; n++)
					builder.Append(s);
				return builder.ToString();
			});
			DefineMethod(StringPrototype, "concat", 1, (i, t, a) =>
			{
				StringBuilder builder = new StringBuilder(ThisString(t, "concat"));
				foreach (JsValue v in a)
					builder.Append(v.ToJsString());
				return builder.ToString();
			});
			DefineMethod(StringPrototype, "localeCompare", 1, (i, t, a) => Math.Sign(string.Compare(ThisString(t, "localeCompare"), Arg(a, 0).ToJsString(), StringComparison.InvariantCulture)));
			DefineMethod(StringPrototype, "normalize", 0, (i, t, a) => ThisString(t, "normalize"));
			DefineMethod(StringPrototype, "split", 2, (i, t, a) => Split(ThisString(t, "split"), Arg(a, 0), Arg(a, 1)));
			DefineMethod(StringPrototype, "replace", 2, (i, t, a) => Replace(ThisString(t, "replace"), Arg(a, 0), Arg(a, 1), false));
			DefineMethod(StringPrototype, "replaceAll", 2, (i, t, a) => Replace(ThisString(t, "replaceAll"), Arg(a, 0), Arg(a, 1), true));
			DefineMethod(StringPrototype, "match", 1, (i, t, a) => Match(ThisString(t, "match"), Arg(a, 0)));
			DefineMethod(StringPrototype, "matchAll", 1, (i, t, a) => MatchAll(ThisString(t, "matchAll"), Arg(a, 0)));
			DefineMethod(StringPrototype, "search", 1, (i, t, a) =>
			{
				JsRegExp regexp = AsRegExp(Arg(a, 0), "");
				System.Text.RegularExpressions.Match m = regexp.Regex.Match(ThisString(t, "search"));
				return m.Success ? m.Index : -1;
			});
			DefineMethod(StringPrototype, SymbolIterator, 0, (i, t, a) => NewArrayIterator(NewArray(Iterate(ThisString(t, "iterator"))), ArrayIterationKind.Values));
			foreach (string tag in new[] { "anchor", "big", "blink", "bold", "fixed", "italics", "small", "strike", "sub", "sup" })
				DefineMethod(StringPrototype, tag, 0, (i, t, a) => ThisString(t, tag));
			DefineMethod(StringPrototype, "toWellFormed", 0, (i, t, a) => ThisString(t, "toWellFormed"));
			DefineMethod(StringPrototype, "isWellFormed", 0, (i, t, a) => true);
		}

		private static readonly char[] Whitespace = { ' ', '\t', '\n', '\r', '\v', '\f', '\u00A0', '\u1680', '\u2000', '\u2001', '\u2002', '\u2003', '\u2004', '\u2005', '\u2006', '\u2007', '\u2008', '\u2009', '\u200A', '\u2028', '\u2029', '\u202F', '\u205F', '\u3000', '\uFEFF' };

		private string ThisString(JsValue thisValue, string method)
		{
			if (thisValue.IsString)
				return thisValue.String;
			if (thisValue.IsObject && thisValue.Object is JsPrimitiveObject { Primitive: { IsString: true } } wrapper)
				return wrapper.Primitive.String;
			if (thisValue.IsNullish)
				throw TypeError("String.prototype." + method + " called on null or undefined");
			return thisValue.ToJsString();
		}

		private string SearchString(JsValue value, string method)
		{
			if (value.IsObject && value.Object is JsRegExp)
				throw TypeError("First argument to String.prototype." + method + " must not be a regular expression");
			return value.ToJsString();
		}

		private static int ClampIndex(JsValue value, int length, int whenUndefined)
		{
			if (value.IsUndefined)
				return whenUndefined;
			double n = value.ToNumber();
			if (double.IsNaN(n))
				return 0;
			return (int)Math.Max(0, Math.Min(length, Math.Truncate(n)));
		}

		private string Pad(string s, JsValue[] arguments, bool atStart)
		{
			int target = (int)Math.Min(100_000_000, Math.Max(0, Arg(arguments, 0).ToNumber()));
			string filler = Arg(arguments, 1).IsUndefined ? " " : arguments[1].ToJsString();
			if (target <= s.Length || filler.Length == 0)
				return s;
			StringBuilder padding = new StringBuilder();
			while (padding.Length < target - s.Length)
				padding.Append(filler);
			padding.Length = target - s.Length;
			return atStart ? padding + s : s + padding;
		}

		private JsValue Split(string s, JsValue separator, JsValue limitValue)
		{
			int limit = limitValue.IsUndefined ? int.MaxValue : (int)limitValue.ToUInt32();
			JsArray result = NewArray();
			if (limit == 0)
				return result;
			if (separator.IsUndefined)
			{
				result.Elements.Add(s);
				return result;
			}
			if (separator.IsObject && separator.Object is JsRegExp regexp)
			{
				if (s.Length == 0)
				{
					if (!regexp.Regex.IsMatch(s))
						result.Elements.Add(s);
					return result;
				}
				int last = 0;
				foreach (System.Text.RegularExpressions.Match m in regexp.Regex.Matches(s))
				{
					if (m.Length == 0 && (m.Index == 0 || m.Index >= s.Length))
						continue;
					if (m.Length == 0 && m.Index == last)
						continue;
					result.Elements.Add(s.Substring(last, m.Index - last));
					if (result.Elements.Count >= limit)
						return result;
					for (int g = 1; g < m.Groups.Count; g++)
					{
						result.Elements.Add(m.Groups[g].Success ? m.Groups[g].Value : JsValue.Undefined);
						if (result.Elements.Count >= limit)
							return result;
					}
					last = m.Index + m.Length;
				}
				result.Elements.Add(s.Substring(last));
				return result;
			}
			string sep = separator.ToJsString();
			if (sep.Length == 0)
			{
				for (int i = 0; i < s.Length && result.Elements.Count < limit; i++)
					result.Elements.Add(s[i].ToString());
				return result;
			}
			foreach (string part in s.Split(sep))
			{
				if (result.Elements.Count >= limit)
					break;
				result.Elements.Add(part);
			}
			return result;
		}

		private JsValue Replace(string s, JsValue pattern, JsValue replacement, bool all)
		{
			JsFunction replacer = replacement.IsCallable ? replacement.Function : null;
			string template = replacer == null ? replacement.ToJsString() : null;
			if (pattern.IsObject && pattern.Object is JsRegExp regexp)
			{
				if (all && !regexp.Global)
					throw TypeError("replaceAll must be called with a global RegExp");
				StringBuilder builder = new StringBuilder();
				int last = 0;
				int count = 0;
				foreach (System.Text.RegularExpressions.Match m in regexp.Regex.Matches(s))
				{
					if (!regexp.Global && count > 0)
						break;
					builder.Append(s, last, m.Index - last);
					builder.Append(replacer != null ? CallReplacer(replacer, m, s) : ExpandTemplate(template, m, s));
					last = m.Index + m.Length;
					count++;
				}
				if (regexp.Global)
					regexp.Put("lastIndex", 0);
				builder.Append(s, last, s.Length - last);
				return builder.ToString();
			}
			string search = pattern.ToJsString();
			StringBuilder output = new StringBuilder();
			int from = 0;
			while (true)
			{
				int index = s.IndexOf(search, from, StringComparison.Ordinal);
				if (index < 0 || from > s.Length)
					break;
				output.Append(s, from, index - from);
				if (replacer != null)
					output.Append(Call(replacer, JsValue.Undefined, new JsValue[] { search, index, s }).ToJsString());
				else
					output.Append(ExpandTemplate(template, search, index, s, null));
				from = index + search.Length;
				if (!all)
					break;
				if (search.Length == 0)
				{
					if (index < s.Length)
						output.Append(s[index]);
					from = index + 1;
				}
			}
			if (from <= s.Length)
				output.Append(s, from, s.Length - from);
			return output.ToString();
		}

		private string CallReplacer(JsFunction replacer, System.Text.RegularExpressions.Match m, string input)
		{
			List<JsValue> arguments = new List<JsValue> { m.Value };
			for (int g = 1; g < m.Groups.Count; g++)
				arguments.Add(m.Groups[g].Success ? m.Groups[g].Value : JsValue.Undefined);
			arguments.Add(m.Index);
			arguments.Add(input);
			JsObject named = NamedGroups(m);
			if (named != null)
				arguments.Add(named);
			return Call(replacer, JsValue.Undefined, arguments.ToArray()).ToJsString();
		}

		private JsObject NamedGroups(System.Text.RegularExpressions.Match m)
		{
			JsObject named = null;
			foreach (Group group in m.Groups)
			{
				if (group.Name.Length > 0 && !char.IsDigit(group.Name[0]))
				{
					named ??= NewObject();
					named.Put(group.Name, group.Success ? group.Value : JsValue.Undefined);
				}
			}
			return named;
		}

		private string ExpandTemplate(string template, System.Text.RegularExpressions.Match m, string input)
		{
			string[] groups = new string[m.Groups.Count];
			for (int g = 0; g < groups.Length; g++)
				groups[g] = m.Groups[g].Success ? m.Groups[g].Value : null;
			return ExpandTemplate(template, m.Value, m.Index, input, groups, m);
		}

		// $& $1 $<name> $` $' and $$ in a replacement string
		private static string ExpandTemplate(string template, string matched, int position, string input, string[] groups, System.Text.RegularExpressions.Match m = null)
		{
			if (template.IndexOf('$') < 0)
				return template;
			StringBuilder builder = new StringBuilder();
			for (int i = 0; i < template.Length; i++)
			{
				char c = template[i];
				if (c != '$' || i + 1 >= template.Length)
				{
					builder.Append(c);
					continue;
				}
				char next = template[i + 1];
				switch (next)
				{
					case '$':
						builder.Append('$');
						i++;
						break;
					case '&':
						builder.Append(matched);
						i++;
						break;
					case '`':
						builder.Append(input, 0, position);
						i++;
						break;
					case '\'':
						builder.Append(input, Math.Min(input.Length, position + matched.Length), Math.Max(0, input.Length - position - matched.Length));
						i++;
						break;
					case '<':
					{
						int close = template.IndexOf('>', i + 2);
						if (close < 0 || m == null)
						{
							builder.Append(c);
							break;
						}
						Group group = m.Groups[template.Substring(i + 2, close - i - 2)];
						if (group.Success)
							builder.Append(group.Value);
						i = close;
						break;
					}
					default:
						if (groups != null && char.IsDigit(next))
						{
							int digits = i + 2 < template.Length && char.IsDigit(template[i + 2]) && int.Parse(template.Substring(i + 1, 2), CultureInfo.InvariantCulture) < groups.Length ? 2 : 1;
							int index = int.Parse(template.Substring(i + 1, digits), CultureInfo.InvariantCulture);
							if (index > 0 && index < groups.Length)
							{
								builder.Append(groups[index]);
								i += digits;
								break;
							}
						}
						builder.Append(c);
						break;
				}
			}
			return builder.ToString();
		}

		private JsValue Match(string s, JsValue pattern)
		{
			JsRegExp regexp = AsRegExp(pattern, "");
			if (!regexp.Global)
				return RegExpExec(regexp, s);
			JsArray result = NewArray();
			foreach (System.Text.RegularExpressions.Match m in regexp.Regex.Matches(s))
				result.Elements.Add(m.Value);
			regexp.Put("lastIndex", 0);
			return result.Elements.Count == 0 ? JsValue.Null : result;
		}

		private JsValue MatchAll(string s, JsValue pattern)
		{
			JsRegExp regexp = AsRegExp(pattern, "g");
			if (!regexp.Global)
				throw TypeError("matchAll must be called with a global RegExp");
			JsArray matches = NewArray();
			foreach (System.Text.RegularExpressions.Match m in regexp.Regex.Matches(s))
				matches.Elements.Add(MatchResult(m, s));
			return NewArrayIterator(matches, ArrayIterationKind.Values);
		}

		private JsRegExp AsRegExp(JsValue value, string flags)
		{
			if (value.IsObject && value.Object is JsRegExp regexp)
				return regexp;
			return NewRegExp(value.IsUndefined ? "(?:)" : Regex.Escape(value.ToJsString()), flags);
		}

		// RegExp

		private void SetupRegExp()
		{
			JsFunction constructor = NewConstructor("RegExp", 2, RegExpPrototype, (i, t, a) => RegExpFrom(a, null), (i, a, target) => RegExpFrom(a, target));
			DefineMethod(RegExpPrototype, "exec", 1, (i, t, a) => RegExpExec(ThisRegExp(t), Arg(a, 0).ToJsString()));
			DefineMethod(RegExpPrototype, "test", 1, (i, t, a) => !RegExpExec(ThisRegExp(t), Arg(a, 0).ToJsString()).IsNull);
			DefineMethod(RegExpPrototype, "toString", 0, (i, t, a) => "/" + GetMember(t, "source").ToJsString() + "/" + GetMember(t, "flags").ToJsString());
			DefineGetter(RegExpPrototype, "source", (i, t, a) => t.IsObject && t.Object is JsRegExp r ? r.Source : "(?:)");
			DefineGetter(RegExpPrototype, "flags", (i, t, a) => t.IsObject && t.Object is JsRegExp r ? r.Flags : "");
			DefineGetter(RegExpPrototype, "global", (i, t, a) => t.IsObject && t.Object is JsRegExp r ? r.Flags.Contains('g') : JsValue.Undefined);
			DefineGetter(RegExpPrototype, "ignoreCase", (i, t, a) => t.IsObject && t.Object is JsRegExp r ? r.Flags.Contains('i') : JsValue.Undefined);
			DefineGetter(RegExpPrototype, "multiline", (i, t, a) => t.IsObject && t.Object is JsRegExp r ? r.Flags.Contains('m') : JsValue.Undefined);
			DefineGetter(RegExpPrototype, "dotAll", (i, t, a) => t.IsObject && t.Object is JsRegExp r ? r.Flags.Contains('s') : JsValue.Undefined);
			DefineGetter(RegExpPrototype, "unicode", (i, t, a) => t.IsObject && t.Object is JsRegExp r ? r.Flags.Contains('u') : JsValue.Undefined);
			DefineGetter(RegExpPrototype, "sticky", (i, t, a) => t.IsObject && t.Object is JsRegExp r ? r.Flags.Contains('y') : JsValue.Undefined);
			DefineGetter(RegExpPrototype, "hasIndices", (i, t, a) => t.IsObject && t.Object is JsRegExp r ? r.Flags.Contains('d') : JsValue.Undefined);
			DefineMethod(RegExpPrototype, "@@replace", 2, (i, t, a) => Replace(Arg(a, 0).ToJsString(), t, Arg(a, 1), false));
			DefineMethod(RegExpPrototype, "@@split", 2, (i, t, a) => Split(Arg(a, 0).ToJsString(), t, Arg(a, 1)));
			DefineMethod(RegExpPrototype, "@@match", 1, (i, t, a) => Match(Arg(a, 0).ToJsString(), t));
			DefineMethod(RegExpPrototype, "@@matchAll", 1, (i, t, a) => MatchAll(Arg(a, 0).ToJsString(), t));
			DefineMethod(RegExpPrototype, "@@search", 1, (i, t, a) =>
			{
				System.Text.RegularExpressions.Match m = ThisRegExp(t).Regex.Match(Arg(a, 0).ToJsString());
				return m.Success ? m.Index : -1;
			});
			DefineMethod(constructor, "escape", 1, (i, t, a) => Regex.Escape(Arg(a, 0).ToJsString()));
		}

		private JsValue RegExpFrom(JsValue[] arguments, JsObject newTarget)
		{
			JsValue pattern = Arg(arguments, 0);
			JsValue flags = Arg(arguments, 1);
			if (pattern.IsObject && pattern.Object is JsRegExp source)
			{
				if (newTarget == null && flags.IsUndefined)
					return source;
				return NewRegExp(source.Source, flags.IsUndefined ? source.Flags : flags.ToJsString(), newTarget);
			}
			return NewRegExp(pattern.IsUndefined ? "(?:)" : pattern.ToJsString(), flags.IsUndefined ? "" : flags.ToJsString(), newTarget);
		}

		public JsRegExp NewRegExp(string pattern, string flags, JsObject newTarget = null)
		{
			RegexOptions options = RegexOptions.None;
			foreach (char flag in flags)
			{
				switch (flag)
				{
					case 'i':
						options |= RegexOptions.IgnoreCase;
						break;
					case 'm':
						options |= RegexOptions.Multiline;
						break;
					case 's':
						options |= RegexOptions.Singleline;
						break;
					case 'g':
					case 'u':
					case 'y':
					case 'd':
					case 'v':
						break;
					default:
						throw SyntaxErrorThrow("Invalid regular expression flags");
				}
			}
			Regex regex;
			try
			{
				regex = new Regex(TranslatePattern(pattern, flags.Contains('m')), options);
			}
			catch (ArgumentException e)
			{
				throw SyntaxErrorThrow("Invalid regular expression: /" + pattern + "/: " + e.Message);
			}
			JsRegExp result = new JsRegExp(PrototypeFromConstructor(newTarget, RegExpPrototype), regex, pattern, flags);
			result.Put("lastIndex", 0, false);
			return result;
		}

		// JavaScript pattern syntax to .NET: ASCII classes outside brackets (inside, .NET's own keep ranges like [\\w-$] legal), the string-end anchor, [^] and \u{...}
		private static string TranslatePattern(string pattern, bool multiline)
		{
			StringBuilder builder = new StringBuilder(pattern.Length + 8);
			bool inClass = false;
			for (int i = 0; i < pattern.Length; i++)
			{
				char c = pattern[i];
				if (c == '\\' && i + 1 < pattern.Length)
				{
					char next = pattern[i + 1];
					i++;
					switch (next)
					{
						case 'd':
							builder.Append(inClass ? "\\d" : "[0-9]");
							break;
						case 'D':
							builder.Append(inClass ? "\\D" : "[^0-9]");
							break;
						case 'w':
							builder.Append(inClass ? "\\w" : "[a-zA-Z0-9_]");
							break;
						case 'W':
							builder.Append(inClass ? "\\W" : "[^a-zA-Z0-9_]");
							break;
						case 'u' when i + 1 < pattern.Length && pattern[i + 1] == '{':
						{
							int close = pattern.IndexOf('}', i + 1);
							if (close < 0)
								throw new ArgumentException("bad \\u{} escape");
							int code = int.Parse(pattern.AsSpan(i + 2, close - i - 2), NumberStyles.HexNumber, CultureInfo.InvariantCulture);
							string text = char.ConvertFromUtf32(code);
							foreach (char ch in text)
								builder.Append("\\u").Append(((int)ch).ToString("X4", CultureInfo.InvariantCulture));
							i = close;
							break;
						}
						case '/':
							builder.Append('/');
							break;
						case '-':
							builder.Append("\\-");
							break;
						default:
							builder.Append('\\').Append(next);
							break;
					}
					continue;
				}
				if (inClass)
				{
					if (c == ']')
						inClass = false;
					else if (c == '[')
					{
						builder.Append("\\[");
						continue;
					}
					builder.Append(c);
					continue;
				}
				switch (c)
				{
					case '[':
						if (i + 2 < pattern.Length && pattern[i + 1] == '^' && pattern[i + 2] == ']')
						{
							builder.Append("[\\s\\S]");
							i += 2;
							break;
						}
						if (i + 1 < pattern.Length && pattern[i + 1] == ']')
						{
							builder.Append("(?!)");
							i++;
							break;
						}
						inClass = true;
						builder.Append('[');
						break;
					case '$':
						builder.Append(multiline ? "(?=\\n|\\r|\\u2028|\\u2029|\\z)" : "\\z");
						break;
					case '(':
						if (i + 2 < pattern.Length && pattern[i + 1] == '?' && pattern[i + 2] == '<' && i + 3 < pattern.Length && pattern[i + 3] != '=' && pattern[i + 3] != '!')
							builder.Append("(?<");
						else
							builder.Append('(');
						if (builder[builder.Length - 1] == '<')
							i += 2;
						break;
					default:
						builder.Append(c);
						break;
				}
			}
			return builder.ToString();
		}

		private JsRegExp ThisRegExp(JsValue thisValue)
		{
			if (thisValue.IsObject && thisValue.Object is JsRegExp regexp)
				return regexp;
			throw TypeError("RegExp method called on incompatible receiver");
		}

		public JsValue RegExpExec(JsRegExp regexp, string input)
		{
			bool useLastIndex = regexp.Global || regexp.Sticky;
			int lastIndex = useLastIndex ? (int)Math.Max(0, regexp.Get("lastIndex", this).ToNumber()) : 0;
			if (lastIndex > input.Length)
			{
				regexp.Put("lastIndex", 0);
				return JsValue.Null;
			}
			System.Text.RegularExpressions.Match m = regexp.Regex.Match(input, lastIndex);
			if (!m.Success || regexp.Sticky && m.Index != lastIndex)
			{
				if (useLastIndex)
					regexp.Put("lastIndex", 0);
				return JsValue.Null;
			}
			if (useLastIndex)
				regexp.Put("lastIndex", m.Index + m.Length);
			return MatchResult(m, input);
		}

		private JsArray MatchResult(System.Text.RegularExpressions.Match m, string input)
		{
			JsArray result = NewArray();
			result.Elements.Add(m.Value);
			for (int g = 1; g < m.Groups.Count; g++)
				result.Elements.Add(m.Groups[g].Success ? m.Groups[g].Value : JsValue.Undefined);
			result.Put("index", m.Index);
			result.Put("input", input);
			JsObject groups = NamedGroups(m);
			result.Put("groups", groups == null ? JsValue.Undefined : groups);
			return result;
		}
	}

	// A regular expression object: the compiled .NET pattern with the source and flags scripts see
	public sealed class JsRegExp : JsObject
	{
		public readonly Regex Regex;
		public readonly string Source;
		public readonly string Flags;

		public JsRegExp(JsObject prototype, Regex regex, string source, string flags) : base(prototype)
		{
			Class = "RegExp";
			Regex = regex;
			Source = source;
			Flags = flags;
		}

		public bool Global => Flags.Contains('g');

		public bool Sticky => Flags.Contains('y');

		public override JsValue ToPrimitive(bool preferNumber) => "/" + Source + "/" + Flags;
	}
}
