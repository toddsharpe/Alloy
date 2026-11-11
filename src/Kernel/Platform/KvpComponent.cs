using System;
using System.Buffers.Binary;
using System.Runtime.CompilerServices;
using Alloy.Kernel.Ke;

namespace Alloy.Kernel.Platform
{
	// The key-value pair exchange integration component {A9A0F4E7-5A45-4D96-B827-8A841E8C03E6}: what Hyper-V Manager and Get-VM show about the guest, and small values either side stores for the other
	internal sealed class KvpComponent : IntegrationComponent
	{
		// An hv_kvp_msg is the operation and pool bytes, a pad, then one of the bodies below; the IP info body skips the pad and is the largest at 7.4 KB
		private const int FrameSize = 8192;
		private const int MaxEntries = 32;
		private const int MaxKey = 64;
		private const int MaxValue = 128;

		private const byte OperationGet = 0;
		private const byte OperationSet = 1;
		private const byte OperationDelete = 2;
		private const byte OperationEnumerate = 3;
		private const byte OperationGetIpInfo = 4;

		// Pools: the host's values for the guest, the guest's for the host, and the guest's own description
		private const byte PoolExternal = 0;
		private const byte PoolGuest = 1;
		private const byte PoolAuto = 2;
		private const int PoolCount = 4;
		private const int AutoCount = 10;

		private const uint RegSz = 1;
		private const uint RegU32 = 4;
		private const uint RegU64 = 11;

		// hv_kvp_exchg_msg_value: a type, the key and value sizes in bytes with their NULs, the key, the value, all UTF-16
		private const int KeyBytes = 512;
		private const int ValueBytes = 2048;
		private const int ExchangeSize = 12 + KeyBytes + ValueBytes;

		// hv_kvp_ipaddr_value: the adapter id, family, whether DHCP, then the address, mask, gateway and DNS lists as UTF-16
		private const int IpAdapterBytes = 256;
		private const int IpFamily = IpAdapterBytes;
		private const int IpDhcp = IpAdapterBytes + 1;
		private const int IpAddress = IpAdapterBytes + 2;
		private const int IpListBytes = 2048;
		private const int IpSubnet = IpAddress + IpListBytes;
		private const int IpGateway = IpSubnet + IpListBytes;
		private const int IpGatewayBytes = 1024;
		private const int IpDns = IpGateway + IpGatewayBytes;
		private const int IpInfoOffset = 2;
		private const byte FamilyIpv4 = 1;

		[InlineArray(MaxKey)]
		private struct KeyChars
		{
			private char _element0;
		}

		[InlineArray(MaxValue)]
		private struct ValueChars
		{
			private char _element0;
		}

		private struct Entry
		{
			public bool Used;
			public byte Pool;
			public int KeyLength;
			public int ValueLength;
			public KeyChars Key;
			public ValueChars Value;
		}

		// Shared by the host's messages (in the VMBus interrupt) and PublishValue (in a syscall), so changes go under the scheduler lock
		private static readonly Entry[] s_entries = new Entry[MaxEntries];

		private ulong _ipRequests;

		protected override ReadOnlySpan<uint> MessageVersions => new uint[] { Version(4, 0), Version(3, 0) };

		private KvpComponent() : base(FrameSize)
		{
		}

		public static bool Start(in VmBusOffer offer)
		{
			KvpComponent component = new KvpComponent();
			return component.OpenChannel(in offer);
		}

		// A value of the guest's own for the host, under the Auto pool's key names or any other; false if it doesn't fit
		public static bool Publish(ReadOnlySpan<char> key, ReadOnlySpan<char> value)
		{
			if (key.Length == 0 || key.Length > MaxKey || value.Length > MaxValue)
				return false;
			ulong flags = Scheduler.Lock();
			bool stored = Store(PoolAuto, key, value);
			Scheduler.Unlock(flags);
			return stored;
		}

