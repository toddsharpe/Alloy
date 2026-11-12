using System;
using Alloy.Abi;
using Alloy.Graphics;
using Alloy.Sdk.UI;
using Alloy.Sdk.Web.Dom;
using Alloy.Sdk.Web.Html;
using Alloy.Sdk.Web.Layout;

namespace Alloy.Sdk.Web.Render
{
	// A control showing a page: lays it out for its bounds, scrolls with the keys and wheel, reports the links under the mouse, and runs its forms
	public sealed class PageControl : Control
	{
		private const int LineStep = 40;
		private const int MaxFieldLength = 4096;

		private readonly SdkFontProvider _fonts = new SdkFontProvider();
		private int _laidOutWidth;
		private int _laidOutHeight;
		private int _pressX = -1;
		private int _pressY = -1;
		private Element _hovered;
		private Element _hoverElement;

		public Page Page { get; private set; }
		public int ScrollY;

		// The text field being typed into, if any
		public Element FocusedField { get; private set; }

		// A link was clicked, the mouse moved onto a link (null when it left one), or a form was submitted
		public Action<Uri> LinkClicked;
		public Action<Uri> LinkHovered;
		public Action<FormSubmission> FormSubmitted;
		// Scripts asked to move through history, showed an alert, or wrote to the console
		public Action<int> HistoryRequested;
		public Action<string> Alerted;
		public Action<string, string> ConsoleOutput;
		private string _focusValue;

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
			_hoverElement = null;
			FocusedField = null;
			Attach(page.Scripts);
		}

		// Hooks the page's scripts to this control: layout for their measurements, scrolling, focus, navigation and the status line
		private void Attach(ScriptHost scripts)
		{
			if (scripts == null)
				return;
			scripts.BoxFor = Page.BoxFor;
			scripts.EnsureLayout = EnsureLayout;
			scripts.ScrollY = () => ScrollY;
			scripts.ScrollTo = y =>
			{
				ScrollY = y;
				ClampScroll();
			};
			scripts.Focus = element =>
			{
				FocusedField = Forms.IsTextField(element) ? element : null;
				_focusValue = element == null ? null : Forms.ValueOf(element);
			};
			scripts.Navigate = submission => FormSubmitted?.Invoke(submission);
			scripts.HistoryMove = delta => HistoryRequested?.Invoke(delta);
			scripts.Alert = text => Alerted?.Invoke(text);
			scripts.Console = (level, text) => ConsoleOutput?.Invoke(level, text);
			scripts.ViewportWidth = Bounds.Width;
			scripts.ViewportHeight = Bounds.Height;
		}

		public void SetBounds(Rectangle bounds)
		{
			Bounds = bounds;
			_laidOutWidth = 0;
			if (Page?.Scripts != null)
			{
				Page.Scripts.ViewportWidth = bounds.Width;
				Page.Scripts.ViewportHeight = bounds.Height;
				Page.Scripts.DispatchSimple("resize", Page.Scripts.Interpreter.Global, false, false);
			}
		}

		public void Blur() => LeaveField();

		private void EnterField(Element field)
		{
			FocusedField = field;
			_focusValue = Forms.ValueOf(field);
			Page.Scripts?.SetActiveElement(field);
			Touch();
		}

		// Leaves the focused field, with a change event when its value moved
		private void LeaveField()
		{
			Element field = FocusedField;
			FocusedField = null;
			if (field == null || Page?.Scripts == null)
				return;
			if (_focusValue != Forms.ValueOf(field))
				Page.Scripts.DispatchSimple("change", field, true, false);
			Page.Scripts.SetActiveElement(null);
			Touch();
		}

		// After scripts ran: the page is laid out again if they changed it
		private void Touch()
		{
			if (Page?.Scripts != null && Page.Scripts.Dirty)
				Invalidate();
		}

