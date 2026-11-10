using System;
using Alloy.Abi;
using Alloy.Graphics;
using Alloy.Sdk;
using Alloy.Sdk.UI;

namespace Alloy.Apps.Calc
{
	// The number on a dark recessed panel, in big molten digits over unlit ones, with the sum so far on a small line above
	internal sealed class Readout : Control
	{
		public Readout(Rectangle bounds) : base(bounds)
		{
		}

		public override void Draw(in Surface surface)
		{
			surface.FillRectangle(Bounds.X, Bounds.Y, Bounds.Width, Bounds.Height, Theme.Well);
			surface.DrawOutline(Bounds.X, Bounds.Y, Bounds.Width, Bounds.Height, Theme.Outline);
			surface.DrawBevel(Bounds.X + 1, Bounds.Y + 1, Bounds.Width - 2, Bounds.Height - 2, 0x000000, 0x2C2F34);
			int right = Bounds.X + Bounds.Width - 10;
			string history = Program.History;
			surface.DrawText(right - Surface.TextWidth(history.Length, 1), Bounds.Y + 12, history, Theme.Hint, 1);
			int digitsY = Bounds.Y + Bounds.Height - 14 - 32;
			const string Unlit = "888888888";
			surface.DrawText(right - Surface.TextWidth(Unlit.Length, 4), digitsY, Unlit, 0x1D100A, 4);
			string value = Program.Value;
			surface.DrawText(right - Surface.TextWidth(value.Length, 4), digitsY, value, Theme.Molten, 4);
		}
	}

	// MetalOS's calc.exe: a four-function calculator with a keypad and the keyboard
	internal static class Program
	{
		// The display fits 9 characters, which also keeps every product inside a long
		private const long MaxValue = 999999999;
		private const long MinValue = -99999999;
		private const int Padding = 14;
		private const int KeyWidth = 72;
		private const int KeyHeight = 56;
		private const int KeyGap = 8;
		private const int ReadoutHeight = 92;
		private const int ClientWidth = 2 * Padding + 4 * KeyWidth + 3 * KeyGap;
		private const int ClientHeight = 2 * Padding + ReadoutHeight + 12 + 4 * KeyHeight + 3 * KeyGap;

		private static long s_total;
		private static long s_entry;
		private static bool s_typing;
		private static char s_pending = '=';
		private static Button[] s_keys;

		public static string Value { get; private set; } = "0";

		public static string History { get; private set; } = "";

		private static int Main()
		{
			// Each calculator opens a step down and right of the last
			int position = 25 + 50 * (Kernel.ProcessId % 8);
			Gui gui = Gui.Create("Calc", position, position + Theme.FrameTop, ClientWidth, ClientHeight, WmProtocol.StyleFramed, Icons.Calc);
			if (gui == null)
				return 1;
			s_keys = new Button[]
			{
				Key("7", 0, 0), Key("8", 1, 0), Key("9", 2, 0), Key("/", 3, 0, ButtonStyle.Operator),
				Key("4", 0, 1), Key("5", 1, 1), Key("6", 2, 1), Key("*", 3, 1, ButtonStyle.Operator),
				Key("1", 0, 2), Key("2", 1, 2), Key("3", 2, 2), Key("-", 3, 2, ButtonStyle.Operator),
				Key("C", 0, 3, ButtonStyle.Danger), Key("0", 1, 3), Key("=", 2, 3, ButtonStyle.Primary), Key("+", 3, 3, ButtonStyle.Operator),
			};
			foreach (Button key in s_keys)
			{
				key.Click = button => Press(button.Text[0]);
				gui.Add(key);
			}
			gui.Add(new Readout(new Rectangle(Padding, Padding, ClientWidth - 2 * Padding, ReadoutHeight)));
			gui.OnEvent = OnEvent;
			gui.Run();
			return 0;
		}

		private static Button Key(string text, int column, int row, ButtonStyle style = ButtonStyle.Normal) =>
			new Button(text, new Rectangle(Padding + (KeyWidth + KeyGap) * column, Padding + ReadoutHeight + 12 + (KeyHeight + KeyGap) * row, KeyWidth, KeyHeight), style);

		private static void Reset()
		{
			s_total = 0;
			s_entry = 0;
			s_typing = false;
			s_pending = '=';
			History = "";
		}

		// Folds the typed number into the total with the pending operator, failing on a divide by zero or a result too long to show
		private static bool Apply()
		{
			switch (s_pending)
			{
				case '+':
					s_total += s_entry;
					break;
				case '-':
					s_total -= s_entry;
					break;
				case '*':
					s_total *= s_entry;
					break;
				case '/':
					if (s_entry == 0)
						return false;
					s_total /= s_entry;
					break;
				default:
					s_total = s_entry;
					break;
			}
			return s_total >= MinValue && s_total <= MaxValue;
		}

		// A key from the keypad or keyboard: a digit, an operator, '=', 'C' (clear) or 'B' (backspace)
		private static void Press(char key)
		{
			if (key >= '0' && key <= '9')
			{
				if (!s_typing)
				{
					s_entry = 0;
					s_typing = true;
				}
				if (s_entry <= MaxValue / 10)
					s_entry = s_entry * 10 + (key - '0');
				Value = s_entry.ToString();
				return;
			}
			if (key == 'C')
			{
				Reset();
				Value = "0";
				return;
			}
			if (key == 'B')
			{
				if (s_typing)
				{
					s_entry /= 10;
					Value = s_entry.ToString();
				}
				return;
			}

			// An operator or = finishes the number being typed, and an operator with nothing typed replaces the pending one
			long before = s_total;
			char pending = s_pending;
			long entry = s_entry;
			bool typed = s_typing;
			if (s_typing && !Apply())
			{
				Reset();
				Value = "Error";
				return;
			}
			s_typing = false;
			if (key == '=')
			{
				History = typed && pending != '=' ? $"{before} {pending} {entry} =" : "";
				s_pending = '=';
			}
			else
			{
				s_pending = key;
				History = $"{s_total} {key}";
			}
			Value = s_total.ToString();
		}

		// The calculator key a keyboard key stands for, or 0
		private static char KeyFor(ushort key)
		{
			if (key >= '0' && key <= '9')
				return (char)key;
			if (key >= VirtualKey.Numpad0 && key <= VirtualKey.Numpad0 + 9)
				return (char)('0' + key - VirtualKey.Numpad0);
			switch (key)
			{
				case VirtualKey.Add:
				case VirtualKey.OemPlus:
					return '+';
				case VirtualKey.Subtract:
				case VirtualKey.OemMinus:
					return '-';
				case VirtualKey.Multiply:
					return '*';
				case VirtualKey.Divide:
				case VirtualKey.OemSlash:
					return '/';
				case VirtualKey.Return:
					return '=';
				case VirtualKey.Escape:
					return 'C';
				case VirtualKey.Back:
					return 'B';
				default:
					return '\0';
			}
		}

		// A typed key works its keypad button, which shows pressed while the key is held
		private static bool OnEvent(Gui gui, in IpcMessage message)
		{
			if (message.Type != WmProtocol.KeyDown && message.Type != WmProtocol.KeyUp)
				return false;
			bool pressed = message.Type == WmProtocol.KeyDown;
			char key = KeyFor((ushort)message.Data[1]);
			if (key == '\0')
				return true;
			foreach (Button button in s_keys)
			{
				if (button.Text[0] == key)
					button.Pressed = pressed;
			}
			if (pressed)
				Press(key);
			return true;
		}
	}
}
