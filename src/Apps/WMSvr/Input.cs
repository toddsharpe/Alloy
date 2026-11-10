using Alloy.Abi;
using Alloy.Sdk;

namespace Alloy.Apps.WMSvr
{
	// Input routing as in MetalOS-WMSvr: the WM keeps its hotkeys and clicks on its own chrome, the focused window gets the rest
	internal static partial class Program
	{
		private static bool ProcessInput()
		{
			if (s_input == null)
				return false;
			bool changed = false;
			while (s_input.TryRead(out InputEvent input))
			{
				if (input.Type == InputEventType.Key)
					changed |= OnKey(input.Key, input.Pressed != 0);
				else if (input.Type == InputEventType.Mouse)
					changed |= OnMouse(in input);
			}
			return changed;
		}

		private static void Launch(int index)
		{
			s_startMenuOpen = false;
			if (index < 0 || index >= s_apps.Length)
				return;
			long process = Kernel.CreateProcess("bin/" + s_apps[index].Exe);
			if (process > 0)
				Kernel.Close(process);
		}

		private static void OpenStartMenu()
		{
			// A fullscreen window would hide the menu, so it steps aside
			if (FullscreenActive)
				Minimize(s_focus);
			s_startMenuOpen = true;
			s_menuSelection = 0;
		}

		// Windows in switch order: the z-order from the top, which is also most recently focused first
		private static void StartSwitch()
		{
			if (s_zcount == 0)
				return;
			s_startMenuOpen = false;
			s_switching = true;
			s_switchSelection = s_zcount > 1 ? 1 : 0;
		}

		private static void CommitSwitch()
		{
			s_switching = false;
			if (s_zcount != 0)
				Focus(s_zorder[s_switchSelection < s_zcount ? s_switchSelection : 0]);
		}

		// Keys the WM keeps for itself: Alt+Tab, Ctrl+Esc and the open start menu; true means the app doesn't see it
		private static bool HandleHotkey(ushort key)
		{
			if (s_switching)
			{
				if (key == VirtualKey.Tab)
					s_switchSelection = s_zcount != 0 ? (s_switchSelection + 1) % s_zcount : 0;
				else if (key == VirtualKey.Escape)
					s_switching = false;
				return true;
			}
			if (s_alt && key == VirtualKey.Tab)
			{
				StartSwitch();
				return true;
			}
			if ((s_ctrl && key == VirtualKey.Escape) || key == VirtualKey.LeftWindows)
			{
				if (s_startMenuOpen)
					s_startMenuOpen = false;
				else
					OpenStartMenu();
				return true;
			}
			if (!s_startMenuOpen)
				return false;

			int count = s_apps.Length;
			if (key == VirtualKey.Up)
				s_menuSelection = s_menuSelection == 0 ? count - 1 : s_menuSelection - 1;
			else if (key == VirtualKey.Down)
				s_menuSelection = (s_menuSelection + 1) % count;
			else if (key == VirtualKey.Return)
				Launch(s_menuSelection);
			else if (key == VirtualKey.Escape)
				s_startMenuOpen = false;
			return true;
		}

		// Returns whether the screen needs redrawing
		private static bool OnKey(ushort key, bool pressed)
		{
			int slot = key & 0xFF;
			if (key == VirtualKey.Control)
				s_ctrl = pressed;

			// Alt always reaches the app, and letting go of it ends a switch once the window losing focus has seen it
			if (key == VirtualKey.Menu)
			{
				s_alt = pressed;
				if (s_focus != null)
					Post(s_focus, pressed ? WmProtocol.KeyDown : WmProtocol.KeyUp, key);
				if (!pressed && s_switching)
				{
					CommitSwitch();
					return true;
				}
				return false;
			}
			if (!pressed && s_swallow[slot])
			{
				s_swallow[slot] = false;
				return false;
			}
			if (pressed && HandleHotkey(key))
			{
				s_swallow[slot] = true;
				return true;
			}
			if (s_focus != null)
				Post(s_focus, pressed ? WmProtocol.KeyDown : WmProtocol.KeyUp, key);
			return false;
		}

		private static void HandleTaskbarPress(int x, int y)
		{
			if (StartButtonRect.Contains(x, y))
			{
				s_startMenuOpen = !s_startMenuOpen;
				s_menuSelection = 0;
				return;
			}
			s_startMenuOpen = false;

			// A task button minimizes the focused window, otherwise restores, focuses and raises its window
			for (int i = 0; i < MaxWindows; i++)
			{
				int buttonX = s_slots[i] == null ? -1 : TaskButtonX(i);
				if (buttonX < 0 || !new Rect(buttonX, TaskbarTop + BarButtonTop, TaskWidth, BarButtonHeight).Contains(x, y))
					continue;
				if (s_slots[i] == s_focus && !s_slots[i].Minimized)
					Minimize(s_slots[i]);
				else
					Focus(s_slots[i]);
				return;
			}
		}

