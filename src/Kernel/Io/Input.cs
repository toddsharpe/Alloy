using System.Threading;
using Alloy.Abi;
using Alloy.Kernel.Ke;
using Alloy.Kernel.Ob;

namespace Alloy.Kernel.Io
{
	// The kernel -> window server input ring (MetalOS KeInputPost), published as the "input" section and "input.ready" event
	internal static unsafe class Input
	{
		// Mouse moves stop going in at half full, keeping the rest for keys and clicks, as in MetalOS
		private const uint MoveLimit = InputRing.Capacity / 2;

		private static InputRing* s_ring;
		private static KEvent s_ready;
		private static ushort s_buttons;

		public static ulong Posted { get; private set; }

		public static void Initialize()
		{
			KSection section = KSection.Create(InputRing.Size);
			s_ring = (InputRing*)BootInfo.ToVirtual(section.Physical);
			s_ready = new KEvent(false);
			Endpoints.Register(InputRing.Endpoint, section);
			Endpoints.Register(InputRing.ReadyEndpoint, s_ready);
		}

		// Drivers call this from their interrupt handlers, so interrupts are already off and there is one producer
		public static void Post(in InputEvent input)
		{
			if (s_ring == null)
				return;
			uint write = s_ring->Write;
			uint count = write - s_ring->Read;
			if (count >= InputRing.Capacity)
				return;
			bool move = input.Type == InputEventType.Mouse && input.Buttons == s_buttons;
			if (move && count >= MoveLimit)
				return;
			if (input.Type == InputEventType.Mouse)
				s_buttons = input.Buttons;

			*InputRing.At(s_ring, write) = input;
			Interlocked.MemoryBarrier();
			s_ring->Write = write + 1;
			Posted++;
			s_ready.Signal();
		}
	}
}
