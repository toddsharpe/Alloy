using System.Runtime.CompilerServices;
using System.Runtime.InteropServices;

namespace System
{
	// Contract with the compiler: the element count is the first field, padded to pointer size, then the elements
	public abstract class Array
	{
		private int _numComponents;

		public int Length => (int)Unsafe.As<RawArrayData>(this).Length;
	}

	// The compiler requires this type to exist; arrays of T derive from it
	public class Array<T> : Array
	{
	}

	[StructLayout(LayoutKind.Sequential)]
	internal class RawArrayData
	{
		public uint Length;
		public uint Padding;
		public byte Data;
	}
}
