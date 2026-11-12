using System;
using System.Collections.Generic;
using System.Globalization;
using Alloy.Sdk.Text;
using Alloy.Sdk.Web.Css;
using Alloy.Sdk.Web.Html;

namespace Alloy.Sdk.Web.Images
{
	// Rasterizes an inline svg element into a bitmap: paths and basic shapes, groups, use and transforms, filled and stroked, scaled to fit
	public static class Svg
	{
		private const int MaxSide = 2048;

		private sealed class CacheEntry
		{
			public int Width;
			public int Height;
			public uint Color;
			public float Rotate;
			public int Attributes;
			public int Children;
			public Bitmap Bitmap;
		}

		// The bitmap for an svg at a size, rendered once and kept on the element until it or its size or colour changes
		public static Bitmap Cached(Element svg, int width, int height, Color currentColor, float rotate = 0)
		{
			if (svg.RenderCache is CacheEntry entry && entry.Width == width && entry.Height == height && entry.Color == currentColor.Argb && entry.Rotate == rotate && entry.Attributes == svg.AttributeVersion && entry.Children == svg.ChildrenVersion)
				return entry.Bitmap;
			Bitmap bitmap = Render(svg, width, height, currentColor, rotate);
			svg.RenderCache = new CacheEntry { Width = width, Height = height, Color = currentColor.Argb, Rotate = rotate, Attributes = svg.AttributeVersion, Children = svg.ChildrenVersion, Bitmap = bitmap };
			return bitmap;
		}

		// Rotate is the CSS transform's rotation in degrees, applied about the centre of the box
		public static Bitmap Render(Element svg, int width, int height, Color currentColor, float rotate = 0)
		{
			// Very large drawings are rendered smaller and scaled up when drawn
			float shrink = Math.Max(1, Math.Max(width, height) / (float)MaxSide);
			int w = Math.Max(1, (int)(width / shrink));
			int h = Math.Max(1, (int)(height / shrink));
			Bitmap bitmap = new Bitmap(w, h);
			float vx = 0;
			float vy = 0;
			float vw = w;
			float vh = h;
			string viewBox = Attribute(svg, "viewBox");
			if (viewBox != null)
			{
				float[] parts = Numbers(viewBox);
				if (parts.Length == 4 && parts[2] > 0 && parts[3] > 0)
				{
					vx = parts[0];
					vy = parts[1];
					vw = parts[2];
					vh = parts[3];
				}
			}
			string aspect = Attribute(svg, "preserveAspectRatio");
			Matrix matrix;
			if (aspect != null && aspect.StartsWith("none", StringComparison.OrdinalIgnoreCase))
				matrix = new Matrix(w / vw, 0, 0, h / vh, -vx * w / vw, -vy * h / vh);
			else
			{
				float scale = Math.Min(w / vw, h / vh);
				matrix = new Matrix(scale, 0, 0, scale, (w - vw * scale) / 2 - vx * scale, (h - vh * scale) / 2 - vy * scale);
			}
			if (rotate != 0)
			{
				float angle = rotate * MathF.PI / 180;
				float cos = MathF.Cos(angle);
				float sin = MathF.Sin(angle);
				Matrix spin = Matrix.Multiply(Matrix.Multiply(new Matrix(1, 0, 0, 1, w / 2f, h / 2f), new Matrix(cos, sin, -sin, cos, 0, 0)), new Matrix(1, 0, 0, 1, -w / 2f, -h / 2f));
				matrix = Matrix.Multiply(spin, matrix);
			}
			Paint paint = new Paint { Fill = Color.Black, HasFill = true, StrokeWidth = 1, Opacity = 1, Current = currentColor };
			paint = paint.With(svg);
			DrawChildren(svg, matrix, paint, bitmap);
			return bitmap;
		}

		private static void DrawChildren(Element parent, Matrix matrix, Paint paint, Bitmap bitmap)
		{
			foreach (Node node in parent.Children)
			{
				if (node is Element child)
					DrawElement(child, matrix, paint, bitmap, 0);
			}
		}