		protected override uint OnMessage(ushort type, Span<byte> body)
		{
			if (type != TypeKvp || body.Length < 4)
				return StatusFail;

			byte operation = body[0];
			byte pool = body[1];
			if (pool >= PoolCount && operation != OperationGetIpInfo)
				return StatusFail;

			switch (operation)
			{
				case OperationGet:
					return body.Length >= 4 + ExchangeSize ? Get(pool, body.Slice(4, ExchangeSize)) : StatusFail;
				case OperationSet:
					return body.Length >= 4 + ExchangeSize ? Set(pool, body.Slice(4, ExchangeSize)) : StatusFail;
				case OperationDelete:
					return body.Length >= 4 + 4 + KeyBytes ? Delete(pool, body.Slice(4, 4 + KeyBytes)) : StatusFail;
				case OperationEnumerate:
					return body.Length >= 4 + 4 + ExchangeSize ? Enumerate(pool, BinaryPrimitives.ReadUInt32LittleEndian(body.Slice(4)), body.Slice(8, ExchangeSize)) : StatusFail;
				case OperationGetIpInfo:
				{
					// The IP info body follows the operation and pool bytes directly, without the pad the other bodies have
					uint status = body.Length >= IpInfoOffset + IpDns + 2 ? GetIpInfo(body.Slice(IpInfoOffset)) : StatusFail;
					if (_ipRequests++ == 0)
						Log.WriteLine(status == StatusOk ? "Kvp: the host read the adapter's IP info" : "Kvp: the host asked for the adapter's IP info before the network stack had an address");
					return status;
				}
				default:
					return StatusFail;
			}
		}

		// The value under a key, from the Auto pool's description or the stored entries
		private static uint Get(byte pool, Span<byte> exchange)
		{
			Span<char> key = stackalloc char[MaxKey];
			int keyLength = ReadUtf16(exchange.Slice(12, Math.Min((int)BinaryPrimitives.ReadUInt32LittleEndian(exchange.Slice(4)), KeyBytes)), key);
			Span<char> value = stackalloc char[MaxValue];
			int valueLength = Lookup(pool, key.Slice(0, keyLength), value);
			if (valueLength < 0)
				return StatusFail;

			BinaryPrimitives.WriteUInt32LittleEndian(exchange, RegSz);
			BinaryPrimitives.WriteUInt32LittleEndian(exchange.Slice(8), (uint)WriteUtf16(exchange.Slice(12 + KeyBytes, ValueBytes), value.Slice(0, valueLength)));
			return StatusOk;
		}

		// A string value, or a number the host sent as one, stored under the key in the pool
		private static uint Set(byte pool, Span<byte> exchange)
		{
			Span<char> key = stackalloc char[MaxKey];
			int keyLength = ReadUtf16(exchange.Slice(12, Math.Min((int)BinaryPrimitives.ReadUInt32LittleEndian(exchange.Slice(4)), KeyBytes)), key);
			Span<char> value = stackalloc char[MaxValue];
			int valueLength;
			uint valueType = BinaryPrimitives.ReadUInt32LittleEndian(exchange);
			Span<byte> data = exchange.Slice(12 + KeyBytes, ValueBytes);
			if (valueType == RegU32)
				valueLength = WriteDecimal(BinaryPrimitives.ReadUInt32LittleEndian(data), value);
			else if (valueType == RegU64)
				valueLength = WriteDecimal(BinaryPrimitives.ReadUInt64LittleEndian(data), value);
			else
				valueLength = ReadUtf16(data.Slice(0, Math.Min((int)BinaryPrimitives.ReadUInt32LittleEndian(exchange.Slice(8)), ValueBytes)), value);
			if (keyLength == 0 || keyLength > MaxKey || valueLength > MaxValue)
				return StatusFail;
			return Store(pool, key.Slice(0, keyLength), value.Slice(0, valueLength)) ? StatusOk : StatusFail;
		}

