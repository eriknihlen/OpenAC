using System.Text.Json.Nodes;
using AcDream.Launcher.Core.Launching;
using AcDream.Launcher.Core.Profiles;
using AcDream.Platform;

namespace AcDream.Launcher.Core.Tests.Launching;

public sealed class SessionConfigComposerTests
{
    private static readonly ApplicationPathSet Paths = new(
        ConfigDirectory: "/cfg/acdream",
        DataDirectory: "/data/acdream",
        CacheDirectory: "/cache/acdream",
        LegacyConfigDirectory: null);

    private static readonly LauncherInstallRecord Install = new(
        DatDirectory: "/dats",
        PreparedAssetPath: "/data/acdream/pak/acdream.pak");

    private static ServerProfile Server() =>
        new() { Name = "Local ACE", Host = "127.0.0.1", Port = 9000 };

    private static AccountProfile Account() =>
        new() { Account = "testaccount", Password = "S3cretPassw0rd!" };

    private static CharacterProfile Character(LaunchMode mode, string? id = "0x5000000A") =>
        new()
        {
            Name = "+Acdream",
            Id = id,
            LaunchMode = mode,
            Plugins = ["ExamplePlugin"],
            LoginCommands = ["/tell someone, hi"],
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
            Path.Combine(Paths.CacheDirectory, "launcher", "sessions", "session-gui", "status.jsonl"),
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
    public void UnconfiguredPluginsOmitTheAllowListSoEveryPluginLoads()
    {
        CharacterProfile character = Character(LaunchMode.Gui);
        character.Plugins = [];
        character.LoginCommands = [];

        ComposedSessionConfig composed = SessionConfigComposer.Compose(
            Server(),
            Account(),
            character,
            Install,
            Paths,
            sessionId: "session-empty-lists");

        JsonObject session = SingleSession(composed);
        Assert.False(session.ContainsKey("plugins"));
        Assert.False(session.ContainsKey("loginCommands"));
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
        character.Plugins = ["acdream.mosstank"];

        ComposedSessionConfig composed = SessionConfigComposer.Compose(
            Server(),
            Account(),
            character,
            Install,
            Paths,
            sessionId: "session-one-plugin");

        JsonObject session = SingleSession(composed);
        Assert.Equal(
            ["acdream.mosstank"],
            session["plugins"]!.AsArray().Select(node => (string?)node));
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
                Paths.CacheDirectory, "launcher", "sessions", "session-probe", "status.jsonl"),
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
                Path.Combine(root, "cache"),
                null);

            ComposedSessionConfig composed = SessionConfigComposer.ComposeAndWrite(
                Server(),
                Account(),
                Character(LaunchMode.Gui),
                Install,
                paths,
                sessionId: "session-write");

            string expectedPath = Path.Combine(
                paths.CacheDirectory, "launcher", "sessions", "session-write", "session.json");
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
                Path.Combine(root, "cache"),
                null);
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
