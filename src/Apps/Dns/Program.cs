using System;
using Alloy.Sdk;

namespace Alloy.Apps.Dns
{
	// A lookup from the terminal: dns <name> asks the configured server for the name's A records and prints every answer
	internal static class Program
	{
		private static int Main(string[] args)
		{
			if (args.Length < 1)
			{
				Console.WriteLine("usage: dns <name>");
				return 1;
			}
			Console.WriteLine($"dns: server {Net.FormatIp(Sdk.Dns.Server)}");
			DnsAnswer[] answers = new DnsAnswer[8];
			ulong start = Kernel.TickCount;
			int count = Sdk.Dns.Query(args[0], answers);
			if (count == 0)
			{
				Console.WriteLine($"dns: no answer for {args[0]}");
				return 1;
			}
			for (int i = 0; i < count; i++)
			{
				if (answers[i].Type == DnsAnswer.TypeA)
					Console.WriteLine($"  {args[0]} A {Net.FormatIp(answers[i].Address)} ttl {answers[i].Ttl}");
				else
					Console.WriteLine($"  {args[0]} CNAME {answers[i].Target} ttl {answers[i].Ttl}");
			}
			Console.WriteLine($"dns: {count} answers in {Kernel.TickCount - start} ms");
			return 0;
		}
	}
}
