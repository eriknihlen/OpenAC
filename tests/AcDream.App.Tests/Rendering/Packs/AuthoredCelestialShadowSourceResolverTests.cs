using System.Numerics;
using AcDream.App.Rendering.Packs;
using AcDream.Core.World;

namespace AcDream.App.Tests.Rendering.Packs;

public sealed class AuthoredCelestialShadowSourceResolverTests
{
    [Fact]
    public void VerifiedDerethIds_AreStable()
    {
        Assert.Equal(0x01001348u, AuthoredCelestialShadowSourceResolver.SunGfxObjId);
        Assert.Equal(0x01001F6Au, AuthoredCelestialShadowSourceResolver.DominantMoonGfxObjId);
        Assert.Equal(0x01001F67u, AuthoredCelestialShadowSourceResolver.SecondaryMoonGfxObjId);
    }

    [Fact]
    public void SunOverlap_WinsRegardlessOfObjectOrder()
    {
        DayGroupData group = Group(
            Celestial(
                AuthoredCelestialShadowSourceResolver.SecondaryMoonGfxObjId,
                Vector3.UnitX),
            Celestial(
                AuthoredCelestialShadowSourceResolver.DominantMoonGfxObjId,
                Vector3.UnitX),
            Celestial(
                AuthoredCelestialShadowSourceResolver.SunGfxObjId,
                Vector3.UnitX));

        AuthoredCelestialShadowSource result = Resolve(group, 0.5f);

        Assert.Equal(AuthoredCelestialShadowSourceKind.Sun, result.Kind);
        Assert.Equal(2, result.ObjectIndex);
        Assert.Equal(AuthoredCelestialShadowSourceResolver.SunGfxObjId, result.GfxObjId);
    }

    [Fact]
    public void MissingSun_FallsBackToDominantMoonBeforeSecondaryMoon()
    {
        DayGroupData group = Group(
            Celestial(
                AuthoredCelestialShadowSourceResolver.SecondaryMoonGfxObjId,
                Vector3.UnitX),
            Celestial(
                AuthoredCelestialShadowSourceResolver.DominantMoonGfxObjId,
                Vector3.UnitX));

        AuthoredCelestialShadowSource result = Resolve(group, 0.5f);

        Assert.Equal(AuthoredCelestialShadowSourceKind.DominantMoon, result.Kind);
        Assert.Equal(1, result.ObjectIndex);
        Assert.Equal(
            AuthoredCelestialShadowSourceResolver.DominantMoonGfxObjId,
            result.GfxObjId);
    }

    [Fact]
    public void FullyTransparentHigherPriorityObjects_FallBackToSecondaryMoon()
    {
        DayGroupData group = Group(
            [
                Celestial(
                    AuthoredCelestialShadowSourceResolver.SunGfxObjId,
                    Vector3.UnitX),
                Celestial(
                    AuthoredCelestialShadowSourceResolver.DominantMoonGfxObjId,
                    Vector3.UnitX),
                Celestial(
                    AuthoredCelestialShadowSourceResolver.SecondaryMoonGfxObjId,
                    Vector3.UnitX),
            ],
            Replacements(
                0f,
                Replace(0, transparent: 1f),
                Replace(1, transparent: 1f)));

        AuthoredCelestialShadowSource result = Resolve(group, 0.5f);

        Assert.Equal(AuthoredCelestialShadowSourceKind.SecondaryMoon, result.Kind);
        Assert.Equal(2, result.ObjectIndex);
        Assert.Equal(
            AuthoredCelestialShadowSourceResolver.SecondaryMoonGfxObjId,
            result.GfxObjId);
    }

