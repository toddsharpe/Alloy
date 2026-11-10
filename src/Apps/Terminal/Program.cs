using System;
using System.Collections.Generic;
using Alloy.Abi;
using Alloy.Graphics;
using Alloy.Sdk;
using Alloy.Sdk.UI;

namespace Alloy.Apps.Terminal
{
	// MetalOS's term.exe: a command shell in a window that runs programs with their output piped into its console
	internal static class Program
	{
		private const string PromptName = "alloy";
		private const string PromptMark = "> ";

		// Command lines go to the kernel in 255 bytes; the ".exe" suffix must still fit
		private const int MaxCommandLength = 100;
		private const int HistorySize = 16;

		// Bounds one tick's draining, so a child that never stops writing can't starve the UI
		private const int MaxDrainPerTick = 4096;

		private static ConsoleView s_console;
		private static readonly char[] s_command = new char[MaxCommandLength + 8];
		private static int s_commandLength;
		private static readonly List<string> s_history = new List<string>();
		private static int s_historyIndex;
		private static bool s_ctrl;

		// The running child: the read ends of its stdout and stderr pipes, and its process handle
		private static long s_childOutput;
		private static long s_childErrors;
		private static long s_child;

		private static int Main()
		{
			Gui gui = Gui.Create("Terminal", 160, 120, 1120, 620, WmProtocol.StyleResizable, Icons.Terminal);
			if (gui == null)
				return 1;
			gui.Background = Theme.IronDeep;
			gui.Animate = true;
			s_console = new ConsoleView(new TextBuffer(), new Rectangle(0, 0, gui.Width, gui.Height));
			gui.Add(s_console);
			Write("Alloy Terminal", ConsoleAttribute.Bright);
			Write("  type help for commands\n", ConsoleAttribute.Dim);
			ShowPrompt();
			gui.OnEvent = OnEvent;
			gui.OnTick = OnTick;
			gui.Run();
			return 0;
		}

		private static void Write(ReadOnlySpan<char> text, ConsoleAttribute attribute)
		{
			s_console.Text.SetAttribute(attribute);
			s_console.Text.Write(text);
			s_console.Text.SetAttribute(ConsoleAttribute.Normal);
		}

		private static void ShowPrompt()
		{
			Write(PromptName, ConsoleAttribute.Prompt);
			Write(PromptMark, ConsoleAttribute.Punctuation);
		}

		// Prompt and command stay on one line, so Backspace never has to cross a wrapped line
		private static int CommandLimit
		{
			get
			{
				int fit = s_console.Text.Columns - PromptName.Length - PromptMark.Length - 1;
				return Math.Max(Math.Min(fit, MaxCommandLength), 0);
			}
		}

		private static ReadOnlySpan<char> Command => new ReadOnlySpan<char>(s_command, 0, s_commandLength);

		// The keyboard reports virtual keys and there is no shift handling yet, so only these type text (as MetalOS)
		private static char ToChar(ushort key)
		{
			if (key >= 'A' && key <= 'Z')
				return (char)(key - 'A' + 'a');
			if ((key >= '0' && key <= '9') || key == ' ')
				return (char)key;
			switch (key)
			{
				case VirtualKey.OemPeriod:
					return '.';
				case VirtualKey.OemComma:
					return ',';
				case VirtualKey.OemMinus:
					return '-';
				case VirtualKey.OemSlash:
					return '/';
				default:
					return '\0';
			}
		}

		// Starts the command with its own stdout and stderr pipes, so its errors can show in red; false if it couldn't be started
		private static bool Launch(string commandLine)
		{
			if (!Pipe.Create(out long outRead, out long outWrite))
				return false;
			if (!Pipe.Create(out long errRead, out long errWrite))
			{
				Kernel.Close(outRead);
				Kernel.Close(outWrite);
				return false;
			}
			StartInfo info = default;
			info.StdOutput = (ulong)outWrite;
			info.StdError = (ulong)errWrite;
			long child = Kernel.CreateProcess(commandLine, 0, info);

			// The child holds its own ends now, so its exit is what ends the pipes
			Kernel.Close(outWrite);
			Kernel.Close(errWrite);
			if (child < 0)
			{
				Kernel.Close(outRead);
				Kernel.Close(errRead);
				return false;
			}
			s_childOutput = outRead;
			s_childErrors = errRead;
			s_child = child;
			return true;
		}

		// Moves what one of the child's pipes holds into the console without blocking; true once that pipe is finished
		private static bool Drain(long pipe, ConsoleAttribute attribute, ref int budget)
		{
			Span<byte> buffer = stackalloc byte[256];
			Span<char> text = stackalloc char[256];
			while (budget > 0)
			{
				long available = Pipe.Available(pipe);
				if (available <= 0)
					return available < 0;
				long read = Pipe.Read(pipe, buffer.Slice(0, (int)Math.Min(Math.Min(available, budget), 256)));
				if (read <= 0)
					return true;
				for (int i = 0; i < read; i++)
					text[i] = (char)buffer[i];
				Write(text.Slice(0, (int)read), attribute);
				budget -= (int)read;
			}
			return false;
		}

