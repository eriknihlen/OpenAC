using AcDream.Plugin.Abstractions;

namespace AcDream.HostParity.Tests;

/// <summary>
/// The item commands, run against both clients. Until now one client answered
/// them through the runtime's item owner and the other through a second
/// implementation written beside it, so a plugin that used an item, salvaged,
/// sold, or waited on an equipment switch got a different answer depending on
/// which client it happened to be loaded into, and nothing compared the two.
/// Both now answer from the one owner, and these scenarios are what says so.
///
/// Every step is recorded AND asserted: what the client answered the plugin,
/// and what left the client for the server. Recording alone compares the two
/// clients and nothing else, and these scenarios showed why that is not
/// enough -- the apply and the salvage were both refused on BOTH clients, for
/// want of a material and a target kind in the staging, and the two
/// transcripts agreed about it line for line.
///
/// Mutation check (2026-09-21), run: handing a plugin an empty list for the
/// container it has open turned <see cref="ACorpseCycleLooksTheSameOnBothClients"/>
/// red on both arms at once, with the two things in the corpse against
/// nothing; the transcripts still agreed, so only the assertion caught it.
///
/// Mutation check (2026-09-20), run: making the shared binding pass hand one
/// arm a <c>Use</c> that always refuses turned every scenario in this file
/// red, on the status lines and on the outbound counts. Separately, stamping
/// the pacing between two uses off a wall clock on one arm instead of the
/// shared simulation clock turned the paced-use scenario and the corpse cycle
/// red. Restoring each turned them green.
/// </summary>
public sealed class ItemParityTests
{
    /// <summary>Long enough for the pacing between two uses to lapse.</summary>
    private const int TicksPastTheUsePacing = 40;

    /// <summary>Past the bound on waiting for a description, at the shared step.</summary>
    private const int TicksPastTheDescriptionWait = 400;

    /// <summary>
    /// A loot rule matching a rare reads the icon drawn beneath the item's
    /// icon, so both icon layers reach a plugin, on both clients, as the
    /// full icon ids the object carries.
    /// </summary>
    [Fact]
    public void AnItemsIconLayersReachAPluginOnBothClients() =>
        ParityScenario.Run(static (arm, transcript) =>
        {
            IItemAutomation items = StageAndTakeItems(arm);

            PluginInventoryItem kit = Assert.Single(
                items.CaptureOwnedItems(),
                static item => item.ObjectId == ParityWorld.Kit);
            transcript.Record("kit.underlay", kit.IconUnderlayId);
            transcript.Record("kit.overlay", kit.IconOverlayId);
            Assert.Equal(ParityWorld.KitIconUnderlay, kit.IconUnderlayId);
            Assert.Equal(ParityWorld.KitIconOverlay, kit.IconOverlayId);
            transcript.Record("kit.coverage", kit.CoverageMask);
            transcript.Record("kit.plural", kit.PluralName);
            Assert.Equal(ParityWorld.KitCoverage, kit.CoverageMask);
            Assert.Equal("Healing Kits", kit.PluralName);

            // The same bits on the world-object record, before any appraisal.
            Assert.True(arm.Host.Automation.Objects.TryGet(
                ParityWorld.Kit, out PluginWorldObject kitObject));
            transcript.Record("kit.worldCoverage", kitObject.CoverageMask);
            Assert.False(kitObject.HasAppraisalData);
            Assert.Equal(ParityWorld.KitCoverage, kitObject.CoverageMask);
        });

