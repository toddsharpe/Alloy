using System;
using System.Collections.Generic;
using System.Globalization;
using System.Text;

namespace Alloy.Sdk.Script
{
	// A block of bytes typed arrays and DataViews look into
	public sealed class JsArrayBuffer : JsObject
	{
		public byte[] Data;

		public JsArrayBuffer(JsObject prototype, int length) : base(prototype)
		{
			Class = "ArrayBuffer";
			Data = new byte[length];
		}

		public JsArrayBuffer(JsObject prototype, byte[] data) : base(prototype)
		{
			Class = "ArrayBuffer";
			Data = data;
		}
	}

	public enum TypedArrayKind
	{
		Int8,
		Uint8,
		Uint8Clamped,
		Int16,
		Uint16,
		Int32,
		Uint32,
		Float32,
		Float64,
	}

	// A typed array: elements read and written straight into its buffer, indices and length exposed as properties
	public sealed class JsTypedArray : JsObject
	{
		public readonly TypedArrayKind Kind;
		public readonly JsArrayBuffer Buffer;
		public readonly int ByteOffset;
		public readonly int Length;

		public JsTypedArray(JsObject prototype, TypedArrayKind kind, JsArrayBuffer buffer, int byteOffset, int length) : base(prototype)
		{
			Class = Name(kind);
			Kind = kind;
			Buffer = buffer;
			ByteOffset = byteOffset;
			Length = length;
		}

		public static int ElementSize(TypedArrayKind kind) => kind switch
		{
			TypedArrayKind.Int8 or TypedArrayKind.Uint8 or TypedArrayKind.Uint8Clamped => 1,
			TypedArrayKind.Int16 or TypedArrayKind.Uint16 => 2,
			TypedArrayKind.Float64 => 8,
			_ => 4,
		};

		public static string Name(TypedArrayKind kind) => kind + "Array";

		public int ByteLength => Length * ElementSize(Kind);

		public double this[int index]
		{
			get
			{
				int at = ByteOffset + index * ElementSize(Kind);
				byte[] data = Buffer.Data;
				switch (Kind)
				{
					case TypedArrayKind.Int8:
						return (sbyte)data[at];
					case TypedArrayKind.Uint8:
					case TypedArrayKind.Uint8Clamped:
						return data[at];
					case TypedArrayKind.Int16:
						return BitConverter.ToInt16(data, at);
					case TypedArrayKind.Uint16:
						return BitConverter.ToUInt16(data, at);
					case TypedArrayKind.Int32:
						return BitConverter.ToInt32(data, at);
					case TypedArrayKind.Uint32:
						return BitConverter.ToUInt32(data, at);
					case TypedArrayKind.Float32:
						return BitConverter.ToSingle(data, at);
					default:
						return BitConverter.ToDouble(data, at);
				}
			}
			set
			{
				int at = ByteOffset + index * ElementSize(Kind);
				byte[] data = Buffer.Data;
				switch (Kind)
				{
					case TypedArrayKind.Int8:
					case TypedArrayKind.Uint8:
						data[at] = (byte)ToInt(value);
						break;
					case TypedArrayKind.Uint8Clamped:
						data[at] = (byte)(double.IsNaN(value) ? 0 : Math.Clamp(Math.Round(value, MidpointRounding.ToEven), 0, 255));
						break;
					case TypedArrayKind.Int16:
					case TypedArrayKind.Uint16:
						BitConverter.TryWriteBytes(data.AsSpan(at, 2), (ushort)ToInt(value));
						break;
					case TypedArrayKind.Int32:
					case TypedArrayKind.Uint32:
						BitConverter.TryWriteBytes(data.AsSpan(at, 4), (uint)ToInt(value));
						break;
					case TypedArrayKind.Float32:
						BitConverter.TryWriteBytes(data.AsSpan(at, 4), (float)value);
						break;
					default:
						BitConverter.TryWriteBytes(data.AsSpan(at, 8), value);
						break;
				}
			}
		}

