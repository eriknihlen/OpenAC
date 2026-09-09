using AcDream.App.Rendering.Residency;

namespace AcDream.App.Tests.Rendering.Residency;

public sealed class ResidencyManagerTests
{
    private sealed class MeshAsset;
    private sealed class TextureAsset;

    [Fact]
    public void ScratchBytesAreCommittedCpuResidence()
    {
        var charges = new ResidencyCharges(
            LogicalBytes: 1,
            CpuPreparedBytes: 2,
            DecodedBytes: 3,
            ScratchBytes: 4,
            StagingBytes: 5);

        Assert.Equal(15, charges.CommittedCpuBytes);
        charges.Validate();
    }

    [Fact]
    public void HandlesAndOwnersReserveZeroAsInvalid()
    {
        var manager = new ResidencyManager();

        OwnerToken owner = manager.CreateOwner(
            ResidencyOwnerKind.Entity,
            logicalId: 42,
            worldGeneration: 7);
        AssetHandle<MeshAsset> asset = manager.Request<MeshAsset>(
            new ResidencyAssetKey(ResidencyDomain.ObjectMeshes, 0x01020304),
            ResidencyPriority.Visible,
            worldGeneration: 7,
            frame: 10);

        Assert.True(owner.IsValid);
        Assert.True(asset.IsValid);
        Assert.NotEqual(0u, owner.Index);
        Assert.NotEqual(0u, asset.Index);
        Assert.NotEqual((ushort)0, owner.Generation);
        Assert.NotEqual((ushort)0, asset.Generation);
    }

    [Fact]
    public void DuplicateAcquireIsIdempotentAndSingleReleaseOwnsLease()
    {
        var manager = new ResidencyManager();
        OwnerToken owner = manager.CreateOwner(
            ResidencyOwnerKind.Entity,
            logicalId: 42,
            worldGeneration: 7);
        AssetHandle<MeshAsset> asset = manager.Request<MeshAsset>(
            new ResidencyAssetKey(ResidencyDomain.ObjectMeshes, 1),
            ResidencyPriority.Near,
            worldGeneration: 7,
            frame: 1);

        AssetLease<MeshAsset> first = manager.Acquire(
            asset,
            owner,
            ResidencyPriority.Visible,
            frame: 2);
        AssetLease<MeshAsset> duplicate = manager.Acquire(
            asset,
            owner,
            ResidencyPriority.Visible,
            frame: 3);

        Assert.Equal(first, duplicate);
        Assert.True(manager.TryGetSnapshot(asset, out var snapshot));
        Assert.Equal(1, snapshot.OwnerCount);
        Assert.Equal(3, snapshot.LastUsedFrame);
        Assert.True(manager.Release(first));
        Assert.False(manager.Release(duplicate));
    }

    [Fact]
    public void RetiringAssetCannotBeAcquiredAndRecyclesWithNewGeneration()
    {
        var manager = new ResidencyManager();
        ResidencyAssetKey key =
            new(ResidencyDomain.ObjectMeshes, 0x02000001);
        AssetHandle<MeshAsset> first = manager.Request<MeshAsset>(
            key,
            ResidencyPriority.Far,
            worldGeneration: 1,
            frame: 0);
        manager.Transition(
            first,
            AssetResidencyState.Prepared,
            new ResidencyCharges(CpuPreparedBytes: 64),
            frame: 1);
        manager.Transition(
            first,
            AssetResidencyState.Retiring,
            new ResidencyCharges(),
            frame: 2);

        Assert.True(manager.CompleteRetirement(first));
        AssetHandle<MeshAsset> replacement = manager.Request<MeshAsset>(
            key,
            ResidencyPriority.Visible,
            worldGeneration: 2,
            frame: 3);

        Assert.Equal(first.Index, replacement.Index);
        Assert.NotEqual(first.Generation, replacement.Generation);
        Assert.False(manager.Touch(first, ResidencyPriority.Visible, frame: 4));
        Assert.False(manager.CompleteRetirement(first));
    }

