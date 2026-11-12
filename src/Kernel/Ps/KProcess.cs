using System;
using System.Buffers.Binary;
using System.Runtime;
using System.Runtime.CompilerServices;
using Alloy.Abi;
using Alloy.Arch.X64;
using Alloy.Kernel.Arch;
using Alloy.Kernel.Io;
using Alloy.Kernel.Ke;
using Alloy.Kernel.Mm;
using Alloy.Kernel.Ob;
using Alloy.Text;

namespace Alloy.Kernel.Ps
{
	// A user address space with its handles and threads; signaled once every thread has exited
	internal sealed class KProcess : KObject
	{
		private const ulong MinimumStack = 256 * 1024;

		private static int s_nextId;
		private static KProcess s_processes;

		public readonly int Id;
		public readonly string Name;
		public readonly HandleTable Handles = new HandleTable();
		public readonly AddressSpace Space;
		public int ExitCode;
		public bool Exiting;
		public ulong ExceptionDispatcher;
		public ulong Peb;

		// Where this process's relative paths start, absolute and tidy; inherited from the parent, "/" for the first process
		public string CurrentDirectory = "/";

		private readonly ulong _root;
		private ulong _imageBase;
		private ulong _stackSize = MinimumStack;
		private int _threadCount;
		private KUserThread _threads;
		private bool _exited;
		private KProcess _next;

		private KProcess(string name, ulong root)
		{
			Id = ++s_nextId;
			Name = name;
			_root = root;
			Space = new AddressSpace(root);
		}

		public ulong Root => _root;

		public override bool IsSignaled => _exited;

		// The image is the command line's first word, looked up from the current directory, then under /bin, then with .exe added; the file read whole into a heap block the caller frees
		private static bool TryResolve(ReadOnlySpan<char> directory, ReadOnlySpan<char> name, Span<char> path, out int length, out byte[] data)
		{
			length = 0;
			data = null;
			bool bare = name.IndexOf('/') < 0 && name.IndexOf('\\') < 0;
			Span<char> candidate = stackalloc char[300];
			for (int attempt = 0; attempt < 3; attempt++)
			{
				SpanWriter writer = new SpanWriter(candidate);
				if (attempt > 0 && bare)
					writer.Append("/bin/");
				writer.Append(name);
				if (attempt == 2)
					writer.Append(".exe");
				length = Paths.Resolve(directory, writer.Written, path);
				if (length < 0)
					continue;
				data = Storage.LoadFile(path.Slice(0, length));
				if (data != null)
					return true;
			}
			return false;
		}

		// Loads a PE from the root file system into a fresh address space and starts its first thread; null if the image is unusable
		public static KProcess Create(ReadOnlySpan<char> commandLine, ulong argument, ReadOnlySpan<char> currentDirectory, KObject stdInput = null, KObject stdOutput = null, KObject stdError = null)
		{
			int space = commandLine.IndexOf(' ');
			Span<char> resolved = stackalloc char[300];
			if (commandLine.Length == 0 || !TryResolve(currentDirectory, space < 0 ? commandLine : commandLine.Slice(0, space), resolved, out int pathLength, out byte[] data))
				return null;
			KProcess process = CreateFrom(resolved.Slice(0, pathLength), data, commandLine, argument, currentDirectory, stdInput, stdOutput, stdError);
			Heap.Free(data);
			return process;
		}

		private static unsafe KProcess CreateFrom(ReadOnlySpan<char> path, ReadOnlySpan<byte> file, ReadOnlySpan<char> commandLine, ulong argument, ReadOnlySpan<char> currentDirectory, KObject stdInput, KObject stdOutput, KObject stdError)
		{
			if (!PeImage.TryParse(file, out PeImage pe) || pe.Machine != PeImage.MachineAmd64 || pe.ImageBase < AddressSpace.UserStart || pe.ImageBase + pe.SizeOfImage > AddressSpace.UserEnd)
				return null;

			// The kernel lives entirely in PML4 slot 256, so sharing that one entry shares every kernel mapping, present and future
			ulong root = PhysicalMemory.Allocate(1);
			if (root == 0)
				return null;
			((ulong*)BootInfo.ToVirtual(root))[256] = ((ulong*)BootInfo.ToVirtual(KernelSpace.Root))[256];
			int slash = path.LastIndexOf('/');
			KProcess process = new KProcess(new string(path.Slice(slash + 1)), root);
			process.CurrentDirectory = new string(currentDirectory);
			process._stackSize = Math.Max(AlignUp(pe.SizeOfStackReserve, Memory.AllocationGranularity), MinimumStack);
			if (!process.LoadImage(in pe) || !process.CreateProcessBlock(commandLine, stdInput, stdOutput, stdError)
				|| process.StartThread(pe.ImageBase + pe.EntryPoint, argument, 0, false) == null)
			{
				process.Handles.CloseAll();
				process.Destroy();
				return null;
			}

			ulong state = Scheduler.Lock();
			process._next = s_processes;
			s_processes = process;
			Scheduler.Unlock(state);
			return process;
		}

