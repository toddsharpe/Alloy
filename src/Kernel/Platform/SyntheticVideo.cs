using System;
using Alloy.Abi;
using Alloy.Kernel.Ke;
using Alloy.Text;

namespace Alloy.Kernel.Platform
{
	// Hyper-V synthetic video (MetalOS HyperVVideoDriver): the GOP framebuffer as VRAM, set to the largest mode the host lists
	internal sealed unsafe class SyntheticVideo : VmBusChannel
	{
		private const int RingPages = 10;
		private const uint PipeData = 1;
		private const uint VersionRequest = 1;
		private const uint VersionResponse = 2;
		private const uint VramLocation = 3;
		private const uint VramLocationAck = 4;
		private const uint SituationUpdate = 5;
		private const uint SituationUpdateAck = 6;
		private const uint Dirt = 10;
		private const uint ResolutionRequest = 13;
		private const uint ResolutionResponse = 14;
		private const uint VersionWin10 = (5 << 16) | 3;
		private const int MaxResolutions = 64;
		private const int AnswerSize = 512;

		private readonly KEvent _answered = new KEvent(false);
		private byte* _answer;
		private volatile uint _awaiting;
		private ulong _situation;
		private ulong _vramSize;
		private int _modes;

		public static SyntheticVideo Active { get; private set; }

		public ulong Vram { get; private set; }

		public uint Width { get; private set; }

		public uint Height { get; private set; }

		// Negotiates once, the first time the window server asks for the screen; false leaves the boot GOP mode in charge
		public static bool Start(BootInfo* boot)
		{
			if (Active != null)
				return true;
			if (!VmBus.TryFind(VmBus.VideoLow, VmBus.VideoHigh, out VmBusOffer offer))
				return false;
			SyntheticVideo video = new SyntheticVideo();
			video._answer = (byte*)Mm.Heap.Allocate(AnswerSize);
			video.Vram = boot->Framebuffer;
			video.Width = boot->FramebufferWidth;
			video.Height = boot->FramebufferHeight;
			video._vramSize = offer.MmioMegabytes != 0 ? (ulong)offer.MmioMegabytes << 20 : boot->FramebufferSize;
			if (!video.Open(in offer, RingPages, RingPages) || !video.Negotiate(boot->FramebufferPixelsPerScanLine))
			{
				Log.WriteLine("SyntheticVideo: negotiation failed, keeping the boot mode");
				return false;
			}
			Active = video;

			SpanWriter line = new SpanWriter(stackalloc char[96]);
			line.Append("SyntheticVideo: ");
			line.Append(video.Width);
			line.Append('x');
			line.Append(video.Height);
			line.Append(", VRAM ");
			line.Append(video._vramSize >> 20);
			line.Append(" MB at 0x");
			line.AppendHex(video.Vram, 8);
			line.Append(", ");
			line.Append(video._modes);
			line.Append(" modes listed, mmio ");
			line.Append(offer.MmioMegabytes);
			Log.WriteLine(ref line);
			return true;
		}

		// Tells the host the whole screen changed; once synthvid is connected the host only redraws what it is told
		public void Flush()
		{
			byte* message = stackalloc byte[34];
			new Span<byte>(message, 34).Clear();
			Header(message, Dirt, 18);
			message[17] = 1;
			*(int*)(message + 26) = (int)Width;
			*(int*)(message + 30) = (int)Height;
			Send(message, 34, 0, PacketInband, 0);
		}

