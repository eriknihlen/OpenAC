using System.Buffers.Binary;
using AcDream.Core.Net.Messages;

namespace AcDream.Core.Net.Tests.Messages;

public sealed class PublicUpdatePropertyIntTests
{
    private static byte[] Build(uint guid, uint property, int value, byte seq = 1, uint opcode = 0x02CEu)
    {
        var b = new byte[17];
        BinaryPrimitives.WriteUInt32LittleEndian(b.AsSpan(0), opcode);
        b[4] = seq;
        BinaryPrimitives.WriteUInt32LittleEndian(b.AsSpan(5), guid);
        BinaryPrimitives.WriteUInt32LittleEndian(b.AsSpan(9), property);
        BinaryPrimitives.WriteInt32LittleEndian(b.AsSpan(13), value);
        return b;
    }

    [Fact]
    public void TryParse_uiEffectsUpdate_returnsGuidPropValue()
    {
        var p = PublicUpdatePropertyInt.TryParse(Build(0x50000001u, property: 18u, value: 0x9));
        Assert.NotNull(p);
        Assert.Equal(0x50000001u, p!.Value.Guid);
        Assert.Equal(18u, p.Value.Property);
        Assert.Equal(0x9, p.Value.Value);
    }

    [Fact]
    public void TryParse_wrongOpcode_returnsNull()
        => Assert.Null(PublicUpdatePropertyInt.TryParse(Build(1, 18, 1, opcode: 0x02CDu)));

    [Fact]
    public void TryParse_truncated_returnsNull()
        => Assert.Null(PublicUpdatePropertyInt.TryParse(new byte[16]));
}
