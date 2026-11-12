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

		private static readonly List<FormSubmission> s_history = new List<FormSubmission>();
		private static int s_historyIndex = -1;
		private static bool s_shift;
		private static bool s_ctrl;
		private static bool s_alt;

		// The load in flight and its result, handed from the fetch thread to the UI thread on the next tick
		private static int s_loadSequence;
		private static FormSubmission s_loading;
		private static Page s_loaded;
		private static bool s_loadedPush;
		private static FormSubmission s_pending;
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
			s_page = new PageControl(default)
			{
				LinkClicked = target => Navigate(Get(target), true),
				LinkHovered = ShowLink,
				FormSubmitted = submission => Navigate(submission, true),
				HistoryRequested = delta =>
				{
					if (delta < 0)
						Back();
					else
						Forward();
				},
				Alerted = text => Status("Alert: " + text),
				ConsoleOutput = ScriptConsole,
			};
			Page.DefaultFonts = new SdkFontProvider();
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
			Navigate(Get(new Uri(start)), true);
			s_gui.Run();
			return 0;
		}

		private static FormSubmission Get(Uri url) => new FormSubmission { Url = url, Method = "GET" };

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
			Navigate(Get(new Uri(url)), true);
		}

		// Starts loading on a worker thread; a load already running finishes first and the newest request follows it
		private static void Navigate(FormSubmission request, bool push)
		{
			if (s_loading != null)
			{
				s_pending = request;
				s_pendingPush = push;
				return;
			}
			s_loading = request;
			int sequence = ++s_loadSequence;
			Status("Loading " + request + " ...");
			Thread worker = new Thread(() =>
			{
				Page page = Page.Load(request);
				lock (s_history)
				{
					if (sequence == s_loadSequence)
					{
						s_loaded = page;
						s_loadedPush = push;
					}
				}
			}, 16 * 1024 * 1024);
			worker.IsBackground = true;
			worker.Start();
		}

		// Scripts' console lines go to the kernel log; errors show on the status line too
		private static void ScriptConsole(string level, string text)
		{
			Console.WriteLine($"browser: script {level}: {text}");
			if (level == "error")
			{
				int newline = text.IndexOf('\n');
				Status("Script error: " + (newline < 0 ? text : text.Substring(0, newline)));
			}
		}

		// Every tick: the page's timers run, a finished load becomes the shown page, and any request made meanwhile starts
		private static bool OnTick(Gui gui)
		{
			bool changed = s_page.Tick();
			if (s_page.Page?.Scripts is { TitleChanged: true } scripts)
			{
				scripts.TitleChanged = false;
				if (s_loading == null)
					Status(s_page.Title);
				changed = true;
			}
			Page page;
			bool push;
			FormSubmission request;
			lock (s_history)
			{
				page = s_loaded;
				push = s_loadedPush;
				request = s_loading;
				s_loaded = null;
			}
			if (page == null)
				return changed;
			s_loading = null;
			s_page.Show(page);
			string shown = Address.Display(page.Url);
			if (push)
			{
				if (s_historyIndex < s_history.Count - 1)
					s_history.RemoveRange(s_historyIndex + 1, s_history.Count - s_historyIndex - 1);
				// A POST is remembered as what was posted, so Reload asks again; anything else as the page's final URL
				s_history.Add(request.Method == "POST" ? request : Get(page.Url ?? request.Url));
				s_historyIndex = s_history.Count - 1;
			}
			if (!s_address.Focused)
				s_address.Text = shown;
			Status(page.Error != null ? page.Error : $"{page.Title}  ({page.Status}, {page.StylesheetUrls.Count} stylesheets, {page.Scripts?.ScriptsRun ?? 0} scripts, fetched in {page.FetchMs} ms)");
			if (s_pending != null)
			{
				FormSubmission next = s_pending;
				bool nextPush = s_pendingPush;
				s_pending = null;
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
			else if (s_page.Page != null && s_loading == null)
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
						if (onAddress)
							s_page.Blur();
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
				s_page.Blur();
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
			if (s_address.Focused)
			{
				if (key == VirtualKey.Escape)
				{
					s_address.Focused = false;
					return true;
				}
				return s_address.OnKey(key, s_shift, s_ctrl);
			}
			if (s_page.FocusedField != null)
				return s_page.OnFieldKey(key, s_shift, s_ctrl);
			if (key == VirtualKey.Back)
			{
				Back();
				return true;
			}
			return s_page.OnKey(key);
		}
	}
}
