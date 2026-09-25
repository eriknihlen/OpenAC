using AcDream.Plugin.Abstractions;
using AcDream.Runtime.Plugins;

namespace AcDream.Runtime.Tests.Plugins;

/// <summary>
/// A plugin with a transport of its own carries the peer bus between
/// computers: it reads what this client tells its neighbours and imports
/// what arrives from elsewhere. An imported peer has to be held to exactly
/// the rules a neighbour's note is read under, or a relay could keep a dead
/// character alive, run a line aimed at somebody else, or count one cast
/// twice.
/// </summary>
public sealed class LocalPluginPeerRelayTests
{
    private const uint Own = 10u;
    private const uint Remote = 30u;

    [Fact]
    public void AnImportedPeerIsListedAsRemoteKeepsItsIdAndGoesStale()
    {
        string root = TemporaryRoot();
        var time = new ManualTimeProvider(Start);
        try
        {
            using var reader = Registry(root, time, 1);

            Assert.True(reader.ImportRemoteClient(Client(999u, Remote, "Gamma"), Own));
            PluginNetworkClient imported = Assert.Single(reader.CaptureRemoteClients(Own));
            Assert.True(imported.IsRemote);
            Assert.Equal(Remote, imported.PlayerId);
            Assert.Equal("Gamma", imported.Name);
            Assert.Equal(["healer"], imported.Tags);
            Assert.Equal(90u, imported.CurrentHealth);
            // The relay's own number means nothing here: the reader gives the
            // peer one of its own.
            Assert.NotEqual(999u, imported.ClientId);
            Assert.NotEqual(0u, imported.ClientId);
            Assert.NotEqual(reader.ClientId, imported.ClientId);

            time.Advance(TimeSpan.FromSeconds(10));
            Assert.True(reader.ImportRemoteClient(
                Client(0u, Remote, "Gamma") with { CurrentHealth = 40u }, Own));
            PluginNetworkClient refreshed = Assert.Single(reader.CaptureRemoteClients(Own));
            Assert.Equal(imported.ClientId, refreshed.ClientId);
            Assert.Equal(40u, refreshed.CurrentHealth);

            time.Advance(LocalPluginPeerRegistry.StaleAfter + TimeSpan.FromMilliseconds(1));
            Assert.Empty(reader.CaptureRemoteClients(Own));
        }
        finally
        {
            Delete(root);
        }
    }

    [Fact]
    public void AnImportIsRefusedWhereANoteWouldBe()
    {
        string root = TemporaryRoot();
        var time = new ManualTimeProvider(Start);
        try
        {
            using var reader = Registry(root, time, 1);
            PluginNetworkClient good = Client(0u, Remote, "Gamma");

            Assert.False(reader.ImportRemoteClient(good with { PlayerId = 0u }, Own));
            Assert.False(reader.ImportRemoteClient(good with { PlayerId = Own }, Own));
            Assert.False(reader.ImportRemoteClient(good with { Name = " " }, Own));
            Assert.False(reader.ImportRemoteClient(good with { Name = new string('n', 129) }, Own));
            Assert.False(reader.ImportRemoteClient(good with { WorldName = null! }, Own));
            Assert.False(reader.ImportRemoteClient(good with { Heading = float.NaN }, Own));
            Assert.False(reader.ImportRemoteClient(
                good with
                {
                    Position = good.Position with { EastWest = double.PositiveInfinity },
                },
                Own));
            Assert.False(reader.ImportRemoteClient(
                good with { Tags = [new string('t', 65)] }, Own));
            Assert.Empty(reader.CaptureRemoteClients(Own));

            // A cast or a line from somebody never imported is refused.
            Assert.False(reader.ImportRemoteCast(Remote, 0x50000012u, 42u, 300, 30d, true));
            Assert.False(reader.ImportRemoteCommand(Remote, [], "/go", 0));
        }
        finally
        {
            Delete(root);
        }
    }

