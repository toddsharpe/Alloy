using System;
using Alloy.Abi;

namespace Alloy.Kernel.Io
{
	// An open file as its file system tracks it: the position and length the caller sees, and FAT32's own cluster bookkeeping
	internal struct FileState
	{
		public long Position;
		public long Length;
		public FileAccess Access;

		// The first cluster, how many clusters run on consecutively from it, and the cluster the last read reached with its index in the chain
		public uint Start;
		public uint Run;
		public uint Cursor;
		public uint CursorIndex;

		// Where the file's directory entry is, for a write that changes its size or first cluster
		public ulong EntrySector;
		public uint EntryOffset;

		public readonly bool Writable => (Access & FileAccess.Write) != 0;
	}

	// A tree of files on some storage, such as FAT32 on a disk partition
	internal abstract class FileSystem
	{
		// False if there's no such file (or, to create it, no such directory), or this file system can't open it that way
		public abstract bool Open(ref FileState file, ReadOnlySpan<char> path, FileAccess access, FileCreate create);

		// Each successful open is closed once, with the file as the open left it
		public abstract void Close(ref FileState file);

		// Up to the buffer's length from the file's position, which the caller advances; zero at the end
		public abstract int Read(ref FileState file, Span<byte> buffer);

		// At the file's position, which the caller advances, growing the file past its end; fewer bytes when the volume fills
		public abstract int Write(ref FileState file, ReadOnlySpan<byte> data);

		// A file, or a directory with nothing in it
		public abstract bool Delete(ReadOnlySpan<char> path);

		// To a name that isn't taken, in the same directory or another
		public abstract bool Rename(ReadOnlySpan<char> from, ReadOnlySpan<char> to);

		public abstract bool CreateDirectory(ReadOnlySpan<char> path);

		// The names in a directory but "." and "..", from the skip-th on, as many as fit; -1 if the path isn't a directory or it can't be read
		public abstract int List(ReadOnlySpan<char> path, int skip, Span<DirectoryEntry> entries);

		// What was written reaches the medium
		public abstract bool Flush();
	}
}
