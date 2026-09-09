using System.Numerics;
using AcDream.App.Rendering.Scene;
using AcDream.App.Rendering.Scene.Arch;
using AcDream.App.Rendering.Walk;
using AcDream.Core.Physics;
using AcDream.Core.World;

namespace AcDream.App.Tests.Rendering.Walk;

public sealed class WalkProductionWorldDataTests
{
    [Fact]
    public void BuildingShellBucketCellId_PortalLessBuildingUsesLandscapePositionCell()
    {
        const uint positionCellId = 0xF4180011u;
        RenderProjectionRecord record = Record(
            id: 0xCF418060u,
            position: new Vector3(53.57f, 13.874f, 160f)) with
        {
            Source = new RenderSourceMetadata() with
            {
                LocalEntityId = 0xCF418060u,
                SourceId = 0x01001FD3u,
                EffectCellId = positionCellId,
                BuildingShellAnchorCellId = 0,
            },
            EntityPayload = new RenderEntityPayload() with
            {
                IsBuildingShell = true,
            },
        };
        var building = new WalkBuilding
        {
            PositionCellId = positionCellId,
            Portals = [],
        };

        Assert.Equal(
            positionCellId,
            WalkProductionWorldData.BuildingShellBucketCellId(in record));
        Assert.Equal(
            positionCellId,
            WalkProductionWorldData.BuildingShellBucketCellId(building));
    }

    [Fact]
    public void BuildingShellBucketCellId_PortalBearingBuildingKeepsInteriorAnchor()
    {
        const uint anchorCellId = 0xF4180112u;
        RenderProjectionRecord record = Record(
            id: 0xCF41805Fu,
            position: new Vector3(36f, 13.8349f, 160f)) with
        {
            Source = new RenderSourceMetadata() with
            {
                LocalEntityId = 0xCF41805Fu,
                SourceId = 0x01001FB7u,
                EffectCellId = 0xF4180009u,
                BuildingShellAnchorCellId = anchorCellId,
            },
            EntityPayload = new RenderEntityPayload() with
            {
                IsBuildingShell = true,
            },
        };
        var building = new WalkBuilding
        {
            PositionCellId = 0xF4180009u,
            Portals =
            [
                new WalkBldPortal { OtherCellId = anchorCellId },
            ],
        };

        Assert.Equal(
            anchorCellId,
            WalkProductionWorldData.BuildingShellBucketCellId(in record));
        Assert.Equal(
            anchorCellId,
            WalkProductionWorldData.BuildingShellBucketCellId(building));
    }

    private static ShadowShape Bsp(uint gfxObjId, float radius = 1f) =>
        ShadowShape.Bsp(
            gfxObjId,
            Vector3.Zero,
            Quaternion.Identity,
            scale: 1f,
            localGeometry: ShadowPartGeometry.Create(
                new FlatCollisionSphere(Vector3.Zero, radius), null));

    private static RenderProjectionRecord IndoorStaticRecord(
        uint entityId, uint sourceId, uint parentCellId) =>
        new RenderProjectionRecord() with
        {
            Id = RenderProjectionId.FromRaw(entityId),
            ProjectionClass = RenderProjectionClass.IndoorCellStatic,
            OwnerIncarnation = RenderOwnerIncarnation.FromRaw(1),
            Transform = new RenderTransform(Matrix4x4.Identity),
            PreviousTransform = new PreviousRenderTransform(Matrix4x4.Identity),
            Residency = new RenderSpatialResidency(
                RenderSpatialBucket.FromRaw(parentCellId), 0x8A020000u, parentCellId),
            Flags = RenderProjectionFlags.Draw,
            Source = new RenderSourceMetadata() with
            {
                LocalEntityId = entityId,
                SourceId = sourceId,
                ParentCellId = parentCellId,
            },
            EntityPayload = new RenderEntityPayload() with
            {
                MeshRefs = [new MeshRef(sourceId, Matrix4x4.Identity)],
            },
        };

    private static RenderProjectionRecord DynamicRecord(
        uint entityId, uint sourceId, uint parentCellId) =>
        IndoorStaticRecord(entityId, sourceId, parentCellId) with
        {
            Id = RenderProjectionId.FromRaw(0x0100_0000_0000_0000u | entityId),
            ProjectionClass = RenderProjectionClass.LiveDynamicRoot,
        };