    /// <summary>
    /// The cap on imported peers is a ceiling on live ones: a stale peer gives
    /// its place up rather than locking a long-running relay out.
    /// </summary>
    [Fact]
    public void TheImportCapCountsOnlyPeersThatAreStillRecent()
    {
        string root = TemporaryRoot();
        var time = new ManualTimeProvider(Start);
        try
        {
            using var reader = Registry(root, time, 1);
            for (uint index = 0u; index < LocalPluginPeerRegistry.MaximumRemoteClients; index++)
                Assert.True(reader.ImportRemoteClient(Client(0u, 1000u + index, "P" + index), Own));
            Assert.False(reader.ImportRemoteClient(Client(0u, 5000u, "Late"), Own));
            // Refreshing one already held is not a new peer.
            Assert.True(reader.ImportRemoteClient(Client(0u, 1000u, "P0"), Own));

            time.Advance(LocalPluginPeerRegistry.StaleAfter + TimeSpan.FromSeconds(1));
            Assert.True(reader.ImportRemoteClient(Client(0u, 5000u, "Late"), Own));
            Assert.Equal("Late", Assert.Single(reader.CaptureRemoteClients(Own)).Name);
        }
        finally
        {
            Delete(root);
        }
    }

    /// <summary>
    /// A character that a neighbour's note already speaks for is read from
    /// the note: arriving by two routes it would otherwise be listed twice
    /// and have every cast counted twice.
    /// </summary>
    [Fact]
    public void ACharacterSeenOnThisComputerIsNotAlsoListedAsRemote()
    {
        string root = TemporaryRoot();
        var time = new ManualTimeProvider(Start);
        try
        {
            using var reader = Registry(root, time, 1);
            using var neighbour = Registry(root, time, 2);
            neighbour.RecordCast(new LocalPluginCast(Remote, 0x50000012u, 42u, 300, 60d, true));
            neighbour.Publish(Client(neighbour.ClientId, Remote, "Gamma"));

            Assert.True(reader.ImportRemoteClient(Client(0u, Remote, "Gamma"), Own));
            Assert.True(reader.ImportRemoteCast(Remote, 0x50000013u, 43u, 300, 60d, true));

            PluginNetworkClient only = Assert.Single(reader.CaptureRemoteClients(Own));
            Assert.False(only.IsRemote);
            Assert.Equal(neighbour.ClientId, only.ClientId);
            PluginPeerCast cast = Assert.Single(reader.CaptureRemoteCasts(0L, "Coldeve", Own));
            Assert.False(cast.IsRemote);
            Assert.Equal(42u, cast.SpellId);
        }
        finally
        {
            Delete(root);
        }
    }

    /// <summary>
    /// A relay that echoes a neighbour back hands this client the same casts
    /// and lines a second way. While the neighbour's note speaks for the
    /// character the echoed copy is hidden; when the note goes away the copy
    /// shows again, and nothing it carried meanwhile may be taken in twice
    /// or run a second time.
    /// </summary>
    [Fact]
    public void WhatAHiddenCopyCarriedIsNotReDeliveredWhenTheNoteGoesAway()
    {
        string root = TemporaryRoot();
        var time = new ManualTimeProvider(Start);
        try
        {
            using var reader = Registry(root, time, 1);
            using var neighbour = Registry(root, time, 2);
            Assert.True(neighbour.RecordCast(
                new LocalPluginCast(Remote, 0x50000012u, 42u, 300, 60d, true)));
            Assert.True(neighbour.RecordCommand(
                new LocalPluginCommand(Remote, [], "/go", 0)));
            neighbour.Publish(Client(neighbour.ClientId, Remote, "Gamma"));

            // The relay's echo of the same cast and line.
            Assert.True(reader.ImportRemoteClient(Client(0u, Remote, "Gamma"), Own));
            Assert.True(reader.ImportRemoteCast(Remote, 0x50000012u, 42u, 300, 60d, true));
            Assert.True(reader.ImportRemoteCommand(Remote, [], "/go", 0));

            PluginPeerCast cast = Assert.Single(reader.CaptureRemoteCasts(0L, "Coldeve", Own));
            Assert.False(cast.IsRemote);
            LocalPluginPeerCommand line = Assert.Single(
                reader.CaptureRemoteCommands(0L, "Coldeve", Own, []));
            Assert.False(line.Command.IsRemote);

            neighbour.Withdraw();
            time.Advance(TimeSpan.FromSeconds(1));
            Assert.True(reader.ImportRemoteClient(Client(0u, Remote, "Gamma"), Own));

            Assert.True(Assert.Single(reader.CaptureRemoteClients(Own)).IsRemote);
            Assert.Empty(reader.CaptureRemoteCasts(cast.Sequence, "Coldeve", Own));
            Assert.Empty(reader.CaptureRemoteCommands(line.Command.Sequence, "Coldeve", Own, []));

            // What the relay carries from now on is taken in as before.
            Assert.True(reader.ImportRemoteCast(Remote, 0x50000012u, 44u, 300, 60d, true));
            PluginPeerCast next = Assert.Single(
                reader.CaptureRemoteCasts(cast.Sequence, "Coldeve", Own));
            Assert.True(next.IsRemote);
            Assert.Equal(44u, next.SpellId);
        }
        finally
        {
            Delete(root);
        }
    }

