using System;
using System.Runtime;
using System.Runtime.CompilerServices;

namespace Internal.Runtime.CompilerHelpers
{
	// Stands in for the native runtime's module startup (modeled on dotnet/runtime StartupCodeHelpers.cs, MIT)
	internal static unsafe class StartupCodeHelpers
	{
		internal static void InitializeModules()
		{
			void** start = PhxModulesStart();
			void** end = PhxModulesEnd();

			// The section merge leaves null padding between the bracket symbols and the real entries
			int moduleIndex = 0;
			for (void** current = start; current < end; current++)
			{
				if (*current != null)
					InitializeModule((ReadyToRunHeader*)*current, moduleIndex++);
			}

			// Eager cctors run after every module's statics exist, so they can touch each other's
			for (void** current = start; current < end; current++)
			{
				if (*current != null)
					RunInitializers((ReadyToRunHeader*)*current, ReadyToRunSectionType.EagerCctor);
			}
		}

		internal static void RunModuleInitializers()
		{
			for (void** current = PhxModulesStart(); current < PhxModulesEnd(); current++)
			{
				if (*current != null)
					RunInitializers((ReadyToRunHeader*)*current, ReadyToRunSectionType.ModuleInitializerList);
			}
		}

		private static void InitializeModule(ReadyToRunHeader* header, int moduleIndex)
		{
			// Statics and code below assume this exact format; a new ILC that changes it must fail here, not later
			if (header->Signature != ReadyToRunHeader.ExpectedSignature || header->MajorVersion != ReadyToRunHeader.ExpectedMajorVersion || header->EntrySize != sizeof(ModuleInfoRow))
				Environment.FailFast("FAILFAST: unexpected ReadyToRun module header version");

			if (GetSection(header, ReadyToRunSectionType.DehydratedData, out _) != null)
				Environment.FailFast("FAILFAST: dehydrated data is not supported; build without --dehydrate");

			// The type manager handle is only read by runtime services we don't have yet; the header identifies the module
			TypeManagerSlot* slot = (TypeManagerSlot*)GetSection(header, ReadyToRunSectionType.TypeManagerIndirection, out _);
			if (slot != null)
			{
				slot->TypeManager = header;
				slot->ModuleIndex = moduleIndex;
			}

			byte* statics = (byte*)GetSection(header, ReadyToRunSectionType.GCStaticRegion, out int length);
			if (statics != null)
				InitializeStatics(statics, length);

			// FrozenObjectRegion (string literals, preinitialized objects) needs no work without a GC
		}

		// Replaces each tagged MethodTable slot with a statics object, copying in preinitialized values if any
		private static void InitializeStatics(byte* region, int length)
		{
			int stride = MethodTable.SupportsRelativePointers ? sizeof(int) : sizeof(nint);
			for (byte* block = region; block < region + length; block += stride)
			{
				nint* pBlock = MethodTable.SupportsRelativePointers ? (nint*)ReadRelPtr32(block) : *(nint**)block;
				nint blockAddr = MethodTable.SupportsRelativePointers ? (nint)ReadRelPtr32(pBlock) : *pBlock;
				if ((blockAddr & GCStaticRegionConstants.Uninitialized) == 0)
					continue;

				object obj = Allocator.NewObject((MethodTable*)(blockAddr & ~GCStaticRegionConstants.Mask));
				if ((blockAddr & GCStaticRegionConstants.HasPreInitializedData) != 0)
				{
					void* preinit = MethodTable.SupportsRelativePointers ? ReadRelPtr32((int*)pBlock + 1) : (void*)*(pBlock + 1);
					Buffer.BulkMoveWithWriteBarrier(ref obj.GetRawData(), ref *(byte*)preinit, obj.GetRawObjectDataSize());
				}

				*pBlock = Unsafe.As<object, nint>(ref obj);
			}
		}

		private static void RunInitializers(ReadyToRunHeader* header, ReadyToRunSectionType section)
		{
			byte* initializers = (byte*)GetSection(header, section, out int length);
			int stride = MethodTable.SupportsRelativePointers ? sizeof(int) : sizeof(nint);
			for (byte* current = initializers; current < initializers + length; current += stride)
			{
				delegate*<void> initializer = MethodTable.SupportsRelativePointers ? (delegate*<void>)ReadRelPtr32(current) : *(delegate*<void>*)current;
				initializer();
			}
		}

		private static void* GetSection(ReadyToRunHeader* header, ReadyToRunSectionType id, out int length)
		{
			ModuleInfoRow* rows = (ModuleInfoRow*)(header + 1);
			for (int i = 0; i < header->NumberOfSections; i++)
			{
				if (rows[i].SectionId == (int)id)
				{
					length = rows[i].Length;
					return rows[i].Start;
				}
			}
			length = 0;
			return null;
		}

		private static void* ReadRelPtr32(void* address) => (byte*)address + *(int*)address;

		private struct TypeManagerSlot
		{
			public void* TypeManager;
			public int ModuleIndex;
		}

		// Bracket symbols around .modules$I, defined in Runtime.asm
		[RuntimeImport("*", "PhxModulesStart")]
		[MethodImpl(MethodImplOptions.InternalCall)]
		private static extern void** PhxModulesStart();

		[RuntimeImport("*", "PhxModulesEnd")]
		[MethodImpl(MethodImplOptions.InternalCall)]
		private static extern void** PhxModulesEnd();

		// Transitions between managed and native code. There is no GC to coordinate with, so they are no-ops.
		[RuntimeExport("RhpReversePInvoke")]
		private static void RhpReversePInvoke(nint frame)
		{
		}

		[RuntimeExport("RhpReversePInvokeReturn")]
		private static void RhpReversePInvokeReturn(nint frame)
		{
		}

		[RuntimeExport("RhpPInvoke")]
		private static void RhpPInvoke(nint frame)
		{
		}

		[RuntimeExport("RhpPInvokeReturn")]
		private static void RhpPInvokeReturn(nint frame)
		{
		}

		[RuntimeExport("RhpGcPoll")]
		private static void RhpGcPoll()
		{
		}

		[RuntimeExport("RhpFallbackFailFast")]
		private static void RhpFallbackFailFast() => Environment.FailFast("FAILFAST: runtime fail fast (stack cookie or internal error)");
	}
}
