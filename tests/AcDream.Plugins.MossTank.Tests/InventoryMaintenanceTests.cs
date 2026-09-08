using AcDream.Plugin.Abstractions;

namespace AcDream.Plugins.MossTank.Tests;

public sealed class InventoryMaintenanceTests
{
    private const uint Player = 1u;

    [Fact]
    public void PlannerAlwaysStacksBeforeCramming()
    {
        var settings = new InventorySettings { AutoStack = true, AutoCram = true };
        PluginInventoryItem pack = Item(10, "Pack", container: Player) with
        {
            ItemsCapacity = 24,
            ContainerSlot = 1,
            Burden = 8,
        };
        PluginInventoryItem source = Item(
            20, "Arrow", wcid: 77, container: Player, stack: 10, maximum: 10) with
        {
            ContainerSlot = 2,
            Burden = 1,
        };
        PluginInventoryItem target = Item(
            21, "Arrow", wcid: 77, container: 10, stack: 6, maximum: 10) with
        {
            ContainerSlot = 0,
            Burden = 1,
        };

        InventoryMaintenancePlan plan = Assert.IsType<InventoryMaintenancePlan>(
            InventoryMaintenancePlanner.Plan(
                [pack, source, target], Player, settings));

        Assert.Equal(InventoryMaintenanceKind.Merge, plan.Kind);
        Assert.Equal(source.ObjectId, plan.SourceObjectId);
        Assert.Equal(target.ObjectId, plan.TargetObjectId);
        Assert.Equal(4u, plan.Amount);
    }

    [Fact]
    public void CramMovesOneMainPackItemIntoFirstSidePackWithRoom()
    {
        var settings = new InventorySettings { AutoStack = false, AutoCram = true };
        PluginInventoryItem full = Item(10, "Full pack", container: Player) with
        {
            ItemsCapacity = 1,
            ContainerSlot = 0,
        };
        PluginInventoryItem destination = Item(11, "Open pack", container: Player) with
        {
            ItemsCapacity = 2,
            ContainerSlot = 1,
        };
        PluginInventoryItem occupant = Item(12, "Occupant", container: 10);
        PluginInventoryItem source = Item(20, "Loose item", container: Player) with
        {
            ContainerSlot = 4,
        };
        PluginInventoryItem foci = Item(21, "Focus", container: Player) with
        {
            ContainerSlot = 2,
            PublicFlags = 0x00800000u,
        };

        InventoryMaintenancePlan plan = Assert.IsType<InventoryMaintenancePlan>(
            InventoryMaintenancePlanner.Plan(
                [full, destination, occupant, foci, source],
                Player,
                settings));

        Assert.Equal(InventoryMaintenanceKind.Cram, plan.Kind);
        Assert.Equal(source.ObjectId, plan.SourceObjectId);
        Assert.Equal(destination.ObjectId, plan.TargetObjectId);
    }

    [Fact]
    public void ControllerWaitsForAuthoritativeReceiptBeforeReplanning()
    {
        var automation = new Automation
        {
            Inventory =
            [
                Item(20, "Arrow", 77, Player, 3, 10),
                Item(21, "Arrow", 77, Player, 8, 10),
            ],
        };
        var controller = new InventoryMaintenanceController(
            new Host(automation),
            new InventorySettings { AutoStack = true });

        Assert.True(controller.Tick(1d, canAct: true));
        Assert.Equal(new[] { (20u, 21u, 2u) }, automation.Merges);

        Assert.True(controller.Tick(1d, canAct: true));
        Assert.Single(automation.Merges);

        automation.Busy = false;
        automation.Completion = new PluginInventoryCompletion(
            1,
            PluginInventoryCommandKind.Merge,
            20u,
            0u);
        automation.Inventory =
        [
            Item(21, "Arrow", 77, Player, 10, 10),
        ];

        Assert.False(controller.Tick(1d, canAct: true));
        Assert.Single(automation.Merges);
        Assert.Equal("Stack/Cram idle", controller.Status);
    }

