using System.Net;
using System.Text;
using AcDream.Launcher.Core.Status;

namespace AcDream.Launcher.Core.Tests.Status;

public sealed class KnownServerCatalogTests : IDisposable
{
    private readonly string _cache = Path.Combine(Path.GetTempPath(), "acdream-known-servers", Guid.NewGuid().ToString("N"));

    public void Dispose()
    {
        if (Directory.Exists(_cache))
        {
            Directory.Delete(_cache, recursive: true);
        }
    }

    private const string ServersJson = """
        [
          { "name": "Coldeve", "description": "A PvE server", "type": "PvE", "software": "ACE",
            "host": "play.coldeve.ac", "port": "9000", "website_url": "https://coldeve.ac/", "discord_url": "https://discord.gg/x" },
          { "name": "AChard", "type": "PvP", "software": "ACE", "host": "a-chard.ddns.net", "port": "9000",
            "website_url": "javascript:alert(1)",
            "players": { "count": 12, "updated_at": "2026-09-19 18:14:26 UTC", "age": "a week ago" } },
          { "name": "NoPort", "host": "example.org" },
          { "name": "BadPort", "host": "example.org", "port": "99999" },
          { "host": "nameless.example", "port": "9000" },
          { "name": "Numeric", "host": "numeric.example", "port": 9050, "unexpected": [1, 2] }
        ]
        """;

    private const string CountsJson = """[{ "server": "coldeve", "count": 675 }, { "server": "AChard", "count": 3 }]""";

    [Fact]
    public void ParseKeepsUsableServersSortedByNameWithTheirCountsAndLinks()
    {
        IReadOnlyList<KnownServer> servers = KnownServerCatalog.Parse(ServersJson, CountsJson);

        Assert.Equal(["AChard", "Coldeve", "Numeric"], servers.Select(server => server.Name));
        KnownServer coldeve = servers[1];
        Assert.Equal(("play.coldeve.ac", 9000, "PvE", "ACE", "A PvE server"),
            (coldeve.Host, coldeve.Port, coldeve.Type, coldeve.Software, coldeve.Description));
        Assert.Equal(new Uri("https://coldeve.ac/"), coldeve.Website);
        Assert.Equal(new Uri("https://discord.gg/x"), coldeve.Discord);
        Assert.Equal(675, coldeve.PlayerCount);
        Assert.Equal(3, servers[0].PlayerCount);
        Assert.Null(servers[0].Website);
        Assert.Equal(9050, servers[2].Port);
        Assert.Null(servers[2].PlayerCount);
    }

    [Fact]
    public void WithoutCountsAServerKeepsTheCountItsOwnEntryReports()
    {
        IReadOnlyList<KnownServer> servers = KnownServerCatalog.Parse(ServersJson, playerCountsJson: "not json");

        Assert.Equal(12, servers.Single(server => server.Name == "AChard").PlayerCount);
        Assert.Null(servers.Single(server => server.Name == "Coldeve").PlayerCount);
    }

    [Fact]
    public async Task AFetchedListIsSavedAndStillOpensOffline()
    {
        var online = new Handler(request => request.RequestUri == KnownServerCatalog.ServersUri ? ServersJson : CountsJson);
        using (var client = new HttpClient(online))
        {
            KnownServerList? fetched = await new KnownServerCatalog(client, _cache).LoadAsync();
            Assert.NotNull(fetched);
            Assert.False(fetched.IsFromCache);
        }

        using var offlineClient = new HttpClient(new Handler(_ => throw new HttpRequestException("offline")));
        KnownServerList? cached = await new KnownServerCatalog(offlineClient, _cache).LoadAsync();

        Assert.NotNull(cached);
        Assert.True(cached.IsFromCache);
        Assert.Equal(675, cached.Servers.Single(server => server.Name == "Coldeve").PlayerCount);
    }

    [Fact]
    public async Task OfflineWithNothingSavedThereIsNoList()
    {
        using var client = new HttpClient(new Handler(_ => throw new HttpRequestException("offline")));

        Assert.Null(await new KnownServerCatalog(client, _cache).LoadAsync());
    }

    [Fact]
    public async Task AnUnreadableResponseNeverReplacesTheSavedList()
    {
        using (var client = new HttpClient(new Handler(request => request.RequestUri == KnownServerCatalog.ServersUri ? ServersJson : CountsJson)))
        {
            await new KnownServerCatalog(client, _cache).LoadAsync();
        }

        using var broken = new HttpClient(new Handler(_ => "<html>maintenance</html>"));
        KnownServerList? list = await new KnownServerCatalog(broken, _cache).LoadAsync();

        Assert.NotNull(list);
        Assert.True(list.IsFromCache);
        Assert.Equal(3, list.Servers.Count);
    }

    [Fact]
    public async Task ARecentListIsReusedWithoutAnotherFetch()
    {
        int requests = 0;
        using var client = new HttpClient(new Handler(request =>
        {
            requests++;
            return request.RequestUri == KnownServerCatalog.ServersUri ? ServersJson : CountsJson;
        }));
        var catalog = new KnownServerCatalog(client, _cache);

        await catalog.LoadAsync();
        await catalog.LoadAsync();

        Assert.Equal(2, requests);
    }

    private sealed class Handler(Func<HttpRequestMessage, string> respond) : HttpMessageHandler
    {
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken) =>
            Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK)
            {
                Content = new StringContent(respond(request), Encoding.UTF8, "application/json"),
            });
    }
}
