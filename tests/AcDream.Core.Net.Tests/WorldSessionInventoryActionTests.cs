using System.Net;
using AcDream.Core.Net.Messages;

namespace AcDream.Core.Net.Tests;

public sealed class WorldSessionInventoryActionTests
{
    [Fact]
    public void SendSalvageUsesNextSequenceAndRetailBody()
    {
        using var session = new WorldSession(
            new IPEndPoint(IPAddress.Loopback, 65000));
        byte[]? captured = null;
        session.GameActionCapture = body => captured = body;

        session.SendSalvage(0x50000010u, [0x50000020u]);

        Assert.Equal(
            InventoryActions.BuildCreateTinkeringTool(
                1u,
                0x50000010u,
                [0x50000020u]),
            captured);
    }

    [Fact]
    public void SendStackableMerge_UsesNextSequenceAndExactBuilderBytes()
    {
        using var session = new WorldSession(
            new IPEndPoint(IPAddress.Loopback, 65000));
        byte[]? captured = null;
        session.GameActionCapture = body => captured = body;

        session.SendStackableMerge(0xAAu, 0xBBu, 5u);

        Assert.Equal(InventoryActions.BuildStackableMerge(1, 0xAAu, 0xBBu, 5u), captured);
    }

    [Fact]
    public void SendStackableSplitToContainer_UsesNextSequenceAndExactBuilderBytes()
    {
        using var session = new WorldSession(
            new IPEndPoint(IPAddress.Loopback, 65000));
        byte[]? captured = null;
        session.GameActionCapture = body => captured = body;

        session.SendStackableSplitToContainer(0xAAu, 0xBBu, 4u, 2u);

        Assert.Equal(
            InventoryActions.BuildStackableSplitToContainer(1, 0xAAu, 0xBBu, 4u, 2u),
            captured);
    }

    [Fact]
    public void SendStackableSplitTo3D_UsesNextSequenceAndExactBuilderBytes()
    {
        using var session = new WorldSession(
            new IPEndPoint(IPAddress.Loopback, 65000));
        byte[]? captured = null;
        session.GameActionCapture = body => captured = body;

        session.SendStackableSplitTo3D(0xAAu, 2u);

        Assert.Equal(InventoryActions.BuildStackableSplitTo3D(1, 0xAAu, 2u), captured);
    }

    [Fact]
    public void SendGiveObject_UsesNextSequenceAndExactBuilderBytes()
    {
        using var session = new WorldSession(
            new IPEndPoint(IPAddress.Loopback, 65000));
        byte[]? captured = null;
        session.GameActionCapture = body => captured = body;

        session.SendGiveObject(targetGuid: 0xAAu, itemGuid: 0xBBu, amount: 3u);

        Assert.Equal(
            InventoryActions.BuildGiveObjectRequest(1, 0xAAu, 0xBBu, 3u),
            captured);
    }

    [Fact]
    public void SendSetInscription_UsesNextSequenceAndExactBuilderBytes()
    {
        using var session = new WorldSession(
            new IPEndPoint(IPAddress.Loopback, 65000));
        byte[]? captured = null;
        session.GameActionCapture = body => captured = body;

        session.SendSetInscription(0xAAu, "Remember.");

        Assert.Equal(
            InventoryActions.BuildSetInscription(1, 0xAAu, "Remember."),
            captured);
    }
}
