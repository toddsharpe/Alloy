using System;
using System.Collections.Generic;
using System.Globalization;
using System.Text;

namespace Alloy.Sdk.Script
{
	// JSON.parse and JSON.stringify
	public sealed partial class Interpreter
	{
		private void SetupJson()
		{
			JsObject json = NewObject();
			json.Class = "JSON";
			Global.Put("JSON", json, false);
			json.Put(SymbolToStringTag, "JSON", false);
			DefineMethod(json, "parse", 2, (i, t, a) =>
			{
				JsonReader reader = new JsonReader(this, Arg(a, 0).ToJsString());
				JsValue value = reader.Parse();
				return Arg(a, 1).IsCallable ? Revive(NewObject(), "", value, a[1].Function) : value;
			});
			DefineMethod(json, "stringify", 3, (i, t, a) =>
			{
				JsonWriter writer = new JsonWriter(this, Arg(a, 1), Arg(a, 2));
				JsObject holder = NewObject();
				holder.Put("", Arg(a, 0));
				return writer.Write(holder, "", Arg(a, 0), 0, out bool present) && present ? writer.Result : JsValue.Undefined;
			});
		}

		private JsValue Revive(JsObject holder, string key, JsValue value, JsFunction reviver)
		{
			holder.Put(key, value);
			if (value.IsObject && value.Object is not JsFunction)
			{
				JsObject obj = value.Object;
				if (obj is JsArray array)
				{
					for (int i = 0; i < array.Elements.Count; i++)
						array.Elements[i] = Revive(obj, i.ToString(CultureInfo.InvariantCulture), array.Elements[i], reviver);
				}
				else
				{
					foreach (string k in OwnEnumerableKeys(obj))
					{
						JsValue revived = Revive(obj, k, obj.Get(k, this), reviver);
						if (revived.IsUndefined)
							obj.Delete(k);
						else
							obj.Put(k, revived);
					}
				}
			}
			return Call(reviver, holder, new JsValue[] { key, holder.Get(key, this) });
		}

		private sealed class JsonReader
		{
			private readonly Interpreter _interpreter;
			private readonly string _text;
			private int _at;

			public JsonReader(Interpreter interpreter, string text)
			{
				_interpreter = interpreter;
				_text = text;
			}

			public JsValue Parse()
			{
				JsValue value = ReadValue();
				SkipWhitespace();
				if (_at < _text.Length)
					throw Error("Unexpected non-whitespace character after JSON");
				return value;
			}

			private JsThrow Error(string message) => _interpreter.SyntaxErrorThrow(message + " at position " + _at + " in JSON");

			private void SkipWhitespace()
			{
				while (_at < _text.Length && (_text[_at] == ' ' || _text[_at] == '\t' || _text[_at] == '\n' || _text[_at] == '\r'))
					_at++;
			}

			private JsValue ReadValue()
			{
				SkipWhitespace();
				if (_at >= _text.Length)
					throw Error("Unexpected end of JSON input");
				char c = _text[_at];
				switch (c)
				{
					case '{':
						return ReadObject();
					case '[':
						return ReadArray();
					case '"':
						return ReadString();
					case 't':
						Expect("true");
						return JsValue.True;
					case 'f':
						Expect("false");
						return JsValue.False;
					case 'n':
						Expect("null");
						return JsValue.Null;
					default:
						if (c == '-' || c >= '0' && c <= '9')
							return ReadNumber();
						throw Error("Unexpected token " + c);
				}
			}

			private void Expect(string word)
			{
				if (string.CompareOrdinal(_text, _at, word, 0, word.Length) != 0)
					throw Error("Unexpected token " + _text[_at]);
				_at += word.Length;
			}

			private JsValue ReadNumber()
			{
				int start = _at;
				if (_text[_at] == '-')
					_at++;
				while (_at < _text.Length && (char.IsDigit(_text[_at]) || _text[_at] == '.' || _text[_at] == 'e' || _text[_at] == 'E' || _text[_at] == '+' || _text[_at] == '-'))
					_at++;
				if (!double.TryParse(_text.AsSpan(start, _at - start), NumberStyles.Float, CultureInfo.InvariantCulture, out double value))
					throw Error("Bad number");
				return value;
			}

