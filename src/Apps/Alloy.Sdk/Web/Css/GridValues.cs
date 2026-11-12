using System;
using System.Collections.Generic;

namespace Alloy.Sdk.Web.Css
{
	public enum GridSizeKind
	{
		Auto,
		Fixed,
		Percent,
		Fr,
		MinContent,
		MaxContent,
	}

	// One breadth in a track list: a length, a percentage, a flex factor, or a content keyword
	public readonly struct GridSize
	{
		public readonly GridSizeKind Kind;
		public readonly float Value;

		public GridSize(GridSizeKind kind, float value = 0)
		{
			Kind = kind;
			Value = value;
		}

		public static readonly GridSize AutoSize = new GridSize(GridSizeKind.Auto);

		public bool IsFlexible => Kind == GridSizeKind.Fr;

		public bool IsDefinite => Kind == GridSizeKind.Fixed || Kind == GridSizeKind.Percent;

		public float Resolve(float containing) => Kind == GridSizeKind.Percent ? containing * Value / 100 : Value;

		public override string ToString() => Kind switch
		{
			GridSizeKind.Fixed => Value + "px",
			GridSizeKind.Percent => Value + "%",
			GridSizeKind.Fr => Value + "fr",
			_ => Kind.ToString().ToLowerInvariant(),
		};
	}

	// A track: a minimum and a maximum breadth, the same unless it was written as minmax()
	public readonly struct GridTrack
	{
		public readonly GridSize Min;
		public readonly GridSize Max;

		public GridTrack(GridSize min, GridSize max)
		{
			Min = min;
			Max = max;
		}

		public GridTrack(GridSize size) : this(size, size)
		{
		}

		public override string ToString() => Min.Kind == Max.Kind && Min.Value == Max.Value ? Max.ToString() : $"minmax({Min}, {Max})";
	}

	// A grid-template-columns or -rows value: fixed tracks, with an optional auto-fill or auto-fit repeat expanded at layout time
	public sealed class GridTemplate
	{
		public readonly List<GridTrack> Before = new List<GridTrack>();
		public readonly List<GridTrack> Repeat = new List<GridTrack>();
		public readonly List<GridTrack> After = new List<GridTrack>();
		public bool HasAutoRepeat;
		public bool AutoFit;

		// Line names in order of the lines they precede, for "name-start"/"name-end" placement; sparse, so a dictionary
		public readonly Dictionary<string, List<int>> LineNames = new Dictionary<string, List<int>>(StringComparer.OrdinalIgnoreCase);

		public bool IsEmpty => Before.Count == 0 && Repeat.Count == 0 && After.Count == 0;

		public int ExplicitCount(int repeatCount) => Before.Count + (HasAutoRepeat ? Repeat.Count * repeatCount : 0) + After.Count;

		// The tracks with the repeat expanded the given number of times
		public List<GridTrack> Expand(int repeatCount)
		{
			List<GridTrack> tracks = new List<GridTrack>(Before);
			if (HasAutoRepeat)
			{
				for (int i = 0; i < repeatCount; i++)
					tracks.AddRange(Repeat);
			}
			tracks.AddRange(After);
			return tracks;
		}
	}

	// Where an item starts or ends on one axis: a line number (negative from the end), a span, a named area, or auto
	public readonly struct GridLine
	{
		public readonly int Line;
		public readonly int Span;
		public readonly string Name;

		public GridLine(int line, int span, string name)
		{
			Line = line;
			Span = span;
			Name = name;
		}

		public static readonly GridLine Auto = default;

		public bool IsAuto => Line == 0 && Span == 0 && Name == null;

		public bool IsSpan => Span > 0;

		public override string ToString() => IsAuto ? "auto" : IsSpan ? "span " + Span : Name ?? Line.ToString();
	}

	public enum GridAutoFlow
	{
		Row,
		Column,
	}
}
