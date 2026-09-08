namespace AcDream.Plugin.Abstractions;

public enum PluginSkillTraining
{
    Unknown = 0,
    Untrained,
    Trained,
    Specialized,
}

public readonly record struct PluginSpellInfo(
    uint SpellId,
    string Name,
    uint Family,
    int Tier,
    int Difficulty,
    int ManaCost,
    float DurationSeconds,
    uint School,
    string Description,
    bool IsSelfTargeted,
    bool IsBeneficial)
{
    public bool IsDebuff { get; init; }
    public bool IsOffensive { get; init; }
    public bool IsFellowship { get; init; }
    public bool IsUntargeted { get; init; }
    /// <summary>
    /// VTank's spell-facing rule: targeted spells require facing except the
    /// authored family range 222..235.
    /// </summary>
    public bool RequiresTurnTo { get; init; }
    public bool IsProjectile { get; init; }
    public bool IsDamageOverTime { get; init; }
    public uint RawFlags { get; init; }
    public int SpellType { get; init; }
    public uint TargetMask { get; init; }
    public float BaseRangeConstant { get; init; }
    public float BaseRangeModifier { get; init; }
    public IReadOnlyList<uint> FormulaComponentIds { get; init; } =
        Array.Empty<uint>();
    /// <summary>
    /// VTank's spell quality. It is the portal spell difficulty unless its
    /// official GameInfoDB override supplies a replacement.
    /// </summary>
    public int? QualityOverride { get; init; }
    public int Quality => QualityOverride ?? Difficulty;
    public uint IconId { get; init; }
}

public readonly record struct PluginActiveEnchantment(
    uint SpellId,
    uint Family,
    int Tier,
    double SecondsRemaining);

public readonly record struct PluginSpellComponentInfo(
    uint ComponentId,
    uint WeenieClassId,
    string Name,
    double BurnRate,
    uint GestureId,
    double GestureSpeed,
    uint IconId,
    uint SortKey,
    string Type,
    string Word);

public readonly record struct PluginSkillInfo(
    uint SkillId,
    string Name,
    PluginSkillTraining Training,
    uint Current)
{
    public uint Base { get; init; } = Current;
    public uint IconId { get; init; }
}

/// <summary>One primary attribute. <paramref name="Kind"/> is 0..5.</summary>
public readonly record struct PluginAttributeInfo(
    int Kind,
    string Name,
    uint Current)
{
    /// <summary>Unenchanted primary-attribute value.</summary>
    public uint Base { get; init; } = Current;
}

public enum PluginCastGate
{
    /// <summary>No live session, or the surface is not bound yet.</summary>
    Unavailable = 0,
    Ready,
    NotKnown,
    Busy,
    /// <summary>The host rejected it for a reason not modelled here.</summary>
    Refused,
}

public interface ICharacterInfo
{
    bool IsInWorld { get; }

    string Name => string.Empty;

    /// <summary>Server-advertised world name used to scope global variables.</summary>
    string WorldName => string.Empty;

    /// <summary>Authenticated account name; expression surfaces expose only its hash.</summary>
    string AccountName => string.Empty;

    int CharacterIndex => -1;

    int Level => 0;

    /// <summary>Unused ordinary slots in the main pack.</summary>
    int MainPackFreeSlots => 0;

    uint ObjectId { get; }

    uint CurrentHealth { get; }
    uint MaxHealth { get; }
    uint CurrentStamina { get; }
    uint MaxStamina { get; }
    uint CurrentMana { get; }
    uint MaxMana { get; }

    int SummoningMastery => 0;

    IReadOnlyList<PluginSkillInfo> Skills { get; }

    /// <summary>The six primary attributes.</summary>
    IReadOnlyList<PluginAttributeInfo> Attributes { get; }

    /// <summary>
    /// Enchantments in force on the local player. Snapshot semantics: the list
    /// is rebuilt by the host, never mutated in place under a reader.
    /// </summary>
    IReadOnlyList<PluginActiveEnchantment> ActiveEnchantments { get; }

    bool TryGetSkill(uint skillId, out PluginSkillInfo skill);
}

public interface ISpellCatalog
{
    IReadOnlyList<PluginSpellInfo> KnownSelfBuffs { get; }

    IReadOnlyList<PluginSpellInfo> KnownAttackSpells =>
        Array.Empty<PluginSpellInfo>();

    IReadOnlyList<PluginSpellInfo> KnownCombatSpells =>
        Array.Empty<PluginSpellInfo>();

    bool IsKnown(uint spellId) => false;

    bool TryGet(uint spellId, out PluginSpellInfo info);

    bool TryGetComponent(uint componentId, out PluginSpellComponentInfo info)
    {
        info = default;
        return false;
    }

    double GetCooldownRemaining(uint cooldownId) => 0d;
}

