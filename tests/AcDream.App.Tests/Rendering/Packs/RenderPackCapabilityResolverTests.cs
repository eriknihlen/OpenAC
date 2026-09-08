using AcDream.App.Plugins;
using AcDream.App.Rendering.Packs;
using AcDream.App.Tests.Rendering.Gpu;
using AcDream.Plugin.Abstractions.Rendering;

namespace AcDream.App.Tests.Rendering.Packs;

public sealed class RenderPackCapabilityResolverTests
{
    private const long MiB = 1024L * 1024L;

    [Fact]
    public void ResolverCarriesActualAdapterLimitsAndAppliesTheDocumentedMemoryShare()
    {
        using var baseline = new RecordingGpuDevice();
        using var device = new RecordingGpuDevice
        {
            Capabilities = baseline.Capabilities with
            {
                MaxImageDimension2D = 1536,
                MaxImageArrayLayers = 2,
                DeviceLocalMemoryBytes = 512UL * 1024 * 1024,
            },
        };

        RenderPackHostCapabilities host = RenderPackCapabilityResolver.Resolve(
            device.Capabilities);

        Assert.Equal(1536, host.MaxImageDimension2D);
        Assert.Equal(2, host.MaxImageArrayLayers);
        Assert.Equal(64L * MiB, host.MaxPackResidentBytes);
        Assert.Equal(64L * MiB, host.MaxPackTransientBytes);
        Assert.Contains(
            RenderCapability.AuthoredCelestialDirectionalLight,
            host.Available);
        Assert.Contains("one eighth", host.MemoryPolicyDescription, StringComparison.Ordinal);
    }

    [Fact]
    public void PresetCompatibilityNamesTheExactArrayLimitAndKeepsLowAvailable()
    {
        RenderPackDescriptor descriptor = BuiltInAtmosphericRenderPack.Descriptor;
        var host = new RenderPackHostCapabilities(
            Enum.GetValues<RenderCapability>().ToHashSet(),
            MaxImageDimension2D: 4096,
            MaxImageArrayLayers: 2,
            MaxPackResidentBytes: 256L * MiB);

        RenderPackValidationResult low = RenderPackValidator.ValidatePresetCompatibility(
            descriptor,
            descriptor.QualityPresets.Single(value => value.Semantic == RenderQualitySemantic.Low),
            host);
        RenderPackValidationResult medium = RenderPackValidator.ValidatePresetCompatibility(
            descriptor,
            descriptor.QualityPresets.Single(value => value.Semantic == RenderQualitySemantic.Medium),
            host);

        Assert.True(low.Success, low.Reason);
        Assert.False(medium.Success);
        Assert.Equal(
            "Preset 'medium' resource 'directional-shadow-depth' needs 3 image-array layers; "
            + "this device provides 2.",
            medium.Reason);
    }

    [Fact]
    public void RuntimeBudgetRejectsResolvedRelativeExtentBeforeAllocation()
    {
        RenderPackDescriptor descriptor = BuiltInAtmosphericRenderPack.Descriptor;
        RenderQualityPreset low = descriptor.QualityPresets.Single(value =>
            value.Semantic == RenderQualitySemantic.Low);
        var host = new RenderPackHostCapabilities(
            Enum.GetValues<RenderCapability>().ToHashSet(),
            MaxImageDimension2D: 1024,
            MaxImageArrayLayers: 2,
            MaxPackResidentBytes: 256L * MiB);

        NotSupportedException error = Assert.Throws<NotSupportedException>(() =>
            RenderPackResourceBudgetPlanner.RequireWithinHost(
                descriptor,
                low,
                1920,
                1080,
                sampleCount: 1,
                host));

        Assert.Contains("1920x1080", error.Message, StringComparison.Ordinal);
        Assert.Contains("maximum 2-D image edge is 1024", error.Message, StringComparison.Ordinal);
    }

