using System;
using System.Runtime.CompilerServices;
using Alloy.Abi;

namespace Alloy.Kernel.Io
{
	// A FAT32 volume: long or short names in any case, '/' or '\' between them, and a path not from the root starts in the default directory
	internal sealed class Fat32 : FileSystem
	{
		// The free cluster count when the volume's FSInfo didn't give one
		public const uint Unknown = 0xFFFFFFFF;

		// Files open at once, however many opens each has
		public const int MaxOpen = 64;

		private const int EntrySize = 32;
		private const int MaxLongName = 255;
		private const int LongNameChars = 13;
		private const long MaxFileSize = 0xFFFFFFFF;

		// A long name of 255 characters takes 20 entries, then the short entry
		private const int MaxEntries = 21;

		private const byte AttrReadOnly = 0x01;
		private const byte AttrVolumeId = 0x08;
		private const byte AttrDirectory = 0x10;
		private const byte AttrArchive = 0x20;
		private const byte AttrLongName = 0x0F;
		private const byte AttrLongNameMask = 0x3F;

		private const byte EntryEnd = 0x00;
		private const byte EntryDeleted = 0xE5;
		private const byte EntryKanjiE5 = 0x05;
		private const byte LongNameLast = 0x40;
		private const byte LongNameOrdinal = 0x1F;

		// Windows keeps a short name's case in byte 12 rather than giving it a long name
		private const byte CaseLowerBase = 0x08;
		private const byte CaseLowerExtension = 0x10;

		private const uint ClusterMask = 0x0FFFFFFF;
		private const uint EndOfChain = 0x0FFFFFFF;

		// Fewer clusters than this make a volume FAT12 or FAT16, whatever its boot sector says
		private const uint MinClusters = 65525;

		// FSInfo's signatures and the fields kept in it
		private const uint FsInfoLead = 0x41615252;
		private const uint FsInfoStruct = 0x61417272;
		private const uint FsInfoTrail = 0xAA550000;
		private const int FsInfoFreeCount = 488;
		private const int FsInfoNextFree = 492;

		// Where a long name entry's 13 UTF-16 characters sit
		private static ReadOnlySpan<byte> LongNameOffsets => new byte[] { 1, 3, 5, 7, 9, 14, 16, 18, 20, 22, 24, 28, 30 };

		// Where a 32-byte directory entry is
		private struct Location
		{
			public ulong Sector;
			public uint Offset;
		}

		[InlineArray(MaxEntries)]
		private struct LocationSlots
		{
			private Location _element0;
		}

		// What a path names, and where its entries are, long name first; the root and the default directory have none
		private struct Entry
		{
			public uint Cluster;
			public uint Size;
			public bool Directory;
			public byte Attributes;
			public int Count;
			public LocationSlots Slots;

			public readonly Location Last => Slots[Count - 1];
		}

		// A file open now, by where its short entry is: how many opens it has, and whether one writes
		private struct OpenFile
		{
			public ulong Sector;
			public uint Offset;
			public uint Count;
			public bool Writer;
		}

		private readonly OpenFile[] _open = new OpenFile[MaxOpen];
		private BlockDevice _volume;
		private SectorCache _cache;
		private int _sectorSize;
		private uint _sectorsPerCluster;
		private ulong _fatStart;
		private ulong _dataStart;
		private uint _clusterCount;
		private uint _root;
		private uint _directory;
		private ulong _fsInfo;
		private uint _freeCount;
		private uint _nextFree;

		public uint FreeClusters => _freeCount;

		// False if the device doesn't hold a FAT32 volume; the cache is this volume's alone
		public bool Mount(BlockDevice volume, SectorCache cache)
		{
			int sectorSize = volume.SectorSize;
			if (sectorSize < 512 || sectorSize > SectorCache.MaxSectorSize)
				return false;

			Span<byte> boot = stackalloc byte[SectorCache.MaxSectorSize];
			boot = boot.Slice(0, sectorSize);
			if (!volume.Read(0, 1, boot) || boot[510] != 0x55 || boot[511] != 0xAA)
				return false;

			ushort bytesPerSector = Read16(boot, 11);
			byte sectorsPerCluster = boot[13];
			ushort reserved = Read16(boot, 14);
			byte fats = boot[16];
			ushort rootEntries = Read16(boot, 17);
			ushort totalSectors16 = Read16(boot, 19);
			ushort fatSize16 = Read16(boot, 22);
			uint totalSectors32 = Read32(boot, 32);
			uint fatSize = Read32(boot, 36);
			ushort flags = Read16(boot, 40);
			ushort version = Read16(boot, 42);
			uint root = Read32(boot, 44);
			ushort fsInfo = Read16(boot, 48);

			// FAT32's own shape: no fixed root directory, no 16-bit sizes, version 0
			ulong totalSectors = totalSectors16 != 0 ? totalSectors16 : totalSectors32;
			if (bytesPerSector != sectorSize || sectorsPerCluster == 0 || (sectorsPerCluster & (sectorsPerCluster - 1)) != 0 ||
				reserved == 0 || fats == 0 || rootEntries != 0 || fatSize16 != 0 || fatSize == 0 || version != 0 || totalSectors > volume.SectorCount)
				return false;

			ulong metadata = reserved + (ulong)fats * fatSize;
			if (metadata >= totalSectors)
				return false;

			// The cluster count makes the FAT type, and the FAT must hold an entry for each cluster and the two reserved ones
			ulong clusters = (totalSectors - metadata) / sectorsPerCluster;
			if (clusters < MinClusters || clusters > ClusterMask - 16 || (ulong)fatSize * (ulong)sectorSize / 4 < clusters + 2)
				return false;

			// With mirroring off, the low bits name the one FAT in use; with it on, a change to FAT 0 goes to every copy
			bool mirrored = (flags & 0x80) == 0;
			uint active = mirrored ? 0 : (uint)(flags & 0x0F);
			if (active >= fats)
				return false;

			_volume = volume;
			_cache = cache;
			_sectorSize = sectorSize;
			_sectorsPerCluster = sectorsPerCluster;
			_fatStart = reserved + (ulong)active * fatSize;
			_dataStart = metadata;
			_clusterCount = (uint)clusters;
			_root = root;
			_directory = root;
			cache.Attach(volume);
			if (mirrored)
				cache.Mirror(_fatStart, fatSize, fatSize, fats - 1u);

			if (!IsData(root))
			{
				_volume = null;
				return false;
			}

			// FSInfo keeps the free count and where to look for a free cluster, when its signatures check out
			_fsInfo = 0;
			_freeCount = Unknown;
			_nextFree = 2;
			if (fsInfo != 0 && fsInfo < reserved)
			{
				ReadOnlySpan<byte> info = cache.Get(fsInfo);
				if (info.Length != 0 && Read32(info, 0) == FsInfoLead && Read32(info, 484) == FsInfoStruct && Read32(info, 508) == FsInfoTrail)
				{
					_fsInfo = fsInfo;
					uint freeCount = Read32(info, FsInfoFreeCount);
					uint nextFree = Read32(info, FsInfoNextFree);
					_freeCount = freeCount <= _clusterCount ? freeCount : Unknown;
					_nextFree = IsData(nextFree) ? nextFree : 2;
				}
			}
			return true;
		}