		// Timers and jobs the page's scripts queued; true when the page needs redrawing
		public bool Tick()
		{
			if (Page?.Scripts == null)
				return false;
			bool changed = Page.Scripts.Pump();
			if (changed)
				Invalidate();
			return changed;
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
			if (Page == null || (_laidOutWidth == Bounds.Width && _laidOutHeight == Bounds.Height && !(Page.Scripts?.Dirty ?? false)))
				return;
			Page.Layout(Bounds.Width, Bounds.Height, _fonts);
			_laidOutWidth = Bounds.Width;
			_laidOutHeight = Bounds.Height;
			ClampScroll();
		}

		// The document changed under the layout (a field edited, a box ticked), so it is laid out again at the next draw
		private void Invalidate() => _laidOutWidth = 0;

		public override void Draw(in Surface surface)
		{
			EnsureLayout();
			surface.FillRectangle(Bounds.X, Bounds.Y, Bounds.Width, Bounds.Height, 0xFFFFFF);
			if (Page?.Display == null)
				return;
			SurfaceRenderer.Draw(in surface, Page.Display, 0, ScrollY, Bounds);
			DrawFocus(in surface);
			DrawScrollbar(in surface);
		}

		// A thin blue ring around the focused field and a caret after its text
		private void DrawFocus(in Surface surface)
		{
			if (FocusedField == null)
				return;
			LayoutBox box = Page.BoxFor(FocusedField);
			if (box == null)
				return;
			int x = Bounds.X + (int)MathF.Round(box.X);
			int y = Bounds.Y + (int)MathF.Round(box.Y) - ScrollY;
			int width = (int)MathF.Round(box.Width);
			int height = (int)MathF.Round(box.Height);
			if (y + height < Bounds.Y || y > Bounds.Y + Bounds.Height)
				return;
			uint ring = 0x1A73E8;
			surface.FillRectangle(x - 1, Math.Max(Bounds.Y, y - 1), width + 2, 1, ring);
			surface.FillRectangle(x - 1, Math.Min(Bounds.Y + Bounds.Height - 1, y + height), width + 2, 1, ring);
			int top = Math.Max(Bounds.Y, y - 1);
			int bottom = Math.Min(Bounds.Y + Bounds.Height, y + height + 1);
			if (bottom > top)
			{
				surface.FillRectangle(x - 1, top, 1, bottom - top, ring);
				surface.FillRectangle(x + width, top, 1, bottom - top, ring);
			}
			float caretX = box.ContentX;
			float caretY = box.ContentY;
			float caretHeight = box.Style.ResolvedLineHeight;
			if (box.Lines != null && box.Lines.Count > 0)
			{
				LineBox last = box.Lines[box.Lines.Count - 1];
				caretY = last.Y;
				caretHeight = last.Height;
				if (last.Fragments.Count > 0 && FocusedField.GetAttribute("value") is { Length: > 0 })
				{
					LineFragment fragment = last.Fragments[last.Fragments.Count - 1];
					caretX = fragment.X + fragment.Width;
				}
			}
			int cx = Bounds.X + (int)MathF.Round(caretX);
			int cy = Bounds.Y + (int)MathF.Round(caretY) - ScrollY;
			if (cy >= Bounds.Y && cy + caretHeight <= Bounds.Y + Bounds.Height && cx < x + width)
				surface.FillRectangle(cx, cy + 1, 1, (int)caretHeight - 2, 0x000000);
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
			Scrolled();
			return true;
		}

		private void Scrolled()
		{
			if (Page?.Scripts == null)
				return;
			Page.Scripts.DispatchSimple("scroll", Page.Document, true, false);
			Touch();
		}