    [Theory]
    [InlineData((int)AssetResidencyState.Cancelled)]
    [InlineData((int)AssetResidencyState.Missing)]
    [InlineData((int)AssetResidencyState.Corrupt)]
    [InlineData((int)AssetResidencyState.Failed)]
    public void TerminalCompletionDetachesOwnersAndRestartsAsNewGeneration(
        int terminalValue)
    {
        var terminal = (AssetResidencyState)terminalValue;
        var manager = new ResidencyManager();
        OwnerToken owner = manager.CreateOwner(
            ResidencyOwnerKind.Landblock,
            logicalId: 0x1234,
            worldGeneration: 5);
        ResidencyAssetKey key =
            new(ResidencyDomain.PreparedMeshCpu, 7);
        AssetHandle<MeshAsset> first = manager.Request<MeshAsset>(
            key,
            ResidencyPriority.DestinationCritical,
            worldGeneration: 5,
            frame: 0);
        manager.Acquire(
            first,
            owner,
            ResidencyPriority.DestinationCritical,
            frame: 0);

        manager.Transition(
            first,
            terminal,
            ResidencyCharges.Zero,
            frame: 1,
            failure: terminal is AssetResidencyState.Corrupt
                or AssetResidencyState.Failed
                ? "fixture"
                : null);
        Assert.True(manager.TryGetSnapshot(first, out var terminalSnapshot));
        Assert.Equal(0, terminalSnapshot.OwnerCount);

        AssetHandle<MeshAsset> replacement = manager.Request<MeshAsset>(
            key,
            ResidencyPriority.Near,
            worldGeneration: 5,
            frame: 2);
        Assert.Equal(first.Index, replacement.Index);
        Assert.NotEqual(first.Generation, replacement.Generation);
        Assert.False(manager.Release(new AssetLease<MeshAsset>(first, owner)));
    }

    [Fact]
    public void RetiringRejectsLiveOwners()
    {
        var manager = new ResidencyManager();
        OwnerToken owner = manager.CreateOwner(
            ResidencyOwnerKind.Entity,
            logicalId: 1,
            worldGeneration: 1);
        AssetHandle<MeshAsset> asset = manager.Request<MeshAsset>(
            new ResidencyAssetKey(ResidencyDomain.ObjectMeshes, 1),
            ResidencyPriority.Visible,
            worldGeneration: 1,
            frame: 0);
        manager.Acquire(asset, owner, ResidencyPriority.Visible, frame: 0);
        manager.Transition(
            asset,
            AssetResidencyState.Prepared,
            new ResidencyCharges(CpuPreparedBytes: 10),
            frame: 1);

        InvalidOperationException error = Assert.Throws<InvalidOperationException>(
            () => manager.Transition(
                asset,
                AssetResidencyState.Retiring,
                ResidencyCharges.Zero,
                frame: 2));

        Assert.Contains("owned asset", error.Message);
    }

    [Fact]
    public void IllegalTransitionAndTerminalChargesFailLoudly()
    {
        var manager = new ResidencyManager();
        AssetHandle<MeshAsset> asset = manager.Request<MeshAsset>(
            new ResidencyAssetKey(ResidencyDomain.ObjectMeshes, 1),
            ResidencyPriority.Far,
            worldGeneration: 1,
            frame: 0);

        Assert.Throws<InvalidOperationException>(() => manager.Transition(
            asset,
            AssetResidencyState.Resident,
            new ResidencyCharges(GpuResidentBytes: 1),
            frame: 1));
        Assert.Throws<InvalidOperationException>(() => manager.Transition(
            asset,
            AssetResidencyState.Failed,
            new ResidencyCharges(CpuPreparedBytes: 1),
            frame: 1));
    }

