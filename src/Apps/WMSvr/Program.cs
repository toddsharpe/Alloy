using System;
using System.Text;
using Alloy.Abi;
using Alloy.Graphics;
using Alloy.Sdk;

namespace Alloy.Apps.WMSvr
{
	internal struct Rect
	{
		public int X;
		public int Y;
		public int Width;
		public int Height;

		public Rect(int x, int y, int width, int height)
		{
			X = x;
			Y = y;
			Width = width;
			Height = height;
		}

		public readonly bool Contains(int x, int y) => x >= X && x < X + Width && y >= Y && y < Y + Height;
	}

	internal sealed class Connection
	{
		public long Reply;
		public long Process;
	}

	internal sealed class WmWindow
	{
		public Connection Owner;
		public ulong Id;
		public long Section;
		public Surface Surface;
		public Rect Client;
		public Rect Restore;
		public string Title;
		public ulong Style;
		public PixelGlyph Icon;
		public bool Minimized;
		public bool Maximized;
	}

	internal enum Button
	{
		None,
		Minimize,
		Maximize,
		Close,
	}

	internal readonly record struct AppEntry(string Label, string Hint, string Exe, PixelGlyph Icon);

	// The user-mode window manager (MetalOS-WMSvr): compositing, frames, focus, drag, z-order, taskbar, start menu, Alt+Tab and input routing
	internal static partial class Program
	{
		private const int MaxWindows = 64;
		private const int MaxConnections = 16;
		private const uint DimKeep = 184;
		private const ulong DoubleClickTime = 400;
		private const int TitleGrip = 96;
		private const ulong FrameMs = 33;
		private const ulong ReapMs = 500;

		private static Surface s_screen;
		private static Surface s_back;
		private static Surface s_desktop;

		private static InputQueue s_input;
		private static int s_mouseX;
		private static int s_mouseY;
		private static ushort s_prevButtons;
		private static bool s_wmPress;
		private static bool s_ctrl;
		private static bool s_alt;
		private static readonly bool[] s_swallow = new bool[256];

		private static readonly Connection[] s_connections = new Connection[MaxConnections];
		private static readonly WmWindow[] s_slots = new WmWindow[MaxWindows];
		private static readonly WmWindow[] s_zorder = new WmWindow[MaxWindows];
		private static int s_zcount;
		private static ulong s_nextWindow = 1;

		private static WmWindow s_focus;
		private static WmWindow s_drag;
		private static WmWindow s_pressedWindow;
		private static Button s_pressedButton;
		private static WmWindow s_lastTitleClick;
		private static ulong s_lastTitleTime;

		private static bool s_startMenuOpen;
		private static bool s_menuPress;
		private static int s_menuSelection;
		private static bool s_switching;
		private static int s_switchSelection;

		private static int Main()
		{
			if (!Surfaces.TryMapFramebuffer(out s_screen, out FramebufferInfo info))
			{
				Console.WriteLine("wmsvr: the framebuffer is already taken");
				return 1;
			}
			s_back = Surfaces.Allocate(s_screen.Width, s_screen.Height);
			s_desktop = Surfaces.Allocate(s_screen.Width, s_screen.Height);
			BuildDesktop();
			Console.WriteLine($"wmsvr: framebuffer {info.Width}x{info.Height}");

			long port = Port.Create();
			Endpoint.Register(WmProtocol.Endpoint, port);

			// The kernel publishes its input ring before init starts; without VMBus input there is none
			s_input = InputQueue.Open();
			Console.WriteLine(s_input != null ? "wmsvr: serving \"wm\", reading the kernel input ring" : "wmsvr: serving \"wm\" without keyboard and mouse");

			ReadOnlySpan<long> handles = stackalloc long[] { port, s_input?.ReadyEvent ?? 0 };
			ReadOnlySpan<long> waits = handles.Slice(0, s_input != null ? 2 : 1);
			bool dirty = true;
			ulong lastFrame = 0;
			ulong lastMinute = ulong.MaxValue;
			ulong lastReap = 0;
			while (true)
			{
				Event.WaitAny(waits, FrameMs);
				long transferred;
				while ((transferred = Port.Receive(port, out IpcMessage message, 0)) >= 0)
					dirty |= Handle(in message, transferred);
				dirty |= ProcessInput();

				ulong now = Kernel.TickCount;
				if (now - lastReap >= ReapMs)
				{
					dirty |= ReapDeadConnections();
					dirty |= PollNetwork();
					lastReap = now;
				}
				SystemTime time = Kernel.Time;
				ulong minute = (ulong)time.Hour * 60 + time.Minute;
				if (minute != lastMinute)
				{
					lastMinute = minute;
					dirty = true;
				}
				if (dirty && now - lastFrame >= FrameMs / 2)
				{
					Composite();
					lastFrame = now;
					dirty = false;
				}
			}
		}

