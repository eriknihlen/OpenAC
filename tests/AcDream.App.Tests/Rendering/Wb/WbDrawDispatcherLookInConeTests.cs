using System.Numerics;
using AcDream.App.Rendering.Walk;
using AcDream.App.Rendering.Wb;
using DatReaderWriter.Types;

namespace AcDream.App.Tests.Rendering.Wb;

public sealed class WbDrawDispatcherLookInConeTests
{
    [Fact]
    public void DrawingSphereAdmissionUsesTheIndividualPartTransformAndLargestScale()
    {
        var cone = new RecordingLookInViews();
        var sphere = new Sphere
        {
            Origin = new Vector3(1f, 2f, 3f),
            Radius = 0.5f,
        };
        Matrix4x4 transform =
            Matrix4x4.CreateScale(2f, 3f, 4f)
            * Matrix4x4.CreateTranslation(10f, 20f, 30f);

        bool visible = WbDrawDispatcher.LookInDrawingSphereVisible(
            cone,
            routeIndex: 7,
            sphere,
            transform,
            out Vector3 center,
            out float radius);

        Assert.True(visible);
        Assert.Equal(7, cone.RouteIndex);
        Assert.Equal(new Vector3(12f, 26f, 42f), center);
        Assert.Equal(center, cone.Center);
        Assert.Equal(2f, radius);
        Assert.Equal(radius, cone.Radius);
    }

    private sealed class RecordingLookInViews : IWalkLookInViewSource
    {
        public IReadOnlyList<uint> LookInCellTurns { get; } = [0xF4180112u];

        public int RouteIndex { get; private set; }

        public Vector3 Center { get; private set; }

        public float Radius { get; private set; }

        public bool SphereVisibleInLookInTurn(
            int routeIndex,
            in Vector3 center,
            float radius,
            bool testSphere = true)
        {
            RouteIndex = routeIndex;
            Center = center;
            Radius = radius;
            return true;
        }
    }
}
