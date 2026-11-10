using System;
using Alloy.Abi;
using Alloy.Sdk;

namespace Alloy.Apps.Ps
{
	// MetalOS's ps.exe: one line per running process
	internal static class Program
	{
		private const int MaxProcesses = 64;

		private static int Main()
		{
			Span<ProcessEntry> processes = stackalloc ProcessEntry[MaxProcesses];
			int count = Kernel.ListProcesses(processes);
			if (count < 0)
			{
				Console.WriteLine("ps: failed to enumerate processes");
				return 1;
			}

			Console.WriteLine($"{"NAME",-20} {"ID",6} {"VIRT(KB)",10} {"THREADS",8}");
			for (int i = 0; i < count; i++)
			{
				ref ProcessEntry process = ref processes[i];
				Console.WriteLine($"{Kernel.Name(in process),-20} {process.Id,6} {process.VirtualSize / 1024,10} {process.Threads,8}");
			}
			return 0;
		}
	}
}
