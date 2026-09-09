using AcDream.App.Rendering;
using AcDream.Core.Textures;
using AcDream.Core.World;

namespace AcDream.App.Tests.Rendering;

public sealed class CompositeTextureArrayCachePolicyTests
{
    [Fact]
    public void DefaultLocationIsUnresolvedAndDistinctFromSlotZero()
    {
        Assert.False(default(BindlessTextureLocation).IsResolved);
        Assert.Equal(BindlessTextureLocation.Unresolved, default(BindlessTextureLocation));
        Assert.False(BindlessTextureLocation.Unresolved.Slot.IsAssigned);

        var slotZero = new BindlessTextureLocation(
            new AcDream.App.Rendering.Gpu.GpuTextureSlot(0), layer: 3);
        Assert.True(slotZero.IsResolved);
        Assert.Equal(0u, slotZero.Slot.Index);
        Assert.Equal(3u, slotZero.Layer);
        Assert.NotEqual(BindlessTextureLocation.Unresolved, slotZero);
    }

    [Fact]
    public void BudgetRejectionReturnsAnUnresolvedLocation()
    {
        var backend = new FakeBackend(maximumLayers: 1);
        var retirements = new DeferredRetirementQueue();
        using var cache = CreateCache(
            backend,
            retirements,
            physicalBudgetBytes: 64);
        cache.BeginFrame();
        Assert.True(cache.TryAddAndAcquire(1, Key(1), Texture(4, 4), out _));

        Assert.False(cache.TryAddAndAcquire(
            2,
            Key(2),
            Texture(4, 4),
            out BindlessTextureLocation rejected));
        Assert.False(rejected.IsResolved);
        Assert.False(rejected.Slot.IsAssigned);
    }

    [Fact]
    public void ResidencySnapshotSeparatesResidentRequestedAndRetiringStorage()
    {
        var backend = new FakeBackend(maximumLayers: 1);
        var retirements = new DeferredRetirementQueue();
        using var cache = CreateCache(
            backend,
            retirements,
            unownedBudgetBytes: 0,
            physicalBudgetBytes: 0);
        cache.BeginFrame();
        Assert.True(cache.TryAddAndAcquire(
            1,
            Key(1),
            Texture(4, 4),
            out _));

        var resident = cache.CaptureResidency();
        Assert.Equal(64, resident.Charges.GpuResidentBytes);
        Assert.Equal(0, resident.Charges.RetiringBytes);
        Assert.Equal(64, resident.CapacityBytes);
        Assert.Equal(64, resident.UsedBytes);

        cache.ReleaseOwner(1);
        cache.Tick();
        retirements.DrainAll();
        cache.Tick();

        var retired = cache.CaptureResidency();
        Assert.Equal(0, retired.Charges.GpuResidentBytes);
        Assert.Equal(0, retired.Charges.RetiringBytes);
        Assert.Equal(0, retired.CapacityBytes);
    }

    [Theory]
    [InlineData(16, 16, 64)]
    [InlineData(128, 128, 64)]
    [InlineData(256, 256, 16)]
    [InlineData(512, 512, 4)]
    [InlineData(1024, 1024, 1)]
    public void CapacityTargetsFourMiBWithoutOversizingLargeLayers(
        int width,
        int height,
        int expected)
    {
        Assert.Equal(
            expected,
            CompositeTextureArrayCache.CalculateLayerCapacity(width, height, driverMaximumLayers: 2048));
    }

    [Fact]
    public void CapacityHonorsDriverLayerLimit()
    {
        Assert.Equal(
            8,
            CompositeTextureArrayCache.CalculateLayerCapacity(16, 16, driverMaximumLayers: 8));
    }