		private static void DrawElement(Element element, Matrix matrix, Paint paint, Bitmap bitmap, int depth)
		{
			if (depth > 32)
				return;
			string tag = element.TagName;
			switch (tag)
			{
				case "defs":
				case "symbol":
				case "clippath":
				case "clipPath":
				case "mask":
				case "marker":
				case "pattern":
				case "lineargradient":
				case "linearGradient":
				case "radialgradient":
				case "radialGradient":
				case "title":
				case "desc":
				case "metadata":
				case "style":
				case "script":
				case "filter":
				case "text":
					return;
			}
			if (IsHidden(element))
				return;
			Matrix local = matrix;
			string transform = Attribute(element, "transform");
			if (transform != null)
				local = Matrix.Multiply(matrix, ParseTransform(transform));
			Paint own = paint.With(element);
			switch (tag)
			{
				case "use":
				{
					string href = Attribute(element, "href") ?? Attribute(element, "xlink:href");
					if (href == null || href.Length < 2 || href[0] != '#')
						return;
					Element target = FindById(element, href.Substring(1));
					if (target == null)
						return;
					float x = Number(Attribute(element, "x"));
					float y = Number(Attribute(element, "y"));
					if (x != 0 || y != 0)
						local = Matrix.Multiply(local, new Matrix(1, 0, 0, 1, x, y));
					if (target.TagName == "symbol" || target.TagName == "svg")
					{
						foreach (Node node in target.Children)
						{
							if (node is Element child)
								DrawElement(child, local, own.With(target), bitmap, depth + 1);
						}
					}
					else
						DrawElement(target, local, own, bitmap, depth + 1);
					return;
				}
				case "path":
				{
					string d = Attribute(element, "d");
					if (d != null)
					{
						PathBuilder path = new PathBuilder(local);
						path.Parse(d);
						DrawPath(path, own, bitmap);
					}
					break;
				}
				case "rect":
				{
					float x = Number(Attribute(element, "x"));
					float y = Number(Attribute(element, "y"));
					float rw = Number(Attribute(element, "width"));
					float rh = Number(Attribute(element, "height"));
					float rx = Number(Attribute(element, "rx"));
					float ry = Number(Attribute(element, "ry"));
					if (Attribute(element, "rx") == null)
						rx = ry;
					if (Attribute(element, "ry") == null)
						ry = rx;
					if (rw > 0 && rh > 0)
					{
						PathBuilder path = new PathBuilder(local);
						path.Rect(x, y, rw, rh, Math.Min(rx, rw / 2), Math.Min(ry, rh / 2));
						DrawPath(path, own, bitmap);
					}
					break;
				}
				case "circle":
				case "ellipse":
				{
					float cx = Number(Attribute(element, "cx"));
					float cy = Number(Attribute(element, "cy"));
					float rx = tag == "circle" ? Number(Attribute(element, "r")) : Number(Attribute(element, "rx"));
					float ry = tag == "circle" ? rx : Number(Attribute(element, "ry"));
					if (rx > 0 && ry > 0)
					{
						PathBuilder path = new PathBuilder(local);
						path.Ellipse(cx, cy, rx, ry);
						DrawPath(path, own, bitmap);
					}
					break;
				}
				case "line":
				{
					PathBuilder path = new PathBuilder(local);
					path.MoveTo(Number(Attribute(element, "x1")), Number(Attribute(element, "y1")));
					path.LineTo(Number(Attribute(element, "x2")), Number(Attribute(element, "y2")));
					DrawPath(path, own.WithoutFill(), bitmap);
					break;
				}
				case "polyline":
				case "polygon":
				{
					float[] points = Numbers(Attribute(element, "points") ?? "");
					if (points.Length >= 4)
					{
						PathBuilder path = new PathBuilder(local);
						path.MoveTo(points[0], points[1]);
						for (int i = 2; i + 1 < points.Length; i += 2)
							path.LineTo(points[i], points[i + 1]);
						if (tag == "polygon")
							path.Close();
						DrawPath(path, tag == "polygon" ? own : own, bitmap);
					}
					break;
				}
			}
			// Children are drawn for groups, and for shapes too, since the HTML parser may have nested siblings under a self-closed shape
			foreach (Node node in element.Children)
			{
				if (node is Element child)
					DrawElement(child, local, own, bitmap, depth + 1);
			}
		}

