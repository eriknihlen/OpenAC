using System.Text.Json;
using AcDream.Headless.Configuration;
using AcDream.Headless.Credentials;
using AcDream.Headless.Diagnostics;
using AcDream.Headless.Hosting;
using AcDream.Headless.Plugins;
using AcDream.Runtime;

namespace AcDream.Headless.Tests;

[Trait("Lane", "Live")]
public sealed class MossTankAutostartLiveSmokeTests
{
    private const string SmokeMetaContent = """
        STATE: {Default} ~~ {
        	IF:	Always
        		DO:	Chat {S2.9 autostart smoke test reached Default state.}
        ~~ }
        """;

    [Fact]
    public void AutostartSmokeTestConnectsLoadsMossTankAndRunsTheDeclaredMacro()
    {
        if (Environment.GetEnvironmentVariable("ACDREAM_LIVE") != "1")
        {
            Assert.Fail(
                "Lane=Live requires ACDREAM_LIVE=1 and a reachable configured server.");
        }

        string host = Environment.GetEnvironmentVariable("ACDREAM_TEST_HOST") ?? "127.0.0.1";
        string portText = Environment.GetEnvironmentVariable("ACDREAM_TEST_PORT") ?? "9000";
        string? user = Environment.GetEnvironmentVariable("ACDREAM_TEST_USER");
        string? pass = Environment.GetEnvironmentVariable("ACDREAM_TEST_PASS");
        Assert.NotNull(user);
        Assert.NotNull(pass);
        Assert.NotEmpty(user!);
        Assert.NotEmpty(pass!);

        using var temporary = new TemporaryDirectory();
        string pluginRoot = InstallRealMossTankPlugin(temporary.Path);
        string vtankRoot = Path.Combine(temporary.Path, "vtank");
        Directory.CreateDirectory(Path.Combine(vtankRoot, "metas"));
        File.WriteAllText(
            Path.Combine(vtankRoot, "metas", "s29-smoke.af"),
            SmokeMetaContent);
        string statusPath = Path.Combine(temporary.Path, "status.jsonl");

        var descriptor = new HeadlessSessionDescriptor
        {
            Id = "s29-smoke",
            Endpoint = new HeadlessEndpointDescriptor
            {
                Host = host,
                Port = int.Parse(portText),
            },
            Account = user!,
            Character = new HeadlessCharacterSelector { Name = "+Acdream" },
            Policy = new HeadlessBotPolicyDescriptor { Id = "idle" },
            Credential = new HeadlessCredentialReference
            {
                Provider = HeadlessCredentialProviderKind.Environment,
                Reference = "ACDREAM_TEST_PASS",
            },
            Plugins = ["acdream.mosstank"],
            PluginSettings = new Dictionary<string, Dictionary<string, string>>
            {
                ["acdream.mosstank"] = new()
                {
                    ["metaProfile"] = "s29-smoke",
                    ["enableMeta"] = "true",
                    ["startMacro"] = "true",
                },
            },
            StatusFile = statusPath,
        };
        var credential = new HeadlessCredentialSecret("live-smoke", pass!);
        var diagnosticsOutput = new StringWriter();
        var chatEntries = new List<string>();

        using var session = new HeadlessSessionHost(
            descriptor,
            credential,
            new HeadlessDiagnosticWriter(diagnosticsOutput),
            sessionOperations: null, // real network
            vtankProfiles: new FilePluginStorage(vtankRoot),
            pluginRoots: [temporary.Path]);
        using IDisposable chatSubscription = session.Runtime.Subscribe(
            new ChatCapture(chatEntries));

        RuntimeSessionStartResult startResult = session.Start();

        var deadline = DateTime.UtcNow + TimeSpan.FromSeconds(20);
        bool enteredWorld = false;
        while (DateTime.UtcNow < deadline)
        {
            session.Tick(0.1d);
            Thread.Sleep(100);
            if (EventNames(ReadStatuses(statusPath)).Contains("enteredWorld"))
            {
                enteredWorld = true;
                break;
            }
        }
        Assert.True(
            enteredWorld,
            "Session did not reach enteredWorld within 20s. "
                + $"Diagnostics: {diagnosticsOutput}");

        var settleDeadline = DateTime.UtcNow + TimeSpan.FromSeconds(15);
        while (DateTime.UtcNow < settleDeadline
            && !chatEntries.Any(static text => text.Contains(
                "S2.9 autostart smoke test reached Default state.",
                StringComparison.Ordinal)))
        {
            session.Tick(0.1d);
            Thread.Sleep(100);
        }

        string[] names = EventNames(ReadStatuses(statusPath));
        Assert.Contains("started", names);
        Assert.Contains("pluginLoaded", names);
        Assert.Contains("connected", names);
        Assert.Contains("characterList", names);
        Assert.Contains("enteredWorld", names);
        JsonElement pluginLoaded = ReadStatuses(statusPath)
            .First(static item => item.GetProperty("e").GetString() == "pluginLoaded");
        Assert.Equal("acdream.mosstank", pluginLoaded.GetProperty("plugin").GetString());
        Assert.True(session.Plugins.LoadedCount >= 1);
        Assert.True(session.Plugins.Host.Automation.IsAvailable);

        Assert.NotEmpty(chatEntries);
        Assert.Contains(
            chatEntries,
            text => text.Contains(
                "S2.9 autostart smoke test reached Default state.",
                StringComparison.Ordinal));

        // Graceful logout (Dispose runs Stop() first) then exit-0 proof via
        // the K2 status stream's own terminal event.
        session.Dispose();
        JsonElement exited = ReadStatuses(statusPath)
            .First(static item => item.GetProperty("e").GetString() == "exited");
        Assert.Equal(0, exited.GetProperty("code").GetInt32());
        Assert.Equal("graceful", exited.GetProperty("reason").GetString());
    }