		// Typing into the focused field: characters, Backspace, Enter submits a single-line field's form, Escape and Tab leave it; true when consumed
		public bool OnFieldKey(ushort key, bool shift, bool ctrl)
		{
			if (FocusedField == null || Page == null)
				return false;
			ScriptHost scripts = Page.Scripts;
			bool multiline = FocusedField.TagName == "textarea";
			if (scripts != null && !scripts.DispatchKey("keydown", FocusedField, KeyName(key, shift), KeyCodeName(key), key, shift, ctrl, false))
			{
				Touch();
				return true;
			}
			switch (key)
			{
				case VirtualKey.Escape:
				case VirtualKey.Tab:
					LeaveField();
					return true;
				case VirtualKey.Return:
					if (multiline)
					{
						Append("\n");
						return true;
					}
					if (scripts == null)
					{
						Submit(FocusedField);
						return true;
					}
					if (_focusValue != Forms.ValueOf(FocusedField))
					{
						_focusValue = Forms.ValueOf(FocusedField);
						scripts.DispatchSimple("change", FocusedField, true, false);
					}
					if (FocusedField != null)
						scripts.SubmitForm(FocusedField);
					Touch();
					return true;
				case VirtualKey.Back:
				{
					string value = Forms.ValueOf(FocusedField);
					if (value.Length > 0)
					{
						Forms.SetValue(FocusedField, ctrl ? "" : value.Substring(0, value.Length - 1));
						Invalidate();
						scripts?.DispatchInput(FocusedField, null);
					}
					return true;
				}
				case VirtualKey.Up:
				case VirtualKey.Down:
				case VirtualKey.Left:
				case VirtualKey.Right:
				case VirtualKey.Home:
				case VirtualKey.End:
				case VirtualKey.Prior:
				case VirtualKey.Next:
					return !multiline && (key == VirtualKey.Left || key == VirtualKey.Right || key == VirtualKey.Home || key == VirtualKey.End);
			}
			if (ctrl)
				return false;
			char c = KeyMap.ToChar(key, shift);
			if (c == '\0')
				return false;
			Append(c.ToString());
			return true;
		}

		private void Append(string text)
		{
			string value = Forms.ValueOf(FocusedField);
			if (value.Length >= MaxFieldLength)
				return;
			Forms.SetValue(FocusedField, value + text);
			Invalidate();
			Page.Scripts?.DispatchInput(FocusedField, text);
		}

		// The key and code names scripts expect for a virtual key
		private static string KeyName(ushort key, bool shift)
		{
			switch (key)
			{
				case VirtualKey.Return:
					return "Enter";
				case VirtualKey.Back:
					return "Backspace";
				case VirtualKey.Escape:
					return "Escape";
				case VirtualKey.Tab:
					return "Tab";
				case VirtualKey.Left:
					return "ArrowLeft";
				case VirtualKey.Right:
					return "ArrowRight";
				case VirtualKey.Up:
					return "ArrowUp";
				case VirtualKey.Down:
					return "ArrowDown";
				case VirtualKey.Home:
					return "Home";
				case VirtualKey.End:
					return "End";
				case VirtualKey.Prior:
					return "PageUp";
				case VirtualKey.Next:
					return "PageDown";
				case VirtualKey.Space:
					return " ";
			}
			char c = KeyMap.ToChar(key, shift);
			return c == '\0' ? "Unidentified" : c.ToString();
		}

		private static string KeyCodeName(ushort key)
		{
			if (key >= 'A' && key <= 'Z')
				return "Key" + (char)key;
			if (key >= '0' && key <= '9')
				return "Digit" + (char)key;
			string name = KeyName(key, false);
			return name == " " ? "Space" : name.Length == 1 ? "Unidentified" : name;
		}

		private void Submit(Element control)
		{
			if (Page?.Url == null)
				return;
			FormSubmission submission = Forms.Submit(control, Page.Url);
			if (submission != null)
				FormSubmitted?.Invoke(submission);
		}

		private void ClampScroll()
		{
			int max = Page?.Display == null ? 0 : Math.Max(0, (int)MathF.Ceiling(Page.Display.Height) - Bounds.Height);
			ScrollY = Math.Clamp(ScrollY, 0, max);
		}

