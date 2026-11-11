using Alloy.Abi;
using Alloy.Kernel.Ob;

namespace Alloy.Kernel.Io
{
	// An open file on the root file system with its position; the last handle to go closes the file system's open
	internal sealed class KFile : KObject
	{
		public readonly FileSystem System;
		public FileState State;

		public KFile(FileSystem system, in FileState state)
		{
			System = system;
			State = state;
		}

		public long Size => State.Length;

		public long Position => State.Position;

		// The end itself is a valid position (reads there return nothing); past it only for a write, which fills the gap with zeros
		public long Seek(long offset, SeekOrigin origin)
		{
			long basis = origin switch
			{
				SeekOrigin.Begin => 0,
				SeekOrigin.Current => State.Position,
				_ => State.Length,
			};
			long target = basis + offset;
			if (target < 0 || (target > State.Length && !State.Writable))
				return -(long)SyscallError.InvalidArgument;
			State.Position = target;
			return target;
		}

		protected override void Close() => Storage.Close(this);
	}
}
