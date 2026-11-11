using System;
using System.Runtime;
using System.Runtime.CompilerServices;
using Alloy.Abi;
using Alloy.Arch.X64;
using Alloy.Kernel.Io;
using Alloy.Kernel.Mm;
using Alloy.Kernel.Ke;
using Alloy.Kernel.Ob;
using Alloy.Kernel.Ps;
using Alloy.Text;

namespace Alloy.Kernel.Arch
{
	// syscall/sysret setup, the per-CPU block behind gs, and the dispatcher for the table in Abi/Syscalls.cs
	internal static class SystemCalls
	{
		public const uint KernelGsBaseMsr = 0xC0000102;
		private const uint EferMsr = 0xC0000080;
		private const uint StarMsr = 0xC0000081;
		private const uint LstarMsr = 0xC0000082;
		private const uint FmaskMsr = 0xC0000084;
		private const uint GsBaseMsr = 0xC0000101;

		// IF, TF, DF, AC and NT are cleared on entry; the entry stub turns interrupts back on once it is on the kernel stack
		private const ulong EntryFlagsMask = 0x47700;

		private struct CpuBlock
		{
			public ulong Self;
			public ulong KernelRsp;
			public ulong UserRsp;
		}

		private static unsafe CpuBlock* s_cpu;

		public static unsafe void Initialize()
		{
			s_cpu = (CpuBlock*)Mm.Heap.Allocate((nuint)sizeof(CpuBlock));
			s_cpu->Self = (ulong)s_cpu;
			Cpu.WriteMsr(GsBaseMsr, (ulong)s_cpu);
			Cpu.WriteMsr(KernelGsBaseMsr, 0);

			// sysret loads cs = base + 16 and ss = base + 8 from the User32Code selector, hence MetalOS's GDT order
			Cpu.WriteMsr(StarMsr, ((ulong)(Descriptors.User32Code | 3) << 48) | ((ulong)Descriptors.KernelCode << 32));
			Cpu.WriteMsr(LstarMsr, PhxSyscallEntryAddress());
			Cpu.WriteMsr(FmaskMsr, EntryFlagsMask);
			Cpu.WriteMsr(EferMsr, Cpu.ReadMsr(EferMsr) | 1);
		}

		public static unsafe void SetKernelStack(ulong top)
		{
			s_cpu->KernelRsp = top;
			Descriptors.SetKernelStack(top);
		}

		// The frame is the thread's whole user context (Interrupts.cs); the result goes back in its rax
		[RuntimeExport("KiSystemCall")]
		private static unsafe void Dispatch(InterruptFrame* frame)
		{
			KUserThread thread = Scheduler.Current.Owner as KUserThread;
			if (thread == null)
				Environment.FailFast("FAILFAST: syscall from a thread with no process");
			KProcess process = thread.Process;

			long result = Handle(frame, thread, process);
			if (frame->Vector == InterruptFrame.SyscallVector)
				frame->Rax = (ulong)result;
			thread.ReturningToUser();
		}

