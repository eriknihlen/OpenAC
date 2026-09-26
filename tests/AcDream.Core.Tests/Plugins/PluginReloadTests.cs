using System.Text.Json;
using AcDream.Core.Plugins;
using AcDream.Core.Selection;
using AcDream.Plugin.Abstractions;

namespace AcDream.Core.Tests.Plugins;

/// <summary>
/// Replacing a running plugin with the copy on disk, as both clients do it:
/// the plugin's files are never held open, <c>/plugin reload</c> and a change
/// to the plugin's folder both load a fresh copy, an update this client
/// cannot run leaves the running copy alone, and the old copy is seen to
/// leave memory or named when it does not.
/// </summary>
public sealed class PluginReloadTests
{
    private const string HelloId = "acdream.test.hello";
    private const string HelloFileName = "AcDream.Core.Tests.Fixtures.HelloPlugin.dll";

    /// <summary>
    /// The plugin's assembly and symbols can be replaced and deleted while
    /// the plugin runs. Mutation (2026-09-26): loading the entry assembly
    /// with LoadFromAssemblyPath again made the overwrite fail on Windows,
    /// where a mapped assembly file cannot be replaced.
    /// </summary>
    [Fact]
    public void ARunningPluginsFilesCanBeOverwrittenAndDeleted()
    {
        using var temporary = new TemporaryDirectory();
        string folder = InstallHello(temporary.Path, "hello", HelloId, withSymbols: true);
        var host = new ReloadHost();
        var plugins = NewSession(host);
        plugins.Start([temporary.Path], allowList: null);
        Assert.Equal(1, plugins.LoadedCount);

        string dll = Path.Combine(folder, HelloFileName);
        string pdb = Path.ChangeExtension(dll, ".pdb");
        File.Copy(FixturePath(), dll, overwrite: true);
        File.Delete(pdb);
        File.Delete(dll);

        Assert.False(File.Exists(dll));
        ReleaseAndCollect(plugins);
    }

    /// <summary>
    /// Mutation (2026-09-26): making Reload return at once left one
    /// "hello-initialized" line and no hot-reload flag.
    /// </summary>
    [Fact]
    public void ReloadCommandStartsAFreshCopyOnTheNextTickAndTheOldCopyLeavesMemory()
    {
        using var temporary = new TemporaryDirectory();
        string folder = InstallHello(temporary.Path, "hello", HelloId);
        var host = new ReloadHost();
        var plugins = NewSession(host);
        plugins.Start([temporary.Path], allowList: null);
        WeakReference first = Assert.Single(plugins.CaptureLoadContextWeakReferences());

        Assert.True(host.Commands.TryHandle($"/plugin reload {HelloId}"));
        Assert.Single(host.Logs, static line => line.StartsWith("hello-initialized", StringComparison.Ordinal));

        host.Tick();

        Assert.Equal(
            [
                $"hello-initialized:hotReload=False:directory={folder}",
                $"hello-initialized:hotReload=True:directory={folder}",
            ],
            host.Logs.Where(static line => line.StartsWith("hello-initialized", StringComparison.Ordinal)));
        Assert.Equal(1, host.Logs.Count(static line => line == "hello-disabled"));
        Assert.Contains("Reloaded acdream.test.hello 1.0.0.", host.Chat);
        Assert.Equal([HelloId], plugins.LoadedPluginIds);

        Collect(first);
        Assert.False(first.IsAlive);
        _watch.Poll();
        host.Tick();
        Assert.Contains(host.Logs, static line => line.Contains("has left memory", StringComparison.Ordinal));
        Assert.DoesNotContain(host.Chat, static line => line.Contains("still in memory", StringComparison.Ordinal));
        ReleaseAndCollect(plugins);
    }

    [Fact]
    public void ReloadAllReloadsEveryRunningPlugin()
    {
        using var temporary = new TemporaryDirectory();
        InstallHello(temporary.Path, "alpha", "acdream.test.alpha");
        InstallHello(temporary.Path, "beta", "acdream.test.beta");
        var host = new ReloadHost();
        var plugins = NewSession(host);
        plugins.Start([temporary.Path], allowList: null);

        Assert.True(host.Commands.TryHandle("/plugin reload all"));
        host.Tick();

        Assert.Equal(
            ["Reloaded acdream.test.alpha 1.0.0.", "Reloaded acdream.test.beta 1.0.0."],
            host.Chat);
        Assert.Equal(["acdream.test.alpha", "acdream.test.beta"], plugins.LoadedPluginIds);
        ReleaseAndCollect(plugins);
    }

