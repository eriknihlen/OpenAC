using AcDream.App.Rendering;

namespace AcDream.App.Tests.Rendering;

public sealed class DirectionalShadowQualityTests
{
    [Theory]
    [InlineData(DirectionalShadowPreset.Low, 2, 768, 72, 4_718_592L)]
    [InlineData(DirectionalShadowPreset.Medium, 3, 1536, 144, 28_311_552L)]
    [InlineData(DirectionalShadowPreset.High, 4, 2048, 240, 67_108_864L)]
    internal void Presets_PreserveHeadlineSemanticsAndPlanningEnvelope(
        DirectionalShadowPreset preset,
        int cascades,
        int resolution,
        float reach,
        long expectedDepthBytes)
    {
        DirectionalShadowQuality quality = DirectionalShadowQuality.For(preset);

        Assert.Equal(cascades, quality.CascadeCount);
        Assert.Equal(resolution, quality.MapResolution);
        Assert.Equal(reach, quality.MaximumReachMeters);
        Assert.Equal(expectedDepthBytes, quality.ApproximateDepthMapBytes);
        Assert.Equal(
            DirectionalShadowSemantics.Headline,
            quality.Semantics & DirectionalShadowSemantics.Headline);
        Assert.True(quality.PackResidentGpuByteBudget >= quality.ApproximateDepthMapBytes);
        Assert.True(quality.IncrementalGpuP50BudgetMilliseconds > 0);
        Assert.True(quality.IncrementalCpuP50BudgetMilliseconds > 0);
    }

    [Fact]
    public void BiasPolicy_ProducesFiniteWorldUnitOffsetsThatScaleWithTexelFootprint()
    {
        DirectionalShadowBiasPolicy policy =
            DirectionalShadowQuality.For(DirectionalShadowPreset.Medium).BiasPolicy;

        DirectionalShadowWorldBias near = policy.Resolve(0.02f);
        DirectionalShadowWorldBias far = policy.Resolve(0.20f);

        Assert.InRange(near.ConstantDepthMeters, policy.MinimumMeters, policy.MaximumMeters);
        Assert.InRange(near.SlopeDepthMeters, policy.MinimumMeters, policy.MaximumMeters);
        Assert.InRange(near.NormalOffsetMeters, policy.MinimumMeters, policy.MaximumMeters);
        Assert.True(far.ConstantDepthMeters > near.ConstantDepthMeters);
        Assert.True(far.SlopeDepthMeters > near.SlopeDepthMeters);
        Assert.True(far.NormalOffsetMeters > near.NormalOffsetMeters);
    }

    [Theory]
    [InlineData(DirectionalShadowPreset.Low)]
    [InlineData(DirectionalShadowPreset.Medium)]
    [InlineData(DirectionalShadowPreset.High)]
    internal void Presets_KeepShaderPinnedMinimumWorldBias(
        DirectionalShadowPreset preset) =>
        Assert.Equal(
            0.001f,
            DirectionalShadowQuality.For(preset).BiasPolicy.MinimumMeters);
}