		private static unsafe long Handle(InterruptFrame* frame, KUserThread thread, KProcess process)
		{
			switch ((Syscall)frame->Number)
			{
				case Syscall.GetTickCount:
					return (long)Scheduler.UptimeMs;

				case Syscall.DebugPrint:
					return DebugPrint(process, frame->A0, frame->A1);

				case Syscall.ExitProcess:
					process.Terminate((int)frame->A0);
					Scheduler.Exit();
					return 0;

				case Syscall.QueryTime:
					return (long)Platform.HyperV.ReferenceTime();

				case Syscall.GetMemoryStatus:
				{
					MemoryStatus status = new MemoryStatus { TotalPhysical = PhysicalMemory.TotalPages * MemoryLayout.PageSize, AvailablePhysical = PhysicalMemory.FreePages * MemoryLayout.PageSize };
					return UserMemory.CopyTo(frame->A0, &status, (ulong)sizeof(MemoryStatus)) ? 0 : Error(SyscallError.InvalidPointer);
				}

				case Syscall.PublishValue:
				{
					// A key and value for the host's key-value pair exchange; an empty value is allowed, an empty key isn't
					Span<char> key = stackalloc char[256];
					Span<char> value = stackalloc char[256];
					int valueLength = 0;
					if (!UserMemory.ReadPath(frame->A0, frame->A1, key, out int keyLength) || (frame->A3 != 0 && !UserMemory.ReadPath(frame->A2, frame->A3, value, out valueLength)))
						return Error(SyscallError.InvalidPointer);
					return Platform.KvpComponent.Publish(key.Slice(0, keyLength), value.Slice(0, valueLength)) ? 0 : Error(SyscallError.InvalidArgument);
				}

				case Syscall.SetExceptionDispatcher:
					process.ExceptionDispatcher = frame->A0;
					return 0;

				case Syscall.GetExitCode:
				{
					KObject target = process.Handles.Get(frame->A0);
					if (target is KProcess other)
						return other.IsSignaled ? other.ExitCode : ThreadFlags.StillActive;
					if (target is KUserThread user)
						return user.IsSignaled ? user.ExitCode : ThreadFlags.StillActive;
					return Error(SyscallError.InvalidHandle);
				}

				case Syscall.CreateProcess:
					return CreateProcess(process, frame->A0, frame->A1, frame->A2, frame->A3);

				case Syscall.GetProcesses:
				{
					const int MaxEntries = 64;
					ulong count = Math.Min(frame->A1, MaxEntries);
					ProcessEntry* entries = stackalloc ProcessEntry[MaxEntries];
					int listed = KProcess.List(entries, (int)count);
					return UserMemory.CopyTo(frame->A0, entries, (ulong)(listed * sizeof(ProcessEntry))) ? listed : Error(SyscallError.InvalidPointer);
				}

				case Syscall.GetProcessId:
					return process.Id;

				case Syscall.OpenProcess:
				{
					KProcess target = KProcess.Find((int)frame->A0);
					return target == null ? Error(SyscallError.NotFound) : process.Handles.Add(target);
				}

				case Syscall.GetSystemTime:
				{
					SystemTime time = Clock.Now();
					return UserMemory.CopyTo(frame->A0, &time, (ulong)sizeof(SystemTime)) ? 0 : Error(SyscallError.InvalidPointer);
				}

				case Syscall.Sleep:
					Scheduler.Sleep(frame->A0);
					return 0;

				case Syscall.Yield:
					Scheduler.Yield();
					return 0;

				case Syscall.CreateThread:
				{
					KUserThread created = process.StartThread(frame->A0, frame->A1, frame->A2, (frame->A3 & ThreadFlags.Suspended) != 0);
					return created == null ? Error(SyscallError.OutOfMemory) : process.Handles.Add(created);
				}

				case Syscall.ExitThread:
					thread.ExitCode = (int)frame->A0;
					Scheduler.Exit();
					return 0;

				case Syscall.OpenCurrentThread:
					return process.Handles.Add(thread);

				case Syscall.GetThreadId:
					return process.Handles.Get(frame->A0) is KUserThread identified ? identified.Id : Error(SyscallError.InvalidHandle);

				case Syscall.SuspendThread:
				case Syscall.ResumeThread:
				{
					if (process.Handles.Get(frame->A0) is not KUserThread target || target.Process != process)
						return Error(SyscallError.InvalidHandle);
					return (Syscall)frame->Number == Syscall.SuspendThread ? target.Suspend() : target.Resume();
				}

				case Syscall.GetThreadContext:
				case Syscall.SetThreadContext:
					return ThreadContext(process, thread, frame);

				case Syscall.OpenFile:
				{
					// FAT32 matches names in any case, so the path keeps the case a new file is given
					Span<char> path = stackalloc char[256];
					if (!ReadResolvedPath(process, frame->A0, frame->A1, path, out int length))
						return Error(SyscallError.InvalidPointer);
					FileAccess access = (FileAccess)frame->A2;
					FileCreate create = (FileCreate)frame->A3;
					if ((access & FileAccess.ReadWrite) == 0 || (access & ~FileAccess.ReadAppend) != 0 || (ulong)create > (ulong)FileCreate.OpenAlways)
						return Error(SyscallError.InvalidArgument);
					KFile file = Storage.Open(path.Slice(0, length), access, create);
					return file == null ? Error(SyscallError.NotFound) : process.Handles.Add(file);
				}

				case Syscall.WriteFile:
				{
					if (process.Handles.Get(frame->A0) is KPipeEnd writer && writer.Writes)
						return WritePipe(writer, frame->A1, frame->A2);
					if (process.Handles.Get(frame->A0) is KFile file)
						return WriteFile(file, frame->A1, frame->A2);
					return Error(SyscallError.InvalidHandle);
				}

				case Syscall.DeleteFile:
				{
					Span<char> path = stackalloc char[256];
					if (!ReadResolvedPath(process, frame->A0, frame->A1, path, out int length))
						return Error(SyscallError.InvalidPointer);
					return Storage.Delete(path.Slice(0, length)) ? 0 : Error(SyscallError.NotFound);
				}

				case Syscall.MoveFile:
				{
					Span<char> from = stackalloc char[256];
					Span<char> to = stackalloc char[256];
					if (!ReadResolvedPath(process, frame->A0, frame->A1, from, out int fromLength) || !ReadResolvedPath(process, frame->A2, frame->A3, to, out int toLength))
						return Error(SyscallError.InvalidPointer);
					return Storage.Move(from.Slice(0, fromLength), to.Slice(0, toLength)) ? 0 : Error(SyscallError.NotFound);
				}

				case Syscall.CreateDirectory:
				{
					Span<char> path = stackalloc char[256];
					if (!ReadResolvedPath(process, frame->A0, frame->A1, path, out int length))
						return Error(SyscallError.InvalidPointer);
					return Storage.CreateDirectory(path.Slice(0, length)) ? 0 : Error(SyscallError.NotFound);
				}

				case Syscall.ListDirectory:
					return ListDirectory(process, frame->A0, frame->A1, frame->A2);

				case Syscall.GetCurrentDirectory:
				{
					// The whole length comes back however much fit, so a caller can size its buffer
					string directory = process.CurrentDirectory;
					byte* bytes = stackalloc byte[256];
					int length = Math.Min(directory.Length, 255);
					for (int i = 0; i < length; i++)
						bytes[i] = (byte)directory[i];
					return UserMemory.CopyTo(frame->A0, bytes, Math.Min((ulong)length, frame->A1)) ? length : Error(SyscallError.InvalidPointer);
				}

				case Syscall.SetCurrentDirectory:
				{
					Span<char> path = stackalloc char[256];
					if (!ReadResolvedPath(process, frame->A0, frame->A1, path, out int length))
						return Error(SyscallError.InvalidPointer);
					if (!Storage.IsDirectory(path.Slice(0, length)))
						return Error(SyscallError.NotFound);
					process.CurrentDirectory = new string(path.Slice(0, length));
					return 0;
				}

				case Syscall.CreatePipe:
				{
					KPipe pipe = KPipe.Create();
					ulong* ends = stackalloc ulong[2];
					ends[0] = (ulong)process.Handles.Add(pipe.Reader);
					ends[1] = (ulong)process.Handles.Add(pipe.Writer);
					if (UserMemory.CopyTo(frame->A0, ends, 16))
						return 0;
					process.Handles.Remove(ends[0]);
					process.Handles.Remove(ends[1]);
					return Error(SyscallError.InvalidPointer);
				}

				case Syscall.PeekPipe:
				{
					// Bytes waiting, or -Closed once the writer is gone and nothing is left (MetalOS PeekNamedPipe)
					if (process.Handles.Get(frame->A0) is not KPipeEnd reader || reader.Writes)
						return Error(SyscallError.InvalidHandle);
					int count = reader.Pipe.Count;
					return count > 0 || reader.Pipe.WriterOpen ? count : Error(SyscallError.Closed);
				}

				case Syscall.ReadFile:
				{
					if (process.Handles.Get(frame->A0) is KPipeEnd pipeEnd && !pipeEnd.Writes)
						return ReadPipe(pipeEnd, frame->A1, frame->A2);
					if (process.Handles.Get(frame->A0) is KFile file)
						return ReadFile(file, frame->A1, frame->A2);
					return Error(SyscallError.InvalidHandle);
				}

				case Syscall.SeekFile:
					return process.Handles.Get(frame->A0) is KFile seekable ? seekable.Seek((long)frame->A1, (SeekOrigin)frame->A2) : Error(SyscallError.InvalidHandle);

				case Syscall.GetFileSize:
					return process.Handles.Get(frame->A0) is KFile sized ? sized.Size : Error(SyscallError.InvalidHandle);

				case Syscall.CloseHandle:
					return process.Handles.Remove(frame->A0) ? 0 : Error(SyscallError.InvalidHandle);

				case Syscall.WaitForObject:
				{
					KObject target = process.Handles.Get(frame->A0);
					if (target == null)
						return Error(SyscallError.InvalidHandle);
					return target.Wait(frame->A1) ? 0 : Error(SyscallError.Timeout);
				}

				case Syscall.CreateEvent:
					return process.Handles.Add(new KEvent(frame->A0 != 0, frame->A1 != 0));

				case Syscall.SetEvent:
				case Syscall.ResetEvent:
				{
					if (process.Handles.Get(frame->A0) is not KEvent kevent)
						return Error(SyscallError.InvalidHandle);
					if ((Syscall)frame->Number == Syscall.SetEvent)
						kevent.Signal();
					else
						kevent.Reset();
					return 0;
				}

				case Syscall.WaitForMultiple:
					return WaitForMultiple(process, frame->A0, frame->A1, frame->A2);

				case Syscall.CreateSharedMemory:
				{
					if (frame->A0 == 0 || frame->A0 > 1UL << 30)
						return Error(SyscallError.InvalidArgument);
					KSection section = KSection.Create(frame->A0);
					return section == null ? Error(SyscallError.OutOfMemory) : process.Handles.Add(section);
				}

				case Syscall.MapSharedMemory:
				{
					if (process.Handles.Get(frame->A0) is not KSection section)
						return Error(SyscallError.InvalidHandle);
					section.AddReference();
					return (long)process.MapShared(section.Physical, section.Pages, PageFlags.Writable | PageFlags.NoExecute);
				}

				case Syscall.MapFramebuffer:
					return MapFramebuffer(process, frame->A0);

				case Syscall.FlushFramebuffer:
					Platform.SyntheticVideo.Active?.Flush();
					return 0;

				case Syscall.NetSend:
				{
					// Copied out of user memory first; the NIC copies it again into a send section
					Platform.SyntheticNic nic = Platform.SyntheticNic.Active;
					if (nic == null)
						return Error(SyscallError.NotFound);
					if (frame->A1 == 0 || frame->A1 > NetFrame.Mtu)
						return Error(SyscallError.InvalidArgument);
					byte* data = stackalloc byte[NetFrame.Mtu];
					if (!UserMemory.CopyFrom(data, frame->A0, frame->A1))
						return Error(SyscallError.InvalidPointer);
					return nic.SendFrame(data, (int)frame->A1) ? 0 : Error(SyscallError.Busy);
				}

				case Syscall.GetInterfaces:
				{
					Platform.SyntheticNic nic = Platform.SyntheticNic.Active;
					if (nic == null || frame->A1 == 0)
						return 0;
					InterfaceInfo info = default;
					info.Present = 1;
					for (int i = 0; i < 6; i++)
						info.Mac[i] = nic.Mac[i];
					return UserMemory.CopyTo(frame->A0, &info, (ulong)sizeof(InterfaceInfo)) ? 1 : Error(SyscallError.InvalidPointer);
				}

				case Syscall.CreatePort:
					return process.Handles.Add(new KPort());

				case Syscall.SendMessage:
					return SendMessage(process, frame->A0, frame->A1, frame->A2);

				case Syscall.ReceiveMessage:
					return ReceiveMessage(process, frame->A0, frame->A1, frame->A2);

				case Syscall.RegisterEndpoint:
				{
					Span<char> name = stackalloc char[256];
					if (!UserMemory.ReadPath(frame->A0, frame->A1, name, out int length))
						return Error(SyscallError.InvalidPointer);
					KObject published = process.Handles.Get(frame->A2);
					if (published == null)
						return Error(SyscallError.InvalidHandle);
					return Endpoints.Register(name.Slice(0, length), published) ? 0 : Error(SyscallError.Busy);
				}

				case Syscall.ConnectEndpoint:
				{
					Span<char> name = stackalloc char[256];
					if (!UserMemory.ReadPath(frame->A0, frame->A1, name, out int length))
						return Error(SyscallError.InvalidPointer);
					KObject found = Endpoints.Find(name.Slice(0, length));
					return found == null ? Error(SyscallError.NotFound) : process.Handles.Add(found);
				}

				case Syscall.VirtualAlloc:
				{
					ulong address = process.Space.Allocate(frame->A0, frame->A1, (uint)frame->A2, (uint)frame->A3);
					return address == 0 ? Error(SyscallError.OutOfMemory) : (long)address;
				}

				case Syscall.VirtualFree:
					return process.Space.Free(frame->A0, frame->A1, (uint)frame->A2) ? 0 : Error(SyscallError.InvalidArgument);

				case Syscall.VirtualProtect:
				{
					uint old = process.Space.Protect(frame->A0, frame->A1, (uint)frame->A2);
					return old != 0 ? old : Error(SyscallError.InvalidArgument);
				}

				case Syscall.VirtualQuery:
				{
					MemoryBasicInformation info;
					if (!process.Space.Query(frame->A0, &info))
						return Error(SyscallError.InvalidArgument);
					return UserMemory.CopyTo(frame->A1, &info, (ulong)sizeof(MemoryBasicInformation)) ? 0 : Error(SyscallError.InvalidPointer);
				}

				default:
					return Error(SyscallError.NotImplemented);
			}
		}

