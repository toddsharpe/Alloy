using System;
using System.Collections.Generic;
using System.Globalization;
using System.Text;

namespace Alloy.Sdk.Script
{
	// The intrinsic objects every script sees, and the helpers the built-ins are written with
	public sealed partial class Interpreter
	{
		public const string SymbolIterator = "@@iterator";
		public const string SymbolAsyncIterator = "@@asyncIterator";
		public const string SymbolHasInstance = "@@hasInstance";
		public const string SymbolToPrimitive = "@@toPrimitive";
		public const string SymbolToStringTag = "@@toStringTag";

		public JsObject ObjectPrototype;
		public JsObject FunctionPrototype;
		public JsObject ArrayPrototype;
		public JsObject StringPrototype;
		public JsObject NumberPrototype;
		public JsObject BooleanPrototype;
		public JsObject SymbolPrototype;
		public JsObject ErrorPrototype;
		public JsObject RegExpPrototype;
		public JsObject DatePrototype;
		public JsObject PromisePrototype;
		public JsObject MapPrototype;
		public JsObject SetPrototype;
		public JsObject WeakMapPrototype;
		public JsObject WeakSetPrototype;
		public JsObject IteratorPrototype;
		public JsObject ArrayIteratorPrototype;
		public JsObject GeneratorPrototype;
		public JsFunction ArrayValues;
		public JsFunction ObjectConstructor;
		public JsFunction ArrayConstructor;
		public JsFunction PromiseConstructor;
		public readonly Dictionary<string, JsObject> ErrorPrototypes = new Dictionary<string, JsObject>(StringComparer.Ordinal);
		private readonly Dictionary<string, JsSymbol> _symbolRegistry = new Dictionary<string, JsSymbol>(StringComparer.Ordinal);
		public Action<string, string> OnConsole;

		private void SetupRealm()
		{
			ObjectPrototype = new JsObject(null);
			Global.Prototype = ObjectPrototype;
			FunctionPrototype = new JsFunction(ObjectPrototype) { Name = "", Native = (i, t, a) => JsValue.Undefined };
			ArrayPrototype = new JsArray(ObjectPrototype);
			StringPrototype = new JsPrimitiveObject(ObjectPrototype, "", "String");
			NumberPrototype = new JsPrimitiveObject(ObjectPrototype, 0, "Number");
			BooleanPrototype = new JsPrimitiveObject(ObjectPrototype, false, "Boolean");
			SymbolPrototype = new JsObject(ObjectPrototype) { Class = "Symbol" };
			ErrorPrototype = new JsObject(ObjectPrototype) { Class = "Error" };
			RegExpPrototype = new JsObject(ObjectPrototype) { Class = "RegExp" };
			DatePrototype = new JsObject(ObjectPrototype) { Class = "Date" };
			PromisePrototype = new JsObject(ObjectPrototype) { Class = "Promise" };
			MapPrototype = new JsObject(ObjectPrototype) { Class = "Map" };
			SetPrototype = new JsObject(ObjectPrototype) { Class = "Set" };
			WeakMapPrototype = new JsObject(ObjectPrototype) { Class = "WeakMap" };
			WeakSetPrototype = new JsObject(ObjectPrototype) { Class = "WeakSet" };
			IteratorPrototype = new JsObject(ObjectPrototype);
			ArrayIteratorPrototype = new JsObject(IteratorPrototype) { Class = "Array Iterator" };
			GeneratorPrototype = new JsObject(IteratorPrototype) { Class = "Generator" };
			DefineMethod(IteratorPrototype, SymbolIterator, 0, (i, t, a) => t);
			Global.Put("globalThis", Global, false);
			Global.Put("window", Global, false);
			Global.Put("self", Global, false);
			Global.Put("undefined", JsValue.Undefined, false);
			Global.Put("NaN", double.NaN, false);
			Global.Put("Infinity", double.PositiveInfinity, false);
			SetupObject();
			SetupFunction();
			SetupSymbol();
			SetupErrors();
			SetupGlobals();
			SetupConsole();
			SetupReflect();
			SetupArray();
			SetupString();
			SetupRegExp();
			SetupNumber();
			SetupBoolean();
			SetupMath();
			SetupJson();
			SetupDate();
			SetupCollections();
			SetupPromise();
			SetupTimers();
			SetupGenerators();
		}

		// Helpers for defining built-ins

		public static JsValue Arg(JsValue[] arguments, int index) => index < arguments.Length ? arguments[index] : JsValue.Undefined;

		public JsFunction NewNative(string name, int length, NativeFunction body) => new JsFunction(FunctionPrototype) { Name = name, Native = body, ExpectedLength = length };

		public JsFunction DefineMethod(JsObject target, string name, int length, NativeFunction body)
		{
			JsFunction function = NewNative(name.StartsWith("@@", StringComparison.Ordinal) ? "[" + name.Substring(2) + "]" : name, length, body);
			target.Put(name, function, false);
			return function;
		}

		public void DefineGetter(JsObject target, string name, NativeFunction getter, NativeFunction setter = null)
		{
			JsFunction get = getter == null ? null : NewNative("get " + name, 0, getter);
			JsFunction set = setter == null ? null : NewNative("set " + name, 1, setter);
			target.DefineAccessor(name, get, set);
		}

