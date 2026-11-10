using System;
using Alloy.Abi;
using Alloy.Kernel.Ob;

namespace Alloy.Kernel.Io
{
	// A read-only view of one initrd file with a seek position
	internal sealed unsafe class KFile : KObject
	{
		private readonly byte* _data;
		private readonly long _size;
		private long _position;

		private KFile(byte* data, long size)
		{
			_data = data;
			_size = size;
		}

		public long Size => _size;

		public static KFile Open(ReadOnlySpan<char> path)
		{
			if (!Initrd.TryFind(path, out byte* data, out long size))
				return null;
			return new KFile(data, size);
		}

		public ReadOnlySpan<byte> Remaining => new ReadOnlySpan<byte>(_data + _position, (int)(_size - _position));

		public void Advance(long count) => _position += count;

		public long Seek(long offset, SeekOrigin origin)
		{
			long target = origin switch
			{
				SeekOrigin.Begin => offset,
				SeekOrigin.Current => _position + offset,
				_ => _size + offset,
			};
			if (target < 0 || target > _size)
				return -(long)SyscallError.InvalidArgument;
			_position = target;
			return target;
		}
	}

	// The initrd the loader handed over, reached through the physical window
	internal static unsafe class Initrd
	{
		private static byte* s_image;
		private static int s_size;

		public static void Initialize(BootInfo* info)
		{
			s_image = (byte*)BootInfo.ToVirtual(info->Initrd);
			s_size = (int)info->InitrdSize;
		}

		public static bool TryFind(ReadOnlySpan<char> path, out byte* data, out long size)
		{
			data = null;
			size = 0;
			ReadOnlySpan<byte> image = new ReadOnlySpan<byte>(s_image, s_size);
			if (!InitrdReader.TryOpen(image, out InitrdReader reader) || !reader.TryFind(path, out ReadOnlySpan<byte> file))
				return false;
			fixed (byte* start = file)
				data = start;
			size = file.Length;
			return true;
		}
	}
}
