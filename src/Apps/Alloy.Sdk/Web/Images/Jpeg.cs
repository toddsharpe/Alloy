using System;
using System.Buffers.Binary;

namespace Alloy.Sdk.Web.Images
{
	// JPEG: baseline and progressive Huffman, 8-bit, grayscale, YCbCr with any subsampling, CMYK and YCCK as Adobe writes them, restart intervals
	public static class Jpeg
	{
		private static readonly int[] s_zigzag =
		{
			0, 1, 8, 16, 9, 2, 3, 10, 17, 24, 32, 25, 18, 11, 4, 5, 12, 19, 26, 33, 40, 48, 41, 34, 27, 20, 13, 6, 7, 14, 21, 28,
			35, 42, 49, 56, 57, 50, 43, 36, 29, 22, 15, 23, 30, 37, 44, 51, 58, 59, 52, 45, 38, 31, 39, 46, 53, 60, 61, 54, 47, 55, 62, 63,
		};

		private static readonly float[] s_cosines = BuildCosines();

		private sealed class Component
		{
			public int Id;
			public int H;
			public int V;
			public int QuantTable;
			public int BlocksPerLine;
			public int BlocksPerColumn;
			public int BlocksWide;
			public int BlocksHigh;
			public short[] Coefficients;
			public int DcTable;
			public int AcTable;
			public int Prediction;
			public byte[] Samples;
			public int SampleStride;
		}

		private sealed class Huffman
		{
			public readonly byte[] Lookup = new byte[1 << 9];
			public readonly byte[] LookupLength = new byte[1 << 9];
			public readonly int[] MaxCode = new int[18];
			public readonly int[] ValuePointer = new int[17];
			public readonly int[] MinCode = new int[17];
			public byte[] Values;
		}

		private sealed class Decoder
		{
			public ReadOnlySpan<byte> Data => _data;
			private readonly byte[] _data;
			public int At;
			public uint Bits;
			public int BitCount;
			public bool HitMarker;
			public int Width;
			public int Height;
			public bool Progressive;
			public Component[] Components;
			public int MaxH = 1;
			public int MaxV = 1;
			public int McusPerLine;
			public int McusPerColumn;
			public int RestartInterval;
			public int EobRun;
			public int AdobeTransform = -1;
			public readonly ushort[][] Quant = new ushort[4][];
			public readonly Huffman[] DcTables = new Huffman[4];
			public readonly Huffman[] AcTables = new Huffman[4];

			public Decoder(byte[] data)
			{
				_data = data;
			}

			// The next bit of entropy-coded data; a marker ends the supply and reads as zeros until the scan resets
			public int ReadBit()
			{
				if (BitCount == 0)
				{
					if (HitMarker || At >= _data.Length)
						return 0;
					byte b = _data[At];
					if (b == 0xFF)
					{
						byte next = At + 1 < _data.Length ? _data[At + 1] : (byte)0xD9;
						if (next == 0)
							At += 2;
						else
						{
							HitMarker = true;
							return 0;
						}
					}
					else
						At++;
					Bits = b;
					BitCount = 8;
				}
				BitCount--;
				return (int)(Bits >> BitCount) & 1;
			}

			public int Receive(int length)
			{
				int value = 0;
				for (int i = 0; i < length; i++)
					value = value << 1 | ReadBit();
				return value;
			}

			public int ReceiveExtend(int length)
			{
				if (length == 0)
					return 0;
				if (length == 1)
					return ReadBit() == 1 ? 1 : -1;
				int value = Receive(length);
				if (value >= 1 << (length - 1))
					return value;
				return value + (-1 << length) + 1;
			}

			public int DecodeHuffman(Huffman table)
			{
				int code = 0;
				for (int length = 1; length <= 16; length++)
				{
					code = code << 1 | ReadBit();
					if (code <= table.MaxCode[length])
					{
						int index = table.ValuePointer[length] + code - table.MinCode[length];
						return index >= 0 && index < table.Values.Length ? table.Values[index] : 0;
					}
				}
				return 0;
			}

			public void ResetBits()
			{
				Bits = 0;
				BitCount = 0;
				HitMarker = false;
			}
		}

