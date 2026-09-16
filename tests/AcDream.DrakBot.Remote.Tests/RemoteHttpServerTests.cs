using System.Net;
using System.Net.Http.Headers;
using System.Net.Sockets;
using System.Net.WebSockets;
using System.Text;
using System.Text.Json;
using AcDream.DrakBot.Tests.Fakes;

namespace AcDream.DrakBot.Remote.Tests;

public sealed class RemoteHttpServerTests
{
    /// <summary>A port nothing listens on right now; the server may still walk up from it.</summary>
    internal static int FreePort()
    {
        var probe = new TcpListener(IPAddress.Loopback, 0);
        probe.Start();
        int port = ((IPEndPoint)probe.LocalEndpoint).Port;
        probe.Stop();
        return port;
    }

    private static (RemoteHttpServer Server, List<RemoteCommand> Commands, FakeLogger Log) Start(string? token = null, Func<uint, Task<byte[]?>>? icons = null)
    {
        var commands = new List<RemoteCommand>();
        var log = new FakeLogger();
        var options = new RemoteOptions { Enabled = true, Port = FreePort(), Token = token };
        var server = new RemoteHttpServer(options, log, command => { commands.Add(command); return true; }, icons);
        Assert.Null(server.TryStart());
        return (server, commands, log);
    }

    private static HttpClient Client(RemoteHttpServer server, string? token = null)
    {
        var client = new HttpClient { BaseAddress = new Uri($"http://127.0.0.1:{server.Port}/") };
        if (token is not null)
            client.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", token);
        return client;
    }

    [Fact]
    public async Task HealthStatusAndTheEmptyDocumentsAreServed()
    {
        (RemoteHttpServer server, _, _) = Start();
        using (server)
        using (HttpClient client = Client(server))
        {
            Assert.Equal("ok", await client.GetStringAsync("healthz"));
            string status = await client.GetStringAsync("status");
            Assert.Contains(RemoteStatusBuilder.Schema, status, StringComparison.Ordinal);
            Assert.Equal(status, await client.GetStringAsync(""));

            server.PublishStatus("{\"clientCount\":1}"u8.ToArray());
            Assert.Equal("{\"clientCount\":1}", await client.GetStringAsync("status.json"));

            Assert.Contains("\"version\":0", await client.GetStringAsync("inventory?pid=" + Environment.ProcessId), StringComparison.Ordinal);
            Assert.Contains(RemoteSettingsBridge.Schema, await client.GetStringAsync("settings"), StringComparison.Ordinal);
            Assert.Contains("\"runs\":[]", await client.GetStringAsync("runs"), StringComparison.Ordinal);
            Assert.Contains("\"maps\":[]", await client.GetStringAsync("maps"), StringComparison.Ordinal);

            using HttpResponseMessage otherClient = await client.GetAsync("inventory?pid=1");
            Assert.Equal(HttpStatusCode.NotFound, otherClient.StatusCode);
            using HttpResponseMessage missing = await client.GetAsync("nothing");
            Assert.Equal(HttpStatusCode.NotFound, missing.StatusCode);
            using HttpResponseMessage video = await client.GetAsync("stream?pid=1");
            Assert.Equal(HttpStatusCode.ServiceUnavailable, video.StatusCode);
            using HttpResponseMessage preflight = await client.SendAsync(new HttpRequestMessage(HttpMethod.Options, "command"));
            Assert.Equal(HttpStatusCode.NoContent, preflight.StatusCode);
            Assert.Equal("*", preflight.Headers.GetValues("Access-Control-Allow-Origin").Single());
        }
    }

    [Fact]
    public async Task ATokenGatesEverythingButHealth()
    {
        (RemoteHttpServer server, _, _) = Start(token: "s3cret");
        using (server)
        {
            using HttpClient anonymous = Client(server);
            Assert.Equal("ok", await anonymous.GetStringAsync("healthz"));
            using HttpResponseMessage denied = await anonymous.GetAsync("status");
            Assert.Equal(HttpStatusCode.Unauthorized, denied.StatusCode);
            using HttpResponseMessage byQuery = await anonymous.GetAsync("status?token=s3cret");
            Assert.Equal(HttpStatusCode.OK, byQuery.StatusCode);
            using HttpResponseMessage wrongQuery = await anonymous.GetAsync("status?token=s3cre");
            Assert.Equal(HttpStatusCode.Unauthorized, wrongQuery.StatusCode);

            using HttpClient bearer = Client(server, "s3cret");
            using HttpResponseMessage allowed = await bearer.GetAsync("status");
            Assert.Equal(HttpStatusCode.OK, allowed.StatusCode);
        }
    }

