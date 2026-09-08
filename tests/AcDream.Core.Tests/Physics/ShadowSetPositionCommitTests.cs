using System.Collections.Immutable;
using System.Numerics;
using AcDream.Core.Physics;

namespace AcDream.Core.Tests.Physics;

public sealed class ShadowSetPositionCommitTests
{
    private const uint Landblock = 0xA9B40000u;
    private const uint Cell1 = Landblock | 0x0001u;
    private const uint Cell9 = Landblock | 0x0009u;

    [Fact]
    public void PreparedAuthoredMoveKeepsOldRowsUntilAtomicApply()
    {
        var registry = RegisteredSingle();
        var moved = new Vector3(36f, 12f, 50f);
        Assert.True(registry.TryPrepareSetPosition(
            1u,
            moved,
            Quaternion.Identity,
            Cell9,
            0f,
            0f,
            PhysicsShadowCommitAction.Replace,
            [Cell9],
            provenShapeless: false,
            suspendOwner: false,
            out var prepared));

        Assert.Equal(new Vector3(12f, 12f, 50f),
            Assert.Single(registry.GetObjectsInCell(Cell1)).Position);
        Assert.Empty(registry.GetObjectsInCell(Cell9));
        Assert.True(registry.TryApplySetPosition(prepared!, out var receipt));
        Assert.True(receipt.Mutated);
        Assert.Empty(registry.GetObjectsInCell(Cell1));
        Assert.Equal(moved,
            Assert.Single(registry.GetObjectsInCell(Cell9)).Position);
    }

    [Fact]
    public void PreparedSuspendedMoveRetainsNewCanonicalPosition()
    {
        var registry = RegisteredSingle();
        var moved = new Vector3(36f, 12f, 50f);
        Assert.True(registry.TryPrepareSetPosition(
            1u,
            moved,
            Quaternion.Identity,
            Cell9,
            0f,
            0f,
            PhysicsShadowCommitAction.Replace,
            [Cell9],
            provenShapeless: false,
            suspendOwner: true,
            out var prepared));

        Assert.Single(registry.GetObjectsInCell(Cell1));
        Assert.True(registry.TryApplySetPosition(prepared!, out _));
        Assert.Empty(registry.GetObjectsInCell(Cell1));
        Assert.Empty(registry.GetObjectsInCell(Cell9));
        Assert.Equal(1, registry.SuspendedRegistrationCount);
        Assert.Equal(moved, prepared!.OwnerState!.Registration.EntityWorldPos);
        Assert.Equal(Cell9, prepared.OwnerState.Registration.SeedCellId);
    }

    [Fact]
    public void PreparedCrossPrefixDispatchesMembershipThenMutationOnce()
    {
        const uint otherCell = 0xA9B50001u;
        var registry = RegisteredSingle();
        var callbacks = new List<string>();
        registry.OwnerPrefixMembershipChanged += (owner, prefix) =>
            callbacks.Add($"prefix:{owner:X8}:{prefix:X8}");
        registry.OwnerMutated += (owner, version) =>
            callbacks.Add($"owner:{owner:X8}:{version}");
        Assert.True(registry.TryPrepareSetPosition(
            1u,
            new Vector3(204f, 12f, 50f),
            Quaternion.Identity,
            otherCell,
            192f,
            0f,
            PhysicsShadowCommitAction.Replace,
            [otherCell],
            provenShapeless: false,
            suspendOwner: false,
            out var prepared));
        Assert.True(registry.TryApplySetPosition(prepared!, out var receipt));
        Assert.Empty(callbacks);

        registry.DispatchSetPositionCommit(receipt);
        registry.DispatchSetPositionCommit(receipt);

        Assert.Equal(
        [
            "prefix:00000001:A9B40000",
            "prefix:00000001:A9B50000",
            $"owner:00000001:{receipt.OwnerVersion}",
        ], callbacks);
    }

