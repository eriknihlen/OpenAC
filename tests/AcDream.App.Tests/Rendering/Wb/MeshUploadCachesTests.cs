using AcDream.App.Rendering.Wb;
using AcDream.Content;
using Chorizite.Core.Render.Enums;

namespace AcDream.App.Tests.Rendering.Wb;

public sealed class MeshUploadCachesTests
{
    [Fact]
    public void CpuCacheHit_AfterCompletedUpload_ReStagesExactlyOnceWithTexturePayload()
    {
        var staging = new MeshUploadStagingQueue();
        var cache = new CpuMeshUploadCache(capacity: 2);
        var ownership = Owned(0x01000010ul);
        byte[] textureBytes = [1, 2, 3, 4];
        var data = new ObjectMeshData { ObjectId = 0x01000010ul, UploadAttempts = 2 };
        data.TextureBatches[(1, 1, TextureFormat.RGBA8)] =
        [
            new TextureBatchData { TextureData = textureBytes },
        ];
        cache.Store(data);

        Assert.True(staging.Stage(data));
        Assert.True(staging.TryDequeue(out var initial));
        Assert.Same(data, initial.Data);
        staging.Complete(initial);

        data.UploadAttempts = 3;
        Assert.True(cache.TryGetAndStageOwned(
            data.ObjectId, staging, ownership, out var cached, out _));
        Assert.Same(data, cached);
        Assert.Equal(0, data.UploadAttempts);
        Assert.Equal(textureBytes, cached!.TextureBatches.Values.Single().Single().TextureData);
        Assert.True(cache.TryGetAndStageOwned(
            data.ObjectId, staging, ownership, out _, out _));
        Assert.True(staging.TryDequeue(out var restaged));
        Assert.Same(data, restaged.Data);
        Assert.False(staging.TryDequeue(out _));
    }

    [Fact]
    public void Retry_RetainsQueueOwnershipUntilCompletion()
    {
        var staging = new MeshUploadStagingQueue();
        var data = new ObjectMeshData { ObjectId = 0x01000010ul };

        Assert.True(staging.Stage(data));
        Assert.True(staging.TryDequeue(out var attempt));
        staging.Requeue(attempt);
        Assert.False(staging.Stage(data));
        Assert.True(staging.TryDequeue(out var retry));
        Assert.Same(data, retry.Data);

        staging.Complete(retry);
        Assert.True(staging.Stage(data));
    }

    [Fact]
    public void UploadCompletion_NeverManufacturesLogicalOwnership()
    {
        const ulong id = 0x01000010ul;
        var ownership = new MeshOwnershipCounter();

        Assert.Equal(1, ownership.Acquire(id));
        Assert.True(ownership.MarkUploadComplete(id));
        Assert.Equal(1, ownership.Count(id));

        Assert.Equal(0, ownership.Release(id));
        Assert.False(ownership.MarkUploadComplete(id)); // late upload after unload
        Assert.Equal(0, ownership.Count(id));
    }

    [Fact]
    public void StagingQueueRejectsProducerWorkAboveByteHighWater()
    {
        var staging = new MeshUploadStagingQueue(maximumCount: 8, maximumBytes: 64);
        ObjectMeshData first = DataWithTexture(1, 40);
        ObjectMeshData second = DataWithTexture(2, 40);

        Assert.True(staging.Stage(first));
        Assert.False(staging.Stage(second));
        Assert.Equal(1, staging.Count);
        Assert.Equal(40, staging.QueuedBytes);

        Assert.True(staging.TryDequeue(out MeshUploadQueueItem firstItem));
        staging.Complete(firstItem);
        Assert.True(staging.Stage(second));
    }