    [Fact]
    public async Task CommandsAreCheckedAndQueued()
    {
        (RemoteHttpServer server, List<RemoteCommand> commands, _) = Start();
        using (server)
        using (HttpClient client = Client(server))
        {
            using HttpResponseMessage accepted = await client.PostAsync("command",
                new StringContent($"{{\"pid\":{Environment.ProcessId},\"action\":\"macro\",\"value\":\"true\"}}", Encoding.UTF8, "application/json"));
            Assert.Equal(HttpStatusCode.Accepted, accepted.StatusCode);
            Assert.Equal(new RemoteCommand("macro", "true"), Assert.Single(commands));

            using HttpResponseMessage objectValue = await client.PostAsync("command",
                new StringContent("{\"action\":\"setSetting\",\"value\":{\"key\":\"vitals.healBelow\",\"value\":0.5}}", Encoding.UTF8, "application/json"));
            Assert.Equal(HttpStatusCode.Accepted, objectValue.StatusCode);
            Assert.Equal("{\"key\":\"vitals.healBelow\",\"value\":0.5}", commands[^1].Value);

            using HttpResponseMessage unknown = await client.PostAsync("command",
                new StringContent("{\"action\":\"launchNukes\",\"value\":\"true\"}", Encoding.UTF8, "application/json"));
            Assert.Equal(HttpStatusCode.BadRequest, unknown.StatusCode);
            using HttpResponseMessage garbage = await client.PostAsync("command", new StringContent("nope", Encoding.UTF8, "application/json"));
            Assert.Equal(HttpStatusCode.BadRequest, garbage.StatusCode);
            using HttpResponseMessage otherPid = await client.PostAsync("command",
                new StringContent("{\"pid\":1,\"action\":\"macro\",\"value\":\"true\"}", Encoding.UTF8, "application/json"));
            Assert.Equal(HttpStatusCode.NotFound, otherPid.StatusCode);
            using HttpResponseMessage wrongMethod = await client.GetAsync("command");
            Assert.Equal(HttpStatusCode.MethodNotAllowed, wrongMethod.StatusCode);
            Assert.Equal(2, commands.Count);
        }
    }

    [Fact]
    public async Task TheFeedPushesEveryPublishedStatus()
    {
        (RemoteHttpServer server, _, _) = Start(token: "t");
        using (server)
        {
            using var socket = new ClientWebSocket();
            using var cancel = new CancellationTokenSource(TimeSpan.FromSeconds(10));
            await socket.ConnectAsync(new Uri($"ws://127.0.0.1:{server.Port}/statusfeed?token=t"), cancel.Token);

            string first = await ReceiveTextAsync(socket, cancel.Token);
            Assert.Contains(RemoteStatusBuilder.Schema, first, StringComparison.Ordinal);
            Assert.Equal(1, server.FeedClients);

            server.PublishStatus("{\"n\":1}"u8.ToArray());
            Assert.Equal("{\"n\":1}", await ReceiveTextAsync(socket, cancel.Token));
            server.PublishStatus("{\"n\":2}"u8.ToArray());
            Assert.Equal("{\"n\":2}", await ReceiveTextAsync(socket, cancel.Token));

            await socket.CloseAsync(WebSocketCloseStatus.NormalClosure, "bye", cancel.Token);
            for (int waited = 0; waited < 50 && server.FeedClients > 0; waited++)
                await Task.Delay(20, cancel.Token);
            Assert.Equal(0, server.FeedClients);
        }
    }

