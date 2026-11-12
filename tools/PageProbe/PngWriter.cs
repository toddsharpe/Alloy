using System;
using System.Buffers.Binary;
using System.IO;
using System.IO.Compression;

namespace Alloy.Tools.PageProbe
{
	// Writes 0xRRGGBB pixels as an 8-bit RGB PNG
	internal static class PngWriter
	{
		public static void Write(string path, uint[] pixels, int width, int height)
		{
			byte[] raw = new byte[height * (width * 3 + 1)];
			int at = 0;
			for (int y = 0; y < height; y++)
			{
				raw[at++] = 0;
				for (int x = 0; x < width; x++)
				{
					uint pixel = pixels[y * width + x];
					raw[at++] = (byte)(pixel >> 16);
					raw[at++] = (byte)(pixel >> 8);
					raw[at++] = (byte)pixel;
				}
			}
			using MemoryStream compressed = new MemoryStream();
			using (ZLibStream zlib = new ZLibStream(compressed, CompressionLevel.Fastest, true))
				zlib.Write(raw, 0, raw.Length);
			using FileStream file = File.Create(path);
			file.Write(new byte[] { 0x89, 0x50, 0x4E, 0x47, 0x0D, 0x0A, 0x1A, 0x0A });
			byte[] header = new byte[13];
			BinaryPrimitives.WriteInt32BigEndian(header, width);
			BinaryPrimitives.WriteInt32BigEndian(header.AsSpan(4), height);
			header[8] = 8;
			header[9] = 2;
			Chunk(file, "IHDR", header);
			Chunk(file, "IDAT", compressed.ToArray());
			Chunk(file, "IEND", Array.Empty<byte>());
		}

		private static void Chunk(Stream file, string type, byte[] data)
		{
			byte[] length = new byte[4];
			BinaryPrimitives.WriteInt32BigEndian(length, data.Length);
			file.Write(length);
			byte[] typeBytes = System.Text.Encoding.ASCII.GetBytes(type);
			file.Write(typeBytes);
			file.Write(data);
			uint crc = Crc(typeBytes, 0xFFFFFFFF);
			crc = Crc(data, crc) ^ 0xFFFFFFFF;
			byte[] crcBytes = new byte[4];
			BinaryPrimitives.WriteUInt32BigEndian(crcBytes, crc);
			file.Write(crcBytes);
		}

		private static uint Crc(byte[] data, uint crc)
		{
			foreach (byte b in data)
			{
				crc ^= b;
				for (int bit = 0; bit < 8; bit++)
					crc = (crc & 1) != 0 ? (crc >> 1) ^ 0xEDB88320 : crc >> 1;
			}
			return crc;
		}
	}
}
