using AcDream.Core.Combat;
using AcDream.Core.Net.Messages;
using AcDream.Headless.Plugins;
using AcDream.Plugin.Abstractions;
using AcDream.Runtime;
using AcDream.Runtime.Gameplay;

namespace AcDream.Headless.Tests;

// The headless host shares RuntimeTradeAutomation / RuntimeVendorAutomation
// with the graphical host over the same GameRuntime; the behaviour itself is
// covered at the Runtime layer. These tests only check the headless-specific
// wiring: the surface is real (not the NoOp default), events are delivered
// once FireTick polls, and Dispose tears the vendor subscription down
// cleanly.
public sealed class HeadlessTradeVendorAutomationTests
{
    [Fact]
    public void TradeAndVendorAreRealNotTheNoOpDefault()
    {
        using GameRuntime runtime = NewRuntime();
        using var host = NewHost(runtime);

        Assert.NotSame(NoOpAutomationSurface.Instance, host.Automation.Trade);
        Assert.NotSame(NoOpAutomationSurface.Instance, host.Automation.Vendor);
    }

    [Fact]
    public void FireTickPollsTradeSoOpenedEventuallyFires()
    {
        using GameRuntime runtime = NewRuntime();
        using var host = NewHost(runtime);
        int opened = 0;
        host.Automation.Trade.Opened += _ => opened++;

        runtime.TradeOwner.ApplyRegister(
            new GameEvents.RegisterTrade(
                runtime.PlayerIdentity.ServerGuid, 0x70000099u, 0uL),
            runtime.PlayerIdentity.ServerGuid);
        Assert.Equal(0, opened);

        host.FireTick(1d / 30d);

        Assert.Equal(1, opened);
    }

    [Fact]
    public void FireTickPollsVendorTransactionCompletion()
    {
        using GameRuntime runtime = NewRuntime();
        using var host = NewHost(runtime);
        int completions = 0;
        host.Automation.Vendor.TransactionCompleted += _ => completions++;

        // No pending transaction: polling must not spuriously report one.
        host.FireTick(1d / 30d);
        Assert.Equal(0, completions);
    }

    [Fact]
    public void DisposeTearsDownTheVendorSubscriptionWithoutThrowing()
    {
        GameRuntime runtime = NewRuntime();
        var host = new HeadlessPluginHost(runtime, new InertLogger());

        host.Dispose();

        // A vendor transition after disposal must not throw back into a
        // torn-down surface.
        var exception = Record.Exception(() => runtime.InventoryOwner.Vendor.Apply(
            0x40001000u,
            default,
            Array.Empty<AcDream.Core.Items.VendorShopItem>()));
        Assert.Null(exception);
        runtime.Dispose();
    }

    // Characterization test: calls RuntimeTradeState.ApplyRegister/ApplyAccept
    // directly rather than through the wire router, so it was already
    // green on f868960 (before any of the A9 fixes). It documents that
    // FireTick's per-tick Poll() delivers PartnerTradeAccepted once the
    // shared RuntimeTradeAutomation observes the state transition -- it does
    // NOT prove the wire message reaches TradeOwner on the headless host
    // (see RegisterTradeGameEventReachesTradeOwnerThroughTheRealWireRouter
    // in HeadlessSessionHostTests.cs for that).
    [Fact]
    public void FireTickPollsTradePartnerAcceptSoPluginsSeeIt()
    {
        using GameRuntime runtime = NewRuntime();
        using var host = NewHost(runtime);
        var partners = new List<uint>();
        host.Automation.Trade.PartnerTradeAccepted += partners.Add;

        uint self = runtime.PlayerIdentity.ServerGuid;
        const uint partnerGuid = 0x70000099u;
        runtime.TradeOwner.ApplyRegister(
            new GameEvents.RegisterTrade(self, partnerGuid, 0uL), self);
        host.FireTick(1d / 30d);
        Assert.Empty(partners);

        runtime.TradeOwner.ApplyAccept(partnerGuid, self);
        host.FireTick(1d / 30d);

        Assert.Equal([partnerGuid], partners);
    }
    private static GameRuntime NewRuntime()
    {
        var operations = new InertOperations();
        return new GameRuntime(new GameRuntimeDependencies(
            operations,
            operations,
            operations,
            operations));
    }

    private static HeadlessPluginHost NewHost(GameRuntime runtime) =>
        new(runtime, new InertLogger());

    private sealed class InertLogger : IPluginLogger
    {
        public void Info(string message) { }
        public void Warn(string message) { }
        public void Error(string message, Exception? error = null) { }
    }

    private sealed class InertOperations
        : IRuntimeCombatAttackOperations,
          IRuntimeCombatTargetOperations,
          IRuntimeCombatModeOperations,
          IRuntimeSpellCastOperations
    {
        public bool CanStartAttack() => false;
        public void PrepareAttackRequest() { }
        public bool SendAttack(AttackHeight height, float power) => false;
        public void SendCancelAttack() { }
        public bool IsDualWield => false;
        public bool PlayerReadyForAttack => false;
        public bool AutoRepeatAttack => false;
        public bool AutoTarget => false;
        public uint? SelectClosestTarget() => null;
        public bool IsInWorld => false;
        public IReadOnlyList<AcDream.Core.Items.ClientObject> GetOrderedEquipment() => [];
        public void NotifyExplicitCombatModeRequest() { }
        public void SendChangeCombatMode(CombatMode mode) { }
        public uint LocalPlayerId => 0u;
        public bool CanSend => false;
        public bool HasRequiredComponents(uint spellId) => true;
        public bool IsTargetCompatible(
            uint targetId,
            AcDream.Core.Spells.SpellMetadata spell,
            bool showMessage) => true;
        public void StopCompletely() { }
        public void SendUntargeted(uint spellId) { }
        public void SendTargeted(uint targetId, uint spellId) { }
        public void DisplayMessage(string message) { }
        public void IncrementBusy() { }
    }
}
