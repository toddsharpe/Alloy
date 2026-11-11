using System;
using System.Collections.Generic;
using Alloy.Sdk.Web.Css;

namespace Alloy.Sdk.Web.Layout
{
	// What layout needs from a font: its vertical metrics and the width of text, so the engine runs on the host with a fake one
	public abstract class LayoutFont
	{
		private float _spaceWidth = -1;

		public abstract float Size { get; }

		public abstract float Ascent { get; }

		public abstract float Descent { get; }

		public abstract float Measure(ReadOnlySpan<char> text);

		public float SpaceWidth
		{
			get
			{
				if (_spaceWidth < 0)
					_spaceWidth = Measure(" ");
				return _spaceWidth;
			}
		}

		// Roughly the height of a lower-case letter, for vertical-align: middle
		public float XHeight => Size * 0.5f;
	}

	// Hands out fonts for computed styles, caching however it likes
	public interface IFontProvider
	{
		LayoutFont GetFont(ComputedStyle style);
	}

	// A font with a fixed advance per character, for tests and for hosts without the real faces
	public sealed class FixedFont : LayoutFont
	{
		private readonly float _size;
		private readonly float _advance;

		public FixedFont(float size, float advanceRatio = 0.5f)
		{
			_size = size;
			_advance = size * advanceRatio;
		}

		public override float Size => _size;

		public override float Ascent => MathF.Round(_size * 0.8f);

		public override float Descent => MathF.Round(_size * 0.2f);

		public override float Measure(ReadOnlySpan<char> text) => text.Length * _advance;
	}

	public sealed class FixedFontProvider : IFontProvider
	{
		private readonly Dictionary<float, FixedFont> _fonts = new Dictionary<float, FixedFont>();
		private readonly float _ratio;

		public FixedFontProvider(float advanceRatio = 0.5f)
		{
			_ratio = advanceRatio;
		}

		public LayoutFont GetFont(ComputedStyle style)
		{
			float size = MathF.Round(style.FontSize);
			if (!_fonts.TryGetValue(size, out FixedFont font))
			{
				font = new FixedFont(size, _ratio);
				_fonts[size] = font;
			}
			return font;
		}
	}
}