    [Fact]
    public void OwnerGenerationPreventsStaleReleaseOfReplacementOwner()
    {
        var manager = new ResidencyManager();
        OwnerToken first = manager.CreateOwner(
            ResidencyOwnerKind.Entity,
            logicalId: 1,
            worldGeneration: 1);
        Assert.True(manager.RetireOwner(first));
        OwnerToken replacement = manager.CreateOwner(
            ResidencyOwnerKind.Entity,
            logicalId: 2,
            worldGeneration: 1);

        Assert.Equal(first.Index, replacement.Index);
        Assert.NotEqual(first.Generation, replacement.Generation);
        Assert.False(manager.RetireOwner(first));
        Assert.Equal(1, manager.ActiveOwnerCount);
    }

    [Fact]
    public void WorldScopedOwnerCannotCrossGeneration()
    {
        var manager = new ResidencyManager();
        OwnerToken owner = manager.CreateOwner(
            ResidencyOwnerKind.Entity,
            logicalId: 1,
            worldGeneration: 2);
        AssetHandle<MeshAsset> asset = manager.Request<MeshAsset>(
            new ResidencyAssetKey(ResidencyDomain.ObjectMeshes, 1),
            ResidencyPriority.Near,
            worldGeneration: 3,
            frame: 0);

        Assert.Throws<InvalidOperationException>(() => manager.Acquire(
            asset,
            owner,
            ResidencyPriority.Near,
            frame: 0));
    }

    [Fact]
    public void EvictionIsOwnerSafePriorityThenAgeThenCostThenIndex()
    {
        var manager = new ResidencyManager();
        AssetHandle<MeshAsset> farOldExpensive = Resident(
            manager, 1, ResidencyPriority.Far, frame: 1, bytes: 10, rebuild: 9);
        AssetHandle<MeshAsset> speculativeNew = Resident(
            manager, 2, ResidencyPriority.Speculative, frame: 9, bytes: 20, rebuild: 9);
        AssetHandle<MeshAsset> farOldCheap = Resident(
            manager, 3, ResidencyPriority.Far, frame: 1, bytes: 30, rebuild: 1);
        OwnerToken owner = manager.CreateOwner(
            ResidencyOwnerKind.Entity,
            logicalId: 9,
            worldGeneration: 1);
        manager.Acquire(
            speculativeNew,
            owner,
            ResidencyPriority.Visible,
            frame: 10);

        IReadOnlyList<ResidencyTrimRequest> requests =
            manager.SelectEvictions(
                ResidencyDomain.ObjectMeshes,
                bytesToRelease: 35,
                maximumCount: 3);

        Assert.Collection(
            requests,
            first => Assert.Equal(farOldCheap.Untyped, first.Asset),
            second => Assert.Equal(farOldExpensive.Untyped, second.Asset));
        Assert.DoesNotContain(
            requests,
            request => request.Asset == speculativeNew.Untyped);
    }

    [Fact]
    public void JournalCoalescesAndStaleObservationCannotReviveReplacement()
    {
        var manager = new ResidencyManager();
        var journal = new ResidencyObservationJournal(maximumEntries: 4);
        ResidencyAssetKey key =
            new(ResidencyDomain.PreparedMeshCpu, 4);
        AssetHandle<MeshAsset> first = manager.Request<MeshAsset>(
            key,
            ResidencyPriority.Far,
            worldGeneration: 1,
            frame: 0);
        Assert.True(journal.TryPublish(new ResidencyObservation(
            first.Untyped,
            ResidencyObservationKind.Transition,
            AssetResidencyState.Prepared,
            new ResidencyCharges(CpuPreparedBytes: 10),
            ResidencyPriority.Far,
            Frame: 1)));
        Assert.True(journal.TryPublish(new ResidencyObservation(
            first.Untyped,
            ResidencyObservationKind.Transition,
            AssetResidencyState.Cancelled,
            ResidencyCharges.Zero,
            ResidencyPriority.Far,
            Frame: 2)));
        Assert.Equal(1, journal.Count);
        Assert.Equal(1, manager.Drain(journal));

        AssetHandle<MeshAsset> replacement = manager.Request<MeshAsset>(
            key,
            ResidencyPriority.Visible,
            worldGeneration: 2,
            frame: 3);
        Assert.True(journal.TryPublish(new ResidencyObservation(
            first.Untyped,
            ResidencyObservationKind.Transition,
            AssetResidencyState.Prepared,
            new ResidencyCharges(CpuPreparedBytes: 10),
            ResidencyPriority.Far,
            Frame: 4)));

        Assert.Equal(0, manager.Drain(journal));
        Assert.True(manager.TryGetSnapshot(replacement, out var snapshot));
        Assert.Equal(AssetResidencyState.Requested, snapshot.State);
    }

