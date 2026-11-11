using System;
using System.Runtime.InteropServices;
using System.Threading;
using Alloy.Abi;
using Alloy.Kernel.Ke;
using Alloy.Kernel.Mm;
using Alloy.Kernel.Ob;

namespace Alloy.Kernel.Io
{
	// The kernel -> window server input ring (MetalOS KeInputPost), published as the "input" section and "input.ready" event
	internal static class Input
	{
		// Mouse moves stop going in at half full, keeping the rest for keys and clicks, as in MetalOS
		private const uint MoveLimit = InputRing.Capacity / 2;

		private static ulong s_physical;
		private static KEvent s_ready;
		private static ushort s_buttons;

		public static ulong Posted { get; private set; }

		public static void Initialize()
		{
			KSection section = KSection.Create(InputRing.Size);
			s_physical = section.Physical;
			s_ready = new KEvent(false);
			Endpoints.Register(InputRing.Endpoint, section);
			Endpoints.Register(InputRing.ReadyEndpoint, s_ready);
		}

		// Drivers call this from their interrupt handlers, so interrupts are already off and there is one producer
		public static void Post(in InputEvent input)
		{
			if (s_physical == 0)
				return;
			Span<byte> memory = PhysicalMemory.Window(s_physical, InputRing.Size);
			ref InputRing ring = ref MemoryMarshal.Cast<byte, InputRing>(memory.Slice(0, InputRing.HeaderSize))[0];
			uint write = ring.Write;
			uint count = write - ring.Read;
			if (count >= InputRing.Capacity)
				return;
			bool move = input.Type == InputEventType.Mouse && input.Buttons == s_buttons;
			if (move && count >= MoveLimit)
				return;
			if (input.Type == InputEventType.Mouse)
				s_buttons = input.Buttons;

			Span<InputEvent> events = MemoryMarshal.Cast<byte, InputEvent>(memory.Slice(InputRing.HeaderSize));
			events[(int)(write % InputRing.Capacity)] = input;
			Interlocked.MemoryBarrier();
			ring.Write = write + 1;
			Posted++;
			s_ready.Signal();
		}
	}
}