    private sealed class ChatCapture(List<string> sink) : IRuntimeEventObserver
    {
        public void OnLifecycle(in RuntimeLifecycleDelta delta) { }
        public void OnCommand(in RuntimeCommandDelta delta) { }
        public void OnEntity(in RuntimeEntityDelta delta) { }
        public void OnInventory(in RuntimeInventoryDelta delta) { }
        public void OnChat(in RuntimeChatDelta delta) => sink.Add(delta.Entry.Text);
        public void OnMovement(in RuntimeMovementDelta delta) { }
        public void OnPortal(in RuntimePortalDelta delta) { }
        public void OnCombat(in RuntimeCombatDelta delta) { }
    }

    private static string InstallRealMossTankPlugin(string root)
    {
        const string fileName = "AcDream.Plugins.MossTank.dll";
        string configuration = new DirectoryInfo(AppContext.BaseDirectory)
            .Parent!.Name;
        string repoRoot = FindRepoRoot(AppContext.BaseDirectory);
        string source = Path.Combine(
            repoRoot,
            "src",
            "AcDream.Plugins.MossTank",
            "bin",
            configuration,
            "net10.0",
            fileName);
        Assert.True(File.Exists(source), $"MossTank plugin DLL not found: {source}");

        string pluginDirectory = Path.Combine(root, "mosstank");
        Directory.CreateDirectory(pluginDirectory);
        File.Copy(source, Path.Combine(pluginDirectory, fileName));
        File.WriteAllText(
            Path.Combine(pluginDirectory, "plugin.json"),
            JsonSerializer.Serialize(new
            {
                id = "acdream.mosstank",
                displayName = "MossTank",
                version = "0.1.0",
                entryDll = fileName,
                apiVersion = 1,
            }));
        return pluginDirectory;
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

    private static JsonElement[] ReadStatuses(string path) =>
        File.Exists(path)
            ? File.ReadAllLines(path)
                .Where(static line => line.Length > 0)
                .Select(static line => JsonDocument.Parse(line).RootElement.Clone())
                .ToArray()
            : [];

    private static string[] EventNames(IEnumerable<JsonElement> events) =>
        events.Select(static item => item.GetProperty("e").GetString()!).ToArray();

    private sealed class TemporaryDirectory : IDisposable
    {
        internal TemporaryDirectory()
        {
            Path = System.IO.Path.Combine(
                System.IO.Path.GetTempPath(),
                $"acdream-headless-s29-smoke-{Guid.NewGuid():N}");
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