		private static bool IsHidden(Element element)
		{
			string display = Attribute(element, "display");
			if (display != null && display.Trim() == "none")
				return true;
			string visibility = Attribute(element, "visibility");
			if (visibility != null && visibility.Trim() == "hidden")
				return true;
			string style = Attribute(element, "style");
			return style != null && (style.Replace(" ", "").Contains("display:none") || style.Replace(" ", "").Contains("visibility:hidden"));
		}

		private static void DrawPath(PathBuilder path, Paint paint, Bitmap bitmap)
		{
			if (path.Subpaths.Count == 0)
				return;
			if (paint.HasFill && paint.Opacity * paint.FillOpacity > 0)
			{
				Rasterizer rasterizer = new Rasterizer(bitmap.Width, bitmap.Height);
				foreach (Subpath subpath in path.Subpaths)
				{
					List<float> points = subpath.Points;
					if (points.Count < 6)
						continue;
					for (int i = 0; i + 3 < points.Count; i += 2)
						rasterizer.Line(points[i], points[i + 1], points[i + 2], points[i + 3]);
					rasterizer.Line(points[points.Count - 2], points[points.Count - 1], points[0], points[1]);
				}
				Composite(bitmap, rasterizer.Finish(paint.EvenOdd), paint.Fill, paint.Opacity * paint.FillOpacity);
			}
			if (paint.HasStroke && paint.StrokeWidth > 0 && paint.Opacity * paint.StrokeOpacity > 0)
			{
				float half = Math.Max(0.35f, paint.StrokeWidth * path.Scale / 2);
				Rasterizer rasterizer = new Rasterizer(bitmap.Width, bitmap.Height);
				foreach (Subpath subpath in path.Subpaths)
				{
					List<float> points = subpath.Points;
					int count = points.Count / 2;
					if (count == 0)
						continue;
					if (count == 1)
					{
						Dot(rasterizer, points[0], points[1], half);
						continue;
					}
					int segments = subpath.Closed ? count : count - 1;
					for (int i = 0; i < segments; i++)
					{
						int j = (i + 1) % count;
						Segment(rasterizer, points[2 * i], points[2 * i + 1], points[2 * j], points[2 * j + 1], half);
					}
					// Round joins and caps: a dot at every vertex covers the gaps between segments
					if (half > 0.75f)
					{
						for (int i = 0; i < count; i++)
							Dot(rasterizer, points[2 * i], points[2 * i + 1], half);
					}
				}
				Composite(bitmap, rasterizer.Finish(false), paint.Stroke, paint.Opacity * paint.StrokeOpacity);
			}
		}

		// A segment as a quad of consistent winding, so overlapping segments add up rather than cancel
		private static void Segment(Rasterizer rasterizer, float x0, float y0, float x1, float y1, float half)
		{
			float dx = x1 - x0;
			float dy = y1 - y0;
			float length = MathF.Sqrt(dx * dx + dy * dy);
			if (length < 0.001f)
				return;
			float nx = -dy / length * half;
			float ny = dx / length * half;
			rasterizer.Line(x0 + nx, y0 + ny, x1 + nx, y1 + ny);
			rasterizer.Line(x1 + nx, y1 + ny, x1 - nx, y1 - ny);
			rasterizer.Line(x1 - nx, y1 - ny, x0 - nx, y0 - ny);
			rasterizer.Line(x0 - nx, y0 - ny, x0 + nx, y0 + ny);
		}

		private static void Dot(Rasterizer rasterizer, float cx, float cy, float radius)
		{
			int steps = Math.Clamp((int)(radius * 4), 8, 64);
			float px = cx + radius;
			float py = cy;
			for (int i = 1; i <= steps; i++)
			{
				float angle = -2 * MathF.PI * i / steps;
				float x = cx + radius * MathF.Cos(angle);
				float y = cy + radius * MathF.Sin(angle);
				rasterizer.Line(px, py, x, y);
				px = x;
				py = y;
			}
		}

