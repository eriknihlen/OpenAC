using System.Text.Json;
using AcDream.Core.Plugins;
using AcDream.Core.Selection;
using AcDream.Plugin.Abstractions;
using AcDream.Plugin.Abstractions.Rendering;

namespace AcDream.Core.Tests.Plugins;

public sealed class PluginSessionTests
{
    [Fact]
    public void ScopedHostPrefixesStorageWithAuthenticatedManifestId()
    {
        var storage = new MemoryStorage();
        using var alpha = new ScopedPluginHost(
            new StubHost(storage),
            "acdream.alpha",
            "Alpha");
        using var beta = new ScopedPluginHost(
            new StubHost(storage),
            "acdream.beta",
            "Beta");

        alpha.Storage.WriteText("profile.json", "alpha");
        beta.Storage.WriteText("profile.json", "beta");
        alpha.Storage.WriteText("imports/route.nav", "nav");

        Assert.Equal("alpha", storage.Text[Path.Combine(
            "acdream.alpha", "files", "profile.json")]);
        Assert.Equal("beta", storage.Text[Path.Combine(
            "acdream.beta", "files", "profile.json")]);
        Assert.Equal(["imports/route.nav"], alpha.Storage.List("imports"));
        Assert.Empty(beta.Storage.List("imports"));
        Assert.Throws<ArgumentException>(() =>
            alpha.Storage.WriteText("../escape.json", "bad"));
    }

    /// <summary>
    /// A named scope -- one character's files -- is its own folder inside the
    /// plugin's, so two characters never share a settings file. Mutation:
    /// drop the storage's OpenScope and the interface default hands back the
    /// plugin's root, which both characters then overwrite.
    /// </summary>
    [Fact]
    public void ScopedHostStorageKeepsEachNamedScopeInItsOwnFolder()
    {
        var storage = new MemoryStorage();
        using var host = new ScopedPluginHost(
            new StubHost(storage),
            "acdream.alpha",
            "Alpha");

        host.Storage.OpenScope(PluginStorageScope.Character("+Mossy XI")).WriteText("settings.json", "xi");
        host.Storage.OpenScope(PluginStorageScope.Character("+Mossy XII")).WriteText("settings.json", "xii");

        Assert.Equal("xi", storage.Text[Path.Combine(
            "acdream.alpha", "files", "character", "+Mossy XI", "settings.json")]);
        Assert.Equal("xii", storage.Text[Path.Combine(
            "acdream.alpha", "files", "character", "+Mossy XII", "settings.json")]);
        Assert.Equal("xi", host.Storage.OpenScope(PluginStorageScope.Character("+Mossy XI")).ReadText("settings.json"));
        Assert.Null(host.Storage.ReadText("settings.json"));
        Assert.Throws<ArgumentException>(() =>
            host.Storage.OpenScope(new PluginStorageScope("../other")));
    }

    [Fact]
    public void ScopedHostForwardsVtankProfilesUnscoped()
    {
        var vtankProfiles = new MemoryStorage();
        using var scope = new ScopedPluginHost(
            new StubHost(vtankProfiles: vtankProfiles),
            "acdream.alpha",
            "Alpha");

        scope.VtankProfiles.WriteText("Shared.usd", "content");

        Assert.Same(vtankProfiles, scope.VtankProfiles);
        Assert.Equal("content", vtankProfiles.Text["Shared.usd"]);
    }

    [Fact]
    public void ScopedHostForwardsSessionSettingsVerbatim()
    {
        var settings = new Dictionary<string, string>
        {
            ["startMacro"] = "true",
        };
        using var scope = new ScopedPluginHost(
            new StubHost(sessionSettings: settings),
            "acdream.alpha",
            "Alpha");

        Assert.Same(settings, scope.SessionSettings);
    }

    [Fact]
    public void ScopedHostForwardsDefaultEmptySessionSettingsWhenHostDeclaresNone()
    {
        using var scope = new ScopedPluginHost(
            new StubHost(),
            "acdream.alpha",
            "Alpha");

        Assert.Empty(scope.SessionSettings);
    }

