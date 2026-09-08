using System.Runtime.CompilerServices;
using AcDream.Headless.Configuration;
using AcDream.Tests.Fixtures.CampaignLa;

namespace AcDream.Headless.Tests;

public sealed class SessionConfigurationSharedFixtureTests
{
    [Fact]
    public void HeadlessReaderAcceptsLauncherCoreComposerDocument()
    {
        using TemporaryFile file = TemporaryFile.Create(
            LauncherCoreSessionConfigFixture.Compose());

        HeadlessConfiguration configuration =
            HeadlessConfigurationLoader.Load(file.Path);

        HeadlessSessionDescriptor session = Assert.Single(configuration.Sessions)!;
        Assert.Equal("composer-contract", session.Id);
        Assert.Equal("composer.example", session.Endpoint.Host);
        Assert.Equal(9010, session.Endpoint.Port);
        Assert.Equal("composer-account", session.Account);
        HeadlessCharacterSelector character =
            Assert.IsType<HeadlessCharacterSelector>(session.Character);
        HeadlessBotPolicyDescriptor policy =
            Assert.IsType<HeadlessBotPolicyDescriptor>(session.Policy);
        Assert.Equal(0x50000001u, character.Id);
        Assert.Equal("idle", policy.Id);
        Assert.Equal(
            HeadlessCredentialProviderKind.StandardInput,
            session.Credential.Provider);
        Assert.Equal("session", session.Credential.Reference);
        Assert.Equal("composer-dats", configuration.Process.Content?.DatDirectory);
        Assert.Equal(
            "composer-dats/acdream.pak",
            configuration.Process.Content?.PreparedAssetPath);
        Assert.Equal(["ComposerPlugin"], session.Plugins);
        Assert.Equal(["/composer command"], session.LoginCommands);
        Assert.Equal(625, session.LoginCommandDelayMs);
        string statusFile = Assert.IsType<string>(session.StatusFile);
        Assert.EndsWith(
            Path.Combine("composer-contract", "status.jsonl"),
            statusFile,
            StringComparison.Ordinal);
        Assert.DoesNotContain(
            LauncherCoreSessionConfigFixture.Password,
            File.ReadAllText(file.Path),
            StringComparison.Ordinal);
    }

    [Fact]
    public void HeadlessReaderPreservesLauncherExplicitEmptyPluginAllowList()
    {
        using TemporaryFile file = TemporaryFile.Create(
            LauncherCoreSessionConfigFixture.ComposeEmptyPlugins());

        HeadlessSessionDescriptor session = Assert.Single(
            HeadlessConfigurationLoader.Load(file.Path).Sessions)!;

        Assert.NotNull(session.Plugins);
        Assert.Empty(session.Plugins);
    }

    [Fact]
    public void HeadlessReaderPreservesProbeLoadNoneAllowList()
    {
        using TemporaryFile file = TemporaryFile.Create(
            LauncherCoreSessionConfigFixture.ComposeProbe());

        HeadlessSessionDescriptor session = Assert.Single(
            HeadlessConfigurationLoader.Load(file.Path).Sessions)!;

        Assert.Equal(HeadlessSessionMode.Probe, session.Mode);
        Assert.NotNull(session.Plugins);
        Assert.Empty(session.Plugins);
    }

    [Fact]
    public void HeadlessReaderAcceptsOptionalLayeredPreparedContent()
    {
        string json = LauncherCoreSessionConfigFixture.Compose();
        json = json.Replace(
            "\"preparedAssetPath\": \"composer-dats/acdream.pak\"",
            "\"preparedAssetPath\": \"composer-dats/acdream.pak\",\n"
            + "      \"preparedAssetOverlayPath\": \"composer-dats/update.pak\",\n"
            + "      \"preparedAssetBaseRecipeVersion\": 4,\n"
            + "      \"preparedAssetEffectiveRecipeVersion\": 5",
            StringComparison.Ordinal);
        using TemporaryFile file = TemporaryFile.Create(json);

        HeadlessContentDescriptor content = Assert.IsType<HeadlessContentDescriptor>(
            HeadlessConfigurationLoader.Load(file.Path).Process.Content);

        Assert.Equal("composer-dats/update.pak", content.PreparedAssetOverlayPath);
        Assert.Equal(4u, content.PreparedAssetBaseRecipeVersion);
        Assert.Equal(5u, content.PreparedAssetEffectiveRecipeVersion);
    }

    [Fact]
    public void HeadlessReaderAcceptsTheProductionShapedSharedFixture()
    {
        HeadlessConfiguration configuration =
            HeadlessConfigurationLoader.Load(SharedFixturePath());

        Assert.Equal(
            "shared-fixture-dats",
            configuration.Process.Content?.DatDirectory);
        Assert.Equal(
            "shared-fixture-dats/acdream.pak",
            configuration.Process.Content?.PreparedAssetPath);
        HeadlessSessionDescriptor session = Assert.Single(configuration.Sessions)!;
        Assert.Equal("shared-fixture", session.Id);
        Assert.Equal("127.0.0.1", session.Endpoint.Host);
        Assert.Equal(9000, session.Endpoint.Port);
        Assert.Equal("sharedaccount", session.Account);
        Assert.Equal("SharedToon", session.Character!.Name);
        Assert.Equal("idle", session.Policy!.Id);
        Assert.Equal(
            HeadlessCredentialProviderKind.StandardInput,
            session.Credential.Provider);
        Assert.Equal("session", session.Credential.Reference);

        Assert.Equal(["ExamplePlugin", "AnotherPlugin"], session.Plugins);
        Assert.Equal(
            ["/tell someone, hi", "/vt start"],
            session.LoginCommands);
        Assert.Equal(750, session.LoginCommandDelayMs);
        Assert.Equal("shared-fixture-status.jsonl", session.StatusFile);
    }

