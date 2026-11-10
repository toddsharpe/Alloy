using System;
using System.Runtime.InteropServices;
using Alloy.Abi;

namespace Alloy.Sdk
{
	// The raw kernel interface through the Win32 layer's stub (src/Win32/win32.asm), linked statically; the rest of the SDK wraps it in typed calls
	public static unsafe class SystemCall
	{
		[DllImport("*", EntryPoint = "PhxSyscall", ExactSpelling = true)]
		private static extern long PhxSyscall(ulong number, ulong a0, ulong a1, ulong a2, ulong a3, ulong a4, ulong a5);

		[DllImport("*", EntryPoint = "PhxGetProcessArgument", ExactSpelling = true)]
		internal static extern ulong PhxGetProcessArgument();

		// A result >= 0 on success, or a negated SyscallError
		public static long Invoke(Syscall number, ulong a0 = 0, ulong a1 = 0, ulong a2 = 0, ulong a3 = 0, ulong a4 = 0, ulong a5 = 0) =>
			PhxSyscall((ulong)number, a0, a1, a2, a3, a4, a5);

		// One line straight to the kernel log (COM1 and the console), bypassing stdout; non-ASCII becomes '?'
		public static void DebugPrint(ReadOnlySpan<char> text)
		{
			byte* bytes = stackalloc byte[200];
			int count = Ascii(text, bytes, 200);
			Invoke(Syscall.DebugPrint, (ulong)bytes, (ulong)count);
		}

		internal static int Ascii(ReadOnlySpan<char> text, byte* destination, int capacity)
		{
			int length = Math.Min(text.Length, capacity);
			for (int i = 0; i < length; i++)
				destination[i] = text[i] < 0x80 ? (byte)text[i] : (byte)'?';
			return length;
		}

		internal static long Error(SyscallError error) => -(long)error;
	}
}