		public JsFunction NewConstructor(string name, int length, JsObject prototype, NativeFunction call, NativeConstructor construct)
		{
			JsFunction function = NewNative(name, length, call ?? ((i, t, a) => throw TypeError("Constructor " + name + " requires 'new'")));
			function.NativeNew = construct;
			function.DefineOwn("prototype", new JsProperty { Value = prototype, Enumerable = false, Writable = false, Configurable = false });
			prototype.Put("constructor", function, false);
			Global.Put(name, function, false);
			return function;
		}

		public JsObject NewObject() => new JsObject(ObjectPrototype);

		public JsArray NewArray(List<JsValue> elements = null) => new JsArray(ArrayPrototype, elements);

		public JsArray NewArray(IEnumerable<JsValue> elements) => new JsArray(ArrayPrototype, new List<JsValue>(elements));

		public JsArray NewStringArray(IEnumerable<string> strings)
		{
			JsArray array = NewArray();
			foreach (string s in strings)
				array.Elements.Add(s);
			return array;
		}

		// The index an array method was given, counted from the end when negative and clamped to the length
		public static int RelativeIndex(JsValue value, int length, int whenUndefined)
		{
			if (value.IsUndefined)
				return whenUndefined;
			double n = value.ToNumber();
			if (double.IsNaN(n))
				n = 0;
			n = Math.Truncate(n);
			if (n < 0)
				n = Math.Max(0, length + n);
			return (int)Math.Min(n, length);
		}

		public JsFunction RequireCallable(JsValue value, string what)
		{
			if (!value.IsCallable)
				throw TypeError(what + " is not a function");
			return value.Function;
		}

		private JsObject RequireObject(JsValue value, string what)
		{
			if (!value.IsObject)
				throw TypeError(what + " is not an object");
			return value.Object;
		}

		// Object

