using System;
using System.Runtime.InteropServices;
using Alloy.Abi;
using Alloy.Arch.X64;
using Alloy.Kernel.Ps;

namespace Alloy.Kernel.Arch
{
	// Converts between a thread's saved user frame and a Windows CONTEXT, for GetThreadContext, SetThreadContext and exceptions
	internal static class UserContext
	{
		private const int MxcsrOffset = 24;
		private const int ContextFlagsOffset = 0x30;

		// Flags ring 3 may change: CF PF AF ZF SF TF DF OF; IF and the reserved bit 1 always stay set
		private const ulong UserFlags = 0xDD5;

		// The ContextFlags a caller asked for, in a CONTEXT it passed as bytes
		public static uint ContextFlagsOf(ReadOnlySpan<byte> context) => MemoryMarshal.Read<uint>(context.Slice(ContextFlagsOffset));

		// A CONTEXT is handled as bytes outside this class; it is pinned here only to fill the fixed-layout struct
		public static unsafe void ToContext(InterruptFrame* frame, Span<byte> context, uint flags)
		{
			if (context.Length < WindowsContext.Size)
				Environment.FailFast("FAILFAST: a CONTEXT buffer too small");
			fixed (byte* pointer = context)
				ToContext(frame, (WindowsContext*)pointer, flags);
		}

		public static unsafe void FromContext(ReadOnlySpan<byte> context, InterruptFrame* frame)
		{
			if (context.Length < WindowsContext.Size)
				Environment.FailFast("FAILFAST: a CONTEXT buffer too small");
			fixed (byte* pointer = context)
				FromContext((WindowsContext*)pointer, frame);
		}

		private static unsafe void ToContext(InterruptFrame* frame, WindowsContext* context, uint flags)
		{
			new Span<byte>(context, WindowsContext.Size).Clear();
			context->ContextFlags = flags;
			context->MxCsr = *(uint*)(frame->FxSave + MxcsrOffset);
			context->SegCs = (ushort)frame->Cs;
			context->SegSs = (ushort)frame->Ss;
			context->SegDs = (ushort)frame->Ss;
			context->SegEs = (ushort)frame->Ss;
			context->SegGs = (ushort)frame->Ss;
			context->EFlags = (uint)frame->Rflags;
			context->Rax = frame->Rax;
			context->Rcx = frame->Rcx;
			context->Rdx = frame->Rdx;
			context->Rbx = frame->Rbx;
			context->Rsp = frame->Rsp;
			context->Rbp = frame->Rbp;
			context->Rsi = frame->Rsi;
			context->Rdi = frame->Rdi;
			context->R8 = frame->R8;
			context->R9 = frame->R9;
			context->R10 = frame->R10;
			context->R11 = frame->R11;
			context->R12 = frame->R12;
			context->R13 = frame->R13;
			context->R14 = frame->R14;
			context->R15 = frame->R15;
			context->Rip = frame->Rip;
			new ReadOnlySpan<byte>(frame->FxSave, 512).CopyTo(new Span<byte>(context->FltSave, 512));
		}

		// Applies the parts ContextFlags names; segments and privileged flags stay as ring 3 requires
		private static unsafe void FromContext(WindowsContext* context, InterruptFrame* frame)
		{
			uint flags = context->ContextFlags;
			if ((flags & (WindowsContext.Control & 0xFF)) != 0)
			{
				frame->Rip = context->Rip;
				frame->Rsp = context->Rsp;
				frame->Rflags = (context->EFlags & UserFlags) | 0x202;
			}
			if ((flags & (WindowsContext.Integer & 0xFF)) != 0)
			{
				frame->Rax = context->Rax;
				frame->Rcx = context->Rcx;
				frame->Rdx = context->Rdx;
				frame->Rbx = context->Rbx;
				frame->Rbp = context->Rbp;
				frame->Rsi = context->Rsi;
				frame->Rdi = context->Rdi;
				frame->R8 = context->R8;
				frame->R9 = context->R9;
				frame->R10 = context->R10;
				frame->R11 = context->R11;
				frame->R12 = context->R12;
				frame->R13 = context->R13;
				frame->R14 = context->R14;
				frame->R15 = context->R15;
			}
			if ((flags & (WindowsContext.FloatingPoint & 0xFF)) != 0)
			{
				new ReadOnlySpan<byte>(context->FltSave, 512).CopyTo(new Span<byte>(frame->FxSave, 512));
				*(uint*)(frame->FxSave + MxcsrOffset) = context->MxCsr & 0xFFFF;
			}
		}

