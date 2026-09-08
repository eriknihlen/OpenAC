using System;
using System.Buffers.Binary;
using AcDream.Core.Net.Messages;
using Xunit;

namespace AcDream.Core.Net.Tests.Messages;

public class SetStateTests
{
    [Fact]
    public void TryParse_WellFormedBody_ReturnsParsed()
    {
        var buf = new byte[16];
        BinaryPrimitives.WriteUInt32LittleEndian(buf.AsSpan(0, 4), 0xF74Bu);
        BinaryPrimitives.WriteUInt32LittleEndian(buf.AsSpan(4, 4), 0x000F4244u); // door guid
        BinaryPrimitives.WriteUInt32LittleEndian(buf.AsSpan(8, 4), 0x00000004u); // ETHEREAL bit
        BinaryPrimitives.WriteUInt16LittleEndian(buf.AsSpan(12, 2), (ushort)355);
        BinaryPrimitives.WriteUInt16LittleEndian(buf.AsSpan(14, 2), (ushort)42);

        var parsed = SetState.TryParse(buf);

        Assert.NotNull(parsed);
        Assert.Equal(0x000F4244u, parsed.Value.Guid);
        Assert.Equal(0x00000004u, parsed.Value.PhysicsState);
        Assert.Equal((ushort)355, parsed.Value.InstanceSequence);
        Assert.Equal((ushort)42, parsed.Value.StateSequence);
    }

    [Fact]
    public void TryParse_Truncated_ReturnsNull()
    {
        var buf = new byte[10];
        Assert.Null(SetState.TryParse(buf));
    }

    [Fact]
    public void TryParse_WrongOpcode_ReturnsNull()
    {
        var buf = new byte[16];
        BinaryPrimitives.WriteUInt32LittleEndian(buf.AsSpan(0, 4), 0xF74Cu);
        Assert.Null(SetState.TryParse(buf));
    }
}
