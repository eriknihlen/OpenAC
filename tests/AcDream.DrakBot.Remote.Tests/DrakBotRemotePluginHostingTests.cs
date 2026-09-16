using System.Net;
using System.Net.Http.Headers;
using System.Net.WebSockets;
using System.Text;
using System.Text.Json;
using AcDream.Core.Plugins;

namespace AcDream.DrakBot.Remote.Tests;

/// <summary>The remote hosted the way the client hosts it: a built-in beside the bot, through <see cref="PluginSession"/>.</summary>
public sealed class DrakBotRemotePluginHostingTests
{
    private static (RemoteTestHost Host, PluginSession Session, DrakBotPlugin Bot, DrakBotRemotePlugin Remote) Host(Action<RemoteTestHost>? configure = null, RemoteHostServices? services = null)
    {
        var host = new RemoteTestHost();
        configure?.Invoke(host);
        var session = new PluginSession(host);
        var bot = new DrakBotPlugin();
        var remote = new DrakBotRemotePlugin(bot, services);
        session.AddBuiltIn(new BuiltInPlugin(DrakBotPlugin.Id, DrakBotPlugin.DisplayName, DrakBotPlugin.Version, bot));
        session.AddBuiltIn(new BuiltInPlugin(DrakBotRemotePlugin.Id, DrakBotRemotePlugin.DisplayName, DrakBotRemotePlugin.Version, remote));
        session.Start([], allowList: null);
        return (host, session, bot, remote);
    }

    [Fact]
    public void WithoutAPortNothingListens()
    {
        (RemoteTestHost host, PluginSession session, _, DrakBotRemotePlugin remote) = Host();
        using (session)
        {
            Assert.Equal([DrakBotPlugin.Id, DrakBotRemotePlugin.Id], session.LoadedPluginIds);
            Assert.False(remote.IsListening);
            Assert.Equal(0, remote.Port);
            Assert.True(host.Commands.TryHandle("/remote status"));
            Assert.Contains(host.Surface.SystemMessages, message => message.Contains("off", StringComparison.Ordinal));
        }
    }

    [Fact]
    public async Task ASessionPortBringsTheRemoteUpAndCommandsRoundTripThroughTheTick()
    {
        int port = RemoteHttpServerTests.FreePort();
        (RemoteTestHost host, PluginSession session, DrakBotPlugin bot, DrakBotRemotePlugin remote) = Host(h =>
        {
            h.SessionSettings["port"] = port.ToString(System.Globalization.CultureInfo.InvariantCulture);
            h.SessionSettings["token"] = "t";
            h.Surface.Name = "Buffy";
        });
        using (session)
        {
            Assert.True(remote.IsListening);
            Assert.Contains(host.Log.Lines, line => line.Contains("listening on", StringComparison.Ordinal));
            using var client = new HttpClient { BaseAddress = new Uri($"http://127.0.0.1:{remote.Port}/") };
            client.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", "t");

            // Nothing has been published before the first tick; a tick publishes the status.
            host.Events.FireTick(0.2);
            using JsonDocument status = JsonDocument.Parse(await client.GetStringAsync("status"));
            JsonElement me = status.RootElement.GetProperty("clients")[0];
            Assert.Equal("Buffy", me.GetProperty("character").GetString());
            Assert.False(me.GetProperty("macroRunning").GetBoolean());

            using HttpResponseMessage accepted = await client.PostAsync("command",
                new StringContent("{\"action\":\"macro\",\"value\":\"true\"}", Encoding.UTF8, "application/json"));
            Assert.Equal(HttpStatusCode.Accepted, accepted.StatusCode);
            Assert.False(bot.Engine!.IsRunning); // queued, not applied, until the tick
            host.Events.FireTick(0.2);
            Assert.True(bot.Engine.IsRunning);

            using var socket = new ClientWebSocket();
            using var cancel = new CancellationTokenSource(TimeSpan.FromSeconds(10));
            await socket.ConnectAsync(new Uri($"ws://127.0.0.1:{remote.Port}/statusfeed?token=t"), cancel.Token);
            string pushed = await RemoteHttpServerTests.ReceiveTextAsync(socket, cancel.Token);
            Assert.Contains("\"macroRunning\":true", pushed, StringComparison.Ordinal);

            string settings = await client.GetStringAsync("settings");
            Assert.Contains("\"vitals.healBelow\":0.6", settings, StringComparison.Ordinal);
            using HttpResponseMessage inventory = await client.GetAsync("inventory");
            Assert.Equal(HttpStatusCode.OK, inventory.StatusCode);

            Assert.True(host.Commands.TryHandle("/remote status"));
            Assert.Contains(host.Surface.SystemMessages, message => message.Contains("listening on", StringComparison.Ordinal));
            Assert.True(host.Commands.TryHandle("/remote off"));
            Assert.False(remote.IsListening);
        }
    }

