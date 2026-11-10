using System;
using System.Diagnostics;
using System.Runtime;
using System.Runtime.CompilerServices;
using Alloy.Abi;
using Alloy.Arch.X64;
using Alloy.Kernel.Arch;
using Alloy.Text;

namespace Alloy.Kernel.Diag
{
	// Fatal errors and breakpoints: registers plus a symbolized stack trace on COM1 and the console
	internal static unsafe class Bugcheck
	{
		private const int MaxFrames = 24;

		private static byte* s_symbolData;
		private static int s_symbolSize;

		public static void Initialize(BootInfo* info)
		{
			if (info->Symbols != 0)
			{
				s_symbolData = (byte*)BootInfo.ToVirtual(info->Symbols);
				s_symbolSize = (int)info->SymbolsSize;
			}
		}

		public static string ExceptionName(int vector) => vector switch
		{
			0 => "DIVIDE_ERROR",
			1 => "DEBUG",
			2 => "NMI",
			3 => "BREAKPOINT",
			4 => "OVERFLOW",
			5 => "BOUND_RANGE",
			6 => "INVALID_OPCODE",
			7 => "DEVICE_NOT_AVAILABLE",
			8 => "DOUBLE_FAULT",
			10 => "INVALID_TSS",
			11 => "SEGMENT_NOT_PRESENT",
			12 => "STACK_FAULT",
			13 => "GENERAL_PROTECTION",
			14 => "PAGE_FAULT",
			16 => "X87_FLOATING_POINT",
			17 => "ALIGNMENT_CHECK",
			18 => "MACHINE_CHECK",
			19 => "SIMD_FLOATING_POINT",
			_ => "EXCEPTION",
		};

		[DoesNotReturn]
		public static void Fatal(InterruptFrame* frame, string reason)
		{
			Report(frame, reason);
			Cpu.HaltForever();
		}

		public static void Report(InterruptFrame* frame, string reason)
		{
			bool fatal = frame->Vector != Interrupts.Breakpoint;
			SpanWriter line = new SpanWriter(stackalloc char[160]);
			line.Append(fatal ? "BUGCHECK: " : "BREAK: ");
			line.Append(reason);
			line.Append(" (vector ");
			line.Append(frame->Vector);
			line.Append(", error 0x");
			line.AppendHex(frame->ErrorCode);
			line.Append(") at ");
			AppendSymbol(ref line, frame->Rip);
			Emit(ref line, fatal);

			if (frame->Vector == Interrupts.PageFault)
			{
				line = new SpanWriter(stackalloc char[96]);
				line.Append("  faulting address 0x");
				line.AppendHex(Cpu.ReadCr2(), 16);
				Emit(ref line, fatal);
			}

			if (fatal)
			{
				Registers("rax", frame->Rax, "rbx", frame->Rbx, "rcx", frame->Rcx, "rdx", frame->Rdx);
				Registers("rsi", frame->Rsi, "rdi", frame->Rdi, "rbp", frame->Rbp, "rsp", frame->Rsp);
				Registers("r8 ", frame->R8, "r9 ", frame->R9, "r10", frame->R10, "r11", frame->R11);
				Registers("r12", frame->R12, "r13", frame->R13, "r14", frame->R14, "r15", frame->R15);
			}

			UnwindContext context = default;
			context.Rip = frame->Rip;
			context.Rsp = frame->Rsp;
			context.Gpr[3] = frame->Rbx;
			context.Gpr[5] = frame->Rbp;
			context.Gpr[6] = frame->Rsi;
			context.Gpr[7] = frame->Rdi;
			context.Gpr[12] = frame->R12;
			context.Gpr[13] = frame->R13;
			context.Gpr[14] = frame->R14;
			context.Gpr[15] = frame->R15;
			StackTrace(ref context, fatal);
		}

		// Prints the trace of whoever called this, for FailFast
		[MethodImpl(MethodImplOptions.NoInlining)]
		public static void TraceCaller()
		{
			ulong* captured = stackalloc ulong[10];
			PhxCaptureContext(captured);
			UnwindContext context = default;
			context.Rip = captured[0];
			context.Rsp = captured[1];
			context.Gpr[3] = captured[2];
			context.Gpr[5] = captured[3];
			context.Gpr[6] = captured[4];
			context.Gpr[7] = captured[5];
			context.Gpr[12] = captured[6];
			context.Gpr[13] = captured[7];
			context.Gpr[14] = captured[8];
			context.Gpr[15] = captured[9];
			StackTrace(ref context, true);
		}

		private static void StackTrace(ref UnwindContext context, bool fatal)
		{
			Span<char> buffer = stackalloc char[160];
			SpanWriter line = new SpanWriter(buffer);
			line.Append("  stack:");
			Emit(ref line, fatal);
			for (int frame = 0; frame < MaxFrames && StackWalker.InKernelImage(context.Rip); frame++)
			{
				line = new SpanWriter(buffer);
				line.Append("    ");
				AppendSymbol(ref line, context.Rip);
				Emit(ref line, fatal);
				if (!StackWalker.Unwind(ref context))
					break;
			}
		}

		private static void AppendSymbol(ref SpanWriter line, ulong address)
		{
			line.Append("0x");
			line.AppendHex(address, 16);
			if (s_symbolData == null || !StackWalker.InKernelImage(address))
				return;
			if (!SymbolTable.TryOpen(new ReadOnlySpan<byte>(s_symbolData, s_symbolSize), out SymbolTable table))
				return;
			if (!table.TryLookup((uint)(address - MemoryLayout.KernelBase), out ReadOnlySpan<byte> name, out uint offset))
				return;
			line.Append(' ');
			foreach (byte c in name)
				line.Append((char)c);
			line.Append("+0x");
			line.AppendHex(offset);
		}

		private static void Registers(string n0, ulong v0, string n1, ulong v1, string n2, ulong v2, string n3, ulong v3)
		{
			SpanWriter line = new SpanWriter(stackalloc char[128]);
			Register(ref line, n0, v0);
			Register(ref line, n1, v1);
			Register(ref line, n2, v2);
			Register(ref line, n3, v3);
			Emit(ref line, true);
		}

		private static void Register(ref SpanWriter line, string name, ulong value)
		{
			line.Append("  ");
			line.Append(name);
			line.Append(' ');
			line.AppendHex(value, 16);
		}

		private static void Emit(ref SpanWriter line, bool alert)
		{
			Log.Serial(line.Written);
			Console.WriteLine(line.Written, alert);
		}

		[RuntimeImport("*", "PhxCaptureContext")]
		[MethodImpl(MethodImplOptions.InternalCall)]
		private static extern void PhxCaptureContext(ulong* context);
	}
}