    [Fact]
    public void TwoOwnerReceiptsDispatchExactlyOnceInReverseOrder()
    {
        const uint otherCell = 0xA9B50001u;
        var registry = RegisteredSingle();
        registry.Register(
            2u, 0x01000002u, new Vector3(13f, 12f, 50f),
            Quaternion.Identity, 1f, 0f, 0f, Landblock,
            seedCellId: Cell1, isStatic: false);
        var owners = new List<uint>();
        registry.OwnerMutated += (owner, _) => owners.Add(owner);

        Assert.True(registry.TryPrepareSetPosition(
            1u, new Vector3(204f, 12f, 50f), Quaternion.Identity,
            otherCell, 192f, 0f, PhysicsShadowCommitAction.Replace,
            [otherCell], provenShapeless: false, suspendOwner: false,
            out var first));
        Assert.True(registry.TryApplySetPosition(first!, out var firstReceipt));
        Assert.True(registry.TryPrepareSetPosition(
            2u, new Vector3(205f, 12f, 50f), Quaternion.Identity,
            otherCell, 192f, 0f, PhysicsShadowCommitAction.Replace,
            [otherCell], provenShapeless: false, suspendOwner: false,
            out var second));
        Assert.True(registry.TryApplySetPosition(second!, out var secondReceipt));

        registry.DispatchSetPositionCommit(secondReceipt);
        registry.DispatchSetPositionCommit(firstReceipt);
        registry.DispatchSetPositionCommit(secondReceipt);
        registry.DispatchSetPositionCommit(firstReceipt);

        Assert.Equal([2u, 1u], owners);
        Assert.Equal(0, registry.PendingSetPositionDispatchCount);
    }

    [Fact]
    public void PrefixObserverMutationCannotRegressFinalOwnerVersion()
    {
        const uint otherCell = 0xA9B50001u;
        var registry = RegisteredSingle();
        var versions = new List<ulong>();
        var prefixes = new List<uint>();
        bool mutated = false;
        registry.OwnerPrefixMembershipChanged += (owner, prefix) =>
        {
            prefixes.Add(prefix);
            if (mutated)
                return;
            mutated = true;
            registry.UpdatePhysicsState(
                owner,
                (uint)PhysicsStateFlags.Hidden);
        };
        registry.OwnerMutated += (_, version) => versions.Add(version);
        Assert.True(registry.TryPrepareSetPosition(
            1u, new Vector3(204f, 12f, 50f), Quaternion.Identity,
            otherCell, 192f, 0f, PhysicsShadowCommitAction.Replace,
            [otherCell], provenShapeless: false, suspendOwner: false,
            out var prepared));
        Assert.True(registry.TryApplySetPosition(prepared!, out var receipt));

        registry.DispatchSetPositionCommit(receipt);

        Assert.Single(versions);
        Assert.Equal(registry.GetOwnerVersion(1u), versions[0]);
        Assert.Equal([Landblock], prefixes);
    }

    [Fact]
    public void PreparedShapelessRequiresExplicitDispositionAndCreatesNoRows()
    {
        var registry = new ShadowObjectRegistry();
        Assert.False(registry.TryPrepareSetPosition(
            77u,
            Vector3.One,
            Quaternion.Identity,
            Cell1,
            0f,
            0f,
            PhysicsShadowCommitAction.Replace,
            [Cell1],
            provenShapeless: false,
            suspendOwner: false,
            out _));
        Assert.True(registry.TryPrepareSetPosition(
            77u,
            Vector3.One,
            Quaternion.Identity,
            Cell1,
            0f,
            0f,
            PhysicsShadowCommitAction.Replace,
            [Cell1],
            provenShapeless: true,
            suspendOwner: false,
            out var prepared));
        Assert.True(registry.TryApplySetPosition(prepared!, out var receipt));
        Assert.False(receipt.Mutated);
        Assert.Empty(registry.GetObjectsInCell(Cell1));
        Assert.False(registry.TryApplySetPosition(prepared!, out _));
    }

