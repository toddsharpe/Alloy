using System;
using System.Collections.Generic;
using System.Globalization;
using System.Text;

namespace Alloy.Sdk.Script
{
	public enum JsType
	{
		Undefined,
		Null,
		Boolean,
		Number,
		String,
		Object,
	}

	// A JavaScript value: the primitives by value, objects by reference
	public readonly struct JsValue
	{
		public readonly JsType Type;
		private readonly double _number;
		private readonly object _reference;

		private JsValue(JsType type, double number, object reference)
		{
			Type = type;
			_number = number;
			_reference = reference;
		}

		public static readonly JsValue Undefined = new JsValue(JsType.Undefined, 0, null);
		public static readonly JsValue Null = new JsValue(JsType.Null, 0, null);
		public static readonly JsValue True = new JsValue(JsType.Boolean, 1, null);
		public static readonly JsValue False = new JsValue(JsType.Boolean, 0, null);
		public static readonly JsValue NaN = new JsValue(JsType.Number, double.NaN, null);
		public static readonly JsValue Zero = new JsValue(JsType.Number, 0, null);
		public static readonly JsValue EmptyString = new JsValue(JsType.String, 0, "");

		public static JsValue FromBoolean(bool value) => value ? True : False;

		public static JsValue FromNumber(double value) => new JsValue(JsType.Number, value, null);

		public static JsValue FromString(string value) => new JsValue(JsType.String, 0, value ?? "");

		public static JsValue FromObject(JsObject value) => value == null ? Null : new JsValue(JsType.Object, 0, value);

		public static implicit operator JsValue(double value) => FromNumber(value);

		public static implicit operator JsValue(string value) => FromString(value);

		public static implicit operator JsValue(bool value) => FromBoolean(value);

		public static implicit operator JsValue(JsObject value) => FromObject(value);

		public bool IsUndefined => Type == JsType.Undefined;

		public bool IsNull => Type == JsType.Null;

		public bool IsNullish => Type == JsType.Undefined || Type == JsType.Null;

		public bool IsNumber => Type == JsType.Number;

		public bool IsString => Type == JsType.String;

		public bool IsBoolean => Type == JsType.Boolean;

		public bool IsObject => Type == JsType.Object;

		public double Number => _number;

		public string String => (string)_reference;

		public bool Boolean => _number != 0;

		public JsObject Object => _reference as JsObject;

		public JsFunction Function => _reference as JsFunction;

		public bool IsCallable => _reference is JsFunction;

		public bool IsSymbol => _reference is JsSymbol;

		public JsSymbol Symbol => _reference as JsSymbol;

		// ToBoolean (ECMA-262 7.1.2)
		public bool Truthy => Type switch
		{
			JsType.Undefined or JsType.Null => false,
			JsType.Boolean => _number != 0,
			JsType.Number => !(_number == 0 || double.IsNaN(_number)),
			JsType.String => ((string)_reference).Length > 0,
			_ => true,
		};

		// ToNumber (7.1.4), with ToPrimitive for objects
		public double ToNumber()
		{
			switch (Type)
			{
				case JsType.Undefined:
					return double.NaN;
				case JsType.Null:
					return 0;
				case JsType.Boolean:
				case JsType.Number:
					return _number;
				case JsType.String:
					return StringToNumber((string)_reference);
				default:
					return Object.ToPrimitive(true).ToNumber();
			}
		}

		public static double StringToNumber(string text)
		{
			text = text.Trim();
			if (text.Length == 0)
				return 0;
			if (text.StartsWith("0x", StringComparison.OrdinalIgnoreCase))
				return long.TryParse(text.AsSpan(2), NumberStyles.HexNumber, CultureInfo.InvariantCulture, out long hex) ? hex : double.NaN;
			if (text.StartsWith("0b", StringComparison.OrdinalIgnoreCase))
			{
				try
				{
					return Convert.ToInt64(text.Substring(2), 2);
				}
				catch (Exception)
				{
					return double.NaN;
				}
			}
			if (text == "Infinity" || text == "+Infinity")
				return double.PositiveInfinity;
			if (text == "-Infinity")
				return double.NegativeInfinity;
			foreach (char c in text)
			{
				if (!(char.IsDigit(c) || c == '.' || c == 'e' || c == 'E' || c == '+' || c == '-'))
					return double.NaN;
			}
			return double.TryParse(text, NumberStyles.Float, CultureInfo.InvariantCulture, out double value) ? value : double.NaN;
		}

		public int ToInt32()
		{
			double n = ToNumber();
			if (double.IsNaN(n) || double.IsInfinity(n))
				return 0;
			double truncated = Math.Truncate(n);
			double modulo = truncated % 4294967296.0;
			if (modulo < 0)
				modulo += 4294967296.0;
			return (int)(uint)modulo;
		}

		public uint ToUInt32() => (uint)ToInt32();

		// ToString (7.1.17)
		public string ToJsString()
		{
			switch (Type)
			{
				case JsType.Undefined:
					return "undefined";
				case JsType.Null:
					return "null";
				case JsType.Boolean:
					return _number != 0 ? "true" : "false";
				case JsType.Number:
					return NumberToString(_number);
				case JsType.String:
					return (string)_reference;
				default:
					return Object.ToPrimitive(false).ToJsString();
			}
		}

		public static string NumberToString(double n)
		{
			if (double.IsNaN(n))
				return "NaN";
			if (double.IsPositiveInfinity(n))
				return "Infinity";
			if (double.IsNegativeInfinity(n))
				return "-Infinity";
			if (n == 0)
				return "0";
			if (n == Math.Floor(n) && Math.Abs(n) < 1e21)
				return ((long)n).ToString(CultureInfo.InvariantCulture);
			string r = n.ToString("R", CultureInfo.InvariantCulture);
			if (r.Contains("E", StringComparison.Ordinal))
			{
				int e = r.IndexOf('E');
				string mantissa = r.Substring(0, e);
				int exponent = int.Parse(r.Substring(e + 1), CultureInfo.InvariantCulture);
				if (exponent >= -6 && exponent < 21)
					return n.ToString("0.####################", CultureInfo.InvariantCulture);
				return mantissa + "e" + (exponent >= 0 ? "+" : "") + exponent.ToString(CultureInfo.InvariantCulture);
			}
			return r;
		}

		public JsValue ToPrimitive(bool preferNumber) => Type == JsType.Object ? Object.ToPrimitive(preferNumber) : this;

		// Strict equality (7.2.15)
		public static bool StrictEquals(JsValue a, JsValue b)
		{
			if (a.Type != b.Type)
				return false;
			switch (a.Type)
			{
				case JsType.Undefined:
				case JsType.Null:
					return true;
				case JsType.Number:
					return a._number == b._number;
				case JsType.Boolean:
					return a._number == b._number;
				case JsType.String:
					return (string)a._reference == (string)b._reference;
				default:
					return ReferenceEquals(a._reference, b._reference);
			}
		}

		// Loose equality (7.2.14)
		public static bool LooseEquals(JsValue a, JsValue b)
		{
			if (a.Type == b.Type)
				return StrictEquals(a, b);
			if (a.IsNullish && b.IsNullish)
				return true;
			if (a.IsNullish || b.IsNullish)
				return false;
			if (a.IsNumber && b.IsString)
				return a._number == b.ToNumber();
			if (a.IsString && b.IsNumber)
				return a.ToNumber() == b._number;
			if (a.IsBoolean)
				return LooseEquals(FromNumber(a._number), b);
			if (b.IsBoolean)
				return LooseEquals(a, FromNumber(b._number));
			if (a.IsObject && !b.IsObject)
				return LooseEquals(a.Object.ToPrimitive(true), b);
			if (b.IsObject && !a.IsObject)
				return LooseEquals(a, b.Object.ToPrimitive(true));
			return false;
		}

		// SameValueZero, for includes and Map keys
		public static bool SameValueZero(JsValue a, JsValue b)
		{
			if (a.IsNumber && b.IsNumber && double.IsNaN(a._number) && double.IsNaN(b._number))
				return true;
			return StrictEquals(a, b);
		}

		public string TypeOf => Type switch
		{
			JsType.Undefined => "undefined",
			JsType.Null => "object",
			JsType.Boolean => "boolean",
			JsType.Number => "number",
			JsType.String => "string",
			_ => _reference is JsSymbol ? "symbol" : IsCallable ? "function" : "object",
		};

		public override string ToString() => ToJsString();
	}

