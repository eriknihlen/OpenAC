using AcDream.Plugin.Abstractions;

namespace AcDream.Plugins.MossTank.Tests;

public sealed class CraftingTests
{
    [Fact]
    public void EmbeddedDatabaseContainsAllOfficialVtankCraftInteractions()
    {
        Assert.Equal(757, VtankCraftDatabase.Recipes.Count);
        VtankCraftRecipe recipe = Assert.Single(
            VtankCraftDatabase.ForResult("Plentiful Healing Kit"));
        Assert.Equal("Soft Bandages", recipe.FirstItem);
        Assert.Equal("Combined Hyssop and Mandrake", recipe.SecondItem);
        Assert.Equal(21u, recipe.RequiredSkill);
        Assert.Equal(157, recipe.Id);
    }

    [Fact]
    public void PlannerRecursivelyCraftsMissingPrerequisiteBeforeDesiredItem()
    {
        var character = new Character(trainedSkill: 21u);
        IReadOnlyList<PluginInventoryItem> inventory =
        [
            Item(1, "Soft Bandages"),
            Item(2, "Treated Mandrake"),
            Item(3, "Treated Hyssop"),
        ];

        CraftingPlan first = Assert.IsType<CraftingPlan>(CraftingPlanner.Plan(
            inventory,
            ["Plentiful Healing Kit"],
            character));

        Assert.Equal("Combined Hyssop and Mandrake", first.Recipe.ResultItem);
        Assert.Equal(2u, first.FirstObjectId);
        Assert.Equal(3u, first.SecondObjectId);

        CraftingPlan final = Assert.IsType<CraftingPlan>(CraftingPlanner.Plan(
            [inventory[0], Item(4, "Combined Hyssop and Mandrake")],
            ["Plentiful Healing Kit"],
            character));
        Assert.Equal("Plentiful Healing Kit", final.Recipe.ResultItem);
        Assert.Equal(1u, final.FirstObjectId);
        Assert.Equal(4u, final.SecondObjectId);
    }

    [Fact]
    public void PlannerRejectsRecipesForUntrainedRequiredSkill()
    {
        CraftingPlan? plan = CraftingPlanner.Plan(
            [Item(1, "Soft Bandages"), Item(2, "Combined Hyssop and Mandrake")],
            ["Plentiful Healing Kit"],
            new Character(trainedSkill: 0u));

        Assert.Null(plan);
    }

    [Fact]
    public void AllPeasSplitsTheFirstProfiledPeaBelowTheComponentThreshold()
    {
        CraftingPlan plan = Assert.IsType<CraftingPlan>(
            CraftingPlanner.PlanPeaSplit(
                [
                    Item(1, "Splitting Tool"),
                    Item(2, "Brimstone Pea"),
                ],
                new HashSet<string>(StringComparer.OrdinalIgnoreCase)
                {
                    CraftingPlanner.AllPeas,
                },
                minimumComponentCount: 20));

        Assert.Equal("Brimstone", plan.Recipe.ResultItem);
        Assert.Equal(1u, plan.FirstObjectId);
        Assert.Equal(2u, plan.SecondObjectId);
    }

    [Fact]
    public void PeaSplitStopsAtTheRequestedComponentCount()
    {
        PluginInventoryItem brimstone = Item(3, "Brimstone") with
        {
            StackSize = 20,
        };

        Assert.Null(CraftingPlanner.PlanPeaSplit(
            [Item(1, "Splitting Tool"), Item(2, "Brimstone Pea"), brimstone],
            new HashSet<string>(StringComparer.OrdinalIgnoreCase)
            {
                "Brimstone Pea",
            },
            minimumComponentCount: 20));
    }

    [Fact]
    public void SameInputRecipeStagesAnExactOneUnitSplitBeforeApplying()
    {
        PluginInventoryItem oil = Item(1, "Chorizite Oil") with
        {
            StackSize = 2,
        };

        CraftingPlan plan = Assert.IsType<CraftingPlan>(CraftingPlanner.Plan(
            [oil],
            ["Strong Chorizite Oil"],
            new Character(trainedSkill: 0u)));

        Assert.True(plan.RequiresSplitFirstStack);
        Assert.Equal(1u, plan.FirstObjectId);
        Assert.Equal(1u, plan.SplitContainerObjectId);
        Assert.Equal(0u, plan.SecondObjectId);
    }

