using System.Collections.Generic;
using System.Numerics;
using AcDream.Core.Lighting;
using Xunit;

namespace AcDream.Core.Tests.Lighting;

public class LightBakeConformanceTests
{
    private static LightSource OrangeTorch(Vector3 pos) => new()
    {
        Kind = LightKind.Point,
        WorldPosition = pos,
        ColorLinear = new Vector3(1.0f, 0.588f, 0.314f),
        Intensity = 100f,
        Range = 4f * 1.3f,                               // falloff 4 × static_light_factor
        IsLit = true,
    };

    [Theory]
    [InlineData(1f)]
    [InlineData(2f)]
    [InlineData(3f)]
    [InlineData(4f)]
    [InlineData(5f)]
    public void SingleOrangeTorch_IsWarmAndBounded_NeverWhite(float dist)
    {
        var vtx = Vector3.Zero;
        var normal = Vector3.UnitX;
        var torch = OrangeTorch(new Vector3(dist, 0f, 0f));

        var c = LightBake.ComputeVertexColor(vtx, normal, new[] { torch });

        Assert.InRange(c.X, 0f, 1f);
        Assert.InRange(c.Y, 0f, 1f);
        Assert.InRange(c.Z, 0f, 1f);
        if (c.X > 0f)
        {
            Assert.True(c.X >= c.Y, $"R({c.X}) >= G({c.Y}) at d={dist}");
            Assert.True(c.Y >= c.Z, $"G({c.Y}) >= B({c.Z}) at d={dist}");
        }
    }

    [Fact]
    public void BeyondRange_ContributesNothing()
    {
        var torch = OrangeTorch(new Vector3(100f, 0f, 0f));
        var c = LightBake.ComputeVertexColor(Vector3.Zero, Vector3.UnitX, new[] { torch });
        Assert.Equal(Vector3.Zero, c);
    }

    [Fact]
    public void ManyOverlappingIntenseTorches_StillClampToOne()
    {
        var lights = new List<LightSource>();
        for (int i = 0; i < 8; i++)
            lights.Add(new LightSource
            {
                Kind = LightKind.Point,
                WorldPosition = new Vector3(1.5f, 0.1f * i, 0f),
                ColorLinear = new Vector3(0.98f, 0.95f, 0.9f),
                Intensity = 100f,
                Range = 5.2f,
                IsLit = true,
            });

        var c = LightBake.ComputeVertexColor(Vector3.Zero, Vector3.UnitX, lights);
        Assert.InRange(c.X, 0f, 1f);
        Assert.InRange(c.Y, 0f, 1f);
        Assert.InRange(c.Z, 0f, 1f);
    }
}
