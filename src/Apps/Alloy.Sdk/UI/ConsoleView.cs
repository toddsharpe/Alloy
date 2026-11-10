using System;
using Alloy.Graphics;

namespace Alloy.Sdk.UI
{
	// What a run of console text is, which picks its color (MetalOS ConsoleAttribute)
	public enum ConsoleAttribute : byte
	{
		Normal,
		Prompt,
		Punctuation,
		Error,
		Dim,
		Bright,
	}

	// Scrollback of text lines: a fixed ring that wraps long lines and drops the oldest when full, so writes never fail (MetalOS TextBuffer)
	public sealed class TextBuffer
	{
		public const int MaxLines = 256;
		public const int MaxColumns = 160;
		private const int TabWidth = 4;

		private readonly char[] _lines = new char[MaxLines * MaxColumns];
		private readonly byte[] _attributes = new byte[MaxLines * MaxColumns];
		private readonly int[] _lengths = new int[MaxLines];
		private int _first;
		private int _last;
		private int _columns;
		private byte _attribute;

		public int Columns => _columns == 0 ? MaxColumns : _columns;

		// Lines kept, including the current (last) one, so never zero
		public int LineCount => _last + 1;

		public int CurrentLength => _lengths[Slot(_last)];

		// Wrap width for text written from now on
		public void SetColumns(int columns) => _columns = columns <= 0 || columns > MaxColumns ? 0 : columns;

		public void SetAttribute(ConsoleAttribute attribute) => _attribute = (byte)attribute;

		public void Write(ReadOnlySpan<char> text)
		{
			foreach (char c in text)
				Write(c);
		}

		public void Write(char c)
		{
			if (c == '\n')
			{
				NewLine();
			}
			else if (c == '\t')
			{
				do
				{
					Put(' ');
				}
				while (CurrentLength % TabWidth != 0);
			}
			else if (c >= ' ' && c <= '~')
			{
				Put(c);
			}
		}

		// Removes the last character of the current line; false if the line is empty
		public bool Backspace()
		{
			int slot = Slot(_last);
			if (_lengths[slot] == 0)
				return false;
			_lengths[slot]--;
			return true;
		}

		public void Clear()
		{
			_first = 0;
			_last = 0;
			_lengths[0] = 0;
		}

		// Index 0 is the oldest line kept
		public ReadOnlySpan<char> Line(int index) => new ReadOnlySpan<char>(_lines, Slot(index) * MaxColumns, _lengths[Slot(index)]);

		public ReadOnlySpan<byte> Attributes(int index) => new ReadOnlySpan<byte>(_attributes, Slot(index) * MaxColumns, _lengths[Slot(index)]);

		private int Slot(int index) => (_first + index) % MaxLines;

		private void Put(char c)
		{
			if (CurrentLength >= Columns)
				NewLine();
			int slot = Slot(_last);
			_attributes[slot * MaxColumns + _lengths[slot]] = _attribute;
			_lines[slot * MaxColumns + _lengths[slot]++] = c;
		}

		private void NewLine()
		{
			if (_last == MaxLines - 1)
				_first = (_first + 1) % MaxLines;
			else
				_last++;
			_lengths[Slot(_last)] = 0;
		}
	}

	// Terminal-style text area: the newest lines that fit (or older ones when scrolled back), a blinking cursor and a scrollbar (MetalOS UI/Console.h)
	public sealed class ConsoleView : Control
	{
		private const int PaddingX = 14;
		private const int PaddingY = 12;
		private const int ScrollbarSpace = 34;
		private const int LineHeight = 20;

		public readonly TextBuffer Text;
		public int Scroll;
		public bool ShowCursor = true;

		public ConsoleView(TextBuffer text, Rectangle bounds) : base(bounds)
		{
			Text = text;
			Background = Theme.IronDeep;
			Text.SetColumns(Cells(bounds.Width));
		}