		private static int ScreenWidth => s_screen.Width;

		private static int TaskbarTop => s_screen.Height - Theme.TaskbarHeight;

		private static bool IsFullscreen(WmWindow window) => (window.Style & WmProtocol.StyleFullscreen) != 0;

		private static bool IsFramed(WmWindow window) => (window.Style & WmProtocol.StyleChromeless) == 0 && !IsFullscreen(window);

		private static bool IsResizable(WmWindow window) => IsFramed(window) && (window.Style & WmProtocol.StyleResizable) != 0;

		private static bool FullscreenActive => s_focus != null && IsFullscreen(s_focus) && !s_focus.Minimized;

		// The window and its frame
		private static Rect Outer(WmWindow window)
		{
			Rect c = window.Client;
			return IsFramed(window) ? new Rect(c.X - Theme.FrameEdge, c.Y - Theme.FrameTop, c.Width + 2 * Theme.FrameEdge, c.Height + Theme.FrameTop + Theme.FrameEdge) : c;
		}

		private static Rect TitleBar(WmWindow window)
		{
			Rect outer = Outer(window);
			return new Rect(outer.X + Theme.FrameEdge, outer.Y + Theme.FrameEdge, outer.Width - 2 * Theme.FrameEdge, Theme.TitleHeight);
		}

		// Title-bar buttons from the right: close, then maximize (resizable windows only), then minimize
		private static Rect ButtonRect(WmWindow window, Button button)
		{
			int slot = button == Button.Close ? 0 : button == Button.Maximize ? 1 : IsResizable(window) ? 2 : 1;
			Rect bar = TitleBar(window);
			int x = bar.X + bar.Width - Theme.ButtonMargin - Theme.ButtonWidth - slot * (Theme.ButtonWidth + Theme.ButtonGap);
			return new Rect(x, bar.Y + (Theme.TitleHeight - Theme.ButtonHeight) / 2, Theme.ButtonWidth, Theme.ButtonHeight);
		}

		private static Button ButtonAt(WmWindow window, int x, int y)
		{
			if (!IsFramed(window))
				return Button.None;
			if (ButtonRect(window, Button.Close).Contains(x, y))
				return Button.Close;
			if (IsResizable(window) && ButtonRect(window, Button.Maximize).Contains(x, y))
				return Button.Maximize;
			if (ButtonRect(window, Button.Minimize).Contains(x, y))
				return Button.Minimize;
			return Button.None;
		}

		// A fullscreen window's surface, as large as fits the screen without changing its shape, centered
		private static Rect FullscreenRect(WmWindow window)
		{
			int width = window.Client.Width, height = window.Client.Height;
			int w = s_screen.Width;
			int h = (int)((long)height * s_screen.Width / width);
			if (h > s_screen.Height)
			{
				h = s_screen.Height;
				w = (int)((long)width * s_screen.Height / height);
			}
			return new Rect((s_screen.Width - w) / 2, (s_screen.Height - h) / 2, w, h);
		}