		// A running process by id, or null once it has exited
		public static KProcess Find(int id)
		{
			ulong flags = Scheduler.Lock();
			KProcess process = s_processes;
			while (process != null && process.Id != id)
				process = process._next;
			Scheduler.Unlock(flags);
			return process;
		}

		// Fills the rows for GetProcesses (MetalOS ps); returns how many
		public static int List(Span<ProcessEntry> entries)
		{
			int count = 0;
			ulong flags = Scheduler.Lock();
			for (KProcess process = s_processes; process != null && count < entries.Length; process = process._next)
			{
				ref ProcessEntry entry = ref entries[count++];
				entry = default;
				entry.Id = (uint)process.Id;
				entry.Threads = (uint)process._threadCount;
				entry.VirtualSize = process.Space.CommittedBytes;
				Span<byte> name = entry.Name;
				for (int i = 0; i < process.Name.Length && i < ProcessEntry.MaxName - 1; i++)
					name[i] = (byte)process.Name[i];
			}
			Scheduler.Unlock(flags);
			return count;
		}

		// Maps physical pages this process doesn't own (sections, the framebuffer) somewhere free in its address space
		public ulong MapShared(ulong physical, ulong pages, PageFlags flags)
		{
			ulong address = Space.Reserve(0, pages * MemoryLayout.PageSize, Memory.ReadWrite, RegionKind.Mapped);
			if (address == 0 || !Space.MapPages(address, physical, pages, flags, false))
				return 0;
			return address;
		}

		// A stack (reserved with a guard page below it, the rest committed) and a thread block, then a thread that enters ring 3 at entry
		public KUserThread StartThread(ulong entry, ulong argument, ulong stackSize, bool suspended)
		{
			ulong reserve = (stackSize == 0 ? _stackSize : Math.Max(AlignUp(stackSize, Memory.AllocationGranularity), Memory.AllocationGranularity)) + MemoryLayout.PageSize;
			ulong stack = Space.Reserve(0, reserve, Memory.ReadWrite, RegionKind.Private);
			ulong teb = Space.Reserve(0, Teb.Pages * MemoryLayout.PageSize, Memory.ReadWrite, RegionKind.Private);
			if (stack == 0 || teb == 0 || !Space.Commit(stack + MemoryLayout.PageSize, reserve - MemoryLayout.PageSize, Memory.ReadWrite) || !Space.Commit(teb, Teb.Pages * MemoryLayout.PageSize, Memory.ReadWrite))
				return null;
			ulong stackTop = stack + reserve;

			KUserThread thread = KUserThread.Create(this, entry, stackTop - 40, argument, teb, suspended);
			thread.StackReservation = stack;
			WriteUser(teb + Teb.StackBase, stackTop);
			WriteUser(teb + Teb.StackLimit, stack + MemoryLayout.PageSize);
			WriteUser(teb + Teb.Self, teb);
			WriteUser(teb + Teb.ProcessId, (ulong)Id);
			WriteUser(teb + Teb.ThreadId, (ulong)thread.Id);
			WriteUser(teb + Teb.ProcessBlock, Peb);
			WriteUser(teb + Teb.DeallocationStack, stack);

			ulong flags = Scheduler.Lock();
			_threadCount++;
			thread.NextInProcess = _threads;
			_threads = thread;
			Scheduler.Admit(thread.Thread);
			Scheduler.Unlock(flags);
			return thread;
		}

		// ExitProcess or a fatal fault: every thread leaves the next time it would return to ring 3, and waiting ones are woken to do so
		public void Terminate(int exitCode)
		{
			ulong flags = Scheduler.Lock();
			if (!Exiting)
			{
				ExitCode = exitCode;
				Exiting = true;
			}
			for (KUserThread thread = _threads; thread != null; thread = thread.NextInProcess)
			{
				if (thread.Thread != Scheduler.Current)
					Scheduler.Interrupt(thread.Thread);
			}
			Scheduler.Unlock(flags);
		}

