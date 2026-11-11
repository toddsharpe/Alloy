using System;

namespace Alloy.Kernel.Io
{
	// Absolute paths from a process's current directory: '/' or '\' between names, "." stays put, ".." goes up and no further than the root
	internal static class Paths
	{
		public const int MaxLength = 255;

		// The path made absolute against the directory and tidied, as a length into result; -1 if it doesn't fit
		public static int Resolve(ReadOnlySpan<char> directory, ReadOnlySpan<char> path, Span<char> result)
		{
			int length = 0;
			if (path.Length == 0 || !IsSeparator(path[0]))
			{
				length = Append(directory, result, 0);
				if (length < 0)
					return -1;
			}
			length = Append(path, result, length);
			if (length != 0)
				return length;

			// The root is the one directory whose path has a separator and no name
			if (result.Length == 0)
				return -1;
			result[0] = '/';
			return 1;
		}

		// Each name of the path onto the one in result, which starts out as what was built so far
		private static int Append(ReadOnlySpan<char> path, Span<char> result, int length)
		{
			int at = 0;
			while (at < path.Length)
			{
				if (IsSeparator(path[at]))
				{
					at++;
					continue;
				}

				int start = at;
				while (at < path.Length && !IsSeparator(path[at]))
					at++;
				ReadOnlySpan<char> name = path.Slice(start, at - start);
				if (name.Length == 1 && name[0] == '.')
					continue;
				if (name.Length == 2 && name[0] == '.' && name[1] == '.')
				{
					while (length > 0 && result[length - 1] != '/')
						length--;
					if (length > 0)
						length--;
					continue;
				}

				if (length + 1 + name.Length > MaxLength || length + 1 + name.Length > result.Length)
					return -1;
				result[length++] = '/';
				name.CopyTo(result.Slice(length));
				length += name.Length;
			}
			return length;
		}

		private static bool IsSeparator(char c) => c == '/' || c == '\\';
	}
}
