using System;

namespace Alloy.Sdk.Text
{
	// Signed-area coverage accumulation (the font-rs method): each line adds the area it sweeps per pixel, and a prefix sum gives the coverage
	public sealed class Rasterizer
	{
		private readonly float[] _accumulation;

		public readonly int Width;
		public readonly int Height;

		public Rasterizer(int width, int height)
		{
			Width = Math.Max(width, 1);
			Height = Math.Max(height, 1);
			_accumulation = new float[Width * Height + 4];
		}

		public void Line(float x0, float y0, float x1, float y1)
		{
			if (y0 == y1)
				return;
			float direction = 1;
			if (y0 > y1)
			{
				direction = -1;
				(x0, x1) = (x1, x0);
				(y0, y1) = (y1, y0);
			}
			float dxdy = (x1 - x0) / (y1 - y0);
			float x = x0;
			int yStart = Math.Max((int)y0, 0);
			int yEnd = Math.Min((int)MathF.Ceiling(y1), Height);
			if (y0 < 0)
				x -= y0 * dxdy;
			for (int y = yStart; y < yEnd; y++)
			{
				int lineStart = y * Width;
				float dy = MathF.Min(y + 1, y1) - MathF.Max(y, y0);
				float xNext = x + dxdy * dy;
				float d = dy * direction;
				float xa = MathF.Min(x, xNext);
				float xb = MathF.Max(x, xNext);
				float xaFloor = MathF.Floor(xa);
				int xaIndex = (int)xaFloor;
				float xbCeiling = MathF.Ceiling(xb);
				int xbIndex = (int)xbCeiling;
				if (xaIndex < 0)
				{
					Add(lineStart, d);
				}
				else if (xbIndex <= xaIndex + 1)
				{
					float xmf = 0.5f * (x + xNext) - xaFloor;
					Add(lineStart + xaIndex, d - d * xmf);
					Add(lineStart + xaIndex + 1, d * xmf);
				}
				else
				{
					float s = 1 / (xb - xa);
					float xaFraction = xa - xaFloor;
					float a0 = 0.5f * s * (1 - xaFraction) * (1 - xaFraction);
					float xbFraction = xb - xbCeiling + 1;
					float am = 0.5f * s * xbFraction * xbFraction;
					Add(lineStart + xaIndex, d * a0);
					if (xbIndex == xaIndex + 2)
					{
						Add(lineStart + xaIndex + 1, d * (1 - a0 - am));
					}
					else
					{
						float a1 = s * (1.5f - xaFraction);
						Add(lineStart + xaIndex + 1, d * (a1 - a0));
						for (int xi = xaIndex + 2; xi < xbIndex - 1; xi++)
							Add(lineStart + xi, d * s);
						Add(lineStart + xbIndex - 1, d * (1 - a1 - (xbIndex - xaIndex - 3) * s - am));
					}
					Add(lineStart + xbIndex, d * am);
				}
				x = xNext;
			}
		}

		private void Add(int index, float value)
		{
			if ((uint)index < (uint)_accumulation.Length)
				_accumulation[index] += value;
		}

		public byte[] Finish() => Finish(false);

		// Coverage per pixel from the running winding sum; even-odd folds the sum so every second winding is a hole
		public byte[] Finish(bool evenOdd)
		{
			byte[] coverage = new byte[Width * Height];
			float sum = 0;
			for (int i = 0; i < coverage.Length; i++)
			{
				sum += _accumulation[i];
				float value = MathF.Abs(sum);
				if (evenOdd)
				{
					value %= 2;
					if (value > 1)
						value = 2 - value;
				}
				coverage[i] = (byte)(value >= 1 ? 255 : (int)(value * 255 + 0.5f));
			}
			return coverage;
		}
	}
}
