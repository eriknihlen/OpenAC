using System.Collections.Immutable;
using System.Numerics;
using System.Reflection;
using System.Text.Json;
using System.Net;
using AcDream.Content;
using AcDream.Core.Chat;
using AcDream.Core.Items;
using AcDream.Core.Net;
using AcDream.Core.Net.Messages;
using AcDream.Core.Physics;
using AcDream.Core.Plugins;
using AcDream.Headless.Configuration;
using AcDream.Headless.Credentials;
using AcDream.Headless.Diagnostics;
using AcDream.Headless.Hosting;
using AcDream.Headless.Plugins;
using AcDream.Plugin.Abstractions;
using AcDream.Runtime;
using AcDream.Runtime.Entities;
using AcDream.Runtime.Plugins;
using AcDream.Runtime.Session;
using AcDream.Tests.Fixtures.LauncherSession;
using DatReaderWriter;
using DatReaderWriter.Options;

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
        Assert.Contains(
            "fixture-status:available=True:published=True:read=enabled",
            output.ToString());

        WeakReference context = Assert.Single(
            plugins.CaptureLoadContextWeakReferences());
        session.Dispose();
        Assert.Contains("fixture-disabled:entitiesSeen=2", output.ToString());
        Assert.True(session.Runtime.CaptureOwnership().IsConverged);
        Assert.True(credential.IsDisposed);
        Collect(context);
        Assert.False(context.IsAlive);
    }

    /// <summary>
    /// <c>/plugin reload</c> through this client's own command route loads a
    /// fresh copy on the next tick, tells a copy started in the world about
    /// the login it missed, and lets the old copy leave memory. The same
    /// script runs against the client with a window in
    /// GraphicalPluginSessionTests. Mutation (2026-09-26): making the
    /// session's reload return at once left one "fixture-hot-reload" line.
    /// </summary>
    [Fact]
    public void ReloadCommandLoadsAFreshCopyAndTheOldOneLeavesMemory()
    {
        using var temporary = new TemporaryDirectory();
        InstallFixture(temporary.Path, FixtureId);
        var output = new StringWriter();
        var diagnostics = new HeadlessDiagnosticWriter(output);
        string statusPath = Path.Combine(temporary.Path, "status.jsonl");
        var credential = new HeadlessCredentialSecret("fixture", "password");
        using var session = new HeadlessSessionHost(
            Descriptor(
                [FixtureId],
                statusPath,
                loginCommands: [],
                loginCommandDelayMs: 0),
            credential,
            diagnostics,
            new FixtureSessionOperations(),
            pluginRoots: [temporary.Path]);
        HeadlessPluginSession plugins = session.Plugins;
        _ = session.Start();
        WeakReference first = Assert.Single(plugins.CaptureLoadContextWeakReferences());
        int loginsBefore = Count(output.ToString(), "fixture-login");

        Assert.True(plugins.Host.TryHandlePluginCommand($"/plugin reload {FixtureId}"));
        plugins.Host.FireTick(0.015);

        string log = output.ToString();
        Assert.Equal(1, Count(log, "fixture-hot-reload=False"));
        Assert.Equal(1, Count(log, "fixture-hot-reload=True"));
        Assert.Contains("Reloaded Host fixture 1.0.0.", log);
        Assert.Equal(loginsBefore + 1, Count(log, "fixture-login"));
        Assert.Equal(1, plugins.LoadedCount);

        plugins.Host.FireTick(0.015);
        Collect(first);
        Assert.False(first.IsAlive);

        IReadOnlyList<WeakReference> contexts = plugins.CaptureLoadContextWeakReferences();
        session.Dispose();
        foreach (WeakReference context in contexts)
            Collect(context);
        Assert.All(contexts, static context => Assert.False(context.IsAlive));
    }

    private static int Count(string text, string value)
    {
        int count = 0;
        for (int index = text.IndexOf(value, StringComparison.Ordinal);
             index >= 0;
             index = text.IndexOf(value, index + value.Length, StringComparison.Ordinal))
        {
            count++;
        }
        return count;
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

    /// <summary>
    /// A plugin driven by a bot has to be able to remember things, exactly
    /// as one driven by a window does. Without its own storage the host
    /// hands every plugin the no-op store: reads answer "nothing is there"
    /// and writes vanish, so a plugin that keeps state beside the shared
    /// profile files silently runs on defaults with nothing in the record
    /// to say so.
    /// </summary>
    /// <summary>
    /// A plugin that publishes its loot rules loads headless as it does with
    /// a window: the session hosts the same classifier directory, so the
    /// registration is taken rather than refused, and it is listed.
    /// Mutation: leave the headless host on the contract's no-op registry and
    /// the registration throws.
    /// </summary>
    [Fact]
    public void APluginCanPublishALootClassifierInAHeadlessSession()
    {
        using var temporary = new TemporaryDirectory();
        var credential = new HeadlessCredentialSecret("fixture", "password");
        using var session = new HeadlessSessionHost(
            Descriptor([], Path.Combine(temporary.Path, "status.jsonl")),
            credential,
            new HeadlessDiagnosticWriter(new StringWriter()),
            new FixtureSessionOperations(),
            pluginRoots: [temporary.Path]);

        IPluginLootClassifierRegistry registry = session.Plugins.Host.LootClassifiers;
        using IDisposable registration = registry.Register(
            "fixture",
            "Fixture classifier",
            new IndifferentClassifier());

        Assert.Contains(
            registry.Available,
            static info => info.DisplayName == "Fixture classifier");
    }

    private sealed class IndifferentClassifier : IPluginLootClassifier
    {
        public PluginLootClassification Classify(
            in PluginLootClassificationContext context) => default;
    }

    [Fact]
    public void APluginsOwnStorageIsReadableAndWritableInAHeadlessSession()
    {
        using var temporary = new TemporaryDirectory();
        string storageRoot = Path.Combine(temporary.Path, "plugin-storage");
        var credential = new HeadlessCredentialSecret("fixture", "password");
        using var session = new HeadlessSessionHost(
            Descriptor([], Path.Combine(temporary.Path, "status.jsonl")),
            credential,
            new HeadlessDiagnosticWriter(new StringWriter()),
            new FixtureSessionOperations(),
            pluginRoots: [temporary.Path],
            storage: new AcDream.Core.Plugins.FilePluginStorage(storageRoot));

        IPluginStorage storage = session.Plugins.Host.Storage;

        Assert.True(storage.IsAvailable);
        storage.WriteText("edwards.tank/profiles/macro/thing.json", "{}");
        Assert.Equal(
            "{}",
            storage.ReadText("edwards.tank/profiles/macro/thing.json"));
        Assert.Contains(
            "edwards.tank/profiles/macro/thing.json",
            storage.List("edwards.tank"));
        Assert.True(File.Exists(Path.Combine(
            storageRoot, "edwards.tank", "profiles", "macro", "thing.json")));
        Assert.True(storage.Delete("edwards.tank/profiles/macro/thing.json"));
    }

    /// <summary>
    /// And a session given no storage still writes nothing anywhere: the
    /// no-op store stays the default, so a host that never configured one
    /// cannot start leaving files behind.
    /// </summary>
    [Fact]
    public void ASessionWithNoConfiguredPluginStorageKeepsTheNoOpStore()
    {
        using var temporary = new TemporaryDirectory();
        var credential = new HeadlessCredentialSecret("fixture", "password");
        using var session = new HeadlessSessionHost(
            Descriptor([], Path.Combine(temporary.Path, "status.jsonl")),
            credential,
            new HeadlessDiagnosticWriter(new StringWriter()),
            new FixtureSessionOperations(),
            pluginRoots: [temporary.Path]);

        Assert.Same(NoOpPluginStorage.Instance, session.Plugins.Host.Storage);
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

    [Fact]
    public void SessionTickFiresThePluginHostTickForEveryRegisteredHandler()
    {
        using var temporary = new TemporaryDirectory();
        var credential = new HeadlessCredentialSecret("fixture", "password");
        using var session = new HeadlessSessionHost(
            Descriptor([], Path.Combine(temporary.Path, "status.jsonl")),
            credential,
            new HeadlessDiagnosticWriter(new StringWriter()),
            new FixtureSessionOperations(),
            pluginRoots: [temporary.Path]);

        var firstElapsed = new List<double>();
        var secondElapsed = new List<double>();
        Action<double> first = elapsed => firstElapsed.Add(elapsed);
        Action<double> second = elapsed => secondElapsed.Add(elapsed);
        session.Plugins.Host.Events.Tick += first;
        session.Plugins.Host.Events.Tick += second;

        // The plugin tick is paced by the runtime's clock rather than by the
        // length of a session turn, so a turn is worth however many whole
        // steps have gone by and each one carries exactly the step.
        session.Tick(0.010d);
        session.Tick(0.010d);
        session.Tick(0.010d);

        double step = AcDream.Runtime.Plugins.RuntimePluginTickClock.StepSeconds;
        Assert.Equal([step, step], firstElapsed);
        Assert.Equal([step, step], secondElapsed);
    }

    [Fact]
    public void AThrowingTickHandlerLogsAWarningAndDoesNotStopALaterHandler()
    {
        using var temporary = new TemporaryDirectory();
        var credential = new HeadlessCredentialSecret("fixture", "password");
        var diagnosticsOutput = new StringWriter();
        using var session = new HeadlessSessionHost(
            Descriptor([], Path.Combine(temporary.Path, "status.jsonl")),
            credential,
            new HeadlessDiagnosticWriter(diagnosticsOutput),
            new FixtureSessionOperations(),
            pluginRoots: [temporary.Path]);

        var laterElapsed = new List<double>();
        Action<double> throwing = _ => throw new InvalidOperationException("boom");
        Action<double> later = elapsed => laterElapsed.Add(elapsed);
        session.Plugins.Host.Events.Tick += throwing;
        session.Plugins.Host.Events.Tick += later;

        session.Tick(AcDream.Runtime.Plugins.RuntimePluginTickClock.StepSeconds);

        Assert.Equal(
            [AcDream.Runtime.Plugins.RuntimePluginTickClock.StepSeconds],
            laterElapsed);
        Assert.Contains(
            "plugin-warn:",
            diagnosticsOutput.ToString(),
            StringComparison.Ordinal);
    }

    [Fact]
    public void AutomationIsAvailableTracksRuntimeInWorldLifecycle()
    {
        using var temporary = new TemporaryDirectory();
        var credential = new HeadlessCredentialSecret("fixture", "password");
        using var session = new HeadlessSessionHost(
            Descriptor([], Path.Combine(temporary.Path, "status.jsonl")),
            credential,
            new HeadlessDiagnosticWriter(new StringWriter()),
            new FixtureSessionOperations(),
            pluginRoots: [temporary.Path]);

        Assert.False(session.Plugins.Host.Automation.IsAvailable);

        _ = session.Start();

        Assert.True(session.Plugins.Host.Automation.IsAvailable);
    }

    [Fact]
    public void SessionSettingsForwardsTheDescriptorsPluginSettings()
    {
        using var temporary = new TemporaryDirectory();
        var credential = new HeadlessCredentialSecret("fixture", "password");
        var declared = new Dictionary<string, Dictionary<string, string>>
        {
            [FixtureId] = new() { ["startMacro"] = "true" },
        };
        using var session = new HeadlessSessionHost(
            Descriptor(
                [],
                Path.Combine(temporary.Path, "status.jsonl"),
                pluginSettings: declared),
            credential,
            new HeadlessDiagnosticWriter(new StringWriter()),
            new FixtureSessionOperations(),
            pluginRoots: [temporary.Path]);

        Assert.Equal(
            "true",
            session.Plugins.Host.SessionSettingsFor(FixtureId)["startMacro"]);
    }

    [Fact]
    public void SessionSettingsForwardsDifferentPluginsToDifferentIds()
    {
        const string otherId = "acdream.test.other-fixture";
        using var temporary = new TemporaryDirectory();
        var credential = new HeadlessCredentialSecret("fixture", "password");
        var declared = new Dictionary<string, Dictionary<string, string>>
        {
            [FixtureId] = new() { ["startMacro"] = "true" },
            [otherId] = new() { ["startMacro"] = "false" },
        };
        using var session = new HeadlessSessionHost(
            Descriptor(
                [],
                Path.Combine(temporary.Path, "status.jsonl"),
                pluginSettings: declared),
            credential,
            new HeadlessDiagnosticWriter(new StringWriter()),
            new FixtureSessionOperations(),
            pluginRoots: [temporary.Path]);

        Assert.Equal(
            "true",
            session.Plugins.Host.SessionSettingsFor(FixtureId)["startMacro"]);
        Assert.Equal(
            "false",
            session.Plugins.Host.SessionSettingsFor(otherId)["startMacro"]);
        Assert.Empty(session.Plugins.Host.SessionSettingsFor("acdream.test.unknown"));
    }

    [Fact]
    public void MutatingTheDescriptorsPluginSettingsAfterConstructionIsNotVisibleToTheHost()
    {
        using var temporary = new TemporaryDirectory();
        var credential = new HeadlessCredentialSecret("fixture", "password");
        var fixtureSettings = new Dictionary<string, string> { ["startMacro"] = "true" };
        var declared = new Dictionary<string, Dictionary<string, string>>
        {
            [FixtureId] = fixtureSettings,
        };
        using var session = new HeadlessSessionHost(
            Descriptor(
                [],
                Path.Combine(temporary.Path, "status.jsonl"),
                pluginSettings: declared),
            credential,
            new HeadlessDiagnosticWriter(new StringWriter()),
            new FixtureSessionOperations(),
            pluginRoots: [temporary.Path]);

        // Mutate both levels after the host already read them.
        fixtureSettings["startMacro"] = "false";
        declared["acdream.test.injected-after-construction"] =
            new() { ["startMacro"] = "true" };

        Assert.Equal(
            "true",
            session.Plugins.Host.SessionSettingsFor(FixtureId)["startMacro"]);
        Assert.Empty(session.Plugins.Host.SessionSettingsFor(
            "acdream.test.injected-after-construction"));
    }

    [Fact]
    public void ChatSubmitRoutesThroughTheRealChatCommandPipelineToARegisteredPluginVerb()
    {
        using var temporary = new TemporaryDirectory();
        var credential = new HeadlessCredentialSecret("fixture", "password");
        using var session = new HeadlessSessionHost(
            Descriptor([], Path.Combine(temporary.Path, "status.jsonl")),
            credential,
            new HeadlessDiagnosticWriter(new StringWriter()),
            new FixtureSessionOperations(),
            pluginRoots: [temporary.Path]);
        string? received = null;
        session.PluginCommands.Register(
            "smoketest",
            command => received = command.Arguments);

        _ = session.Start();
        bool accepted = session.Plugins.Host.Automation.Chat.Submit(
            "/smoketest hello");

        Assert.True(accepted);
        Assert.Equal("hello", received);
    }

    [Fact]
    public void ChatPostSystemMessageAppendsToTheRuntimeCommunicationTranscript()
    {
        using var temporary = new TemporaryDirectory();
        var credential = new HeadlessCredentialSecret("fixture", "password");
        using var session = new HeadlessSessionHost(
            Descriptor([], Path.Combine(temporary.Path, "status.jsonl")),
            credential,
            new HeadlessDiagnosticWriter(new StringWriter()),
            new FixtureSessionOperations(),
            pluginRoots: [temporary.Path]);
        int before = session.Runtime.Chat.Count;

        session.Plugins.Host.Automation.Chat.PostSystemMessage("hello from autostart");

        Assert.Equal(before + 1, session.Runtime.Chat.Count);
    }

    [Fact]
    public void GraphicalOnlyPluginRequestedOnHeadlessHostReportsPluginFailed()
    {
        using var temporary = new TemporaryDirectory();
        const string graphicalOnlyId = "acdream.test.graphical-only";
        string pluginDirectory = Path.Combine(temporary.Path, "graphical-only");
        Directory.CreateDirectory(pluginDirectory);
        File.WriteAllText(
            Path.Combine(pluginDirectory, "plugin.json"),
            JsonSerializer.Serialize(new
            {
                id = graphicalOnlyId,
                displayName = "Graphical only",
                version = "1.0.0",
                entryDll = "deliberately-missing.dll",
                apiVersion = 1,
                hosts = new[] { "graphical" },
            }));
        string statusPath = Path.Combine(temporary.Path, "status.jsonl");
        var credential = new HeadlessCredentialSecret("fixture", "password");
        using var session = new HeadlessSessionHost(
            Descriptor([graphicalOnlyId], statusPath),
            credential,
            new HeadlessDiagnosticWriter(new StringWriter()),
            new FixtureSessionOperations(),
            pluginRoots: [temporary.Path]);

        _ = session.Start();

        Assert.Equal(0, session.Plugins.LoadedCount);
        JsonElement failed = ReadStatuses(statusPath)
            .Single(static item =>
                item.GetProperty("e").GetString() == "pluginFailed");
        string error = failed.GetProperty("error").GetString()!;
        Assert.Contains("runs only on the graphical host", error);
    }

    [Fact]
    public void ChatCaptureMessagesReturnsTextAddedToTheRuntimeCommunicationTranscript()
    {
        using var temporary = new TemporaryDirectory();
        var credential = new HeadlessCredentialSecret("fixture", "password");
        using var session = new HeadlessSessionHost(
            Descriptor([], Path.Combine(temporary.Path, "status.jsonl")),
            credential,
            new HeadlessDiagnosticWriter(new StringWriter()),
            new FixtureSessionOperations(),
            pluginRoots: [temporary.Path]);

        session.Runtime.CommunicationOwner.AddText(
            "Archer tells you, buff",
            RetailLogTextType.Tell);

        PluginChatMessage message = Assert.Single(
            session.Plugins.Host.Automation.Chat.CaptureMessages(0));
        Assert.Equal("Archer tells you, buff", message.Text);
    }

    [Fact]
    public void CharacterAndSpellsReportRealRuntimeStateOnceTheSessionEntersTheWorld()
    {
        using var temporary = new TemporaryDirectory();
        var credential = new HeadlessCredentialSecret("fixture", "password");
        using var session = new HeadlessSessionHost(
            Descriptor([], Path.Combine(temporary.Path, "status.jsonl")),
            credential,
            new HeadlessDiagnosticWriter(new StringWriter()),
            new FixtureSessionOperations(),
            pluginRoots: [temporary.Path]);

        Assert.Equal(0u, session.Plugins.Host.Automation.Character.ObjectId);
        Assert.False(session.Plugins.Host.Automation.Spells.IsKnown(1u));

        _ = session.Start();
        session.Runtime.CharacterOwner.Spellbook.OnSpellLearned(1u);

        Assert.Equal(0x50000001u, session.Plugins.Host.Automation.Character.ObjectId);
        Assert.True(session.Plugins.Host.Automation.Spells.IsKnown(1u));
    }

    [Fact]
    public void ItemsAreAvailableOnceTheSessionEntersTheWorld()
    {
        using var temporary = new TemporaryDirectory();
        var credential = new HeadlessCredentialSecret("fixture", "password");
        using var session = new HeadlessSessionHost(
            Descriptor([], Path.Combine(temporary.Path, "status.jsonl")),
            credential,
            new HeadlessDiagnosticWriter(new StringWriter()),
            new FixtureSessionOperations(),
            pluginRoots: [temporary.Path]);

        Assert.False(session.Plugins.Host.Automation.Items.IsAvailable);

        _ = session.Start();

        Assert.True(session.Plugins.Host.Automation.Items.IsAvailable);
    }

    [Fact]
    public void DropGiveAndApplyStartOnceBoundToSendersThatAcceptTheRequest()
    {
        using var temporary = new TemporaryDirectory();
        var credential = new HeadlessCredentialSecret("fixture", "password");
        using var session = new HeadlessSessionHost(
            Descriptor([], Path.Combine(temporary.Path, "status.jsonl")),
            credential,
            new HeadlessDiagnosticWriter(new StringWriter()),
            new FixtureSessionOperations(),
            pluginRoots: [temporary.Path]);
        _ = session.Start();

        const uint item = 0x50000A01u;
        const uint target = 0x50000A02u;
        uint player = session.Plugins.Host.Automation.Character.ObjectId;
        session.Runtime.InventoryOwner.Objects.AddOrUpdate(new ClientObject
        {
            ObjectId = item,
            ContainerId = player,
            StackSize = 1,
            StackSizeMax = 1,
        });
        session.Runtime.InventoryOwner.Objects.AddOrUpdate(new ClientObject
        {
            ObjectId = target,
            ContainerId = player,
        });
        var surface = (RuntimeAutomationSurface)session.Plugins.Host.Automation;
        surface.BindItems(
            useItem: static _ => true,
            applyItem: static (_, _) => true,
            moveItem: static (_, _, _, _) => true,
            mergeItems: static (_, _, _) => true,
            dropItem: static (_, _) => true,
            giveItem: static (_, _, _) => true,
            pickupItem: static (_, _) =>
                AcDream.Runtime.Gameplay.RuntimeBackpackPlacementOutcome.Sent,
            identifyItem: static _ => true);

        Assert.Equal(
            PluginItemCommandStatus.Started,
            surface.Items.Drop(item).Status);
        Assert.Equal(
            PluginItemCommandStatus.Started,
            surface.Items.Give(item, target).Status);
        Assert.Equal(
            PluginItemCommandStatus.Started,
            surface.Items.Apply(item, target).Status);
    }

    [Fact]
    public void DropAndGiveRefuseAnAmountGreaterThanTheStackBeforeTheDelegate()
    {
        using var temporary = new TemporaryDirectory();
        var credential = new HeadlessCredentialSecret("fixture", "password");
        using var session = new HeadlessSessionHost(
            Descriptor([], Path.Combine(temporary.Path, "status.jsonl")),
            credential,
            new HeadlessDiagnosticWriter(new StringWriter()),
            new FixtureSessionOperations(),
            pluginRoots: [temporary.Path]);
        _ = session.Start();

        const uint item = 0x50000A01u;
        const uint target = 0x50000A02u;
        uint player = session.Plugins.Host.Automation.Character.ObjectId;
        session.Runtime.InventoryOwner.Objects.AddOrUpdate(new ClientObject
        {
            ObjectId = item,
            ContainerId = player,
            StackSize = 5,
            StackSizeMax = 10,
        });
        session.Runtime.InventoryOwner.Objects.AddOrUpdate(new ClientObject
        {
            ObjectId = target,
            ContainerId = player,
        });
        int dropCalls = 0;
        int giveCalls = 0;
        var surface = (RuntimeAutomationSurface)session.Plugins.Host.Automation;
        surface.BindItems(
            useItem: static _ => true,
            applyItem: static (_, _) => true,
            moveItem: static (_, _, _, _) => true,
            mergeItems: static (_, _, _) => true,
            dropItem: (_, _) => { dropCalls++; return true; },
            giveItem: (_, _, _) => { giveCalls++; return true; },
            pickupItem: static (_, _) =>
                AcDream.Runtime.Gameplay.RuntimeBackpackPlacementOutcome.Sent,
            identifyItem: static _ => true);

        Assert.Equal(
            PluginItemCommandStatus.Refused,
            surface.Items.Drop(item, amount: 6u).Status);
        Assert.Equal(
            PluginItemCommandStatus.Refused,
            surface.Items.Give(item, target, amount: 6u).Status);
        Assert.Equal(0, dropCalls);
        Assert.Equal(0, giveCalls);
    }

    [Fact]
    public void EquipmentIsAvailableOnceTheSessionEntersTheWorld()
    {
        using var temporary = new TemporaryDirectory();
        var credential = new HeadlessCredentialSecret("fixture", "password");
        using var session = new HeadlessSessionHost(
            Descriptor([], Path.Combine(temporary.Path, "status.jsonl")),
            credential,
            new HeadlessDiagnosticWriter(new StringWriter()),
            new FixtureSessionOperations(),
            pluginRoots: [temporary.Path]);

        Assert.False(session.Plugins.Host.Automation.Equipment.IsAvailable);

        _ = session.Start();

        Assert.True(session.Plugins.Host.Automation.Equipment.IsAvailable);
    }

    /// <summary>
    /// A plugin is told where another creature's BODY is, because that body
    /// is carried between the server's updates and the server's last word
    /// about the creature is already stale by the time a plugin reads it.
    /// A creature with no body yet -- which is every creature in a session
    /// with no content lease -- has only that last word, and gets it.
    /// </summary>
    [Fact]
    public void ACreaturesPositionIsItsBodyAndFallsBackToTheServersLastWord()
    {
        const uint remote = 0x50000099u;
        const uint cell = 0x01010100u;
        using var temporary = new TemporaryDirectory();
        var credential = new HeadlessCredentialSecret("fixture", "password");
        using var session = new HeadlessSessionHost(
            Descriptor([], Path.Combine(temporary.Path, "status.jsonl")),
            credential,
            new HeadlessDiagnosticWriter(new StringWriter()),
            new FixtureSessionOperations(),
            pluginRoots: [temporary.Path]);
        _ = session.Start();

        RuntimeEntityRecord record = session.Runtime.EntityObjects
            .RegisterEntity(Spawn(remote, 1f, cell))
            .Canonical!;

        // No body yet: the server's last word is all there is.
        Assert.True(session.Plugins.Host.Automation.Objects.TryGet(
            remote,
            out PluginWorldObject bodyless));
        PluginNavigationPosition spawned =
            RuntimeAutomationSurface.ProjectNavigationPosition(
                new Position(
                    cell,
                    new Vector3(1f, 10f, 5f),
                    Quaternion.Identity));
        Assert.Equal(
            0d,
            bodyless.Position.HorizontalDistanceMeters(spawned),
            3);

        // Given a body, and the body carried fifty metres east of where the
        // creature spawned, that is where a plugin is told it is.
        var body = new PhysicsBody { Position = new Vector3(51f, 10f, 5f) };
        body.SnapToCell(cell, body.Position, body.Position);
        session.Runtime.EntityObjects.Entities.SetPhysicsBody(record, body);

        Assert.True(session.Plugins.Host.Automation.Objects.TryGet(
            remote,
            out PluginWorldObject value));
        PluginNavigationPosition carried =
            RuntimeAutomationSurface.ProjectNavigationPosition(
                new Position(
                    cell,
                    new Vector3(51f, 10f, 5f),
                    Quaternion.Identity));
        Assert.Equal(0d, value.Position.HorizontalDistanceMeters(carried), 3);
        Assert.True(
            value.Position.HorizontalDistanceMeters(spawned) > 40d,
            "The creature's position is still where it spawned.");
    }

    [Trait("Lane", "InstalledDat")]
    [Fact]
    public void SpellComponentsResolveThroughTheSessionsContentLeaseMagicCatalog()
    {
        string? datDir = InstalledDatTestPath.Resolve();
        if (datDir is null)
        {
            Assert.Fail(
                "Lane=InstalledDat requires an installed retail DAT directory; see docs/release-gate.md.");
            return;
        }

        using var dats = new DatCollection(datDir, DatAccessType.Read);
        using var adapter = new DatCollectionAdapter(dats);
        MagicCatalog catalog = MagicCatalog.Load(adapter);
        SpellComponentDescriptor known = catalog.Components.Values
            .First(static component => component.SpellComponentId != 0u);

        using var temporary = new TemporaryDirectory();
        var credential = new HeadlessCredentialSecret("fixture", "password");
        using var contentOwner = new HeadlessProcessContentOwner(
            ContentDescriptor(),
            _ => { },
            new InstalledMagicContentFactory(catalog));
        using HeadlessProcessContentOwner.HeadlessProcessContentLease lease =
            contentOwner.AcquireLease("headless-session");
        using var session = new HeadlessSessionHost(
            Descriptor([], Path.Combine(temporary.Path, "status.jsonl")),
            credential,
            new HeadlessDiagnosticWriter(new StringWriter()),
            new FixtureSessionOperations(),
            contentLease: lease,
            pluginRoots: [temporary.Path]);

        Assert.True(session.Plugins.Host.Automation.Spells.TryGetComponent(
            known.SpellComponentId,
            out PluginSpellComponentInfo info));
        Assert.Equal(known.Name, info.Name);
    }

    [Fact]
    public void SpellComponentsAreUnavailableWithoutAContentLease()
    {
        using var temporary = new TemporaryDirectory();
        var credential = new HeadlessCredentialSecret("fixture", "password");
        using var session = new HeadlessSessionHost(
            Descriptor([], Path.Combine(temporary.Path, "status.jsonl")),
            credential,
            new HeadlessDiagnosticWriter(new StringWriter()),
            new FixtureSessionOperations(),
            pluginRoots: [temporary.Path]);

        Assert.False(session.Plugins.Host.Automation.Spells.TryGetComponent(
            4200u,
            out _));
    }

    [Fact]
    public void PluginStorageWrittenThroughAScopedHostReadsBackThroughAFreshSessionOverTheSameDirectory()
    {
        using var temporary = new TemporaryDirectory();
        using var storageRoot = new TemporaryDirectory();
        using (var session = new HeadlessSessionHost(
            Descriptor([], Path.Combine(temporary.Path, "status.jsonl")),
            new HeadlessCredentialSecret("fixture", "password"),
            new HeadlessDiagnosticWriter(new StringWriter()),
            new FixtureSessionOperations(),
            pluginRoots: [temporary.Path],
            storage: new FilePluginStorage(storageRoot.Path)))
        {
            var scope = new ScopedPluginHost(
                session.Plugins.Host,
                "acdream.test.storage",
                "Storage fixture");
            scope.Storage.WriteText("settings.json", "hello");
        }

        using var fresh = new HeadlessSessionHost(
            Descriptor([], Path.Combine(temporary.Path, "fresh-status.jsonl")),
            new HeadlessCredentialSecret("fixture", "password"),
            new HeadlessDiagnosticWriter(new StringWriter()),
            new FixtureSessionOperations(),
            pluginRoots: [temporary.Path],
            storage: new FilePluginStorage(storageRoot.Path));
        var freshScope = new ScopedPluginHost(
            fresh.Plugins.Host,
            "acdream.test.storage",
            "Storage fixture");

        Assert.Equal("hello", freshScope.Storage.ReadText("settings.json"));
    }

    private static HeadlessContentDescriptor ContentDescriptor() => new()
    {
        DatDirectory = "fixture-dats",
        PreparedAssetPath = "fixture.pak",
    };

    private sealed class InstalledMagicContentFactory(MagicCatalog magic)
        : IHeadlessProcessContentFactory
    {
        public HeadlessOpenedProcessContent Open(
            HeadlessContentDescriptor descriptor,
            Action<string> diagnostic) =>
            new(
                DispatchProxy.Create<IDatReaderWriter, TestResourceProxy>(),
                DispatchProxy.Create<ITestPreparedSource, TestResourceProxy>(),
                magic,
                ImmutableArray.CreateRange(new float[256]));
    }

    private static HeadlessSessionDescriptor Descriptor(
        List<string> plugins,
        string statusPath,
        IReadOnlyList<string>? loginCommands = null,
        int loginCommandDelayMs = 500,
        Dictionary<string, Dictionary<string, string>>? pluginSettings = null) => new()
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
            PluginSettings = pluginSettings,
        };

    private static WorldSession.EntitySpawn Spawn(
        uint guid,
        float x,
        uint cell = 0x01010001u) => new(
        guid,
        new CreateObject.ServerPosition(
            cell,
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
        LiveStatusFile.ReadAllLines(path)
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

        public WorldSession CreateSession(IPEndPoint endpoint)
        {
            // This scripted server never prompts the client to complete its
            // login, so the session is taken as already past it.
            var session = new WorldSession(endpoint).TakingItsSends();
            session.AssumeLoginCompleteForTesting();
            return session;
        }

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
