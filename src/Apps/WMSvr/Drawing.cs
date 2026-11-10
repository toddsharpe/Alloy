using System;
using System.Buffers.Binary;
using Alloy.Abi;
using Alloy.Graphics;
using Alloy.Sdk;

namespace Alloy.Apps.WMSvr
{
	// Everything the window manager draws, laid out as in MetalOS-WMSvr
	internal static partial class Program
	{
		private const int BarButtonTop = 5;
		private const int BarButtonHeight = 30;
		private const int StartX = 6;
		private const int StartWidth = 10 + Theme.Glyph + 10 + 7 * Theme.Glyph + 14;
		private const int TaskX = StartX + StartWidth + 12;
		private const int TaskWidth = 232;
		private const int TaskGap = 6;
		private const int TaskChars = (TaskWidth - 10 - Theme.Glyph - 10 - 10) / Theme.Glyph;
		private const int ClockWidth = 5 * Theme.Glyph;
		private const string Offline = "OFFLINE";

		private const int MenuWidth = 380;
		private const int MenuHeader = 72;
		private const int MenuPad = 6;
		private const int MenuItemHeight = 56;
		private const int MenuFooter = 36;

		private const int TileWidth = 240;
		private const int TileGap = 24;
		private const int ThumbHeight = 128;
		private const int TileHeight = 2 + 8 + ThumbHeight + 12 + Theme.Glyph + 8 + 2;
		private const int TileChars = (TileWidth - 4 - 16 - Theme.Glyph - 10) / Theme.Glyph;

		private static Theme.Face StartFace => new Theme.Face(0x2C2E33, 0x202125, 0x4A4E56, 0x121316, Theme.Bone);
		private static Theme.Face StartHover => new Theme.Face(0x35373D, 0x27292D, 0x50545C, 0x121316, Theme.Bone);
		private static Theme.Face StartOpen => new Theme.Face(0x18191C, 0x1F2024, 0x070708, 0x2F3238, Theme.Molten);
		private static Theme.Face TaskActive => new Theme.Face(0x35373D, 0x27292D, 0x4A4E56, 0x27292D, Theme.Bone);
		private static Theme.Face TaskHover => new Theme.Face(0x212226, 0x212226, 0x2F3238, 0x212226, Theme.Bone);
		private static Theme.Face TaskBehind => new Theme.Face(0x16171A, 0x16171A, 0x070708, 0x26282C, 0x9B978F);
		private static Theme.Face TaskMinimized => new Theme.Face(0x111214, 0x111214, 0x070708, 0x1D1E21, 0x5F5B56);

		private static readonly AppEntry[] s_apps =
		{
			new AppEntry("Terminal", "Command shell", "term.exe", Icons.Terminal),
			new AppEntry("Calc", "Four-function calculator", "calc.exe", Icons.Calc),
			new AppEntry("Fire", "The Doom fire effect", "fire.exe", Icons.Flame),
			new AppEntry("Doom", "Ultimate Doom, full screen", "doom.exe", Icons.Skull),
		};

		private static Memory<byte> s_netAddress;
		private static uint s_address;
		private static string s_addressText = Offline;

		private static void Composite()
		{
			if (FullscreenActive)
			{
				// A fullscreen window covers everything, with black bars where its shape doesn't match the screen
				Rect fit = FullscreenRect(s_focus);
				s_back.Fill(0);
				s_back.BlitScaled(fit.X, fit.Y, fit.Width, fit.Height, in s_focus.Surface);
			}
			else
			{
				s_back.Blit(0, 0, in s_desktop);
				for (int i = s_zcount - 1; i >= 0; i--)
				{
					if (!s_zorder[i].Minimized)
						DrawWindow(s_zorder[i]);
				}
				DrawTaskbar();
				DrawStartMenu();
			}
			DrawSwitcher();
			s_screen.Blit(0, 0, in s_back);
			SystemCall.Invoke(Syscall.FlushFramebuffer);
		}

