using AcDream.Core.Plugins;
using AcDream.Core.Selection;
using AcDream.Plugin.Abstractions;
using AcDream.Plugin.Abstractions.Rendering;

namespace AcDream.Core.Tests.Plugins;

public class PluginLoaderTests
{
    private static string FixturePluginPath()
    {
        const string fileName = "AcDream.Core.Tests.Fixtures.HelloPlugin.dll";
        string colocated = Path.Combine(AppContext.BaseDirectory, fileName);
        if (File.Exists(colocated))
            return colocated;

        // walk up from the test bin dir to the repo root, then into the fixture's build output
        var baseDir = AppContext.BaseDirectory;
        var configuration = new DirectoryInfo(baseDir).Parent!.Name; // Debug / Release
        var repoRoot = FindRepoRoot(baseDir);
        return Path.Combine(
            repoRoot,
            "tests", "AcDream.Core.Tests.Fixtures.HelloPlugin", "bin", configuration, "net10.0",
            fileName);
    }

    private static string FindRepoRoot(string startDir)
    {
        var dir = new DirectoryInfo(startDir);
        while (dir is not null && !File.Exists(Path.Combine(dir.FullName, "AcDream.slnx")))
            dir = dir.Parent;
        return dir?.FullName ?? throw new InvalidOperationException("repo root not found");
    }

    private sealed class StubHost : IPluginHost
    {
        public bool HasUi => true;
        public IPluginLogger Log { get; } = new StubLogger();
        public IGameState State { get; } = new StubState();
        public IEvents Events { get; } = new StubEvents();
        public ISelectionService Selection { get; } = new SelectionState();
        public IUiRegistry Ui { get; } = new StubUiRegistry();
        public IAutomationSurface Automation => NoOpAutomationSurface.Instance;
    }

    private sealed class StubUiRegistry : IUiRegistry
    {
        public void AddMarkupPanel(string markupPath, object binding) { }
    }

    private sealed class StubLogger : IPluginLogger
    {
        public void Info(string message) { }
        public void Warn(string message) { }
        public void Error(string message, Exception? exception = null) { }
    }

    private sealed class StubState : IGameState
    {
        public IReadOnlyList<WorldEntitySnapshot> Entities { get; } = Array.Empty<WorldEntitySnapshot>();
    }

    private sealed class StubEvents : IEvents
    {
        public event Action<WorldEntitySnapshot> EntitySpawned
        {
            add { }
            remove { }
        }

        public event Action<double> Tick
        {
            add { }
            remove { }
        }
    }

    private sealed class RecordingRenderPackRegistry : IRenderPackRegistry, IDisposable
    {
        private readonly List<Registration> _registrations = [];

        public int ActiveCount => _registrations.Count(static item => item.Active);
        public RenderPackDescriptor? Descriptor { get; private set; }

        public IDisposable Register(
            RenderPackDescriptor descriptor,
            IRenderPackAssets assets)
        {
            Descriptor = descriptor;
            var registration = new Registration();
            _registrations.Add(registration);
            return registration;
        }

        public void Dispose()
        {
            foreach (Registration registration in _registrations)
                registration.Dispose();
        }

        private sealed class Registration : IDisposable
        {
            public bool Active { get; private set; } = true;
            public void Dispose() => Active = false;
        }
    }

    [Fact]
    public void Load_FixtureDll_InstantiatesPluginAndCallsInitialize()
    {
        var dllPath = FixturePluginPath();
        Assert.True(File.Exists(dllPath), $"fixture dll not found: {dllPath}");

        var host = new StubHost();
        var manifest = new PluginManifest(
            Id: "acdream.test.hello",
            DisplayName: "Hello",
            Version: "0.0.1",
            EntryDll: Path.GetFileName(dllPath),
            ApiVersion: 1,
            Dependencies: Array.Empty<string>());

        var loaded = PluginLoader.Load(
            pluginDirectory: Path.GetDirectoryName(dllPath)!,
            manifest: manifest,
            host: host);

        Assert.True(loaded.Success, loaded.Error?.ToString());
        Assert.NotNull(loaded.Plugin);
        Assert.Equal("HelloPlugin", loaded.Plugin!.GetType().Name);
        loaded.Plugin.Disable();
        loaded.LoadContext!.Unload();
    }

