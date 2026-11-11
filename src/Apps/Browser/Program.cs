using System;
using System.Collections.Generic;
using System.Threading;
using Alloy.Abi;
using Alloy.Graphics;
using Alloy.Sdk.UI;
using Alloy.Sdk.Web;
using Alloy.Sdk.Web.Render;

namespace Alloy.Apps.Browser
{
	// The address bar: typed text with a caret, Enter to go
	internal sealed class AddressBar : Control
	{
		private const int MaxLength = 512;

		public string Text = "";
		public bool Focused;
		public Action<string> Submit;

		public AddressBar(Rectangle bounds) : base(bounds)
		{
		}

		public override void Draw(in Surface surface)
		{
			surface.DrawFace(Bounds.X, Bounds.Y, Bounds.Width, Bounds.Height, Focused ? Theme.Sunken : Theme.Raised);
			int visible = Math.Max(1, (Bounds.Width - 16) / Theme.Glyph);
			string shown = Text.Length > visible ? Text.Substring(Text.Length - visible) : Text;
			int y = Bounds.Y + (Bounds.Height - Theme.Glyph) / 2;
			int end = surface.DrawText(Bounds.X + 8, y, shown, Focused ? Theme.Bone : Theme.Text, Theme.TextScale);
			if (Focused)
				surface.FillRectangle(end + 1, y, 2, Theme.Glyph, Theme.Ember);
		}

		// Typed characters, backspace and Enter; true when the key changed something
		public bool OnKey(ushort key, bool shift, bool ctrl)
		{
			switch (key)
			{
				case VirtualKey.Return:
					Submit?.Invoke(Text);
					return true;
				case VirtualKey.Back:
					if (ctrl)
						Text = "";
					else if (Text.Length > 0)
						Text = Text.Substring(0, Text.Length - 1);
					return true;
				default:
					char c = KeyMap.ToChar(key, shift);
					if (c == '\0' || Text.Length >= MaxLength)
						return false;
					Text += c;
					return true;
			}
		}
	}

	// US keyboard virtual keys to the characters a URL needs
	internal static class KeyMap
	{
		public static char ToChar(ushort key, bool shift)
		{
			if (key >= 'A' && key <= 'Z')
				return shift ? (char)key : (char)(key - 'A' + 'a');
			if (key >= '0' && key <= '9')
				return shift ? ")!@#$%^&*("[key - '0'] : (char)key;
			if (key >= VirtualKey.Numpad0 && key <= VirtualKey.Numpad0 + 9)
				return (char)('0' + key - VirtualKey.Numpad0);
			switch (key)
			{
				case VirtualKey.Space:
					return ' ';
				case VirtualKey.OemPeriod:
					return shift ? '>' : '.';
				case VirtualKey.OemComma:
					return shift ? '<' : ',';
				case VirtualKey.OemMinus:
					return shift ? '_' : '-';
				case VirtualKey.OemPlus:
					return shift ? '+' : '=';
				case VirtualKey.OemSlash:
					return shift ? '?' : '/';
				case VirtualKey.OemSemicolon:
					return shift ? ':' : ';';
				case VirtualKey.OemTilde:
					return shift ? '~' : '`';
				case VirtualKey.OemOpenBracket:
					return shift ? '{' : '[';
				case VirtualKey.OemCloseBracket:
					return shift ? '}' : ']';
				case VirtualKey.OemBackslash:
					return shift ? '|' : '\\';
				case VirtualKey.OemQuote:
					return shift ? '"' : '\'';
				case VirtualKey.Decimal:
					return '.';
				case VirtualKey.Divide:
					return '/';
				default:
					return '\0';
			}
		}
	}

	// The browser window: toolbar with back, forward, reload and the address bar, the page, and a status line; pages load on their own thread
	internal static class Program
	{
		private const int ToolbarHeight = 44;
		private const int StatusHeight = 24;
		private const string Home = "http://info.cern.ch";

