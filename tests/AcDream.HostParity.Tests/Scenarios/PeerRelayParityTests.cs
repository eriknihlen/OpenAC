using AcDream.Core.Spells;
using AcDream.Plugin.Abstractions;
using AcDream.Runtime.Plugins;

namespace AcDream.HostParity.Tests;

/// <summary>
/// A plugin carries the peer bus to characters played on other computers:
/// it reads what this client tells its neighbours, sends that over a
/// transport of its own, and imports what comes back. Both clients have to
/// offer this or a relay behaves differently with and without a window, so
/// these scenarios drive it through <see cref="IPluginHost"/> on both.
///
/// Mutation checks (2026-09-25):
/// * having the self capture skip the character id turned
///   <see cref="EitherClientHandsARelayWhatItTellsItsNeighbours"/> red;
/// * dropping the imported peers from the notes the client reads turned
///   <see cref="EitherClientTakesInARemotePeerItsCastsAndItsLines"/> red with
///   "did not list the imported peer".
/// </summary>
public sealed class PeerRelayParityTests
{
    private const uint SharedSpell = 42u;
    private const uint RemoteCharacter = 0x50000044u;
    private const string Verb = "parityrelay";

    private static readonly Guid OtherClient =
        Guid.Parse("7ea50000-0000-0000-0000-0000000000c9");

    /// <summary>
    /// The outgoing half: what a relay reads about this client is exactly
    /// what a neighbour on this computer reads from its note.
    /// </summary>
    [Fact]
    public void EitherClientHandsARelayWhatItTellsItsNeighbours() =>
        ParityScenario.Run(static (arm, transcript) =>
        {
            _ = ParityWorld.Stage(arm);
            InstallSpell(arm);
            INetworkAutomation network = arm.Host.Automation.Network;

            transcript.Step("self");
            bool captured = network.TryCaptureSelf(out PluginNetworkClient self);
            transcript.Record("captured", captured);
            Assert.True(captured, $"{arm.Name} would not say what it tells its neighbours.");
            transcript.Record("playerId", self.PlayerId);
            transcript.Record("name", self.Name);
            transcript.Record("isRemote", self.IsRemote);
            transcript.Record("maxHealth", self.MaxHealth);
            Assert.Equal(arm.Host.Automation.Character.ObjectId, self.PlayerId);
            Assert.Equal(arm.Host.Automation.Character.Name, self.Name);
            Assert.False(self.IsRemote);
            Assert.NotEqual(0u, self.ClientId);

            // A neighbour reads the same client under the same id.
            arm.Advance();
            using (var onlooker = new LocalPluginPeerRegistry(
                arm.PeerDirectory, timeProvider: null, OtherClient))
            {
                PluginNetworkClient seen = Assert.Single(onlooker.CaptureRemoteClients());
                Assert.Equal(self.ClientId, seen.ClientId);
                Assert.Equal(self.PlayerId, seen.PlayerId);
            }

            transcript.Step("announced");
            Assert.True(network.AnnounceCastAttempt(ParityWorld.Monster, SharedSpell, 357));
            Assert.True(network.AnnounceCastSuccess(ParityWorld.Monster, SharedSpell, 357, 60d));
            Assert.True(network.BroadcastCommand($"/{Verb} go", ["squad"], 250));
            PluginPeerCast[] casts = network.CaptureOwnCasts(0L).ToArray();
            transcript.Record("casts", casts.Length);
            Assert.Equal(2, casts.Length);
            transcript.Record("firstLanded", casts[0].Landed);
            transcript.Record("secondLanded", casts[1].Landed);
            transcript.Record("caster", casts[1].CasterObjectId);
            Assert.False(casts[0].Landed);
            Assert.True(casts[1].Landed);
            Assert.All(casts, cast => Assert.Equal(self.PlayerId, cast.CasterObjectId));
            Assert.All(casts, cast => Assert.Equal(self.ClientId, cast.ClientId));
            Assert.InRange(casts[1].SecondsRemaining, 55d, 60d);
            Assert.Empty(network.CaptureOwnCasts(casts[1].Sequence));

            PluginPeerCommand command = Assert.Single(network.CaptureOwnCommands(0L));
            transcript.Record("line", command.Line);
            transcript.Record("tags", string.Join(",", command.Tags));
            Assert.Equal($"/{Verb} go", command.Line);
            Assert.Equal(["squad"], command.Tags);
            Assert.Equal(self.PlayerId, command.SenderObjectId);
        });