		private static void FinishCommand()
		{
			Kernel.Close(s_childOutput);
			Kernel.Close(s_childErrors);
			Kernel.Close(s_child);
			s_childOutput = s_childErrors = s_child = 0;
			if (s_console.Text.CurrentLength != 0)
				s_console.Text.Write('\n');
			ShowPrompt();
		}

		private static void Remember(string line)
		{
			if (s_history.Count == HistorySize)
				s_history.RemoveAt(0);
			s_history.Add(line);
			s_historyIndex = s_history.Count;
		}

		// Swaps the line being typed for another (from the history), on screen too
		private static void ReplaceCommand(ReadOnlySpan<char> text)
		{
			while (s_commandLength != 0)
			{
				s_commandLength--;
				s_console.Text.Backspace();
			}
			int limit = CommandLimit;
			for (int i = 0; i < text.Length && s_commandLength < limit; i++)
			{
				s_command[s_commandLength++] = text[i];
				s_console.Text.Write(text[i]);
			}
		}

		private static void ShowHelp()
		{
			Write("Run: ", ConsoleAttribute.Dim);
			Write("hello ps time calc fire doom dhcp ping nettest ipctest crash\n", ConsoleAttribute.Normal);
			Write("Built in: ", ConsoleAttribute.Dim);
			Write("help clear\n", ConsoleAttribute.Normal);
			Write("Keys: ", ConsoleAttribute.Dim);
			Write("Up/Down history, PgUp/PgDn scroll, Ctrl+L clear\n", ConsoleAttribute.Normal);
		}

		private static void ClearScreen()
		{
			s_console.Text.Clear();
			s_console.Scroll = 0;
			ShowPrompt();
			s_console.Text.Write(Command);
		}

		private static void RunCommand()
		{
			s_console.Text.Write('\n');
			if (s_commandLength == 0)
			{
				ShowPrompt();
				return;
			}
			string typed = new string(Command);
			Remember(typed);
			s_commandLength = 0;
			if (typed == "help")
			{
				ShowHelp();
				ShowPrompt();
				return;
			}
			if (typed == "clear")
			{
				ClearScreen();
				return;
			}

			// The program name gets .exe when it has no extension, so "ps" runs ps.exe
			int space = typed.IndexOf(' ');
			string name = space < 0 ? typed : typed.Substring(0, space);
			string line = name.Contains('.') ? typed : name + ".exe" + (space < 0 ? "" : typed.Substring(space));
			if (!Launch(line))
			{
				Write(line, ConsoleAttribute.Error);
				Write(": could not run\n", ConsoleAttribute.Error);
				ShowPrompt();
			}
		}

		private static void OnKey(ushort key, bool pressed)
		{
			if (key == VirtualKey.Control)
			{
				s_ctrl = pressed;
				return;
			}
			if (!pressed)
				return;

			// Scrolling back works while a command runs too
			if (key == VirtualKey.Prior || key == VirtualKey.Next)
			{
				int page = Math.Max(s_console.Rows - 1, 1);
				s_console.ScrollBy(key == VirtualKey.Prior ? page : -page);
				return;
			}

			// Other input is ignored while a command runs (it has no stdin to send it to)
			if (s_childOutput != 0)
				return;
			s_console.Scroll = 0;
			if (s_ctrl && key == 'L')
			{
				ClearScreen();
				return;
			}
			switch (key)
			{
				case VirtualKey.Return:
					RunCommand();
					break;

				case VirtualKey.Back:
					if (s_commandLength != 0)
					{
						s_commandLength--;
						s_console.Text.Backspace();
					}
					break;

				case VirtualKey.Up:
					if (s_historyIndex > 0)
						ReplaceCommand(s_history[--s_historyIndex]);
					break;

				case VirtualKey.Down:
					if (s_historyIndex < s_history.Count)
					{
						s_historyIndex++;
						ReplaceCommand(s_historyIndex == s_history.Count ? "" : s_history[s_historyIndex]);
					}
					break;

				default:
					char c = ToChar(key);
					if (c != '\0' && s_commandLength < CommandLimit)
					{
						s_command[s_commandLength++] = c;
						s_console.Text.Write(c);
					}
					break;
			}
		}

		private static bool OnEvent(Gui gui, in IpcMessage message)
		{
			if (message.Type == WmProtocol.KeyDown || message.Type == WmProtocol.KeyUp)
			{
				OnKey((ushort)message.Data[1], message.Type == WmProtocol.KeyDown);
				return true;
			}
			if (message.Type == WmProtocol.Resize)
				s_console.SetBounds(new Rectangle(0, 0, gui.Width, gui.Height));
			return false;
		}

		// Every tick collects a running command's output, which is also when its end is noticed (MetalOS does this on each paint)
		private static bool OnTick(Gui gui)
		{
			s_console.ShowCursor = s_childOutput == 0;
			if (s_childOutput == 0)
				return false;
			int budget = MaxDrainPerTick;
			bool outputDone = Drain(s_childOutput, ConsoleAttribute.Normal, ref budget);
			bool errorsDone = Drain(s_childErrors, ConsoleAttribute.Error, ref budget);
			if (outputDone && errorsDone)
				FinishCommand();
			return true;
		}
	}
}
