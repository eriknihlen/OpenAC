using AcDream.Plugin.Abstractions;

namespace AcDream.Plugins.MossTank.Tests;

public sealed class PetAutomationTests
{
    [Fact]
    public void Select_UsesHighestPriorityTargetsElementAndDensityRange()
    {
        var settings = Settings(MonsterDamageType.PlayerAuto, MonsterDamageType.Fire);
        settings.PetRangeMode = PetRangeMode.Custom;
        settings.PetCustomRange = 8f;
        settings.PetMonsterDensity = 2;
        settings.Rules.Insert(0, new MonsterRule(
            "name#Boss",
            new MonsterRuleActions
            {
                Priority = 4,
                DamageType = MonsterDamageType.Fire,
                PetDamageType = MonsterDamageType.PlayerAuto,
            }));
        PluginInventoryItem cold = Device(1, 49387, mastery: 3, level: 100);
        PluginInventoryItem fire = Device(2, 49380, mastery: 3, level: 100);

        PetAutomationChoice choice = PetAutomation.Select(
            [cold, fire],
            [Target(10, "Trash", 4), Target(11, "Boss", 7), Target(12, "Far", 9)],
            new Character(mastery: 3, summoning: 300),
            settings,
            activeOwnedPetCount: 0,
            allowRefill: true,
            allowSummon: true);

        Assert.Equal(PetAutomationActionKind.Summon, choice.Kind);
        Assert.Equal(2u, choice.Device.ObjectId);
        Assert.Equal(11u, choice.Target.ObjectId);
        Assert.Equal(MonsterDamageType.Fire, choice.DamageType);
    }

    [Fact]
    public void Select_RejectsWrongMasteryAndInsufficientSummoningSkill()
    {
        var settings = Settings(MonsterDamageType.Auto);
        PluginInventoryItem wrongMastery = Device(1, 49380, mastery: 2, level: 50);
        PluginInventoryItem tooDifficult = Device(2, 49387, mastery: 3, level: 400);

        PetAutomationChoice choice = PetAutomation.Select(
            [wrongMastery, tooDifficult],
            [Target(10, "Target", 4)],
            new Character(mastery: 3, summoning: 300),
            settings,
            0,
            allowRefill: true,
            allowSummon: true);

        Assert.Equal(PetAutomationActionKind.None, choice.Kind);
    }

    [Fact]
    public void Select_ExplicitElementNeverFallsBackButPlayerAutoDoes()
    {
        PluginInventoryItem cold = Device(1, 49387, mastery: 3, level: 100);
        var explicitFire = Settings(MonsterDamageType.Fire);
        var playerAuto = Settings(
            MonsterDamageType.PlayerAuto,
            MonsterDamageType.Fire);
        var character = new Character(mastery: 3, summoning: 300);
        PluginCombatTarget[] targets = [Target(10, "Target", 4)];

        Assert.Equal(PetAutomationActionKind.None, PetAutomation.Select(
            [cold], targets, character, explicitFire, 0, true, true).Kind);
        Assert.Equal(PetAutomationActionKind.Summon, PetAutomation.Select(
            [cold], targets, character, playerAuto, 0, true, true).Kind);
    }

    [Fact]
    public void RefillWaitsForPeaceModeAndSummonDoesNot()
    {
        var settings = Settings(MonsterDamageType.Cold);
        settings.PetRefillCountNormal = 5;
        var items = new ItemAutomation
        {
            Items =
            [
                Device(1, 49387, mastery: 3, level: 100, structure: 5, maximum: 50),
                Item(2, PetDeviceCatalog.EncapsulatedSpiritWeenieClassId),
            ],
        };
        var character = new Character(3, 300);
        PluginCombatTarget[] targets = [Target(10, "Target", 4)];
        var automation = new PetAutomation();
        bool inPeace = false;

        Assert.True(automation.Tick(
            items,
            character,
            targets,
            settings,
            1d,
            out string blocked,
            readyToRefillInPeace: () => inPeace));
        Assert.Empty(items.Applies);
        Assert.Contains("peace mode", blocked, StringComparison.Ordinal);

        inPeace = true;
        Assert.True(automation.Tick(
            items,
            character,
            targets,
            settings,
            1.1d,
            out _,
            readyToRefillInPeace: () => inPeace));
        Assert.Equal([(2u, 1u)], items.Applies);
    }

    [Fact]
    public void Select_RefillsChosenDeviceWithEncapsulatedSpiritBeforeSummon()
    {
        var settings = Settings(MonsterDamageType.Cold);
        settings.PetRefillCountNormal = 5;
        PluginInventoryItem device = Device(
            1, 49387, mastery: 3, level: 100, structure: 5, maximum: 50);
        PluginInventoryItem spirit = Item(
            2, PetDeviceCatalog.EncapsulatedSpiritWeenieClassId);

        PetAutomationChoice choice = PetAutomation.Select(
            [device, spirit],
            [Target(10, "Target", 4)],
            new Character(mastery: 3, summoning: 300),
            settings,
            0,
            allowRefill: true,
            allowSummon: true);

        Assert.Equal(PetAutomationActionKind.Refill, choice.Kind);
        Assert.Equal(spirit.ObjectId, choice.Tool.ObjectId);
        Assert.Equal(device.ObjectId, choice.Device.ObjectId);
    }

