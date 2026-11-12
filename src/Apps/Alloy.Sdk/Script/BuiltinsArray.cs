using System;
using System.Collections.Generic;
using System.Globalization;
using System.Text;

namespace Alloy.Sdk.Script
{
	// Array and the array iterator
	public sealed partial class Interpreter
	{
		private void SetupArray()
		{
			ArrayConstructor = NewConstructor("Array", 1, ArrayPrototype, (i, t, a) => ConstructArray(a, null), (i, a, target) => ConstructArray(a, target));
			DefineMethod(ArrayConstructor, "isArray", 1, (i, t, a) => Arg(a, 0).IsObject && a[0].Object is JsArray { Class: "Array" });
			DefineMethod(ArrayConstructor, "of", 0, (i, t, a) => NewArray(new List<JsValue>(a)));
			DefineMethod(ArrayConstructor, "from", 1, (i, t, a) =>
			{
				JsValue source = Arg(a, 0);
				JsFunction map = Arg(a, 1).IsUndefined ? null : RequireCallable(a[1], "Array.from mapper");
				JsArray result = NewArray();
				if (source.IsNullish)
					throw TypeError(source.ToJsString() + " is not iterable");
				bool iterable = source.IsString || source.IsObject && GetMember(source, SymbolIterator).IsCallable;
				IEnumerable<JsValue> items = iterable ? Iterate(source) : ArgumentsFromArrayLike(source);
				int index = 0;
				foreach (JsValue item in items)
				{
					result.Elements.Add(map == null ? item : Call(map, Arg(a, 2), new JsValue[] { item, index }));
					index++;
				}
				return result;
			});
			DefineMethod(ArrayPrototype, "push", 1, (i, t, a) =>
			{
				JsArray array = ThisArray(t, "push");
				CheckWritable(array);
				array.Elements.AddRange(a);
				return array.Elements.Count;
			});
			DefineMethod(ArrayPrototype, "pop", 0, (i, t, a) =>
			{
				JsArray array = ThisArray(t, "pop");
				CheckWritable(array);
				if (array.Elements.Count == 0)
					return JsValue.Undefined;
				JsValue last = array.Elements[array.Elements.Count - 1];
				array.Elements.RemoveAt(array.Elements.Count - 1);
				return last;
			});
			DefineMethod(ArrayPrototype, "shift", 0, (i, t, a) =>
			{
				JsArray array = ThisArray(t, "shift");
				CheckWritable(array);
				if (array.Elements.Count == 0)
					return JsValue.Undefined;
				JsValue first = array.Elements[0];
				array.Elements.RemoveAt(0);
				return first;
			});
			DefineMethod(ArrayPrototype, "unshift", 1, (i, t, a) =>
			{
				JsArray array = ThisArray(t, "unshift");
				CheckWritable(array);
				array.Elements.InsertRange(0, a);
				return array.Elements.Count;
			});
			DefineMethod(ArrayPrototype, "slice", 2, (i, t, a) =>
			{
				List<JsValue> items = ThisElements(t);
				int start = RelativeIndex(Arg(a, 0), items.Count, 0);
				int end = RelativeIndex(Arg(a, 1), items.Count, items.Count);
				return NewArray(end > start ? items.GetRange(start, end - start) : new List<JsValue>());
			});
			DefineMethod(ArrayPrototype, "splice", 2, (i, t, a) =>
			{
				JsArray array = ThisArray(t, "splice");
				CheckWritable(array);
				int start = RelativeIndex(Arg(a, 0), array.Elements.Count, 0);
				int count = a.Length < 2 ? array.Elements.Count - start : (int)Math.Max(0, Math.Min(array.Elements.Count - start, Arg(a, 1).ToNumber()));
				if (a.Length == 0)
					count = 0;
				JsArray removed = NewArray(array.Elements.GetRange(start, count));
				array.Elements.RemoveRange(start, count);
				if (a.Length > 2)
					array.Elements.InsertRange(start, a.AsSpan(2).ToArray());
				return removed;
			});
			DefineMethod(ArrayPrototype, "concat", 1, (i, t, a) =>
			{
				JsArray result = NewArray(new List<JsValue>(ThisElements(t)));
				foreach (JsValue item in a)
				{
					if (item.IsObject && item.Object is JsArray array)
						result.Elements.AddRange(array.Elements);
					else
						result.Elements.Add(item);
				}
				return result;
			});
			DefineMethod(ArrayPrototype, "join", 1, (i, t, a) => Join(ThisElements(t), Arg(a, 0).IsUndefined ? "," : a[0].ToJsString()));
			DefineMethod(ArrayPrototype, "toString", 0, (i, t, a) => Join(ThisElements(t), ","));
			DefineMethod(ArrayPrototype, "toLocaleString", 0, (i, t, a) => Join(ThisElements(t), ","));
			DefineMethod(ArrayPrototype, "reverse", 0, (i, t, a) =>
			{
				JsArray array = ThisArray(t, "reverse");
				CheckWritable(array);
				array.Elements.Reverse();
				return array;
			});
			DefineMethod(ArrayPrototype, "toReversed", 0, (i, t, a) =>
			{
				List<JsValue> copy = new List<JsValue>(ThisElements(t));
				copy.Reverse();
				return NewArray(copy);
			});
			DefineMethod(ArrayPrototype, "indexOf", 1, (i, t, a) =>
			{
				List<JsValue> items = ThisElements(t);
				for (int n = RelativeIndex(Arg(a, 1), items.Count, 0); n < items.Count; n++)
				{
					if (JsValue.StrictEquals(items[n], Arg(a, 0)))
						return n;
				}
				return -1;
			});
			DefineMethod(ArrayPrototype, "lastIndexOf", 1, (i, t, a) =>
			{
				List<JsValue> items = ThisElements(t);
				int from = a.Length > 1 ? RelativeIndex(a[1], items.Count, items.Count - 1) : items.Count - 1;
				for (int n = Math.Min(from, items.Count - 1); n >= 0; n--)
				{
					if (JsValue.StrictEquals(items[n], Arg(a, 0)))
						return n;
				}
				return -1;
			});
			DefineMethod(ArrayPrototype, "includes", 1, (i, t, a) =>
			{
				List<JsValue> items = ThisElements(t);
				for (int n = RelativeIndex(Arg(a, 1), items.Count, 0); n < items.Count; n++)
				{
					if (JsValue.SameValueZero(items[n], Arg(a, 0)))
						return true;
				}
				return false;
			});
			DefineMethod(ArrayPrototype, "at", 1, (i, t, a) =>
			{
				List<JsValue> items = ThisElements(t);
				double n = Math.Truncate(Arg(a, 0).ToNumber());
				if (double.IsNaN(n))
					n = 0;
				if (n < 0)
					n += items.Count;
				return n >= 0 && n < items.Count ? items[(int)n] : JsValue.Undefined;
			});
			DefineMethod(ArrayPrototype, "find", 1, (i, t, a) => FindIn(t, a, false, false));
			DefineMethod(ArrayPrototype, "findIndex", 1, (i, t, a) => FindIn(t, a, true, false));
			DefineMethod(ArrayPrototype, "findLast", 1, (i, t, a) => FindIn(t, a, false, true));
			DefineMethod(ArrayPrototype, "findLastIndex", 1, (i, t, a) => FindIn(t, a, true, true));
			DefineMethod(ArrayPrototype, "filter", 1, (i, t, a) =>
			{
				JsFunction callback = RequireCallable(Arg(a, 0), Describe(a, 0));
				List<JsValue> items = ThisElements(t);
				JsArray result = NewArray();
				for (int n = 0; n < items.Count; n++)
				{
					if (Call(callback, Arg(a, 1), new JsValue[] { items[n], n, t }).Truthy)
						result.Elements.Add(items[n]);
				}
				return result;
			});
			DefineMethod(ArrayPrototype, "map", 1, (i, t, a) =>
			{
				JsFunction callback = RequireCallable(Arg(a, 0), Describe(a, 0));
				List<JsValue> items = ThisElements(t);
				JsArray result = NewArray(new List<JsValue>(items.Count));
				for (int n = 0; n < items.Count; n++)
					result.Elements.Add(Call(callback, Arg(a, 1), new JsValue[] { items[n], n, t }));
				return result;
			});
			DefineMethod(ArrayPrototype, "forEach", 1, (i, t, a) =>
			{
				JsFunction callback = RequireCallable(Arg(a, 0), Describe(a, 0));
				List<JsValue> items = ThisElements(t);
				for (int n = 0; n < items.Count; n++)
					Call(callback, Arg(a, 1), new JsValue[] { items[n], n, t });
				return JsValue.Undefined;
			});
			DefineMethod(ArrayPrototype, "some", 1, (i, t, a) =>
			{
				JsFunction callback = RequireCallable(Arg(a, 0), Describe(a, 0));
				List<JsValue> items = ThisElements(t);
				for (int n = 0; n < items.Count; n++)
				{
					if (Call(callback, Arg(a, 1), new JsValue[] { items[n], n, t }).Truthy)
						return true;
				}
				return false;
			});
			DefineMethod(ArrayPrototype, "every", 1, (i, t, a) =>
			{
				JsFunction callback = RequireCallable(Arg(a, 0), Describe(a, 0));
				List<JsValue> items = ThisElements(t);
				for (int n = 0; n < items.Count; n++)
				{
					if (!Call(callback, Arg(a, 1), new JsValue[] { items[n], n, t }).Truthy)
						return false;
				}
				return true;
			});
			DefineMethod(ArrayPrototype, "reduce", 1, (i, t, a) => Reduce(t, a, false));
			DefineMethod(ArrayPrototype, "reduceRight", 1, (i, t, a) => Reduce(t, a, true));
			DefineMethod(ArrayPrototype, "sort", 1, (i, t, a) =>
			{
				JsArray array = ThisArray(t, "sort");
				CheckWritable(array);
				JsFunction compare = Arg(a, 0).IsUndefined ? null : RequireCallable(a[0], "The comparison function");
				SortElements(array.Elements, compare);
				return array;
			});
			DefineMethod(ArrayPrototype, "toSorted", 1, (i, t, a) =>
			{
				List<JsValue> copy = new List<JsValue>(ThisElements(t));
				SortElements(copy, Arg(a, 0).IsUndefined ? null : RequireCallable(a[0], "The comparison function"));
				return NewArray(copy);
			});
			DefineMethod(ArrayPrototype, "flat", 0, (i, t, a) =>
			{
				JsArray result = NewArray();
				Flatten(ThisElements(t), Arg(a, 0).IsUndefined ? 1 : (int)Math.Min(1000, Math.Max(0, Arg(a, 0).ToNumber())), result.Elements);
				return result;
			});
			DefineMethod(ArrayPrototype, "flatMap", 1, (i, t, a) =>
			{
				JsFunction callback = RequireCallable(Arg(a, 0), Describe(a, 0));
				List<JsValue> items = ThisElements(t);
				JsArray result = NewArray();
				for (int n = 0; n < items.Count; n++)
				{
					JsValue mapped = Call(callback, Arg(a, 1), new JsValue[] { items[n], n, t });
					if (mapped.IsObject && mapped.Object is JsArray inner)
						result.Elements.AddRange(inner.Elements);
					else
						result.Elements.Add(mapped);
				}
				return result;
			});
			DefineMethod(ArrayPrototype, "fill", 1, (i, t, a) =>
			{
				JsArray array = ThisArray(t, "fill");
				CheckWritable(array);
				int start = RelativeIndex(Arg(a, 1), array.Elements.Count, 0);
				int end = RelativeIndex(Arg(a, 2), array.Elements.Count, array.Elements.Count);
				for (int n = start; n < end; n++)
					array.Elements[n] = Arg(a, 0);
				return array;
			});
			DefineMethod(ArrayPrototype, "copyWithin", 2, (i, t, a) =>
			{
				JsArray array = ThisArray(t, "copyWithin");
				CheckWritable(array);
				int count = array.Elements.Count;
				int target = RelativeIndex(Arg(a, 0), count, 0);
				int start = RelativeIndex(Arg(a, 1), count, 0);
				int end = RelativeIndex(Arg(a, 2), count, count);
				JsValue[] slice = array.Elements.GetRange(start, Math.Max(0, end - start)).ToArray();
				for (int n = 0; n < slice.Length && target + n < count; n++)
					array.Elements[target + n] = slice[n];
				return array;
			});
			DefineMethod(ArrayPrototype, "with", 2, (i, t, a) =>
			{
				List<JsValue> copy = new List<JsValue>(ThisElements(t));
				double n = Math.Truncate(Arg(a, 0).ToNumber());
				if (n < 0)
					n += copy.Count;
				if (n < 0 || n >= copy.Count)
					throw RangeError("Invalid index : " + Arg(a, 0).ToJsString());
				copy[(int)n] = Arg(a, 1);
				return NewArray(copy);
			});
			DefineMethod(ArrayPrototype, "keys", 0, (i, t, a) => NewArrayIterator(t, ArrayIterationKind.Keys));
			DefineMethod(ArrayPrototype, "entries", 0, (i, t, a) => NewArrayIterator(t, ArrayIterationKind.Entries));
			ArrayValues = DefineMethod(ArrayPrototype, "values", 0, (i, t, a) => NewArrayIterator(t, ArrayIterationKind.Values));
			ArrayPrototype.Put(SymbolIterator, ArrayValues, false);
			DefineMethod(ArrayIteratorPrototype, "next", 0, (i, t, a) =>
			{
				if (!t.IsObject || t.Object is not JsArrayIterator iterator)
					throw TypeError("next method called on incompatible receiver");
				return iterator.Next(this);
			});
			ArrayIteratorPrototype.Put(SymbolToStringTag, "Array Iterator", false);
		}

