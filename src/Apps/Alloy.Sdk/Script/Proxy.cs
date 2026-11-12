using System;
using System.Collections.Generic;

namespace Alloy.Sdk.Script
{
	// The traps shared by proxies of plain objects and of functions
	internal sealed class ProxyTraps
	{
		public JsObject Target;
		public JsObject Handler;
		public bool Revoked;

		public JsValue Trap(Interpreter interpreter, string name)
		{
			if (Revoked)
				throw interpreter.TypeError("Cannot perform '" + name + "' on a proxy that has been revoked");
			return Handler.Get(name, interpreter, Handler);
		}

		public JsProperty GetOwnProperty(Interpreter interpreter, JsObject proxy, string key)
		{
			JsValue trap = Trap(interpreter, "getOwnPropertyDescriptor");
			if (!trap.IsCallable)
				return Target.GetOwnProperty(key);
			JsValue descriptor = interpreter.Call(trap, Handler, new JsValue[] { Target, KeyValue(interpreter, key) });
			if (!descriptor.IsObject)
				return null;
			JsProperty property = new JsProperty
			{
				Enumerable = interpreter.GetMember(descriptor, "enumerable").Truthy,
				Configurable = interpreter.GetMember(descriptor, "configurable").Truthy,
				Writable = interpreter.GetMember(descriptor, "writable").Truthy,
				Value = interpreter.GetMember(descriptor, "value"),
			};
			JsValue getter = interpreter.GetMember(descriptor, "get");
			JsValue setter = interpreter.GetMember(descriptor, "set");
			if (getter.IsCallable)
				property.Getter = getter.Function;
			if (setter.IsCallable)
				property.Setter = setter.Function;
			return property;
		}

		public JsValue Get(Interpreter interpreter, JsObject proxy, string key, JsValue receiver)
		{
			JsValue trap = Trap(interpreter, "get");
			if (!trap.IsCallable)
				return Target.Get(key, interpreter, receiver.IsObject && ReferenceEquals(receiver.Object, proxy) ? Target : receiver);
			return interpreter.Call(trap, Handler, new JsValue[] { Target, KeyValue(interpreter, key), receiver });
		}

		public void Set(Interpreter interpreter, JsObject proxy, string key, JsValue value, JsValue receiver)
		{
			JsValue trap = Trap(interpreter, "set");
			if (!trap.IsCallable)
			{
				Target.Set(key, value, interpreter, receiver.IsObject && ReferenceEquals(receiver.Object, proxy) ? Target : receiver);
				return;
			}
			interpreter.Call(trap, Handler, new JsValue[] { Target, KeyValue(interpreter, key), value, receiver });
		}

		public bool Has(Interpreter interpreter, string key)
		{
			JsValue trap = Trap(interpreter, "has");
			if (!trap.IsCallable)
				return Target.HasProperty(key);
			return interpreter.Call(trap, Handler, new JsValue[] { Target, KeyValue(interpreter, key) }).Truthy;
		}

		public bool Delete(Interpreter interpreter, string key)
		{
			JsValue trap = Trap(interpreter, "deleteProperty");
			if (!trap.IsCallable)
				return Target.Delete(key);
			return interpreter.Call(trap, Handler, new JsValue[] { Target, KeyValue(interpreter, key) }).Truthy;
		}

		public void DefineOwn(Interpreter interpreter, string key, JsProperty property)
		{
			JsValue trap = Trap(interpreter, "defineProperty");
			if (!trap.IsCallable)
			{
				Target.DefineOwn(key, property);
				return;
			}
			JsObject descriptor = interpreter.NewObject();
			if (property.IsAccessor)
			{
				descriptor.Put("get", property.Getter == null ? JsValue.Undefined : property.Getter);
				descriptor.Put("set", property.Setter == null ? JsValue.Undefined : property.Setter);
			}
			else
			{
				descriptor.Put("value", property.Value);
				descriptor.Put("writable", property.Writable);
			}
			descriptor.Put("enumerable", property.Enumerable);
			descriptor.Put("configurable", property.Configurable);
			interpreter.Call(trap, Handler, new JsValue[] { Target, KeyValue(interpreter, key), descriptor });
		}

		public IEnumerable<string> OwnKeys(Interpreter interpreter)
		{
			JsValue trap = Trap(interpreter, "ownKeys");
			if (!trap.IsCallable)
				return interpreter.OwnKeys(Target, true);
			List<string> keys = new List<string>();
			foreach (JsValue key in interpreter.Iterate(interpreter.Call(trap, Handler, new JsValue[] { Target })))
				keys.Add(interpreter.ToPropertyKey(key));
			return keys;
		}

		// Property keys cross into script as strings, or as the symbol objects the hidden names stand for
		private static JsValue KeyValue(Interpreter interpreter, string key) => key.StartsWith("@@", StringComparison.Ordinal) ? interpreter.SymbolFor(key) : key;
	}