		// A near-black field with a faint ember glow rising from the bottom edge and the wordmark pressed in, drawn once
		private static void BuildDesktop()
		{
			int width = s_desktop.Width;
			int height = s_desktop.Height;
			s_desktop.Fill(Theme.Void);

			// An ellipse centered below the bottom edge; t is the distance from its center in thousandths of its radius
			long centerX = width / 2;
			long centerY = (long)height * 112 / 100;
			long radiusX = (long)width * 1500 / 1920;
			long radiusY = (long)height * 560 / 1080;
			const uint Inner = 0xFF6018;
			const uint OuterGlow = 0xA0280C;
			for (int y = 0; y < height; y++)
			{
				long dy = (y - centerY) * 1000 / radiusY;
				if (dy < -720 || dy > 720)
					continue;
				Span<uint> row = s_desktop.Row(y);
				for (int x = 0; x < width; x++)
				{
					long dx = (x - centerX) * 1000 / radiusX;
					uint t = IntSqrt((ulong)(dx * dx + dy * dy));
					if (t >= 720)
						continue;

					// Glow strength in thousandths: 170 at the center, 70 by t = 420, gone by t = 720
					uint glow = OuterGlow;
					uint alpha;
					if (t <= 420)
					{
						glow = Surface.Blend(Inner, OuterGlow, (int)t, 420);
						alpha = 170 - 100 * t / 420;
					}
					else
					{
						alpha = 70 * (720 - t) / 300;
					}
					row[x] = Surface.Blend(Theme.Void, glow, (int)alpha, 1000);
				}
			}

			// The wordmark low on the right: a dark edge above, then letters a shade lighter than what is under them
			const int Scale = 6;
			const string Mark = "ALLOY";
			int markX = width - 72 - Surface.TextWidth(Mark.Length, Scale);
			int markY = height - 96 - 8 * Scale;
			for (int pass = 0; pass < 2; pass++)
			{
				for (int i = 0; i < Mark.Length; i++)
				{
					ReadOnlySpan<byte> map = Font8x8.Glyph(Mark[i]);
					for (int row = 0; row < 8; row++)
					{
						for (int column = 0; column < 8; column++)
						{
							if ((map[row] & (1 << column)) == 0)
								continue;
							int cellX = markX + (i * 8 + column) * Scale;
							int cellY = markY + row * Scale - (pass == 0 ? 2 : 0);
							if (pass == 0)
							{
								s_desktop.Darken(cellX, cellY, Scale, Scale, 150);
								continue;
							}
							for (int y = 0; y < Scale; y++)
							{
								Span<uint> pixel = s_desktop.Row(cellY + y).Slice(cellX, Scale);
								for (int x = 0; x < Scale; x++)
									pixel[x] = Surface.Blend(pixel[x], Theme.Steel, 7, 100);
							}
						}
					}
				}
			}
		}

		private static uint IntSqrt(ulong value)
		{
			ulong result = 0;
			ulong bit = 1UL << 62;
			while (bit > value)
				bit >>= 2;
			ulong rest = value;
			while (bit != 0)
			{
				if (rest >= result + bit)
				{
					rest -= result + bit;
					result = (result >> 1) + bit;
				}
				else
				{
					result >>= 1;
				}
				bit >>= 2;
			}
			return (uint)result;
		}

		// A soft shadow under and beside a window: 1px bands that fade out with distance
		private static void DrawShadow(Rect outer)
		{
			int right = outer.X + outer.Width;
			int bottom = outer.Y + outer.Height;
			for (int i = 0; i < 6; i++)
			{
				uint keep = 130 + (uint)i * 20;
				int side = outer.Height > 10 ? outer.Height - 10 + i : 0;
				s_back.Darken(outer.X - i, bottom + i, outer.Width + 2 * i, 1, keep);
				s_back.Darken(right + i, outer.Y + 10, 1, side, keep);
				s_back.Darken(outer.X - 1 - i, outer.Y + 10, 1, side, keep);
			}
		}

		private static void DrawTitleButton(WmWindow window, Button button, bool focused)
		{
			Rect rect = ButtonRect(window, button);
			bool hover = !s_switching && rect.Contains(s_mouseX, s_mouseY) && HitTest(s_mouseX, s_mouseY) == window;
			bool pressed = hover && s_pressedWindow == window && s_pressedButton == button;
			Theme.Face face = button == Button.Close
				? (pressed ? Theme.DangerSunken : hover ? Theme.DangerHover : Theme.Raised)
				: (pressed ? Theme.Sunken : hover ? Theme.RaisedHover : Theme.Raised);
			s_back.DrawFace(rect.X, rect.Y, rect.Width, rect.Height, face);

			PixelGlyph glyph = button == Button.Close ? Icons.Close : button == Button.Maximize ? (window.Maximized ? Icons.Restore : Icons.Maximize) : Icons.Minimize;
			s_back.DrawGlyph(rect.X + (Theme.ButtonWidth - 10) / 2, rect.Y + (Theme.ButtonHeight - 10) / 2, glyph, 2, focused || hover ? 256u : 150u);
		}

