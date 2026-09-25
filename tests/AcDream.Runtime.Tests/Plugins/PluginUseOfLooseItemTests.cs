using AcDream.Core.Items;
using AcDream.Plugin.Abstractions;
using AcDream.Runtime.Gameplay;
using AcDream.Runtime.Plugins;

namespace AcDream.Runtime.Tests.Plugins;

/// <summary>
/// A plugin's plain Use of an item lying loose on the ground, or of an item in
/// the container the character has open, picks it up into the pack -- the
/// same thing a double-click on it does -- rather than being refused because
/// the item has no use of its own. The walk-then-use route under test is the
/// runtime owner both clients hand plugins; the pick-up it asks for is the
/// runtime's own placement, here captured where a host would send it.
///
/// Mutation check, run 2026-09-25: removing the pick-up branch from the
/// world-object use route turns the three pick-up tests red (NotUseable for
/// the book and the loot, a bare use for the potion).
/// </summary>
public sealed class PluginUseOfLooseItemTests
{
    private const uint Player = 0x5000000Au;
    private const uint Tome = 0x8000E5AEu;
    private const uint Potion = 0x8000E5AFu;
    private const uint Corpse = 0x8000E868u;
    private const uint Jaw = 0x8000E869u;

    [Fact]
    public void UsingALooseBookWithNoUsePicksItUpIntoThePack()
    {
        using var f = new Fixture();
        f.Objects.AddOrUpdate(new ClientObject
        {
            ObjectId = Tome,
            Name = "Damaged Yalaini Tome",
            Type = ItemType.Writable,
            Useability = ItemUseability.No,
        });

        AutomationUseOutcome outcome = f.WorldObjectUse.TryUse(Tome);

        Assert.Equal(AutomationUseOutcome.Started, outcome);
        Assert.Equal(new[] { (Tome, Player, 0) }, f.Pickups);
        Assert.Empty(f.Transport.Uses);
    }

    [Fact]
    public void UsingAnItemInTheOpenCorpsePicksItUpIntoThePack()
    {
        using var f = new Fixture { GroundObjectId = Corpse };
        f.Objects.AddOrUpdate(new ClientObject
        {
            ObjectId = Corpse,
            Name = "Corpse of Insatiable Eater",
            Type = ItemType.Container,
            PublicWeenieBitfield = (uint)(
                PublicWeenieFlags.Corpse | PublicWeenieFlags.Openable),
            Useability = ItemUseability.Remote,
        });
        f.Objects.AddOrUpdate(new ClientObject
        {
            ObjectId = Jaw,
            Name = "Insatiable Eater Jaw",
            Type = ItemType.Misc,
            ContainerId = Corpse,
        });

        AutomationUseOutcome outcome = f.WorldObjectUse.TryUse(Jaw);

        Assert.Equal(AutomationUseOutcome.Started, outcome);
        Assert.Equal(new[] { (Jaw, Player, 0) }, f.Pickups);
        Assert.Empty(f.Transport.Uses);
    }

    [Fact]
    public void UsingAUseableLooseItemPicksItUpRatherThanUsingIt()
    {
        using var f = new Fixture();
        f.Objects.AddOrUpdate(new ClientObject
        {
            ObjectId = Potion,
            Name = "Health Potion",
            Type = ItemType.Food,
            Useability = ItemUseability.Contained,
        });

        AutomationUseOutcome outcome = f.WorldObjectUse.TryUse(Potion);

        Assert.Equal(AutomationUseOutcome.Started, outcome);
        Assert.Equal(new[] { (Potion, Player, 0) }, f.Pickups);
        Assert.Empty(f.Transport.Uses);
    }

    [Fact]
    public void UsingAFixtureWithNoUseIsStillRefused()
    {
        // Something fixed in place is not loose, so nothing is picked up.
        using var f = new Fixture();
        f.Objects.AddOrUpdate(new ClientObject
        {
            ObjectId = Tome,
            Name = "Statue",
            Type = ItemType.Misc,
            PublicWeenieBitfield = (uint)PublicWeenieFlags.Stuck,
            Useability = ItemUseability.No,
        });

        AutomationUseOutcome outcome = f.WorldObjectUse.TryUse(Tome);

        Assert.Equal(AutomationUseOutcome.NotUseable, outcome);
        Assert.Empty(f.Pickups);
        Assert.Empty(f.Transport.Uses);
    }

    [Fact]
    public void APickUpWithNoRoomIsRefusedWithTheReason()
    {
        PluginItemCommandResult result =
            RuntimeAutomationSurface.MapWorldObjectUseOutcome(
                AutomationUseOutcome.NoRoom);

        Assert.Equal(PluginItemCommandStatus.Refused, result.Status);
        Assert.False(string.IsNullOrEmpty(result.Notice));
    }

    private sealed class Fixture : IDisposable
    {
        public Fixture()
        {
            Objects.AddOrUpdate(new ClientObject
            {
                ObjectId = Player,
                Name = "Player",
                Type = ItemType.Creature,
            });
            Interaction = new RuntimeItemInteraction(
                Objects,
                new RuntimeInteractionTransactionState(
                    new InventoryTransactionState(Objects)),
                new InteractionState(),
                playerGuid: () => Player,
                sendUse: null,
                sendUseWithTarget: null,
                sendWield: null,
                sendDrop: null,
                groundObjectId: () => GroundObjectId,
                placeInBackpack: (item, container, placement) =>
                    Pickups.Add((item, container, placement)));
            WorldObjectUse = new RuntimeWorldObjectUse(
                Interaction,
                Transport,
                new WithinReach(),
                guid => Objects.Get(guid)?.Useability);
        }

        internal uint GroundObjectId { get; set; }
        internal ClientObjectTable Objects { get; } = new();
        internal RuntimeItemInteraction Interaction { get; }
        internal RuntimeWorldObjectUse WorldObjectUse { get; }
        internal Transport Transport { get; } = new();
        internal List<(uint Item, uint Container, int Placement)> Pickups { get; } = [];

        public void Dispose() => Interaction.Dispose();
    }

    private sealed class Transport : IRuntimeInteractionTransport
    {
        private uint _sequence;

        public List<uint> Uses { get; } = [];

        public bool IsInWorld => true;

        public bool TrySendUse(uint serverGuid, out uint sequence)
        {
            sequence = ++_sequence;
            Uses.Add(serverGuid);
            return true;
        }

        public bool TrySendPickup(
            uint itemGuid,
            uint destinationContainerId,
            int placement,
            out uint sequence)
        {
            sequence = ++_sequence;
            return true;
        }
    }

    // Everything is already within reach, so no walk is ever begun.
    private sealed class WithinReach : IRuntimeApproachSource
    {
        public bool TryPlanApproach(uint serverGuid, out RuntimeApproachPlan plan)
        {
            plan = new RuntimeApproachPlan(
                serverGuid, 0u, default, 0u, 0.6f, true, false, 0f, 0f);
            return true;
        }

        public bool BeginApproach(
            in RuntimeApproachPlan plan,
            Action<RuntimeInteractionApproachToken>? arm = null) => false;

        public uint? StalledTicks() => null;

        public void CancelApproach()
        {
        }
    }
}
