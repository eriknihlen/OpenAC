using System.Buffers.Binary;
using AcDream.Core.Items;
using AcDream.Core.Net;
using AcDream.Core.Net.Messages;
using AcDream.Plugin.Abstractions;
using AcDream.Runtime.Entities;

namespace AcDream.HostParity.Tests;

/// <summary>
/// An item the client still lists in the packs after the server has lost
/// it. A plugin finds one by asking the server to appraise every carried
/// item and noting the ones it refuses, then asks the client to let go of
/// them. Both halves -- the refusal a plugin reads, and the item leaving the
/// client -- have to come out the same on both clients, and the letting go
/// must not tell the server anything.
/// </summary>
public sealed class StaleItemParityTests
{
    /// <summary>A carried item the server no longer has.</summary>
    private const uint Ghost = 0x50000060u;

    /// <summary>A carried item the server still has.</summary>
    private const uint Keeper = 0x50000061u;

    /// <summary>
    /// Mutation checks, run 2026-09-25: reading the appraisal slot's flag as
    /// always false, and letting <c>ForgetStaleItem</c> answer before the
    /// item has really gone, each turn this red on both arms. Making the
    /// window's deletion controller refuse the delete turns the windowed arm
    /// red, and so does leaving the window's delete route unbound.
    /// </summary>
    [Fact]
    public void AnItemTheServerRefusesToAppraiseIsLetGoOfTheSameOnBothClients() =>
        ParityScenario.Run(static (arm, transcript) =>
        {
            IItemAutomation items = Stage(arm);
            IWorldObjectAutomation objects = arm.Host.Automation.Objects;
            ILootAutomation loot = arm.Host.Automation.Loot;

            transcript.Step("ask about the lost item; the server refuses");
            Record(transcript, "identify", objects.Identify(Ghost));
            arm.Server.AppraisalResponse(Ghost, [], success: false);
            arm.Advance();
            RecordAppraisal(transcript, loot);
            RecordObject(transcript, objects, Ghost);
            transcript.RecordOutbound(arm);
            Assert.Equal(Ghost, loot.Appraisal.CurrentObjectId);
            Assert.True(loot.Appraisal.CurrentObjectUnsuccessful);
            Assert.True(objects.TryGet(Ghost, out PluginWorldObject ghost));
            Assert.True(ghost.LastAppraisalUnsuccessful);

            transcript.Step("ask about the kept item; the server answers");
            Record(transcript, "identify", objects.Identify(Keeper));
            arm.Server.AppraisalResponse(Keeper, [(ValueProperty, 40)]);
            arm.Advance();
            RecordAppraisal(transcript, loot);
            RecordObject(transcript, objects, Keeper);
            transcript.RecordOutbound(arm);
            Assert.Equal(Keeper, loot.Appraisal.CurrentObjectId);
            Assert.False(loot.Appraisal.CurrentObjectUnsuccessful);

            transcript.Step("let go of the kept item");
            PluginItemCommandResult kept = items.ForgetStaleItem(Keeper);
            Record(transcript, "forget", kept);
            Assert.Equal(PluginItemCommandStatus.Refused, kept.Status);
            Assert.True(objects.TryGet(Keeper, out _));

            transcript.Step("let go of the lost item");
            _ = arm.Operations.TakeOutbound();
            int removals = 0;
            arm.Runtime.InventoryOwner.Objects.ObjectRemoved += removed =>
            {
                if (removed.ObjectId == Ghost)
                    removals++;
            };
            int windowDeletesBefore = arm is WindowedArm before ? before.WindowDeletes : 0;
            PluginItemCommandResult forgot = items.ForgetStaleItem(Ghost);
            Record(transcript, "forget", forgot);
            transcript.Record("removed-events", removals);
            // The windowed client lets go through its own delete route, the
            // one a server delete takes there; the windowless client retires
            // the object itself. Both must land in the same place.
            if (arm is WindowedArm windowed)
            {
                Assert.Equal(1, windowed.WindowDeletes - windowDeletesBefore);
            }
            Assert.Equal(1, removals);
            Assert.DoesNotContain(
                arm.Runtime.InventoryOwner.Objects.GetContents(ParityWorld.Player),
                static id => id == Ghost);
            RecordObject(transcript, objects, Ghost);
            transcript.Record(
                "owned.has-ghost",
                items.CaptureOwnedItems().Any(static item => item.ObjectId == Ghost));
            Assert.Equal(PluginItemCommandStatus.Completed, forgot.Status);
            Assert.False(
                objects.TryGet(Ghost, out _),
                $"{arm.Name}: the lost item is still known.");
            Assert.DoesNotContain(
                items.CaptureOwnedItems(),
                static item => item.ObjectId == Ghost);
            Assert.False(
                arm.Runtime.EntityObjects.Entities.TryGetActive(Ghost, out _),
                $"{arm.Name}: the lost item kept its server record.");
            Assert.True(objects.TryGet(Keeper, out _));
            // Nothing about the item may reach the server: the client is
            // taking the server's silence at its word, not asking.
            string ghostBytes = LittleEndianHex(Ghost);
            Assert.DoesNotContain(
                arm.Operations.Outbound,
                message => message.Body.Contains(ghostBytes, StringComparison.Ordinal));
            transcript.RecordOutbound(arm);

            transcript.Step("let go of it a second time");
            PluginItemCommandResult again = items.ForgetStaleItem(Ghost);
            Record(transcript, "forget", again);
            Assert.Equal(PluginItemCommandStatus.InvalidItem, again.Status);

            transcript.Step("let go of the character");
            PluginItemCommandResult self = items.ForgetStaleItem(ParityWorld.Player);
            Record(transcript, "forget", self);
            Assert.Equal(PluginItemCommandStatus.InvalidItem, self.Status);
            Assert.True(objects.TryGet(ParityWorld.Player, out _));

            transcript.Step("let go of something not carried");
            PluginItemCommandResult monster = items.ForgetStaleItem(ParityWorld.Monster);
            Record(transcript, "forget", monster);
            Assert.Equal(PluginItemCommandStatus.InvalidItem, monster.Status);
            Assert.True(objects.TryGet(ParityWorld.Monster, out _));
            transcript.RecordOutbound(arm);
        });