public readonly record struct PluginChatMessage(
    ulong Sequence,
    uint SenderObjectId,
    int Kind,
    string Sender,
    string Text,
    string ChannelName);

public interface IPluginChat
{
    IReadOnlyList<PluginChatMessage> CaptureMessages(ulong afterSequence) =>
        Array.Empty<PluginChatMessage>();

    void PostSystemMessage(string text);

    bool Submit(string text) => false;
}

public interface IMagicCommands
{
    bool IsCasting { get; }

    PluginCastCompletion LastCompletion => default;

    PluginCastGate EvaluateGate(uint spellId);

    bool Cast(uint spellId);

    PluginCastGate EvaluateGate(uint spellId, uint targetObjectId) =>
        PluginCastGate.Refused;

    bool Cast(uint spellId, uint targetObjectId) => false;
}

public interface IAutomationSurface
{
    bool IsAvailable { get; }

    ICharacterInfo Character { get; }
    ISpellCatalog Spells { get; }
    IMagicCommands Magic { get; }
    IPluginChat Chat { get; }

    ICombatAutomation Combat => NoOpAutomationSurface.Instance;

    IEquipmentAutomation Equipment => NoOpAutomationSurface.Instance;

    IItemAutomation Items => NoOpAutomationSurface.Instance;

    ILootAutomation Loot => NoOpAutomationSurface.Instance;

    /// <summary>Authoritative fellowship vitals for helper spell policy.</summary>
    IFellowshipAutomation Fellowship => NoOpAutomationSurface.Instance;

    IEnchantmentAutomation Enchantments => NoOpAutomationSurface.Instance;

    INavigationAutomation Navigation => NoOpAutomationSurface.Instance;

    IWorldObjectAutomation Objects => NoOpAutomationSurface.Instance;

    IWorldTimeAutomation WorldTime => NoOpAutomationSurface.Instance;

    ILoginAutomation Login => NoOpAutomationSurface.Instance;

    INetworkAutomation Network => NoOpAutomationSurface.Instance;

    IRecoveryAutomation Recovery => NoOpAutomationSurface.Instance;

    IProjectileAutomation Projectiles => NoOpAutomationSurface.Instance;

    ISelectionAutomation Selection => NoOpAutomationSurface.Instance;
}