    [Fact]
    public void EffectiveReplacement_ProvidesItsGfxIdentityAndSortCenter()
    {
        const uint replacementGfxObjId = 0x0100ABCDu;
        DayGroupData group = Group(
            [
                Celestial(
                    AuthoredCelestialShadowSourceResolver.DominantMoonGfxObjId,
                    -Vector3.UnitX),
            ],
            Replacements(
                0f,
                Replace(
                    0,
                    gfxObjId: replacementGfxObjId,
                    transparent: 0.35f,
                    sortCenter: Vector3.UnitX)));

        AuthoredCelestialShadowSource result = Resolve(group, 0.5f);

        Assert.Equal(AuthoredCelestialShadowSourceKind.DominantMoon, result.Kind);
        Assert.Equal(replacementGfxObjId, result.GfxObjId);
        AssertVectorClose(Vector3.UnitZ, result.SurfaceToLightDirection);
    }

    [Fact]
    public void ReplacementRotation_IsAppliedBeforeTheSkyArcRotation()
    {
        DayGroupData group = Group(
            [
                Celestial(
                    AuthoredCelestialShadowSourceResolver.SunGfxObjId,
                    Vector3.UnitY),
            ],
            Replacements(0f, Replace(0, rotate: 90f)));

        AuthoredCelestialShadowSource result = Resolve(group, 0.5f);

        Assert.Equal(AuthoredCelestialShadowSourceKind.Sun, result.Kind);
        AssertVectorClose(Vector3.UnitZ, result.SurfaceToLightDirection);
        AssertClose(1f, result.ElevationSin);
    }

    [Fact]
    public void SkyTransformDirection_MatchesTheAuthoredAnalyticTransform()
    {
        DayGroupData group = Group(
            [
                Celestial(
                    AuthoredCelestialShadowSourceResolver.SecondaryMoonGfxObjId,
                    new Vector3(2f, 1f, 3f),
                    beginAngle: 0f,
                    endAngle: 80f,
                    beginTime: 0f,
                    endTime: 1f),
            ],
            Replacements(0f, Replace(0, rotate: 30f)));

        AuthoredCelestialShadowSource result = Resolve(group, 0.5f);

        Vector3 expected = new(
            -0.05839998f,
            -0.03580622f,
            0.99765092f);
        Assert.Equal(AuthoredCelestialShadowSourceKind.SecondaryMoon, result.Kind);
        AssertVectorClose(expected, result.SurfaceToLightDirection);
        AssertClose(expected.Z, result.ElevationSin);
    }

    [Fact]
    public void NoVisibleOrAboveHorizonCandidate_ReturnsNone()
    {
        DayGroupData group = Group(
            Celestial(
                AuthoredCelestialShadowSourceResolver.SunGfxObjId,
                Vector3.UnitX,
                beginAngle: 90f,
                endAngle: 90f,
                beginTime: 0.1f,
                endTime: 0.2f),
            Celestial(
                AuthoredCelestialShadowSourceResolver.DominantMoonGfxObjId,
                Vector3.UnitX,
                beginAngle: -10f),
            Celestial(
                AuthoredCelestialShadowSourceResolver.SecondaryMoonGfxObjId,
                Vector3.UnitX,
                beginAngle: 0f));

        AuthoredCelestialShadowSource result = Resolve(group, 0.5f);

        Assert.False(result.IsAvailable);
        Assert.Equal(AuthoredCelestialShadowSourceKind.None, result.Kind);
        Assert.Equal(-1, result.ObjectIndex);
        Assert.Equal(0u, result.GfxObjId);
    }

    [Fact]
    public void MidnightWrap_IsVisibleOnBothSidesAndNotAtMidday()
    {
        DayGroupData group = Group(
            Celestial(
                AuthoredCelestialShadowSourceResolver.SecondaryMoonGfxObjId,
                Vector3.UnitX,
                beginAngle: 80f,
                endAngle: 100f,
                beginTime: 0.9f,
                endTime: 0.1f));

        AuthoredCelestialShadowSource beforeMidnight = Resolve(group, 0.95f);
        AuthoredCelestialShadowSource afterMidnight = Resolve(group, 0.05f);
        AuthoredCelestialShadowSource midday = Resolve(group, 0.5f);

        Assert.Equal(
            AuthoredCelestialShadowSourceKind.SecondaryMoon,
            beforeMidnight.Kind);
        Assert.Equal(
            AuthoredCelestialShadowSourceKind.SecondaryMoon,
            afterMidnight.Kind);
        Assert.True(beforeMidnight.ElevationSin > 0.99f);
        Assert.True(afterMidnight.ElevationSin > 0.99f);
        Assert.Equal(AuthoredCelestialShadowSourceKind.None, midday.Kind);
    }

