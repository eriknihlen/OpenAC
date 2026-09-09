using AcDream.App.Rendering;

namespace AcDream.App.Tests.Rendering;

public sealed class StandaloneBindlessTextureCacheTests
{
    [Fact]
    public void SharedSurfaceRemainsLiveUntilFinalEmitterOwnerLeaves()
    {
        var backend = new FakeBackend();
        var retirements = new DeferredRetirementQueue();
        using var cache = CreateCache(backend, retirements);
        StandaloneBindlessTextureResource resource = Resource(1, bytes: 4);

        cache.AddAndAcquire(10, resource);
        Assert.True(cache.TryAcquire(20, 1, out StandaloneBindlessTextureResource? shared));
        Assert.Same(resource, shared);

        cache.ReleaseOwner(10);
        Assert.Equal(1, cache.ActiveResourceCount);
        Assert.Equal(0, cache.UnownedEntryCount);
        Assert.Empty(retirements.Actions);

        cache.ReleaseOwner(20);
        Assert.Equal(0, cache.ActiveResourceCount);
        Assert.Equal(1, cache.UnownedEntryCount);
        Assert.Empty(retirements.Actions);

        Assert.True(cache.TryAcquire(30, 1, out StandaloneBindlessTextureResource? reused));
        Assert.Same(resource, reused);
        Assert.Equal(0, cache.UnownedEntryCount);
        Assert.Empty(retirements.Actions);
    }

    [Fact]
    public void UnownedCountBoundEvictsOldestAndDefersPhysicalRetirement()
    {
        var backend = new FakeBackend();
        var retirements = new DeferredRetirementQueue();
        using var cache = CreateCache(
            backend,
            retirements,
            unownedBudgetBytes: 1_000,
            maximumUnownedCount: 2);

        AddAndRelease(cache, ownerId: 10, Resource(1, bytes: 4));
        AddAndRelease(cache, ownerId: 20, Resource(2, bytes: 4));
        AddAndRelease(cache, ownerId: 30, Resource(3, bytes: 4));

        Assert.Equal(3, cache.EntryCount);
        Assert.Empty(retirements.Actions);
        cache.Tick();

        Assert.Equal(2, cache.EntryCount);
        Assert.Equal(12, cache.AllocatedBytes);
        Assert.Equal(4, cache.RetiringBytes);
        Assert.Equal(2, cache.UnownedEntryCount);
        Assert.Single(retirements.Actions);
        Assert.Empty(backend.Events);
        Assert.False(cache.TryAcquire(40, 1, out _));
        Assert.True(cache.TryAcquire(40, 2, out _));

        retirements.Drain();
        Assert.Equal(["nonresident:1", "delete:1"], backend.Events);
        Assert.Equal(8, cache.AllocatedBytes);
        Assert.Equal(0, cache.RetiringBytes);
    }

    [Fact]
    public void UnownedByteBoundNeverEvictsALiveEmitterTexture()
    {
        var backend = new FakeBackend();
        var retirements = new DeferredRetirementQueue();
        using var cache = CreateCache(
            backend,
            retirements,
            unownedBudgetBytes: 8,
            maximumUnownedCount: 10);

        cache.AddAndAcquire(10, Resource(1, bytes: 8));
        cache.AddAndAcquire(20, Resource(2, bytes: 8));
        cache.ReleaseOwner(10);

        Assert.Equal(2, cache.EntryCount);
        Assert.Equal(8, cache.UnownedBytes);
        Assert.Empty(retirements.Actions);

        cache.AddAndAcquire(30, Resource(3, bytes: 8));
        cache.ReleaseOwner(30);

        Assert.Equal(3, cache.EntryCount);
        Assert.Empty(retirements.Actions);
        cache.Tick();

        Assert.Equal(2, cache.EntryCount);
        Assert.Equal(8, cache.UnownedBytes);
        Assert.Single(retirements.Actions);
        Assert.True(cache.TryAcquire(40, 2, out _));
        Assert.False(cache.TryAcquire(40, 1, out _));
    }

    [Fact]
    public void RepeatedAcquireByOneEmitterIsIdempotent()
    {
        var backend = new FakeBackend();
        using var cache = CreateCache(backend, new DeferredRetirementQueue());
        StandaloneBindlessTextureResource resource = Resource(1, bytes: 4);

        cache.AddAndAcquire(10, resource);
        Assert.True(cache.TryAcquire(10, 1, out _));
        Assert.True(cache.TryAcquire(10, 1, out _));
        Assert.Equal(1, cache.OwnerCount);
        Assert.Equal(1, cache.ActiveResourceCount);

        cache.ReleaseOwner(10);
        Assert.Equal(0, cache.OwnerCount);
        Assert.Equal(0, cache.ActiveResourceCount);
        Assert.Equal(1, cache.UnownedEntryCount);
    }