		public static bool TryDecode(ReadOnlySpan<byte> span, out Bitmap bitmap, out string error)
		{
			bitmap = null;
			error = null;
			if (span.Length < 4 || span[0] != 0xFF || span[1] != 0xD8)
			{
				error = "not a JPEG";
				return false;
			}
			Decoder d = new Decoder(span.ToArray());
			d.At = 2;
			bool sawFrame = false;
			bool sawScan = false;
			ReadOnlySpan<byte> data = d.Data;
			while (d.At < data.Length)
			{
				if (data[d.At] != 0xFF)
				{
					d.At++;
					continue;
				}
				while (d.At < data.Length && data[d.At] == 0xFF)
					d.At++;
				if (d.At >= data.Length)
					break;
				int marker = data[d.At++];
				if (marker == 0xD8 || (marker >= 0xD0 && marker <= 0xD7) || marker == 0x01 || marker == 0)
					continue;
				if (marker == 0xD9)
					break;
				if (d.At + 2 > data.Length)
					break;
				int length = BinaryPrimitives.ReadUInt16BigEndian(data.Slice(d.At));
				if (length < 2 || d.At + length > data.Length)
				{
					error = "truncated segment";
					return false;
				}
				ReadOnlySpan<byte> segment = data.Slice(d.At + 2, length - 2);
				int segmentEnd = d.At + length;
				switch (marker)
				{
					case 0xDB:
						if (!ReadQuantTables(d, segment, out error))
							return false;
						break;
					case 0xC4:
						if (!ReadHuffmanTables(d, segment, out error))
							return false;
						break;
					case 0xC0:
					case 0xC1:
					case 0xC2:
						if (sawFrame)
						{
							error = "second frame";
							return false;
						}
						d.Progressive = marker == 0xC2;
						if (!ReadFrame(d, segment, out error))
							return false;
						sawFrame = true;
						break;
					case 0xC3:
					case 0xC5:
					case 0xC6:
					case 0xC7:
					case 0xC9:
					case 0xCA:
					case 0xCB:
					case 0xCD:
					case 0xCE:
					case 0xCF:
						error = "unsupported JPEG process (lossless or arithmetic)";
						return false;
					case 0xDD:
						d.RestartInterval = segment.Length >= 2 ? BinaryPrimitives.ReadUInt16BigEndian(segment) : 0;
						break;
					case 0xEE:
						if (segment.Length >= 12 && segment[0] == 'A' && segment[1] == 'd' && segment[2] == 'o' && segment[3] == 'b' && segment[4] == 'e')
							d.AdobeTransform = segment[11];
						break;
					case 0xDA:
					{
						if (!sawFrame)
						{
							error = "scan before frame";
							return false;
						}
						d.At = segmentEnd;
						if (!DecodeScan(d, segment, out error))
							return false;
						sawScan = true;
						continue;
					}
				}
				d.At = segmentEnd;
			}
			if (!sawFrame || !sawScan)
			{
				error = "no image data";
				return false;
			}
			foreach (Component component in d.Components)
			{
				if (d.Quant[component.QuantTable] == null)
				{
					error = "missing quantization table";
					return false;
				}
				Reconstruct(d, component);
			}
			bitmap = ToBitmap(d);
			return true;
		}

		private static bool ReadQuantTables(Decoder d, ReadOnlySpan<byte> segment, out string error)
		{
			error = null;
			int at = 0;
			while (at < segment.Length)
			{
				int precision = segment[at] >> 4;
				int id = segment[at] & 15;
				at++;
				if (id > 3 || at + (precision == 0 ? 64 : 128) > segment.Length)
				{
					error = "bad quantization table";
					return false;
				}
				ushort[] table = new ushort[64];
				for (int i = 0; i < 64; i++)
				{
					if (precision == 0)
						table[s_zigzag[i]] = segment[at++];
					else
					{
						table[s_zigzag[i]] = BinaryPrimitives.ReadUInt16BigEndian(segment.Slice(at));
						at += 2;
					}
				}
				d.Quant[id] = table;
			}
			return true;
		}