		private void SetupObject()
		{
			ObjectConstructor = NewConstructor("Object", 1, ObjectPrototype,
				(i, t, a) => a.Length == 0 || a[0].IsNullish ? NewObject() : ToObject(a[0]),
				(i, a, target) => a.Length == 0 || a[0].IsNullish ? new JsObject(PrototypeFromConstructor(target, ObjectPrototype)) : ToObject(a[0]));
			DefineMethod(ObjectConstructor, "keys", 1, (i, t, a) => NewStringArray(OwnEnumerableKeys(ToObject(Arg(a, 0)))));
			DefineMethod(ObjectConstructor, "values", 1, (i, t, a) =>
			{
				JsObject obj = ToObject(Arg(a, 0));
				JsArray result = NewArray();
				foreach (string key in OwnEnumerableKeys(obj))
					result.Elements.Add(obj.Get(key, this));
				return result;
			});
			DefineMethod(ObjectConstructor, "entries", 1, (i, t, a) =>
			{
				JsObject obj = ToObject(Arg(a, 0));
				JsArray result = NewArray();
				foreach (string key in OwnEnumerableKeys(obj))
					result.Elements.Add(NewArray(new List<JsValue> { key, obj.Get(key, this) }));
				return result;
			});
			DefineMethod(ObjectConstructor, "assign", 2, (i, t, a) =>
			{
				JsObject target = ToObject(Arg(a, 0));
				for (int n = 1; n < a.Length; n++)
					CopyDataProperties(target, a[n]);
				return target;
			});
			DefineMethod(ObjectConstructor, "fromEntries", 1, (i, t, a) =>
			{
				JsObject result = NewObject();
				foreach (JsValue entry in Iterate(Arg(a, 0)))
					result.Put(ToPropertyKey(GetMember(entry, "0")), GetMember(entry, "1"));
				return result;
			});
			DefineMethod(ObjectConstructor, "create", 2, (i, t, a) =>
			{
				JsValue prototype = Arg(a, 0);
				if (!prototype.IsObject && !prototype.IsNull)
					throw TypeError("Object prototype may only be an Object or null: " + prototype.ToJsString());
				JsObject result = new JsObject(prototype.Object);
				if (a.Length > 1 && !a[1].IsUndefined)
					DefineProperties(result, a[1]);
				return result;
			});
			DefineMethod(ObjectConstructor, "getPrototypeOf", 1, (i, t, a) => (JsValue)ToObject(Arg(a, 0)).Prototype);
			DefineMethod(ObjectConstructor, "setPrototypeOf", 2, (i, t, a) =>
			{
				JsValue target = Arg(a, 0);
				if (target.IsObject)
					target.Object.Prototype = Arg(a, 1).Object;
				return target;
			});
			DefineMethod(ObjectConstructor, "defineProperty", 3, (i, t, a) =>
			{
				JsObject target = RequireObject(Arg(a, 0), "Object.defineProperty called on non-object");
				DefineProperty(target, ToPropertyKey(Arg(a, 1)), Arg(a, 2));
				return target;
			});
			DefineMethod(ObjectConstructor, "defineProperties", 2, (i, t, a) =>
			{
				JsObject target = RequireObject(Arg(a, 0), "Object.defineProperties called on non-object");
				DefineProperties(target, Arg(a, 1));
				return target;
			});
			DefineMethod(ObjectConstructor, "getOwnPropertyNames", 1, (i, t, a) => NewStringArray(OwnKeys(ToObject(Arg(a, 0)), false)));
			DefineMethod(ObjectConstructor, "getOwnPropertySymbols", 1, (i, t, a) =>
			{
				JsArray result = NewArray();
				foreach (string key in ToObject(Arg(a, 0)).OwnKeys)
				{
					if (key.StartsWith("@@", StringComparison.Ordinal))
						result.Elements.Add(SymbolFor(key));
				}
				return result;
			});
			DefineMethod(ObjectConstructor, "getOwnPropertyDescriptor", 2, (i, t, a) => FromDescriptor(ToObject(Arg(a, 0)).GetOwnProperty(ToPropertyKey(Arg(a, 1)))));
			DefineMethod(ObjectConstructor, "getOwnPropertyDescriptors", 1, (i, t, a) =>
			{
				JsObject obj = ToObject(Arg(a, 0));
				JsObject result = NewObject();
				foreach (string key in OwnKeys(obj, true))
					result.Put(key, FromDescriptor(obj.GetOwnProperty(key)));
				return result;
			});
			DefineMethod(ObjectConstructor, "freeze", 1, (i, t, a) =>
			{
				if (Arg(a, 0).IsObject)
				{
					JsObject obj = a[0].Object;
					obj.Extensible = false;
					foreach (string key in new List<string>(obj.OwnKeys))
					{
						JsProperty property = obj.GetOwnProperty(key);
						property.Configurable = false;
						if (!property.IsAccessor)
							property.Writable = false;
					}
					if (obj is JsArray array)
						array.Frozen = true;
				}
				return Arg(a, 0);
			});
			DefineMethod(ObjectConstructor, "isFrozen", 1, (i, t, a) =>
			{
				if (!Arg(a, 0).IsObject)
					return true;
				JsObject obj = a[0].Object;
				if (obj.Extensible)
					return false;
				foreach (string key in obj.OwnKeys)
				{
					JsProperty property = obj.GetOwnProperty(key);
					if (property.Configurable || !property.IsAccessor && property.Writable)
						return false;
				}
				return true;
			});
			DefineMethod(ObjectConstructor, "seal", 1, (i, t, a) =>
			{
				if (Arg(a, 0).IsObject)
				{
					a[0].Object.Extensible = false;
					foreach (string key in a[0].Object.OwnKeys)
						a[0].Object.GetOwnProperty(key).Configurable = false;
				}
				return Arg(a, 0);
			});
			DefineMethod(ObjectConstructor, "isSealed", 1, (i, t, a) => !Arg(a, 0).IsObject || !a[0].Object.Extensible);
			DefineMethod(ObjectConstructor, "preventExtensions", 1, (i, t, a) =>
			{
				if (Arg(a, 0).IsObject)
					a[0].Object.Extensible = false;
				return Arg(a, 0);
			});
			DefineMethod(ObjectConstructor, "isExtensible", 1, (i, t, a) => Arg(a, 0).IsObject && a[0].Object.Extensible);
			DefineMethod(ObjectConstructor, "is", 2, (i, t, a) =>
			{
				JsValue x = Arg(a, 0);
				JsValue y = Arg(a, 1);
				if (x.IsNumber && y.IsNumber && x.Number == 0 && y.Number == 0)
					return double.IsNegative(x.Number) == double.IsNegative(y.Number);
				return JsValue.SameValueZero(x, y);
			});
			DefineMethod(ObjectPrototype, "hasOwnProperty", 1, (i, t, a) => ToObject(t).HasOwnProperty(ToPropertyKey(Arg(a, 0))) || t.IsString && (ToPropertyKey(Arg(a, 0)) == "length" || JsArray.TryIndex(ToPropertyKey(Arg(a, 0)), out int index) && index < t.String.Length));
			DefineMethod(ObjectConstructor, "hasOwn", 2, (i, t, a) => ToObject(Arg(a, 0)).HasOwnProperty(ToPropertyKey(Arg(a, 1))));
			DefineMethod(ObjectPrototype, "isPrototypeOf", 1, (i, t, a) =>
			{
				if (!Arg(a, 0).IsObject || !t.IsObject)
					return false;
				for (JsObject o = a[0].Object.Prototype; o != null; o = o.Prototype)
				{
					if (o == t.Object)
						return true;
				}
				return false;
			});
			DefineMethod(ObjectPrototype, "propertyIsEnumerable", 1, (i, t, a) => ToObject(t).GetOwnProperty(ToPropertyKey(Arg(a, 0)))?.Enumerable ?? false);
			DefineMethod(ObjectPrototype, "toString", 0, (i, t, a) => ObjectToString(t));
			DefineMethod(ObjectPrototype, "toLocaleString", 0, (i, t, a) => ObjectToString(t));
			DefineMethod(ObjectPrototype, "valueOf", 0, (i, t, a) => (JsValue)ToObject(t));
			DefineGetter(ObjectPrototype, "__proto__", (i, t, a) => (JsValue)ToObject(t).Prototype, (i, t, a) =>
			{
				if (t.IsObject && (Arg(a, 0).IsObject || Arg(a, 0).IsNull))
					t.Object.Prototype = a[0].Object;
				return JsValue.Undefined;
			});
		}