		// A blocking loop gives up when the process is being torn down, so the thread can leave
		private static bool Terminating => Scheduler.Current.Owner is KUserThread user && user.Process.Exiting;

		// GetThreadContext and SetThreadContext on a sibling thread's saved user frame; CONTEXT_EXCEPTION_REQUEST also reports whether it is in a syscall
		private static unsafe long ThreadContext(KProcess process, KUserThread caller, InterruptFrame* frame)
		{
			if (process.Handles.Get(frame->A0) is not KUserThread target || target.Process != process || target == caller)
				return Error(SyscallError.InvalidHandle);
			byte* buffer = stackalloc byte[WindowsContext.Size];
			WindowsContext* context = (WindowsContext*)buffer;
			if (!UserMemory.CopyFrom(context, frame->A1, WindowsContext.Size))
				return Error(SyscallError.InvalidPointer);
			if ((Syscall)frame->Number == Syscall.SetThreadContext)
			{
				UserContext.FromContext(context, target.Frame);
				return 0;
			}
			uint requested = context->ContextFlags;
			uint flags = requested & ~(WindowsContext.ExceptionRequest | WindowsContext.ExceptionReporting | WindowsContext.ServiceActive | WindowsContext.ExceptionActive);
			if ((requested & WindowsContext.ExceptionRequest) != 0)
				flags |= WindowsContext.ExceptionReporting | (target.InSystemCall ? WindowsContext.ServiceActive : 0);
			UserContext.ToContext(target.Frame, context, flags);
			return UserMemory.CopyTo(frame->A1, context, WindowsContext.Size) ? 0 : Error(SyscallError.InvalidPointer);
		}

