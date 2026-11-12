using System;
using System.Collections.Generic;

namespace Alloy.Sdk.Script
{
	// Map, Set, WeakMap and WeakSet, all over one insertion-ordered table keyed by SameValueZero
	public sealed partial class Interpreter
	{
		private void SetupCollections()
		{
			JsFunction map = NewConstructor("Map", 0, MapPrototype, null, (i, a, target) => ConstructMap(a, target, MapPrototype, false, false));
			JsFunction set = NewConstructor("Set", 0, SetPrototype, null, (i, a, target) => ConstructMap(a, target, SetPrototype, true, false));
			NewConstructor("WeakMap", 0, WeakMapPrototype, null, (i, a, target) => ConstructMap(a, target, WeakMapPrototype, false, true));
			NewConstructor("WeakSet", 0, WeakSetPrototype, null, (i, a, target) => ConstructMap(a, target, WeakSetPrototype, true, true));
			DefineMethod(map, "groupBy", 2, (i, t, a) =>
			{
				JsFunction callback = RequireCallable(Arg(a, 1), "callback");
				JsMap result = new JsMap(MapPrototype, false);
				int index = 0;
				foreach (JsValue item in Iterate(Arg(a, 0)))
				{
					JsValue key = Call(callback, JsValue.Undefined, new JsValue[] { item, index++ });
					JsValue group = result.Get(key);
					if (group.IsUndefined)
					{
						group = NewArray();
						result.Set(key, group);
					}
					((JsArray)group.Object).Elements.Add(item);
				}
				return result;
			});
			DefineMethod(ObjectConstructor, "groupBy", 2, (i, t, a) =>
			{
				JsFunction callback = RequireCallable(Arg(a, 1), "callback");
				JsObject result = new JsObject(null);
				int index = 0;
				foreach (JsValue item in Iterate(Arg(a, 0)))
				{
					string key = ToPropertyKey(Call(callback, JsValue.Undefined, new JsValue[] { item, index++ }));
					JsValue group = result.Get(key, this);
					if (group.IsUndefined)
					{
						group = NewArray();
						result.Put(key, group);
					}
					((JsArray)group.Object).Elements.Add(item);
				}
				return result;
			});
			foreach (JsObject prototype in new[] { MapPrototype, WeakMapPrototype })
			{
				DefineMethod(prototype, "get", 1, (i, t, a) => ThisMap(t).Get(Arg(a, 0)));
				DefineMethod(prototype, "set", 2, (i, t, a) =>
				{
					ThisMap(t).Set(Arg(a, 0), Arg(a, 1));
					return t;
				});
			}
			foreach (JsObject prototype in new[] { SetPrototype, WeakSetPrototype })
			{
				DefineMethod(prototype, "add", 1, (i, t, a) =>
				{
					ThisMap(t).Set(Arg(a, 0), Arg(a, 0));
					return t;
				});
			}
			foreach (JsObject prototype in new[] { MapPrototype, SetPrototype, WeakMapPrototype, WeakSetPrototype })
			{
				DefineMethod(prototype, "has", 1, (i, t, a) => ThisMap(t).Has(Arg(a, 0)));
				DefineMethod(prototype, "delete", 1, (i, t, a) => ThisMap(t).Remove(Arg(a, 0)));
				prototype.Put(SymbolToStringTag, prototype.Class, false);
			}
			foreach (JsObject prototype in new[] { MapPrototype, SetPrototype })
			{
				bool isSet = prototype == SetPrototype;
				DefineMethod(prototype, "clear", 0, (i, t, a) =>
				{
					ThisMap(t).Clear();
					return JsValue.Undefined;
				});
				DefineGetter(prototype, "size", (i, t, a) => ThisMap(t).Count);
				DefineMethod(prototype, "forEach", 1, (i, t, a) =>
				{
					JsFunction callback = RequireCallable(Arg(a, 0), Describe(a, 0));
					foreach (KeyValuePair<JsValue, JsValue> pair in ThisMap(t).Entries())
						Call(callback, Arg(a, 1), new[] { pair.Value, pair.Key, t });
					return JsValue.Undefined;
				});
				DefineMethod(prototype, "keys", 0, (i, t, a) => MapIterator(ThisMap(t), MapIterationKind.Keys));
				DefineMethod(prototype, "entries", 0, (i, t, a) => MapIterator(ThisMap(t), MapIterationKind.Entries));
				JsFunction values = DefineMethod(prototype, "values", 0, (i, t, a) => MapIterator(ThisMap(t), MapIterationKind.Values));
				if (isSet)
					prototype.Put("keys", values, false);
				prototype.Put(SymbolIterator, isSet ? values : prototype.Get("entries", this).Function, false);
			}
			foreach (string name in new[] { "union", "intersection", "difference", "symmetricDifference" })
			{
				string op = name;
				DefineMethod(SetPrototype, op, 1, (i, t, a) =>
				{
					JsMap self = ThisMap(t);
					JsMap other = ThisMap(Arg(a, 0));
					JsMap result = new JsMap(SetPrototype, true);
					foreach (KeyValuePair<JsValue, JsValue> pair in self.Entries())
					{
						bool inOther = other.Has(pair.Key);
						if (op == "union" || op == "intersection" && inOther || (op == "difference" || op == "symmetricDifference") && !inOther)
							result.Set(pair.Key, pair.Key);
					}
					if (op == "union" || op == "symmetricDifference")
					{
						foreach (KeyValuePair<JsValue, JsValue> pair in other.Entries())
						{
							if (op == "union" || !self.Has(pair.Key))
								result.Set(pair.Key, pair.Key);
						}
					}
					return result;
				});
			}
			DefineMethod(SetPrototype, "isSubsetOf", 1, (i, t, a) =>
			{
				JsMap other = ThisMap(Arg(a, 0));
				foreach (KeyValuePair<JsValue, JsValue> pair in ThisMap(t).Entries())
				{
					if (!other.Has(pair.Key))
						return false;
				}
				return true;
			});
			DefineMethod(SetPrototype, "isSupersetOf", 1, (i, t, a) =>
			{
				JsMap self = ThisMap(t);
				foreach (KeyValuePair<JsValue, JsValue> pair in ThisMap(Arg(a, 0)).Entries())
				{
					if (!self.Has(pair.Key))
						return false;
				}
				return true;
			});
			DefineMethod(SetPrototype, "isDisjointFrom", 1, (i, t, a) =>
			{
				JsMap self = ThisMap(t);
				foreach (KeyValuePair<JsValue, JsValue> pair in ThisMap(Arg(a, 0)).Entries())
				{
					if (self.Has(pair.Key))
						return false;
				}
				return true;
			});
		}

