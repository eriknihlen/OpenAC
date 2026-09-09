using System.Buffers.Binary;
using AcDream.Core.Net.Messages;
using Xunit;

namespace AcDream.Core.Net.Tests.Messages;

public sealed class PrivateUpdatePropertyIntTests
{
    // 0x02CD body: opcode(4) + seq(1) + property(4) + value(4) = 13 bytes. NO guid.
    private static byte[] Build(uint property, int value, byte seq = 1, uint opcode = 0x02CDu)
    {
        var b = new byte[13];
        BinaryPrimitives.WriteUInt32LittleEndian(b.AsSpan(0), opcode);
        b[4] = seq;
        BinaryPrimitives.WriteUInt32LittleEndian(b.AsSpan(5), property);
        BinaryPrimitives.WriteInt32LittleEndian(b.AsSpan(9), value);
        return b;
    }

    [Fact]
    public void TryParse_encumbranceVal_returnsPropValue()
    {
        var p = PrivateUpdatePropertyInt.TryParse(Build(property: 5u, value: 1500));
        Assert.NotNull(p);
        Assert.Equal(5u, p!.Value.Property);
        Assert.Equal(1500, p.Value.Value);
    }

    [Fact]
    public void TryParse_wrongOpcode_returnsNull()
        => Assert.Null(PrivateUpdatePropertyInt.TryParse(Build(5, 1, opcode: 0x02CEu)));

    [Fact]
    public void TryParse_truncated_returnsNull()
        => Assert.Null(PrivateUpdatePropertyInt.TryParse(new byte[12]));
}
