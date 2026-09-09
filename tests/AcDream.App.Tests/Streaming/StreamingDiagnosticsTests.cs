using AcDream.App.Streaming;

namespace AcDream.App.Tests.Streaming;

public sealed class StreamingDiagnosticsTests
{
    [Theory]
    [InlineData(null, null)]
    [InlineData("", null)]
    [InlineData("0", null)]
    [InlineData("garbage", null)]
    [InlineData("1", StreamingDiagnostics.DefaultTunnelFreezeFrame)]
    [InlineData("true", StreamingDiagnostics.DefaultTunnelFreezeFrame)]
    [InlineData("TRUE", StreamingDiagnostics.DefaultTunnelFreezeFrame)]
    [InlineData("2", 2)]
    [InlineData("72", 72)]
    [InlineData("120", 120)]
    [InlineData("121", null)]
    public void TunnelFreezeParser_AcceptsTheDefaultAliasOrAnAuthoredFrame(
        string? raw,
        int? expected) =>
        Assert.Equal(expected, StreamingDiagnostics.ParseTunnelFreezeFrame(raw));
}