    /// <summary>
    /// The other way round: the relay carried a character first and its note
    /// shows up here later. The note's rings hold what the relay already
    /// delivered; only what the note says after that is new.
    /// </summary>
    [Fact]
    public void ANoteThatShowsAfterItsRelayedCopyStartsAtTheEndOfItsRings()
    {
        string root = TemporaryRoot();
        var time = new ManualTimeProvider(Start);
        try
        {
            using var reader = Registry(root, time, 1);
            Assert.True(reader.ImportRemoteClient(Client(0u, Remote, "Gamma"), Own));
            Assert.True(reader.ImportRemoteCast(Remote, 0x50000012u, 42u, 300, 60d, true));
            Assert.True(reader.ImportRemoteCommand(Remote, [], "/go", 0));
            PluginPeerCast relayed = Assert.Single(reader.CaptureRemoteCasts(0L, "Coldeve", Own));
            LocalPluginPeerCommand relayedLine = Assert.Single(
                reader.CaptureRemoteCommands(0L, "Coldeve", Own, []));

            using var neighbour = Registry(root, time, 2);
            Assert.True(neighbour.RecordCast(
                new LocalPluginCast(Remote, 0x50000012u, 42u, 300, 60d, true)));
            Assert.True(neighbour.RecordCommand(
                new LocalPluginCommand(Remote, [], "/go", 0)));
            neighbour.Publish(Client(neighbour.ClientId, Remote, "Gamma"));

            Assert.False(Assert.Single(reader.CaptureRemoteClients(Own)).IsRemote);
            Assert.Empty(reader.CaptureRemoteCasts(relayed.Sequence, "Coldeve", Own));
            Assert.Empty(reader.CaptureRemoteCommands(relayedLine.Command.Sequence, "Coldeve", Own, []));

            time.Advance(TimeSpan.FromSeconds(1));
            Assert.True(neighbour.RecordCast(
                new LocalPluginCast(Remote, 0x50000012u, 45u, 300, 60d, true)));
            neighbour.Publish(Client(neighbour.ClientId, Remote, "Gamma"));
            PluginPeerCast fresh = Assert.Single(
                reader.CaptureRemoteCasts(relayed.Sequence, "Coldeve", Own));
            Assert.False(fresh.IsRemote);
            Assert.Equal(45u, fresh.SpellId);
        }
        finally
        {
            Delete(root);
        }
    }