    [Fact]
    public void ControllerWaitsForSplitReceiptAndBothPublishedStacksBeforeApplying()
    {
        var automation = new Automation
        {
            Inventory = [Item(20, "Chorizite Oil") with
            {
                ContainerObjectId = 1u,
                StackSize = 2,
            }],
        };
        var settings = new InventorySettings
        {
            AutoCraftItems = true,
            SplitPeas = false,
        };
        var profiles = new CombatSettings();
        profiles.ConsumableNames.Add("Strong Chorizite Oil");
        var controller = new CraftingController(
            new Host(automation),
            settings,
            profiles);

        Assert.True(controller.Tick(0d, canAct: true));
        Assert.Equal([(20u, 1u, 1u)], automation.Moves);
        Assert.Empty(automation.Applies);

        automation.InventoryCompletion = new PluginInventoryCompletion(
            1,
            PluginInventoryCommandKind.SplitToContainer,
            20u,
            0u);
        automation.Busy = false;
        Assert.True(controller.Tick(0.05d, canAct: true));
        Assert.Empty(automation.Applies);
        Assert.Equal("AutoCraft waiting for split inventory", controller.Status);

        automation.Inventory =
        [
            Item(20, "Chorizite Oil") with
            {
                ContainerObjectId = 1u,
                StackSize = 1,
            },
            Item(21, "Chorizite Oil") with
            {
                ContainerObjectId = 1u,
                StackSize = 1,
            },
        ];
        Assert.True(controller.Tick(0.05d, canAct: true));
        Assert.Equal([(20u, 21u)], automation.Applies);
        Assert.Equal("Crafting Strong Chorizite Oil", controller.Status);
    }

    [Fact]
    public void ARuleBelowTheWinnerObservesTheSplitButDoesNotResumeTheCraft()
    {
        var automation = new Automation
        {
            Inventory = [Item(20, "Chorizite Oil") with
            {
                ContainerObjectId = 1u,
                StackSize = 2,
            }],
        };
        var settings = new InventorySettings
        {
            AutoCraftItems = true,
            SplitPeas = false,
        };
        var profiles = new CombatSettings();
        profiles.ConsumableNames.Add("Strong Chorizite Oil");
        var controller = new CraftingController(
            new Host(automation),
            settings,
            profiles);

        Assert.True(controller.Tick(0d, canAct: true));
        Assert.Equal([(20u, 1u, 1u)], automation.Moves);

        automation.InventoryCompletion = new PluginInventoryCompletion(
            1,
            PluginInventoryCommandKind.SplitToContainer,
            20u,
            0u);
        automation.Busy = false;
        automation.Inventory =
        [
            Item(20, "Chorizite Oil") with
            {
                ContainerObjectId = 1u,
                StackSize = 1,
            },
            Item(21, "Chorizite Oil") with
            {
                ContainerObjectId = 1u,
                StackSize = 1,
            },
        ];

        // A rule above won this pass. The receipt is still observed...
        Assert.True(controller.Tick(0.05d, canAct: false));
        Assert.Empty(automation.Applies);

        Assert.True(controller.Tick(0.05d, canAct: true));
        Assert.Equal([(20u, 21u)], automation.Applies);
    }

    [Fact]
    public void SplitTimeoutIsWallClockNotThreeTimesTheRuleListDepth()
    {
        var automation = new Automation
        {
            Inventory = [Item(20, "Chorizite Oil") with
            {
                ContainerObjectId = 1u,
                StackSize = 2,
            }],
        };
        var settings = new InventorySettings
        {
            AutoCraftItems = true,
            SplitPeas = false,
        };
        var profiles = new CombatSettings();
        profiles.ConsumableNames.Add("Strong Chorizite Oil");
        var controller = new CraftingController(
            new Host(automation),
            settings,
            profiles);

        Assert.True(controller.Tick(0d, canAct: true));
        Assert.Equal([(20u, 1u, 1u)], automation.Moves);

        for (int pass = 0; pass < 9; pass++)
        {
            Assert.True(controller.TickCritical(1d, canAct: true));
            Assert.True(controller.Tick(1d, canAct: true));
            Assert.True(controller.TickIdle(1d, canAct: true));
        }
        Assert.NotEqual("AutoCraft split timed out", controller.Status);

        Assert.True(controller.TickCritical(1.5d, canAct: true));
        Assert.Equal("AutoCraft split timed out", controller.Status);
    }

