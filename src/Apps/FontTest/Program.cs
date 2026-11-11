using System;
using Alloy.Abi;
using Alloy.Graphics;
using Alloy.Sdk;
using Alloy.Sdk.Text;
using Alloy.Sdk.UI;

namespace Alloy.Apps.FontTest
{
	// A window of sample text in the shipped faces at several sizes, with the time the first draw took
	internal sealed class Sampler : Control
	{
		private const string Sample = "The quick brown fox jumps over the lazy dog 0123456789 @#&%";

		private static readonly (string Face, int Size)[] s_lines =
		{
			(Fonts.Sans, 12), (Fonts.Sans, 14), (Fonts.Sans, 16), (Fonts.Sans, 20), (Fonts.Sans, 28), (Fonts.Sans, 40),
			(Fonts.SansBold, 16), (Fonts.SansBold, 24), (Fonts.Serif, 16), (Fonts.Serif, 24), (Fonts.SerifBold, 20), (Fonts.Mono, 14), (Fonts.Mono, 18),
		};

		private long _firstDrawMs = -1;

		public Sampler(Rectangle bounds) : base(bounds)
		{
		}

		public override void Draw(in Surface surface)
		{
			ulong start = Kernel.TickCount;
			int y = Bounds.Y;
			foreach ((string face, int size) in s_lines)
			{
				Font font = Fonts.Get(face, size);
				if (font == null)
				{
					surface.DrawText(Bounds.X, y, $"{face} {size}: font file missing", Theme.ErrorText, 1);
					y += 16;
					continue;
				}
				string label = $"{face} {size}px";
				surface.DrawText(Bounds.X, y + Math.Max(0, (font.LineHeight - 8) / 2), label, Theme.Hint, 1);
				int x = Bounds.X + 160;
				int width = font.Measure(Sample);
				surface.FillRectangle(x, y, width, font.LineHeight, Theme.IronDeep);
				font.DrawTop(in surface, x, y, Sample, Theme.Bone);
				surface.DrawText(x + width + 8, y + Math.Max(0, (font.LineHeight - 8) / 2), $"{width}x{font.LineHeight}", Theme.Muted, 1);
				y += font.LineHeight + 6;
			}
			if (_firstDrawMs < 0)
				_firstDrawMs = (long)(Kernel.TickCount - start);
			surface.DrawText(Bounds.X, Bounds.Y + Bounds.Height - 12, $"first draw {_firstDrawMs} ms, glyphs rasterized on demand and cached", Theme.Hint, 1);
		}
	}

	internal static class Program
	{
		private static int Main()
		{
			Gui gui = Gui.Create("Fonts", 60, 60 + Theme.FrameTop, 1180, 620, WmProtocol.StyleFramed, Icons.Window);
			if (gui == null)
				return 1;
			gui.Add(new Sampler(new Rectangle(16, 16, 1148, 588)));
			gui.Run();
			return 0;
		}
	}
}