		// Source-over of one colour through a coverage mask onto straight-alpha ARGB pixels
		private static void Composite(Bitmap bitmap, byte[] coverage, Color color, float opacity)
		{
			uint[] pixels = bitmap.Pixels;
			float alphaScale = color.A / 255f * opacity;
			int count = Math.Min(coverage.Length, pixels.Length);
			for (int i = 0; i < count; i++)
			{
				if (coverage[i] == 0)
					continue;
				float sa = coverage[i] / 255f * alphaScale;
				if (sa <= 0)
					continue;
				uint under = pixels[i];
				float da = (under >> 24) / 255f;
				float oa = sa + da * (1 - sa);
				if (oa <= 0)
					continue;
				float r = (color.R * sa + ((under >> 16) & 0xFF) * da * (1 - sa)) / oa;
				float g = (color.G * sa + ((under >> 8) & 0xFF) * da * (1 - sa)) / oa;
				float b = (color.B * sa + (under & 0xFF) * da * (1 - sa)) / oa;
				pixels[i] = (uint)(int)(oa * 255 + 0.5f) << 24 | (uint)(int)(r + 0.5f) << 16 | (uint)(int)(g + 0.5f) << 8 | (uint)(int)(b + 0.5f);
			}
		}

		private static Element FindById(Element from, string id)
		{
			Node root = from;
			while (root.Parent != null)
				root = root.Parent;
			foreach (Element element in Descendants(root))
			{
				if (element.Id == id)
					return element;
			}
			return null;
		}

		private static IEnumerable<Element> Descendants(Node node)
		{
			foreach (Node child in node.Children)
			{
				if (child is Element element)
				{
					yield return element;
					foreach (Element inner in Descendants(element))
						yield return inner;
				}
			}
		}

		// Attributes as the HTML parser stored them, whichever case the markup used
		private static string Attribute(Element element, string name)
		{
			foreach (KeyValuePair<string, string> attribute in element.Attributes)
			{
				if (string.Equals(attribute.Key, name, StringComparison.OrdinalIgnoreCase))
					return attribute.Value;
			}
			return null;
		}

		// A presentation attribute, or the same property from the style attribute, which wins
		private static string Property(Element element, string name)
		{
			string style = Attribute(element, "style");
			if (style != null)
			{
				foreach (string declaration in style.Split(';'))
				{
					int colon = declaration.IndexOf(':');
					if (colon > 0 && declaration.Substring(0, colon).Trim().Equals(name, StringComparison.OrdinalIgnoreCase))
						return declaration.Substring(colon + 1).Trim();
				}
			}
			return Attribute(element, name);
		}

		private static float Number(string text)
		{
			if (string.IsNullOrEmpty(text))
				return 0;
			text = text.Trim();
			int end = 0;
			while (end < text.Length && (char.IsDigit(text[end]) || text[end] is '.' or '-' or '+' or 'e' or 'E'))
				end++;
			return float.TryParse(text.AsSpan(0, end), NumberStyles.Float, CultureInfo.InvariantCulture, out float value) ? value : 0;
		}

		private static readonly char[] s_separators = { ' ', ',', '\t', '\n', '\r' };

		private static float[] Numbers(string text)
		{
			string[] parts = text.Split(s_separators, StringSplitOptions.RemoveEmptyEntries);
			List<float> values = new List<float>(parts.Length);
			foreach (string part in parts)
			{
				if (float.TryParse(part, NumberStyles.Float, CultureInfo.InvariantCulture, out float value))
					values.Add(value);
			}
			return values.ToArray();
		}

		// translate(), scale(), rotate(), skewX(), skewY() and matrix(), composed left to right
		private static Matrix ParseTransform(string text)
		{
			Matrix result = Matrix.Identity;
			int at = 0;
			while (at < text.Length)
			{
				int open = text.IndexOf('(', at);
				if (open < 0)
					break;
				int close = text.IndexOf(')', open);
				if (close < 0)
					break;
				string name = text.Substring(at, open - at).Trim().ToLowerInvariant();
				float[] args = Numbers(text.Substring(open + 1, close - open - 1));
				Matrix step = Matrix.Identity;
				switch (name)
				{
					case "translate":
						step = new Matrix(1, 0, 0, 1, args.Length > 0 ? args[0] : 0, args.Length > 1 ? args[1] : 0);
						break;
					case "scale":
						step = new Matrix(args.Length > 0 ? args[0] : 1, 0, 0, args.Length > 1 ? args[1] : (args.Length > 0 ? args[0] : 1), 0, 0);
						break;
					case "rotate":
					{
						float angle = (args.Length > 0 ? args[0] : 0) * MathF.PI / 180;
						float cos = MathF.Cos(angle);
						float sin = MathF.Sin(angle);
						step = new Matrix(cos, sin, -sin, cos, 0, 0);
						if (args.Length > 2)
							step = Matrix.Multiply(Matrix.Multiply(new Matrix(1, 0, 0, 1, args[1], args[2]), step), new Matrix(1, 0, 0, 1, -args[1], -args[2]));
						break;
					}
					case "skewx":
						step = new Matrix(1, 0, MathF.Tan((args.Length > 0 ? args[0] : 0) * MathF.PI / 180), 1, 0, 0);
						break;
					case "skewy":
						step = new Matrix(1, MathF.Tan((args.Length > 0 ? args[0] : 0) * MathF.PI / 180), 0, 1, 0, 0);
						break;
					case "matrix":
						if (args.Length == 6)
							step = new Matrix(args[0], args[1], args[2], args[3], args[4], args[5]);
						break;
				}
				result = Matrix.Multiply(result, step);
				at = close + 1;
			}
			return result;
		}