		// Hands the screen to one user process (the window server) at the largest synthvid mode; the kernel console stops drawing
		private static unsafe long MapFramebuffer(KProcess process, ulong infoAddress)
		{
			BootInfo* boot = Alloy.Kernel.KernelEnvironment.BootInfo;
			if (!Diag.Console.Release())
				return Error(SyscallError.Busy);
			ulong framebuffer = boot->Framebuffer;
			FramebufferInfo info = new FramebufferInfo { Width = boot->FramebufferWidth, Height = boot->FramebufferHeight, Stride = boot->FramebufferPixelsPerScanLine };
			if (Platform.SyntheticVideo.Start(boot))
			{
				Platform.SyntheticVideo video = Platform.SyntheticVideo.Active;
				framebuffer = video.Vram;
				info.Width = video.Width;
				info.Height = video.Height;
				info.Stride = video.Width;
			}
			if (!UserMemory.CopyTo(infoAddress, &info, (ulong)sizeof(FramebufferInfo)))
				return Error(SyscallError.InvalidPointer);
			ulong physical = framebuffer & ~(MemoryLayout.PageSize - 1);
			ulong pages = MemoryLayout.PagesFor(framebuffer + (ulong)info.Stride * info.Height * 4 - physical);
			ulong address = process.MapShared(physical, pages, PageFlags.Writable | PageFlags.NoExecute);
			return address == 0 ? Error(SyscallError.OutOfMemory) : (long)(address + (framebuffer - physical));
		}

