using System;
using Alloy.Abi;
using Alloy.Sdk;

namespace Alloy.Apps.Time
{
	// MetalOS's time.exe: the wall-clock time from the RTC
	internal static class Program
	{
		private static int Main()
		{
			SystemTime time = Kernel.Time;
			Console.WriteLine("The current time is:");
			Console.WriteLine($"{time.Hour:D2}:{time.Minute:D2}:{time.Second:D2}.{time.Milliseconds:D3}");
			return 0;
		}
	}
}
