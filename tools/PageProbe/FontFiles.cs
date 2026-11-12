using System;
using System.IO;

namespace Alloy.Sdk.Text
{
	// The host's copy of the SDK's font source: the faces in the repo's build/os/fonts, found by walking up from the executable
	internal static class FontFiles
	{
		public static string Directory = Locate();

		public static byte[] Read(string name)
		{
			if (Directory == null)
				return null;
			string path = Path.Combine(Directory, name + ".ttf");
			return File.Exists(path) ? File.ReadAllBytes(path) : null;
		}

		private static string Locate()
		{
			for (string dir = AppContext.BaseDirectory; dir != null; dir = Path.GetDirectoryName(dir))
			{
				string fonts = Path.Combine(dir, "build", "os", "fonts");
				if (System.IO.Directory.Exists(fonts))
					return fonts;
			}
			return null;
		}
	}
}