    [Fact]
    public void CompatibleTexturesShareArrayAndOwnersShareExactSlice()
    {
        var backend = new FakeBackend(maximumLayers: 2);
        var retirements = new DeferredRetirementQueue();
        using var cache = CreateCache(backend, retirements);
        cache.BeginFrame();

        Assert.True(cache.TryAddAndAcquire(1, Key(1), Texture(4, 4), out BindlessTextureLocation first));
        Assert.True(cache.TryAcquire(2, Key(1), out BindlessTextureLocation shared));
        Assert.True(cache.TryAddAndAcquire(1, Key(2), Texture(4, 4), out BindlessTextureLocation second));

        Assert.Equal(first, shared);
        Assert.Equal(first.Slot, second.Slot);
        Assert.NotEqual(first.Layer, second.Layer);
        Assert.Single(backend.Created);
        Assert.Equal(2, backend.Uploads.Count);
        Assert.Equal(2, cache.ActiveResourceCount);
    }

    [Fact]
    public void FinalReleaseCachesAndReacquireAvoidsUpload()
    {
        var backend = new FakeBackend(maximumLayers: 2);
        using var cache = CreateCache(backend, new DeferredRetirementQueue());
        cache.BeginFrame();
        Assert.True(cache.TryAddAndAcquire(1, Key(1), Texture(4, 4), out BindlessTextureLocation original));
        Assert.True(cache.TryAcquire(2, Key(1), out _));

        cache.ReleaseOwner(1);
        Assert.Equal(0, cache.UnownedEntryCount);
        cache.ReleaseOwner(2);
        Assert.Equal(1, cache.UnownedEntryCount);
        Assert.Empty(backend.NonResident);
        Assert.Empty(backend.Deleted);

        Assert.True(cache.TryAcquire(3, Key(1), out BindlessTextureLocation reacquired));
        Assert.Equal(original, reacquired);
        Assert.Single(backend.Uploads);
        Assert.Equal(0, cache.UnownedEntryCount);
    }

    [Fact]
    public void EvictedLayerCannotBeReusedBeforeFenceAndOldCallbackCannotTouchReplacement()
    {
        var backend = new FakeBackend(maximumLayers: 1);
        var retirements = new DeferredRetirementQueue();
        using var cache = CreateCache(
            backend,
            retirements,
            unownedBudgetBytes: 0,
            physicalBudgetBytes: long.MaxValue);
        cache.BeginFrame();
        Assert.True(cache.TryAddAndAcquire(1, Key(1), Texture(4, 4), out BindlessTextureLocation old));
        cache.ReleaseOwner(1);
        cache.Tick();

        Assert.Equal(1, retirements.Count);
        cache.BeginFrame();
        Assert.True(cache.TryAddAndAcquire(2, Key(1), Texture(4, 4), out BindlessTextureLocation replacement));
        Assert.NotEqual(old.Slot, replacement.Slot);
        Assert.Equal(2, backend.Created.Count);

        retirements.DrainAll();
        Assert.True(cache.TryAcquire(3, Key(1), out BindlessTextureLocation stillReplacement));
        Assert.Equal(replacement, stillReplacement);
    }

    [Fact]
    public void MaintenanceBatchesLogicalEvictionButDeletesOnlyOneArrayPerTick()
    {
        var backend = new FakeBackend(maximumLayers: 1);
        var retirements = new DeferredRetirementQueue();
        using var cache = CreateCache(
            backend,
            retirements,
            unownedBudgetBytes: 0,
            physicalBudgetBytes: 0);
        cache.BeginFrame();
        Assert.True(cache.TryAddAndAcquire(1, Key(1), Texture(4, 4), out _));
        cache.BeginFrame();
        Assert.True(cache.TryAddAndAcquire(1, Key(2), Texture(4, 4), out _));
        cache.BeginFrame();
        Assert.True(cache.TryAddAndAcquire(1, Key(3), Texture(4, 4), out _));
        cache.ReleaseOwner(1);

        cache.Tick();
        Assert.Equal(0, cache.CachedEntryCount);
        Assert.Equal(3, retirements.Count);
        Assert.Empty(backend.Deleted);

        retirements.DrainAll();
        cache.Tick();
        Assert.Equal(0, cache.CachedEntryCount);
        Assert.Single(backend.Deleted);
        Assert.Equal(
            ["nonresident:1", "delete:1"],
            backend.Events.TakeLast(2));

        cache.Tick();
        Assert.Equal(2, backend.Deleted.Count);
    }