	// A property: its value or its accessor pair, and the flags that matter to the code pages run
	public sealed class JsProperty
	{
		public JsValue Value;
		public JsFunction Getter;
		public JsFunction Setter;
		public bool Enumerable = true;
		public bool Writable = true;
		public bool Configurable = true;

		public bool IsAccessor => Getter != null || Setter != null;
	}

	// An ordinary object: a prototype and ordered properties; subclasses give arrays, functions and host objects their behaviour
	public class JsObject
	{
		public JsObject Prototype;
		public string Class = "Object";
		public bool Extensible = true;
		private Dictionary<string, JsProperty> _properties;
		private List<string> _order;

		public JsObject(JsObject prototype)
		{
			Prototype = prototype;
		}

		public virtual IEnumerable<string> OwnKeys
		{
			get
			{
				if (_order == null)
					yield break;
				foreach (string key in _order)
				{
					if (_properties.ContainsKey(key))
						yield return key;
				}
			}
		}

		public int OwnCount => _properties?.Count ?? 0;

		public virtual JsProperty GetOwnProperty(string key) => _properties != null && _properties.TryGetValue(key, out JsProperty property) ? property : null;

		public JsProperty GetProperty(string key)
		{
			for (JsObject o = this; o != null; o = o.Prototype)
			{
				JsProperty property = o.GetOwnProperty(key);
				if (property != null)
					return property;
			}
			return null;
		}

