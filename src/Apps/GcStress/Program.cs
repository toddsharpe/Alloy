using System;
using System.Collections.Generic;
using System.Runtime.InteropServices;
using System.Runtime.Intrinsics;
using System.Runtime.Intrinsics.X86;
using System.Threading;
using Alloy.Sdk;

namespace Alloy.Apps.GcStress
{
	// Hammers the runtime the way the browser does: Interlocked on heap fields, big and small allocations, threads blocked in syscalls, workers that start and exit while collections run, and live AVX registers across preemption
	internal static class Program
	{
		private sealed class Counter
		{
			public int Value;
			public long Total;
			public Counter Next;
		}

		private static readonly Counter s_shared = new Counter();
		private static int s_running;
		private static int s_failed;
		private static int s_spawned;
		private static volatile bool s_stop;

		private static int Main(string[] args)
		{
			// The argument is seconds, plus 1000 times a mask of workers to run: 1 interlocked, 2 large objects, 4 syscalls, 8 spawner, 16 vectors, 32 the Win32 layer's suspension self-test; 0 runs them all
			ulong argument = args.Length > 0 && ulong.TryParse(args[0], out ulong parsed) ? parsed : 0;
			int seconds = argument % 1000 > 0 ? (int)(argument % 1000) : 60;
			int mask = argument >= 1000 ? (int)(argument / 1000) : 31;
			Console.WriteLine($"gcstress: {seconds} s of interlocked, large object, syscall, short-lived thread and vector workers (mask {mask})");
			Console.WriteLine($"  Vector256 accelerated: {Vector256.IsHardwareAccelerated}, AVX2: {Avx2.IsSupported}, Vector512 accelerated: {Vector512.IsHardwareAccelerated}");
			if ((mask & 1) != 0)
			{
				StartWorker(Interlocked_, 0);
				StartWorker(Interlocked_, 1);
			}
			if ((mask & 2) != 0)
				StartWorker(LargeObjects, 2);
			if ((mask & 4) != 0)
			{
				StartWorker(Syscalls, 3);
				StartWorker(Syscalls, 4);
			}
			if ((mask & 8) != 0)
				StartWorker(Spawner, 5);
			if ((mask & 16) != 0)
			{
				StartWorker(Vectors, 6);
				StartWorker(Vectors, 7);
			}
			if ((mask & 32) != 0)
				StartWorker(Contexts, 8);
			ulong started = Kernel.TickCount;
			long lastTotal = 0;
			int second = 0;
			while (Kernel.TickCount - started < (ulong)seconds * 1000)
			{
				Kernel.Sleep(1000);
				second++;
				long total = Interlocked.Read(ref s_shared.Total);
				if (second % 5 == 0 || s_failed > 0)
					Console.WriteLine($"  {second,3} s: {(total - lastTotal) / 5} ops/s, {GC.CollectionCount(0)} gen0, {GC.CollectionCount(1)} gen1, {GC.CollectionCount(2)} gen2, {s_running} running, {s_spawned} spawned");
				if (second % 5 == 0)
					lastTotal = total;
				if (s_failed > 0)
					break;
			}
			s_stop = true;
			Kernel.Sleep(300);
			Console.WriteLine(s_failed > 0 ? "gcstress: FAILED" : $"gcstress: ok, {Interlocked.Read(ref s_shared.Total)} ops, value {s_shared.Value}, {s_spawned} threads spawned");
			return s_failed > 0 ? 1 : 0;
		}

		private static void StartWorker(ParameterizedThreadStart work, int id)
		{
			Thread worker = new Thread(work, 1024 * 1024);
			worker.IsBackground = true;
			worker.Start(id);
		}

