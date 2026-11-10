using System.Runtime.CompilerServices;

namespace Alloy.Abi
{
	// The fixed-size message ports carry; Type is the protocol's own opcode, Data its arguments
	public struct IpcMessage
	{
		public const int PayloadSize = 64;

		public ulong Type;
		public IpcData Data;
	}

	// Seven argument slots laid out inline, indexable without unsafe code
	[InlineArray(7)]
	public struct IpcData
	{
		private ulong _element0;
	}
}
