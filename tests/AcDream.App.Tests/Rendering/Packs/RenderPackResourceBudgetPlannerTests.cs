using AcDream.App.Rendering.Packs;
using AcDream.App.Rendering.Wb;

namespace AcDream.App.Tests.Rendering.Packs;

public sealed class RenderPackResourceBudgetPlannerTests
{
    private const long MiB = 1024L * 1024L;

    [Fact]
    public void Low_1080p_resolves_actual_images_below_its_64_mib_ceiling()
    {
        var descriptor = BuiltInAtmosphericRenderPack.Descriptor;
        var preset = descriptor.QualityPresets.Single(value => value.Id == "low");

        RenderPackResourceBudget budget = RenderPackResourceBudgetPlanner
            .RequireWithinPreset(descriptor, preset, 1920, 1080, sampleCount: 1);

        Assert.InRange(budget.RetainedGpuBytes, 40L * MiB, 42L * MiB);
        Assert.Equal(0, budget.MultisampleGpuBytes);
        Assert.Equal(2, budget.LargestImageLayerCount);
        Assert.Equal(1920, budget.LargestImageWidth);
    }

    [Fact]
    public void Low_1440pFundsQuarterResolutionBloomAndPreAdmitsBothTransformFlights()
    {
        var descriptor = BuiltInAtmosphericRenderPack.Descriptor;
        var preset = descriptor.QualityPresets.Single(value => value.Id == "low");

        RenderPackResourceBudget budget = RenderPackResourceBudgetPlanner
            .RequireWithinPreset(descriptor, preset, 2560, 1440, sampleCount: 1);

        long expected = checked(
            2560L * 1440L * 12L
            + 768L * 768L * 2L * sizeof(float)
            + 640L * 360L * (8L + 8L + 4L + 8L + 8L)
            + 2L * WorldTransformCapacityPolicy.InitialBindingSizeBytes);
        Assert.Equal(expected, budget.RetainedGpuBytes);
        Assert.InRange(budget.RetainedGpuBytes, 62L * MiB, 64L * MiB);
        Assert.True(budget.RetainedGpuBytes <= preset.MaxResidentGpuBytes);
        Assert.Equal(2560, budget.LargestImageWidth);
        Assert.Equal(1440, budget.LargestImageHeight);
    }

    [Fact]
    public void Medium_1080p_tracks_multisample_bytes_separately_from_resident_ceiling()
    {
        var descriptor = BuiltInAtmosphericRenderPack.Descriptor;
        var preset = descriptor.QualityPresets.Single(value => value.Id == "medium");

        RenderPackResourceBudget budget = RenderPackResourceBudgetPlanner
            .RequireWithinPreset(descriptor, preset, 1920, 1080, sampleCount: 2);

        Assert.True(budget.RetainedGpuBytes < 128L * MiB);
        Assert.Equal(1920L * 1080L * 12L * 2L, budget.MultisampleGpuBytes);
        Assert.Equal(
            checked(budget.RetainedGpuBytes + budget.MultisampleGpuBytes),
            budget.TotalGpuBytes);
        Assert.Equal(3, budget.LargestImageLayerCount);
    }

    [Fact]
    public void Four_k_rejects_low_before_size_dependent_allocation()
    {
        var descriptor = BuiltInAtmosphericRenderPack.Descriptor;
        var preset = descriptor.QualityPresets.Single(value => value.Id == "low");

        NotSupportedException error = Assert.Throws<NotSupportedException>(() =>
            RenderPackResourceBudgetPlanner.RequireWithinPreset(
                descriptor,
                preset,
                3840,
                2160,
                sampleCount: 4));

        Assert.Contains("low", error.Message, StringComparison.OrdinalIgnoreCase);
        Assert.Contains("3840x2160", error.Message, StringComparison.Ordinal);
        Assert.Contains("resident GPU bytes", error.Message, StringComparison.Ordinal);
    }

    [Fact]
    public void Four_k_high_remains_available_and_records_all_four_cascades()
    {
        var descriptor = BuiltInAtmosphericRenderPack.Descriptor;
        var preset = descriptor.QualityPresets.Single(value => value.Id == "high");

        RenderPackResourceBudget budget = RenderPackResourceBudgetPlanner
            .RequireWithinPreset(descriptor, preset, 3840, 2160, sampleCount: 4);

        Assert.True(budget.RetainedGpuBytes <= 256L * MiB);
        Assert.Equal(4, budget.LargestImageLayerCount);
        Assert.Equal(3840, budget.LargestImageWidth);
        Assert.Equal(2160, budget.LargestImageHeight);
    }
}