		// An affine transform: x' = A x + C y + E, y' = B x + D y + F
		private readonly struct Matrix
		{
			public readonly float A, B, C, D, E, F;

			public Matrix(float a, float b, float c, float d, float e, float f)
			{
				A = a;
				B = b;
				C = c;
				D = d;
				E = e;
				F = f;
			}

			public static readonly Matrix Identity = new Matrix(1, 0, 0, 1, 0, 0);

			public void Apply(float x, float y, out float tx, out float ty)
			{
				tx = A * x + C * y + E;
				ty = B * x + D * y + F;
			}

			// The size change a unit length undergoes, for flattening and stroke widths
			public float Scale => MathF.Sqrt(MathF.Abs(A * D - B * C));

			// Outer first, then inner: points are transformed by inner, then by outer
			public static Matrix Multiply(Matrix outer, Matrix inner) => new Matrix(
				outer.A * inner.A + outer.C * inner.B,
				outer.B * inner.A + outer.D * inner.B,
				outer.A * inner.C + outer.C * inner.D,
				outer.B * inner.C + outer.D * inner.D,
				outer.A * inner.E + outer.C * inner.F + outer.E,
				outer.B * inner.E + outer.D * inner.F + outer.F);
		}

		// Fill and stroke state, inherited down the tree with each element's own attributes applied over it
		private struct Paint
		{
			public Color Fill;
			public bool HasFill;
			public Color Stroke;
			public bool HasStroke;
			public float StrokeWidth;
			public float Opacity;
			public float FillOpacity;
			public float StrokeOpacity;
			public bool EvenOdd;
			public Color Current;

			public Paint With(Element element)
			{
				Paint paint = this;
				if (paint.FillOpacity == 0 && paint.StrokeOpacity == 0 && !paint.HasStroke)
				{
					paint.FillOpacity = 1;
					paint.StrokeOpacity = 1;
				}
				string color = Property(element, "color");
				if (color != null && TryColor(color, paint.Current, out Color current, out bool noColor) && !noColor)
					paint.Current = current;
				string fill = Property(element, "fill");
				if (fill != null && TryColor(fill, paint.Current, out Color fillColor, out bool noFill))
				{
					paint.Fill = fillColor;
					paint.HasFill = !noFill;
				}
				string stroke = Property(element, "stroke");
				if (stroke != null && TryColor(stroke, paint.Current, out Color strokeColor, out bool noStroke))
				{
					paint.Stroke = strokeColor;
					paint.HasStroke = !noStroke;
				}
				string width = Property(element, "stroke-width");
				if (width != null)
					paint.StrokeWidth = Number(width);
				string opacity = Property(element, "opacity");
				if (opacity != null)
					paint.Opacity *= Math.Clamp(Number(opacity), 0, 1);
				string fillOpacity = Property(element, "fill-opacity");
				if (fillOpacity != null)
					paint.FillOpacity = Math.Clamp(Number(fillOpacity), 0, 1);
				string strokeOpacity = Property(element, "stroke-opacity");
				if (strokeOpacity != null)
					paint.StrokeOpacity = Math.Clamp(Number(strokeOpacity), 0, 1);
				string rule = Property(element, "fill-rule") ?? Property(element, "clip-rule");
				if (rule != null)
					paint.EvenOdd = rule.Trim() == "evenodd";
				return paint;
			}

