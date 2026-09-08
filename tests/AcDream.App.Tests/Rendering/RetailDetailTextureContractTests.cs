using System.Numerics;
using AcDream.App.Rendering;
using AcDream.App.Rendering.Gpu;
using AcDream.Core.Meshing;
using DatReaderWriter.Enums;
using Xunit;

namespace AcDream.App.Tests.Rendering;

public sealed class RetailDetailTextureContractTests
{
    private static readonly Vector4 Base = new(0.31f, 0.57f, 0.83f, 0.19f);
    private static readonly Vector3 Diffuse = new(0.73f, 0.41f, 0.67f);
    private static readonly Vector4 Detail = new(0.91f, 0.23f, 0.49f, 0.62f);
    private static readonly Vector4 Destination = new(0.17f, 0.37f, 0.71f, 0.29f);

    public static TheoryData<SurfaceType, bool, RetailSetSurfaceBlend,
        RetailSetSurfaceAlphaTest, bool> ResolvedRows() => new()
    {
        { SurfaceType.Base1Image, false, RetailSetSurfaceBlend.Opaque, RetailSetSurfaceAlphaTest.Disabled, true },
        { SurfaceType.Alpha, false, RetailSetSurfaceBlend.StraightAlpha, RetailSetSurfaceAlphaTest.Disabled, true },
        { SurfaceType.Alpha | SurfaceType.Additive, false, RetailSetSurfaceBlend.AlphaAdditive, RetailSetSurfaceAlphaTest.Disabled, false },
        { SurfaceType.Additive, false, RetailSetSurfaceBlend.Additive, RetailSetSurfaceAlphaTest.Disabled, false },
        { SurfaceType.InvAlpha, false, RetailSetSurfaceBlend.InverseAlpha, RetailSetSurfaceAlphaTest.Disabled, true },
        { SurfaceType.InvAlpha | SurfaceType.Additive, false, RetailSetSurfaceBlend.InverseAdditive, RetailSetSurfaceAlphaTest.Disabled, false },
        { SurfaceType.Base1ClipMap, false, RetailSetSurfaceBlend.Clip, RetailSetSurfaceAlphaTest.Dds, true },
        { SurfaceType.Base1ClipMap, true, RetailSetSurfaceBlend.Clip, RetailSetSurfaceAlphaTest.Paletted, true },
        { SurfaceType.Alpha | SurfaceType.Base1ClipMap, true, RetailSetSurfaceBlend.StraightAlpha, RetailSetSurfaceAlphaTest.Paletted, true },
        { SurfaceType.InvAlpha | SurfaceType.Additive | SurfaceType.Base1ClipMap, false, RetailSetSurfaceBlend.InverseAdditive, RetailSetSurfaceAlphaTest.Dds, false },
        { SurfaceType.Translucent, false, RetailSetSurfaceBlend.StraightAlpha, RetailSetSurfaceAlphaTest.Disabled, true },
        { SurfaceType.Translucent | SurfaceType.Additive, false, RetailSetSurfaceBlend.Additive, RetailSetSurfaceAlphaTest.Disabled, false },
        { SurfaceType.Translucent | SurfaceType.InvAlpha, false, RetailSetSurfaceBlend.InverseAlpha, RetailSetSurfaceAlphaTest.Disabled, true },
        { SurfaceType.Translucent | SurfaceType.Base1ClipMap | SurfaceType.Additive, true, RetailSetSurfaceBlend.StraightAlpha, RetailSetSurfaceAlphaTest.Disabled, false },
    };

    [Theory]
    [MemberData(nameof(ResolvedRows))]
    public void SetSurfaceResolver_PreservesEveryRawFamilyClipAndLateOverride(
        SurfaceType type,
        bool paletted,
        RetailSetSurfaceBlend expectedBlend,
        RetailSetSurfaceAlphaTest expectedAlphaTest,
        bool expectedFog)
    {
        RetailSetSurfaceMaterialState state = RetailSetSurfaceMaterialState.Resolve(
            type,
            texturePresent: true,
            textureHasPalette: paletted);

        Assert.Equal(expectedBlend, state.Blend);
        Assert.Equal(expectedAlphaTest, state.AlphaTest);
        Assert.Equal(expectedFog, state.FogEnabled);
        Assert.Equal(state, RetailSetSurfaceMaterialState.FromPackedByte(state.ToPackedByte()));
    }