		private JsValue ObjectToString(JsValue value)
		{
			switch (value.Type)
			{
				case JsType.Undefined:
					return "[object Undefined]";
				case JsType.Null:
					return "[object Null]";
				case JsType.String:
					return "[object String]";
				case JsType.Number:
					return "[object Number]";
				case JsType.Boolean:
					return "[object Boolean]";
			}
			JsValue tag = value.Object.Get(SymbolToStringTag, this, value);
			if (tag.IsString)
				return "[object " + tag.String + "]";
			string cls = value.Object is JsArray ? "Array" : value.Object is JsFunction ? "Function" : value.Object.Class;
			if (cls != "Array" && cls != "Function" && cls != "Error" && cls != "Boolean" && cls != "Number" && cls != "String" && cls != "Date" && cls != "RegExp" && cls != "Arguments")
				cls = "Object";
			return "[object " + cls + "]";
		}

		public List<string> OwnKeys(JsObject obj, bool includeSymbols)
		{
			List<string> keys = new List<string>();
			if (obj is JsArray array)
			{
				keys.AddRange(array.IndexKeys());
				keys.Add("length");
			}
			else if (obj is JsPrimitiveObject { Primitive: { IsString: true } } wrapper)
			{
				for (int i = 0; i < wrapper.Primitive.String.Length; i++)
					keys.Add(i.ToString(CultureInfo.InvariantCulture));
				keys.Add("length");
			}
			foreach (string key in obj.OwnKeys)
			{
				bool symbol = key.StartsWith("@@", StringComparison.Ordinal);
				if (symbol ? includeSymbols : IsVisibleKey(key))
					keys.Add(key);
			}
			return keys;
		}

		private void DefineProperties(JsObject target, JsValue descriptors)
		{
			JsObject source = ToObject(descriptors);
			foreach (string key in OwnEnumerableKeys(source, true))
				DefineProperty(target, key, source.Get(key, this));
		}

		// Applies a property descriptor, keeping the attributes a partial descriptor does not mention
		public void DefineProperty(JsObject target, string key, JsValue descriptor)
		{
			JsObject d = RequireObject(descriptor, "Property description must be an object");
			JsProperty existing = target.GetOwnProperty(key);
			if (existing != null && !existing.Configurable && !(target is JsArray && key != "length"))
				throw TypeError("Cannot redefine property: " + key);
			JsValue getter = d.Get("get", this);
			JsValue setter = d.Get("set", this);
			JsProperty property = new JsProperty
			{
				Enumerable = d.HasProperty("enumerable") ? d.Get("enumerable", this).Truthy : existing?.Enumerable ?? false,
				Configurable = d.HasProperty("configurable") ? d.Get("configurable", this).Truthy : existing?.Configurable ?? false,
			};
			if (!getter.IsUndefined || !setter.IsUndefined)
			{
				if (!getter.IsUndefined && !getter.IsCallable || !setter.IsUndefined && !setter.IsCallable)
					throw TypeError("Getter and setter must be functions");
				property.Getter = getter.IsUndefined ? existing?.Getter : getter.Function;
				property.Setter = setter.IsUndefined ? existing?.Setter : setter.Function;
				property.Writable = false;
			}
			else
			{
				property.Writable = d.HasProperty("writable") ? d.Get("writable", this).Truthy : existing != null && !existing.IsAccessor && existing.Writable;
				property.Value = d.HasProperty("value") ? d.Get("value", this) : existing != null && !existing.IsAccessor ? existing.Value : JsValue.Undefined;
				if (target is JsArray array && key == "length")
				{
					array.Set("length", property.Value, this, array);
					return;
				}
			}
			target.DefineOwn(key, property);
		}

		private JsValue FromDescriptor(JsProperty property)
		{
			if (property == null)
				return JsValue.Undefined;
			JsObject result = NewObject();
			if (property.IsAccessor)
			{
				result.Put("get", property.Getter == null ? JsValue.Undefined : property.Getter);
				result.Put("set", property.Setter == null ? JsValue.Undefined : property.Setter);
			}
			else
			{
				result.Put("value", property.Value);
				result.Put("writable", property.Writable);
			}
			result.Put("enumerable", property.Enumerable);
			result.Put("configurable", property.Configurable);
			return result;
		}

		// Function

		private void SetupFunction()
		{
			JsFunction constructor = NewConstructor("Function", 1, FunctionPrototype, (i, t, a) => FunctionFromSource(a), (i, a, target) => FunctionFromSource(a));
			DefineMethod(FunctionPrototype, "call", 1, (i, t, a) =>
			{
				JsFunction function = RequireCallable(t, "Function.prototype.call target");
				JsValue[] rest = a.Length > 1 ? a.AsSpan(1).ToArray() : Array.Empty<JsValue>();
				return Call(function, Arg(a, 0), rest);
			});
			DefineMethod(FunctionPrototype, "apply", 2, (i, t, a) =>
			{
				JsFunction function = RequireCallable(t, "Function.prototype.apply target");
				return Call(function, Arg(a, 0), ArgumentsFromArrayLike(Arg(a, 1)));
			});
			DefineMethod(FunctionPrototype, "bind", 1, (i, t, a) =>
			{
				JsFunction function = RequireCallable(t, "Bind must be called on a function");
				JsFunction bound = new JsFunction(FunctionPrototype)
				{
					Name = "bound " + function.Name,
					Target = function,
					BoundThis = Arg(a, 0),
					HasBoundThis = true,
					BoundArguments = a.Length > 1 ? a.AsSpan(1).ToArray() : Array.Empty<JsValue>(),
					ExpectedLength = Math.Max(0, function.ExpectedLength - Math.Max(0, a.Length - 1)),
				};
				return bound;
			});
			DefineMethod(FunctionPrototype, "toString", 0, (i, t, a) => t.IsCallable ? t.Function.ToString() : throw TypeError("Function.prototype.toString requires that 'this' be a Function"));
			DefineMethod(FunctionPrototype, SymbolHasInstance, 1, (i, t, a) => t.IsCallable && Arg(a, 0).IsObject && OrdinaryHasInstance(t.Function, a[0]));
		}