		private static void DrawWindow(WmWindow window)
		{
			bool focused = window == s_focus;
			if (!IsFramed(window))
			{
				if (focused)
					s_back.Blit(window.Client.X, window.Client.Y, in window.Surface);
				else
					s_back.BlitDimmed(window.Client.X, window.Client.Y, in window.Surface, DimKeep);
				return;
			}

			Rect outer = Outer(window);
			DrawShadow(outer);
			if (focused)
				s_back.DrawOutline(outer.X - 1, outer.Y - 1, outer.Width + 2, outer.Height + 2, 0x2A170F);
			s_back.DrawOutline(outer.X, outer.Y, outer.Width, outer.Height, Theme.Outline);
			s_back.DrawBevel(outer.X + 1, outer.Y + 1, outer.Width - 2, outer.Height - 2, focused ? Theme.BevelLight : 0x34373D, Theme.BevelDark);

			// Title bar: gradient, then the accent line that marks focus
			Rect bar = TitleBar(window);
			s_back.FillGradient(bar.X, bar.Y, bar.Width, bar.Height, focused ? Theme.GunmetalTop : Theme.GunmetalDimTop, focused ? Theme.GunmetalBottom : Theme.GunmetalDimBottom);
			s_back.FillRectangle(bar.X, bar.Y + Theme.TitleHeight, bar.Width, Theme.AccentHeight, focused ? Theme.Ember : Theme.AccentOff);

			int textY = bar.Y + (Theme.TitleHeight - Theme.Glyph) / 2;
			s_back.DrawGlyph(bar.X + 8, textY, window.Icon, Theme.TextScale, focused ? 256u : 150u);
			int textX = bar.X + 8 + Theme.Glyph + 10;
			int buttonsLeft = ButtonRect(window, Button.Minimize).X;
			int maxChars = buttonsLeft - 8 > textX ? (buttonsLeft - 8 - textX) / Theme.Glyph : 0;
			if (focused)
				DrawFitted(textX + 2, textY + 2, window.Title, maxChars, 0x000000, Theme.TextScale);
			DrawFitted(textX, textY, window.Title, maxChars, focused ? Theme.Bone : Theme.TextDim, Theme.TextScale);

			DrawTitleButton(window, Button.Minimize, focused);
			if (IsResizable(window))
				DrawTitleButton(window, Button.Maximize, focused);
			DrawTitleButton(window, Button.Close, focused);

			if (focused)
				s_back.Blit(window.Client.X, window.Client.Y, in window.Surface);
			else
				s_back.BlitDimmed(window.Client.X, window.Client.Y, in window.Surface, DimKeep);
		}

		private static Rect StartButtonRect => new Rect(StartX, TaskbarTop + BarButtonTop, StartWidth, BarButtonHeight);

		// Left edge of the tray (network status and clock); task buttons stop before it
		private static int TrayLeft
		{
			get
			{
				int separator = ScreenWidth - 14 - ClockWidth - 18;
				return separator - 10 - Surface.TextWidth(s_addressText.Length, Theme.TextScale) - 10 - Theme.Glyph;
			}
		}

		// Taskbar layout: one button per window in slot order until the tray; -1 when the window's button doesn't fit
		private static int TaskButtonX(int slot)
		{
			int x = TaskX;
			int limit = TrayLeft - 12;
			for (int i = 0; i < MaxWindows; i++)
			{
				if (s_slots[i] == null)
					continue;
				if (x + TaskWidth > limit)
					return -1;
				if (i == slot)
					return x;
				x += TaskWidth + TaskGap;
			}
			return -1;
		}