    [Fact]
    public void UploadFailureRollsBackAndDeletesNewEmptyArray()
    {
        var backend = new FakeBackend(maximumLayers: 2) { FailNextUpload = true };
        using var cache = CreateCache(backend, new DeferredRetirementQueue());
        cache.BeginFrame();

        Assert.Throws<InvalidOperationException>(
            () => cache.TryAddAndAcquire(1, Key(1), Texture(4, 4), out _));
        Assert.Equal(0, cache.CachedEntryCount);
        Assert.Equal(0, cache.AtlasCount);
        Assert.Equal(0, cache.AllocatedBytes);
        Assert.Equal(["nonresident:1", "delete:1"], backend.Events);
    }

    [Fact]
    public void UploadAndRollbackFailureRetainsEmptyAtlasForMaintenanceRetry()
    {
        var backend = new FakeBackend(maximumLayers: 2)
        {
            FailNextUpload = true,
            FailNextNonResident = true,
        };
        using var cache = CreateCache(backend, new DeferredRetirementQueue());
        cache.BeginFrame();

        Assert.Throws<AggregateException>(
            () => cache.TryAddAndAcquire(1, Key(1), Texture(4, 4), out _));
        Assert.Equal(128, cache.AllocatedBytes);
        Assert.Equal(1, cache.AtlasCount);

        cache.Tick();
        Assert.Equal(0, cache.AllocatedBytes);
        Assert.Equal(0, cache.AtlasCount);
        Assert.Equal(["nonresident:1", "delete:1"], backend.Events);
    }

    [Fact]
    public void DisposeMakesEveryHandleNonResidentBeforeDeletingAnyArray()
    {
        var backend = new FakeBackend(maximumLayers: 1);
        var cache = CreateCache(backend, new DeferredRetirementQueue());
        cache.BeginFrame();
        Assert.True(cache.TryAddAndAcquire(1, Key(1), Texture(4, 4), out _));
        cache.BeginFrame();
        Assert.True(cache.TryAddAndAcquire(1, Key(2), Texture(8, 8), out _));

        cache.Dispose();

        int firstDelete = backend.Events.FindIndex(e => e.StartsWith("delete:"));
        Assert.Equal(2, backend.Events.Take(firstDelete).Count(e => e.StartsWith("nonresident:")));
        Assert.Equal(2, backend.Deleted.Count);
        cache.Dispose();
        Assert.Equal(4, backend.Events.Count);
    }

    [Fact]
    public void StructuralPaletteEqualityRejectsSameHashWithDifferentRanges()
    {
        const ulong collision = 1234;
        var left = new PaletteCompositeIdentity(
            new PaletteOverride(1, [new PaletteOverride.SubPaletteRange(2, 3, 4)]),
            collision);
        var right = new PaletteCompositeIdentity(
            new PaletteOverride(1, [new PaletteOverride.SubPaletteRange(9, 3, 4)]),
            collision);

        Assert.NotEqual(left, right);
        Assert.NotEqual(
            new CompositeTextureKey(CompositeTextureKind.PaletteComposite, 1, 0, left),
            new CompositeTextureKey(CompositeTextureKind.PaletteComposite, 1, 0, right));
    }

    [Fact]
    public void UploadBudgetIncludesIncomingLayerAndResetsEachFrame()
    {
        var backend = new FakeBackend(maximumLayers: 8);
        using var cache = new CompositeTextureArrayCache(
            backend,
            new DeferredRetirementQueue(),
            unownedBudgetBytes: long.MaxValue,
            physicalBudgetBytes: long.MaxValue,
            maximumUploadsPerFrame: 8,
            maximumUploadBytesPerFrame: 100);

        cache.BeginFrame();
        Assert.True(cache.TryAddAndAcquire(1, Key(1), Texture(4, 4), out _)); // 64 bytes
        Assert.False(cache.TryAddAndAcquire(1, Key(2), Texture(4, 4), out _));
        Assert.False(cache.CanStartUpload);
        Assert.Single(backend.Uploads);

        cache.BeginFrame();
        Assert.True(cache.CanStartUpload);
        Assert.True(cache.TryAddAndAcquire(1, Key(2), Texture(4, 4), out _));
        Assert.Equal(2, backend.Uploads.Count);
    }

