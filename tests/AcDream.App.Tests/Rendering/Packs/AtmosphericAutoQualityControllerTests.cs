using AcDream.App.Rendering;
using AcDream.App.Rendering.Packs;

namespace AcDream.App.Tests.Rendering.Packs;

public sealed class AtmosphericAutoQualityControllerTests
{
    [Fact]
    public void Downgrade_requires_long_consecutive_over_budget_window()
    {
        var controller = new AtmosphericAutoQualityController(
            AtmosphericQualityLevel.High);
        var sample = new AtmosphericQualityMeasurement(
            InclusivePackGpuMillisecondsP99: 7.0,
            IncrementalCpuMillisecondsP99: 1.1,
            ResidentGpuBytes: 250L * 1024 * 1024,
            StableFrameBoundary: true);

        for (int i = 0;
             i < AtmosphericAutoQualityController.DowngradeHysteresisFrames - 1;
             i++)
            controller.Observe(in sample);
        Assert.Equal(AtmosphericQualityLevel.High, controller.Snapshot.Current);

        AtmosphericAutoQualitySnapshot changed = controller.Observe(in sample);
        Assert.Equal(AtmosphericQualityLevel.Medium, changed.Current);
        Assert.Equal(AtmosphericAutoQualityController.ChangeCooldownFrames,
            changed.CooldownFramesRemaining);
    }

    [Fact]
    public void Unstable_frames_never_advance_hysteresis()
    {
        var controller = new AtmosphericAutoQualityController(
            AtmosphericQualityLevel.High);
        var sample = new AtmosphericQualityMeasurement(20, 20, 0, false);

        for (int i = 0; i < 1000; i++)
            controller.Observe(in sample);

        Assert.Equal(AtmosphericQualityLevel.High, controller.Snapshot.Current);
        Assert.Equal(0, controller.Snapshot.ConsecutiveOverBudgetFrames);
    }

    [Fact]
    public void Upgrade_requires_nine_hundred_headroom_frames()
    {
        var controller = new AtmosphericAutoQualityController(
            AtmosphericQualityLevel.Low);
        var sample = new AtmosphericQualityMeasurement(
            InclusivePackGpuMillisecondsP99: 0.5,
            IncrementalCpuMillisecondsP99: 0.1,
            ResidentGpuBytes: 16L * 1024 * 1024,
            StableFrameBoundary: true);

        for (int i = 0;
             i < AtmosphericAutoQualityController.UpgradeHysteresisFrames - 1;
             i++)
            controller.Observe(in sample);
        Assert.Equal(AtmosphericQualityLevel.Low, controller.Snapshot.Current);

        Assert.Equal(
            AtmosphericQualityLevel.Medium,
            controller.Observe(in sample).Current);
    }

    [Fact]
    public void LowRequestsWholePackFallbackWithoutDroppingHeadlineSemantics()
    {
        var controller = new AtmosphericAutoQualityController(
            AtmosphericQualityLevel.Low);
        var sample = new AtmosphericQualityMeasurement(100, 100, long.MaxValue, true);

        for (int i = 0;
             i < AtmosphericAutoQualityController.DowngradeHysteresisFrames - 1;
             i++)
            controller.Observe(in sample);

        Assert.Equal(AtmosphericQualityLevel.Low, controller.Snapshot.Current);
        Assert.False(controller.Snapshot.SafeFallbackToRetailRequested);

        AtmosphericAutoQualitySnapshot fallback = controller.Observe(in sample);

        Assert.Equal(AtmosphericQualityLevel.Low, fallback.Current);
        Assert.True(fallback.SafeFallbackToRetailRequested);
        Assert.Equal(
            DirectionalShadowSemantics.Headline,
            DirectionalShadowQuality.For(DirectionalShadowPreset.Low).Semantics);
    }

    [Fact]
    public void PackDeclaredBudgetsAreTheAutomaticQualityAuthority()
    {
        AtmosphericQualityBudget[] budgets =
        [
            new(0.5, 0.1, 16 * 1024 * 1024),
            new(1.0, 0.2, 32 * 1024 * 1024),
            new(1.5, 0.3, 64 * 1024 * 1024),
        ];
        var controller = new AtmosphericAutoQualityController(
            budgets,
            AtmosphericQualityLevel.High);
        var sample = new AtmosphericQualityMeasurement(
            InclusivePackGpuMillisecondsP99: 1.6,
            IncrementalCpuMillisecondsP99: 0.1,
            ResidentGpuBytes: 8 * 1024 * 1024,
            StableFrameBoundary: true);

        for (int i = 0; i < AtmosphericAutoQualityController.DowngradeHysteresisFrames; i++)
            controller.Observe(in sample);

        Assert.Equal(AtmosphericQualityLevel.Medium, controller.Snapshot.Current);
    }
}
