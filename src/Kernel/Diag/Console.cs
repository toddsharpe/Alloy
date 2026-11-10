using System;
using Alloy.Abi;
using Alloy.Graphics;

namespace Alloy.Kernel.Diag
{
	// The boot console, laid out like MetalOS's LoadingScreen: black, a blue border and title bar, white text
	internal static unsafe class Console
	{
		private const uint Foreground = 0x00FFFFFF;
		private const uint Background = 0x00000000;
		private const uint Border = 0x000000FF;
		private const uint Alert = 0x00FF4040;
		private const int Left = 5;
		private const int Top = 25;

		private static Surface s_screen;
		private static int s_x;
		private static int s_y;
		private static bool s_ready;

		public static void Initialize(BootInfo* info)
		{
			uint* pixels = (uint*)(MemoryLayout.KernelFramebuffer + (info->Framebuffer & (MemoryLayout.PageSize - 1)));
			s_screen = new Surface(pixels, (int)info->FramebufferWidth, (int)info->FramebufferHeight, (int)info->FramebufferPixelsPerScanLine);
			s_screen.Fill(Background);
			s_screen.DrawBorder(0, 0, s_screen.Width, s_screen.Height, 3, Border);
			int x = 9;
			int y = 9;
			s_screen.DrawText(ref x, ref y, x, "Alloy", Border);
			s_screen.FillRectangle(0, 20, s_screen.Width, 3, Border);
			s_x = Left;
			s_y = Top;
			s_ready = true;
		}

		public static bool Ready => s_ready;

		private static bool s_released;

		// The window server takes the screen once; from then on the console only logs to COM1
		public static bool Release()
		{
			ulong flags = Ke.Scheduler.Lock();
			bool first = !s_released;
			s_released = true;
			s_ready = false;
			Ke.Scheduler.Unlock(flags);
			return first;
		}

		public static Surface Screen => s_screen;

		// Right-aligned text in the title bar
		public static void Status(ReadOnlySpan<char> text)
		{
			if (!s_ready)
				return;
			ulong flags = Ke.Scheduler.Lock();
			int width = 40 * Surface.CharWidth;
			int x = s_screen.Width - width - 6;
			int y = 9;
			s_screen.FillRectangle(x, 3, width, 17, Background);
			s_screen.DrawText(ref x, ref y, x, text, Foreground);
			Ke.Scheduler.Unlock(flags);
		}

		public static void WriteLine(ReadOnlySpan<char> text, bool alert = false)
		{
			if (!s_ready)
				return;
			if (s_y + Surface.LineHeight > s_screen.Height - 3)
			{
				s_screen.ScrollUp(Top, Surface.LineHeight, Background);
				s_screen.DrawBorder(0, 0, s_screen.Width, s_screen.Height, 3, Border);
				s_y -= Surface.LineHeight;
			}
			s_x = Left;
			s_screen.DrawText(ref s_x, ref s_y, Left, text, alert ? Alert : Foreground);
			s_x = Left;
			s_y += Surface.LineHeight;
		}
	}
}