		// ToInt32 semantics: modulo 2^32 after truncation, NaN and infinities to zero
		private static int ToInt(double value)
		{
			if (double.IsNaN(value) || double.IsInfinity(value))
				return 0;
			double truncated = Math.Truncate(value) % 4294967296.0;
			if (truncated < 0)
				truncated += 4294967296.0;
			return (int)(uint)truncated;
		}

		public override JsProperty GetOwnProperty(string key)
		{
			if (JsArray.TryIndex(key, out int index))
				return index < Length ? new JsProperty { Value = this[index], Configurable = false } : null;
			return base.GetOwnProperty(key);
		}

		public override JsValue Get(string key, Interpreter interpreter, JsValue receiver)
		{
			if (JsArray.TryIndex(key, out int index))
				return index < Length ? this[index] : JsValue.Undefined;
			return base.Get(key, interpreter, receiver);
		}

		public override void Set(string key, JsValue value, Interpreter interpreter, JsValue receiver)
		{
			if (JsArray.TryIndex(key, out int index))
			{
				if (index < Length)
					this[index] = value.ToNumber();
				return;
			}
			base.Set(key, value, interpreter, receiver);
		}

		public override IEnumerable<string> OwnKeys
		{
			get
			{
				for (int i = 0; i < Length; i++)
					yield return i.ToString(CultureInfo.InvariantCulture);
				foreach (string key in base.OwnKeys)
					yield return key;
			}
		}
	}

	// ArrayBuffer, the nine typed arrays and DataView
	public sealed partial class Interpreter
	{
		public JsObject ArrayBufferPrototype;
		public JsObject TypedArrayPrototype;
		private readonly Dictionary<TypedArrayKind, JsObject> _typedArrayPrototypes = new Dictionary<TypedArrayKind, JsObject>();

		public JsArrayBuffer NewArrayBuffer(byte[] data) => new JsArrayBuffer(ArrayBufferPrototype, data);

		public JsTypedArray NewUint8Array(byte[] data) => new JsTypedArray(_typedArrayPrototypes[TypedArrayKind.Uint8], TypedArrayKind.Uint8, NewArrayBuffer(data), 0, data.Length);

		public JsTypedArray NewTypedArray(TypedArrayKind kind, int length) => new JsTypedArray(_typedArrayPrototypes[kind], kind, new JsArrayBuffer(ArrayBufferPrototype, length * JsTypedArray.ElementSize(kind)), 0, length);

		// The bytes a script handed over as a body or a blob part: a buffer, a view on one, or an array of numbers
		public byte[] BytesOf(JsValue value)
		{
			if (!value.IsObject)
				return null;
			switch (value.Object)
			{
				case JsArrayBuffer buffer:
					return buffer.Data;
				case JsTypedArray view:
				{
					byte[] bytes = new byte[view.ByteLength];
					Array.Copy(view.Buffer.Data, view.ByteOffset, bytes, 0, bytes.Length);
					return bytes;
				}
				case JsArray array:
				{
					byte[] bytes = new byte[array.Elements.Count];
					for (int i = 0; i < bytes.Length; i++)
						bytes[i] = (byte)array.Elements[i].ToInt32();
					return bytes;
				}
				default:
					if (value.Object.Class == "DataView" && value.Object.Get("buffer", this).Object is JsArrayBuffer viewed)
						return viewed.Data;
					return null;
			}
		}