    [Fact]
    public void JournalHasExplicitBoundForDistinctObservations()
    {
        var manager = new ResidencyManager();
        var journal = new ResidencyObservationJournal(maximumEntries: 1);
        AssetHandle<MeshAsset> first = manager.Request<MeshAsset>(
            new ResidencyAssetKey(ResidencyDomain.PreparedMeshCpu, 1),
            ResidencyPriority.Far,
            worldGeneration: 1,
            frame: 0);
        AssetHandle<TextureAsset> second = manager.Request<TextureAsset>(
            new ResidencyAssetKey(ResidencyDomain.StandaloneTextures, 2),
            ResidencyPriority.Far,
            worldGeneration: 1,
            frame: 0);

        Assert.True(journal.TryPublish(Touch(first, frame: 1)));
        Assert.False(journal.TryPublish(Touch(second, frame: 1)));
        Assert.Equal(1, journal.Count);
    }

    [Fact]
    public void DomainSourcesAggregateWithoutCountingMappedAddressSpaceAsRam()
    {
        var manager = new ResidencyManager();
        manager.RegisterDomainSource(new DelegateResidencyDomainSource(
            ResidencyDomain.PreparedPackage,
            () => new ResidencyDomainSnapshot(
                ResidencyDomain.PreparedPackage,
                EntryCount: 1,
                OwnerCount: 1,
                Charges: new ResidencyCharges(
                    LogicalBytes: 64,
                    PinnedBytes: 32,
                    MappedVirtualBytes: 30_000))));
        manager.RegisterDomainSource(new DelegateResidencyDomainSource(
            ResidencyDomain.StandaloneTextures,
            () => new ResidencyDomainSnapshot(
                ResidencyDomain.StandaloneTextures,
                EntryCount: 2,
                OwnerCount: 1,
                Charges: new ResidencyCharges(
                    GpuResidentBytes: 128),
                BudgetBytes: 256,
                CapacityBytes: 128,
                UsedBytes: 96,
                LargestFreeBytes: 16)));

        ResidencySnapshot snapshot = manager.CaptureSnapshot();

        Assert.Equal(64, snapshot.TotalCharges.CommittedCpuBytes);
        Assert.Equal(30_000, snapshot.TotalCharges.MappedVirtualBytes);
        Assert.Equal(128, snapshot.TotalCharges.PhysicalGpuBytes);
        Assert.Equal(
            16,
            snapshot.Get(ResidencyDomain.StandaloneTextures)
                .FragmentedFreeBytes);
        Assert.Equal(256, snapshot.TotalBudgetBytes);
        Assert.Equal(128, snapshot.TotalCapacityBytes);
        Assert.Equal(96, snapshot.TotalUsedBytes);
        Assert.Equal(16, snapshot.TotalFragmentedFreeBytes);
        Assert.Equal(3, snapshot.TotalEntries);
        Assert.Equal(2, snapshot.TotalOwners);
    }

