using System.Numerics;
using AcDream.App.Rendering;
using AcDream.Core.CharGen;
using Xunit;

namespace AcDream.App.Tests.Rendering;

public class ChargenPreviewCameraTests
{
    [Theory]
    [InlineData((uint)ChargenHeritageGroup.Aluvian, 0f, -0.550000012f, 1.64999998f)]
    [InlineData((uint)ChargenHeritageGroup.Gharundim, 0f, -0.550000012f, 1.64999998f)]
    [InlineData((uint)ChargenHeritageGroup.Gearknight, 0f, -0.550000012f, 1.64999998f)]
    [InlineData((uint)ChargenHeritageGroup.Undead, 0f, -0.550000012f, 1.64999998f)]
    [InlineData((uint)ChargenHeritageGroup.Tumerok, 0f, -0.850000024f, 1.64999998f)]
    [InlineData((uint)ChargenHeritageGroup.Olthoi, 0f, -1.85000002f, 1.85000002f)]
    [InlineData((uint)ChargenHeritageGroup.OlthoiAcid, 0f, -3.04999995f, 2.75f)]
    public void ResolveDefaultEye_MatchesRetailPerHeritageLiterals(uint heritageId, float x, float y, float z)
    {
        Vector3 eye = ChargenPreviewCamera.ResolveDefaultEye(heritageId);
        Assert.Equal(x, eye.X, 4);
        Assert.Equal(y, eye.Y, 4);
        Assert.Equal(z, eye.Z, 4);
    }

    [Theory]
    [InlineData((uint)ChargenHeritageGroup.Aluvian, 0f, -2.5f, 0.95f)]
    [InlineData((uint)ChargenHeritageGroup.Tumerok, 0f, -2.5f, 0.95f)]
    [InlineData((uint)ChargenHeritageGroup.Olthoi, 0f, -3.79999995f, 1.14999998f)]
    [InlineData((uint)ChargenHeritageGroup.OlthoiAcid, 0f, -5.69999981f, 1.64999998f)]
    public void ResolveZoomedOutEye_MatchesRetailPerHeritageLiterals(uint heritageId, float x, float y, float z)
    {
        Vector3 eye = ChargenPreviewCamera.ResolveZoomedOutEye(heritageId);
        Assert.Equal(x, eye.X, 4);
        Assert.Equal(y, eye.Y, 4);
        Assert.Equal(z, eye.Z, 4);
    }

    [Fact]
    public void Constructor_DefaultsToStandardHeritageEye_ForUnknownHeritageId()
    {
        var cam = new ChargenPreviewCamera(heritageId: 0u);
        Assert.Equal(ChargenPreviewCamera.ResolveDefaultEye(0u), cam.Eye);
    }

    [Fact]
    public void SetHeritage_UpdatesEyeToTheNewHeritagesProfile()
    {
        var cam = new ChargenPreviewCamera((uint)ChargenHeritageGroup.Aluvian);
        cam.SetHeritage((uint)ChargenHeritageGroup.Olthoi);
        Assert.Equal(ChargenPreviewCamera.ResolveDefaultEye((uint)ChargenHeritageGroup.Olthoi), cam.Eye);
    }

    [Fact]
    public void View_LooksStraightDownPlusY_ZeroYawZeroPitch()
    {
        var cam = new ChargenPreviewCamera((uint)ChargenHeritageGroup.Aluvian) { Aspect = 1f };
        var forward = -new Vector3(cam.View.M13, cam.View.M23, cam.View.M33);
        Assert.Equal(0f, forward.X, 4);
        Assert.Equal(1f, forward.Y, 4);
        Assert.Equal(0f, forward.Z, 4);
    }

    [Fact]
    public void Eye_RoundTripsThroughViewMatrixInversion()
    {
        var cam = new ChargenPreviewCamera((uint)ChargenHeritageGroup.Olthoi) { Aspect = 1f };
        Assert.True(Matrix4x4.Invert(cam.View, out var inv));
        Vector3 eye = inv.Translation;
        Assert.Equal(cam.Eye.X, eye.X, 3);
        Assert.Equal(cam.Eye.Y, eye.Y, 3);
        Assert.Equal(cam.Eye.Z, eye.Z, 3);
    }

    [Fact]
    public void Projection_IsFiniteAndUsesAspect()
    {
        var cam = new ChargenPreviewCamera { Aspect = 1.5f };
        Assert.True(float.IsFinite(cam.Projection.M11));
        Assert.NotEqual(0f, cam.Projection.M34);
    }

    [Fact]
    public void RotationSecondsPerRevolution_IsExactlyThreeSeconds()
    {
        Assert.Equal(3.0f, ChargenPreviewCamera.RotationSecondsPerRevolution);
    }

    [Fact]
    public void ZoomTweenDurationSeconds_IsExactlyZeroPointSix()
    {
        Assert.Equal(0.6f, ChargenPreviewCamera.ZoomTweenDurationSeconds);
    }
}
