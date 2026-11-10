using System;
using Alloy.Abi;
using Alloy.Efi;
using Alloy.Text;

namespace Alloy.Boot
{
	// Boot services wrappers for the loader; every failure is fatal since there is nothing to fall back to
	internal static class Uefi
	{
		private static unsafe EfiBootServices* BootServices => EfiApplication.SystemTable->BootServices;

		public static void Check(ulong status, string what)
		{
			if (!EfiStatus.IsError(status))
				return;

			// Logged from a stack buffer so this also works after ExitBootServices, when nothing can allocate
			SpanWriter line = new SpanWriter(stackalloc char[128]);
			line.Append("FAILFAST: ");
			line.Append(what);
			line.Append(" failed, status 0x");
			line.AppendHex(status, 16);
			Log.WriteLine(ref line);
			Environment.FailFast("FAILFAST: UEFI call failed");
		}

		public static unsafe ulong AllocatePages(ulong pages)
		{
			ulong address;
			Check(BootServices->AllocatePages(EfiAllocateType.AnyPages, EfiMemoryType.LoaderData, (nuint)pages, &address), "AllocatePages");
			return address;
		}

		public static unsafe ulong AllocateZeroedPages(ulong pages)
		{
			ulong address = AllocatePages(pages);
			new Span<byte>((void*)address, (int)(pages * MemoryLayout.PageSize)).Clear();
			return address;
		}

		// Page table allocator for PageTableBuilder; firmware memory is identity mapped, so physical is directly usable
		public static ulong AllocatePageTable() => AllocateZeroedPages(1);

		public static unsafe T* OpenProtocol<T>(void* handle, EfiGuid guid, string what) where T : unmanaged
		{
			void* protocol;
			Check(BootServices->HandleProtocol(handle, &guid, &protocol), what);
			return (T*)protocol;
		}

		public static unsafe EfiFileProtocol* OpenBootVolume()
		{
			EfiLoadedImageProtocol* image = OpenProtocol<EfiLoadedImageProtocol>(EfiApplication.ImageHandle, EfiGuids.LoadedImageProtocol, "LoadedImage");
			EfiSimpleFileSystemProtocol* fileSystem = OpenProtocol<EfiSimpleFileSystemProtocol>(image->DeviceHandle, EfiGuids.SimpleFileSystemProtocol, "SimpleFileSystem");
			EfiFileProtocol* root;
			Check(fileSystem->OpenVolume(fileSystem, &root), "OpenVolume");
			return root;
		}

		// Reads a whole file into fresh pages; returns false only when the file is optional and missing
		public static unsafe bool ReadFile(EfiFileProtocol* root, string path, bool optional, out ulong address, out ulong size)
		{
			address = 0;
			size = 0;

			EfiFileProtocol* file;
			ulong status;
			fixed (char* name = path)
				status = root->Open(root, &file, name, EfiFileProtocol.ModeRead, 0);
			if (status == EfiStatus.NotFound && optional)
				return false;
			Check(status, path);

			byte* info = stackalloc byte[512];
			nuint infoSize = 512;
			EfiGuid fileInfo = EfiGuids.FileInfo;
			Check(file->GetInfo(file, &fileInfo, &infoSize, info), "GetInfo");
			size = ((EfiFileInfo*)info)->FileSize;

			address = AllocatePages(MemoryLayout.PagesFor(size == 0 ? 1 : size));
			nuint read = (nuint)size;
			Check(file->Read(file, &read, (void*)address), "Read");
			if (read != size)
				Environment.FailFast(string.Concat("FAILFAST: short read of ", path));
			file->Close(file);
			return true;
		}

		public static unsafe void* FindConfigurationTable(EfiGuid guid)
		{
			EfiSystemTable* st = EfiApplication.SystemTable;
			for (nuint i = 0; i < st->NumberOfTableEntries; i++)
			{
				if (st->ConfigurationTable[i].VendorGuid.Equals(guid))
					return st->ConfigurationTable[i].VendorTable;
			}
			return null;
		}
	}
}
