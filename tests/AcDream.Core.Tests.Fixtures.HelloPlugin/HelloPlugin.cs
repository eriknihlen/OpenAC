using AcDream.Plugin.Abstractions;
using AcDream.Plugin.Abstractions.Rendering;

namespace AcDream.Core.Tests.Fixtures.HelloPlugin;

public sealed class HelloPlugin : IAcDreamPlugin, IRenderPackPlugin, IRenderPackAssets
{
    private bool _disabled;

    public int InitializeCount { get; private set; }
    public int EnableCount { get; private set; }
    public int DisableCount { get; private set; }
    public IPluginHost? ReceivedHost { get; private set; }

    public void Initialize(IPluginHost host)
    {
        ReceivedHost = host;
        InitializeCount++;
        host.Log.Info(
            $"hello-initialized:hotReload={host.IsHotReload}:directory={host.PluginDirectory}");
    }

    public void Enable()
    {
        EnableCount++;
        IPluginHost? host = ReceivedHost;
        if (host is null)
            return;
        host.Events.LoginComplete += OnLoginComplete;
        host.Events.Tick += OnTick;
        // A plugin that subscribes to something outside its host keeps its
        // own code reachable after it is unloaded, which is what the host's
        // unload check has to notice and report.
        string directory = host.PluginDirectory ?? string.Empty;
        if (File.Exists(Path.Combine(directory, "leak-on-enable")))
            AppDomain.CurrentDomain.ProcessExit += OnProcessExit;
        if (File.Exists(Path.Combine(directory, "throw-on-enable")))
            throw new InvalidOperationException("fixture enable failed on purpose");
    }

    public void Disable()
    {
        DisableCount++;
        _disabled = true;
        IPluginHost? host = ReceivedHost;
        if (host is null)
            return;
        host.Events.LoginComplete -= OnLoginComplete;
        host.Events.Tick -= OnTick;
        host.Log.Info("hello-disabled");
    }

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
        if (File.Exists(Path.Combine(registry.PluginDirectory ?? string.Empty, "throw-after-render-register")))
        {
            throw new InvalidOperationException(
                "fixture render-pack registration failed after publishing a descriptor");
        }
    }

    public Stream OpenRead(string assetKey) =>
        new MemoryStream([], writable: false);

    private void OnLoginComplete() => ReceivedHost?.Log.Info("hello-login");

    private void OnTick(double elapsedSeconds)
    {
        if (_disabled)
            ReceivedHost?.Log.Warn("hello-tick-after-disable");
    }

    private void OnProcessExit(object? sender, EventArgs e) => _ = EnableCount;
}
