using System.Text.Json;
using System.Runtime.CompilerServices;
using AcDream.App.Configuration;
using AcDream.App.Plugins;
using AcDream.Core.Plugins;
using AcDream.Core.Selection;
using AcDream.Platform;
using AcDream.Plugin.Abstractions;
using AcDream.Runtime.Session;
using AcDream.Tests.Fixtures.LauncherSession;

namespace AcDream.App.Tests.Plugins;

public sealed class GraphicalPluginSessionTests
{
    private const string FixtureId = "acdream.test.host-fixture";
    private const string ThrowingId = "acdream.test.throwing-fixture";
    private const string InitializeThrowingId =
        "acdream.test.initialize-throwing-fixture";

    [Fact]
    public void ConfiguredSetLoadsOnlyAllowedPluginAndReportsBothOutcomes()
    {
        using var temporary = new TemporaryDirectory();
        ApplicationPathSet paths = Paths(temporary.Path);
        InstallFixture(paths.PluginsDirectory, FixtureId);
        string statusPath = Path.Combine(temporary.Path, "status.jsonl");
        var logger = new CapturingLogger();
        var state = new WorldGameState();
        var events = new WorldEvents();
        var selection = new SelectionState();
        var ui = new BufferedUiRegistry();
        var host = new AppPluginHost(logger, state, events, selection, ui,
            NoOpAutomationSurface.Instance);

        using GraphicalPluginSession plugins = GraphicalPluginSession.Create(
            paths,
            [FixtureId.ToUpperInvariant(), "acdream.test.missing"],
            "gui-session",
            host,
            new SessionStatusWriter(statusPath));
        plugins.Start();

        Assert.Equal(1, plugins.LoadedCount);
        Assert.True(host.HasUi);
        AssertPanelWasRegisteredAndReleaseBinding(ui);
        Assert.Contains(
            logger.Messages,
            message => message.Contains("fixture-enabled:hasUi=True", StringComparison.Ordinal));

        JsonElement[] statuses = ReadStatuses(statusPath);
        Assert.Equal(["started", "pluginLoaded", "pluginFailed"], EventNames(statuses));
        Assert.Equal(FixtureId, statuses[1].GetProperty("plugin").GetString());
        Assert.Equal(
            "acdream.test.missing",
            statuses[2].GetProperty("plugin").GetString());
        Assert.Contains(
            "not found",
            statuses[2].GetProperty("error").GetString(),
            StringComparison.OrdinalIgnoreCase);

        WeakReference context = Assert.Single(
            plugins.CaptureLoadContextWeakReferences());
        plugins.Dispose();
        Assert.Equal(0, ui.RegistrationCount);
        Collect(context);
        Assert.False(context.IsAlive);
    }

    [Fact]
    public void ExplicitEmptyConfiguredSetLoadsNone()
    {
        using var temporary = new TemporaryDirectory();
        ApplicationPathSet paths = Paths(temporary.Path);
        InstallFixture(paths.PluginsDirectory, FixtureId);
        string statusPath = Path.Combine(temporary.Path, "status.jsonl");
        string configPath = Path.Combine(temporary.Path, "session.json");
        File.WriteAllText(
            configPath,
            LauncherCoreSessionConfigFixture.ComposeEmptyPlugins());
        (_, SessionDescriptor descriptor) =
            SessionConfigurationLoader.Load(configPath);
        var ui = new BufferedUiRegistry();
        var host = new AppPluginHost(
            new CapturingLogger(),
            new WorldGameState(),
            new WorldEvents(),
            new SelectionState(),
            ui,
            NoOpAutomationSurface.Instance);

        using GraphicalPluginSession plugins = GraphicalPluginSession.Create(
            paths,
            descriptor.Plugins,
            "gui-session",
            host,
            new SessionStatusWriter(statusPath));
        plugins.Start();

        Assert.Equal(0, plugins.LoadedCount);
        Assert.NotNull(descriptor.Plugins);
        Assert.Empty(descriptor.Plugins);
        Assert.Empty(ui.Drain());
        Assert.Equal(["started"], EventNames(ReadStatuses(statusPath)));
    }

