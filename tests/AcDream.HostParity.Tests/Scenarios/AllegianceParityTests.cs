using AcDream.Core.Net.Messages;
using AcDream.Plugin.Abstractions;

namespace AcDream.HostParity.Tests;

/// <summary>
/// Swearing to a patron and breaking a tie, run against both clients.
///
/// Regression guard, in the sense the suite README gives the word: the owner
/// of both commands is the shared runtime surface, so the two arms run the
/// same code and agreement is true by construction. What earns its keep here
/// is that each step is ASSERTED per arm -- on the answer the plugin was
/// given AND on the id that really left the client for the server -- so a
/// client that composed nothing, or composed the wrong id, is caught rather
/// than agreeing with the other client about doing nothing.
///
/// Mutation checks (2026-09-22), each run: sending the character's own id in
/// place of the patron's turned
/// <see cref="SwearingToAPlayerLeavesBothClientsWithThatPlayersId"/> red on
/// both arms at once, on the id read back off the wire; dropping the
/// player-kind half of the swear check turned
/// <see cref="SwearingToACreatureIsRefusedTheSameOnBothClients"/> red on a
/// swear that should never have been composed; and dropping the membership
/// check turned
/// <see cref="BreakingWithAStrangerIsRefusedTheSameOnBothClients"/> red the
/// same way; and deleting the runtime's in-world ask turned
/// <see cref="ArrivingInTheWorldAsksTheServerAboutTheAllegianceOnBothClients"/>
/// red on an empty outbound record. Restoring each turned them green.
///
/// The allegiance these scenarios stand on is said by the harness server and
/// read by each client's own parser and inbound route, so the asking, the
/// parsing and the routing are all inside the comparison rather than written
/// into the runtime owner by hand.
/// </summary>
public sealed class AllegianceParityTests
{
    /// <summary>The client action that says "swear to that one".</summary>
    private const uint SwearAction = 0x001Du;

    /// <summary>The client action that says "break with that one".</summary>
    private const uint BreakAction = 0x001Eu;

    /// <summary>
    /// The client action that asks the server to state the allegiance, and
    /// to keep the client told about it.
    /// </summary>
    private const uint UpdateRequestAction = 0x001Fu;

    /// <summary>
    /// Both clients ask the server about the allegiance as they arrive in
    /// the world, without anyone opening a panel. Nothing about an
    /// allegiance is known until the server is asked, so a client that did
    /// not ask would refuse every break for the whole session -- which is
    /// what the client with no window did, because the ask used to live in
    /// the other client's social panel.
    /// </summary>
    [Fact]
    public void ArrivingInTheWorldAsksTheServerAboutTheAllegianceOnBothClients() =>
        ParityScenario.RunFromLogin(static (arm, transcript) =>
        {
            transcript.Step("arrive in the world");
            // Nothing has been asked before the character is in: a request
            // into a session the server has not let the character into has
            // nothing to answer it.
            Assert.Empty(Sent(arm, UpdateRequestAction));

            arm.EnterWorld();

            // Asked once, in the subscribe form, by THIS client -- read off
            // the wire rather than counted, so a client that composed
            // nothing cannot agree with the other about doing nothing.
            ParityOutbound request =
                Assert.Single(Sent(arm, UpdateRequestAction));
            Assert.Equal(1u, WordAfterTheAction(request));
            transcript.RecordOutbound(arm);
        });

    /// <summary>
    /// And nothing else. Arriving in the world is a moment when a client
    /// could start saying all sorts of things to the server unasked; what it
    /// says is this one question. The scenario that pins the question reads
    /// only the messages of that kind, so it would not notice a second
    /// client learning to announce itself on arrival -- this one counts
    /// everything that left.
    /// </summary>
    [Fact]
    public void ArrivingInTheWorldSendsNothingButThatOneQuestion() =>
        ParityScenario.RunFromLogin(static (arm, transcript) =>
        {
            transcript.Step("arrive in the world");
            Assert.Empty(arm.Operations.Outbound);

            arm.EnterWorld();

            ParityOutbound only = Assert.Single(arm.Operations.Outbound);
            transcript.Record("action", only.GameAction);
            Assert.Equal(UpdateRequestAction, only.GameAction);
            transcript.RecordOutbound(arm);
        });

