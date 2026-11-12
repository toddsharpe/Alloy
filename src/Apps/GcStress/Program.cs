using System;
using System.Collections.Generic;
using System.Threading;
using Alloy.Sdk;

namespace Alloy.Apps.GcStress
{
	// Hammers Interlocked on heap fields from several threads while allocating hard, to shake out the worker-thread fault seen in the browser
	internal static class Program
	{
		private sealed class Counter
		{
			public int Value;
			public long Total;
			public Counter Next;
		}

		private static Counter s_shared = new Counter();
		private static int s_running;
		private static int s_failed;
		private static volatile bool s_stop;

		private static int Main()
		{
			int threads = Kernel.ProcessArgument > 0 ? (int)Kernel.ProcessArgument : 4;
			Console.WriteLine($"gcstress: {threads} threads for 30 s");
			for (int i = 0; i < threads; i++)
			{
				Thread worker = new Thread(Work, 1024 * 1024);
				worker.IsBackground = true;
				worker.Start(i);
			}
			ulong started = Kernel.TickCount;
			long lastTotal = 0;
			int second = 0;
			while (Kernel.TickCount - started < 30000)
			{
				Kernel.Sleep(1000);
				second++;
				long total = Interlocked.Read(ref s_shared.Total);
				Console.WriteLine($"  {second,2} s: {total - lastTotal} ops, {GC.CollectionCount(0)} gen0, {GC.CollectionCount(1)} gen1, {GC.CollectionCount(2)} gen2, {s_running} running");
				lastTotal = total;
				if (s_failed > 0)
					break;
			}
			s_stop = true;
			Kernel.Sleep(200);
			Console.WriteLine(s_failed > 0 ? "gcstress: FAILED" : $"gcstress: ok, {Interlocked.Read(ref s_shared.Total)} ops, value {s_shared.Value}");
			return s_failed > 0 ? 1 : 0;
		}

		private static void Work(object state)
		{
			int id = (int)state;
			Interlocked.Increment(ref s_running);
			try
			{
				Random random = new Random(id * 7919 + 1);
				List<byte[]> kept = new List<byte[]>();
				Counter mine = new Counter();
				long ops = 0;
				while (!s_stop)
				{
					// Interlocked on a shared object, on a thread-private one, and on an object that is replaced so it moves between generations
					Interlocked.Increment(ref s_shared.Value);
					Interlocked.Decrement(ref s_shared.Value);
					Interlocked.Add(ref mine.Value, 3);
					Interlocked.Exchange(ref mine.Next, new Counter { Value = id });
					Interlocked.CompareExchange(ref mine.Next.Value, 1, id);
					byte[] garbage = new byte[random.Next(16, 4096)];
					garbage[0] = (byte)id;
					if ((ops & 63) == 0)
					{
						kept.Add(garbage);
						if (kept.Count > 256)
							kept.RemoveAt(0);
					}
					if ((ops & 1023) == 0)
						Interlocked.Add(ref s_shared.Total, 1024);
					ops++;
				}
			}
			catch (Exception x)
			{
				Interlocked.Increment(ref s_failed);
				Console.WriteLine($"  worker {id} failed: {x.GetType().Name}: {x.Message}");
			}
			finally
			{
				Interlocked.Decrement(ref s_running);
			}
		}
	}
}
