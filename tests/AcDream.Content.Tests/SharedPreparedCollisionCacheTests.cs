using System.Collections.Immutable;
using AcDream.Content.Pak;
using AcDream.Core.Physics;

namespace AcDream.Content.Tests;

public sealed class SharedPreparedCollisionCacheTests
{
    [Fact]
    public void LoadedPayloadIsSharedByReferenceAndLeastRecentlyUsedIsBounded()
    {
        var source = new FixtureSource();
        using var cache = new SharedPreparedCollisionCache(
            source,
            capacity: 2);

        FlatSetupCollision first =
            Assert.IsType<FlatSetupCollision>(
                cache.ReadSetupCollision(1u).Data);
        Assert.Same(
            first,
            cache.ReadSetupCollision(1u).Data);
        _ = cache.ReadSetupCollision(2u);
        Assert.Same(
            first,
            cache.ReadSetupCollision(1u).Data);
        _ = cache.ReadSetupCollision(3u);

        SharedPreparedCollisionCacheSnapshot snapshot =
            cache.CaptureSnapshot();
        Assert.Equal(2, snapshot.Count);
        Assert.True(snapshot.IsBounded);
        Assert.Equal(2, snapshot.Hits);
        Assert.Equal(3, snapshot.Misses);
        Assert.Equal(1, source.SetupReads[1u]);
        Assert.Equal(1, source.SetupReads[2u]);

        _ = cache.ReadSetupCollision(2u);

        Assert.Equal(2, source.SetupReads[2u]);
        Assert.True(cache.CaptureSnapshot().IsBounded);
    }

    [Fact]
    public void TypeParticipatesInIdentityAndDisposeDoesNotOwnPackage()
    {
        var source = new FixtureSource();
        var cache = new SharedPreparedCollisionCache(source, capacity: 4);

        FlatSetupCollision setup =
            Assert.IsType<FlatSetupCollision>(
                cache.ReadSetupCollision(7u).Data);
        FlatGfxObjCollisionAsset gfx =
            Assert.IsType<FlatGfxObjCollisionAsset>(
                cache.ReadGfxObjCollision(7u).Data);

        Assert.NotSame(setup, gfx);
        Assert.Equal(1, source.SetupReads[7u]);
        Assert.Equal(1, source.GfxReads[7u]);

        cache.Dispose();

        Assert.False(source.IsDisposed);
        Assert.True(cache.CaptureSnapshot().IsDisposed);
        Assert.Throws<ObjectDisposedException>(
            () => cache.ReadSetupCollision(7u));
    }

    private sealed class FixtureSource : IPreparedCollisionSource
    {
        internal Dictionary<uint, int> SetupReads { get; } = new();
        internal Dictionary<uint, int> GfxReads { get; } = new();
        internal bool IsDisposed { get; private set; }

        public PreparedCollisionSourceStats CollisionStats => default;

        public PreparedAssetPresence ProbeCollision(
            PakAssetType type,
            uint sourceFileId) =>
            PreparedAssetPresence.Available;

        public PreparedCollisionReadResult<FlatGfxObjCollisionAsset>
            ReadGfxObjCollision(
                uint sourceFileId,
                CancellationToken cancellationToken = default)
        {
            cancellationToken.ThrowIfCancellationRequested();
            Increment(GfxReads, sourceFileId);
            return PreparedCollisionReadResult<
                FlatGfxObjCollisionAsset>.Loaded(
                new FlatGfxObjCollisionAsset(
                    EmptyPhysicsBsp(),
                    null,
                    null));
        }

        public PreparedCollisionReadResult<FlatSetupCollision>
            ReadSetupCollision(
                uint sourceFileId,
                CancellationToken cancellationToken = default)
        {
            cancellationToken.ThrowIfCancellationRequested();
            Increment(SetupReads, sourceFileId);
            return PreparedCollisionReadResult<FlatSetupCollision>.Loaded(
                new FlatSetupCollision(
                    ImmutableArray<FlatCollisionCylinder>.Empty,
                    ImmutableArray<FlatCollisionSphere>.Empty,
                    0f,
                    0f,
                    0f,
                    0f));
        }

        public PreparedCollisionReadResult<
            FlatCellStructureCollisionAsset>
            ReadCellStructureCollision(
                uint sourceFileId,
                CancellationToken cancellationToken = default) =>
            PreparedCollisionReadResult<
                FlatCellStructureCollisionAsset>.Missing;

        public PreparedCollisionReadResult<FlatEnvCellTopology>
            ReadEnvCellTopology(
                uint sourceFileId,
                CancellationToken cancellationToken = default) =>
            PreparedCollisionReadResult<FlatEnvCellTopology>.Missing;

        public void Dispose() => IsDisposed = true;

        private static FlatPhysicsBsp EmptyPhysicsBsp() => new(
            -1,
            ImmutableArray<FlatPhysicsBspNode>.Empty,
            ImmutableArray<int>.Empty,
            FlatPolygonTable.Empty);

        private static void Increment(
            Dictionary<uint, int> counts,
            uint id) =>
            counts[id] = counts.TryGetValue(id, out int count)
                ? count + 1
                : 1;
    }
}
