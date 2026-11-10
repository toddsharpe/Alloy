using System;
using Alloy.Devices;
using Alloy.Kernel.Diag;
using Alloy.Text;

namespace Alloy.Kernel
{
	// Kernel output: always COM1, and the framebuffer console once it is up
	internal static unsafe class Log
	{
		private static Uart16550 s_serial;

		public static void Initialize()
		{
			s_serial = Uart16550.Com1;
			s_serial.Initialize();
			Environment.SetFailFastWriter(&WriteFailFast);
		}

		public static void WriteLine(ReadOnlySpan<char> text)
		{
			ulong flags = Ke.Scheduler.Lock();
			Serial(text);
			Diag.Console.WriteLine(text);
			Ke.Scheduler.Unlock(flags);
		}

		public static void WriteLine(ref SpanWriter writer) => WriteLine(writer.Written);

		public static void Serial(ReadOnlySpan<char> text)
		{
			s_serial.Write(text);
			s_serial.Write("\n");
		}

		private static void WriteFailFast(string message)
		{
			Serial(message);
			Diag.Console.WriteLine(message, true);
			Bugcheck.TraceCaller();
		}
	}
}