		// Version (newest first, as hyperv_fb), the resolution list, the VRAM location, then the mode
		private bool Negotiate(uint bootPitch)
		{
			byte* message = stackalloc byte[48];
			uint version = 0;
			for (int i = 0; i < 3; i++)
			{
				uint candidate = i == 0 ? VersionWin10 : i == 1 ? (2u << 16) | 3 : 3u;
				Header(message, VersionRequest, 4);
				*(uint*)(message + 16) = candidate;
				if (!Request(message, 20, VersionResponse))
					return false;
				if (_answer[4] != 0)
				{
					version = candidate;
					break;
				}
			}
			if (version == 0)
				return false;

			// Picked now, since the next answer reuses the buffer; a host that lists nothing gets 1920x1080, as in MetalOS
			uint width = 1920, height = 1080;
			if (version >= VersionWin10)
			{
				Header(message, ResolutionRequest, 1);
				message[16] = MaxResolutions;
				if (Request(message, 17, ResolutionResponse) && _answer[128] != 0)
				{
					_modes = _answer[128];
					Largest(out width, out height);
				}
			}

			// The host only accepts a VRAM address inside its MMIO region, which on Gen2 is the GOP framebuffer
			new Span<byte>(message, 48).Clear();
			Header(message, VramLocation, 17);
			*(ulong*)(message + 16) = Vram;
			message[24] = 1;
			*(ulong*)(message + 25) = Vram;
			if (!Request(message, 33, VramLocationAck))
				return false;

			return SetMode(width, height) || SetMode(Width, Height, bootPitch * 4);
		}

		// The largest listed mode that fits the VRAM; the host's own default is its boot mode, not its best
		private void Largest(out uint width, out uint height)
		{
			width = Width;
			height = Height;
			int count = Math.Min((int)_answer[128], MaxResolutions);
			for (int i = 0; i < count; i++)
			{
				uint w = *(ushort*)(_answer + 131 + 4 * i);
				uint h = *(ushort*)(_answer + 133 + 4 * i);
				if ((ulong)w * h * 4 <= _vramSize && (ulong)w * h > (ulong)width * height)
				{
					width = w;
					height = h;
				}
			}
		}

		private bool SetMode(uint width, uint height, uint pitchBytes = 0)
		{
			if (pitchBytes == 0)
				pitchBytes = width * 4;
			if ((ulong)height * pitchBytes > _vramSize)
				return false;
			byte* message = stackalloc byte[48];
			new Span<byte>(message, 48).Clear();
			Header(message, SituationUpdate, 27);
			*(ulong*)(message + 16) = ++_situation;
			message[24] = 1;
			message[25] = 1;
			message[30] = 32;
			*(uint*)(message + 31) = width;
			*(uint*)(message + 35) = height;
			*(uint*)(message + 39) = pitchBytes;
			if (!Request(message, 43, SituationUpdateAck))
				return false;
			Width = width;
			Height = height;
			return true;
		}

		// Every message is |pipe type, size|synthvid type, size|body|, both sizes counting from the synthvid header
		private static void Header(byte* message, uint type, int bodySize)
		{
			*(uint*)message = PipeData;
			*(uint*)(message + 4) = (uint)(8 + bodySize);
			*(uint*)(message + 8) = type;
			*(uint*)(message + 12) = (uint)(8 + bodySize);
		}

		// Arms before sending, then waits up to 2 s; an answer that arrives after its wait ended can't satisfy the next request
		private bool Request(byte* message, int size, uint answer)
		{
			ulong flags = Scheduler.Lock();
			_answered.Reset();
			_awaiting = answer;
			Scheduler.Unlock(flags);
			bool answered = Send(message, (uint)size, 1, PacketInband, CompletionRequested) && _answered.Wait(2000);
			_awaiting = 0;
			return answered;
		}

		protected override void OnPacket(VmBusPacket* packet, byte* payload, uint size)
		{
			if (packet->Type != PacketInband || size < 16 || *(uint*)payload != PipeData)
				return;
			uint message = *(uint*)(payload + 8);
			if (_awaiting == 0 || message != _awaiting)
				return;
			int length = (int)Math.Min(size - 16, AnswerSize);
			new Span<byte>(_answer, AnswerSize).Clear();
			new ReadOnlySpan<byte>(payload + 16, length).CopyTo(new Span<byte>(_answer, AnswerSize));
			_awaiting = 0;
			_answered.Signal();
		}
	}
}