		private bool OrdinaryHasInstance(JsFunction function, JsValue value)
		{
			if (function.Target != null)
				return InstanceOf(value, function.Target);
			JsValue prototype = GetMember(function, "prototype");
			if (!prototype.IsObject)
				return false;
			for (JsObject o = value.Object.Prototype; o != null; o = o.Prototype)
			{
				if (o == prototype.Object)
					return true;
			}
			return false;
		}

		private JsValue FunctionFromSource(JsValue[] arguments)
		{
			StringBuilder parameters = new StringBuilder();
			for (int i = 0; i < arguments.Length - 1; i++)
				parameters.Append(i > 0 ? "," : "").Append(arguments[i].ToJsString());
			string body = arguments.Length > 0 ? arguments[arguments.Length - 1].ToJsString() : "";
			try
			{
				FunctionNode node = Parser.ParseFunctionBody(parameters.ToString(), body);
				return MakeFunction(node, GlobalScope);
			}
			catch (SyntaxError e)
			{
				throw SyntaxErrorThrow(e.Message);
			}
		}

		public JsValue[] ArgumentsFromArrayLike(JsValue value)
		{
			if (value.IsNullish)
				return Array.Empty<JsValue>();
			if (value.IsObject && value.Object is JsArray array)
				return array.Elements.ToArray();
			if (!value.IsObject)
				throw TypeError("CreateListFromArrayLike called on non-object");
			int length = (int)Math.Max(0, Math.Min(int.MaxValue, GetMember(value, "length").ToNumber()));
			JsValue[] result = new JsValue[length];
			for (int i = 0; i < length; i++)
				result[i] = GetMember(value, i.ToString(CultureInfo.InvariantCulture));
			return result;
		}

		// Symbol

		private void SetupSymbol()
		{
			JsFunction constructor = NewConstructor("Symbol", 0, SymbolPrototype, (i, t, a) => NewSymbol(Arg(a, 0).IsUndefined ? "" : Arg(a, 0).ToJsString()), null);
			constructor.NativeNew = null;
			constructor.Put("iterator", WellKnown("Symbol.iterator", SymbolIterator), false);
			constructor.Put("asyncIterator", WellKnown("Symbol.asyncIterator", SymbolAsyncIterator), false);
			constructor.Put("hasInstance", WellKnown("Symbol.hasInstance", SymbolHasInstance), false);
			constructor.Put("toPrimitive", WellKnown("Symbol.toPrimitive", SymbolToPrimitive), false);
			constructor.Put("toStringTag", WellKnown("Symbol.toStringTag", SymbolToStringTag), false);
			constructor.Put("species", WellKnown("Symbol.species", "@@species"), false);
			DefineMethod(constructor, "for", 1, (i, t, a) =>
			{
				string key = Arg(a, 0).ToJsString();
				return SymbolFor("@@for:" + key, key);
			});
			DefineMethod(constructor, "keyFor", 1, (i, t, a) => Arg(a, 0).IsSymbol && a[0].Symbol.Key.StartsWith("@@for:", StringComparison.Ordinal) ? a[0].Symbol.Description : JsValue.Undefined);
			DefineMethod(SymbolPrototype, "toString", 0, (i, t, a) => t.IsSymbol ? t.Symbol.ToString() : throw TypeError("not a symbol"));
			DefineMethod(SymbolPrototype, "valueOf", 0, (i, t, a) => t);
			DefineGetter(SymbolPrototype, "description", (i, t, a) => t.IsSymbol ? t.Symbol.Description : JsValue.Undefined);
			SymbolPrototype.Put(SymbolToStringTag, "Symbol", false);
		}

		private JsSymbol WellKnown(string description, string key) => SymbolFor(key, description);

		public JsSymbol NewSymbol(string description) => new JsSymbol(description) { Prototype = SymbolPrototype };

		// The one symbol object for a key, so the same property name always compares equal
		public JsSymbol SymbolFor(string key, string description = null)
		{
			if (!_symbolRegistry.TryGetValue(key, out JsSymbol symbol))
			{
				symbol = new JsSymbol(description ?? key.Substring(2), key) { Prototype = SymbolPrototype };
				_symbolRegistry[key] = symbol;
			}
			return symbol;
		}

		// Errors

