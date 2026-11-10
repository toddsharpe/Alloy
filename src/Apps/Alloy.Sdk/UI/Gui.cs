using System.Collections.Generic;
using Alloy.Abi;
using Alloy.Graphics;

namespace Alloy.Sdk.UI
{
	// Handles an event first; true means it was handled and needs no default handling
	public delegate bool GuiEventHandler(Gui gui, in IpcMessage message);

	// Runs every tick (about 30 a second); true means the window needs redrawing
	public delegate bool GuiTickHandler(Gui gui);

	// An app window (MetalOS user/GUI.h): the WM draws the frame and title, this draws the client area and its controls
	public sealed class Gui
	{
		private const ulong TickMs = 33;

		public readonly Window Window;
		public uint Background = Theme.Iron;
		public bool Animate;
		public GuiEventHandler OnEvent;
		public GuiTickHandler OnTick;

		private readonly List<Control> _children = new List<Control>();
		private ushort _prevButtons;
		private Control _pressed;

		private Gui(Window window)
		{
			Window = window;
		}

		public static Gui Create(string title, int x, int y, int width, int height, ulong style = WmProtocol.StyleFramed, PixelGlyph icon = null)
		{
			Window window = Window.Create(title, x, y, width, height, style, icon);
			return window == null ? null : new Gui(window);
		}

		public int Width => Window.Surface.Width;

		public int Height => Window.Surface.Height;

		public void Add(Control child) => _children.Add(child);

		// Until the WM asks the window to close; draws and presents whenever something changed
		public void Run()
		{
			Draw();
			while (true)
			{
				bool dirty = Animate;
				if (Window.WaitEvent(out IpcMessage message, TickMs))
				{
					do
					{
						if (message.Data[0] != Window.Id)
							continue;
						if (message.Type == WmProtocol.Close)
							return;
						if (message.Type == WmProtocol.Resize)
							Window.Resize((int)message.Data[1], (int)message.Data[2]);
						if (OnEvent == null || !OnEvent(this, in message))
							Default(in message);
						dirty = true;
					}
					while (Window.TryGetEvent(out message));
				}
				if (OnTick != null)
					dirty |= OnTick(this);
				if (dirty)
					Draw();
			}
		}

		public void Draw()
		{
			Surface surface = Window.Surface;
			surface.Fill(Background);
			foreach (Control child in _children)
				child.Draw(in surface);
			Window.Present();
		}

		private void Default(in IpcMessage message)
		{
			if (message.Type == WmProtocol.Mouse)
				OnMouse((ushort)message.Data[1], (int)(long)message.Data[2], (int)(long)message.Data[3]);
		}

		// The child drawn last under a point, since it is the one on top
		private Control HitTest(int x, int y)
		{
			for (int i = _children.Count - 1; i >= 0; i--)
			{
				if (_children[i].Bounds.Contains(x, y))
					return _children[i];
			}
			return null;
		}

		// A click is the left button going down and coming back up over the same child; the child under the mouse shows as hovered
		private void OnMouse(ushort buttons, int x, int y)
		{
			Control under = HitTest(x, y);
			foreach (Control child in _children)
				child.Hovered = child == under;
			bool left = (buttons & InputEvent.LeftButton) != 0;
			bool wasLeft = (_prevButtons & InputEvent.LeftButton) != 0;
			if (left && !wasLeft)
			{
				_pressed = under;
				if (_pressed != null)
					_pressed.Pressed = true;
			}
			else if (!left && wasLeft)
			{
				if (_pressed != null)
				{
					_pressed.Pressed = false;
					if (under == _pressed)
						_pressed.OnClick();
				}
				_pressed = null;
			}
			_prevButtons = buttons;
		}
	}
}
