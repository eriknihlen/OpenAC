using System;
using System.Buffers.Binary;
using AcDream.Core.Net.Messages;
using Xunit;

namespace AcDream.Core.Net.Tests.Messages;

public sealed class InteractRequestsTests
{
    [Fact]
    public void BuildUse_WritesOpcode0x0036AndTarget()
    {
        byte[] body = InteractRequests.BuildUse(gameActionSequence: 2, targetGuid: 0xDEAD);

        Assert.Equal(16, body.Length);
        Assert.Equal(InteractRequests.UseOpcode,
            BinaryPrimitives.ReadUInt32LittleEndian(body.AsSpan(8)));
        Assert.Equal(0xDEADu,
            BinaryPrimitives.ReadUInt32LittleEndian(body.AsSpan(12)));
    }

    [Fact]
    public void BuildUseWithTarget_WritesBothGuids()
    {
        byte[] body = InteractRequests.BuildUseWithTarget(
            gameActionSequence: 3, sourceGuid: 0x1000, targetGuid: 0x2000);

        Assert.Equal(20, body.Length);
        Assert.Equal(InteractRequests.UseWithTargetOpcode,
            BinaryPrimitives.ReadUInt32LittleEndian(body.AsSpan(8)));
        Assert.Equal(0x1000u,
            BinaryPrimitives.ReadUInt32LittleEndian(body.AsSpan(12)));
        Assert.Equal(0x2000u,
            BinaryPrimitives.ReadUInt32LittleEndian(body.AsSpan(16)));
    }

    [Fact]
    public void BuildTeleToLifestone_IsEnvelopeOnly()
    {
        byte[] body = InteractRequests.BuildTeleToLifestone(gameActionSequence: 7);
        Assert.Equal(12, body.Length);
        Assert.Equal(InteractRequests.TeleToLifestoneOpcode,
            BinaryPrimitives.ReadUInt32LittleEndian(body.AsSpan(8)));
    }

    [Fact]
    public void BuildPickUp_WritesOpcode0x0019AndPayload()
    {
        byte[] body = InteractRequests.BuildPickUp(
            gameActionSequence: 5,
            itemGuid:           0xABCDu,
            containerGuid:      0x5000000Au,
            placement:          0);

        Assert.Equal(24, body.Length);
        Assert.Equal(InteractRequests.GameActionEnvelope,
            BinaryPrimitives.ReadUInt32LittleEndian(body.AsSpan(0)));
        Assert.Equal(5u,
            BinaryPrimitives.ReadUInt32LittleEndian(body.AsSpan(4)));
        Assert.Equal(InteractRequests.PutItemInContainerOpcode,
            BinaryPrimitives.ReadUInt32LittleEndian(body.AsSpan(8)));
        Assert.Equal(0xABCDu,
            BinaryPrimitives.ReadUInt32LittleEndian(body.AsSpan(12)));
        Assert.Equal(0x5000000Au,
            BinaryPrimitives.ReadUInt32LittleEndian(body.AsSpan(16)));
        Assert.Equal(0,
            BinaryPrimitives.ReadInt32LittleEndian(body.AsSpan(20)));
    }

    [Fact]
    public void BuildPickUp_NegativePlacement_WritesSignedLittleEndian()
    {
        byte[] body = InteractRequests.BuildPickUp(
            gameActionSequence: 1,
            itemGuid:           0x1u,
            containerGuid:      0x2u,
            placement:          -1);

        Assert.Equal(-1,
            BinaryPrimitives.ReadInt32LittleEndian(body.AsSpan(20)));
    }
}