		private static uint Delete(byte pool, Span<byte> request)
		{
			Span<char> key = stackalloc char[MaxKey];
			int keyLength = ReadUtf16(request.Slice(4, Math.Min((int)BinaryPrimitives.ReadUInt32LittleEndian(request), KeyBytes)), key);
			int index = Find(pool, key.Slice(0, keyLength));
			if (index < 0)
				return StatusFail;
			s_entries[index].Used = false;
			return StatusOk;
		}

		// The index-th name in a pool with its value; past the last the status says to stop
		private static uint Enumerate(byte pool, uint index, Span<byte> exchange)
		{
			Span<char> key = stackalloc char[MaxKey];
			Span<char> value = stackalloc char[MaxValue];
			int keyLength;
			int valueLength;
			if (pool == PoolAuto)
			{
				if (index >= AutoCount)
					return StatusContinue;
				keyLength = Copy(AutoName((int)index), key);
				valueLength = Lookup(PoolAuto, key.Slice(0, keyLength), value);
			}
			else
			{
				int found = -1;
				uint seen = 0;
				for (int i = 0; i < MaxEntries && found < 0; i++)
				{
					if (s_entries[i].Used && s_entries[i].Pool == pool && seen++ == index)
						found = i;
				}
				if (found < 0)
					return StatusContinue;
				keyLength = s_entries[found].KeyLength;
				valueLength = s_entries[found].ValueLength;
				((ReadOnlySpan<char>)s_entries[found].Key).Slice(0, keyLength).CopyTo(key);
				((ReadOnlySpan<char>)s_entries[found].Value).Slice(0, valueLength).CopyTo(value);
			}

			BinaryPrimitives.WriteUInt32LittleEndian(exchange, RegSz);
			BinaryPrimitives.WriteUInt32LittleEndian(exchange.Slice(4), (uint)WriteUtf16(exchange.Slice(12, KeyBytes), key.Slice(0, keyLength)));
			BinaryPrimitives.WriteUInt32LittleEndian(exchange.Slice(8), (uint)WriteUtf16(exchange.Slice(12 + KeyBytes, ValueBytes), value.Slice(0, valueLength)));
			return StatusOk;
		}

		// The one adapter's IPv4 setup as the network stack published it; nothing to say until it has an address
		private static uint GetIpInfo(Span<byte> info)
		{
			Span<char> value = stackalloc char[MaxValue];
			int length = Lookup(PoolAuto, "NetworkAddressIPv4", value);
			if (length <= 0)
				return StatusFail;

			info[IpFamily] = FamilyIpv4;
			info[IpDhcp] = (byte)(Lookup(PoolAuto, "NetworkDhcpIPv4", value) == 1 && value[0] == '1' ? 1 : 0);
			WriteUtf16(info.Slice(IpAddress, IpListBytes), value.Slice(0, Math.Max(Lookup(PoolAuto, "NetworkAddressIPv4", value), 0)));
			WriteUtf16(info.Slice(IpSubnet, IpListBytes), value.Slice(0, Math.Max(Lookup(PoolAuto, "NetworkSubnetIPv4", value), 0)));
			WriteUtf16(info.Slice(IpGateway, IpGatewayBytes), value.Slice(0, Math.Max(Lookup(PoolAuto, "NetworkGatewayIPv4", value), 0)));
			WriteUtf16(info.Slice(IpDns, Math.Min(IpListBytes, info.Length - IpDns)), value.Slice(0, Math.Max(Lookup(PoolAuto, "NetworkDnsIPv4", value), 0)));
			return StatusOk;
		}

		// The Auto pool's keys, in the order the host enumerates them
		private static string AutoName(int index) => index switch
		{
			0 => "FullyQualifiedDomainName",
			1 => "IntegrationServicesVersion",
			2 => "NetworkAddressIPv4",
			3 => "NetworkAddressIPv6",
			4 => "OSBuildNumber",
			5 => "OSName",
			6 => "OSMajorVersion",
			7 => "OSMinorVersion",
			8 => "OSVersion",
			_ => "ProcessorArchitecture",
		};