    [Fact]
    public void AnImportedCastIsReadOnceMarkedRemoteAndCountsDown()
    {
        string root = TemporaryRoot();
        var time = new ManualTimeProvider(Start);
        try
        {
            using var reader = Registry(root, time, 1);
            Assert.True(reader.ImportRemoteClient(Client(0u, Remote, "Gamma"), Own));
            Assert.True(reader.ImportRemoteCast(Remote, 0x50000012u, 42u, 357, 30d, true));
            // An attempt carries no duration, whatever the relay said.
            Assert.True(reader.ImportRemoteCast(Remote, 0x50000012u, 43u, 0, 99d, false));
            // Well-formedness is the note rule.
            Assert.False(reader.ImportRemoteCast(Remote, 0u, 42u, 357, 30d, true));
            Assert.False(reader.ImportRemoteCast(Remote, 0x50000012u, 42u, -1, 30d, true));
            Assert.False(reader.ImportRemoteCast(Remote, 0x50000012u, 42u, 357, 0d, true));
            Assert.False(reader.ImportRemoteCast(Remote, 0x50000012u, 42u, 357, double.NaN, true));
            // A peer playing in another world names other creatures entirely,
            // so its cast is never handed to a reader here.
            Assert.True(reader.ImportRemoteClient(
                Client(0u, Remote + 1u, "Elsewhere") with { WorldName = "Frostfell" }, Own));
            Assert.True(reader.ImportRemoteCast(Remote + 1u, 0x50000012u, 44u, 357, 30d, true));

            time.Advance(TimeSpan.FromSeconds(5));
            PluginPeerCast[] casts = reader.CaptureRemoteCasts(0L, "Coldeve", Own).ToArray();
            Assert.Equal(2, casts.Length);
            PluginNetworkClient peer = Assert.Single(
                reader.CaptureRemoteClients(Own), static client => client.PlayerId == Remote);
            Assert.All(casts, cast => Assert.True(cast.IsRemote));
            Assert.All(casts, cast => Assert.Equal(peer.ClientId, cast.ClientId));
            Assert.Equal(Remote, casts[0].CasterObjectId);
            Assert.True(casts[0].Landed);
            Assert.Equal(25d, casts[0].SecondsRemaining, 3);
            Assert.False(casts[1].Landed);
            Assert.Equal(0d, casts[1].SecondsRemaining);
            Assert.Empty(reader.CaptureRemoteCasts(casts[1].Sequence, "Coldeve", Own));
        }
        finally
        {
            Delete(root);
        }
    }

    [Fact]
    public void AnImportedCastIsRefusedOnceItsCasterHasGoneStale()
    {
        string root = TemporaryRoot();
        var time = new ManualTimeProvider(Start);
        try
        {
            using var reader = Registry(root, time, 1);
            Assert.True(reader.ImportRemoteClient(Client(0u, Remote, "Gamma"), Own));
            time.Advance(LocalPluginPeerRegistry.StaleAfter + TimeSpan.FromMilliseconds(1));
            Assert.False(reader.ImportRemoteCast(Remote, 0x50000012u, 42u, 357, 30d, true));
            Assert.False(reader.ImportRemoteCommand(Remote, [], "/go", 0));
        }
        finally
        {
            Delete(root);
        }
    }

    [Fact]
    public void AnImportedLineReachesOnlyTheLabelsItIsAimedAt()
    {
        string root = TemporaryRoot();
        var time = new ManualTimeProvider(Start);
        try
        {
            // The highest client id there is, so every imported peer's id
            // is below it and would rank ahead if imported peers were
            // counted in the stagger order.
            using var reader = new LocalPluginPeerRegistry(
                root, time, Guid.Parse("ffffffff-0000-0000-0000-000000000000"));
            Assert.True(reader.ImportRemoteClient(Client(0u, Remote, "Gamma"), Own));
            Assert.True(reader.ImportRemoteClient(Client(0u, Remote + 1u, "Delta"), Own));
            Assert.True(reader.ImportRemoteCommand(Remote, ["healer"], "/heal me", 200));
            Assert.True(reader.ImportRemoteCommand(Remote, ["tank"], "/taunt", 0));
            Assert.False(reader.ImportRemoteCommand(Remote, [], "bad\nline", 0));
            Assert.False(reader.ImportRemoteCommand(Remote, [], "/go", -1));

            LocalPluginPeerCommand only = Assert.Single(
                reader.CaptureRemoteCommands(0L, "Coldeve", Own, ["healer"]));
            Assert.True(only.Command.IsRemote);
            Assert.Equal("/heal me", only.Command.Line);
            Assert.Equal(Remote, only.Command.SenderObjectId);
            Assert.Equal(["healer"], only.Command.Tags);
            // No neighbour on this computer ranks ahead, so this client holds
            // the first place and waits one delay: an imported peer's client
            // id is this client's own invention and holds no place here.
            Assert.Equal(200, only.StaggerMilliseconds);
        }
        finally
        {
            Delete(root);
        }
    }

