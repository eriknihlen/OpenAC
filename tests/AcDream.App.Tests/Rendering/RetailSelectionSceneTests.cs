using System.Numerics;
using AcDream.App.Rendering;
using AcDream.App.Rendering.Scene;
using AcDream.App.Rendering.Selection;
using AcDream.Core.Selection;
using AcDream.Core.World;

namespace AcDream.App.Tests.Rendering;

public sealed class RetailSelectionSceneTests
{
    private sealed class GeometrySource(RetailSelectionMesh mesh)
        : IRetailSelectionGeometrySource
    {
        public RetailSelectionMesh? Resolve(uint gfxObjId)
            => gfxObjId == 0x0100_0001u ? mesh : null;
    }

    [Fact]
    public void PublishedHitCarriesLogicalEntityIdentity()
    {
        var scene = CreateScene();
        var entity = Entity(localId: 44u, serverGuid: 0x5000_0044u);
        Publish(scene, entity);

        RetailSelectionHit? hit = PickCenter(scene);

        Assert.NotNull(hit);
        Assert.Equal(entity.ServerGuid, hit.Value.ServerGuid);
        Assert.Equal(entity.Id, hit.Value.LocalEntityId);
    }

    [Fact]
    public void ResetClearsPublishedFrameAndLightingPulse()
    {
        double now = 1d;
        var pulse = new RetailSelectionLightingPulse(() => now);
        var scene = CreateScene(pulse);
        var entity = Entity(localId: 45u, serverGuid: 0x5000_0045u);
        Publish(scene, entity);
        scene.BeginLightingPulse(entity.ServerGuid, entity.Id);

        scene.Reset();

        Assert.Null(PickCenter(scene));
        Assert.False(scene.TryGetLighting(entity.ServerGuid, entity.Id, out _));
    }

    [Fact]
    public void AbortFrame_DiscardsBuildingSelectionAndPreservesPublishedFrame()
    {
        var scene = CreateScene();
        var published = Entity(localId: 46u, serverGuid: 0x5000_0046u);
        Publish(scene, published);
        var rejected = Entity(localId: 47u, serverGuid: 0x5000_0047u);

        scene.BeginFrame();
        (Matrix4x4 view, Matrix4x4 projection) = Camera();
        scene.SetViewFrustum(FrustumPlanes.FromViewProjection(view * projection));
        scene.AddVisiblePart(
            rejected,
            partIndex: 0,
            gfxObjId: 0x0100_0001u,
            Matrix4x4.CreateTranslation(0f, 0f, -5f));
        scene.AbortFrame();

        RetailSelectionHit? hit = PickCenter(scene);
        Assert.NotNull(hit);
        Assert.Equal(published.Id, hit.Value.LocalEntityId);
    }

    [Fact]
    public void CurrentPathRefereeRecordsOnlyAcceptedSelectionParts()
    {
        var oracle = new CurrentRenderSceneOracle();
        var scene = CreateScene();
        scene.SetCurrentRenderSceneObserver(oracle);
        var entity = Entity(localId: 48u, serverGuid: 0x5000_0048u);

        Publish(scene, entity);

        Assert.Equal(1uL, oracle.Snapshot.CompletedSelectionFrameSequence);
        Assert.Equal(1, oracle.Snapshot.SelectionPartCount);
        CurrentRenderSelectionFingerprint part =
            Assert.Single(oracle.SelectionParts);
        Assert.Equal(entity.ServerGuid, part.ServerGuid);
        Assert.Equal(entity.Id, part.LocalEntityId);
        Assert.Equal(0, part.PartIndex);
        Assert.Equal(0x0100_0001u, part.GfxObjId);

        scene.BeginFrame();
        scene.AbortFrame();
        Assert.Equal(1, oracle.Snapshot.AbortedSelectionFrames);
        Assert.Empty(oracle.SelectionParts);
    }

    [Fact]
    public void PrivateViewportPartsOutsideWorldFrame_DoNotEnterPickingOrReferee()
    {
        var oracle = new CurrentRenderSceneOracle();
        var scene = CreateScene();
        scene.SetCurrentRenderSceneObserver(oracle);
        var world = Entity(localId: 49u, serverGuid: 0x5000_0049u);
        var privateViewport =
            Entity(localId: 50u, serverGuid: 0x5000_0050u);
        Publish(scene, world);

        scene.AddVisiblePart(
            privateViewport,
            partIndex: 0,
            gfxObjId: 0x0100_0001u,
            Matrix4x4.CreateTranslation(0f, 0f, -5f));

        RetailSelectionHit? hit = PickCenter(scene);
        Assert.NotNull(hit);
        Assert.Equal(world.Id, hit.Value.LocalEntityId);
        CurrentRenderSelectionFingerprint fingerprint =
            Assert.Single(oracle.SelectionParts);
        Assert.Equal(world.Id, fingerprint.LocalEntityId);
        Assert.Equal(1, oracle.Snapshot.SelectionPartCount);
    }

    private static RetailSelectionScene CreateScene(
        RetailSelectionLightingPulse? pulse = null)
    {
        var mesh = new RetailSelectionMesh(
            Vector3.Zero,
            2f,
            [new RetailSelectionPolygon(
                [
                    new(-1f, -1f, 0f),
                    new( 1f, -1f, 0f),
                    new( 1f,  1f, 0f),
                    new(-1f,  1f, 0f),
                ],
                SingleSided: false)]);
        return new RetailSelectionScene(new GeometrySource(mesh), pulse);
    }

    private static WorldEntity Entity(uint localId, uint serverGuid)
        => new()
        {
            Id = localId,
            ServerGuid = serverGuid,
            SourceGfxObjOrSetupId = 0x0200_0001u,
            Position = Vector3.Zero,
            Rotation = Quaternion.Identity,
            MeshRefs = [],
        };

    private static void Publish(RetailSelectionScene scene, WorldEntity entity)
    {
        scene.BeginFrame();
        (Matrix4x4 view, Matrix4x4 projection) = Camera();
        scene.SetViewFrustum(FrustumPlanes.FromViewProjection(view * projection));
        scene.AddVisiblePart(
            entity,
            partIndex: 0,
            gfxObjId: 0x0100_0001u,
            Matrix4x4.CreateTranslation(0f, 0f, -5f));
        scene.CompleteFrame();
    }

    private static RetailSelectionHit? PickCenter(RetailSelectionScene scene)
    {
        (Matrix4x4 view, Matrix4x4 projection) = Camera();
        return scene.Pick(
            400f,
            300f,
            new Vector2(800f, 600f),
            view,
            projection,
            skipServerGuid: 0u);
    }

    private static (Matrix4x4 View, Matrix4x4 Projection) Camera()
    {
        Matrix4x4 view = Matrix4x4.CreateLookAt(
            Vector3.Zero,
            -Vector3.UnitZ,
            Vector3.UnitY);
        Matrix4x4 projection = Matrix4x4.CreatePerspectiveFieldOfView(
            MathF.PI / 3f,
            4f / 3f,
            0.1f,
            100f);
        return (view, projection);
    }
}
