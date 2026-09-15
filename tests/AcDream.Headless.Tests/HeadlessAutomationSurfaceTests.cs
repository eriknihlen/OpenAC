using System.Net;
using AcDream.Automation;
using AcDream.Core.Net;
using AcDream.Core.Net.Messages;
using AcDream.DrakBot;
using AcDream.DrakBot.Remote;
using AcDream.Headless.Configuration;
using AcDream.Headless.Credentials;
using AcDream.Headless.Diagnostics;
using AcDream.Headless.Hosting;
using AcDream.Plugin.Abstractions;
using AcDream.Runtime.Chat;
using AcDream.Runtime.Session;
using AcDream.Tests.Fixtures.LauncherSession;

namespace AcDream.Headless.Tests;

/// <summary>
/// The headless host hands plugins the same automation surface the
/// graphical client does, bound to the session's runtime: no sub-surface
/// is the inert NoOp one, and the bits that need a binding (items,
/// equipment, projectiles, session commands) have one.
/// </summary>
public sealed class HeadlessAutomationSurfaceTests
{
    [Fact]
    public void PluginsGetTheRealSurfaceBoundToTheSessionRuntime()
    {
        string statusPath = Path.Combine(
            Path.GetTempPath(),
            $"acdream-headless-automation-{Guid.NewGuid():N}.jsonl");
        using var cleanup = new DeleteOnDispose(statusPath);
        var credential = new HeadlessCredentialSecret("fixture", "password");
        using var session = new HeadlessSessionHost(
            Descriptor(
                [DrakBotPlugin.Id],
                statusPath,
                pluginSettings: new()
                {
                    [DrakBotPlugin.Id] = new()
                    {
                        [DrakBotPlugin.PatrolOnLoginSetting] = "true",
                    },
                }),
            credential,
            new HeadlessDiagnosticWriter(new StringWriter()),
            new FixtureSessionOperations());

        IAutomationSurface automation = session.Plugins.Host.Automation;
        RuntimeAutomationSurface surface =
            Assert.IsType<RuntimeAutomationSurface>(automation);
        Assert.Same(surface, session.Automation);
        Assert.Equal(
            "true",
            session.Plugins.Host.SessionSettingsFor(DrakBotPlugin.Id)[
                DrakBotPlugin.PatrolOnLoginSetting]);

        // Every sub-surface is the bound surface itself, never the NoOp one.
        object[] subSurfaces =
        [
            automation.Character, automation.Spells, automation.Magic,
            automation.Chat, automation.Combat, automation.Equipment,
            automation.Items, automation.Loot, automation.Fellowship,
            automation.Enchantments, automation.Navigation, automation.Objects,
            automation.WorldTime, automation.Login, automation.Network,
            automation.Recovery, automation.Projectiles, automation.MovementProbe,
            automation.Dungeon, automation.Selection,
        ];
        Assert.All(subSurfaces, sub => Assert.Same(surface, sub));

        Assert.False(automation.IsAvailable);
        _ = session.Start();
        Assert.True(automation.IsAvailable);
        Assert.True(automation.Items.IsAvailable);
        Assert.True(automation.Equipment.IsAvailable);
        Assert.True(automation.Projectiles.IsAvailable);
        Assert.True(automation.MovementProbe.IsAvailable);
        Assert.Equal(
            PluginNavigationCommandStatus.Accepted,
            automation.Navigation.ClearMovementIntent());

        // The console reaches the bot through the registry the surface owns.
        Assert.Equal(1, session.Plugins.LoadedCount);
        Assert.Equal(
            SubmitOutcome.ClientHandled,
            session.SubmitConsoleLine("/drakbot status"));
        Assert.True(surface.TryHandlePluginCommand("/bot status"));
    }

    [Fact]
    public async Task ASessionThatNamesTheRemoteGetsItOnThePortItSets()
    {
        string statusPath = Path.Combine(
            Path.GetTempPath(),
            $"acdream-headless-remote-{Guid.NewGuid():N}.jsonl");
        using var cleanup = new DeleteOnDispose(statusPath);
        var probe = new System.Net.Sockets.TcpListener(IPAddress.Loopback, 0);
        probe.Start();
        int port = ((IPEndPoint)probe.LocalEndpoint).Port;
        probe.Stop();
        var credential = new HeadlessCredentialSecret("fixture", "password");
        using var session = new HeadlessSessionHost(
            Descriptor(
                [DrakBotPlugin.Id, DrakBotRemotePlugin.Id],
                statusPath,
                pluginSettings: new()
                {
                    [DrakBotRemotePlugin.Id] = new()
                    {
                        ["port"] = port.ToString(System.Globalization.CultureInfo.InvariantCulture),
                    },
                }),
            credential,
            new HeadlessDiagnosticWriter(new StringWriter()),
            new FixtureSessionOperations());

        _ = session.Start();

        Assert.Equal(2, session.Plugins.LoadedCount);
        using var client = new HttpClient { BaseAddress = new Uri($"http://127.0.0.1:{port}/") };
        Assert.Equal("ok", await client.GetStringAsync("healthz"));
        Assert.Contains(
            "acdream.drakbot.remote",
            await client.GetStringAsync("status"),
            StringComparison.Ordinal);
    }

    [Fact]
    public void ASessionThatNamesOnlyTheBotHasNoRemote()
    {
        string statusPath = Path.Combine(
            Path.GetTempPath(),
            $"acdream-headless-noremote-{Guid.NewGuid():N}.jsonl");
        using var cleanup = new DeleteOnDispose(statusPath);
        var credential = new HeadlessCredentialSecret("fixture", "password");
        using var session = new HeadlessSessionHost(
            Descriptor([DrakBotPlugin.Id], statusPath),
            credential,
            new HeadlessDiagnosticWriter(new StringWriter()),
            new FixtureSessionOperations());

        _ = session.Start();

        Assert.Equal(1, session.Plugins.LoadedCount);
    }

    private static HeadlessSessionDescriptor Descriptor(
        List<string> plugins,
        string statusPath,
        Dictionary<string, Dictionary<string, string>>? pluginSettings = null) => new()
        {
            Id = "headless-automation",
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
            LoginCommandDelayMs = 0,
            PluginSettings = pluginSettings,
        };

    private sealed class DeleteOnDispose(string path) : IDisposable
    {
        public void Dispose()
        {
            try
            {
                File.Delete(path);
            }
            catch (IOException)
            {
            }
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
}
