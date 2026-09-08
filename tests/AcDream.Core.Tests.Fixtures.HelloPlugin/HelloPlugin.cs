using AcDream.Plugin.Abstractions;
using AcDream.Plugin.Abstractions.Rendering;

namespace AcDream.Core.Tests.Fixtures.HelloPlugin;

public sealed class HelloPlugin : IAcDreamPlugin, IRenderPackPlugin, IRenderPackAssets
{
    public int InitializeCount { get; private set; }
    public int EnableCount { get; private set; }
    public int DisableCount { get; private set; }
    public IPluginHost? ReceivedHost { get; private set; }

    public void Initialize(IPluginHost host)
    {
        ReceivedHost = host;
        InitializeCount++;
    }

    public void Enable() => EnableCount++;
    public void Disable() => DisableCount++;

    public void Register(IRenderPackRegistry registry)
    {
        ArgumentNullException.ThrowIfNull(registry);
        _ = registry.Register(
            new RenderPackDescriptor(
                Id: "acdream.test.noop-pack",
                DisplayName: "Test no-op pack",
                PackVersion: new Version(1, 0, 0),
                PackApiVersion: RenderPackApi.Current,
                HighestTier: RenderPackTier.Tier1,
                RequiredCapabilities: [],
                OptionalCapabilities: [],
                Resources: [],
                Passes: [],
                SceneReplays: [],
                PipelineVariants: [],
                QualityPresets: [],
                Settings: [],
                AtmospherePolicy: null)
            {
                FeatureSummary = "Test-only no-op render-pack fixture.",
            },
            this);
        string? directory = Path.GetDirectoryName(typeof(HelloPlugin).Assembly.Location);
        if (directory is not null
            && File.Exists(Path.Combine(directory, "throw-after-render-register")))
        {
            throw new InvalidOperationException(
                "fixture render-pack registration failed after publishing a descriptor");
        }
    }

    public Stream OpenRead(string assetKey) =>
        new MemoryStream([], writable: false);
}