    [Fact]
    public void ClearInvalidatesPreparedUnappliedShapelessCommit()
    {
        var registry = new ShadowObjectRegistry();
        Assert.True(registry.TryPrepareSetPosition(
            77u,
            Vector3.One,
            Quaternion.Identity,
            Cell1,
            0f,
            0f,
            PhysicsShadowCommitAction.Replace,
            [Cell1],
            provenShapeless: true,
            suspendOwner: false,
            out var prepared));

        registry.Clear();

        Assert.False(registry.TryApplySetPosition(prepared!, out _));
        Assert.Equal(0, registry.PendingSetPositionDispatchCount);
        Assert.Empty(registry.GetObjectsInCell(Cell1));
    }

    [Fact]
    public void PreparedCommitRejectsStaleGlobalRevisionWithoutMutation()
    {
        var registry = RegisteredSingle();
        Assert.True(registry.TryPrepareSetPosition(
            1u, new Vector3(36f, 12f, 50f), Quaternion.Identity,
            Cell9, 0f, 0f, PhysicsShadowCommitAction.Replace, [Cell9],
            provenShapeless: false, suspendOwner: false, out var prepared));
        registry.Register(
            2u, 0x01000002u, new Vector3(13f, 12f, 50f),
            Quaternion.Identity, 1f, 0f, 0f, Landblock,
            seedCellId: Cell1, isStatic: false);
        Vector3 before = registry.GetObjectsInCell(Cell1)
            .Single(entry => entry.EntityId == 1u).Position;

        Assert.False(registry.TryApplySetPosition(prepared!, out _));

        Assert.Equal(before, registry.GetObjectsInCell(Cell1)
            .Single(entry => entry.EntityId == 1u).Position);
        Assert.Empty(registry.GetObjectsInCell(Cell9));
    }

    [Fact]
    public void PreparedCommitRejectsStaleOwnerVersionWithoutMutation()
    {
        var registry = RegisteredSingle();
        Assert.True(registry.TryPrepareSetPosition(
            1u, new Vector3(36f, 12f, 50f), Quaternion.Identity,
            Cell9, 0f, 0f, PhysicsShadowCommitAction.Replace, [Cell9],
            provenShapeless: false, suspendOwner: false, out var prepared));
        registry.UpdatePhysicsState(1u, (uint)PhysicsStateFlags.Hidden);
        ShadowEntry before = Assert.Single(registry.GetObjectsInCell(Cell1));

        Assert.False(registry.TryApplySetPosition(prepared!, out _));

        Assert.Equal(before, Assert.Single(registry.GetObjectsInCell(Cell1)));
        Assert.Empty(registry.GetObjectsInCell(Cell9));
    }

    [Fact]
    public void PreparedApplyTailAllocatesZeroManagedBytes()
    {
        // Warm the generic owner-value and dictionary replacement paths.
        var warm = RegisteredSingle();
        Assert.True(warm.TryPrepareSetPosition(
            1u, new Vector3(36f, 12f, 50f), Quaternion.Identity,
            Cell9, 0f, 0f, PhysicsShadowCommitAction.Replace, [Cell9],
            provenShapeless: false, suspendOwner: false, out var warmPrepared));
        Assert.True(warm.TryApplySetPosition(warmPrepared!, out _));

        var registry = RegisteredSingle();
        Assert.True(registry.TryPrepareSetPosition(
            1u, new Vector3(36f, 12f, 50f), Quaternion.Identity,
            Cell9, 0f, 0f, PhysicsShadowCommitAction.Replace, [Cell9],
            provenShapeless: false, suspendOwner: false, out var prepared));
        long before = GC.GetAllocatedBytesForCurrentThread();

        bool applied = registry.TryApplySetPosition(prepared!, out _);
        long allocated = GC.GetAllocatedBytesForCurrentThread() - before;

        Assert.True(applied);
        Assert.Equal(0, allocated);
    }

