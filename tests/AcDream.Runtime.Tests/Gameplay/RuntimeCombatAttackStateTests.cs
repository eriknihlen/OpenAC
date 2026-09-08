using AcDream.Core.Combat;
using AcDream.Runtime.Gameplay;

namespace AcDream.Runtime.Tests.Gameplay;

public sealed class RuntimeCombatAttackStateTests
{
    [Fact]
    public void NormalStance_ChargesLinearlyOverOneSecond_AndReleaseSendsCurrentPower()
    {
        double now = 10d;
        var sent = new List<(AttackHeight Height, float Power)>();
        var combat = new CombatState();
        using var controller = Create(combat, () => now, sent);
        combat.SetCombatMode(CombatMode.Melee);

        controller.PressAttack(AttackHeight.High);
        now = 10.5d;

        Assert.Equal(0.5f, controller.PowerBarLevel, 3);
        controller.ReleaseAttack();

        var attack = Assert.Single(sent);
        Assert.Equal(AttackHeight.High, attack.Height);
        Assert.Equal(0.5f, attack.Power, 3);
    }

    [Fact]
    public void DualWieldStance_UsesRetailPointEightSecondPowerUpTime()
    {
        double now = 3d;
        var combat = new CombatState();
        using var controller = Create(
            combat,
            () => now,
            [],
            isDualWield: () => true);
        combat.SetCombatMode(CombatMode.Melee);

        controller.PressAttack(AttackHeight.Medium);
        now = 3.4d;

        Assert.Equal(0.5f, controller.PowerBarLevel, 3);
    }

    [Fact]
    public void EarlyRelease_CommitsOnUseTimeTickAtReleasedPower()
    {
        double now = 0d;
        var sent = new List<(AttackHeight Height, float Power)>();
        var combat = new CombatState();
        using var controller = Create(combat, () => now, sent);
        combat.SetCombatMode(CombatMode.Missile);
        controller.SetDesiredPower(1f);

        controller.PressAttack(AttackHeight.Low);
        now = 0.25d;
        controller.ReleaseAttack();
        Assert.Empty(sent);

        now = 0.26d;
        controller.Tick();

        var attack = Assert.Single(sent);
        Assert.Equal(AttackHeight.Low, attack.Height);
        Assert.Equal(0.25f, attack.Power, 3);
    }

    [Fact]
    public void HoldBinding_UsesPressAndReleaseTransitions()
    {
        double now = 1d;
        var sent = new List<(AttackHeight Height, float Power)>();
        var combat = new CombatState();
        using var controller = Create(combat, () => now, sent);
        combat.SetCombatMode(CombatMode.Melee);

        Assert.True(controller.HandleCommand(new RuntimeCombatAttackInput(
            RuntimeCombatAttackCommand.LowAttack,
            RuntimeInputActivation.Press)));
        now = 1.5d;
        Assert.True(controller.HandleCommand(new RuntimeCombatAttackInput(
            RuntimeCombatAttackCommand.LowAttack,
            RuntimeInputActivation.Release)));

        Assert.Equal(AttackHeight.Low, Assert.Single(sent).Height);
    }

    [Fact]
    public void LeavingTargetedCombat_CancelsAnActiveBuild()
    {
        double now = 0d;
        var combat = new CombatState();
        using var controller = Create(combat, () => now, []);
        combat.SetCombatMode(CombatMode.Melee);
        controller.PressAttack(AttackHeight.Medium);
        now = 0.4d;

        combat.SetCombatMode(CombatMode.NonCombat);

        Assert.False(controller.BuildInProgress);
        Assert.False(controller.AttackRequestInProgress);
        Assert.Equal(0f, controller.PowerBarLevel);
    }

    [Fact]
    public void RequestWaitsUntilPlayerReachesReadyStanceBeforeBuilding()
    {
        double now = 0d;
        bool ready = false;
        var combat = new CombatState();
        using var controller = new RuntimeCombatAttackState(
            combat,
            () => true,
            (_, _) => true,
            playerReadyForAttack: () => ready,
            now: () => now);
        combat.SetCombatMode(CombatMode.Missile);

        controller.PressAttack(AttackHeight.High);
        Assert.True(controller.AttackRequestInProgress);
        Assert.False(controller.BuildInProgress);

        ready = true;
        now = 5d;
        controller.Tick();

        Assert.True(controller.BuildInProgress);
        Assert.Equal(0f, controller.PowerBarLevel);
    }

