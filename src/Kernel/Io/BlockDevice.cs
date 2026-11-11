using System;

namespace Alloy.Kernel.Io
{
	// Sectors in and out: a disk, or a partition's window onto one
	internal abstract class BlockDevice
	{
		public abstract int SectorSize { get; }

		public abstract ulong SectorCount { get; }

		// False on a device error or a range past the end; the buffer holds count whole sectors
		public abstract bool Read(ulong sector, int count, Span<byte> buffer);

		public abstract bool Write(ulong sector, int count, ReadOnlySpan<byte> buffer);

		// What was written reaches the medium
		public abstract bool Flush();
	}

	// A window onto a run of a disk's sectors, numbered from the run's start
	internal sealed class Partition : BlockDevice
	{
		private readonly BlockDevice _disk;
		private readonly ulong _first;
		private readonly ulong _count;

		public Partition(BlockDevice disk, ulong first, ulong count)
		{
			_disk = disk;
			_first = first;
			_count = count <= disk.SectorCount && first <= disk.SectorCount - count ? count : 0;
		}

		public override int SectorSize => _disk.SectorSize;

		public override ulong SectorCount => _count;

		public override bool Read(ulong sector, int count, Span<byte> buffer) => InRange(sector, count) && _disk.Read(_first + sector, count, buffer);

		public override bool Write(ulong sector, int count, ReadOnlySpan<byte> buffer) => InRange(sector, count) && _disk.Write(_first + sector, count, buffer);

		public override bool Flush() => _disk.Flush();

		private bool InRange(ulong sector, int count) => count >= 0 && (ulong)count <= _count && sector <= _count - (ulong)count;
	}
}