		private static void DrawTaskButton(WmWindow window, Rect rect)
		{
			bool active = window == s_focus && !window.Minimized;
			bool hover = !active && rect.Contains(s_mouseX, s_mouseY);
			Theme.Face face = active ? TaskActive : hover ? TaskHover : window.Minimized ? TaskMinimized : TaskBehind;
			s_back.DrawFace(rect.X, rect.Y, rect.Width, rect.Height, face);
			if (active)
				s_back.FillRectangle(rect.X + 1, rect.Y + rect.Height - 2, rect.Width - 2, 2, Theme.Ember);

			// A minimized window's button has a broken edge, like it's only half there
			if (window.Minimized && !hover)
			{
				for (int x = rect.X + 2; x < rect.X + rect.Width - 2; x += 8)
				{
					s_back.FillRectangle(x, rect.Y, 4, 1, Theme.Divider);
					s_back.FillRectangle(x, rect.Y + rect.Height - 1, 4, 1, Theme.Divider);
				}
			}

			uint keep = active || hover ? 256u : window.Minimized ? 110u : 180u;
			int y = rect.Y + (BarButtonHeight - Theme.Glyph) / 2;
			s_back.DrawGlyph(rect.X + 10, y, window.Icon, Theme.TextScale, keep);
			DrawFitted(rect.X + 10 + Theme.Glyph + 10, y, window.Title, TaskChars, face.Label, Theme.TextScale);
		}

		private static void DrawTaskbar()
		{
			int top = TaskbarTop;
			int width = s_back.Width;
			s_back.FillGradient(0, top, width, Theme.TaskbarHeight, 0x1C1D21, 0x111214);
			s_back.FillRectangle(0, top - 1, width, 1, 0x000000);
			s_back.FillRectangle(0, top, width, 1, 0x3B3E45);

			Rect start = StartButtonRect;
			Theme.Face startFace = s_startMenuOpen ? StartOpen : start.Contains(s_mouseX, s_mouseY) ? StartHover : StartFace;
			s_back.DrawFace(start.X, start.Y, start.Width, start.Height, startFace);
			int textY = start.Y + (BarButtonHeight - Theme.Glyph) / 2;
			s_back.DrawGlyph(start.X + 10, textY, Icons.Flame, Theme.TextScale);
			s_back.DrawText(start.X + 10 + Theme.Glyph + 10, textY, "Alloy", startFace.Label, Theme.TextScale);
			s_back.FillRectangle(start.X + StartWidth + 5, top + 7, 1, 26, Theme.Divider);

			for (int i = 0; i < MaxWindows; i++)
			{
				int x = s_slots[i] == null ? -1 : TaskButtonX(i);
				if (x >= 0)
					DrawTaskButton(s_slots[i], new Rect(x, top + BarButtonTop, TaskWidth, BarButtonHeight));
			}

			// Tray: network status, a separator, then the time over the date
			int clockX = ScreenWidth - 14 - ClockWidth;
			int separator = clockX - 18;
			int addressX = separator - 10 - Surface.TextWidth(s_addressText.Length, Theme.TextScale);
			s_back.DrawGlyph(addressX - 10 - Theme.Glyph, textY, Icons.Network, Theme.TextScale, s_address != 0 ? 256u : 90u);
			s_back.DrawText(addressX, textY, s_addressText, s_address != 0 ? 0xC9C3B6 : Theme.Muted, Theme.TextScale);
			s_back.FillRectangle(separator, top + 7, 1, 26, Theme.Divider);

			SystemTime time = Kernel.Time;
			s_back.DrawText(clockX, top + 6, $"{time.Hour:D2}:{time.Minute:D2}", Theme.Bone, Theme.TextScale);

			int month = time.Month >= 1 && time.Month <= 12 ? time.Month : 1;
			string weekday = "SUNMONTUEWEDTHUFRISAT".Substring(3 * WeekDay(time.Year, month, time.Day), 3);
			string date = $"{weekday} {time.Day:D2} {"JANFEBMARAPRMAYJUNJULAUGSEPOCTNOVDEC".Substring(3 * (month - 1), 3)}";
			s_back.DrawText(ScreenWidth - 14 - Surface.TextWidth(date.Length, 1), top + 26, date, Theme.Steel, 1);
		}