    [Fact]
    public void CpuCacheHitReportsHighWaterWithoutPretendingItWasStaged()
    {
        var staging = new MeshUploadStagingQueue(maximumCount: 8, maximumBytes: 64);
        var cache = new CpuMeshUploadCache(capacity: 2);
        var ownership = Owned(2);
        ObjectMeshData queued = DataWithTexture(1, 40);
        ObjectMeshData cached = DataWithTexture(2, 40);
        cache.Store(cached);
        Assert.True(staging.Stage(queued));

        Assert.True(cache.TryGetAndStageOwned(
            2, staging, ownership, out ObjectMeshData? found, out MeshStageResult result));
        Assert.Same(cached, found);
        Assert.Equal(MeshStageResult.HighWater, result);
        Assert.Equal(1, staging.Count);

        Assert.True(staging.TryDequeue(out MeshUploadQueueItem queuedItem));
        staging.Complete(queuedItem);
        Assert.True(cache.TryGetAndStageOwned(
            2, staging, ownership, out _, out result));
        Assert.Equal(MeshStageResult.Staged, result);
        Assert.True(staging.TryDequeue(out MeshUploadQueueItem staged));
        Assert.Same(cached, staged.Data);
    }

    [Fact]
    public void StalePrefixDrainIsBoundedAndDoesNotConsumeOwnedHead()
    {
        const int total = 1000;
        var staging = new MeshUploadStagingQueue(
            maximumCount: total + 1,
            maximumBytes: long.MaxValue);
        var ownership = new MeshOwnershipCounter();
        for (ulong id = 1; id <= total; id++)
            Assert.True(staging.Stage(new ObjectMeshData { ObjectId = id }));
        ownership.Acquire(total);

        Assert.Equal(64, staging.DiscardUnownedPrefix(ownership, maximum: 64));
        Assert.Equal(total - 64, staging.Count);

        Assert.Equal(total - 65, staging.DiscardUnownedPrefix(ownership, maximum: total));
        Assert.True(staging.TryPeek(out MeshUploadQueueItem owned));
        Assert.Equal((ulong)total, owned.Data.ObjectId);
    }

    [Fact]
    public void CpuCacheRearmsMeshAfterStaleStageWasDiscarded()
    {
        const ulong id = 0x01000010;
        var staging = new MeshUploadStagingQueue();
        var cache = new CpuMeshUploadCache(capacity: 2);
        var ownership = new MeshOwnershipCounter();
        var data = DataWithTexture(id, 16);
        cache.Store(data);
        Assert.True(staging.Stage(data));

        Assert.Equal(1, staging.DiscardUnownedPrefix(ownership, maximum: 1));
        Assert.Equal(0, staging.Count);

        ownership.Acquire(id);
        Assert.True(cache.TryGetAndStageOwned(
            id, staging, ownership, out ObjectMeshData? rearmed, out _));
        Assert.Same(data, rearmed);
        Assert.Equal(0, staging.DiscardUnownedPrefix(ownership, maximum: 1));
        Assert.True(staging.TryPeek(out MeshUploadQueueItem queued));
        Assert.Same(data, queued.Data);
    }

    [Fact]
    public void CpuCacheHitWithoutOwnerDoesNotRecreateStaleUploadWork()
    {
        const ulong id = 0x01000010;
        var staging = new MeshUploadStagingQueue();
        var cache = new CpuMeshUploadCache(capacity: 2);
        var ownership = new MeshOwnershipCounter();
        ObjectMeshData data = DataWithTexture(id, 16);
        cache.Store(data);

        Assert.True(cache.TryGetAndStageOwned(
            id,
            staging,
            ownership,
            out ObjectMeshData? cached,
            out MeshStageResult result));

        Assert.Same(data, cached);
        Assert.Equal(MeshStageResult.NotOwned, result);
        Assert.Equal(0, staging.Count);
        Assert.Equal(0, staging.ClaimCount);

        ownership.Acquire(id);
        Assert.True(cache.TryGetAndStageOwned(
            id,
            staging,
            ownership,
            out _,
            out result));
        Assert.Equal(MeshStageResult.Staged, result);
        Assert.Equal(1, staging.Count);
    }