		// The command line names the image; the StartInfo's handles are the caller's, and the child gets its own references
		private static unsafe long CreateProcess(KProcess process, ulong line, ulong length, ulong argument, ulong startInfo)
		{
			Span<char> commandLine = stackalloc char[256];
			if (!UserMemory.ReadPath(line, length, commandLine, out int count))
				return Error(SyscallError.InvalidPointer);
			StartInfo info = default;
			if (startInfo != 0 && !UserMemory.CopyFrom(&info, startInfo, (ulong)sizeof(StartInfo)))
				return Error(SyscallError.InvalidPointer);
			KObject input = info.StdInput == 0 ? null : process.Handles.Get(info.StdInput);
			KObject output = info.StdOutput == 0 ? null : process.Handles.Get(info.StdOutput);
			KObject error = info.StdError == 0 ? null : process.Handles.Get(info.StdError);
			if ((info.StdInput != 0 && input == null) || (info.StdOutput != 0 && output == null) || (info.StdError != 0 && error == null))
				return Error(SyscallError.InvalidHandle);
			KProcess child = KProcess.Create(commandLine.Slice(0, count), argument, process.CurrentDirectory, input, output, error);
			return child == null ? Error(SyscallError.NotFound) : process.Handles.Add(child);
		}

		// A path from the caller made absolute against the process's current directory; false if it can't be read or is too long once resolved
		private static bool ReadResolvedPath(KProcess process, ulong address, ulong length, Span<char> result, out int written)
		{
			Span<char> given = stackalloc char[256];
			written = 0;
			if (!UserMemory.ReadPath(address, length, given, out int givenLength))
				return false;
			written = Paths.Resolve(process.CurrentDirectory, given.Slice(0, givenLength), result);
			return written > 0;
		}