		private JsValue ConstructArray(JsValue[] arguments, JsObject newTarget)
		{
			JsArray array = new JsArray(PrototypeFromConstructor(newTarget, ArrayPrototype));
			if (arguments.Length == 1 && arguments[0].IsNumber)
			{
				double length = arguments[0].Number;
				if (length < 0 || length != Math.Floor(length) || length > 100_000_000)
					throw RangeError("Invalid array length");
				for (int i = 0; i < (int)length; i++)
					array.Elements.Add(JsValue.Undefined);
			}
			else
				array.Elements.AddRange(arguments);
			return array;
		}

		private static string Describe(JsValue[] arguments, int index) => Arg(arguments, index).IsUndefined ? "undefined" : Arg(arguments, index).ToJsString();

		private JsArray ThisArray(JsValue thisValue, string method)
		{
			if (thisValue.IsObject && thisValue.Object is JsArray array)
				return array;
			throw TypeError("Array.prototype." + method + " called on a non-array");
		}

		private void CheckWritable(JsArray array)
		{
			if (array.Frozen)
				throw TypeError("Cannot modify a frozen array");
		}

		// The elements of this: an array's list directly, anything else read through its length
		private List<JsValue> ThisElements(JsValue thisValue)
		{
			if (thisValue.IsObject && thisValue.Object is JsArray array)
				return array.Elements;
			if (thisValue.IsNullish)
				throw TypeError("Array.prototype method called on null or undefined");
			return new List<JsValue>(ArgumentsFromArrayLike(thisValue));
		}

