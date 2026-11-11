using System;
using System.Buffers.Binary;

namespace Alloy.Kernel.Platform
{
	// The heartbeat integration component {57164F39-9115-4E78-AB55-382F3BD5422D}: the host sends a sequence number every few seconds and reads the guest as responding while it comes back one higher
	internal sealed class HeartbeatComponent : IntegrationComponent
	{
		// heartbeat_msg_data: the sequence number, then reserved words the host may leave out
		private const int BodySize = 8;

		private ulong _beats;

		protected override ReadOnlySpan<uint> MessageVersions => new uint[] { Version(3, 0), Version(1, 0) };

		// Heartbeats answered since boot
		public static ulong Beats => Active == null ? 0 : Active._beats;

		public static HeartbeatComponent Active { get; private set; }

		public static bool Start(in VmBusOffer offer)
		{
			HeartbeatComponent component = new HeartbeatComponent();
			if (!component.OpenChannel(in offer))
				return false;
			Active = component;
			return true;
		}

		protected override uint OnMessage(ushort type, Span<byte> body)
		{
			if (type != TypeHeartbeat || body.Length < BodySize)
				return StatusFail;

			BinaryPrimitives.WriteUInt64LittleEndian(body, BinaryPrimitives.ReadUInt64LittleEndian(body) + 1);
			if (_beats++ == 0)
				Log.WriteLine("Heartbeat: the host is listening");
			return StatusOk;
		}
	}
}
