using System;
using System.IO;

namespace Alloy.Sdk
{
	// A Stream over one of NetSvr's stream sockets, so code written against streams (HTTP, TLS) runs on Alloy's network
	public sealed class NetStream : Stream
	{
		private readonly long _socket;
		private readonly ulong _timeoutMs;
		private bool _closed;

		// Set when a read failed: timed out or reset
		public string Error { get; private set; }

		public bool TimedOut { get; private set; }

		public NetStream(long socket, ulong timeoutMs)
		{
			_socket = socket;
			_timeoutMs = timeoutMs;
		}

		public override bool CanRead => true;

		public override bool CanSeek => false;

		public override bool CanWrite => true;

		public override long Length => throw new NotSupportedException();

		public override long Position { get => throw new NotSupportedException(); set => throw new NotSupportedException(); }

		public override void Flush()
		{
		}

		public override int Read(byte[] buffer, int offset, int count) => Read(buffer.AsSpan(offset, count));

		public override int Read(Span<byte> buffer)
		{
			if (_closed || buffer.Length == 0)
				return 0;
			long got = Net.Receive(_socket, buffer, _timeoutMs);
			if (got <= 0)
			{
				_closed = true;
				if (got < 0)
				{
					TimedOut = got == SystemCall.Error(Abi.SyscallError.Timeout);
					Error = TimedOut ? "the server stopped answering" : "the connection was reset";
				}
				return 0;
			}
			return (int)got;
		}

		public override void Write(byte[] buffer, int offset, int count) => Write(buffer.AsSpan(offset, count));

		public override void Write(ReadOnlySpan<byte> buffer)
		{
			if (Net.Send(_socket, buffer) < buffer.Length)
				throw new IOException("the connection closed while sending");
		}

		public override long Seek(long offset, SeekOrigin origin) => throw new NotSupportedException();

		public override void SetLength(long value) => throw new NotSupportedException();

		protected override void Dispose(bool disposing)
		{
			if (!_closed)
				_closed = true;
			base.Dispose(disposing);
		}
	}
}
