using System;
using System.Runtime;
using System.Runtime.CompilerServices;
using Alloy.Abi;
using Alloy.Graphics;
using Alloy.Runtime;

namespace Alloy.User.Doom
{
	// doomgeneric on Alloy: the game is C (external/MetalOS.Doom), its platform layer and C runtime are these C# exports
	internal static class Program
	{
		private const int ResX = 1024;
		private const int ResY = 768;

		// Doom draws its 320x200 frame scaled 3x into the top of DG_ScreenBuffer, centered across (MetalOS doomgeneric_metalos.cpp)
		private const int PictureScale = 3;
		private const int PictureWidth = 320 * PictureScale;
		private const int PictureHeight = 200 * PictureScale;
		private const int PictureLeft = (ResX - PictureWidth) / 2;

		private const int StdHandles = 3;
		private const int HandleOffset = 16;

		private static Window s_window;

		[RuntimeImport("*", "doomgeneric_Create")]
		[MethodImpl(MethodImplOptions.InternalCall)]
		private static extern unsafe void DoomCreate(int argc, byte** argv);

		[RuntimeImport("*", "doomgeneric_Tick")]
		[MethodImpl(MethodImplOptions.InternalCall)]
		private static extern void DoomTick();

		[RuntimeImport("*", "PhxDoomScreen")]
		[MethodImpl(MethodImplOptions.InternalCall)]
		private static extern unsafe uint* DoomScreen();

		private static unsafe int Main()
		{
			byte** argv = (byte**)Process.Allocate(4 * 8);
			argv[0] = CString("doom");
			argv[1] = CString("-iwad");
			argv[2] = CString("DOOM.WAD");
			argv[3] = null;
			DoomCreate(3, argv);
			while (true)
				DoomTick();
		}

		private static unsafe byte* CString(string text)
		{
			byte* bytes = (byte*)Process.Allocate((ulong)text.Length + 1);
			for (int i = 0; i < text.Length; i++)
				bytes[i] = (byte)text[i];
			return bytes;
		}

		[RuntimeExport("DG_Init")]
		private static void Init()
		{
			s_window = Window.Create("Doom", 0, 0, PictureWidth, PictureHeight, WmProtocol.StyleFullscreen, Icons.Skull);
			if (s_window == null)
				Process.Exit(1);
		}

		[RuntimeExport("DG_DrawFrame")]
		private static unsafe void DrawFrame()
		{
			uint* screen = DoomScreen();
			Surface surface = s_window.Surface;
			for (int y = 0; y < PictureHeight; y++)
				new ReadOnlySpan<uint>(screen + y * ResX + PictureLeft, PictureWidth).CopyTo(new Span<uint>(surface.Pixels + y * surface.Stride, PictureWidth));
			s_window.Present();
			Keyboard.Drain();
		}

		[RuntimeExport("DG_SleepMs")]
		private static void SleepMs(uint milliseconds) => Process.Sleep(milliseconds);

		[RuntimeExport("DG_GetTicksMs")]
		private static uint GetTicksMs() => (uint)Process.TickCount;

		[RuntimeExport("DG_GetKey")]
		private static unsafe int GetKey(int* pressed, byte* key) => Keyboard.TryGet(pressed, key) ? 1 : 0;

		[RuntimeExport("DG_SetWindowTitle")]
		private static unsafe void SetWindowTitle(byte* title)
		{
		}

		// The C runtime's file layer over the kernel's files; handles are offset past the stdio streams 0-2
		[RuntimeExport("fopen")]
		private static unsafe void* FileOpen(byte* name, byte* mode)
		{
			// r opens a file that's there, w empties or makes one, and a makes one if need be and writes each time at its end; + adds the other direction
			bool both = false;
			for (byte* m = mode; *m != 0; m++)
				both |= *m == '+';
			FileAccess access;
			FileCreate create;
			switch (mode[0])
			{
				case (byte)'r':
					access = both ? FileAccess.ReadWrite : FileAccess.Read;
					create = FileCreate.OpenExisting;
					break;
				case (byte)'w':
					access = both ? FileAccess.ReadWrite : FileAccess.Write;
					create = FileCreate.CreateAlways;
					break;
				case (byte)'a':
					access = both ? FileAccess.ReadAppend : FileAccess.Append;
					create = FileCreate.OpenAlways;
					break;
				default:
					return null;
			}
			long handle = File.Open(CPath(name), access, create);
			return handle <= 0 ? null : (void*)(handle + HandleOffset);
		}

