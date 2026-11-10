using System.Runtime.CompilerServices;

namespace System.Runtime.InteropServices
{
	public enum LayoutKind
	{
		Sequential = 0,
		Explicit = 2,
		Auto = 3,
	}

	public enum CharSet
	{
		None = 1,
		Ansi = 2,
		Unicode = 3,
		Auto = 4,
	}

	// The compiler encodes `where T : unmanaged` as a modreq on this type
	public enum UnmanagedType
	{
		Bool = 0x2,
		I1 = 0x3,
		U1 = 0x4,
		I2 = 0x5,
		U2 = 0x6,
		I4 = 0x7,
		U4 = 0x8,
		I8 = 0x9,
		U8 = 0xA,
		R4 = 0xB,
		R8 = 0xC,
		LPStr = 0x14,
		LPWStr = 0x15,
		SysInt = 0x1F,
		SysUInt = 0x20,
		FunctionPtr = 0x26,
	}

	public enum CallingConvention
	{
		Winapi = 1,
		Cdecl = 2,
		StdCall = 3,
		ThisCall = 4,
		FastCall = 5,
	}

	[AttributeUsage(AttributeTargets.Class | AttributeTargets.Struct, Inherited = false)]
	public sealed class StructLayoutAttribute : Attribute
	{
		public StructLayoutAttribute(LayoutKind layoutKind)
		{
			Value = layoutKind;
		}

		public LayoutKind Value { get; }
		public int Pack;
		public int Size;
		public CharSet CharSet;
	}

	[AttributeUsage(AttributeTargets.Field, Inherited = false)]
	public sealed class FieldOffsetAttribute : Attribute
	{
		public FieldOffsetAttribute(int offset)
		{
			Value = offset;
		}

		public int Value { get; }
	}

	[AttributeUsage(AttributeTargets.Parameter, Inherited = false)]
	public sealed class InAttribute : Attribute
	{
	}

	[AttributeUsage(AttributeTargets.Parameter, Inherited = false)]
	public sealed class OutAttribute : Attribute
	{
	}

	[AttributeUsage(AttributeTargets.Method, Inherited = false)]
	public sealed class UnmanagedCallersOnlyAttribute : Attribute
	{
		public Type[] CallConvs;
		public string EntryPoint;
	}

	[AttributeUsage(AttributeTargets.Method, Inherited = false)]
	public sealed class DllImportAttribute : Attribute
	{
		public DllImportAttribute(string dllName)
		{
			Value = dllName;
		}

		public string Value { get; }
		public string EntryPoint;
		public CharSet CharSet;
		public bool SetLastError;
		public bool ExactSpelling;
		public CallingConvention CallingConvention;
	}

	[AttributeUsage(AttributeTargets.Method, Inherited = false)]
	public sealed class SuppressGCTransitionAttribute : Attribute
	{
	}

	// The IL scanner looks this up even when nothing implements it
	public interface IDynamicInterfaceCastable
	{
		RuntimeTypeHandle GetInterfaceImplementation(RuntimeTypeHandle interfaceType);

		static RuntimeTypeHandle GetDynamicInterfaceImplementation(IDynamicInterfaceCastable instance, RuntimeTypeHandle interfaceType)
			=> instance.GetInterfaceImplementation(interfaceType);
	}

	public static unsafe class MemoryMarshal
	{
		public static ref T GetArrayDataReference<T>(T[] array) => ref Unsafe.As<byte, T>(ref Unsafe.As<RawArrayData>(array).Data);

		public static ref T GetReference<T>(Span<T> span) => ref span._reference;

		public static ref T GetReference<T>(ReadOnlySpan<T> span) => ref span._reference;

		public static Span<T> CreateSpan<T>(ref T reference, int length) => new Span<T>(ref reference, length);

		public static ReadOnlySpan<T> CreateReadOnlySpan<T>(ref readonly T reference, int length) => new ReadOnlySpan<T>(ref Unsafe.AsRef(in reference), length);

		public static Span<byte> AsBytes<T>(Span<T> span) where T : unmanaged =>
			new Span<byte>(ref Unsafe.As<T, byte>(ref span._reference), checked(span.Length * sizeof(T)));

		public static ReadOnlySpan<byte> AsBytes<T>(ReadOnlySpan<T> span) where T : unmanaged =>
			new ReadOnlySpan<byte>(ref Unsafe.As<T, byte>(ref span._reference), checked(span.Length * sizeof(T)));

		public static Span<TTo> Cast<TFrom, TTo>(Span<TFrom> span) where TFrom : unmanaged where TTo : unmanaged =>
			new Span<TTo>(ref Unsafe.As<TFrom, TTo>(ref span._reference), (int)((ulong)span.Length * (ulong)sizeof(TFrom) / (ulong)sizeof(TTo)));

		public static ReadOnlySpan<TTo> Cast<TFrom, TTo>(ReadOnlySpan<TFrom> span) where TFrom : unmanaged where TTo : unmanaged =>
			new ReadOnlySpan<TTo>(ref Unsafe.As<TFrom, TTo>(ref span._reference), (int)((ulong)span.Length * (ulong)sizeof(TFrom) / (ulong)sizeof(TTo)));

		public static T Read<T>(ReadOnlySpan<byte> source) where T : unmanaged
		{
			if (sizeof(T) > source.Length)
				ThrowHelper.ThrowArgumentOutOfRange();
			return Unsafe.ReadUnaligned<T>(ref source._reference);
		}

		public static bool TryRead<T>(ReadOnlySpan<byte> source, out T value) where T : unmanaged
		{
			if (sizeof(T) > source.Length)
			{
				value = default;
				return false;
			}
			value = Unsafe.ReadUnaligned<T>(ref source._reference);
			return true;
		}

		public static void Write<T>(Span<byte> destination, in T value) where T : unmanaged
		{
			if (sizeof(T) > destination.Length)
				ThrowHelper.ThrowArgumentOutOfRange();
			Unsafe.WriteUnaligned(ref destination._reference, value);
		}
	}
}

namespace System.Runtime
{
	// Exports a managed method under a fixed native symbol name (used for compiler helpers and entry points)
	[AttributeUsage(AttributeTargets.Method)]
	internal sealed class RuntimeExportAttribute : Attribute
	{
		public RuntimeExportAttribute(string entry)
		{
		}
	}

	// Binds an extern method directly to a native symbol, with no transition frame
	[AttributeUsage(AttributeTargets.Method)]
	internal sealed class RuntimeImportAttribute : Attribute
	{
		public RuntimeImportAttribute(string lib)
		{
		}

		public RuntimeImportAttribute(string lib, string entry)
		{
		}
	}
}
