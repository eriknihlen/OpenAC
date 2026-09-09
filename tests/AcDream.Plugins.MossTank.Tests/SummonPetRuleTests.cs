using AcDream.Plugin.Abstractions;

namespace AcDream.Plugins.MossTank.Tests;

public sealed class SummonPetRuleTests
{
    private const uint SummonCooldownSpellId = unchecked((uint)(-32555));

    private static MacroPassContext Pass(double elapsed = 0.3d) =>
        new(elapsed, CanAct: true);

    private static SummonPetRule Rule(
        Automation automation,
        CombatSettings settings,
        bool combatOwnsTarget = false) =>
        new(new FakeHost(automation), settings, () => combatOwnsTarget);

    private static CombatSettings Settings()
    {
        var settings = new CombatSettings
        {
            Enabled = true,
            SummonPets = true,
            MaximumRange = 10d,
        };
        settings.Rules.Clear();
        settings.Rules.Add(new MonsterRule(
            "DEFAULT",
            new MonsterRuleActions
            {
                DamageType = MonsterDamageType.Auto,
                PetDamageType = MonsterDamageType.Cold,
            }));
        for (uint id = 1; id <= 20; id++)
            settings.CombatItemObjectIds.Add(id);
        return settings;
    }

    private static Automation Ready() => new()
    {
        Targets = [new PluginCombatTarget(10, "Drudge", 1, 4f, 0f, true, 1f)],
        ItemEntries = [Device(1u, 49387u)],
    };

    [Fact]
    public void SummonsWithMonstersInRangeAndNoSelectedTarget()
    {
        Automation automation = Ready();
        SummonPetRule rule = Rule(automation, Settings());

        Assert.True(rule.ValidNow(Pass()));
        Assert.Equal([1u], automation.Uses);
    }

    /// <summary>h1.cs:32-35 — EnableCombat.</summary>
    [Fact]
    public void CombatDisabledIsInvalid()
    {
        Automation automation = Ready();
        CombatSettings settings = Settings();
        settings.Enabled = false;

        Assert.False(Rule(automation, settings).ValidNow(Pass()));
        Assert.Empty(automation.Uses);
    }

    /// <summary>h1.cs:36-39 — SummonPets.</summary>
    [Fact]
    public void SummonPetsOffIsInvalid()
    {
        Automation automation = Ready();
        CombatSettings settings = Settings();
        settings.SummonPets = false;

        Assert.False(Rule(automation, settings).ValidNow(Pass()));
        Assert.Empty(automation.CaptureCalls);
        Assert.Empty(automation.Uses);
    }

    /// <summary>h1.cs:40-43 — the Summoning skill must be trained.</summary>
    [Fact]
    public void UntrainedSummoningIsInvalid()
    {
        Automation automation = Ready();
        automation.SummoningTraining = PluginSkillTraining.Untrained;

        Assert.False(Rule(automation, Settings()).ValidNow(Pass()));
        Assert.Empty(automation.Uses);
    }

    [Fact]
    public void SummonCooldownEnchantmentIsInvalid()
    {
        Automation automation = Ready();
        automation.Enchantments =
            [new PluginActiveEnchantment(SummonCooldownSpellId, 0u, 0, 30d)];

        Assert.False(Rule(automation, Settings()).ValidNow(Pass()));
        Assert.Empty(automation.Uses);

        automation.Enchantments = [];
        Assert.True(Rule(automation, Settings()).ValidNow(Pass()));
    }

    [Fact]
    public void NoMonstersInRangeIsInvalid()
    {
        Automation automation = Ready();
        automation.Targets = [];

        Assert.False(Rule(automation, Settings()).ValidNow(Pass()));
        Assert.Empty(automation.Uses);
    }

    [Fact]
    public void MonsterDensityBelowTheSettingIsInvalid()
    {
        Automation automation = Ready();
        CombatSettings settings = Settings();
        settings.PetMonsterDensity = 2;

        Assert.False(Rule(automation, settings).ValidNow(Pass()));
        Assert.Empty(automation.Uses);
    }

    [Fact]
    public void CombatAlreadyOwningATargetIsInvalid()
    {
        Automation automation = Ready();

        Assert.False(
            Rule(automation, Settings(), combatOwnsTarget: true).ValidNow(Pass()));
        Assert.Empty(automation.Uses);
    }

    [Fact]
    public void IndependentTrackNeverRefills()
    {
        Automation automation = Ready();
        automation.ItemEntries =
        [
            Device(1u, 49387u, structure: 0),
            Item(2u, PetDeviceCatalog.EncapsulatedSpiritWeenieClassId) with
            {
                Name = "Encapsulated Spirit",
            },
        ];

        Assert.False(Rule(automation, Settings()).ValidNow(Pass()));
        Assert.Empty(automation.Applies);
        Assert.Empty(automation.Uses);
    }

    private static PluginInventoryItem Device(
        uint id,
        uint wcid,
        int structure = 50) => Item(id, wcid) with
        {
            PetClass = 49000,
            SummoningMastery = 0,
            UseRequiresSkill = 54,
            UseRequiresSkillLevel = 100,
            Structure = structure,
            MaximumStructure = 50,
        };

