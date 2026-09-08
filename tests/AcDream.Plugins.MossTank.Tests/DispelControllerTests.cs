using AcDream.Plugin.Abstractions;

namespace AcDream.Plugins.MossTank.Tests;

public sealed class DispelControllerTests
{
    private const uint SelfDispel = (uint)SpellId.EradicateLifeMagicSelf;

    [Fact]
    public void LearnedSelfDispelWithChorizitePrecedesDispelItems()
    {
        var automation = new Automation
        {
            Active = [new PluginActiveEnchantment(100u, 7u, 7, 120d)],
            SpellLookup = [Vulnerability(100u, 400), SelfDispelSpell()],
            KnownSpellIds = new HashSet<uint> { SelfDispel },
            Inventory = [Item(10u, "Chorizite"), Item(11u, "Rune of Dispel")],
            Mode = PluginCombatMode.Magic,
        };
        var controller = new DispelController(
            new Host(automation),
            new VitalSettings
            {
                CastDispelSelf = true,
                UseDispelItems = true,
            });

        Assert.True(controller.Tick(0d, canAct: true));
        Assert.Equal((SelfDispel, 1u), automation.TargetedCast);
        Assert.Equal(0u, automation.UsedItem);

        Assert.True(controller.Tick(1d, canAct: true));
        Assert.Equal("Casting Eradicate Life Magic Self", controller.Status);
        automation.CastCompletion = new PluginCastCompletion(
            1,
            SelfDispel,
            1u,
            0u);
        Assert.True(controller.Tick(0.05d, canAct: true));
        Assert.Equal(
            "Dispel completed: Eradicate Life Magic Self",
            controller.Status);
    }

    [Fact]
    public void MissingChoriziteFallsThroughToOfficialDispelItemOrder()
    {
        var automation = new Automation
        {
            Active = [new PluginActiveEnchantment(100u, 7u, 7, 120d)],
            SpellLookup = [Vulnerability(100u, 375), SelfDispelSpell()],
            KnownSpellIds = new HashSet<uint> { SelfDispel },
            Inventory =
            [
                Item(20u, "Condensed Dispel Potion"),
                Item(21u, "Black Market Gem of Dispelling"),
            ],
            Mode = PluginCombatMode.Magic,
        };
        var controller = new DispelController(
            new Host(automation),
            new VitalSettings
            {
                CastDispelSelf = true,
                UseDispelItems = true,
            });

        Assert.True(controller.Tick(0d, canAct: true));

        Assert.Equal(21u, automation.UsedItem);
        Assert.Equal(default, automation.TargetedCast);
    }

    [Fact]
    public void DispelItemsIgnoreVulnerabilitiesAboveTheirDifficultyLimit()
    {
        var automation = new Automation
        {
            Active = [new PluginActiveEnchantment(100u, 7u, 7, 120d)],
            SpellLookup = [Vulnerability(100u, 401)],
            Inventory = [Item(11u, "Rune of Dispel")],
            Mode = PluginCombatMode.Magic,
        };
        var controller = new DispelController(
            new Host(automation),
            new VitalSettings { UseDispelItems = true });

        Assert.False(controller.Tick(0d, canAct: true));
        Assert.Equal(0u, automation.UsedItem);
        Assert.Equal("Dispel idle", controller.Status);
    }