		// Where a path that doesn't start at the root begins; false if it isn't a directory
		public bool SetDirectory(ReadOnlySpan<char> path)
		{
			if (_volume == null || !Walk(path, out Entry entry) || !entry.Directory)
				return false;

			_directory = entry.Cluster;
			return true;
		}

		// Any number may read a file but one writes, and one anyone has open isn't emptied, deleted or renamed; false too when MaxOpen files are open
		public override bool Open(ref FileState file, ReadOnlySpan<char> path, FileAccess access, FileCreate create)
		{
			if (_volume == null)
				return false;

			bool writing = (access & FileAccess.Write) != 0;
			Entry entry;
			int open;
			if (Walk(path, out entry))
			{
				// A directory isn't a file, and a read-only file isn't written
				if (entry.Directory || entry.Count == 0 || (writing && (entry.Attributes & AttrReadOnly) != 0))
					return false;

				// A second writer, or emptying a file under those who have it open, would leave their view of its clusters out of date
				Location at = entry.Last;
				open = FindOpen(at.Sector, at.Offset);
				if (open >= 0 && ((writing && _open[open].Writer) || create == FileCreate.CreateAlways))
					return false;

				// The table is checked before anything changes, so a full one fails an open cleanly
				if (open < 0 && (open = FindFreeOpen()) < 0)
					return false;

				if (create == FileCreate.CreateAlways)
				{
					if (!writing)
						return false;

					FreeChain(entry.Cluster);
					entry.Cluster = 0;
					entry.Size = 0;
					if (!SetEntry(at, 0, 0) || !Commit())
						return false;
				}
			}
			else
			{
				open = FindFreeOpen();
				if (open < 0 || create == FileCreate.OpenExisting || !writing || !Parent(path, out uint directory, out ReadOnlySpan<char> leaf) || !ValidName(leaf))
					return false;

				Span<byte> shortEntry = stackalloc byte[EntrySize];
				shortEntry.Clear();
				shortEntry[11] = AttrArchive;
				bool added = AddEntries(directory, leaf, shortEntry, out entry);
				if (!Commit() || !added)
					return false;
			}

			Location last = entry.Last;
			ref OpenFile slot = ref _open[open];
			if (slot.Count == 0)
			{
				slot.Sector = last.Sector;
				slot.Offset = last.Offset;
				slot.Writer = false;
			}
			slot.Count++;
			slot.Writer = slot.Writer || writing;

			file = default;
			file.Start = entry.Cluster;
			file.Length = entry.Size;
			file.Access = access;
			file.EntrySector = last.Sector;
			file.EntryOffset = last.Offset;

			// How far the chain runs on in consecutive clusters, as a file written in one go does, so reads there need no FAT and go in one request
			if (IsData(entry.Cluster))
			{
				long clusterBytes = (long)_sectorsPerCluster * _sectorSize;
				uint clusters = (uint)((entry.Size + clusterBytes - 1) / clusterBytes);
				file.Run = 1;
				for (uint cluster = entry.Cluster; file.Run < clusters && IsData(cluster + 1) && Next(cluster) == cluster + 1; cluster++)
					file.Run++;
			}
			return true;
		}

		public override void Close(ref FileState file)
		{
			int open = FindOpen(file.EntrySector, file.EntryOffset);
			if (open < 0)
				return;

			if (file.Writable)
				_open[open].Writer = false;
			_open[open].Count--;
		}

		public override int Read(ref FileState file, Span<byte> buffer)
		{
			if (_volume == null || file.Position >= file.Length)
				return 0;

			int total = (int)Math.Min((long)buffer.Length, file.Length - file.Position);
			long clusterBytes = (long)_sectorsPerCluster * _sectorSize;
			long position = file.Position;
			int done = 0;
			while (done < total)
			{
				// A chain that ends before the file does gives what it has
				uint index = (uint)(position / clusterBytes);
				uint cluster = ClusterAt(ref file, index);
				if (!IsData(cluster))
					break;

				// The clusters that follow on from this one in the file's run read with it
				long run = index < file.Run ? file.Run - index : 1;
				long offset = position % clusterBytes;
				ulong sector = ClusterSector(cluster) + (ulong)(offset / _sectorSize);
				int inSector = (int)(offset % _sectorSize);
				int left = total - done;
				int moved;
				if (inSector == 0 && left >= _sectorSize)
				{
					// Whole sectors, up to the end of the run, go straight to the caller
					int sectors = (int)Math.Min((run * clusterBytes - offset) / _sectorSize, (long)(left / _sectorSize));
					if (!_volume.Read(sector, sectors, buffer.Slice(done, sectors * _sectorSize)))
						break;
					moved = sectors * _sectorSize;
				}
				else
				{
					ReadOnlySpan<byte> data = _cache.Get(sector);
					if (data.Length == 0)
						break;
					moved = Math.Min(_sectorSize - inSector, left);
					data.Slice(inSector, moved).CopyTo(buffer.Slice(done, moved));
				}

				done += moved;
				position += moved;
			}
			return done;
		}

