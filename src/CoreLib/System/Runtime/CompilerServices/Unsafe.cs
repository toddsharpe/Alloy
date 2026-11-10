namespace System.Runtime.CompilerServices
{
	// ILC replaces these bodies (UnsafeIntrinsics.cs); a self-recursive body running means it stopped recognizing one
	public static class Unsafe
	{
		[Intrinsic]
		public static unsafe void* AsPointer<T>(ref readonly T value) => AsPointer(in value);

		[Intrinsic]
		public static int SizeOf<T>() => SizeOf<T>();

		[Intrinsic]
		public static T As<T>(object o) where T : class => As<T>(o);

		[Intrinsic]
		public static ref TTo As<TFrom, TTo>(ref TFrom source) => ref As<TFrom, TTo>(ref source);

		[Intrinsic]
		public static unsafe ref T AsRef<T>(void* source) => ref AsRef<T>(source);

		[Intrinsic]
		public static ref T AsRef<T>(ref readonly T source) => ref AsRef(in source);

		[Intrinsic]
		public static ref T Add<T>(ref T source, int elementOffset) => ref Add(ref source, elementOffset);

		[Intrinsic]
		public static ref T Add<T>(ref T source, nint elementOffset) => ref Add(ref source, elementOffset);

		[Intrinsic]
		public static ref T Add<T>(ref T source, nuint elementOffset) => ref Add(ref source, elementOffset);

		[Intrinsic]
		public static unsafe void* Add<T>(void* source, int elementOffset) => Add<T>(source, elementOffset);

		[Intrinsic]
		public static ref T AddByteOffset<T>(ref T source, nint byteOffset) => ref AddByteOffset(ref source, byteOffset);

		[Intrinsic]
		public static ref T AddByteOffset<T>(ref T source, nuint byteOffset) => ref AddByteOffset(ref source, byteOffset);

		[Intrinsic]
		public static ref T Subtract<T>(ref T source, int elementOffset) => ref Subtract(ref source, elementOffset);

		[Intrinsic]
		public static ref T Subtract<T>(ref T source, nint elementOffset) => ref Subtract(ref source, elementOffset);

		[Intrinsic]
		public static ref T SubtractByteOffset<T>(ref T source, nint byteOffset) => ref SubtractByteOffset(ref source, byteOffset);

		[Intrinsic]
		public static nint ByteOffset<T>(ref readonly T origin, ref readonly T target) => ByteOffset(in origin, in target);

		[Intrinsic]
		public static bool AreSame<T>(ref readonly T left, ref readonly T right) => AreSame(in left, in right);

		[Intrinsic]
		public static bool IsAddressLessThan<T>(ref readonly T left, ref readonly T right) => IsAddressLessThan(in left, in right);

		[Intrinsic]
		public static bool IsAddressGreaterThan<T>(ref readonly T left, ref readonly T right) => IsAddressGreaterThan(in left, in right);

		[Intrinsic]
		public static ref T NullRef<T>() => ref NullRef<T>();

		[Intrinsic]
		public static bool IsNullRef<T>(ref readonly T source) => IsNullRef(in source);

		[Intrinsic]
		public static unsafe T Read<T>(void* source) => Read<T>(source);

		[Intrinsic]
		public static unsafe void Write<T>(void* destination, T value) => Write(destination, value);

		[Intrinsic]
		public static unsafe T ReadUnaligned<T>(void* source) => ReadUnaligned<T>(source);

		[Intrinsic]
		public static T ReadUnaligned<T>(ref readonly byte source) => ReadUnaligned<T>(in source);

		[Intrinsic]
		public static unsafe void WriteUnaligned<T>(void* destination, T value) => WriteUnaligned(destination, value);

		[Intrinsic]
		public static void WriteUnaligned<T>(ref byte destination, T value) => WriteUnaligned(ref destination, value);

		[Intrinsic]
		public static unsafe void CopyBlock(void* destination, void* source, uint byteCount) => CopyBlock(destination, source, byteCount);

		[Intrinsic]
		public static unsafe void CopyBlockUnaligned(void* destination, void* source, uint byteCount) => CopyBlockUnaligned(destination, source, byteCount);

		[Intrinsic]
		public static unsafe void InitBlock(void* startAddress, byte value, uint byteCount) => InitBlock(startAddress, value, byteCount);

		[Intrinsic]
		public static unsafe void InitBlockUnaligned(void* startAddress, byte value, uint byteCount) => InitBlockUnaligned(startAddress, value, byteCount);

		[Intrinsic]
		public static void SkipInit<T>(out T value) => SkipInit(out value);
	}
}