		private static bool ReadHuffmanTables(Decoder d, ReadOnlySpan<byte> segment, out string error)
		{
			error = null;
			int at = 0;
			while (at + 17 <= segment.Length)
			{
				int kind = segment[at] >> 4;
				int id = segment[at] & 15;
				at++;
				int[] counts = new int[17];
				int total = 0;
				for (int i = 1; i <= 16; i++)
				{
					counts[i] = segment[at++];
					total += counts[i];
				}
				if (id > 3 || at + total > segment.Length)
				{
					error = "bad Huffman table";
					return false;
				}
				Huffman table = new Huffman { Values = segment.Slice(at, total).ToArray() };
				at += total;
				int code = 0;
				int k = 0;
				for (int length = 1; length <= 16; length++)
				{
					table.ValuePointer[length] = k;
					table.MinCode[length] = code;
					code += counts[length];
					k += counts[length];
					table.MaxCode[length] = counts[length] > 0 ? code - 1 : -1;
					code <<= 1;
				}
				table.MaxCode[17] = int.MaxValue;
				if (kind == 0)
					d.DcTables[id] = table;
				else
					d.AcTables[id] = table;
			}
			return true;
		}

		private static bool ReadFrame(Decoder d, ReadOnlySpan<byte> segment, out string error)
		{
			error = null;
			if (segment.Length < 6 || segment[0] != 8)
			{
				error = "only 8-bit JPEGs are supported";
				return false;
			}
			d.Height = BinaryPrimitives.ReadUInt16BigEndian(segment.Slice(1));
			d.Width = BinaryPrimitives.ReadUInt16BigEndian(segment.Slice(3));
			int count = segment[5];
			if (d.Width <= 0 || d.Height <= 0 || (long)d.Width * d.Height > ImageDecoder.MaxPixels || count < 1 || count > 4 || segment.Length < 6 + count * 3)
			{
				error = "bad frame header";
				return false;
			}
			d.Components = new Component[count];
			for (int i = 0; i < count; i++)
			{
				Component component = new Component { Id = segment[6 + i * 3], H = segment[7 + i * 3] >> 4, V = segment[7 + i * 3] & 15, QuantTable = segment[8 + i * 3] & 3 };
				if (component.H < 1 || component.H > 4 || component.V < 1 || component.V > 4)
				{
					error = "bad sampling factors";
					return false;
				}
				d.MaxH = Math.Max(d.MaxH, component.H);
				d.MaxV = Math.Max(d.MaxV, component.V);
				d.Components[i] = component;
			}
			d.McusPerLine = (d.Width + 8 * d.MaxH - 1) / (8 * d.MaxH);
			d.McusPerColumn = (d.Height + 8 * d.MaxV - 1) / (8 * d.MaxV);
			foreach (Component component in d.Components)
			{
				component.BlocksPerLine = (int)Math.Ceiling(Math.Ceiling(d.Width / 8.0) * component.H / d.MaxH);
				component.BlocksPerColumn = (int)Math.Ceiling(Math.Ceiling(d.Height / 8.0) * component.V / d.MaxV);
				component.BlocksWide = d.McusPerLine * component.H;
				component.BlocksHigh = d.McusPerColumn * component.V;
				long blocks = (long)component.BlocksWide * component.BlocksHigh;
				if (blocks * 64 > int.MaxValue / 2)
				{
					error = "image too large";
					return false;
				}
				component.Coefficients = new short[blocks * 64];
			}
			return true;
		}

