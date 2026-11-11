using System;
using Alloy.Abi;
using Alloy.Graphics;
using Alloy.Sdk.UI;
using Alloy.Sdk.Web;
using Alloy.Sdk.Web.Render;

namespace Alloy.Apps.PageView
{
	// view <url>: a page rendered into a window, scrolled with the keys and wheel, links followed by clicking
	internal static class Program
	{
		private static int Main(string[] args)
		{
			if (args.Length < 1)
			{
				Console.WriteLine("usage: view <url>");
				return 1;
			}
			string url = Address.Normalize(args[0]);
			if (url == null)
			{
				Console.WriteLine($"view: not a URL: {args[0]}");
				return 1;
			}
			Gui gui = Gui.Create("View", 40, 40 + Theme.FrameTop, 1024, 720, WmProtocol.StyleResizable, Icons.Window);
			if (gui == null)
				return 1;
			PageControl page = new PageControl(new Rectangle(0, 0, gui.Width, gui.Height));
			gui.Background = 0xFFFFFF;
			gui.Add(page);
			Open(page, url);
			page.LinkClicked = target => Open(page, target.ToString());
			gui.OnEvent = (Gui g, in IpcMessage message) =>
			{
				if (message.Type == WmProtocol.KeyDown)
					return page.OnKey((ushort)message.Data[1]);
				if (message.Type == WmProtocol.Mouse)
					return page.OnMouse((ushort)message.Data[1], (int)(long)message.Data[2], (int)(long)message.Data[3], (int)(long)message.Data[4]);
				if (message.Type == WmProtocol.Resize)
					page.SetBounds(new Rectangle(0, 0, g.Width, g.Height));
				return false;
			};
			gui.Run();
			return 0;
		}

		private static void Open(PageControl control, string url)
		{
			Page page = Page.Load(url);
			control.Show(page);
			Console.WriteLine($"view: {url}: status {page.Status}, {page.StylesheetUrls.Count} linked stylesheets, fetched in {page.FetchMs} ms{(page.Error != null ? ", " + page.Error : "")}");
		}
	}
}