    [Fact]
    public void ScopedHostProjectsSessionSettingsPerPluginIdWhenHostSupportsIt()
    {
        var host = new PerPluginStubHost(new Dictionary<string, Dictionary<string, string>>
        {
            ["acdream.alpha"] = new() { ["startMacro"] = "true" },
            ["acdream.beta"] = new() { ["startMacro"] = "false" },
        });
        using var alpha = new ScopedPluginHost(host, "acdream.alpha", "Alpha");
        using var beta = new ScopedPluginHost(host, "acdream.beta", "Beta");
        using var gamma = new ScopedPluginHost(host, "acdream.gamma", "Gamma");

        Assert.Equal("true", alpha.SessionSettings["startMacro"]);
        Assert.Equal("false", beta.SessionSettings["startMacro"]);
        Assert.Empty(gamma.SessionSettings);
    }

    [Fact]
    public void ScopedHostNamespacesAndUnregistersLootClassifierOnDispose()
    {
        var global = new PluginLootClassifierRegistry();
        var scope = new ScopedPluginHost(
            new StubHost(lootClassifiers: global),
            "acdream.looter",
            "Looter");

        scope.LootClassifiers.Register(
            "main",
            "My loot rules",
            new KeepClassifier());

        PluginLootClassifierInfo registered = Assert.Single(global.Available);
        Assert.Equal("acdream.looter/main", registered.Id);
        Assert.Equal("My loot rules", registered.DisplayName);

        scope.Dispose();

        Assert.Empty(global.Available);
    }

    [Fact]
    public void AbsentAllowListLoadsEveryDiscoveredPlugin()
    {
        using var temporary = new TemporaryDirectory();
        InstallFixture(temporary.Path, "alpha", "acdream.test.alpha");
        InstallFixture(temporary.Path, "beta", "acdream.test.beta");
        var statuses = new List<PluginSessionStatus>();
        var plugins = new PluginSession(new StubHost(), statuses.Add);

        plugins.Start([temporary.Path], allowList: null);

        Assert.Equal(2, plugins.LoadedCount);
        Assert.Equal(
            ["acdream.test.alpha", "acdream.test.beta"],
            plugins.LoadedPluginIds);
        Assert.All(
            statuses,
            status => Assert.Equal(PluginSessionStatusKind.Loaded, status.Kind));
        ReleaseAndCollect(plugins);
    }

    [Fact]
    public void AllowListIsCaseInsensitiveAndOneFailureDoesNotBlockAnotherPlugin()
    {
        using var temporary = new TemporaryDirectory();
        InstallFixture(temporary.Path, "good", "acdream.test.good");
        InstallBroken(temporary.Path, "broken", "acdream.test.broken");
        var statuses = new List<PluginSessionStatus>();
        var plugins = new PluginSession(new StubHost(), statuses.Add);

        plugins.Start(
            [temporary.Path],
            [
                "ACDREAM.TEST.BROKEN",
                "ACDREAM.TEST.GOOD",
                "acdream.test.missing",
            ]);

        Assert.Equal(["acdream.test.good"], plugins.LoadedPluginIds);
        Assert.Equal(
            [
                ("ACDREAM.TEST.BROKEN", PluginSessionStatusKind.Failed),
                ("acdream.test.good", PluginSessionStatusKind.Loaded),
                ("acdream.test.missing", PluginSessionStatusKind.Failed),
            ],
            statuses.Select(static status => (status.Plugin, status.Kind)));
        Assert.All(
            statuses.Where(static status => status.Kind == PluginSessionStatusKind.Failed),
            status => Assert.False(string.IsNullOrWhiteSpace(status.Error)));
        ReleaseAndCollect(plugins);
    }

    [Fact]
    public void ExplicitEmptyAllowListLoadsNothing()
    {
        using var temporary = new TemporaryDirectory();
        InstallFixture(temporary.Path, "fixture", "acdream.test.fixture");
        var statuses = new List<PluginSessionStatus>();
        using var plugins = new PluginSession(new StubHost(), statuses.Add);

        plugins.Start([temporary.Path], []);

        Assert.Equal(0, plugins.LoadedCount);
        Assert.Empty(statuses);
    }

