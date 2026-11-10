namespace Alloy.Graphics
{
	// MetalOS's "Forge" look (Graphics/Theme.h): gunmetal chrome, bone text, one ember accent; colors are 0xRRGGBB pixels
	public static class Theme
	{
		public const uint Void = 0x0B0B0D;
		public const uint Iron = 0x131417;
		public const uint IronDeep = 0x0E0F11;
		public const uint Well = 0x08090A;
		public const uint GunmetalTop = 0x2E3036;
		public const uint GunmetalBottom = 0x1F2024;
		public const uint GunmetalDimTop = 0x25272B;
		public const uint GunmetalDimBottom = 0x1B1C1F;

		public const uint Outline = 0x050506;
		public const uint BevelLight = 0x3D4047;
		public const uint BevelDark = 0x0C0D0F;
		public const uint Divider = 0x2B2D32;
		public const uint AccentOff = 0x26282C;

		public const uint Bone = 0xE9E3D6;
		public const uint Text = 0xDDD6C8;
		public const uint TextDim = 0x858178;
		public const uint Steel = 0x8A9099;
		public const uint Hint = 0x7C818A;
		public const uint Muted = 0x6F747C;

		public const uint Ember = 0xFF6A1F;
		public const uint EmberText = 0xFF7A2E;
		public const uint Molten = 0xFF9A3C;
		public const uint Hot = 0xFFCF5A;
		public const uint Blood = 0xB3200F;
		public const uint ErrorText = 0xEF5A3C;
		public const uint Selection = 0x2A1710;
		public const uint SelectionEdge = 0x5A2A14;

		// Text is the 8x8 system font scaled up; UI text is 2x
		public const int TextScale = 2;
		public const int Glyph = 8 * TextScale;

		public const int FrameEdge = 2;
		public const int TitleHeight = 28;
		public const int AccentHeight = 2;
		public const int FrameTop = FrameEdge + TitleHeight + AccentHeight;
		public const int ButtonWidth = 26;
		public const int ButtonHeight = 22;
		public const int ButtonGap = 4;
		public const int ButtonMargin = 6;
		public const int TaskbarHeight = 40;

		public struct Face
		{
			public uint Top;
			public uint Bottom;
			public uint Light;
			public uint Dark;
			public uint Label;

			public Face(uint top, uint bottom, uint light, uint dark, uint label)
			{
				Top = top;
				Bottom = bottom;
				Light = light;
				Dark = dark;
				Label = label;
			}
		}

		public static Face Raised => new Face(0x3A3D44, 0x2B2D32, 0x50545C, 0x17181B, Bone);
		public static Face RaisedHover => new Face(0x4A4E56, 0x383B41, 0x5C6068, 0x17181B, 0xFFFFFF);
		public static Face Sunken => new Face(0x1C1D20, 0x1C1D20, 0x070708, 0x3A3D44, Bone);
		public static Face Operator => new Face(0x26282C, 0x1C1D20, 0x3A3D44, 0x111214, EmberText);
		public static Face OperatorHover => new Face(0x2E3035, 0x222327, 0x44474E, 0x111214, Molten);
		public static Face OperatorSunken => new Face(0x18191C, 0x18191C, 0x070708, 0x34373D, EmberText);
		public static Face Primary => new Face(0xF0662A, 0xC2410F, 0xFF9A5C, 0x7A2408, 0x1A0B05);
		public static Face PrimaryHover => new Face(0xFF7838, 0xD04C14, 0xFFAE78, 0x7A2408, 0x1A0B05);
		public static Face PrimarySunken => new Face(0xA8380C, 0xC84A14, 0x6A1F06, 0xE0703A, 0x1A0B05);
		public static Face Danger => new Face(0x6A1D13, 0x4E140C, 0x8E3224, 0x2A0904, 0xF3DCD2);
		public static Face DangerLift => new Face(0x7A2418, 0x5A170E, 0x9E3A2A, 0x2A0904, 0xFFFFFF);
		public static Face DangerHover => new Face(0xC22A14, 0x8F1A0E, 0xE0503A, 0x4A0C05, 0xFFFFFF);
		public static Face DangerSunken => new Face(0x5A1009, 0x5A1009, 0x2A0604, 0x8F1A0E, Bone);
	}
}