		// Runs on the reaper when one of this process's threads is gone; the last one tears the address space down
		public void ThreadExited(KUserThread thread)
		{
			ulong flags = Scheduler.Lock();
			if (_threads == thread)
			{
				_threads = thread.NextInProcess;
			}
			else
			{
				for (KUserThread at = _threads; at != null; at = at.NextInProcess)
				{
					if (at.NextInProcess == thread)
					{
						at.NextInProcess = thread.NextInProcess;
						break;
					}
				}
			}
			bool last = --_threadCount == 0;
			Scheduler.Unlock(flags);
			if (!last)
			{
				// The stack and TEB go back to the address space now; the last thread's leave with the whole space below
				Space.Free(thread.StackReservation, 0, Memory.Release);
				Space.Free(thread.Teb, 0, Memory.Release);
				return;
			}

			Handles.CloseAll();
			Destroy();
			flags = Scheduler.Lock();
			_exited = true;
			RemoveFromList();
			WakeWaiters();
			Scheduler.Unlock(flags);

			SpanWriter line = new SpanWriter(stackalloc char[96]);
			line.Append("Process ");
			line.Append(Name);
			line.Append(" (");
			line.Append(Id);
			line.Append(") exited with ");
			line.Append(ExitCode);
			Log.WriteLine(ref line);
		}

		// The image's pages go in one physically contiguous run, mapped section by section; every page stays owned by the process
		private unsafe bool LoadImage(in PeImage pe)
		{
			ulong pages = MemoryLayout.PagesFor(pe.SizeOfImage);
			ulong physical = PhysicalMemory.Allocate(pages);
			if (physical == 0)
				return false;
			Span<byte> image = PhysicalMemory.Window(physical, pages * MemoryLayout.PageSize);
			if (!pe.TryLoad(image) || PeImage.Relocate(image, pe.GetDirectory(PeImage.DirectoryBaseRelocation), pe.ImageBase, pe.ImageBase) < 0
				|| Space.Reserve(pe.ImageBase, pages * MemoryLayout.PageSize, Memory.ExecuteWriteCopy, RegionKind.Image) != pe.ImageBase
				|| !Space.MapPages(pe.ImageBase, physical, pages, PageFlags.NoExecute, true))
			{
				PhysicalMemory.Free(physical, pages);
				return false;
			}
			for (int i = 0; i < pe.SectionCount; i++)
			{
				PeSection section = pe.GetSection(i);
				if (section.VirtualSize == 0)
					continue;
				PageFlags flags = (section.IsWritable ? PageFlags.Writable : PageFlags.None) | (section.IsExecutable ? PageFlags.None : PageFlags.NoExecute);
				Space.MapPages(pe.ImageBase + section.VirtualAddress, physical + section.VirtualAddress, MemoryLayout.PagesFor(section.VirtualSize), flags, true);
			}
			_imageBase = pe.ImageBase;
			return true;
		}

		// The PEB: the image base where Windows keeps it, then Alloy's parameters (standard handles, now the child's own, and the command line)
		private bool CreateProcessBlock(ReadOnlySpan<char> commandLine, KObject stdInput, KObject stdOutput, KObject stdError)
		{
			Peb = Space.Reserve(0, MemoryLayout.PageSize, Memory.ReadWrite, RegionKind.Private);
			if (Peb == 0 || !Space.Commit(Peb, MemoryLayout.PageSize, Memory.ReadWrite) || !Space.TryTranslate(Peb, out ulong physical))
				return false;
			Span<byte> block = PhysicalMemory.Window(physical, MemoryLayout.PageSize);
			BinaryPrimitives.WriteUInt64LittleEndian(block.Slice(Abi.Peb.ImageBase), _imageBase);
			BinaryPrimitives.WriteUInt64LittleEndian(block.Slice(Abi.Peb.StdInput), stdInput == null ? 0 : (ulong)Handles.Add(stdInput));
			BinaryPrimitives.WriteUInt64LittleEndian(block.Slice(Abi.Peb.StdOutput), stdOutput == null ? 0 : (ulong)Handles.Add(stdOutput));
			BinaryPrimitives.WriteUInt64LittleEndian(block.Slice(Abi.Peb.StdError), stdError == null ? 0 : (ulong)Handles.Add(stdError));
			int length = Math.Min(commandLine.Length, Abi.Peb.MaxCommandLine);
			BinaryPrimitives.WriteUInt64LittleEndian(block.Slice(Abi.Peb.CommandLineLength), (ulong)length);
			for (int i = 0; i < length; i++)
				block[Abi.Peb.CommandLine + i] = (byte)commandLine[i];
			int nameLength = Math.Min(Name.Length, Abi.Peb.MaxImageName);
			BinaryPrimitives.WriteUInt64LittleEndian(block.Slice(Abi.Peb.ImageNameLength), (ulong)nameLength);
			for (int i = 0; i < nameLength; i++)
				block[Abi.Peb.ImageName + i] = (byte)Name[i];
			return true;
		}

