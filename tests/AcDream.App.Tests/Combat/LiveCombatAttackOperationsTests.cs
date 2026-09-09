using AcDream.App.Combat;
using AcDream.App.Input;
using AcDream.App.Net;
using AcDream.Core.Combat;
using AcDream.Runtime.Gameplay;
using AcDream.Core.Net;

namespace AcDream.App.Tests.Combat;

public sealed class LiveCombatAttackOperationsTests
{
    [Fact]
    public void UnboundSlotFailsClosedAndRejectsASecondOwner()
    {
        var slot = new CombatAttackOperationsSlot();
        var first = new FakeOperations();
        var second = new FakeOperations();

        Assert.False(slot.CanStartAttack());
        Assert.False(slot.SendAttack(AttackHeight.Medium, 0.5f));
        Assert.False(slot.PlayerReadyForAttack);

        slot.Bind(first);
        slot.Bind(first);
        Assert.Throws<InvalidOperationException>(() => slot.Bind(second));
    }

    [Fact]
    public void OwnedBindingReleasesOnlyItsExactOwnerAndAllowsRebind()
    {
        var slot = new CombatAttackOperationsSlot();
        var first = new FakeOperations { CanStartValue = false };
        var second = new FakeOperations { CanStartValue = true };

        IDisposable firstBinding = slot.BindOwned(first);
        Assert.Throws<InvalidOperationException>(() => slot.BindOwned(second));

        firstBinding.Dispose();
        using IDisposable secondBinding = slot.BindOwned(second);
        firstBinding.Dispose();

        Assert.True(slot.CanStartAttack());
    }

    [Fact]
    public void CanStartRequiresLiveWorldBeforeTargetResolution()
    {
        Harness harness = CreateHarness();
        harness.Targets.Acquired = 0x1234u;

        Assert.False(harness.Owner.CanStartAttack());

        Assert.Equal(0, harness.Targets.ResolveCount);
    }

    [Fact]
    public void UnsupportedCombatModeRefusesSilently()
    {
        Harness harness = CreateHarness(inWorld: true);

        Assert.False(harness.Owner.CanStartAttack());

        Assert.Empty(harness.Feedback.Messages);
        Assert.Equal(0, harness.Targets.ResolveCount);
    }

    [Fact]
    public void SupportedModeResolvesTargetUsingLiveAutoTargetSetting()
    {
        Harness harness = CreateHarness(inWorld: true);
        harness.Combat.SetCombatMode(CombatMode.Melee);
        harness.Settings.AutoTarget = true;
        harness.Targets.Acquired = 0x1234u;
        harness.Targets.SelectedObjectId = 0x1234u;

        Assert.True(harness.Owner.CanStartAttack());

        Assert.True(harness.Targets.LastAutoTarget);
        Assert.Equal(1, harness.Targets.ResolveCount);
        Assert.Empty(harness.Feedback.Messages);
    }

    [Fact]
    public void MissingTargetPreservesRetailUserFeedback()
    {
        Harness harness = CreateHarness(inWorld: true);
        harness.Combat.SetCombatMode(CombatMode.Missile);

        Assert.False(harness.Owner.CanStartAttack());

        Assert.Equal(
            [AcDream.Core.Chat.ClientTextRefusals.MustSelectCombatTarget],
            harness.Feedback.Messages);
    }

    private static Harness CreateHarness(bool inWorld = false)
    {
        var combat = new CombatState();
        var targets = new FakeTargets();
        var settings = new FakeSettings();
        var player = new RuntimeLocalPlayerMovementState();
        var live = new FakeLiveSource { IsInWorld = inWorld };
        var feedback = new FakeFeedback();
        var outbound = new LocalPlayerOutboundController(
            static (_, _, _, _, _, _) => { });
        var owner = new LiveCombatAttackOperations(
            combat,
            targets,
            settings,
            player,
            outbound,
            live,
            live,
            feedback);
        return new Harness(owner, combat, targets, settings, feedback);
    }

    private sealed record Harness(
        LiveCombatAttackOperations Owner,
        CombatState Combat,
        FakeTargets Targets,
        FakeSettings Settings,
        FakeFeedback Feedback);

    private sealed class FakeTargets : ICombatAttackTargetSource
    {
        public uint? SelectedObjectId { get; set; }
        public uint? Acquired { get; set; }
        public int ResolveCount { get; private set; }
        public bool LastAutoTarget { get; private set; }
        public uint? GetSelectedOrClosestCombatTarget(bool autoTarget)
        {
            ResolveCount++;
            LastAutoTarget = autoTarget;
            return Acquired;
        }
    }

    private sealed class FakeSettings : ICombatGameplaySettingsSource
    {
        public bool AutoTarget { get; set; }
        public bool AutoRepeatAttack { get; set; }
        public bool ViewCombatTarget { get; set; }
    }

    private sealed class FakeLiveSource : ILiveInWorldSource, ILiveWorldSessionSource
    {
        public bool IsInWorld { get; set; }
        public WorldSession? CurrentSession => null;
    }

    private sealed class FakeFeedback : ICombatFeedbackSink
    {
        public List<string> Messages { get; } = [];
        public void Show(string message) => Messages.Add(message);
    }

    private sealed class FakeOperations : IRuntimeCombatAttackOperations
    {
        public bool CanStartValue { get; init; } = true;
        public bool CanStartAttack() => CanStartValue;
        public void PrepareAttackRequest()
        {
        }
        public bool SendAttack(AttackHeight height, float power) => true;
        public void SendCancelAttack()
        {
        }
        public bool IsDualWield => true;
        public bool PlayerReadyForAttack => true;
        public bool AutoRepeatAttack => true;
    }
}