    [Fact]
    public void DestinationReveal_RaisesOnlyItemCountAndRestoresNormalProfile()
    {
        var backend = new FakeBackend(maximumLayers: 64);
        using var cache = new CompositeTextureArrayCache(
            backend,
            new DeferredRetirementQueue(),
            unownedBudgetBytes: long.MaxValue,
            physicalBudgetBytes: long.MaxValue,
            maximumUploadsPerFrame: 2,
            maximumUploadBytesPerFrame: long.MaxValue);

        cache.BeginFrame(destinationRevealUploadPriority: true);
        Assert.True(cache.TryAddAndAcquire(1, Key(1), Texture(1, 1), out _));
        Assert.True(cache.TryAddAndAcquire(1, Key(2), Texture(1, 1), out _));
        Assert.True(cache.TryAddAndAcquire(1, Key(3), Texture(1, 1), out _));

        cache.BeginFrame();
        Assert.True(cache.TryAddAndAcquire(1, Key(4), Texture(1, 1), out _));
        Assert.True(cache.TryAddAndAcquire(1, Key(5), Texture(1, 1), out _));
        Assert.False(cache.TryAddAndAcquire(1, Key(6), Texture(1, 1), out _));
    }

    [Fact]
    public void OversizedTextureIsAllowedAsOnlyUploadToGuaranteeProgress()
    {
        var backend = new FakeBackend(maximumLayers: 8);
        using var cache = new CompositeTextureArrayCache(
            backend,
            new DeferredRetirementQueue(),
            unownedBudgetBytes: long.MaxValue,
            physicalBudgetBytes: long.MaxValue,
            maximumUploadsPerFrame: 8,
            maximumUploadBytesPerFrame: 32);

        cache.BeginFrame();
        Assert.True(cache.TryAddAndAcquire(1, Key(1), Texture(4, 4), out _));
        Assert.False(cache.TryAddAndAcquire(1, Key(2), Texture(1, 1), out _));
        Assert.Single(backend.Uploads);
    }

    [Fact]
    public void DimensionPreflightRejectsBeforeAllocatingDecodedPixelsForBlockedUpload()
    {
        var backend = new FakeBackend(maximumLayers: 8);
        using var cache = new CompositeTextureArrayCache(
            backend,
            new DeferredRetirementQueue(),
            unownedBudgetBytes: long.MaxValue,
            physicalBudgetBytes: long.MaxValue,
            maximumUploadsPerFrame: 8,
            maximumUploadBytesPerFrame: 100);

        cache.BeginFrame();
        Assert.True(cache.CanPrepareUpload(4, 4));
        Assert.True(cache.TryAddAndAcquire(1, Key(1), Texture(4, 4), out _)); // 64 bytes
        Assert.False(cache.CanPrepareUpload(4, 4));
        Assert.False(cache.CanStartUpload);
        Assert.Single(backend.Uploads);
    }

    [Fact]
    public void DimensionPreflightHonorsOneNewArrayPerFrame()
    {
        var backend = new FakeBackend(maximumLayers: 8);
        using var cache = CreateCache(backend, new DeferredRetirementQueue());

        cache.BeginFrame();
        Assert.True(cache.TryAddAndAcquire(1, Key(1), Texture(4, 4), out _));
        Assert.False(cache.CanPrepareUpload(8, 8));
        Assert.False(cache.CanStartUpload);
        Assert.Single(backend.Created);

        cache.BeginFrame();
        cache.Tick();
        Assert.True(cache.CanPrepareUpload(8, 8));
    }