    [Fact]
    public void UsingACarriedItemLooksTheSameOnBothClients() =>
        ParityScenario.Run(static (arm, transcript) =>
        {
            IItemAutomation items = StageAndTakeItems(arm);

            transcript.Step("before");
            RecordItemState(transcript, items);

            // Nothing is in flight and the character carries what the
            // scenario staged: two clients that both had no items at all
            // would agree about the rest of this and prove nothing.
            Assert.True(items.IsAvailable, $"{arm.Name} has no item surface.");
            Assert.False(items.IsBusy);
            Assert.Equal(CarriedItems, items.CaptureOwnedItems().Count);

            transcript.Step("use the kit");
            PluginItemCommandResult use = items.Use(ParityWorld.Kit);
            Record(transcript, "use", use);
            RecordItemState(transcript, items);
            // The use really left the client, naming the kit. A client that
            // answered Started and sent nothing would agree with one that
            // sent it, and a plugin would wait for ever.
            Assert.Equal(PluginItemCommandStatus.Started, use.Status);
            Assert.True(items.IsBusy);
            Assert.Equal(ParityWorld.Kit, WhatTheUseNamed(arm));
            transcript.RecordOutbound(arm);

            transcript.Step("the server answers");
            arm.Server.UseDone();
            arm.Advance();
            RecordItemState(transcript, items);
            transcript.Record("completion.error", items.LastCompletion.WeenieError);
            transcript.Record(
                "completion.source", items.LastCompletion.SourceObjectId);
            transcript.RecordOutbound(arm);
            // The completion a plugin reads stays empty on both clients and
            // is recorded rather than asserted: it is armed by the route a
            // click on something in the WORLD takes, and a carried item's
            // use does not arm it, so the server's answer to this use never
            // reaches it. Making it assertable means arming it where a
            // carried item's use is dispatched, which is a client change and
            // not a test one.

            transcript.Step("and the next use is let through");
            for (int step = 0; step < TicksPastTheUsePacing; step++)
                arm.Advance();
            PluginItemCommandResult again = items.Use(ParityWorld.Kit);
            Record(transcript, "use", again);
            RecordItemState(transcript, items);
            // What the client really did with the answer: the pacing has
            // lapsed, the next use is let through, and it goes out. Two
            // clients both stuck busy for the rest of the session would
            // agree with each other.
            Assert.Equal(PluginItemCommandStatus.Started, again.Status);
            Assert.Equal(ParityWorld.Kit, WhatTheUseNamed(arm));
            transcript.RecordOutbound(arm);
        });

    [Fact]
    public void ApplyingAnItemToATargetLooksTheSameOnBothClients() =>
        ParityScenario.Run(static (arm, transcript) =>
        {
            IItemAutomation items = StageAndTakeItems(arm);

            transcript.Step("apply the stone to the kit");
            PluginItemCommandResult applied =
                items.Apply(ParityWorld.TargetedItem, ParityWorld.Kit);
            Record(transcript, "apply", applied);
            RecordItemState(transcript, items);
            // The apply really went out, naming both halves of it. While the
            // stone was staged without a target kind this was refused on
            // BOTH clients, which passed the comparison and proved nothing.
            Assert.Equal(PluginItemCommandStatus.Started, applied.Status);
            Assert.True(items.IsBusy);
            Assert.Equal(
                (ParityWorld.TargetedItem, ParityWorld.Kit), TheApply(arm));
            transcript.RecordOutbound(arm);

            transcript.Step("apply it to something that is not there");
            PluginItemCommandResult nowhere =
                items.Apply(ParityWorld.TargetedItem, 0x5000_00FFu);
            Record(transcript, "apply", nowhere);
            // Turned away for the target, and nothing sent for it.
            Assert.Equal(PluginItemCommandStatus.InvalidTarget, nowhere.Status);
            Assert.Empty(Sent(arm, ApplyAction));
            transcript.RecordOutbound(arm);
        });

    /// <summary>
    /// Refusals. The reason text matters as much as the status: a bot reads
    /// it, and the client that answered a targeted item with a bare "no" told
    /// a bot nothing it could act on.
    /// </summary>
    [Fact]
    public void RefusingAnItemLooksTheSameOnBothClients() =>
        ParityScenario.Run(static (arm, transcript) =>
        {
            IItemAutomation items = StageAndTakeItems(arm);

            transcript.Step("use a targeted item with no target");
            PluginItemCommandResult bare = items.Use(ParityWorld.TargetedItem);
            Record(transcript, "use", bare);
            // The reason is the point of this one: a bot reads it, and a
            // bare "no" tells it nothing it can act on. Two bare "no"s agree
            // with each other perfectly.
            Assert.Equal(PluginItemCommandStatus.Refused, bare.Status);
            Assert.Equal(
                "This item requires a target; call Apply(objectId, targetObjectId) instead.",
                bare.Notice);

            transcript.Step("use something that is not there");
            PluginItemCommandResult missing = items.Use(0x5000_00FFu);
            Record(transcript, "use", missing);
            Assert.Equal(PluginItemCommandStatus.InvalidItem, missing.Status);

            transcript.Step("use a creature the player does not own");
            PluginItemCommandResult creature = items.Use(ParityWorld.Monster);
            Record(transcript, "use", creature);
            Assert.Equal(PluginItemCommandStatus.Refused, creature.Status);
            Assert.Equal("That cannot be used.", creature.Notice);
            // Three refusals, and nothing left either client for any of them.
            Assert.Empty(Sent(arm, UseAction));
            Assert.Empty(Sent(arm, ApplyAction));
            transcript.RecordOutbound(arm);
        });

