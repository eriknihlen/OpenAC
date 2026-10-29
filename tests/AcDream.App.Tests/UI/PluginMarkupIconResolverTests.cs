using System.Collections.Generic;
using AcDream.App.Plugins;
using AcDream.App.UI;
using AcDream.Tests.Fixtures.PluginIcons;

namespace AcDream.App.Tests.UI;

/// <summary>
/// One plugin window's file icons: read in the background, held in the
/// plugin's image table for as long as the window holds them, and let go
/// when the window goes, without taking them from another window.
/// </summary>
public sealed class PluginMarkupIconResolverTests : IDisposable
{
    private readonly string _root = Path.Combine(Path.GetTempPath(), $"acdream-markup-icons-{Guid.NewGuid():N}");
    private readonly List<string> _reports = [];
    private readonly Backend _backend = new();
    private readonly PluginImageTable _table;
    private readonly List<(Func<PluginFileImageResult> Read, TaskCompletionSource<PluginFileImageResult> Done)> _held = [];
    private bool _holdReads;
    private long _now = 1_000_000;

    public PluginMarkupIconResolverTests()
    {
        Directory.CreateDirectory(Path.Combine(_root, "icons"));
        File.WriteAllBytes(Path.Combine(_root, "icons", "sword.png"), PngTestData.Sized(20, 10));
        _table = new PluginImageTable("acme.hello", PluginImageBudget.Default, _reports.Add);
        _table.Bind(_backend, Environment.CurrentManagedThreadId);
    }

    public void Dispose()
    {
        _table.Dispose();
        Directory.Delete(_root, recursive: true);
    }

    private sealed class Retail : IMarkupIconResolver
    {
        public (uint tex, int w, int h) ResolveDid(uint did) => (1u, 32, 32);

        public (uint tex, int w, int h) ResolveSpell(uint spellId) => (2u, 32, 32);

        public (uint tex, int w, int h) ResolveItem(uint objectId) => (3u, 32, 32);
    }

    private sealed class Backend : IPluginImageBackend
    {
        private uint _next = 500u;

        public List<(uint Texture, int Width, int Height)> Uploads { get; } = [];

        public List<uint> Released { get; } = [];

        public bool TryGetClientArt(uint surfaceId, out uint texture, out int width, out int height) =>
            None(out texture, out width, out height);

        public bool TryGetSpellIcon(uint spellId, out uint texture, out int width, out int height) =>
            None(out texture, out width, out height);

        public bool TryGetObjectIcon(uint objectId, out uint texture, out int width, out int height) =>
            None(out texture, out width, out height);

        public uint UploadOwned(byte[] rgba, int width, int height, string debugName)
        {
            uint texture = ++_next;
            Uploads.Add((texture, width, height));
            return texture;
        }

        public bool ReleaseOwned(uint texture)
        {
            Released.Add(texture);
            return true;
        }

        private static bool None(out uint texture, out int width, out int height)
        {
            (texture, width, height) = (0u, 0, 0);
            return false;
        }
    }

    private PluginFileImageLoader Loader() => new(_reports.Add, Run);

    private Task<PluginFileImageResult> Run(Func<PluginFileImageResult> read)
    {
        if (!_holdReads)
            return Task.FromResult(read());
        var done = new TaskCompletionSource<PluginFileImageResult>();
        _held.Add((read, done));
        return done.Task;
    }

    private void FinishReads()
    {
        foreach ((Func<PluginFileImageResult> read, TaskCompletionSource<PluginFileImageResult> done) in _held)
            done.TrySetResult(read());
        _held.Clear();
    }

    private PluginMarkupIconResolver Resolver(PluginFileImageLoader loader) =>
        new(new Retail(), loader, () => _table, "acme.hello", _root, () => _now);

    [Fact]
    public void ClientIconsGoToTheRetailResolver()
    {
        using PluginMarkupIconResolver resolver = Resolver(Loader());

        Assert.Equal(1u, resolver.ResolveDid(9u).tex);
        Assert.Equal(2u, resolver.ResolveSpell(9u).tex);
        Assert.Equal(3u, resolver.ResolveItem(9u).tex);
    }