    [Fact]
    public void ForcedPressureAcrossEveryDomainConvergesToZero()
    {
        const long budget = 64;
        var manager = new ResidencyManager();
        var current = new Dictionary<
            ResidencyDomain,
            ResidencyDomainSnapshot>();
        foreach (ResidencyDomain domain in Enum.GetValues<ResidencyDomain>())
        {
            current[domain] = new ResidencyDomainSnapshot(
                domain,
                EntryCount: 2,
                OwnerCount: 1,
                Charges: new ResidencyCharges(
                    CpuPreparedBytes: budget * 2),
                BudgetBytes: budget,
                Hits: 3,
                Misses: 2,
                Evictions: 1);
            ResidencyDomain capturedDomain = domain;
            manager.RegisterDomainSource(new DelegateResidencyDomainSource(
                capturedDomain,
                () => current[capturedDomain]));
        }

        ResidencySnapshot pressured = manager.CaptureSnapshot();

        Assert.Equal(
            Enum.GetValues<ResidencyDomain>().Length,
            pressured.Domains.Count);
        Assert.All(
            pressured.Domains,
            domain => Assert.True(
                domain.Charges.CommittedCpuBytes > domain.BudgetBytes));
        Assert.Equal(
            budget * 2 * pressured.Domains.Count,
            pressured.TotalCharges.CommittedCpuBytes);

        foreach (ResidencyDomain domain in Enum.GetValues<ResidencyDomain>())
        {
            current[domain] = new ResidencyDomainSnapshot(
                domain,
                EntryCount: 0,
                OwnerCount: 0,
                Charges: ResidencyCharges.Zero,
                BudgetBytes: budget,
                Hits: 3,
                Misses: 2,
                Evictions: 3);
        }

        ResidencySnapshot drained = manager.CaptureSnapshot();

        Assert.True(drained.TotalCharges.IsZero);
        Assert.Equal(0, drained.TotalEntries);
        Assert.Equal(0, drained.TotalOwners);
        Assert.Equal(budget * drained.Domains.Count, drained.TotalBudgetBytes);
        Assert.Equal(30, drained.TotalEvictions);
    }

    [Fact]
    public void DomainSourceRejectsImpossibleAllocatorFacts()
    {
        var source = new DelegateResidencyDomainSource(
            ResidencyDomain.GlobalMeshArena,
            () => new ResidencyDomainSnapshot(
                ResidencyDomain.GlobalMeshArena,
                EntryCount: 1,
                OwnerCount: 0,
                Charges: ResidencyCharges.Zero,
                CapacityBytes: 64,
                UsedBytes: 65));

        Assert.Throws<InvalidOperationException>(
            () => source.CaptureResidency());
    }

    [Fact]
    public void OnlyOneCanonicalSourceMayOwnADomain()
    {
        var manager = new ResidencyManager();
        var first = new DelegateResidencyDomainSource(
            ResidencyDomain.ObjectMeshes,
            () => default);
        manager.RegisterDomainSource(first);

        Assert.Throws<InvalidOperationException>(() =>
            manager.RegisterDomainSource(
                new DelegateResidencyDomainSource(
                    ResidencyDomain.ObjectMeshes,
                    () => default)));
    }

    private static AssetHandle<MeshAsset> Resident(
        ResidencyManager manager,
        ulong id,
        ResidencyPriority priority,
        long frame,
        long bytes,
        long rebuild)
    {
        AssetHandle<MeshAsset> asset = manager.Request<MeshAsset>(
            new ResidencyAssetKey(ResidencyDomain.ObjectMeshes, id),
            priority,
            worldGeneration: 1,
            frame,
            rebuild);
        manager.Transition(
            asset,
            AssetResidencyState.Prepared,
            new ResidencyCharges(CpuPreparedBytes: bytes),
            frame);
        manager.Transition(
            asset,
            AssetResidencyState.UploadPending,
            new ResidencyCharges(
                StagingBytes: bytes,
                GpuRequestedBytes: bytes),
            frame);
        manager.Transition(
            asset,
            AssetResidencyState.Resident,
            new ResidencyCharges(GpuResidentBytes: bytes),
            frame);
        return asset;
    }

    private static ResidencyObservation Touch<T>(
        AssetHandle<T> asset,
        long frame) =>
        new(
            asset.Untyped,
            ResidencyObservationKind.Touch,
            AssetResidencyState.Absent,
            ResidencyCharges.Zero,
            ResidencyPriority.Visible,
            frame);
}