    [Fact]
    public void GraphicalKindSet_RegistersRenderPackAndWithdrawsBeforeUnload()
    {
        using var temporary = new TemporaryDirectory();
        InstallFixture(
            temporary.Path,
            "render",
            "acdream.test.render",
            [PluginKind.RenderPack]);
        var statuses = new List<PluginSessionStatus>();
        var registry = new RecordingRenderPackRegistry();
        var plugins = new PluginSession(
            new StubHost(),
            statuses.Add,
            registry,
            [PluginKind.Gameplay, PluginKind.RenderPack]);

        plugins.Start([temporary.Path], allowList: null);

        Assert.Equal(1, plugins.LoadedCount);
        Assert.Equal(1, registry.ActiveCount);
        Assert.Equal("acdream.test.noop-pack", registry.Descriptor?.Id);
        IReadOnlyList<WeakReference> contexts =
            plugins.CaptureLoadContextWeakReferences();
        plugins.Dispose();
        Assert.Equal(0, registry.ActiveCount);
        Collect(contexts);
    }

    [Fact]
    public void GameplayOnlyHost_SkipsUnrequestedRenderPackBeforeDllProbe()
    {
        using var temporary = new TemporaryDirectory();
        InstallBroken(
            temporary.Path,
            "render",
            "acdream.test.render",
            [PluginKind.RenderPack]);
        var statuses = new List<PluginSessionStatus>();
        using var plugins = new PluginSession(new StubHost(), statuses.Add);

        plugins.Start([temporary.Path], allowList: null);

        Assert.Equal(0, plugins.LoadedCount);
        Assert.Empty(statuses);
        Assert.Empty(plugins.CaptureLoadContextWeakReferences());
    }

    [Fact]
    public void RenderPackRegisterFailure_WithdrawsPartialRegistrationBeforeUnload()
    {
        using var temporary = new TemporaryDirectory();
        InstallFixture(
            temporary.Path,
            "render",
            "acdream.test.render",
            [PluginKind.RenderPack]);
        File.WriteAllText(
            Path.Combine(temporary.Path, "render", "throw-after-render-register"),
            string.Empty);
        var registry = new RecordingRenderPackRegistry();
        var statuses = new List<PluginSessionStatus>();
        var plugins = new PluginSession(
            new StubHost(),
            statuses.Add,
            registry,
            [PluginKind.Gameplay, PluginKind.RenderPack]);

        plugins.Start([temporary.Path], allowList: null);

        Assert.Equal(0, plugins.LoadedCount);
        Assert.Equal(0, registry.ActiveCount);
        PluginSessionStatus status = Assert.Single(statuses);
        Assert.Equal(PluginSessionStatusKind.Failed, status.Kind);
        Assert.Contains("failed after publishing", status.Error);
        IReadOnlyList<WeakReference> contexts =
            plugins.CaptureLoadContextWeakReferences();
        plugins.Dispose();
        Collect(contexts);
    }

    [Fact]
    public void GameplayOnlyHost_ExplicitRenderPackReportsKindWithoutDllProbe()
    {
        using var temporary = new TemporaryDirectory();
        InstallBroken(
            temporary.Path,
            "render",
            "acdream.test.render",
            [PluginKind.RenderPack]);
        var statuses = new List<PluginSessionStatus>();
        using var plugins = new PluginSession(new StubHost(), statuses.Add);

        plugins.Start([temporary.Path], ["acdream.test.render"]);

        PluginSessionStatus status = Assert.Single(statuses);
        Assert.Equal(PluginSessionStatusKind.Failed, status.Kind);
        Assert.Contains("does not support", status.Error);
        Assert.DoesNotContain("entry dll", status.Error);
        Assert.Empty(plugins.CaptureLoadContextWeakReferences());
    }

    [Fact]
    public void RequestedPluginBelowMinHostVersionFailsBeforeLoading()
    {
        using var temporary = new TemporaryDirectory();
        InstallFixtureWithHostFields(
            temporary.Path, "fixture", "acdream.test.toonew", minHostVersion: "9.0.0");
        var statuses = new List<PluginSessionStatus>();
        using var plugins = new PluginSession(
            new StubHost(),
            statuses.Add,
            hostKind: PluginHostKind.Headless,
            hostVersion: new PluginHostVersion(0, 1, 7));

        plugins.Start([temporary.Path], ["acdream.test.toonew"]);

        Assert.Equal(0, plugins.LoadedCount);
        PluginSessionStatus status = Assert.Single(statuses);
        Assert.Equal(PluginSessionStatusKind.Failed, status.Kind);
        Assert.Contains("requires OpenAC 9.0.0 or newer", status.Error);
        Assert.Empty(plugins.CaptureLoadContextWeakReferences());
    }

