using System;
using Alloy.Kernel.Ke;

namespace Alloy.Kernel.Platform
{
	// The time synchronization integration component {9527E630-D0AE-497B-ADCE-E80AB0175CAF}: the host's UTC at boot and after a pause, and samples every few seconds, which set the clock
	internal sealed class TimeSyncComponent : IntegrationComponent
	{
		// ictimesync_ref_data (version 4): the host's time, the guest's reference counter when it was taken, flags, leap flags, stratum
		private const int ReferenceBodySize = 24;
		private const int ReferenceFlags = 16;

		// ictimesync_data (earlier versions): the host's time, the child's time, the round trip, flags
		private const int LegacyBodySize = 25;
		private const int LegacyFlags = 24;

		private const byte FlagSync = 1;
		private const byte FlagSample = 2;

		private ulong _syncs;

		protected override ReadOnlySpan<uint> MessageVersions => new uint[] { Version(4, 0), Version(3, 0), Version(1, 0) };

		public static bool Start(in VmBusOffer offer)
		{
			TimeSyncComponent component = new TimeSyncComponent();
			return component.OpenChannel(in offer);
		}

		// A probe asks nothing; a sync or a sample gives the host's time, in 100 ns units since 1601, to set the clock from
		protected override unsafe uint OnMessage(ushort type, byte* body, int length)
		{
			if (type != TypeTimeSync)
				return StatusFail;

			bool reference = NegotiatedVersion >= Version(4, 0);
			if (length < (reference ? ReferenceBodySize : LegacyBodySize))
				return StatusFail;

			ulong hostTime = *(ulong*)body;
			ulong taken = reference ? *(ulong*)(body + 8) : HyperV.ReferenceTime();
			byte flags = body[reference ? ReferenceFlags : LegacyFlags];
			if ((flags & (FlagSync | FlagSample)) != 0)
			{
				Clock.SetHostTime(hostTime, taken);
				_syncs++;
			}
			return StatusOk;
		}
	}
}