    [Fact]
    public async Task TheFeedRefusesWithoutTheToken()
    {
        (RemoteHttpServer server, _, _) = Start(token: "t");
        using (server)
        {
            using var socket = new ClientWebSocket();
            using var cancel = new CancellationTokenSource(TimeSpan.FromSeconds(10));
            await Assert.ThrowsAsync<WebSocketException>(() =>
                socket.ConnectAsync(new Uri($"ws://127.0.0.1:{server.Port}/statusfeed"), cancel.Token));
        }
    }

    [Fact]
    public async Task IconsComeFromTheHostAndAreCachedByTheirId()
    {
        (RemoteHttpServer noIcons, _, _) = Start();
        using (noIcons)
        using (HttpClient client = Client(noIcons))
        {
            using HttpResponseMessage unavailable = await client.GetAsync("icon?did=100663296");
            Assert.Equal(HttpStatusCode.ServiceUnavailable, unavailable.StatusCode);
        }

        byte[] png = [0x89, 0x50, 0x4E, 0x47];
        (RemoteHttpServer server, _, _) = Start(icons: did => Task.FromResult(did == 0x06001234u ? png : null));
        using (server)
        using (HttpClient client = Client(server))
        {
            using HttpResponseMessage found = await client.GetAsync("icon?did=" + 0x06001234u);
            Assert.Equal(HttpStatusCode.OK, found.StatusCode);
            Assert.Equal("image/png", found.Content.Headers.ContentType?.MediaType);
            Assert.Equal(png, await found.Content.ReadAsByteArrayAsync());
            Assert.Equal("\"06001234\"", found.Headers.ETag?.Tag);

            using var conditional = new HttpRequestMessage(HttpMethod.Get, "icon?did=" + 0x06001234u);
            conditional.Headers.IfNoneMatch.Add(new EntityTagHeaderValue("\"06001234\""));
            using HttpResponseMessage unchanged = await client.SendAsync(conditional);
            Assert.Equal(HttpStatusCode.NotModified, unchanged.StatusCode);

            using HttpResponseMessage missing = await client.GetAsync("icon?did=100663297");
            Assert.Equal(HttpStatusCode.NotFound, missing.StatusCode);
            using HttpResponseMessage bad = await client.GetAsync("icon");
            Assert.Equal(HttpStatusCode.BadRequest, bad.StatusCode);
        }
    }