		private JsValue ConstructMap(JsValue[] arguments, JsObject newTarget, JsObject fallback, bool isSet, bool weak)
		{
			JsMap result = new JsMap(PrototypeFromConstructor(newTarget, fallback), isSet) { Class = fallback.Class };
			JsValue source = Arg(arguments, 0);
			if (source.IsNullish)
				return result;
			JsValue adder = GetMember(result, isSet ? "add" : "set");
			foreach (JsValue item in Iterate(source))
			{
				if (isSet)
					Call(adder, result, new[] { item });
				else
				{
					if (!item.IsObject)
						throw TypeError("Iterator value " + item.ToJsString() + " is not an entry object");
					Call(adder, result, new[] { GetMember(item, "0"), GetMember(item, "1") });
				}
			}
			return result;
		}

		private JsMap ThisMap(JsValue thisValue)
		{
			if (thisValue.IsObject && thisValue.Object is JsMap map)
				return map;
			throw TypeError("Method called on incompatible receiver " + thisValue.ToJsString());
		}

		private JsValue MapIterator(JsMap map, MapIterationKind kind)
		{
			JsArray snapshot = NewArray();
			foreach (KeyValuePair<JsValue, JsValue> pair in map.Entries())
			{
				switch (kind)
				{
					case MapIterationKind.Keys:
						snapshot.Elements.Add(pair.Key);
						break;
					case MapIterationKind.Values:
						snapshot.Elements.Add(pair.Value);
						break;
					default:
						snapshot.Elements.Add(NewArray(new List<JsValue> { pair.Key, pair.Value }));
						break;
				}
			}
			return NewArrayIterator(snapshot, ArrayIterationKind.Values);
		}
	}