		public virtual bool HasProperty(string key) => GetProperty(key) != null;

		public bool HasOwnProperty(string key) => GetOwnProperty(key) != null;

		// [[Get]] with accessors run against the receiver
		public virtual JsValue Get(string key, Interpreter interpreter, JsValue receiver)
		{
			JsProperty property = GetProperty(key);
			if (property == null)
				return JsValue.Undefined;
			if (property.IsAccessor)
				return property.Getter == null ? JsValue.Undefined : interpreter.Call(property.Getter, receiver, Array.Empty<JsValue>());
			return property.Value;
		}

		public JsValue Get(string key, Interpreter interpreter) => Get(key, interpreter, this);

		// [[Set]]: an own data property is written, a setter up the chain is called, anything else makes a new own property
		public virtual void Set(string key, JsValue value, Interpreter interpreter, JsValue receiver)
		{
			JsProperty own = GetOwnProperty(key);
			if (own != null)
			{
				if (own.IsAccessor)
				{
					if (own.Setter != null)
						interpreter.Call(own.Setter, receiver, new[] { value });
					return;
				}
				if (own.Writable)
					own.Value = value;
				return;
			}
			JsProperty inherited = Prototype?.GetProperty(key);
			if (inherited != null && inherited.IsAccessor)
			{
				if (inherited.Setter != null)
					interpreter.Call(inherited.Setter, receiver, new[] { value });
				return;
			}
			if (inherited != null && !inherited.Writable)
				return;
			if (!Extensible)
				return;
			DefineOwn(key, new JsProperty { Value = value });
		}

		public void Set(string key, JsValue value, Interpreter interpreter) => Set(key, value, interpreter, this);

		// A plain data property, as the built-ins and host objects define theirs
		public void Put(string key, JsValue value, bool enumerable = true)
		{
			JsProperty own = GetOwnProperty(key);
			if (own != null && !own.IsAccessor)
			{
				own.Value = value;
				own.Enumerable = enumerable;
				return;
			}
			DefineOwn(key, new JsProperty { Value = value, Enumerable = enumerable });
		}

		public void DefineAccessor(string key, JsFunction getter, JsFunction setter, bool enumerable = false) =>
			DefineOwn(key, new JsProperty { Getter = getter, Setter = setter, Enumerable = enumerable });

		public virtual void DefineOwn(string key, JsProperty property)
		{
			_properties ??= new Dictionary<string, JsProperty>(StringComparer.Ordinal);
			_order ??= new List<string>();
			if (!_properties.ContainsKey(key))
				_order.Add(key);
			_properties[key] = property;
		}

		public virtual bool Delete(string key)
		{
			if (_properties == null || !_properties.TryGetValue(key, out JsProperty property))
				return true;
			if (!property.Configurable)
				return false;
			_properties.Remove(key);
			_order.Remove(key);
			return true;
		}

