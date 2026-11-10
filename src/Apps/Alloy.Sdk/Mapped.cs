using System;
using System.Buffers;
using System.Runtime.InteropServices;
using Alloy.Abi;
using Alloy.Graphics;

namespace Alloy.Sdk
{
	// Memory the kernel mapped into this process, as Memory<T>: it never moves and is never unmapped, so it is pinned by nature
	internal sealed class MappedMemory<T> : MemoryManager<T> where T : unmanaged
	{
		private readonly unsafe T* _address;
		private readonly int _length;

		public unsafe MappedMemory(void* address, int length)
		{
			_address = (T*)address;
			_length = length;
		}

		public override unsafe Span<T> GetSpan() => new Span<T>(_address, _length);

		public override unsafe MemoryHandle Pin(int elementIndex = 0) => new MemoryHandle(_address + elementIndex);

		public override void Unpin()
		{
		}

		protected override void Dispose(bool disposing)
		{
		}
	}

	// The kernel's input ring (Abi InputRing), drained by the window server; one consumer
	public sealed class InputQueue
	{
		private readonly unsafe InputRing* _ring;

		private unsafe InputQueue(InputRing* ring, long ready)
		{
			_ring = ring;
			ReadyEvent = ready;
		}

		// Signaled while events are waiting, for a WaitAny alongside other handles
		public long ReadyEvent { get; }

		// The kernel publishes the ring before init starts; without VMBus input there is none
		public static unsafe InputQueue Open()
		{
			long input = Endpoint.Connect(InputRing.Endpoint);
			long ready = Endpoint.Connect(InputRing.ReadyEndpoint);
			long address = input > 0 && ready > 0 ? SharedMemory.MapAddress(input) : 0;
			return address > 0 ? new InputQueue((InputRing*)address, ready) : null;
		}

		public unsafe bool TryRead(out InputEvent input)
		{
			if (_ring->Read == _ring->Write)
			{
				input = default;
				return false;
			}
			input = *InputRing.At(_ring, _ring->Read);
			_ring->Read = _ring->Read + 1;
			return true;
		}
	}

	// The kernel's received-frame ring (Abi NetFrame), drained by the network stack; one consumer
	public sealed class NetFrameRing
	{
		private readonly unsafe byte* _ring;

		private unsafe NetFrameRing(byte* ring, long ready)
		{
			_ring = ring;
			ReadyEvent = ready;
		}

		public long ReadyEvent { get; }

		public static unsafe NetFrameRing Open()
		{
			long ring = Endpoint.Connect(NetFrame.RxEndpoint);
			long ready = Endpoint.Connect(NetFrame.RxReadyEndpoint);
			long address = ring > 0 && ready > 0 ? SharedMemory.MapAddress(ring) : 0;
			return address > 0 ? new NetFrameRing((byte*)address, ready) : null;
		}

		// The oldest frame still in the ring, or empty; it stays valid until Advance
		public unsafe ReadOnlySpan<byte> Peek()
		{
			uint* counters = NetFrame.Counters(_ring);
			if (counters[1] == counters[0])
				return ReadOnlySpan<byte>.Empty;
			NetFrame* frame = NetFrame.At(_ring, counters[1]);
			int length = Math.Min((int)frame->Length, NetFrame.Mtu);
			return new ReadOnlySpan<byte>(&frame->Data, length);
		}

		public unsafe void Advance()
		{
			uint* counters = NetFrame.Counters(_ring);
			counters[1] = counters[1] + 1;
		}
	}

	// Surfaces over memory other than a window's: the screen, back buffers, and windows as the server maps them
	public static class Surfaces
	{
		// Zeroed memory of this process's own, never freed; for back buffers that live as long as the program
		public static unsafe Surface Allocate(int width, int height) =>
			new Surface((uint*)NativeMemory.AllocZeroed((nuint)width * (nuint)height * 4), width, height, width);

		// The screen at the kernel's largest mode, once per system (the window server's); false if another process has it
		public static unsafe bool TryMapFramebuffer(out Surface screen, out FramebufferInfo info)
		{
			FramebufferInfo result;
			long framebuffer = SystemCall.Invoke(Syscall.MapFramebuffer, (ulong)&result);
			info = result;
			if (framebuffer <= 0)
			{
				screen = default;
				return false;
			}
			screen = new Surface((uint*)framebuffer, (int)info.Width, (int)info.Height, (int)info.Stride);
			return true;
		}

		// A window's shared section as the server sees it: the header copied out, the pixels after it as a surface
		public static unsafe bool TryMapWindow(long section, out WindowHeader header, out Surface surface)
		{
			long address = section > 0 ? SharedMemory.MapAddress(section) : 0;
			if (address <= 0)
			{
				header = default;
				surface = default;
				return false;
			}
			header = *(WindowHeader*)address;
			if (header.Width <= 0 || header.Height <= 0)
			{
				surface = default;
				return false;
			}
			surface = new Surface((uint*)(address + WindowHeader.PixelOffset), header.Width, header.Height, header.Width);
			return true;
		}
	}
}