    [Fact]
    public void CatalogKeepsPackVisibleAndPublishesPerPresetUnavailableReasons()
    {
        RenderPackDescriptor descriptor = BuiltInAtmosphericRenderPack.Descriptor;
        using var registry = new BufferedRenderPackRegistry();
        using IDisposable registration = registry.Register(descriptor, new EmptyAssets());
        var host = new RenderPackHostCapabilities(
            Enum.GetValues<RenderCapability>().ToHashSet(),
            MaxImageDimension2D: 4096,
            MaxImageArrayLayers: 2,
            MaxPackResidentBytes: 64L * MiB,
            MemoryPolicyDescription: "test 512-MiB adapter policy");

        RenderPackCatalog catalog = RenderPackCatalog.Build(registry.Snapshot(), host);
        Assert.True(catalog.TryGet(descriptor.Id, out RenderPackCatalogEntry entry));
        Assert.True(entry.IsCompatible, entry.IncompatibilityReason);
        Assert.Null(entry.PresetIncompatibilityReasons["low"]);
        Assert.Contains(
            "declares a 134217728-byte resident GPU ceiling",
            entry.PresetIncompatibilityReasons["medium"],
            StringComparison.Ordinal);
        Assert.Contains(
            "declares a 268435456-byte resident GPU ceiling",
            entry.PresetIncompatibilityReasons["high"],
            StringComparison.Ordinal);
    }

    [Fact]
    public void MissingGpuTimestampsDisablesOnlyAutoAndLeavesExplicitLowAvailable()
    {
        RenderPackDescriptor descriptor = BuiltInAtmosphericRenderPack.Descriptor;
        HashSet<RenderCapability> available = Enum.GetValues<RenderCapability>().ToHashSet();
        available.Remove(RenderCapability.GpuTimestampQueries);
        var host = new RenderPackHostCapabilities(available, 4096, 4, 256L * MiB);

        RenderPackValidationResult low = RenderPackValidator.ValidatePresetCompatibility(
            descriptor,
            descriptor.QualityPresets.Single(value => value.Semantic == RenderQualitySemantic.Low),
            host);
        RenderPackValidationResult auto = RenderPackValidator.ValidatePresetCompatibility(
            descriptor,
            descriptor.QualityPresets.Single(value => value.Semantic == RenderQualitySemantic.Automatic),
            host);

        Assert.True(low.Success, low.Reason);
        Assert.False(auto.Success);
        Assert.Contains("asynchronous GPU timestamp queries", auto.Reason, StringComparison.Ordinal);
        Assert.Contains("explicit Low remains available", auto.Reason, StringComparison.Ordinal);
    }

    [Fact]
    public void MissingMultiviewMakesHintedLowUnavailableButLeavesOrdinaryMediumAvailable()
    {
        RenderPackDescriptor descriptor = BuiltInAtmosphericRenderPack.Descriptor;
        HashSet<RenderCapability> available = Enum.GetValues<RenderCapability>().ToHashSet();
        available.Remove(RenderCapability.MultiviewDirectionalShadowCascades);
        var host = new RenderPackHostCapabilities(available, 4096, 4, 256L * MiB);

        RenderPackValidationResult low = RenderPackValidator.ValidatePresetCompatibility(
            descriptor,
            descriptor.QualityPresets.Single(value => value.Semantic == RenderQualitySemantic.Low),
            host);
        RenderPackValidationResult medium = RenderPackValidator.ValidatePresetCompatibility(
            descriptor,
            descriptor.QualityPresets.Single(value => value.Semantic == RenderQualitySemantic.Medium),
            host);

        Assert.False(low.Success);
        Assert.Contains("MultiviewDirectionalShadowCascades", low.Reason, StringComparison.Ordinal);
        Assert.True(medium.Success, medium.Reason);
    }

    private sealed class EmptyAssets : IRenderPackAssets
    {
        public Stream OpenRead(string assetKey) => Stream.Null;
    }
}
