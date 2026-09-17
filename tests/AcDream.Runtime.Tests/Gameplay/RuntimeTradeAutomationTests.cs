using AcDream.Core.Net.Messages;
using AcDream.Plugin.Abstractions;
using AcDream.Runtime.Gameplay;
using AcDream.Runtime.Tests.Support;

namespace AcDream.Runtime.Tests.Gameplay;

// RuntimeTradeAutomation is the E-TRADE plugin surface adapter: it projects
// RuntimeTradeState and sends outbound trade commands over the live
// WorldSession, shared verbatim by the graphical and headless hosts.
public sealed class RuntimeTradeAutomationTests
{
    [Fact]
    public void CommandsReportUnavailableBeforeTheSessionReachesInWorld()
    {
        using var host = new NoWindowGameRuntimeHost();
        var trade = new RuntimeTradeAutomation(host.Runtime);

        Assert.False(trade.IsAvailable);
        Assert.Equal(
            PluginTradeCommandStatus.Unavailable,
            trade.Add(0x80000001u).Status);
        Assert.Equal(
            PluginTradeCommandStatus.Unavailable,
            trade.Accept().Status);
    }

    [Fact]
    public void CommandsReportNotOpenWhenNoTradeIsRegistered()
    {
        using var host = new NoWindowGameRuntimeHost();
        host.Start();
        var trade = new RuntimeTradeAutomation(host.Runtime);

        Assert.True(trade.IsAvailable);
        Assert.False(trade.IsOpen);
        Assert.Equal(
            PluginTradeCommandStatus.NotOpen,
            trade.Add(0x80000001u).Status);
        Assert.Equal(
            PluginTradeCommandStatus.NotOpen,
            trade.Accept().Status);
    }

    [Fact]
    public void ProjectsTheRegisteredTradeAndSendsAcceptedCommands()
    {
        using var host = new NoWindowGameRuntimeHost();
        host.Start();
        uint self = host.Runtime.PlayerIdentity.ServerGuid;
        uint partner = 0x70000099u;
        host.Runtime.TradeOwner.ApplyRegister(
            new GameEvents.RegisterTrade(self, partner, 0uL),
            self);
        var trade = new RuntimeTradeAutomation(host.Runtime);
        var captured = new List<byte[]>();
        host.Runtime.Session.CurrentSession!.GameActionCapture = body => captured.Add(body);

        Assert.True(trade.IsOpen);
        Assert.Equal(partner, trade.PartnerObjectId);
        Assert.False(trade.MyAccepted);
        Assert.False(trade.PartnerAccepted);

        Assert.Equal(
            PluginTradeCommandStatus.Sent,
            trade.Add(0x80000001u).Status);
        Assert.NotEmpty(captured);
        Assert.Equal(
            PluginTradeCommandStatus.Sent,
            trade.Accept().Status);
        Assert.Equal(
            PluginTradeCommandStatus.Sent,
            trade.Decline().Status);
        Assert.Equal(
            PluginTradeCommandStatus.Sent,
            trade.Reset().Status);
        Assert.Equal(
            PluginTradeCommandStatus.Sent,
            trade.End().Status);
    }

    [Fact]
    public void RejectsAddingAnInvalidItem()
    {
        using var host = new NoWindowGameRuntimeHost();
        host.Start();
        uint self = host.Runtime.PlayerIdentity.ServerGuid;
        host.Runtime.TradeOwner.ApplyRegister(
            new GameEvents.RegisterTrade(self, 0x70000099u, 0uL),
            self);
        var trade = new RuntimeTradeAutomation(host.Runtime);

        Assert.Equal(
            PluginTradeCommandStatus.InvalidItem,
            trade.Add(0u).Status);
    }

    [Fact]
    public void PollRaisesOpenedClosedAndItemAddedFromStateTransitions()
    {
        using var host = new NoWindowGameRuntimeHost();
        host.Start();
        uint self = host.Runtime.PlayerIdentity.ServerGuid;
        uint partner = 0x70000099u;
        var trade = new RuntimeTradeAutomation(host.Runtime);

        int openedCount = 0;
        int closedCount = 0;
        var addedMine = new List<uint>();
        var addedTheirs = new List<uint>();
        trade.Opened += _ => openedCount++;
        trade.Closed += () => closedCount++;
        trade.ItemAdded += added =>
        {
            if (added.Mine) addedMine.Add(added.ItemObjectId);
            else addedTheirs.Add(added.ItemObjectId);
        };

        // No poll yet -- nothing has fired.
        Assert.Equal(0, openedCount);

        host.Runtime.TradeOwner.ApplyRegister(
            new GameEvents.RegisterTrade(self, partner, 0uL),
            self);
        trade.Poll();
        Assert.Equal(1, openedCount);

        host.Runtime.TradeOwner.ApplyAdd(
            new GameEvents.AddToTrade(0x80000001u, (uint)RuntimeTradeSide.Self, 0u));
        host.Runtime.TradeOwner.ApplyAdd(
            new GameEvents.AddToTrade(0x80000002u, (uint)RuntimeTradeSide.Partner, 0u));
        trade.Poll();
        Assert.Equal([0x80000001u], addedMine);
        Assert.Equal([0x80000002u], addedTheirs);

        // Polling again without a new revision must not re-raise ItemAdded.
        trade.Poll();
        Assert.Single(addedMine);
        Assert.Single(addedTheirs);

        host.Runtime.TradeOwner.ApplyClose();
        trade.Poll();
        Assert.Equal(1, closedCount);
        Assert.False(trade.IsOpen);
    }