    [Fact]
    public void CpuCacheUsesByteBudgetInAdditionToEntryCount()
    {
        var staging = new MeshUploadStagingQueue();
        var cache = new CpuMeshUploadCache(capacity: 10, byteCapacity: 64);
        var ownership = Owned(1, 2);
        cache.Store(DataWithTexture(1, 40));
        cache.Store(DataWithTexture(2, 40));

        Assert.False(cache.TryGetAndStageOwned(
            1, staging, ownership, out _, out _));
        Assert.True(cache.TryGetAndStageOwned(
            2, staging, ownership, out _, out _));
        Assert.Equal(1, cache.Count);
        Assert.Equal(40, cache.ResidentBytes);
    }

    [Fact]
    public void CpuCacheRejectsOversizedEntryBeforeEvictingOrPublishingIt()
    {
        var staging = new MeshUploadStagingQueue(maximumBytes: 256);
        var cache = new CpuMeshUploadCache(capacity: 2, byteCapacity: 64);
        var ownership = Owned(1, 2);
        ObjectMeshData retained = DataWithTexture(1, 40);
        ObjectMeshData oversized = DataWithTexture(2, 80);

        Assert.True(cache.Store(retained));
        Assert.False(cache.Store(oversized));

        Assert.Equal(1, cache.Count);
        Assert.Equal(40, cache.ResidentBytes);
        Assert.False(cache.TryGetAndStageOwned(
            2, staging, ownership, out _, out _));
        Assert.True(cache.TryGetAndStageOwned(
            1, staging, ownership, out ObjectMeshData? found, out _));
        Assert.Same(retained, found);
    }

    [Fact]
    public void OversizedReplacementCannotDisplaceBoundedCachedGeneration()
    {
        var staging = new MeshUploadStagingQueue(maximumBytes: 256);
        var cache = new CpuMeshUploadCache(capacity: 2, byteCapacity: 64);
        var ownership = Owned(1);
        ObjectMeshData retained = DataWithTexture(1, 40);

        Assert.True(cache.Store(retained));
        Assert.False(cache.Store(DataWithTexture(1, 80)));
        Assert.True(cache.TryGetAndStageOwned(
            1, staging, ownership, out ObjectMeshData? found, out _));
        Assert.Same(retained, found);
        Assert.Equal(40, cache.ResidentBytes);
    }

    [Fact]
    public void DequeuedUnownedGenerationHandsOffToRacingCacheHit()
    {
        const ulong id = 0x01000010;
        var staging = new MeshUploadStagingQueue();
        var cache = new CpuMeshUploadCache(capacity: 2);
        var ownership = new MeshOwnershipCounter();
        ObjectMeshData data = DataWithTexture(id, 16);
        cache.Store(data);

        Assert.True(staging.Stage(data));
        Assert.True(staging.TryDequeue(out MeshUploadQueueItem dequeued));

        ownership.Acquire(id);
        Assert.True(cache.TryGetAndStageOwned(
            id, staging, ownership, out ObjectMeshData? cached, out _));
        Assert.Same(data, cached);
        Assert.Equal(0, staging.Count);

        Assert.True(staging.CompleteOrRestageIfOwned(dequeued, ownership));
        Assert.True(staging.TryDequeue(out MeshUploadQueueItem handedOff));
        Assert.Same(data, handedOff.Data);
        Assert.False(staging.TryDequeue(out _));
    }

    [Fact]
    public void OwnerArrivingAfterUnownedCompletionStagesCachedPayloadExactlyOnce()
    {
        const ulong id = 0x01000010;
        var staging = new MeshUploadStagingQueue();
        var cache = new CpuMeshUploadCache(capacity: 2);
        var ownership = new MeshOwnershipCounter();
        ObjectMeshData data = DataWithTexture(id, 16);
        cache.Store(data);

        Assert.True(staging.Stage(data));
        Assert.True(staging.TryDequeue(out MeshUploadQueueItem dequeued));
        Assert.False(staging.CompleteOrRestageIfOwned(dequeued, ownership));
        Assert.Equal(0, staging.Count);

        ownership.Acquire(id);
        Assert.True(cache.TryGetAndStageOwned(
            id, staging, ownership, out ObjectMeshData? cached, out _));
        Assert.Same(data, cached);
        Assert.True(staging.TryDequeue(out MeshUploadQueueItem handedOff));
        Assert.Same(data, handedOff.Data);
        Assert.False(staging.TryDequeue(out _));
    }