		// The address NetSvr publishes once dhcp configures it, checked about twice a second; true when it changed
		private static bool PollNetwork()
		{
			if (s_netAddress.IsEmpty)
			{
				long section = Endpoint.Connect(NetProtocol.AddressEndpoint);
				if (section > 0)
					s_netAddress = SharedMemory.Map(section, 4096);
			}
			uint address = s_netAddress.IsEmpty ? 0 : BinaryPrimitives.ReadUInt32LittleEndian(s_netAddress.Span);
			if (address == s_address)
				return false;
			s_address = address;
			s_addressText = address != 0 ? Net.FormatIp(address) : Offline;
			return true;
		}

		// 0 is Sunday (Sakamoto's method), worked out from the date since the clock chip's own weekday isn't kept reliably
		private static int WeekDay(int year, int month, int day)
		{
			ReadOnlySpan<byte> offsets = new byte[] { 0, 3, 2, 5, 0, 3, 5, 1, 4, 6, 2, 4 };
			if (month < 3)
				year--;
			return (year + year / 4 - year / 100 + year / 400 + offsets[month - 1] + day) % 7;
		}

		// The start menu sits just above the taskbar, aligned with the Start button
		private static Rect StartMenuRect
		{
			get
			{
				int height = MenuHeader + 2 * MenuPad + s_apps.Length * MenuItemHeight + MenuFooter;
				return new Rect(StartX, TaskbarTop - 6 - height, MenuWidth, height);
			}
		}

		private static Rect MenuItemRect(int index)
		{
			Rect menu = StartMenuRect;
			return new Rect(menu.X + 2, menu.Y + MenuHeader + MenuPad + index * MenuItemHeight, menu.Width - 4, MenuItemHeight);
		}

		private static void DrawStartMenu()
		{
			if (!s_startMenuOpen)
				return;
			Rect menu = StartMenuRect;
			DrawShadow(menu);
			s_back.DrawOutline(menu.X, menu.Y, menu.Width, menu.Height, Theme.Outline);
			s_back.FillGradient(menu.X + 1, menu.Y + 1, menu.Width - 2, menu.Height - 2, 0x1A1B1F, 0x141518);
			s_back.DrawBevel(menu.X + 1, menu.Y + 1, menu.Width - 2, menu.Height - 2, Theme.BevelLight, Theme.BevelDark);

			// Header: the wordmark over an ember line
			s_back.FillRectangle(menu.X + 2, menu.Y + 2, menu.Width - 4, MenuHeader - 4, 0x111214);
			s_back.FillRectangle(menu.X + 2, menu.Y + MenuHeader - 2, menu.Width - 4, 2, Theme.Ember);
			s_back.DrawGlyph(menu.X + 16, menu.Y + 18, Icons.Flame, 4);
			s_back.DrawText(menu.X + 67, menu.Y + 27, "ALLOY", 0x000000, 3);
			s_back.DrawText(menu.X + 64, menu.Y + 24, "ALLOY", Theme.EmberText, 3);

			for (int i = 0; i < s_apps.Length; i++)
			{
				Rect item = MenuItemRect(i);
				bool selected = i == s_menuSelection;
				if (selected)
				{
					s_back.FillRectangle(item.X, item.Y, item.Width, item.Height, Theme.Selection);
					s_back.DrawOutline(item.X, item.Y, item.Width, item.Height, Theme.SelectionEdge);
				}
				s_back.DrawGlyph(item.X + 14, item.Y + 12, s_apps[i].Icon, 4);
				s_back.DrawText(item.X + 60, item.Y + 12, s_apps[i].Label, selected ? Theme.Molten : Theme.Bone, Theme.TextScale);
				s_back.DrawText(item.X + 60, item.Y + 36, s_apps[i].Hint, selected ? 0xB4A294 : Theme.Steel, 1);
			}

			// Footer: the keyboard shortcuts
			int footer = menu.Y + menu.Height - MenuFooter;
			s_back.FillRectangle(menu.X + 2, footer, menu.Width - 4, 1, Theme.AccentOff);
			s_back.DrawText(menu.X + 16, footer + 14, "CTRL+ESC MENU", Theme.Hint, 1);
			const string SwitchHint = "ALT+TAB SWITCH";
			s_back.DrawText(menu.X + menu.Width - 16 - Surface.TextWidth(SwitchHint.Length, 1), footer + 14, SwitchHint, Theme.Hint, 1);
		}