		public override int Write(ref FileState file, ReadOnlySpan<byte> data)
		{
			if (_volume == null || !file.Writable || file.Position >= MaxFileSize)
				return 0;

			// A write past the end fills the gap with zeros first, as reading there would have given
			int count = (int)Math.Min((long)data.Length, MaxFileSize - file.Position);
			long gap = file.Position > file.Length ? file.Position - file.Length : 0;
			bool filled = gap == 0 || WriteAt(ref file, file.Length, default, gap) == gap;
			int done = filled ? (int)WriteAt(ref file, file.Position, data.Slice(0, count), count) : 0;
			return Commit() ? done : 0;
		}

		public override bool Delete(ReadOnlySpan<char> path)
		{
			if (_volume == null || !Walk(path, out Entry entry) || entry.Count == 0 || FindOpen(entry.Last.Sector, entry.Last.Offset) >= 0)
				return false;

			// A read-only file stays, and a directory goes only empty and never the root or the default directory
			if ((entry.Attributes & AttrReadOnly) != 0 || (entry.Directory && (entry.Cluster == _root || entry.Cluster == _directory || !IsEmpty(entry.Cluster))))
				return false;

			FreeChain(entry.Cluster);
			for (int i = 0; i < entry.Count; i++)
			{
				Span<byte> sector = _cache.Modify(entry.Slots[i].Sector);
				if (sector.Length == 0)
					return false;
				sector[(int)entry.Slots[i].Offset] = EntryDeleted;
			}
			return Commit();
		}

		public override bool Rename(ReadOnlySpan<char> from, ReadOnlySpan<char> to)
		{
			if (_volume == null || !Walk(from, out Entry source) || source.Count == 0 || FindOpen(source.Last.Sector, source.Last.Offset) >= 0 ||
				!Parent(to, out uint directory, out ReadOnlySpan<char> leaf) || !ValidName(leaf))
				return false;

			if (Find(directory, leaf, out _))
				return false;

			// A directory can't go into itself or anything below it
			if (source.Directory)
			{
				uint at = directory;
				for (uint steps = 0; steps < _clusterCount && at != _root; steps++)
				{
					if (at == source.Cluster || at == 0)
						return false;
					at = ParentOf(at);
				}
			}

			// The new entries keep everything of the old short entry but its name
			Span<byte> shortEntry = stackalloc byte[EntrySize];
			Location old = source.Last;
			ReadOnlySpan<byte> sector = _cache.Get(old.Sector);
			if (sector.Length == 0)
				return false;
			sector.Slice((int)old.Offset, EntrySize).CopyTo(shortEntry);

			bool done = AddEntries(directory, leaf, shortEntry, out _);
			for (int i = 0; done && i < source.Count; i++)
			{
				Span<byte> slot = _cache.Modify(source.Slots[i].Sector);
				done = slot.Length != 0;
				if (done)
					slot[(int)source.Slots[i].Offset] = EntryDeleted;
			}

			// A directory that moved points its ".." at its new parent
			if (done && source.Directory && Find(source.Cluster, "..", out Entry dotDot))
				done = SetEntry(dotDot.Last, directory == _root ? 0 : directory, 0);
			return Commit() && done;
		}

		public override bool CreateDirectory(ReadOnlySpan<char> path)
		{
			if (_volume == null || !Parent(path, out uint directory, out ReadOnlySpan<char> leaf) || !ValidName(leaf) || Find(directory, leaf, out _))
				return false;

			uint cluster = Allocate(_nextFree);
			if (cluster == 0)
				return false;

			// "." and ".." first, ".." to the root as cluster 0
			bool done = ZeroCluster(cluster);
			Span<byte> first = done ? _cache.Modify(ClusterSector(cluster)) : default;
			if (first.Length != 0)
			{
				uint parent = directory == _root ? 0 : directory;
				first.Slice(0, 11).Fill((byte)' ');
				first[0] = (byte)'.';
				first[11] = AttrDirectory;
				Write16(first, 20, (ushort)(cluster >> 16));
				Write16(first, 26, (ushort)cluster);
				first.Slice(EntrySize, 11).Fill((byte)' ');
				first[EntrySize] = (byte)'.';
				first[EntrySize + 1] = (byte)'.';
				first[EntrySize + 11] = AttrDirectory;
				Write16(first, EntrySize + 20, (ushort)(parent >> 16));
				Write16(first, EntrySize + 26, (ushort)parent);
			}

			Span<byte> shortEntry = stackalloc byte[EntrySize];
			shortEntry.Clear();
			shortEntry[11] = AttrDirectory;
			Write16(shortEntry, 20, (ushort)(cluster >> 16));
			Write16(shortEntry, 26, (ushort)cluster);
			done = first.Length != 0 && AddEntries(directory, leaf, shortEntry, out _);
			if (!done)
				FreeChain(cluster);
			return Commit() && done;
		}

