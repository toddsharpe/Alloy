using System;
using System.Diagnostics;

namespace Alloy.Apps.Crash
{
	// MetalOS's crash.exe: faults on purpose; the runtime turns the fault into an unhandled exception, and the kernel ends this process and nothing else
	internal static class Program
	{
		// Statics that are never assigned, so the compiler can't fold the fault away or prove it ahead of time
#pragma warning disable CS0649
		private static ulong s_address;
		private static int s_zero;
#pragma warning restore CS0649

		private static unsafe int Main(string[] args)
		{
			Console.WriteLine("Userspace crash demo.");
			string[] commandLine = Environment.GetCommandLineArgs();
			Console.WriteLine($"argc: {commandLine.Length}");
			for (int i = 0; i < commandLine.Length; i++)
				Console.WriteLine($"argv[{i}]: {commandLine[i]}");
			Console.WriteLine();
			if (args.Length < 1)
			{
				Console.WriteLine("Usage: crash [null, div0, brk, throw]");
				return -1;
			}

			switch (args[0])
			{
				case "null":
					*(int*)s_address = 1;
					break;
				case "div0":
					Console.WriteLine(1 / s_zero);
					break;
				case "brk":
					Debugger.Break();
					break;
				case "throw":
					throw new InvalidOperationException("thrown on purpose");
				default:
					Console.WriteLine($"Unknown type: {args[0]}");
					break;
			}
			return 0;
		}
	}
}