    [Fact]
    public void PortalScaleOwnerReleaseQueuesOnlyThePerFrameEvictionBudget()
    {
        var backend = new FakeBackend();
        var retirements = new DeferredRetirementQueue();
        using var cache = CreateCache(
            backend,
            retirements,
            unownedBudgetBytes: 1_000,
            maximumUnownedCount: 2);

        for (uint id = 1; id <= 100; id++)
            AddAndRelease(cache, ownerId: id, Resource(id, bytes: 4));

        Assert.Empty(retirements.Actions);
        cache.Tick();
        Assert.Single(retirements.Actions);
        cache.Tick(maximumEvictions: 3);
        Assert.Equal(4, retirements.Actions.Count);
        Assert.Equal(96, cache.EntryCount);
    }

    [Fact]
    public void FailedRetirementQueueAdmissionRetainsReleaseForPublicationRetry()
    {
        var backend = new FakeBackend();
        var retirements = new DeferredRetirementQueue { FailNextAdmission = true };
        using var cache = CreateCache(
            backend,
            retirements,
            unownedBudgetBytes: 1_000,
            maximumUnownedCount: 1);
        AddAndRelease(cache, ownerId: 10, Resource(1, bytes: 4));
        AddAndRelease(cache, ownerId: 20, Resource(2, bytes: 4));

        Assert.Throws<InvalidOperationException>(() => cache.Tick());
        Assert.Equal(1, cache.EntryCount);
        Assert.Equal(1, cache.UnownedEntryCount);
        Assert.Equal(1, cache.AwaitingRetirementPublicationCount);

        cache.Tick();
        Assert.Equal(1, cache.EntryCount);
        Assert.Single(retirements.Actions);
        Assert.Equal(0, cache.AwaitingRetirementPublicationCount);
    }

    [Fact]
    public void DeleteFailureDoesNotRepeatCommittedResidencyRelease()
    {
        var backend = new FakeBackend { FailDeleteSurfaceIdOnce = 1 };
        var retirements = new DeferredRetirementQueue();
        using var cache = CreateCache(
            backend,
            retirements,
            unownedBudgetBytes: 1_000,
            maximumUnownedCount: 1);
        AddAndRelease(cache, ownerId: 10, Resource(1, bytes: 4));
        AddAndRelease(cache, ownerId: 20, Resource(2, bytes: 4));
        cache.Tick();

        Assert.Throws<InvalidOperationException>(() => retirements.DrainOne());
        retirements.Drain();

        Assert.Equal(1, backend.Events.Count(static e => e == "nonresident:1"));
        Assert.Equal(2, backend.Events.Count(static e => e == "delete:1"));
    }

    [Fact]
    public void DisposeMakesEveryHandleNonResidentBeforeDeletingAnyTexture()
    {
        var backend = new FakeBackend();
        var cache = CreateCache(backend, new DeferredRetirementQueue());
        cache.AddAndAcquire(10, Resource(1, bytes: 4));
        cache.AddAndAcquire(20, Resource(2, bytes: 4));

        cache.Dispose();

        Assert.Equal(
            ["nonresident:1", "nonresident:2", "delete:1", "delete:2"],
            backend.Events);
    }

    [Fact]
    public void DisposePreservesAcceptedFenceRetirementAccountingUntilCompletion()
    {
        var backend = new FakeBackend();
        var retirements = new DeferredRetirementQueue();
        var cache = CreateCache(
            backend,
            retirements,
            unownedBudgetBytes: 1_000,
            maximumUnownedCount: 1);
        AddAndRelease(cache, ownerId: 10, Resource(1, bytes: 4));
        AddAndRelease(cache, ownerId: 20, Resource(2, bytes: 4));
        cache.Tick();

        Assert.Equal(8, cache.AllocatedBytes);
        Assert.Equal(4, cache.RetiringBytes);

        cache.Dispose();

        Assert.Equal(4, cache.AllocatedBytes);
        Assert.Equal(4, cache.RetiringBytes);
        retirements.Drain();
        Assert.Equal(0, cache.AllocatedBytes);
        Assert.Equal(0, cache.RetiringBytes);
    }

