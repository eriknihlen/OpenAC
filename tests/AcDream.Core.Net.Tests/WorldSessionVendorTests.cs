using System.Net;
using AcDream.Core.Net;
using AcDream.Core.Net.Messages;

namespace AcDream.Core.Net.Tests;

public sealed class WorldSessionVendorTests
{
    private static WorldSession NewSession()
    {
        var ep = new IPEndPoint(IPAddress.Loopback, 65001);
        return new WorldSession(ep);
    }

    [Fact]
    public void SendBuy_EmitsBytesIdenticalToVendorRequestsBuildBuy()
    {
        using var session = NewSession();
        byte[]? captured = null;
        session.GameActionCapture = body => captured = body;

        session.SendBuy(
            vendorGuid: 0x40001000u,
            itemGuid: 0x50002000u,
            amount: 3,
            alternateCurrencyId: 0u);

        byte[] expected = VendorRequests.BuildBuy(
            gameActionSequence: 1,
            vendorGuid: 0x40001000u,
            amount: 3,
            itemGuid: 0x50002000u,
            alternateCurrencyId: 0u);

        Assert.NotNull(captured);
        Assert.Equal(expected, captured);
    }

    [Fact]
    public void SendBuy_IncrementsTheSharedGameActionSequenceLikeEveryOtherSend()
    {
        using var session = NewSession();
        byte[]? captured = null;
        session.GameActionCapture = body => captured = body;

        session.SendTalk("first");
        session.SendBuy(0x40001000u, 0x50002000u, 1, 0u); // should be sequence 2

        byte[] expected = VendorRequests.BuildBuy(
            gameActionSequence: 2,
            vendorGuid: 0x40001000u,
            amount: 1,
            itemGuid: 0x50002000u,
            alternateCurrencyId: 0u);

        Assert.Equal(expected, captured);
    }


    [Fact]
    public void SendBuy_BatchedOverload_EmitsBytesIdenticalToVendorRequestsBuildBuyWithAList()
    {
        using var session = NewSession();
        byte[]? captured = null;
        session.GameActionCapture = body => captured = body;

        var items = new (int Amount, uint ItemGuid)[]
        {
            (1, 0x50002000u),
            (10, 0x50002001u),
        };
        session.SendBuy(0x40001000u, items, 0u);

        byte[] expected = VendorRequests.BuildBuy(
            gameActionSequence: 1,
            vendorGuid: 0x40001000u,
            items: items,
            alternateCurrencyId: 0u);

        Assert.NotNull(captured);
        Assert.Equal(expected, captured);
    }


    [Fact]
    public void SendSell_EmitsBytesIdenticalToVendorRequestsBuildSell()
    {
        using var session = NewSession();
        byte[]? captured = null;
        session.GameActionCapture = body => captured = body;

        var items = new (int Amount, uint ItemGuid)[] { (1, 0x50002000u) };
        session.SendSell(0x40001000u, items);

        byte[] expected = VendorRequests.BuildSell(
            gameActionSequence: 1,
            vendorGuid: 0x40001000u,
            items: items);

        Assert.NotNull(captured);
        Assert.Equal(expected, captured);
    }

    [Fact]
    public void SendSell_IncrementsTheSharedGameActionSequenceLikeEveryOtherSend()
    {
        using var session = NewSession();
        byte[]? captured = null;
        session.GameActionCapture = body => captured = body;

        session.SendTalk("first");
        var items = new (int Amount, uint ItemGuid)[] { (1, 0x50002000u) };
        session.SendSell(0x40001000u, items); // should be sequence 2

        byte[] expected = VendorRequests.BuildSell(
            gameActionSequence: 2,
            vendorGuid: 0x40001000u,
            items: items);

        Assert.Equal(expected, captured);
    }
}
