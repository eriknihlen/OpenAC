using AcDream.Core.Combat;
using AcDream.Core.Items;
using AcDream.Core.Selection;
using AcDream.Runtime.Gameplay;

namespace AcDream.Runtime.Tests.Gameplay;

public sealed class RuntimeActionStateTests
{
    [Fact]
    public void HealthActivityPublishesMonotonicRevisionAndAgeAndResets()
    {
        double now = 10d;
        using var inventory = NewInventoryTransactions();
        using var actions = RuntimeActionTestFactory.Create(inventory, () => now);

        actions.Combat.OnUpdateHealth(0x50000001u, 0.75f);
        now = 12.5d;

        Assert.True(actions.TryGetHealthActivity(
            0x50000001u, out long firstRevision, out double age));
        Assert.Equal(1, firstRevision);
        Assert.Equal(2.5d, age, 3);

        actions.Combat.OnUpdateHealth(0x50000001u, 0.5f);
        Assert.True(actions.TryGetHealthActivity(
            0x50000001u, out long secondRevision, out age));
        Assert.True(secondRevision > firstRevision);
        Assert.Equal(0d, age, 3);

        actions.ResetSession();
        Assert.False(actions.TryGetHealthActivity(
            0x50000001u, out _, out double missingAge));
        Assert.True(double.IsPositiveInfinity(missingAge));
    }

    [Fact]
    public void ViewProjectsTheExactCanonicalChildrenAndRevisions()
    {
        using var inventory = NewInventoryTransactions();
        using var actions = RuntimeActionTestFactory.Create(inventory);

        actions.Selection.Select(
            0x50000001u,
            SelectionChangeSource.World);
        actions.Combat.SetCombatMode(CombatMode.Missile);
        actions.Combat.OnUpdateHealth(0x50000001u, 0.625f);
        actions.Interaction.EnterUseItemOnTarget(0x70000001u);

        RuntimeActionSnapshot snapshot = actions.View.Snapshot;

        Assert.Equal(1, snapshot.SelectionRevision);
        Assert.Equal(0x50000001u, snapshot.SelectedObjectId);
        Assert.Equal(2, snapshot.CombatRevision);
        Assert.Equal(CombatMode.Missile, snapshot.CombatMode);
        Assert.Equal(1, snapshot.TrackedTargetHealthCount);
        Assert.Equal(1, snapshot.InteractionRevision);
        Assert.Equal(
            InteractionModeKind.UseItemOnTarget,
            snapshot.InteractionMode);
        Assert.Equal(0x70000001u, snapshot.InteractionSourceObjectId);
        Assert.True(
            actions.View.TryGetHealth(0x50000001u, out float health));
        Assert.Equal(0.625f, health);
    }

    [Fact]
    public void ResetSessionConvergesEveryChildAfterObserverFailures()
    {
        using var inventory = NewInventoryTransactions();
        var actions = RuntimeActionTestFactory.Create(inventory);
        actions.Selection.Select(
            0x50000001u,
            SelectionChangeSource.World);
        actions.Combat.SetCombatMode(CombatMode.Melee);
        actions.Combat.OnUpdateHealth(0x50000001u, 0.25f);
        actions.Interaction.EnterExamine();

        actions.Selection.Changed += static _ =>
            throw new InvalidOperationException("selection observer");
        actions.Combat.CombatModeChanged += static _ =>
            throw new InvalidOperationException("combat observer");
        actions.Interaction.Changed += static _ =>
            throw new InvalidOperationException("interaction observer");

        Assert.Throws<AggregateException>(actions.ResetSession);

        RuntimeActionOwnershipSnapshot snapshot =
            actions.CaptureOwnership();
        Assert.Equal(0u, snapshot.SelectedObjectId);
        Assert.Equal(0u, snapshot.PreviousObjectId);
        Assert.Equal(0u, snapshot.PreviousValidObjectId);
        Assert.Equal(CombatMode.NonCombat, snapshot.CombatMode);
        Assert.Equal(0, snapshot.TrackedTargetHealthCount);
        Assert.Equal(InteractionMode.None, snapshot.InteractionMode);
        Assert.Throws<AggregateException>(actions.Dispose);
    }

    [Fact]
    public void DisposeIsTerminalAndConvergedAfterObserverFailures()
    {
        using var inventory = NewInventoryTransactions();
        var actions = RuntimeActionTestFactory.Create(inventory);
        actions.Selection.Select(
            0x50000001u,
            SelectionChangeSource.World);
        actions.Combat.SetCombatMode(CombatMode.Magic);
        actions.Combat.OnUpdateHealth(0x50000001u, 0.5f);
        actions.Interaction.EnterUse();
        actions.Selection.Changed += static _ =>
            throw new InvalidOperationException("selection observer");
        actions.Combat.CombatModeChanged += static _ =>
            throw new InvalidOperationException("combat observer");
        actions.Interaction.Changed += static _ =>
            throw new InvalidOperationException("interaction observer");

        Assert.Throws<AggregateException>(actions.Dispose);

        RuntimeActionOwnershipSnapshot snapshot =
            actions.CaptureOwnership();
        Assert.True(snapshot.IsConverged);
        actions.Dispose();
    }

    [Fact]
    public void InstancesAreFullyIsolated()
    {
        using var firstInventory = NewInventoryTransactions();
        using var secondInventory = NewInventoryTransactions();
        using var first = RuntimeActionTestFactory.Create(firstInventory);
        using var second = RuntimeActionTestFactory.Create(secondInventory);

        first.Selection.Select(
            0x50000001u,
            SelectionChangeSource.World);
        first.Combat.SetCombatMode(CombatMode.Missile);
        first.Interaction.EnterUse();

        Assert.Equal(
            new RuntimeActionSnapshot(
                0,
                0u,
                0u,
                0u,
                0,
                CombatMode.NonCombat,
                0,
                0,
                InteractionModeKind.None,
                0u,
                new RuntimeInteractionTransactionSnapshot(
                    IsDisposed: false,
                    Revision: 0,
                    LastUseSourceId: 0u,
                    LastUseTargetId: 0u,
                    AwaitingAppraisalId: 0u,
                    CurrentAppraisalId: 0u,
                    OutboundCount: 0,
                    HasPendingPickup: false,
                    PendingPickupToken: 0u,
                    DispatchFailureCount: 0),
                new RuntimeCombatAttackSnapshot(
                    Revision: 0,
                    RequestedHeight: AttackHeight.Medium,
                    DesiredPower: 0.5f,
                    PowerBarLevel: 0f,
                    BuildInProgress: false,
                    RequestInProgress: false,
                    RequestedPower: 0f),
                new RuntimeSpellCastSnapshot(
                    Revision: 0,
                    LastRequestedSpellId: 0u,
                    LastRequestedTargetId: 0u)),
            second.View.Snapshot);
    }

    private static InventoryTransactionState NewInventoryTransactions() =>
        new(new ClientObjectTable());
}
