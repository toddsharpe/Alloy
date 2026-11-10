using System.Runtime.InteropServices;
using Alloy.Abi;

namespace Alloy.Tests;

public class PeImageTests
{
	// The test host is an x64 PE32+ with ASLR, so it carries real DIR64 relocations
	private static byte[] HostImage() => File.ReadAllBytes(Environment.ProcessPath!);

	[Fact]
	public void ParsesARealImage()
	{
		byte[] file = HostImage();
		Assert.True(PeImage.TryParse(file, out PeImage pe));
		Assert.Equal(PeImage.MachineAmd64, pe.Machine);
		Assert.True(pe.SectionCount > 0);
		Assert.True(pe.EntryPoint < pe.SizeOfImage);
		Assert.True(pe.GetDirectory(PeImage.DirectoryBaseRelocation).Size > 0);
	}

	[Fact]
	public void RelocationRoundTripsToTheSameBytes()
	{
		byte[] file = HostImage();
		Assert.True(PeImage.TryParse(file, out PeImage pe));
		byte[] memory = new byte[pe.SizeOfImage];
		Assert.True(pe.TryLoad(memory));
		byte[] original = (byte[])memory.Clone();

		PeDataDirectory relocations = pe.GetDirectory(PeImage.DirectoryBaseRelocation);
		ulong moved = pe.ImageBase + 0x7_0000_0000;
		int count = PeImage.Relocate(memory, relocations, pe.ImageBase, moved);
		Assert.True(count > 0);
		Assert.False(memory.AsSpan().SequenceEqual(original));
		Assert.Equal(count, PeImage.Relocate(memory, relocations, moved, pe.ImageBase));
		Assert.True(memory.AsSpan().SequenceEqual(original));
	}

	[Fact]
	public void LoadsSectionsAtTheirRvas()
	{
		byte[] file = HostImage();
		Assert.True(PeImage.TryParse(file, out PeImage pe));
		byte[] memory = new byte[pe.SizeOfImage];
		Assert.True(pe.TryLoad(memory));

		PeSection first = pe.GetSection(0);
		int length = (int)Math.Min(first.SizeOfRawData, first.VirtualSize);
		Assert.True(memory.AsSpan((int)first.VirtualAddress, length).SequenceEqual(file.AsSpan((int)first.PointerToRawData, length)));
		Assert.False(pe.TryLoad(new byte[pe.SizeOfImage - 1]));
	}

	[Fact]
	public void RejectsMalformedImages()
	{
		byte[] file = HostImage();
		Assert.False(PeImage.TryParse(file.AsSpan(0, 0x40), out _));
		Assert.False(PeImage.TryParse(new byte[4096], out _));

		byte[] badNtOffset = (byte[])file.Clone();
		MemoryMarshal.Write(badNtOffset.AsSpan(0x3C), file.Length);
		Assert.False(PeImage.TryParse(badNtOffset, out _));

		// A relocation block claiming more bytes than the directory holds must be refused, not walked
		Assert.True(PeImage.TryParse(file, out PeImage pe));
		byte[] memory = new byte[pe.SizeOfImage];
		Assert.True(pe.TryLoad(memory));
		PeDataDirectory relocations = pe.GetDirectory(PeImage.DirectoryBaseRelocation);
		MemoryMarshal.Write(memory.AsSpan((int)relocations.VirtualAddress + 4), relocations.Size + 8);
		Assert.Equal(-1, PeImage.Relocate(memory, relocations, pe.ImageBase, pe.ImageBase + 0x1000));
	}

	// Checks what the loader enforces on the real kernel.exe, when a build has produced one
	[Fact]
	public void KernelImageMeetsTheLoadersRules()
	{
		string? kernel = FindKernel();
		if (kernel is null)
			return;

		byte[] file = File.ReadAllBytes(kernel);
		Assert.True(PeImage.TryParse(file, out PeImage pe));
		Assert.Equal(PeImage.SubsystemNative, pe.Subsystem);
		Assert.Equal(MemoryLayout.KernelBase, pe.ImageBase);
		Assert.Equal(MemoryLayout.PageSize, pe.SectionAlignment);
		Assert.Equal(0u, pe.GetDirectory(PeImage.DirectoryImport).Size);
		for (int i = 0; i < pe.SectionCount; i++)
		{
			PeSection section = pe.GetSection(i);
			Assert.False(section.IsWritable && section.IsExecutable);
		}
	}

	private static string? FindKernel()
	{
		for (DirectoryInfo? directory = new(AppContext.BaseDirectory); directory is not null; directory = directory.Parent)
		{
			string candidate = Path.Combine(directory.FullName, "build", "bin", "kernel.exe");
			if (File.Exists(Path.Combine(directory.FullName, "Alloy.sln")))
				return File.Exists(candidate) ? candidate : null;
		}
		return null;
	}
}