    [Fact]
    public void PhysicalAdmissionReclaimsCompatibleLayerBeforeAllocatingAgain()
    {
        var backend = new FakeBackend(maximumLayers: 1);
        var retirements = new DeferredRetirementQueue();
        using var cache = CreateCache(
            backend,
            retirements,
            unownedBudgetBytes: long.MaxValue,
            physicalBudgetBytes: 64);

        cache.BeginFrame();
        Assert.True(cache.TryAddAndAcquire(1, Key(1), Texture(4, 4), out BindlessTextureLocation first));
        cache.ReleaseOwner(1);

        cache.BeginFrame();
        Assert.False(cache.TryAddAndAcquire(2, Key(2), Texture(4, 4), out _));
        Assert.Single(backend.Created);
        Assert.Equal(64, cache.AllocatedBytes);

        cache.Tick();
        Assert.Single(retirements.Actions);
        retirements.DrainAll();

        cache.BeginFrame();
        cache.Tick();
        Assert.True(cache.TryAddAndAcquire(2, Key(2), Texture(4, 4), out BindlessTextureLocation reused));
        Assert.Equal(first.Slot, reused.Slot);
        Assert.Single(backend.Created);
        Assert.Equal(64, cache.AllocatedBytes);
    }

    [Fact]
    public void AtlasRelease_NonResidentFailureRetainsAtlasAndAccountingForRetry()
    {
        var backend = new FakeBackend(maximumLayers: 1);
        var retirements = new DeferredRetirementQueue();
        using var cache = CreateCache(
            backend,
            retirements,
            unownedBudgetBytes: 0,
            physicalBudgetBytes: 0);
        cache.BeginFrame();
        Assert.True(cache.TryAddAndAcquire(1, Key(1), Texture(4, 4), out _));
        cache.ReleaseOwner(1);
        cache.Tick();
        retirements.DrainAll();

        backend.FailNextNonResident = true;
        Assert.Throws<InvalidOperationException>(cache.Tick);
        Assert.Equal(64, cache.AllocatedBytes);
        Assert.Equal(1, cache.AtlasCount);
        Assert.Empty(backend.Deleted);

        cache.Tick();
        Assert.Equal(0, cache.AllocatedBytes);
        Assert.Equal(0, cache.AtlasCount);
        Assert.Single(backend.NonResident);
        Assert.Single(backend.Deleted);
    }

    [Fact]
    public void AtlasRelease_DeleteFailureRevokesReuseButPreservesPhysicalAccounting()
    {
        var backend = new FakeBackend(maximumLayers: 1);
        var retirements = new DeferredRetirementQueue();
        using var cache = CreateCache(
            backend,
            retirements,
            unownedBudgetBytes: 0,
            physicalBudgetBytes: 0);
        cache.BeginFrame();
        Assert.True(cache.TryAddAndAcquire(1, Key(1), Texture(4, 4), out _));
        cache.ReleaseOwner(1);
        cache.Tick();
        retirements.DrainAll();

        backend.FailNextDelete = true;
        Assert.Throws<InvalidOperationException>(cache.Tick);
        Assert.Equal(64, cache.AllocatedBytes);
        Assert.Single(backend.NonResident);
        Assert.Empty(backend.Deleted);

        cache.BeginFrame();
        Assert.False(cache.TryAddAndAcquire(2, Key(2), Texture(4, 4), out _));
        Assert.Single(backend.Created);

        cache.Tick();
        Assert.Equal(0, cache.AllocatedBytes);
        Assert.Single(backend.NonResident);
        Assert.Single(backend.Deleted);

        cache.BeginFrame();
        Assert.True(cache.TryAddAndAcquire(2, Key(2), Texture(4, 4), out _));
        Assert.Equal(2, backend.Created.Count);
    }