			public Paint WithoutFill()
			{
				Paint paint = this;
				paint.HasFill = false;
				return paint;
			}
		}

		// none, currentColor, any CSS colour; a gradient or pattern reference falls back to the current colour so logos stay visible
		private static bool TryColor(string text, Color current, out Color color, out bool none)
		{
			color = current;
			none = false;
			text = text.Trim();
			if (text.Equals("none", StringComparison.OrdinalIgnoreCase) || text.Equals("transparent", StringComparison.OrdinalIgnoreCase))
			{
				none = true;
				return true;
			}
			if (text.Equals("currentColor", StringComparison.OrdinalIgnoreCase) || text.StartsWith("url(", StringComparison.OrdinalIgnoreCase) || text.Equals("inherit", StringComparison.OrdinalIgnoreCase))
				return true;
			List<CssToken> tokens = CssTokenizer.Tokenize(text);
			return CssValue.TryColor(tokens, 0, out color, out _);
		}

		private sealed class Subpath
		{
			public readonly List<float> Points = new List<float>();
			public bool Closed;
		}

		// Builds device-space polylines from path data, flattening curves and arcs according to the transform's scale
		private sealed class PathBuilder
		{
			public readonly List<Subpath> Subpaths = new List<Subpath>();
			public readonly float Scale;
			private readonly Matrix _matrix;
			private Subpath _current;
			private float _x;
			private float _y;
			private float _startX;
			private float _startY;
			private float _controlX;
			private float _controlY;
			private char _lastCommand;

			public PathBuilder(Matrix matrix)
			{
				_matrix = matrix;
				Scale = Math.Max(matrix.Scale, 0.01f);
			}

			private void Emit(float x, float y)
			{
				if (_current == null)
				{
					_current = new Subpath();
					Subpaths.Add(_current);
				}
				_matrix.Apply(x, y, out float tx, out float ty);
				_current.Points.Add(tx);
				_current.Points.Add(ty);
			}

			public void MoveTo(float x, float y)
			{
				_current = new Subpath();
				Subpaths.Add(_current);
				_x = _startX = x;
				_y = _startY = y;
				Emit(x, y);
			}

			public void LineTo(float x, float y)
			{
				if (_current == null)
					MoveTo(_x, _y);
				Emit(x, y);
				_x = x;
				_y = y;
			}

			public void Close()
			{
				if (_current != null)
					_current.Closed = true;
				_x = _startX;
				_y = _startY;
				_current = null;
			}

			public void CubicTo(float x1, float y1, float x2, float y2, float x, float y)
			{
				int steps = Steps(Distance(_x, _y, x1, y1) + Distance(x1, y1, x2, y2) + Distance(x2, y2, x, y));
				float x0 = _x;
				float y0 = _y;
				for (int i = 1; i <= steps; i++)
				{
					float t = i / (float)steps;
					float u = 1 - t;
					float px = u * u * u * x0 + 3 * u * u * t * x1 + 3 * u * t * t * x2 + t * t * t * x;
					float py = u * u * u * y0 + 3 * u * u * t * y1 + 3 * u * t * t * y2 + t * t * t * y;
					LineTo(px, py);
				}
				_controlX = x2;
				_controlY = y2;
			}

			public void QuadTo(float x1, float y1, float x, float y)
			{
				int steps = Steps(Distance(_x, _y, x1, y1) + Distance(x1, y1, x, y));
				float x0 = _x;
				float y0 = _y;
				for (int i = 1; i <= steps; i++)
				{
					float t = i / (float)steps;
					float u = 1 - t;
					LineTo(u * u * x0 + 2 * u * t * x1 + t * t * x, u * u * y0 + 2 * u * t * y1 + t * t * y);
				}
				_controlX = x1;
				_controlY = y1;
			}