    [Fact]
    public void SwearingToAPlayerLeavesBothClientsWithThatPlayersId() =>
        ParityScenario.Run(static (arm, transcript) =>
        {
            IAllegianceAutomation allegiance = StageWorld(arm);

            transcript.Step("swear to the other player");
            PluginAllegianceCommandResult result =
                allegiance.Swear(ParityWorld.OtherPlayer);
            Record(transcript, "swear", result);
            arm.Advance();
            // One swear went out, and it names the player that was asked
            // for. A client that composed nothing would agree with the
            // other client about nothing, so the id is read back off the
            // wire rather than counted.
            Assert.Equal(PluginAllegianceCommandStatus.Sent, result.Status);
            Assert.Equal(ParityWorld.OtherPlayer, WhoWasNamed(arm, SwearAction));
            Assert.Empty(Sent(arm, BreakAction));
            transcript.RecordOutbound(arm);
        });

    [Fact]
    public void BreakingWithThePatronLeavesBothClientsWithThePatronsId() =>
        ParityScenario.Run(static (arm, transcript) =>
        {
            IAllegianceAutomation allegiance = StageWorld(arm);

            transcript.Step("break with the patron");
            PluginAllegianceCommandResult result =
                allegiance.Break(ParityWorld.Patron);
            Record(transcript, "break", result);
            arm.Advance();
            // The patron is not in the world at all, and the tie is still
            // broken: a break asks the server about the allegiance list, not
            // about who is standing where.
            Assert.Equal(PluginAllegianceCommandStatus.Sent, result.Status);
            Assert.Equal(ParityWorld.Patron, WhoWasNamed(arm, BreakAction));
            Assert.Empty(Sent(arm, SwearAction));
            transcript.RecordOutbound(arm);
        });

    /// <summary>
    /// Something standing right there that is not a player. Both clients
    /// refuse it for the target rather than sending a number the server can
    /// only throw away.
    /// </summary>
    [Fact]
    public void SwearingToACreatureIsRefusedTheSameOnBothClients() =>
        ParityScenario.Run(static (arm, transcript) =>
        {
            IAllegianceAutomation allegiance = StageWorld(arm);

            transcript.Step("swear to a creature");
            PluginAllegianceCommandResult result =
                allegiance.Swear(ParityWorld.Monster);
            Record(transcript, "swear", result);
            arm.Advance();
            Assert.Equal(PluginAllegianceCommandStatus.InvalidTarget, result.Status);
            Assert.Empty(Sent(arm, SwearAction));
            transcript.RecordOutbound(arm);
        });

    /// <summary>
    /// Another player, visible, who is in no allegiance with this character.
    /// Being visible is not what a break needs, and both clients say so.
    /// </summary>
    [Fact]
    public void BreakingWithAStrangerIsRefusedTheSameOnBothClients() =>
        ParityScenario.Run(static (arm, transcript) =>
        {
            IAllegianceAutomation allegiance = StageWorld(arm);

            transcript.Step("break with a player outside the allegiance");
            PluginAllegianceCommandResult result =
                allegiance.Break(ParityWorld.OtherPlayer);
            Record(transcript, "break", result);
            arm.Advance();
            Assert.Equal(PluginAllegianceCommandStatus.InvalidTarget, result.Status);
            Assert.Empty(Sent(arm, BreakAction));
            transcript.RecordOutbound(arm);
        });