		private static Gui s_gui;
		private static Button s_back;
		private static Button s_forward;
		private static Button s_reload;
		private static AddressBar s_address;
		private static PageControl s_page;
		private static Label s_status;

		private static readonly List<string> s_history = new List<string>();
		private static int s_historyIndex = -1;
		private static bool s_shift;
		private static bool s_ctrl;
		private static bool s_alt;

		// The load in flight and its result, handed from the fetch thread to the UI thread on the next tick
		private static int s_loadSequence;
		private static string s_loadingUrl;
		private static Page s_loaded;
		private static int s_loadedSequence;
		private static bool s_loadedPush;
		private static string s_pendingUrl;
		private static bool s_pendingPush;

		private static int Main(string[] args)
		{
			string start = args.Length > 0 ? Address.Normalize(args[0]) ?? Home : Home;
			s_gui = Gui.Create("Browser", 24, 24 + Theme.FrameTop, 1200, 820, WmProtocol.StyleResizable, Icons.Globe);
			if (s_gui == null)
				return 1;
			s_gui.Background = Theme.Iron;
			s_back = new Button("<", default) { Click = _ => Back() };
			s_forward = new Button(">", default) { Click = _ => Forward() };
			s_reload = new Button("Reload", default) { Click = _ => Reload() };
			s_address = new AddressBar(default) { Submit = Go };
			s_page = new PageControl(default) { LinkClicked = target => Navigate(target.ToString(), true), LinkHovered = ShowLink };
			s_status = new Label("", default, 1) { Foreground = Theme.Hint, Background = Theme.Iron };
			Arrange();
			s_gui.Add(s_page);
			s_gui.Add(s_back);
			s_gui.Add(s_forward);
			s_gui.Add(s_reload);
			s_gui.Add(s_address);
			s_gui.Add(s_status);
			s_gui.OnEvent = OnEvent;
			s_gui.OnTick = OnTick;
			Navigate(start, true);
			s_gui.Run();
			return 0;
		}

		private static void Arrange()
		{
			int width = s_gui.Width;
			int height = s_gui.Height;
			int y = (ToolbarHeight - 30) / 2;
			s_back.Bounds = new Rectangle(8, y, 34, 30);
			s_forward.Bounds = new Rectangle(46, y, 34, 30);
			s_reload.Bounds = new Rectangle(84, y, 110, 30);
			s_address.Bounds = new Rectangle(202, y, Math.Max(100, width - 210), 30);
			s_page.SetBounds(new Rectangle(0, ToolbarHeight, width, Math.Max(0, height - ToolbarHeight - StatusHeight)));
			s_status.Bounds = new Rectangle(0, height - StatusHeight, width, StatusHeight);
		}

		private static void Go(string text)
		{
			string url = Address.Normalize(text);
			if (url == null)
			{
				Status("Not a URL: " + text);
				return;
			}
			s_address.Focused = false;
			Navigate(url, true);
		}

		// Starts loading a URL on a worker thread; a load already running finishes first and the newest request follows it
		private static void Navigate(string url, bool push)
		{
			if (s_loadingUrl != null)
			{
				s_pendingUrl = url;
				s_pendingPush = push;
				return;
			}
			s_loadingUrl = url;
			int sequence = ++s_loadSequence;
			Status("Loading " + url + " ...");
			Thread worker = new Thread(() =>
			{
				Page page = Page.Load(url);
				lock (s_history)
				{
					if (sequence == s_loadSequence)
					{
						s_loaded = page;
						s_loadedSequence = sequence;
						s_loadedPush = push;
					}
				}
			});
			worker.IsBackground = true;
			worker.Start();
		}

