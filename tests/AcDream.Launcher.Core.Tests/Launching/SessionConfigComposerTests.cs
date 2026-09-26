using System.Text.Json.Nodes;
using AcDream.Launcher.Core.Launching;
using AcDream.Launcher.Core.Plugins;
using AcDream.Launcher.Core.Profiles;
using AcDream.Platform;

namespace AcDream.Launcher.Core.Tests.Launching;

public sealed class SessionConfigComposerTests
{
    private static readonly ApplicationPathSet Paths = new(
        ConfigDirectory: "/cfg/acdream",
        DataDirectory: "/data/acdream",
        CacheDirectory: "/cache/acdream");

    private static readonly LauncherInstallRecord Install = new(
        DatDirectory: "/dats",
        PreparedAssetPath: "/data/acdream/pak/acdream.pak");

    private static ServerProfile Server() =>
        new() { Name = "Local ACE", Host = "127.0.0.1", Port = 9000 };

    private static AccountProfile Account() =>
        new()
        {
            Account = "testaccount",
            Password = "S3cretPassw0rd!",
            LoginCommands = ["/tell someone, hi"],
        };

    private static CharacterProfile Character(LaunchMode mode, string? id = "0x5000000A") =>
        new()
        {
            Name = "+Acdream",
            Id = id,
            LaunchMode = mode,
            Plugins = ["ExamplePlugin"],
        };

