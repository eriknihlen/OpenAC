using System.Buffers.Binary;
using AcDream.Core.Net.Messages;
using Xunit;

namespace AcDream.Core.Net.Tests.Messages;

public sealed class SetStackSizeTests
{
    private static byte[] Build(uint guid, int stackSize, int value, byte seq = 1, uint opcode = 0x0197u)
    {
        var b = new byte[17];
        BinaryPrimitives.WriteUInt32LittleEndian(b.AsSpan(0), opcode);
        b[4] = seq;
        BinaryPrimitives.WriteUInt32LittleEndian(b.AsSpan(5), guid);
        BinaryPrimitives.WriteInt32LittleEndian(b.AsSpan(9), stackSize);
        BinaryPrimitives.WriteInt32LittleEndian(b.AsSpan(13), value);
        return b;
    }

    [Fact]
    public void TryParse_validStack_returnsGuidStackValue()
    {
        var p = SetStackSize.TryParse(Build(0x50000A01u, stackSize: 25, value: 125));
        Assert.NotNull(p);
        Assert.Equal(0x50000A01u, p!.Value.Guid);
        Assert.Equal(25, p.Value.StackSize);
        Assert.Equal(125, p.Value.Value);
    }

    [Fact]
    public void TryParse_wrongOpcode_returnsNull()
        => Assert.Null(SetStackSize.TryParse(Build(1, 1, 1, opcode: 0x0196u)));

    [Fact]
    public void TryParse_truncated_returnsNull()
        => Assert.Null(SetStackSize.TryParse(new byte[16]));
}