			// An elliptical arc by the endpoint parameterization, converted to its centre and swept in short segments
			public void ArcTo(float rx, float ry, float rotation, bool largeArc, bool sweep, float x, float y)
			{
				float x1 = _x;
				float y1 = _y;
				if (x1 == x && y1 == y)
					return;
				rx = MathF.Abs(rx);
				ry = MathF.Abs(ry);
				if (rx == 0 || ry == 0)
				{
					LineTo(x, y);
					return;
				}
				float phi = rotation * MathF.PI / 180;
				float cos = MathF.Cos(phi);
				float sin = MathF.Sin(phi);
				float dx2 = (x1 - x) / 2;
				float dy2 = (y1 - y) / 2;
				float x1p = cos * dx2 + sin * dy2;
				float y1p = -sin * dx2 + cos * dy2;
				float lambda = x1p * x1p / (rx * rx) + y1p * y1p / (ry * ry);
				if (lambda > 1)
				{
					float root = MathF.Sqrt(lambda);
					rx *= root;
					ry *= root;
				}
				float sign = largeArc == sweep ? -1 : 1;
				float numerator = rx * rx * ry * ry - rx * rx * y1p * y1p - ry * ry * x1p * x1p;
				float denominator = rx * rx * y1p * y1p + ry * ry * x1p * x1p;
				float coefficient = denominator == 0 ? 0 : sign * MathF.Sqrt(MathF.Max(0, numerator / denominator));
				float cxp = coefficient * rx * y1p / ry;
				float cyp = -coefficient * ry * x1p / rx;
				float cx = cos * cxp - sin * cyp + (x1 + x) / 2;
				float cy = sin * cxp + cos * cyp + (y1 + y) / 2;
				float ux = (x1p - cxp) / rx;
				float uy = (y1p - cyp) / ry;
				float vx = (-x1p - cxp) / rx;
				float vy = (-y1p - cyp) / ry;
				float theta = Angle(1, 0, ux, uy);
				float delta = Angle(ux, uy, vx, vy);
				if (!sweep && delta > 0)
					delta -= 2 * MathF.PI;
				else if (sweep && delta < 0)
					delta += 2 * MathF.PI;
				int steps = Steps(MathF.Abs(delta) * Math.Max(rx, ry));
				for (int i = 1; i <= steps; i++)
				{
					float t = theta + delta * i / steps;
					float ct = MathF.Cos(t);
					float st = MathF.Sin(t);
					LineTo(cx + rx * cos * ct - ry * sin * st, cy + rx * sin * ct + ry * cos * st);
				}
				_x = x;
				_y = y;
			}

			private static float Angle(float ux, float uy, float vx, float vy)
			{
				float dot = ux * vx + uy * vy;
				float length = MathF.Sqrt((ux * ux + uy * uy) * (vx * vx + vy * vy));
				if (length == 0)
					return 0;
				float angle = MathF.Acos(Math.Clamp(dot / length, -1, 1));
				return ux * vy - uy * vx < 0 ? -angle : angle;
			}

			public void Rect(float x, float y, float w, float h, float rx, float ry)
			{
				if (rx <= 0 || ry <= 0)
				{
					MoveTo(x, y);
					LineTo(x + w, y);
					LineTo(x + w, y + h);
					LineTo(x, y + h);
					Close();
					return;
				}
				MoveTo(x + rx, y);
				LineTo(x + w - rx, y);
				ArcTo(rx, ry, 0, false, true, x + w, y + ry);
				LineTo(x + w, y + h - ry);
				ArcTo(rx, ry, 0, false, true, x + w - rx, y + h);
				LineTo(x + rx, y + h);
				ArcTo(rx, ry, 0, false, true, x, y + h - ry);
				LineTo(x, y + ry);
				ArcTo(rx, ry, 0, false, true, x + rx, y);
				Close();
			}

			public void Ellipse(float cx, float cy, float rx, float ry)
			{
				int steps = Steps(2 * MathF.PI * Math.Max(rx, ry));
				MoveTo(cx + rx, cy);
				for (int i = 1; i < steps; i++)
				{
					float t = 2 * MathF.PI * i / steps;
					LineTo(cx + rx * MathF.Cos(t), cy + ry * MathF.Sin(t));
				}
				Close();
			}

			private int Steps(float userLength) => Math.Clamp((int)MathF.Ceiling(userLength * Scale / 2.5f), 3, 120);

			private static float Distance(float x0, float y0, float x1, float y1) => MathF.Sqrt((x1 - x0) * (x1 - x0) + (y1 - y0) * (y1 - y0));