    private static PluginInventoryItem Item(
        uint id,
        string name,
        uint wcid = 0u,
        uint container = Player,
        int stack = 1,
        int maximum = 1) => new(
            id, wcid, name, 0x80u, container, 0u, 0u, 0u, 0u, 0u, 0u,
            stack, 0, 0, 0u, 0, 0, 0u, false, 0d, 0, 0, 0, 0d, 0, 0, 0)
        {
            MaximumStackSize = maximum,
        };

    private sealed class Automation
        : IAutomationSurface, ICharacterInfo, IItemAutomation, IPluginChat
    {
        public bool IsAvailable => true;
        public ICharacterInfo Character => this;
        public ISpellCatalog Spells => NoOpAutomationSurface.Instance;
        public IMagicCommands Magic => NoOpAutomationSurface.Instance;
        public IPluginChat Chat => this;
        public IItemAutomation Items => this;
        public bool IsInWorld => true;
        public uint ObjectId => Player;
        public uint CurrentHealth => 0;
        public uint MaxHealth => 0;
        public uint CurrentStamina => 0;
        public uint MaxStamina => 0;
        public uint CurrentMana => 0;
        public uint MaxMana => 0;
        public IReadOnlyList<PluginSkillInfo> Skills => [];
        public IReadOnlyList<PluginAttributeInfo> Attributes => [];
        public IReadOnlyList<PluginActiveEnchantment> ActiveEnchantments => [];
        public bool IsBusy => Busy;
        public bool Busy { get; set; }
        public PluginInventoryCompletion Completion { get; set; }
        public PluginInventoryCompletion LastInventoryCompletion => Completion;
        public IReadOnlyList<PluginInventoryItem> Inventory { get; set; } = [];
        public List<(uint Source, uint Target, uint Amount)> Merges { get; } = [];
        public IReadOnlyList<PluginInventoryItem> CaptureOwnedItems() => Inventory;
        public PluginItemCommandResult Merge(
            uint sourceObjectId,
            uint targetObjectId,
            uint amount = 0u)
        {
            Merges.Add((sourceObjectId, targetObjectId, amount));
            Busy = true;
            return new(PluginItemCommandStatus.Started);
        }
        public bool TryGetSkill(uint skillId, out PluginSkillInfo skill)
        {
            skill = default;
            return false;
        }
        public void PostSystemMessage(string text) { }
    }

    private sealed class Host(IAutomationSurface automation) : IPluginHost
    {
        public bool HasUi => false;
        public IPluginLogger Log => Logger.Instance;
        public IGameState State => EmptyState.Instance;
        public IEvents Events => EmptyEvents.Instance;
        public ISelectionService Selection => EmptySelection.Instance;
        public IUiRegistry Ui => NoOpUiRegistry.Instance;
        public IAutomationSurface Automation { get; } = automation;
    }

    private sealed class Logger : IPluginLogger
    {
        public static Logger Instance { get; } = new();
        public void Info(string message) { }
        public void Warn(string message) { }
        public void Error(string message, Exception? exception = null) { }
    }

    private sealed class EmptyState : IGameState
    {
        public static EmptyState Instance { get; } = new();
        public IReadOnlyList<WorldEntitySnapshot> Entities => [];
    }

    private sealed class EmptyEvents : IEvents
    {
        public static EmptyEvents Instance { get; } = new();
        public event Action<WorldEntitySnapshot> EntitySpawned
        {
            add { }
            remove { }
        }
        public event Action<double> Tick
        {
            add { }
            remove { }
        }
    }

    private sealed class EmptySelection : ISelectionService
    {
        public static EmptySelection Instance { get; } = new();
        public uint? SelectedObjectId => null;
        public uint? PreviousObjectId => null;
        public event Action<SelectionChangedEvent> Changed
        {
            add { }
            remove { }
        }
        public bool Select(uint objectId) => false;
        public bool Clear() => false;
    }
}