    [Fact]
    public void ThrowAfterRegistrationRollsBackUiAndEventsAndCollectsContext()
    {
        using var temporary = new TemporaryDirectory();
        ApplicationPathSet paths = Paths(temporary.Path);
        string pluginDirectory = InstallFixture(
            paths.PluginsDirectory,
            ThrowingId,
            "throwing-fixture");
        File.WriteAllText(
            Path.Combine(pluginDirectory, "throw-after-register"),
            string.Empty);
        string statusPath = Path.Combine(temporary.Path, "status.jsonl");
        var events = new WorldEvents();
        var selection = new SelectionState();
        var ui = new BufferedUiRegistry();
        var host = new AppPluginHost(
            new CapturingLogger(),
            new WorldGameState(),
            events,
            selection,
            ui,
            NoOpAutomationSurface.Instance);

        using GraphicalPluginSession plugins = GraphicalPluginSession.Create(
            paths,
            [ThrowingId],
            "gui-session",
            host,
            new SessionStatusWriter(statusPath));
        plugins.Start();

        Assert.Equal(0, plugins.LoadedCount);
        Assert.Empty(ui.Drain());
        Assert.Equal(0, ui.RegistrationCount);
        events.FireEntitySpawned(new WorldEntitySnapshot(
            1u,
            2u,
            default,
            System.Numerics.Quaternion.Identity));
        Assert.True(((ISelectionService)selection).Select(7u));
        Assert.False(File.Exists(
            Path.Combine(pluginDirectory, "unexpected-callback")));
        Assert.Equal(
            ["started", "pluginFailed"],
            EventNames(ReadStatuses(statusPath)));

        WeakReference context = Assert.Single(
            plugins.CaptureLoadContextWeakReferences());
        plugins.Dispose();
        Collect(context);
        Assert.False(context.IsAlive);
    }

    [Fact]
    public void InitializeFailureRollsBackEveryRegistrationBeforeUnload()
    {
        using var temporary = new TemporaryDirectory();
        ApplicationPathSet paths = Paths(temporary.Path);
        string pluginDirectory = InstallFixture(
            paths.PluginsDirectory,
            InitializeThrowingId,
            "initialize-throwing-fixture");
        File.WriteAllText(
            Path.Combine(pluginDirectory, "throw-during-initialize"),
            string.Empty);
        string statusPath = Path.Combine(temporary.Path, "status.jsonl");
        var events = new WorldEvents();
        var selection = new SelectionState();
        var ui = new BufferedUiRegistry();
        var host = new AppPluginHost(
            new CapturingLogger(),
            new WorldGameState(),
            events,
            selection,
            ui,
            NoOpAutomationSurface.Instance);

        using GraphicalPluginSession plugins = GraphicalPluginSession.Create(
            paths,
            [InitializeThrowingId],
            "gui-session",
            host,
            new SessionStatusWriter(statusPath));
        plugins.Start();

        Assert.Equal(0, plugins.LoadedCount);
        Assert.Empty(ui.Drain());
        Assert.Equal(0, ui.RegistrationCount);
        Assert.Equal(
            "ui=True;events=True;selection=True",
            File.ReadAllText(Path.Combine(
                pluginDirectory,
                "unload-observation")));
        events.FireEntitySpawned(new WorldEntitySnapshot(
            1u,
            2u,
            default,
            System.Numerics.Quaternion.Identity));
        Assert.True(((ISelectionService)selection).Select(9u));
        Assert.False(File.Exists(
            Path.Combine(pluginDirectory, "unexpected-callback")));
        Assert.Equal(
            ["started", "pluginFailed"],
            EventNames(ReadStatuses(statusPath)));

        WeakReference context = Assert.Single(
            plugins.CaptureLoadContextWeakReferences());
        plugins.Dispose();
        Collect(context);
        Assert.False(context.IsAlive);
    }

    private static ApplicationPathSet Paths(string root) => new(
        Path.Combine(root, "config"),
        Path.Combine(root, "data"),
        Path.Combine(root, "cache"),
        LegacyConfigDirectory: null);

