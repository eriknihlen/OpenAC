using AcDream.Plugin.Abstractions;

namespace AcDream.Plugins.MossTank.Tests;

public sealed class VitalRechargeTests
{
    [Fact]
    public void RetailDefaultsAreExactNineThresholds()
    {
        var settings = new VitalSettings();

        Assert.Equal(0.75, settings.NormalHealth);
        Assert.Equal(0.50, settings.NormalStamina);
        Assert.Equal(0.50, settings.NormalMana);
        Assert.Equal(0.01, settings.NoTargetHealth);
        Assert.Equal(0.01, settings.NoTargetStamina);
        Assert.Equal(0.01, settings.NoTargetMana);
        Assert.Equal(0.20, settings.HelperHealth);
        Assert.Equal(0.01, settings.HelperStamina);
        Assert.Equal(0.01, settings.HelperMana);
    }

    [Fact]
    public void NoTargetTopoffExtendsRatherThanReplacesCombatThreshold()
    {
        var settings = new VitalSettings { NoTargetHealth = 0.90 };
        var surface = new Surface { CurrentHealth = 80 };

        Assert.Null(VitalPlan.DecideNeed(surface, settings, noTarget: false));
        Assert.Equal(
            VitalKind.Health,
            VitalPlan.DecideNeed(surface, settings, noTarget: true));
    }

    [Fact]
    public void MagicHealthHandlerOrderMatchesOfficialDefaultTable()
    {
        Assert.Equal(
            [
                VitalRechargeMethod.StaminaToHealth,
                VitalRechargeMethod.ManaToHealth,
                VitalRechargeMethod.RegularSpell,
                VitalRechargeMethod.Food,
                VitalRechargeMethod.Kit,
            ],
            VitalRechargePlanner.Handlers(VitalKind.Health, true, 15));
        Assert.Equal(
            [
                VitalRechargeMethod.Kit,
                VitalRechargeMethod.StaminaToHealth,
                VitalRechargeMethod.ManaToHealth,
                VitalRechargeMethod.RegularSpell,
                VitalRechargeMethod.Food,
            ],
            VitalRechargePlanner.Handlers(VitalKind.Health, true, 16));
    }

    [Fact]
    public void NonMagicHealthConversionExistsOnlyInOfficialEmergencyBand()
    {
        Assert.Equal(
            [
                VitalRechargeMethod.Food,
                VitalRechargeMethod.Kit,
                VitalRechargeMethod.StaminaToHealth,
                VitalRechargeMethod.RegularSpell,
            ],
            VitalRechargePlanner.Handlers(VitalKind.Health, false, 10));
        Assert.Equal(
            [
                VitalRechargeMethod.Food,
                VitalRechargeMethod.Kit,
                VitalRechargeMethod.RegularSpell,
            ],
            VitalRechargePlanner.Handlers(VitalKind.Health, false, 15));
        Assert.Equal(
            [
                VitalRechargeMethod.Kit,
                VitalRechargeMethod.Food,
                VitalRechargeMethod.RegularSpell,
            ],
            VitalRechargePlanner.Handlers(VitalKind.Health, false, 16));
    }

    [Fact]
    public void RechargeHandlerSetCanOverrideOneStanceVitalBand()
    {
        const string profile =
            "combat-health-critical=Regular Spell > Kit Recharge; "
            + "magic-mana-normal=Recharge With Food > Regular Spell";

        Assert.Equal(
            [VitalRechargeMethod.RegularSpell, VitalRechargeMethod.Kit],
            VitalRechargePlanner.Handlers(
                VitalKind.Health,
                false,
                5,
                profile));
        Assert.Equal(
            [VitalRechargeMethod.Food, VitalRechargeMethod.RegularSpell],
            VitalRechargePlanner.Handlers(
                VitalKind.Mana,
                true,
                50,
                profile));
    }

    [Fact]
    public void RechargeBoostAdjustmentTemporarilyRaisesNeed()
    {
        var surface = new Surface { CurrentHealth = 80 };
        var settings = new VitalSettings { NormalHealth = 0.75 };

        Assert.Null(VitalPlan.DecideNeed(surface, settings, noTarget: false));
        Assert.Equal(
            VitalKind.Health,
            VitalPlan.DecideNeed(
                surface,
                settings,
                noTarget: false,
                healthCurrentAdjustment: 40));
    }

    [Fact]
    public void IncantationHealthAliasParticipatesInRegularSpellHandler()
    {
        var surface = new Surface
        {
            Mode = PluginCombatMode.Magic,
            CurrentHealth = 50,
            Spells =
            [
                Spell(
                    (uint)SpellId.AdjaSIntervention,
                    "Adja's Intervention",
                    1u,
                    400),
            ],
        };

        Assert.True(VitalRechargePlanner.TryPlan(
            VitalKind.Health,
            surface,
            new VitalSettings { MinimumHealKitSuccessChance = 100 },
            new CombatSettings(),
            out VitalRechargeChoice choice));
        Assert.Equal((uint)SpellId.AdjaSIntervention, choice.SpellId);
    }

