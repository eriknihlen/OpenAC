using AcDream.Plugin.Abstractions;

namespace AcDream.Plugins.MossTank.Tests;

public sealed class ProfileGiveControllerTests
{
    [Fact]
    public void NamedProfileGivesOnlyKeepMatchesAndWaitsForCompletion()
    {
        var automation = new FakeAutomation
        {
            ObjectsValue =
            [
                new PluginWorldObject(
                    100u, 0u, "Mule", PluginObjectClass.Player,
                    0u, 0u, 0u),
            ],
            ItemsValue =
            [
                Item(10u, "Trade Pyreal"),
                Item(11u, "Personal Note"),
            ],
        };
        var storage = new MemoryStorage();
        var host = new FakeHost(automation, storage);
        var profiles = new MossTankLootProfileStore(host);
        profiles.BindCharacter(automation.Name);
        Assert.True(profiles.Create("Mule Items", false, [], out _));
        profiles.SaveCurrent(
        [
            new LootRule
            {
                Expression = "name ~= trade",
                Action = LootAction.Keep,
            },
            new LootRule
            {
                Expression = "*",
                Action = LootAction.NoLoot,
            },
        ]);
        var controller = new ProfileGiveController(host, profiles);

        Assert.True(controller.TryStart("Mule Items.utl", "Mule"));
        Assert.True(controller.Tick(0.1d, canAct: true));
        Assert.Equal([(10u, 100u, 0u)], automation.Gives);

        Assert.True(controller.Tick(0.1d, canAct: true));
        Assert.Single(automation.Gives);
        automation.Completion = new PluginInventoryCompletion(
            1,
            PluginInventoryCommandKind.Give,
            10u,
            0u);
        Assert.True(controller.Tick(0.1d, canAct: true));
        Assert.False(controller.Tick(0.1d, canAct: true));
        Assert.False(controller.IsRunning);
        Assert.Contains("1 item(s)", controller.Status, StringComparison.Ordinal);
    }

    [Fact]
    public void StartRejectsBusyMissingProfileAndMissingTarget()
    {
        var automation = new FakeAutomation
        {
            ObjectsValue =
            [
                new PluginWorldObject(
                    100u, 0u, "Mule", PluginObjectClass.Npc,
                    0u, 0u, 0u),
            ],
        };
        var storage = new MemoryStorage();
        var host = new FakeHost(automation, storage);
        var profiles = new MossTankLootProfileStore(host);
        profiles.BindCharacter(automation.Name);
        var controller = new ProfileGiveController(host, profiles);

        Assert.False(controller.TryStart("Missing", "Mule"));
        Assert.True(profiles.Create("Empty", false, [], out _));
        Assert.False(controller.TryStart("Empty", "Missing"));
        Assert.True(controller.TryStart("Empty", "Mule"));
        Assert.False(controller.TryStart("Empty", "Mule"));
    }

    private static PluginInventoryItem Item(uint id, string name) => new(
        id, 0u, name, 0u, 1u, 0u, 0u, 0u, 0u, 0u, 0u,
        1, 0, 0, 0u, 0, 0, 0u, false, 0d, 0, 0, 0, 0d, 0, 0, 0);

    private sealed class FakeAutomation
        : IAutomationSurface, ICharacterInfo, IItemAutomation,
          IWorldObjectAutomation
    {
        public bool IsAvailable { get; set; } = true;
        public ICharacterInfo Character => this;
        public ISpellCatalog Spells => NoOpAutomationSurface.Instance;
        public IMagicCommands Magic => NoOpAutomationSurface.Instance;
        public IPluginChat Chat => NoOpAutomationSurface.Instance;
        public IItemAutomation Items => this;
        public IWorldObjectAutomation Objects => this;
        public bool IsInWorld => IsAvailable;
        public string Name => "Tester";
        public uint ObjectId => 1u;
        public uint CurrentHealth => 0u;
        public uint MaxHealth => 0u;
        public uint CurrentStamina => 0u;
        public uint MaxStamina => 0u;
        public uint CurrentMana => 0u;
        public uint MaxMana => 0u;
        public IReadOnlyList<PluginSkillInfo> Skills => [];
        public IReadOnlyList<PluginAttributeInfo> Attributes => [];
        public IReadOnlyList<PluginActiveEnchantment> ActiveEnchantments => [];
        public IReadOnlyList<PluginInventoryItem> ItemsValue { get; set; } = [];
        public IReadOnlyList<PluginWorldObject> ObjectsValue { get; set; } = [];
        public PluginInventoryCompletion Completion { get; set; }
        public List<(uint Item, uint Target, uint Amount)> Gives { get; } = [];
        bool IItemAutomation.IsAvailable => true;
        bool IItemAutomation.IsBusy => false;
        bool IWorldObjectAutomation.IsAvailable => true;
        public PluginInventoryCompletion LastInventoryCompletion => Completion;
        public IReadOnlyList<PluginInventoryItem> CaptureOwnedItems() => ItemsValue;
        public IReadOnlyList<PluginWorldObject> CaptureObjects() => ObjectsValue;
        public bool TryGet(uint objectId, out PluginWorldObject value)
        {
            foreach (PluginWorldObject item in ObjectsValue)
            {
                if (item.ObjectId == objectId)
                {
                    value = item;
                    return true;
                }
            }
            value = default;
            return false;
        }
        public bool TryCaptureProperties(
            uint objectId,
            out PluginItemProperties properties)
        {
            properties = new PluginItemProperties(
                new Dictionary<uint, int>(),
                new Dictionary<uint, long>(),
                new Dictionary<uint, bool>(),
                new Dictionary<uint, double>(),
                new Dictionary<uint, string>(),
                new Dictionary<uint, uint>(),
                new Dictionary<uint, uint>());
            return true;
        }
        public PluginItemCommandResult Give(
            uint objectId,
            uint targetObjectId,
            uint amount = 0u)
        {
            Gives.Add((objectId, targetObjectId, amount));
            return new PluginItemCommandResult(PluginItemCommandStatus.Started);
        }
        public bool TryGetSkill(uint skillId, out PluginSkillInfo skill)
        {
            skill = default;
            return false;
        }
    }

    private sealed class FakeHost(
        FakeAutomation automation,
        IPluginStorage storage) : IPluginHost
    {
        public bool HasUi => false;
        public IPluginLogger Log { get; } = new FakeLogger();
        public IGameState State { get; } = new FakeState();
        public IEvents Events { get; } = new FakeEvents();
        public ISelectionService Selection { get; } = new FakeSelection();
        public IUiRegistry Ui => NoOpUiRegistry.Instance;
        public IPluginStorage Storage => storage;
        public IAutomationSurface Automation => automation;
        public IPluginStorage VtankProfiles => storage;
    }

    private sealed class MemoryStorage : IPluginStorage
    {
        private readonly Dictionary<string, string> _text =
            new(StringComparer.Ordinal);
        public bool IsAvailable => true;
        public string? ReadText(string key) =>
            _text.TryGetValue(key, out string? value) ? value : null;
        public void WriteText(string key, string content) => _text[key] = content;
        public bool Delete(string key) => _text.Remove(key);
    }

    private sealed class FakeLogger : IPluginLogger
    {
        public void Info(string message) { }
        public void Warn(string message) { }
        public void Error(string message, Exception? exception = null) { }
    }

    private sealed class FakeState : IGameState
    {
        public IReadOnlyList<WorldEntitySnapshot> Entities => [];
    }

    private sealed class FakeEvents : IEvents
    {
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

    private sealed class FakeSelection : ISelectionService
    {
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
