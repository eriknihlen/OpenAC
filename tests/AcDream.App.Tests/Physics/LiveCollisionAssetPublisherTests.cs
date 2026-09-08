using System.Collections.Immutable;
using AcDream.App.Physics;
using AcDream.Content;
using AcDream.Content.Pak;
using AcDream.Core.Physics;
using DatReaderWriter.DBObjs;
using DatReaderWriter.Enums;
using DatReaderWriter.Types;

namespace AcDream.App.Tests.Physics;

public sealed class LiveCollisionAssetPublisherTests
{
    [Fact]
    public void LiveGfxAndSetup_PublishExactPreparedViewOnce()
    {
        const uint GfxId = 0x0100_0042u;
        const uint SetupId = 0x0200_0042u;
        var cache = new PhysicsDataCache();
        var source = new RecordingSource();
        var publisher = new LiveCollisionAssetPublisher(cache, source);
        GfxObj gfx = PhysicsGfx();
        var setup = new Setup();

        publisher.CacheGfxObj(GfxId, gfx);
        publisher.CacheGfxObj(GfxId, gfx);
        publisher.CacheSetup(SetupId, setup);
        publisher.CacheSetup(SetupId, setup);

        Assert.Same(source.Gfx, cache.GetFlatGfxObj(GfxId));
        Assert.Same(
            source.Gfx.PhysicsBsp,
            cache.GetGfxObj(GfxId)!.FlatPhysicsBsp);
        Assert.Same(source.Setup, cache.GetFlatSetup(SetupId));
        Assert.Same(
            source.Setup,
            cache.GetSetup(SetupId)!.FlatCollision);
        Assert.Equal(1, source.GfxReads);
        Assert.Equal(1, source.SetupReads);
    }

    [Fact]
    public void VisualOnlyGfx_RemembersPreparedEmptyAssetWithoutGraphRecord()
    {
        const uint GfxId = 0x0100_0043u;
        var cache = new PhysicsDataCache();
        var source = new RecordingSource();
        var publisher = new LiveCollisionAssetPublisher(cache, source);
        var gfx = new GfxObj();

        publisher.CacheGfxObj(GfxId, gfx);
        publisher.CacheGfxObj(GfxId, gfx);

        Assert.Null(cache.GetGfxObj(GfxId));
        Assert.Same(source.Gfx, cache.GetFlatGfxObj(GfxId));
        Assert.Equal(1, source.GfxReads);
    }

    [Fact]
    public void MissingPreparedAsset_RejectsGraphOnlyLivePublication()
    {
        var cache = new PhysicsDataCache();
        var source = new RecordingSource
        {
            GfxResult =
                PreparedCollisionReadResult<FlatGfxObjCollisionAsset>
                    .Missing,
        };
        var publisher = new LiveCollisionAssetPublisher(cache, source);

        InvalidDataException fault = Assert.Throws<InvalidDataException>(
            () => publisher.CacheGfxObj(0x0100_0044u, PhysicsGfx()));

        Assert.Contains("Missing", fault.Message, StringComparison.Ordinal);
        Assert.Equal(0, cache.GfxObjCount);
        Assert.Equal(0, cache.FlatGfxObjCount);
    }

    private static GfxObj PhysicsGfx() => new()
    {
        Flags = GfxObjFlags.HasPhysics,
        PhysicsBSP = new PhysicsBSPTree
        {
            Root = new PhysicsBSPNode
            {
                Type = BSPNodeType.Leaf,
            },
        },
        VertexArray = new VertexArray(),
        PhysicsPolygons = new Dictionary<ushort, Polygon>(),
    };

    private sealed class RecordingSource : IPreparedCollisionSource
    {
        private static readonly FlatPhysicsBsp EmptyPhysics = new(
            -1,
            ImmutableArray<FlatPhysicsBspNode>.Empty,
            ImmutableArray<int>.Empty,
            FlatPolygonTable.Empty);

        public FlatGfxObjCollisionAsset Gfx { get; } =
            new(EmptyPhysics, null, null);
        public FlatSetupCollision Setup { get; } = new(
            ImmutableArray<FlatCollisionCylinder>.Empty,
            ImmutableArray<FlatCollisionSphere>.Empty,
            0f,
            0f,
            0f,
            0f);
        public int GfxReads { get; private set; }
        public int SetupReads { get; private set; }

        public PreparedCollisionReadResult<FlatGfxObjCollisionAsset>?
            GfxResult { get; init; }

        public PreparedAssetPresence ProbeCollision(
            PakAssetType type,
            uint sourceFileId) => PreparedAssetPresence.Available;

        public PreparedCollisionReadResult<FlatGfxObjCollisionAsset>
            ReadGfxObjCollision(
                uint sourceFileId,
                CancellationToken cancellationToken = default)
        {
            GfxReads++;
            return GfxResult ??
                PreparedCollisionReadResult<FlatGfxObjCollisionAsset>
                    .Loaded(Gfx);
        }

        public PreparedCollisionReadResult<FlatSetupCollision>
            ReadSetupCollision(
                uint sourceFileId,
                CancellationToken cancellationToken = default)
        {
            SetupReads++;
            return PreparedCollisionReadResult<FlatSetupCollision>
                .Loaded(Setup);
        }

        public PreparedCollisionReadResult<FlatCellStructureCollisionAsset>
            ReadCellStructureCollision(
                uint sourceFileId,
                CancellationToken cancellationToken = default) =>
            PreparedCollisionReadResult<FlatCellStructureCollisionAsset>
                .Missing;

        public PreparedCollisionReadResult<FlatEnvCellTopology>
            ReadEnvCellTopology(
                uint sourceFileId,
                CancellationToken cancellationToken = default) =>
            PreparedCollisionReadResult<FlatEnvCellTopology>.Missing;

        public PreparedCollisionSourceStats CollisionStats => default;

        public void Dispose()
        {
        }
    }
}