		private static void Guarded(object state, Action<int> body)
		{
			int id = (int)state;
			Interlocked.Increment(ref s_running);
			try
			{
				body(id);
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

		// Interlocked on a shared object, a private one and one that is replaced so it moves between generations, with steady garbage
		private static void Interlocked_(object state) => Guarded(state, id =>
		{
			Random random = new Random(id * 7919 + 1);
			List<byte[]> kept = new List<byte[]>();
			Counter mine = new Counter();
			long ops = 0;
			while (!s_stop)
			{
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
		});

		[DllImport("*", EntryPoint = "PhxContextSelfTest", ExactSpelling = true)]
		private static extern int PhxContextSelfTest(int milliseconds);

		// The Win32 layer suspends a spinning thread, reads and redirects its context and checks every register, as the GC does to stop threads; details go to the serial log
		private static void Contexts(object state) => Guarded(state, id =>
		{
			while (!s_stop)
			{
				int failures = PhxContextSelfTest(2000);
				if (failures != 0)
				{
					Interlocked.Increment(ref s_failed);
					Console.WriteLine($"  worker {id}: context self-test reported {failures} failures");
					break;
				}
				Interlocked.Add(ref s_shared.Total, 1);
			}
		});

		// A running sum kept in a 256-bit register, checked against the iteration count: the upper halves survive only if the kernel saves them across interrupts
		private static void Vectors(object state) => Guarded(state, id =>
		{
			Vector256<long> step = Vector256.Create(1L, 2L, 3L, 4L);
			Vector256<long> sum = Vector256<long>.Zero;
			long rounds = 0;
			while (!s_stop)
			{
				sum += step;
				rounds++;
				if ((rounds & 0xFFFF) == 0)
				{
					Vector256<long> expected = Vector256.Create(rounds, 2 * rounds, 3 * rounds, 4 * rounds);
					if (sum != expected)
					{
						Interlocked.Increment(ref s_failed);
						Console.WriteLine($"  worker {id}: vector register corrupted after {rounds} rounds: {sum} instead of {expected}");
						break;
					}
					Interlocked.Add(ref s_shared.Total, 0x10000);
				}
			}
		});

		// Arrays on the large object heap, as decoded images are, some kept, most dropped
		private static void LargeObjects(object state) => Guarded(state, id =>
		{
			Random random = new Random(id * 104729 + 3);
			uint[][] kept = new uint[8][];
			long ops = 0;
			while (!s_stop)
			{
				uint[] pixels = new uint[random.Next(30000, 400000)];
				pixels[pixels.Length - 1] = (uint)ops;
				kept[ops % kept.Length] = pixels;
				Interlocked.Increment(ref s_shared.Total);
				Interlocked.Increment(ref s_shared.Value);
				Interlocked.Decrement(ref s_shared.Value);
				ops++;
			}
		});

		// Blocked in the kernel most of the time, as the network and UI threads are, touching the shared counters between waits
		private static void Syscalls(object state) => Guarded(state, id =>
		{
			long handle = Event.Create();
			long ops = 0;
			while (!s_stop)
			{
				if ((ops & 1) == 0)
					Kernel.Sleep(1);
				else
					Event.WaitAny(new[] { handle }, 1);
				Interlocked.Increment(ref s_shared.Value);
				Interlocked.Decrement(ref s_shared.Value);
				Interlocked.Increment(ref s_shared.Total);
				string text = ops.ToString() + " " + id;
				if (text.Length == 0)
					break;
				ops++;
			}
		});

		// Starts a worker that allocates, counts and exits, over and over, so threads leave while collections happen
		private static void Spawner(object state) => Guarded(state, id =>
		{
			while (!s_stop)
			{
				Thread child = new Thread(() =>
				{
					try
					{
						Counter local = new Counter();
						for (int i = 0; i < 2000; i++)
						{
							Interlocked.Increment(ref s_shared.Value);
							Interlocked.Add(ref local.Value, i);
							byte[] garbage = new byte[i % 512 + 16];
							garbage[0] = 1;
							Interlocked.Decrement(ref s_shared.Value);
						}
						Interlocked.Add(ref s_shared.Total, 2000);
					}
					catch (Exception x)
					{
						Interlocked.Increment(ref s_failed);
						Console.WriteLine($"  spawned thread failed: {x.GetType().Name}: {x.Message}");
					}
				}, 256 * 1024);
				child.IsBackground = true;
				child.Start();
				Interlocked.Increment(ref s_spawned);
				child.Join();
			}
		});
	}
}