    [Fact]
    public void RequestedPluginAboveMaxHostVersionFailsBeforeLoading()
    {
        using var temporary = new TemporaryDirectory();
        InstallFixtureWithHostFields(
            temporary.Path, "fixture", "acdream.test.tooold", maxHostVersion: "0.1.0");
        var statuses = new List<PluginSessionStatus>();
        using var plugins = new PluginSession(
            new StubHost(),
            statuses.Add,
            hostKind: PluginHostKind.Headless,
            hostVersion: new PluginHostVersion(0, 1, 7));

        plugins.Start([temporary.Path], ["acdream.test.tooold"]);

        Assert.Equal(0, plugins.LoadedCount);
        PluginSessionStatus status = Assert.Single(statuses);
        Assert.Equal(PluginSessionStatusKind.Failed, status.Kind);
        Assert.Contains("supports OpenAC up to 0.1.0", status.Error);
        Assert.Empty(plugins.CaptureLoadContextWeakReferences());
    }

    [Fact]
    public void RequestedPluginOnSkippedHostVersionFailsBeforeLoading()
    {
        using var temporary = new TemporaryDirectory();
        InstallFixtureWithHostFields(
            temporary.Path,
            "fixture",
            "acdream.test.skipped",
            skipHostVersions: ["0.1.7"]);
        var statuses = new List<PluginSessionStatus>();
        using var plugins = new PluginSession(
            new StubHost(),
            statuses.Add,
            hostKind: PluginHostKind.Headless,
            hostVersion: new PluginHostVersion(0, 1, 7));

        plugins.Start([temporary.Path], ["acdream.test.skipped"]);

        Assert.Equal(0, plugins.LoadedCount);
        PluginSessionStatus status = Assert.Single(statuses);
        Assert.Equal(PluginSessionStatusKind.Failed, status.Kind);
        Assert.Contains("is marked broken on OpenAC 0.1.7", status.Error);
        Assert.Empty(plugins.CaptureLoadContextWeakReferences());
    }

    [Fact]
    public void UnrequestedIncompatiblePluginIsSkippedSilently()
    {
        using var temporary = new TemporaryDirectory();
        InstallFixtureWithHostFields(
            temporary.Path, "fixture", "acdream.test.toonew", minHostVersion: "9.0.0");
        var statuses = new List<PluginSessionStatus>();
        using var plugins = new PluginSession(
            new StubHost(),
            statuses.Add,
            hostKind: PluginHostKind.Headless,
            hostVersion: new PluginHostVersion(0, 1, 7));

        plugins.Start([temporary.Path], allowList: null);

        Assert.Equal(0, plugins.LoadedCount);
        Assert.Empty(statuses);
        Assert.Empty(plugins.CaptureLoadContextWeakReferences());
    }

    [Fact]
    public void HostKindMismatchRequestedPluginFailsWithReason()
    {
        using var temporary = new TemporaryDirectory();
        InstallFixtureWithHostFields(
            temporary.Path, "fixture", "acdream.test.headless-only", hosts: ["headless"]);
        var statuses = new List<PluginSessionStatus>();
        using var plugins = new PluginSession(
            new StubHost(),
            statuses.Add,
            hostKind: PluginHostKind.Graphical,
            hostVersion: new PluginHostVersion(0, 1, 7));

        plugins.Start([temporary.Path], ["acdream.test.headless-only"]);

        Assert.Equal(0, plugins.LoadedCount);
        PluginSessionStatus status = Assert.Single(statuses);
        Assert.Equal(PluginSessionStatusKind.Failed, status.Kind);
        Assert.Contains("runs only on the headless host", status.Error);
        Assert.Empty(plugins.CaptureLoadContextWeakReferences());
    }

