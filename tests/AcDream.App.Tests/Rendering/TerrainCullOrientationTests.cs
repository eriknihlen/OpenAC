using System;
using System.Numerics;
using Xunit;

namespace AcDream.App.Tests.Rendering;

public class TerrainCullOrientationTests
{
    private static readonly Vector3[] Triangle =
    {
        new(-1f, 10f, 94f),
        new( 1f, 10f, 94f),
        new( 1f, 12f, 94f),
    };

    private static float NdcSignedArea2(Vector3 eye, Vector3 forward)
    {
        var view = Matrix4x4.CreateLookAt(eye, eye + forward, Vector3.UnitZ);
        var proj = Matrix4x4.CreatePerspectiveFieldOfView(MathF.PI / 3f, 16f / 9f, 0.1f, 5000f);
        var viewProj = view * proj;

        Span<Vector2> ndc = stackalloc Vector2[3];
        for (int i = 0; i < 3; i++)
        {
            var c = Vector4.Transform(new Vector4(Triangle[i], 1f), viewProj);
            Assert.True(c.W > 1e-3f, "test triangle must be in front of the eye");
            ndc[i] = new Vector2(c.X / c.W, c.Y / c.W);
        }

        // Twice the signed area: > 0 = CCW in NDC (GL window space keeps the
        // orientation — NDC y up maps to window y up, no flip).
        return (ndc[1].X - ndc[0].X) * (ndc[2].Y - ndc[0].Y)
             - (ndc[1].Y - ndc[0].Y) * (ndc[2].X - ndc[0].X);
    }

    [Fact]
    public void EyeAboveTerrainPlane_WindsCcw_FrontFaceKept()
    {
        float area = NdcSignedArea2(new Vector3(0f, 5f, 96.5f), new Vector3(0f, 1f, -0.3f));
        Assert.True(area > 0f,
            $"above-plane eye must see the terrain triangle CCW (area2={area}) — " +
            "FrontFace(Ccw)+Cull(Back) would otherwise cull terrain from above");
    }

    [Fact]
    public void EyeBelowTerrainPlane_WindsCw_BackfaceCulled()
    {
        float area = NdcSignedArea2(new Vector3(0f, 5f, 92.5f), new Vector3(0f, 1f, 0.2f));
        Assert.True(area < 0f,
            $"below-plane eye must see the terrain triangle CW (area2={area}) — " +
            "it must backface-cull like retail's which_side2 eye-side gate");
    }
}
