using Alloy.Kernel.Io;

namespace Alloy.Tests;

// A disk in memory for the storage tests, its sectors open to the test that builds or checks them
internal sealed class MemoryDisk : BlockDevice
{
	private readonly byte[] _bytes;

	// Requests made, for a test that checks how a file system batches them
	public int Reads;

	public MemoryDisk(int sectorSize, ulong sectorCount)
	{
		SectorSize = sectorSize;
		SectorCount = sectorCount;
		_bytes = new byte[(long)sectorSize * (long)sectorCount];
	}

	public override int SectorSize { get; }

	public override ulong SectorCount { get; }

	public Span<byte> Sector(ulong sector) => _bytes.AsSpan((int)(sector * (ulong)SectorSize), SectorSize);

	public override bool Read(ulong sector, int count, Span<byte> buffer)
	{
		if (count < 0 || (ulong)count > SectorCount || sector > SectorCount - (ulong)count)
			return false;
		Reads++;
		_bytes.AsSpan((int)(sector * (ulong)SectorSize), count * SectorSize).CopyTo(buffer);
		return true;
	}

	public override bool Write(ulong sector, int count, ReadOnlySpan<byte> buffer)
	{
		if (count < 0 || (ulong)count > SectorCount || sector > SectorCount - (ulong)count)
			return false;
		buffer.Slice(0, count * SectorSize).CopyTo(_bytes.AsSpan((int)(sector * (ulong)SectorSize)));
		return true;
	}

	public override bool Flush() => true;
}
