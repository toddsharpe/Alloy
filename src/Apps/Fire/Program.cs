using System;
using Alloy.Abi;
using Alloy.Graphics;
using Alloy.Sdk;

namespace Alloy.Apps.Fire
{
	// MetalOS's fire.exe: the PSX Doom fire (fabiensanglard.net/doom_fire_psx) in a quarter of the screen near the bottom-right
	internal static class Program
	{
		private const int PixelSize = 2;
		private const ulong FrameMs = 33;

		private static ReadOnlySpan<byte> Palette => new byte[]
		{
			0x07, 0x07, 0x07, 0x1F, 0x07, 0x07, 0x2F, 0x0F, 0x07, 0x47, 0x0F, 0x07, 0x57, 0x17, 0x07, 0x67, 0x1F, 0x07, 0x77, 0x1F, 0x07,
			0x8F, 0x27, 0x07, 0x9F, 0x2F, 0x07, 0xAF, 0x3F, 0x07, 0xBF, 0x47, 0x07, 0xC7, 0x47, 0x07, 0xDF, 0x4F, 0x07, 0xDF, 0x57, 0x07,
			0xDF, 0x57, 0x07, 0xD7, 0x5F, 0x07, 0xD7, 0x5F, 0x07, 0xD7, 0x67, 0x0F, 0xCF, 0x6F, 0x0F, 0xCF, 0x77, 0x0F, 0xCF, 0x7F, 0x0F,
			0xCF, 0x87, 0x17, 0xC7, 0x87, 0x17, 0xC7, 0x8F, 0x17, 0xC7, 0x97, 0x1F, 0xBF, 0x9F, 0x1F, 0xBF, 0x9F, 0x1F, 0xBF, 0xA7, 0x27,
			0xBF, 0xA7, 0x27, 0xBF, 0xAF, 0x2F, 0xB7, 0xAF, 0x2F, 0xB7, 0xB7, 0x2F, 0xB7, 0xB7, 0x37, 0xCF, 0xCF, 0x6F, 0xDF, 0xDF, 0x9F,
			0xEF, 0xEF, 0xC7, 0xFF, 0xFF, 0xFF,
		};

		private static uint s_seed = 1;

		private static int Main()
		{
			// A quarter of the work area, framed, near the bottom-right corner (MetalOS Fire/Main.cpp)
			if (!Window.Connect())
				return 1;
			int outerWidth = Window.ScreenWidth / 2;
			int outerHeight = Window.ScreenHeight / 2;
			Window window = Window.Create("Fire", Window.ScreenWidth - outerWidth - 24 + Theme.FrameEdge, Window.ScreenHeight - outerHeight - 24 + Theme.FrameTop,
				outerWidth - 2 * Theme.FrameEdge, outerHeight - Theme.FrameTop - Theme.FrameEdge, WmProtocol.StyleFramed, Icons.Flame);
			if (window == null)
				return 1;

			int width = window.Surface.Width / PixelSize;
			int height = window.Surface.Height / PixelSize;
			int colors = Palette.Length / 3;
			byte[] indexes = new byte[width * height];
			for (int x = 0; x < width; x++)
				indexes[(height - 1) * width + x] = (byte)(colors - 1);

			while (true)
			{
				while (Window.TryGetEvent(out IpcMessage message))
				{
					if (message.Type == WmProtocol.Close)
						return 0;
				}

				for (int x = 0; x < width; x++)
				{
					for (int y = 1; y < height; y++)
						Spread(indexes, width, x, y);
				}

				Surface surface = window.Surface;
				for (int y = 0; y < height; y++)
				{
					for (int x = 0; x < width; x++)
					{
						int index = indexes[y * width + x] * 3;
						uint color = ((uint)Palette[index] << 16) | ((uint)Palette[index + 1] << 8) | Palette[index + 2];
						surface.FillRectangle(x * PixelSize, y * PixelSize, PixelSize, PixelSize, color);
					}
				}
				window.Present();
				Kernel.Sleep(FrameMs);
			}
		}

		private static void Spread(byte[] indexes, int width, int x, int y)
		{
			int index = indexes[y * width + x];
			if (index == 0)
			{
				indexes[(y - 1) * width + x] = 0;
				return;
			}
			int random = Random() & 3;
			int target = x;
			if (random == 0 && target > 0)
				target--;
			else if (random == 3 && target < width - 1)
				target++;
			indexes[(y - 1) * width + target] = (byte)(index - (random & 1));
		}

		private static int Random()
		{
			s_seed = s_seed * 1103515245 + 12345;
			return (int)(s_seed >> 16) & 0x7FFF;
		}
	}
}