    [Fact]
    public void MovingAnItemIntoAPackLooksTheSameOnBothClients() =>
        ParityScenario.Run(static (arm, transcript) =>
        {
            IItemAutomation items = StageAndTakeItems(arm);

            transcript.Step("move the kit into the side pack");
            PluginItemCommandResult move =
                items.MoveToContainer(ParityWorld.Kit, ParityWorld.SidePack);
            Record(transcript, "move", move);
            RecordItemState(transcript, items);
            // The move really went out, naming the thing and the pack it is
            // going into.
            Assert.Equal(PluginItemCommandStatus.Started, move.Status);
            Assert.Equal((ParityWorld.Kit, ParityWorld.SidePack), TheMove(arm));
            transcript.RecordOutbound(arm);

            transcript.Step("and again while the first is in flight");
            PluginItemCommandResult second = items.MoveToContainer(
                ParityWorld.ScrapItem, ParityWorld.SidePack);
            Record(transcript, "move", second);
            // One request at a time: the second is held back rather than
            // racing the first, and nothing went out for it.
            Assert.Equal(PluginItemCommandStatus.Busy, second.Status);
            Assert.Empty(Sent(arm, MoveAction));
            transcript.RecordOutbound(arm);
        });

    /// <summary>
    /// A move asked to join a stack pours into the matching stack already in
    /// the pack instead of taking a slot beside it; the same move not asked to
    /// join is the plain one. Both clients have to choose the same request, or
    /// a plugin tidying its packs ends up with a different pack on each.
    /// </summary>
    [Fact]
    public void MovingAStackOntoOneInAPackLooksTheSameOnBothClients() =>
        ParityScenario.Run(static (arm, transcript) =>
        {
            _ = ParityWorld.Stage(arm);
            ParityWorld.StageTwoStacksOfOneKind(arm.Runtime);
            _ = arm.Operations.TakeOutbound();
            IItemAutomation items = arm.Host.Automation.Items;

            transcript.Step("move part of the loose coins, joining a stack");
            PluginItemCommandResult join = items.MoveToContainer(
                ParityWorld.CarriedCoin,
                ParityWorld.SidePack,
                amount: 15u,
                placement: 0,
                joinStack: true);
            Record(transcript, "join", join);
            // The join really went out as a merge naming the stack in the
            // pack and the amount asked for, and no plain move went with it.
            Assert.Equal(PluginItemCommandStatus.Started, join.Status);
            Assert.Equal(
                (ParityWorld.CarriedCoin, ParityWorld.SidePackCoin, 15u),
                TheMerge(arm));
            Assert.Empty(Sent(arm, MoveAction));
            Assert.Empty(Sent(arm, SplitToContainerAction));
            transcript.RecordOutbound(arm);
        });

