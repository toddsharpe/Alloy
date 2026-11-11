using System;
using System.IO;
using System.IO.Compression;
using System.Linq;
using System.Text;

namespace Alloy.Apps.ZipTest
{
	// Proves the BCL pieces the browser leans on work on Alloy: deflate and gzip from System.IO.Compression, Uri, LINQ and UTF-8
	internal static class Program
	{
		private static int Main()
		{
			byte[] raw = Encoding.UTF8.GetBytes(string.Concat(Enumerable.Repeat("alloy deflate test ", 200)));
			byte[] deflated = Pack(raw, false);
			byte[] gzipped = Pack(raw, true);
			byte[] inflated = Unpack(deflated, false);
			byte[] gunzipped = Unpack(gzipped, true);
			bool deflateOk = inflated.AsSpan().SequenceEqual(raw);
			bool gzipOk = gunzipped.AsSpan().SequenceEqual(raw) && gzipped[0] == 0x1F && gzipped[1] == 0x8B;
			Console.WriteLine($"  {(deflateOk ? "ok  " : "FAIL")} deflate {raw.Length} -> {deflated.Length} -> {inflated.Length}");
			Console.WriteLine($"  {(gzipOk ? "ok  " : "FAIL")} gzip {raw.Length} -> {gzipped.Length} -> {gunzipped.Length}");

			Uri uri = new Uri("http://info.cern.ch:8080/hypertext/WWW/TheProject.html?q=a%20b#top");
			Uri relative = new Uri(uri, "../Other.html");
			bool uriOk = uri.Host == "info.cern.ch" && uri.Port == 8080 && uri.AbsolutePath == "/hypertext/WWW/TheProject.html" && Uri.UnescapeDataString(uri.Query) == "?q=a b" && relative.AbsolutePath == "/hypertext/Other.html";
			Console.WriteLine($"  {(uriOk ? "ok  " : "FAIL")} Uri {uri.Host}:{uri.Port}{uri.AbsolutePath} -> {relative.AbsolutePath}");

			string utf8 = Encoding.UTF8.GetString(new byte[] { 0xC3, 0xA9, 0xE2, 0x82, 0xAC });
			double parsed = double.Parse("12.5em".AsSpan(0, 4));
			bool textOk = utf8 == "é€" && parsed == 12.5 && $"{parsed:F2}" == "12.50" && raw.Sum(b => (long)b) > 0;
			Console.WriteLine($"  {(textOk ? "ok  " : "FAIL")} UTF-8 {utf8.Length} chars, double {parsed:F2}");
			return deflateOk && gzipOk && uriOk && textOk ? 0 : 1;
		}

		private static byte[] Pack(byte[] data, bool gzip)
		{
			using MemoryStream packed = new MemoryStream();
			using (Stream compressor = gzip ? new GZipStream(packed, CompressionLevel.Optimal, true) : new DeflateStream(packed, CompressionLevel.Optimal, true))
				compressor.Write(data, 0, data.Length);
			return packed.ToArray();
		}

		private static byte[] Unpack(byte[] data, bool gzip)
		{
			using MemoryStream packed = new MemoryStream(data);
			using MemoryStream unpacked = new MemoryStream();
			using (Stream decompressor = gzip ? new GZipStream(packed, CompressionMode.Decompress) : new DeflateStream(packed, CompressionMode.Decompress))
				decompressor.CopyTo(unpacked);
			return unpacked.ToArray();
		}
	}
}