    [Fact]
    public void ReloadingAPluginThatIsNotRunningSaysSo()
    {
        using var temporary = new TemporaryDirectory();
        var host = new ReloadHost();
        using var plugins = NewSession(host);
        plugins.Start([temporary.Path], allowList: null);

        Assert.True(host.Commands.TryHandle("/plugin reload acdream.test.absent"));
        Assert.True(host.Commands.TryHandle("/plugin"));
        host.Tick();

        Assert.Equal(
            [
                "Usage: /plugin reload <plugin id>, or /plugin reload all",
                "No plugin 'acdream.test.absent' is running in this client.",
            ],
            host.Chat);
    }

    /// <summary>
    /// The new copy is read and checked before the running one is let go,
    /// so an update this client cannot run changes nothing. Mutation
    /// (2026-09-26): skipping RefuseReplacement disabled the running copy
    /// and left the plugin off.
    /// </summary>
    [Theory]
    [InlineData("minHostVersion", "requires OpenAC 9.0.0 or newer (this is 0.1.20)")]
    [InlineData("apiVersion", "needs plugin API version 99")]
    public void ANewCopyThisClientCannotRunLeavesTheRunningCopyAlone(
        string field,
        string expected)
    {
        using var temporary = new TemporaryDirectory();
        string folder = InstallHello(temporary.Path, "hello", HelloId);
        var host = new ReloadHost();
        var plugins = new PluginSession(
            host,
            report: null,
            renderPacks: null,
            supportedKinds: null,
            hostKind: PluginHostKind.Graphical,
            hostVersion: PluginHostVersion.FromInformationalVersion("0.1.20"),
            timeProvider: null,
            unloadWatch: _watch);
        plugins.Start([temporary.Path], allowList: null);
        WriteManifest(
            folder,
            HelloId,
            version: "2.0.0",
            apiVersion: field == "apiVersion" ? 99 : 1,
            minHostVersion: field == "minHostVersion" ? "9.0.0" : null);

        plugins.RequestReload(HelloId);
        host.Tick();

        string line = Assert.Single(host.Chat);
        Assert.Contains("acdream.test.hello 2.0.0 was not loaded", line);
        Assert.Contains(expected, line);
        Assert.Contains("The running copy stays.", line);
        Assert.DoesNotContain("hello-disabled", host.Logs);
        Assert.Single(host.Logs, static entry => entry.StartsWith("hello-initialized", StringComparison.Ordinal));
        Assert.Equal([HelloId], plugins.LoadedPluginIds);
        ReleaseAndCollect(plugins);
    }

    /// <summary>
    /// Mutation (2026-09-26): taking the call that raises the missed login
    /// out of Activate left no "hello-login" line after the reload.
    /// </summary>
    [Fact]
    public void APluginStartedWhileTheCharacterIsInTheWorldHearsTheLogin()
    {
        using var temporary = new TemporaryDirectory();
        InstallHello(temporary.Path, "hello", HelloId);
        var host = new ReloadHost();
        var plugins = NewSession(host);
        plugins.Start([temporary.Path], allowList: null);
        Assert.DoesNotContain("hello-login", host.Logs);

        host.Automation.InWorld = true;
        plugins.RequestReload(HelloId);
        host.Tick();

        Assert.Single(host.Logs, static line => line == "hello-login");
        ReleaseAndCollect(plugins);
    }

    /// <summary>
    /// A reload runs inside a tick, after that tick has taken its list of
    /// handlers. Mutation (2026-09-26): registering the plugin's own tick
    /// handler with the host unguarded called the disabled copy from the
    /// rest of the tick that unloaded it.
    /// </summary>
    [Fact]
    public void AnUnloadedCopyIsNotTickedByTheTickThatUnloadedIt()
    {
        using var temporary = new TemporaryDirectory();
        InstallHello(temporary.Path, "hello", HelloId);
        var host = new ReloadHost();
        var plugins = NewSession(host);
        plugins.Start([temporary.Path], allowList: null);

        // The first new copy subscribes after the session's own handler, so
        // the second reload unloads it from inside a tick that will still
        // call it.
        plugins.RequestReload(HelloId);
        host.Tick();
        plugins.RequestReload(HelloId);
        host.Tick();

        Assert.Equal(2, host.Chat.Count(static line => line.StartsWith("Reloaded", StringComparison.Ordinal)));
        Assert.DoesNotContain("hello-tick-after-disable", host.Logs);
        ReleaseAndCollect(plugins);
    }