    /// <summary>
    /// The incoming half: an imported peer is listed as remote, its cast is
    /// handed to a plugin as remote, and a line it broadcast is run on this
    /// client's own command bus, on either client.
    /// </summary>
    [Fact]
    public void EitherClientTakesInARemotePeerItsCastsAndItsLines() =>
        ParityScenario.Run(static (arm, transcript) =>
        {
            _ = ParityWorld.Stage(arm);
            InstallSpell(arm);
            INetworkAutomation network = arm.Host.Automation.Network;
            var ran = new List<string>();
            using IDisposable verb = arm.Host.Commands.Register(
                Verb, command => ran.Add(command.RawText));

            transcript.Step("import");
            PluginNetworkClient remote = RemotePeer(arm);
            transcript.Record("ownRefused", network.ImportRemoteClient(
                remote with { PlayerId = arm.Host.Automation.Character.ObjectId }));
            Assert.False(network.ImportRemoteClient(
                remote with { PlayerId = arm.Host.Automation.Character.ObjectId }));
            transcript.Record("accepted", network.ImportRemoteClient(remote));
            PluginNetworkClient[] listed = network.CaptureClients()
                .Where(static client => client.PlayerId == RemoteCharacter)
                .ToArray();
            Assert.True(
                listed.Length == 1,
                $"{arm.Name} did not list the imported peer: {listed.Length}.");
            transcript.Record("isRemote", listed[0].IsRemote);
            transcript.Record("health", listed[0].CurrentHealth);
            Assert.True(listed[0].IsRemote);
            Assert.Equal(55u, listed[0].CurrentHealth);

            transcript.Step("cast");
            transcript.Record("unknownSpellRefused", network.ImportRemoteCast(
                RemoteCharacter, ParityWorld.Monster, 777u, 300, 30d, true));
            Assert.False(network.ImportRemoteCast(
                RemoteCharacter, ParityWorld.Monster, 777u, 300, 30d, true));
            Assert.True(network.ImportRemoteCast(
                RemoteCharacter, ParityWorld.Monster, SharedSpell, 300, 30d, true));
            PluginPeerCast cast = Assert.Single(network.CaptureCasts(0L));
            transcript.Record("castIsRemote", cast.IsRemote);
            transcript.Record("caster", cast.CasterObjectId);
            Assert.True(cast.IsRemote);
            Assert.Equal(RemoteCharacter, cast.CasterObjectId);
            Assert.Equal(listed[0].ClientId, cast.ClientId);
            Assert.InRange(cast.SecondsRemaining, 25d, 30d);

            transcript.Step("line");
            Assert.True(network.ImportRemoteCommand(RemoteCharacter, $"/{Verb} go", [], 0));
            arm.Advance();
            transcript.Record("linesRun", ran.Count);
            Assert.True(ran.Count == 1, $"{arm.Name} did not run the imported line: {ran.Count}.");
            Assert.Equal($"/{Verb} go", ran[0]);
            PluginPeerCommand read = Assert.Single(network.CaptureCommands(0L));
            transcript.Record("lineIsRemote", read.IsRemote);
            Assert.True(read.IsRemote);
        });

    private static PluginNetworkClient RemotePeer(ParityArm arm) => new(
        0u,
        RemoteCharacter,
        "Faraway",
        arm.Host.Automation.Character.WorldName,
        new PluginNavigationPosition(
            ParityPlayerBody.Cell,
            ParityWorld.PlayerX,
            ParityWorld.PlayerY,
            ParityPlayerBody.GroundHeight,
            0f,
            true),
        [],
        55u, 100u, 100u, 100u, 100u, 100u,
        0f);

    private static void InstallSpell(ParityArm arm) =>
        arm.Runtime.CharacterOwner.InstallSpellMetadata(
            SpellTable.Create(
            [
                new SpellMetadata(
                    SharedSpell,
                    "Fire Vulnerability Other VII",
                    "Life Magic",
                    7u,
                    0u,
                    string.Empty,
                    60f,
                    10,
                    true,
                    false,
                    string.Empty,
                    0,
                    350,
                    0u,
                    7,
                    false,
                    true,
                    false,
                    0f,
                    0u,
                    0u,
                    1u,
                    0),
            ]));
}
