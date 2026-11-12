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

	// One tab: its history, the load in flight, and the page it shows when it is the current tab
	internal sealed class Tab
	{
		public readonly List<FormSubmission> History = new List<FormSubmission>();
		public int HistoryIndex = -1;
		public Page Page;
		public int ScrollY;
		public string Title = "New tab";
		public int LoadSequence;
		public FormSubmission Loading;
		public Page Loaded;
		public bool LoadedPush;
		public FormSubmission Pending;
		public bool PendingPush;

		public FormSubmission Current => HistoryIndex >= 0 && HistoryIndex < History.Count ? History[HistoryIndex] : null;
	}

	// The strip of tabs above the toolbar: the current one raised, a close mark on it, and a plus at the end for a new tab
	internal sealed class TabBar : Control
	{
		public const int Height = 30;
		public const int PlusWidth = 30;
		public const int CloseWidth = 18;
		public List<Tab> Tabs;
		public Tab Current;

		public TabBar(Rectangle bounds) : base(bounds)
		{
		}

		public int TabWidth => Tabs == null || Tabs.Count == 0 ? 0 : Math.Clamp((Bounds.Width - 12 - PlusWidth) / Tabs.Count, 60, 220);

		public Rectangle TabBounds(int index) => new Rectangle(Bounds.X + 4 + index * TabWidth, Bounds.Y + 4, TabWidth - 2, Height - 4);

		public Rectangle PlusBounds => new Rectangle(Bounds.X + 4 + (Tabs?.Count ?? 0) * TabWidth + 2, Bounds.Y + 4, PlusWidth, Height - 4);

		// The tab under a point, -1 for the plus, or -2 for nothing; closing when the point is on the current tab's close mark
		public int HitTest(int x, int y, out bool close)
		{
			close = false;
			if (!Bounds.Contains(x, y) || Tabs == null)
				return -2;
			if (PlusBounds.Contains(x, y))
				return -1;
			for (int i = 0; i < Tabs.Count; i++)
			{
				Rectangle tab = TabBounds(i);
				if (!tab.Contains(x, y))
					continue;
				close = Tabs[i] == Current && x >= tab.X + tab.Width - CloseWidth;
				return i;
			}
			return -2;
		}

		public override void Draw(in Surface surface)
		{
			surface.FillRectangle(Bounds.X, Bounds.Y, Bounds.Width, Bounds.Height, Theme.IronDeep);
			if (Tabs == null)
				return;
			for (int i = 0; i < Tabs.Count; i++)
			{
				Tab tab = Tabs[i];
				Rectangle bounds = TabBounds(i);
				bool current = tab == Current;
				if (current)
					surface.DrawFace(bounds.X, bounds.Y, bounds.Width, bounds.Height, Theme.Raised);
				else
					surface.FillRectangle(bounds.X, bounds.Y, bounds.Width, bounds.Height, Theme.AccentOff);
				string title = tab.Loading != null && tab.Page == null ? "Loading..." : tab.Title.Length == 0 ? "New tab" : tab.Title;
				int room = Math.Max(1, (bounds.Width - 12 - (current ? CloseWidth : 0)) / 8);
				if (title.Length > room)
					title = room > 3 ? title.Substring(0, room - 3) + "..." : title.Substring(0, room);
				surface.DrawText(bounds.X + 6, bounds.Y + (bounds.Height - 8) / 2, title, current ? Theme.Bone : Theme.TextDim, 1);
				if (current)
					surface.DrawText(bounds.X + bounds.Width - CloseWidth + 5, bounds.Y + (bounds.Height - 8) / 2, "x", Theme.Hint, 1);
			}
			Rectangle plus = PlusBounds;
			surface.DrawFace(plus.X, plus.Y, plus.Width, plus.Height, Theme.Raised);
			surface.DrawText(plus.X + (plus.Width - 8) / 2, plus.Y + (plus.Height - 8) / 2, "+", Theme.Bone, 1);
		}
	}

	// The browser window: tabs, a toolbar with back, forward, reload and the address bar, the page, and a status line; pages load on their own thread
	internal static class Program
	{
		private const int ToolbarHeight = 44;
		private const int StatusHeight = 24;
		private const string Home = "http://info.cern.ch";

		private static Gui s_gui;
		private static TabBar s_tabBar;
		private static Button s_back;
		private static Button s_forward;
		private static Button s_reload;
		private static AddressBar s_address;
		private static PageControl s_page;
		private static Label s_status;

		private static readonly List<Tab> s_tabs = new List<Tab>();
		private static Tab s_tab;
		private static readonly object s_loadLock = new object();
		private static bool s_shift;
		private static bool s_ctrl;
		private static bool s_alt;
		private static bool s_leftDown;

		private static int Main(string[] args)
		{
			string start = args.Length > 0 ? Address.Normalize(args[0]) ?? Home : Home;
			s_gui = Gui.Create("Browser", 24, 24 + Theme.FrameTop, 1200, 820, WmProtocol.StyleResizable, Icons.Globe);
			if (s_gui == null)
				return 1;
			s_gui.Background = Theme.Iron;
			s_tabBar = new TabBar(default) { Tabs = s_tabs };
			s_back = new Button("<", default) { Click = _ => Back() };
			s_forward = new Button(">", default) { Click = _ => Forward() };
			s_reload = new Button("Reload", default) { Click = _ => Reload() };
			s_address = new AddressBar(default) { Submit = Go };
			s_page = new PageControl(default)
			{
				LinkClicked = target =>
				{
					if (s_ctrl)
						NewTab(Get(target), false);
					else
						Navigate(s_tab, Get(target), true);
				},
				LinkHovered = ShowLink,
				FormSubmitted = submission => Navigate(s_tab, submission, true),
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
			s_gui.Add(s_tabBar);
			s_gui.Add(s_back);
			s_gui.Add(s_forward);
			s_gui.Add(s_reload);
			s_gui.Add(s_address);
			s_gui.Add(s_status);
			s_gui.OnEvent = OnEvent;
			s_gui.OnTick = OnTick;
			NewTab(Get(new Uri(start)), true);
			s_gui.Run();
			return 0;
		}

		private static FormSubmission Get(Uri url) => new FormSubmission { Url = url, Method = "GET" };

		private static void Arrange()
		{
			int width = s_gui.Width;
			int height = s_gui.Height;
			s_tabBar.Bounds = new Rectangle(0, 0, width, TabBar.Height);
			int y = TabBar.Height + (ToolbarHeight - 30) / 2;
			s_back.Bounds = new Rectangle(8, y, 34, 30);
			s_forward.Bounds = new Rectangle(46, y, 34, 30);
			s_reload.Bounds = new Rectangle(84, y, 110, 30);
			s_address.Bounds = new Rectangle(202, y, Math.Max(100, width - 210), 30);
			int top = TabBar.Height + ToolbarHeight;
			s_page.SetBounds(new Rectangle(0, top, width, Math.Max(0, height - top - StatusHeight)));
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
			Navigate(s_tab, Get(new Uri(url)), true);
		}

		// Opens a tab, loading the request in it; it becomes the current tab unless it was opened in the background
		private static void NewTab(FormSubmission request, bool select)
		{
			Tab tab = new Tab();
			s_tabs.Add(tab);
			if (select || s_tab == null)
				Select(tab);
			if (request != null)
				Navigate(tab, request, true);
			else if (select)
			{
				s_address.Focused = true;
				s_address.Text = "";
				s_page.Blur();
			}
		}

		// Makes a tab current: the page control shows its page at its scroll position, the bar and the address bar follow
		private static void Select(Tab tab)
		{
			if (tab == s_tab)
				return;
			if (s_tab != null)
				s_tab.ScrollY = s_page.ScrollY;
			s_tab = tab;
			s_tabBar.Current = tab;
			s_page.Show(tab.Page);
			s_page.ScrollY = tab.ScrollY;
			s_address.Focused = false;
			s_address.Text = tab.Page != null ? Address.Display(tab.Page.Url) : tab.Current?.Url?.ToString() ?? "";
			if (tab.Loading != null)
				Status("Loading " + tab.Loading + " ...");
			else if (tab.Page != null)
				Status(tab.Page.Error ?? tab.Title);
			else
				Status("");
		}

		// Closes a tab; the one before it takes over, and the last tab closing leaves a fresh empty one
		private static void CloseTab(Tab tab)
		{
			int index = s_tabs.IndexOf(tab);
			if (index < 0)
				return;
			s_tabs.RemoveAt(index);
			tab.LoadSequence++;
			if (s_tabs.Count == 0)
			{
				s_tab = null;
				NewTab(null, true);
				return;
			}
			if (tab == s_tab)
			{
				s_tab = null;
				Select(s_tabs[Math.Min(index, s_tabs.Count - 1)]);
			}
		}

		private static void CycleTab(int delta)
		{
			if (s_tabs.Count < 2)
				return;
			int index = (s_tabs.IndexOf(s_tab) + delta + s_tabs.Count) % s_tabs.Count;
			Select(s_tabs[index]);
		}

		// Starts loading on a worker thread; a load already running in the tab finishes first and the newest request follows it
		private static void Navigate(Tab tab, FormSubmission request, bool push)
		{
			if (tab.Loading != null)
			{
				tab.Pending = request;
				tab.PendingPush = push;
				return;
			}
			tab.Loading = request;
			int sequence = ++tab.LoadSequence;
			if (tab == s_tab)
				Status("Loading " + request + " ...");
			Thread worker = new Thread(() =>
			{
				Page page = Page.Load(request);
				lock (s_loadLock)
				{
					if (sequence == tab.LoadSequence)
					{
						tab.Loaded = page;
						tab.LoadedPush = push;
					}
				}
			}, 64 * 1024 * 1024);
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

		// Every tick: the current page's timers run, finished loads become their tabs' pages, and requests made meanwhile start
		private static bool OnTick(Gui gui)
		{
			bool changed = s_page.Tick();
			if (s_page.Page?.Scripts is { TitleChanged: true } scripts)
			{
				scripts.TitleChanged = false;
				s_tab.Title = s_page.Title;
				if (s_tab.Loading == null)
					Status(s_page.Title);
				changed = true;
			}
			foreach (Tab tab in s_tabs)
			{
				Page page;
				bool push;
				FormSubmission request;
				lock (s_loadLock)
				{
					page = tab.Loaded;
					push = tab.LoadedPush;
					request = tab.Loading;
					tab.Loaded = null;
				}
				if (page == null)
					continue;
				tab.Loading = null;
				tab.Page = page;
				tab.Title = page.Error != null ? "Error" : page.Title.Length > 0 ? page.Title : Address.Display(page.Url);
				if (push)
				{
					if (tab.HistoryIndex < tab.History.Count - 1)
						tab.History.RemoveRange(tab.HistoryIndex + 1, tab.History.Count - tab.HistoryIndex - 1);
					// A POST is remembered as what was posted, so Reload asks again; anything else as the page's final URL
					tab.History.Add(request.Method == "POST" ? request : Get(page.Url ?? request.Url));
					tab.HistoryIndex = tab.History.Count - 1;
				}
				if (tab == s_tab)
				{
					s_page.Show(page);
					if (!s_address.Focused)
						s_address.Text = Address.Display(page.Url);
					Status(page.Error != null ? page.Error : $"{page.Title}  ({page.Status}, {page.StylesheetUrls.Count} stylesheets, {page.Scripts?.ScriptsRun ?? 0} scripts{(page.Scripts?.ScriptsSkipped > 0 ? $" ({page.Scripts.ScriptsSkipped} skipped)" : "")}, fetched in {page.FetchMs} ms)");
				}
				if (tab.Pending != null)
				{
					FormSubmission next = tab.Pending;
					bool nextPush = tab.PendingPush;
					tab.Pending = null;
					Navigate(tab, next, nextPush);
				}
				changed = true;
			}
			return changed;
		}

		private static void Back()
		{
			if (s_tab.HistoryIndex <= 0)
				return;
			s_tab.HistoryIndex--;
			Navigate(s_tab, s_tab.History[s_tab.HistoryIndex], false);
		}

		private static void Forward()
		{
			if (s_tab.HistoryIndex >= s_tab.History.Count - 1)
				return;
			s_tab.HistoryIndex++;
			Navigate(s_tab, s_tab.History[s_tab.HistoryIndex], false);
		}

		private static void Reload()
		{
			if (s_tab.HistoryIndex >= 0)
				Navigate(s_tab, s_tab.History[s_tab.HistoryIndex], false);
		}

		private static void ShowLink(Uri link)
		{
			if (link != null)
				Status(link.ToString());
			else if (s_page.Page != null && s_tab.Loading == null)
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
					bool left = (buttons & InputEvent.LeftButton) != 0;
					bool released = s_leftDown && !left;
					s_leftDown = left;
					if (released && s_tabBar.Bounds.Contains(x, y))
						return OnTabClick(x, y);
					if (left)
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

		// A release on the tab strip: the plus opens a tab, the current tab's mark closes it, any other tab becomes current
		private static bool OnTabClick(int x, int y)
		{
			int hit = s_tabBar.HitTest(x, y, out bool close);
			if (hit == -2)
				return false;
			if (hit == -1)
				NewTab(null, true);
			else if (close)
				CloseTab(s_tabs[hit]);
			else
				Select(s_tabs[hit]);
			return true;
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
			if (s_ctrl && key == 'T')
			{
				NewTab(null, true);
				return true;
			}
			if (s_ctrl && key == 'W')
			{
				CloseTab(s_tab);
				return true;
			}
			if (s_ctrl && key == VirtualKey.Tab)
			{
				CycleTab(s_shift ? -1 : 1);
				return true;
			}
			if (s_ctrl && key >= '1' && key <= '9')
			{
				int index = key - '1';
				if (index < s_tabs.Count)
					Select(s_tabs[index]);
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
