using System.Numerics;
using AcDream.Core.World;
using Xunit;

namespace AcDream.Core.Tests.World;

[Collection(DerethDateTimeCollection.Name)]
public sealed class SkyStateTests
{
    [Fact]
    public void Default_Has4Keyframes()
    {
        var sky = SkyStateProvider.Default();
        Assert.Equal(4, sky.KeyframeCount);
    }

    [Fact]
    public void Interpolate_AtExactKeyframe_ReturnsThatFrameData()
    {
        var sky = SkyStateProvider.Default();
        var noon = sky.Interpolate(0.5f);   // noon keyframe

        Assert.InRange(noon.SunColor.X, 0.9f, 1.1f);
        Assert.InRange(noon.SunColor.Y, 0.9f, 1.1f);
    }

    [Fact]
    public void Interpolate_BetweenKeyframes_LerpsRawInputs()
    {
        var sky = SkyStateProvider.Default();
        var dawn  = sky.Interpolate(0.25f);
        var noon  = sky.Interpolate(0.5f);
        var midPt = sky.Interpolate(0.375f);

        float low  = System.Math.Min(dawn.DirColor.Y, noon.DirColor.Y);
        float high = System.Math.Max(dawn.DirColor.Y, noon.DirColor.Y);
        Assert.InRange(midPt.DirColor.Y, low, high);
    }

    [Fact]
    public void RetailSunVector_AtZenith_HasMagnitudeEqualToBrightness()
    {
        // Sun straight up (P=90°): cos(P)=0, sin(P)=1.
        //   sunVec = (sin(H)×B×0, 0, B×1) = (0, 0, B)
        //   |sunVec| = B
        var kf = new SkyKeyframe(
            Begin: 0.5f,
            SunHeadingDeg: 0f,
            SunPitchDeg:   90f,
            DirColor:      Vector3.One,
            DirBright:     1.5f,
            AmbColor:      Vector3.One,
            AmbBright:     0.3f,
            FogColor:      Vector3.One,
            FogDensity:    0f);

        var v = SkyStateProvider.RetailSunVector(kf);
        Assert.InRange(v.Length(), 1.49f, 1.51f);
    }

    [Fact]
    public void RetailSunVector_MagnitudeAlwaysEqualsDirBright()
    {
        var horizon = new SkyKeyframe(
            Begin: 0f, SunHeadingDeg: 0f, SunPitchDeg: 0f,
            DirColor: Vector3.One, DirBright: 2.0f,
            AmbColor: Vector3.One, AmbBright: 1f,
            FogColor: Vector3.One, FogDensity: 0f);
        Assert.InRange(SkyStateProvider.RetailSunVector(horizon).Length(), 1.99f, 2.01f);

        var dawn = new SkyKeyframe(
            Begin: 0f, SunHeadingDeg: 90f, SunPitchDeg: 0.9f,
            DirColor: Vector3.One, DirBright: 0.224f,
            AmbColor: Vector3.One, AmbBright: 0.40f,
            FogColor: Vector3.One, FogDensity: 0f);
        var v = SkyStateProvider.RetailSunVector(dawn);
        Assert.InRange(v.X, 0.223f, 0.225f);
        Assert.InRange(v.Y, -0.001f, 0.001f);
        Assert.InRange(v.Z, 0.003f, 0.004f);   // DirBright·sin(0.9°) ≈ 0.0035
        Assert.InRange(v.Length(), 0.223f, 0.225f);   // = DirBright
    }

    [Fact]
    public void SunColor_UsesRetailMagnitudeNotDirBrightDirectly()
    {
        // At sun pitch 90° (zenith) with H=0, B=2: |sunVec| = 2.
        // SunColor = DirColor × |sunVec| = (0.5, 0.5, 0.5) × 2 = (1, 1, 1).
        var kf = new SkyKeyframe(
            Begin: 0.5f,
            SunHeadingDeg: 0f,
            SunPitchDeg:   90f,
            DirColor:      new Vector3(0.5f, 0.5f, 0.5f),
            DirBright:     2.0f,
            AmbColor:      Vector3.One,
            AmbBright:     0.3f,
            FogColor:      Vector3.One,
            FogDensity:    0f);

        Assert.InRange(kf.SunColor.X, 0.99f, 1.01f);
        Assert.InRange(kf.SunColor.Y, 0.99f, 1.01f);
        Assert.InRange(kf.SunColor.Z, 0.99f, 1.01f);
    }

    [Fact]
    public void AmbientColor_BoostsByTwentyPercentOfSunVectorLength()
    {
        // |sunVec| = 1 (horizon north), AmbBright = 0.4, AmbColor = (1,1,1).
        // AmbientColor = AmbColor × (AmbBright + 0.2 × |sunVec|)
        //              = (1,1,1) × (0.4 + 0.2) = (0.6, 0.6, 0.6).
        var kf = new SkyKeyframe(
            Begin: 0f,
            SunHeadingDeg: 0f,
            SunPitchDeg:   0f,
            DirColor:      Vector3.One,
            DirBright:     1f,
            AmbColor:      Vector3.One,
            AmbBright:     0.4f,
            FogColor:      Vector3.One,
            FogDensity:    0f);

        Assert.InRange(kf.AmbientColor.X, 0.59f, 0.61f);
    }

    [Fact]
    public void Interpolate_Wraps_AcrossMidnight()
    {
        var sky = SkyStateProvider.Default();
        var justAfterMidnight = sky.Interpolate(0.01f);

        // Should return finite valid state (not NaN).
        Assert.False(float.IsNaN(justAfterMidnight.SunColor.X));
        Assert.False(float.IsNaN(justAfterMidnight.AmbientColor.X));
    }

    [Fact]
    public void SunDirectionFromKeyframe_ReturnsUnitVector()
    {
        var kf = new SkyKeyframe(
            Begin: 0.5f,
            SunHeadingDeg: 180f,  // south
            SunPitchDeg:   70f,
            DirColor:      Vector3.One,
            DirBright:     1f,
            AmbColor:      Vector3.One,
            AmbBright:     1f,
            FogColor:      Vector3.One,
            FogDensity:    0.001f);

        var dir = SkyStateProvider.SunDirectionFromKeyframe(kf);
        Assert.InRange(dir.Length(), 0.99f, 1.01f);
    }

    [Fact]
    public void WorldTimeService_SyncFromServer_SetsTicks()
    {
        var service = new WorldTimeService(SkyStateProvider.Default());
        service.SyncFromServer(12345.0);

        Assert.InRange(service.NowTicks, 12345.0, 12346.0);
    }

    [Fact]
    public void WorldTimeService_DayFraction_RespectsSync()
    {
        var service = new WorldTimeService(SkyStateProvider.Default());
        // Need to aim for dayFraction 0.5 (Gloaming-and-Half, slot 15 since tick 0 = slot 7).
        // Sync to (0.5 - 7/16) * DayTicks = (1/16) * DayTicks — 1 slot past Morntide-and-Half = Midsong.
        // Actually simpler: target fraction 7/16 (slot 7 = Morntide-and-Half) by syncing to tick 0.
        service.SyncFromServer(0);

        Assert.InRange(service.DayFraction, 0.43, 0.44);   // 7/16 = 0.4375
        Assert.True(service.IsDaytime);
    }
}