		// What the Auto pool says when the guest hasn't published a value; 9 is PROCESSOR_ARCHITECTURE_AMD64
		private static string AutoDefault(int index) => index switch
		{
			0 => "alloy",
			1 => "0.1",
			4 => "1",
			5 => "Alloy",
			6 => "0",
			7 => "1",
			8 => "0.1",
			9 => "9",
			_ => "",
		};

		// The value under a key: a stored entry of the pool first, then for the Auto pool its defaults; -1 if there's none
		private static int Lookup(byte pool, ReadOnlySpan<char> key, Span<char> value)
		{
			int index = Find(pool, key);
			if (index >= 0)
			{
				int length = s_entries[index].ValueLength;
				((ReadOnlySpan<char>)s_entries[index].Value).Slice(0, length).CopyTo(value);
				return length;
			}
			if (pool != PoolAuto)
				return -1;
			for (int i = 0; i < AutoCount; i++)
			{
				if (key.SequenceEqual(AutoName(i)))
					return Copy(AutoDefault(i), value);
			}
			return -1;
		}

		private static int Find(byte pool, ReadOnlySpan<char> key)
		{
			for (int i = 0; i < MaxEntries; i++)
			{
				if (s_entries[i].Used && s_entries[i].Pool == pool && ((ReadOnlySpan<char>)s_entries[i].Key).Slice(0, s_entries[i].KeyLength).SequenceEqual(key))
					return i;
			}
			return -1;
		}

		// Over the key's entry in the pool, or into a free one; false when there's none
		private static bool Store(byte pool, ReadOnlySpan<char> key, ReadOnlySpan<char> value)
		{
			int index = Find(pool, key);
			for (int i = 0; index < 0 && i < MaxEntries; i++)
			{
				if (!s_entries[i].Used)
					index = i;
			}
			if (index < 0)
				return false;

			ref Entry entry = ref s_entries[index];
			entry.Used = true;
			entry.Pool = pool;
			entry.KeyLength = key.Length;
			entry.ValueLength = value.Length;
			key.CopyTo(entry.Key);
			value.CopyTo(entry.Value);
			return true;
		}

		private static int Copy(string text, Span<char> destination)
		{
			for (int i = 0; i < text.Length; i++)
				destination[i] = text[i];
			return text.Length;
		}

		// UTF-16 up to its NUL or the end, as ASCII with '?' for the rest; what doesn't fit is dropped
		private static int ReadUtf16(ReadOnlySpan<byte> bytes, Span<char> destination)
		{
			int length = 0;
			for (int i = 0; i + 1 < bytes.Length && length < destination.Length; i += 2)
			{
				ushort unit = BinaryPrimitives.ReadUInt16LittleEndian(bytes.Slice(i));
				if (unit == 0)
					break;
				destination[length++] = unit < 0x80 ? (char)unit : '?';
			}
			return length;
		}

		// The text and its NUL as UTF-16, the rest of the field cleared; returns the size in bytes with the NUL
		private static int WriteUtf16(Span<byte> field, ReadOnlySpan<char> text)
		{
			field.Clear();
			int length = Math.Min(text.Length, field.Length / 2 - 1);
			for (int i = 0; i < length; i++)
				BinaryPrimitives.WriteUInt16LittleEndian(field.Slice(2 * i), text[i]);
			return 2 * (length + 1);
		}

		private static int WriteDecimal(ulong value, Span<char> destination)
		{
			int length = 0;
			do
			{
				destination[length++] = (char)('0' + value % 10);
				value /= 10;
			}
			while (value != 0);
			for (int i = 0; i < length / 2; i++)
			{
				char swap = destination[i];
				destination[i] = destination[length - 1 - i];
				destination[length - 1 - i] = swap;
			}
			return length;
		}
	}
}
