using System.Buffers.Binary;
using AcDream.Core.Net.Messages;
using Xunit;

namespace AcDream.Core.Net.Tests.Messages;

public sealed class PickupEventTests
{
    [Fact]
    public void RejectsWrongOpcode()
    {
        Span<byte> body = stackalloc byte[12];
        BinaryPrimitives.WriteUInt32LittleEndian(body, 0xDEADBEEFu);

        Assert.Null(PickupEvent.TryParse(body));
    }

    [Fact]
    public void RejectsTruncated()
    {
        Assert.Null(PickupEvent.TryParse(ReadOnlySpan<byte>.Empty));
        Assert.Null(PickupEvent.TryParse(new byte[11]));
    }

    [Fact]
    public void ParsesGuidAndSequences()
    {
        Span<byte> body = stackalloc byte[12];
        BinaryPrimitives.WriteUInt32LittleEndian(body, PickupEvent.Opcode);
        BinaryPrimitives.WriteUInt32LittleEndian(body.Slice(4), 0x80000727u);
        BinaryPrimitives.WriteUInt16LittleEndian(body.Slice(8), 0x1234);
        BinaryPrimitives.WriteUInt16LittleEndian(body.Slice(10), 0x5678);

        var parsed = PickupEvent.TryParse(body);

        Assert.NotNull(parsed);
        Assert.Equal(0x80000727u, parsed!.Value.Guid);
        Assert.Equal((ushort)0x1234, parsed.Value.InstanceSequence);
        Assert.Equal((ushort)0x5678, parsed.Value.PositionSequence);
    }
}