			// The d attribute: commands with implicit repeats, compact numbers like 1.5.5 and -.75, and arc flags that may run together
			public void Parse(string d)
			{
				int at = 0;
				char command = '\0';
				List<float> args = new List<float>(8);
				while (at < d.Length)
				{
					char c = d[at];
					if (c == ' ' || c == ',' || c == '\t' || c == '\n' || c == '\r')
					{
						at++;
						continue;
					}
					if (char.IsLetter(c))
					{
						command = c;
						at++;
						args.Clear();
						if (command == 'Z' || command == 'z')
						{
							Close();
							_lastCommand = command;
						}
						continue;
					}
					if (command == '\0')
						return;
					int needed = ArgumentCount(command);
					if (needed == 0)
						return;
					args.Clear();
					for (int i = 0; i < needed; i++)
					{
						bool flag = (command == 'A' || command == 'a') && (i == 3 || i == 4);
						if (!ReadNumber(d, ref at, flag, out float value))
							return;
						args.Add(value);
					}
					Apply(command, args);
					_lastCommand = command;
					// A moveto's extra coordinate pairs are implicit linetos
					if (command == 'M')
						command = 'L';
					else if (command == 'm')
						command = 'l';
				}
			}

			private static int ArgumentCount(char command) => char.ToUpperInvariant(command) switch
			{
				'M' or 'L' or 'T' => 2,
				'H' or 'V' => 1,
				'C' => 6,
				'S' or 'Q' => 4,
				'A' => 7,
				_ => 0,
			};

			private static bool ReadNumber(string d, ref int at, bool flag, out float value)
			{
				value = 0;
				while (at < d.Length && (d[at] == ' ' || d[at] == ',' || d[at] == '\t' || d[at] == '\n' || d[at] == '\r'))
					at++;
				if (at >= d.Length)
					return false;
				if (flag)
				{
					if (d[at] != '0' && d[at] != '1')
						return false;
					value = d[at] - '0';
					at++;
					return true;
				}
				int start = at;
				if (d[at] == '-' || d[at] == '+')
					at++;
				bool digits = false;
				bool dot = false;
				while (at < d.Length)
				{
					char c = d[at];
					if (char.IsDigit(c))
						digits = true;
					else if (c == '.' && !dot)
						dot = true;
					else
						break;
					at++;
				}
				if (at < d.Length && (d[at] == 'e' || d[at] == 'E') && digits)
				{
					int save = at;
					at++;
					if (at < d.Length && (d[at] == '-' || d[at] == '+'))
						at++;
					if (at < d.Length && char.IsDigit(d[at]))
					{
						while (at < d.Length && char.IsDigit(d[at]))
							at++;
					}
					else
						at = save;
				}
				return digits && float.TryParse(d.AsSpan(start, at - start), NumberStyles.Float, CultureInfo.InvariantCulture, out value);
			}

			private void Apply(char command, List<float> a)
			{
				bool relative = char.IsLower(command);
				float ox = relative ? _x : 0;
				float oy = relative ? _y : 0;
				switch (char.ToUpperInvariant(command))
				{
					case 'M':
						MoveTo(ox + a[0], oy + a[1]);
						break;
					case 'L':
						LineTo(ox + a[0], oy + a[1]);
						break;
					case 'H':
						LineTo(ox + a[0], _y);
						break;
					case 'V':
						LineTo(_x, oy + a[0]);
						break;
					case 'C':
						CubicTo(ox + a[0], oy + a[1], ox + a[2], oy + a[3], ox + a[4], oy + a[5]);
						break;
					case 'S':
					{
						bool smooth = _lastCommand is 'C' or 'c' or 'S' or 's';
						float x1 = smooth ? 2 * _x - _controlX : _x;
						float y1 = smooth ? 2 * _y - _controlY : _y;
						CubicTo(x1, y1, ox + a[0], oy + a[1], ox + a[2], oy + a[3]);
						break;
					}
					case 'Q':
						QuadTo(ox + a[0], oy + a[1], ox + a[2], oy + a[3]);
						break;
					case 'T':
					{
						bool smooth = _lastCommand is 'Q' or 'q' or 'T' or 't';
						float x1 = smooth ? 2 * _x - _controlX : _x;
						float y1 = smooth ? 2 * _y - _controlY : _y;
						QuadTo(x1, y1, ox + a[0], oy + a[1]);
						break;
					}
					case 'A':
						ArcTo(a[0], a[1], a[2], a[3] != 0, a[4] != 0, ox + a[5], oy + a[6]);
						break;
				}
			}
		}
	}
}
