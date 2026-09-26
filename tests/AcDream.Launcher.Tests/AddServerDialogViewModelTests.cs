using System.Globalization;
using AcDream.Launcher.Core.Orchestration;
using AcDream.Launcher.Core.Profiles;
using AcDream.Launcher.Core.Status;
using AcDream.Launcher.ViewModels;
using AcDream.Platform;

namespace AcDream.Launcher.Tests;

public sealed class AddServerDialogViewModelTests : IDisposable
{
    private readonly string _root = Path.Combine(Path.GetTempPath(), "acdream-tests", Guid.NewGuid().ToString("N"));
    private readonly LauncherProfileStore _store;
    private readonly LauncherOrchestrator _core;
    private readonly AddServerDialogViewModel _dialog;

    public AddServerDialogViewModelTests()
    {
        var paths = new ApplicationPathSet(Path.Combine(_root, "config"), Path.Combine(_root, "data"), Path.Combine(_root, "cache"));
        _store = LauncherProfileStore.ForApplicationPaths(paths);
        _core = new LauncherOrchestrator(_store, paths, new LauncherExecutableSet("unused-client", "unused-headless"));
        _core.LoadProfiles();
        _dialog = new AddServerDialogViewModel(_core);
    }

    public void Dispose()
    {
        _core.Dispose();
        if (Directory.Exists(_root)) Directory.Delete(_root, recursive: true);
    }

    private static KnownServerList List(bool fromCache = false) => new(
    [
        new KnownServer("AChard", "a-chard.ddns.net", 9000, "PvP", "ACE", "PK server", null, null, 3),
        new KnownServer("Coldeve", "play.coldeve.ac", 9000, "PvE", "ACE", "End of retail", new Uri("https://coldeve.ac/"), null, 675),
        new KnownServer("Leafcull", "leafcull.example", 9010, "PvE", "GDLE", "Retail-like", null, null, null),
    ], fromCache, new DateTimeOffset(2026, 9, 20, 12, 0, 0, TimeSpan.Zero));

    [Fact]
    public void KnownServersShowTheirTypePlayersAndWhetherTheyAreAlreadyAdded()
    {
        _core.AddServer("coldeve", "PLAY.coldeve.ac", 9000);

        _dialog.ShowList(List());

        Assert.Equal("3 servers · list and player counts from TreeStats", _dialog.ListStatus);
        Assert.Equal(["All types", "PvP", "PvE"], _dialog.Types);
        Assert.Equal(["AChard", "Coldeve", "Leafcull"], _dialog.Servers.Select(row => row.Name));
        KnownServerRowViewModel coldeve = _dialog.Servers[1];
        Assert.Equal(("play.coldeve.ac:9000", "PvE", "675"), (coldeve.Endpoint, coldeve.Type, coldeve.Players));
        Assert.True(coldeve.IsAdded);
        Assert.False(coldeve.AddCommand.CanExecute(null));
        Assert.Equal("—", _dialog.Servers[2].Players);
        Assert.False(_dialog.Servers[0].IsAdded);
    }

    [Fact]
    public void AddingAKnownServerSavesItAndMarksItAdded()
    {
        _dialog.ShowList(List());
        KnownServerRowViewModel leafcull = _dialog.Servers.Single(row => row.Name == "Leafcull");

        leafcull.AddCommand.Execute(null);

        ServerProfile saved = Assert.Single(_store.Document.Servers);
        Assert.Equal(("Leafcull", "leafcull.example", 9010), (saved.Name, saved.Host, saved.Port));
        Assert.True(leafcull.IsAdded);
        Assert.Equal("Added Leafcull. Add your accounts on it with Accounts.", _dialog.Notice);
    }

    [Fact]
    public void SearchAndTypeNarrowTheList()
    {
        _dialog.ShowList(List());

        _dialog.SelectedType = "PvE";
        Assert.Equal(["Coldeve", "Leafcull"], _dialog.Servers.Select(row => row.Name));
        _dialog.Search = "retail-like";
        Assert.Equal(["Leafcull"], _dialog.Servers.Select(row => row.Name));
        _dialog.Search = "coldeve.ac";
        Assert.Equal(["Coldeve"], _dialog.Servers.Select(row => row.Name));
    }

    [Fact]
    public void OfflineOnlyYourOwnServerCanBeAdded()
    {
        _dialog.ShowList(null);

        Assert.Empty(_dialog.Servers);
        Assert.False(_dialog.HasKnownServers);
        Assert.StartsWith("Offline", _dialog.ListStatus);

        _dialog.Name = "My local ACE";
        _dialog.Host = "127.0.0.1";
        _dialog.Port = "70000";
        _dialog.AddOwnCommand.Execute(null);
        Assert.Equal("The port is a number from 1 to 65535.", _dialog.Error);
        Assert.Empty(_store.Document.Servers);

        _dialog.Port = "9000";
        _dialog.AddOwnCommand.Execute(null);
        Assert.Null(_dialog.Error);
        Assert.Equal(("My local ACE", "127.0.0.1", 9000), (_store.Document.Servers[0].Name, _store.Document.Servers[0].Host, _store.Document.Servers[0].Port));
    }

    [Fact]
    public void AnOfflineCopySaysWhenItWasSavedTheSameWayInEveryLanguage()
    {
        CultureInfo before = CultureInfo.CurrentCulture;
        try
        {
            CultureInfo.CurrentCulture = new CultureInfo("sv-SE");
            _dialog.ShowList(List(fromCache: true));
        }
        finally
        {
            CultureInfo.CurrentCulture = before;
        }

        Assert.EndsWith("offline copy from Sep 20, 2026", _dialog.ListStatus);
    }

    [Fact]
    public async Task AListThatFailsToLoadLeavesYourOwnServerAndNeverThrows()
    {
        using var client = new HttpClient(new ThrowingHandler());
        _dialog.UseCatalog(new KnownServerCatalog(client, Path.Combine(_root, "cache")));

        await _dialog.OpenAsync();

        Assert.True(_dialog.IsOpen);
        Assert.Empty(_dialog.Servers);
        Assert.StartsWith("The known server list could not be loaded", _dialog.ListStatus);
        Assert.False(_dialog.IsLoading);
    }

    private sealed class ThrowingHandler : HttpMessageHandler
    {
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken) =>
            throw new InvalidOperationException("unexpected");
    }

    [Fact]
    public void AServerNameAlreadyTakenIsRefusedWithAReason()
    {
        _core.AddServer("Coldeve", "other.example", 9001);
        _dialog.ShowList(List());
        KnownServerRowViewModel coldeve = _dialog.Servers.Single(row => row.Name == "Coldeve");

        Assert.True(coldeve.IsAdded);
        _dialog.Name = "Coldeve";
        _dialog.Host = "somewhere.example";
        _dialog.AddOwnCommand.Execute(null);

        Assert.Equal("A server named 'Coldeve' already exists.", _dialog.Error);
        Assert.Single(_store.Document.Servers);
    }
}
