using Alloy.Abi;

namespace Alloy.Sdk.UI
{
	// US keyboard virtual keys to characters, for text fields
	public static class KeyMap
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
				case VirtualKey.Multiply:
					return '*';
				case VirtualKey.Add:
					return '+';
				case VirtualKey.Subtract:
					return '-';
				default:
					return '\0';
			}
		}
	}
}
