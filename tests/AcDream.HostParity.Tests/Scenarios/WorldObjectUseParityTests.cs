using AcDream.Plugin.Abstractions;

namespace AcDream.HostParity.Tests;

/// <summary>
/// Using an object the character does not own -- a corpse, a chest, a vendor,
/// a townsfolk, a door -- run against both clients. Until now only the client
/// with a window had this at all: it walked to the object and used it on
/// arrival, while the client without one refused the call outright, so a bot
/// that opened corpses could not be proved on the client anyone plays. Both
/// now take the one runtime route, and these scenarios are what says so.
///
/// Each step is recorded AND asserted, on the answer the plugin was given
/// and on what left the client for the server. The comparison alone cannot
/// tell a client that walked to the corpse from one that did nothing, since
/// two clients doing nothing agree line for line.
///
/// Mutation check (2026-09-21), run: making the shared route send the use
/// from wherever the character stands instead of walking first turned
/// <see cref="UsingACorpseSeveralMetresOffLooksTheSameOnBothClients"/> and
/// <see cref="TwoUsesInARowAreRefusedTheSameOnBothClients"/> red on both arms
/// at once -- a use on the wire where there should have been none.
///
/// Mutation check (2026-09-20), run: taking the walk-then-use binding out of
/// the shared binding pass for one arm turned three of these four red on the
/// status lines, exactly as the client without a window used to answer;
/// restoring it turned them green. The fourth stays green under that
/// mutation on purpose -- a guid nothing answers to is refused before the
/// route is reached at all, so it is here to pin that the two clients agree
/// on the refusal that comes first, not on the route.
/// </summary>
public sealed class WorldObjectUseParityTests
{
    [Fact]
    public void UsingACorpseSeveralMetresOffLooksTheSameOnBothClients() =>
        ParityScenario.Run(static (arm, transcript) =>
        {
            IItemAutomation items = StageWorld(arm);

            transcript.Step("use the corpse");
            PluginItemCommandResult use = items.Use(ParityWorld.Corpse);
            Record(transcript, "use", use);
            transcript.Record("busy", items.IsBusy);
            // The corpse is three metres off, so the client takes the walk
            // and holds the use back until it arrives. Said outright: a
            // client that refused outright -- which is what the one without
            // a window used to do -- answers a different word here, and two
            // clients that both refused would agree.
            Assert.Equal(PluginItemCommandStatus.Started, use.Status);
            Assert.True(items.IsBusy);
            Assert.Empty(Sent(arm, UseAction));
            transcript.RecordOutbound(arm);

            transcript.Step("a few frames later");
            for (int tick = 0; tick < 10; tick++)
                arm.Advance();
            transcript.Record("busy", items.IsBusy);
            // A tenth of a second in, the character is still on its way and
            // still nothing has been sent for the use.
            Assert.True(items.IsBusy);
            Assert.Empty(Sent(arm, UseAction));
            transcript.RecordOutbound(arm);
        });

    [Fact]
    public void UsingSomethingThatIsNotThereIsRefusedTheSameOnBothClients() =>
        ParityScenario.Run(static (arm, transcript) =>
        {
            IItemAutomation items = StageWorld(arm);

            transcript.Step("use a guid nothing answers to");
            PluginItemCommandResult use = items.Use(0x5000_00FFu);
            Record(transcript, "use", use);
            transcript.Record("busy", items.IsBusy);
            // Turned away for the thing itself, before any route is reached:
            // nothing is sent and the client is left free for the next
            // request rather than holding the gate.
            Assert.Equal(PluginItemCommandStatus.InvalidItem, use.Status);
            Assert.False(items.IsBusy);
            Assert.Empty(Sent(arm, UseAction));
            transcript.RecordOutbound(arm);
        });

    /// <summary>
    /// Using the character itself. Neither client turns this away: the
    /// character is at arm's length from itself, so the use is composed and
    /// sent there and then and the server is left to decide what it means.
    /// What matters here is that both clients do the same thing with it --
    /// the scenario used to be named for a refusal that never happened, and
    /// nothing said otherwise because nothing looked at the answer.
    /// </summary>
    [Fact]
    public void UsingTheCharacterItselfIsSentTheSameWayOnBothClients() =>
        ParityScenario.Run(static (arm, transcript) =>
        {
            IItemAutomation items = StageWorld(arm);
            uint character = arm.Runtime.PlayerIdentity.ServerGuid;

            transcript.Step("use the character");
            PluginItemCommandResult use = items.Use(character);
            Record(transcript, "use", use);
            transcript.Record("busy", items.IsBusy);
            Assert.Equal(PluginItemCommandStatus.Started, use.Status);
            Assert.True(items.IsBusy);
            Assert.Equal(character, WhatTheUseNamed(arm));
            transcript.RecordOutbound(arm);
        });

    [Fact]
    public void TwoUsesInARowAreRefusedTheSameOnBothClients() =>
        ParityScenario.Run(static (arm, transcript) =>
        {
            IItemAutomation items = StageWorld(arm);

            transcript.Step("use the corpse");
            PluginItemCommandResult first = items.Use(ParityWorld.Corpse);
            Record(transcript, "first", first);
            Assert.Equal(PluginItemCommandStatus.Started, first.Status);

            transcript.Step("ask again straight away");
            PluginItemCommandResult second = items.Use(ParityWorld.Corpse);
            Record(transcript, "second", second);
            transcript.Record("busy", items.IsBusy);
            // The first walk is still under way, so the second ask is held
            // back rather than arming a second walk at the same corpse.
            Assert.Equal(PluginItemCommandStatus.Busy, second.Status);
            Assert.True(items.IsBusy);
            Assert.Empty(Sent(arm, UseAction));
            transcript.RecordOutbound(arm);
        });

