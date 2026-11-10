using System.Runtime;
using System.Runtime.CompilerServices;
using Internal.Runtime;

namespace System
{
	// Contract with the compiler: a char array whose base size includes one extra char, the null terminator
	public sealed unsafe class String
	{
		internal const int FIRST_CHAR_OFFSET = 12; // MethodTable* + length

		private int _stringLength;
		private char _firstChar;

		[Intrinsic]
		public static readonly string Empty = "";

		public int Length => _stringLength;

		public char this[int index]
		{
			[Intrinsic]
			get
			{
				if ((uint)index >= (uint)_stringLength)
					ThrowHelper.ThrowIndexOutOfRange();
				return Unsafe.Add(ref _firstChar, (nint)(uint)index);
			}
		}

		public ref readonly char GetPinnableReference() => ref _firstChar;

		internal ref char GetRawStringData() => ref _firstChar;

		// The compiler routes `new string(...)` to the static Ctor with the matching signature
		[MethodImpl(MethodImplOptions.InternalCall)]
		public extern String(char* value);
		[MethodImpl(MethodImplOptions.InternalCall)]
		public extern String(char* value, int startIndex, int length);
		[MethodImpl(MethodImplOptions.InternalCall)]
		public extern String(char[] value);
		[MethodImpl(MethodImplOptions.InternalCall)]
		public extern String(char[] value, int startIndex, int length);
		[MethodImpl(MethodImplOptions.InternalCall)]
		public extern String(ReadOnlySpan<char> value);
		[MethodImpl(MethodImplOptions.InternalCall)]
		public extern String(char c, int count);

		private static string Ctor(char* value)
		{
			if (value == null)
				return Empty;
			int length = 0;
			while (value[length] != '\0')
				length++;
			return Ctor(new ReadOnlySpan<char>(value, length));
		}

		private static string Ctor(char* value, int startIndex, int length) => Ctor(new ReadOnlySpan<char>(value + startIndex, length));

		private static string Ctor(char[] value) => value == null ? Empty : Ctor(new ReadOnlySpan<char>(value));

		private static string Ctor(char[] value, int startIndex, int length) => Ctor(new ReadOnlySpan<char>(value, startIndex, length));

		private static string Ctor(ReadOnlySpan<char> value)
		{
			if (value.Length == 0)
				return Empty;
			string result = FastAllocateString(value.Length);
			value.CopyTo(new Span<char>(ref result._firstChar, value.Length));
			return result;
		}

		private static string Ctor(char c, int count)
		{
			if (count <= 0)
				return Empty;
			string result = FastAllocateString(count);
			new Span<char>(ref result._firstChar, count).Fill(c);
			return result;
		}

		internal static string FastAllocateString(int length) => Unsafe.As<string>(Allocator.NewArray(MethodTable.Of<string>(), length));

		public static bool IsNullOrEmpty(string value) => (object)value == null || value._stringLength == 0;

		public static string Concat(string str0, string str1) => Concat(str0, str1, null, null);

		public static string Concat(string str0, string str1, string str2) => Concat(str0, str1, str2, null);

		public static string Concat(string str0, string str1, string str2, string str3)
		{
			int length = LengthOf(str0) + LengthOf(str1) + LengthOf(str2) + LengthOf(str3);
			if (length == 0)
				return Empty;

			string result = FastAllocateString(length);
			Span<char> destination = new Span<char>(ref result._firstChar, length);
			int offset = 0;
			offset = Append(destination, offset, str0);
			offset = Append(destination, offset, str1);
			offset = Append(destination, offset, str2);
			Append(destination, offset, str3);
			return result;

			static int LengthOf(string s) => (object)s == null ? 0 : s._stringLength;

			static int Append(Span<char> destination, int offset, string s)
			{
				if ((object)s == null)
					return offset;
				new ReadOnlySpan<char>(ref s._firstChar, s._stringLength).CopyTo(destination.Slice(offset));
				return offset + s._stringLength;
			}
		}

		public static bool Equals(string a, string b)
		{
			if ((object)a == (object)b)
				return true;
			if ((object)a == null || (object)b == null || a._stringLength != b._stringLength)
				return false;
			return new ReadOnlySpan<char>(ref a._firstChar, a._stringLength).SequenceEqual(new ReadOnlySpan<char>(ref b._firstChar, b._stringLength));
		}

		public bool Equals(string value) => Equals(this, value);

		public override bool Equals(object obj) => obj is string s && Equals(this, s);

		public static bool operator ==(string a, string b) => Equals(a, b);

		public static bool operator !=(string a, string b) => !Equals(a, b);

		// FNV-1a; stable across boots, which helps when hashes show up in logs
		public override int GetHashCode()
		{
			uint hash = 2166136261;
			for (int i = 0; i < _stringLength; i++)
				hash = (hash ^ Unsafe.Add(ref _firstChar, i)) * 16777619;
			return (int)hash;
		}

		public override string ToString() => this;

		public static implicit operator ReadOnlySpan<char>(string value) =>
			(object)value == null ? default : new ReadOnlySpan<char>(ref value._firstChar, value._stringLength);
	}
}