		// Through a kernel buffer a piece at a time: what the file gave up to count, 0 at its end
		private static unsafe long ReadFile(KFile file, ulong buffer, ulong count)
		{
			if (count == 0)
				return 0;
			int chunkSize = (int)Math.Min(count, Storage.ChunkSize);
			byte* chunk = (byte*)Mm.Heap.Allocate((nuint)chunkSize);
			if (chunk == null)
				return Error(SyscallError.OutOfMemory);
			long result = 0;
			while ((ulong)result < count)
			{
				int size = (int)Math.Min(count - (ulong)result, (ulong)chunkSize);
				int read = Storage.Read(file, new Span<byte>(chunk, size));
				if (read <= 0)
					break;
				if (!UserMemory.CopyTo(buffer + (ulong)result, chunk, (ulong)read))
				{
					result = result > 0 ? result : Error(SyscallError.InvalidPointer);
					break;
				}
				result += read;
				if (read < size)
					break;
			}
			Mm.Heap.Free(chunk);
			return result;
		}

		// Everything if the volume has room; fewer bytes when it fills
		private static unsafe long WriteFile(KFile file, ulong buffer, ulong count)
		{
			if (count == 0)
				return 0;
			int chunkSize = (int)Math.Min(count, Storage.ChunkSize);
			byte* chunk = (byte*)Mm.Heap.Allocate((nuint)chunkSize);
			if (chunk == null)
				return Error(SyscallError.OutOfMemory);
			long result = 0;
			while ((ulong)result < count)
			{
				int size = (int)Math.Min(count - (ulong)result, (ulong)chunkSize);
				if (!UserMemory.CopyFrom(chunk, buffer + (ulong)result, (ulong)size))
				{
					result = result > 0 ? result : Error(SyscallError.InvalidPointer);
					break;
				}
				int written = Storage.Write(file, new ReadOnlySpan<byte>(chunk, size));
				result += written;
				if (written < size)
					break;
			}
			Mm.Heap.Free(chunk);
			return result;
		}

