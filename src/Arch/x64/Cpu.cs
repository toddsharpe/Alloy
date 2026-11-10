using System.Diagnostics;
using System.Runtime;
using System.Runtime.CompilerServices;

namespace Alloy.Arch.X64
{
	public struct CpuIdResult
	{
		public uint Eax;
		public uint Ebx;
		public uint Ecx;
		public uint Edx;
	}

	// Ring-0 instructions, implemented in Cpu.asm and bound by symbol with no transition frame
	public static unsafe class Cpu
	{
		[RuntimeImport("*", "PhxInByte")]
		[MethodImpl(MethodImplOptions.InternalCall)]
		public static extern byte InByte(ushort port);

		[RuntimeImport("*", "PhxOutByte")]
		[MethodImpl(MethodImplOptions.InternalCall)]
		public static extern void OutByte(ushort port, byte value);

		[RuntimeImport("*", "PhxInWord")]
		[MethodImpl(MethodImplOptions.InternalCall)]
		public static extern ushort InWord(ushort port);

		[RuntimeImport("*", "PhxOutWord")]
		[MethodImpl(MethodImplOptions.InternalCall)]
		public static extern void OutWord(ushort port, ushort value);

		[RuntimeImport("*", "PhxInDword")]
		[MethodImpl(MethodImplOptions.InternalCall)]
		public static extern uint InDword(ushort port);

		[RuntimeImport("*", "PhxOutDword")]
		[MethodImpl(MethodImplOptions.InternalCall)]
		public static extern void OutDword(ushort port, uint value);

		[RuntimeImport("*", "PhxReadMsr")]
		[MethodImpl(MethodImplOptions.InternalCall)]
		public static extern ulong ReadMsr(uint msr);

		[RuntimeImport("*", "PhxWriteMsr")]
		[MethodImpl(MethodImplOptions.InternalCall)]
		public static extern void WriteMsr(uint msr, ulong value);

		[RuntimeImport("*", "PhxReadCr0")]
		[MethodImpl(MethodImplOptions.InternalCall)]
		public static extern ulong ReadCr0();

		[RuntimeImport("*", "PhxReadCr2")]
		[MethodImpl(MethodImplOptions.InternalCall)]
		public static extern ulong ReadCr2();

		[RuntimeImport("*", "PhxReadCr3")]
		[MethodImpl(MethodImplOptions.InternalCall)]
		public static extern ulong ReadCr3();

		[RuntimeImport("*", "PhxReadCr4")]
		[MethodImpl(MethodImplOptions.InternalCall)]
		public static extern ulong ReadCr4();

		[RuntimeImport("*", "PhxWriteCr0")]
		[MethodImpl(MethodImplOptions.InternalCall)]
		public static extern void WriteCr0(ulong value);

		[RuntimeImport("*", "PhxWriteCr3")]
		[MethodImpl(MethodImplOptions.InternalCall)]
		public static extern void WriteCr3(ulong value);

		[RuntimeImport("*", "PhxWriteCr4")]
		[MethodImpl(MethodImplOptions.InternalCall)]
		public static extern void WriteCr4(ulong value);

		[RuntimeImport("*", "PhxReadFlags")]
		[MethodImpl(MethodImplOptions.InternalCall)]
		public static extern ulong ReadFlags();

		[RuntimeImport("*", "PhxDisableInterrupts")]
		[MethodImpl(MethodImplOptions.InternalCall)]
		public static extern void DisableInterrupts();

		[RuntimeImport("*", "PhxEnableInterrupts")]
		[MethodImpl(MethodImplOptions.InternalCall)]
		public static extern void EnableInterrupts();

		[RuntimeImport("*", "PhxPause")]
		[MethodImpl(MethodImplOptions.InternalCall)]
		public static extern void Pause();

		[RuntimeImport("*", "PhxInvalidatePage")]
		[MethodImpl(MethodImplOptions.InternalCall)]
		public static extern void InvalidatePage(void* address);

		[RuntimeImport("*", "PhxReadTsc")]
		[MethodImpl(MethodImplOptions.InternalCall)]
		public static extern ulong ReadTsc();

		[RuntimeImport("*", "PhxCpuId")]
		[MethodImpl(MethodImplOptions.InternalCall)]
		private static extern void CpuIdRaw(uint leaf, uint subleaf, CpuIdResult* result);

		[RuntimeImport("*", "PhxHaltForever")]
		[MethodImpl(MethodImplOptions.InternalCall)]
		[DoesNotReturn]
		public static extern void HaltForever();

		public static CpuIdResult CpuId(uint leaf, uint subleaf = 0)
		{
			CpuIdResult result;
			CpuIdRaw(leaf, subleaf, &result);
			return result;
		}
	}
}
