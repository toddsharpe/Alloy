using System;
using System.Text;
using Alloy.Abi;

namespace Alloy.Sdk
{
	// The system clipboard, kept by the window server so every app sees the same text
	public static class Clipboard
	{
		public static unsafe bool SetText(string text)
		{
			if (!Window.Connect())
				return false;
			byte[] bytes = Encoding.UTF8.GetBytes(text ?? "");
			if (bytes.Length > WmProtocol.MaxClipboardBytes)
				Array.Resize(ref bytes, WmProtocol.MaxClipboardBytes);
			long section = SharedMemory.Create((ulong)Math.Max(bytes.Length, 1));
			long address = section > 0 ? SharedMemory.MapAddress(section) : -1;
			if (address <= 0)
				return false;
			new Span<byte>(bytes).CopyTo(new Span<byte>((void*)address, bytes.Length));
			IpcMessage message = default;
			message.Type = WmProtocol.SetClipboard;
			message.Data[1] = (ulong)bytes.Length;
			message.Data[6] = Window.ClientId;
			Port.Send(Window.ServerPort, message, section);
			Kernel.Close(section);
			return true;
		}

		// The clipboard text, or an empty string when there is none or the server does not answer in time
		public static unsafe string GetText()
		{
			if (!Window.Connect())
				return "";
			long port = Port.Create();
			if (port <= 0)
				return "";
			IpcMessage message = default;
			message.Type = WmProtocol.GetClipboard;
			message.Data[6] = Window.ClientId;
			Port.Send(Window.ServerPort, message, port);
			string text = "";
			long section = Port.Receive(port, out IpcMessage reply, 2000);
			if (section >= 0 && reply.Type == WmProtocol.ClipboardText)
			{
				int length = (int)Math.Min(reply.Data[1], (ulong)WmProtocol.MaxClipboardBytes);
				long address = length > 0 && section > 0 ? SharedMemory.MapAddress(section) : 0;
				if (address > 0)
					text = Encoding.UTF8.GetString(new ReadOnlySpan<byte>((void*)address, length));
			}
			if (section > 0)
				Kernel.Close(section);
			Kernel.Close(port);
			return text;
		}
	}
}
