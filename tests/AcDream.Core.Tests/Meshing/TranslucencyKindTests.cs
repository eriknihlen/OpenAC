using AcDream.Core.Meshing;
using DatReaderWriter.Enums;

namespace AcDream.Core.Tests.Meshing;

public class TranslucencyKindTests
{
    [Fact]
    public void Opaque_FromZeroFlags_ReturnsOpaque()
        => Assert.Equal(TranslucencyKind.Opaque, TranslucencyKindExtensions.FromSurfaceType((SurfaceType)0));

    [Fact]
    public void Opaque_FromBase1SolidFlag_ReturnsOpaque()
        => Assert.Equal(TranslucencyKind.Opaque, TranslucencyKindExtensions.FromSurfaceType(SurfaceType.Base1Solid));

    [Fact]
    public void Opaque_FromBase1ImageFlag_ReturnsOpaque()
        => Assert.Equal(TranslucencyKind.Opaque, TranslucencyKindExtensions.FromSurfaceType(SurfaceType.Base1Image));

    [Fact]
    public void ClipMap_FromBase1ClipMapFlag_ReturnsClipMap()
        => Assert.Equal(TranslucencyKind.ClipMap, TranslucencyKindExtensions.FromSurfaceType(SurfaceType.Base1ClipMap));

    [Fact]
    public void ClipMap_WithOtherOpaqueFlags_ReturnsClipMap()
        => Assert.Equal(TranslucencyKind.ClipMap,
            TranslucencyKindExtensions.FromSurfaceType(SurfaceType.Base1ClipMap | SurfaceType.Gouraud));

    [Fact]
    public void AlphaBlend_FromAlphaFlag_ReturnsAlphaBlend()
        => Assert.Equal(TranslucencyKind.AlphaBlend, TranslucencyKindExtensions.FromSurfaceType(SurfaceType.Alpha));

    [Fact]
    public void AlphaBlend_FromTranslucentFlag_ReturnsAlphaBlend()
        => Assert.Equal(TranslucencyKind.AlphaBlend, TranslucencyKindExtensions.FromSurfaceType(SurfaceType.Translucent));

    [Fact]
    public void AlphaBlend_FromAlphaAndTranslucentFlags_ReturnsAlphaBlend()
        => Assert.Equal(TranslucencyKind.AlphaBlend,
            TranslucencyKindExtensions.FromSurfaceType(SurfaceType.Alpha | SurfaceType.Translucent));

    [Fact]
    public void AlphaBlend_AlphaWithClipMap_AlphaWins()
        => Assert.Equal(TranslucencyKind.AlphaBlend,
            TranslucencyKindExtensions.FromSurfaceType(SurfaceType.Alpha | SurfaceType.Base1ClipMap));

    [Fact]
    public void AlphaBlend_TranslucentClipMapAdditiveCloud_ReturnsAlphaBlend()
        => Assert.Equal(TranslucencyKind.AlphaBlend,
            TranslucencyKindExtensions.FromSurfaceType(
                SurfaceType.Base1ClipMap
                | SurfaceType.Translucent
                | SurfaceType.Alpha
                | SurfaceType.Additive));

    [Fact]
    public void InvAlpha_FromInvAlphaFlag_ReturnsInvAlpha()
        => Assert.Equal(TranslucencyKind.InvAlpha, TranslucencyKindExtensions.FromSurfaceType(SurfaceType.InvAlpha));

    [Fact]
    public void InvAlpha_InvAlphaBeatsAlpha()
        => Assert.Equal(TranslucencyKind.InvAlpha,
            TranslucencyKindExtensions.FromSurfaceType(SurfaceType.InvAlpha | SurfaceType.Alpha));

    [Fact]
    public void Additive_FromAdditiveFlag_ReturnsAdditive()
        => Assert.Equal(TranslucencyKind.Additive, TranslucencyKindExtensions.FromSurfaceType(SurfaceType.Additive));

    [Fact]
    public void Additive_AdditiveBeatsNonTranslucentBlendFlags()
        => Assert.Equal(TranslucencyKind.Additive,
            TranslucencyKindExtensions.FromSurfaceType(
                SurfaceType.Additive | SurfaceType.InvAlpha | SurfaceType.Alpha | SurfaceType.Base1ClipMap));

    [Fact]
    public void OpacityFromSurfaceTranslucency_NonTranslucentIgnoresRawValue()
    {
        Assert.Equal(1f, TranslucencyKindExtensions.OpacityFromSurfaceTranslucency(SurfaceType.Base1Image, 0f));
        Assert.Equal(1f, TranslucencyKindExtensions.OpacityFromSurfaceTranslucency(SurfaceType.Base1Image, 0.75f));
    }

    [Fact]
    public void OpacityFromSurfaceTranslucency_TranslucentInvertsAndClamps()
    {
        Assert.Equal(1f, TranslucencyKindExtensions.OpacityFromSurfaceTranslucency(SurfaceType.Translucent, -0.25f));
        Assert.Equal(0.75f, TranslucencyKindExtensions.OpacityFromSurfaceTranslucency(SurfaceType.Translucent, 0.25f));
        Assert.Equal(0f, TranslucencyKindExtensions.OpacityFromSurfaceTranslucency(SurfaceType.Translucent, 1.25f));
    }

    [Fact]
    public void DisablesFixedFunctionFog_RawAdditiveEvenWhenBlendForcedToAlpha()
    {
        var cloud = SurfaceType.Base1ClipMap
            | SurfaceType.Translucent
            | SurfaceType.Alpha
            | SurfaceType.Additive;

        Assert.Equal(TranslucencyKind.AlphaBlend, TranslucencyKindExtensions.FromSurfaceType(cloud));
        Assert.True(TranslucencyKindExtensions.DisablesFixedFunctionFog(cloud));
    }
}