    [Fact]
    public void DeferredDispatchRejectsReceiptAfterInterveningOwnerMutation()
    {
        const uint otherCell = 0xA9B50001u;
        var registry = RegisteredSingle();
        var versions = new List<ulong>();
        var prefixes = new List<uint>();
        registry.OwnerMutated += (_, version) => versions.Add(version);
        registry.OwnerPrefixMembershipChanged += (_, prefix) =>
            prefixes.Add(prefix);
        Assert.True(registry.TryPrepareSetPosition(
            1u, new Vector3(204f, 12f, 50f), Quaternion.Identity,
            otherCell, 192f, 0f, PhysicsShadowCommitAction.Replace,
            [otherCell], provenShapeless: false, suspendOwner: false,
            out var prepared));
        Assert.True(registry.TryApplySetPosition(prepared!, out var receipt));

        registry.UpdatePhysicsState(1u, (uint)PhysicsStateFlags.Hidden);
        ulong current = registry.GetOwnerVersion(1u);
        registry.DispatchSetPositionCommit(receipt);

        Assert.Equal([current], versions);
        Assert.Empty(prefixes);
    }

    [Fact]
    public void NoneRefreshesFrameWithoutChangingExactMembership()
    {
        var registry = RegisteredSingle();
        ulong version = registry.GetOwnerVersion(1u);
        var moved = new Vector3(14f, 12f, 50f);

        registry.CommitSetPosition(
            1u, moved, Quaternion.Identity, Cell1, 0f, 0f,
            PhysicsShadowCommitAction.None, []);

        ShadowEntry entry = Assert.Single(registry.GetObjectsInCell(Cell1));
        Assert.Equal(moved, entry.Position);
        Assert.Empty(registry.GetObjectsInCell(Cell9));
        Assert.Equal(version + 1UL, registry.GetOwnerVersion(1u));
    }

    [Fact]
    public void ReplaceDeduplicatesExactCellsAndPreserveRetainsThem()
    {
        var registry = RegisteredSingle();
        var replaced = new Vector3(36f, 12f, 50f);
        registry.CommitSetPosition(
            1u, replaced, Quaternion.Identity, Cell9, 0f, 0f,
            PhysicsShadowCommitAction.Replace,
            ImmutableArray.Create(Cell9, Cell9, 0u));

        Assert.Empty(registry.GetObjectsInCell(Cell1));
        Assert.Equal(replaced,
            Assert.Single(registry.GetObjectsInCell(Cell9)).Position);

        var preserved = new Vector3(38f, 12f, 50f);
        registry.CommitSetPosition(
            1u, preserved, Quaternion.Identity, Cell9, 0f, 0f,
            PhysicsShadowCommitAction.Preserve, []);
        Assert.Equal(preserved,
            Assert.Single(registry.GetObjectsInCell(Cell9)).Position);

        var emptyReplace = new Vector3(40f, 12f, 50f);
        registry.CommitSetPosition(
            1u, emptyReplace, Quaternion.Identity, Cell9, 0f, 0f,
            PhysicsShadowCommitAction.Replace, []);
        Assert.Equal(emptyReplace,
            Assert.Single(registry.GetObjectsInCell(Cell9)).Position);
    }

    [Fact]
    public void RecalculateUsesCanonicalFloodInsteadOfRetainedCells()
    {
        var registry = RegisteredSingle();
        var moved = new Vector3(36f, 12f, 50f);

        registry.CommitSetPosition(
            1u, moved, Quaternion.Identity, Cell9, 0f, 0f,
            PhysicsShadowCommitAction.Recalculate, []);

        Assert.Empty(registry.GetObjectsInCell(Cell1));
        Assert.Equal(moved,
            Assert.Single(registry.GetObjectsInCell(Cell9)).Position);
    }