    /// <summary>
    /// Using a book lying loose on the ground, one with no use of its own.
    /// Using a loose item means picking it up, on both clients: the answer is
    /// a start, and what leaves the client is a pick-up into the character's
    /// own pack, never a use. Mutation check (2026-09-25), run: taking the
    /// pick-up out of the shared route turned this and the next scenario red
    /// on both arms (a refusal, and no pick-up on the wire).
    /// </summary>
    [Fact]
    public void UsingALooseBookPicksItUpTheSameOnBothClients() =>
        ParityScenario.Run(static (arm, transcript) =>
        {
            IItemAutomation items = StageWorld(arm);
            ParityWorld.Add(arm.Runtime, LooseBook, ParityWorld.PlayerX + 2f,
                new AcDream.Core.Items.ClientObject
                {
                    ObjectId = LooseBook,
                    Type = AcDream.Core.Items.ItemType.Writable,
                    Name = "Damaged Tome",
                    StackSize = 1,
                    Useability = AcDream.Core.Items.ItemUseability.No,
                });
            _ = arm.Operations.TakeOutbound();

            transcript.Step("use the book");
            PluginItemCommandResult use = items.Use(LooseBook);
            Record(transcript, "use", use);
            Assert.Equal(PluginItemCommandStatus.Started, use.Status);
            Assert.Empty(Sent(arm, UseAction));
            Assert.Equal(
                (LooseBook, ParityWorld.Player),
                WhatThePickUpNamed(arm));
            transcript.RecordOutbound(arm);
        });

    /// <summary>
    /// Using something inside the corpse the character has open picks it up
    /// the same way, on both clients.
    /// </summary>
    [Fact]
    public void UsingSomethingInTheOpenCorpsePicksItUpTheSameOnBothClients() =>
        ParityScenario.Run(static (arm, transcript) =>
        {
            IItemAutomation items = StageWorld(arm);
            Assert.True(arm.Runtime.InventoryOwner.ExternalContainers
                .RequestOpen(ParityWorld.Corpse, isCorpse: true));
            ParityWorld.DeliverCorpseContents(arm.Runtime);
            _ = arm.Operations.TakeOutbound();

            transcript.Step("use the gem inside");
            PluginItemCommandResult use = items.Use(ParityWorld.CorpseGem);
            Record(transcript, "use", use);
            Assert.Equal(PluginItemCommandStatus.Started, use.Status);
            Assert.Empty(Sent(arm, UseAction));
            Assert.Equal(
                (ParityWorld.CorpseGem, ParityWorld.Player),
                WhatThePickUpNamed(arm));
            transcript.RecordOutbound(arm);
        });

    /// <summary>A book on the ground two metres out, staged by the scenario that uses it.</summary>
    private const uint LooseBook = 0x50000060u;

    /// <summary>The client action that says "put that in this container".</summary>
    private const uint PickUpAction = 0x0019u;

    /// <summary>What the one pick-up this arm sent named: the item and where it goes.</summary>
    private static (uint Item, uint Container) WhatThePickUpNamed(ParityArm arm)
    {
        ParityOutbound message = Assert.Single(Sent(arm, PickUpAction));
        byte[] body = Convert.FromHexString(message.Body);
        return (
            System.Buffers.Binary.BinaryPrimitives.ReadUInt32LittleEndian(body.AsSpan(12)),
            System.Buffers.Binary.BinaryPrimitives.ReadUInt32LittleEndian(body.AsSpan(16)));
    }

    /// <summary>The client action that says "use that".</summary>
    private const uint UseAction = 0x0036u;

    /// <summary>
    /// Every use this arm has asked to send. Counting messages will not do
    /// it: a character walking to a corpse is telling the server where it is
    /// the whole time.
    /// </summary>
    private static IReadOnlyList<ParityOutbound> Sent(ParityArm arm, uint action)
        => [.. arm.Operations.Outbound.Where(
            message => message.GameAction == action)];

    /// <summary>What the one use this arm sent named.</summary>
    private static uint WhatTheUseNamed(ParityArm arm)
    {
        ParityOutbound message = Assert.Single(Sent(arm, UseAction));
        byte[] body = Convert.FromHexString(message.Body);
        return System.Buffers.Binary.BinaryPrimitives
            .ReadUInt32LittleEndian(body.AsSpan(12));
    }

    private static IItemAutomation StageWorld(ParityArm arm)
    {
        _ = ParityWorld.Stage(arm);
        ParityWorld.StageCorpse(arm.Runtime);
        _ = arm.Operations.TakeOutbound();
        return arm.Host.Automation.Items;
    }

    private static void Record(
        ParityTranscript transcript, string key, PluginItemCommandResult result)
    {
        transcript.Record($"{key}.status", result.Status.ToString());
        transcript.Record($"{key}.notice", result.Notice);
    }
}