    [Fact]
    public void MagicModeUsesProfiledViableKitBeforeRegularHealAboveEmergencyBand()
    {
        var surface = new Surface
        {
            Mode = PluginCombatMode.Magic,
            CurrentHealth = 50,
            Skills = [Skill(21u, 400u)],
            Items = [Kit(10u, "Plentiful Healing Kit", booster: 2)],
        };
        var combat = new CombatSettings();
        combat.ConsumableNames.Add("Plentiful Healing Kit");
        surface.Spells = [Spell(100u, "Heal Self VII", family: 1u, quality: 300)];

        Assert.True(VitalRechargePlanner.TryPlan(
            VitalKind.Health,
            surface,
            new VitalSettings(),
            combat,
            out VitalRechargeChoice choice));
        Assert.Equal(VitalRechargeSourceKind.Kit, choice.SourceKind);
        Assert.Equal(10u, choice.ItemObjectId);
    }

    [Fact]
    public void EmergencyMagicHealthPrefersStaminaConversionBeforeKit()
    {
        var surface = new Surface
        {
            Mode = PluginCombatMode.Magic,
            CurrentHealth = 10,
            Skills = [Skill(21u, 400u), Skill(33u, 400u)],
            Items = [Kit(10u, "Plentiful Healing Kit", booster: 2)],
            Spells =
            [
                Spell(101u, "Stamina to Health Self VII", 5u, 300),
                Spell(102u, "Heal Self VII", 1u, 300),
            ],
        };
        var combat = new CombatSettings();
        combat.ConsumableNames.Add("Plentiful Healing Kit");

        Assert.True(VitalRechargePlanner.TryPlan(
            VitalKind.Health,
            surface,
            new VitalSettings(),
            combat,
            out VitalRechargeChoice choice));
        Assert.Equal(VitalRechargeSourceKind.LearnedSpell, choice.SourceKind);
        Assert.Equal(101u, choice.SpellId);
    }

    [Fact]
    public void DirectLearnedSpellWinsFinalQualityTieAgainstCasterItem()
    {
        PluginSpellInfo learned = Spell(101u, "Heal Self VII", 1u, 300);
        PluginSpellInfo itemSpell = Spell(102u, "Heal Self VII", 1u, 300);
        var surface = new Surface
        {
            Mode = PluginCombatMode.Magic,
            CurrentHealth = 50,
            Spells = [learned],
            Lookup = [itemSpell],
            Items = [Caster(20u, "Healing Lens", itemSpell.SpellId)],
        };
        var combat = new CombatSettings();
        combat.CombatItemNames.Add("Healing Lens");

        Assert.True(VitalRechargePlanner.TryPlan(
            VitalKind.Health,
            surface,
            new VitalSettings { MinimumHealKitSuccessChance = 100 },
            combat,
            out VitalRechargeChoice choice));
        Assert.Equal(VitalRechargeSourceKind.LearnedSpell, choice.SourceKind);
        Assert.Equal(learned.SpellId, choice.SpellId);
    }

    [Fact]
    public void HelperChoosesLowestInRangeFellowAndStrongestFamilySpell()
    {
        PluginSpellInfo basis = Spell(
            (uint)SpellId.AdjaSGift,
            "Adja's Gift",
            900u,
            100);
        PluginSpellInfo strong = Spell(300u, "Adja's Grace", 900u, 350);
        var surface = new Surface
        {
            Mode = PluginCombatMode.Magic,
            Spells = [strong],
            Lookup = [basis],
            InFellowship = true,
            Fellows =
            [
                Fellow(70u, "Near", health: 19, distance: 10f),
                Fellow(71u, "Lowest", health: 5, distance: 20f),
                Fellow(72u, "Out of range", health: 1, distance: 100f),
            ],
        };

        Assert.True(VitalRechargePlanner.TryPlanHelper(
            surface,
            new VitalSettings(),
            out VitalRechargeChoice choice));
        Assert.Equal(VitalKind.Health, choice.Vital);
        Assert.Equal(71u, choice.TargetObjectId);
        Assert.Equal(strong.SpellId, choice.SpellId);
    }

    [Fact]
    public void HealKitChanceUsesRetailLogisticDifficultyFormula()
    {
        var surface = new Surface { CurrentHealth = 50 };
        double chance = VitalRechargePlanner.HealKitChance(
            90u,
            10,
            surface,
            VitalKind.Health,
            PluginCombatMode.Peace);

        Assert.Equal(0.5d, chance, precision: 10);
    }

    private static PluginSkillInfo Skill(uint id, uint current) =>
        new(id, string.Empty, PluginSkillTraining.Trained, current);

    private static PluginSpellInfo Spell(
        uint id,
        string name,
        uint family,
        int quality) =>
        new(id, name, family, 7, quality, 10, 0f, 33u, string.Empty, true, true);

