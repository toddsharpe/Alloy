using System;
using System.Buffers.Binary;
using Alloy.Kernel.Ke;

namespace Alloy.Kernel.Platform
{
	// The shutdown integration component {0E0B6031-5213-4934-818B-38D90CED39DB}: Hyper-V Manager's Shut Down and Restart, carried out by a kernel thread once the host has its answer
	internal sealed class ShutdownComponent : IntegrationComponent
	{
		// shutdown_msg_data: the reason code, a timeout, the flags that say what to do, then a message for the user
		private const int BodySize = 12;
		private const int FlagsOffset = 8;

		private static ShutdownComponent s_instance;
		private readonly KEvent _request = new KEvent(false);
		private bool _restart;

		protected override ReadOnlySpan<uint> MessageVersions => new uint[] { Version(3, 0), Version(1, 0) };

		public static unsafe bool Start(in VmBusOffer offer)
		{
			ShutdownComponent component = new ShutdownComponent();
			if (!component.OpenChannel(in offer))
				return false;
			s_instance = component;
			Scheduler.Start("shutdown", &Worker, null);
			return true;
		}

		// 0 and 1 ask for a power off, 2 and 3 for a restart; 4 and 5 (hibernate) and the rest are refused
		protected override uint OnMessage(ushort type, Span<byte> body)
		{
			if (type != TypeShutdown || body.Length < BodySize)
				return StatusFail;

			uint flags = BinaryPrimitives.ReadUInt32LittleEndian(body.Slice(FlagsOffset));
			if (flags > 3)
				return StatusFail;

			_restart = flags >= 2;
			Log.WriteLine(_restart ? "Shutdown: the host asked for a restart" : "Shutdown: the host asked for a power off");
			_request.Signal();
			return StatusOk;
		}

		private static unsafe void Worker(void* argument)
		{
			s_instance._request.Wait();
			Power.Shutdown(s_instance._restart);
		}
	}
}