    [Fact]
    public void PollRaisesPartnerTradeAcceptedOnlyOnTheRisingEdge()
    {
        using var host = new NoWindowGameRuntimeHost();
        host.Start();
        uint self = host.Runtime.PlayerIdentity.ServerGuid;
        uint partner = 0x70000099u;
        host.Runtime.TradeOwner.ApplyRegister(
            new GameEvents.RegisterTrade(self, partner, 0uL),
            self);
        var trade = new RuntimeTradeAutomation(host.Runtime);
        trade.Poll();

        int accepted = 0;
        uint lastPartner = 0u;
        trade.PartnerTradeAccepted += id =>
        {
            accepted++;
            lastPartner = id;
        };

        host.Runtime.TradeOwner.ApplyAccept(partner, self);
        trade.Poll();
        Assert.Equal(1, accepted);
        Assert.Equal(partner, lastPartner);

        // Bumping the revision again without a fresh accept must not re-fire.
        host.Runtime.TradeOwner.ApplyAdd(
            new GameEvents.AddToTrade(0x80000003u, (uint)RuntimeTradeSide.Self, 0u));
        trade.Poll();
        Assert.Equal(1, accepted);
    }

    [Fact]
    public void APartnerSwapWithinOneOpenWindowClosesTheOldTradeAndOpensTheNew()
    {
        using var host = new NoWindowGameRuntimeHost();
        host.Start();
        uint self = host.Runtime.PlayerIdentity.ServerGuid;
        uint firstPartner = 0x70000099u;
        uint secondPartner = 0x7000009Au;
        host.Runtime.TradeOwner.ApplyRegister(
            new GameEvents.RegisterTrade(self, firstPartner, 0uL),
            self);
        var trade = new RuntimeTradeAutomation(host.Runtime);

        var opened = new List<uint>();
        int closed = 0;
        trade.Opened += o => opened.Add(o.PartnerObjectId);
        trade.Closed += () => closed++;

        trade.Poll();
        Assert.Equal([firstPartner], opened);
        Assert.Equal(0, closed);

        // The retail-look window's own trade owner can register a second
        // trade for a different partner while IsOpen never dipped to false
        // in between -- a Poll() cadence that only diffs IsOpen would miss
        // this entirely and keep reporting the first partner.
        host.Runtime.TradeOwner.ApplyRegister(
            new GameEvents.RegisterTrade(self, secondPartner, 0uL),
            self);
        trade.Poll();

        Assert.Equal(1, closed);
        Assert.Equal([firstPartner, secondPartner], opened);
        Assert.Equal(secondPartner, trade.PartnerObjectId);
    }

    [Fact]
    public void AcceptIsANoOpOnceTheLocalSideAlreadyAccepted()
    {
        using var host = new NoWindowGameRuntimeHost();
        host.Start();
        uint self = host.Runtime.PlayerIdentity.ServerGuid;
        uint partner = 0x70000099u;
        host.Runtime.TradeOwner.ApplyRegister(
            new GameEvents.RegisterTrade(self, partner, 0uL),
            self);
        var trade = new RuntimeTradeAutomation(host.Runtime);
        var captured = new List<byte[]>();
        host.Runtime.Session.CurrentSession!.GameActionCapture = body => captured.Add(body);

        Assert.Equal(PluginTradeCommandStatus.Sent, trade.Accept().Status);
        Assert.NotEmpty(captured);

        // Simulates the server echoing the accept back down the wire.
        host.Runtime.TradeOwner.ApplyAccept(self, self);
        Assert.True(trade.MyAccepted);

        captured.Clear();
        Assert.Equal(PluginTradeCommandStatus.AlreadyAccepted, trade.Accept().Status);
        Assert.Empty(captured);
    }
}
