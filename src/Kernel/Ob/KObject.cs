using Alloy.Kernel.Ke;

namespace Alloy.Kernel.Ob
{
	// Anything a handle can refer to; waitable so WaitForObject works on processes, threads and events alike
	internal abstract class KObject : KWaitable
	{
		private int _references;

		public override bool IsSignaled => false;

		// Handles, queued messages and the endpoint registry each hold a reference
		public void AddReference() => _references++;

		public void Release()
		{
			if (--_references == 0)
				Close();
		}

		// Called when the last reference goes; objects that own resources release them here
		protected virtual void Close()
		{
		}
	}

	// Per-process handles: small integers indexing a growable table; 0 is never a valid handle
	internal sealed class HandleTable
	{
		private KObject[] _objects = new KObject[16];

		public long Add(KObject value)
		{
			for (int i = 1; i < _objects.Length; i++)
			{
				if (_objects[i] == null)
				{
					value.AddReference();
					_objects[i] = value;
					return i;
				}
			}

			KObject[] larger = new KObject[_objects.Length * 2];
			for (int i = 0; i < _objects.Length; i++)
				larger[i] = _objects[i];
			int handle = _objects.Length;
			Mm.Heap.Free(_objects);
			_objects = larger;
			value.AddReference();
			_objects[handle] = value;
			return handle;
		}

		public KObject Get(ulong handle) => handle != 0 && handle < (ulong)_objects.Length ? _objects[handle] : null;

		public bool Remove(ulong handle)
		{
			KObject value = Get(handle);
			if (value == null)
				return false;
			_objects[handle] = null;
			value.Release();
			return true;
		}

		public void CloseAll()
		{
			for (int i = 1; i < _objects.Length; i++)
			{
				if (_objects[i] != null)
				{
					_objects[i].Release();
					_objects[i] = null;
				}
			}
		}
	}
}