    private static RenderProjectionRecord OutdoorStaticRecord(
        uint entityId, uint sourceId, uint cellId, bool isBuildingShell = false) =>
        new RenderProjectionRecord() with
        {
            Id = RenderProjectionId.FromRaw(0x0200_0000_0000_0000u | entityId),
            ProjectionClass = RenderProjectionClass.OutdoorStatic,
            OwnerIncarnation = RenderOwnerIncarnation.FromRaw(1),
            Transform = new RenderTransform(Matrix4x4.Identity),
            PreviousTransform = new PreviousRenderTransform(Matrix4x4.Identity),
            Residency = new RenderSpatialResidency(
                RenderSpatialBucket.FromRaw(cellId), cellId & 0xFFFF0000u, cellId),
            Flags = RenderProjectionFlags.Draw,
            Source = new RenderSourceMetadata() with
            {
                LocalEntityId = entityId,
                SourceId = sourceId,
                ParentCellId = cellId,
            },
            EntityPayload = new RenderEntityPayload() with
            {
                IsBuildingShell = isBuildingShell,
            },
        };

    private static void Register(
        ShadowObjectRegistry shadows,
        uint entityId,
        uint seedCellId,
        uint landblockId)
    {
        IReadOnlyList<ShadowShape> parts = new[] { Bsp(0x01001234u) };
        shadows.RegisterMultiPart(
            entityId,
            Vector3.Zero,
            Quaternion.Identity,
            parts,
            state: 0u,
            flags: EntityCollisionFlags.None,
            worldOffsetX: 0f,
            worldOffsetY: 0f,
            landblockId: landblockId,
            seedCellId: seedCellId,
            isStatic: true,
            partArray: parts);
    }

    [Fact]
    public void GetCellStatics_RegisteredEntityIsResolvedByLocalEntityIdAtTheRetailCell()
    {
        const uint entityId = 0x48A02001u;
        const uint retailCellId = 0x8A02015Fu;
        const uint decoyParentCellId = 0x8A0201C1u;

        var shadows = new ShadowObjectRegistry();
        Register(shadows, entityId, retailCellId, landblockId: 0x8A020000u);
        Assert.True(shadows.TryGetRetailCellArray(entityId, out IReadOnlyList<uint> retailCells));
        Assert.Equal(new[] { retailCellId }, retailCells);

        RenderSceneGeneration generation = RenderSceneGeneration.FromRaw(1);
        using var scene = new ArchRenderScene(generation);
        RenderProjectionRecord projection =
            IndoorStaticRecord(entityId, sourceId: 0x02000001u, decoyParentCellId);
        scene.Apply([RenderProjectionDelta.Register(generation, 1, projection)]);

        var worldData = new WalkProductionWorldData(new WalkBuildingRegistry(), shadows);
        worldData.BeginFrame(
            scene.OpenQuery(), 0x8A020000u, renderCenterLbX: 0x8A, renderCenterLbY: 0x02);

        Assert.Contains(
            worldData.GetCellStatics(retailCellId).Records,
            record => record.Id == projection.Id);
        Assert.DoesNotContain(
            worldData.GetCellStatics(decoyParentCellId).Records,
            record => record.Id == projection.Id);
        Assert.Equal(0, worldData.UnregisteredRenderMembershipCount);
    }

    [Fact]
    public void GetCellStatics_RegistryAheadOfSceneContributesToNoCellAndCountsFallback()
    {
        const uint entityId = 0x48A02002u;
        const uint retailCellId = 0x8A02015Fu;

        var shadows = new ShadowObjectRegistry();
        Register(shadows, entityId, retailCellId, landblockId: 0x8A020000u);
        Assert.True(shadows.TryGetRetailCellArray(entityId, out _));

        RenderSceneGeneration generation = RenderSceneGeneration.FromRaw(1);
        using var scene = new ArchRenderScene(generation);
        // Deliberately no scene.Apply — the projected record does not exist
        // yet this frame.

        var worldData = new WalkProductionWorldData(new WalkBuildingRegistry(), shadows);
        worldData.BeginFrame(
            scene.OpenQuery(), 0x8A020000u, renderCenterLbX: 0x8A, renderCenterLbY: 0x02);

        Assert.Equal(0, worldData.GetCellStatics(retailCellId).Records.Count);
        Assert.Equal(1, worldData.UnregisteredRenderMembershipCount);
    }