    /// <summary>The same move, not asked to join: a plain split into the pack.</summary>
    [Fact]
    public void MovingAStackWithoutJoiningLooksTheSameOnBothClients() =>
        ParityScenario.Run(static (arm, transcript) =>
        {
            _ = ParityWorld.Stage(arm);
            ParityWorld.StageTwoStacksOfOneKind(arm.Runtime);
            _ = arm.Operations.TakeOutbound();
            IItemAutomation items = arm.Host.Automation.Items;

            transcript.Step("move part of the loose coins, not joining");
            PluginItemCommandResult split = items.MoveToContainer(
                ParityWorld.CarriedCoin,
                ParityWorld.SidePack,
                amount: 15u,
                placement: 0,
                joinStack: false);
            Record(transcript, "split", split);
            Assert.Equal(PluginItemCommandStatus.Started, split.Status);
            Assert.Empty(Sent(arm, MergeAction));
            ParityOutbound message =
                Assert.Single(Sent(arm, SplitToContainerAction));
            Assert.Equal(ParityWorld.CarriedCoin, Field(message, 12));
            Assert.Equal(ParityWorld.SidePack, Field(message, 16));
            transcript.RecordOutbound(arm);
        });

    [Fact]
    public void SalvagingAndSellingLookTheSameOnBothClients() =>
        ParityScenario.Run(static (arm, transcript) =>
        {
            IItemAutomation items = StageAndTakeItems(arm);

            transcript.Step("salvage the scrap with the tool");
            PluginItemCommandResult salvage =
                items.Salvage(ParityWorld.SalvageTool, [ParityWorld.ScrapItem]);
            Record(transcript, "salvage", salvage);
            // The salvage really went out, naming the tool and the one thing
            // fed to it. While the scrap was staged without a material this
            // was refused on BOTH clients and the comparison passed.
            Assert.Equal(PluginItemCommandStatus.Started, salvage.Status);
            Assert.Equal(
                (ParityWorld.SalvageTool, ParityWorld.ScrapItem),
                TheSalvage(arm));
            transcript.RecordOutbound(arm);

            transcript.Step("salvage with something that is not a tool");
            PluginItemCommandResult notATool =
                items.Salvage(ParityWorld.Kit, [ParityWorld.ScrapItem]);
            Record(transcript, "salvage", notATool);
            Assert.Equal(PluginItemCommandStatus.InvalidTarget, notATool.Status);

            transcript.Step("sell with no vendor trading");
            transcript.Record("vendor", items.ActiveVendorObjectId);
            PluginItemCommandResult sell = items.Sell(ParityWorld.ScrapItem);
            Record(transcript, "sell", sell);
            // No vendor is open, the reason says so, and nothing is sent.
            Assert.Equal(0u, items.ActiveVendorObjectId);
            Assert.Equal(PluginItemCommandStatus.InvalidTarget, sell.Status);
            Assert.Equal("No vendor is open.", sell.Notice);
            Assert.Empty(Sent(arm, SalvageAction));
            transcript.RecordOutbound(arm);
        });

    /// <summary>
    /// Two uses close together. The second is early, not failed, and both
    /// clients have to say so off the same clock: a client stamping the
    /// pacing from a wall clock and one stamping it from the simulation clock
    /// disagree about every use that follows the first.
    /// </summary>
    [Fact]
    public void APacedUseIsReportedBusyOnBothClients() =>
        ParityScenario.Run(static (arm, transcript) =>
        {
            _ = StageAndTakeItems(arm);
            ILootAutomation loot = arm.Host.Automation.Loot;
            // At arm's length, so the open sends its use there and then.
            // Three metres off it begins a walk instead, and then every step
            // below answers busy on both clients -- which agrees line for
            // line and says nothing about the pacing this is about.
            ParityWorld.StageCorpse(arm.Runtime, ParityWorld.WithinArmsReach);

            transcript.Step("open the corpse");
            PluginItemCommandResult first = loot.Open(ParityWorld.Corpse);
            Record(transcript, "open", first);
            Assert.Equal(PluginItemCommandStatus.Started, first.Status);
            Assert.Equal(ParityWorld.Corpse, WhatTheUseNamed(arm));
            transcript.RecordOutbound(arm);

            transcript.Step("open it again straight away");
            PluginItemCommandResult tooSoon = loot.Open(ParityWorld.Corpse);
            Record(transcript, "open", tooSoon);
            // Early, not failed -- and nothing went out for it.
            Assert.Equal(PluginItemCommandStatus.Busy, tooSoon.Status);
            Assert.Empty(Sent(arm, UseAction));
            transcript.RecordOutbound(arm);

            transcript.Step("and again once the pacing has lapsed");
            arm.Server.UseDone();
            for (int step = 0; step < TicksPastTheUsePacing; step++)
                arm.Advance();
            PluginItemCommandResult later = loot.Open(ParityWorld.Corpse);
            Record(transcript, "open", later);
            // The pacing is measured off the one simulation clock both
            // clients advance, so simulated waiting really reaches it and
            // the third open goes out.
            Assert.Equal(PluginItemCommandStatus.Started, later.Status);
            Assert.Equal(ParityWorld.Corpse, WhatTheUseNamed(arm));
            transcript.RecordOutbound(arm);
        });