		// Writes through the physical window, since this process's pages may not be the ones mapped right now
		private void WriteUser(ulong address, ulong value)
		{
			if (Space.TryTranslate(address, out ulong physical))
				BinaryPrimitives.WriteUInt64LittleEndian(PhysicalMemory.Window(physical, 8), value);
		}

		private void Destroy()
		{
			Scheduler.UseKernelTables();
			Space.Destroy();
			PhysicalMemory.Free(_root, 1);
		}

		// Interrupts must be off
		private void RemoveFromList()
		{
			if (s_processes == this)
			{
				s_processes = _next;
				return;
			}
			for (KProcess at = s_processes; at != null; at = at._next)
			{
				if (at._next == this)
				{
					at._next = _next;
					return;
				}
			}
		}

		private static ulong AlignUp(ulong value, ulong alignment) => (value + alignment - 1) & ~(alignment - 1);
	}

	// The kernel side of a user thread: its KThread, its user frame at the top of the kernel stack, its TEB, and its suspend count
	internal sealed class KUserThread : KObject
	{
		public readonly KProcess Process;
		public readonly KThread Thread;
		public readonly ulong Teb;
		public KUserThread NextInProcess;
		public int ExitCode = (int)ThreadFlags.StillActive;
		public ulong StackReservation;

		private readonly ulong _entry;
		private readonly ulong _stack;
		private readonly ulong _argument;
		private readonly KEvent _resume = new KEvent(false);
		private int _suspendCount;
		private bool _exited;

		private KUserThread(KProcess process, KThread thread, ulong entry, ulong stack, ulong argument, ulong teb, bool suspended)
		{
			Process = process;
			Thread = thread;
			_entry = entry;
			_stack = stack;
			_argument = argument;
			Teb = teb;
			_suspendCount = suspended ? 1 : 0;
		}

		public int Id => Thread.Id;

		public override bool IsSignaled => _exited;

		// The user context saved by every entry from ring 3, always at the same place: the top of the kernel stack
		public unsafe InterruptFrame* Frame => (InterruptFrame*)(Thread.StackTop - InterruptFrame.Size);

		// Blocked in the kernel on the thread's behalf, rather than stopped in ring 3
		public unsafe bool InSystemCall => Frame->Vector == InterruptFrame.SyscallVector;

		public static unsafe KUserThread Create(KProcess process, ulong entry, ulong stack, ulong argument, ulong teb, bool suspended)
		{
			KThread thread = KThread.Create(process.Name, &Start, null, InterruptFrame.Size);
			KUserThread user = new KUserThread(process, thread, entry, stack, argument, teb, suspended);
			thread.Owner = user;
			return user;
		}

		// Returns the previous suspend count; the thread stops before it next returns to ring 3
		public int Suspend()
		{
			ulong flags = Scheduler.Lock();
			int previous = _suspendCount++;
			Scheduler.Unlock(flags);
			return previous;
		}

		public int Resume()
		{
			ulong flags = Scheduler.Lock();
			int previous = _suspendCount;
			if (_suspendCount > 0 && --_suspendCount == 0)
				_resume.Signal();
			Scheduler.Unlock(flags);
			return previous;
		}

		// Called before every return to ring 3: threads of an ending process leave here, and a suspended thread waits here
		public void ReturningToUser()
		{
			while (true)
			{
				if (Process.Exiting)
					Scheduler.Exit();
				if (System.Threading.Volatile.Read(ref _suspendCount) == 0)
					return;
				_resume.Wait();
			}
		}

		public void Exited()
		{
			ulong flags = Scheduler.Lock();
			_exited = true;
			if (ExitCode == (int)ThreadFlags.StillActive)
				ExitCode = Process.ExitCode;
			WakeWaiters();
			Scheduler.Unlock(flags);
			Process.ThreadExited(this);
		}

		private static unsafe void Start(void* argument)
		{
			KUserThread user = (KUserThread)Scheduler.Current.Owner;
			UserContext.Initialize(user.Frame, user._entry, user._stack, user._argument);
			user.ReturningToUser();
			PhxResumeUser(user.Frame);
		}

		[RuntimeImport("*", "PhxResumeUser")]
		[MethodImpl(MethodImplOptions.InternalCall)]
		private static extern unsafe void PhxResumeUser(InterruptFrame* frame);
	}
}