public sealed class NoOpAutomationSurface
    : IAutomationSurface, ICharacterInfo, ISpellCatalog, IMagicCommands,
      IPluginChat, ICombatAutomation
      , IEquipmentAutomation, IItemAutomation, ILootAutomation,
      IFellowshipAutomation, IEnchantmentAutomation, INavigationAutomation
      , IWorldObjectAutomation, IWorldTimeAutomation, ILoginAutomation,
      INetworkAutomation, IRecoveryAutomation, IProjectileAutomation
      , ISelectionAutomation
{
    public static NoOpAutomationSurface Instance { get; } = new();

    private NoOpAutomationSurface()
    {
    }

    public bool IsAvailable => false;
    public ICharacterInfo Character => this;
    public ISpellCatalog Spells => this;
    public IMagicCommands Magic => this;
    public IPluginChat Chat => this;
    public ICombatAutomation Combat => this;
    public IEquipmentAutomation Equipment => this;
    public IItemAutomation Items => this;
    public ILootAutomation Loot => this;
    public IFellowshipAutomation Fellowship => this;
    public IEnchantmentAutomation Enchantments => this;
    public INavigationAutomation Navigation => this;
    public IWorldObjectAutomation Objects => this;
    public IWorldTimeAutomation WorldTime => this;
    public ILoginAutomation Login => this;
    public INetworkAutomation Network => this;
    public IRecoveryAutomation Recovery => this;
    public IProjectileAutomation Projectiles => this;
    public ISelectionAutomation Selection => this;

    public void PostSystemMessage(string text)
    {
    }

    public bool Submit(string text) => false;

    PluginNavigationSnapshot INavigationAutomation.Snapshot => default;
    public bool TryGetObject(
        uint objectId,
        out PluginNavigationObject value)
    {
        value = default;
        return false;
    }
    public PluginNavigationCommandStatus SetMovementIntent(
        in PluginMovementIntent intent) =>
        PluginNavigationCommandStatus.Unavailable;
    public PluginNavigationCommandStatus ClearMovementIntent() =>
        PluginNavigationCommandStatus.Unavailable;

    public bool IsInWorld => false;
    public uint ObjectId => 0;
    public uint CurrentHealth => 0;
    public uint MaxHealth => 0;
    public uint CurrentStamina => 0;
    public uint MaxStamina => 0;
    public uint CurrentMana => 0;
    public uint MaxMana => 0;
    public int SummoningMastery => 0;

    public IReadOnlyList<PluginSkillInfo> Skills { get; } = Array.Empty<PluginSkillInfo>();
    public IReadOnlyList<PluginAttributeInfo> Attributes { get; } =
        Array.Empty<PluginAttributeInfo>();
    public IReadOnlyList<PluginActiveEnchantment> ActiveEnchantments { get; } =
        Array.Empty<PluginActiveEnchantment>();
    public IReadOnlyList<PluginSpellInfo> KnownSelfBuffs { get; } =
        Array.Empty<PluginSpellInfo>();
    public IReadOnlyList<PluginSpellInfo> KnownAttackSpells { get; } =
        Array.Empty<PluginSpellInfo>();
    public IReadOnlyList<PluginSpellInfo> KnownCombatSpells { get; } =
        Array.Empty<PluginSpellInfo>();

    public bool TryGetSkill(uint skillId, out PluginSkillInfo skill)
    {
        skill = default;
        return false;
    }

    public bool TryGet(uint spellId, out PluginSpellInfo info)
    {
        info = default;
        return false;
    }

    public bool IsCasting => false;
    public PluginCastCompletion LastCompletion => default;
    public PluginCastGate EvaluateGate(uint spellId) => PluginCastGate.Unavailable;
    public bool Cast(uint spellId) => false;
    public PluginCastGate EvaluateGate(uint spellId, uint targetObjectId) =>
        PluginCastGate.Unavailable;
    public bool Cast(uint spellId, uint targetObjectId) => false;

    public PluginCombatSnapshot Snapshot => default;
    public IReadOnlyList<PluginCombatTarget> CaptureHostileTargets(
        float maximumDistance) => Array.Empty<PluginCombatTarget>();
    public PluginCombatCommandResult EnterDefaultMode() => new(
        PluginCombatCommandStatus.Unavailable);
    bool IEquipmentAutomation.IsAvailable => false;
    bool IEquipmentAutomation.IsBusy => false;
    public IReadOnlyList<PluginEquipmentItem> CaptureOwnedEquipment() =>
        Array.Empty<PluginEquipmentItem>();
    public PluginEquipmentCommandResult Equip(
        uint objectId,
        uint requestedLocation = 0u) =>
        new(PluginEquipmentCommandStatus.Unavailable);
    bool IItemAutomation.IsAvailable => false;
    bool IItemAutomation.IsBusy => false;
    int IItemAutomation.ActiveOwnedPetCount => 0;
    PluginItemUseCompletion IItemAutomation.LastCompletion => default;
    PluginItemUseCompletion ILootAutomation.LastItemUseCompletion => default;
    PluginInventoryCompletion ILootAutomation.LastInventoryCompletion => default;
    PluginAppraisalState ILootAutomation.Appraisal => default;
    public IReadOnlyList<PluginInventoryItem> CaptureOwnedItems() =>
        Array.Empty<PluginInventoryItem>();
    public PluginItemCommandResult Use(uint objectId) =>
        new(PluginItemCommandStatus.Unavailable);
    public PluginItemCommandResult Apply(uint objectId, uint targetObjectId) =>
        new(PluginItemCommandStatus.Unavailable);
    public IReadOnlyList<PluginLootContainer> CaptureCorpses(
        float maximumDistance) => Array.Empty<PluginLootContainer>();
    public IReadOnlyList<PluginInventoryItem> CaptureCurrentContents() =>
        Array.Empty<PluginInventoryItem>();
    public PluginItemCommandResult Open(uint containerObjectId) =>
        new(PluginItemCommandStatus.Unavailable);
    public PluginItemCommandResult Identify(uint objectId) =>
        new(PluginItemCommandStatus.Unavailable);
    public PluginItemCommandResult Pickup(uint objectId, bool mainPack = false) =>
        new(PluginItemCommandStatus.Unavailable);
    public bool IsInFellowship => false;
    public IReadOnlyList<PluginFellowMember> CaptureMembers() =>
        Array.Empty<PluginFellowMember>();
    public IReadOnlyList<PluginTrackedEnchantment> Capture(
        uint targetObjectId) => Array.Empty<PluginTrackedEnchantment>();
    public bool ReportCast(
        uint targetObjectId,
        uint spellId,
        double durationSeconds) => false;
    PluginWorldTimeSnapshot IWorldTimeAutomation.Snapshot => default;
    public PluginCombatCommandResult BeginPhysicalAttack(
        uint targetObjectId, PluginAttackHeight height, float power) => new(
            PluginCombatCommandStatus.Unavailable);
    public PluginCombatCommandResult ReleasePhysicalAttack() => new(
        PluginCombatCommandStatus.Unavailable);
    public PluginCombatCommandResult AbortPhysicalAttack() => new(
        PluginCombatCommandStatus.Unavailable);
}