		public override int List(ReadOnlySpan<char> path, int skip, Span<DirectoryEntry> entries)
		{
			if (_volume == null || !Walk(path, out Entry directory) || !directory.Directory)
				return -1;

			Span<char> longName = stackalloc char[MaxLongName];
			Span<Location> slots = stackalloc Location[MaxEntries];
			Span<char> shortName = stackalloc char[13];
			NameScanner names = new NameScanner(this, directory.Cluster, longName, slots);
			int seen = 0;
			int count = 0;
			while (count < entries.Length && names.Next(out ReadOnlySpan<byte> entry, out ReadOnlySpan<char> name, out _))
			{
				if (entry[0] == (byte)'.')
					continue;
				if (seen++ < skip)
					continue;

				// A short name without a long one shows in the case Windows' flags in byte 12 give it
				if (name.Length == 0)
					name = shortName.Slice(0, ShortName(entry, shortName, (entry[12] & CaseLowerBase) != 0, (entry[12] & CaseLowerExtension) != 0));

				ref DirectoryEntry item = ref entries[count++];
				item = default;
				Span<byte> bytes = item.Name;
				for (int i = 0; i < name.Length && i < DirectoryEntry.MaxName - 1; i++)
					bytes[i] = (byte)name[i];
				item.Size = Read32(entry, 28);
				item.Directory = (entry[11] & AttrDirectory) != 0 ? 1u : 0u;
			}
			return names.Failed ? -1 : count;
		}

		public override bool Flush() => _volume != null && Commit() && _volume.Flush();

		// The open file whose short entry is there; -1 if there's none
		private int FindOpen(ulong sector, uint offset)
		{
			for (int i = 0; i < MaxOpen; i++)
			{
				if (_open[i].Count != 0 && _open[i].Sector == sector && _open[i].Offset == offset)
					return i;
			}
			return -1;
		}

		private int FindFreeOpen()
		{
			for (int i = 0; i < MaxOpen; i++)
			{
				if (_open[i].Count == 0)
					return i;
			}
			return -1;
		}

		private bool IsData(uint cluster) => cluster >= 2 && cluster < _clusterCount + 2;

		private ulong ClusterSector(uint cluster) => _dataStart + (ulong)(cluster - 2) * _sectorsPerCluster;

		// The FAT entry for a cluster: the next in its chain, zero for a free one; false if the FAT can't be read or written
		private bool GetFat(uint cluster, out uint value)
		{
			ulong offset = (ulong)cluster * 4;
			ReadOnlySpan<byte> data = _cache.Get(_fatStart + offset / (ulong)_sectorSize);
			value = 0;
			if (data.Length == 0)
				return false;

			value = Read32(data, (int)(offset % (ulong)_sectorSize)) & ClusterMask;
			return true;
		}

		// The top four bits of an entry are reserved, so they're kept
		private bool SetFat(uint cluster, uint value)
		{
			ulong offset = (ulong)cluster * 4;
			Span<byte> data = _cache.Modify(_fatStart + offset / (ulong)_sectorSize);
			if (data.Length == 0)
				return false;

			int at = (int)(offset % (ulong)_sectorSize);
			Write32(data, at, (Read32(data, at) & ~ClusterMask) | (value & ClusterMask));
			return true;
		}

		private uint Next(uint cluster) => GetFat(cluster, out uint value) ? value : 0;

		// A free cluster, marked the end of a chain, searched for from hint; zero when the volume is full
		private uint Allocate(uint hint)
		{
			if (_freeCount == 0)
				return 0;

			uint start = IsData(hint) ? hint : 2;
			for (uint i = 0; i < _clusterCount; i++)
			{
				uint cluster = 2 + (start - 2 + i) % _clusterCount;
				if (!GetFat(cluster, out uint value))
					return 0;
				if (value != 0)
					continue;

				if (!SetFat(cluster, EndOfChain))
					return 0;
				_nextFree = IsData(cluster + 1) ? cluster + 1 : 2;
				if (_freeCount != Unknown)
					_freeCount--;
				return cluster;
			}
			return 0;
		}

		// A chain can't be longer than the volume has clusters, so a looped one ends
		private void FreeChain(uint first)
		{
			uint cluster = first;
			for (uint steps = 0; IsData(cluster) && steps < _clusterCount; steps++)
			{
				if (!GetFat(cluster, out uint next) || !SetFat(cluster, 0))
					return;
				if (_freeCount != Unknown)
					_freeCount++;
				cluster = next;
			}
		}

		private bool ZeroCluster(uint cluster)
		{
			for (uint s = 0; s < _sectorsPerCluster; s++)
			{
				if (_cache.Claim(ClusterSector(cluster) + s).Length == 0)
					return false;
			}
			return true;
		}

		// The index-th cluster of the file's chain: from its run, else walked on from the nearest cluster known
		private uint ClusterAt(ref FileState file, uint index)
		{
			if (index < file.Run)
				return file.Start + index;

			// Past the run the FAT is walked, from the run's last cluster or from where the last read left off if that's nearer
			uint cluster = file.Run != 0 ? file.Start + file.Run - 1 : file.Start;
			uint at = file.Run != 0 ? file.Run - 1 : 0;
			if (file.Cursor != 0 && file.CursorIndex > at && file.CursorIndex <= index)
			{
				cluster = file.Cursor;
				at = file.CursorIndex;
			}

			for (; at < index; at++)
			{
				if (!IsData(cluster))
					return 0;
				cluster = Next(cluster);
			}

			file.Cursor = cluster;
			file.CursorIndex = index;
			return cluster;
		}