    [Fact]
    public void PendingClientCompatibilityCannotComposePlayOrProbeSession()
    {
        LauncherInstallRecord pending = Install with
        {
            RequiresClientCompatibilityConfirmation = true,
        };

        InvalidOperationException play = Assert.Throws<InvalidOperationException>(() =>
            SessionConfigComposer.Compose(
                Server(),
                Account(),
                Character(LaunchMode.Gui),
                pending,
                Paths,
                "pending-play"));
        InvalidOperationException probe = Assert.Throws<InvalidOperationException>(() =>
            SessionConfigComposer.ComposeProbe(
                Server(),
                Account(),
                pending,
                Paths,
                "pending-probe"));

        Assert.Contains("matching client", play.Message, StringComparison.OrdinalIgnoreCase);
        Assert.Contains("matching client", probe.Message, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void GuiModeIncludesCharacterSelectorAndOmitsPolicy()
    {
        ComposedSessionConfig composed = SessionConfigComposer.Compose(
            Server(),
            Account(),
            Character(LaunchMode.Gui),
            Install,
            Paths,
            sessionId: "session-gui");

        JsonObject session = SingleSession(composed);

        AssertKeys(
            session,
            "id", "endpoint", "account", "character", "credential",
            "plugins", "loginCommands", "statusFile");

        Assert.Equal("session-gui", (string?)session["id"]);
        Assert.Equal("testaccount", (string?)session["account"]);
        Assert.Equal(0x5000000Au, (uint?)session["character"]!["id"]);
        Assert.Null(session["character"]!["name"]);
        Assert.Null(session["character"]!["index"]);
        Assert.Equal("standardInput", (string?)session["credential"]!["provider"]);
        Assert.Equal("session", (string?)session["credential"]!["reference"]);
        Assert.Equal(
            new[] { "ExamplePlugin" },
            session["plugins"]!.AsArray().Select(n => (string?)n));
        Assert.Equal(
            new[] { "/tell someone, hi" },
            session["loginCommands"]!.AsArray().Select(n => (string?)n));
        Assert.Equal(
            Path.Combine(Path.Combine(Paths.DataDirectory, "logs", "launcher"), "session-gui", "status.jsonl"),
            (string?)session["statusFile"]);
    }

    [Fact]
    public void GuiSelectModeOmitsCharacterFieldEntirely()
    {
        ComposedSessionConfig composed = SessionConfigComposer.Compose(
            Server(),
            Account(),
            Character(LaunchMode.GuiSelect),
            Install,
            Paths,
            sessionId: "session-guiselect");

        JsonObject session = SingleSession(composed);

        AssertKeys(
            session,
            "id", "endpoint", "account", "credential",
            "plugins", "loginCommands", "statusFile");
        Assert.False(session.ContainsKey("character"));
        Assert.False(session.ContainsKey("policy"));
    }

    [Fact]
    public void HeadlessModeIncludesCharacterAndIdlePolicy()
    {
        ComposedSessionConfig composed = SessionConfigComposer.Compose(
            Server(),
            Account(),
            Character(LaunchMode.Headless),
            Install,
            Paths,
            sessionId: "session-headless",
            loginCommandDelayMs: 750);

        JsonObject session = SingleSession(composed);

        AssertKeys(
            session,
            "id", "endpoint", "account", "character", "policy", "credential",
            "plugins", "loginCommands", "loginCommandDelayMs", "statusFile");
        Assert.Equal(0x5000000Au, (uint?)session["character"]!["id"]);
        Assert.Equal("idle", (string?)session["policy"]!["id"]);
        Assert.Equal(750, (int?)session["loginCommandDelayMs"]);
    }

    [Fact]
    public void GuiModeFallsBackToNameSelectorWhenIdIsMissing()
    {
        ComposedSessionConfig composed = SessionConfigComposer.Compose(
            Server(),
            Account(),
            Character(LaunchMode.Gui, id: null),
            Install,
            Paths,
            sessionId: "session-gui-name");

        JsonObject session = SingleSession(composed);
        Assert.Null(session["character"]!["id"]);
        Assert.Equal("+Acdream", (string?)session["character"]!["name"]);
    }

    [Fact]
    public void GuiModeFallsBackToNameSelectorWhenIdIsAHandTypedDecimalWithoutThe0xPrefix()
    {
        ComposedSessionConfig composed = SessionConfigComposer.Compose(
            Server(),
            Account(),
            Character(LaunchMode.Gui, id: "12345678"),
            Install,
            Paths,
            sessionId: "session-gui-decimal-id");

        JsonObject session = SingleSession(composed);
        Assert.Null(session["character"]!["id"]);
        Assert.Equal("+Acdream", (string?)session["character"]!["name"]);
    }

    [Fact]
    public void GuiModeFallsBackToNameSelectorWhenTheParsedIdIsZero()
    {
        // Both host loaders reject `id: 0` outright,
        // so a parsed-but-zero id is not a usable selector either.
        ComposedSessionConfig composed = SessionConfigComposer.Compose(
            Server(),
            Account(),
            Character(LaunchMode.Gui, id: "0x00000000"),
            Install,
            Paths,
            sessionId: "session-gui-zero-id");

        JsonObject session = SingleSession(composed);
        Assert.Null(session["character"]!["id"]);
        Assert.Equal("+Acdream", (string?)session["character"]!["name"]);
    }

    [Fact]
    public void UnconfiguredPluginsExplicitlyLoadNone()
    {
        // A blank list means none, not "every plugin loads".
        CharacterProfile character = Character(LaunchMode.Gui);
        character.Plugins = [];
        AccountProfile account = Account();
        account.LoginCommands = [];

        ComposedSessionConfig composed = SessionConfigComposer.Compose(
            Server(),
            account,
            character,
            Install,
            Paths,
            sessionId: "session-empty-lists");

        JsonObject session = SingleSession(composed);
        Assert.True(session.ContainsKey("plugins"));
        Assert.Empty(session["plugins"]!.AsArray());
        Assert.False(session.ContainsKey("loginCommands"));
        Assert.Empty(composed.PluginStatusLines);
    }

    [Fact]
    public void ThePluginIdNoneEmitsAnExplicitLoadNoneAllowList()
    {
        CharacterProfile character = Character(LaunchMode.Gui);
        character.Plugins = ["none"];

        ComposedSessionConfig composed = SessionConfigComposer.Compose(
            Server(),
            Account(),
            character,
            Install,
            Paths,
            sessionId: "session-no-plugins");

        JsonObject session = SingleSession(composed);
        Assert.True(session.ContainsKey("plugins"));
        Assert.Empty(session["plugins"]!.AsArray());
    }

    [Fact]
    public void ConfiguredPluginIdsArePassedThroughUnchanged()
    {
        CharacterProfile character = Character(LaunchMode.Gui);
        character.Plugins = ["edwards.tank"];

        ComposedSessionConfig composed = SessionConfigComposer.Compose(
            Server(),
            Account(),
            character,
            Install,
            Paths,
            sessionId: "session-one-plugin");

        JsonObject session = SingleSession(composed);
        Assert.Equal(
            ["edwards.tank"],
            session["plugins"]!.AsArray().Select(node => (string?)node));
    }

    [Fact]
    public void ABlockedConfiguredPluginIsFilteredWithOneStatusLine()
    {
        CharacterProfile character = Character(LaunchMode.Gui);
        character.Plugins = ["edwards.hello", "edwards.tank"];
        PluginCatalog catalog = PluginCatalog.Parse("""
            {
              "schemaVersion": 1,
              "plugins": [
                { "id": "edwards.hello", "name": "Hello", "author": "Shane Edwards",
                  "description": "Says hello.", "repo": "shaneedwards/openac-plugin-hello" }
              ],
              "blocked": [
                { "id": "edwards.hello", "versions": ["*"], "reason": "test" }
              ]
            }
            """);

        ComposedSessionConfig composed = SessionConfigComposer.Compose(
            Server(),
            Account(),
            character,
            Install,
            Paths,
            sessionId: "session-blocked-plugin",
            catalog: catalog);

        JsonObject session = SingleSession(composed);
        Assert.Equal(
            ["edwards.tank"],
            session["plugins"]!.AsArray().Select(node => (string?)node));
        Assert.Equal(
            ["Plugin 'edwards.hello' is blocked and was not loaded."],
            composed.PluginStatusLines);
    }

    [Theory]
    [InlineData("0.1.0", true)]
    [InlineData("0.0.9", false)]
    public void ABlockOnOneVersionFiltersOnlyThatInstalledVersion(string blockedVersion, bool filtered)
    {
        using var plugins = new TempPluginRoot();
        plugins.WriteFolder("edwards.hello", writeEntryDll: true);   // installs version 0.1.0
        CharacterProfile character = Character(LaunchMode.Gui);
        character.Plugins = ["edwards.hello"];
        PluginCatalog catalog = PluginCatalog.Parse($$"""
            {
              "schemaVersion": 1,
              "plugins": [
                { "id": "edwards.hello", "name": "Hello", "author": "Shane Edwards",
                  "description": "Says hello.", "repo": "shaneedwards/openac-plugin-hello" }
              ],
              "blocked": [
                { "id": "edwards.hello", "versions": ["{{blockedVersion}}"], "reason": "test" }
              ]
            }
            """);

        ComposedSessionConfig composed = SessionConfigComposer.Compose(
            Server(),
            Account(),
            character,
            Install,
            plugins.Paths,
            sessionId: "session-version-block",
            catalog: catalog);

        JsonObject session = SingleSession(composed);
        Assert.Equal(
            filtered ? [] : ["edwards.hello"],
            session["plugins"]!.AsArray().Select(node => (string?)node));
        Assert.Equal(filtered ? 1 : 0, composed.PluginStatusLines.Count);
    }

    [Fact]
    public void WithNoCatalogAndNoCacheConfiguredPluginsAreNotFiltered()
    {
        CharacterProfile character = Character(LaunchMode.Gui);
        character.Plugins = ["edwards.hello"];

        ComposedSessionConfig composed = SessionConfigComposer.Compose(
            Server(),
            Account(),
            character,
            Install,
            Paths,
            sessionId: "session-no-catalog-no-cache");

        JsonObject session = SingleSession(composed);
        Assert.Equal(
            ["edwards.hello"],
            session["plugins"]!.AsArray().Select(node => (string?)node));
        Assert.Empty(composed.PluginStatusLines);
    }

    [Fact]
    public void WithNoCatalogTheCachedListStillFiltersBlockedIds()
    {
        string cacheDirectory = Path.Combine(
            Path.GetTempPath(),
            "acdream-session-config-plugins-cache-tests",
            Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(cacheDirectory);
        try
        {
            File.WriteAllText(Path.Combine(cacheDirectory, "plugins.json"), """
                {
                  "schemaVersion": 1,
                  "plugins": [
                    { "id": "edwards.hello", "name": "Hello", "author": "Shane Edwards",
                      "description": "Says hello.", "repo": "shaneedwards/openac-plugin-hello" }
                  ],
                  "blocked": [
                    { "id": "edwards.hello", "versions": ["*"], "reason": "test" }
                  ]
                }
                """);
            var paths = Paths with { CacheDirectory = cacheDirectory };
            CharacterProfile character = Character(LaunchMode.Gui);
            character.Plugins = ["edwards.hello"];

            ComposedSessionConfig composed = SessionConfigComposer.Compose(
                Server(),
                Account(),
                character,
                Install,
                paths,
                sessionId: "session-cached-catalog");

            JsonObject session = SingleSession(composed);
            Assert.Empty(session["plugins"]!.AsArray());
            Assert.Equal(
                ["Plugin 'edwards.hello' is blocked and was not loaded."],
                composed.PluginStatusLines);
        }
        finally
        {
            Directory.Delete(cacheDirectory, recursive: true);
        }
    }

    [Fact]
    public void ARefusedDirectInstallIsExcludedWithAStatusLineAndNoCatalogOrCache()
    {
        using var plugins = new TempPluginRoot();
        plugins.WriteFolder("edwards.broken", writeEntryDll: false);
        CharacterProfile character = Character(LaunchMode.Gui);
        character.Plugins = ["edwards.broken"];

        ComposedSessionConfig composed = SessionConfigComposer.Compose(
            Server(),
            Account(),
            character,
            Install,
            plugins.Paths,
            sessionId: "session-refused-direct");

        JsonObject session = SingleSession(composed);
        Assert.Empty(session["plugins"]!.AsArray());
        Assert.Equal(
            ["Plugin 'edwards.broken' failed its install checks and was not loaded."],
            composed.PluginStatusLines);
    }

    [Fact]
    public void ADuplicatedInstallIsExcludedWithAStatusLineAndNoCatalogOrCache()
    {
        using var plugins = new TempPluginRoot();
        plugins.WriteFolder("edwards.hello", writeEntryDll: true);
        plugins.AddRecord("edwards.hello");
        plugins.WriteFolder("aaa-stray", writeEntryDll: true, id: "edwards.hello");
        CharacterProfile character = Character(LaunchMode.Gui);
        character.Plugins = ["edwards.hello"];

        ComposedSessionConfig composed = SessionConfigComposer.Compose(
            Server(),
            Account(),
            character,
            Install,
            plugins.Paths,
            sessionId: "session-duplicated-install");

        JsonObject session = SingleSession(composed);
        Assert.Empty(session["plugins"]!.AsArray());
        Assert.Equal(
            ["Plugin 'edwards.hello' has more than one copy installed and was not loaded."],
            composed.PluginStatusLines);
    }

    /// <summary>A real <see cref="ApplicationPathSet"/> under a fresh temp directory, so a Direct
    /// install's folder actually exists for <see cref="DirectInstallCheck"/> to walk, and no stray
    /// <c>plugins.json</c> cache from another test can leak in.</summary>
    private sealed class TempPluginRoot : IDisposable
    {
        private readonly string _root = Path.Combine(
            Path.GetTempPath(),
            "acdream-session-config-plugins-tests",
            Guid.NewGuid().ToString("N"));

        public TempPluginRoot()
        {
            Paths = new ApplicationPathSet(
                Path.Combine(_root, "config"),
                Path.Combine(_root, "data"),
                Path.Combine(_root, "cache"));
        }

        public ApplicationPathSet Paths { get; }

        public void WriteFolder(string folderName, bool writeEntryDll, string? id = null)
        {
            string directory = Path.Combine(Paths.PluginsDirectory, folderName);
            Directory.CreateDirectory(directory);
            string pluginId = id ?? folderName;
            File.WriteAllText(Path.Combine(directory, "plugin.json"), $$"""
                {
                  "id": "{{pluginId}}",
                  "displayName": "{{pluginId}}",
                  "version": "0.1.0",
                  "entryDll": "{{pluginId}}.dll",
                  "apiVersion": 1,
                  "minHostVersion": "0.1.0",
                  "hosts": ["headless", "graphical"]
                }
                """);
            if (writeEntryDll)
            {
                File.WriteAllBytes(Path.Combine(directory, $"{pluginId}.dll"), []);
            }
        }

        public void AddRecord(string id)
        {
            InstalledPluginRecordStore store = InstalledPluginRecordStore.ForApplicationPaths(Paths);
            store.Load();
            store.Records.Add(new InstalledPluginRecord(
                id,
                "shaneedwards/openac-plugin-hello",
                PluginInstallSource.Listed,
                "0.1.0",
                "v0.1.0",
                new string('a', 64),
                DateTimeOffset.UtcNow,
                null));
            store.Save();
        }

        public void Dispose()
        {
            if (Directory.Exists(_root))
            {
                Directory.Delete(_root, recursive: true);
            }
        }
    }

    [Fact]
    public void ProcessContentCarriesInstallRecordAndPathsIsOmittedByDefault()
    {
        ComposedSessionConfig composed = SessionConfigComposer.Compose(
            Server(),
            Account(),
            Character(LaunchMode.Gui),
            Install,
            Paths,
            sessionId: "session-content");

        JsonObject root = ParseRoot(composed);
        Assert.Equal(1, (int?)root["version"]);
        JsonObject process = root["process"]!.AsObject();

        AssertKeys(process, "content");
        Assert.False(process.ContainsKey("paths"));

        JsonObject content = process["content"]!.AsObject();
        AssertKeys(content, "datDirectory", "preparedAssetPath");
        Assert.Equal(Install.DatDirectory, (string?)content["datDirectory"]);
        Assert.Equal(Install.PreparedAssetPath, (string?)content["preparedAssetPath"]);
    }

    [Fact]
    public void NormalPlaySessionsOmitTheModeFieldEntirely()
    {
        foreach (LaunchMode mode in new[] { LaunchMode.Gui, LaunchMode.GuiSelect, LaunchMode.Headless })
        {
            ComposedSessionConfig composed = SessionConfigComposer.Compose(
                Server(),
                Account(),
                Character(mode),
                Install,
                Paths,
                sessionId: $"session-mode-omit-{mode}");

            JsonObject session = SingleSession(composed);
            Assert.False(session.ContainsKey("mode"));
        }
    }

    [Fact]
    public void ProbeModeSetsModeAndCarriesExplicitLoadNonePluginAllowList()
    {
        ComposedSessionConfig composed = SessionConfigComposer.ComposeProbe(
            Server(),
            Account(),
            Install,
            Paths,
            sessionId: "session-probe");

        JsonObject session = SingleSession(composed);

        AssertKeys(
            session,
            "id", "mode", "endpoint", "account", "credential", "plugins", "statusFile");

        Assert.Equal("session-probe", (string?)session["id"]);
        Assert.Equal("probe", (string?)session["mode"]);
        Assert.Equal("127.0.0.1", (string?)session["endpoint"]!["host"]);
        Assert.Equal(9000, (int?)session["endpoint"]!["port"]);
        Assert.Equal("testaccount", (string?)session["account"]);
        Assert.Equal("standardInput", (string?)session["credential"]!["provider"]);
        Assert.False(session.ContainsKey("character"));
        Assert.False(session.ContainsKey("policy"));
        Assert.Empty(session["plugins"]!.AsArray());
        Assert.False(session.ContainsKey("loginCommands"));
        Assert.False(session.ContainsKey("loginCommandDelayMs"));
        Assert.Equal(
            Path.Combine(
                Path.Combine(Paths.DataDirectory, "logs", "launcher"), "session-probe", "status.jsonl"),
            (string?)session["statusFile"]);
    }

    [Fact]
    public void ProbeModeDocumentNeverContainsThePassword()
    {
        AccountProfile account = Account();

        ComposedSessionConfig composed = SessionConfigComposer.ComposeProbe(
            Server(),
            account,
            Install,
            Paths,
            sessionId: "session-probe-pw");

        string json = SessionConfigComposer.Serialize(composed.Document);
        Assert.DoesNotContain(account.Password, json, StringComparison.Ordinal);
    }

    [Fact]
    public void ProbeModeProcessSettingsMatchNormalComposition()
    {
        ComposedSessionConfig composed = SessionConfigComposer.ComposeProbe(
            Server(),
            Account(),
            Install,
            Paths,
            sessionId: "session-probe-content");

        JsonObject root = ParseRoot(composed);
        JsonObject process = root["process"]!.AsObject();
        AssertKeys(process, "content");

        JsonObject content = process["content"]!.AsObject();
        Assert.Equal(Install.DatDirectory, (string?)content["datDirectory"]);
        Assert.Equal(Install.PreparedAssetPath, (string?)content["preparedAssetPath"]);
    }

    [Fact]
    public void ComposedDocumentNeverContainsThePassword()
    {
        AccountProfile account = Account();

        foreach (LaunchMode mode in new[] { LaunchMode.Gui, LaunchMode.GuiSelect, LaunchMode.Headless })
        {
            ComposedSessionConfig composed = SessionConfigComposer.Compose(
                Server(),
                account,
                Character(mode),
                Install,
                Paths,
                sessionId: $"session-{mode}");

            string json = SessionConfigComposer.Serialize(composed.Document);
            Assert.DoesNotContain(account.Password, json, StringComparison.Ordinal);
        }
    }

    [Fact]
    public void ComposeAndWriteWritesSessionJsonUnderTheExpectedPath()
    {
        string root = Path.Combine(
            Path.GetTempPath(),
            "acdream-launcher-composer-tests",
            Guid.NewGuid().ToString("N"));
        try
        {
            var paths = new ApplicationPathSet(
                Path.Combine(root, "cfg"),
                Path.Combine(root, "data"),
                Path.Combine(root, "cache"));

            ComposedSessionConfig composed = SessionConfigComposer.ComposeAndWrite(
                Server(),
                Account(),
                Character(LaunchMode.Gui),
                Install,
                paths,
                sessionId: "session-write");

            string expectedPath = Path.Combine(
                Path.Combine(paths.DataDirectory, "logs", "launcher"), "session-write", "session.json");
            Assert.Equal(expectedPath, composed.ConfigFilePath);
            Assert.True(File.Exists(expectedPath));

            string text = File.ReadAllText(expectedPath);
            Assert.DoesNotContain(Account().Password, text, StringComparison.Ordinal);
        }
        finally
        {
            if (Directory.Exists(root))
            {
                Directory.Delete(root, recursive: true);
            }
        }
    }

    [Fact]
    public void ComposeProbeAndWriteWritesThePasswordFreeProbeDocument()
    {
        string root = Path.Combine(
            Path.GetTempPath(),
            "acdream-launcher-probe-writer-tests",
            Guid.NewGuid().ToString("N"));
        try
        {
            var paths = new ApplicationPathSet(
                Path.Combine(root, "cfg"),
                Path.Combine(root, "data"),
                Path.Combine(root, "cache"));
            ComposedSessionConfig composed = SessionConfigComposer.ComposeProbeAndWrite(
                Server(),
                Account(),
                Install,
                paths,
                "probe-write");

            string json = File.ReadAllText(composed.ConfigFilePath);
            Assert.Contains("\"mode\": \"probe\"", json, StringComparison.Ordinal);
            Assert.DoesNotContain("\"character\"", json, StringComparison.Ordinal);
            Assert.DoesNotContain(Account().Password, json, StringComparison.Ordinal);
        }
        finally
        {
            if (Directory.Exists(root))
            {
                Directory.Delete(root, recursive: true);
            }
        }
    }

    private static JsonObject ParseRoot(ComposedSessionConfig composed)
    {
        string json = SessionConfigComposer.Serialize(composed.Document);
        return JsonNode.Parse(json)!.AsObject();
    }

    private static JsonObject SingleSession(ComposedSessionConfig composed)
    {
        JsonObject root = ParseRoot(composed);
        JsonArray sessions = root["sessions"]!.AsArray();
        return Assert.Single(sessions)!.AsObject();
    }

    private static void AssertKeys(JsonObject obj, params string[] expectedKeys)
    {
        var actual = new HashSet<string>(obj.Select(kv => kv.Key), StringComparer.Ordinal);
        var expected = new HashSet<string>(expectedKeys, StringComparer.Ordinal);
        Assert.Equal(expected, actual);
    }
}