    [Fact]
    public void AtlasRelease_PostCommitNonResidentFailureDoesNotReplayMutation()
    {
        var backend = new FakeBackend(maximumLayers: 1);
        var retirements = new DeferredRetirementQueue();
        using var cache = CreateCache(
            backend,
            retirements,
            unownedBudgetBytes: 0,
            physicalBudgetBytes: 0);
        cache.BeginFrame();
        Assert.True(cache.TryAddAndAcquire(1, Key(1), Texture(4, 4), out _));
        cache.ReleaseOwner(1);
        cache.Tick();
        retirements.DrainAll();

        backend.FailNextNonResidentAfterCommit = true;
        Assert.Throws<GpuResourceMutationException>(cache.Tick);
        Assert.Single(backend.NonResident);
        Assert.Empty(backend.Deleted);

        cache.Tick();
        Assert.Single(backend.NonResident);
        Assert.Single(backend.Deleted);
        Assert.Equal(0, cache.AllocatedBytes);
    }

    [Fact]
    public void AtlasRelease_PostCommitDeleteFailureDoesNotDeleteOrAccountTwice()
    {
        var backend = new FakeBackend(maximumLayers: 1);
        var retirements = new DeferredRetirementQueue();
        using var cache = CreateCache(
            backend,
            retirements,
            unownedBudgetBytes: 0,
            physicalBudgetBytes: 0);
        cache.BeginFrame();
        Assert.True(cache.TryAddAndAcquire(1, Key(1), Texture(4, 4), out _));
        cache.ReleaseOwner(1);
        cache.Tick();
        retirements.DrainAll();

        backend.FailNextDeleteAfterCommit = true;
        Assert.Throws<GpuResourceMutationException>(cache.Tick);
        Assert.Single(backend.NonResident);
        Assert.Single(backend.Deleted);
        Assert.Equal(64, cache.AllocatedBytes);

        cache.Tick();
        Assert.Single(backend.Deleted);
        Assert.Equal(0, cache.AllocatedBytes);
    }

    [Fact]
    public void LayerRetirement_PublicationFailureKeepsSlotUnavailableUntilRetry()
    {
        var backend = new FakeBackend(maximumLayers: 1);
        var retirements = new DeferredRetirementQueue { FailNextRetire = true };
        using var cache = CreateCache(
            backend,
            retirements,
            unownedBudgetBytes: 0,
            physicalBudgetBytes: 64);
        cache.BeginFrame();
        Assert.True(cache.TryAddAndAcquire(1, Key(1), Texture(4, 4), out _));
        cache.ReleaseOwner(1);

        Assert.Throws<InvalidOperationException>(cache.Tick);
        Assert.Equal(0, cache.CachedEntryCount);
        Assert.Empty(retirements.Actions);

        cache.BeginFrame();
        Assert.Single(retirements.Actions);
        cache.BeginFrame();
        Assert.False(cache.TryAddAndAcquire(2, Key(2), Texture(4, 4), out _));

        retirements.DrainAll();
        cache.BeginFrame();
        Assert.True(cache.TryAddAndAcquire(2, Key(2), Texture(4, 4), out _));
        Assert.Single(backend.Created);
    }

    [Fact]
    public void Dispose_ResidencyFailureDeletesNothingAndRetryResumesExactAtlas()
    {
        var backend = new FakeBackend(maximumLayers: 1);
        var cache = CreateCache(backend, new DeferredRetirementQueue());
        cache.BeginFrame();
        Assert.True(cache.TryAddAndAcquire(1, Key(1), Texture(4, 4), out _));
        cache.BeginFrame();
        Assert.True(cache.TryAddAndAcquire(1, Key(2), Texture(8, 8), out _));

        backend.FailNextNonResident = true;
        Assert.Throws<AggregateException>(cache.Dispose);
        Assert.Empty(backend.Deleted);
        Assert.Single(backend.NonResident);

        cache.Dispose();
        Assert.Equal(2, backend.NonResident.Count);
        Assert.Equal(2, backend.Deleted.Count);
        Assert.Equal(0, cache.AllocatedBytes);
    }