    [Fact]
    public async Task AHostThatReadsDungeonsGetsFloorPlansDrawnForWhereTheCharacterIs()
    {
        int port = RemoteHttpServerTests.FreePort();
        var asked = new List<uint>();
        var services = new RemoteHostServices
        {
            DungeonGeometry = landblock =>
            {
                asked.Add(landblock);
                return new RemoteDungeonGeometry(landblock,
                [
                    new RemoteDungeonPolygon(RemoteDungeonSurface.Floor, [new(-5720f, -11400f), new(-5710f, -11400f), new(-5710f, -11390f), new(-5720f, -11390f)], -6f, -6f),
                ]);
            },
        };
        (RemoteTestHost host, PluginSession session, _, DrakBotRemotePlugin remote) = Host(h =>
        {
            h.SessionSettings["port"] = port.ToString(System.Globalization.CultureInfo.InvariantCulture);
            h.Surface.Position = new AcDream.Plugin.Abstractions.PluginNavigationPosition(0x61450103u, -23.8d, -47.4d, -0.025d, 0f, IsOutdoor: false);
        }, services);
        using (session)
        {
            using var client = new HttpClient { BaseAddress = new Uri($"http://127.0.0.1:{remote.Port}/") };
            host.Events.FireTick(0.2);
            using JsonDocument status = JsonDocument.Parse(await client.GetStringAsync("status"));
            Assert.True(status.RootElement.GetProperty("capabilities").GetProperty("maps").GetBoolean());

            // The tick reads the dungeon, the pool draws it, a later tick publishes it.
            var deadline = DateTime.UtcNow.AddSeconds(10);
            int count = 0;
            while (count == 0 && DateTime.UtcNow < deadline)
            {
                host.Events.FireTick(1.0);
                await Task.Delay(10);
                using JsonDocument maps = JsonDocument.Parse(await client.GetStringAsync("maps"));
                count = maps.RootElement.GetProperty("count").GetInt32();
            }
            Assert.Equal(1, count);
            Assert.Equal([0x61450000u], asked);
            using HttpResponseMessage png = await client.GetAsync("map?lb=61450000&layer=0");
            Assert.Equal(HttpStatusCode.OK, png.StatusCode);
            Assert.Equal("image/png", png.Content.Headers.ContentType?.MediaType);
        }
    }

    [Fact]
    public void SetupWritesTheFileAndStartsListening()
    {
        int port = RemoteHttpServerTests.FreePort();
        (RemoteTestHost host, PluginSession session, _, DrakBotRemotePlugin remote) = Host();
        using (session)
        {
            Assert.True(host.Commands.TryHandle($"/remote setup {port} mytoken lan"));
            Assert.True(remote.IsListening);
            // The plugin's storage is scoped under its id.
            string savedKey = Assert.Single(host.Storage.Text.Keys, key => key.EndsWith(RemoteOptions.FileName, StringComparison.Ordinal));
            RemoteOptions saved = RemoteOptions.FromJson(host.Storage.Text[savedKey]);
            Assert.True(saved.Enabled);
            Assert.Equal(port, saved.Port);
            Assert.Equal("mytoken", saved.Token);
            Assert.True(saved.BindsEveryInterface);

            // Every interface without a token is refused, and nothing is written.
            host.Storage.Delete(savedKey);
            Assert.True(host.Commands.TryHandle($"/remote setup {port} lan"));
            Assert.DoesNotContain(host.Storage.Text.Keys, key => key.EndsWith(RemoteOptions.FileName, StringComparison.Ordinal));
            Assert.Contains(host.Surface.SystemMessages, message => message.Contains("token is required", StringComparison.Ordinal));
        }
    }

    [Fact]
    public void DisposingTheSessionStopsTheListener()
    {
        int port = RemoteHttpServerTests.FreePort();
        (_, PluginSession session, _, DrakBotRemotePlugin remote) = Host(h =>
            h.SessionSettings["port"] = port.ToString(System.Globalization.CultureInfo.InvariantCulture));
        Assert.True(remote.IsListening);

        session.Dispose();

        Assert.False(remote.IsListening);
    }
}
