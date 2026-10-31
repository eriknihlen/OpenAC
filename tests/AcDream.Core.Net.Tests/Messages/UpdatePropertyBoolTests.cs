using System.Buffers.Binary;
using AcDream.Core.Net.Messages;

namespace AcDream.Core.Net.Tests.Messages;

/// <summary>
/// The true/false property updates the server sends when one quality
/// changes on an object (public) or on the player (private).
/// </summary>
public sealed class UpdatePropertyBoolTests
{
    private static byte[] BuildPublic(
        uint opcode, uint guid, uint property, uint value, byte seq = 1)
    {
        byte[] body = new byte[17];
        BinaryPrimitives.WriteUInt32LittleEndian(body, opcode);
        body[4] = seq;
        BinaryPrimitives.WriteUInt32LittleEndian(body.AsSpan(5), guid);
        BinaryPrimitives.WriteUInt32LittleEndian(body.AsSpan(9), property);
        BinaryPrimitives.WriteUInt32LittleEndian(body.AsSpan(13), value);
        return body;
    }

    private static byte[] BuildPrivate(
        uint opcode, uint property, uint value, byte seq = 1)
    {
        byte[] body = new byte[13];
        BinaryPrimitives.WriteUInt32LittleEndian(body, opcode);
        body[4] = seq;
        BinaryPrimitives.WriteUInt32LittleEndian(body.AsSpan(5), property);
        BinaryPrimitives.WriteUInt32LittleEndian(body.AsSpan(9), value);
        return body;
    }

    [Theory]
    [InlineData(0u, false)]
    [InlineData(1u, true)]
    [InlineData(2u, true)]
    public void PublicBool_ReadsGuidPropertyAndAnyNonZeroWordAsTrue(uint word, bool value)
    {
        var p = PublicUpdatePropertyBool.TryParse(
            BuildPublic(0x02D2u, 0x7E166001u, property: 24u, value: word));

        Assert.NotNull(p);
        Assert.Equal(0x7E166001u, p!.Value.Guid);
        Assert.Equal(24u, p.Value.Property);
        Assert.Equal(value, p.Value.Value);
    }

    [Fact]
    public void PublicBool_RejectsForeignOpcodeAndTruncatedBody()
    {
        Assert.Null(PublicUpdatePropertyBool.TryParse(BuildPublic(0x02DAu, 1u, 24u, 1u)));
        Assert.Null(PublicUpdatePropertyBool.TryParse(
            BuildPublic(0x02D2u, 1u, 24u, 1u).AsSpan(0, 16)));
    }

    [Fact]
    public void PrivateBool_ReadsPropertyAndValue()
    {
        var p = PrivateUpdatePropertyBool.TryParse(
            BuildPrivate(0x02D1u, property: 24u, value: 1u));

        Assert.NotNull(p);
        Assert.Equal(24u, p!.Value.Property);
        Assert.True(p.Value.Value);
    }

    [Fact]
    public void PrivateBool_RejectsForeignOpcodeAndTruncatedBody()
    {
        Assert.Null(PrivateUpdatePropertyBool.TryParse(BuildPrivate(0x02D2u, 24u, 1u)));
        Assert.Null(PrivateUpdatePropertyBool.TryParse(
            BuildPrivate(0x02D1u, 24u, 1u).AsSpan(0, 12)));
    }
}
