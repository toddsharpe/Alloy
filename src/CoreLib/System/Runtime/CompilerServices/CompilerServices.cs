namespace System.Runtime.CompilerServices
{
	public static class RuntimeHelpers
	{
		// Lets C#'s fixed statement work on strings
		public static int OffsetToStringData => string.FIRST_CHAR_OFFSET;

		[Intrinsic]
		public static bool IsReferenceOrContainsReferences<T>() => IsReferenceOrContainsReferences<T>();

		// Constant arrays and spans: ILC expands these when the field handle is a compile-time constant
		[Intrinsic]
		public static void InitializeArray(Array array, RuntimeFieldHandle fldHandle) => ThrowHelper.FailFast("InitializeArray was not expanded by the compiler");

		[Intrinsic]
		public static ReadOnlySpan<T> CreateSpan<T>(RuntimeFieldHandle fldHandle)
		{
			ThrowHelper.FailFast("CreateSpan was not expanded by the compiler");
			return default;
		}
	}

	public static class RuntimeFeature
	{
		public const string UnmanagedSignatureCallingConvention = nameof(UnmanagedSignatureCallingConvention);
		public const string DefaultImplementationsOfInterfaces = nameof(DefaultImplementationsOfInterfaces);
		public const string NumericIntPtr = nameof(NumericIntPtr);
		public const string ByRefFields = nameof(ByRefFields);
		public const string ByRefLikeGenerics = nameof(ByRefLikeGenerics);
	}

	// Contract with the compiler: one pointer, the cctor's address; 0 once it has run
	public struct StaticClassConstructionContext
	{
		public volatile nint cctorMethodAddress;
	}

	[AttributeUsage(AttributeTargets.Class | AttributeTargets.Struct | AttributeTargets.Method | AttributeTargets.Field | AttributeTargets.Property | AttributeTargets.Constructor, Inherited = false)]
	public sealed class IntrinsicAttribute : Attribute
	{
	}

	[AttributeUsage(AttributeTargets.Method | AttributeTargets.Constructor, Inherited = false)]
	public sealed class MethodImplAttribute : Attribute
	{
		public MethodImplAttribute()
		{
		}

		public MethodImplAttribute(MethodImplOptions methodImplOptions)
		{
			Value = methodImplOptions;
		}

		public MethodImplOptions Value { get; }
	}

	[Flags]
	public enum MethodImplOptions
	{
		Unmanaged = 0x0004,
		NoInlining = 0x0008,
		ForwardRef = 0x0010,
		Synchronized = 0x0020,
		NoOptimization = 0x0040,
		PreserveSig = 0x0080,
		AggressiveInlining = 0x0100,
		AggressiveOptimization = 0x0200,
		InternalCall = 0x1000,
	}

	[AttributeUsage(AttributeTargets.Struct)]
	public sealed class InlineArrayAttribute : Attribute
	{
		public InlineArrayAttribute(int length)
		{
			Length = length;
		}

		public int Length { get; }
	}

	[AttributeUsage(AttributeTargets.Module | AttributeTargets.Class | AttributeTargets.Struct | AttributeTargets.Interface | AttributeTargets.Constructor | AttributeTargets.Method | AttributeTargets.Property | AttributeTargets.Event, Inherited = false)]
	public sealed class SkipLocalsInitAttribute : Attribute
	{
	}

	// Runs the class's cctor at startup (EagerCctor section) instead of on first access
	[AttributeUsage(AttributeTargets.Class | AttributeTargets.Struct)]
	public sealed class EagerStaticClassConstructionAttribute : Attribute
	{
	}

	[AttributeUsage(AttributeTargets.Assembly | AttributeTargets.Class | AttributeTargets.Method)]
	public sealed class ExtensionAttribute : Attribute
	{
	}

	[AttributeUsage(AttributeTargets.All, Inherited = true)]
	public sealed class CompilerGeneratedAttribute : Attribute
	{
	}

	[AttributeUsage(AttributeTargets.Field, Inherited = false)]
	public sealed class FixedBufferAttribute : Attribute
	{
		public FixedBufferAttribute(Type elementType, int length)
		{
		}
	}

	[AttributeUsage(AttributeTargets.Struct)]
	public sealed class UnsafeValueTypeAttribute : Attribute
	{
	}

	[AttributeUsage(AttributeTargets.Struct)]
	public sealed class IsByRefLikeAttribute : Attribute
	{
	}

	[AttributeUsage(AttributeTargets.All, Inherited = false)]
	public sealed class IsReadOnlyAttribute : Attribute
	{
	}

	[AttributeUsage(AttributeTargets.All)]
	public sealed class IsUnmanagedAttribute : Attribute
	{
	}

	[AttributeUsage(AttributeTargets.Module, AllowMultiple = false, Inherited = false)]
	public sealed class RefSafetyRulesAttribute : Attribute
	{
		public RefSafetyRulesAttribute(int version)
		{
		}
	}

	[AttributeUsage(AttributeTargets.Parameter, AllowMultiple = false, Inherited = false)]
	public sealed class ScopedRefAttribute : Attribute
	{
	}

	[AttributeUsage(AttributeTargets.Method, Inherited = false)]
	public sealed class ModuleInitializerAttribute : Attribute
	{
	}

	public static class IsExternalInit
	{
	}

	public static class IsVolatile
	{
	}

	public class CallConvCdecl
	{
	}

	public class CallConvStdcall
	{
	}

	public class CallConvSuppressGCTransition
	{
	}
}
