using System.Numerics;
using AcDream.Core.Selection;

namespace AcDream.Core.Tests.Selection;

public sealed class RetailWorldPickerTests
{
    [Fact]
    public void BuildRayStartsAtCameraViewpointRatherThanNearPlane()
    {
        var eye = new Vector3(10f, 20f, 30f);
        Matrix4x4 view = Matrix4x4.CreateLookAt(eye, eye - Vector3.UnitZ, Vector3.UnitY);
        Matrix4x4 projection = Matrix4x4.CreatePerspectiveFieldOfView(
            MathF.PI / 3f, 4f / 3f, 0.1f, 1000f);

        var ray = WorldPicker.BuildRay(400f, 300f, 800f, 600f, view, projection);

        Assert.True(Vector3.Distance(eye, ray.Origin) < 0.0001f);
        Assert.True(Vector3.Distance(-Vector3.UnitZ, ray.Direction) < 0.0001f);
    }

    [Fact]
    public void PolygonHitGloballyBeatsNearerSphereFallback()
    {
        var sphereOnly = Part(1u, z: -3f, polygons: []);
        var polygon = Part(2u, z: -8f, polygons: [SquareAtLocalZ(0f)]);

        RetailSelectionHit? hit = RetailWorldPicker.Pick(
            Vector3.Zero, -Vector3.UnitZ, [sphereOnly, polygon]);

        Assert.NotNull(hit);
        Assert.Equal(2u, hit.Value.ServerGuid);
        Assert.Equal(1002u, hit.Value.LocalEntityId);
        Assert.True(hit.Value.PolygonHit);
    }

    [Fact]
    public void SphereIsUsedOnlyWhenNoVisiblePolygonHits()
    {
        var part = Part(0xABCDu, z: -5f, polygons: []);

        RetailSelectionHit? hit = RetailWorldPicker.Pick(
            Vector3.Zero, -Vector3.UnitZ, [part]);

        Assert.NotNull(hit);
        Assert.Equal(0xABCDu, hit.Value.ServerGuid);
        Assert.False(hit.Value.PolygonHit);
        Assert.Equal(3d, hit.Value.Distance, 4);
    }

    [Fact]
    public void StopsAtFirstHitPolygonInDatOrderForEachPart()
    {
        var datOrdered = Part(
            10u,
            z: 0f,
            polygons: [SquareAtLocalZ(-10f), SquareAtLocalZ(-5f)],
            sphereCenter: new Vector3(0f, 0f, -7.5f),
            sphereRadius: 5f);
        var competitor = Part(20u, z: -7f, polygons: [SquareAtLocalZ(0f)]);

        RetailSelectionHit? hit = RetailWorldPicker.Pick(
            Vector3.Zero, -Vector3.UnitZ, [datOrdered, competitor]);

        Assert.NotNull(hit);
        Assert.Equal(20u, hit.Value.ServerGuid);
    }

    [Fact]
    public void SingleSidedPolygonRejectsItsBackFace()
    {
        var front = new RetailSelectionPart(
            1u,
            1001u,
            0,
            Matrix4x4.Identity,
            new RetailSelectionMesh(
                new Vector3(0f, 0f, -5f), 2f,
                [SquareAtLocalZ(-5f, singleSided: true)]));

        RetailSelectionHit? backHit = RetailWorldPicker.Pick(
            new Vector3(0f, 0f, -10f), Vector3.UnitZ, [front]);

        Assert.NotNull(backHit);
        Assert.False(backHit.Value.PolygonHit);
    }

    [Fact]
    public void InverseScaledPartPreservesWorldDistance()
    {
        var transform = Matrix4x4.CreateScale(2f)
            * Matrix4x4.CreateTranslation(0f, 0f, -10f);
        var part = new RetailSelectionPart(
            3u,
            1003u,
            0,
            transform,
            new RetailSelectionMesh(Vector3.Zero, 1f, [SquareAtLocalZ(0f)]));

        RetailSelectionHit? hit = RetailWorldPicker.Pick(
            Vector3.Zero, -Vector3.UnitZ, [part]);

        Assert.NotNull(hit);
        Assert.True(hit.Value.PolygonHit);
        Assert.Equal(10d, hit.Value.Distance, 4);
    }

    [Fact]
    public void SkipGuidAndNonServerPartsNeverCompete()
    {
        RetailSelectionHit? hit = RetailWorldPicker.Pick(
            Vector3.Zero,
            -Vector3.UnitZ,
            [Part(0u, -2f, [SquareAtLocalZ(0f)]), Part(7u, -4f, [SquareAtLocalZ(0f)])],
            skipServerGuid: 7u);

        Assert.Null(hit);
    }

    [Fact]
    public void DrawingSphereRejectsRayWhichBeginsInsideLikeRetail()
    {
        bool hit = RetailWorldPicker.TryIntersectSphere(
            Vector3.Zero, Vector3.UnitX, Vector3.Zero, 2f, out _);

        Assert.False(hit);
    }

    [Fact]
    public void ConvexEdgeTestRejectsAConcaveNotchRatherThanUsingEvenOddFill()
    {
        var concave = new RetailSelectionPolygon(
            [
                new(-2f, -2f, -5f),
                new( 2f, -2f, -5f),
                new( 2f,  2f, -5f),
                new( 0f,  0f, -5f),
                new(-2f,  2f, -5f),
            ],
            SingleSided: false);

        bool hit = RetailWorldPicker.TryIntersectPolygon(
            new Vector3(0f, 1f, 0f), -Vector3.UnitZ, concave, out _);

        Assert.False(hit);
    }

    private static RetailSelectionPart Part(
        uint guid,
        float z,
        IReadOnlyList<RetailSelectionPolygon> polygons,
        Vector3? sphereCenter = null,
        float sphereRadius = 2f)
        => new(
            guid,
            guid + 1000u,
            0,
            Matrix4x4.CreateTranslation(0f, 0f, z),
            new RetailSelectionMesh(
                sphereCenter ?? Vector3.Zero,
                sphereRadius,
                polygons));

    private static RetailSelectionPolygon SquareAtLocalZ(
        float z,
        bool singleSided = false)
        => new(
            [
                new Vector3(-1f, -1f, z),
                new Vector3( 1f, -1f, z),
                new Vector3( 1f,  1f, z),
                new Vector3(-1f,  1f, z),
            ],
            singleSided);
}
