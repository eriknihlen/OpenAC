using System.Buffers.Binary;
using AcDream.Core.Net.Messages;

namespace AcDream.Core.Net.Tests.Messages;

public sealed class PrivateUpdatePropertyInt64Tests
{
    private static byte[] Build(
        uint property,
        long value,
        byte sequence = 1,
        uint opcode = PrivateUpdatePropertyInt64.Opcode)
    {
        var body = new byte[PrivateUpdatePropertyInt64.BodySize];
        BinaryPrimitives.WriteUInt32LittleEndian(body, opcode);
        body[4] = sequence;
        BinaryPrimitives.WriteUInt32LittleEndian(body.AsSpan(5), property);
        BinaryPrimitives.WriteInt64LittleEndian(body.AsSpan(9), value);
        return body;
    }

    [Theory]
    [InlineData(1u, 1_234_567_890L)]
    [InlineData(2u, 987_654_321L)]
    [InlineData(6u, -1L)]
    public void TryParse_RetailBody_ReturnsSignedPropertyValue(uint property, long value)
    {
        var parsed = PrivateUpdatePropertyInt64.TryParse(Build(property, value, sequence: 0x7B));

        Assert.NotNull(parsed);
        Assert.Equal(property, parsed.Value.Property);
        Assert.Equal(value, parsed.Value.Value);
    }

    [Fact]
    public void TryParse_WrongOpcode_ReturnsNull()
        => Assert.Null(PrivateUpdatePropertyInt64.TryParse(
            Build(1u, 100L, opcode: 0x02D0u)));

    [Fact]
    public void TryParse_Truncated_ReturnsNull()
        => Assert.Null(PrivateUpdatePropertyInt64.TryParse(
            new byte[PrivateUpdatePropertyInt64.BodySize - 1]));
}
