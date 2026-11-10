using System;
using Alloy.Sdk;

namespace Alloy.Apps.Init
{
	// The first process, as MetalOS's init: the window manager first so it owns the screen and serves "wm", the network stack, then the terminal
	internal static class Program
	{
		private static int Main()
		{
			Console.WriteLine("init: Alloy user mode is up");
			Start("wmsvr.exe");
			Start("netsvr.exe");
			Start("term.exe");
			Console.WriteLine("init: started the window manager, the network stack and the terminal");
			return 0;
		}

		private static void Start(string name)
		{
			long process = Kernel.CreateProcess(name);
			if (process < 0)
				Console.WriteLine($"init: could not start {name}");
			else
				Kernel.Close(process);
		}
	}
}