    [Fact]
    public void DuplicateIdAcrossRootsFailsAndLoadsNeither()
    {
        using var first = new TemporaryDirectory();
        using var second = new TemporaryDirectory();
        InstallFixture(first.Path, "fixture", "acdream.test.dup");
        InstallFixture(second.Path, "fixture", "acdream.test.dup");
        var statuses = new List<PluginSessionStatus>();
        using var plugins = new PluginSession(new StubHost(), statuses.Add);

        plugins.Start([first.Path, second.Path], allowList: null);

        Assert.Equal(0, plugins.LoadedCount);
        PluginSessionStatus status = Assert.Single(statuses);
        Assert.Equal(PluginSessionStatusKind.Failed, status.Kind);
        Assert.Contains("more than one plugin folder", status.Error);
        Assert.Contains(Path.Combine(first.Path, "fixture"), status.Error);
        Assert.Contains(Path.Combine(second.Path, "fixture"), status.Error);
        Assert.Empty(plugins.CaptureLoadContextWeakReferences());
    }

    [Fact]
    public void DuplicateIdWithinRootFailsAndLoadsNeither()
    {
        using var temporary = new TemporaryDirectory();
        InstallFixture(temporary.Path, "alpha", "acdream.test.dup");
        InstallFixture(temporary.Path, "beta", "acdream.test.dup");
        var statuses = new List<PluginSessionStatus>();
        using var plugins = new PluginSession(new StubHost(), statuses.Add);

        plugins.Start([temporary.Path], allowList: null);

        Assert.Equal(0, plugins.LoadedCount);
        PluginSessionStatus status = Assert.Single(statuses);
        Assert.Equal(PluginSessionStatusKind.Failed, status.Kind);
        Assert.Contains("more than one plugin folder", status.Error);
        Assert.Contains(Path.Combine(temporary.Path, "alpha"), status.Error);
        Assert.Contains(Path.Combine(temporary.Path, "beta"), status.Error);
        Assert.Empty(plugins.CaptureLoadContextWeakReferences());
    }

    [Fact]
    public void IncompatibleSecondCopyIsNotCountedAsDuplicate()
    {
        using var temporary = new TemporaryDirectory();
        InstallFixture(temporary.Path, "alpha", "acdream.test.mixed");
        InstallFixtureWithHostFields(
            temporary.Path, "beta", "acdream.test.mixed", minHostVersion: "9.0.0");
        var statuses = new List<PluginSessionStatus>();
        using var plugins = new PluginSession(
            new StubHost(),
            statuses.Add,
            hostKind: PluginHostKind.Headless,
            hostVersion: new PluginHostVersion(0, 1, 7));

        plugins.Start([temporary.Path], allowList: null);

        Assert.Equal(1, plugins.LoadedCount);
        Assert.Equal(["acdream.test.mixed"], plugins.LoadedPluginIds);
        Assert.All(
            statuses,
            status => Assert.Equal(PluginSessionStatusKind.Loaded, status.Kind));
        ReleaseAndCollect(plugins);
    }

    [Fact]
    public void DuplicateIdInAllowListFailsAndLoadsNoCopy()
    {
        using var temporary = new TemporaryDirectory();
        InstallFixture(temporary.Path, "alpha", "acdream.test.dup-listed");
        InstallFixture(temporary.Path, "beta", "acdream.test.dup-listed");
        var statuses = new List<PluginSessionStatus>();
        using var plugins = new PluginSession(new StubHost(), statuses.Add);

        plugins.Start([temporary.Path], ["acdream.test.dup-listed"]);

        Assert.Equal(0, plugins.LoadedCount);
        PluginSessionStatus status = Assert.Single(statuses);
        Assert.Equal(PluginSessionStatusKind.Failed, status.Kind);
        Assert.Contains("more than one plugin folder", status.Error);
        Assert.Contains(Path.Combine(temporary.Path, "alpha"), status.Error);
        Assert.Contains(Path.Combine(temporary.Path, "beta"), status.Error);
        Assert.Empty(plugins.CaptureLoadContextWeakReferences());
    }

    private static void ReleaseAndCollect(PluginSession plugins)
    {
        IReadOnlyList<WeakReference> contexts =
            plugins.CaptureLoadContextWeakReferences();
        plugins.Dispose();
        Collect(contexts);
    }