    [Fact]
    public void MovementAbort_DuringRepeat_SendsCancelAndPreventsNextAttack()
    {
        double now = 20d;
        int cancels = 0;
        var sent = new List<(AttackHeight Height, float Power)>();
        var combat = new CombatState();
        using var controller = new RuntimeCombatAttackState(
            combat,
            canStartAttack: () => true,
            sendAttack: (height, power) =>
            {
                sent.Add((height, power));
                return true;
            },
            sendCancelAttack: () => cancels++,
            autoRepeatAttack: () => true,
            now: () => now);
        combat.SetCombatMode(CombatMode.Melee);

        controller.PressAttack(AttackHeight.Medium);
        now += 0.5d;
        controller.ReleaseAttack();
        Assert.Single(sent);
        Assert.True(controller.RepeatAttackInProgress);

        controller.HandleCommand(new RuntimeCombatAttackInput(
            RuntimeCombatAttackCommand.AbortForMovement,
            RuntimeInputActivation.Press));
        combat.OnAttackDone(1u, 0u);

        Assert.Equal(1, cancels);
        Assert.Single(sent);
        Assert.False(controller.RepeatAttackInProgress);
        Assert.False(controller.BuildInProgress);
        Assert.Equal(0f, controller.PowerBarLevel);
    }

    [Fact]
    public void MovementAbort_WhileIdle_DoesNotSendCancel()
    {
        int cancels = 0;
        var combat = new CombatState();
        using var controller = new RuntimeCombatAttackState(
            combat,
            canStartAttack: () => true,
            sendAttack: (_, _) => true,
            sendCancelAttack: () => cancels++);

        controller.AbortAutomaticAttack();

        Assert.Equal(0, cancels);
    }

    [Fact]
    public void AttackDonePublishesOneCompletionReceiptAndResetClearsIt()
    {
        var combat = new CombatState();
        using var controller = new RuntimeCombatAttackState(
            combat,
            canStartAttack: () => true,
            sendAttack: (_, _) => true);

        combat.OnAttackDone(47u, 0x1234u);

        Assert.Equal(1, controller.CompletionRevision);
        Assert.Equal(47u, controller.CompletionSequence);
        Assert.Equal(0x1234u, controller.CompletionWeenieError);

        controller.ResetSession();
        Assert.Equal(0, controller.CompletionRevision);
        Assert.Equal(0u, controller.CompletionSequence);
        Assert.Equal(0u, controller.CompletionWeenieError);
    }

    [Fact]
    public void StartAttackRequest_PreparesPlayerMovementBeforePowerBuild()
    {
        var events = new List<string>();
        var combat = new CombatState();
        RuntimeCombatAttackState? controller = null;
        controller = new RuntimeCombatAttackState(
            combat,
            canStartAttack: () => true,
            sendAttack: (_, _) =>
            {
                events.Add("send");
                return true;
            },
            prepareAttackRequest: () =>
            {
                Assert.True(controller!.AttackRequestInProgress);
                Assert.Equal(1f, controller.RequestedAttackPower);
                events.Add("prepare");
            });
        using (controller)
        {
        combat.SetCombatMode(CombatMode.Missile);
        controller.SetDesiredPower(0f);

        controller.PressAttack(AttackHeight.Medium);

        Assert.Equal(new[] { "prepare" }, events);
        Assert.True(controller.AttackRequestInProgress);
        Assert.True(controller.BuildInProgress);

        controller.ReleaseAttack();
        Assert.Equal(new[] { "prepare", "send" }, events);
        }
    }

    [Fact]
    public void ResetSession_RestoresRetailBeginDefaultsWithoutSendingCancel()
    {
        double now = 1d;
        int cancels = 0;
        var combat = new CombatState();
        using var controller = new RuntimeCombatAttackState(
            combat,
            canStartAttack: () => true,
            sendAttack: (_, _) => true,
            sendCancelAttack: () => cancels++,
            now: () => now);
        combat.SetCombatMode(CombatMode.Melee);
        controller.SetDesiredPower(1f);
        controller.PressAttack(AttackHeight.High);
        now = 1.5d;

        controller.ResetSession();

        Assert.False(controller.AttackRequestInProgress);
        Assert.False(controller.BuildInProgress);
        Assert.Equal(0f, controller.RequestedAttackPower);
        Assert.Equal(0f, controller.PowerBarLevel);
        Assert.Equal(AttackHeight.Medium, controller.RequestedHeight);
        Assert.Equal(RuntimeCombatAttackState.InitialDesiredPower, controller.DesiredPower);
        Assert.Equal(0, cancels);
    }

    private static RuntimeCombatAttackState Create(
        CombatState combat,
        Func<double> now,
        List<(AttackHeight Height, float Power)> sent,
        Func<bool>? isDualWield = null)
        => new(
            combat,
            canStartAttack: () => true,
            sendAttack: (height, power) =>
            {
                sent.Add((height, power));
                return true;
            },
            isDualWield: isDualWield,
            autoRepeatAttack: () => false,
            now: now);
}