		// The clusters the file's chain has once it's grown toward count, reusing any already chained past its end; fewer when the volume fills
		private uint Extend(ref FileState file, uint count)
		{
			// What the chain has: the clusters its length needs, and any linked on past them
			long clusterBytes = (long)_sectorsPerCluster * _sectorSize;
			uint have = (uint)((file.Length + clusterBytes - 1) / clusterBytes);
			if (have >= count)
				return have;

			uint last = have != 0 ? ClusterAt(ref file, have - 1) : 0;
			if (have == 0 && IsData(file.Start))
			{
				last = file.Start;
				have = 1;
			}
			while (last != 0 && have < count)
			{
				uint next = Next(last);
				if (!IsData(next))
					break;
				last = next;
				have++;
			}

			// The rest are new, each after the last so a file written in order stays in one run
			while (have < count)
			{
				uint cluster = Allocate(last != 0 ? last + 1 : _nextFree);
				if (cluster == 0)
					break;

				if (last != 0)
				{
					if (!SetFat(last, cluster))
						break;
				}
				else
				{
					file.Start = cluster;
					file.Run = 0;
				}

				if (file.Run == have && cluster == file.Start + have)
					file.Run++;
				last = cluster;
				have++;
			}
			return have;
		}

		// Bytes at a position, or zeros with no data, through the cluster chain
		private long WriteAt(ref FileState file, long position, ReadOnlySpan<byte> data, long bytes)
		{
			long clusterBytes = (long)_sectorsPerCluster * _sectorSize;
			uint needed = (uint)((position + bytes + clusterBytes - 1) / clusterBytes);
			long room = (long)Extend(ref file, needed) * clusterBytes;
			long total = room > position ? Math.Min(bytes, room - position) : 0;
			bool zeros = data.Length == 0;

			long at = position;
			long done = 0;
			while (done < total)
			{
				uint index = (uint)(at / clusterBytes);
				uint cluster = ClusterAt(ref file, index);
				if (!IsData(cluster))
					break;

				long run = index < file.Run ? file.Run - index : 1;
				long offset = at % clusterBytes;
				ulong sector = ClusterSector(cluster) + (ulong)(offset / _sectorSize);
				int inSector = (int)(offset % _sectorSize);
				long left = total - done;
				int moved;
				if (!zeros && inSector == 0 && left >= _sectorSize)
				{
					// Whole sectors, up to the end of the run, go straight to the device, and any copy cached is out of date
					int sectors = (int)Math.Min((run * clusterBytes - offset) / _sectorSize, left / _sectorSize);
					if (!_volume.Write(sector, sectors, data.Slice((int)done, sectors * _sectorSize)))
						break;
					_cache.Discard(sector, (ulong)sectors);
					moved = sectors * _sectorSize;
				}
				else
				{
					// A sector wholly past the file's end has nothing to keep, so it isn't read
					Span<byte> cached = at - inSector >= file.Length ? _cache.Claim(sector) : _cache.Modify(sector);
					if (cached.Length == 0)
						break;
					moved = (int)Math.Min((long)(_sectorSize - inSector), left);
					if (zeros)
						cached.Slice(inSector, moved).Clear();
					else
						data.Slice((int)done, moved).CopyTo(cached.Slice(inSector, moved));
				}

				done += moved;
				at += moved;
			}

			// The entry gets the new size, and the first cluster an empty file was just given
			if (position + done > file.Length)
				file.Length = position + done;
			SetEntry(new Location { Sector = file.EntrySector, Offset = file.EntryOffset }, file.Start, (uint)file.Length);
			return done;
		}

		// Each 32-byte entry slot of a directory in order along its chain; the entry is the cache's, valid until the cache is next used
		private ref struct Scanner
		{
			private readonly Fat32 _fs;
			private ReadOnlySpan<byte> _data;
			private ulong _sectorNumber;
			private uint _cluster;
			private uint _steps;
			private uint _sector;
			private int _offset;
			private bool _loaded;

			// The chain's last cluster seen
			public uint Last;

			// A sector couldn't be read
			public bool Failed;

			public Scanner(Fat32 fs, uint directory)
			{
				_fs = fs;
				_cluster = directory;
				_offset = fs._sectorSize;
				Last = directory;
			}

			public bool Next(out ReadOnlySpan<byte> entry, out Location at)
			{
				entry = default;
				at = default;
				if (Failed)
					return false;

				while (true)
				{
					if (_offset < _fs._sectorSize)
					{
						entry = _data.Slice(_offset, EntrySize);
						at = new Location { Sector = _sectorNumber, Offset = (uint)_offset };
						_offset += EntrySize;
						return true;
					}

					// The next sector, into the next cluster when this one's are done
					if (_loaded)
					{
						_sector++;
						if (_sector == _fs._sectorsPerCluster)
						{
							_sector = 0;
							_cluster = _fs.Next(_cluster);
							_steps++;
						}
					}
					_loaded = true;
					if (!_fs.IsData(_cluster) || _steps >= _fs._clusterCount)
						return false;

					Last = _cluster;
					_sectorNumber = _fs.ClusterSector(_cluster) + _sector;
					_data = _fs._cache.Get(_sectorNumber);
					if (_data.Length == 0)
					{
						Failed = true;
						return false;
					}
					_offset = 0;
				}
			}
		}

