namespace Alloy.Kernel.Ke
{
	// One thread waiting on one object; a multi-object wait links one block into each object
	internal sealed class KWaitBlock
	{
		public KThread Thread;
		public KWaitable Object;
		public KWaitBlock NextInObject;
		public KWaitBlock NextInThread;
		public int Index;
	}

	// Anything a thread can block on; waiters are served in arrival order
	internal abstract class KWaitable
	{
		private KWaitBlock _blocks;

		public abstract bool IsSignaled { get; }

		// Takes the signal for a waiter that is about to return (auto-reset objects clear themselves here)
		protected virtual void Acquire()
		{
		}

		public unsafe bool Wait(ulong timeoutMs = ulong.MaxValue)
		{
			KWaitable self = this;
			return WaitAny(&self, 1, timeoutMs) == 0;
		}

		// Returns the index of the object that satisfied the wait, or -1 on timeout
		public static unsafe int WaitAny(KWaitable* objects, int count, ulong timeoutMs)
		{
			ulong flags = Scheduler.Lock();
			for (int i = 0; i < count; i++)
			{
				if (objects[i].IsSignaled)
				{
					objects[i].Acquire();
					Scheduler.Unlock(flags);
					return i;
				}
			}
			if (timeoutMs == 0)
			{
				Scheduler.Unlock(flags);
				return -1;
			}

			KThread thread = Scheduler.Current;
			thread.WaitIndex = -1;
			thread.InWait = true;
			for (int i = 0; i < count; i++)
			{
				KWaitBlock block = new KWaitBlock { Thread = thread, Object = objects[i], Index = i, NextInThread = thread.WaitBlocks };
				thread.WaitBlocks = block;
				objects[i].Append(block);
			}

			Scheduler.Block(timeoutMs);

			for (KWaitBlock block = thread.WaitBlocks; block != null;)
			{
				KWaitBlock next = block.NextInThread;
				block.Object.Unlink(block);
				Mm.Heap.Free(block);
				block = next;
			}
			thread.WaitBlocks = null;
			int index = thread.WaitIndex;
			Scheduler.Unlock(flags);
			return index;
		}

		// Called with interrupts off whenever the object may have become signaled
		protected void WakeWaiters()
		{
			KWaitBlock block = _blocks;
			while (block != null && IsSignaled)
			{
				KWaitBlock next = block.NextInObject;
				if (block.Thread.InWait)
				{
					block.Thread.InWait = false;
					block.Thread.WaitIndex = block.Index;
					Acquire();
					Scheduler.Wake(block.Thread);
				}
				block = next;
			}
		}

		private void Append(KWaitBlock block)
		{
			block.NextInObject = null;
			if (_blocks == null)
			{
				_blocks = block;
				return;
			}
			KWaitBlock at = _blocks;
			while (at.NextInObject != null)
				at = at.NextInObject;
			at.NextInObject = block;
		}

		private void Unlink(KWaitBlock block)
		{
			if (_blocks == block)
			{
				_blocks = block.NextInObject;
				return;
			}
			for (KWaitBlock at = _blocks; at != null; at = at.NextInObject)
			{
				if (at.NextInObject == block)
				{
					at.NextInObject = block.NextInObject;
					return;
				}
			}
		}
	}

	internal sealed class KEvent : Ob.KObject
	{
		private readonly bool _manualReset;
		private bool _signaled;

		public KEvent(bool manualReset, bool initialState = false)
		{
			_manualReset = manualReset;
			_signaled = initialState;
		}

		public override bool IsSignaled => _signaled;

		protected override void Acquire()
		{
			if (!_manualReset)
				_signaled = false;
		}

		public void Signal()
		{
			ulong flags = Scheduler.Lock();
			_signaled = true;
			WakeWaiters();
			Scheduler.Unlock(flags);
		}

		public void Reset()
		{
			ulong flags = Scheduler.Lock();
			_signaled = false;
			Scheduler.Unlock(flags);
		}
	}
}