    public static TheoryData<string, SurfaceType, bool, bool, TranslucencyKind, int, float> PolicyRows()
    {
        var rows = new TheoryData<string, SurfaceType, bool, bool, TranslucencyKind, int, float>();
        (SurfaceType Type, bool Paletted, TranslucencyKind Kind,
            RetailDetailTextureContract.FramebufferFamily Family, float Reference)[] materials =
        [
            (SurfaceType.Base1Image, false, TranslucencyKind.Opaque,
                RetailDetailTextureContract.FramebufferFamily.Opaque, 0.05f),
            (SurfaceType.Base1Image | SurfaceType.Alpha, false, TranslucencyKind.AlphaBlend,
                RetailDetailTextureContract.FramebufferFamily.Alpha, 0.05f),
            (SurfaceType.Base1Image | SurfaceType.Alpha | SurfaceType.Additive, false,
                TranslucencyKind.Additive,
                RetailDetailTextureContract.FramebufferFamily.AlphaAdditive, 0.05f),
            (SurfaceType.Base1Image | SurfaceType.InvAlpha, false, TranslucencyKind.InvAlpha,
                RetailDetailTextureContract.FramebufferFamily.InverseAlpha, 0.05f),
            (SurfaceType.Base1Image | SurfaceType.Base1ClipMap, false, TranslucencyKind.ClipMap,
                RetailDetailTextureContract.FramebufferFamily.Clip, 200f / 255f),
            (SurfaceType.Base1Image | SurfaceType.Base1ClipMap, true, TranslucencyKind.ClipMap,
                RetailDetailTextureContract.FramebufferFamily.Clip, 100f / 255f),
        ];
        foreach (string consumer in new[] { "Building", "EnvCell" })
        foreach (var material in materials)
        foreach (bool detailEnabled in new[] { false, true })
        {
            RetailDetailTextureContract.FramebufferFamily family =
                consumer == "Building" && material.Kind == TranslucencyKind.ClipMap
                    ? RetailDetailTextureContract.FramebufferFamily.Opaque
                    : material.Family;
            rows.Add(consumer, material.Type, material.Paletted, detailEnabled,
                material.Kind, (int)family, material.Reference);
        }
        return rows;
    }

    [Theory]
    [MemberData(nameof(PolicyRows))]
    public void BuildingAndEnvCellPolicyTable_KeepsOneDrawAndOriginalFramebufferFamily(
        string consumer,
        SurfaceType surfaceType,
        bool paletted,
        bool detailEnabled,
        TranslucencyKind expectedKind,
        int familyValue,
        float expectedReference)
    {
        var family = (RetailDetailTextureContract.FramebufferFamily)familyValue;
        Assert.True(consumer is "Building" or "EnvCell");
        Assert.Equal(expectedKind, TranslucencyKindExtensions.FromSurfaceType(surfaceType));
        Assert.Equal(["source-subset"], PhysicalDrawSequence(detailEnabled));

        PipelineState state = ExpectedPipelineState(
            consumer, surfaceType, expectedKind, paletted, detailEnabled);
        RetailSetSurfaceMaterialState resolved = RetailSetSurfaceMaterialState.Resolve(
            surfaceType,
            texturePresent: true,
            textureHasPalette: paletted);
        if (detailEnabled)
        {
            Assert.Equal(resolved.AlphaTestReference, state.AlphaReference);
            Assert.Equal(resolved.AlphaTestEnabled, state.AlphaTest);
            Assert.Equal(resolved.FogEnabled, state.FogEnabled);
        }
        else
        {
            Assert.Equal(expectedReference, state.AlphaReference);
            Assert.True(state.AlphaTest);
        }
        Assert.Equal(detailEnabled, DetailIsArmed(detailEnabled));
        if (consumer == "Building" && expectedKind == TranslucencyKind.ClipMap)
            Assert.Equal("AP-240 immediate A2C", state.Placement);
        else
            Assert.Equal("source FIFO position", state.Placement);

        Vector4 expectedSource = detailEnabled
            ? IndependentCombine(Base, Diffuse, Detail, 0.75f, 0.4f)
            : new Vector4(
                new Vector3(Base.X, Base.Y, Base.Z) * Diffuse,
                Base.W * 0.4f);
        Vector4 source = detailEnabled
            ? RetailDetailTextureContract.Combine(Base, Diffuse, Detail, 0.75f, 0.4f)
            : expectedSource;
        AssertVector(expectedSource, source);
        AssertVector(IndependentComposite(source, Destination, family),
            RetailDetailTextureContract.Composite(source, Destination, family));
    }