		private string Join(List<JsValue> items, string separator)
		{
			StringBuilder builder = new StringBuilder();
			for (int i = 0; i < items.Count; i++)
			{
				if (i > 0)
					builder.Append(separator);
				if (!items[i].IsNullish)
					builder.Append(items[i].ToJsString());
			}
			return builder.ToString();
		}

		private JsValue FindIn(JsValue thisValue, JsValue[] arguments, bool wantIndex, bool fromEnd)
		{
			JsFunction callback = RequireCallable(Arg(arguments, 0), Describe(arguments, 0));
			List<JsValue> items = ThisElements(thisValue);
			for (int step = 0; step < items.Count; step++)
			{
				int n = fromEnd ? items.Count - 1 - step : step;
				if (n >= items.Count)
					break;
				if (Call(callback, Arg(arguments, 1), new JsValue[] { items[n], n, thisValue }).Truthy)
					return wantIndex ? n : items[n];
			}
			return wantIndex ? -1 : JsValue.Undefined;
		}

		private JsValue Reduce(JsValue thisValue, JsValue[] arguments, bool fromEnd)
		{
			JsFunction callback = RequireCallable(Arg(arguments, 0), Describe(arguments, 0));
			List<JsValue> items = ThisElements(thisValue);
			int index = fromEnd ? items.Count - 1 : 0;
			int step = fromEnd ? -1 : 1;
			JsValue accumulator;
			if (arguments.Length > 1)
				accumulator = arguments[1];
			else
			{
				if (items.Count == 0)
					throw TypeError("Reduce of empty array with no initial value");
				accumulator = items[index];
				index += step;
			}
			for (; index >= 0 && index < items.Count; index += step)
				accumulator = Call(callback, JsValue.Undefined, new JsValue[] { accumulator, items[index], index, thisValue });
			return accumulator;
		}