    [Fact]
    public void ReacquiredObjectGetsNewImmutableQueueGeneration()
    {
        var staging = new MeshUploadStagingQueue();
        var data = new ObjectMeshData { ObjectId = 0x01000010 };

        Assert.True(staging.Stage(data));
        Assert.True(staging.TryDequeue(out MeshUploadQueueItem first));
        staging.Complete(first);
        Assert.True(staging.Stage(data));
        Assert.True(staging.TryPeek(out MeshUploadQueueItem replacement));

        Assert.NotEqual(first.Generation, replacement.Generation);
        Assert.Throws<InvalidOperationException>(() => staging.Complete(first));
        Assert.True(staging.TryPeek(out MeshUploadQueueItem stillQueued));
        Assert.Equal(replacement.Generation, stillQueued.Generation);
    }

    [Fact]
    public void OversizedHeadIsExplicitlyBoundedAndFollowingProducerBackpressures()
    {
        var staging = new MeshUploadStagingQueue(
            maximumCount: 8,
            maximumBytes: 64,
            maximumSingleEntryBytes: 96);
        ObjectMeshData oversized = DataWithTexture(1, 80);

        Assert.True(staging.Stage(oversized));
        Assert.False(staging.Stage(DataWithTexture(2, 1)));
        Assert.Throws<NotSupportedException>(() => staging.Stage(DataWithTexture(3, 97)));
        Assert.Equal(1, staging.Count);
        Assert.Equal(80, staging.QueuedBytes);
    }

    [Fact]
    public void DequeuedGenerationStillCountsAgainstProducerByteAndCountBounds()
    {
        var staging = new MeshUploadStagingQueue(maximumCount: 1, maximumBytes: 64);
        ObjectMeshData first = DataWithTexture(1, 40);

        Assert.True(staging.Stage(first));
        Assert.True(staging.TryDequeue(out MeshUploadQueueItem inFlight));
        Assert.Equal(0, staging.Count);
        Assert.Equal(1, staging.ClaimCount);
        Assert.Equal(0, staging.QueuedBytes);
        Assert.Equal(40, staging.ClaimedBytes);
        Assert.True(staging.IsAtHighWater);
        Assert.False(staging.Stage(DataWithTexture(2, 1)));

        staging.Complete(inFlight);
        Assert.True(staging.Stage(DataWithTexture(2, 1)));
    }

    [Fact]
    public void CpuCacheStats_TracksHitsMissesAndEvictions()
    {
        var staging = new MeshUploadStagingQueue();
        var cache = new CpuMeshUploadCache(capacity: 1);
        var ownership = Owned(1);

        Assert.False(cache.TryGetAndStageOwned(
            1, staging, ownership, out _, out _)); // miss
        cache.Store(DataWithTexture(1, 4));
        Assert.True(cache.TryGetAndStageOwned(
            1, staging, ownership, out _, out _)); // hit
        cache.Store(DataWithTexture(2, 4));

        CacheStats stats = cache.Stats;
        Assert.Equal(1, stats.Hits);
        Assert.Equal(1, stats.Misses);
        Assert.Equal(1, stats.Evictions);
    }

    private static MeshOwnershipCounter Owned(params ulong[] ids)
    {
        var ownership = new MeshOwnershipCounter();
        foreach (ulong id in ids)
            ownership.Acquire(id);
        return ownership;
    }

    private static ObjectMeshData DataWithTexture(ulong id, int bytes)
    {
        var data = new ObjectMeshData { ObjectId = id };
        data.TextureBatches[(1, 1, TextureFormat.RGBA8)] =
        [
            new TextureBatchData { TextureData = new byte[bytes] },
        ];
        return data;
    }
}