    [Fact]
    public void AFileDrawsNothingWhileItIsReadAndDrawsOnceItIsReady()
    {
        _holdReads = true;
        using PluginMarkupIconResolver resolver = Resolver(Loader());

        Assert.Equal((0u, 0, 0), resolver.ResolveFile("icons/sword.png"));
        Assert.Empty(_backend.Uploads);

        FinishReads();

        Assert.Equal((501u, 20, 10), resolver.ResolveFile("icons/sword.png"));
    }

    [Fact]
    public void AFileIsReadAndUploadedOnceHoweverOftenItIsDrawn()
    {
        int reads = 0;
        var loader = new PluginFileImageLoader(_reports.Add, read =>
        {
            reads++;
            return Task.FromResult(read());
        });
        using PluginMarkupIconResolver resolver = Resolver(loader);

        Assert.Equal((501u, 20, 10), resolver.ResolveFile("icons/sword.png"));
        Assert.Equal((501u, 20, 10), resolver.ResolveFile("icons/sword.png"));

        Assert.Equal(1, reads);
        Assert.Single(_backend.Uploads);
        Assert.Equal(1, _table.Count);
    }

    [Fact]
    public void TwoSpellingsOfOneFileAreOneImage()
    {
        using PluginMarkupIconResolver resolver = Resolver(Loader());

        Assert.Equal((501u, 20, 10), resolver.ResolveFile("icons/sword.png"));
        Assert.Equal((501u, 20, 10), resolver.ResolveFile("./icons/sword.png"));
        Assert.Equal((501u, 20, 10), resolver.ResolveFile(@"icons\sword.png"));

        Assert.Single(_backend.Uploads);
        Assert.Equal(1, _table.Count);
    }

    [Fact]
    public void AFileIsUploadedWithItsSmallerLevelsInThePluginsBudget()
    {
        using PluginMarkupIconResolver resolver = Resolver(Loader());

        resolver.ResolveFile("icons/sword.png");

        // 20x10, 10x5, 5x2, 2x1, 1x1.
        Assert.Equal((20L * 10 + 10 * 5 + 5 * 2 + 2 * 1 + 1) * 4, _table.OwnedBytes);
    }

    [Fact]
    public void AMissingFileIsLookedUpOnceAndReportedOnce()
    {
        int reads = 0;
        var loader = new PluginFileImageLoader(_reports.Add, read =>
        {
            reads++;
            return Task.FromResult(read());
        });
        using PluginMarkupIconResolver resolver = Resolver(loader);

        Assert.Equal((0u, 0, 0), resolver.ResolveFile("icons/gone.png"));
        Assert.Equal((0u, 0, 0), resolver.ResolveFile("icons/gone.png"));

        Assert.Equal(1, reads);
        string report = Assert.Single(_reports);
        Assert.Contains("acme.hello/icons/gone.png", report, StringComparison.Ordinal);
        Assert.Contains("does not exist", report, StringComparison.Ordinal);
    }

    [Fact]
    public void AWindowWithNoPluginFolderDrawsNoFilesAndSaysWhyOnce()
    {
        using var resolver = new PluginMarkupIconResolver(new Retail(), Loader(), () => _table, "acme.hello", null);

        Assert.Equal((0u, 0, 0), resolver.ResolveFile("icons/sword.png"));
        Assert.Equal((0u, 0, 0), resolver.ResolveFile("icons/sword.png"));

        Assert.Contains("no plugin folder", Assert.Single(_reports), StringComparison.Ordinal);
        Assert.Empty(_backend.Uploads);
    }

    [Fact]
    public void AnUnmountedWindowLetsGoOfItsImages()
    {
        PluginMarkupIconResolver resolver = Resolver(Loader());
        resolver.ResolveFile("icons/sword.png");

        resolver.Dispose();
        resolver.Dispose();

        Assert.Equal([501u], _backend.Released);
        Assert.Equal(0, _table.Count);
        Assert.Equal(0L, _table.OwnedBytes);
        Assert.Equal((0u, 0, 0), resolver.ResolveFile("icons/sword.png"));
    }

