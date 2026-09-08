using System.Collections.Generic;
using AcDream.App.UI;
using AcDream.App.UI.Layout;
using AcDream.App.UI.Testing;
using AcDream.Core.Items;

namespace AcDream.App.Tests.UI;

public sealed class RetailUiInteractionFlowTests
{
    private const uint Player = 0x50000001u;
    private const uint Hauberk = 0x50001001u;
    private const uint HealthKit = 0x50001002u;
    private const uint Sword = 0x50001003u;
    private const uint Bow = 0x50001004u;
    private const uint SlotsButtonId = 0x100005BEu;
    private const uint ChestArmorSlotId = 0x100005ACu;
    private const uint HealthKitUseability = 0x00220008u;

    private const EquipMask HauberkMask =
        EquipMask.ChestArmor
        | EquipMask.AbdomenArmor
        | EquipMask.UpperArmArmor
        | EquipMask.LowerArmArmor;

    private sealed class TestElement : UiElement { }

    private sealed class Harness
    {
        public readonly UiRoot Root = new() { Width = 800, Height = 600 };
        public readonly ClientObjectTable Objects = new();
        public readonly ImportedLayout Layout;
        public readonly List<uint> Uses = new();
        public readonly List<(uint Source, uint Target)> UseWithTarget = new();
        public readonly List<(uint Item, uint Mask)> Wields = new();
        public readonly List<(uint Item, uint Container, int Placement)> Puts = new();
        public readonly AcDream.Core.Selection.SelectionState Selection = new();
        public readonly List<uint> Drops = new();
        public long Now = 10_000;

        public Harness()
        {
            var layoutRoot = new TestElement { Width = 360, Height = 180 };
            var grid = ItemList(
                InventoryController.ContentsGridId,
                left: 10,
                top: 10,
                width: 192,
                height: 96);
            var sideBags = ItemList(
                InventoryController.ContainerListId,
                left: 210,
                top: 10,
                width: 36,
                height: 96);
            var mainPack = ItemList(
                InventoryController.TopContainerId,
                left: 210,
                top: 112,
                width: 36,
                height: 36);
            var chestArmor = ItemList(
                ChestArmorSlotId,
                left: 280,
                top: 10,
                width: 32,
                height: 32);
            var slotsButton = new UiButton(
                new ElementInfo { Id = SlotsButtonId, Type = 1 },
                static _ => (0u, 0, 0))
            {
                DatElementId = SlotsButtonId,
                Left = 270,
                Top = 56,
                Width = 56,
                Height = 20,
            };
            var dollViewport = new UiViewport
            {
                DatElementId = PaperdollController.DollViewportId,
                Left = 270,
                Top = 80,
                Width = 70,
                Height = 90,
            };
            var dollDragMask = new UiButton(
                new ElementInfo { Id = PaperdollController.DollDragMaskId, Type = 1 },
                static _ => (0u, 0, 0))
            {
                DatElementId = PaperdollController.DollDragMaskId,
                Left = 270,
                Top = 80,
                Width = 70,
                Height = 90,
                ZOrder = 1,
            };
            var meter = new UiMeter
            {
                DatElementId = InventoryController.BurdenMeterId,
                Left = 248,
                Top = 10,
                Width = 11,
                Height = 58,
            };
            var titleText = TextHost(InventoryController.TitleTextId, 10, 116, 192, 14);
            var burdenText = TextHost(InventoryController.BurdenTextId, 248, 72, 36, 14);
            var burdenCaption = TextHost(InventoryController.BurdenCaptionId, 248, 88, 36, 14);
            var contentsCaption = TextHost(InventoryController.ContentsCaptionId, 10, 132, 192, 14);
            var scrollbar = new UiScrollbar
            {
                DatElementId = InventoryController.ContentsScrollbarId,
                Left = 192,
                Top = 10,
                Width = 16,
                Height = 96,
            };

            var byId = new Dictionary<uint, UiElement>
            {
                [InventoryController.ContentsGridId] = grid,
                [InventoryController.ContainerListId] = sideBags,
                [InventoryController.TopContainerId] = mainPack,
                [InventoryController.BurdenMeterId] = meter,
                [InventoryController.TitleTextId] = titleText,
                [InventoryController.BurdenTextId] = burdenText,
                [InventoryController.BurdenCaptionId] = burdenCaption,
                [InventoryController.ContentsCaptionId] = contentsCaption,
                [InventoryController.ContentsScrollbarId] = scrollbar,
                [ChestArmorSlotId] = chestArmor,
                [SlotsButtonId] = slotsButton,
                [PaperdollController.DollViewportId] = dollViewport,
                [PaperdollController.DollDragMaskId] = dollDragMask,
            };

            layoutRoot.AddChild(grid);
            layoutRoot.AddChild(sideBags);
            layoutRoot.AddChild(mainPack);
            layoutRoot.AddChild(chestArmor);
            layoutRoot.AddChild(slotsButton);
            layoutRoot.AddChild(dollViewport);
            layoutRoot.AddChild(dollDragMask);
            layoutRoot.AddChild(meter);
            layoutRoot.AddChild(titleText);
            layoutRoot.AddChild(burdenText);
            layoutRoot.AddChild(burdenCaption);
            layoutRoot.AddChild(contentsCaption);
            layoutRoot.AddChild(scrollbar);
            Layout = new ImportedLayout(layoutRoot, byId);
            Root.AddChild(layoutRoot);

            Objects.AddOrUpdate(new ClientObject
            {
                ObjectId = Player,
                Name = "Player",
                Type = ItemType.Creature,
                ItemsCapacity = 102,
                ContainersCapacity = 7,
            });
        }