    [Fact]
    public void AuthoredEnergy_ComesFromDirectionalColorTimesBrightness()
    {
        DayGroupData group = Group(
            Celestial(
                AuthoredCelestialShadowSourceResolver.SunGfxObjId,
                Vector3.UnitX));
        SkyKeyframe sky = Sky(
            dirColor: new Vector3(0.4f, 0.8f, 0.2f),
            dirBright: 0.5f);

        AuthoredCelestialShadowSource selected =
            AuthoredCelestialShadowSourceResolver.Resolve(group, 0.5f, in sky);
        AuthoredCelestialShadowSource noCandidate =
            AuthoredCelestialShadowSourceResolver.Resolve(null, 0.5f, in sky);

        AssertClose(0.4f, selected.AuthoredEnergy);
        AssertClose(0.4f, noCandidate.AuthoredEnergy);
    }

    private static AuthoredCelestialShadowSource Resolve(
        DayGroupData group,
        float dayFraction)
    {
        SkyKeyframe sky = Sky();
        return AuthoredCelestialShadowSourceResolver.Resolve(
            group,
            dayFraction,
            in sky);
    }

    private static DayGroupData Group(params SkyObjectData[] skyObjects) =>
        Group(skyObjects, []);

    private static DayGroupData Group(
        IReadOnlyList<SkyObjectData> skyObjects,
        params DatSkyKeyframeData[] skyTimes) => new()
    {
        Name = "Synthetic",
        ChanceOfOccur = 1f,
        SkyObjects = skyObjects,
        SkyTimes = skyTimes,
    };

    private static SkyObjectData Celestial(
        uint gfxObjId,
        Vector3 sortCenter,
        float beginAngle = 90f,
        float? endAngle = null,
        float beginTime = 0f,
        float endTime = 0f) => new()
    {
        GfxObjId = gfxObjId,
        AuthoredSortCenter = sortCenter,
        BeginTime = beginTime,
        EndTime = endTime,
        BeginAngle = beginAngle,
        EndAngle = endAngle ?? beginAngle,
    };

    private static DatSkyKeyframeData Replacements(
        float begin,
        params SkyObjectReplaceData[] replacements) => new()
    {
        Keyframe = Sky(begin: begin),
        Replaces = replacements,
    };

    private static SkyObjectReplaceData Replace(
        uint objectIndex,
        uint gfxObjId = 0u,
        float rotate = 0f,
        float transparent = 0f,
        Vector3? sortCenter = null) => new()
    {
        ObjectIndex = objectIndex,
        GfxObjId = gfxObjId,
        Rotate = rotate,
        Transparent = transparent,
        AuthoredSortCenter = sortCenter ?? Vector3.Zero,
    };

    private static SkyKeyframe Sky(
        float begin = 0f,
        Vector3? dirColor = null,
        float dirBright = 1f) => new(
        Begin: begin,
        SunHeadingDeg: 90f,
        SunPitchDeg: 45f,
        DirColor: dirColor ?? Vector3.One,
        DirBright: dirBright,
        AmbColor: new Vector3(0.2f),
        AmbBright: 0.4f,
        FogColor: new Vector3(0.4f),
        FogDensity: 0f);

    private static void AssertVectorClose(Vector3 expected, Vector3 actual)
    {
        AssertClose(expected.X, actual.X);
        AssertClose(expected.Y, actual.Y);
        AssertClose(expected.Z, actual.Z);
    }

    private static void AssertClose(float expected, float actual) =>
        Assert.InRange(MathF.Abs(expected - actual), 0f, 1e-5f);
}