    [Fact]
    public void AWindowUnmountedWhileItsFileIsReadNeverHoldsIt()
    {
        _holdReads = true;
        PluginMarkupIconResolver resolver = Resolver(Loader());
        resolver.ResolveFile("icons/sword.png");

        resolver.Dispose();
        FinishReads();
        resolver.ResolveFile("icons/sword.png");

        Assert.Empty(_backend.Uploads);
        Assert.Equal(0, _table.Count);
    }

    [Fact]
    public void TwoWindowsShowingAFileThatChangesEachKeepDrawingUntilRebuilt()
    {
        PluginFileImageLoader loader = Loader();
        PluginMarkupIconResolver first = Resolver(loader);
        using PluginMarkupIconResolver second = Resolver(loader);
        Assert.Equal((501u, 20, 10), first.ResolveFile("icons/sword.png"));
        Assert.Equal((501u, 20, 10), second.ResolveFile("icons/sword.png"));

        string path = Path.Combine(_root, "icons", "sword.png");
        File.WriteAllBytes(path, PngTestData.Sized(40, 20));
        File.SetLastWriteTimeUtc(path, DateTime.UtcNow.AddMinutes(1));

        // The first window is rebuilt: its old resolver goes, a new one reads the new file.
        first.Dispose();
        using PluginMarkupIconResolver rebuilt = Resolver(loader);

        Assert.Equal((502u, 40, 20), rebuilt.ResolveFile("icons/sword.png"));
        Assert.Equal((501u, 20, 10), second.ResolveFile("icons/sword.png"));
        Assert.Empty(_backend.Released);
    }

    [Fact]
    public void FileIconsAreRefusedPastThePluginsImageCount()
    {
        File.WriteAllBytes(Path.Combine(_root, "icons", "shield.png"), PngTestData.Sized(4, 4));
        using var table = new PluginImageTable(
            "acme.hello",
            new PluginImageBudget(MaximumCount: 1, MaximumBytes: 1024 * 1024, MaximumDimension: 2048),
            _reports.Add);
        table.Bind(_backend, Environment.CurrentManagedThreadId);
        using var resolver = new PluginMarkupIconResolver(new Retail(), Loader(), () => table, "acme.hello", _root);

        Assert.NotEqual(0u, resolver.ResolveFile("icons/sword.png").tex);
        Assert.Equal((0u, 0, 0), resolver.ResolveFile("icons/shield.png"));

        Assert.Contains(_reports, line => line.Contains("1 images", StringComparison.Ordinal));
    }

    [Fact]
    public void ABoundIconRunningThroughManyFilesKeepsOnlyRecentOnesHeld()
    {
        // A small budget, so a window holding every file it ever drew would fill it.
        using var table = new PluginImageTable(
            "acme.hello",
            new PluginImageBudget(MaximumCount: 8, MaximumBytes: 1024 * 1024, MaximumDimension: 2048),
            _reports.Add);
        table.Bind(_backend, Environment.CurrentManagedThreadId);
        using var resolver = new PluginMarkupIconResolver(
            new Retail(), Loader(), () => table, "acme.hello", _root, () => _now);
        for (int i = 0; i < 40; i++)
            File.WriteAllBytes(Path.Combine(_root, "icons", $"status{i}.png"), PngTestData.Sized(4, 4));

        // The bound path names a new file every second, and the window draws it every frame.
        for (int i = 0; i < 40; i++)
        {
            Assert.NotEqual(0u, resolver.ResolveFile($"icons/status{i}.png").tex);
            _now += PluginMarkupIconResolver.ReleaseCheckIntervalMilliseconds;
            resolver.ReleaseUndrawn();
            Assert.True(table.Count <= 6, $"the table holds {table.Count} images after file {i}");
        }

        Assert.DoesNotContain(_reports, line => line.Contains("refused", StringComparison.Ordinal));
        Assert.True(resolver.RememberedFileCount <= 6);
    }