    [Fact]
    public void AttenuatedAwakenerDispelsTheHighestScoringNearbyFellow()
    {
        PluginSpellInfo fire = Vulnerability(100u, 350) with
        {
            QualityOverride = 300,
        };
        PluginSpellInfo cold = new(
            101u,
            "Cold Vulnerability Other VII",
            Family: 8u,
            Tier: 7,
            Difficulty: 350,
            ManaCost: 0,
            DurationSeconds: 300f,
            School: 32u,
            Description: string.Empty,
            IsSelfTargeted: false,
            IsBeneficial: false)
        {
            IsDebuff = true,
            IsOffensive = true,
            QualityOverride = 320,
        };
        var automation = new Automation
        {
            SpellLookup = [fire, cold],
            Inventory =
            [
                Item(50u, "Attenuated Awakener") with
                {
                    EquippedLocation = 1u,
                },
            ],
            Mode = PluginCombatMode.Magic,
            SkillsById = new Dictionary<uint, PluginSkillInfo>
            {
                [31u] = new(31u, "Creature Enchantment",
                    PluginSkillTraining.Trained, 300u),
                [14u] = new(14u, "Arcane Lore",
                    PluginSkillTraining.Trained, 110u),
            },
            Members =
            [
                Fellow(2u, "One vuln", 4f),
                Fellow(3u, "Two vulns", 5f),
                Fellow(4u, "Too far", 5.1f),
            ],
            TrackedByTarget = new Dictionary<uint, IReadOnlyList<PluginTrackedEnchantment>>
            {
                [2u] = [Tracked(2u, fire)],
                [3u] = [Tracked(3u, fire), Tracked(3u, cold)],
                [4u] = [Tracked(4u, fire), Tracked(4u, cold)],
            },
        };
        var controller = new DispelController(
            new Host(automation),
            new VitalSettings { UseDispelDrum = true });

        Assert.True(controller.Tick(0d, canAct: true));
        Assert.Equal((50u, 3u), automation.AppliedItem);
        Assert.Equal(
            "Using Attenuated Awakener on Two vulns",
            controller.Status);
    }

    private static PluginSpellInfo Vulnerability(uint id, int difficulty) => new(
        id,
        "Fire Vulnerability Other VII",
        Family: 7u,
        Tier: 7,
        Difficulty: difficulty,
        ManaCost: 0,
        DurationSeconds: 300f,
        School: 32u,
        Description: string.Empty,
        IsSelfTargeted: false,
        IsBeneficial: false)
    {
        IsDebuff = true,
        IsOffensive = true,
    };

    private static PluginSpellInfo SelfDispelSpell() => new(
        SelfDispel,
        "Eradicate Life Magic Self",
        Family: 8u,
        Tier: 7,
        Difficulty: 400,
        ManaCost: 10,
        DurationSeconds: 0f,
        School: 33u,
        Description: string.Empty,
        IsSelfTargeted: true,
        IsBeneficial: true);

    private static PluginFellowMember Fellow(uint id, string name, float distance) =>
        new(id, name, 100u, 100u, 100u, 100u, 100u, 100u, distance);

    private static PluginTrackedEnchantment Tracked(
        uint target,
        PluginSpellInfo spell) => new(
            target,
            spell.SpellId,
            spell.Family,
            spell.Quality,
            spell.IsUntargeted,
            120d);

    private static PluginInventoryItem Item(uint id, string name) => new(
        id, 0u, name, 0x80u, 1u, 0u, 0u, 0u, 0u, 0u, 0u,
        1, 0, 0, 0u, 0, 0, 0u, false, 0d, 0, 0, 0, 0d, 0, 0, 0);

