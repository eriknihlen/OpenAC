using System.Text.Json;
using System.Net;
using AcDream.Core.Net;
using AcDream.Core.Net.Messages;
using AcDream.Headless.Configuration;
using AcDream.Headless.Credentials;
using AcDream.Headless.Diagnostics;
using AcDream.Headless.Hosting;
using AcDream.Headless.Plugins;
using AcDream.Plugin.Abstractions;
using AcDream.Runtime;
using AcDream.Runtime.Session;
using AcDream.Tests.Fixtures.CampaignLa;

namespace AcDream.Headless.Tests;

public sealed class HeadlessPluginSessionTests
{
    private const string FixtureId = "acdream.test.host-fixture";
    private const string BrokenId = "acdream.test.broken";
    private const string ThrowingId = "acdream.test.throwing-fixture";

    [Fact]
    public void ConfiguredSetBorrowsRuntimeUsesNoOpUiIsolatesFailureAndUnloads()
    {
        using var temporary = new TemporaryDirectory();
        InstallFixture(temporary.Path, FixtureId);
        InstallBrokenPlugin(temporary.Path, BrokenId);
        var output = new StringWriter();
        var diagnostics = new HeadlessDiagnosticWriter(output);
        string statusPath = Path.Combine(temporary.Path, "status.jsonl");
        var credential = new HeadlessCredentialSecret("fixture", "password");
        using var session = new HeadlessSessionHost(
            Descriptor(
                [FixtureId.ToUpperInvariant(), BrokenId],
                statusPath,
                loginCommands: ["/"],
                loginCommandDelayMs: 0),
            credential,
            diagnostics,
            new FixtureSessionOperations(),
            pluginRoots: [temporary.Path]);
        HeadlessPluginSession plugins = session.Plugins;
        _ = session.Start();
        _ = session.Runtime.EntityObjects.RegisterEntity(Spawn(0x50000001u, 1f));

        Assert.Equal(1, plugins.LoadedCount);
        Assert.False(plugins.Host.HasUi);
        Assert.Same(NoOpUiRegistry.Instance, plugins.Host.Ui);
        Assert.Same(
            session.Runtime.ActionOwner.Selection,
            plugins.Host.Selection);
        WorldEntitySnapshot first = Assert.Single(plugins.Host.State.Entities);
        Assert.Equal(1_000_000u, first.Id);
        Assert.Equal(0x02000001u, first.SourceId);

        _ = session.Runtime.EntityObjects.RegisterEntity(Spawn(0x50000002u, 2f));
        Assert.Equal(2, plugins.Host.State.Entities.Count);

        JsonElement[] statuses = ReadStatuses(statusPath);
        Assert.Equal(
            [
                "started", "pluginLoaded", "pluginFailed", "connected",
                "characterList", "enteredWorld", "loginCommandFailed",
            ],
            EventNames(statuses));
        Assert.Equal(FixtureId, statuses[1].GetProperty("plugin").GetString());
        Assert.Equal(BrokenId, statuses[2].GetProperty("plugin").GetString());
        Assert.Contains(
            "entry dll not found",
            statuses[2].GetProperty("error").GetString()!,
            StringComparison.OrdinalIgnoreCase);
        Assert.Equal(0, statuses[6].GetProperty("commandIndex").GetInt32());
        Assert.Equal("/", statuses[6].GetProperty("command").GetString());
        Assert.Contains("fixture-enabled:hasUi=False:entities=0", output.ToString());

        WeakReference context = Assert.Single(
            plugins.CaptureLoadContextWeakReferences());
        session.Dispose();
        Assert.Contains("fixture-disabled:entitiesSeen=2", output.ToString());
        Assert.True(session.Runtime.CaptureOwnership().IsConverged);
        Assert.True(credential.IsDisposed);
        Collect(context);
        Assert.False(context.IsAlive);
    }

    [Fact]
    public void ExplicitEmptyConfiguredSetLoadsNone()
    {
        using var temporary = new TemporaryDirectory();
        InstallFixture(temporary.Path, FixtureId);
        var output = new StringWriter();
        string statusPath = Path.Combine(temporary.Path, "status.jsonl");
        var credential = new HeadlessCredentialSecret("fixture", "password");

        using var session = new HeadlessSessionHost(
            Descriptor([], statusPath),
            credential,
            new HeadlessDiagnosticWriter(output),
            new FixtureSessionOperations(),
            pluginRoots: [temporary.Path]);

        _ = session.Start();
        Assert.Equal(0, session.Plugins.LoadedCount);
        Assert.Equal(
            ["started", "connected", "characterList", "enteredWorld"],
            EventNames(ReadStatuses(statusPath)));
        Assert.DoesNotContain("fixture-", output.ToString());
    }

