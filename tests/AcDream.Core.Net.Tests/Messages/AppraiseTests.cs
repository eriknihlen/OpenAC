using System;
using System.Buffers.Binary;
using AcDream.Core.Net.Messages;
using Xunit;

namespace AcDream.Core.Net.Tests.Messages;

public sealed class AppraiseTests
{
    [Fact]
    public void AppraiseRequest_Build_EmitsCorrectWireBytes()
    {
        byte[] body = AppraiseRequest.Build(gameActionSequence: 7, targetGuid: 0x12345678u);

        Assert.Equal(16, body.Length);
        Assert.Equal(AppraiseRequest.GameActionEnvelope,
            BinaryPrimitives.ReadUInt32LittleEndian(body));
        Assert.Equal(7u,
            BinaryPrimitives.ReadUInt32LittleEndian(body.AsSpan(4)));
        Assert.Equal(AppraiseRequest.SubOpcode,
            BinaryPrimitives.ReadUInt32LittleEndian(body.AsSpan(8)));
        Assert.Equal(0x12345678u,
            BinaryPrimitives.ReadUInt32LittleEndian(body.AsSpan(12)));
    }

    [Fact]
    public void ParseIdentifyResponseHeader_RoundTrip()
    {
        byte[] payload = new byte[12];
        BinaryPrimitives.WriteUInt32LittleEndian(payload,            0xDEADBEEFu);   // guid
        BinaryPrimitives.WriteUInt32LittleEndian(payload.AsSpan(4),  0x000F);          // flags
        BinaryPrimitives.WriteUInt32LittleEndian(payload.AsSpan(8),  1u);              // success

        var parsed = GameEvents.ParseIdentifyResponseHeader(payload);
        Assert.NotNull(parsed);
        Assert.Equal(0xDEADBEEFu, parsed!.Value.Guid);
        Assert.Equal(0x000Fu, parsed.Value.AppraiseFlags);
        Assert.True(parsed.Value.Success);
    }

    [Fact]
    public void ParseWieldObject_RoundTrip()
    {
        byte[] payload = new byte[8];
        BinaryPrimitives.WriteUInt32LittleEndian(payload,            0x1000u);
        BinaryPrimitives.WriteUInt32LittleEndian(payload.AsSpan(4),  0x00400000u);  // MeleeWeapon slot

        var parsed = GameEvents.ParseWieldObject(payload);
        Assert.NotNull(parsed);
        Assert.Equal(0x1000u, parsed!.Value.ItemGuid);
        Assert.Equal(0x00400000u, parsed.Value.EquipLoc);
    }

    [Fact]
    public void ParsePutObjInContainer_RoundTrip()
    {
        byte[] payload = new byte[16];
        BinaryPrimitives.WriteUInt32LittleEndian(payload,             0x1001u);
        BinaryPrimitives.WriteUInt32LittleEndian(payload.AsSpan(4),   0x2001u);
        BinaryPrimitives.WriteUInt32LittleEndian(payload.AsSpan(8),   3u);
        BinaryPrimitives.WriteUInt32LittleEndian(payload.AsSpan(12),  1u);

        var parsed = GameEvents.ParsePutObjInContainer(payload);
        Assert.NotNull(parsed);
        Assert.Equal(0x1001u, parsed!.Value.ItemGuid);
        Assert.Equal(0x2001u, parsed.Value.ContainerGuid);
        Assert.Equal(3u,      parsed.Value.Placement);
        Assert.Equal(1u,      parsed.Value.ContainerType);
    }
}