		// A C path as a string, without a leading "./"
		private static unsafe string CPath(byte* name)
		{
			Span<char> path = stackalloc char[256];
			int length = 0;
			while (name[length] != 0 && length < 255)
			{
				path[length] = (char)name[length];
				length++;
			}
			while (length > 2 && path[0] == '.' && path[1] == '/')
			{
				path = path.Slice(2);
				length -= 2;
			}
			return new string(path.Slice(0, length));
		}

		[RuntimeExport("fread")]
		private static unsafe nuint FileRead(void* buffer, nuint size, nuint count, void* file)
		{
			long handle = (long)file - HandleOffset;
			if (size == 0 || handle <= 0)
				return 0;
			long read = File.Read(handle, buffer, (ulong)(size * count));
			return read <= 0 ? 0 : (nuint)read / size;
		}

		[RuntimeExport("fwrite")]
		private static unsafe nuint FileWrite(void* buffer, nuint size, nuint count, void* file)
		{
			if ((long)file < StdHandles)
			{
				PhxWrite((byte*)buffer, size * count);
				return count;
			}
			long handle = (long)file - HandleOffset;
			if (size == 0 || handle <= 0)
				return 0;
			long written = File.Write(handle, buffer, (ulong)(size * count));
			return written <= 0 ? 0 : (nuint)written / size;
		}

		[RuntimeExport("fseek")]
		private static unsafe int FileSeek(void* file, int offset, int origin) => File.Seek((long)file - HandleOffset, offset, (SeekOrigin)origin) < 0 ? -1 : 0;

		[RuntimeExport("ftell")]
		private static unsafe int FileTell(void* file) => (int)File.Seek((long)file - HandleOffset, 0, SeekOrigin.Current);

		[RuntimeExport("fclose")]
		private static unsafe int FileClose(void* file) => (long)file < HandleOffset ? 0 : (int)File.Close((long)file - HandleOffset);

		[RuntimeExport("fflush")]
		private static unsafe int FileFlush(void* file) => 0;

		[RuntimeExport("remove")]
		private static unsafe int Remove(byte* name) => File.Delete(CPath(name)) ? 0 : -1;

		[RuntimeExport("rename")]
		private static unsafe int Rename(byte* from, byte* to) => File.Move(CPath(from), CPath(to)) ? 0 : -1;

		[RuntimeExport("exit")]
		private static void Exit(int code) => Process.Exit(code);

		// The C heap: bump allocation from 16 MB chunks with a size header; Doom manages its own zone inside one big block
		private const ulong ChunkSize = 16 * 1024 * 1024;
		private static unsafe byte* s_heap;
		private static ulong s_heapUsed = ChunkSize;

		[RuntimeExport("malloc")]
		private static unsafe void* Malloc(nuint size)
		{
			ulong needed = ((ulong)size + 16 + 15) & ~15UL;
			if (needed > ChunkSize / 2)
			{
				byte* large = (byte*)Process.Allocate(needed);
				if (large == null)
					return null;
				*(ulong*)large = size;
				return large + 16;
			}
			if (s_heapUsed + needed > ChunkSize)
			{
				s_heap = (byte*)Process.Allocate(ChunkSize);
				if (s_heap == null)
					return null;
				s_heapUsed = 0;
			}
			byte* block = s_heap + s_heapUsed;
			s_heapUsed += needed;
			*(ulong*)block = size;
			return block + 16;
		}

		[RuntimeExport("free")]
		private static unsafe void Free(void* block)
		{
		}

		[RuntimeExport("realloc")]
		private static unsafe void* Realloc(void* block, nuint size)
		{
			if (block == null)
				return Malloc(size);
			void* moved = Malloc(size);
			if (moved != null)
			{
				nuint old = (nuint)(*(ulong*)((byte*)block - 16));
				new ReadOnlySpan<byte>(block, (int)Math.Min(old, size)).CopyTo(new Span<byte>(moved, (int)size));
			}
			return moved;
		}