    [Fact]
    public void GetCellStatics_UnregisteredEntityContributesToNoCellWithoutCounting()
    {
        const uint entityId = 0x48A02003u;
        const uint parentCellId = 0x8A02015Fu;

        var shadows = new ShadowObjectRegistry();
        Assert.False(shadows.TryGetRetailCellArray(entityId, out _));

        RenderSceneGeneration generation = RenderSceneGeneration.FromRaw(1);
        using var scene = new ArchRenderScene(generation);
        RenderProjectionRecord projection =
            IndoorStaticRecord(entityId, sourceId: 0x02000003u, parentCellId);
        scene.Apply([RenderProjectionDelta.Register(generation, 1, projection)]);

        var worldData = new WalkProductionWorldData(new WalkBuildingRegistry(), shadows);
        worldData.BeginFrame(
            scene.OpenQuery(), 0x8A020000u, renderCenterLbX: 0x8A, renderCenterLbY: 0x02);

        Assert.Equal(0, worldData.GetCellStatics(parentCellId).Records.Count);
        Assert.Equal(0, worldData.UnregisteredRenderMembershipCount);
    }

    [Fact]
    public void UnregisteredRenderMembershipCount_CountsDistinctEntitiesNotCellVisits()
    {
        const uint entityId = 0x87640002u;
        const uint seedCellId = 0x87640030u;

        IReadOnlyList<ShadowShape> parts = new[] { Bsp(0x01001234u) };
        var shadows = new ShadowObjectRegistry();
        shadows.RegisterMultiPart(
            entityId,
            Vector3.Zero,
            Quaternion.Identity,
            parts,
            state: 0u,
            flags: EntityCollisionFlags.None,
            worldOffsetX: 0f,
            worldOffsetY: 0f,
            landblockId: 0x87640000u,
            seedCellId: seedCellId,
            isStatic: true,
            partArray: parts);
        Assert.True(shadows.TryGetRetailCellArray(entityId, out IReadOnlyList<uint> cells));
        Assert.True(cells.Count >= 2, "fixture must actually cross more than one cell");

        RenderSceneGeneration generation = RenderSceneGeneration.FromRaw(1);
        using var scene = new ArchRenderScene(generation);

        var worldData = new WalkProductionWorldData(new WalkBuildingRegistry(), shadows);
        worldData.BeginFrame(
            scene.OpenQuery(), 0x87640000u, renderCenterLbX: 0x87, renderCenterLbY: 0x64);

        foreach (uint cellId in cells)
            Assert.Equal(0, worldData.GetOutdoorStatics(cellId).Records.Count);

        Assert.Equal(1, worldData.UnregisteredRenderMembershipCount);
    }

    [Fact]
    public void GetCellDynamics_OnlyReturnsDynamicClassRecordsFromTheSameCell()
    {
        const uint staticEntityId = 0x48A02010u;
        const uint dynamicEntityId = 0x48A02011u;
        const uint sharedCellId = 0x8A02015Fu;

        var shadows = new ShadowObjectRegistry();
        Register(shadows, staticEntityId, sharedCellId, landblockId: 0x8A020000u);
        Register(shadows, dynamicEntityId, sharedCellId, landblockId: 0x8A020000u);

        RenderSceneGeneration generation = RenderSceneGeneration.FromRaw(1);
        using var scene = new ArchRenderScene(generation);
        RenderProjectionRecord staticProjection =
            IndoorStaticRecord(staticEntityId, sourceId: 0x02000010u, sharedCellId);
        RenderProjectionRecord dynamicProjection =
            DynamicRecord(dynamicEntityId, sourceId: 0x02000011u, sharedCellId);
        scene.Apply([
            RenderProjectionDelta.Register(generation, 1, staticProjection),
            RenderProjectionDelta.Register(generation, 2, dynamicProjection),
        ]);

        var worldData = new WalkProductionWorldData(new WalkBuildingRegistry(), shadows);
        worldData.BeginFrame(
            scene.OpenQuery(), 0x8A020000u, renderCenterLbX: 0x8A, renderCenterLbY: 0x02);

        Assert.Contains(
            worldData.GetCellStatics(sharedCellId).Records,
            record => record.Id == staticProjection.Id);
        Assert.DoesNotContain(
            worldData.GetCellStatics(sharedCellId).Records,
            record => record.Id == dynamicProjection.Id);

        Assert.Contains(
            worldData.GetCellDynamics(sharedCellId).Records,
            record => record.Id == dynamicProjection.Id);
        Assert.DoesNotContain(
            worldData.GetCellDynamics(sharedCellId).Records,
            record => record.Id == staticProjection.Id);
    }

