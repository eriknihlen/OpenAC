using AcDream.Core.Rendering;
using Xunit;

namespace AcDream.Core.Tests.Rendering;

public sealed class RenderingDiagnosticsTests
{
    [Theory]
    [InlineData(0x00000029ul, false)]
    [InlineData(0xA9B40029ul, false)]
    [InlineData(0x00000100ul, true)]
    [InlineData(0xA9B40105ul, true)]
    public void IsEnvCellId_DistinguishesOutdoorVsIndoorByLow16Bits(ulong id, bool expected)
        => Assert.Equal(expected, RenderingDiagnostics.IsEnvCellId(id));

    [Theory]
    [InlineData(0xA9B40031u, true, false)]
    [InlineData(0xA9B40171u, true, true)]
    [InlineData(0xA9B40171u, false, false)]
    [InlineData(0u, true, false)]
    public void ShouldRenderIndoor_UsesPlayerCellAndResolvedRoot(
        uint playerCellId,
        bool renderRootResolved,
        bool expected)
        => Assert.Equal(
            expected,
            RenderingDiagnostics.ShouldRenderIndoor(playerCellId, renderRootResolved));
}
