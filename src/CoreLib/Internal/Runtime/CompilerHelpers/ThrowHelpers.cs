using System;

namespace Internal.Runtime.CompilerHelpers
{
	// Called by compiler-generated code (JitHelper.cs). Without exception support every one of these is fatal.
	internal static class ThrowHelpers
	{
		private static void ThrowOverflowException() => Environment.FailFast("FAILFAST: arithmetic overflow");
		private static void ThrowIndexOutOfRangeException() => Environment.FailFast("FAILFAST: index out of range");
		private static void ThrowNullReferenceException() => Environment.FailFast("FAILFAST: null reference");
		private static void ThrowDivideByZeroException() => Environment.FailFast("FAILFAST: divide by zero");
		private static void ThrowArrayTypeMismatchException() => Environment.FailFast("FAILFAST: array type mismatch");
		private static void ThrowPlatformNotSupportedException() => Environment.FailFast("FAILFAST: platform not supported");
		private static void ThrowNotImplementedException() => Environment.FailFast("FAILFAST: not implemented");
		private static void ThrowNotSupportedException() => Environment.FailFast("FAILFAST: not supported");
		private static void ThrowArgumentException() => Environment.FailFast("FAILFAST: invalid argument");
		private static void ThrowArgumentOutOfRangeException() => Environment.FailFast("FAILFAST: argument out of range");

		// The scanner substitutes these for method bodies it removed or could not compile
		private static void ThrowFeatureBodyRemoved() => Environment.FailFast("FAILFAST: called a method body removed by the compiler");
		private static void ThrowInstanceBodyRemoved() => Environment.FailFast("FAILFAST: called an instance method on a type that is never allocated");
		private static void ThrowBodyRemoved() => Environment.FailFast("FAILFAST: called a method body removed by the compiler");

		// Compile-time binding failures surface here at run time; the id is an ExceptionStringID
		private static void ThrowInvalidProgramException(int id) => Environment.FailFast("FAILFAST: invalid program (ILC could not compile a method)");
		private static void ThrowInvalidProgramExceptionWithArgument(int id, string methodName) => Environment.FailFast(string.Concat("FAILFAST: invalid program: ", methodName));
		private static void ThrowTypeLoadException(int id, string className, string typeName) => Environment.FailFast(string.Concat("FAILFAST: type load: ", typeName));
		private static void ThrowTypeLoadExceptionWithArgument(int id, string className, string typeName, string messageArg) => Environment.FailFast(string.Concat("FAILFAST: type load: ", typeName));
		private static void ThrowMissingMethodException(int id, string methodName) => Environment.FailFast(string.Concat("FAILFAST: missing method: ", methodName));
		private static void ThrowMissingFieldException(int id, string fieldName) => Environment.FailFast(string.Concat("FAILFAST: missing field: ", fieldName));
		private static void ThrowFileNotFoundException(int id, string fileName) => Environment.FailFast(string.Concat("FAILFAST: file not found: ", fileName));
		private static void ThrowBadImageFormatException(int id) => Environment.FailFast("FAILFAST: bad image format");
		private static void ThrowMarshalDirectiveException(int id) => Environment.FailFast("FAILFAST: marshal directive");
		private static void ThrowAmbiguousMatchException(int id) => Environment.FailFast("FAILFAST: ambiguous match");
	}
}