		private void SetupTypedArrays()
		{
			ArrayBufferPrototype = new JsObject(ObjectPrototype) { Class = "ArrayBuffer" };
			JsFunction arrayBuffer = NewConstructor("ArrayBuffer", 1, ArrayBufferPrototype, null, (i, a, target) =>
			{
				double length = Arg(a, 0).IsUndefined ? 0 : Arg(a, 0).ToNumber();
				if (double.IsNaN(length) || length < 0 || length > 256 * 1024 * 1024)
					throw RangeError("Array buffer allocation failed");
				return new JsArrayBuffer(PrototypeFromConstructor(target, ArrayBufferPrototype), (int)length);
			});
			DefineMethod(arrayBuffer, "isView", 1, (i, t, a) => Arg(a, 0).IsObject && (a[0].Object is JsTypedArray || a[0].Object.Class == "DataView"));
			DefineGetter(ArrayBufferPrototype, "byteLength", (i, t, a) => ThisBuffer(t).Data.Length);
			DefineGetter(ArrayBufferPrototype, "maxByteLength", (i, t, a) => ThisBuffer(t).Data.Length);
			DefineGetter(ArrayBufferPrototype, "resizable", (i, t, a) => false);
			DefineGetter(ArrayBufferPrototype, "detached", (i, t, a) => false);
			DefineMethod(ArrayBufferPrototype, "slice", 2, (i, t, a) =>
			{
				byte[] data = ThisBuffer(t).Data;
				int start = RelativeIndex(Arg(a, 0), data.Length, 0);
				int end = RelativeIndex(Arg(a, 1), data.Length, data.Length);
				return NewArrayBuffer(end > start ? data[start..end] : Array.Empty<byte>());
			});
			ArrayBufferPrototype.Put(SymbolToStringTag, "ArrayBuffer", false);
			Global.Put("SharedArrayBuffer", arrayBuffer, false);

			TypedArrayPrototype = new JsObject(ObjectPrototype);
			JsFunction typedArray = NewNative("TypedArray", 0, (i, t, a) => throw TypeError("Abstract class TypedArray not directly constructable"));
			typedArray.Put("prototype", TypedArrayPrototype, false);
			TypedArrayPrototype.Put("constructor", typedArray, false);
			SetupTypedArrayPrototype();
			foreach (TypedArrayKind kind in Enum.GetValues<TypedArrayKind>())
			{
				TypedArrayKind captured = kind;
				JsObject prototype = new JsObject(TypedArrayPrototype) { Class = JsTypedArray.Name(kind) };
				_typedArrayPrototypes[kind] = prototype;
				JsFunction constructor = NewConstructor(JsTypedArray.Name(kind), 3, prototype, null, (i, a, target) => ConstructTypedArray(captured, a, PrototypeFromConstructor(target, prototype)));
				constructor.Prototype = typedArray;
				constructor.Put("BYTES_PER_ELEMENT", JsTypedArray.ElementSize(kind), false);
				prototype.Put("BYTES_PER_ELEMENT", JsTypedArray.ElementSize(kind), false);
				prototype.Put(SymbolToStringTag, JsTypedArray.Name(kind), false);
				DefineMethod(constructor, "from", 1, (i, t, a) =>
				{
					JsFunction map = Arg(a, 1).IsUndefined ? null : RequireCallable(a[1], "mapper");
					List<JsValue> items = new List<JsValue>(Iterate(Arg(a, 0)));
					JsTypedArray result = NewTypedArray(captured, items.Count);
					for (int n = 0; n < items.Count; n++)
						result[n] = (map == null ? items[n] : Call(map, Arg(a, 2), new JsValue[] { items[n], n })).ToNumber();
					return result;
				});
				DefineMethod(constructor, "of", 0, (i, t, a) =>
				{
					JsTypedArray result = NewTypedArray(captured, a.Length);
					for (int n = 0; n < a.Length; n++)
						result[n] = a[n].ToNumber();
					return result;
				});
			}
			SetupDataView();
		}

		private JsArrayBuffer ThisBuffer(JsValue thisValue)
		{
			if (thisValue.IsObject && thisValue.Object is JsArrayBuffer buffer)
				return buffer;
			throw TypeError("Method ArrayBuffer.prototype method called on incompatible receiver");
		}

		private JsTypedArray ThisTyped(JsValue thisValue)
		{
			if (thisValue.IsObject && thisValue.Object is JsTypedArray array)
				return array;
			throw TypeError("this is not a typed array.");
		}

