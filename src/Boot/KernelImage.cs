using System;
using Alloy.Abi;
using Alloy.Efi;

namespace Alloy.Boot
{
	// kernel.exe laid out in physically contiguous pages, ready to map at its link base
	internal struct KernelImage
	{
		public ulong Physical;
		public ulong ImageBase;
		public uint SizeOfImage;
		public ulong EntryPoint;
		public ulong File;
		public ulong FileSize;

		public readonly unsafe ReadOnlySpan<byte> FileBytes => new ReadOnlySpan<byte>((void*)File, (int)FileSize);

		public static unsafe KernelImage Load(EfiFileProtocol* root, string path)
		{
			KernelImage kernel = default;
			Uefi.ReadFile(root, path, false, out kernel.File, out kernel.FileSize);

			if (!PeImage.TryParse(kernel.FileBytes, out PeImage pe))
				Environment.FailFast("FAILFAST: kernel.exe is not a valid PE32+ image");
			if (pe.Machine != PeImage.MachineAmd64 || pe.Subsystem != PeImage.SubsystemNative)
				Environment.FailFast("FAILFAST: kernel.exe must be an x64 native-subsystem image");
			if (pe.ImageBase != MemoryLayout.KernelBase || pe.SectionAlignment != MemoryLayout.PageSize)
				Environment.FailFast("FAILFAST: kernel.exe must be linked at KernelBase with page-aligned sections");
			if (pe.GetDirectory(PeImage.DirectoryImport).Size != 0)
				Environment.FailFast("FAILFAST: kernel.exe must not import anything");

			kernel.ImageBase = pe.ImageBase;
			kernel.SizeOfImage = pe.SizeOfImage;
			kernel.EntryPoint = pe.ImageBase + pe.EntryPoint;
			kernel.Physical = Uefi.AllocatePages(MemoryLayout.PagesFor(pe.SizeOfImage));

			Span<byte> memory = new Span<byte>((void*)kernel.Physical, (int)pe.SizeOfImage);
			if (!pe.TryLoad(memory))
				Environment.FailFast("FAILFAST: kernel.exe sections do not fit its image");

			// Loaded at its link base today, so the delta is zero; this validates the table and keeps relocation honest
			if (PeImage.Relocate(memory, pe.GetDirectory(PeImage.DirectoryBaseRelocation), pe.ImageBase, kernel.ImageBase) < 0)
				Environment.FailFast("FAILFAST: kernel.exe has malformed relocations");
			return kernel;
		}
	}
}
