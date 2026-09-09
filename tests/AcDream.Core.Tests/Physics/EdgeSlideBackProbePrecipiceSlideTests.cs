using System.Numerics;
using AcDream.Core.Physics;
using Xunit;
using Plane = System.Numerics.Plane;

namespace AcDream.Core.Tests.Physics;

public class EdgeSlideBackProbePrecipiceSlideTests
{
    [Fact]
    public void PrecipiceSlide_BackProbeState_CrossesEdge_DoesNotWedge()
    {
        var transition = new Transition();
        var sp = transition.SpherePath;

        sp.GlobalCurrCenter[0].Origin = new Vector3(0f, 0f, 1f);
        sp.GlobalCurrCenter[0].Radius = 0.5f;

        var plane = new Plane(Vector3.UnitZ, -1f);
        sp.SetWalkable(
            plane,
            new[]
            {
                new Vector3(-1f, -1f, 1f),
                new Vector3(1f, -1f, 1f),
                new Vector3(1f, 1f, 1f),
                new Vector3(-1f, 1f, 1f),
            },
            Vector3.UnitZ);

        sp.CheckPos = new Vector3(1.2f, 0f, 1f);
        sp.GlobalSphere[0].Origin = sp.CheckPos;
        sp.GlobalSphere[0].Radius = 0.5f;

        var result = sp.PrecipiceSlide(transition);

        Assert.NotEqual(TransitionState.Collided, result);
        Assert.True(
            result is TransitionState.Slid or TransitionState.Adjusted or TransitionState.OK,
            $"Back-probe PrecipiceSlide must slide/adjust across the found edge, not wedge; got {result}.");

        Assert.False(sp.WalkableValid);
    }

    [Fact]
    public void PrecipiceSlide_NoEdgeCrossed_ReturnsCollided_MatchingRetail()
    {
        var transition = new Transition();
        var sp = transition.SpherePath;

        sp.GlobalCurrCenter[0].Origin = new Vector3(0f, 0f, 1f);
        sp.GlobalCurrCenter[0].Radius = 0.5f;

        var plane = new Plane(Vector3.UnitZ, -1f);
        sp.SetWalkable(
            plane,
            new[]
            {
                new Vector3(-1f, -1f, 1f),
                new Vector3(1f, -1f, 1f),
                new Vector3(1f, 1f, 1f),
                new Vector3(-1f, 1f, 1f),
            },
            Vector3.UnitZ);

        sp.CheckPos = new Vector3(0.1f, 0f, 1f);
        sp.GlobalSphere[0].Origin = sp.CheckPos;
        sp.GlobalSphere[0].Radius = 0.5f;

        var result = sp.PrecipiceSlide(transition);

        Assert.Equal(TransitionState.Collided, result);
    }
}