		private void Flatten(List<JsValue> items, int depth, List<JsValue> into)
		{
			foreach (JsValue item in items)
			{
				if (depth > 0 && item.IsObject && item.Object is JsArray inner)
					Flatten(inner.Elements, depth - 1, into);
				else
					into.Add(item);
			}
		}

		// A stable merge sort; undefined sorts last and the default order compares strings
		public void SortElements(List<JsValue> items, JsFunction compare)
		{
			List<JsValue> defined = new List<JsValue>(items.Count);
			int undefinedCount = 0;
			foreach (JsValue item in items)
			{
				if (item.IsUndefined)
					undefinedCount++;
				else
					defined.Add(item);
			}
			JsValue[] buffer = new JsValue[defined.Count];
			JsValue[] source = defined.ToArray();
			MergeSort(source, buffer, 0, source.Length, compare);
			items.Clear();
			items.AddRange(source);
			for (int i = 0; i < undefinedCount; i++)
				items.Add(JsValue.Undefined);
		}

		private void MergeSort(JsValue[] items, JsValue[] buffer, int start, int end, JsFunction compare)
		{
			if (end - start < 2)
				return;
			int middle = (start + end) / 2;
			MergeSort(items, buffer, start, middle, compare);
			MergeSort(items, buffer, middle, end, compare);
			int left = start;
			int right = middle;
			int output = start;
			while (left < middle && right < end)
				buffer[output++] = SortCompare(items[right], items[left], compare) < 0 ? items[right++] : items[left++];
			while (left < middle)
				buffer[output++] = items[left++];
			while (right < end)
				buffer[output++] = items[right++];
			Array.Copy(buffer, start, items, start, end - start);
		}

