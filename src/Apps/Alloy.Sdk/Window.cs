using System;
using Alloy.Abi;
using Alloy.Graphics;

namespace Alloy.Sdk
{
	// A window on the user-mode window server: a shared section holding a WindowHeader and the surface the app draws into
	public sealed class Window
	{
		private static long s_server;
		private static long s_reply;
		private static ulong s_client;

		public readonly ulong Id;
		public Surface Surface;
		private unsafe WindowHeader* _header;

		// The screen above the taskbar, from the server's Connected reply (MetalOS GetScreenRect)
		public static int ScreenWidth { get; private set; }

		public static int ScreenHeight { get; private set; }

		// Waitable alongside other handles: signaled while events are queued for this app
		public static long EventPort => s_reply;

		internal static long ServerPort => s_server;

		internal static ulong ClientId => s_client;

		private unsafe Window(ulong id, WindowHeader* header)
		{
			Id = id;
			_header = header;
			Surface = new Surface((uint*)((byte*)header + WindowHeader.PixelOffset), header->Width, header->Height, header->Width);
		}

		public static unsafe Window Create(string title, int x, int y, int width, int height, ulong style = WmProtocol.StyleFramed, PixelGlyph icon = null)
		{
			if (!Connect())
				return null;
			long section = NewSection(width, height, out WindowHeader* header);
			if (section < 0)
				return null;
			header->X = x;
			header->Y = y;
			header->Style = style;
			for (int i = 0; i < title.Length && i < WmProtocol.MaxTitle - 1; i++)
				header->Title[i] = (byte)title[i];
			PixelGlyph glyph = icon ?? Icons.Window;
			for (int row = 0; row < 8; row++)
				header->IconRows[row] = glyph.Row(row);
			header->IconColor1 = glyph.Color1;
			header->IconColor2 = glyph.Color2;
			header->IconColor3 = glyph.Color3;

			IpcMessage message = default;
			message.Type = WmProtocol.CreateWindow;
			message.Data[6] = s_client;
			Port.Send(s_server, message, section);
			Kernel.Close(section);

			while (Port.Receive(s_reply, out IpcMessage reply) >= 0)
			{
				if (reply.Type == WmProtocol.WindowCreated)
					return new Window(reply.Data[0], header);
			}
			return null;
		}

		// Asks the server to composite again; the surface is shared, so nothing is copied
		public void Present() => Send(WmProtocol.Present);

		public void Destroy() => Send(WmProtocol.DestroyWindow);

		// Answers the server's Resize event with a surface of that size; the old one stays mapped until the process exits
		public unsafe bool Resize(int width, int height)
		{
			if (width <= 0 || height <= 0)
				return false;
			long section = NewSection(width, height, out WindowHeader* header);
			if (section < 0)
				return false;
			*header = *_header;
			header->Width = width;
			header->Height = height;
			_header = header;
			Surface = new Surface((uint*)((byte*)header + WindowHeader.PixelOffset), width, height, width);

			IpcMessage message = default;
			message.Type = WmProtocol.ResizeWindow;
			message.Data[0] = Id;
			message.Data[6] = s_client;
			Port.Send(s_server, message, section);
			Kernel.Close(section);
			return true;
		}

		// Input and other events for this app's windows, without blocking; Data[0] names the window
		public static bool TryGetEvent(out IpcMessage message) => Port.Receive(s_reply, out message, 0) >= 0;

		public static bool WaitEvent(out IpcMessage message, ulong timeoutMs = ulong.MaxValue) => Port.Receive(s_reply, out message, timeoutMs) >= 0;

		private void Send(ulong type)
		{
			IpcMessage message = default;
			message.Type = type;
			message.Data[0] = Id;
			message.Data[6] = s_client;
			Port.Send(s_server, message);
		}

		private static unsafe long NewSection(int width, int height, out WindowHeader* header)
		{
			long section = SharedMemory.Create(WindowHeader.PixelOffset + (ulong)width * (ulong)height * 4);
			long address = section > 0 ? SharedMemory.MapAddress(section) : -1;
			if (address <= 0)
			{
				header = null;
				return -1;
			}
			header = (WindowHeader*)address;
			header->Width = width;
			header->Height = height;
			return section;
		}

		// Once per process, before the first window; it sends this process's id, so the server can notice when it exits and drop its windows
		public static bool Connect()
		{
			if (s_server > 0)
				return true;
			s_server = Endpoint.WaitFor(WmProtocol.Endpoint, 10000);
			if (s_server <= 0)
				return false;
			s_reply = Port.Create();
			IpcMessage message = default;
			message.Type = WmProtocol.Connect;
			message.Data[0] = (ulong)Kernel.ProcessId;
			Port.Send(s_server, message, s_reply);
			Port.Receive(s_reply, out IpcMessage reply);
			s_client = reply.Data[0];
			ScreenWidth = (int)reply.Data[1];
			ScreenHeight = (int)reply.Data[2];
			return reply.Type == WmProtocol.Connected;
		}
	}
}