		private static unsafe byte* s_line;
		private static int s_lineLength;

		// Collects printf output into lines for the kernel log
		[RuntimeExport("PhxWrite")]
		private static unsafe void PhxWrite(byte* text, nuint length)
		{
			if (s_line == null)
				s_line = (byte*)Process.Allocate(200);
			for (nuint i = 0; i < length; i++)
			{
				byte c = text[i];
				if (c == '\n' || s_lineLength == 199)
				{
					Span<char> line = stackalloc char[200];
					for (int j = 0; j < s_lineLength; j++)
						line[j] = (char)s_line[j];
					Console.WriteLine(line.Slice(0, s_lineLength));
					s_lineLength = 0;
					if (c == '\n')
						continue;
				}
				if (c != '\r')
					s_line[s_lineLength++] = c;
			}
		}
	}

	// Keys from the window server, translated to Doom's codes (doomkeys.h) and queued for DG_GetKey
	internal static class Keyboard
	{
		private const int Capacity = 64;
		private static unsafe ushort* s_queue;
		private static int s_read;
		private static int s_write;

		public static void Drain()
		{
			while (Window.TryGetEvent(out IpcMessage message))
			{
				if (message.Type == WmProtocol.Close)
					Process.Exit(0);
				if (message.Type != WmProtocol.KeyDown && message.Type != WmProtocol.KeyUp)
					continue;
				byte key = ToDoom((ushort)message.Data[1]);
				if (key != 0)
					Push(message.Type == WmProtocol.KeyDown, key);
			}
		}

		public static unsafe bool TryGet(int* pressed, byte* key)
		{
			if (s_queue == null || s_read == s_write)
				return false;
			ushort entry = s_queue[s_read];
			s_read = (s_read + 1) % Capacity;
			*pressed = entry >> 8;
			*key = (byte)entry;
			return true;
		}

		// MetalOS's convertToDoomKey, plus both shifts, alt, the function keys and the punctuation Doom binds
		private static byte ToDoom(ushort key)
		{
			switch (key)
			{
				case VirtualKey.Return:
					return 13;
				case VirtualKey.Escape:
					return 27;
				case VirtualKey.Tab:
					return 9;
				case VirtualKey.Back:
					return 0x7F;
				case VirtualKey.Left:
					return 0xAC;
				case VirtualKey.Up:
					return 0xAD;
				case VirtualKey.Right:
					return 0xAE;
				case VirtualKey.Down:
					return 0xAF;
				case VirtualKey.Control:
					return 0xA3;
				case VirtualKey.Space:
					return 0xA2;
				case VirtualKey.Shift:
				case VirtualKey.LeftShift:
				case VirtualKey.RightShift:
					return 0x80 + 0x36;
				case VirtualKey.Menu:
					return 0x80 + 0x38;
				case VirtualKey.OemMinus:
					return (byte)'-';
				case VirtualKey.OemPlus:
					return (byte)'=';
				case VirtualKey.OemComma:
					return (byte)',';
				case VirtualKey.OemPeriod:
					return (byte)'.';
			}
			if (key >= VirtualKey.F1 && key <= VirtualKey.F1 + 9)
				return (byte)(0x80 + 0x3B + key - VirtualKey.F1);
			if (key == VirtualKey.F1 + 10 || key == VirtualKey.F12)
				return (byte)(0x80 + 0x57 + key - VirtualKey.F1 - 10);
			if ((key >= 'A' && key <= 'Z') || (key >= '0' && key <= '9'))
				return (byte)(key >= 'A' && key <= 'Z' ? key - 'A' + 'a' : key);
			return 0;
		}

		private static unsafe void Push(bool pressed, byte key)
		{
			if (s_queue == null)
				s_queue = (ushort*)Process.Allocate(Capacity * 2);
			s_queue[s_write] = (ushort)((pressed ? 1 << 8 : 0) | key);
			s_write = (s_write + 1) % Capacity;
		}
	}
}