    private sealed class Automation
        : IAutomationSurface, ICharacterInfo, ISpellCatalog, IMagicCommands,
          ICombatAutomation, IItemAutomation
          , IFellowshipAutomation, IEnchantmentAutomation
    {
        public bool IsAvailable => true;
        public ICharacterInfo Character => this;
        public ISpellCatalog Spells => this;
        public IMagicCommands Magic => this;
        public IPluginChat Chat => NoOpAutomationSurface.Instance;
        public ICombatAutomation Combat => this;
        public IItemAutomation Items => this;
        public IFellowshipAutomation Fellowship => this;
        public IEnchantmentAutomation Enchantments => this;
        public bool IsInWorld => true;
        public uint ObjectId => 1u;
        public uint CurrentHealth => 100u;
        public uint MaxHealth => 100u;
        public uint CurrentStamina => 100u;
        public uint MaxStamina => 100u;
        public uint CurrentMana => 100u;
        public uint MaxMana => 100u;
        public IReadOnlyDictionary<uint, PluginSkillInfo> SkillsById { get; init; } =
            new Dictionary<uint, PluginSkillInfo>();
        public IReadOnlyList<PluginSkillInfo> Skills => [.. SkillsById.Values];
        public IReadOnlyList<PluginAttributeInfo> Attributes => [];
        public IReadOnlyList<PluginActiveEnchantment> ActiveEnchantments => Active;
        public IReadOnlyList<PluginActiveEnchantment> Active { get; init; } = [];
        public IReadOnlyList<PluginSpellInfo> KnownSelfBuffs => [];
        public IReadOnlyList<PluginSpellInfo> SpellLookup { get; init; } = [];
        public IReadOnlySet<uint> KnownSpellIds { get; init; } =
            new HashSet<uint>();
        public IReadOnlyList<PluginInventoryItem> Inventory { get; init; } = [];
        public PluginCombatMode Mode { get; set; }
        public PluginCombatSnapshot Snapshot => new(
            0u, Mode, default, 0f, 0f, false, false, false, false);
        public bool IsCasting => false;
        public PluginCastCompletion CastCompletion { get; set; }
        public PluginCastCompletion LastCompletion => CastCompletion;
        public PluginItemUseCompletion ItemCompletion { get; set; }
        PluginItemUseCompletion IItemAutomation.LastCompletion => ItemCompletion;
        bool IItemAutomation.IsAvailable => true;
        bool IItemAutomation.IsBusy => false;
        public (uint Spell, uint Target) TargetedCast { get; private set; }
        public uint UsedItem { get; private set; }
        public (uint Source, uint Target) AppliedItem { get; private set; }
        public bool IsInFellowship => Members.Count != 0;
        public IReadOnlyList<PluginFellowMember> Members { get; init; } = [];
        public IReadOnlyDictionary<uint, IReadOnlyList<PluginTrackedEnchantment>>
            TrackedByTarget { get; init; } =
                new Dictionary<uint, IReadOnlyList<PluginTrackedEnchantment>>();

        public bool TryGetSkill(uint skillId, out PluginSkillInfo skill)
        {
            return SkillsById.TryGetValue(skillId, out skill);
        }
        public bool TryGet(uint spellId, out PluginSpellInfo info)
        {
            foreach (PluginSpellInfo spell in SpellLookup)
            {
                if (spell.SpellId == spellId)
                {
                    info = spell;
                    return true;
                }
            }
            info = default;
            return false;
        }
        public bool IsKnown(uint spellId) => KnownSpellIds.Contains(spellId);
        public IReadOnlyList<PluginCombatTarget> CaptureHostileTargets(
            float maximumDistance) => [];
        public PluginCombatCommandResult EnterDefaultMode() =>
            new(PluginCombatCommandStatus.AlreadyReady);
        public PluginCombatCommandResult EnterMode(PluginCombatMode mode)
        {
            Mode = mode;
            return new(PluginCombatCommandStatus.ModeChangeSent);
        }
        public PluginCombatCommandResult BeginPhysicalAttack(
            uint targetObjectId,
            PluginAttackHeight height,
            float power) => new(PluginCombatCommandStatus.Refused);
        public PluginCombatCommandResult ReleasePhysicalAttack() =>
            new(PluginCombatCommandStatus.Refused);
        public PluginCombatCommandResult AbortPhysicalAttack() =>
            new(PluginCombatCommandStatus.Stopped);
        public PluginCastGate EvaluateGate(uint spellId) => PluginCastGate.Ready;
        public PluginCastGate EvaluateGate(uint spellId, uint targetObjectId) =>
            PluginCastGate.Ready;
        public bool Cast(uint spellId) => false;
        public bool Cast(uint spellId, uint targetObjectId)
        {
            TargetedCast = (spellId, targetObjectId);
            return true;
        }
        public IReadOnlyList<PluginInventoryItem> CaptureOwnedItems() => Inventory;
        public PluginItemCommandResult Use(uint objectId)
        {
            UsedItem = objectId;
            return new(PluginItemCommandStatus.Started);
        }
        public PluginItemCommandResult Apply(uint objectId, uint targetObjectId)
        {
            AppliedItem = (objectId, targetObjectId);
            return new(PluginItemCommandStatus.Started);
        }
        public IReadOnlyList<PluginFellowMember> CaptureMembers() => Members;
        public IReadOnlyList<PluginTrackedEnchantment> Capture(uint targetObjectId) =>
            TrackedByTarget.TryGetValue(targetObjectId, out var tracked)
                ? tracked
                : [];
    }

    private sealed class Host(Automation automation) : IPluginHost
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
