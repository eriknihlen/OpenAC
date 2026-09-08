using AcDream.Core.Meshing;
using DatReaderWriter.Enums;

namespace AcDream.Core.Tests.Meshing;

public sealed class RetailUntexturedSurfacePolicyTests
{
    [Theory]
    [InlineData(SurfaceType.Base1Solid, true)]
    [InlineData((SurfaceType)0, true)] // neither bit set — untextured
    [InlineData(SurfaceType.Base1Image, false)] // BASE1_IMAGE (0x2) — textured
    [InlineData(SurfaceType.Base1ClipMap, false)] // BASE1_CLIPMAP (0x4) — textured
    [InlineData(SurfaceType.Base1Image | SurfaceType.Base1Solid, false)]
    [InlineData(SurfaceType.Base1Image | SurfaceType.Additive, false)] // unrelated flags alongside a textured bit stay textured
    public void IsUntextured_MatchesRetailBitmask(SurfaceType type, bool expected)
    {
        Assert.Equal(expected, RetailUntexturedSurfacePolicy.IsUntextured(type));
    }

    [Theory]
    // (isBuildingShell, isUntextured) -> draws
    [InlineData(false, true, true)]
    [InlineData(true, true, false)]
    [InlineData(false, false, true)] // ordinary object, textured subset — always drawn
    [InlineData(true, false, true)] // building shell, textured subset — the shell gate only touches untextured subsets
    public void Draws_MatchesRetailBuildingShellGate(
        bool isBuildingShell,
        bool isUntextured,
        bool expectedDraws)
    {
        Assert.Equal(
            expectedDraws,
            RetailUntexturedSubsetPolicy.Draws(isBuildingShell, isUntextured));
    }
}
