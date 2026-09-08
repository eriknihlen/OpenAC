using AcDream.Core.Rendering.Wb;

namespace AcDream.Core.Tests.Rendering.Wb;

public sealed class EnvCellGeometryIdentityTests
{
    [Fact]
    public void Compute_EmptySurfaces_IsStableGoldenValue()
    {
        Assert.Equal(
            0xEC48_CD4F_7A03_5AD0UL,
            EnvCellGeometryIdentity.Compute(0x42, 7, Array.Empty<ushort>()));
    }

    [Fact]
    public void Compute_OrderedSurfaces_IsStableGoldenValue()
    {
        Assert.Equal(
            0xEE36_FCB2_C067_E94BUL,
            EnvCellGeometryIdentity.Compute(0x42, 7, new ushort[] { 1, 2, 3 }));
    }

    [Fact]
    public void Compute_SurfaceOrderIsPartOfIdentity()
    {
        var forward = EnvCellGeometryIdentity.Compute(1, 1, new ushort[] { 10, 20 });
        var reverse = EnvCellGeometryIdentity.Compute(1, 1, new ushort[] { 20, 10 });

        Assert.NotEqual(forward, reverse);
    }

    [Fact]
    public void Compute_AlwaysUsesDeduplicatedGeometryNamespace()
    {
        var id = EnvCellGeometryIdentity.Compute(
            uint.MaxValue,
            ushort.MaxValue,
            new ushort[] { ushort.MaxValue, ushort.MaxValue });

        Assert.NotEqual(0UL, id & 0x2_0000_0000UL);
        Assert.Equal(0xE000_0000_0000_0000UL, id & 0xF000_0000_0000_0000UL);
    }

    [Fact]
    public void LegacyWorldBuilderCollision_InstalledDatTuples_GetDistinctModernIds()
    {
        ushort[] firstSurfaces = [0x013B, 0x0034];
        ushort[] secondSurfaces = [0x04FC, 0x0034];

        ulong firstLegacy = EnvCellGeometryIdentity.ComputeLegacyWorldBuilder(
            0x0277,
            0,
            firstSurfaces);
        ulong secondLegacy = EnvCellGeometryIdentity.ComputeLegacyWorldBuilder(
            0x0276,
            0,
            secondSurfaces);

        Assert.Equal(0x2_020E_8C13UL, firstLegacy);
        Assert.Equal(firstLegacy, secondLegacy);
        Assert.NotEqual(
            EnvCellGeometryIdentity.Compute(0x0277, 0, firstSurfaces),
            EnvCellGeometryIdentity.Compute(0x0276, 0, secondSurfaces));
    }
}
