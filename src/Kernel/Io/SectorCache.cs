using System;

namespace Alloy.Kernel.Io
{
	// A few of a block device's sectors kept in memory, the one used longest ago replaced and a modified one written back then or at a flush
	internal sealed class SectorCache
	{
		public const int MaxSectorSize = 4096;
		public const int Slots = 16;

		private readonly ulong[] _sector = new ulong[Slots];
		private readonly ulong[] _used = new ulong[Slots];
		private readonly bool[] _valid = new bool[Slots];
		private readonly bool[] _dirty = new bool[Slots];
		private readonly byte[] _data = new byte[Slots * MaxSectorSize];
		private BlockDevice _device;
		private ulong _tick;
		private ulong _mirrorFirst;
		private ulong _mirrorCount;
		private ulong _mirrorStride;
		private uint _mirrorCopies;

		public void Attach(BlockDevice device)
		{
			_device = device;
			_mirrorCopies = 0;
			for (int i = 0; i < Slots; i++)
			{
				_valid[i] = false;
				_dirty[i] = false;
			}
		}

		// Sectors in [first, first + count) are written again stride, 2 * stride... sectors on, copies times
		public void Mirror(ulong first, ulong count, ulong stride, uint copies)
		{
			_mirrorFirst = first;
			_mirrorCount = count;
			_mirrorStride = stride;
			_mirrorCopies = copies;
		}

		// The sector, valid until the next call; empty if the device can't read it, or can't take the modified sector it replaces
		public ReadOnlySpan<byte> Get(ulong sector)
		{
			int slot = Find(sector, true);
			return slot < 0 ? default : Data(slot);
		}

		// The sector to change in place; it's written back when replaced or flushed
		public Span<byte> Modify(ulong sector)
		{
			int slot = Find(sector, true);
			if (slot < 0)
				return default;
			_dirty[slot] = true;
			return Data(slot);
		}

		// The sector to change in place, all zero rather than read, for one whose contents don't matter
		public Span<byte> Claim(ulong sector)
		{
			int slot = Find(sector, false);
			if (slot < 0)
				return default;
			Span<byte> data = Data(slot);
			data.Clear();
			_dirty[slot] = true;
			return data;
		}

		// Sectors written to the device directly: any copy held is out of date, modified or not
		public void Discard(ulong sector, ulong count)
		{
			for (int i = 0; i < Slots; i++)
			{
				if (_valid[i] && _sector[i] >= sector && _sector[i] - sector < count)
				{
					_valid[i] = false;
					_dirty[i] = false;
				}
			}
		}

		// Every modified sector to the device; false if one couldn't be written
		public bool Flush()
		{
			bool written = true;
			for (int i = 0; i < Slots; i++)
			{
				if (_valid[i] && _dirty[i])
					written = WriteBack(i) && written;
			}
			return written;
		}

		private Span<byte> Data(int slot) => new Span<byte>(_data, slot * MaxSectorSize, _device.SectorSize);

		private int Find(ulong sector, bool read)
		{
			int oldest = 0;
			for (int i = 0; i < Slots; i++)
			{
				if (_valid[i] && _sector[i] == sector)
				{
					_used[i] = ++_tick;
					return i;
				}

				if (!_valid[i] || (_valid[oldest] && _used[i] < _used[oldest]))
					oldest = i;
			}

			if (_valid[oldest] && _dirty[oldest] && !WriteBack(oldest))
				return -1;

			_valid[oldest] = false;
			if (read && !_device.Read(sector, 1, Data(oldest)))
				return -1;

			_sector[oldest] = sector;
			_used[oldest] = ++_tick;
			_valid[oldest] = true;
			_dirty[oldest] = false;
			return oldest;
		}

		private bool WriteBack(int slot)
		{
			ReadOnlySpan<byte> data = Data(slot);
			if (!_device.Write(_sector[slot], 1, data))
				return false;

			if (_sector[slot] >= _mirrorFirst && _sector[slot] - _mirrorFirst < _mirrorCount)
			{
				for (uint copy = 1; copy <= _mirrorCopies; copy++)
				{
					if (!_device.Write(_sector[slot] + copy * _mirrorStride, 1, data))
						return false;
				}
			}

			_dirty[slot] = false;
			return true;
		}
	}
}