		// One scan: the components it covers, its spectral band and approximation bits, then the entropy-coded MCUs with restarts
		private static bool DecodeScan(Decoder d, ReadOnlySpan<byte> header, out string error)
		{
			error = null;
			if (header.Length < 1)
			{
				error = "bad scan header";
				return false;
			}
			int count = header[0];
			if (count < 1 || count > 4 || header.Length < 1 + count * 2 + 3)
			{
				error = "bad scan header";
				return false;
			}
			Component[] scan = new Component[count];
			for (int i = 0; i < count; i++)
			{
				int id = header[1 + i * 2];
				int tables = header[2 + i * 2];
				Component found = null;
				foreach (Component component in d.Components)
				{
					if (component.Id == id)
						found = component;
				}
				if (found == null)
				{
					error = "scan for unknown component";
					return false;
				}
				found.DcTable = tables >> 4;
				found.AcTable = tables & 15;
				scan[i] = found;
			}
			int ss = header[1 + count * 2];
			int se = header[2 + count * 2];
			int ah = header[3 + count * 2] >> 4;
			int al = header[3 + count * 2] & 15;
			if (!d.Progressive)
			{
				ss = 0;
				se = 63;
				ah = 0;
				al = 0;
			}
			foreach (Component component in d.Components)
				component.Prediction = 0;
			d.EobRun = 0;
			d.ResetBits();

			int mcuCount;
			bool single = count == 1;
			if (single)
				mcuCount = scan[0].BlocksPerLine * scan[0].BlocksPerColumn;
			else
				mcuCount = d.McusPerLine * d.McusPerColumn;
			int restart = d.RestartInterval > 0 ? d.RestartInterval : int.MaxValue;
			int mcu = 0;
			while (mcu < mcuCount)
			{
				int batch = Math.Min(restart, mcuCount - mcu);
				for (int n = 0; n < batch; n++, mcu++)
				{
					if (single)
					{
						Component component = scan[0];
						int row = mcu / component.BlocksPerLine;
						int column = mcu % component.BlocksPerLine;
						DecodeBlock(d, component, row, column, ss, se, ah, al);
					}
					else
					{
						int mcuRow = mcu / d.McusPerLine;
						int mcuColumn = mcu % d.McusPerLine;
						foreach (Component component in scan)
						{
							for (int v = 0; v < component.V; v++)
							{
								for (int h = 0; h < component.H; h++)
									DecodeBlock(d, component, mcuRow * component.V + v, mcuColumn * component.H + h, ss, se, ah, al);
							}
						}
					}
				}
				// Between intervals: a restart marker, after which predictions start over
				d.BitCount = 0;
				d.Bits = 0;
				ReadOnlySpan<byte> data = d.Data;
				while (d.At + 1 < data.Length && !(data[d.At] == 0xFF && data[d.At + 1] != 0 && data[d.At + 1] != 0xFF))
					d.At++;
				if (d.At + 1 < data.Length && data[d.At] == 0xFF && data[d.At + 1] >= 0xD0 && data[d.At + 1] <= 0xD7)
				{
					d.At += 2;
					d.HitMarker = false;
					foreach (Component component in d.Components)
						component.Prediction = 0;
					d.EobRun = 0;
				}
				else
					break;
			}
			return true;
		}

		private static void DecodeBlock(Decoder d, Component component, int row, int column, int ss, int se, int ah, int al)
		{
			if (row >= component.BlocksHigh || column >= component.BlocksWide)
				return;
			int offset = (row * component.BlocksWide + column) * 64;
			short[] k = component.Coefficients;
			if (!d.Progressive)
			{
				int t = d.DecodeHuffman(d.DcTables[component.DcTable] ?? Empty);
				int diff = t == 0 ? 0 : d.ReceiveExtend(t);
				component.Prediction += diff;
				k[offset] = (short)component.Prediction;
				int i = 1;
				Huffman ac = d.AcTables[component.AcTable] ?? Empty;
				while (i < 64)
				{
					int rs = d.DecodeHuffman(ac);
					int s = rs & 15;
					int r = rs >> 4;
					if (s == 0)
					{
						if (r < 15)
							break;
						i += 16;
						continue;
					}
					i += r;
					if (i > 63)
						break;
					k[offset + s_zigzag[i]] = (short)d.ReceiveExtend(s);
					i++;
				}
				return;
			}
			if (ss == 0)
			{
				// DC scans: the first carries the value, later ones a refinement bit
				if (ah == 0)
				{
					int t = d.DecodeHuffman(d.DcTables[component.DcTable] ?? Empty);
					int diff = t == 0 ? 0 : d.ReceiveExtend(t);
					component.Prediction += diff;
					k[offset] = (short)(component.Prediction << al);
				}
				else if (d.ReadBit() == 1)
					k[offset] |= (short)(1 << al);
				return;
			}
			if (ah == 0)
			{
				// AC first scan for this band, with end-of-band runs
				if (d.EobRun > 0)
				{
					d.EobRun--;
					return;
				}
				Huffman ac = d.AcTables[component.AcTable] ?? Empty;
				int i = ss;
				while (i <= se)
				{
					int rs = d.DecodeHuffman(ac);
					int s = rs & 15;
					int r = rs >> 4;
					if (s == 0)
					{
						if (r < 15)
						{
							d.EobRun = (1 << r) - 1;
							if (r > 0)
								d.EobRun += d.Receive(r);
							break;
						}
						i += 16;
						continue;
					}
					i += r;
					if (i > 63)
						break;
					k[offset + s_zigzag[i]] = (short)(d.ReceiveExtend(s) * (1 << al));
					i++;
				}
				return;
			}
			RefineAc(d, component, offset, ss, se, al);
		}