    [MethodImpl(MethodImplOptions.NoInlining)]
    private static void AssertPanelWasRegisteredAndReleaseBinding(
        BufferedUiRegistry ui)
    {
        BufferedUiRegistry.Pending panel = Assert.Single(ui.Drain());
        Assert.EndsWith(
            "fixture-panel.xml",
            panel.MarkupPath,
            StringComparison.Ordinal);
        Assert.Equal(FixtureId, panel.Owner.Id);
        Assert.Equal("Host fixture", panel.Owner.DisplayName);
        Assert.Equal("fixture-panel", panel.Descriptor.WindowId);
        Assert.Equal("Host fixture", panel.Descriptor.Title);
        Assert.Equal(
            "AcDream.Plugin.Tests.Fixtures.HostPlugin",
            panel.Binding.GetType().Assembly.GetName().Name);
    }

    private static JsonElement[] ReadStatuses(string path) =>
        File.ReadAllLines(path)
            .Select(static line => JsonDocument.Parse(line).RootElement.Clone())
            .ToArray();

    private static string[] EventNames(IEnumerable<JsonElement> events) =>
        events.Select(static item => item.GetProperty("e").GetString()!).ToArray();

    private static string InstallFixture(
        string root,
        string id,
        string directoryName = "host-fixture")
    {
        string source = FixtureAssemblyPath();
        Assert.True(File.Exists(source), $"fixture DLL not found: {source}");
        string pluginDirectory = Path.Combine(root, directoryName);
        Directory.CreateDirectory(pluginDirectory);
        string fileName = Path.GetFileName(source);
        File.Copy(source, Path.Combine(pluginDirectory, fileName));
        File.WriteAllText(
            Path.Combine(pluginDirectory, "plugin.json"),
            JsonSerializer.Serialize(new
            {
                id,
                displayName = "Host fixture",
                version = "1.0.0",
                entryDll = fileName,
                apiVersion = 1,
            }));
        return pluginDirectory;
    }

    private static string FixtureAssemblyPath()
    {
        string fileName = "AcDream.Plugin.Tests.Fixtures.HostPlugin.dll";
        string colocated = Path.Combine(AppContext.BaseDirectory, fileName);
        if (File.Exists(colocated))
            return colocated;

        string configuration = new DirectoryInfo(AppContext.BaseDirectory)
            .Parent!.Name;
        string root = FindRepoRoot(AppContext.BaseDirectory);
        return Path.Combine(
            root,
            "tests",
            "AcDream.Plugin.Tests.Fixtures.HostPlugin",
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

    private static void Collect(WeakReference reference)
    {
        for (int attempt = 0; attempt < 10 && reference.IsAlive; attempt++)
        {
            GC.Collect();
            GC.WaitForPendingFinalizers();
            GC.Collect();
        }
    }

    private sealed class CapturingLogger : IPluginLogger
    {
        internal List<string> Messages { get; } = [];

        public void Info(string message) => Messages.Add(message);
        public void Warn(string message) => Messages.Add(message);
        public void Error(string message, Exception? exception = null) =>
            Messages.Add(exception is null ? message : $"{message}: {exception.Message}");
    }

    private sealed class TemporaryDirectory : IDisposable
    {
        internal TemporaryDirectory()
        {
            Path = System.IO.Path.Combine(
                System.IO.Path.GetTempPath(),
                $"acdream-graphical-plugins-{Guid.NewGuid():N}");
            Directory.CreateDirectory(Path);
        }

        internal string Path { get; }

        public void Dispose()
        {
            for (int attempt = 0; Directory.Exists(Path); attempt++)
            {
                try
                {
                    Directory.Delete(Path, recursive: true);
                    return;
                }
                catch (Exception error)
                    when (error is IOException or UnauthorizedAccessException
                        && attempt < 9)
                {
                    GC.Collect();
                    GC.WaitForPendingFinalizers();
                    Thread.Sleep(10);
                }
            }
        }
    }
}