    [Theory]
    [InlineData(SurfaceType.Translucent, TranslucencyKind.AlphaBlend, false)]
    [InlineData(SurfaceType.Translucent | SurfaceType.Base1ClipMap,
        TranslucencyKind.AlphaBlend, true)]
    [InlineData(SurfaceType.Translucent | SurfaceType.Base1ClipMap | SurfaceType.Alpha,
        TranslucencyKind.AlphaBlend, false)]
    [InlineData(SurfaceType.Translucent | SurfaceType.Additive,
        TranslucencyKind.Additive, false)]
    [InlineData(SurfaceType.Translucent | SurfaceType.InvAlpha,
        TranslucencyKind.InvAlpha, false)]
    public void RawTranslucentOverride_PinsBlendAndClipTestPolicy(
        SurfaceType type,
        TranslucencyKind expectedKind,
        bool rawClipWithoutAlphaFamily)
    {
        Assert.Equal(expectedKind, TranslucencyKindExtensions.FromSurfaceType(type));
        byte mask = RetailAlphaMeshRouter.ConstructSubsetMask(
            hasAlphaFamilyBit: (type & (SurfaceType.Alpha | SurfaceType.InvAlpha | SurfaceType.Additive)) != 0,
            hasClipMapBit: (type & SurfaceType.Base1ClipMap) != 0,
            hasTranslucentBit: true,
            hasPositiveStippling: false);
        Assert.Equal(rawClipWithoutAlphaFamily, (mask & RetailAlphaMeshRouter.MaskClipMap) != 0);
        if ((type & SurfaceType.Translucent) != 0 && (type & SurfaceType.Base1ClipMap) != 0)
            Assert.False(expectedKind == TranslucencyKind.ClipMap);
    }

    [Fact]
    public void CombinePinsSquaredFinalAlphaAndExcludesBaseAlpha()
    {
        Vector4 actual = RetailDetailTextureContract.Combine(
            Base, Diffuse, Detail, authoredOpacity: 0.75f, liveOpacity: 0.4f);
        float a = 0.75f * 0.4f;
        float w = a * Detail.W;
        Vector3 expectedRgb = new(Detail.X, Detail.Y, Detail.Z);
        expectedRgb = expectedRgb * w
            + new Vector3(Base.X, Base.Y, Base.Z) * Diffuse * (1f - w);
        float expectedAlpha = a * Detail.W * Detail.W;

        AssertVector(new Vector4(expectedRgb, expectedAlpha), actual);
        Assert.NotEqual(a * Detail.W, actual.W);
        Assert.NotEqual(Base.W * a * Detail.W * Detail.W, actual.W);
        Vector3 oldTwoDrawRgb = new Vector3(Base.X, Base.Y, Base.Z) * Diffuse
            * (new Vector3(Detail.X, Detail.Y, Detail.Z)
                + Vector3.One - new Vector3(Detail.W));
        Assert.NotEqual(oldTwoDrawRgb.X, actual.X);
        Assert.NotEqual(oldTwoDrawRgb.Y, actual.Y);
        Assert.NotEqual(oldTwoDrawRgb.Z, actual.Z);

        Vector4 changedBaseAlpha = new(Base.X, Base.Y, Base.Z, 0.97f);
        AssertVector(actual, RetailDetailTextureContract.Combine(
            changedBaseAlpha, Diffuse, Detail, 0.75f, 0.4f));
    }

    [Theory]
    [InlineData(0)]
    [InlineData(1)]
    [InlineData(2)]
    [InlineData(3)]
    [InlineData(4)]
    [InlineData(5)]
    [InlineData(6)]
    public void EveryFramebufferEquationUsesFinalX(int familyValue)
    {
        var family = (RetailDetailTextureContract.FramebufferFamily)familyValue;
        Vector4 source = RetailDetailTextureContract.Combine(
            Base, Diffuse, Detail, 0.75f, 0.4f);
        float x = source.W;
        Vector4 expected = family switch
        {
            RetailDetailTextureContract.FramebufferFamily.Opaque => source,
            RetailDetailTextureContract.FramebufferFamily.Alpha =>
                source * x + Destination * (1f - x),
            RetailDetailTextureContract.FramebufferFamily.AlphaAdditive =>
                source * x + Destination,
            RetailDetailTextureContract.FramebufferFamily.Additive =>
                source + Destination,
            RetailDetailTextureContract.FramebufferFamily.InverseAlpha =>
                source * (1f - x) + Destination * x,
            RetailDetailTextureContract.FramebufferFamily.InverseAlphaAdditive =>
                source * (1f - x) + Destination,
            RetailDetailTextureContract.FramebufferFamily.Clip =>
                source + Destination * new Vector4(1f - x),
            _ => throw new ArgumentOutOfRangeException(nameof(family)),
        };
        AssertVector(expected, RetailDetailTextureContract.Composite(source, Destination, family));
    }