		// Text cut to maxChars, ending in ".." when it doesn't fit
		private static void DrawFitted(int x, int y, string text, int maxChars, uint color, int scale)
		{
			if (text.Length <= maxChars)
			{
				s_back.DrawText(x, y, text, color, scale);
				return;
			}
			int kept = maxChars > 2 ? maxChars - 2 : 0;
			int end = s_back.DrawText(x, y, text.AsSpan(0, kept), color, scale);
			s_back.DrawText(end, y, "..", color, scale);
		}

		private static void Raise(WmWindow window)
		{
			int index = ZIndex(window);
			if (index < 0)
				return;
			for (int i = index; i > 0; i--)
				s_zorder[i] = s_zorder[i - 1];
			s_zorder[0] = window;
		}

		private static int ZIndex(WmWindow window) => Array.IndexOf(s_zorder, window, 0, s_zcount);

		// The topmost window that isn't minimized and contains the point
		private static WmWindow HitTest(int x, int y)
		{
			for (int i = 0; i < s_zcount; i++)
			{
				if (!s_zorder[i].Minimized && Outer(s_zorder[i]).Contains(x, y))
					return s_zorder[i];
			}
			return null;
		}

		private static WmWindow TopmostVisible()
		{
			for (int i = 0; i < s_zcount; i++)
			{
				if (!s_zorder[i].Minimized)
					return s_zorder[i];
			}
			return null;
		}

		private static WmWindow Find(Connection owner, ulong id)
		{
			foreach (WmWindow window in s_slots)
			{
				if (window != null && window.Id == id && (owner == null || window.Owner == owner))
					return window;
			}
			return null;
		}

		// A message for a window to its owner's port, stamped with the window's id; a full port drops it
		private static void Post(WmWindow window, ulong type, ulong a = 0, ulong b = 0, ulong c = 0, ulong d = 0)
		{
			IpcMessage message = default;
			message.Type = type;
			message.Data[0] = window.Id;
			message.Data[1] = a;
			message.Data[2] = b;
			message.Data[3] = c;
			message.Data[4] = d;
			Port.Send(window.Owner.Reply, message);
		}

		// Keeps enough of a framed window's title bar on screen (and above the taskbar) to grab it again
		private static void KeepReachable(WmWindow window)
		{
			if (!IsFramed(window))
				return;
			Rect outer = Outer(window);
			int right = outer.X + outer.Width;
			if (right < TitleGrip)
				window.Client.X += TitleGrip - right;
			if (outer.X > ScreenWidth - TitleGrip)
				window.Client.X -= outer.X - (ScreenWidth - TitleGrip);
			if (outer.Y < 0)
				window.Client.Y -= outer.Y;
			if (outer.Y > TaskbarTop - Theme.FrameTop)
				window.Client.Y -= outer.Y - (TaskbarTop - Theme.FrameTop);
		}

		// Focus shows and raises a window; a fullscreen window only shows while it has focus, so one losing it is minimized
		private static void Focus(WmWindow window)
		{
			if (s_focus != null && s_focus != window && IsFullscreen(s_focus))
				s_focus.Minimized = true;
			s_focus = window;
			if (window != null)
			{
				window.Minimized = false;
				Raise(window);
			}
		}

		// Maximize fills the space above the taskbar; the app draws at the new size once it answers with ResizeWindow
		private static void ToggleMaximize(WmWindow window)
		{
			if (!IsResizable(window))
				return;
			Rect target = window.Restore;
			if (!window.Maximized)
			{
				window.Restore = window.Client;
				target = new Rect(Theme.FrameEdge, Theme.FrameTop, ScreenWidth - 2 * Theme.FrameEdge, TaskbarTop - Theme.FrameTop - Theme.FrameEdge);
			}
			window.Maximized = !window.Maximized;
			window.Client.X = target.X;
			window.Client.Y = target.Y;
			Post(window, WmProtocol.Resize, (ulong)target.Width, (ulong)target.Height);
		}