    [Fact]
    public void Load_RenderPackOnlyFixture_RegistersWithoutGameplayEntrypointRequirement()
    {
        string dllPath = FixturePluginPath();
        var host = new StubHost();
        using var registry = new RecordingRenderPackRegistry();
        var manifest = new PluginManifest(
            Id: "acdream.test.render-pack",
            DisplayName: "Render pack",
            Version: "1.0.0",
            EntryDll: Path.GetFileName(dllPath),
            ApiVersion: 1,
            Dependencies: [],
            Kinds: [PluginKind.RenderPack]);

        LoadedPlugin loaded = PluginLoader.Load(
            Path.GetDirectoryName(dllPath)!,
            manifest,
            host,
            registry);

        Assert.True(loaded.Success, loaded.Error?.ToString());
        Assert.Null(loaded.Plugin);
        Assert.NotNull(loaded.RenderPackPlugin);
        Assert.Equal(1, registry.ActiveCount);
        Assert.Equal("acdream.test.noop-pack", registry.Descriptor?.Id);
        loaded.LoadContext!.Unload();
    }

    [Fact]
    public void Load_UnsupportedApiVersion_IsRefusedBeforeAnyCodeLoads()
    {
        var host = new StubHost();
        var manifest = new PluginManifest(
            Id: "future.plugin",
            DisplayName: "Future",
            Version: "1.0.0",
            EntryDll: "nope.dll",           // deliberately nonexistent:
            ApiVersion: PluginApi.Current + 1,
            Dependencies: Array.Empty<string>());

        var loaded = PluginLoader.Load("/does/not/exist", manifest, host);

        Assert.False(loaded.Success);
        var mismatch = Assert.IsType<PluginApiVersionException>(loaded.Error);
        Assert.Contains("future.plugin", mismatch.Message);
        Assert.Null(loaded.LoadContext);
    }

    [Fact]
    public void Load_MinimumSupportedApiVersion_PassesTheGate()
    {
        var host = new StubHost();
        var manifest = new PluginManifest(
            Id: "old.plugin",
            DisplayName: "Old",
            Version: "1.0.0",
            EntryDll: "nope.dll",
            ApiVersion: PluginApi.MinimumSupported,
            Dependencies: Array.Empty<string>());

        var loaded = PluginLoader.Load("/does/not/exist", manifest, host);

        // Fails on the missing dll, NOT on the version gate.
        Assert.False(loaded.Success);
        Assert.IsType<FileNotFoundException>(loaded.Error);
    }

    [Fact]
    public void Load_MissingDll_ReturnsFailure()
    {
        var host = new StubHost();
        var manifest = new PluginManifest(
            Id: "x",
            DisplayName: "X",
            Version: "0.0.1",
            EntryDll: "nope.dll",
            ApiVersion: 1,
            Dependencies: Array.Empty<string>());

        var loaded = PluginLoader.Load("/does/not/exist", manifest, host);

        Assert.False(loaded.Success);
        Assert.NotNull(loaded.Error);
    }

    [Fact]
    public void Load_DllWithNoPluginImpl_ReturnsFailure()
    {
        var coreDllDir = AppContext.BaseDirectory;
        var host = new StubHost();
        var manifest = new PluginManifest(
            Id: "x",
            DisplayName: "X",
            Version: "0.0.1",
            EntryDll: "AcDream.Core.dll",
            ApiVersion: 1,
            Dependencies: Array.Empty<string>());

        var loaded = PluginLoader.Load(coreDllDir, manifest, host);

        Assert.False(loaded.Success);
        Assert.Contains("IAcDreamPlugin", loaded.Error!.Message);
        Assert.NotNull(loaded.LoadContext);
        loaded.LoadContext!.Unload();
    }
}