		public int Rows => Bounds.Height > 2 * PaddingY ? (Bounds.Height - 2 * PaddingY) / LineHeight : 0;

		// New text wraps at the new width; lines already written keep theirs
		public void SetBounds(Rectangle bounds)
		{
			Bounds = bounds;
			Text.SetColumns(Cells(bounds.Width));
		}

		// Moves the view back (positive) or toward the newest lines (negative)
		public void ScrollBy(int lines)
		{
			Scroll = Math.Max(Scroll + lines, 0);
			ClampScroll();
		}

		public override void Draw(in Surface surface)
		{
			surface.FillRectangle(Bounds.X, Bounds.Y, Bounds.Width, Bounds.Height, Background);
			ClampScroll();
			int rows = Rows;
			int count = Text.LineCount;
			int end = count - Scroll;
			int first = end > rows ? end - rows : 0;
			int y = Bounds.Y + PaddingY;
			for (int i = first; i < end; i++)
			{
				DrawLine(surface, Bounds.X + PaddingX, y, i);
				y += LineHeight;
			}

			// A blinking block at the end of the newest line, when it's in view
			if (ShowCursor && Scroll == 0 && Kernel.TickCount / 530 % 2 == 0 && end > first)
				surface.FillRectangle(Bounds.X + PaddingX + Text.CurrentLength * Theme.Glyph, y - LineHeight, Theme.Glyph, Theme.Glyph, Theme.Ember);
			DrawScrollbar(surface, rows, count);
		}

		// Character cells across a width, inside the padding and clear of the scrollbar
		private static int Cells(int pixels) => pixels > PaddingX + ScrollbarSpace ? (pixels - PaddingX - ScrollbarSpace) / Theme.Glyph : 1;

		private static uint AttributeColor(byte attribute) => (ConsoleAttribute)attribute switch
		{
			ConsoleAttribute.Prompt => Theme.EmberText,
			ConsoleAttribute.Punctuation => Theme.Steel,
			ConsoleAttribute.Error => Theme.ErrorText,
			ConsoleAttribute.Dim => Theme.Muted,
			ConsoleAttribute.Bright => Theme.Bone,
			_ => Theme.Text,
		};

		private void ClampScroll()
		{
			int most = Math.Max(Text.LineCount - Rows, 0);
			if (Scroll > most)
				Scroll = most;
		}

		// One line, as runs of characters that share an attribute
		private void DrawLine(in Surface surface, int x, int y, int index)
		{
			ReadOnlySpan<char> line = Text.Line(index);
			ReadOnlySpan<byte> attributes = Text.Attributes(index);
			int start = 0;
			while (start < line.Length)
			{
				int end = start + 1;
				while (end < line.Length && attributes[end] == attributes[start])
					end++;
				surface.DrawText(x + start * Theme.Glyph, y, line.Slice(start, end - start), AttributeColor(attributes[start]), Theme.TextScale);
				start = end;
			}
		}

		// A thin track down the right edge, with a thumb for the part in view
		private void DrawScrollbar(in Surface surface, int rows, int count)
		{
			if (Bounds.Height <= 2 * PaddingY)
				return;
			int trackX = Bounds.X + Bounds.Width - 16, trackY = Bounds.Y + PaddingY, trackHeight = Bounds.Height - 2 * PaddingY;
			surface.FillRectangle(trackX, trackY, 8, trackHeight, 0x16171A);
			int thumbHeight = trackHeight;
			int thumbY = trackY;
			if (count > rows && rows != 0)
			{
				int most = count - rows;
				thumbHeight = Math.Max((int)((long)trackHeight * rows / count), 24);
				thumbY = trackY + (int)((long)(trackHeight - thumbHeight) * (most - Scroll) / most);
			}
			surface.FillRectangle(trackX, thumbY, 8, thumbHeight, 0x3A3D44);
			surface.DrawBevel(trackX, thumbY, 8, thumbHeight, 0x50545C, 0x3A3D44);
		}
	}
}