    /// <summary>What the gem is worth, as the server states it.</summary>
    private const uint ValueProperty = 19u;

    private static IItemAutomation Stage(ParityArm arm)
    {
        _ = ParityWorld.Stage(arm);
        ParityWorld.StageCarriedItems(arm.Runtime);
        Carry(arm, Ghost, "Lost Gem");
        Carry(arm, Keeper, "Kept Gem");
        _ = arm.Operations.TakeOutbound();
        return arm.Host.Automation.Items;
    }

    /// <summary>
    /// A carried item as the server creates one: no place in the world, a
    /// container, and a server record behind it.
    /// </summary>
    private static void Carry(ParityArm arm, uint guid, string name)
    {
        var spawn = new WorldSession.EntitySpawn(
            guid,
            null,
            0x02000001u,
            Array.Empty<CreateObject.AnimPartChange>(),
            Array.Empty<CreateObject.TextureChange>(),
            Array.Empty<CreateObject.SubPaletteSwap>(),
            null,
            null,
            name,
            null,
            null,
            null,
            PhysicsState: 0,
            InstanceSequence: 1,
            MovementSequence: 1,
            ServerControlSequence: 1,
            PositionSequence: 1) with
        {
            ContainerId = ParityWorld.Player,
        };
        RuntimeEntityRecord record = arm.Runtime.EntityObjects
            .RegisterEntity(spawn)
            .Canonical!;
        Assert.True(arm.Runtime.EntityObjects.ApplyAcceptedSpawn(
            record,
            record.CreateIntegrationVersion,
            record.Snapshot,
            replaceGeneration: false));
        ClientObject? item = arm.Runtime.InventoryOwner.Objects.Get(guid);
        Assert.NotNull(item);
        Assert.Equal(ParityWorld.Player, item!.ContainerId);
    }

    private static string LittleEndianHex(uint value)
    {
        var bytes = new byte[4];
        BinaryPrimitives.WriteUInt32LittleEndian(bytes, value);
        return Convert.ToHexString(bytes);
    }

    private static void Record(
        ParityTranscript transcript, string key, PluginItemCommandResult result)
    {
        transcript.Record($"{key}.status", result.Status.ToString());
        transcript.Record($"{key}.notice", result.Notice);
    }

    private static void RecordAppraisal(
        ParityTranscript transcript, ILootAutomation loot)
    {
        PluginAppraisalState appraisal = loot.Appraisal;
        transcript.Record("appraisal.awaiting", appraisal.AwaitingObjectId);
        transcript.Record("appraisal.current", appraisal.CurrentObjectId);
        transcript.Record("appraisal.unsuccessful", appraisal.CurrentObjectUnsuccessful);
    }

    private static void RecordObject(
        ParityTranscript transcript, IWorldObjectAutomation objects, uint objectId)
    {
        bool known = objects.TryGet(objectId, out PluginWorldObject value);
        transcript.Record($"known[0x{objectId:X8}]", known);
        transcript.Record(
            $"unsuccessful[0x{objectId:X8}]",
            known && value.LastAppraisalUnsuccessful);
    }
}
