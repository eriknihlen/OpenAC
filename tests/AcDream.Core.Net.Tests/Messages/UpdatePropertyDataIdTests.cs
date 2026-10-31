using System.Buffers.Binary;
using AcDream.Core.Net.Messages;

namespace AcDream.Core.Net.Tests.Messages;

/// <summary>
/// #35: the data-id and instance-id property updates the server sends when one
/// quality changes on an object — an icon overlay swap among them.
/// </summary>
public sealed class UpdatePropertyDataIdTests
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

    [Fact]
    public void PublicDataId_ReadsGuidPropertyAndValue()
    {
        var p = PublicUpdatePropertyDataId.TryParse(
            BuildPublic(0x02D8u, 0x50000001u, property: 52u, value: 0x06001234u));

        Assert.NotNull(p);
        Assert.Equal(0x50000001u, p!.Value.Guid);
        Assert.Equal(52u, p.Value.Property);
        Assert.Equal(0x06001234u, p.Value.Value);
    }

    [Fact]
    public void PublicDataId_RejectsForeignOpcode()
        => Assert.Null(PublicUpdatePropertyDataId.TryParse(
            BuildPublic(0x02D7u, 1u, 52u, 2u)));

    [Fact]
    public void PublicDataId_RejectsTruncatedBody()
        => Assert.Null(PublicUpdatePropertyDataId.TryParse(new byte[16]));

    [Fact]
    public void PrivateDataId_ReadsPropertyAndValue()
    {
        var p = PrivateUpdatePropertyDataId.TryParse(
            BuildPrivate(0x02D7u, property: 8u, value: 0x06005678u));

        Assert.NotNull(p);
        Assert.Equal(8u, p!.Value.Property);
        Assert.Equal(0x06005678u, p.Value.Value);
    }

    [Fact]
    public void PrivateDataId_RejectsForeignOpcode()
        => Assert.Null(PrivateUpdatePropertyDataId.TryParse(
            BuildPrivate(0x02D8u, 8u, 1u)));

    [Fact]
    public void PrivateDataId_RejectsTruncatedBody()
        => Assert.Null(PrivateUpdatePropertyDataId.TryParse(new byte[12]));

    [Fact]
    public void PublicInstanceId_ReadsGuidPropertyAndValue()
    {
        var p = PublicUpdatePropertyInstanceId.TryParse(
            BuildPublic(0x02DAu, 0x50000009u, property: 3u, value: 0x50000001u));

        Assert.NotNull(p);
        Assert.Equal(0x50000009u, p!.Value.Guid);
        Assert.Equal(3u, p.Value.Property);
        Assert.Equal(0x50000001u, p.Value.Value);
    }

    [Fact]
    public void PublicInstanceId_RejectsForeignOpcode()
        => Assert.Null(PublicUpdatePropertyInstanceId.TryParse(
            BuildPublic(0x02D8u, 1u, 3u, 2u)));

    [Fact]
    public void PrivateInstanceId_ReadsPropertyAndValue()
    {
        var p = PrivateUpdatePropertyInstanceId.TryParse(
            BuildPrivate(0x02D9u, property: 2u, value: 0x50000002u));

        Assert.NotNull(p);
        Assert.Equal(2u, p!.Value.Property);
        Assert.Equal(0x50000002u, p.Value.Value);
    }

    [Fact]
    public void PrivateInstanceId_RejectsForeignOpcode()
        => Assert.Null(PrivateUpdatePropertyInstanceId.TryParse(
            BuildPrivate(0x02DAu, 2u, 1u)));
}
