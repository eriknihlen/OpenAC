using System.Numerics;
using AcDream.App.Rendering.Scene;
using AcDream.App.Streaming;
using AcDream.Core.Physics;
using AcDream.Core.World;
using DatReaderWriter.DBObjs;

namespace AcDream.App.Tests.Streaming;

public sealed class GpuWorldStateRenderTraversalTests
{
    private const uint FirstLandblock = 0x1234FFFFu;
    private const uint SecondLandblock = 0x1235FFFFu;
    private const uint ReusedLandblock = 0x1236FFFFu;

    [Fact]
    public void TraversalKeys_MirrorResidentLandblockAndMutableBucketOrder()
    {
        var state = new GpuWorldState();
        WorldEntity first = Entity(1, 0x80000001u);
        WorldEntity moved = Entity(2, 0x80000002u);
        WorldEntity secondLandblockEntity = Entity(3, 0x80000003u);
        state.AddLandblock(Landblock(FirstLandblock, first, moved));
        state.AddLandblock(Landblock(
            SecondLandblock,
            secondLandblockEntity));

        AssertSort(state, first, landblockOrder: 1, entityIndex: 0);
        AssertSort(state, moved, landblockOrder: 1, entityIndex: 1);
        AssertSort(
            state,
            secondLandblockEntity,
            landblockOrder: 2,
            entityIndex: 0);

        state.RemoveLiveEntityProjection(first);

        AssertSort(state, moved, landblockOrder: 1, entityIndex: 0);

        WorldEntity replacement = Entity(4, 0x80000004u);
        state.AddLandblock(Landblock(SecondLandblock, replacement));

        Assert.False(state.TryGetRenderTraversalSortKey(
            secondLandblockEntity,
            out _));
        AssertSort(state, replacement, landblockOrder: 2, entityIndex: 0);

        state.RemoveLandblock(FirstLandblock);
        WorldEntity reused = Entity(5, 0x80000005u);
        state.AddLandblock(Landblock(ReusedLandblock, reused));

        AssertSort(state, reused, landblockOrder: 1, entityIndex: 0);
        Assert.Equal(
            [ReusedLandblock, SecondLandblock],
            state.LandblockEntriesWithoutAnimatedIndex
                .Select(static entry => entry.LandblockId)
                .ToArray());
    }

    [Fact]
    public void TraversalKey_IsUnavailableWhileProjectionIsPending()
    {
        var state = new GpuWorldState();
        WorldEntity pending = Entity(1, 0x80000001u);

        state.PlaceLiveEntityProjection(FirstLandblock, pending);

        Assert.False(state.TryGetRenderTraversalSortKey(pending, out _));
    }

    [Fact]
    public void StableRenderViews_AllocateOnlyWhenSpatialContentChanges()
    {
        var state = new GpuWorldState();
        state.AddLandblock(Landblock(
            FirstLandblock,
            Entity(1, 0x80000001u)));
        state.SetLandblockAabb(
            FirstLandblock,
            new Vector3(1f, 2f, 3f),
            new Vector3(4f, 5f, 6f));

        var entries = state.LandblockEntries;
        var bounds = state.LandblockBounds;
        _ = entries.Count;
        _ = bounds.Count;
        ZeroAllocationProbe.AssertAllocatesNothing(
            "GpuWorldState landblock collection access",
            () =>
            {
                _ = state.LandblockEntries.Count;
                _ = state.LandblockBounds.Count;
            });

        Assert.Same(entries, state.LandblockEntries);
        Assert.Same(bounds, state.LandblockBounds);

        state.AddLandblock(Landblock(
            SecondLandblock,
            Entity(2, 0x80000002u)));

        Assert.Same(entries, state.LandblockEntries);
        Assert.Same(bounds, state.LandblockBounds);
        Assert.Equal(2, entries.Count);
        Assert.Equal(2, bounds.Count);
    }

    private static void AssertSort(
        GpuWorldState state,
        WorldEntity entity,
        uint landblockOrder,
        int entityIndex)
    {
        Assert.True(state.TryGetRenderTraversalSortKey(
            entity,
            out RenderSortKey actual));
        Assert.Equal(
            RenderTraversalSortKey.Compose(landblockOrder, entityIndex),
            actual);
    }

    private static LoadedLandblock Landblock(
        uint landblockId,
        params WorldEntity[] entities) =>
        new(
            landblockId,
            new LandBlock(),
            entities,
            PhysicsDatBundle.Empty);

    private static WorldEntity Entity(uint id, uint serverGuid) =>
        new()
        {
            Id = id,
            ServerGuid = serverGuid,
            SourceGfxObjOrSetupId = 0x02000001u,
            Position = Vector3.Zero,
            Rotation = Quaternion.Identity,
            MeshRefs =
            [
                new MeshRef(0x01000001u, Matrix4x4.Identity),
            ],
        };
}
