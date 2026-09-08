using System.Buffers.Binary;
using AcDream.Core.Net.Messages;

namespace AcDream.Core.Net.Tests.Messages;

public sealed class ParentEventTests
{
    [Fact]
    public void TryParse_RetailWireOrder()
    {
        var body = new byte[24];
        BinaryPrimitives.WriteUInt32LittleEndian(body.AsSpan(0, 4), ParentEvent.Opcode);
        BinaryPrimitives.WriteUInt32LittleEndian(body.AsSpan(4, 4), 0x50000001u);
        BinaryPrimitives.WriteUInt32LittleEndian(body.AsSpan(8, 4), 0x60000002u);
        BinaryPrimitives.WriteUInt32LittleEndian(body.AsSpan(12, 4), 2u);
        BinaryPrimitives.WriteUInt32LittleEndian(body.AsSpan(16, 4), 3u);
        BinaryPrimitives.WriteUInt16LittleEndian(body.AsSpan(20, 2), 0x1234);
        BinaryPrimitives.WriteUInt16LittleEndian(body.AsSpan(22, 2), 0x5678);

        var parsed = ParentEvent.TryParse(body);

        Assert.NotNull(parsed);
        Assert.Equal(0x50000001u, parsed.Value.ParentGuid);
        Assert.Equal(0x60000002u, parsed.Value.ChildGuid);
        Assert.Equal(2u, parsed.Value.ParentLocation);
        Assert.Equal(3u, parsed.Value.PlacementId);
        Assert.Equal((ushort)0x1234, parsed.Value.ParentInstanceSequence);
        Assert.Equal((ushort)0x5678, parsed.Value.ChildPositionSequence);
    }

    [Fact]
    public void TryParse_RejectsTruncationAndWrongOpcode()
    {
        Assert.Null(ParentEvent.TryParse(new byte[23]));

        var body = new byte[24];
        BinaryPrimitives.WriteUInt32LittleEndian(body, 0xF748u);
        Assert.Null(ParentEvent.TryParse(body));
    }
}