		// Wheel scrolls, moves report the link under the mouse, and a press and release on the same spot clicks what is there; true when a redraw is needed
		public bool OnMouse(ushort buttons, int x, int y, int wheel)
		{
			if (Page == null)
				return false;
			if (wheel != 0 && Page.Display != null)
			{
				ScrollY -= wheel * LineStep * 3 / 2;
				ClampScroll();
				Scrolled();
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
					DispatchMouseAt("mousedown", x, y);
				}
				return false;
			}
			int pressX = _pressX;
			int pressY = _pressY;
			_pressX = _pressY = -1;
			if (!Bounds.Contains(x, y))
				return false;
			bool allowed = DispatchMouseAt("mouseup", x, y);
			if (Math.Abs(pressX - x) > 3 || Math.Abs(pressY - y) > 3)
				return !allowed || _laidOutWidth == 0;
			return Click(x - Bounds.X, y - Bounds.Y + ScrollY) || _laidOutWidth == 0;
		}

		// The element under a document point, for the events scripts listen to
		private Element ElementAt(float x, float y)
		{
			LayoutBox hit = Page.Root == null ? null : Painter.HitTest(Page.Root, x, y);
			for (Node node = hit?.Node; node != null; node = node.Parent)
			{
				if (node is Element element)
					return element;
			}
			return Page.Document?.Body ?? Page.Document?.DocumentElement;
		}

		private bool DispatchMouseAt(string type, int x, int y)
		{
			if (Page?.Scripts == null)
				return true;
			float px = x - Bounds.X;
			float py = y - Bounds.Y + ScrollY;
			Element target = ElementAt(px, py);
			if (target == null)
				return true;
			bool allowed = Page.Scripts.DispatchMouse(type, target, px, py);
			Touch();
			return allowed;
		}

		// A click goes to the page's scripts first, then lands on a control, then on a link
		private bool Click(float x, float y)
		{
			ScriptHost scripts = Page.Scripts;
			if (scripts != null)
			{
				Element target = ElementAt(x, y);
				if (target != null && !scripts.DispatchMouse("click", target, x, y))
				{
					Touch();
					return true;
				}
			}
			Element control = Page.ControlAt(x, y);
			if (control != null)
			{
				if (Forms.IsTextField(control))
				{
					if (FocusedField != control)
					{
						LeaveField();
						EnterField(control);
					}
					return true;
				}
				LeaveField();
				if (Forms.IsCheckable(control))
				{
					Forms.Toggle(control);
					Invalidate();
					scripts?.DispatchInput(control, null);
					scripts?.DispatchSimple("change", control, true, false);
					return true;
				}
				if (control.TagName == "select")
				{
					Forms.Advance(control);
					Invalidate();
					scripts?.DispatchInput(control, null);
					scripts?.DispatchSimple("change", control, true, false);
					return true;
				}
				if (Forms.IsSubmitButton(control))
				{
					if (scripts != null)
						scripts.SubmitForm(control);
					else
						Submit(control);
					Touch();
					return true;
				}
				return false;
			}
			bool hadFocus = FocusedField != null;
			LeaveField();
			Element link = Page.LinkAt(x, y);
			if (link == null)
				return hadFocus;
			string href = link.GetAttribute("href") ?? "";
			if (scripts != null && href.StartsWith("#", StringComparison.Ordinal))
			{
				scripts.PerformClickDefault(link);
				Touch();
				return true;
			}
			Uri destination = Page.Resolve(href);
			if (destination != null)
				LinkClicked?.Invoke(destination);
			return hadFocus;
		}

		private Element LinkAt(int x, int y) => Bounds.Contains(x, y) ? Page.LinkAt(x - Bounds.X, y - Bounds.Y + ScrollY) : null;

		private void UpdateHover(int x, int y)
		{
			Element link = LinkAt(x, y);
			if (link != _hovered)
			{
				_hovered = link;
				LinkHovered?.Invoke(link == null ? null : Page.Resolve(link.GetAttribute("href")));
			}
			// The element under the pointer gets mouseover and mouseout, and the :hover chain follows it
			float px = x - Bounds.X;
			float py = y - Bounds.Y + ScrollY;
			Element at = Bounds.Contains(x, y) && Page.Root != null ? ElementAt(px, py) : null;
			if (at == _hoverElement)
				return;
			Element previous = _hoverElement;
			_hoverElement = at;
			if (Page.Scripts != null)
			{
				if (previous != null)
					Page.Scripts.DispatchMouse("mouseout", previous, px, py);
				if (at != null)
					Page.Scripts.DispatchMouse("mouseover", at, px, py);
				Touch();
			}
			if (Page.SetHover(at))
				Invalidate();
		}
	}
}
