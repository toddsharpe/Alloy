using System;
using Alloy.Abi;

namespace Alloy.Kernel.Diag
{
	// Register state for unwinding; Gpr is indexed by the x64 unwind register numbers (0 rax ... 4 rsp, 5 rbp ... 15 r15)
	internal unsafe struct UnwindContext
	{
		public ulong Rip;
		public fixed ulong Gpr[16];

		public ulong Rsp
		{
			readonly get => Gpr[4];
			set => Gpr[4] = value;
		}
	}

	// A diagnostics-only virtual unwinder over the kernel's .pdata/.xdata (the Windows x64 unwind format)
	internal static unsafe class StackWalker
	{
		private const int UwopPushNonvol = 0;
		private const int UwopAllocLarge = 1;
		private const int UwopAllocSmall = 2;
		private const int UwopSetFpreg = 3;
		private const int UwopSaveNonvol = 4;
		private const int UwopSaveNonvolFar = 5;
		private const int UwopEpilog = 6;
		private const int UwopSaveXmm128 = 8;
		private const int UwopSaveXmm128Far = 9;
		private const int UwopPushMachframe = 10;
		private const int UnwFlagChainInfo = 4;

		private static ulong s_imageBase;
		private static uint s_imageSize;
		private static uint* s_functions;
		private static int s_functionCount;

		public static void Initialize(ulong imageBase)
		{
			uint size = *(uint*)(imageBase + *(uint*)(imageBase + 0x3C) + 24 + 56);
			if (!PeImage.TryParseLoaded(new ReadOnlySpan<byte>((void*)imageBase, (int)size), out PeImage pe))
				return;
			PeDataDirectory pdata = pe.GetDirectory(PeImage.DirectoryException);
			s_imageBase = imageBase;
			s_imageSize = pe.SizeOfImage;
			s_functions = (uint*)(imageBase + pdata.VirtualAddress);
			s_functionCount = (int)(pdata.Size / 12);
		}

		public static bool InKernelImage(ulong address) => s_imageBase != 0 && address - s_imageBase < s_imageSize;

		// Steps to the caller's frame; false when the walk leaves the kernel image or the stack looks wrong
		public static bool Unwind(ref UnwindContext context)
		{
			if (!InKernelImage(context.Rip))
				return false;

			uint rva = (uint)(context.Rip - s_imageBase);
			uint* function = Lookup(rva);
			if (function == null)
				return PopReturn(ref context);

			uint offset = rva - function[0];
			bool machineFrame = false;
			while (true)
			{
				byte* info = (byte*)(s_imageBase + function[2]);
				int flags = info[0] >> 3;
				int prolog = info[1];
				int count = info[2];
				int frameRegister = info[3] & 0xF;
				int frameOffset = info[3] >> 4;
				ushort* codes = (ushort*)(info + 4);

				// Saved registers are addressed from the frame base: the frame register once established, else rsp
				ulong frameBase = context.Rsp;
				if (frameRegister != 0 && FrameEstablished(codes, count, offset))
					frameBase = context.Gpr[frameRegister] - (ulong)frameOffset * 16;

				for (int i = 0; i < count;)
				{
					int codeOffset = codes[i] & 0xFF;
					int op = (codes[i] >> 8) & 0xF;
					int opInfo = codes[i] >> 12;
					int slots = Slots(op, opInfo);
					if (codeOffset <= offset || offset >= prolog)
					{
						switch (op)
						{
							case UwopPushNonvol:
								context.Gpr[opInfo] = *(ulong*)context.Rsp;
								context.Rsp += 8;
								break;
							case UwopAllocLarge:
								context.Rsp += opInfo == 0 ? codes[i + 1] * 8UL : *(uint*)&codes[i + 1];
								break;
							case UwopAllocSmall:
								context.Rsp += (ulong)opInfo * 8 + 8;
								break;
							case UwopSetFpreg:
								context.Rsp = context.Gpr[frameRegister] - (ulong)frameOffset * 16;
								break;
							case UwopSaveNonvol:
								context.Gpr[opInfo] = *(ulong*)(frameBase + codes[i + 1] * 8UL);
								break;
							case UwopSaveNonvolFar:
								context.Gpr[opInfo] = *(ulong*)(frameBase + *(uint*)&codes[i + 1]);
								break;
							case UwopPushMachframe:
								context.Rsp += opInfo == 0 ? 0UL : 8UL;
								context.Rip = *(ulong*)context.Rsp;
								context.Rsp = *(ulong*)(context.Rsp + 24);
								machineFrame = true;
								break;
						}
					}
					i += slots;
				}

				if ((flags & UnwFlagChainInfo) == 0)
					break;
				function = (uint*)&codes[(count + 1) & ~1];
				offset = uint.MaxValue;
			}

			return machineFrame || PopReturn(ref context);
		}

		private static bool PopReturn(ref UnwindContext context)
		{
			if ((context.Rsp & 7) != 0 || context.Rsp < 0xFFFF800000000000)
				return false;
			context.Rip = *(ulong*)context.Rsp;
			context.Rsp += 8;
			return true;
		}

		private static bool FrameEstablished(ushort* codes, int count, uint offset)
		{
			for (int i = 0; i < count; i += Slots((codes[i] >> 8) & 0xF, codes[i] >> 12))
			{
				if (((codes[i] >> 8) & 0xF) == UwopSetFpreg)
					return (codes[i] & 0xFF) <= offset;
			}
			return false;
		}

		private static int Slots(int op, int opInfo) => op switch
		{
			UwopAllocLarge => opInfo == 0 ? 2 : 3,
			UwopSaveNonvol => 2,
			UwopSaveNonvolFar => 3,
			UwopEpilog => 2,
			UwopSaveXmm128 => 2,
			UwopSaveXmm128Far => 3,
			_ => 1,
		};

		private static uint* Lookup(uint rva)
		{
			int low = 0;
			int high = s_functionCount - 1;
			while (low <= high)
			{
				int middle = (low + high) / 2;
				uint* entry = s_functions + middle * 3;
				if (rva < entry[0])
					high = middle - 1;
				else if (rva >= entry[1])
					low = middle + 1;
				else
					return entry;
			}
			return null;
		}
	}
}