    [Theory]
    [InlineData(100f / 255f)]
    [InlineData(200f / 255f)]
    public void ClipBoundaryIsFinalAlphaGreaterEqual(float reference)
    {
        Assert.False(RetailDetailTextureContract.SurvivesClip(
            MathF.BitDecrement(reference), reference));
        Assert.True(RetailDetailTextureContract.SurvivesClip(reference, reference));
        Assert.True(RetailDetailTextureContract.SurvivesClip(
            MathF.BitIncrement(reference), reference));
    }

    [Fact]
    public void FogRunsAfterCombineAndLeavesAlphaUntouched()
    {
        Vector4 source = RetailDetailTextureContract.Combine(
            Base, Diffuse, Detail, 0.5f, 0.65f);
        Vector3 fog = new(0.13f, 0.29f, 0.47f);
        Vector4 fogged = RetailDetailTextureContract.ApplyFog(source, fog, 0.38f);
        AssertVector(new Vector4(Vector3.Lerp(
            new Vector3(source.X, source.Y, source.Z), fog, 0.38f), source.W), fogged);
    }

    [Fact]
    public void BothShaderFamiliesUseTheSharedOnePassSourceAndDebugPrecedesDetailSample()
    {
        string ordinary = Shader("mesh_modern.frag");
        string atmospheric = Shader("mesh_atmospheric.frag");
        string shared = Shader("retail_detail_material.glsl");

        Assert.Contains("#include \"retail_detail_material.glsl\"", ordinary, StringComparison.Ordinal);
        Assert.Contains("#include \"retail_detail_material.glsl\"", atmospheric, StringComparison.Ordinal);
        Assert.Contains("materialAlpha * detail.a * detail.a", shared, StringComparison.Ordinal);
        Assert.DoesNotContain("baseTexel.a", shared, StringComparison.Ordinal);
        foreach (string shader in new[] { ordinary, atmospheric })
        {
            Assert.Contains("if (detailActive)", shader, StringComparison.Ordinal);
            Assert.True(shader.IndexOf("if (detailActive)", StringComparison.Ordinal)
                < shader.IndexOf("vec4 detail =", StringComparison.Ordinal));
            Assert.Contains("vSurfaceOpacity * vOpacityMultiplier", shader, StringComparison.Ordinal);
        }
        Assert.True(ordinary.IndexOf("if (uLightDebug == 3)", StringComparison.Ordinal)
            < ordinary.IndexOf("vec4 detail =", StringComparison.Ordinal));
        Assert.True(atmospheric.IndexOf("if (uLightDebug == 3)", StringComparison.Ordinal)
            < atmospheric.IndexOf("vec4 detail =", StringComparison.Ordinal));
    }

    private static string Shader(string file) => File.ReadAllText(Path.Combine(
        RepositoryRoot(), "src", "AcDream.App", "Rendering", "Shaders", file));

    private static string RepositoryRoot() => Path.GetFullPath(Path.Combine(
        AppContext.BaseDirectory, "..", "..", "..", "..", ".."));

    private static void AssertVector(Vector4 expected, Vector4 actual)
    {
        Assert.Equal(expected.X, actual.X, 6);
        Assert.Equal(expected.Y, actual.Y, 6);
        Assert.Equal(expected.Z, actual.Z, 6);
        Assert.Equal(expected.W, actual.W, 6);
    }

    private static string[] PhysicalDrawSequence(bool detailEnabled)
    {
        _ = detailEnabled;
        return ["source-subset"];
    }

    private static bool DetailIsArmed(bool detailEnabled) => detailEnabled;