		// new Int32Array(length | iterable | typedArray | buffer, byteOffset, length)
		private JsValue ConstructTypedArray(TypedArrayKind kind, JsValue[] arguments, JsObject prototype)
		{
			int size = JsTypedArray.ElementSize(kind);
			JsValue first = Arg(arguments, 0);
			if (first.IsObject && first.Object is JsArrayBuffer buffer)
			{
				int offset = Arg(arguments, 1).IsUndefined ? 0 : (int)Arg(arguments, 1).ToNumber();
				if (offset < 0 || offset % size != 0 || offset > buffer.Data.Length)
					throw RangeError("Start offset of " + JsTypedArray.Name(kind) + " should be a multiple of " + size);
				int length = Arg(arguments, 2).IsUndefined ? (buffer.Data.Length - offset) / size : (int)Arg(arguments, 2).ToNumber();
				if (length < 0 || offset + length * size > buffer.Data.Length)
					throw RangeError("Invalid typed array length: " + length);
				return new JsTypedArray(prototype, kind, buffer, offset, length);
			}
			if (first.IsObject)
			{
				List<JsValue> items = first.Object is JsTypedArray source ? Elements(source) : new List<JsValue>(first.Object is JsArray || GetMember(first, SymbolIterator).IsCallable ? Iterate(first) : ArgumentsFromArrayLike(first));
				JsTypedArray result = new JsTypedArray(prototype, kind, new JsArrayBuffer(ArrayBufferPrototype, items.Count * size), 0, items.Count);
				for (int i = 0; i < items.Count; i++)
					result[i] = items[i].ToNumber();
				return result;
			}
			double count = first.IsUndefined ? 0 : first.ToNumber();
			if (double.IsNaN(count) || count < 0 || count > 64 * 1024 * 1024)
				throw RangeError("Invalid typed array length: " + first.ToJsString());
			return new JsTypedArray(prototype, kind, new JsArrayBuffer(ArrayBufferPrototype, (int)count * size), 0, (int)count);
		}

		private static List<JsValue> Elements(JsTypedArray array)
		{
			List<JsValue> items = new List<JsValue>(array.Length);
			for (int i = 0; i < array.Length; i++)
				items.Add(array[i]);
			return items;
		}

		private JsTypedArray SameKind(JsTypedArray like, List<JsValue> items)
		{
			JsTypedArray result = NewTypedArray(like.Kind, items.Count);
			for (int i = 0; i < items.Count; i++)
				result[i] = items[i].ToNumber();
			return result;
		}