		// Each name in a directory in turn: its short entry, its long name (empty when there's none whole and tied to it by checksum), and where its entries are, long name first
		private ref struct NameScanner
		{
			private Scanner _scan;
			private readonly Span<char> _longName;
			private readonly Span<Location> _slots;
			private bool _longValid;
			private byte _longChecksum;
			private int _longNext;
			private int _longCount;
			private int _longLength;

			public NameScanner(Fat32 fs, uint directory, Span<char> longName, Span<Location> slots)
			{
				_scan = new Scanner(fs, directory);
				_longName = longName;
				_slots = slots;
			}

			public bool Failed => _scan.Failed;

			public ReadOnlySpan<Location> Slots(int count) => _slots.Slice(0, count);

			public bool Next(out ReadOnlySpan<byte> entry, out ReadOnlySpan<char> longName, out int count)
			{
				longName = default;
				count = 0;
				while (_scan.Next(out entry, out Location at))
				{
					if (entry[0] == EntryEnd)
						break;

					byte attributes = entry[11];
					if (entry[0] == EntryDeleted)
					{
						_longValid = false;
						continue;
					}

					// Long name entries come last part first, each with its ordinal; any break in the run drops the long name
					if ((attributes & AttrLongNameMask) == AttrLongName)
					{
						int ordinal = entry[0] & LongNameOrdinal;
						if ((entry[0] & LongNameLast) != 0)
						{
							_longName.Clear();
							_longChecksum = entry[13];
							_longNext = ordinal;
							_longCount = 0;
							_longLength = 0;
							_longValid = ordinal >= 1 && ordinal <= MaxEntries - 1;
						}
						if (!_longValid || ordinal != _longNext || entry[13] != _longChecksum)
						{
							_longValid = false;
							continue;
						}

						for (int i = 0; i < LongNameChars; i++)
						{
							ushort unit = Read16(entry, LongNameOffsets[i]);
							int index = (ordinal - 1) * LongNameChars + i;
							if (unit == 0 || index >= MaxLongName)
								break;
							_longName[index] = unit >= 0x20 && unit < 0x7F ? (char)unit : '?';
							if (index + 1 > _longLength)
								_longLength = index + 1;
						}
						_slots[_longCount++] = at;
						_longNext--;
						continue;
					}

					if ((attributes & AttrVolumeId) != 0)
					{
						_longValid = false;
						continue;
					}

					// A long name tied to this entry is its own, and its entries go with it
					bool longOwned = _longValid && _longNext == 0 && _longChecksum == ShortChecksum(entry);
					_longValid = false;
					count = (longOwned ? _longCount : 0) + 1;
					_slots[count - 1] = at;
					longName = longOwned ? _longName.Slice(0, _longLength) : default;
					return true;
				}
				entry = default;
				return false;
			}
		}

		// A name in a directory: its long name or its short name, in any case
		private bool Find(uint directory, ReadOnlySpan<char> name, out Entry found)
		{
			found = default;
			if (name.Length == 0 || name.Length > MaxLongName)
				return false;

			Span<char> longName = stackalloc char[MaxLongName];
			Span<Location> slots = stackalloc Location[MaxEntries];
			Span<char> shortName = stackalloc char[13];
			NameScanner names = new NameScanner(this, directory, longName, slots);
			while (names.Next(out ReadOnlySpan<byte> entry, out ReadOnlySpan<char> stored, out int count))
			{
				bool match = stored.Length != 0 && SameName(stored, name);
				if (!match)
					match = SameName(shortName.Slice(0, ShortName(entry, shortName, false, false)), name);
				if (!match)
					continue;

				found.Cluster = ((uint)Read16(entry, 20) << 16) | Read16(entry, 26);
				found.Size = Read32(entry, 28);
				found.Directory = (entry[11] & AttrDirectory) != 0;
				found.Attributes = entry[11];
				found.Count = count;
				ReadOnlySpan<Location> where = names.Slots(count);
				for (int i = 0; i < count; i++)
					found.Slots[i] = where[i];
				return true;
			}
			return false;
		}

		// Each name in turn from the root or the default directory, "." staying put
		private bool Walk(ReadOnlySpan<char> path, out Entry found)
		{
			found = default;
			found.Cluster = path.Length != 0 && IsSeparator(path[0]) ? _root : _directory;
			found.Directory = true;
			found.Attributes = AttrDirectory;

			int at = 0;
			while (at < path.Length)
			{
				if (IsSeparator(path[at]))
				{
					at++;
					continue;
				}

				int start = at;
				while (at < path.Length && !IsSeparator(path[at]))
					at++;
				ReadOnlySpan<char> name = path.Slice(start, at - start);
				if (name.Length == 1 && name[0] == '.')
					continue;

				// A file can't be passed through
				if (!found.Directory)
					return false;

				// ".." goes up no further than the root, and like the root names no entry a change could reach
				if (name.Length == 2 && name[0] == '.' && name[1] == '.')
				{
					uint parent = found.Cluster == _root ? _root : ParentOf(found.Cluster);
					if (parent == 0)
						return false;
					found = default;
					found.Cluster = parent;
					found.Directory = true;
					found.Attributes = AttrDirectory;
					continue;
				}

				uint directory = found.Cluster;
				if (!Find(directory, name, out found))
					return false;
			}
			return true;
		}

		// The directory a path's last name goes in, and that name
		private bool Parent(ReadOnlySpan<char> path, out uint directory, out ReadOnlySpan<char> leaf)
		{
			directory = 0;
			leaf = default;
			int end = path.Length;
			while (end > 0 && IsSeparator(path[end - 1]))
				end--;

			int start = end;
			while (start > 0 && !IsSeparator(path[start - 1]))
				start--;

			if (start == end || !Walk(path.Slice(0, start), out Entry parent) || !parent.Directory)
				return false;

			directory = parent.Cluster;
			leaf = path.Slice(start, end - start);
			return true;
		}

		private bool IsEmpty(uint directory)
		{
			bool empty = true;
			Scanner scan = new Scanner(this, directory);
			while (scan.Next(out ReadOnlySpan<byte> entry, out _))
			{
				if (entry[0] == EntryEnd)
					break;
				if (entry[0] == EntryDeleted || (entry[11] & AttrLongNameMask) == AttrLongName || entry[0] == (byte)'.')
					continue;

				empty = false;
				break;
			}
			return !scan.Failed && empty;
		}

		// The directory above, from its ".." entry; the root's child names cluster 0
		private uint ParentOf(uint directory)
		{
			if (!Find(directory, "..", out Entry dotDot))
				return 0;
			return dotDot.Cluster == 0 ? _root : dotDot.Cluster;
		}

