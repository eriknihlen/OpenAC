using AcDream.App.Rendering;

namespace AcDream.App.Tests.Rendering;

public sealed class FramePacingPolicyTests
{
    [Fact]
    public void VSync_request_uses_driver_swap_without_software_limit()
    {
        var policy = FramePacingPolicy.Resolve(
            requestedVSync: true,
            uncappedRendering: false,
            monitorRefreshHz: 144);

        Assert.True(policy.UseVSync);
        Assert.Null(policy.SoftwareLimitHz);
    }

    [Theory]
    [InlineData(60)]
    [InlineData(120)]
    [InlineData(144)]
    [InlineData(240)]
    public void Normal_VSync_off_uses_active_monitor_refresh_limit(int refreshHz)
    {
        var policy = FramePacingPolicy.Resolve(
            requestedVSync: false,
            uncappedRendering: false,
            monitorRefreshHz: refreshHz);

        Assert.False(policy.UseVSync);
        Assert.Equal((double)refreshHz, policy.SoftwareLimitHz);
    }

    [Theory]
    [InlineData(null)]
    [InlineData(0)]
    [InlineData(-1)]
    public void Missing_or_invalid_monitor_refresh_uses_safe_fallback(int? refreshHz)
    {
        var policy = FramePacingPolicy.Resolve(
            requestedVSync: false,
            uncappedRendering: false,
            monitorRefreshHz: refreshHz);

        Assert.False(policy.UseVSync);
        Assert.Equal(FramePacingPolicy.FallbackRefreshHz, policy.SoftwareLimitHz);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void Explicit_uncapped_diagnostic_disables_both_pacing_mechanisms(
        bool requestedVSync)
    {
        var policy = FramePacingPolicy.Resolve(
            requestedVSync,
            uncappedRendering: true,
            monitorRefreshHz: 144);

        Assert.False(policy.UseVSync);
        Assert.Null(policy.SoftwareLimitHz);
    }
}
