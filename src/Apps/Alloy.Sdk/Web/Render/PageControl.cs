using System;
using Alloy.Abi;
using Alloy.Graphics;
using Alloy.Sdk.UI;
using Alloy.Sdk.Web.Html;
using Alloy.Sdk.Web.Layout;

namespace Alloy.Sdk.Web.Render
{
	// A control showing a page: lays it out for its bounds, scrolls with the keys and wheel, and reports the links under the mouse
	public sealed class PageControl : Control
	{
		private const int LineStep = 40;

		private readonly SdkFontProvider _fonts = new SdkFontProvider();
		private int _laidOutWidth;
		private int _laidOutHeight;
		private int _pressX = -1;
		private int _pressY = -1;
		private Element _hovered;

		public Page Page { get; private set; }
		public int ScrollY;

		// A link was clicked, or the mouse moved onto a link (null when it left one)
		public Action<Uri> LinkClicked;
		public Action<Uri> LinkHovered;

		public PageControl(Rectangle bounds) : base(bounds)
		{
		}

		public string Title => Page?.Title ?? "";

		public void Show(Page page)
		{
			Page = page;
			ScrollY = 0;
			_laidOutWidth = 0;
			_hovered = null;
		}

		public void SetBounds(Rectangle bounds)
		{
			Bounds = bounds;
			_laidOutWidth = 0;
		}

		public int CountBoxes()
		{
			static int Count(LayoutBox box)
			{
				int count = 1;
				foreach (LayoutBox child in box.Children)
					count += Count(child);
				return count;
			}
			return Page?.Root == null ? 0 : Count(Page.Root);
		}

		private void EnsureLayout()
		{
			if (Page == null || (_laidOutWidth == Bounds.Width && _laidOutHeight == Bounds.Height))
				return;
			Page.Layout(Bounds.Width, Bounds.Height, _fonts);
			_laidOutWidth = Bounds.Width;
			_laidOutHeight = Bounds.Height;
			ClampScroll();
		}

		public override void Draw(in Surface surface)
		{
			EnsureLayout();
			surface.FillRectangle(Bounds.X, Bounds.Y, Bounds.Width, Bounds.Height, 0xFFFFFF);
			if (Page?.Display == null)
				return;
			SurfaceRenderer.Draw(in surface, Page.Display, 0, ScrollY, Bounds);
			DrawScrollbar(in surface);
		}

		private void DrawScrollbar(in Surface surface)
		{
			float total = Page.Display.Height;
			if (total <= Bounds.Height)
				return;
			int track = Bounds.Height;
			int thumb = Math.Max(20, (int)(track * Bounds.Height / total));
			int top = (int)((track - thumb) * (ScrollY / (total - Bounds.Height)));
			surface.FillRectangle(Bounds.X + Bounds.Width - 6, Bounds.Y, 6, track, 0xEEEEEE);
			surface.FillRectangle(Bounds.X + Bounds.Width - 5, Bounds.Y + top, 4, thumb, 0x999999);
		}

		// Scrolling keys; true when the key was one
		public bool OnKey(ushort key)
		{
			if (Page?.Display == null)
				return false;
			int page = Math.Max(LineStep, Bounds.Height - LineStep);
			switch (key)
			{
				case VirtualKey.Down:
					ScrollY += LineStep;
					break;
				case VirtualKey.Up:
					ScrollY -= LineStep;
					break;
				case VirtualKey.Next:
				case VirtualKey.Space:
					ScrollY += page;
					break;
				case VirtualKey.Prior:
					ScrollY -= page;
					break;
				case VirtualKey.Home:
					ScrollY = 0;
					break;
				case VirtualKey.End:
					ScrollY = int.MaxValue;
					break;
				default:
					return false;
			}
			ClampScroll();
			return true;
		}

		private void ClampScroll()
		{
			int max = Page?.Display == null ? 0 : Math.Max(0, (int)MathF.Ceiling(Page.Display.Height) - Bounds.Height);
			ScrollY = Math.Clamp(ScrollY, 0, max);
		}

		// Wheel scrolls, moves report the link under the mouse, and a press and release on the same spot follows a link; true when a redraw is needed
		public bool OnMouse(ushort buttons, int x, int y, int wheel)
		{
			if (Page == null)
				return false;
			if (wheel != 0 && Page.Display != null)
			{
				ScrollY -= wheel * LineStep * 3 / 2;
				ClampScroll();
				return true;
			}
			bool down = (buttons & InputEvent.LeftButton) != 0;
			if (!down && _pressX < 0)
			{
				UpdateHover(x, y);
				return false;
			}
			if (down)
			{
				if (_pressX < 0 && Bounds.Contains(x, y))
				{
					_pressX = x;
					_pressY = y;
				}
				return false;
			}
			int pressX = _pressX;
			int pressY = _pressY;
			_pressX = _pressY = -1;
			if (Math.Abs(pressX - x) > 3 || Math.Abs(pressY - y) > 3 || !Bounds.Contains(x, y))
				return false;
			Element link = LinkAt(x, y);
			if (link == null)
				return false;
			Uri target = Page.Resolve(link.GetAttribute("href"));
			if (target != null)
				LinkClicked?.Invoke(target);
			return false;
		}

		private Element LinkAt(int x, int y) => Bounds.Contains(x, y) ? Page.LinkAt(x - Bounds.X, y - Bounds.Y + ScrollY) : null;

		private void UpdateHover(int x, int y)
		{
			Element link = LinkAt(x, y);
			if (link == _hovered)
				return;
			_hovered = link;
			LinkHovered?.Invoke(link == null ? null : Page.Resolve(link.GetAttribute("href")));
		}
	}
}