		private void SetupErrors()
		{
			JsFunction error = DefineErrorType("Error", ErrorPrototype);
			DefineMethod(ErrorPrototype, "toString", 0, (i, t, a) =>
			{
				if (!t.IsObject)
					throw TypeError("Error.prototype.toString called on non-object");
				JsValue name = t.Object.Get("name", this, t);
				JsValue message = t.Object.Get("message", this, t);
				string n = name.IsUndefined ? "Error" : name.ToJsString();
				string m = message.IsUndefined ? "" : message.ToJsString();
				return n.Length == 0 ? m : m.Length == 0 ? n : n + ": " + m;
			});
			DefineMethod(error, "captureStackTrace", 1, (i, t, a) =>
			{
				if (Arg(a, 0).IsObject)
					a[0].Object.Put("stack", a[0].Object.Get("name", this).ToJsString() + ": " + a[0].Object.Get("message", this).ToJsString() + StackTrace(), false);
				return JsValue.Undefined;
			});
			foreach (string type in new[] { "TypeError", "RangeError", "ReferenceError", "SyntaxError", "EvalError", "URIError", "AggregateError", "InternalError" })
			{
				JsObject prototype = new JsObject(ErrorPrototype) { Class = "Error" };
				JsFunction constructor = DefineErrorType(type, prototype);
				constructor.Prototype = error;
			}
		}

		private JsFunction DefineErrorType(string type, JsObject prototype)
		{
			ErrorPrototypes[type] = prototype;
			prototype.Put("name", type, false);
			prototype.Put("message", "", false);
			JsFunction constructor = NewConstructor(type, 1, prototype, (i, t, a) => ConstructError(type, a, null), (i, a, target) => ConstructError(type, a, target));
			return constructor;
		}

		private JsValue ConstructError(string type, JsValue[] arguments, JsObject newTarget)
		{
			int messageIndex = type == "AggregateError" ? 1 : 0;
			JsObject error = new JsObject(PrototypeFromConstructor(newTarget, ErrorPrototypes[type])) { Class = "Error" };
			JsValue message = Arg(arguments, messageIndex);
			if (!message.IsUndefined)
				error.Put("message", message.ToJsString(), false);
			JsValue options = Arg(arguments, messageIndex + 1);
			if (options.IsObject && options.Object.HasProperty("cause"))
				error.Put("cause", options.Object.Get("cause", this), false);
			if (type == "AggregateError")
				error.Put("errors", NewArray(Iterate(Arg(arguments, 0))), false);
			string name = error.Get("name", this).ToJsString();
			error.Put("stack", name + (message.IsUndefined ? "" : ": " + message.ToJsString()) + StackTrace(), false);
			return error;
		}

		// Global functions

		private void SetupGlobals()
		{
			DefineMethod(Global, "parseInt", 2, (i, t, a) => ParseInt(Arg(a, 0).ToJsString(), Arg(a, 1)));
			DefineMethod(Global, "parseFloat", 1, (i, t, a) => ParseFloat(Arg(a, 0).ToJsString()));
			DefineMethod(Global, "isNaN", 1, (i, t, a) => double.IsNaN(Arg(a, 0).ToNumber()));
			DefineMethod(Global, "isFinite", 1, (i, t, a) => double.IsFinite(Arg(a, 0).ToNumber()));
			DefineMethod(Global, "encodeURIComponent", 1, (i, t, a) => UriEncode(Arg(a, 0).ToJsString(), "-_.!~*'()"));
			DefineMethod(Global, "encodeURI", 1, (i, t, a) => UriEncode(Arg(a, 0).ToJsString(), "-_.!~*'();/?:@&=+$,#"));
			DefineMethod(Global, "decodeURIComponent", 1, (i, t, a) => UriDecode(Arg(a, 0).ToJsString(), ""));
			DefineMethod(Global, "decodeURI", 1, (i, t, a) => UriDecode(Arg(a, 0).ToJsString(), ";/?:@&=+$,#"));
			DefineMethod(Global, "escape", 1, (i, t, a) => UriEncode(Arg(a, 0).ToJsString(), "@*_+-./"));
			DefineMethod(Global, "unescape", 1, (i, t, a) => UriDecode(Arg(a, 0).ToJsString(), ""));
			DefineMethod(Global, "eval", 1, (i, t, a) => Arg(a, 0).IsString ? EvalInGlobal(a[0].String) : Arg(a, 0));
			DefineMethod(Global, "structuredClone", 1, (i, t, a) => StructuredClone(Arg(a, 0), new Dictionary<JsObject, JsObject>()));
		}

		private JsValue EvalInGlobal(string source)
		{
			try
			{
				return Run(Parser.Parse(source));
			}
			catch (SyntaxError e)
			{
				throw SyntaxErrorThrow(e.Message);
			}
		}