		// The names in a directory from the listing's index on, up to 16 a call into its entries; the index moves past those returned, so fewer than asked means the end
		private static unsafe long ListDirectory(KProcess process, ulong pathAddress, ulong pathLength, ulong listingAddress)
		{
			const int MaxEntries = 16;
			Span<char> path = stackalloc char[256];
			DirectoryListing listing;
			if (!ReadResolvedPath(process, pathAddress, pathLength, path, out int length) || !UserMemory.CopyFrom(&listing, listingAddress, (ulong)sizeof(DirectoryListing)))
				return Error(SyscallError.InvalidPointer);
			if (listing.Index > 0x7FFFFFFF)
				return Error(SyscallError.InvalidArgument);

			int count = (int)Math.Min(listing.Count, MaxEntries);
			DirectoryEntry* entries = stackalloc DirectoryEntry[MaxEntries];
			int listed = Storage.List(path.Slice(0, length), (int)listing.Index, new Span<DirectoryEntry>(entries, count));
			if (listed < 0)
				return Error(SyscallError.NotFound);

			listing.Index += (ulong)listed;
			if (!UserMemory.CopyTo(listing.Entries, entries, (ulong)(listed * sizeof(DirectoryEntry))) || !UserMemory.CopyTo(listingAddress, &listing, (ulong)sizeof(DirectoryListing)))
				return Error(SyscallError.InvalidPointer);
			return listed;
		}

		// Returns what was there, up to 256 bytes, blocking while the pipe is empty and still has a writer; 0 is end of file
		private static unsafe long ReadPipe(KPipeEnd reader, ulong buffer, ulong count)
		{
			byte* chunk = stackalloc byte[256];
			while (true)
			{
				ulong flags = Scheduler.Lock();
				int taken = reader.Pipe.Take(chunk, (int)Math.Min(count, 256));
				bool open = reader.Pipe.WriterOpen;
				Scheduler.Unlock(flags);
				if (taken > 0)
				{
					reader.Pipe.Writer.Notify();
					return UserMemory.CopyTo(buffer, chunk, (ulong)taken) ? taken : Error(SyscallError.InvalidPointer);
				}
				if (!open || count == 0)
					return 0;
				if (!reader.Wait() && Terminating)
					return Error(SyscallError.Closed);
			}
		}

		// Writes everything, blocking while the pipe is full; -Closed if nobody can read it any more
		private static unsafe long WritePipe(KPipeEnd writer, ulong buffer, ulong count)
		{
			byte* chunk = stackalloc byte[256];
			ulong written = 0;
			while (written < count)
			{
				int size = (int)Math.Min(count - written, 256);
				if (!UserMemory.CopyFrom(chunk, buffer + written, (ulong)size))
					return Error(SyscallError.InvalidPointer);
				int offset = 0;
				while (offset < size)
				{
					ulong flags = Scheduler.Lock();
					bool open = writer.Pipe.ReaderOpen;
					int put = open ? writer.Pipe.Put(chunk + offset, size - offset) : 0;
					Scheduler.Unlock(flags);
					if (!open)
						return written > 0 ? (long)written : Error(SyscallError.Closed);
					if (put > 0)
					{
						offset += put;
						written += (ulong)put;
						writer.Pipe.Reader.Notify();
					}
					else
					{
						if (!writer.Wait() && Terminating)
							return Error(SyscallError.Closed);
					}
				}
			}
			return (long)written;
		}