        public ItemInteractionController BindInventoryInteraction()
        {
            var interaction = new ItemInteractionController(
                Objects,
                new AcDream.Runtime.Gameplay.RuntimeInteractionTransactionState(new InventoryTransactionState(Objects)),
                new InteractionState(),
                playerGuid: () => Player,
                sendUse: Uses.Add,
                sendUseWithTarget: (source, target) => UseWithTarget.Add((source, target)),
                sendWield: (item, mask) => Wields.Add((item, mask)),
                sendDrop: Drops.Add,
                nowMs: () => Now,
                sendPutItemInContainer: (item, container, placement) =>
                    Puts.Add((item, container, placement)));

            InventoryController.Bind(
                Layout,
                Objects,
                playerGuid: () => Player,
                iconIds: static (_, _, _, _, _) => 0x1234u,
                strength: () => 100,
                selection: Selection,
                datFont: null,
                itemInteraction: interaction);

            Root.DragReleasedOutsideUi += (payload, _, _) =>
            {
                if (payload is ItemDragPayload itemPayload)
                    interaction.DropToWorld(itemPayload);
            };

            return interaction;
        }

        public void BindPaperdoll(
            ItemInteractionController? itemInteraction = null,
            PaperdollClickMap? clickMap = null)
        {
            itemInteraction ??= new ItemInteractionController(
                Objects,
                new AcDream.Runtime.Gameplay.RuntimeInteractionTransactionState(new InventoryTransactionState(Objects)),
                new InteractionState(),
                () => Player,
                sendUse: null,
                sendUseWithTarget: null,
                sendWield: (item, mask) => Wields.Add((item, mask)),
                sendDrop: null,
                sendPutItemInContainer: (item, container, placement) =>
                    Puts.Add((item, container, placement)));
            PaperdollController.Bind(
                Layout,
                Objects,
                playerGuid: () => Player,
                iconIds: static (_, _, _, _, _) => 0x1234u,
                selection: Selection,
                itemInteraction: itemInteraction,
                emptySlotSprite: 0x06004D20u,
                clickMap: clickMap ?? SolidClickMap(0x00, 0x00, 0xFF));
        }

        public static PaperdollClickMap SolidClickMap(byte r, byte g, byte b)
        {
            const int width = 70;
            const int height = 90;
            var pixels = new byte[width * height * 4];
            for (int i = 0; i < pixels.Length; i += 4)
            {
                pixels[i] = r;
                pixels[i + 1] = g;
                pixels[i + 2] = b;
                pixels[i + 3] = 0xFF;
            }
            return new PaperdollClickMap(pixels, width, height);
        }

        public RetailUiAutomationProbe Probe()
            => new(Root, Objects);

        public void SeedHauberk(int slot = 0)
        {
            Objects.AddOrUpdate(new ClientObject
            {
                ObjectId = Hauberk,
                Name = "Hauberk",
                Type = ItemType.Armor,
                ValidLocations = HauberkMask,
                IconId = 0x06001234u,
            });
            Objects.MoveItem(Hauberk, Player, slot);
        }

        public void SeedHealthKit(int slot = 0)
        {
            Objects.AddOrUpdate(new ClientObject
            {
                ObjectId = HealthKit,
                Name = "Health Kit",
                Type = ItemType.Misc,
                IconId = 0x06001235u,
                Useability = HealthKitUseability,
                TargetType = (uint)ItemType.Creature,
            });
            Objects.MoveItem(HealthKit, Player, slot);
        }