	internal enum MapIterationKind
	{
		Keys,
		Values,
		Entries,
	}

	// Insertion-ordered entries with SameValueZero keys; deleted entries leave a hole until the next compaction
	public sealed class JsMap : JsObject
	{
		private readonly Dictionary<MapKey, int> _index = new Dictionary<MapKey, int>();
		private readonly List<KeyValuePair<JsValue, JsValue>> _entries = new List<KeyValuePair<JsValue, JsValue>>();
		private readonly List<bool> _live = new List<bool>();
		public readonly bool IsSet;

		public JsMap(JsObject prototype, bool isSet) : base(prototype)
		{
			Class = isSet ? "Set" : "Map";
			IsSet = isSet;
		}

		public int Count => _index.Count;

		public bool Has(JsValue key) => _index.ContainsKey(new MapKey(key));

		public JsValue Get(JsValue key) => _index.TryGetValue(new MapKey(key), out int at) ? _entries[at].Value : JsValue.Undefined;

		public void Set(JsValue key, JsValue value)
		{
			if (key.IsNumber && key.Number == 0)
				key = 0;
			MapKey k = new MapKey(key);
			if (_index.TryGetValue(k, out int at))
			{
				_entries[at] = new KeyValuePair<JsValue, JsValue>(_entries[at].Key, value);
				return;
			}
			_index[k] = _entries.Count;
			_entries.Add(new KeyValuePair<JsValue, JsValue>(key, value));
			_live.Add(true);
		}

		public bool Remove(JsValue key)
		{
			MapKey k = new MapKey(key);
			if (!_index.TryGetValue(k, out int at))
				return false;
			_index.Remove(k);
			_live[at] = false;
			if (_entries.Count > 32 && _index.Count < _entries.Count / 2)
				Compact();
			return true;
		}

		public void Clear()
		{
			_index.Clear();
			_entries.Clear();
			_live.Clear();
		}

		private void Compact()
		{
			List<KeyValuePair<JsValue, JsValue>> kept = new List<KeyValuePair<JsValue, JsValue>>(_index.Count);
			for (int i = 0; i < _entries.Count; i++)
			{
				if (_live[i])
					kept.Add(_entries[i]);
			}
			_entries.Clear();
			_live.Clear();
			_index.Clear();
			foreach (KeyValuePair<JsValue, JsValue> entry in kept)
			{
				_index[new MapKey(entry.Key)] = _entries.Count;
				_entries.Add(entry);
				_live.Add(true);
			}
		}

		// Entries in insertion order, tolerating additions and deletions while iterating
		public IEnumerable<KeyValuePair<JsValue, JsValue>> Entries()
		{
			for (int i = 0; i < _entries.Count; i++)
			{
				if (_live[i])
					yield return _entries[i];
			}
		}

		private readonly struct MapKey : IEquatable<MapKey>
		{
			private readonly JsValue _value;

			public MapKey(JsValue value)
			{
				_value = value;
			}

			public bool Equals(MapKey other) => JsValue.SameValueZero(_value, other._value);

			public override bool Equals(object obj) => obj is MapKey other && Equals(other);

			public override int GetHashCode()
			{
				switch (_value.Type)
				{
					case JsType.Undefined:
						return 1;
					case JsType.Null:
						return 2;
					case JsType.Boolean:
						return _value.Boolean ? 3 : 4;
					case JsType.Number:
						return double.IsNaN(_value.Number) ? 5 : (_value.Number == 0 ? 0 : _value.Number.GetHashCode());
					case JsType.String:
						return _value.String.GetHashCode();
					default:
						return System.Runtime.CompilerServices.RuntimeHelpers.GetHashCode(_value.Object);
				}
			}
		}
	}
}