		// ToPrimitive via valueOf and toString, in the order the hint asks for
		public virtual JsValue ToPrimitive(bool preferNumber)
		{
			Interpreter interpreter = Interpreter.Current;
			if (interpreter != null)
			{
				JsValue exotic = Get(Interpreter.SymbolToPrimitive, interpreter, this);
				if (exotic.IsCallable)
				{
					JsValue result = interpreter.Call(exotic.Function, this, new JsValue[] { preferNumber ? "number" : "string" });
					if (result.IsObject && !result.IsSymbol)
						throw interpreter.TypeError("Cannot convert object to primitive value");
					return result;
				}
			}
			string[] order = preferNumber ? new[] { "valueOf", "toString" } : new[] { "toString", "valueOf" };
			foreach (string name in order)
			{
				JsValue method = interpreter == null ? JsValue.Undefined : Get(name, interpreter, this);
				if (method.IsCallable)
				{
					JsValue result = interpreter.Call(method.Function, this, Array.Empty<JsValue>());
					if (!result.IsObject)
						return result;
				}
			}
			return "[object " + Class + "]";
		}

		public override string ToString() => "[object " + Class + "]";
	}

	// Arrays keep their elements in a list and expose length as a special property
	public sealed class JsArray : JsObject
	{
		public readonly List<JsValue> Elements;
		public bool Frozen;

		public JsArray(JsObject prototype, List<JsValue> elements = null) : base(prototype)
		{
			Class = "Array";
			Elements = elements ?? new List<JsValue>();
		}

		public int Length => Elements.Count;

		public static bool TryIndex(string key, out int index)
		{
			index = -1;
			if (key.Length == 0 || key.Length > 10)
				return false;
			foreach (char c in key)
			{
				if (c < '0' || c > '9')
					return false;
			}
			if (key.Length > 1 && key[0] == '0')
				return false;
			return int.TryParse(key, NumberStyles.None, CultureInfo.InvariantCulture, out index);
		}

		public override JsProperty GetOwnProperty(string key)
		{
			if (key == "length")
				return new JsProperty { Value = Elements.Count, Enumerable = false, Configurable = false };
			if (TryIndex(key, out int index))
				return index < Elements.Count ? new JsProperty { Value = Elements[index], Writable = !Frozen, Configurable = !Frozen } : null;
			return base.GetOwnProperty(key);
		}

		public override JsValue Get(string key, Interpreter interpreter, JsValue receiver)
		{
			if (key == "length")
				return Elements.Count;
			if (TryIndex(key, out int index))
				return index < Elements.Count ? Elements[index] : JsValue.Undefined;
			return base.Get(key, interpreter, receiver);
		}

		public override void Set(string key, JsValue value, Interpreter interpreter, JsValue receiver)
		{
			if (Frozen && (key == "length" || TryIndex(key, out _)))
				return;
			if (key == "length")
			{
				int length = (int)Math.Max(0, Math.Min(int.MaxValue, value.ToNumber()));
				if (length < Elements.Count)
					Elements.RemoveRange(length, Elements.Count - length);
				else
				{
					while (Elements.Count < length)
						Elements.Add(JsValue.Undefined);
				}
				return;
			}
			if (TryIndex(key, out int index))
			{
				if (index > 50_000_000)
					return;
				while (Elements.Count <= index)
					Elements.Add(JsValue.Undefined);
				Elements[index] = value;
				return;
			}
			base.Set(key, value, interpreter, receiver);
		}

		public override void DefineOwn(string key, JsProperty property)
		{
			if (TryIndex(key, out int index) && !property.IsAccessor)
			{
				while (Elements.Count <= index)
					Elements.Add(JsValue.Undefined);
				Elements[index] = property.Value;
				return;
			}
			base.DefineOwn(key, property);
		}

		public override bool Delete(string key)
		{
			if (TryIndex(key, out int index))
			{
				if (index < Elements.Count)
					Elements[index] = JsValue.Undefined;
				return true;
			}
			return base.Delete(key);
		}

		public IEnumerable<string> IndexKeys()
		{
			for (int i = 0; i < Elements.Count; i++)
				yield return i.ToString(CultureInfo.InvariantCulture);
		}
	}

	// A symbol: an object standing in for the primitive, keyed by a string no script can spell
	public sealed class JsSymbol : JsObject
	{
		public readonly string Key;
		public readonly string Description;
		private static int _next;

		public JsSymbol(string description, string key = null) : base(null)
		{
			Class = "Symbol";
			Description = description;
			Key = key ?? "@@sym" + System.Threading.Interlocked.Increment(ref _next) + ":" + description;
			Extensible = false;
		}

		public override JsValue ToPrimitive(bool preferNumber) => "Symbol(" + Description + ")";

		public override string ToString() => "Symbol(" + Description + ")";
	}

	// A function: either script (parameters, body, the scope it closed over) or native (a delegate)
	public delegate JsValue NativeFunction(Interpreter interpreter, JsValue thisValue, JsValue[] arguments);

