namespace Alloy.Abi
{
	// The app <-> window server protocol over IpcMessage ports (MetalOS Protocol_wm.h, carried on Alloy ports)
	public static class WmProtocol
	{
		public const string Endpoint = "wm";
		public const int MaxTitle = 32;

		// Requests to the server's port; Data[6] carries the client id the server assigned on Connect
		public const ulong Connect = 1;
		public const ulong CreateWindow = 2;
		public const ulong Present = 3;
		public const ulong DestroyWindow = 4;
		public const ulong ResizeWindow = 5;

		// Messages back on the client's reply port; Data[0] is the window they are for
		public const ulong Connected = 100;
		public const ulong WindowCreated = 101;
		public const ulong KeyDown = 102;
		public const ulong KeyUp = 103;
		// Mouse carries the buttons, the client x and y, and the wheel movement (signed) in Data[1] to Data[4]
		public const ulong Mouse = 104;
		public const ulong Close = 105;
		public const ulong Resize = 106;

		// How the server frames a window, as MetalOS's WindowStyle flags
		public const ulong StyleFramed = 0;
		public const ulong StyleResizable = 1;
		public const ulong StyleChromeless = 2;
		public const ulong StyleFullscreen = 4;
	}

	// The first page of a window's shared section, written by the app before CreateWindow or ResizeWindow; pixels follow at PixelOffset
	public struct WindowHeader
	{
		public const int PixelOffset = 4096;

		public int X;
		public int Y;
		public int Width;
		public int Height;
		public ulong Style;
		public TitleBytes Title;
		public IconRowBits IconRows;
		public uint IconColor1;
		public uint IconColor2;
		public uint IconColor3;
	}

	// The title as ASCII, NUL padded
	[System.Runtime.CompilerServices.InlineArray(WmProtocol.MaxTitle)]
	public struct TitleBytes
	{
		private byte _element0;
	}

	// The 8x8 icon, two bits a cell, one row per entry (PixelGlyph.Row)
	[System.Runtime.CompilerServices.InlineArray(8)]
	public struct IconRowBits
	{
		private ushort _element0;
	}

	// Filled by MapFramebuffer
	public struct FramebufferInfo
	{
		public uint Width;
		public uint Height;
		public uint Stride;
		public uint Reserved;
	}
}
