using System;
using Alloy.Abi;

namespace Alloy.Sdk
{
	// Files on the Alloy partition through the kernel's handles: a path is from the root, '/' or '\' between names, in any case
	public static class File
	{
		// A handle, or a negative error; Write access with CreateAlways empties or makes the file, with OpenAlways makes it if need be
		public static unsafe long Open(string path, FileAccess access = FileAccess.Read, FileCreate create = FileCreate.OpenExisting)
		{
			byte* bytes = stackalloc byte[256];
			int length = SystemCall.Ascii(path, bytes, 255);
			return SystemCall.Invoke(Syscall.OpenFile, (ulong)bytes, (ulong)length, (ulong)access, (ulong)create);
		}

		// Bytes from the position, which moves on; 0 at the end
		public static unsafe long Read(long handle, Span<byte> buffer)
		{
			fixed (byte* pointer = buffer)
				return SystemCall.Invoke(Syscall.ReadFile, (ulong)handle, (ulong)pointer, (ulong)buffer.Length);
		}

		// Bytes at the position, or at the end for an Append open; fewer than given when the volume fills
		public static unsafe long Write(long handle, ReadOnlySpan<byte> data)
		{
			fixed (byte* pointer = data)
				return SystemCall.Invoke(Syscall.WriteFile, (ulong)handle, (ulong)pointer, (ulong)data.Length);
		}

		// The new position, or a negative error; past the end only on a file open for writing
		public static long Seek(long handle, long offset, SeekOrigin origin) => SystemCall.Invoke(Syscall.SeekFile, (ulong)handle, (ulong)offset, (ulong)origin);

		public static long Size(long handle) => SystemCall.Invoke(Syscall.GetFileSize, (ulong)handle);

		public static bool Close(long handle) => Kernel.Close(handle);

		// A file, or a directory with nothing in it
		public static unsafe bool Delete(string path)
		{
			byte* bytes = stackalloc byte[256];
			int length = SystemCall.Ascii(path, bytes, 255);
			return SystemCall.Invoke(Syscall.DeleteFile, (ulong)bytes, (ulong)length) == 0;
		}

		// To a name that isn't taken, in the same directory or another
		public static unsafe bool Move(string from, string to)
		{
			byte* fromBytes = stackalloc byte[256];
			byte* toBytes = stackalloc byte[256];
			int fromLength = SystemCall.Ascii(from, fromBytes, 255);
			int toLength = SystemCall.Ascii(to, toBytes, 255);
			return SystemCall.Invoke(Syscall.MoveFile, (ulong)fromBytes, (ulong)fromLength, (ulong)toBytes, (ulong)toLength) == 0;
		}

		// The whole of a file, or null if it can't be opened or read
		public static byte[] ReadAllBytes(string path)
		{
			long handle = Open(path);
			if (handle <= 0)
				return null;
			long size = Size(handle);
			byte[] data = new byte[size < 0 ? 0 : size];
			int total = 0;
			while (total < data.Length)
			{
				long read = Read(handle, data.AsSpan(total));
				if (read <= 0)
					break;
				total += (int)read;
			}
			Close(handle);
			return total == data.Length ? data : null;
		}

		// The file's contents replaced; false if it couldn't be made or the volume filled
		public static bool WriteAllBytes(string path, ReadOnlySpan<byte> data)
		{
			long handle = Open(path, FileAccess.Write, FileCreate.CreateAlways);
			if (handle <= 0)
				return false;
			bool written = Write(handle, data) == data.Length;
			Close(handle);
			return written;
		}

		public static string Name(in DirectoryEntry entry)
		{
			ReadOnlySpan<byte> name = entry.Name;
			int length = name.IndexOf((byte)0);
			return System.Text.Encoding.ASCII.GetString(length < 0 ? name : name.Slice(0, length));
		}
	}

	public static class Directory
	{
		public static unsafe bool Create(string path)
		{
			byte* bytes = stackalloc byte[256];
			int length = SystemCall.Ascii(path, bytes, 255);
			return SystemCall.Invoke(Syscall.CreateDirectory, (ulong)bytes, (ulong)length) == 0;
		}

		// The names in a directory from index on, but "." and ".."; index moves past those returned, so fewer than the span holds means the end; -1 if it isn't a directory
		public static unsafe int List(string path, Span<DirectoryEntry> entries, ref ulong index)
		{
			byte* bytes = stackalloc byte[256];
			int length = SystemCall.Ascii(path, bytes, 255);
			DirectoryListing listing;
			long result;
			fixed (DirectoryEntry* pointer = entries)
			{
				listing = new DirectoryListing { Entries = (ulong)pointer, Count = (ulong)entries.Length, Index = index };
				result = SystemCall.Invoke(Syscall.ListDirectory, (ulong)bytes, (ulong)length, (ulong)&listing);
			}
			index = listing.Index;
			return result < 0 ? -1 : (int)result;
		}
	}
}