    /// <summary>
    /// A previous copy still in memory when the watch ends is named once, in
    /// plain terms, and not before: the runtime is given the whole watch to
    /// collect it in its own time. Mutation (2026-09-26): reporting on the
    /// first poll that found the copy alive named it at once.
    /// </summary>
    [Fact]
    public void AnOldCopyStillInMemoryWhenTheWatchEndsIsNamedOnceInChat()
    {
        using var temporary = new TemporaryDirectory();
        string folder = InstallHello(temporary.Path, "leaky", "acdream.test.leaky");
        File.WriteAllText(Path.Combine(folder, "leak-on-enable"), string.Empty);
        var host = new ReloadHost();
        using var plugins = NewSession(host);
        plugins.Start([temporary.Path], allowList: null);

        plugins.RequestReload("acdream.test.leaky");
        host.Tick();
        _time.Advance(PluginUnloadWatch.Window - TimeSpan.FromSeconds(1));
        _watch.Poll();
        host.Tick();
        Assert.DoesNotContain(host.Chat, static line => line.Contains("still in memory", StringComparison.Ordinal));

        _time.Advance(TimeSpan.FromSeconds(1));
        _watch.Poll();
        _watch.Poll();
        host.Tick(2);

        Assert.Equal(
            ["The previous copy of acdream.test.leaky is still in memory; it is freed when the client restarts."],
            host.Chat.Where(static line => line.Contains("still in memory", StringComparison.Ordinal)));
    }

    /// <summary>
    /// A reload never forces a collection on the thread that ticks the game:
    /// the old copy is looked at later, from the watch. Mutation (2026-09-26):
    /// the earlier check collected on every eighth tick for a second.
    /// </summary>
    [Fact]
    public void TickingAfterAReloadDoesNotCollect()
    {
        using var temporary = new TemporaryDirectory();
        InstallHello(temporary.Path, "hello", HelloId);
        var host = new ReloadHost();
        var plugins = NewSession(host);
        plugins.Start([temporary.Path], allowList: null);
        plugins.RequestReload(HelloId);
        host.Tick();

        int before = GC.CollectionCount(GC.MaxGeneration);
        host.Tick(200);

        Assert.Equal(before, GC.CollectionCount(GC.MaxGeneration));
        ReleaseAndCollect(plugins);
    }

    /// <summary>
    /// A change to the entry assembly marks the plugin, and it reloads once
    /// its folder has been quiet for the quiet period. Mutation (2026-09-26):
    /// ignoring the quiet period reloaded on the first tick after the change.
    /// </summary>
    [Fact]
    public void ChangingTheEntryAssemblyReloadsThePluginOnceItsFolderIsQuiet()
    {
        using var temporary = new TemporaryDirectory();
        string folder = InstallHello(temporary.Path, "hello", HelloId);
        var time = new ManualTime();
        var host = new ReloadHost();
        var plugins = NewSession(host, time);
        plugins.Start([temporary.Path], allowList: null);

        File.Copy(FixturePath(), Path.Combine(folder, HelloFileName), overwrite: true);
        WaitFor(() => plugins.HasPendingFileChange(HelloId));

        host.Tick();
        Assert.Empty(host.Chat);

        for (int attempt = 0; attempt < 20 && host.Chat.Count == 0; attempt++)
        {
            time.Advance(PluginSession.QuietPeriod);
            host.Tick();
        }
        Assert.Equal(["Reloaded acdream.test.hello 1.0.0."], host.Chat);
        ReleaseAndCollect(plugins);
    }

