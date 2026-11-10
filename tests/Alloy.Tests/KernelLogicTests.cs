using System.Runtime.InteropServices;
using System.Text;
using Alloy.Abi;
using Alloy.Kernel.Mm;
using Alloy.Tools;

namespace Alloy.Tests;

public unsafe class BitmapAllocatorTests
{
	[Fact]
	public void StartsFullAndHandsOutFreedRuns()
	{
		byte* bits = (byte*)NativeMemory.Alloc(16);
		try
		{
			BitmapAllocator pages = new(bits, 100);
			Assert.Equal(0UL, pages.FreeCount);
			Assert.Equal(ulong.MaxValue, pages.Allocate(1));

			pages.MarkFree(10, 20);
			Assert.Equal(20UL, pages.FreeCount);
			Assert.Equal(10UL, pages.Allocate(5));
			Assert.Equal(15UL, pages.Allocate(15));
			Assert.Equal(ulong.MaxValue, pages.Allocate(1));

			pages.MarkFree(12, 2);
			Assert.Equal(ulong.MaxValue, pages.Allocate(3));
			Assert.Equal(12UL, pages.Allocate(2));
		}
		finally
		{
			NativeMemory.Free(bits);
		}
	}

	[Fact]
	public void MarkingIsIdempotentAndClampedToTheCount()
	{
		byte* bits = (byte*)NativeMemory.Alloc(2);
		try
		{
			BitmapAllocator pages = new(bits, 10);
			pages.MarkFree(0, 50);
			pages.MarkFree(3, 2);
			Assert.Equal(10UL, pages.FreeCount);
			pages.MarkUsed(8, 10);
			pages.MarkUsed(8, 2);
			Assert.Equal(8UL, pages.FreeCount);
			Assert.True(pages.IsUsed(9));
			Assert.True(pages.IsUsed(10));
		}
		finally
		{
			NativeMemory.Free(bits);
		}
	}

	[Fact]
	public void WrapsAroundToFindRunsBeforeTheHint()
	{
		byte* bits = (byte*)NativeMemory.Alloc(8);
		try
		{
			BitmapAllocator pages = new(bits, 64);
			pages.MarkFree(0, 64);
			Assert.Equal(0UL, pages.Allocate(64));
			pages.MarkFree(0, 4);
			Assert.Equal(0UL, pages.Allocate(4));
		}
		finally
		{
			NativeMemory.Free(bits);
		}
	}
}

public class SymbolTableTests
{
	[Fact]
	public void ParsesALinkMapAndLooksUpOffsets()
	{
		string[] map =
		[
			" Preferred load address is ffff800001000000",
			"  Address         Publics by Value              Rva+Base               Lib:Object",
			" 0000:00000000       __ImageBase                0000000000000000     <linker-defined>",
			" 0002:00000060       kernel_Alloy_Kernel_Program__Main ffff800001003060 f   kernel.obj",
			" 0002:00000000       KiSystemStartup            ffff800001003000 f   kernel.obj",
			" 0001:00000010       PhxBreakpoint              ffff800001001010 f   Arch.asm.obj",
		];
		byte[] data = SymbolWriter.FromLinkMap(map, "kernel_");

		Assert.True(SymbolTable.TryOpen(data, out SymbolTable table));
		Assert.Equal(3, table.Count);
		Assert.True(table.TryLookup(0x3075, out ReadOnlySpan<byte> name, out uint offset));
		Assert.Equal("Alloy_Kernel_Program__Main", Encoding.ASCII.GetString(name));
		Assert.Equal(0x15u, offset);
		Assert.True(table.TryLookup(0x3000, out name, out offset));
		Assert.Equal("KiSystemStartup", Encoding.ASCII.GetString(name));
		Assert.Equal(0u, offset);
		Assert.False(table.TryLookup(0x1000, out _, out _));
	}

	[Fact]
	public void RejectsTruncatedTables()
	{
		byte[] data = SymbolWriter.Build(new SortedDictionary<uint, string> { [0x1000] = "a", [0x2000] = "b" });
		Assert.True(SymbolTable.TryOpen(data, out _));
		Assert.False(SymbolTable.TryOpen(data.AsSpan(0, 20), out _));
		Assert.False(SymbolTable.TryOpen(new byte[16], out _));
	}
}