		private bool ShortTaken(uint directory, ReadOnlySpan<byte> alias)
		{
			bool taken = false;
			Scanner scan = new Scanner(this, directory);
			while (scan.Next(out ReadOnlySpan<byte> entry, out _))
			{
				if (entry[0] == EntryEnd)
					break;
				if (entry[0] == EntryDeleted || (entry[11] & AttrLongNameMask) == AttrLongName)
					continue;

				taken = entry.Slice(0, 11).SequenceEqual(alias);
				if (taken)
					break;
			}
			return scan.Failed || taken;
		}

		// The 8.3 alias: the name upper-cased as a short name holds it, with ~N over the end of the base when that lost something or is taken
		private bool MakeAlias(uint directory, ReadOnlySpan<char> name, Span<byte> alias, out bool longName)
		{
			Span<byte> base8 = stackalloc byte[8];
			int baseLength = 0;
			Span<byte> extension = stackalloc byte[3];
			int extensionLength = 0;
			bool lossy = false;

			// Leading dots are dropped, and the last dot left starts the extension
			int start = 0;
			while (start < name.Length && name[start] == '.')
			{
				start++;
				lossy = true;
			}
			int dot = name.Length;
			for (int i = start; i < name.Length; i++)
			{
				if (name[i] == '.')
					dot = i;
			}

			for (int i = start; i < dot; i++)
			{
				if (name[i] == ' ' || name[i] == '.')
				{
					lossy = true;
					continue;
				}
				byte c = ShortChar(name[i], ref lossy);
				if (baseLength < base8.Length)
					base8[baseLength++] = c;
				else
					lossy = true;
			}
			for (int i = dot + 1; i < name.Length; i++)
			{
				if (name[i] == ' ')
				{
					lossy = true;
					continue;
				}
				byte c = ShortChar(name[i], ref lossy);
				if (extensionLength < extension.Length)
					extension[extensionLength++] = c;
				else
					lossy = true;
			}
			if (baseLength == 0)
			{
				base8[baseLength++] = (byte)'_';
				lossy = true;
			}

			alias.Fill((byte)' ');
			base8.Slice(0, baseLength).CopyTo(alias);
			extension.Slice(0, extensionLength).CopyTo(alias.Slice(8));

			// A name that is its alias exactly, case included, needs no long name
			Span<char> formatted = stackalloc char[13];
			int formattedLength = ShortName(alias, formatted, false, false);
			longName = lossy || !SameCase(formatted.Slice(0, formattedLength), name);
			if (!lossy && !ShortTaken(directory, alias))
				return true;

			longName = true;
			Span<byte> tail = stackalloc byte[8];
			for (uint number = 1; number < 1000000; number++)
			{
				int tailLength = 0;
				for (uint n = number; n != 0; n /= 10)
					tail[tailLength++] = (byte)('0' + n % 10);

				int keep = Math.Min(baseLength, base8.Length - 1 - tailLength);
				alias.Slice(0, 8).Fill((byte)' ');
				base8.Slice(0, keep).CopyTo(alias);
				alias[keep] = (byte)'~';
				for (int i = 0; i < tailLength; i++)
					alias[keep + 1 + i] = tail[tailLength - 1 - i];

				if (!ShortTaken(directory, alias))
					return true;
			}
			return false;
		}

		// count free slots in a row, the directory grown when its chain runs out
		private bool FreeSlots(uint directory, int count, Span<Location> slots)
		{
			int run = 0;
			Scanner scan = new Scanner(this, directory);
			while (run < count && scan.Next(out ReadOnlySpan<byte> entry, out Location at))
			{
				if (entry[0] != EntryEnd && entry[0] != EntryDeleted)
				{
					run = 0;
					continue;
				}
				slots[run++] = at;
			}
			if (scan.Failed)
				return false;

			// The run goes on into new clusters, all free, after the chain's last
			uint last = scan.Last;
			while (run < count)
			{
				uint cluster = Allocate(last + 1);
				if (cluster == 0 || !ZeroCluster(cluster) || !SetFat(last, cluster))
					return false;

				for (uint s = 0; s < _sectorsPerCluster && run < count; s++)
				{
					for (int offset = 0; offset < _sectorSize && run < count; offset += EntrySize)
						slots[run++] = new Location { Sector = ClusterSector(cluster) + s, Offset = (uint)offset };
				}
				last = cluster;
			}
			return true;
		}

		// A name's entries into free slots: its long name when it has one, then the short entry from the template with the alias over its name
		private bool AddEntries(uint directory, ReadOnlySpan<char> name, ReadOnlySpan<byte> shortEntry, out Entry added)
		{
			added = default;
			Span<byte> alias = stackalloc byte[11];
			if (!MakeAlias(directory, name, alias, out bool longName))
				return false;

			int parts = longName ? (name.Length + LongNameChars - 1) / LongNameChars : 0;
			added.Count = parts + 1;
			Span<Location> slots = stackalloc Location[MaxEntries];
			if (!FreeSlots(directory, added.Count, slots))
				return false;
			for (int i = 0; i < added.Count; i++)
				added.Slots[i] = slots[i];

			// The alias is upper case, so the case flags Windows keeps in byte 12 are cleared
			Span<byte> entry = stackalloc byte[EntrySize];
			shortEntry.CopyTo(entry);
			alias.CopyTo(entry);
			entry[12] = 0;

			// Long name entries last part first, each with 13 characters, the name's end marked by a zero and the rest padded
			byte checksum = ShortChecksum(entry);
			for (int i = 0; i < parts; i++)
			{
				int ordinal = parts - i;
				Span<byte> sector = _cache.Modify(slots[i].Sector);
				if (sector.Length == 0)
					return false;

				Span<byte> part = sector.Slice((int)slots[i].Offset, EntrySize);
				part.Clear();
				part[0] = (byte)(ordinal | (i == 0 ? LongNameLast : 0));
				part[11] = AttrLongName;
				part[13] = checksum;
				for (int k = 0; k < LongNameChars; k++)
				{
					int index = (ordinal - 1) * LongNameChars + k;
					ushort unit = index < name.Length ? (ushort)(byte)name[index] : index == name.Length ? (ushort)0x0000 : (ushort)0xFFFF;
					Write16(part, LongNameOffsets[k], unit);
				}
			}

			Location at = slots[parts];
			Span<byte> target = _cache.Modify(at.Sector);
			if (target.Length == 0)
				return false;
			entry.CopyTo(target.Slice((int)at.Offset, EntrySize));

			added.Cluster = ((uint)Read16(entry, 20) << 16) | Read16(entry, 26);
			added.Size = Read32(entry, 28);
			added.Attributes = entry[11];
			added.Directory = (entry[11] & AttrDirectory) != 0;
			return true;
		}