		private static void OnPress(int x, int y)
		{
			// Mid-switch clicks do nothing, and a fullscreen window takes the whole mouse
			if (s_switching)
			{
				s_wmPress = true;
				return;
			}
			if (FullscreenActive)
				return;
			if (s_startMenuOpen && StartMenuRect.Contains(x, y))
			{
				s_menuPress = true;
				s_wmPress = true;
				return;
			}
			if (y >= TaskbarTop)
			{
				HandleTaskbarPress(x, y);
				s_wmPress = true;
				return;
			}

			s_startMenuOpen = false;
			WmWindow hit = HitTest(x, y);
			if (hit == null)
				return;
			Focus(hit);
			Button button = ButtonAt(hit, x, y);
			if (button != Button.None)
			{
				s_pressedWindow = hit;
				s_pressedButton = button;
				s_wmPress = true;
				return;
			}

			// Only the title bar drags a window, and the app sees none of it; a second click soon after maximizes
			if (IsFramed(hit) && TitleBar(hit).Contains(x, y))
			{
				s_wmPress = true;
				ulong now = Kernel.TickCount;
				if (s_lastTitleClick == hit && now - s_lastTitleTime < DoubleClickTime)
				{
					s_lastTitleClick = null;
					ToggleMaximize(hit);
				}
				else
				{
					s_lastTitleClick = hit;
					s_lastTitleTime = now;
					if (!hit.Maximized)
						s_drag = hit;
				}
				return;
			}

			// The frame's edges belong to the WM; a press in the client area goes to the app
			if (!hit.Client.Contains(x, y))
				s_wmPress = true;
		}

		private static void OnRelease(int x, int y)
		{
			// A title-bar button acts when it's released over the button it was pressed on
			if (s_pressedButton != Button.None)
			{
				WmWindow window = s_pressedWindow;
				Button button = s_pressedButton;
				s_pressedWindow = null;
				s_pressedButton = Button.None;
				if (window != null && ZIndex(window) >= 0 && ButtonAt(window, x, y) == button)
				{
					if (button == Button.Close)
						CloseWindow(window);
					else if (button == Button.Minimize)
						Minimize(window);
					else
						ToggleMaximize(window);
				}
			}
			if (s_menuPress)
			{
				s_menuPress = false;
				for (int i = 0; i < s_apps.Length; i++)
				{
					if (s_startMenuOpen && StartMenuRect.Contains(x, y) && MenuItemRect(i).Contains(x, y))
						Launch(i);
				}
			}
			if (s_drag != null)
				KeepReachable(s_drag);
			s_drag = null;
		}

		private static bool OnMouse(in InputEvent input)
		{
			ushort buttons = input.Buttons;
			bool move = buttons == s_prevButtons;
			int x = (int)((long)s_screen.Width * input.X / InputEvent.MaxPosition);
			int y = (int)((long)s_screen.Height * input.Y / InputEvent.MaxPosition);
			bool left = (buttons & InputEvent.LeftButton) != 0;
			bool down = left && (s_prevButtons & InputEvent.LeftButton) == 0;
			bool up = !left && (s_prevButtons & InputEvent.LeftButton) != 0;

			if (down)
			{
				OnPress(x, y);
			}
			else if (left && s_drag != null)
			{
				s_drag.Client.X += x - s_mouseX;
				s_drag.Client.Y += y - s_mouseY;
			}
			if (up)
				OnRelease(x, y);

			// The menu item under the mouse is the selected one
			if (s_startMenuOpen)
			{
				for (int i = 0; i < s_apps.Length; i++)
				{
					if (MenuItemRect(i).Contains(x, y))
						s_menuSelection = i;
				}
			}
			s_mouseX = x;
			s_mouseY = y;
			s_prevButtons = buttons;

			// The app doesn't see a press the WM handled, nor the moves and release that follow it
			if (s_wmPress)
			{
				if (up)
					s_wmPress = false;
				return true;
			}
			if (s_focus == null)
				return true;

			// Apps hit-test in client coordinates; a fullscreen window's are its surface's
			long clientX, clientY;
			if (FullscreenActive)
			{
				Rect fit = FullscreenRect(s_focus);
				clientX = (long)(x - fit.X) * s_focus.Client.Width / fit.Width;
				clientY = (long)(y - fit.Y) * s_focus.Client.Height / fit.Height;
			}
			else
			{
				clientX = x - s_focus.Client.X;
				clientY = y - s_focus.Client.Y;
			}
			if (!move || !FullscreenActive)
				Post(s_focus, WmProtocol.Mouse, buttons, (ulong)clientX, (ulong)clientY);
			return !FullscreenActive;
		}
	}
}
