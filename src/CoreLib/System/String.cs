using System.Runtime;
using System.Runtime.CompilerServices;
using Internal.Runtime;

namespace System
{
	public enum StringComparison
	{
		Ordinal = 4,
		OrdinalIgnoreCase = 5,
	}

	// Contract with the compiler: a char array whose base size includes one extra char, the null terminator
	public sealed class String : IEquatable<string>, IComparable<string>, IComparable
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
		public extern unsafe String(char* value);
		[MethodImpl(MethodImplOptions.InternalCall)]
		public extern unsafe String(char* value, int startIndex, int length);
		[MethodImpl(MethodImplOptions.InternalCall)]
		public extern String(char[] value);
		[MethodImpl(MethodImplOptions.InternalCall)]
		public extern String(char[] value, int startIndex, int length);
		[MethodImpl(MethodImplOptions.InternalCall)]
		public extern String(ReadOnlySpan<char> value);
		[MethodImpl(MethodImplOptions.InternalCall)]
		public extern String(char c, int count);

		private static unsafe string Ctor(char* value)
		{
			if (value == null)
				return Empty;
			int length = 0;
			while (value[length] != '\0')
				length++;
			return Ctor(new ReadOnlySpan<char>(value, length));
		}

		private static unsafe string Ctor(char* value, int startIndex, int length) => Ctor(new ReadOnlySpan<char>(value + startIndex, length));

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

		internal static unsafe string FastAllocateString(int length) => Unsafe.As<string>(Allocator.NewArray(MethodTable.Of<string>(), length));

		private static ReadOnlySpan<char> Chars(string s) => (object)s == null ? default : new ReadOnlySpan<char>(ref s._firstChar, s._stringLength);

		public static bool IsNullOrEmpty(string value) => (object)value == null || value._stringLength == 0;

		public static bool IsNullOrWhiteSpace(string value)
		{
			if ((object)value == null)
				return true;
			for (int i = 0; i < value._stringLength; i++)
			{
				if (!char.IsWhiteSpace(value[i]))
					return false;
			}
			return true;
		}

		public static string Concat(string str0, string str1) => Concat(Chars(str0), Chars(str1));

		public static string Concat(string str0, string str1, string str2) => Concat(Chars(str0), Chars(str1), Chars(str2));

		public static string Concat(string str0, string str1, string str2, string str3) => Concat(Chars(str0), Chars(str1), Chars(str2), Chars(str3));

		public static string Concat(ReadOnlySpan<char> str0, ReadOnlySpan<char> str1)
		{
			int length = str0.Length + str1.Length;
			if (length == 0)
				return Empty;
			string result = FastAllocateString(length);
			Span<char> destination = new Span<char>(ref result._firstChar, length);
			str0.CopyTo(destination);
			str1.CopyTo(destination.Slice(str0.Length));
			return result;
		}

		public static string Concat(ReadOnlySpan<char> str0, ReadOnlySpan<char> str1, ReadOnlySpan<char> str2)
		{
			int length = str0.Length + str1.Length + str2.Length;
			if (length == 0)
				return Empty;
			string result = FastAllocateString(length);
			Span<char> destination = new Span<char>(ref result._firstChar, length);
			str0.CopyTo(destination);
			str1.CopyTo(destination.Slice(str0.Length));
			str2.CopyTo(destination.Slice(str0.Length + str1.Length));
			return result;
		}

		public static string Concat(ReadOnlySpan<char> str0, ReadOnlySpan<char> str1, ReadOnlySpan<char> str2, ReadOnlySpan<char> str3)
		{
			int length = str0.Length + str1.Length + str2.Length + str3.Length;
			if (length == 0)
				return Empty;
			string result = FastAllocateString(length);
			Span<char> destination = new Span<char>(ref result._firstChar, length);
			str0.CopyTo(destination);
			str1.CopyTo(destination.Slice(str0.Length));
			str2.CopyTo(destination.Slice(str0.Length + str1.Length));
			str3.CopyTo(destination.Slice(str0.Length + str1.Length + str2.Length));
			return result;
		}

		// Five or more pieces come through here
		public static string Concat(params string[] values) => Concat(new ReadOnlySpan<string>(values));

