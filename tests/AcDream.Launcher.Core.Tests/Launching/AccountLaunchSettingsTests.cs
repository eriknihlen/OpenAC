using System.Text.Json.Nodes;
using AcDream.Launcher.Core.Launching;
using AcDream.Launcher.Core.Profiles;
using AcDream.Platform;

namespace AcDream.Launcher.Core.Tests.Launching;

/// <summary>Every character on an account launches with the account's plugins and logon commands,
/// unless the character has its own plugin list.</summary>
public sealed class AccountLaunchSettingsTests
{
    private static readonly ApplicationPathSet Paths = new(
        ConfigDirectory: "/cfg/acdream",
        DataDirectory: "/data/acdream",
        CacheDirectory: "/cache/acdream");

    private static readonly LauncherInstallRecord Install = new(
        DatDirectory: "/dats",
        PreparedAssetPath: "/data/acdream/pak/acdream.pak");

    private static AccountProfile Account() => new()
    {
        Account = "testaccount",
        Password = "pw",
        Plugins = ["a.one", "b.two"],
        LoginCommands = ["/vt start", "/vt nav load bore"],
    };

    private static JsonObject Compose(AccountProfile account, CharacterProfile character)
    {
        ComposedSessionConfig composed = SessionConfigComposer.Compose(
            new ServerProfile { Name = "Local", Host = "127.0.0.1", Port = 9000 },
            account,
            character,
            Install,
            Paths,
            sessionId: "session-account-settings");
        return JsonNode.Parse(SessionConfigComposer.Serialize(composed.Document))!["sessions"]!.AsArray().Single()!.AsObject();
    }

    private static string[] Strings(JsonNode? node) =>
        [.. node!.AsArray().Select(item => (string)item!)];

    [Fact]
    public void ACharacterWithoutItsOwnListLaunchesWithTheAccountsPlugins()
    {
        JsonObject session = Compose(Account(), new CharacterProfile { Name = "+Hero", LaunchMode = LaunchMode.Gui });

        Assert.Equal(["a.one", "b.two"], Strings(session["plugins"]));
    }

    [Fact]
    public void ACharactersOwnListReplacesTheAccountsPlugins()
    {
        JsonObject session = Compose(
            Account(),
            new CharacterProfile { Name = "+Hero", LaunchMode = LaunchMode.Gui, Plugins = ["c.three"] });

        Assert.Equal(["c.three"], Strings(session["plugins"]));
    }

    [Fact]
    public void AnEmptyOwnListLoadsNoPlugins()
    {
        JsonObject session = Compose(
            Account(),
            new CharacterProfile { Name = "+Hero", LaunchMode = LaunchMode.Gui, Plugins = [] });

        Assert.Empty(session["plugins"]!.AsArray());
    }

    [Fact]
    public void TheCharacterScreenLaunchCarriesTheAccountsPluginsAndCommands()
    {
        JsonObject session = Compose(
            Account(),
            new CharacterProfile { Name = string.Empty, LaunchMode = LaunchMode.GuiSelect });

        Assert.Equal(["a.one", "b.two"], Strings(session["plugins"]));
        Assert.Equal(["/vt start", "/vt nav load bore"], Strings(session["loginCommands"]));
    }

    [Fact]
    public void EveryCharacterRunsTheAccountsLogonCommandsInOrder()
    {
        JsonObject session = Compose(
            Account(),
            new CharacterProfile { Name = "+Hero", LaunchMode = LaunchMode.Headless, Plugins = ["c.three"] });

        Assert.Equal(["/vt start", "/vt nav load bore"], Strings(session["loginCommands"]));
    }
}