    [Fact]
    public void GetCellStatics_ExcludesBuildingShellRecordsFromTheSameCell()
    {
        const uint shellEntityId = 0x48A02020u;
        const uint ordinaryEntityId = 0x48A02021u;
        const uint sharedCellId = 0x8A02015Fu;

        var shadows = new ShadowObjectRegistry();
        Register(shadows, shellEntityId, sharedCellId, landblockId: 0x8A020000u);
        Register(shadows, ordinaryEntityId, sharedCellId, landblockId: 0x8A020000u);

        RenderSceneGeneration generation = RenderSceneGeneration.FromRaw(1);
        using var scene = new ArchRenderScene(generation);
        RenderProjectionRecord shellProjection =
            IndoorStaticRecord(shellEntityId, sourceId: 0x02000020u, sharedCellId) with
            {
                EntityPayload = new RenderEntityPayload() with { IsBuildingShell = true },
            };
        RenderProjectionRecord ordinaryProjection =
            IndoorStaticRecord(ordinaryEntityId, sourceId: 0x02000021u, sharedCellId);
        scene.Apply([
            RenderProjectionDelta.Register(generation, 1, shellProjection),
            RenderProjectionDelta.Register(generation, 2, ordinaryProjection),
        ]);

        var worldData = new WalkProductionWorldData(new WalkBuildingRegistry(), shadows);
        worldData.BeginFrame(
            scene.OpenQuery(), 0x8A020000u, renderCenterLbX: 0x8A, renderCenterLbY: 0x02);

        Assert.DoesNotContain(
            worldData.GetCellStatics(sharedCellId).Records,
            record => record.Id == shellProjection.Id);
        Assert.Contains(
            worldData.GetCellStatics(sharedCellId).Records,
            record => record.Id == ordinaryProjection.Id);
    }

    [Fact]
    public void GetOutdoorStatics_UsesTheSameRegistryDrivenViewAsIndoor()
    {
        const uint entityId = 0x87640001u;
        const uint outdoorCellId = 0x87640030u;

        var shadows = new ShadowObjectRegistry();
        Register(shadows, entityId, outdoorCellId, landblockId: 0x87640000u);
        Assert.True(shadows.TryGetRetailCellArray(entityId, out IReadOnlyList<uint> retailCells));
        Assert.Contains(outdoorCellId, retailCells);

        RenderSceneGeneration generation = RenderSceneGeneration.FromRaw(1);
        using var scene = new ArchRenderScene(generation);
        RenderProjectionRecord projection =
            OutdoorStaticRecord(entityId, sourceId: 0x02000030u, outdoorCellId);
        scene.Apply([RenderProjectionDelta.Register(generation, 1, projection)]);

        var worldData = new WalkProductionWorldData(new WalkBuildingRegistry(), shadows);
        worldData.BeginFrame(
            scene.OpenQuery(), 0x87640000u, renderCenterLbX: 0x87, renderCenterLbY: 0x64);

        Assert.Contains(
            worldData.GetOutdoorStatics(outdoorCellId).Records,
            record => record.Id == projection.Id);
    }

    [Fact]
    public void GetCellStatics_CachesTheResultForTheRestOfTheFrame()
    {
        const uint entityId = 0x48A02030u;
        const uint retailCellId = 0x8A02015Fu;

        var shadows = new ShadowObjectRegistry();
        Register(shadows, entityId, retailCellId, landblockId: 0x8A020000u);

        RenderSceneGeneration generation = RenderSceneGeneration.FromRaw(1);
        using var scene = new ArchRenderScene(generation);
        RenderProjectionRecord projection =
            IndoorStaticRecord(entityId, sourceId: 0x02000030u, retailCellId);
        scene.Apply([RenderProjectionDelta.Register(generation, 1, projection)]);

        var worldData = new WalkProductionWorldData(new WalkBuildingRegistry(), shadows);
        worldData.BeginFrame(
            scene.OpenQuery(), 0x8A020000u, renderCenterLbX: 0x8A, renderCenterLbY: 0x02);

        WalkFrameStaticRecords first = worldData.GetCellStatics(retailCellId);
        WalkFrameStaticRecords second = worldData.GetCellStatics(retailCellId);

        Assert.Equal(first.Records.Array, second.Records.Array);
        Assert.Equal(first.Records.Offset, second.Records.Offset);
        Assert.Equal(first.Records.Count, second.Records.Count);
    }

    private static RenderProjectionRecord Record(uint id, Vector3 position) =>
        new RenderProjectionRecord() with
        {
            Id = RenderProjectionId.FromRaw(id),
            Transform = new RenderTransform(Matrix4x4.CreateTranslation(position)),
            Source = new RenderSourceMetadata() with { LocalEntityId = id },
        };
}