		// A fresh thread's frame: the entry point with its argument in rcx, as Windows starts a thread, and the default FPU state
		public static unsafe void Initialize(InterruptFrame* frame, ulong rip, ulong rsp, ulong argument)
		{
			new Span<byte>(frame, InterruptFrame.Size).Clear();
			*(ushort*)frame->FxSave = 0x037F;
			*(uint*)(frame->FxSave + MxcsrOffset) = 0x1F80;
			*(uint*)(frame->FxSave + MxcsrOffset + 4) = 0xFFFF;
			frame->Rip = rip;
			frame->Rsp = rsp;
			frame->Rcx = argument;
			frame->Rflags = 0x202;
			frame->Cs = Descriptors.UserCode | 3;
			frame->Ss = Descriptors.UserData | 3;
		}
	}

	// Hands a ring-3 fault to the process's dispatcher: a CONTEXT and EXCEPTION_RECORD on the user stack, as Windows' KiUserExceptionDispatcher gets them
	internal static class ExceptionDelivery
	{
		public static unsafe bool Deliver(KProcess process, InterruptFrame* frame)
		{
			WindowsExceptionRecord record = default;
			switch (frame->Vector)
			{
				case 0:
					record.ExceptionCode = WindowsExceptionRecord.IntegerDivideByZero;
					break;
				case 1:
					record.ExceptionCode = WindowsExceptionRecord.SingleStep;
					break;
				case 3:
					// int3 leaves rip after itself; Windows reports the breakpoint's own address
					frame->Rip--;
					record.ExceptionCode = WindowsExceptionRecord.Breakpoint;
					break;
				case 4:
					record.ExceptionCode = WindowsExceptionRecord.IntegerOverflow;
					break;
				case 5:
					record.ExceptionCode = WindowsExceptionRecord.ArrayBoundsExceeded;
					break;
				case 6:
					record.ExceptionCode = WindowsExceptionRecord.IllegalInstruction;
					break;
				case 13:
					// Windows reports a general protection fault (usually a non-canonical address) as an access violation at -1
					record.ExceptionCode = WindowsExceptionRecord.AccessViolation;
					record.NumberParameters = 2;
					record.ExceptionInformation[1] = ulong.MaxValue;
					break;
				case 14:
					record.ExceptionCode = WindowsExceptionRecord.AccessViolation;
					record.NumberParameters = 2;
					record.ExceptionInformation[0] = (frame->ErrorCode & 0x10) != 0 ? 8UL : (frame->ErrorCode & 2) != 0 ? 1UL : 0;
					record.ExceptionInformation[1] = Cpu.ReadCr2();
					break;
				case 16:
				case 19:
					record.ExceptionCode = WindowsExceptionRecord.FloatMultipleTraps;
					break;
				default:
					return false;
			}
			record.ExceptionAddress = frame->Rip;

			// Below the faulting rsp: the CONTEXT, the record, then a return slot and shadow space, as if the dispatcher had been called
			ulong contextAddress = (frame->Rsp - WindowsContext.Size - 64) & ~15UL;
			ulong recordAddress = (contextAddress - WindowsExceptionRecord.Size) & ~15UL;
			ulong rsp = recordAddress - 40;
			Span<byte> context = stackalloc byte[WindowsContext.Size];
			UserContext.ToContext(frame, context, WindowsContext.All);
			if (!UserMemory.CopyTo(contextAddress, context) || !UserMemory.CopyTo(recordAddress, in record) || !UserMemory.CopyTo<ulong>(rsp, 0))
				return false;

			frame->Rip = process.ExceptionDispatcher;
			frame->Rsp = rsp;
			frame->Rcx = recordAddress;
			frame->Rdx = contextAddress;
			frame->Rflags &= ~0x100UL;
			return true;
		}
	}
}