    /// <summary>
    /// The looting cycle a bot runs: open, wait for the contents, read them,
    /// look one over, take it, and try to close while the take is still in
    /// flight. Every step's busy state and container id is written down,
    /// because a bot branches on all of them.
    /// </summary>
    [Fact]
    public void ACorpseCycleLooksTheSameOnBothClients() =>
        ParityScenario.Run(static (arm, transcript) =>
        {
            _ = StageAndTakeItems(arm);
            ILootAutomation loot = arm.Host.Automation.Loot;
            // At arm's length. Three metres off, every step of this cycle
            // was refused on both clients -- the open began a walk, the
            // contents never arrived, and the gem was never there to look at
            // or take -- and the two transcripts agreed about all of it.
            ParityWorld.StageCorpse(arm.Runtime, ParityWorld.WithinArmsReach);

            transcript.Step("nothing open");
            RecordLootState(transcript, loot);
            transcript.Record("corpses", loot.CaptureCorpses(10f).Count);
            Assert.False(loot.IsBusy);
            Assert.Equal(0u, loot.CurrentContainerId);
            Assert.Equal(ParityWorld.Corpse, Assert.Single(
                loot.CaptureCorpses(10f)).ObjectId);

            transcript.Step("open it");
            PluginItemCommandResult open = loot.Open(ParityWorld.Corpse);
            Record(transcript, "open", open);
            RecordLootState(transcript, loot);
            // The open really went out, and the client is waiting on THIS
            // corpse rather than on nothing.
            Assert.Equal(PluginItemCommandStatus.Started, open.Status);
            Assert.Equal(ParityWorld.Corpse, loot.RequestedContainerId);
            Assert.Equal(ParityWorld.Corpse, WhatTheUseNamed(arm));
            transcript.RecordOutbound(arm);

            transcript.Step("the contents arrive");
            arm.Server.UseDone();
            arm.Deliver(static runtime =>
                ParityWorld.DeliverCorpseContents(runtime));
            arm.Advance();
            RecordLootState(transcript, loot);
            RecordContents(transcript, loot);
            // What is in the corpse, in the order the server listed it.
            Assert.Equal(ParityWorld.Corpse, loot.CurrentContainerId);
            Assert.True(
                loot.CurrentContentsReady,
                $"{arm.Name} never finished reading the corpse.");
            Assert.Equal(
                new[] { ParityWorld.CorpseCoin, ParityWorld.CorpseGem },
                loot.CaptureCurrentContents()
                    .Select(static item => item.ObjectId));

            transcript.Step("look the gem over");
            PluginItemCommandResult identify =
                loot.Identify(ParityWorld.CorpseGem);
            Record(transcript, "identify", identify);
            RecordAppraisal(transcript, loot);
            // The question went out and the client is waiting for the answer
            // about the gem: a plugin polls exactly this.
            Assert.Equal(PluginItemCommandStatus.Started, identify.Status);
            Assert.Equal(ParityWorld.CorpseGem, loot.Appraisal.AwaitingObjectId);
            transcript.RecordOutbound(arm);

            transcript.Step("the description comes back");
            arm.Deliver(static runtime =>
                runtime.ItemInteractionOwner.AcceptAppraisalResponse(
                    ParityWorld.CorpseGem));
            arm.Advance();
            RecordAppraisal(transcript, loot);
            // The wait is over and the answer is about the gem that was
            // asked about.
            Assert.Equal(0u, loot.Appraisal.AwaitingObjectId);
            Assert.Equal(ParityWorld.CorpseGem, loot.Appraisal.CurrentObjectId);

            transcript.Step("take the gem");
            for (int step = 0; step < TicksPastTheUsePacing; step++)
                arm.Advance();
            PluginItemCommandResult pickup = loot.Pickup(ParityWorld.CorpseGem);
            Record(transcript, "pickup", pickup);
            RecordLootState(transcript, loot);
            // The take really went out, naming the gem and the pack it is
            // going into.
            Assert.Equal(PluginItemCommandStatus.Started, pickup.Status);
            Assert.Equal(
                (ParityWorld.CorpseGem, ParityWorld.Player), TheMove(arm));
            transcript.RecordOutbound(arm);

            transcript.Step("try to close while the take is out");
            for (int step = 0; step < TicksPastTheUsePacing; step++)
                arm.Advance();
            PluginItemCommandResult close = loot.Close(ParityWorld.Corpse);
            Record(transcript, "close", close);
            RecordLootState(transcript, loot);
            // The take is still out, so the close waits its turn rather than
            // racing it, and the corpse stays open and read.
            Assert.Equal(PluginItemCommandStatus.Busy, close.Status);
            Assert.Equal(ParityWorld.Corpse, loot.CurrentContainerId);
            Assert.Empty(Sent(arm, MoveAction));
            transcript.RecordOutbound(arm);
        });