		public static string Concat(ReadOnlySpan<string> values)
		{
			int length = 0;
			for (int i = 0; i < values.Length; i++)
				length += (object)values[i] == null ? 0 : values[i]._stringLength;
			if (length == 0)
				return Empty;
			string result = FastAllocateString(length);
			Span<char> destination = new Span<char>(ref result._firstChar, length);
			int offset = 0;
			for (int i = 0; i < values.Length; i++)
			{
				ReadOnlySpan<char> piece = Chars(values[i]);
				piece.CopyTo(destination.Slice(offset));
				offset += piece.Length;
			}
			return result;
		}

		public static string Concat(object arg0) => arg0 == null ? Empty : arg0.ToString();

		public static string Concat(object arg0, object arg1) => Concat(arg0 == null ? null : arg0.ToString(), arg1 == null ? null : arg1.ToString());

		public static string Concat(object arg0, object arg1, object arg2) =>
			Concat(arg0 == null ? null : arg0.ToString(), arg1 == null ? null : arg1.ToString(), arg2 == null ? null : arg2.ToString());

		public static string Join(string separator, params string[] values) => Join(separator, new ReadOnlySpan<string>(values));

		public static string Join(string separator, ReadOnlySpan<string> values)
		{
			if (values.Length == 0)
				return Empty;
			ReadOnlySpan<char> glue = Chars(separator);
			int length = glue.Length * (values.Length - 1);
			for (int i = 0; i < values.Length; i++)
				length += (object)values[i] == null ? 0 : values[i]._stringLength;
			if (length == 0)
				return Empty;
			string result = FastAllocateString(length);
			Span<char> destination = new Span<char>(ref result._firstChar, length);
			int offset = 0;
			for (int i = 0; i < values.Length; i++)
			{
				if (i != 0)
				{
					glue.CopyTo(destination.Slice(offset));
					offset += glue.Length;
				}
				ReadOnlySpan<char> piece = Chars(values[i]);
				piece.CopyTo(destination.Slice(offset));
				offset += piece.Length;
			}
			return result;
		}

		public static bool Equals(string a, string b)
		{
			if ((object)a == (object)b)
				return true;
			if ((object)a == null || (object)b == null || a._stringLength != b._stringLength)
				return false;
			return Chars(a).SequenceEqual(Chars(b));
		}

		public static bool Equals(string a, string b, StringComparison comparisonType)
		{
			if (comparisonType == StringComparison.Ordinal)
				return Equals(a, b);
			if ((object)a == (object)b)
				return true;
			if ((object)a == null || (object)b == null || a._stringLength != b._stringLength)
				return false;
			for (int i = 0; i < a._stringLength; i++)
			{
				if (char.ToUpper(a[i]) != char.ToUpper(b[i]))
					return false;
			}
			return true;
		}

		public bool Equals(string value) => Equals(this, value);

		public bool Equals(string value, StringComparison comparisonType) => Equals(this, value, comparisonType);

		public override bool Equals(object obj) => obj is string s && Equals(this, s);

		public static bool operator ==(string a, string b) => Equals(a, b);

		public static bool operator !=(string a, string b) => !Equals(a, b);

		// Ordinal: by char value, then by length
		public static int Compare(string strA, string strB) => CompareOrdinal(strA, strB);

		public static int CompareOrdinal(string strA, string strB)
		{
			if ((object)strA == (object)strB)
				return 0;
			if ((object)strA == null)
				return -1;
			if ((object)strB == null)
				return 1;
			int length = Math.Min(strA._stringLength, strB._stringLength);
			for (int i = 0; i < length; i++)
			{
				int difference = strA[i] - strB[i];
				if (difference != 0)
					return difference;
			}
			return strA._stringLength - strB._stringLength;
		}

		public int CompareTo(string strB) => CompareOrdinal(this, strB);

		public int CompareTo(object value)
		{
			if (value == null)
				return 1;
			if (value is string other)
				return CompareOrdinal(this, other);
			ThrowHelper.ThrowArgument("a value of another type");
			return 0;
		}

		// FNV-1a; stable across boots, which helps when hashes show up in logs
		public override int GetHashCode()
		{
			uint hash = 2166136261;
			for (int i = 0; i < _stringLength; i++)
				hash = (hash ^ Unsafe.Add(ref _firstChar, i)) * 16777619;
			return (int)hash;
		}

		public override string ToString() => this;

		public static implicit operator ReadOnlySpan<char>(string value) => Chars(value);