	public sealed class JsProxy : JsObject
	{
		internal readonly ProxyTraps Traps;
		private readonly Interpreter _interpreter;

		internal JsProxy(Interpreter interpreter, ProxyTraps traps) : base(traps.Target.Prototype)
		{
			_interpreter = interpreter;
			Traps = traps;
			Class = traps.Target.Class;
		}

		public override JsProperty GetOwnProperty(string key) => Traps.GetOwnProperty(_interpreter, this, key);

		public override JsValue Get(string key, Interpreter interpreter, JsValue receiver) => Traps.Get(interpreter, this, key, receiver);

		public override void Set(string key, JsValue value, Interpreter interpreter, JsValue receiver) => Traps.Set(interpreter, this, key, value, receiver);

		public override bool HasProperty(string key) => Traps.Has(_interpreter, key);

		public override bool Delete(string key) => Traps.Delete(_interpreter, key);

		public override void DefineOwn(string key, JsProperty property) => Traps.DefineOwn(_interpreter, key, property);

		public override IEnumerable<string> OwnKeys => Traps.OwnKeys(_interpreter);

		public override JsValue ToPrimitive(bool preferNumber) => Traps.Target.ToPrimitive(preferNumber);
	}

	// A proxy whose target is callable is callable itself, through the apply and construct traps
	public sealed class JsProxyFunction : JsFunction
	{
		internal readonly ProxyTraps Traps;
		private readonly Interpreter _interpreter;

		internal JsProxyFunction(Interpreter interpreter, ProxyTraps traps) : base(traps.Target.Prototype)
		{
			_interpreter = interpreter;
			Traps = traps;
			JsFunction target = (JsFunction)traps.Target;
			Name = target.Name;
			ExpectedLength = target.ExpectedLength;
			Native = (i, t, a) =>
			{
				JsValue apply = traps.Trap(i, "apply");
				if (!apply.IsCallable)
					return i.Call(target, t, a);
				return i.Call(apply, traps.Handler, new JsValue[] { target, t, i.NewArray(new List<JsValue>(a)) });
			};
			if (target.IsConstructor)
			{
				NativeNew = (i, a, newTarget) =>
				{
					JsValue construct = traps.Trap(i, "construct");
					JsValue actualTarget = newTarget == null || ReferenceEquals(newTarget, this) ? (JsValue)target : newTarget;
					if (!construct.IsCallable)
						return i.Construct(target, a, actualTarget);
					JsValue result = i.Call(construct, traps.Handler, new JsValue[] { target, i.NewArray(new List<JsValue>(a)), actualTarget });
					if (!result.IsObject)
						throw i.TypeError("proxy [[Construct]] must return an object");
					return result;
				};
			}
		}

		public override JsProperty GetOwnProperty(string key) => Traps.GetOwnProperty(_interpreter, this, key);

		public override JsValue Get(string key, Interpreter interpreter, JsValue receiver) => Traps.Get(interpreter, this, key, receiver);

		public override void Set(string key, JsValue value, Interpreter interpreter, JsValue receiver) => Traps.Set(interpreter, this, key, value, receiver);

		public override bool HasProperty(string key) => Traps.Has(_interpreter, key);

		public override bool Delete(string key) => Traps.Delete(_interpreter, key);

		public override void DefineOwn(string key, JsProperty property) => Traps.DefineOwn(_interpreter, key, property);

		public override IEnumerable<string> OwnKeys => Traps.OwnKeys(_interpreter);
	}

	public sealed partial class Interpreter
	{
		private void SetupProxy()
		{
			JsFunction proxy = NewNative("Proxy", 2, (i, t, a) => throw TypeError("Constructor Proxy requires 'new'"));
			proxy.NativeNew = (i, a, target) => NewProxy(Arg(a, 0), Arg(a, 1));
			Global.Put("Proxy", proxy, false);
			DefineMethod(proxy, "revocable", 2, (i, t, a) =>
			{
				JsValue created = NewProxy(Arg(a, 0), Arg(a, 1));
				ProxyTraps traps = created.Object is JsProxy p ? p.Traps : ((JsProxyFunction)created.Object).Traps;
				JsObject result = NewObject();
				result.Put("proxy", created);
				result.Put("revoke", NewNative("revoke", 0, (ii, tt, aa) =>
				{
					traps.Revoked = true;
					return JsValue.Undefined;
				}));
				return result;
			});
		}

		private JsValue NewProxy(JsValue target, JsValue handler)
		{
			if (!target.IsObject || !handler.IsObject)
				throw TypeError("Cannot create proxy with a non-object as target or handler");
			ProxyTraps traps = new ProxyTraps { Target = target.Object, Handler = handler.Object };
			if (target.IsCallable)
				return new JsProxyFunction(this, traps);
			return new JsProxy(this, traps);
		}
	}
}
