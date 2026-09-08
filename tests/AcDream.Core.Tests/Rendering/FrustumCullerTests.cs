using System.Numerics;
using AcDream.App.Rendering;
using Xunit;

namespace AcDream.Core.Tests.Rendering;

public class FrustumCullerTests
{
    [Fact]
    public void IdentityVP_EverythingVisible()
    {
        var vp = Matrix4x4.CreateOrthographic(2f, 2f, 0.1f, 100f);
        var planes = FrustumPlanes.FromViewProjection(vp);

        // Box at origin, well within the frustum.
        Assert.True(FrustumCuller.IsAabbVisible(planes,
            new Vector3(-0.5f, -0.5f, -50f),
            new Vector3(0.5f, 0.5f, -1f)));
    }

    [Fact]
    public void PerspectiveCamera_BoxInFront_Visible()
    {
        var view = Matrix4x4.CreateLookAt(
            new Vector3(0, 0, 0),
            new Vector3(0, 0, -1),
            Vector3.UnitY);
        var proj = Matrix4x4.CreatePerspectiveFieldOfView(
            MathF.PI / 3f, 1f, 1f, 1000f);
        var planes = FrustumPlanes.FromViewProjection(view * proj);

        Assert.True(FrustumCuller.IsAabbVisible(planes,
            new Vector3(-10f, -10f, -100f),
            new Vector3(10f, 10f, -10f)));
    }

    [Fact]
    public void PerspectiveCamera_BoxBehind_NotVisible()
    {
        var view = Matrix4x4.CreateLookAt(
            new Vector3(0, 0, 0),
            new Vector3(0, 0, -1),
            Vector3.UnitY);
        var proj = Matrix4x4.CreatePerspectiveFieldOfView(
            MathF.PI / 3f, 1f, 1f, 1000f);
        var planes = FrustumPlanes.FromViewProjection(view * proj);

        Assert.False(FrustumCuller.IsAabbVisible(planes,
            new Vector3(-10f, -10f, 10f),
            new Vector3(10f, 10f, 100f)));
    }

    [Fact]
    public void PerspectiveCamera_BoxFarLeft_NotVisible()
    {
        var view = Matrix4x4.CreateLookAt(
            new Vector3(0, 0, 0),
            new Vector3(0, 0, -1),
            Vector3.UnitY);
        var proj = Matrix4x4.CreatePerspectiveFieldOfView(
            MathF.PI / 3f, 1f, 1f, 1000f);
        var planes = FrustumPlanes.FromViewProjection(view * proj);

        // Box way off to the left.
        Assert.False(FrustumCuller.IsAabbVisible(planes,
            new Vector3(-1000f, -10f, -50f),
            new Vector3(-500f, 10f, -10f)));
    }

    [Fact]
    public void PerspectiveCamera_BoxStraddlingNearPlane_Visible()
    {
        var view = Matrix4x4.CreateLookAt(
            new Vector3(0, 0, 0),
            new Vector3(0, 0, -1),
            Vector3.UnitY);
        var proj = Matrix4x4.CreatePerspectiveFieldOfView(
            MathF.PI / 3f, 1f, 1f, 1000f);
        var planes = FrustumPlanes.FromViewProjection(view * proj);

        Assert.True(FrustumCuller.IsAabbVisible(planes,
            new Vector3(-1f, -1f, -2f),
            new Vector3(1f, 1f, 0.5f)));
    }

    [Theory]
    [InlineData(0.1f, 1000f)]
    [InlineData(1f, 1000f)]
    [InlineData(1f, 100f)]
    [InlineData(2.5f, 5000f)]
    public void PerspectiveCamera_ExtractedNearPlane_SitsAtTheCameraNearDistance(
        float nearDistance, float farDistance)
    {
        var view = Matrix4x4.CreateLookAt(
            new Vector3(0, 0, 0),
            new Vector3(0, 0, -1),
            Vector3.UnitY);
        var proj = Matrix4x4.CreatePerspectiveFieldOfView(
            MathF.PI / 3f, 16f / 9f, nearDistance, farDistance);
        var planes = FrustumPlanes.FromViewProjection(view * proj);

        var near = planes.Near;

        // The plane is normalized and faces down -Z, into the frustum.
        Assert.Equal(1f, new Vector3(near.X, near.Y, near.Z).Length(), 4);
        Assert.Equal(0f, near.X, 4);
        Assert.Equal(0f, near.Y, 4);
        Assert.Equal(-1f, near.Z, 4);

        Assert.Equal(nearDistance, -near.W, 3);

        // A point just inside the near plane is kept; one just outside is not.
        float eps = nearDistance * 0.01f;
        Assert.True(FrustumCuller.IsAabbVisible(planes,
            new Vector3(-0.01f, -0.01f, -(nearDistance + eps)),
            new Vector3(0.01f, 0.01f, -(nearDistance + eps))));
        Assert.False(FrustumCuller.IsAabbVisible(planes,
            new Vector3(-0.01f, -0.01f, -(nearDistance - eps)),
            new Vector3(0.01f, 0.01f, -(nearDistance - eps))));
    }

    [Fact]
    public void PerspectiveCamera_FarPlane_IsUnchangedByTheNearFix()
    {
        const float FarDistance = 100f;
        var view = Matrix4x4.CreateLookAt(
            new Vector3(0, 0, 0),
            new Vector3(0, 0, -1),
            Vector3.UnitY);
        var proj = Matrix4x4.CreatePerspectiveFieldOfView(
            MathF.PI / 3f, 16f / 9f, 1f, FarDistance);
        var planes = FrustumPlanes.FromViewProjection(view * proj);

        var far = planes.Far;
        Assert.Equal(1f, new Vector3(far.X, far.Y, far.Z).Length(), 4);
        Assert.Equal(1f, far.Z, 4);
        Assert.Equal(FarDistance, far.W, 3);
    }

    [Fact]
    public void PerspectiveCamera_BoxBeyondFarPlane_NotVisible()
    {
        var view = Matrix4x4.CreateLookAt(
            new Vector3(0, 0, 0),
            new Vector3(0, 0, -1),
            Vector3.UnitY);
        var proj = Matrix4x4.CreatePerspectiveFieldOfView(
            MathF.PI / 3f, 1f, 1f, 100f);
        var planes = FrustumPlanes.FromViewProjection(view * proj);

        // Box far beyond the far plane.
        Assert.False(FrustumCuller.IsAabbVisible(planes,
            new Vector3(-10f, -10f, -500f),
            new Vector3(10f, 10f, -200f)));
    }

    [Fact]
    public void AcdreamCamera_LandblockInFront_Visible()
    {
        var view = Matrix4x4.CreateLookAt(
            new Vector3(96f, 96f, 150f),
            new Vector3(96f, 288f, 100f),   // looking roughly +Y (forward toward next landblock)
            Vector3.UnitZ);                  // Z-up
        var proj = Matrix4x4.CreatePerspectiveFieldOfView(
            MathF.PI / 3f, 16f / 9f, 1f, 5000f);
        var planes = FrustumPlanes.FromViewProjection(view * proj);

        // Landblock-sized AABB 192 units ahead in Y.
        Assert.True(FrustumCuller.IsAabbVisible(planes,
            new Vector3(0f, 192f, 50f),
            new Vector3(192f, 384f, 200f)));

        Assert.False(FrustumCuller.IsAabbVisible(planes,
            new Vector3(0f, -384f, 50f),
            new Vector3(192f, -192f, 200f)));
    }
}
