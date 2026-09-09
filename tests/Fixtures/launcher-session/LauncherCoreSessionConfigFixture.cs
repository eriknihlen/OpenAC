using AcDream.Launcher.Core.Launching;
using AcDream.Launcher.Core.Profiles;
using AcDream.Platform;

namespace AcDream.Tests.Fixtures.LauncherSession;

internal static class LauncherCoreSessionConfigFixture
{
    internal const string Password = "must-not-be-serialized";

    internal static string Compose()
    {
        var server = new ServerProfile
        {
            Name = "Composer Server",
            Host = "composer.example",
            Port = 9010,
        };
        var account = new AccountProfile
        {
            Account = "composer-account",
            Password = Password,
        };
        var character = new CharacterProfile
        {
            Name = "Composer Character",
            Id = "0x50000001",
            LaunchMode = LaunchMode.Headless,
            Plugins = ["ComposerPlugin"],
            LoginCommands = ["/composer command"],
        };
        var install = new LauncherInstallRecord(
            "composer-dats",
            "composer-dats/acdream.pak");
        var paths = new ApplicationPathSet(
            Path.Combine(Path.GetTempPath(), "composer-config"),
            Path.Combine(Path.GetTempPath(), "composer-data"),
            Path.Combine(Path.GetTempPath(), "composer-cache"),
            LegacyConfigDirectory: null);

        ComposedSessionConfig composed = SessionConfigComposer.Compose(
            server,
            account,
            character,
            install,
            paths,
            "composer-contract",
            loginCommandDelayMs: 625);

        return SessionConfigComposer.Serialize(composed.Document);
    }

    internal static string ComposeEmptyPlugins()
    {
        (ServerProfile server, AccountProfile account,
            LauncherInstallRecord install, ApplicationPathSet paths) = Inputs();
        var character = new CharacterProfile
        {
            Name = "Composer Character",
            Id = "0x50000001",
            LaunchMode = LaunchMode.Headless,
            Plugins = ["none"],
            LoginCommands = [],
        };

        ComposedSessionConfig composed = SessionConfigComposer.Compose(
            server,
            account,
            character,
            install,
            paths,
            "composer-empty-plugins");
        return SessionConfigComposer.Serialize(composed.Document);
    }

    internal static string ComposeProbe()
    {
        (ServerProfile server, AccountProfile account,
            LauncherInstallRecord install, ApplicationPathSet paths) = Inputs();
        ComposedSessionConfig composed = SessionConfigComposer.ComposeProbe(
            server,
            account,
            install,
            paths,
            "composer-probe");
        return SessionConfigComposer.Serialize(composed.Document);
    }

    private static (
        ServerProfile Server,
        AccountProfile Account,
        LauncherInstallRecord Install,
        ApplicationPathSet Paths) Inputs() =>
        (
            new ServerProfile
            {
                Name = "Composer Server",
                Host = "composer.example",
                Port = 9010,
            },
            new AccountProfile
            {
                Account = "composer-account",
                Password = Password,
            },
            new LauncherInstallRecord(
                "composer-dats",
                "composer-dats/acdream.pak"),
            new ApplicationPathSet(
                Path.Combine(Path.GetTempPath(), "composer-config"),
                Path.Combine(Path.GetTempPath(), "composer-data"),
                Path.Combine(Path.GetTempPath(), "composer-cache"),
                LegacyConfigDirectory: null));
}