    private static void Collect(IReadOnlyList<WeakReference> contexts)
    {
        for (int attempt = 0;
             attempt < 10 && contexts.Any(static context => context.IsAlive);
             attempt++)
        {
            GC.Collect();
            GC.WaitForPendingFinalizers();
            GC.Collect();
        }
        Assert.All(contexts, static context => Assert.False(context.IsAlive));
    }

    private static void InstallFixture(string root, string folder, string id)
        => InstallFixture(root, folder, id, kinds: null);

    private static void InstallFixture(
        string root,
        string folder,
        string id,
        IReadOnlyList<PluginKind>? kinds)
    {
        string source = FixturePluginPath();
        Assert.True(File.Exists(source), $"fixture DLL not found: {source}");
        string pluginDirectory = Path.Combine(root, folder);
        Directory.CreateDirectory(pluginDirectory);
        string fileName = Path.GetFileName(source);
        File.Copy(source, Path.Combine(pluginDirectory, fileName));
        WriteManifest(pluginDirectory, id, fileName, kinds);
    }

    private static void InstallBroken(
        string root,
        string folder,
        string id,
        IReadOnlyList<PluginKind>? kinds = null)
    {
        string pluginDirectory = Path.Combine(root, folder);
        Directory.CreateDirectory(pluginDirectory);
        WriteManifest(pluginDirectory, id, "missing.dll", kinds);
    }

    private static void WriteManifest(
        string directory,
        string id,
        string entryDll,
        IReadOnlyList<PluginKind>? kinds = null) =>
        File.WriteAllText(
            Path.Combine(directory, "plugin.json"),
            JsonSerializer.Serialize(new
            {
                id,
                displayName = id,
                version = "1.0.0",
                entryDll,
                apiVersion = 1,
                kinds = kinds?.Select(static kind => kind.ToString()),
            }));

    private static void InstallFixtureWithHostFields(
        string root,
        string folder,
        string id,
        string? minHostVersion = null,
        string? maxHostVersion = null,
        IReadOnlyList<string>? skipHostVersions = null,
        IReadOnlyList<string>? hosts = null)
    {
        string source = FixturePluginPath();
        Assert.True(File.Exists(source), $"fixture DLL not found: {source}");
        string pluginDirectory = Path.Combine(root, folder);
        Directory.CreateDirectory(pluginDirectory);
        string fileName = Path.GetFileName(source);
        File.Copy(source, Path.Combine(pluginDirectory, fileName));
        File.WriteAllText(
            Path.Combine(pluginDirectory, "plugin.json"),
            JsonSerializer.Serialize(new
            {
                id,
                displayName = id,
                version = "1.0.0",
                entryDll = fileName,
                apiVersion = 1,
                minHostVersion,
                maxHostVersion,
                skipHostVersions,
                hosts,
            }));
    }

    private sealed class RecordingRenderPackRegistry : IRenderPackRegistry
    {
        private readonly List<Registration> _registrations = [];

        internal int ActiveCount => _registrations.Count;
        internal RenderPackDescriptor? Descriptor { get; private set; }

        public IDisposable Register(
            RenderPackDescriptor descriptor,
            IRenderPackAssets assets)
        {
            Descriptor = descriptor;
            var registration = new Registration(this, assets);
            _registrations.Add(registration);
            return registration;
        }

        private void Remove(Registration registration) =>
            _registrations.Remove(registration);

        private sealed class Registration(
            RecordingRenderPackRegistry owner,
            IRenderPackAssets assets) : IDisposable
        {
            private RecordingRenderPackRegistry? _owner = owner;
            private IRenderPackAssets? _assets = assets;

            public void Dispose()
            {
                RecordingRenderPackRegistry? current =
                    Interlocked.Exchange(ref _owner, null);
                _assets = null;
                current?.Remove(this);
            }
        }
    }

    private static string FixturePluginPath()
    {
        string fileName = "AcDream.Core.Tests.Fixtures.HelloPlugin.dll";
        string colocated = Path.Combine(AppContext.BaseDirectory, fileName);
        if (File.Exists(colocated))
            return colocated;

        string configuration = new DirectoryInfo(AppContext.BaseDirectory)
            .Parent!.Name;
        string root = FindRepoRoot(AppContext.BaseDirectory);
        return Path.Combine(
            root,
            "tests",
            "AcDream.Core.Tests.Fixtures.HelloPlugin",
            "bin",
            configuration,
            "net10.0",
            fileName);
    }