			private string ReadString()
			{
				_at++;
				StringBuilder builder = null;
				int start = _at;
				while (true)
				{
					if (_at >= _text.Length)
						throw Error("Unterminated string");
					char c = _text[_at];
					if (c == '"')
					{
						string result = builder == null ? _text.Substring(start, _at - start) : builder.Append(_text, start, _at - start).ToString();
						_at++;
						return result;
					}
					if (c < ' ')
						throw Error("Bad control character in string literal");
					if (c != '\\')
					{
						_at++;
						continue;
					}
					builder ??= new StringBuilder();
					builder.Append(_text, start, _at - start);
					_at++;
					if (_at >= _text.Length)
						throw Error("Unterminated string");
					char escape = _text[_at++];
					switch (escape)
					{
						case '"':
						case '\\':
						case '/':
							builder.Append(escape);
							break;
						case 'b':
							builder.Append('\b');
							break;
						case 'f':
							builder.Append('\f');
							break;
						case 'n':
							builder.Append('\n');
							break;
						case 'r':
							builder.Append('\r');
							break;
						case 't':
							builder.Append('\t');
							break;
						case 'u':
							if (_at + 4 > _text.Length || !int.TryParse(_text.AsSpan(_at, 4), NumberStyles.HexNumber, CultureInfo.InvariantCulture, out int code))
								throw Error("Bad Unicode escape");
							builder.Append((char)code);
							_at += 4;
							break;
						default:
							throw Error("Bad escaped character");
					}
					start = _at;
				}
			}

			private JsValue ReadArray()
			{
				_at++;
				JsArray array = _interpreter.NewArray();
				SkipWhitespace();
				if (_at < _text.Length && _text[_at] == ']')
				{
					_at++;
					return array;
				}
				while (true)
				{
					array.Elements.Add(ReadValue());
					SkipWhitespace();
					if (_at >= _text.Length)
						throw Error("Unexpected end of JSON input");
					char c = _text[_at++];
					if (c == ']')
						return array;
					if (c != ',')
						throw Error("Expected ',' or ']' after array element");
				}
			}

			private JsValue ReadObject()
			{
				_at++;
				JsObject obj = _interpreter.NewObject();
				SkipWhitespace();
				if (_at < _text.Length && _text[_at] == '}')
				{
					_at++;
					return obj;
				}
				while (true)
				{
					SkipWhitespace();
					if (_at >= _text.Length || _text[_at] != '"')
						throw Error("Expected property name");
					string key = ReadString();
					SkipWhitespace();
					if (_at >= _text.Length || _text[_at] != ':')
						throw Error("Expected ':' after property name");
					_at++;
					JsValue value = ReadValue();
					if (key == "__proto__")
						obj.DefineOwn(key, new JsProperty { Value = value });
					else
						obj.Put(key, value);
					SkipWhitespace();
					if (_at >= _text.Length)
						throw Error("Unexpected end of JSON input");
					char c = _text[_at++];
					if (c == '}')
						return obj;
					if (c != ',')
						throw Error("Expected ',' or '}' after property value");
				}
			}
		}

		private sealed class JsonWriter
		{
			private readonly Interpreter _interpreter;
			private readonly JsFunction _replacer;
			private readonly HashSet<string> _allowed;
			private readonly string _indent;
			private readonly StringBuilder _builder = new StringBuilder();
			private readonly List<JsObject> _stack = new List<JsObject>();

			public JsonWriter(Interpreter interpreter, JsValue replacer, JsValue space)
			{
				_interpreter = interpreter;
				if (replacer.IsCallable)
					_replacer = replacer.Function;
				else if (replacer.IsObject && replacer.Object is JsArray list)
				{
					_allowed = new HashSet<string>(StringComparer.Ordinal);
					foreach (JsValue item in list.Elements)
					{
						if (item.IsString || item.IsNumber)
							_allowed.Add(item.ToJsString());
					}
				}
				if (space.IsObject && space.Object is JsPrimitiveObject wrapper)
					space = wrapper.Primitive;
				if (space.IsNumber)
					_indent = new string(' ', (int)Math.Max(0, Math.Min(10, space.Number)));
				else if (space.IsString)
					_indent = space.String.Length > 10 ? space.String.Substring(0, 10) : space.String;
				else
					_indent = "";
			}