		private bool SetEntry(Location at, uint cluster, uint size)
		{
			Span<byte> sector = _cache.Modify(at.Sector);
			if (sector.Length == 0)
				return false;

			Span<byte> entry = sector.Slice((int)at.Offset, EntrySize);
			Write16(entry, 20, (ushort)(cluster >> 16));
			Write16(entry, 26, (ushort)cluster);
			Write32(entry, 28, size);
			return true;
		}

		// Modified sectors and the free count to the device, as each change ends
		private bool Commit()
		{
			if (_fsInfo != 0)
			{
				Span<byte> info = _cache.Modify(_fsInfo);
				if (info.Length == 0)
					return false;
				Write32(info, FsInfoFreeCount, _freeCount);
				Write32(info, FsInfoNextFree, _nextFree);
			}
			return _cache.Flush();
		}

		private static char Lower(char c) => c >= 'A' && c <= 'Z' ? (char)(c - 'A' + 'a') : c;

		private static char Upper(char c) => c >= 'a' && c <= 'z' ? (char)(c - 'a' + 'A') : c;

		// A stored name against a path component, ignoring case
		private static bool SameName(ReadOnlySpan<char> stored, ReadOnlySpan<char> name)
		{
			if (stored.Length != name.Length)
				return false;
			for (int i = 0; i < name.Length; i++)
			{
				if (Lower(stored[i]) != Lower(name[i]))
					return false;
			}
			return true;
		}

		private static bool SameCase(ReadOnlySpan<char> a, ReadOnlySpan<char> b)
		{
			if (a.Length != b.Length)
				return false;
			for (int i = 0; i < a.Length; i++)
			{
				if (a[i] != b[i])
					return false;
			}
			return true;
		}

		// The 8.3 name as BASE.EXT, without padding, either part lower-cased if asked; returns its length
		private static int ShortName(ReadOnlySpan<byte> entry, Span<char> name, bool lowerBase, bool lowerExtension)
		{
			int at = 0;
			for (int i = 0; i < 8 && entry[i] != (byte)' '; i++)
			{
				char c = i == 0 && entry[i] == EntryKanjiE5 ? (char)EntryDeleted : (char)entry[i];
				name[at++] = lowerBase ? Lower(c) : c;
			}

			if (entry[8] != (byte)' ')
			{
				name[at++] = '.';
				for (int i = 8; i < 11 && entry[i] != (byte)' '; i++)
					name[at++] = lowerExtension ? Lower((char)entry[i]) : (char)entry[i];
			}
			return at;
		}

		// What a long name's entries carry to tie them to their short entry
		private static byte ShortChecksum(ReadOnlySpan<byte> entry)
		{
			byte sum = 0;
			for (int i = 0; i < 11; i++)
				sum = (byte)(((sum & 1) << 7) + (sum >> 1) + entry[i]);
			return sum;
		}

		private static bool IsSeparator(char c) => c == '/' || c == '\\';

		// A name a file can be given: printable, none of the characters FAT reserves, not "." or "..", not ending in a dot or space
		private static bool ValidName(ReadOnlySpan<char> name)
		{
			if (name.Length == 0 || name.Length > MaxLongName || name[name.Length - 1] == '.' || name[name.Length - 1] == ' ')
				return false;

			for (int i = 0; i < name.Length; i++)
			{
				char c = name[i];
				if (c < 0x20 || c > 0x7E || "\"*/:<>?\\|".AsSpan().IndexOf(c) >= 0)
					return false;
			}
			return true;
		}

		// A character as a short name holds it: upper case, or '_' for one it can't (which loses something)
		private static byte ShortChar(char c, ref bool lossy)
		{
			char upper = Upper(c);
			if ((upper >= 'A' && upper <= 'Z') || (upper >= '0' && upper <= '9') || "$%'-_@~`!(){}^#&".AsSpan().IndexOf(upper) >= 0)
				return (byte)upper;

			lossy = true;
			return (byte)'_';
		}

		private static ushort Read16(ReadOnlySpan<byte> bytes, int offset) => (ushort)(bytes[offset] | (bytes[offset + 1] << 8));

		private static uint Read32(ReadOnlySpan<byte> bytes, int offset) => (uint)(bytes[offset] | (bytes[offset + 1] << 8) | (bytes[offset + 2] << 16) | (bytes[offset + 3] << 24));

		private static void Write16(Span<byte> bytes, int offset, ushort value)
		{
			bytes[offset] = (byte)value;
			bytes[offset + 1] = (byte)(value >> 8);
		}

		private static void Write32(Span<byte> bytes, int offset, uint value)
		{
			bytes[offset] = (byte)value;
			bytes[offset + 1] = (byte)(value >> 8);
			bytes[offset + 2] = (byte)(value >> 16);
			bytes[offset + 3] = (byte)(value >> 24);
		}
	}
}
