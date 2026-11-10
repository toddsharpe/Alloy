using System.Runtime;
using System.Runtime.CompilerServices;
using Alloy.Arch.X64;

namespace Alloy.Kernel.Arch
{
	// GDT, TSS and IDT in MetalOS's layout; syscall/sysret depend on the order of the code and data descriptors
	internal static unsafe class Descriptors
	{
		public const ushort KernelCode = 0x08;
		public const ushort KernelData = 0x10;
		public const ushort User32Code = 0x18;
		public const ushort UserData = 0x20;
		public const ushort UserCode = 0x28;
		public const ushort TaskState = 0x30;

		public const int IstDoubleFault = 1;
		public const int IstNmi = 2;
		public const int IstDebug = 3;
		public const int IstMachineCheck = 4;

		private const int TssSize = 104;
		private const int TssRsp0Offset = 4;
		private const int TssIstOffset = 36;

		private static byte* s_tss;

		public static void Initialize()
		{
			ulong* gdt = (ulong*)PhxGdt();
			gdt[0] = 0;
			gdt[1] = 0x00AF9A000000FFFF;
			gdt[2] = 0x00CF92000000FFFF;
			gdt[3] = 0x00CFFA000000FFFF;
			gdt[4] = 0x00CFF2000000FFFF;
			gdt[5] = 0x00AFFA000000FFFF;

			s_tss = PhxTss();
			new System.Span<byte>(s_tss, TssSize).Clear();
			*(ushort*)(s_tss + 102) = TssSize;
			nuint stackSize;
			byte* stacks = PhxIstStacks(&stackSize);
			for (int ist = 1; ist <= 4; ist++)
				*(ulong*)(s_tss + TssIstOffset + (ist - 1) * 8) = (ulong)(stacks + (nuint)ist * stackSize);

			ulong tss = (ulong)s_tss;
			gdt[6] = (TssSize - 1) | ((tss & 0xFFFFFF) << 16) | (0x89UL << 40) | (((tss >> 24) & 0xFF) << 56);
			gdt[7] = tss >> 32;

			byte* gdtr = stackalloc byte[10];
			Describe(gdtr, PhxGdt(), 8 * 8);
			PhxLoadGdt(gdtr);
			PhxLoadTss(TaskState);

			ulong* idt = (ulong*)PhxIdt();
			ulong* stubs = PhxIsrTable();
			for (int vector = 0; vector < 256; vector++)
			{
				int ist = vector switch { 2 => IstNmi, 8 => IstDoubleFault, 18 => IstMachineCheck, _ => 0 };
				byte attributes = vector == 3 ? (byte)0xEE : (byte)0x8E;
				ulong handler = stubs[vector];
				idt[vector * 2] = (handler & 0xFFFF) | ((ulong)KernelCode << 16) | ((ulong)ist << 32) | ((ulong)attributes << 40) | (((handler >> 16) & 0xFFFF) << 48);
				idt[vector * 2 + 1] = handler >> 32;
			}
			byte* idtr = stackalloc byte[10];
			Describe(idtr, PhxIdt(), 256 * 16);
			PhxLoadIdt(idtr);
		}

		// The stack the CPU switches to when an interrupt or syscall arrives from ring 3
		public static void SetKernelStack(ulong rsp0) => *(ulong*)(s_tss + TssRsp0Offset) = rsp0;

		// The 10-byte limit and base operand of lgdt and lidt
		private static void Describe(byte* register, void* table, int size)
		{
			*(ushort*)register = (ushort)(size - 1);
			*(ulong*)(register + 2) = (ulong)table;
		}

		[RuntimeImport("*", "PhxGdt")]
		[MethodImpl(MethodImplOptions.InternalCall)]
		private static extern void* PhxGdt();

		[RuntimeImport("*", "PhxTss")]
		[MethodImpl(MethodImplOptions.InternalCall)]
		private static extern byte* PhxTss();

		[RuntimeImport("*", "PhxIdt")]
		[MethodImpl(MethodImplOptions.InternalCall)]
		private static extern void* PhxIdt();

		[RuntimeImport("*", "PhxIstStacks")]
		[MethodImpl(MethodImplOptions.InternalCall)]
		private static extern byte* PhxIstStacks(nuint* stackSize);

		[RuntimeImport("*", "PhxIsrTable")]
		[MethodImpl(MethodImplOptions.InternalCall)]
		private static extern ulong* PhxIsrTable();

		[RuntimeImport("*", "PhxLoadGdt")]
		[MethodImpl(MethodImplOptions.InternalCall)]
		private static extern void PhxLoadGdt(void* gdtr);

		[RuntimeImport("*", "PhxLoadTss")]
		[MethodImpl(MethodImplOptions.InternalCall)]
		private static extern void PhxLoadTss(ushort selector);

		[RuntimeImport("*", "PhxLoadIdt")]
		[MethodImpl(MethodImplOptions.InternalCall)]
		private static extern void PhxLoadIdt(void* idtr);
	}
}