			public string Result => _builder.ToString();

			// Writes one property value; false when the value is skipped (undefined, functions, symbols)
			public bool Write(JsObject holder, string key, JsValue value, int depth, out bool present)
			{
				present = true;
				if (value.IsObject)
				{
					JsValue toJson = value.Object.Get("toJSON", _interpreter, value);
					if (toJson.IsCallable)
						value = _interpreter.Call(toJson.Function, value, new JsValue[] { key });
				}
				if (_replacer != null)
					value = _interpreter.Call(_replacer, holder, new JsValue[] { key, value });
				if (value.IsObject && value.Object is JsPrimitiveObject wrapper && wrapper.Class != "Date")
					value = wrapper.Primitive;
				switch (value.Type)
				{
					case JsType.Null:
						_builder.Append("null");
						return true;
					case JsType.Boolean:
						_builder.Append(value.Boolean ? "true" : "false");
						return true;
					case JsType.Number:
						_builder.Append(double.IsFinite(value.Number) ? JsValue.NumberToString(value.Number) : "null");
						return true;
					case JsType.String:
						Quote(value.String);
						return true;
					case JsType.Object:
						if (value.IsCallable || value.IsSymbol)
						{
							present = false;
							return false;
						}
						WriteObject(value.Object, depth);
						return true;
					default:
						present = false;
						return false;
				}
			}

			private void WriteObject(JsObject obj, int depth)
			{
				if (_stack.Contains(obj))
					throw _interpreter.TypeError("Converting circular structure to JSON");
				_stack.Add(obj);
				string newline = _indent.Length > 0 ? "\n" + Repeat(depth + 1) : "";
				string closing = _indent.Length > 0 ? "\n" + Repeat(depth) : "";
				if (obj is JsArray array)
				{
					_builder.Append('[');
					for (int i = 0; i < array.Elements.Count; i++)
					{
						_builder.Append(i > 0 ? "," : "").Append(newline);
						int before = _builder.Length;
						if (!Write(obj, i.ToString(CultureInfo.InvariantCulture), array.Elements[i], depth + 1, out _))
						{
							_builder.Length = before;
							_builder.Append("null");
						}
					}
					_builder.Append(array.Elements.Count > 0 ? closing : "").Append(']');
				}
				else
				{
					_builder.Append('{');
					bool any = false;
					foreach (string key in _interpreter.OwnEnumerableKeys(obj))
					{
						if (_allowed != null && !_allowed.Contains(key))
							continue;
						int before = _builder.Length;
						_builder.Append(any ? "," : "").Append(newline);
						Quote(key);
						_builder.Append(_indent.Length > 0 ? ": " : ":");
						if (Write(obj, key, obj.Get(key, _interpreter), depth + 1, out _))
							any = true;
						else
							_builder.Length = before;
					}
					_builder.Append(any ? closing : "").Append('}');
				}
				_stack.RemoveAt(_stack.Count - 1);
			}

			private string Repeat(int depth)
			{
				StringBuilder builder = new StringBuilder();
				for (int i = 0; i < depth; i++)
					builder.Append(_indent);
				return builder.ToString();
			}

			private void Quote(string s)
			{
				_builder.Append('"');
				foreach (char c in s)
				{
					switch (c)
					{
						case '"':
							_builder.Append("\\\"");
							break;
						case '\\':
							_builder.Append("\\\\");
							break;
						case '\b':
							_builder.Append("\\b");
							break;
						case '\f':
							_builder.Append("\\f");
							break;
						case '\n':
							_builder.Append("\\n");
							break;
						case '\r':
							_builder.Append("\\r");
							break;
						case '\t':
							_builder.Append("\\t");
							break;
						default:
							if (c < ' ' || char.IsSurrogate(c) && !IsPaired(s, c))
								_builder.Append("\\u").Append(((int)c).ToString("x4", CultureInfo.InvariantCulture));
							else
								_builder.Append(c);
							break;
					}
				}
				_builder.Append('"');
			}

			private static bool IsPaired(string s, char c)
			{
				int index = s.IndexOf(c);
				return char.IsHighSurrogate(c) ? index + 1 < s.Length && char.IsLowSurrogate(s[index + 1]) : index > 0 && char.IsHighSurrogate(s[index - 1]);
			}
		}
	}
}
