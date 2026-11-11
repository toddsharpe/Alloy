using System;
using Alloy.Abi;
using Alloy.Kernel.Ke;
using Alloy.Kernel.Mm;
using Alloy.Text;

namespace Alloy.Kernel.Io
{
	// The boot disk's partitions from its GPT, and FAT32 on the first basic data partition as the root every path resolves against
	internal static class Storage
	{
		// The most a file is read or written in one piece on a caller's behalf
		public const int ChunkSize = 64 * 1024;

		private static FileSystem s_root;

		// File systems keep state between calls (FAT32's sector cache and open files), so calls into one take turns: an auto-reset event that starts signaled
		private static KEvent s_lock;

		public static bool Mounted => s_root != null;

		// The disk's partitions logged, then its first basic data partition (Alloy) mounted as the FAT32 root; the EFI system partition stays unmounted
		public static void Mount(BlockDevice disk)
		{
			s_lock = new KEvent(false, true);
			if (disk == null)
			{
				Log.WriteLine("Storage: no disk");
				return;
			}

			if (!Gpt.Read(disk, out GptTable table))
			{
				Log.WriteLine("Storage: disk 0 has no GPT that checks out");
				return;
			}

			int os = -1;
			for (int i = 0; i < table.Count; i++)
			{
				ref GptPartition entry = ref table.Partitions[i];
				bool isData = entry.IsType(Gpt.BasicDataLow, Gpt.BasicDataHigh);
				if (isData && os < 0)
					os = i;

				SpanWriter line = new SpanWriter(stackalloc char[160]);
				line.Append("Storage: disk 0 partition ");
				line.Append(i);
				line.Append(": \"");
				ReadOnlySpan<byte> name = entry.Name;
				for (int c = 0; c < entry.NameLength; c++)
					line.Append((char)name[c]);
				line.Append("\" (");
				line.Append(isData ? "basic data" : entry.IsType(Gpt.EfiSystemLow, Gpt.EfiSystemHigh) ? "EFI system" : "other");
				line.Append("), ");
				line.Append((entry.SectorCount * (ulong)disk.SectorSize) >> 20);
				line.Append(" MB at sector ");
				line.Append(entry.FirstSector);
				Log.WriteLine(ref line);
			}

			// The OS and its data are on the first basic data partition, a bare name like term.exe at its root; the EFI system partition holds only what the loader loads
			if (os < 0)
			{
				Log.WriteLine("Storage: no basic data partition, so no file system");
				return;
			}

			Partition volume = new Partition(disk, table.Partitions[os].FirstSector, table.Partitions[os].SectorCount);
			Fat32 fat = new Fat32();
			if (!fat.Mount(volume, new SectorCache()))
			{
				Log.WriteLine("Storage: the basic data partition isn't FAT32, so no file system");
				return;
			}
			s_root = fat;

			SpanWriter mounted = new SpanWriter(stackalloc char[96]);
			mounted.Append("Storage: FAT32 on partition ");
			mounted.Append(os);
			mounted.Append(" is the root, ");
			if (fat.FreeClusters == Fat32.Unknown)
				mounted.Append("free count unknown");
			else
			{
				mounted.Append(fat.FreeClusters);
				mounted.Append(" free clusters");
			}
			Log.WriteLine(ref mounted);
		}

		// Null if there's no such file, it can't be opened that way, or nothing is mounted
		public static KFile Open(ReadOnlySpan<char> path, FileAccess access, FileCreate create)
		{
			if (s_root == null)
				return null;

			FileState state = default;
			s_lock.Wait();
			bool opened = s_root.Open(ref state, path, access, create);
			s_lock.Signal();
			return opened ? new KFile(s_root, in state) : null;
		}

		internal static void Close(KFile file)
		{
			s_lock.Wait();
			file.System.Close(ref file.State);
			s_lock.Signal();
		}

		// From the file's position, which moves on; zero at the end
		public static int Read(KFile file, Span<byte> buffer)
		{
			s_lock.Wait();
			int read = file.System.Read(ref file.State, buffer);
			s_lock.Signal();
			file.State.Position += read;
			return read;
		}

		// At the file's position, or at the end for an append wherever the position was moved to; fewer bytes than asked when the volume fills
		public static int Write(KFile file, ReadOnlySpan<byte> data)
		{
			s_lock.Wait();
			if ((file.State.Access & FileAccess.Append) == FileAccess.Append)
				file.State.Position = file.State.Length;
			int written = file.System.Write(ref file.State, data);
			s_lock.Signal();
			file.State.Position += written;
			return written;
		}

		public static bool Delete(ReadOnlySpan<char> path)
		{
			if (s_root == null)
				return false;
			s_lock.Wait();
			bool deleted = s_root.Delete(path);
			s_lock.Signal();
			return deleted;
		}

		public static bool Move(ReadOnlySpan<char> from, ReadOnlySpan<char> to)
		{
			if (s_root == null)
				return false;
			s_lock.Wait();
			bool moved = s_root.Rename(from, to);
			s_lock.Signal();
			return moved;
		}

		public static bool CreateDirectory(ReadOnlySpan<char> path)
		{
			if (s_root == null)
				return false;
			s_lock.Wait();
			bool created = s_root.CreateDirectory(path);
			s_lock.Signal();
			return created;
		}

		// The names in a directory from the skip-th on, as many as fit; -1 if the path isn't a directory
		public static int List(ReadOnlySpan<char> path, int skip, Span<DirectoryEntry> entries)
		{
			if (s_root == null)
				return -1;
			s_lock.Wait();
			int listed = s_root.List(path, skip, entries);
			s_lock.Signal();
			return listed;
		}

		// The whole of a file in a heap block the caller frees; null if it's missing, empty or can't be read
		public static unsafe byte* LoadFile(ReadOnlySpan<char> path, out long size)
		{
			size = 0;
			KFile file = Open(path, FileAccess.Read, FileCreate.OpenExisting);
			if (file == null)
				return null;

			byte* data = null;
			if (file.Size > 0 && file.Size <= 0x7FFFFFFF)
			{
				data = (byte*)Heap.Allocate((nuint)file.Size);
				long total = 0;
				while (data != null && total < file.Size)
				{
					int read = Read(file, new Span<byte>(data + total, (int)Math.Min(file.Size - total, ChunkSize)));
					if (read <= 0)
						break;
					total += read;
				}
				if (data != null && total != file.Size)
				{
					Heap.Free(data);
					data = null;
				}
				size = data != null ? total : 0;
			}

			// Nothing holds a handle to this file, so it is closed and freed here
			Close(file);
			Heap.Free(file);
			return data;
		}
	}
}