    /// <summary>
    /// What a plugin writes into its own <c>files</c> folder, or beside its
    /// code as a log or data file, is not a new copy of the plugin: it
    /// neither marks the plugin nor holds back a reload that is waiting for
    /// the code to go quiet, which a plugin writing every few ticks would
    /// otherwise do forever. Mutation (2026-09-26): counting every write
    /// outside the files folder kept restarting the quiet period, and the
    /// plugin did not reload.
    /// </summary>
    [Fact]
    public void WritingThePlayersFilesNeitherMarksNorHoldsBackAReload()
    {
        using var temporary = new TemporaryDirectory();
        string folder = InstallHello(temporary.Path, "hello", HelloId);
        var time = new ManualTime();
        var host = new ReloadHost();
        var plugins = NewSession(host, time);
        plugins.Start([temporary.Path], allowList: null);

        string files = Path.Combine(folder, "files");
        Directory.CreateDirectory(files);
        File.WriteAllText(Path.Combine(files, "plugin.json"), "{}");
        File.WriteAllText(Path.Combine(files, HelloFileName), "not the plugin");
        File.WriteAllText(Path.Combine(folder, "plugin.log"), "a line");
        Thread.Sleep(300);
        Assert.False(plugins.HasPendingFileChange(HelloId));

        File.SetLastWriteTimeUtc(Path.Combine(folder, "plugin.json"), DateTime.UtcNow);
        WaitFor(() => plugins.HasPendingFileChange(HelloId));
        Thread.Sleep(300);
        time.Advance(TimeSpan.FromMilliseconds(600));
        File.WriteAllText(Path.Combine(files, "state.json"), "{\"saved\":true}");
        File.AppendAllText(Path.Combine(folder, "plugin.log"), "another line");
        Thread.Sleep(300);
        time.Advance(TimeSpan.FromMilliseconds(500));
        host.Tick();

        Assert.Equal(["Reloaded acdream.test.hello 1.0.0."], host.Chat);
        ReleaseAndCollect(plugins);
    }

    /// <summary>
    /// A new copy that starts and then throws from Enable leaves the plugin
    /// off; the next reload brings it back. Mutation (2026-09-26): not
    /// remembering the plugin's folder when it goes off made the second
    /// reload answer "No plugin ... is running".
    /// </summary>
    [Fact]
    public void ANewCopyThatFailsToStartIsOffUntilTheNextReload()
    {
        using var temporary = new TemporaryDirectory();
        string folder = InstallHello(temporary.Path, "hello", HelloId);
        var host = new ReloadHost();
        var plugins = NewSession(host);
        plugins.Start([temporary.Path], allowList: null);

        string marker = Path.Combine(folder, "throw-on-enable");
        File.WriteAllText(marker, string.Empty);
        plugins.RequestReload(HelloId);
        host.Tick();
        Assert.StartsWith("acdream.test.hello 1.0.0 failed to start", Assert.Single(host.Chat));
        Assert.Empty(plugins.LoadedPluginIds);

        File.Delete(marker);
        plugins.RequestReload(HelloId);
        host.Tick();
        Assert.Equal("Reloaded acdream.test.hello 1.0.0.", host.Chat[^1]);
        Assert.Equal([HelloId], plugins.LoadedPluginIds);
        ReleaseAndCollect(plugins);
    }

    /// <summary>
    /// A new copy that cannot even be loaded never replaces the running
    /// copy, and a later good copy does.
    /// </summary>
    [Fact]
    public void ANewCopyThatCannotBeLoadedLeavesTheRunningCopy()
    {
        using var temporary = new TemporaryDirectory();
        string folder = InstallHello(temporary.Path, "hello", HelloId);
        var host = new ReloadHost();
        var plugins = NewSession(host);
        plugins.Start([temporary.Path], allowList: null);

        WriteManifest(folder, HelloId, version: "2.0.0", entryDll: "missing.dll");
        plugins.RequestReload(HelloId);
        host.Tick();
        Assert.Contains("could not be loaded", Assert.Single(host.Chat));
        Assert.Equal([HelloId], plugins.LoadedPluginIds);

        WriteManifest(folder, HelloId, version: "2.0.1");
        plugins.RequestReload(HelloId);
        host.Tick();
        Assert.Equal("Reloaded acdream.test.hello 2.0.1.", host.Chat[^1]);
        Assert.Equal([HelloId], plugins.LoadedPluginIds);
        ReleaseAndCollect(plugins);
    }

    /// <summary>
    /// A plugin that failed when the client started is not forgotten: its
    /// folder is watched and asking for it by name tries it again.
    /// Mutation (2026-09-26): not keeping plugins that failed at startup made
    /// the reload answer "No plugin ... is running".
    /// </summary>
    [Fact]
    public void APluginThatFailedAtStartupIsTriedAgain()
    {
        using var temporary = new TemporaryDirectory();
        string folder = InstallHello(temporary.Path, "hello", HelloId);
        string marker = Path.Combine(folder, "throw-on-enable");
        File.WriteAllText(marker, string.Empty);
        var time = new ManualTime();
        var host = new ReloadHost();
        var plugins = NewSession(host, time);
        plugins.Start([temporary.Path], allowList: null);
        Assert.Empty(plugins.LoadedPluginIds);

        File.Delete(marker);
        File.SetLastWriteTimeUtc(Path.Combine(folder, "plugin.json"), DateTime.UtcNow);
        WaitFor(() => plugins.HasPendingFileChange(HelloId));
        time.Advance(PluginSession.QuietPeriod);
        host.Tick();
        Assert.Equal(["Reloaded acdream.test.hello 1.0.0."], host.Chat);
        Assert.Equal([HelloId], plugins.LoadedPluginIds);
        ReleaseAndCollect(plugins);
    }

