using System;
using System.Numerics;
using System.Text;

namespace Alloy.Sdk.Security.Crypto
{
	// A DER reader over a span: tag, length, contents, with the shapes certificates and signatures use
	public ref struct Asn1Reader
	{
		private readonly ReadOnlySpan<byte> _data;
		private int _at;

		public const byte Boolean = 0x01;
		public const byte Integer = 0x02;
		public const byte BitString = 0x03;
		public const byte OctetString = 0x04;
		public const byte Null = 0x05;
		public const byte ObjectIdentifier = 0x06;
		public const byte Utf8String = 0x0C;
		public const byte PrintableString = 0x13;
		public const byte Ia5String = 0x16;
		public const byte UtcTime = 0x17;
		public const byte GeneralizedTime = 0x18;
		public const byte Sequence = 0x30;
		public const byte Set = 0x31;

		public Asn1Reader(ReadOnlySpan<byte> data)
		{
			_data = data;
			_at = 0;
		}

		public bool AtEnd => _at >= _data.Length;

		public int Position => _at;

		public ReadOnlySpan<byte> Remaining => _data.Slice(_at);

		// The next tag without consuming anything; 0 at the end
		public byte PeekTag() => _at < _data.Length ? _data[_at] : (byte)0;

		// Reads one TLV; the element span covers the whole encoding, the contents span just the value
		public bool TryReadElement(out byte tag, out ReadOnlySpan<byte> contents, out ReadOnlySpan<byte> element)
		{
			tag = 0;
			contents = default;
			element = default;
			int start = _at;
			if (_at + 2 > _data.Length)
				return false;
			tag = _data[_at++];
			if ((tag & 0x1F) == 0x1F)
				return false;
			int length = _data[_at++];
			if (length >= 0x80)
			{
				int count = length & 0x7F;
				if (count == 0 || count > 4 || _at + count > _data.Length)
					return false;
				length = 0;
				for (int i = 0; i < count; i++)
					length = length << 8 | _data[_at++];
				if (length < 0)
					return false;
			}
			if (_at + length > _data.Length)
				return false;
			contents = _data.Slice(_at, length);
			_at += length;
			element = _data.Slice(start, _at - start);
			return true;
		}

		public bool TryReadExpected(byte expected, out ReadOnlySpan<byte> contents)
		{
			contents = default;
			if (PeekTag() != expected)
				return false;
			return TryReadElement(out _, out contents, out _);
		}

		public bool TryReadSequence(out Asn1Reader inner) => TryReadConstructed(Sequence, out inner);

		public bool TryReadSet(out Asn1Reader inner) => TryReadConstructed(Set, out inner);

		public bool TryReadConstructed(byte tag, out Asn1Reader inner)
		{
			inner = default;
			if (!TryReadExpected(tag, out ReadOnlySpan<byte> contents))
				return false;
			inner = new Asn1Reader(contents);
			return true;
		}

		// A context-specific [n] element, explicit or implicit, if the next tag is that one
		public bool TryReadContext(int number, out Asn1Reader inner)
		{
			inner = default;
			byte tag = PeekTag();
			if ((tag & 0xC0) != 0x80 || (tag & 0x1F) != number)
				return false;
			if (!TryReadElement(out _, out ReadOnlySpan<byte> contents, out _))
				return false;
			inner = new Asn1Reader(contents);
			return true;
		}

		public bool TryReadInteger(out BigInteger value)
		{
			value = default;
			if (!TryReadExpected(Integer, out ReadOnlySpan<byte> contents) || contents.Length == 0)
				return false;
			value = new BigInteger(contents, isUnsigned: false, isBigEndian: true);
			return true;
		}

		public bool TryReadOid(out string oid)
		{
			oid = null;
			if (!TryReadExpected(ObjectIdentifier, out ReadOnlySpan<byte> contents) || contents.Length == 0)
				return false;
			oid = DecodeOid(contents);
			return true;
		}

		public static string DecodeOid(ReadOnlySpan<byte> contents)
		{
			StringBuilder text = new StringBuilder();
			long value = 0;
			bool first = true;
			foreach (byte b in contents)
			{
				value = value << 7 | (uint)(b & 0x7F);
				if ((b & 0x80) != 0)
					continue;
				if (first)
				{
					long top = value < 40 ? 0 : value < 80 ? 1 : 2;
					text.Append(top).Append('.').Append(value - top * 40);
					first = false;
				}
				else
					text.Append('.').Append(value);
				value = 0;
			}
			return text.ToString();
		}

		// A bit string's bytes after the unused-bits count, which is zero for keys and signatures
		public bool TryReadBitString(out ReadOnlySpan<byte> bits)
		{
			bits = default;
			if (!TryReadExpected(BitString, out ReadOnlySpan<byte> contents) || contents.Length == 0)
				return false;
			bits = contents.Slice(1);
			return true;
		}

		public bool TryReadOctetString(out ReadOnlySpan<byte> bytes) => TryReadExpected(OctetString, out bytes);

		public bool TryReadBoolean(out bool value)
		{
			value = false;
			if (!TryReadExpected(Boolean, out ReadOnlySpan<byte> contents) || contents.Length != 1)
				return false;
			value = contents[0] != 0;
			return true;
		}

		public bool TryReadString(out string text)
		{
			text = null;
			byte tag = PeekTag();
			if (!TryReadElement(out _, out ReadOnlySpan<byte> contents, out _))
				return false;
			text = tag switch
			{
				0x1E => Encoding.BigEndianUnicode.GetString(contents),
				_ => Encoding.UTF8.GetString(contents),
			};
			return true;
		}

		// UTCTime (YYMMDDHHMMSSZ) or GeneralizedTime (YYYYMMDDHHMMSSZ) as UTC
		public bool TryReadTime(out DateTime time)
		{
			time = default;
			byte tag = PeekTag();
			if ((tag != UtcTime && tag != GeneralizedTime) || !TryReadElement(out _, out ReadOnlySpan<byte> contents, out _))
				return false;
			string text = Encoding.ASCII.GetString(contents);
			try
			{
				int year;
				int at;
				if (tag == UtcTime)
				{
					int yy = int.Parse(text.Substring(0, 2));
					year = yy >= 50 ? 1900 + yy : 2000 + yy;
					at = 2;
				}
				else
				{
					year = int.Parse(text.Substring(0, 4));
					at = 4;
				}
				int month = int.Parse(text.Substring(at, 2));
				int day = int.Parse(text.Substring(at + 2, 2));
				int hour = int.Parse(text.Substring(at + 4, 2));
				int minute = int.Parse(text.Substring(at + 6, 2));
				int second = text.Length > at + 9 && char.IsDigit(text[at + 8]) ? int.Parse(text.Substring(at + 8, 2)) : 0;
				time = new DateTime(year, month, day, hour, minute, second, DateTimeKind.Utc);
				return true;
			}
			catch (Exception)
			{
				return false;
			}
		}

		public bool Skip() => TryReadElement(out _, out _, out _);
	}
}
