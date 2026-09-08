using System;
using System.Numerics;
using AcDream.Core.Lighting;
using Xunit;

namespace AcDream.Core.Tests.Lighting;

public sealed class LightBakeTests
{
    private static LightSource Torch(Vector3 pos, float intensity = 100f, float range = 10f)
        => new LightSource
        {
            Kind = LightKind.Point,
            WorldPosition = pos,
            ColorLinear = Vector3.One,
            Intensity = intensity,
            Range = range,
            IsLit = true,
        };

    [Fact]
    public void NearTorch_FacingIt_SaturatesToColor()
    {
        var c = LightBake.PointContribution(
            Vector3.Zero, new Vector3(0, 0, 1), Torch(new Vector3(0, 0, 2)));
        Assert.Equal(1f, c.X, 4);
        Assert.Equal(1f, c.Y, 4);
        Assert.Equal(1f, c.Z, 4);
    }

    [Fact]
    public void FarTorch_FallsOffSmoothly()
    {
        // Torch 8 m above (still within Range 10). scale=(1-0.8)·100·(8/512)=0.3125.
        var c = LightBake.PointContribution(
            Vector3.Zero, new Vector3(0, 0, 1), Torch(new Vector3(0, 0, 8)));
        Assert.Equal(0.3125f, c.X, 4);
        Assert.Equal(0.3125f, c.Y, 4);
        Assert.Equal(0.3125f, c.Z, 4);
    }

    [Fact]
    public void OutOfRange_ContributesNothing()
    {
        // Torch 11 m above, Range 10 → dist >= falloff_eff, skipped.
        var c = LightBake.PointContribution(
            Vector3.Zero, new Vector3(0, 0, 1), Torch(new Vector3(0, 0, 11)));
        Assert.Equal(Vector3.Zero, c);
    }

    [Fact]
    public void FacingAway_BeyondWrap_ContributesNothing()
    {
        // Normal points away (−Z) from a torch above: N·D=−2, wrap=(1/1.5)(−2+1)<0.
        var c = LightBake.PointContribution(
            Vector3.Zero, new Vector3(0, 0, -1), Torch(new Vector3(0, 0, 2)));
        Assert.Equal(Vector3.Zero, c);
    }

    [Fact]
    public void HalfLambertWrap_LightsSurfaceAngledPast90Degrees()
    {
        double t = 100.0 * Math.PI / 180.0;
        var n = new Vector3((float)Math.Sin(t), 0, (float)Math.Cos(t));
        var c = LightBake.PointContribution(Vector3.Zero, n, Torch(new Vector3(0, 0, 2)));
        Assert.True(c.X > 0f, "half-Lambert wrap should light a surface angled past 90°");
    }

    [Fact]
    public void ComputeVertexColor_SumsLightsAndClampsToOne()
    {
        var lights = new[]
        {
            Torch(new Vector3(0, 0, 2)),
            Torch(new Vector3(0, 0, 2)),
        };
        var c = LightBake.ComputeVertexColor(Vector3.Zero, new Vector3(0, 0, 1), lights);
        Assert.Equal(1f, c.X, 4);
        Assert.Equal(1f, c.Y, 4);
        Assert.Equal(1f, c.Z, 4);
    }

    [Fact]
    public void ComputeVertexColor_SkipsDirectionalAndUnlit()
    {
        var lights = new[]
        {
            new LightSource { Kind = LightKind.Directional, WorldPosition = new Vector3(0,0,2),
                ColorLinear = Vector3.One, Intensity = 100f, Range = 10f, IsLit = true },
            new LightSource { Kind = LightKind.Point, WorldPosition = new Vector3(0,0,2),
                ColorLinear = Vector3.One, Intensity = 100f, Range = 10f, IsLit = false },
        };
        var c = LightBake.ComputeVertexColor(Vector3.Zero, new Vector3(0, 0, 1), lights);
        Assert.Equal(Vector3.Zero, c);
    }
}