		private void SetupTypedArrayPrototype()
		{
			JsObject proto = TypedArrayPrototype;
			DefineGetter(proto, "length", (i, t, a) => ThisTyped(t).Length);
			DefineGetter(proto, "byteLength", (i, t, a) => ThisTyped(t).ByteLength);
			DefineGetter(proto, "byteOffset", (i, t, a) => ThisTyped(t).ByteOffset);
			DefineGetter(proto, "buffer", (i, t, a) => ThisTyped(t).Buffer);
			DefineGetter(proto, SymbolToStringTag, (i, t, a) => t.IsObject && t.Object is JsTypedArray array ? array.Class : JsValue.Undefined);
			DefineMethod(proto, "set", 2, (i, t, a) =>
			{
				JsTypedArray target = ThisTyped(t);
				int offset = Arg(a, 1).IsUndefined ? 0 : (int)Arg(a, 1).ToNumber();
				List<JsValue> items = Arg(a, 0).IsObject && a[0].Object is JsTypedArray source ? Elements(source) : new List<JsValue>(ArgumentsFromArrayLike(Arg(a, 0)));
				if (offset < 0 || offset + items.Count > target.Length)
					throw RangeError("offset is out of bounds");
				for (int n = 0; n < items.Count; n++)
					target[offset + n] = items[n].ToNumber();
				return JsValue.Undefined;
			});
			DefineMethod(proto, "subarray", 2, (i, t, a) =>
			{
				JsTypedArray array = ThisTyped(t);
				int start = RelativeIndex(Arg(a, 0), array.Length, 0);
				int end = RelativeIndex(Arg(a, 1), array.Length, array.Length);
				return new JsTypedArray(array.Prototype, array.Kind, array.Buffer, array.ByteOffset + start * JsTypedArray.ElementSize(array.Kind), Math.Max(0, end - start));
			});
			DefineMethod(proto, "slice", 2, (i, t, a) =>
			{
				JsTypedArray array = ThisTyped(t);
				List<JsValue> items = Elements(array);
				int start = RelativeIndex(Arg(a, 0), items.Count, 0);
				int end = RelativeIndex(Arg(a, 1), items.Count, items.Count);
				return SameKind(array, end > start ? items.GetRange(start, end - start) : new List<JsValue>());
			});
			DefineMethod(proto, "fill", 1, (i, t, a) =>
			{
				JsTypedArray array = ThisTyped(t);
				int start = RelativeIndex(Arg(a, 1), array.Length, 0);
				int end = RelativeIndex(Arg(a, 2), array.Length, array.Length);
				double value = Arg(a, 0).ToNumber();
				for (int n = start; n < end; n++)
					array[n] = value;
				return array;
			});
			DefineMethod(proto, "reverse", 0, (i, t, a) =>
			{
				JsTypedArray array = ThisTyped(t);
				for (int lo = 0, hi = array.Length - 1; lo < hi; lo++, hi--)
					(array[lo], array[hi]) = (array[hi], array[lo]);
				return array;
			});
			DefineMethod(proto, "sort", 1, (i, t, a) =>
			{
				JsTypedArray array = ThisTyped(t);
				List<JsValue> items = Elements(array);
				if (Arg(a, 0).IsUndefined)
					items.Sort((x, y) => x.Number.CompareTo(y.Number));
				else
					SortElements(items, RequireCallable(a[0], "The comparison function"));
				for (int n = 0; n < items.Count; n++)
					array[n] = items[n].Number;
				return array;
			});
			DefineMethod(proto, "copyWithin", 2, (i, t, a) =>
			{
				JsTypedArray array = ThisTyped(t);
				int target = RelativeIndex(Arg(a, 0), array.Length, 0);
				int start = RelativeIndex(Arg(a, 1), array.Length, 0);
				int end = RelativeIndex(Arg(a, 2), array.Length, array.Length);
				List<JsValue> items = Elements(array);
				for (int n = 0; start + n < end && target + n < array.Length; n++)
					array[target + n] = items[start + n].Number;
				return array;
			});
			DefineMethod(proto, "join", 1, (i, t, a) =>
			{
				StringBuilder text = new StringBuilder();
				string separator = Arg(a, 0).IsUndefined ? "," : a[0].ToJsString();
				JsTypedArray array = ThisTyped(t);
				for (int n = 0; n < array.Length; n++)
					text.Append(n > 0 ? separator : "").Append(JsValue.NumberToString(array[n]));
				return text.ToString();
			});
			DefineMethod(proto, "toString", 0, (i, t, a) => Call(proto.Get("join", this), t, Array.Empty<JsValue>()));
			DefineMethod(proto, "toLocaleString", 0, (i, t, a) => Call(proto.Get("join", this), t, Array.Empty<JsValue>()));
			DefineMethod(proto, "at", 1, (i, t, a) =>
			{
				JsTypedArray array = ThisTyped(t);
				double n = Math.Truncate(Arg(a, 0).ToNumber());
				if (n < 0)
					n += array.Length;
				return n >= 0 && n < array.Length ? array[(int)n] : JsValue.Undefined;
			});
			foreach (string name in new[] { "indexOf", "lastIndexOf", "includes", "find", "findIndex", "findLast", "findLastIndex", "forEach", "some", "every", "reduce", "reduceRight", "keys", "entries", "values" })
			{
				string method = name;
				DefineMethod(proto, method, 1, (i, t, a) => Call(ArrayPrototype.Get(method, this), NewArray(Elements(ThisTyped(t))), a));
			}
			DefineMethod(proto, "map", 1, (i, t, a) =>
			{
				JsTypedArray array = ThisTyped(t);
				JsValue mapped = Call(ArrayPrototype.Get("map", this), NewArray(Elements(array)), a);
				return SameKind(array, ((JsArray)mapped.Object).Elements);
			});
			DefineMethod(proto, "filter", 1, (i, t, a) =>
			{
				JsTypedArray array = ThisTyped(t);
				JsValue filtered = Call(ArrayPrototype.Get("filter", this), NewArray(Elements(array)), a);
				return SameKind(array, ((JsArray)filtered.Object).Elements);
			});
			DefineMethod(proto, "toReversed", 0, (i, t, a) =>
			{
				JsTypedArray array = ThisTyped(t);
				List<JsValue> items = Elements(array);
				items.Reverse();
				return SameKind(array, items);
			});
			DefineMethod(proto, "toSorted", 1, (i, t, a) =>
			{
				JsTypedArray array = ThisTyped(t);
				List<JsValue> items = Elements(array);
				if (Arg(a, 0).IsUndefined)
					items.Sort((x, y) => x.Number.CompareTo(y.Number));
				else
					SortElements(items, RequireCallable(a[0], "The comparison function"));
				return SameKind(array, items);
			});
			proto.Put(SymbolIterator, proto.Get("values", this), false);
		}