		public char[] ToCharArray() => Chars(this).ToArray();

		public void CopyTo(Span<char> destination) => Chars(this).CopyTo(destination);

		public string Substring(int startIndex) => Substring(startIndex, _stringLength - startIndex);

		public string Substring(int startIndex, int length)
		{
			if (startIndex < 0 || length < 0 || startIndex > _stringLength - length)
				ThrowHelper.ThrowArgumentOutOfRange();
			if (length == 0)
				return Empty;
			if (startIndex == 0 && length == _stringLength)
				return this;
			return Ctor(Chars(this).Slice(startIndex, length));
		}

		public int IndexOf(char value) => Chars(this).IndexOf(value);

		public int IndexOf(char value, int startIndex)
		{
			if ((uint)startIndex > (uint)_stringLength)
				ThrowHelper.ThrowArgumentOutOfRange();
			int index = Chars(this).Slice(startIndex).IndexOf(value);
			return index < 0 ? -1 : startIndex + index;
		}

		public int IndexOf(string value) => Chars(this).IndexOf(Chars(value));

		public int LastIndexOf(char value) => Chars(this).LastIndexOf(value);

		public bool Contains(char value) => IndexOf(value) >= 0;

		public bool Contains(string value) => IndexOf(value) >= 0;

		public bool StartsWith(char value) => _stringLength != 0 && _firstChar == value;

		public bool StartsWith(string value) => Chars(this).StartsWith(Chars(value));

		public bool EndsWith(char value) => _stringLength != 0 && this[_stringLength - 1] == value;

		public bool EndsWith(string value) => Chars(this).EndsWith(Chars(value));

		public string Trim() => Slice(Chars(this).Trim());

		public string TrimStart() => Slice(Chars(this).TrimStart());

		public string TrimEnd() => Slice(Chars(this).TrimEnd());

		public string Trim(char trimChar) => Slice(Chars(this).Trim(trimChar));

		// The same string when nothing was trimmed, so trimming a clean string allocates nothing
		private string Slice(ReadOnlySpan<char> part) => part.Length == _stringLength ? this : Ctor(part);

		public string ToUpperInvariant() => Map(true);

		public string ToLowerInvariant() => Map(false);

		public string ToUpper() => Map(true);

		public string ToLower() => Map(false);

		private string Map(bool upper)
		{
			if (_stringLength == 0)
				return this;
			string result = FastAllocateString(_stringLength);
			for (int i = 0; i < _stringLength; i++)
				Unsafe.Add(ref result._firstChar, i) = upper ? char.ToUpper(this[i]) : char.ToLower(this[i]);
			return result;
		}

		public string Replace(char oldChar, char newChar)
		{
			if (IndexOf(oldChar) < 0)
				return this;
			string result = FastAllocateString(_stringLength);
			for (int i = 0; i < _stringLength; i++)
				Unsafe.Add(ref result._firstChar, i) = this[i] == oldChar ? newChar : this[i];
			return result;
		}

		public string PadLeft(int totalWidth, char paddingChar = ' ')
		{
			if (totalWidth <= _stringLength)
				return this;
			string result = FastAllocateString(totalWidth);
			Span<char> destination = new Span<char>(ref result._firstChar, totalWidth);
			destination.Slice(0, totalWidth - _stringLength).Fill(paddingChar);
			Chars(this).CopyTo(destination.Slice(totalWidth - _stringLength));
			return result;
		}

		public string PadRight(int totalWidth, char paddingChar = ' ')
		{
			if (totalWidth <= _stringLength)
				return this;
			string result = FastAllocateString(totalWidth);
			Span<char> destination = new Span<char>(ref result._firstChar, totalWidth);
			Chars(this).CopyTo(destination);
			destination.Slice(_stringLength).Fill(paddingChar);
			return result;
		}

		// Pieces between separators, empty ones included, as the BCL does without options
		public string[] Split(char separator)
		{
			int count = 1;
			for (int i = 0; i < _stringLength; i++)
			{
				if (this[i] == separator)
					count++;
			}
			string[] parts = new string[count];
			int start = 0;
			int part = 0;
			for (int i = 0; i <= _stringLength; i++)
			{
				if (i == _stringLength || this[i] == separator)
				{
					parts[part++] = Substring(start, i - start);
					start = i + 1;
				}
			}
			return parts;
		}
	}
}