    [Fact]
    public void SuspendedPreserveRestoresExactRowsAndConsumesReceipt()
    {
        var registry = RegisteredSingle();
        Assert.True(registry.Suspend(1u));
        Assert.Equal(0, registry.TotalRegistered);
        Assert.Equal(1, registry.SuspendedRegistrationCount);
        var moved = new Vector3(15f, 12f, 50f);

        registry.CommitSetPosition(
            1u, moved, Quaternion.Identity, Cell1, 0f, 0f,
            PhysicsShadowCommitAction.Preserve, []);

        Assert.Equal(1, registry.TotalRegistered);
        Assert.Equal(0, registry.SuspendedRegistrationCount);
        Assert.Equal(moved,
            Assert.Single(registry.GetObjectsInCell(Cell1)).Position);
    }

    [Fact]
    public void SuspendedReceiptCopiesAcrossCollisionRootAndClearsOnTeardown()
    {
        var source = RegisteredSingle();
        Assert.True(source.Suspend(1u));
        var destination = new ShadowObjectRegistry();

        Assert.False(destination.RefreshRetainedOwnerFrom(
            source, 1u, Landblock, out ulong version));
        Assert.Equal(source.GetOwnerVersion(1u), version);
        Assert.Equal(1, destination.RetainedRegistrationCount);
        Assert.Equal(1, destination.SuspendedRegistrationCount);

        destination.CommitSetPosition(
            1u, new Vector3(16f, 12f, 50f), Quaternion.Identity,
            Cell1, 0f, 0f, PhysicsShadowCommitAction.None, []);
        Assert.Single(destination.GetObjectsInCell(Cell1));
        destination.Deregister(1u);
        Assert.Equal(0, destination.RetainedRegistrationCount);
        Assert.Equal(0, destination.SuspendedRegistrationCount);
        destination.Clear();
        Assert.Equal(0, destination.TotalRegistered);
    }

    [Fact]
    public void MultiPartNoneRefreshesEachPartFromRootTransform()
    {
        var registry = new ShadowObjectRegistry();
        IReadOnlyList<ShadowShape> shapes =
        [
            Shape(0x01000001u, new Vector3(1f, 0f, 0f)),
            Shape(0x01000002u, new Vector3(0f, 1f, 0f)),
        ];
        registry.RegisterMultiPart(
            2u, new Vector3(12f, 12f, 50f), Quaternion.Identity,
            shapes, 0u, EntityCollisionFlags.None, 0f, 0f, Landblock,
            Cell1);
        Quaternion rotation = Quaternion.CreateFromAxisAngle(
            Vector3.UnitZ, MathF.PI / 2f);
        var root = new Vector3(14f, 12f, 50f);

        registry.CommitSetPosition(
            2u, root, rotation, Cell1, 0f, 0f,
            PhysicsShadowCommitAction.None, []);

        ShadowEntry[] entries = registry.GetObjectsInCell(Cell1)
            .Where(entry => entry.EntityId == 2u)
            .OrderBy(entry => entry.GfxObjId)
            .ToArray();
        Assert.Equal(2, entries.Length);
        Assert.Equal(root + Vector3.Transform(shapes[0].LocalPosition, rotation),
            entries[0].Position);
        Assert.Equal(root + Vector3.Transform(shapes[1].LocalPosition, rotation),
            entries[1].Position);
    }

    private static ShadowObjectRegistry RegisteredSingle()
    {
        var registry = new ShadowObjectRegistry();
        registry.Register(
            1u, 0x01000001u, new Vector3(12f, 12f, 50f),
            Quaternion.Identity, 1f, 0f, 0f, Landblock,
            seedCellId: Cell1, isStatic: false);
        return registry;
    }

    private static ShadowShape Shape(uint gfxObjId, Vector3 local)
        => ShadowShape.Bsp(
            gfxObjId,
            local,
            Quaternion.Identity,
            scale: 1f,
            localGeometry: ShadowPartGeometry.Create(new FlatCollisionSphere(Vector3.Zero, 0.25f), null));
}
