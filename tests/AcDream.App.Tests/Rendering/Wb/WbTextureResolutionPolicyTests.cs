using AcDream.App.Rendering.Wb;

namespace AcDream.App.Tests.Rendering.Wb;

public sealed class WbTextureResolutionPolicyTests
{
    [Fact]
    public void BakedDefaultPaletteAndRuntimePaletteOverlayKeepDistinctOwnership()
    {
        Assert.Equal(
            WbTextureResolutionKind.SharedAtlas,
            WbTextureResolutionPolicy.Select(
                hasOriginalTextureOverride: false,
                hasPaletteOverride: false,
                sourceIsPaletteIndexed: true));
        Assert.Equal(
            WbTextureResolutionKind.PaletteComposite,
            WbTextureResolutionPolicy.Select(
                hasOriginalTextureOverride: false,
                hasPaletteOverride: true,
                sourceIsPaletteIndexed: true));
    }

    [Theory]
    [InlineData(false, false, false, (int)WbTextureResolutionKind.SharedAtlas)]
    [InlineData(false, true, false, (int)WbTextureResolutionKind.SharedAtlas)]
    [InlineData(true, false, false, (int)WbTextureResolutionKind.OriginalTextureOverride)]
    [InlineData(true, true, false, (int)WbTextureResolutionKind.OriginalTextureOverride)]
    [InlineData(false, true, true, (int)WbTextureResolutionKind.PaletteComposite)]
    [InlineData(true, true, true, (int)WbTextureResolutionKind.PaletteComposite)]
    public void Select_MatchesRetailImageOwnership(
        bool hasOriginalTextureOverride,
        bool hasPaletteOverride,
        bool sourceIsPaletteIndexed,
        int expected)
    {
        Assert.Equal((WbTextureResolutionKind)expected, WbTextureResolutionPolicy.Select(
            hasOriginalTextureOverride,
            hasPaletteOverride,
            sourceIsPaletteIndexed));
    }
}