    /// <summary>
    /// A description nobody ever answers. One question is allowed out at a
    /// time, so the second is refused while the first is still out, and the
    /// wait is only let go of when somebody asks again -- not by a timer.
    /// Both clients have to hold and release the one slot at the same steps,
    /// or a bot polling for an answer stalls forever on one of them.
    ///
    /// The bound on that wait and the pacing between two uses beside it are
    /// now measured against one clock, the session's own simulation clock,
    /// so simulated waiting really does reach it: six simulated seconds of
    /// silence here end the wait, and the next question takes the slot.
    /// </summary>
    [Fact]
    public void AnUnansweredDescriptionExpiresTheSameOnBothClients() =>
        ParityScenario.Run(static (arm, transcript) =>
        {
            IItemAutomation items = StageAndTakeItems(arm);
            IWorldObjectAutomation objects = arm.Host.Automation.Objects;
            ILootAutomation loot = arm.Host.Automation.Loot;

            transcript.Step("ask about the kit");
            Record(transcript, "identify", objects.Identify(ParityWorld.Kit));
            RecordAppraisal(transcript, loot);
            transcript.RecordOutbound(arm);

            transcript.Step("ask about something else while the first is out");
            Record(transcript, "identify", objects.Identify(ParityWorld.ScrapItem));
            RecordAppraisal(transcript, loot);
            transcript.RecordOutbound(arm);

            transcript.Step("nothing ever answers");
            for (int step = 0; step < TicksPastTheDescriptionWait; step++)
                arm.Advance();
            RecordAppraisal(transcript, loot);
            transcript.Record("busy", items.IsBusy);

            // The wait has run out by now, so this is the step that takes the
            // slot over -- and the one that has to take it on both clients.
            transcript.Step("ask about something else after giving up");
            Record(transcript, "identify", objects.Identify(ParityWorld.ScrapItem));
            RecordAppraisal(transcript, loot);
            transcript.RecordOutbound(arm);
            // Both arms agreeing that nothing happened would pass the
            // comparison and prove nothing, so the one slot really has to
            // have moved off the question nobody answered.
            Assert.Equal(
                ParityWorld.ScrapItem, loot.Appraisal.AwaitingObjectId);
        });

    /// <summary>How many things the staged character carries.</summary>
    private const int CarriedItems = 5;

    /// <summary>The client actions these scenarios look for on the wire.</summary>
    private const uint UseAction = 0x0036u;
    private const uint ApplyAction = 0x0035u;
    private const uint MoveAction = 0x0019u;
    private const uint SalvageAction = 0x027Du;
    private const uint MergeAction = 0x0054u;
    private const uint SplitToContainerAction = 0x0055u;

    /// <summary>
    /// Everything this arm asked to send that carries one named client
    /// action. Counting messages will not do: a character standing in the
    /// world is telling the server where it is the whole time.
    /// </summary>
    private static IReadOnlyList<ParityOutbound> Sent(ParityArm arm, uint action)
        => [.. arm.Operations.Outbound.Where(
            message => message.GameAction == action)];