        private static UiItemList ItemList(uint id, float left, float top, float width, float height)
            => new(static _ => (1u, 32, 32))
            {
                DatElementId = id,
                Left = left,
                Top = top,
                Width = width,
                Height = height,
            };

        private static UiElement TextHost(uint id, float left, float top, float width, float height)
            => new TestElement
            {
                DatElementId = id,
                Left = left,
                Top = top,
                Width = width,
                Height = height,
                ClickThrough = true,
            };
    }

    [Fact]
    public void InventoryDoubleClick_hauberk_routesThroughUiRootAndEquipsFullMask()
    {
        var h = new Harness();
        h.SeedHauberk();
        h.BindInventoryInteraction();
        var probe = h.Probe();

        Assert.True(probe.DoubleClickItem(Hauberk, ItemDragSource.Inventory));

        Assert.Equal(new[] { (Hauberk, (uint)HauberkMask) }, h.Wields);
        var pending = probe.AssertItem(
            Hauberk,
            equippedLocation: EquipMask.None,
            containerId: Player);
        Assert.True(pending.Success, pending.Message);

        Assert.True(h.Objects.ApplyConfirmedServerWield(
            Hauberk, Player, HauberkMask));

        var state = probe.AssertItem(
            Hauberk,
            equippedLocation: HauberkMask,
            containerId: 0u);
        Assert.True(state.Success, state.Message);
    }

    [Fact]
    public void InventoryDoubleClick_bow_replacesSwordOnlyAfterConfirmedUnwield()
    {
        var h = new Harness();
        h.Objects.AddOrUpdate(new ClientObject
        {
            ObjectId = Sword,
            Name = "Sword",
            Type = ItemType.MeleeWeapon,
            CombatUse = 1,
            ValidLocations = EquipMask.MeleeWeapon,
            IconId = 0x06001234u,
        });
        h.Objects.MoveItem(Sword, Player, -1, EquipMask.MeleeWeapon);
        h.Objects.AddOrUpdate(new ClientObject
        {
            ObjectId = Bow,
            Name = "Bow",
            Type = ItemType.MissileWeapon,
            CombatUse = 1,
            ValidLocations = EquipMask.MissileWeapon,
            IconId = 0x06001235u,
        });
        h.Objects.MoveItem(Bow, Player, 0);
        h.BindInventoryInteraction();
        var probe = h.Probe();

        Assert.True(probe.DoubleClickItem(Bow, ItemDragSource.Inventory));
        Assert.Equal(new[] { (Sword, Player, 0) }, h.Puts);
        Assert.Empty(h.Wields);
        Assert.Equal(EquipMask.MeleeWeapon,
            h.Objects.Get(Sword)!.CurrentlyEquippedLocation);

        Assert.True(h.Objects.ApplyConfirmedServerMove(Sword, Player, 0u, 0));

        Assert.Equal(new[] { (Bow, (uint)EquipMask.MissileWeapon) }, h.Wields);
        Assert.Equal(EquipMask.None,
            h.Objects.Get(Bow)!.CurrentlyEquippedLocation);

        Assert.True(h.Objects.ApplyConfirmedServerWield(
            Bow, Player, EquipMask.MissileWeapon));

        Assert.Equal(EquipMask.MissileWeapon,
            h.Objects.Get(Bow)!.CurrentlyEquippedLocation);
    }

    [Fact]
    public void InventoryTargetUse_thenMainPackClick_selfTargetsThroughUiRoot()
    {
        var h = new Harness();
        h.SeedHealthKit();
        var interaction = h.BindInventoryInteraction();
        var probe = h.Probe();

        Assert.True(probe.DoubleClickItem(HealthKit, ItemDragSource.Inventory));
        Assert.True(interaction.IsTargetModeActive);
        Assert.True(probe.ClickElement(InventoryController.TopContainerId));

        Assert.Equal(new[] { (HealthKit, Player) }, h.UseWithTarget);
        Assert.False(interaction.IsTargetModeActive);
    }

    [Fact]
    public void InventoryTargetUse_thenPaperdollBodyClick_selfTargetsThroughUiRoot()
    {
        var h = new Harness();
        h.SeedHealthKit();
        var interaction = h.BindInventoryInteraction();
        h.BindPaperdoll(interaction);
        var probe = h.Probe();

        Assert.True(probe.DoubleClickItem(HealthKit, ItemDragSource.Inventory));
        Assert.True(interaction.IsTargetModeActive);
        Assert.True(probe.ClickElement(PaperdollController.DollDragMaskId));

        Assert.Equal(new[] { (HealthKit, Player) }, h.UseWithTarget);
        Assert.False(interaction.IsTargetModeActive);
    }

