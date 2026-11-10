using System;
using Alloy.Devices;
using Alloy.Efi;
using Alloy.Text;

namespace Alloy.Boot
{
	// Loader output goes to COM1 (what boot tests read) and to the UEFI console (what the Hyper-V window shows)
	internal static class Log
	{
		private static Uart16550 s_serial;

		public static unsafe void Initialize()
		{
			s_serial = Uart16550.Com1;
			s_serial.Initialize();
			EfiApplication.SystemTable->ConOut->ClearScreen(EfiApplication.SystemTable->ConOut);
			Environment.SetFailFastWriter(&WriteFailFast);
		}

		public static void WriteLine(ReadOnlySpan<char> text)
		{
			s_serial.Write(text);
			s_serial.Write("\n");
			EfiApplication.ConsoleWrite(text);
			EfiApplication.ConsoleWrite("\r\n");
		}

		public static void WriteLine(ref SpanWriter writer) => WriteLine(writer.Written);

		private static void WriteFailFast(string message) => WriteLine(message);
	}
}
