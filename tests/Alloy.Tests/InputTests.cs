using Alloy.Abi;

namespace Alloy.Tests;

public class InputTests
{
	[Fact]
	public unsafe void EventsAre16BytesAndFitTheRing()
	{
		Assert.Equal(16, sizeof(InputEvent));
		Assert.Equal(InputRing.Size, (ulong)InputRing.HeaderSize + InputRing.Capacity * (ulong)sizeof(InputEvent));
	}

	[Fact]
	public unsafe void FreeRunningCountersWrapIntoTheRing()
	{
		byte[] region = new byte[InputRing.Size];
		fixed (byte* bytes = region)
		{
			InputRing* ring = (InputRing*)bytes;
			Assert.Equal((nint)(bytes + InputRing.HeaderSize), (nint)InputRing.At(ring, 0));
			Assert.Equal((nint)InputRing.At(ring, 1), (nint)InputRing.At(ring, InputRing.Capacity + 1));
			Assert.Equal((nint)InputRing.At(ring, InputRing.Capacity - 1), (nint)InputRing.At(ring, uint.MaxValue));

			ring->Write = uint.MaxValue;
			ring->Read = uint.MaxValue - 3;
			ring->Write++;
			Assert.Equal(4u, ring->Write - ring->Read);
		}
	}
}