    [Fact]
    public void AbsentLaunchContractFieldsFallBackToPinnedDefaults()
    {
        using TemporaryFile file = TemporaryFile.Create(
            """
            {
              "version": 1,
              "sessions": [
                {
                  "id": "no-launch-contract-fields",
                  "endpoint": { "host": "127.0.0.1", "port": 9000 },
                  "account": "account",
                  "character": { "index": 0 },
                  "policy": { "id": "idle" },
                  "credential": { "provider": "environment", "reference": "X" }
                }
              ]
            }
            """);

        HeadlessConfiguration configuration =
            HeadlessConfigurationLoader.Load(file.Path);

        HeadlessSessionDescriptor session = Assert.Single(configuration.Sessions)!;
        Assert.Null(session.Plugins);
        Assert.Null(session.LoginCommands);
        Assert.Equal(500, session.LoginCommandDelayMs);
        Assert.Null(session.StatusFile);
    }

    [Fact]
    public void EmptyPluginsEntryFailsLoad()
    {
        using TemporaryFile file = TemporaryFile.Create(
            """
            {
              "version": 1,
              "sessions": [
                {
                  "id": "bad-plugins",
                  "endpoint": { "host": "127.0.0.1", "port": 9000 },
                  "account": "account",
                  "character": { "index": 0 },
                  "policy": { "id": "idle" },
                  "credential": { "provider": "environment", "reference": "X" },
                  "plugins": ["Ok", "   "]
                }
              ]
            }
            """);

        Assert.Throws<HeadlessConfigurationException>(
            () => HeadlessConfigurationLoader.Load(file.Path));
    }

    [Fact]
    public void NegativeLoginCommandDelayFailsLoad()
    {
        using TemporaryFile file = TemporaryFile.Create(
            """
            {
              "version": 1,
              "sessions": [
                {
                  "id": "bad-delay",
                  "endpoint": { "host": "127.0.0.1", "port": 9000 },
                  "account": "account",
                  "character": { "index": 0 },
                  "policy": { "id": "idle" },
                  "credential": { "provider": "environment", "reference": "X" },
                  "loginCommandDelayMs": -1
                }
              ]
            }
            """);

        Assert.Throws<HeadlessConfigurationException>(
            () => HeadlessConfigurationLoader.Load(file.Path));
    }

    [Fact]
    public void BlankStatusFileFailsLoad()
    {
        using TemporaryFile file = TemporaryFile.Create(
            """
            {
              "version": 1,
              "sessions": [
                {
                  "id": "bad-status-file",
                  "endpoint": { "host": "127.0.0.1", "port": 9000 },
                  "account": "account",
                  "character": { "index": 0 },
                  "policy": { "id": "idle" },
                  "credential": { "provider": "environment", "reference": "X" },
                  "statusFile": "   "
                }
              ]
            }
            """);

        Assert.Throws<HeadlessConfigurationException>(
            () => HeadlessConfigurationLoader.Load(file.Path));
    }

    internal static string SharedFixturePath(
        [CallerFilePath] string sourcePath = "") =>
        Path.Combine(
            FindRepositoryRoot(sourcePath),
            "tests",
            "Fixtures",
            "campaign-la",
            "session-config-shared-fixture.json");

    private static string FindRepositoryRoot(string sourcePath)
    {
        string[] starts =
        {
            Path.GetDirectoryName(sourcePath) ?? string.Empty,
            Directory.GetCurrentDirectory(),
            AppContext.BaseDirectory,
        };
        foreach (string start in starts)
        {
            if (string.IsNullOrEmpty(start))
                continue;

            DirectoryInfo? directory = new(start);
            while (directory is not null)
            {
                if (File.Exists(Path.Combine(directory.FullName, "AcDream.slnx")))
                    return directory.FullName;
                directory = directory.Parent;
            }
        }

        throw new DirectoryNotFoundException(
            "Could not find AcDream.slnx above the working or output directory.");
    }

    private sealed class TemporaryFile : IDisposable
    {
        private TemporaryFile(string path) => Path = path;

        internal string Path { get; }

        internal static TemporaryFile Create(string json)
        {
            string path = System.IO.Path.Combine(
                System.IO.Path.GetTempPath(),
                $"acdream-headless-la1-{Guid.NewGuid():N}.json");
            File.WriteAllText(path, json);
            return new TemporaryFile(path);
        }

        public void Dispose() => File.Delete(Path);
    }
}
