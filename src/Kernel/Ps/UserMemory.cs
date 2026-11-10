using System;
using System.Runtime;
using System.Runtime.CompilerServices;
using Alloy.Abi;

namespace Alloy.Kernel.Ps
{
	// The only way the kernel touches user memory: range checks, then a copy that SMAP allows and the fixup table recovers
	internal static unsafe class UserMemory
	{
		public static bool IsUserRange(ulong address, ulong length) =>
			address >= MemoryLayout.UserStart && length <= MemoryLayout.UserEnd && address <= MemoryLayout.UserEnd - length;

		public static bool CopyFrom(void* destination, ulong userSource, ulong length) =>
			IsUserRange(userSource, length) && (length == 0 || PhxCopyUser(destination, (void*)userSource, (nuint)length) != 0);

		public static bool CopyTo(ulong userDestination, void* source, ulong length) =>
			IsUserRange(userDestination, length) && (length == 0 || PhxCopyUser((void*)userDestination, source, (nuint)length) != 0);

		// Reads an ASCII path of at most 255 bytes into chars
		public static bool ReadPath(ulong address, ulong length, Span<char> destination, out int written)
		{
			written = 0;
			if (length == 0 || length > 255 || (ulong)destination.Length < length)
				return false;
			byte* bytes = stackalloc byte[256];
			if (!CopyFrom(bytes, address, length))
				return false;
			for (int i = 0; i < (int)length; i++)
				destination[i] = (char)bytes[i];
			written = (int)length;
			return true;
		}

		[RuntimeImport("*", "PhxCopyUser")]
		[MethodImpl(MethodImplOptions.InternalCall)]
		private static extern int PhxCopyUser(void* destination, void* source, nuint count);
	}
}