    [Fact]
    public void PaperdollBodyClick_uncoveredPart_selectsPlayerThroughUiRoot()
    {
        var h = new Harness();
        h.BindPaperdoll();
        var probe = h.Probe();

        Assert.True(probe.ClickElement(PaperdollController.DollDragMaskId));

        Assert.Equal(Player, h.Selection.SelectedObjectId);
    }

    [Fact]
    public void PaperdollBodyClick_coveredPart_selectsUpperEquippedItemThroughUiRoot()
    {
        var h = new Harness();
        h.SeedHauberk();
        h.Objects.MoveItem(Hauberk, Player, -1, HauberkMask);
        h.BindPaperdoll(
            clickMap: Harness.SolidClickMap(0x00, 0xFF, 0x00));
        var probe = h.Probe();

        Assert.True(probe.ClickElement(PaperdollController.DollDragMaskId));

        Assert.Equal(Hauberk, h.Selection.SelectedObjectId);
    }

    [Fact]
    public void InventoryTargetUse_invalidInventoryItem_isConsumedWithoutSelectionFallback()
    {
        var h = new Harness();
        h.SeedHealthKit(slot: 0);
        h.SeedHauberk(slot: 1);
        var interaction = h.BindInventoryInteraction();
        var probe = h.Probe();

        Assert.True(probe.DoubleClickItem(HealthKit, ItemDragSource.Inventory));
        Assert.True(probe.DoubleClickItem(Hauberk, ItemDragSource.Inventory));

        Assert.Empty(h.UseWithTarget);
        Assert.Empty(h.Wields);
        Assert.False(interaction.IsTargetModeActive);
        Assert.Equal(HealthKit, h.Selection.SelectedObjectId);
    }

    [Fact]
    public void InventoryTargetUse_invalidWornItem_isConsumedWithoutPaperdollSelectionFallback()
    {
        var h = new Harness();
        h.SeedHealthKit(slot: 0);
        h.SeedHauberk(slot: 1);
        h.Objects.MoveItem(Hauberk, Player, -1, HauberkMask);
        var interaction = h.BindInventoryInteraction();
        h.BindPaperdoll(interaction);
        var probe = h.Probe();

        Assert.True(probe.DoubleClickItem(HealthKit, ItemDragSource.Inventory));
        Assert.True(probe.ClickElement(SlotsButtonId));
        Assert.True(probe.ClickItem(Hauberk, ItemDragSource.Equipment));

        Assert.Empty(h.UseWithTarget);
        Assert.False(interaction.IsTargetModeActive);
        Assert.Equal(HealthKit, h.Selection.SelectedObjectId);
    }

    [Fact]
    public void InventoryDragOutsideUi_routesThroughRootOutsideHookAndDropsToWorld()
    {
        var h = new Harness();
        h.SeedHauberk();
        h.BindInventoryInteraction();
        var probe = h.Probe();

        Assert.True(probe.DragItemOutside(Hauberk, 700, 500, ItemDragSource.Inventory));

        Assert.Equal(new[] { Hauberk }, h.Drops);
        var pending = probe.AssertItem(Hauberk, containerId: Player, slot: 0);
        Assert.True(pending.Success, pending.Message);

        Assert.True(h.Objects.ApplyConfirmedServerMove(Hauberk, 0u, 0u, -1));

        var state = probe.AssertItem(Hauberk, containerId: 0u, slot: -1);
        Assert.True(state.Success, state.Message);
    }

    [Fact]
    public void InventoryDragToPaperdollChestArmorSlot_usesFullHauberkCoverageMask()
    {
        var h = new Harness();
        h.SeedHauberk();
        h.BindInventoryInteraction();
        h.BindPaperdoll();
        var probe = h.Probe();

        Assert.True(probe.ClickElement(SlotsButtonId));
        Assert.True(probe.DragItemToElement(Hauberk, ChestArmorSlotId, ItemDragSource.Inventory));

        Assert.Equal(new[] { (Hauberk, (uint)HauberkMask) }, h.Wields);
        var pending = probe.AssertItem(
            Hauberk,
            equippedLocation: EquipMask.None,
            containerId: Player);
        Assert.True(pending.Success, pending.Message);

        Assert.True(h.Objects.ApplyConfirmedServerWield(
            Hauberk, Player, HauberkMask));

        var state = probe.AssertItem(
            Hauberk,
            equippedLocation: HauberkMask,
            containerId: 0u);
        Assert.True(state.Success, state.Message);
    }
}