    [Fact]
    public void Select_DoesNothingWhileOwnedCombatPetIsActive()
    {
        var settings = Settings(MonsterDamageType.Cold);

        PetAutomationChoice choice = PetAutomation.Select(
            [Device(1, 49387, 3, 100)],
            [Target(10, "Target", 4)],
            new Character(3, 300),
            settings,
            activeOwnedPetCount: 1,
            allowRefill: true,
            allowSummon: true);

        Assert.Equal(PetAutomationActionKind.None, choice.Kind);
    }

    [Fact]
    public void Tick_WaitsForUseDoneAndHonorsRetailCooldown()
    {
        var items = new ItemAutomation
        {
            Items = [Device(1, 49387, 3, 100)],
        };
        var automation = new PetAutomation();
        var settings = Settings(MonsterDamageType.Cold);
        var character = new Character(3, 300);
        PluginCombatTarget[] targets = [Target(10, "Target", 4)];

        Assert.True(automation.Tick(
            items, character, targets, settings, 1d, out _));
        Assert.Equal(new[] { 1u }, items.Uses);

        Assert.True(automation.Tick(
            items, character, targets, settings, 1.1d, out string pending));
        Assert.Contains("Summoning", pending, StringComparison.Ordinal);
        Assert.Single(items.Uses);

        items.Completion = new PluginItemUseCompletion(1, 1, 0, 0);
        Assert.False(automation.Tick(
            items, character, targets, settings, 1.2d, out _));
        Assert.Single(items.Uses);

        Assert.False(automation.Tick(
            items, character, targets, settings, 46.1d, out _));
        Assert.Single(items.Uses);
        Assert.True(automation.Tick(
            items, character, targets, settings, 46.2d, out _));
        Assert.Equal(2, items.Uses.Count);
    }

    [Theory]
    [InlineData(48886u, (int)MonsterDamageType.Bludgeon)]
    [InlineData(49366u, (int)MonsterDamageType.Acid)]
    [InlineData(49380u, (int)MonsterDamageType.Fire)]
    [InlineData(49387u, (int)MonsterDamageType.Cold)]
    [InlineData(49373u, (int)MonsterDamageType.Electric)]
    public void Catalog_MapsRetailDeviceWcids(
        uint wcid,
        int expected)
        => Assert.Equal(
            (MonsterDamageType)expected,
            PetDeviceCatalog.DamageType(wcid));

    private static CombatSettings Settings(
        MonsterDamageType pet,
        MonsterDamageType attack = MonsterDamageType.Auto)
    {
        var settings = new CombatSettings { SummonPets = true };
        settings.Rules.Clear();
        settings.Rules.Add(new MonsterRule(
            "DEFAULT",
            new MonsterRuleActions
            {
                DamageType = attack,
                PetDamageType = pet,
            }));
        for (uint id = 1; id <= 20; id++)
            settings.CombatItemObjectIds.Add(id);
        return settings;
    }

    private static PluginCombatTarget Target(uint id, string name, float distance) =>
        new(id, name, 1, distance, 0, true, 1f);

    private static PluginInventoryItem Device(
        uint id,
        uint wcid,
        int mastery,
        int level,
        int structure = 50,
        int maximum = 50) =>
        Item(id, wcid) with
        {
            PetClass = 49000,
            SummoningMastery = mastery,
            UseRequiresSkill = 54,
            UseRequiresSkillLevel = level,
            Structure = structure,
            MaximumStructure = maximum,
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

    private sealed class Character(int mastery, uint summoning) : ICharacterInfo
    {
        public bool IsInWorld => true;
        public uint ObjectId => 1;
        public uint CurrentHealth => 100;
        public uint MaxHealth => 100;
        public uint CurrentStamina => 100;
        public uint MaxStamina => 100;
        public uint CurrentMana => 100;
        public uint MaxMana => 100;
        public int SummoningMastery => mastery;
        public IReadOnlyList<PluginSkillInfo> Skills =>
            [new(54, "Summoning", PluginSkillTraining.Trained, summoning)];
        public IReadOnlyList<PluginAttributeInfo> Attributes => [];
        public IReadOnlyList<PluginActiveEnchantment> ActiveEnchantments => [];

        public bool TryGetSkill(uint skillId, out PluginSkillInfo skill)
        {
            if (skillId == 54)
            {
                skill = Skills[0];
                return true;
            }
            skill = default;
            return false;
        }
    }

    private sealed class ItemAutomation : IItemAutomation
    {
        public bool IsAvailable => true;
        public bool IsBusy { get; set; }
        public int ActiveOwnedPetCount { get; set; }
        public PluginItemUseCompletion Completion { get; set; }
        public PluginItemUseCompletion LastCompletion => Completion;
        public IReadOnlyList<PluginInventoryItem> Items { get; set; } = [];
        public List<uint> Uses { get; } = [];
        public List<(uint Source, uint Target)> Applies { get; } = [];
        public IReadOnlyList<PluginInventoryItem> CaptureOwnedItems() => Items;

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
    }
}