    [Fact]
    public void FailedResidencyReleaseNeverDeletesThatBackingTexture()
    {
        var backend = new FakeBackend { FailNonResidentSurfaceId = 1 };
        var cache = CreateCache(backend, new DeferredRetirementQueue());
        cache.AddAndAcquire(10, Resource(1, bytes: 4));
        cache.AddAndAcquire(20, Resource(2, bytes: 4));

        AggregateException failure = Assert.Throws<AggregateException>(() => cache.Dispose());

        Assert.Contains("surface 1", failure.ToString());
        Assert.DoesNotContain("delete:1", backend.Events);
        Assert.Contains("delete:2", backend.Events);
    }

    [Fact]
    public void DisposeRetriesOnlyTheFailedDeleteStage()
    {
        var backend = new FakeBackend { FailDeleteSurfaceIdOnce = 1 };
        var cache = CreateCache(backend, new DeferredRetirementQueue());
        cache.AddAndAcquire(10, Resource(1, bytes: 4));
        cache.AddAndAcquire(20, Resource(2, bytes: 4));

        Assert.Throws<AggregateException>(() => cache.Dispose());
        cache.Dispose();

        Assert.Equal(1, backend.Events.Count(static e => e == "nonresident:1"));
        Assert.Equal(1, backend.Events.Count(static e => e == "nonresident:2"));
        Assert.Equal(2, backend.Events.Count(static e => e == "delete:1"));
        Assert.Equal(1, backend.Events.Count(static e => e == "delete:2"));
    }

    [Fact]
    public void BackendDisposeReentryObservesActiveTransactionWithoutRepeatingStages()
    {
        var backend = new FakeBackend();
        StandaloneBindlessTextureCache? cache = null;
        backend.OnMutation = _ => cache!.Dispose();
        cache = CreateCache(backend, new DeferredRetirementQueue());
        cache.AddAndAcquire(10, Resource(1, bytes: 4));

        cache.Dispose();

        Assert.Equal(["nonresident:1", "delete:1"], backend.Events);
        cache.Dispose();
        Assert.Equal(["nonresident:1", "delete:1"], backend.Events);
    }

    private static StandaloneBindlessTextureCache CreateCache(
        FakeBackend backend,
        DeferredRetirementQueue retirements,
        long unownedBudgetBytes = 64,
        int maximumUnownedCount = 8) =>
        new(
            backend,
            retirements,
            unownedBudgetBytes,
            maximumUnownedCount);

    private static void AddAndRelease(
        StandaloneBindlessTextureCache cache,
        uint ownerId,
        StandaloneBindlessTextureResource resource)
    {
        cache.AddAndAcquire(ownerId, resource);
        cache.ReleaseOwner(ownerId);
    }

    private static StandaloneBindlessTextureResource Resource(uint surfaceId, long bytes) =>
        new()
        {
            SurfaceId = surfaceId,
            Name = surfaceId,
            Handle = surfaceId + 1_000UL,
            Slot = new AcDream.App.Rendering.Gpu.GpuTextureSlot(surfaceId),
            Bytes = bytes,
        };

    private sealed class DeferredRetirementQueue : IGpuResourceRetirementQueue
    {
        public bool FailNextAdmission { get; set; }
        public Queue<Action> Actions { get; } = new();

        public void Retire(Action release)
        {
            if (FailNextAdmission)
            {
                FailNextAdmission = false;
                throw new InvalidOperationException("retirement queue full");
            }
            Actions.Enqueue(release);
        }

        public void Drain()
        {
            while (Actions.TryDequeue(out Action? release))
                release();
        }

        public void DrainOne()
        {
            Assert.True(Actions.TryDequeue(out Action? release));
            try
            {
                release();
            }
            catch
            {
                Actions.Enqueue(release);
                throw;
            }
        }
    }

    private sealed class FakeBackend : IStandaloneBindlessTextureBackend
    {
        public uint FailNonResidentSurfaceId { get; init; }
        public uint FailDeleteSurfaceIdOnce { get; set; }
        public List<string> Events { get; } = [];
        public Action<string>? OnMutation { get; set; }

        public void MakeNonResident(StandaloneBindlessTextureResource resource)
        {
            Events.Add($"nonresident:{resource.SurfaceId}");
            OnMutation?.Invoke("nonresident");
            if (resource.SurfaceId == FailNonResidentSurfaceId)
                throw new InvalidOperationException($"Could not release surface {resource.SurfaceId}.");
        }

        public void Delete(StandaloneBindlessTextureResource resource)
        {
            Events.Add($"delete:{resource.SurfaceId}");
            OnMutation?.Invoke("delete");
            if (resource.SurfaceId == FailDeleteSurfaceIdOnce)
            {
                FailDeleteSurfaceIdOnce = 0;
                throw new InvalidOperationException($"Could not delete surface {resource.SurfaceId}.");
            }
        }
    }
}