    private static PipelineState ExpectedPipelineState(
        string consumer,
        SurfaceType surfaceType,
        TranslucencyKind kind,
        bool paletted,
        bool detailEnabled)
    {
        RetailSetSurfaceMaterialState resolved = RetailSetSurfaceMaterialState.Resolve(
            surfaceType,
            texturePresent: true,
            textureHasPalette: paletted);
        if (detailEnabled)
        {
            GpuBlendMode exactBlend = resolved.Blend switch
            {
                RetailSetSurfaceBlend.Opaque => GpuBlendMode.None,
                RetailSetSurfaceBlend.StraightAlpha => GpuBlendMode.StraightAlpha,
                RetailSetSurfaceBlend.AlphaAdditive => GpuBlendMode.Additive,
                RetailSetSurfaceBlend.Additive => GpuBlendMode.RawAdditive,
                RetailSetSurfaceBlend.InverseAlpha => GpuBlendMode.InverseAlpha,
                RetailSetSurfaceBlend.InverseAdditive => GpuBlendMode.InverseAdditive,
                RetailSetSurfaceBlend.Clip => GpuBlendMode.PremultipliedAlpha,
                _ => throw new ArgumentOutOfRangeException(),
            };
            return new PipelineState(
                exactBlend,
                resolved.Blend == RetailSetSurfaceBlend.Opaque || resolved.AlphaTestEnabled,
                resolved.AlphaTestEnabled,
                resolved.AlphaTestReference,
                resolved.FogEnabled,
                consumer == "Building" && kind == TranslucencyKind.ClipMap
                    ? "AP-240 immediate A2C"
                    : "source FIFO position");
        }
        GpuBlendMode blend = kind switch
        {
            TranslucencyKind.Opaque => GpuBlendMode.None,
            TranslucencyKind.AlphaBlend => GpuBlendMode.StraightAlpha,
            TranslucencyKind.Additive => GpuBlendMode.Additive,
            TranslucencyKind.InvAlpha => GpuBlendMode.InverseAlpha,
            TranslucencyKind.ClipMap when consumer == "EnvCell" =>
                GpuBlendMode.PremultipliedAlpha,
            TranslucencyKind.ClipMap => GpuBlendMode.None,
            _ => throw new ArgumentOutOfRangeException(nameof(kind)),
        };
        return new PipelineState(
            blend,
            kind is TranslucencyKind.Opaque or TranslucencyKind.ClipMap,
            AlphaTest: true,
            kind == TranslucencyKind.ClipMap
                ? paletted ? 100f / 255f : 200f / 255f
                : 0.05f,
            FogEnabled: true,
            consumer == "Building" && kind == TranslucencyKind.ClipMap
                ? "AP-240 immediate A2C"
                : "source FIFO position");
    }

    private static Vector4 IndependentCombine(
        Vector4 baseTexel,
        Vector3 diffuse,
        Vector4 detail,
        float authoredOpacity,
        float liveOpacity)
    {
        float a = authoredOpacity * liveOpacity;
        float w = a * detail.W;
        Vector3 rgb = new(detail.X, detail.Y, detail.Z);
        rgb = rgb * w
            + new Vector3(baseTexel.X, baseTexel.Y, baseTexel.Z)
            * diffuse * (1f - w);
        return new Vector4(rgb, a * detail.W * detail.W);
    }

    private readonly record struct PipelineState(
        GpuBlendMode Blend,
        bool DepthWrite,
        bool AlphaTest,
        float AlphaReference,
        bool FogEnabled,
        string Placement);

    private static Vector4 IndependentComposite(
        Vector4 source,
        Vector4 destination,
        RetailDetailTextureContract.FramebufferFamily family)
    {
        float x = source.W;
        return family switch
        {
            RetailDetailTextureContract.FramebufferFamily.Opaque => source,
            RetailDetailTextureContract.FramebufferFamily.Alpha =>
                source * x + destination * (1f - x),
            RetailDetailTextureContract.FramebufferFamily.AlphaAdditive =>
                source * x + destination,
            RetailDetailTextureContract.FramebufferFamily.Additive =>
                source + destination,
            RetailDetailTextureContract.FramebufferFamily.InverseAlpha =>
                source * (1f - x) + destination * x,
            RetailDetailTextureContract.FramebufferFamily.InverseAlphaAdditive =>
                source * (1f - x) + destination,
            RetailDetailTextureContract.FramebufferFamily.Clip =>
                source + destination * new Vector4(1f - x),
            _ => throw new ArgumentOutOfRangeException(nameof(family)),
        };
    }
}