		private static bool Handle(in IpcMessage message, long transferred)
		{
			switch (message.Type)
			{
				case WmProtocol.Connect:
					Accept(in message, transferred);
					return false;

				case WmProtocol.CreateWindow:
					return CreateWindow(Owner(in message), transferred);

				case WmProtocol.ResizeWindow:
					return ResizeWindow(Owner(in message), message.Data[0], transferred);

				case WmProtocol.DestroyWindow:
				{
					WmWindow window = Find(Owner(in message), message.Data[0]);
					if (window != null)
						DestroyWindow(window);
					return window != null;
				}

				case WmProtocol.Present:
					return true;

				case WmProtocol.SetClipboard:
					SetClipboard(transferred, (int)Math.Min(message.Data[1], (ulong)WmProtocol.MaxClipboardBytes));
					return false;

				case WmProtocol.GetClipboard:
					SendClipboard(transferred);
					return false;

				default:
					if (transferred > 0)
						Kernel.Close(transferred);
					return false;
			}
		}

		private static Connection Owner(in IpcMessage message) => message.Data[6] < MaxConnections ? s_connections[(int)message.Data[6]] : null;

		// The clipboard: one text for the whole system, as UTF-8 bytes
		private static byte[] s_clipboard = Array.Empty<byte>();

		private static void SetClipboard(long section, int length)
		{
			if (section <= 0)
				return;
			if (length == 0)
				s_clipboard = Array.Empty<byte>();
			else
			{
				Memory<byte> mapped = SharedMemory.Map(section, length);
				if (mapped.Length == length)
					s_clipboard = mapped.ToArray();
			}
			Kernel.Close(section);
		}

		// Answers on the port the request carried, with the text in a fresh section the asker closes
		private static void SendClipboard(long port)
		{
			if (port <= 0)
				return;
			IpcMessage answer = default;
			answer.Type = WmProtocol.ClipboardText;
			answer.Data[1] = (ulong)s_clipboard.Length;
			long section = 0;
			if (s_clipboard.Length > 0)
			{
				section = SharedMemory.Create((ulong)s_clipboard.Length);
				Memory<byte> mapped = section > 0 ? SharedMemory.Map(section, s_clipboard.Length) : Memory<byte>.Empty;
				if (mapped.Length == s_clipboard.Length)
					new ReadOnlySpan<byte>(s_clipboard).CopyTo(mapped.Span);
				else
					answer.Data[1] = 0;
			}
			Port.Send(port, answer, section > 0 ? section : 0);
			if (section > 0)
				Kernel.Close(section);
			Kernel.Close(port);
		}

		// The app's reply port arrives with Connect, and its process id lets dead apps be noticed
		private static void Accept(in IpcMessage message, long reply)
		{
			IpcMessage answer = default;
			answer.Type = WmProtocol.Connected;
			answer.Data[0] = ulong.MaxValue;
			answer.Data[1] = (ulong)s_screen.Width;
			answer.Data[2] = (ulong)TaskbarTop;
			for (int i = 0; i < MaxConnections; i++)
			{
				if (s_connections[i] == null)
				{
					s_connections[i] = new Connection { Reply = reply, Process = Kernel.OpenProcess((int)message.Data[0]) };
					answer.Data[0] = (ulong)i;
					break;
				}
			}
			Port.Send(reply, answer);
		}