		private JsValue StructuredClone(JsValue value, Dictionary<JsObject, JsObject> seen)
		{
			if (!value.IsObject)
				return value;
			if (seen.TryGetValue(value.Object, out JsObject existing))
				return existing;
			if (value.IsCallable)
				throw new JsThrow(NewError("DataCloneError", "function could not be cloned"));
			if (value.Object is JsArray array)
			{
				JsArray copy = NewArray();
				seen[array] = copy;
				foreach (JsValue element in array.Elements)
					copy.Elements.Add(StructuredClone(element, seen));
				return copy;
			}
			if (value.Object is JsPrimitiveObject primitive)
				return new JsPrimitiveObject(primitive.Prototype, primitive.Primitive, primitive.Class);
			if (value.Object is JsMap map)
			{
				JsMap copy = new JsMap(map.Prototype, map.IsSet);
				seen[map] = copy;
				foreach (KeyValuePair<JsValue, JsValue> pair in map.Entries())
					copy.Set(StructuredClone(pair.Key, seen), StructuredClone(pair.Value, seen));
				return copy;
			}
			JsObject result = value.Object.Class == "Error" ? new JsObject(value.Object.Prototype) { Class = "Error" } : NewObject();
			seen[value.Object] = result;
			foreach (string key in value.Object.Class == "Error" ? new List<string> { "name", "message", "stack", "cause" } : OwnEnumerableKeys(value.Object))
			{
				if (value.Object.HasProperty(key))
					result.Put(key, StructuredClone(value.Object.Get(key, this, value), seen), value.Object.Class != "Error");
			}
			return result;
		}

		public static JsValue ParseInt(string text, JsValue radixValue)
		{
			text = text.Trim();
			int sign = 1;
			if (text.StartsWith("-", StringComparison.Ordinal))
			{
				sign = -1;
				text = text.Substring(1);
			}
			else if (text.StartsWith("+", StringComparison.Ordinal))
				text = text.Substring(1);
			int radix = radixValue.IsUndefined ? 0 : radixValue.ToInt32();
			if (radix == 0)
				radix = 10;
			if ((radix == 10 || radix == 16) && text.StartsWith("0x", StringComparison.OrdinalIgnoreCase) && (radixValue.IsUndefined || radix == 16))
			{
				radix = 16;
				text = text.Substring(2);
			}
			if (radix < 2 || radix > 36)
				return double.NaN;
			double result = 0;
			int digits = 0;
			foreach (char c in text)
			{
				int digit = c >= '0' && c <= '9' ? c - '0' : c >= 'a' && c <= 'z' ? c - 'a' + 10 : c >= 'A' && c <= 'Z' ? c - 'A' + 10 : 99;
				if (digit >= radix)
					break;
				result = result * radix + digit;
				digits++;
			}
			return digits == 0 ? double.NaN : sign * result;
		}

		public static JsValue ParseFloat(string text)
		{
			text = text.TrimStart();
			int end = 0;
			if (end < text.Length && (text[end] == '+' || text[end] == '-'))
				end++;
			if (string.CompareOrdinal(text, end, "Infinity", 0, 8) == 0)
				return text[0] == '-' ? double.NegativeInfinity : double.PositiveInfinity;
			int digits = 0;
			while (end < text.Length && char.IsDigit(text[end]))
			{
				end++;
				digits++;
			}
			if (end < text.Length && text[end] == '.')
			{
				end++;
				while (end < text.Length && char.IsDigit(text[end]))
				{
					end++;
					digits++;
				}
			}
			if (digits == 0)
				return double.NaN;
			if (end < text.Length && (text[end] == 'e' || text[end] == 'E'))
			{
				int save = end;
				end++;
				if (end < text.Length && (text[end] == '+' || text[end] == '-'))
					end++;
				int exponentDigits = 0;
				while (end < text.Length && char.IsDigit(text[end]))
				{
					end++;
					exponentDigits++;
				}
				if (exponentDigits == 0)
					end = save;
			}
			return double.TryParse(text.Substring(0, end), NumberStyles.Float, CultureInfo.InvariantCulture, out double value) ? value : double.NaN;
		}

		private static string UriEncode(string text, string unreserved)
		{
			StringBuilder builder = new StringBuilder(text.Length + 8);
			byte[] buffer = new byte[4];
			for (int i = 0; i < text.Length; i++)
			{
				char c = text[i];
				if (c < 128 && (char.IsAsciiLetterOrDigit(c) || unreserved.IndexOf(c) >= 0))
				{
					builder.Append(c);
					continue;
				}
				int length;
				if (char.IsHighSurrogate(c) && i + 1 < text.Length && char.IsLowSurrogate(text[i + 1]))
				{
					length = Encoding.UTF8.GetBytes(text, i, 2, buffer, 0);
					i++;
				}
				else if (char.IsSurrogate(c))
					throw (Exception)Current?.NewUriError() ?? new InvalidOperationException("URI malformed");
				else
					length = Encoding.UTF8.GetBytes(text, i, 1, buffer, 0);
				for (int b = 0; b < length; b++)
					builder.Append('%').Append(buffer[b].ToString("X2", CultureInfo.InvariantCulture));
			}
			return builder.ToString();
		}

		private static string UriDecode(string text, string reserved)
		{
			if (text.IndexOf('%') < 0)
				return text;
			List<byte> bytes = new List<byte>();
			StringBuilder builder = new StringBuilder(text.Length);
			for (int i = 0; i < text.Length; i++)
			{
				if (text[i] != '%')
				{
					Flush(bytes, builder);
					builder.Append(text[i]);
					continue;
				}
				if (i + 2 >= text.Length || !int.TryParse(text.AsSpan(i + 1, 2), NumberStyles.HexNumber, CultureInfo.InvariantCulture, out int value))
					throw (Exception)Current?.NewUriError() ?? new InvalidOperationException("URI malformed");
				if (value < 128 && reserved.IndexOf((char)value) >= 0)
				{
					Flush(bytes, builder);
					builder.Append(text, i, 3);
				}
				else
					bytes.Add((byte)value);
				i += 2;
			}
			Flush(bytes, builder);
			return builder.ToString();
		}

