using System.Numerics;
using AcDream.App.Rendering.Wb;
using AcDream.Core.World;

namespace AcDream.App.Tests.Rendering.Wb;

public sealed class WbDrawDispatcherCompositeWarmupTests
{
    private const uint Destination = 0x1234FFFFu;

    [Fact]
    public void NewlyPublishedEntitySnapshotRequiresRebuildAfterPriorSnapshotWasReady()
    {
        IReadOnlyList<WorldEntity> emptyReadySnapshot = Array.Empty<WorldEntity>();
        IReadOnlyList<WorldEntity> publishedDestinationSnapshot =
            [CreateEntity(parentCell: Destination, withPalette: true)];

        Assert.True(WbDrawDispatcher.RequiresCompositeWarmupRebuild(
            emptyReadySnapshot,
            Destination,
            currentRadius: 0,
            publishedDestinationSnapshot,
            Destination,
            nextRadius: 0));
    }

    [Fact]
    public void MutatedStableEntityViewRetainsScopeWhenGenerationChanges()
    {
        IReadOnlyList<WorldEntity> stableView = new List<WorldEntity>();

        Assert.False(WbDrawDispatcher.RequiresCompositeWarmupRebuild(
            stableView,
            Destination,
            currentRadius: 0,
            stableView,
            Destination,
            nextRadius: 0));
    }

    [Fact]
    public void MembershipMutationDoesNotRestartAnIncompletePass()
    {
        Assert.False(WbDrawDispatcher.ShouldBeginCompositeWarmupRescan(
            scanComplete: false,
            scanGeneration: 41,
            entityGeneration: 42));
    }

    [Fact]
    public void MembershipMutationSchedulesFollowUpAfterPassCompletes()
    {
        Assert.True(WbDrawDispatcher.ShouldBeginCompositeWarmupRescan(
            scanComplete: true,
            scanGeneration: 41,
            entityGeneration: 42));
    }

    [Fact]
    public void LargeRetainedWorldDiscoveryConvergesIndependentlyOfUploadBudget()
    {
        const int entityCount = 21_025;
        int scanIndex = 0;
        int frameCount = 0;

        while (scanIndex < entityCount)
        {
            scanIndex = WbDrawDispatcher.CompositeWarmupScanEnd(
                scanIndex,
                entityCount);
            frameCount++;
        }

        Assert.Equal(6, frameCount);
        Assert.Equal(entityCount, scanIndex);
        Assert.True(
            WbDrawDispatcher.MaximumCompositeWarmupScanEntitiesPerFrame
            > WbDrawDispatcher.MaximumCompositeWarmupPrepareEntitiesPerFrame);
    }

    [Fact]
    public void PaletteEntityInsideDestinationRadiusIsCandidate()
    {
        WorldEntity entity = CreateEntity(parentCell: 0x1235FFFEu, withPalette: true);

        Assert.True(WbDrawDispatcher.IsCompositeWarmupCandidate(entity, Destination, radius: 1));
    }

    [Fact]
    public void EntityOutsideDestinationRadiusIsExcluded()
    {
        WorldEntity entity = CreateEntity(parentCell: 0x1236FFFEu, withPalette: true);

        Assert.False(WbDrawDispatcher.IsCompositeWarmupCandidate(entity, Destination, radius: 1));
    }

    [Fact]
    public void CellLessEntityIsExcludedFromKnownDestination()
    {
        WorldEntity entity = CreateEntity(parentCell: null, withPalette: true);

        Assert.False(WbDrawDispatcher.IsCompositeWarmupCandidate(entity, Destination, radius: 1));
    }

    [Fact]
    public void SameLandblockIndoorCellIsCandidateAtZeroRadius()
    {
        WorldEntity entity = CreateEntity(parentCell: 0x1234012Au, withPalette: true);

        Assert.True(WbDrawDispatcher.IsCompositeWarmupCandidate(entity, Destination, radius: 0));
    }

    [Fact]
    public void EntityWithoutPaletteOrSurfaceOverridesIsExcluded()
    {
        WorldEntity entity = CreateEntity(parentCell: Destination, withPalette: false);

        Assert.False(WbDrawDispatcher.IsCompositeWarmupCandidate(entity, Destination, radius: 0));
    }

    [Fact]
    public void SurfaceOverrideEntityIsCandidateWithoutPalette()
    {
        WorldEntity entity = CreateEntity(parentCell: Destination, withPalette: false);
        entity.MeshRefs =
        [
            new MeshRef(0x01000001u, Matrix4x4.Identity)
            {
                SurfaceOverrides = new Dictionary<uint, uint> { [0x08000001u] = 0x05000001u },
            },
        ];

        Assert.True(WbDrawDispatcher.IsCompositeWarmupCandidate(entity, Destination, radius: 0));
    }

    private static WorldEntity CreateEntity(uint? parentCell, bool withPalette) => new()
    {
        Id = 1,
        SourceGfxObjOrSetupId = 0x01000001u,
        Position = Vector3.Zero,
        Rotation = Quaternion.Identity,
        MeshRefs = [new MeshRef(0x01000001u, Matrix4x4.Identity)],
        ParentCellId = parentCell,
        PaletteOverride = withPalette
            ? new PaletteOverride(0x04000001u, Array.Empty<PaletteOverride.SubPaletteRange>())
            : null,
    };
}