    /// <summary>
    /// Who heads the allegiance, who the character is sworn to and who is
    /// sworn to it, as the server described them -- read through each
    /// client's own parser and route, and asserted per arm.
    ///
    /// Mutation check (2026-09-25): asking for the patron of the monarch
    /// instead of the character turned this red on both arms.
    /// </summary>
    [Fact]
    public void EitherClientNamesTheMonarchThePatronAndTheVassals() =>
        ParityScenario.Run(static (arm, transcript) =>
        {
            _ = ParityWorld.Stage(arm);
            const uint Vassal = 0x50000077u;
            arm.Server.AllegianceUpdate(new ClientCommandResponses.AllegianceUpdate(
                Rank: 3u,
                TotalMembers: 4u,
                TotalVassals: 1u,
                RecordCount: 4,
                AllegianceName: "The Order",
                Monarch: new ClientCommandResponses.AllegianceMemberRecord(
                    ParityWorld.Monarch, 0u, true, "Monarch",
                    Rank: 6, Level: 275u, Gender: 2, HeritageGroup: 3),
                Records:
                [
                    new ClientCommandResponses.AllegianceMemberRecord(
                        ParityWorld.Patron, ParityWorld.Monarch, false, "Patron",
                        Rank: 4, Level: 180u, Gender: 1, HeritageGroup: 1),
                    new ClientCommandResponses.AllegianceMemberRecord(
                        ParityWorld.Player, ParityWorld.Patron, true, "Parity",
                        Rank: 3, Level: 90u, Gender: 1, HeritageGroup: 2),
                    new ClientCommandResponses.AllegianceMemberRecord(
                        Vassal, ParityWorld.Player, true, "Squire",
                        Rank: 1, Level: 20u, Gender: 2, HeritageGroup: 8),
                ]));
            PluginAllegianceSnapshot snapshot = arm.Host.Automation.Allegiance.Snapshot;

            transcript.Step("identities");
            transcript.Record("monarch", snapshot.Monarch?.Name);
            transcript.Record("patron", snapshot.Patron?.Name);
            transcript.Record("vassals", string.Join(",", snapshot.Vassals.Select(static vassal => vassal.Name)));
            transcript.Record("followers", snapshot.VassalCount);
            Assert.Equal(
                new PluginAllegianceMember(ParityWorld.Monarch, "Monarch", 6u, 275u, 3, 2, true),
                snapshot.Monarch);
            Assert.Equal(
                new PluginAllegianceMember(ParityWorld.Patron, "Patron", 4u, 180u, 1, 1, false),
                snapshot.Patron);
            Assert.Equal(
                [new PluginAllegianceMember(Vassal, "Squire", 1u, 20u, 8, 2, true)],
                snapshot.Vassals);
            Assert.Equal(1u, snapshot.VassalCount);
            Assert.Equal(4u, snapshot.MemberCount);
        });

    /// <summary>
    /// The character in the world with an allegiance behind it, another
    /// player standing next to it, and the outbound record wiped so what a
    /// scenario reads off the wire is its own.
    /// </summary>
    private static IAllegianceAutomation StageWorld(ParityArm arm)
    {
        _ = ParityWorld.Stage(arm);
        ParityWorld.StageAnotherPlayer(arm.Runtime);
        ParityWorld.StageAllegiance(arm);
        _ = arm.Operations.TakeOutbound();
        return arm.Host.Automation.Allegiance;
    }

    /// <summary>Every command of this kind this arm has asked to send.</summary>
    private static IReadOnlyList<ParityOutbound> Sent(ParityArm arm, uint action)
        => [.. arm.Operations.Outbound.Where(
            message => message.GameAction == action)];

    /// <summary>Who the one command of this kind this arm sent named.</summary>
    private static uint WhoWasNamed(ParityArm arm, uint action) =>
        WordAfterTheAction(Assert.Single(Sent(arm, action)));

    /// <summary>
    /// The one word a client action carries after naming itself: an object
    /// id for a swear or a break, and on or off for an update request.
    /// </summary>
    private static uint WordAfterTheAction(ParityOutbound message)
    {
        byte[] body = Convert.FromHexString(message.Body);
        return System.Buffers.Binary.BinaryPrimitives
            .ReadUInt32LittleEndian(body.AsSpan(12));
    }

    private static void Record(
        ParityTranscript transcript,
        string key,
        PluginAllegianceCommandResult result)
    {
        transcript.Record($"{key}.status", result.Status.ToString());
        transcript.Record($"{key}.notice", result.Notice);
    }
}