    [Fact]
    public void RenderPackOnlyRequest_IsRejectedBeforeHeadlessLoadsItsDll()
    {
        using var temporary = new TemporaryDirectory();
        const string renderPackId = "acdream.test.render-only";
        string pluginDirectory = Path.Combine(temporary.Path, "render-only");
        Directory.CreateDirectory(pluginDirectory);
        File.WriteAllText(
            Path.Combine(pluginDirectory, "plugin.json"),
            JsonSerializer.Serialize(new
            {
                id = renderPackId,
                displayName = "Render only",
                version = "1.0.0",
                entryDll = "deliberately-missing.dll",
                apiVersion = 1,
                kinds = new[] { "renderPack" },
            }));
        string statusPath = Path.Combine(temporary.Path, "status.jsonl");
        var credential = new HeadlessCredentialSecret("fixture", "password");
        using var session = new HeadlessSessionHost(
            Descriptor([renderPackId], statusPath),
            credential,
            new HeadlessDiagnosticWriter(new StringWriter()),
            new FixtureSessionOperations(),
            pluginRoots: [temporary.Path]);

        _ = session.Start();

        Assert.Equal(0, session.Plugins.LoadedCount);
        Assert.Empty(session.Plugins.CaptureLoadContextWeakReferences());
        JsonElement failed = ReadStatuses(statusPath)
            .Single(static item =>
                item.GetProperty("e").GetString() == "pluginFailed");
        string error = failed.GetProperty("error").GetString()!;
        Assert.Contains("does not support", error);
        Assert.DoesNotContain("entry dll", error, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void ThrowAfterRegistrationRollsBackEventsAndCollectsContext()
    {
        using var temporary = new TemporaryDirectory();
        string pluginDirectory = InstallFixture(
            temporary.Path,
            ThrowingId,
            "throwing-fixture");
        File.WriteAllText(
            Path.Combine(pluginDirectory, "throw-after-register"),
            string.Empty);
        string statusPath = Path.Combine(temporary.Path, "status.jsonl");
        var credential = new HeadlessCredentialSecret("fixture", "password");
        using var session = new HeadlessSessionHost(
            Descriptor([ThrowingId], statusPath),
            credential,
            new HeadlessDiagnosticWriter(new StringWriter()),
            new FixtureSessionOperations(),
            pluginRoots: [temporary.Path]);

        _ = session.Start();
        Assert.Equal(0, session.Plugins.LoadedCount);
        _ = session.Runtime.EntityObjects.RegisterEntity(Spawn(0x50000001u, 1f));
        Assert.True(((ISelectionService)session.Runtime.ActionOwner.Selection)
            .Select(7u));
        Assert.False(File.Exists(
            Path.Combine(pluginDirectory, "unexpected-callback")));
        Assert.Equal(
            [
                "started", "pluginFailed", "connected", "characterList",
                "enteredWorld",
            ],
            EventNames(ReadStatuses(statusPath)));

        WeakReference context = Assert.Single(
            session.Plugins.CaptureLoadContextWeakReferences());
        session.Dispose();
        Collect(context);
        Assert.False(context.IsAlive);
    }

    [Fact]
    public void LauncherProbeRoundTripKeepsPluginsDisabledInTheRealHost()
    {
        using var temporary = new TemporaryDirectory();
        InstallFixture(temporary.Path, FixtureId);
        string configPath = Path.Combine(temporary.Path, "probe.json");
        File.WriteAllText(
            configPath,
            LauncherCoreSessionConfigFixture.ComposeProbe());
        HeadlessSessionDescriptor descriptor = Assert.Single(
            HeadlessConfigurationLoader.Load(configPath).Sessions)! with
        {
            StatusFile = Path.Combine(temporary.Path, "probe-status.jsonl"),
        };
        var credential = new HeadlessCredentialSecret("fixture", "password");
        using var session = new HeadlessSessionHost(
            descriptor,
            credential,
            new HeadlessDiagnosticWriter(new StringWriter()),
            new FixtureSessionOperations(),
            pluginRoots: [temporary.Path]);

        RuntimeSessionStartResult result = session.Start();

        Assert.Equal(RuntimeSessionStartStatus.ProbeComplete, result.Status);
        Assert.NotNull(descriptor.Plugins);
        Assert.Empty(descriptor.Plugins);
        Assert.Equal(0, session.Plugins.LoadedCount);
        Assert.Equal(
            ["started", "connected", "characterList"],
            EventNames(ReadStatuses(descriptor.StatusFile!)));
    }

    [Fact]
    public async Task LateSubscriberReplayQueuesConcurrentRegistrationExactlyOnceInOrder()
    {
        using var temporary = new TemporaryDirectory();
        var credential = new HeadlessCredentialSecret("fixture", "password");
        using var session = new HeadlessSessionHost(
            Descriptor([], Path.Combine(temporary.Path, "status.jsonl")),
            credential,
            new HeadlessDiagnosticWriter(new StringWriter()),
            new FixtureSessionOperations(),
            pluginRoots: [temporary.Path]);
        _ = session.Runtime.EntityObjects.RegisterEntity(Spawn(0x50000001u, 1f));
        HeadlessPluginHost host = session.Plugins.Host;
        using var replayCaptured = new ManualResetEventSlim();
        using var releaseReplay = new ManualResetEventSlim();
        host.ReplayCapturedForTest = () =>
        {
            replayCaptured.Set();
            Assert.True(releaseReplay.Wait(TimeSpan.FromSeconds(10)));
        };
        var observed = new List<uint>();
        Action<WorldEntitySnapshot> handler = snapshot =>
        {
            lock (observed)
                observed.Add(snapshot.Id);
        };

        Task subscribe = Task.Run(() => host.Events.EntitySpawned += handler);
        Assert.True(replayCaptured.Wait(TimeSpan.FromSeconds(10)));
        using var registrationStarted = new ManualResetEventSlim();
        Exception? registrationError = null;
        var registrationThread = new Thread(() =>
        {
            registrationStarted.Set();
            try
            {
                _ = session.Runtime.EntityObjects.RegisterEntity(
                    Spawn(0x50000002u, 2f));
            }
            catch (Exception error)
            {
                registrationError = error;
            }
        })
        {
            IsBackground = true,
            Name = "Headless plugin concurrent registration contract",
        };
        registrationThread.Start();
        bool registrationStartedInTime = registrationStarted.Wait(
            TimeSpan.FromSeconds(10));
        bool registrationBlockedOnReplay = registrationStartedInTime &&
            SpinWait.SpinUntil(
                () => (registrationThread.ThreadState & ThreadState.WaitSleepJoin) != 0,
                TimeSpan.FromSeconds(10));
        try
        {
            Assert.True(
                registrationBlockedOnReplay,
                "registration never waited for the active replay read lease");
        }
        finally
        {
            releaseReplay.Set();
        }
        await subscribe.WaitAsync(TimeSpan.FromSeconds(10));
        Assert.True(registrationThread.Join(TimeSpan.FromSeconds(10)));
        Assert.Null(registrationError);
        host.Events.EntitySpawned -= handler;

        Assert.Equal([1_000_000u, 1_000_001u], observed);
    }

    private static HeadlessSessionDescriptor Descriptor(
        List<string> plugins,
        string statusPath,
        IReadOnlyList<string>? loginCommands = null,
        int loginCommandDelayMs = 500) => new()
        {
            Id = "headless-session",
            Endpoint = new HeadlessEndpointDescriptor
            {
                Host = "127.0.0.1",
                Port = 9000,
            },
            Account = "account",
            Character = new HeadlessCharacterSelector
            {
                Name = "Fixture",
            },
            Policy = new HeadlessBotPolicyDescriptor
            {
                Id = "idle",
            },
            Credential = new HeadlessCredentialReference
            {
                Provider = HeadlessCredentialProviderKind.Environment,
                Reference = "FIXTURE_PASSWORD",
            },
            Plugins = plugins,
            StatusFile = statusPath,
            LoginCommands = loginCommands is null ? null : [.. loginCommands],
            LoginCommandDelayMs = loginCommandDelayMs,
        };

    private static WorldSession.EntitySpawn Spawn(uint guid, float x) => new(
        guid,
        new CreateObject.ServerPosition(
            0x01010001u,
            x,
            10f,
            5f,
            1f,
            0f,
            0f,
            0f),
        0x02000001u,
        [],
        [],
        [],
        null,
        null,
        "Fixture",
        null,
        null,
        null);

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
        WriteManifest(pluginDirectory, id, fileName);
        return pluginDirectory;
    }

    private static void InstallBrokenPlugin(string root, string id)
    {
        string pluginDirectory = Path.Combine(root, "broken");
        Directory.CreateDirectory(pluginDirectory);
        WriteManifest(pluginDirectory, id, "missing.dll");
    }

    private static void WriteManifest(
        string directory,
        string id,
        string entryDll) =>
        File.WriteAllText(
            Path.Combine(directory, "plugin.json"),
            JsonSerializer.Serialize(new
            {
                id,
                displayName = "Host fixture",
                version = "1.0.0",
                entryDll,
                apiVersion = 1,
            }));

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

    private sealed class FixtureSessionOperations : ILiveSessionOperations
    {
        private static readonly CharacterList.Parsed Characters = new(
            0u,
            [new CharacterList.Character(0x50000001u, "Fixture", 0u)],
            [],
            1,
            "account",
            true,
            true);

        public IPEndPoint ResolveEndpoint(string host, int port) =>
            new(IPAddress.Loopback, port);

        public WorldSession CreateSession(IPEndPoint endpoint) => new(endpoint);

        public void Connect(WorldSession session, string user, string password)
        {
        }

        public CharacterList.Parsed? GetCharacters(WorldSession session) => Characters;

        public void EnterWorld(WorldSession session, int activeCharacterIndex)
        {
        }

        public void Tick(WorldSession session)
        {
        }

        public void DisposeSession(WorldSession session) => session.Dispose();
    }

    private sealed class TemporaryDirectory : IDisposable
    {
        internal TemporaryDirectory()
        {
            Path = System.IO.Path.Combine(
                System.IO.Path.GetTempPath(),
                $"acdream-headless-plugins-{Guid.NewGuid():N}");
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