    [Fact]
    public void CraftingWaitsForPeaceModeBeforeApplyingARecipe()
    {
        var automation = new Automation
        {
            Inventory =
            [
                Item(1, "Chorizite Oil") with { StackSize = 2 },
            ],
        };
        var settings = new InventorySettings
        {
            AutoCraftItems = true,
            SplitPeas = false,
        };
        var profiles = new CombatSettings();
        profiles.ConsumableNames.Add("Strong Chorizite Oil");
        var controller = new CraftingController(
            new Host(automation),
            settings,
            profiles);
        bool inPeace = false;
        controller.BindPeaceGate(() => inPeace);

        Assert.True(controller.Tick(0d, canAct: true));
        Assert.Empty(automation.Applies);
        Assert.Empty(automation.Moves);
        Assert.Contains("peace mode", controller.Status, StringComparison.Ordinal);

        inPeace = true;
        Assert.True(controller.Tick(0.5d, canAct: true));
        Assert.NotEmpty(automation.Moves);
    }

    [Fact]
    public void AmmunitionRequestCraftsEvenWhenGeneralAutoCraftIsDisabled()
    {
        var automation = new Automation
        {
            TrainedSkill = 37u,
            Inventory =
            [
                Item(1u, "Wrapped Bundle of Deadly Fire Arrowheads"),
                Item(2u, "Wrapped Bundle of Arrowshafts"),
            ],
        };
        var controller = new CraftingController(
            new Host(automation),
            new InventorySettings { AutoCraftItems = false },
            new CombatSettings());

        Assert.True(controller.Request("Deadly Fire Arrow"));

        Assert.Equal([(1u, 2u)], automation.Applies);
        Assert.Equal("Crafting Deadly Fire Arrow", controller.Status);
    }

    [Theory]
    [InlineData("Greater Stamina Kit", 0x00010000u, 0, (int)ConsumableCategory.StaminaKit)]
    [InlineData("Greater Mana Kit", 0x00010000u, 0, (int)ConsumableCategory.ManaKit)]
    [InlineData("Greater Healing Kit", 0x00010000u, 0, (int)ConsumableCategory.HealthKit)]
    [InlineData("Mana Food", 0u, 6, (int)ConsumableCategory.ManaFood)]
    [InlineData("Stamina Food", 0u, 4, (int)ConsumableCategory.StaminaFood)]
    [InlineData("Health Food", 0u, 2, (int)ConsumableCategory.HealthFood)]
    [InlineData("Intricate Lockpick", 0x00020000u, 0, (int)ConsumableCategory.Lockpick)]
    public void ConsumableClassificationUsesRetailPropertiesAndExactKitExceptions(
        string name,
        uint publicFlags,
        int boosterVital,
        int expected)
    {
        PluginInventoryItem item = Item(1u, name) with
        {
            PublicFlags = publicFlags,
            BoosterVital = boosterVital,
        };

        Assert.Equal((ConsumableCategory)expected, ConsumableClassifier.Classify(item));
    }

    [Fact]
    public void IdleCraftingRestocksTheConfiguredConsumableCategoryCount()
    {
        var automation = new Automation
        {
            TrainedSkill = 21u,
            Inventory =
            [
                Item(1u, "Soft Bandages"),
                Item(2u, "Combined Hyssop and Mandrake"),
                Item(3u, "Plentiful Healing Kit"),
            ],
        };
        var settings = new InventorySettings
        {
            AutoCraftItems = true,
            SplitPeas = false,
            IdleHealthKitCount = 2,
        };
        var profiles = new CombatSettings();
        profiles.ConsumableNames.Add("Plentiful Healing Kit");
        profiles.ConsumableCategories["Plentiful Healing Kit"] =
            ConsumableCategory.HealthKit;
        var controller = new CraftingController(
            new Host(automation),
            settings,
            profiles);

        Assert.True(controller.TickIdle(0d, canAct: true));
        Assert.Equal([(1u, 2u)], automation.Applies);
        Assert.Equal("Crafting Plentiful Healing Kit", controller.Status);
    }

