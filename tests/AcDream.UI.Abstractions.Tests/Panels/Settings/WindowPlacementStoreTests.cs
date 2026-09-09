using System;
using System.IO;
using AcDream.UI.Abstractions.Panels.Settings;

namespace AcDream.UI.Abstractions.Tests.Panels.Settings;

public sealed class WindowPlacementStoreTests : IDisposable
{
    private readonly string _path = Path.Combine(Path.GetTempPath(), $"window-placement-{Guid.NewGuid():N}.json");
    private static readonly UiWindowLayout Fallback = new(10, 20, 300, 200, true, false, false);

    public void Dispose() => File.Delete(_path);

    [Fact]
    public void LatestPlacementWinsOverAnOlderExactResolutionAndPreservesEveryField()
    {
        var store = new SettingsStore(_path);
        store.SaveWindowLayout("alice", "1920x1080", "chat", Fallback);
        var latest = new UiWindowPlacement(new(42, 84, 501, 251, false, true, true, 7), 2560, 1440);
        store.SaveWindowPlacement("alice", "chat", latest);
        Assert.Equal(latest, store.LoadWindowPlacement("alice", "1920x1080", "chat", Fallback));
        Assert.Equal(latest.Layout, store.LoadWindowLayout("alice", "2560x1440", "chat", Fallback));
        Assert.Equal(Fallback, store.LoadWindowLayout("alice", "1920x1080", "chat", Fallback));
        Assert.Null(store.LoadWindowPlacement("bob", "1920x1080", "chat", Fallback));
    }

    [Fact]
    public void LegacyNearestLayoutRetainsSourceDimensionsAndCharacterIsolation()
    {
        var store = new SettingsStore(_path);
        store.SaveWindowLayout("alice", "1280x720", "chat", Fallback with { X = 90 });
        store.SaveWindowLayout("alice", "2560x1440", "chat", Fallback with { X = 180 });
        store.SaveWindowLayout("bob", "1600x900", "chat", Fallback with { X = 999 });
        Assert.Equal(new UiWindowPlacement(Fallback with { X = 90 }, 1280, 720),
            store.LoadWindowPlacement("alice", "1600x900", "chat", Fallback));
    }

    [Fact]
    public void LegacyPositionUsesRequestedDimensionsAndFallbackState()
    {
        var store = new SettingsStore(_path);
        store.SaveWindowPosition("alice", "radar", new(75, 95));
        Assert.Equal(new UiWindowPlacement(Fallback with { X = 75, Y = 95 }, 1920, 1080),
            store.LoadWindowPlacement("alice", "1920x1080", "radar", Fallback));
        Assert.Null(store.LoadWindowPlacement("bob", "1920x1080", "radar", Fallback));
    }

    [Theory]
    [InlineData("{")]
    [InlineData("[]")]
    [InlineData("{\"windowPlacements\":17,\"windowLayouts\":[]}")]
    [InlineData("{\"windowPlacements\":{\"alice\":{\"chat\":{\"screenWidth\":0,\"screenHeight\":1080,\"layout\":{}}}}}")]
    public void MalformedOrInvalidPlacementDoesNotThrow(string json)
    {
        File.WriteAllText(_path, json);
        Assert.Null(new SettingsStore(_path).LoadWindowPlacement("alice", "1920x1080", "chat", Fallback));
    }

    [Fact]
    public void InvalidCanonicalEntryFallsBackToLegacyAndMalformedFieldsUseDefaults()
    {
        File.WriteAllText(_path, """
            {"windowPlacements":{"alice":{"chat":{"screenWidth":"bad","screenHeight":1080,"layout":{}}}},
             "windowLayouts":{"alice":{"1280x720":{"chat":{"x":"bad","y":42,"visible":"bad"}}}}}
            """);
        Assert.Equal(new UiWindowPlacement(Fallback with { Y = 42 }, 1280, 720),
            new SettingsStore(_path).LoadWindowPlacement("alice", "1920x1080", "chat", Fallback));
    }

    [Fact]
    public void InvalidRequestedDimensionsReturnNullAndInvalidSaveDoesNotWrite()
    {
        var store = new SettingsStore(_path);
        Assert.Null(store.LoadWindowPlacement("alice", "bad", "chat", Fallback));
        Assert.Throws<ArgumentOutOfRangeException>(() => store.SaveWindowPlacement("alice", "chat", new(Fallback, 0, 1080)));
        Assert.False(File.Exists(_path));
    }
}
