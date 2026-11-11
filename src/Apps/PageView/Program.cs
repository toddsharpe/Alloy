using System;
using Alloy.Abi;
using Alloy.Graphics;
using Alloy.Sdk;
using Alloy.Sdk.UI;
using Alloy.Sdk.Web;
using Alloy.Sdk.Web.Html;
using Alloy.Sdk.Web.Render;

namespace Alloy.Apps.PageView
{
	// A page rendered into a window: scroll with the keys, click links to follow them
	internal sealed class PageControl : Control
	{
		private readonly SdkFontProvider _fonts = new SdkFontProvider();
		private Page _page;
		private int _scrollY;
		private int _laidOutWidth;
		private int _laidOutHeight;
		private int _pressX = -1;
		private int _pressY = -1;

		public Gui Gui;

		public PageControl(Rectangle bounds) : base(bounds)
		{
		}

		public void Open(string url)
		{
			_page = Page.Load(url);
			_scrollY = 0;
			_laidOutWidth = 0;
			Console.WriteLine($"view: {url}: status {_page.Status}, {_page.StylesheetUrls.Count} linked stylesheets, fetched in {_page.FetchMs} ms{(_page.Error != null ? ", " + _page.Error : "")}");
		}

		private void EnsureLayout()
		{
			if (_page == null || (_laidOutWidth == Bounds.Width && _laidOutHeight == Bounds.Height))
				return;
			_page.Layout(Bounds.Width, Bounds.Height, _fonts);
			_laidOutWidth = Bounds.Width;
			_laidOutHeight = Bounds.Height;
			Console.WriteLine($"view: laid out {CountBoxes(_page.Root)} boxes, {_page.Display.Items.Count} display items, document {_page.Display.Height}px tall in {_page.LayoutMs} ms");
			ClampScroll();
		}

		private static int CountBoxes(Alloy.Sdk.Web.Layout.LayoutBox box)
		{
			int count = 1;
			foreach (Alloy.Sdk.Web.Layout.LayoutBox child in box.Children)
				count += CountBoxes(child);
			return count;
		}

		public override void Draw(in Surface surface)
		{
			EnsureLayout();
			surface.FillRectangle(Bounds.X, Bounds.Y, Bounds.Width, Bounds.Height, 0xFFFFFF);
			if (_page?.Display == null)
				return;
			SurfaceRenderer.Draw(in surface, _page.Display, 0, _scrollY, Bounds);
			DrawScrollbar(in surface);
		}

		private void DrawScrollbar(in Surface surface)
		{
			float total = _page.Display.Height;
			if (total <= Bounds.Height)
				return;
			int track = Bounds.Height;
			int thumb = Math.Max(20, (int)(track * Bounds.Height / total));
			int top = (int)((track - thumb) * (_scrollY / (total - Bounds.Height)));
			surface.FillRectangle(Bounds.X + Bounds.Width - 6, Bounds.Y, 6, track, 0xEEEEEE);
			surface.FillRectangle(Bounds.X + Bounds.Width - 5, Bounds.Y + top, 4, thumb, 0x999999);
		}

		public bool OnKey(ushort key)
		{
			if (_page?.Display == null)
				return false;
			int page = Math.Max(40, Bounds.Height - 40);
			switch (key)
			{
				case VirtualKey.Down:
					_scrollY += 40;
					break;
				case VirtualKey.Up:
					_scrollY -= 40;
					break;
				case VirtualKey.Next:
				case VirtualKey.Space:
					_scrollY += page;
					break;
				case VirtualKey.Prior:
					_scrollY -= page;
					break;
				case VirtualKey.Home:
					_scrollY = 0;
					break;
				case VirtualKey.End:
					_scrollY = int.MaxValue;
					break;
				default:
					return false;
			}
			ClampScroll();
			return true;
		}

		private void ClampScroll()
		{
			int max = _page?.Display == null ? 0 : Math.Max(0, (int)MathF.Ceiling(_page.Display.Height) - Bounds.Height);
			_scrollY = Math.Clamp(_scrollY, 0, max);
		}

		// A press and release on the same spot over a link opens it
		public bool OnMouse(ushort buttons, int x, int y, int wheel)
		{
			if (wheel != 0 && _page?.Display != null)
			{
				_scrollY -= wheel * 60;
				ClampScroll();
				return true;
			}
			bool down = (buttons & InputEvent.LeftButton) != 0;
			if (down)
			{
				_pressX = x;
				_pressY = y;
				return false;
			}
			if (_pressX < 0)
				return false;
			int pressX = _pressX;
			int pressY = _pressY;
			_pressX = _pressY = -1;
			if (Math.Abs(pressX - x) > 3 || Math.Abs(pressY - y) > 3 || !Bounds.Contains(x, y) || _page == null)
				return false;
			Element link = _page.LinkAt(x - Bounds.X, y - Bounds.Y + _scrollY);
			if (link == null)
				return false;
			Uri target = _page.Resolve(link.GetAttribute("href"));
			if (target == null || (target.Scheme != "http" && target.Scheme != "https"))
				return false;
			Open(target.ToString());
			return true;
		}

		public string Title => _page?.Title ?? "";
	}

	internal static class Program
	{
		private static int Main(string[] args)
		{
			if (args.Length < 1)
			{
				Console.WriteLine("usage: view <url>");
				return 1;
			}
			string url = args[0].Contains("://") ? args[0] : "http://" + args[0];
			Gui gui = Gui.Create("View", 40, 40 + Theme.FrameTop, 1024, 720, WmProtocol.StyleResizable, Icons.Window);
			if (gui == null)
				return 1;
			PageControl page = new PageControl(new Rectangle(0, 0, gui.Width, gui.Height)) { Gui = gui };
			gui.Background = 0xFFFFFF;
			gui.Add(page);
			page.Open(url);
			gui.OnEvent = (Gui g, in IpcMessage message) =>
			{
				if (message.Type == WmProtocol.KeyDown)
					return page.OnKey((ushort)message.Data[1]);
				if (message.Type == WmProtocol.Mouse)
					return page.OnMouse((ushort)message.Data[1], (int)(long)message.Data[2], (int)(long)message.Data[3], (int)(long)message.Data[4]);
				if (message.Type == WmProtocol.Resize)
					page.Bounds = new Rectangle(0, 0, g.Width, g.Height);
				return false;
			};
			gui.Run();
			return 0;
		}
	}
}