    [Fact]
    public void APluginWhoseManifestWasUnreadableAtStartupIsTriedAgainByName()
    {
        using var temporary = new TemporaryDirectory();
        string folder = InstallHello(temporary.Path, "hello", HelloId);
        string manifest = File.ReadAllText(Path.Combine(folder, "plugin.json"));
        File.WriteAllText(Path.Combine(folder, "plugin.json"), "{ not json");
        var host = new ReloadHost();
        var plugins = NewSession(host);
        plugins.Start([temporary.Path], ["hello"]);
        Assert.Empty(plugins.LoadedPluginIds);

        File.WriteAllText(Path.Combine(folder, "plugin.json"), manifest.Replace(HelloId, "hello", StringComparison.Ordinal));
        Assert.True(host.Commands.TryHandle("/plugin reload hello"));
        host.Tick();
        Assert.Equal(["Reloaded hello 1.0.0."], host.Chat);
        ReleaseAndCollect(plugins);
    }

    /// <summary>
    /// The assemblies a plugin runs are the ones read when it was prepared.
    /// Mutation (2026-09-26): reading the assembly file when it is loaded,
    /// as the first version did, loaded whatever an update had just written.
    /// </summary>
    [Fact]
    public void APluginsAssembliesComeFromTheCopyReadWhenItWasPrepared()
    {
        using var temporary = new TemporaryDirectory();
        string folder = InstallHello(temporary.Path, "hello", HelloId);
        string dll = Path.Combine(folder, HelloFileName);
        var package = PluginPackageSnapshot.Read(folder);
        var context = new PluginAssemblyLoadContext(package, dll);
        try
        {
            File.WriteAllBytes(dll, [1, 2, 3]);
            File.Copy(FixturePath(), Path.Combine(folder, "Late.dll"));

            Assert.Equal("AcDream.Core.Tests.Fixtures.HelloPlugin", context.LoadEntry(dll).GetName().Name);
            Assert.Throws<FileNotFoundException>(
                () => context.LoadEntry(Path.Combine(folder, "Late.dll")));
        }
        finally
        {
            context.Unload();
        }
    }

    private static void Collect(WeakReference context)
    {
        for (int attempt = 0; attempt < 10 && context.IsAlive; attempt++)
        {
            GC.Collect();
            GC.WaitForPendingFinalizers();
            GC.Collect();
        }
    }

    private readonly ManualTime _time = new();
    private readonly PluginUnloadWatch _watch;

    public PluginReloadTests() => _watch = new PluginUnloadWatch(_time, runTimer: false);

    private PluginSession NewSession(ReloadHost host, TimeProvider? time = null) =>
        new(
            host,
            report: null,
            renderPacks: null,
            supportedKinds: null,
            hostKind: null,
            hostVersion: null,
            timeProvider: time,
            unloadWatch: _watch);

    private static void WaitFor(Func<bool> condition)
    {
        for (int attempt = 0; attempt < 200 && !condition(); attempt++)
            Thread.Sleep(50);
        Assert.True(condition(), "the folder change was never seen");
    }