		// AC refinement: already non-zero coefficients get a correction bit, zero ones are placed by run length
		private static void RefineAc(Decoder d, Component component, int offset, int ss, int se, int al)
		{
			short[] k = component.Coefficients;
			int p1 = 1 << al;
			int m1 = -1 << al;
			int i = ss;
			if (d.EobRun <= 0)
			{
				Huffman ac = d.AcTables[component.AcTable] ?? Empty;
				while (i <= se)
				{
					int rs = d.DecodeHuffman(ac);
					int s = rs & 15;
					int r = rs >> 4;
					int value = 0;
					if (s == 0)
					{
						if (r < 15)
						{
							d.EobRun = 1 << r;
							if (r > 0)
								d.EobRun += d.Receive(r);
							break;
						}
					}
					else
						value = d.ReadBit() == 1 ? p1 : m1;
					while (i <= se)
					{
						int z = offset + s_zigzag[i];
						if (k[z] != 0)
						{
							if (d.ReadBit() == 1 && (k[z] & p1) == 0)
								k[z] = (short)(k[z] >= 0 ? k[z] + p1 : k[z] + m1);
						}
						else
						{
							if (r == 0)
							{
								if (value != 0)
									k[z] = (short)value;
								i++;
								break;
							}
							r--;
						}
						i++;
					}
				}
			}
			if (d.EobRun > 0)
			{
				while (i <= se)
				{
					int z = offset + s_zigzag[i];
					if (k[z] != 0 && d.ReadBit() == 1 && (k[z] & p1) == 0)
						k[z] = (short)(k[z] >= 0 ? k[z] + p1 : k[z] + m1);
					i++;
				}
				d.EobRun--;
			}
		}

		private static readonly Huffman Empty = new Huffman { Values = Array.Empty<byte>() };

		private static float[] BuildCosines()
		{
			float[] table = new float[64];
			for (int x = 0; x < 8; x++)
			{
				for (int u = 0; u < 8; u++)
					table[x * 8 + u] = (float)((u == 0 ? Math.Sqrt(0.5) : 1.0) * Math.Cos((2 * x + 1) * u * Math.PI / 16) / 2);
			}
			return table;
		}