    /// <summary>
    /// The outgoing half: a relay reads what this client announced, once per
    /// cursor, with what is left of a success, until it ages out.
    /// </summary>
    [Fact]
    public void OwnAnnouncementsAreReadBackInOrderUntilTheyAgeOut()
    {
        string root = TemporaryRoot();
        var time = new ManualTimeProvider(Start);
        try
        {
            using var own = Registry(root, time, 1);
            Assert.True(own.RecordCast(new LocalPluginCast(Own, 0x50000012u, 42u, 357, 0d, false)));
            Assert.True(own.RecordCast(new LocalPluginCast(Own, 0x50000012u, 42u, 357, 60d, true)));
            Assert.True(own.RecordCommand(new LocalPluginCommand(Own, ["squad"], "/go", 100)));

            time.Advance(TimeSpan.FromSeconds(4));
            PluginPeerCast[] casts = own.CaptureOwnCasts(0L).ToArray();
            Assert.Equal(2, casts.Length);
            Assert.Equal([1L, 2L], casts.Select(static cast => cast.Sequence));
            Assert.All(casts, cast => Assert.Equal(own.ClientId, cast.ClientId));
            Assert.All(casts, cast => Assert.False(cast.IsRemote));
            Assert.False(casts[0].Landed);
            Assert.Equal(0d, casts[0].SecondsRemaining);
            Assert.True(casts[1].Landed);
            Assert.Equal(56d, casts[1].SecondsRemaining, 3);
            Assert.Single(own.CaptureOwnCasts(1L));
            Assert.Empty(own.CaptureOwnCasts(2L));

            PluginPeerCommand command = Assert.Single(own.CaptureOwnCommands(0L));
            Assert.Equal(1L, command.Sequence);
            Assert.Equal(own.ClientId, command.ClientId);
            Assert.Equal(Own, command.SenderObjectId);
            Assert.Equal("/go", command.Line);
            Assert.Equal(["squad"], command.Tags);
            Assert.Equal(Start, command.SentAt);

            time.Advance(LocalPluginPeerRegistry.StaleAfter);
            Assert.Empty(own.CaptureOwnCasts(0L));
            Assert.Empty(own.CaptureOwnCommands(0L));
        }
        finally
        {
            Delete(root);
        }
    }

    [Fact]
    public void ForgottenAnnouncementsAreNotReadBackAndTheNumberingCarriesOn()
    {
        string root = TemporaryRoot();
        var time = new ManualTimeProvider(Start);
        try
        {
            using var own = Registry(root, time, 1);
            Assert.True(own.RecordCast(new LocalPluginCast(Own, 0x50000012u, 42u, 357, 60d, true)));
            Assert.True(own.RecordCommand(new LocalPluginCommand(Own, [], "/go", 0)));

            own.ForgetOwnAnnouncements();

            Assert.Empty(own.CaptureOwnCasts(0L));
            Assert.Empty(own.CaptureOwnCommands(0L));
            Assert.True(own.RecordCast(new LocalPluginCast(Own + 1u, 0x50000012u, 43u, 357, 60d, true)));
            PluginPeerCast next = Assert.Single(own.CaptureOwnCasts(0L));
            Assert.Equal(2L, next.Sequence);
            Assert.Equal(Own + 1u, next.CasterObjectId);
        }
        finally
        {
            Delete(root);
        }
    }

    private static readonly DateTimeOffset Start =
        new(2026, 9, 25, 12, 0, 0, TimeSpan.Zero);

    private static LocalPluginPeerRegistry Registry(
        string root,
        TimeProvider time,
        int which) => new(root, time, Guid.Parse($"{which:D8}-0000-0000-0000-000000000000"));

    private static string TemporaryRoot() => Path.Combine(
        Path.GetTempPath(),
        $"acdream-plugin-relay-{Guid.NewGuid():N}");

    private static void Delete(string root)
    {
        if (Directory.Exists(root))
            Directory.Delete(root, recursive: true);
    }

    private static PluginNetworkClient Client(uint clientId, uint playerId, string name) => new(
        clientId,
        playerId,
        name,
        "Coldeve",
        new PluginNavigationPosition(0x7F7F0001u, 33.5d, -72.8d, 1d, 90f, true),
        ["healer"],
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