    private static void ReleaseAndCollect(PluginSession plugins)
    {
        IReadOnlyList<WeakReference> contexts = plugins.CaptureLoadContextWeakReferences();
        plugins.Dispose();
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

    private static string InstallHello(
        string root,
        string folderName,
        string id,
        bool withSymbols = false)
    {
        string source = FixturePath();
        Assert.True(File.Exists(source), $"fixture DLL not found: {source}");
        string folder = Path.Combine(root, folderName);
        Directory.CreateDirectory(folder);
        File.Copy(source, Path.Combine(folder, HelloFileName));
        string symbols = Path.ChangeExtension(source, ".pdb");
        if (withSymbols)
        {
            Assert.True(File.Exists(symbols), $"fixture symbols not found: {symbols}");
            File.Copy(symbols, Path.Combine(folder, Path.GetFileName(symbols)));
        }
        WriteManifest(folder, id, version: "1.0.0");
        return folder;
    }

    private static void WriteManifest(
        string folder,
        string id,
        string version,
        int apiVersion = 1,
        string? minHostVersion = null,
        string entryDll = HelloFileName) =>
        File.WriteAllText(
            Path.Combine(folder, "plugin.json"),
            JsonSerializer.Serialize(new
            {
                id,
                displayName = id,
                version,
                entryDll,
                apiVersion,
                minHostVersion,
            }));

    private static string FixturePath()
    {
        string colocated = Path.Combine(AppContext.BaseDirectory, HelloFileName);
        if (File.Exists(colocated))
            return colocated;
        string configuration = new DirectoryInfo(AppContext.BaseDirectory).Parent!.Name;
        DirectoryInfo? directory = new(AppContext.BaseDirectory);
        while (directory is not null
            && !File.Exists(Path.Combine(directory.FullName, "AcDream.slnx")))
        {
            directory = directory.Parent;
        }
        return Path.Combine(
            directory!.FullName,
            "tests",
            "AcDream.Core.Tests.Fixtures.HelloPlugin",
            "bin",
            configuration,
            "net10.0",
            HelloFileName);
    }

    private sealed class ManualTime : TimeProvider
    {
        private long _ticks;

        public override long TimestampFrequency => TimeSpan.TicksPerSecond;

        public override long GetTimestamp() => Interlocked.Read(ref _ticks);

        internal void Advance(TimeSpan by) => Interlocked.Add(ref _ticks, by.Ticks);
    }

    /// <summary>
    /// A host with the pieces a reload touches: a real command registry, the
    /// real event source so the test can tick it, and chat and log that
    /// record what was said.
    /// </summary>
    private sealed class ReloadHost : IPluginHost
    {
        private readonly WorldEvents _events = new();
        private readonly RecordingLog _log = new();

        public bool HasUi => false;
        public IPluginLogger Log => _log;
        public IGameState State { get; } = new WorldGameState();
        public IEvents Events => _events;
        public ISelectionService Selection { get; } = new SelectionState();
        public IUiRegistry Ui => NoOpUiRegistry.Instance;
        public PluginCommandRegistry Commands { get; } = new();
        IPluginCommandRegistry IPluginHost.Commands => Commands;
        public RecordingAutomation Automation { get; } = new();
        IAutomationSurface IPluginHost.Automation => Automation;

        internal List<string> Logs => _log.Lines;
        internal List<string> Chat => Automation.Lines;

        internal void Tick(int count = 1)
        {
            for (int index = 0; index < count; index++)
                _events.FireTick(0.015);
        }
    }

    private sealed class RecordingLog : IPluginLogger
    {
        internal List<string> Lines { get; } = [];

        public void Info(string message) => Lines.Add(message);
        public void Warn(string message) => Lines.Add(message);
        public void Error(string message, Exception? exception = null) =>
            Lines.Add(exception is null ? message : $"{message}: {exception.Message}");
    }

    private sealed class RecordingAutomation : IAutomationSurface, ICharacterInfo, IPluginChat
    {
        internal bool InWorld { get; set; }
        internal List<string> Lines { get; } = [];

        public bool IsAvailable => true;
        public ICharacterInfo Character => this;
        public ISpellCatalog Spells => NoOpAutomationSurface.Instance;
        public IMagicCommands Magic => NoOpAutomationSurface.Instance;
        public IPluginChat Chat => this;

        public bool IsInWorld => InWorld;
        public uint ObjectId => 0u;
        public uint CurrentHealth => 0u;
        public uint MaxHealth => 0u;
        public uint CurrentStamina => 0u;
        public uint MaxStamina => 0u;
        public uint CurrentMana => 0u;
        public uint MaxMana => 0u;
        public IReadOnlyList<PluginSkillInfo> Skills => [];
        public IReadOnlyList<PluginAttributeInfo> Attributes => [];
        public IReadOnlyList<PluginActiveEnchantment> ActiveEnchantments => [];

        public bool TryGetSkill(uint skillId, out PluginSkillInfo skill)
        {
            skill = default;
            return false;
        }

        public void PostSystemMessage(string text) => Lines.Add(text);
    }

    private sealed class TemporaryDirectory : IDisposable
    {
        internal TemporaryDirectory()
        {
            Path = System.IO.Path.Combine(
                System.IO.Path.GetTempPath(),
                $"acdream-plugin-reload-{Guid.NewGuid():N}");
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