		// Dequantizes and inverse-transforms every block of a component into its sample plane
		private static void Reconstruct(Decoder d, Component component)
		{
			ushort[] quant = d.Quant[component.QuantTable];
			component.SampleStride = component.BlocksWide * 8;
			component.Samples = new byte[component.SampleStride * component.BlocksHigh * 8];
			float[] block = new float[64];
			float[] temp = new float[64];
			short[] k = component.Coefficients;
			for (int row = 0; row < component.BlocksHigh; row++)
			{
				for (int column = 0; column < component.BlocksWide; column++)
				{
					int offset = (row * component.BlocksWide + column) * 64;
					bool dcOnly = true;
					for (int i = 1; i < 64 && dcOnly; i++)
						dcOnly = k[offset + i] == 0;
					int baseX = column * 8;
					int baseY = row * 8;
					if (dcOnly)
					{
						int value = Clamp((int)MathF.Round(k[offset] * quant[0] / 8f) + 128);
						for (int y = 0; y < 8; y++)
						{
							int at = (baseY + y) * component.SampleStride + baseX;
							for (int x = 0; x < 8; x++)
								component.Samples[at + x] = (byte)value;
						}
						continue;
					}
					for (int i = 0; i < 64; i++)
						block[i] = k[offset + i] * quant[i];
					// Rows then columns of the separable 1-D transform
					for (int y = 0; y < 8; y++)
					{
						for (int x = 0; x < 8; x++)
						{
							float sum = 0;
							for (int u = 0; u < 8; u++)
								sum += s_cosines[x * 8 + u] * block[y * 8 + u];
							temp[y * 8 + x] = sum;
						}
					}
					for (int x = 0; x < 8; x++)
					{
						for (int y = 0; y < 8; y++)
						{
							float sum = 0;
							for (int v = 0; v < 8; v++)
								sum += s_cosines[y * 8 + v] * temp[v * 8 + x];
							component.Samples[(baseY + y) * component.SampleStride + baseX + x] = (byte)Clamp((int)MathF.Round(sum) + 128);
						}
					}
				}
			}
		}

		private static int Clamp(int value) => value < 0 ? 0 : value > 255 ? 255 : value;

		// Upsamples the components to the image grid and converts to RGB
		private static Bitmap ToBitmap(Decoder d)
		{
			Bitmap bitmap = new Bitmap(d.Width, d.Height);
			Component[] c = d.Components;
			bool adobeRgb = c.Length == 3 && d.AdobeTransform == 0;
			bool rgbIds = c.Length == 3 && c[0].Id == 'R' && c[1].Id == 'G' && c[2].Id == 'B';
			for (int y = 0; y < d.Height; y++)
			{
				for (int x = 0; x < d.Width; x++)
				{
					int s0 = Sample(d, c[0], x, y);
					uint pixel;
					if (c.Length == 1)
						pixel = (uint)(s0 << 16 | s0 << 8 | s0);
					else if (c.Length == 2)
						pixel = (uint)(s0 << 16 | s0 << 8 | s0);
					else if (c.Length == 3)
					{
						int s1 = Sample(d, c[1], x, y);
						int s2 = Sample(d, c[2], x, y);
						pixel = adobeRgb || rgbIds ? (uint)(s0 << 16 | s1 << 8 | s2) : YccToRgb(s0, s1, s2);
					}
					else
					{
						int s1 = Sample(d, c[1], x, y);
						int s2 = Sample(d, c[2], x, y);
						int s3 = Sample(d, c[3], x, y);
						int cc, m, yy;
						if (d.AdobeTransform == 2)
						{
							uint rgb = YccToRgb(s0, s1, s2);
							cc = (int)(rgb >> 16) & 0xFF;
							m = (int)(rgb >> 8) & 0xFF;
							yy = (int)rgb & 0xFF;
						}
						else
						{
							cc = s0;
							m = s1;
							yy = s2;
						}
						// Adobe stores CMYK inverted, so these are already "how much light", scaled by the key
						int r = cc * s3 / 255;
						int g = m * s3 / 255;
						int b = yy * s3 / 255;
						pixel = (uint)(r << 16 | g << 8 | b);
					}
					bitmap.Pixels[y * d.Width + x] = 0xFF000000 | pixel;
				}
			}
			return bitmap;
		}

		private static int Sample(Decoder d, Component component, int x, int y)
		{
			int sx = component.H == d.MaxH ? x : x * component.H / d.MaxH;
			int sy = component.V == d.MaxV ? y : y * component.V / d.MaxV;
			return component.Samples[sy * component.SampleStride + sx];
		}

		private static uint YccToRgb(int y, int cb, int cr)
		{
			float fy = y;
			float fcb = cb - 128;
			float fcr = cr - 128;
			int r = Clamp((int)MathF.Round(fy + 1.402f * fcr));
			int g = Clamp((int)MathF.Round(fy - 0.344136f * fcb - 0.714136f * fcr));
			int b = Clamp((int)MathF.Round(fy + 1.772f * fcb));
			return (uint)(r << 16 | g << 8 | b);
		}
	}
}
