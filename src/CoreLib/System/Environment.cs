using System.Diagnostics;

namespace System
{
	public static unsafe partial class Environment
	{
		// Set by the image at startup so a fatal error reaches its log (COM1 for the loader and kernel)
		internal static delegate*<string, void> s_failFastWriter;

		public static void SetFailFastWriter(delegate*<string, void> writer) => s_failFastWriter = writer;

		[DoesNotReturn]
		public static void FailFast(string message)
		{
			// Guard against a failure inside the writer recursing forever
			delegate*<string, void> writer = s_failFastWriter;
			s_failFastWriter = null;
			if (writer != null)
				writer(message ?? "FailFast");
			Halt();
		}

		// Supplied by the platform layer: never returns
		[DoesNotReturn]
		private static partial void Halt();
	}

	// Errors that would be exceptions in .NET; there is no exception support yet
	internal static class ThrowHelper
	{
		[DoesNotReturn]
		internal static void FailFast(string message) => Environment.FailFast(message);

		[DoesNotReturn]
		internal static void ThrowIndexOutOfRange() => Environment.FailFast("FAILFAST: index out of range");

		[DoesNotReturn]
		internal static void ThrowArgumentOutOfRange() => Environment.FailFast("FAILFAST: argument out of range");

		[DoesNotReturn]
		internal static void ThrowArgument(string message) => Environment.FailFast(string.Concat("FAILFAST: invalid argument: ", message));

		[DoesNotReturn]
		internal static void ThrowInvalidCast() => Environment.FailFast("FAILFAST: invalid cast");

		[DoesNotReturn]
		internal static void ThrowArrayTypeMismatch() => Environment.FailFast("FAILFAST: array type mismatch");

		[DoesNotReturn]
		internal static void ThrowOutOfMemory() => Environment.FailFast("FAILFAST: out of memory");
	}
}

namespace System.Diagnostics
{
	[AttributeUsage(AttributeTargets.Method, Inherited = false)]
	public sealed class DoesNotReturnAttribute : Attribute
	{
	}

	public static class Debug
	{
		[Conditional("DEBUG")]
		public static void Assert(bool condition, string message = null)
		{
			if (!condition)
				Environment.FailFast(string.Concat("FAILFAST: assert: ", message ?? "(no message)"));
		}
	}
}