		private static unsafe long WaitForMultiple(KProcess process, ulong handles, ulong count, ulong timeoutMs)
		{
			const int MaxObjects = 16;
			if (count == 0 || count > MaxObjects)
				return Error(SyscallError.InvalidArgument);
			ulong* values = stackalloc ulong[MaxObjects];
			if (!UserMemory.CopyFrom(values, handles, count * 8))
				return Error(SyscallError.InvalidPointer);
			// Object references held as raw pointers: nothing moves them, and C# can't stackalloc references
			nint* objects = stackalloc nint[MaxObjects];
			for (ulong i = 0; i < count; i++)
			{
				KObject target = process.Handles.Get(values[i]);
				if (target == null)
					return Error(SyscallError.InvalidHandle);
				objects[i] = Unsafe.As<KObject, nint>(ref target);
			}
			int index = KWaitable.WaitAny((KWaitable*)objects, (int)count, timeoutMs);
			return index < 0 ? Error(SyscallError.Timeout) : index;
		}

		private static unsafe long SendMessage(KProcess process, ulong portHandle, ulong payload, ulong transferHandle)
		{
			if (process.Handles.Get(portHandle) is not KPort port)
				return Error(SyscallError.InvalidHandle);
			KMessage message = new KMessage();
			bool copied;
			fixed (IpcMessage* destination = &message.Payload)
				copied = UserMemory.CopyFrom(destination, payload, IpcMessage.PayloadSize);
			if (!copied)
			{
				Mm.Heap.Free(message);
				return Error(SyscallError.InvalidPointer);
			}
			if (transferHandle != 0)
			{
				message.Transfer = process.Handles.Get(transferHandle);
				if (message.Transfer == null)
				{
					Mm.Heap.Free(message);
					return Error(SyscallError.InvalidHandle);
				}
				message.Transfer.AddReference();
			}
			if (port.Send(message))
				return 0;
			message.Transfer?.Release();
			Mm.Heap.Free(message);
			return Error(SyscallError.Busy);
		}

		// Blocks until a message arrives; returns the handle of the object it carried, 0 if none, or -Timeout
		private static unsafe long ReceiveMessage(KProcess process, ulong portHandle, ulong payload, ulong timeoutMs)
		{
			if (process.Handles.Get(portHandle) is not KPort port)
				return Error(SyscallError.InvalidHandle);
			KMessage message;
			while ((message = port.Take()) == null)
			{
				if (!port.Wait(timeoutMs))
					return Error(SyscallError.Timeout);
			}

			long result = 0;
			bool copied;
			fixed (IpcMessage* source = &message.Payload)
				copied = UserMemory.CopyTo(payload, source, IpcMessage.PayloadSize);
			if (!copied)
				result = Error(SyscallError.InvalidPointer);
			else if (message.Transfer != null)
				result = process.Handles.Add(message.Transfer);
			message.Transfer?.Release();
			Mm.Heap.Free(message);
			return result;
		}

		private static unsafe long DebugPrint(KProcess process, ulong address, ulong length)
		{
			const int MaxLength = 200;
			byte* bytes = stackalloc byte[MaxLength];
			ulong count = Math.Min(length, MaxLength);
			if (!UserMemory.CopyFrom(bytes, address, count))
				return Error(SyscallError.InvalidPointer);

			SpanWriter line = new SpanWriter(stackalloc char[MaxLength + 32]);
			line.Append('[');
			line.Append(process.Name);
			line.Append("] ");
			for (ulong i = 0; i < count; i++)
				line.Append(bytes[i] >= 0x20 && bytes[i] < 0x7F ? (char)bytes[i] : '?');
			Log.WriteLine(ref line);
			return (long)count;
		}

		private static long Error(SyscallError error) => -(long)error;

		[RuntimeImport("*", "PhxSyscallEntryAddress")]
		[MethodImpl(MethodImplOptions.InternalCall)]
		private static extern ulong PhxSyscallEntryAddress();
	}
}
