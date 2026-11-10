namespace Alloy.Abi
{
	public enum InputEventType : ushort
	{
		None = 0,
		Key = 1,
		Mouse = 2,
	}

	// One record in the kernel -> window server input ring (MetalOS's Message for KeyEvent and MouseEvent)
	public struct InputEvent
	{
		public InputEventType Type;
		public ushort Key;
		public ushort Pressed;
		public ushort Buttons;
		public ushort X;
		public ushort Y;
		public short Wheel;
		public ushort Reserved;

		public const ushort LeftButton = 1;
		public const ushort RightButton = 2;

		// The synthetic mouse reports absolute HID positions from 0 to MaxPosition across the screen
		public const int MaxPosition = 32767;
	}

	// The shared ring the kernel's input drivers fill and the window server drains; free-running counters, one producer, one consumer
	public struct InputRing
	{
		public const string Endpoint = "input";
		public const string ReadyEndpoint = "input.ready";
		public const uint Capacity = 512;
		public const int HeaderSize = 64;
		public const ulong Size = HeaderSize + Capacity * 16;

		public volatile uint Write;
		public volatile uint Read;

		public static unsafe InputEvent* At(InputRing* ring, uint counter) => (InputEvent*)((byte*)ring + HeaderSize) + counter % Capacity;
	}

	// Windows virtual-key codes, as MetalOS.Keys.h has them; letters and digits are their ASCII capitals
	public static class VirtualKey
	{
		public const ushort None = 0;
		public const ushort Back = 0x08;
		public const ushort Tab = 0x09;
		public const ushort Return = 0x0D;
		public const ushort Shift = 0x10;
		public const ushort Control = 0x11;
		public const ushort Menu = 0x12;
		public const ushort Capital = 0x14;
		public const ushort Escape = 0x1B;
		public const ushort Space = 0x20;
		public const ushort Prior = 0x21;
		public const ushort Next = 0x22;
		public const ushort End = 0x23;
		public const ushort Home = 0x24;
		public const ushort Left = 0x25;
		public const ushort Up = 0x26;
		public const ushort Right = 0x27;
		public const ushort Down = 0x28;
		public const ushort Insert = 0x2D;
		public const ushort Delete = 0x2E;
		public const ushort LeftWindows = 0x5B;
		public const ushort Numpad0 = 0x60;
		public const ushort Numpad1 = 0x61;
		public const ushort Numpad3 = 0x63;
		public const ushort Numpad5 = 0x65;
		public const ushort Multiply = 0x6A;
		public const ushort Add = 0x6B;
		public const ushort Subtract = 0x6D;
		public const ushort Decimal = 0x6E;
		public const ushort Divide = 0x6F;
		public const ushort F1 = 0x70;
		public const ushort F12 = 0x7B;
		public const ushort NumLock = 0x90;
		public const ushort Scroll = 0x91;
		public const ushort LeftShift = 0xA0;
		public const ushort RightShift = 0xA1;
		public const ushort OemSemicolon = 0xBA;
		public const ushort OemPlus = 0xBB;
		public const ushort OemComma = 0xBC;
		public const ushort OemMinus = 0xBD;
		public const ushort OemPeriod = 0xBE;
		public const ushort OemSlash = 0xBF;
		public const ushort OemTilde = 0xC0;
		public const ushort OemOpenBracket = 0xDB;
		public const ushort OemBackslash = 0xDC;
		public const ushort OemCloseBracket = 0xDD;
		public const ushort OemQuote = 0xDE;
	}
}