		// A window's client area scaled to fit inside a box, keeping its shape
		private static void DrawThumbnail(WmWindow window, Rect box)
		{
			int width = window.Client.Width, height = window.Client.Height;
			int w = box.Width;
			int h = (int)((long)height * box.Width / width);
			if (h > box.Height)
			{
				h = box.Height;
				w = (int)((long)width * box.Height / height);
			}
			if (w > 0 && h > 0)
				s_back.BlitScaled(box.X + (box.Width - w) / 2, box.Y + (box.Height - h) / 2, w, h, in window.Surface);
		}

		private static void DrawSwitcher()
		{
			if (!s_switching)
				return;
			int count = s_zcount;
			if (count == 0)
			{
				s_switching = false;
				return;
			}
			if (s_switchSelection >= count)
				s_switchSelection = 0;

			// As many tiles as fit across the screen, scrolled so the selection shows
			int shown = Math.Min((ScreenWidth - 120 + TileGap) / (TileWidth + TileGap), count);
			int first = s_switchSelection >= shown ? s_switchSelection - shown + 1 : 0;
			s_back.Darken(0, 0, s_back.Width, s_back.Height, 110);

			int panelWidth = shown * TileWidth + (shown - 1) * TileGap + 44;
			const int PanelHeight = 18 + 8 + 18 + TileHeight + 22;
			Rect panel = new Rect((ScreenWidth - panelWidth) / 2, (s_back.Height - PanelHeight) / 2, panelWidth, PanelHeight);
			DrawShadow(panel);
			s_back.FillRectangle(panel.X, panel.Y, panel.Width, panel.Height, 0x141518);
			s_back.DrawOutline(panel.X, panel.Y, panel.Width, panel.Height, Theme.Outline);
			s_back.FillRectangle(panel.X, panel.Y, panel.Width, 2, Theme.Ember);
			s_back.DrawText(panel.X + 22, panel.Y + 18, "SWITCH WINDOW", Theme.Hint, 1);
			const string Hint = "HOLD ALT, TAB TO MOVE";
			s_back.DrawText(panel.X + panel.Width - 22 - Surface.TextWidth(Hint.Length, 1), panel.Y + 18, Hint, Theme.Hint, 1);

			for (int i = 0; i < shown; i++)
			{
				WmWindow window = s_zorder[first + i];
				bool selected = first + i == s_switchSelection;
				Rect tile = new Rect(panel.X + 22 + i * (TileWidth + TileGap), panel.Y + 18 + 8 + 18, TileWidth, TileHeight);
				if (selected)
				{
					s_back.FillRectangle(tile.X, tile.Y, tile.Width, tile.Height, 0x1D1612);
					s_back.DrawOutline(tile.X, tile.Y, tile.Width, tile.Height, Theme.Ember);
					s_back.DrawOutline(tile.X + 1, tile.Y + 1, tile.Width - 2, tile.Height - 2, Theme.Ember);
				}

				// Thumbnail: a sliver of title bar over the window's contents
				Rect thumb = new Rect(tile.X + 10, tile.Y + 10, TileWidth - 20, ThumbHeight);
				s_back.FillRectangle(thumb.X, thumb.Y, thumb.Width, thumb.Height, Theme.IronDeep);
				s_back.FillGradient(thumb.X, thumb.Y, thumb.Width, 8, Theme.GunmetalTop, Theme.GunmetalBottom);
				if (selected)
					s_back.FillRectangle(thumb.X, thumb.Y + 8, thumb.Width, 1, Theme.Ember);
				DrawThumbnail(window, new Rect(thumb.X + 4, thumb.Y + 12, thumb.Width - 8, thumb.Height - 16));
				s_back.DrawOutline(thumb.X, thumb.Y, thumb.Width, thumb.Height, Theme.Outline);

				int labelY = thumb.Y + ThumbHeight + 12;
				s_back.DrawGlyph(tile.X + 10, labelY, window.Icon, Theme.TextScale, selected ? 256u : 180u);
				DrawFitted(tile.X + 10 + Theme.Glyph + 10, labelY, window.Title, TileChars, selected ? Theme.Bone : 0x9B978F, Theme.TextScale);
			}
		}
	}
}
