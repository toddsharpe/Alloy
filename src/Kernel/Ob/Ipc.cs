using System;
using Alloy.Abi;
using Alloy.Kernel.Ke;
using Alloy.Kernel.Mm;

namespace Alloy.Kernel.Ob
{
	// Physically contiguous pages that several processes can map; never freed while any process might still map them
	internal sealed class KSection : KObject
	{
		public readonly ulong Physical;
		public readonly ulong Pages;

		private KSection(ulong physical, ulong pages)
		{
			Physical = physical;
			Pages = pages;
		}

		public static KSection Create(ulong size)
		{
			ulong pages = MemoryLayout.PagesFor(size);
			ulong physical = PhysicalMemory.Allocate(pages);
			return physical == 0 ? null : new KSection(physical, pages);
		}
	}

	// One message in a port: a fixed payload plus at most one object handed to the receiver
	internal sealed unsafe class KMessage
	{
		public IpcMessage Payload;
		public KObject Transfer;
		public KMessage Next;
	}

	// A message queue; signaled while it holds messages, so receivers block instead of polling
	internal sealed class KPort : KObject
	{
		private const int Capacity = 256;

		private KMessage _head;
		private KMessage _tail;
		private int _count;

		public override bool IsSignaled => _count > 0;

		public bool Send(KMessage message)
		{
			ulong flags = Scheduler.Lock();
			if (_count == Capacity)
			{
				Scheduler.Unlock(flags);
				return false;
			}
			message.Next = null;
			if (_tail == null)
				_head = message;
			else
				_tail.Next = message;
			_tail = message;
			_count++;
			WakeWaiters();
			Scheduler.Unlock(flags);
			return true;
		}

		// Removes the oldest message; null when empty
		public KMessage Take()
		{
			ulong flags = Scheduler.Lock();
			KMessage message = _head;
			if (message != null)
			{
				_head = message.Next;
				if (_head == null)
					_tail = null;
				_count--;
			}
			Scheduler.Unlock(flags);
			return message;
		}
	}

	// Named objects servers publish and clients look up, replacing MetalOS's fixed 16-entry endpoint table
	internal static class Endpoints
	{
		private sealed class Entry
		{
			public string Name;
			public KObject Value;
			public Entry Next;
		}

		private static Entry s_entries;

		public static bool Register(ReadOnlySpan<char> name, KObject value)
		{
			if (Find(name) != null)
				return false;
			value.AddReference();
			s_entries = new Entry { Name = new string(name), Value = value, Next = s_entries };
			return true;
		}

		public static KObject Find(ReadOnlySpan<char> name)
		{
			for (Entry entry = s_entries; entry != null; entry = entry.Next)
			{
				if (name.SequenceEqual(entry.Name))
					return entry.Value;
			}
			return null;
		}
	}
}