    private static string FindRepoRoot(string start)
    {
        DirectoryInfo? directory = new(start);
        while (directory is not null
            && !File.Exists(Path.Combine(directory.FullName, "AcDream.slnx")))
        {
            directory = directory.Parent;
        }
        return directory?.FullName
            ?? throw new InvalidOperationException("Repository root not found.");
    }

    private sealed class StubHost(
        IPluginStorage? storage = null,
        IPluginLootClassifierRegistry? lootClassifiers = null,
        IPluginStorage? vtankProfiles = null,
        IReadOnlyDictionary<string, string>? sessionSettings = null) : IPluginHost
    {
        public bool HasUi => false;
        public IPluginLogger Log { get; } = new StubLogger();
        public IGameState State { get; } = new StubState();
        public IEvents Events { get; } = new StubEvents();
        public ISelectionService Selection { get; } = new SelectionState();
        public IUiRegistry Ui => NoOpUiRegistry.Instance;
        public IAutomationSurface Automation => NoOpAutomationSurface.Instance;
        public IPluginStorage Storage { get; } =
            storage ?? NoOpPluginStorage.Instance;
        public IPluginLootClassifierRegistry LootClassifiers { get; } =
            lootClassifiers ?? NoOpPluginLootClassifierRegistry.Instance;
        public IPluginStorage VtankProfiles { get; } =
            vtankProfiles ?? NoOpPluginStorage.Instance;
        public IReadOnlyDictionary<string, string> SessionSettings { get; } =
            sessionSettings ?? new Dictionary<string, string>();
    }

    private sealed class PerPluginStubHost(
        IReadOnlyDictionary<string, Dictionary<string, string>> byPlugin)
        : IPluginHost, IPerPluginSessionSettings
    {
        public bool HasUi => false;
        public IPluginLogger Log { get; } = new StubLogger();
        public IGameState State { get; } = new StubState();
        public IEvents Events { get; } = new StubEvents();
        public ISelectionService Selection { get; } = new SelectionState();
        public IUiRegistry Ui => NoOpUiRegistry.Instance;
        public IAutomationSurface Automation => NoOpAutomationSurface.Instance;

        public IReadOnlyDictionary<string, string> SessionSettingsFor(string pluginId) =>
            byPlugin.TryGetValue(pluginId, out Dictionary<string, string>? settings)
                ? settings
                : new Dictionary<string, string>();
    }

    private sealed class KeepClassifier : IPluginLootClassifier
    {
        public PluginLootClassification Classify(
            in PluginLootClassificationContext context) => new(
                true,
                PluginLootAction.Keep);
    }

    private sealed class MemoryStorage : IPluginStorage
    {
        public Dictionary<string, string> Text { get; } =
            new(StringComparer.Ordinal);
        public bool IsAvailable => true;
        public string? ReadText(string key) =>
            Text.TryGetValue(key, out string? value) ? value : null;
        public IReadOnlyList<string> List(string prefix) => Text.Keys
            .Where(key => key.StartsWith(prefix + Path.DirectorySeparatorChar,
                StringComparison.Ordinal))
            .OrderBy(static key => key, StringComparer.Ordinal)
            .ToArray();
        public void WriteText(string key, string content) => Text[key] = content;
        public bool Delete(string key) => Text.Remove(key);
    }

    private sealed class StubLogger : IPluginLogger
    {
        public void Info(string message) { }
        public void Warn(string message) { }
        public void Error(string message, Exception? exception = null) { }
    }

    private sealed class StubState : IGameState
    {
        public IReadOnlyList<WorldEntitySnapshot> Entities => [];
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

    private sealed class TemporaryDirectory : IDisposable
    {
        internal TemporaryDirectory()
        {
            Path = System.IO.Path.Combine(
                System.IO.Path.GetTempPath(),
                $"acdream-plugin-session-{Guid.NewGuid():N}");
            Directory.CreateDirectory(Path);
        }

        internal string Path { get; }

        public void Dispose()
        {
            if (Directory.Exists(Path))
                Directory.Delete(Path, recursive: true);
        }
    }
}