    private static PluginInventoryItem Item(uint id, string name) => new(
        id, 0u, name, 0x80u, 1u, 0u, 0u, 0u, 0u, 0u, 0u,
        1, 0, 0, 0u, 0, 0, 0u, false, 0d, 0, 0, 0, 0d, 0, 0, 0);

    private sealed class Character(uint trainedSkill) : ICharacterInfo
    {
        public bool IsInWorld => true;
        public uint ObjectId => 1u;
        public uint CurrentHealth => 0;
        public uint MaxHealth => 0;
        public uint CurrentStamina => 0;
        public uint MaxStamina => 0;
        public uint CurrentMana => 0;
        public uint MaxMana => 0;
        public IReadOnlyList<PluginSkillInfo> Skills => trainedSkill == 0u
            ? []
            : [new(trainedSkill, "Craft", PluginSkillTraining.Trained, 300)];
        public IReadOnlyList<PluginAttributeInfo> Attributes => [];
        public IReadOnlyList<PluginActiveEnchantment> ActiveEnchantments => [];
        public bool TryGetSkill(uint skillId, out PluginSkillInfo skill)
        {
            if (skillId == trainedSkill && trainedSkill != 0u)
            {
                skill = Skills[0];
                return true;
            }
            skill = default;
            return false;
        }
    }

    private sealed class Automation
        : IAutomationSurface, ICharacterInfo, IItemAutomation
    {
        public bool IsAvailable => true;
        public ICharacterInfo Character => this;
        public ISpellCatalog Spells => NoOpAutomationSurface.Instance;
        public IMagicCommands Magic => NoOpAutomationSurface.Instance;
        public IPluginChat Chat => NoOpAutomationSurface.Instance;
        public IItemAutomation Items => this;
        public bool IsInWorld => true;
        public uint ObjectId => 1u;
        public uint CurrentHealth => 0u;
        public uint MaxHealth => 0u;
        public uint CurrentStamina => 0u;
        public uint MaxStamina => 0u;
        public uint CurrentMana => 0u;
        public uint MaxMana => 0u;
        public uint TrainedSkill { get; set; }
        public IReadOnlyList<PluginSkillInfo> Skills => TrainedSkill == 0u
            ? []
            : [new(TrainedSkill, "Craft", PluginSkillTraining.Trained, 300)];
        public IReadOnlyList<PluginAttributeInfo> Attributes => [];
        public IReadOnlyList<PluginActiveEnchantment> ActiveEnchantments => [];
        public bool IsBusy => Busy;
        public bool Busy { get; set; }
        public PluginInventoryCompletion InventoryCompletion { get; set; }
        public PluginInventoryCompletion LastInventoryCompletion =>
            InventoryCompletion;
        public IReadOnlyList<PluginInventoryItem> Inventory { get; set; } = [];
        public List<(uint Source, uint Container, uint Amount)> Moves { get; } = [];
        public List<(uint Source, uint Target)> Applies { get; } = [];
        public IReadOnlyList<PluginInventoryItem> CaptureOwnedItems() => Inventory;
        public PluginItemCommandResult MoveToContainer(
            uint objectId,
            uint containerObjectId,
            uint amount = 0u,
            int placement = 0)
        {
            Moves.Add((objectId, containerObjectId, amount));
            Busy = true;
            return new(PluginItemCommandStatus.Started);
        }
        public PluginItemCommandResult Apply(uint objectId, uint targetObjectId)
        {
            Applies.Add((objectId, targetObjectId));
            Busy = true;
            return new(PluginItemCommandStatus.Started);
        }
        public bool TryGetSkill(uint skillId, out PluginSkillInfo skill)
        {
            if (skillId == TrainedSkill && TrainedSkill != 0u)
            {
                skill = Skills[0];
                return true;
            }
            skill = default;
            return false;
        }
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