	public delegate JsValue NativeConstructor(Interpreter interpreter, JsValue[] arguments, JsObject newTarget);

	public class JsFunction : JsObject
	{
		public string Name;
		public FunctionNode Node;
		public Scope Closure;
		public NativeFunction Native;
		public NativeConstructor NativeNew;
		public ClassNode ClassNode;
		public Scope ClassScope;
		public JsValue BoundThis;
		public bool HasBoundThis;
		public JsValue[] BoundArguments;
		public JsFunction Target;
		public JsObject HomeObject;
		public bool IsArrow;
		public bool IsClassConstructor;
		public bool IsDerivedConstructor;
		public JsValue LexicalThis;
		public JsValue LexicalNewTarget;
		public int ExpectedLength;

		// The script or module that was running when the function was made, for stack traces
		public string Source;

		public JsFunction(JsObject prototype) : base(prototype)
		{
			Class = "Function";
			Source = Interpreter.Current?.CurrentSource;
		}

		public bool IsConstructor => Target != null ? Target.IsConstructor : Native != null ? NativeNew != null : IsClassConstructor || (!IsArrow && Node != null && !Node.IsMethod && !Node.IsAsync && !Node.IsGenerator);

		public override string ToString() => Node != null ? "function " + Name + "() { [code] }" : "function " + Name + "() { [native code] }";
	}

	// A primitive wrapped as an object (new Number(1), or a primitive used as this), and Date's time value
	public sealed class JsPrimitiveObject : JsObject
	{
		public JsValue Primitive;

		public JsPrimitiveObject(JsObject prototype, JsValue primitive, string cls) : base(prototype)
		{
			Primitive = primitive;
			Class = cls;
		}

		public override JsValue ToPrimitive(bool preferNumber) => Class == "Date" ? base.ToPrimitive(preferNumber) : Primitive;
	}

	// A thrown JavaScript value travelling through the host stack
	public sealed class JsThrow : Exception
	{
		public readonly JsValue Value;

		public JsThrow(JsValue value) : base(Describe(value))
		{
			Value = value;
		}

		private static string Describe(JsValue value)
		{
			try
			{
				if (value.IsObject)
				{
					Interpreter interpreter = Interpreter.Current;
					if (interpreter != null)
					{
						JsValue name = value.Object.Get("name", interpreter);
						JsValue message = value.Object.Get("message", interpreter);
						if (!name.IsUndefined || !message.IsUndefined)
							return (name.IsUndefined ? "Error" : name.ToJsString()) + ": " + message.ToJsString();
					}
				}
				return value.ToJsString();
			}
			catch (Exception)
			{
				return "exception";
			}
		}
	}

	// A lexical scope: bindings by name, with the parent chain closures walk
	public sealed class Scope
	{
		public readonly Scope Parent;
		public readonly Dictionary<string, Binding> Bindings = new Dictionary<string, Binding>(StringComparer.Ordinal);
		public readonly bool IsFunctionScope;
		public JsValue ThisValue;
		public bool HasThis;
		public bool ThisPending;
		public JsFunction FunctionObject;
		public JsValue NewTarget;
		public JsObject HomeObject;
		// The module whose top level this scope is, for import() and import.meta inside it
		public object Module;

		public Scope(Scope parent, bool isFunctionScope)
		{
			Parent = parent;
			IsFunctionScope = isFunctionScope;
		}

		public Binding Lookup(string name)
		{
			for (Scope s = this; s != null; s = s.Parent)
			{
				if (s.Bindings.TryGetValue(name, out Binding binding))
					return binding;
			}
			return null;
		}

		public Binding Declare(string name, JsValue value, bool mutable = true, bool initialized = true)
		{
			Binding binding = new Binding { Value = value, Mutable = mutable, Initialized = initialized };
			Bindings[name] = binding;
			return binding;
		}

		public Scope FunctionScope()
		{
			Scope s = this;
			while (s.Parent != null && !s.IsFunctionScope)
				s = s.Parent;
			return s;
		}

		// The nearest scope that carries a this value: a function's, or the global one
		public Scope ThisScope()
		{
			for (Scope s = this; s != null; s = s.Parent)
			{
				if (s.HasThis)
					return s;
			}
			return null;
		}
	}

	public sealed class Binding
	{
		public JsValue Value;
		public bool Mutable;
		public bool Initialized;
	}
}