    [Fact]
    public void AFileLetGoForNotBeingDrawnIsReadAgainWhenDrawnAgain()
    {
        int reads = 0;
        var loader = new PluginFileImageLoader(_reports.Add, read =>
        {
            reads++;
            return Task.FromResult(read());
        });
        using PluginMarkupIconResolver resolver = Resolver(loader);
        Assert.Equal((501u, 20, 10), resolver.ResolveFile("icons/sword.png"));

        // Drawn recently: kept.
        _now += PluginMarkupIconResolver.ReleaseAfterMilliseconds - 1;
        resolver.ReleaseUndrawn();
        Assert.Equal(1, _table.Count);
        Assert.Empty(_backend.Released);

        // Not drawn for the whole wait: let go.
        _now += PluginMarkupIconResolver.ReleaseCheckIntervalMilliseconds;
        resolver.ReleaseUndrawn();
        Assert.Equal([501u], _backend.Released);
        Assert.Equal(0, _table.Count);
        Assert.Equal(0, resolver.RememberedFileCount);

        Assert.Equal((502u, 20, 10), resolver.ResolveFile("icons/sword.png"));
        Assert.Equal(2, reads);
        Assert.Equal(1, _table.Count);
    }

    [Fact]
    public void AFileRefusedForWantOfRoomDrawsOnceAnotherIsLetGo()
    {
        File.WriteAllBytes(Path.Combine(_root, "icons", "shield.png"), PngTestData.Sized(4, 4));
        using var table = new PluginImageTable(
            "acme.hello",
            new PluginImageBudget(MaximumCount: 1, MaximumBytes: 1024 * 1024, MaximumDimension: 2048),
            _reports.Add);
        table.Bind(_backend, Environment.CurrentManagedThreadId);
        using var resolver = new PluginMarkupIconResolver(
            new Retail(), Loader(), () => table, "acme.hello", _root, () => _now);
        Assert.NotEqual(0u, resolver.ResolveFile("icons/sword.png").tex);
        Assert.Equal((0u, 0, 0), resolver.ResolveFile("icons/shield.png"));

        // The sword stops being drawn; the shield goes on being drawn.
        for (long waited = 0; waited <= PluginMarkupIconResolver.ReleaseAfterMilliseconds;
             waited += PluginMarkupIconResolver.ReleaseCheckIntervalMilliseconds)
        {
            _now += PluginMarkupIconResolver.ReleaseCheckIntervalMilliseconds;
            resolver.ResolveFile("icons/shield.png");
            resolver.ReleaseUndrawn();
        }

        Assert.Equal((502u, 4, 4), resolver.ResolveFile("icons/shield.png"));
        Assert.Equal(1, table.Count);
    }

    [Fact]
    public void AFileRefusedForWantOfRoomDrawsOnceAnotherWindowGoes()
    {
        File.WriteAllBytes(Path.Combine(_root, "icons", "shield.png"), PngTestData.Sized(4, 4));
        using var table = new PluginImageTable(
            "acme.hello",
            new PluginImageBudget(MaximumCount: 1, MaximumBytes: 1024 * 1024, MaximumDimension: 2048),
            _reports.Add);
        table.Bind(_backend, Environment.CurrentManagedThreadId);
        PluginFileImageLoader loader = Loader();
        var first = new PluginMarkupIconResolver(new Retail(), loader, () => table, "acme.hello", _root, () => _now);
        using var second = new PluginMarkupIconResolver(new Retail(), loader, () => table, "acme.hello", _root, () => _now);
        Assert.NotEqual(0u, first.ResolveFile("icons/sword.png").tex);
        Assert.Equal((0u, 0, 0), second.ResolveFile("icons/shield.png"));

        first.Dispose();

        Assert.Equal((502u, 4, 4), second.ResolveFile("icons/shield.png"));
    }

    [Fact]
    public void AnImageThePluginsTableLetGoOfDrawsNothing()
    {
        using PluginMarkupIconResolver resolver = Resolver(Loader());
        resolver.ResolveFile("icons/sword.png");

        _table.Unbind();

        Assert.Equal((0u, 0, 0), resolver.ResolveFile("icons/sword.png"));
    }
}