    private static PluginInventoryItem Kit(
        uint id,
        string name,
        int booster) => Item(id, name) with
        {
            BoosterVital = booster,
            BoostValue = 0,
            HealKitModifier = 1.2,
        };

    private static PluginInventoryItem Caster(
        uint id,
        string name,
        uint spellId) => new(
            id, 1u, name, 0x8000u, 1u, 0u, 0u, 0u, 0u, 0u, 0u,
            1, 0, 0, spellId, 0, 0, 0u, false, 0d, 0, 0, 0, 0d,
            0, 0, 0);

    private static PluginInventoryItem Item(uint id, string name) => new(
        id, 1u, name, 0x80u, 1u, 0u, 0u, 0u, 0u, 0u, 0u,
        1, 1, 1, 0u, 0, 0, 0u, false, 0d, 0, 0, 0, 0d,
        21, 0, 0);

    private static PluginFellowMember Fellow(
        uint id,
        string name,
        uint health,
        float distance) => new(
            id, name, health, 100u, 100u, 100u, 100u, 100u, distance);

    private sealed class Surface :
        IAutomationSurface,
        ICharacterInfo,
        ISpellCatalog,
        IMagicCommands,
        IPluginChat,
        ICombatAutomation,
        IItemAutomation,
        IFellowshipAutomation
    {
        public bool IsAvailable => true;
        public ICharacterInfo Character => this;
        public ISpellCatalog SpellsCatalog => this;
        ISpellCatalog IAutomationSurface.Spells => this;
        public IMagicCommands Magic => this;
        public IPluginChat Chat => this;
        public ICombatAutomation Combat => this;
        public IItemAutomation ItemsAutomation => this;
        IItemAutomation IAutomationSurface.Items => this;
        public IFellowshipAutomation Fellowship => this;
        public bool IsInWorld => true;
        public uint ObjectId => 1u;
        public uint CurrentHealth { get; init; } = 100u;
        public uint MaxHealth { get; init; } = 100u;
        public uint CurrentStamina { get; init; } = 100u;
        public uint MaxStamina { get; init; } = 100u;
        public uint CurrentMana { get; init; } = 100u;
        public uint MaxMana { get; init; } = 100u;
        public IReadOnlyList<PluginSkillInfo> Skills { get; init; } = [];
        public IReadOnlyList<PluginAttributeInfo> Attributes => [];
        public IReadOnlyList<PluginActiveEnchantment> ActiveEnchantments => [];
        public IReadOnlyList<PluginSpellInfo> Spells { get; set; } = [];
        public IReadOnlyList<PluginSpellInfo> Lookup { get; init; } = [];
        public IReadOnlyList<PluginSpellInfo> KnownSelfBuffs => Spells;
        public IReadOnlyList<PluginInventoryItem> Items { get; init; } = [];
        public bool InFellowship { get; init; }
        public IReadOnlyList<PluginFellowMember> Fellows { get; init; } = [];
        public PluginCombatMode Mode { get; init; } = PluginCombatMode.Peace;
        public PluginCombatSnapshot Snapshot => new(0u, Mode, default, 0f, 0f,
            false, false, false, false);
        bool IItemAutomation.IsAvailable => true;
        bool IFellowshipAutomation.IsInFellowship => InFellowship;
        public bool IsCasting => false;

        public bool TryGetSkill(uint skillId, out PluginSkillInfo skill)
        {
            foreach (PluginSkillInfo candidate in Skills)
            {
                if (candidate.SkillId == skillId)
                {
                    skill = candidate;
                    return true;
                }
            }
            skill = default;
            return false;
        }

        public bool TryGet(uint spellId, out PluginSpellInfo info)
        {
            foreach (PluginSpellInfo candidate in Spells.Concat(Lookup))
            {
                if (candidate.SpellId == spellId)
                {
                    info = candidate;
                    return true;
                }
            }
            info = default;
            return false;
        }

        public IReadOnlyList<PluginInventoryItem> CaptureOwnedItems() => Items;
        public IReadOnlyList<PluginFellowMember> CaptureMembers() => Fellows;
        public IReadOnlyList<PluginCombatTarget> CaptureHostileTargets(float maximumDistance) => [];
        public PluginCombatCommandResult EnterDefaultMode() => new(PluginCombatCommandStatus.AlreadyReady);
        public PluginCombatCommandResult BeginPhysicalAttack(uint targetObjectId, PluginAttackHeight height, float power) => new(PluginCombatCommandStatus.Started);
        public PluginCombatCommandResult ReleasePhysicalAttack() => new(PluginCombatCommandStatus.Released);
        public PluginCombatCommandResult AbortPhysicalAttack() => new(PluginCombatCommandStatus.Stopped);
        public PluginCastGate EvaluateGate(uint spellId) => PluginCastGate.Ready;
        public bool Cast(uint spellId) => true;
        public void PostSystemMessage(string text) { }
    }
}
