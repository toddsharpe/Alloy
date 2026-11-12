using System;
using Alloy.Graphics;

namespace Alloy.Sdk.UI
{
	// Something drawn in a window's client area (MetalOS UI/Control.h); Bounds are window coordinates
	public abstract class Control
	{
		public Rectangle Bounds;
		public uint Foreground = Theme.Bone;
		public uint Background = Theme.Iron;
		public bool Hovered;
		public bool Pressed;

		protected Control(Rectangle bounds)
		{
			Bounds = bounds;
		}

		public abstract void Draw(in Surface surface);

		// The window got a left click inside Bounds; most controls ignore it
		public virtual void OnClick()
		{
		}
	}

	// What a button does decides how it looks: plain, an operator, the main action or a destructive one
	public enum ButtonStyle
	{
		Normal,
		Operator,
		Primary,
		Danger,
	}

	public sealed class Button : Control
	{
		public string Text;
		public ButtonStyle Style;
		public Action<Button> Click;

		public Button(string text, Rectangle bounds, ButtonStyle style = ButtonStyle.Normal) : base(bounds)
		{
			Text = text;
			Style = style;
		}

		// Centered, and nudged down a pixel while pressed so it reads as pushed in
		public override void Draw(in Surface surface)
		{
			Theme.Face face = Face();
			surface.DrawFace(Bounds.X, Bounds.Y, Bounds.Width, Bounds.Height, face);
			int x = Bounds.X + (Bounds.Width - Surface.TextWidth(Text.Length, Theme.TextScale)) / 2;
			int y = Bounds.Y + (Bounds.Height - Theme.Glyph) / 2 + (Pressed ? 1 : 0);
			surface.DrawText(x, y, Text, face.Label, Theme.TextScale);
		}

		public override void OnClick() => Click?.Invoke(this);

		private Theme.Face Face() => Style switch
		{
			ButtonStyle.Operator => Pressed ? Theme.OperatorSunken : Hovered ? Theme.OperatorHover : Theme.Operator,
			ButtonStyle.Primary => Pressed ? Theme.PrimarySunken : Hovered ? Theme.PrimaryHover : Theme.Primary,
			ButtonStyle.Danger => Pressed ? Theme.DangerSunken : Hovered ? Theme.DangerLift : Theme.Danger,
			_ => Pressed ? Theme.Sunken : Hovered ? Theme.RaisedHover : Theme.Raised,
		};
	}

	public sealed class Label : Control
	{
		public string Text;
		public int Scale;

		public Label(string text, Rectangle bounds, int scale = Theme.TextScale) : base(bounds)
		{
			Text = text;
			Scale = scale;
		}

		public override void Draw(in Surface surface)
		{
			surface.FillRectangle(Bounds.X, Bounds.Y, Bounds.Width, Bounds.Height, Background);
			surface.DrawText(Bounds.X + 5, Bounds.Y + 5, Text, Foreground, Scale);
		}
	}
}
