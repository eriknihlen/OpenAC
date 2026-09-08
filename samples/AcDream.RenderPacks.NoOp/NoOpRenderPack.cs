using AcDream.Plugin.Abstractions.Rendering;

namespace AcDream.RenderPacks.NoOp;

public sealed class NoOpRenderPack : IRenderPackPlugin, IRenderPackAssets
{
    private static RenderPackDescriptor Descriptor { get; } = new(
        "sample.no-op-render-pack",
        "No-op Render Pack Sample",
        new Version(1, 0, 0),
        RenderPackApi.Current,
        RenderPackTier.Tier1,
        RequiredCapabilities: [],
        OptionalCapabilities: [],
        Resources: [],
        Passes: [],
        SceneReplays: [],
        PipelineVariants: [],
        QualityPresets:
        [
            new RenderQualityPreset(
                "conformance",
                "Conformance",
                RequiredCapabilities: [],
                ResourceOverrides: [],
                SettingOverrides: [],
                MaxResidentGpuBytes: 0,
                MaxIncrementalGpuMillisecondsP50: 0,
                MaxIncrementalGpuMillisecondsP99: 0,
                MaxIncrementalCpuMillisecondsP50: 0,
                MaxIncrementalCpuMillisecondsP99: 0),
        ],
        Settings: [],
        AtmospherePolicy: null)
    {
        FeatureSummary = "No visual changes; exercises render-pack discovery and atomic activation.",
    };

    public void Register(IRenderPackRegistry registry)
    {
        ArgumentNullException.ThrowIfNull(registry);
        registry.Register(Descriptor, this);
    }

    public Stream OpenRead(string assetKey) =>
        throw new FileNotFoundException(
            "The no-op conformance pack declares no assets.",
            assetKey);
}