		private static void Flush(List<byte> bytes, StringBuilder builder)
		{
			if (bytes.Count == 0)
				return;
			builder.Append(Encoding.UTF8.GetString(bytes.ToArray()));
			bytes.Clear();
		}

		private JsThrow NewUriError() => new JsThrow(NewError("URIError", "URI malformed"));

		// console

		private void SetupConsole()
		{
			JsObject console = NewObject();
			Global.Put("console", console, false);
			foreach (string level in new[] { "log", "info", "warn", "error", "debug", "trace", "dir", "table" })
			{
				string name = level;
				DefineMethod(console, name, 0, (i, t, a) =>
				{
					StringBuilder builder = new StringBuilder();
					for (int n = 0; n < a.Length; n++)
						builder.Append(n > 0 ? " " : "").Append(ToDisplayString(a[n]));
					OnConsole?.Invoke(name, builder.ToString());
					return JsValue.Undefined;
				});
			}
			DefineMethod(console, "assert", 0, (i, t, a) =>
			{
				if (!Arg(a, 0).Truthy)
					OnConsole?.Invoke("error", "Assertion failed" + (a.Length > 1 ? ": " + ToDisplayString(a[1]) : ""));
				return JsValue.Undefined;
			});
			foreach (string noop in new[] { "group", "groupCollapsed", "groupEnd", "time", "timeEnd", "timeLog", "count", "countReset", "clear", "profile", "profileEnd", "timeStamp" })
				DefineMethod(console, noop, 0, (i, t, a) => JsValue.Undefined);
		}

		// Reflect

		private void SetupReflect()
		{
			JsObject reflect = NewObject();
			Global.Put("Reflect", reflect, false);
			DefineMethod(reflect, "ownKeys", 1, (i, t, a) =>
			{
				JsArray result = NewArray();
				foreach (string key in OwnKeys(RequireObject(Arg(a, 0), "Reflect.ownKeys target"), true))
					result.Elements.Add(key.StartsWith("@@", StringComparison.Ordinal) ? SymbolFor(key) : key);
				return result;
			});
			DefineMethod(reflect, "has", 2, (i, t, a) => RequireObject(Arg(a, 0), "Reflect.has target").HasProperty(ToPropertyKey(Arg(a, 1))));
			DefineMethod(reflect, "get", 2, (i, t, a) => RequireObject(Arg(a, 0), "Reflect.get target").Get(ToPropertyKey(Arg(a, 1)), this, a.Length > 2 ? a[2] : a[0]));
			DefineMethod(reflect, "set", 3, (i, t, a) =>
			{
				RequireObject(Arg(a, 0), "Reflect.set target").Set(ToPropertyKey(Arg(a, 1)), Arg(a, 2), this, a.Length > 3 ? a[3] : a[0]);
				return true;
			});
			DefineMethod(reflect, "getPrototypeOf", 1, (i, t, a) => (JsValue)RequireObject(Arg(a, 0), "Reflect.getPrototypeOf target").Prototype);
			DefineMethod(reflect, "setPrototypeOf", 2, (i, t, a) =>
			{
				RequireObject(Arg(a, 0), "Reflect.setPrototypeOf target").Prototype = Arg(a, 1).Object;
				return true;
			});
			DefineMethod(reflect, "apply", 3, (i, t, a) => Call(RequireCallable(Arg(a, 0), "Reflect.apply target"), Arg(a, 1), ArgumentsFromArrayLike(Arg(a, 2))));
			DefineMethod(reflect, "construct", 2, (i, t, a) => Construct(Arg(a, 0), ArgumentsFromArrayLike(Arg(a, 1)), a.Length > 2 ? a[2] : Arg(a, 0)));
			DefineMethod(reflect, "defineProperty", 3, (i, t, a) =>
			{
				try
				{
					DefineProperty(RequireObject(Arg(a, 0), "Reflect.defineProperty target"), ToPropertyKey(Arg(a, 1)), Arg(a, 2));
					return true;
				}
				catch (JsThrow)
				{
					return false;
				}
			});
			DefineMethod(reflect, "deleteProperty", 2, (i, t, a) => RequireObject(Arg(a, 0), "Reflect.deleteProperty target").Delete(ToPropertyKey(Arg(a, 1))));
			DefineMethod(reflect, "getOwnPropertyDescriptor", 2, (i, t, a) => FromDescriptor(RequireObject(Arg(a, 0), "Reflect.getOwnPropertyDescriptor target").GetOwnProperty(ToPropertyKey(Arg(a, 1)))));
			DefineMethod(reflect, "isExtensible", 1, (i, t, a) => RequireObject(Arg(a, 0), "Reflect.isExtensible target").Extensible);
			DefineMethod(reflect, "preventExtensions", 1, (i, t, a) =>
			{
				RequireObject(Arg(a, 0), "Reflect.preventExtensions target").Extensible = false;
				return true;
			});
		}
	}
}