		private int SortCompare(JsValue a, JsValue b, JsFunction compare)
		{
			if (compare == null)
				return string.CompareOrdinal(a.ToJsString(), b.ToJsString());
			double result = Call(compare, JsValue.Undefined, new[] { a, b }).ToNumber();
			return double.IsNaN(result) ? 0 : Math.Sign(result);
		}

		private JsValue NewArrayIterator(JsValue iterable, ArrayIterationKind kind)
		{
			if (iterable.IsNullish)
				throw TypeError("Array iterator called on null or undefined");
			return new JsArrayIterator(ArrayIteratorPrototype, iterable, kind);
		}
	}

	internal enum ArrayIterationKind
	{
		Keys,
		Values,
		Entries,
	}

	// Iterates an array or array-like by index, reading the live length each step
	internal sealed class JsArrayIterator : JsObject
	{
		private JsValue _target;
		private readonly ArrayIterationKind _kind;
		private int _index;

		public JsArrayIterator(JsObject prototype, JsValue target, ArrayIterationKind kind) : base(prototype)
		{
			Class = "Array Iterator";
			_target = target;
			_kind = kind;
		}

		public JsValue Next(Interpreter interpreter)
		{
			if (_target.IsUndefined)
				return IteratorResult(interpreter, JsValue.Undefined, true);
			int length = _target.IsObject && _target.Object is JsArray array ? array.Elements.Count : (int)interpreter.GetMember(_target, "length").ToNumber();
			if (_index >= length)
			{
				_target = JsValue.Undefined;
				return IteratorResult(interpreter, JsValue.Undefined, true);
			}
			int index = _index++;
			JsValue value = _kind == ArrayIterationKind.Keys ? index : interpreter.GetMember(_target, index.ToString(CultureInfo.InvariantCulture));
			if (_kind == ArrayIterationKind.Entries)
				value = interpreter.NewArray(new List<JsValue> { index, value });
			return IteratorResult(interpreter, value, false);
		}

		public static JsValue IteratorResult(Interpreter interpreter, JsValue value, bool done)
		{
			JsObject result = interpreter.NewObject();
			result.Put("value", value);
			result.Put("done", done);
			return result;
		}
	}
}
