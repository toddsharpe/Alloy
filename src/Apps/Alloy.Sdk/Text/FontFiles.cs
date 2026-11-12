namespace Alloy.Sdk.Text
{
	// Where the faces come from on Alloy: the image's /fonts directory; host tools compile their own copy of this class
	internal static class FontFiles
	{
		public static byte[] Read(string name) => File.ReadAllBytes($"/fonts/{name}.ttf");
	}
}