    private static PluginInventoryItem Item(uint id, uint wcid) => new(
        ObjectId: id,
        WeenieClassId: wcid,
        Name: $"Item {id}",
        ItemType: 0,
        ContainerObjectId: 1,
        WielderObjectId: 0,
        ValidLocations: 0,
        EquippedLocation: 0,
        Useability: 0,
        TargetType: 0,
        PublicFlags: 0,
        StackSize: 1,
        Structure: 1,
        MaximumStructure: 1,
        SpellId: 0,
        PetClass: 0,
        SummoningMastery: 0,
        ProcSpellId: 0,
        ProcSpellSelfTargeted: false,
        ProcSpellRate: 0,
        WeaponSkill: 0,
        DamageType: 0,
        Damage: 0,
        DamageVariance: 0,
        UseRequiresSkill: 0,
        UseRequiresSkillLevel: 0,
        UseRequiresSkillSpecialized: 0);

    private sealed class FakeHost(Automation automation) : IPluginHost
    {
        public bool HasUi => false;
        public IPluginLogger Log { get; } = new StubLogger();
        public IGameState State { get; } = new StubState();
        public IEvents Events { get; } = new StubEvents();
        public ISelectionService Selection { get; } = new StubSelection();
        public IUiRegistry Ui => NoOpUiRegistry.Instance;
        public IAutomationSurface Automation { get; } = automation;
    }

    private sealed class StubLogger : IPluginLogger
    {
        public void Info(string message) { }
        public void Warn(string message) { }
        public void Error(string message, Exception? exception = null) { }
    }

    private sealed class StubState : IGameState
    {
        public IReadOnlyList<WorldEntitySnapshot> Entities => [];
    }

    private sealed class StubEvents : IEvents
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

    private sealed class StubSelection : ISelectionService
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

    private sealed class Automation
        : IAutomationSurface, ICharacterInfo, IItemAutomation, ICombatAutomation
    {
        public bool IsAvailable => true;
        public ICharacterInfo Character => this;
        public IItemAutomation Items => this;
        public ICombatAutomation Combat => this;
        public ISpellCatalog Spells => NoOpAutomationSurface.Instance;
        public IMagicCommands Magic => NoOpAutomationSurface.Instance;
        public IPluginChat Chat => NoOpAutomationSurface.Instance;

        public PluginSkillTraining SummoningTraining { get; set; } =
            PluginSkillTraining.Trained;
        public IReadOnlyList<PluginActiveEnchantment> Enchantments { get; set; } = [];
        public IReadOnlyList<PluginCombatTarget> Targets { get; set; } = [];
        public IReadOnlyList<PluginInventoryItem> ItemEntries { get; set; } = [];
        public List<uint> Uses { get; } = [];
        public List<(uint Source, uint Target)> Applies { get; } = [];

        public bool IsInWorld => true;
        public uint ObjectId => 1;
        public uint CurrentHealth => 100;
        public uint MaxHealth => 100;
        public uint CurrentStamina => 100;
        public uint MaxStamina => 100;
        public uint CurrentMana => 100;
        public uint MaxMana => 100;
        public int SummoningMastery => 0;
        public IReadOnlyList<PluginSkillInfo> Skills =>
            [new(54, "Summoning", SummoningTraining, 300)];
        public IReadOnlyList<PluginAttributeInfo> Attributes => [];
        IReadOnlyList<PluginActiveEnchantment> ICharacterInfo.ActiveEnchantments =>
            Enchantments;

        public bool TryGetSkill(uint skillId, out PluginSkillInfo skill)
        {
            if (skillId == 54u)
            {
                skill = Skills[0];
                return true;
            }
            skill = default;
            return false;
        }

        bool IItemAutomation.IsAvailable => true;
        bool IItemAutomation.IsBusy => false;
        int IItemAutomation.ActiveOwnedPetCount => 0;
        PluginItemUseCompletion IItemAutomation.LastCompletion => default;
        public IReadOnlyList<PluginInventoryItem> CaptureOwnedItems() => ItemEntries;

        public PluginItemCommandResult Use(uint objectId)
        {
            Uses.Add(objectId);
            return new(PluginItemCommandStatus.Started);
        }

        public PluginItemCommandResult Apply(uint objectId, uint targetObjectId)
        {
            Applies.Add((objectId, targetObjectId));
            return new(PluginItemCommandStatus.Started);
        }

        PluginCombatSnapshot ICombatAutomation.Snapshot => default;
        public List<float> CaptureCalls { get; } = [];
        IReadOnlyList<PluginCombatTarget> ICombatAutomation.CaptureHostileTargets(
            float maximumDistance)
        {
            CaptureCalls.Add(maximumDistance);
            return Targets;
        }
        PluginCombatCommandResult ICombatAutomation.EnterDefaultMode() =>
            new(PluginCombatCommandStatus.Unavailable);
        PluginCombatCommandResult ICombatAutomation.BeginPhysicalAttack(
            uint targetObjectId,
            PluginAttackHeight height,
            float power) => new(PluginCombatCommandStatus.Unavailable);
        PluginCombatCommandResult ICombatAutomation.ReleasePhysicalAttack() =>
            new(PluginCombatCommandStatus.Unavailable);
        PluginCombatCommandResult ICombatAutomation.AbortPhysicalAttack() =>
            new(PluginCombatCommandStatus.Unavailable);
    }
}
