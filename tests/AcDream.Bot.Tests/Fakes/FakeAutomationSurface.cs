using AcDream.Plugin.Abstractions;

namespace AcDream.Bot.Tests.Fakes;

/// <summary>
/// A scriptable automation surface. Tests set state directly and read back
/// the commands the bot issued. Every command is recorded in <see cref="Commands"/>
/// in order, as "verb:detail".
/// </summary>
internal sealed class FakeAutomationSurface
    : IAutomationSurface, ICharacterInfo, ISpellCatalog, IMagicCommands,
      IPluginChat, ICombatAutomation, ILootAutomation, INavigationAutomation,
      IItemAutomation, IWorldObjectAutomation
{
    public List<string> Commands { get; } = [];

    // ── availability / character ──────────────────────────────────────────
    public bool IsAvailable { get; set; } = true;
    public bool IsInWorld { get; set; } = true;
    public uint ObjectId { get; set; } = 0x50000001u;
    public uint CurrentHealth { get; set; } = 100;
    public uint MaxHealth { get; set; } = 100;
    public uint CurrentStamina { get; set; } = 100;
    public uint MaxStamina { get; set; } = 100;
    public uint CurrentMana { get; set; } = 100;
    public uint MaxMana { get; set; } = 100;
    public IReadOnlyList<PluginSkillInfo> Skills { get; set; } = [];
    public IReadOnlyList<PluginAttributeInfo> Attributes { get; set; } = [];
    public List<PluginActiveEnchantment> Enchantments { get; } = [];
    IReadOnlyList<PluginActiveEnchantment> ICharacterInfo.ActiveEnchantments => Enchantments;
    public bool TryGetSkill(uint skillId, out PluginSkillInfo skill)
    {
        skill = default;
        return false;
    }

    // ── spells ────────────────────────────────────────────────────────────
    public List<PluginSpellInfo> SelfBuffs { get; } = [];
    public List<PluginSpellInfo> AttackSpells { get; } = [];
    public List<PluginSpellInfo> CombatSpells { get; } = [];
    public HashSet<uint> MissingComponents { get; } = [];
    IReadOnlyList<PluginSpellInfo> ISpellCatalog.KnownSelfBuffs => SelfBuffs;
    IReadOnlyList<PluginSpellInfo> ISpellCatalog.KnownAttackSpells => AttackSpells;
    IReadOnlyList<PluginSpellInfo> ISpellCatalog.KnownCombatSpells => CombatSpells;
    public bool TryGet(uint spellId, out PluginSpellInfo info)
    {
        foreach (PluginSpellInfo candidate in SelfBuffs.Concat(AttackSpells).Concat(CombatSpells))
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

    // ── magic ─────────────────────────────────────────────────────────────
    public bool IsCasting { get; set; }
    public PluginCastCompletion LastCompletion { get; set; }
    public PluginCastRequestResult NextCastResult { get; set; } = PluginCastRequestResult.Sent;
    public PluginCastGate EvaluateGate(uint spellId) => PluginCastGate.Ready;
    public bool Cast(uint spellId) => RequestCast(spellId) == PluginCastRequestResult.Sent;
    public PluginCastRequestResult RequestCast(uint spellId)
    {
        Commands.Add($"cast:{spellId}");
        if (NextCastResult == PluginCastRequestResult.Sent)
            IsCasting = true;
        return NextCastResult;
    }
    public PluginCastRequestResult RequestCast(uint spellId, uint targetObjectId)
    {
        Commands.Add($"cast:{spellId}@{targetObjectId}");
        if (NextCastResult == PluginCastRequestResult.Sent)
            IsCasting = true;
        return NextCastResult;
    }
    public bool HasComponents(uint spellId) => !MissingComponents.Contains(spellId);

    /// <summary>Simulates the server finishing the in-flight cast.</summary>
    public void CompleteCast(uint spellId, bool success = true, uint target = 0u)
    {
        IsCasting = false;
        LastCompletion = new PluginCastCompletion(
            LastCompletion.Revision + 1, spellId, target, success ? 0u : 1u);
    }

    // ── chat ──────────────────────────────────────────────────────────────
    public List<string> SystemMessages { get; } = [];
    public void PostSystemMessage(string text) => SystemMessages.Add(text);

    // ── combat ────────────────────────────────────────────────────────────
    public PluginCombatSnapshot CombatSnapshot { get; set; } = new(
        0u, PluginCombatMode.Peace, PluginAttackHeight.Medium, 1f, 0f,
        false, false, false, false);
    PluginCombatSnapshot ICombatAutomation.Snapshot => CombatSnapshot;
    public List<PluginCombatTarget> Hostiles { get; } = [];
    public IReadOnlyList<PluginCombatTarget> CaptureHostileTargets(float maximumDistance) =>
        Hostiles.Where(h => h.Distance <= maximumDistance).ToArray();
    public PluginCombatCommandResult EnterDefaultMode()
    {
        Commands.Add("mode:default");
        return new(PluginCombatCommandStatus.ModeChangeSent);
    }
    public PluginCombatCommandResult EnterMode(PluginCombatMode mode)
    {
        Commands.Add($"mode:{mode}");
        if (CombatSnapshot.Mode == mode)
            return new(PluginCombatCommandStatus.AlreadyReady);
        CombatSnapshot = CombatSnapshot with { Mode = mode };
        return new(PluginCombatCommandStatus.ModeChangeSent);
    }
    public PluginCombatCommandResult BeginPhysicalAttack(uint targetObjectId, PluginAttackHeight height, float power)
    {
        Commands.Add($"attack:{targetObjectId}:{height}:{power}");
        CombatSnapshot = CombatSnapshot with
        {
            SelectedObjectId = targetObjectId,
            RequestInProgress = true,
            BuildInProgress = true,
            DesiredPower = power,
            PowerBarLevel = 0f,
        };
        return new(PluginCombatCommandStatus.Started);
    }
    public PluginCombatCommandResult ReleasePhysicalAttack()
    {
        Commands.Add("release");
        CombatSnapshot = CombatSnapshot with { BuildInProgress = false, ServerResponsePending = true };
        return new(PluginCombatCommandStatus.Released);
    }
    public PluginCombatCommandResult AbortPhysicalAttack()
    {
        Commands.Add("abort");
        CombatSnapshot = CombatSnapshot with
        {
            RequestInProgress = false, BuildInProgress = false, ServerResponsePending = false,
        };
        return new(PluginCombatCommandStatus.Stopped);
    }
    /// <summary>Simulates the server reporting the swing done.</summary>
    public void CompleteSwing() => CombatSnapshot = CombatSnapshot with
    {
        RequestInProgress = false,
        ServerResponsePending = false,
        CompletionRevision = CombatSnapshot.CompletionRevision + 1,
    };

    // ── loot ──────────────────────────────────────────────────────────────
    public bool LootBusy { get; set; }
    bool ILootAutomation.IsAvailable => IsAvailable;
    bool ILootAutomation.IsBusy => LootBusy;
    public uint CurrentContainerId { get; set; }
    public PluginInventoryCompletion LastInventoryCompletion { get; set; }
    public PluginAppraisalState Appraisal { get; set; }
    public List<PluginLootContainer> Corpses { get; } = [];
    public Dictionary<uint, List<PluginInventoryItem>> CorpseContents { get; } = [];
    public HashSet<uint> AppraisedObjects { get; } = [];
    public IReadOnlyList<PluginLootContainer> CaptureCorpses(float maximumDistance) =>
        Corpses.Where(c => c.Distance <= maximumDistance).ToArray();
    public IReadOnlyList<PluginInventoryItem> CaptureCurrentContents() =>
        CorpseContents.TryGetValue(CurrentContainerId, out List<PluginInventoryItem>? items)
            ? items.ToArray()
            : [];
    public PluginItemCommandResult Open(uint containerObjectId)
    {
        Commands.Add($"open:{containerObjectId}");
        return new(PluginItemCommandStatus.Started);
    }
    public PluginItemCommandResult Identify(uint objectId)
    {
        Commands.Add($"identify:{objectId}");
        return new(PluginItemCommandStatus.Started);
    }
    public PluginItemCommandResult Pickup(uint objectId, bool mainPack = false)
    {
        Commands.Add($"pickup:{objectId}");
        return new(PluginItemCommandStatus.Started);
    }
    /// <summary>Simulates the item arriving in the pack.</summary>
    public void CompletePickup(uint objectId)
    {
        foreach (List<PluginInventoryItem> items in CorpseContents.Values)
            items.RemoveAll(item => item.ObjectId == objectId);
        LastInventoryCompletion = new PluginInventoryCompletion(
            LastInventoryCompletion.Revision + 1, PluginInventoryCommandKind.Pickup, objectId, 0u);
    }

    // ── items ─────────────────────────────────────────────────────────────
    public List<PluginInventoryItem> OwnedItems { get; } = [];
    bool IItemAutomation.IsAvailable => IsAvailable;
    bool IItemAutomation.IsBusy => false;
    public IReadOnlyList<PluginInventoryItem> CaptureOwnedItems() => OwnedItems.ToArray();
    public PluginItemCommandResult Apply(uint objectId, uint targetObjectId)
    {
        Commands.Add($"apply:{objectId}@{targetObjectId}");
        return new(PluginItemCommandStatus.Started);
    }

    // ── world objects ─────────────────────────────────────────────────────
    bool IWorldObjectAutomation.IsAvailable => IsAvailable;
    public bool TryGet(uint objectId, out PluginWorldObject value)
    {
        if (AppraisedObjects.Contains(objectId))
        {
            value = new PluginWorldObject(objectId, 0u, "item", PluginObjectClass.Misc, 0u, 0u, 0u)
            {
                HasAppraisalData = true,
            };
            return true;
        }
        value = default;
        return false;
    }

    // ── navigation ────────────────────────────────────────────────────────
    public PluginNavigationPosition Position { get; set; } = new(0x00010100u, 0d, 0d, 0d, 0f, true);
    public bool NavigationAvailable { get; set; } = true;
    PluginNavigationSnapshot INavigationAutomation.Snapshot => new(
        NavigationAvailable, false, ObjectId, Position, IsMoving, false);
    public bool IsMoving { get; set; }
    public PluginMovementIntent? Intent { get; private set; }
    public float? FacedHeading { get; private set; }
    public bool TryGetObject(uint objectId, out PluginNavigationObject value)
    {
        value = default;
        return false;
    }
    public PluginNavigationCommandStatus SetMovementIntent(in PluginMovementIntent intent)
    {
        Commands.Add($"move:{Describe(intent)}");
        Intent = intent;
        IsMoving = true;
        return PluginNavigationCommandStatus.Accepted;
    }
    public PluginNavigationCommandStatus ClearMovementIntent()
    {
        Commands.Add("move:clear");
        Intent = null;
        IsMoving = false;
        return PluginNavigationCommandStatus.Accepted;
    }
    public PluginNavigationCommandStatus FaceHeading(float headingDegrees)
    {
        Commands.Add($"face:{headingDegrees:0}");
        FacedHeading = headingDegrees;
        Position = Position with { HeadingDegrees = headingDegrees };
        return PluginNavigationCommandStatus.Accepted;
    }

    private static string Describe(in PluginMovementIntent intent)
    {
        var parts = new List<string>();
        if (intent.Forward) parts.Add("forward");
        if (intent.Backward) parts.Add("back");
        if (intent.StrafeLeft) parts.Add("left");
        if (intent.StrafeRight) parts.Add("right");
        if (intent.Jump) parts.Add("jump");
        return string.Join("+", parts);
    }

    // ── surface ───────────────────────────────────────────────────────────
    public ICharacterInfo Character => this;
    public ISpellCatalog Spells => this;
    public IMagicCommands Magic => this;
    public IPluginChat Chat => this;
    public ICombatAutomation Combat => this;
    public ILootAutomation Loot => this;
    public INavigationAutomation Navigation => this;
    public IItemAutomation Items => this;
    public IWorldObjectAutomation Objects => this;
}

internal sealed class FakeLogger : IPluginLogger
{
    public List<string> Lines { get; } = [];
    public void Info(string message) => Lines.Add("info: " + message);
    public void Warn(string message) => Lines.Add("warn: " + message);
    public void Error(string message, Exception? exception = null) =>
        Lines.Add("error: " + message + (exception is null ? string.Empty : " " + exception.Message));
}

internal static class Spell
{
    public static PluginSpellInfo SelfBuff(uint id, string name, uint family, int tier) => new(
        id, name, family, tier, tier * 50, tier * 10, 1800f, 0u, string.Empty, true, true);

    public static PluginSpellInfo Attack(uint id, string name, int tier) => new(
        id, name, 900u + (uint)id, tier, tier * 50, tier * 10, 0f, 0u, string.Empty, false, false)
    {
        IsOffensive = true,
        TargetMask = 0x10u,
    };
}