		private static bool CreateWindow(Connection owner, long section)
		{
			int slot = Array.IndexOf(s_slots, null);
			if (owner == null || slot < 0 || !Surfaces.TryMapWindow(section, out WindowHeader header, out Surface surface))
			{
				if (section > 0)
					Kernel.Close(section);
				return false;
			}

			WmWindow window = new WmWindow
			{
				Owner = owner,
				Id = s_nextWindow++,
				Section = section,
				Surface = surface,
				Client = new Rect(header.X, header.Y, header.Width, header.Height),
				Style = header.Style,
				Title = Title(in header),
				Icon = Icon(in header),
			};
			window.Restore = window.Client;
			KeepReachable(window);
			s_slots[slot] = window;
			for (int i = s_zcount; i > 0; i--)
				s_zorder[i] = s_zorder[i - 1];
			s_zorder[0] = window;
			s_zcount++;
			Focus(window);
			Post(window, WmProtocol.WindowCreated);
			return true;
		}

		// Swaps in the app's new surface; the app has already let go of the old one
		private static bool ResizeWindow(Connection owner, ulong id, long section)
		{
			WmWindow window = Find(owner, id);
			if (window == null || !Surfaces.TryMapWindow(section, out WindowHeader header, out Surface surface))
			{
				if (section > 0)
					Kernel.Close(section);
				return false;
			}
			Kernel.Close(window.Section);
			window.Section = section;
			window.Surface = surface;
			window.Client.Width = header.Width;
			window.Client.Height = header.Height;
			KeepReachable(window);
			return true;
		}

		private static string Title(in WindowHeader header)
		{
			ReadOnlySpan<byte> title = header.Title;
			int length = title.IndexOf((byte)0);
			return Encoding.ASCII.GetString(length < 0 ? title : title.Slice(0, length));
		}

		// The icon the app sent, or the generic one when it sent none
		private static PixelGlyph Icon(in WindowHeader header)
		{
			ulong bits = 0, moreBits = 0;
			for (int row = 0; row < 4; row++)
			{
				bits |= (ulong)header.IconRows[row] << (16 * row);
				moreBits |= (ulong)header.IconRows[row + 4] << (16 * row);
			}
			return bits == 0 && moreBits == 0 ? Icons.Window : new PixelGlyph(8, 8, bits, moreBits, header.IconColor1, header.IconColor2, header.IconColor3);
		}

		private static void DestroyWindow(WmWindow window)
		{
			int index = ZIndex(window);
			if (index >= 0)
			{
				for (int i = index; i < s_zcount - 1; i++)
					s_zorder[i] = s_zorder[i + 1];
				s_zorder[--s_zcount] = null;
			}
			int slot = Array.IndexOf(s_slots, window);
			if (slot >= 0)
				s_slots[slot] = null;
			if (s_drag == window)
				s_drag = null;
			if (s_pressedWindow == window)
			{
				s_pressedWindow = null;
				s_pressedButton = Button.None;
			}
			if (s_lastTitleClick == window)
				s_lastTitleClick = null;
			if (s_focus == window)
				s_focus = TopmostVisible();
			Kernel.Close(window.Section);
		}

		// Asks the owning app to close (it leaves its message loop), then drops the window
		private static void CloseWindow(WmWindow window)
		{
			Post(window, WmProtocol.Close);
			DestroyWindow(window);
		}

		// Hidden from compositing but kept in the taskbar; the next visible window takes focus
		private static void Minimize(WmWindow window)
		{
			window.Minimized = true;
			if (s_focus == window)
				s_focus = TopmostVisible();
		}

		// Reclaims windows and connections whose process has exited, cleanly or by crashing
		private static bool ReapDeadConnections()
		{
			bool changed = false;
			for (int i = 0; i < MaxConnections; i++)
			{
				Connection connection = s_connections[i];
				if (connection == null || (connection.Process > 0 && Kernel.Wait(connection.Process, 0) < 0))
					continue;
				for (int slot = 0; slot < MaxWindows; slot++)
				{
					if (s_slots[slot] != null && s_slots[slot].Owner == connection)
						DestroyWindow(s_slots[slot]);
				}
				if (connection.Process > 0)
					Kernel.Close(connection.Process);
				Kernel.Close(connection.Reply);
				s_connections[i] = null;
				changed = true;
			}
			return changed;
		}
	}
}
