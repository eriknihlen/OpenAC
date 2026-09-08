using AcDream.App.Plugins;
using AcDream.Plugin.Abstractions;

namespace AcDream.App.Tests.Plugins;

public sealed class LocalPluginPeerRegistryTests
{
    [Fact]
    public void PublishesRemoteClientsIgnoresSelfAndExpiresStaleHeartbeat()
    {
        string root = Path.Combine(
            Path.GetTempPath(),
            $"acdream-plugin-peers-{Guid.NewGuid():N}");
        var time = new ManualTimeProvider(
            new DateTimeOffset(2026, 8, 27, 12, 0, 0, TimeSpan.Zero));
        try
        {
            using var first = new LocalPluginPeerRegistry(
                root,
                time,
                Guid.Parse("11111111-1111-1111-1111-111111111111"));
            using var second = new LocalPluginPeerRegistry(
                root,
                time,
                Guid.Parse("22222222-2222-2222-2222-222222222222"));
            first.Publish(Client(first.ClientId, 10u, "Alpha", ["one"]));
            second.Publish(Client(second.ClientId, 20u, "Beta", ["two"]));

            PluginNetworkClient remote = Assert.Single(
                first.CaptureRemoteClients());
            Assert.Equal(second.ClientId, remote.ClientId);
            Assert.Equal("Beta", remote.Name);
            Assert.Equal(["two"], remote.Tags);
            Assert.Equal(33.5d, remote.Position.EastWest);

            time.Advance(LocalPluginPeerRegistry.StaleAfter
                + TimeSpan.FromMilliseconds(1));
            Assert.Empty(first.CaptureRemoteClients());
        }
        finally
        {
            if (Directory.Exists(root))
                Directory.Delete(root, recursive: true);
        }
    }

    private static PluginNetworkClient Client(
        uint clientId,
        uint playerId,
        string name,
        IReadOnlyList<string> tags) => new(
            clientId,
            playerId,
            name,
            "Coldeve",
            new PluginNavigationPosition(
                0x7F7F0001u, 33.5d, -72.8d, 1d, 90f, true),
            tags,
            90u,
            70u,
            80u,
            100u,
            100u,
            100u,
            90f);

    private sealed class ManualTimeProvider(DateTimeOffset utcNow) : TimeProvider
    {
        private DateTimeOffset _utcNow = utcNow;
        public override DateTimeOffset GetUtcNow() => _utcNow;
        public void Advance(TimeSpan elapsed) => _utcNow += elapsed;
    }
}
