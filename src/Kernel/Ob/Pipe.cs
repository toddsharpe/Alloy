using System;
using Alloy.Kernel.Ke;

namespace Alloy.Kernel.Ob
{
	// A one-way byte stream between processes (MetalOS CreatePipe): a ring buffer with a read end and a write end
	internal sealed unsafe class KPipe
	{
		public const int Capacity = 4096;

		private readonly byte* _buffer;
		private uint _read;
		private uint _write;

		public readonly KPipeEnd Reader;
		public readonly KPipeEnd Writer;
		public bool ReaderOpen = true;
		public bool WriterOpen = true;

		private KPipe()
		{
			_buffer = (byte*)Mm.Heap.Allocate(Capacity);
			Reader = new KPipeEnd(this, false);
			Writer = new KPipeEnd(this, true);
		}

		public static KPipe Create() => new KPipe();

		public int Count => (int)(_write - _read);

		// Interrupts must be off; returns how many bytes moved
		public int Take(byte* destination, int count)
		{
			int taken = Math.Min(count, Count);
			for (int i = 0; i < taken; i++)
				destination[i] = _buffer[(_read + (uint)i) % Capacity];
			_read += (uint)taken;
			return taken;
		}

		public int Put(byte* source, int count)
		{
			int put = Math.Min(count, Capacity - Count);
			for (int i = 0; i < put; i++)
				_buffer[(_write + (uint)i) % Capacity] = source[i];
			_write += (uint)put;
			return put;
		}
	}

	// One end of a pipe; the read end is signaled while there is data or no writer, the write end while there is room or no reader
	internal sealed class KPipeEnd : KObject
	{
		public readonly KPipe Pipe;
		public readonly bool Writes;

		public KPipeEnd(KPipe pipe, bool writes)
		{
			Pipe = pipe;
			Writes = writes;
		}

		public override bool IsSignaled => Writes ? Pipe.Count < KPipe.Capacity || !Pipe.ReaderOpen : Pipe.Count > 0 || !Pipe.WriterOpen;

		// Wakes whoever waits on this end after the other end changed the pipe
		public void Notify()
		{
			ulong flags = Scheduler.Lock();
			WakeWaiters();
			Scheduler.Unlock(flags);
		}

		// The last handle to this end is gone: a reader then sees end of file, a writer a closed pipe
		protected override void Close()
		{
			ulong flags = Scheduler.Lock();
			if (Writes)
				Pipe.WriterOpen = false;
			else
				Pipe.ReaderOpen = false;
			Scheduler.Unlock(flags);
			(Writes ? Pipe.Reader : Pipe.Writer).Notify();
		}
	}
}