    private static CompositeTextureArrayCache CreateCache(
        FakeBackend backend,
        DeferredRetirementQueue retirements,
        long unownedBudgetBytes = long.MaxValue,
        long physicalBudgetBytes = long.MaxValue) =>
        new(
            backend,
            retirements,
            unownedBudgetBytes,
            physicalBudgetBytes,
            maximumUploadsPerFrame: 100,
            maximumUploadBytesPerFrame: long.MaxValue);

    private static CompositeTextureKey Key(uint id) =>
        new(CompositeTextureKind.OriginalTextureOverride, id, id + 100, default);

    private static DecodedTexture Texture(int width, int height) =>
        new(new byte[width * height * 4], width, height);

    private sealed class DeferredRetirementQueue : IGpuResourceRetirementQueue
    {
        private readonly Queue<Action> _actions = new();
        public int Count => _actions.Count;
        public IReadOnlyCollection<Action> Actions => _actions;
        public bool FailNextRetire { get; set; }
        public void Retire(Action release)
        {
            if (FailNextRetire)
            {
                FailNextRetire = false;
                throw new InvalidOperationException("synthetic retirement publication failure");
            }
            _actions.Enqueue(release);
        }
        public void DrainAll()
        {
            while (_actions.TryDequeue(out Action? action))
                action();
        }
    }

    private sealed class FakeBackend(int maximumLayers) : ICompositeTextureArrayBackend
    {
        private uint _nextName = 1;
        public int MaximumArrayLayers { get; } = maximumLayers;
        public List<CompositeTextureArrayResource> Created { get; } = [];
        public List<(uint Name, int Layer)> Uploads { get; } = [];
        public List<uint> NonResident { get; } = [];
        public List<uint> Deleted { get; } = [];
        public List<string> Events { get; } = [];
        public bool FailNextUpload { get; set; }
        public bool FailNextNonResident { get; set; }
        public bool FailNextDelete { get; set; }
        public bool FailNextNonResidentAfterCommit { get; set; }
        public bool FailNextDeleteAfterCommit { get; set; }

        public CompositeTextureArrayResource Create(int width, int height, int capacity)
        {
            uint name = _nextName++;
            var resource = new CompositeTextureArrayResource
            {
                Name = name,
                Handle = 1000UL + name,
                Slot = new AcDream.App.Rendering.Gpu.GpuTextureSlot(name),
                Width = width,
                Height = height,
                Capacity = capacity,
                Bytes = (long)width * height * 4 * capacity,
            };
            Created.Add(resource);
            return resource;
        }

        public void Upload(CompositeTextureArrayResource resource, int layer, byte[] rgba)
        {
            if (FailNextUpload)
            {
                FailNextUpload = false;
                throw new InvalidOperationException("synthetic upload failure");
            }
            Uploads.Add((resource.Name, layer));
        }

        public void MakeNonResident(CompositeTextureArrayResource resource)
        {
            if (FailNextNonResident)
            {
                FailNextNonResident = false;
                throw new InvalidOperationException("synthetic non-resident failure");
            }
            NonResident.Add(resource.Name);
            Events.Add($"nonresident:{resource.Name}");
            if (FailNextNonResidentAfterCommit)
            {
                FailNextNonResidentAfterCommit = false;
                throw new GpuResourceMutationException(
                    "synthetic post-commit nonresident failure",
                    mutationCommitted: true,
                    new InvalidOperationException("observer"));
            }
        }

        public void Delete(CompositeTextureArrayResource resource)
        {
            if (FailNextDelete)
            {
                FailNextDelete = false;
                throw new InvalidOperationException("synthetic delete failure");
            }
            Deleted.Add(resource.Name);
            Events.Add($"delete:{resource.Name}");
            if (FailNextDeleteAfterCommit)
            {
                FailNextDeleteAfterCommit = false;
                throw new GpuResourceMutationException(
                    "synthetic post-commit delete failure",
                    mutationCommitted: true,
                    new InvalidOperationException("observer"));
            }
        }
    }
}
