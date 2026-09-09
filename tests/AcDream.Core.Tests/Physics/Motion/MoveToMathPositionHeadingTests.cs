using System.Numerics;
using AcDream.Core.Physics;
using AcDream.Core.Physics.Motion;
using Xunit;

namespace AcDream.Core.Tests.Physics.Motion;

public sealed class MoveToMathPositionHeadingTests
{
    private const float Tol = 0.01f;


    [Fact]
    public void North_PlusY_IsZero()
    {
        float h = MoveToMath.PositionHeading(Vector3.Zero, new Vector3(0f, 1f, 0f));
        Assert.Equal(0f, h, 2);
    }

    [Fact]
    public void East_PlusX_Is90()
    {
        float h = MoveToMath.PositionHeading(Vector3.Zero, new Vector3(1f, 0f, 0f));
        Assert.Equal(90f, h, 2);
    }

    [Fact]
    public void South_MinusY_Is180()
    {
        float h = MoveToMath.PositionHeading(Vector3.Zero, new Vector3(0f, -1f, 0f));
        Assert.Equal(180f, h, 2);
    }

    [Fact]
    public void West_MinusX_Is270()
    {
        float h = MoveToMath.PositionHeading(Vector3.Zero, new Vector3(-1f, 0f, 0f));
        Assert.Equal(270f, h, 2);
    }

    [Fact]
    public void Heading_IsAlways_InZeroToThreeSixtyRange()
    {
        // NE diagonal
        float h = MoveToMath.PositionHeading(Vector3.Zero, new Vector3(1f, 1f, 0f));
        Assert.InRange(h, 0f, 360f);
        Assert.Equal(45f, h, 2);
    }

    [Fact]
    public void Heading_IgnoresZ_HorizontalOnly()
    {
        float h1 = MoveToMath.PositionHeading(new Vector3(0, 0, 5f), new Vector3(1f, 0f, -10f));
        float h2 = MoveToMath.PositionHeading(new Vector3(0, 0, -3f), new Vector3(1f, 0f, 100f));
        Assert.Equal(h1, h2, 2);
        Assert.Equal(90f, h1, 2);
    }

    // ── GetHeading: extracts heading from a body orientation quaternion ────

    [Fact]
    public void GetHeading_IdentityQuaternion_FacesHeadingZero()
    {
        float h = MoveToMath.GetHeading(Quaternion.Identity);
        Assert.Equal(0f, h, 1);
    }

    [Fact]
    public void GetHeading_SetHeading_RoundTrips_Cardinals()
    {
        foreach (float heading in new[] { 0f, 90f, 180f, 270f, 45f, 359f })
        {
            var q = MoveToMath.SetHeading(Quaternion.Identity, heading);
            float back = MoveToMath.GetHeading(q);
            float diff = MathF.Abs(back - heading);
            if (diff > 180f) diff = 360f - diff;
            Assert.True(diff < 0.5f, $"heading {heading} round-tripped to {back}");
        }
    }

    [Fact]
    public void SetHeading_North_ProducesForwardVectorFacingPlusY()
    {
        var q = MoveToMath.SetHeading(Quaternion.Identity, 0f);
        var forward = Vector3.Transform(new Vector3(0f, 1f, 0f), q);
        Assert.True(forward.Y > 0.9f, $"expected +Y forward, got {forward}");
    }

    [Fact]
    public void SetHeading_East_ProducesForwardVectorFacingPlusX()
    {
        var q = MoveToMath.SetHeading(Quaternion.Identity, 90f);
        var forward = Vector3.Transform(new Vector3(0f, 1f, 0f), q);
        Assert.True(forward.X > 0.9f, $"expected +X forward, got {forward}");
    }
}