		private void SetupDataView()
		{
			JsObject proto = new JsObject(ObjectPrototype) { Class = "DataView" };
			proto.Put(SymbolToStringTag, "DataView", false);
			NewConstructor("DataView", 1, proto, null, (i, a, target) =>
			{
				if (!(Arg(a, 0).IsObject && a[0].Object is JsArrayBuffer buffer))
					throw TypeError("First argument to DataView constructor must be an ArrayBuffer");
				int offset = Arg(a, 1).IsUndefined ? 0 : (int)Arg(a, 1).ToNumber();
				int length = Arg(a, 2).IsUndefined ? buffer.Data.Length - offset : (int)Arg(a, 2).ToNumber();
				if (offset < 0 || length < 0 || offset + length > buffer.Data.Length)
					throw RangeError("Start offset is outside the bounds of the buffer");
				JsObject view = new JsObject(PrototypeFromConstructor(target, proto)) { Class = "DataView" };
				view.Put("buffer", buffer, false);
				view.Put("byteOffset", offset, false);
				view.Put("byteLength", length, false);
				return view;
			});
			foreach (TypedArrayKind kind in Enum.GetValues<TypedArrayKind>())
			{
				if (kind == TypedArrayKind.Uint8Clamped)
					continue;
				TypedArrayKind captured = kind;
				int size = JsTypedArray.ElementSize(kind);
				DefineMethod(proto, "get" + kind, 1, (i, t, a) =>
				{
					JsArrayBuffer buffer = (JsArrayBuffer)t.Object.Get("buffer", this).Object;
					int at = (int)t.Object.Get("byteOffset", this).Number + Arg(a, 0).ToInt32();
					if (at < 0 || at + size > buffer.Data.Length)
						throw RangeError("Offset is outside the bounds of the DataView");
					byte[] bytes = buffer.Data[at..(at + size)];
					if (!Arg(a, 1).Truthy && size > 1)
						Array.Reverse(bytes);
					return new JsTypedArray(_typedArrayPrototypes[captured], captured, new JsArrayBuffer(ArrayBufferPrototype, bytes), 0, 1)[0];
				});
				DefineMethod(proto, "set" + kind, 2, (i, t, a) =>
				{
					JsArrayBuffer buffer = (JsArrayBuffer)t.Object.Get("buffer", this).Object;
					int at = (int)t.Object.Get("byteOffset", this).Number + Arg(a, 0).ToInt32();
					if (at < 0 || at + size > buffer.Data.Length)
						throw RangeError("Offset is outside the bounds of the DataView");
					JsTypedArray scratch = new JsTypedArray(_typedArrayPrototypes[captured], captured, new JsArrayBuffer(ArrayBufferPrototype, size), 0, 1);
					scratch[0] = Arg(a, 1).ToNumber();
					byte[] bytes = scratch.Buffer.Data;
					if (!Arg(a, 2).Truthy && size > 1)
						Array.Reverse(bytes);
					Array.Copy(bytes, 0, buffer.Data, at, size);
					return JsValue.Undefined;
				});
			}
		}
	}
}