    /// <summary>One unsigned number out of a client action's body.</summary>
    private static uint Field(ParityOutbound message, int offset)
    {
        byte[] body = Convert.FromHexString(message.Body);
        return System.Buffers.Binary.BinaryPrimitives
            .ReadUInt32LittleEndian(body.AsSpan(offset));
    }

    /// <summary>What the one use this arm sent named.</summary>
    private static uint WhatTheUseNamed(ParityArm arm) =>
        Field(Assert.Single(Sent(arm, UseAction)), 12);

    /// <summary>What the one apply named: the thing, then what it was used on.</summary>
    private static (uint Item, uint Target) TheApply(ParityArm arm)
    {
        ParityOutbound message = Assert.Single(Sent(arm, ApplyAction));
        return (Field(message, 12), Field(message, 16));
    }

    /// <summary>What the one move named: the thing, then the pack.</summary>
    private static (uint Item, uint Container) TheMove(ParityArm arm)
    {
        ParityOutbound message = Assert.Single(Sent(arm, MoveAction));
        return (Field(message, 12), Field(message, 16));
    }

    /// <summary>What the one merge named: the stack poured, the stack joined, the amount.</summary>
    private static (uint Source, uint Target, uint Amount) TheMerge(ParityArm arm)
    {
        ParityOutbound message = Assert.Single(Sent(arm, MergeAction));
        return (Field(message, 12), Field(message, 16), Field(message, 20));
    }

    /// <summary>
    /// What the one salvage named: the tool, then the first thing fed to it.
    /// The count of things comes between them.
    /// </summary>
    private static (uint Tool, uint Item) TheSalvage(ParityArm arm)
    {
        ParityOutbound message = Assert.Single(Sent(arm, SalvageAction));
        return (Field(message, 12), Field(message, 20));
    }

    /// <summary>
    /// Puts a character with a body and a full pack on this arm and hands
    /// back its item surface.
    /// </summary>
    private static IItemAutomation StageAndTakeItems(ParityArm arm)
    {
        _ = ParityWorld.Stage(arm);
        ParityWorld.StageCarriedItems(arm.Runtime);
        _ = arm.Operations.TakeOutbound();
        return arm.Host.Automation.Items;
    }

    private static void Record(
        ParityTranscript transcript, string key, PluginItemCommandResult result)
    {
        transcript.Record($"{key}.status", result.Status.ToString());
        transcript.Record($"{key}.notice", result.Notice);
    }

    private static void RecordItemState(
        ParityTranscript transcript, IItemAutomation items)
    {
        transcript.Record("available", items.IsAvailable);
        transcript.Record("busy", items.IsBusy);
        transcript.Record("owned", items.CaptureOwnedItems().Count);
    }

    private static void RecordLootState(
        ParityTranscript transcript, ILootAutomation loot)
    {
        transcript.Record("available", loot.IsAvailable);
        transcript.Record("busy", loot.IsBusy);
        transcript.Record("requested", loot.RequestedContainerId);
        transcript.Record("current", loot.CurrentContainerId);
        transcript.Record("ready", loot.CurrentContentsReady);
    }

    private static void RecordContents(
        ParityTranscript transcript, ILootAutomation loot)
    {
        IReadOnlyList<PluginInventoryItem> contents =
            loot.CaptureCurrentContents();
        transcript.Record("contents.count", contents.Count);
        for (int index = 0; index < contents.Count; index++)
        {
            transcript.Record($"contents[{index}].id", contents[index].ObjectId);
            transcript.Record($"contents[{index}].name", contents[index].Name);
        }
    }

    private static void RecordAppraisal(
        ParityTranscript transcript, ILootAutomation loot)
    {
        PluginAppraisalState appraisal = loot.Appraisal;
        transcript.Record("appraisal.awaiting", appraisal.AwaitingObjectId);
        transcript.Record("appraisal.current", appraisal.CurrentObjectId);
        transcript.Record("appraisal.abandoned", appraisal.LastAbandonedObjectId);
    }
}