		// Every tick: a finished load becomes the shown page, and any request made meanwhile starts
		private static bool OnTick(Gui gui)
		{
			Page page;
			bool push;
			lock (s_history)
			{
				page = s_loaded;
				push = s_loadedPush;
				s_loaded = null;
			}
			if (page == null)
				return false;
			s_loadingUrl = null;
			s_page.Show(page);
			string shown = Address.Display(page.Url) is { Length: > 0 } display ? display : s_history.Count > 0 ? s_history[s_historyIndex] : "";
			if (push)
			{
				if (s_historyIndex < s_history.Count - 1)
					s_history.RemoveRange(s_historyIndex + 1, s_history.Count - s_historyIndex - 1);
				s_history.Add(shown);
				s_historyIndex = s_history.Count - 1;
			}
			if (!s_address.Focused)
				s_address.Text = shown;
			Status(page.Error != null ? page.Error : $"{page.Title}  ({page.Status}, {page.StylesheetUrls.Count} stylesheets, fetched in {page.FetchMs} ms)");
			if (s_pendingUrl != null)
			{
				string next = s_pendingUrl;
				bool nextPush = s_pendingPush;
				s_pendingUrl = null;
				Navigate(next, nextPush);
			}
			return true;
		}

		private static void Back()
		{
			if (s_historyIndex <= 0)
				return;
			s_historyIndex--;
			Navigate(s_history[s_historyIndex], false);
		}

		private static void Forward()
		{
			if (s_historyIndex >= s_history.Count - 1)
				return;
			s_historyIndex++;
			Navigate(s_history[s_historyIndex], false);
		}

		private static void Reload()
		{
			if (s_historyIndex >= 0)
				Navigate(s_history[s_historyIndex], false);
		}

		private static void ShowLink(Uri link)
		{
			if (link != null)
				Status(link.ToString());
			else if (s_page.Page != null && s_loadingUrl == null)
				Status(s_page.Page.Error ?? s_page.Title);
			s_gui.Draw();
		}

		private static void Status(string text)
		{
			int visible = Math.Max(1, (s_gui.Width - 10) / 8);
			s_status.Text = text.Length > visible ? text.Substring(0, visible - 3) + "..." : text;
		}

		private static bool OnEvent(Gui gui, in IpcMessage message)
		{
			switch (message.Type)
			{
				case WmProtocol.Resize:
					Arrange();
					return false;
				case WmProtocol.KeyDown:
				case WmProtocol.KeyUp:
					return OnKey((ushort)message.Data[1], message.Type == WmProtocol.KeyDown);
				case WmProtocol.Mouse:
				{
					ushort buttons = (ushort)message.Data[1];
					int x = (int)(long)message.Data[2];
					int y = (int)(long)message.Data[3];
					int wheel = (int)(long)message.Data[4];
					if ((buttons & InputEvent.LeftButton) != 0)
					{
						bool onAddress = s_address.Bounds.Contains(x, y);
						if (onAddress != s_address.Focused && (onAddress || s_page.Bounds.Contains(x, y)))
							s_address.Focused = onAddress;
					}
					return s_page.OnMouse(buttons, x, y, wheel);
				}
				default:
					return false;
			}
		}

		private static bool OnKey(ushort key, bool pressed)
		{
			switch (key)
			{
				case VirtualKey.Shift:
				case VirtualKey.LeftShift:
				case VirtualKey.RightShift:
					s_shift = pressed;
					return false;
				case VirtualKey.Control:
					s_ctrl = pressed;
					return false;
				case VirtualKey.Menu:
					s_alt = pressed;
					return false;
			}
			if (!pressed)
				return false;
			if (s_ctrl && key == 'L')
			{
				s_address.Focused = true;
				s_address.Text = "";
				return true;
			}
			if (key == VirtualKey.F1 + 4)
			{
				Reload();
				return true;
			}
			if (s_alt && key == VirtualKey.Left)
			{
				Back();
				return true;
			}
			if (s_alt && key == VirtualKey.Right)
			{
				Forward();
				return true;
			}
			if (key == VirtualKey.Escape)
			{
				bool changed = s_address.Focused;
				s_address.Focused = false;
				return changed;
			}
			if (s_address.Focused)
				return s_address.OnKey(key, s_shift, s_ctrl);
			if (key == VirtualKey.Back)
			{
				Back();
				return true;
			}
			return s_page.OnKey(key);
		}
	}
}
