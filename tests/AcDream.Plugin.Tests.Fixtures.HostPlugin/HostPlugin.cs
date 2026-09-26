using AcDream.Plugin.Abstractions;
using AcDream.Plugin.Abstractions.Rendering;
using System.Runtime.Loader;

namespace AcDream.Plugin.Tests.Fixtures.HostPlugin;

public sealed class HostPlugin : IAcDreamPlugin, IRenderPackPlugin, IRenderPackAssets
{
    private IPluginHost? _host;
    private string? _assemblyDirectory;
    private bool _throwAfterRegistration;
    private bool _throwDuringInitialize;
    private int _entitiesSeen;

    public void Initialize(IPluginHost host)
    {
        _host = host ?? throw new ArgumentNullException(nameof(host));
        _assemblyDirectory = host.PluginDirectory ?? FixtureDirectory();
        _throwAfterRegistration = File.Exists(
            Path.Combine(_assemblyDirectory!, "throw-after-register"));
        _throwDuringInitialize = File.Exists(
            Path.Combine(_assemblyDirectory!, "throw-during-initialize"));
        host.Log.Info($"fixture-initialized:hasUi={host.HasUi}");
        if (_throwDuringInitialize)
        {
            RegisterHostCallbacks(host);
            AssemblyLoadContext.GetLoadContext(typeof(HostPlugin).Assembly)!
                .Unloading += OnUnloading;
            throw new InvalidOperationException(
                "fixture initialize failed after registering UI, entity, and selection callbacks");
        }
    }

    public void Enable()
    {
        IPluginHost host = _host
            ?? throw new InvalidOperationException("The fixture was not initialized.");
        RegisterHostCallbacks(host);
        if (_throwAfterRegistration)
        {
            throw new InvalidOperationException(
                "fixture enable failed after registering UI and events");
        }
        host.Log.Info(
            $"fixture-enabled:hasUi={host.HasUi}:entities={host.State.Entities.Count}");
        // The status board: publish under this plugin's own id and read it
        // back by the id the tests install the fixture under.
        bool published = host.StatusBoard.Publish("state", "enabled");
        string read = host.StatusBoard.TryRead(
            "acdream.test.host-fixture", "state", out string value)
            ? value
            : "<none>";
        host.Log.Info(
            $"fixture-status:available={host.StatusBoard.IsAvailable}:published={published}:read={read}");
        host.Events.LoginComplete += OnLoginComplete;
        host.Log.Info($"fixture-hot-reload={host.IsHotReload}");
    }

    private void OnLoginComplete() => _host?.Log.Info("fixture-login");

    public void Disable()
    {
        IPluginHost? host = _host;
        if (host is null)
            return;
        if (_throwAfterRegistration || _throwDuringInitialize)
        {
            throw new InvalidOperationException(
                "fixture disable intentionally refuses cleanup");
        }
        host.Events.EntitySpawned -= OnEntitySpawned;
        host.Events.LoginComplete -= OnLoginComplete;
        host.Selection.Changed -= OnSelectionChanged;
        host.Log.Info($"fixture-disabled:entitiesSeen={_entitiesSeen}");
        _host = null;
    }

    public void Register(IRenderPackRegistry registry)
    {
        ArgumentNullException.ThrowIfNull(registry);
        string directory = FixtureDirectory();
        if (File.Exists(Path.Combine(directory, "register-no-render-packs")))
            return;
        string versionPath = Path.Combine(directory, "render-pack-version.txt");
        Version version = File.Exists(versionPath)
            ? Version.Parse(File.ReadAllText(versionPath).Trim())
            : new Version(1, 0, 0);
        _ = registry.Register(
            new RenderPackDescriptor(
                Id: "acdream.test.external-render-pack",
                DisplayName: "External graphical render-pack fixture",
                PackVersion: version,
                PackApiVersion: RenderPackApi.Current,
                HighestTier: RenderPackTier.Tier1,
                RequiredCapabilities: [],
                OptionalCapabilities: [],
                Resources: [],
                Passes: [],
                SceneReplays: [],
                PipelineVariants: [],
                QualityPresets:
                [
                    Preset("low", "Low"),
                    Preset("high", "High"),
                ],
                Settings: [],
                AtmospherePolicy: null)
            {
                FeatureSummary = "Collectible external no-op render-pack fixture.",
            },
            this);
        if (File.Exists(Path.Combine(directory, "throw-after-render-pack-register")))
        {
            throw new InvalidOperationException(
                "fixture render-pack registration failed after publishing its descriptor");
        }
    }

    public Stream OpenRead(string assetKey) =>
        new MemoryStream([], writable: false);

    private static RenderQualityPreset Preset(string id, string displayName) => new(
        id,
        displayName,
        RequiredCapabilities: [],
        ResourceOverrides: [],
        SettingOverrides: [],
        MaxResidentGpuBytes: 0,
        MaxIncrementalGpuMillisecondsP50: 0,
        MaxIncrementalGpuMillisecondsP99: 0,
        MaxIncrementalCpuMillisecondsP50: 0,
        MaxIncrementalCpuMillisecondsP99: 0);

    private void RegisterHostCallbacks(IPluginHost host)
    {
        host.Ui.AddMarkupPanel(
            Path.Combine(AppContext.BaseDirectory, "fixture-panel.xml"),
            this);
        host.Events.EntitySpawned += OnEntitySpawned;
        host.Selection.Changed += OnSelectionChanged;
    }

    private void OnEntitySpawned(WorldEntitySnapshot snapshot)
    {
        _entitiesSeen++;
        RecordUnexpectedCallback(snapshot.Id);
    }

    private void OnSelectionChanged(SelectionChangedEvent change) =>
        RecordUnexpectedCallback(change.SelectedObjectId ?? 0u);

    private void RecordUnexpectedCallback(uint objectId)
    {
        if ((_throwAfterRegistration || _throwDuringInitialize)
            && _assemblyDirectory is not null)
        {
            File.AppendAllText(
                Path.Combine(_assemblyDirectory, "unexpected-callback"),
                $"{objectId}{Environment.NewLine}");
        }
    }

    private void OnUnloading(AssemblyLoadContext context)
    {
        IPluginHost host = _host!;
        bool uiClosed = Rejects(() => host.Ui.AddMarkupPanel(
            Path.Combine(AppContext.BaseDirectory, "unloading-panel.xml"),
            this));
        bool eventsClosed = Rejects(() =>
        {
            host.Events.EntitySpawned += OnEntitySpawned;
        });
        bool selectionClosed = Rejects(() =>
        {
            host.Selection.Changed += OnSelectionChanged;
        });
        File.WriteAllText(
            Path.Combine(_assemblyDirectory!, "unload-observation"),
            $"ui={uiClosed};events={eventsClosed};selection={selectionClosed}");
    }

    /// <summary>
    /// The fixture's folder. The host loads the assembly from memory, so it
    /// has no location of its own; the host names its load context after the
    /// plugin folder, which a render pack, having no host, reads instead.
    /// </summary>
    private static string FixtureDirectory()
    {
        string location = typeof(HostPlugin).Assembly.Location;
        return location.Length > 0
            ? Path.GetDirectoryName(location)!
            : AssemblyLoadContext.GetLoadContext(typeof(HostPlugin).Assembly)?.Name
                ?? string.Empty;
    }

    private static bool Rejects(Action action)
    {
        try
        {
            action();
            return false;
        }
        catch (ObjectDisposedException)
        {
            return true;
        }
    }
}
