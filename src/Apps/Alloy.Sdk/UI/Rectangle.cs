namespace Alloy.Sdk.UI
{
	// An integer rectangle in window or surface pixels
	public struct Rectangle
	{
		public int X;
		public int Y;
		public int Width;
		public int Height;

		public Rectangle(int x, int y, int width, int height)
		{
			X = x;
			Y = y;
			Width = width;
			Height = height;
		}

		public readonly bool Contains(int x, int y) => x >= X && x < X + Width && y >= Y && y < Y + Height;
	}
}