    [Fact]
    public async Task FloorPlansAreListedAndServedOncePublished()
    {
        (RemoteHttpServer server, _, _) = Start();
        using (server)
        using (HttpClient client = Client(server))
        {
            using JsonDocument none = JsonDocument.Parse(await client.GetStringAsync("maps"));
            Assert.Equal(0, none.RootElement.GetProperty("count").GetInt32());
            using HttpResponseMessage nothingYet = await client.GetAsync("map?lb=6145&layer=0");
            Assert.Equal(HttpStatusCode.NotFound, nothingYet.StatusCode);

            var geometry = new RemoteDungeonGeometry(0x61450000u,
            [
                new RemoteDungeonPolygon(RemoteDungeonSurface.Floor, [new(0f, 0f), new(10f, 0f), new(10f, 10f), new(0f, 10f)], 0f, 0f),
                new RemoteDungeonPolygon(RemoteDungeonSurface.Floor, [new(20f, 0f), new(30f, 0f), new(30f, 10f), new(20f, 10f)], -6f, -6f),
            ]);
            RemoteDungeonMaps maps = RemoteDungeonMapRasterizer.Render(geometry, new DateTime(2026, 9, 16, 17, 0, 0, DateTimeKind.Utc))!;
            server.PublishMaps([maps]);

            using JsonDocument listed = JsonDocument.Parse(await client.GetStringAsync("maps"));
            Assert.Equal("acdream.drakbot.maps/1", listed.RootElement.GetProperty("schema").GetString());
            Assert.Equal(2, listed.RootElement.GetProperty("count").GetInt32());
            JsonElement first = listed.RootElement.GetProperty("maps")[0];
            Assert.Equal("61450000", first.GetProperty("landblock").GetString());
            Assert.Equal(0, first.GetProperty("layer").GetInt32());
            Assert.Equal(-6d, first.GetProperty("z").GetDouble());
            Assert.Equal(maps.Layers[0].Width, first.GetProperty("w").GetInt32());
            Assert.Equal(maps.Layers[0].Height, first.GetProperty("h").GetInt32());
            Assert.Equal(maps.Layers[0].XMin, first.GetProperty("xMin").GetInt32());
            Assert.Equal(maps.Layers[0].YMin, first.GetProperty("yMin").GetInt32());
            Assert.Equal(maps.Layers[0].Png.Length, first.GetProperty("bytes").GetInt32());
            Assert.Equal("2026-09-16T17:00:00.0000000Z", first.GetProperty("mtime").GetString());
            Assert.Equal(0d, listed.RootElement.GetProperty("maps")[1].GetProperty("z").GetDouble());

            // The phone writes the landblock either way round: the status document's eight digits or the top four.
            using HttpResponseMessage upper = await client.GetAsync("map?lb=61450000&layer=1");
            Assert.Equal(HttpStatusCode.OK, upper.StatusCode);
            Assert.Equal("image/png", upper.Content.Headers.ContentType?.MediaType);
            Assert.Equal(maps.Layers[1].Png, await upper.Content.ReadAsByteArrayAsync());
            Assert.Equal(maps.Layers[1].ETag, upper.Headers.ETag?.Tag);
            using HttpResponseMessage lower = await client.GetAsync("map?lb=6145");
            Assert.Equal(maps.Layers[0].Png, await lower.Content.ReadAsByteArrayAsync());

            using var conditional = new HttpRequestMessage(HttpMethod.Get, "map?lb=6145&layer=1");
            conditional.Headers.IfNoneMatch.Add(new EntityTagHeaderValue(maps.Layers[1].ETag));
            using HttpResponseMessage unchanged = await client.SendAsync(conditional);
            Assert.Equal(HttpStatusCode.NotModified, unchanged.StatusCode);

            using HttpResponseMessage noSuchLayer = await client.GetAsync("map?lb=6145&layer=2");
            Assert.Equal(HttpStatusCode.NotFound, noSuchLayer.StatusCode);
            using HttpResponseMessage elsewhere = await client.GetAsync("map?lb=A9B4&layer=0");
            Assert.Equal(HttpStatusCode.NotFound, elsewhere.StatusCode);
            using HttpResponseMessage bad = await client.GetAsync("map?layer=0");
            Assert.Equal(HttpStatusCode.BadRequest, bad.StatusCode);
        }
    }

    [Theory]
    [InlineData("6145", 0x61450000u)]
    [InlineData("61450000", 0x61450000u)]
    [InlineData("61450103", 0x61450000u)]
    [InlineData("a9b4", 0xA9B40000u)]
    public void ALandblockReadsEitherWayRound(string text, uint expected)
    {
        Assert.True(RemoteHttpServer.TryParseLandblock(text, out uint landblock));
        Assert.Equal(expected, landblock);
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("0")]
    [InlineData("dungeon")]
    public void ALandblockThatIsNotOneIsRefused(string? text) =>
        Assert.False(RemoteHttpServer.TryParseLandblock(text, out _));

    [Fact]
    public void ABusyPortIsWalkedPast()
    {
        int port = FreePort();
        var blocker = new TcpListener(IPAddress.Loopback, port);
        blocker.Start();
        try
        {
            var options = new RemoteOptions { Enabled = true, Port = port };
            using var server = new RemoteHttpServer(options, new FakeLogger(), _ => true, null);
            Assert.Null(server.TryStart());
            Assert.NotEqual(port, server.Port);
            Assert.InRange(server.Port, port + 1, port + RemoteOptions.PortSpan - 1);
        }
        finally
        {
            blocker.Stop();
        }
    }

    internal static async Task<string> ReceiveTextAsync(ClientWebSocket socket, CancellationToken cancellation)
    {
        var buffer = new byte[64 * 1024];
        var text = new StringBuilder();
        while (true)
        {
            WebSocketReceiveResult result = await socket.ReceiveAsync(buffer, cancellation);
            if (result.MessageType == WebSocketMessageType.Close)
                throw new InvalidOperationException("the feed closed");
            text.Append(Encoding.UTF8.GetString(buffer, 0, result.Count));
            if (result.EndOfMessage)
                return text.ToString();
        }
    }
}
