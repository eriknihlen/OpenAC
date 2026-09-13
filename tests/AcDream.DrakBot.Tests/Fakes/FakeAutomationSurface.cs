using AcDream.Plugin.Abstractions;

namespace AcDream.DrakBot.Tests.Fakes;

/// <summary>
/// A scriptable automation surface. Tests set state directly and read back
/// the commands the bot issued. Every command is recorded in <see cref="Commands"/>
/// in order, as "verb:detail".
/// </summary>
internal sealed class FakeAutomationSurface
    : IAutomationSurface, ICharacterInfo, ISpellCatalog, IMagicCommands,
      IPluginChat, ICombatAutomation, ILootAutomation, INavigationAutomation,
      IItemAutomation, IWorldObjectAutomation, IProjectileAutomation,
      IMovementProbeAutomation, IEnchantmentAutomation, IFellowshipAutomation, IEquipmentAutomation
{
    public List<string> Commands { get; } = [];

    // ── equipment ─────────────────────────────────────────────────────────
    public List<PluginEquipmentItem> Equipment { get; } = [];
    bool IEquipmentAutomation.IsAvailable => IsAvailable;
    bool IEquipmentAutomation.IsBusy => false;
    public IReadOnlyList<PluginEquipmentItem> CaptureOwnedEquipment() => Equipment.ToArray();
    /// <summary>Wields the item at once, unwielding anything in the same slot.</summary>
    public PluginEquipmentCommandResult Equip(uint objectId, uint requestedLocation = 0u)
    {
        Commands.Add($"equip:{objectId}");
        int index = Equipment.FindIndex(e => e.ObjectId == objectId);
        if (index < 0)
            return new(PluginEquipmentCommandStatus.InvalidItem);
        PluginEquipmentItem item = Equipment[index];
        for (int other = 0; other < Equipment.Count; other++)
        {
            if (Equipment[other].IsEquipped && Equipment[other].CombatUse == item.CombatUse)
                Equipment[other] = Equipment[other] with { EquippedLocation = 0u };
        }
        Equipment[index] = item with { EquippedLocation = item.ValidLocations };
        return new(PluginEquipmentCommandStatus.Started);
    }
    IEquipmentAutomation IAutomationSurface.Equipment => this;

    // ── fellowship ────────────────────────────────────────────────────────
    public List<PluginFellowMember> Fellows { get; } = [];
    public uint LeaderObjectId { get; set; }
    public bool IsInFellowship => Fellows.Count > 0;
    public int MemberCount => Fellows.Count;
    public IReadOnlyList<PluginFellowMember> CaptureMembers() => Fellows.ToArray();
    IFellowshipAutomation IAutomationSurface.Fellowship => this;
    public PluginFellowshipCommandResult Recruit(uint targetObjectId)
    {
        Commands.Add($"recruit:{targetObjectId}");
        return new(PluginFellowshipCommandStatus.Accepted);
    }
    public PluginFellowshipCommandResult SetOpen(bool isOpen)
    {
        Commands.Add($"fellowopen:{isOpen}");
        return new(PluginFellowshipCommandStatus.Accepted);
    }

    // ── enchantments the character landed on others ───────────────────────
    /// <summary>Tracked enchantments per target, as the client records the character's own casts.</summary>
    public List<PluginTrackedEnchantment> Landed { get; } = [];
    public IReadOnlyList<PluginTrackedEnchantment> Capture(uint targetObjectId) =>
        Landed.Where(e => e.TargetObjectId == targetObjectId).ToArray();
    public bool ReportCast(uint targetObjectId, uint spellId, double durationSeconds)
    {
        uint family = SelfBuffs.Concat(CombatSpells).Concat(AttackSpells)
            .Where(s => s.SpellId == spellId).Select(s => s.Family).FirstOrDefault(spellId);
        Landed.Add(new PluginTrackedEnchantment(targetObjectId, spellId, family, 1, false, durationSeconds));
        return true;
    }

    // ── availability / character ──────────────────────────────────────────
    public bool IsAvailable { get; set; } = true;
    public bool IsInWorld { get; set; } = true;
    public uint ObjectId { get; set; } = 0x50000001u;
    public string Name { get; set; } = "Tester";
    public int SummoningMastery { get; set; }
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
        foreach (PluginSkillInfo known in Skills)
        {
            if (known.SkillId == skillId)
            {
                skill = known;
                return true;
            }
        }
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
    /// <summary>Chat lines the game showed, served by <see cref="CaptureMessages"/> in sequence order.</summary>
    public List<PluginChatMessage> ChatMessages { get; } = [];
    public void Hear(string text, string sender = "") =>
        ChatMessages.Add(new PluginChatMessage((ulong)ChatMessages.Count + 1, 0u, 0, sender, text, string.Empty));
    public IReadOnlyList<PluginChatMessage> CaptureMessages(ulong afterSequence) =>
        ChatMessages.Where(message => message.Sequence > afterSequence).ToArray();
    public bool Submit(string text)
    {
        Commands.Add($"chat:{text}");
        return true;
    }

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
    /// <summary>Appraised properties per object, served by <see cref="TryCaptureProperties"/>.</summary>
    public Dictionary<uint, PluginItemProperties> Properties { get; } = [];
    public bool TryCaptureProperties(uint objectId, out PluginItemProperties properties) =>
        Properties.TryGetValue(objectId, out properties);
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
    public PluginItemUseCompletion LastItemUseCompletion { get; set; }
    PluginItemUseCompletion IItemAutomation.LastCompletion => LastItemUseCompletion;
    public IReadOnlyList<PluginInventoryItem> CaptureOwnedItems() => OwnedItems.ToArray();
    public PluginItemCommandResult Apply(uint objectId, uint targetObjectId)
    {
        Commands.Add($"apply:{objectId}@{targetObjectId}");
        return new(PluginItemCommandStatus.Started);
    }
    public PluginItemCommandResult MoveToContainer(uint objectId, uint containerObjectId, uint amount = 0u, int placement = 0)
    {
        Commands.Add($"move:{objectId}>{containerObjectId}");
        return new(PluginItemCommandStatus.Started);
    }
    public PluginItemCommandResult Merge(uint sourceObjectId, uint targetObjectId, uint amount = 0u)
    {
        Commands.Add($"merge:{sourceObjectId}>{targetObjectId}");
        return new(PluginItemCommandStatus.Started);
    }
    public PluginItemCommandResult Give(uint objectId, uint targetObjectId, uint amount = 0u)
    {
        Commands.Add($"give:{objectId}>{targetObjectId}");
        return new(PluginItemCommandStatus.Started);
    }
    public PluginItemCommandResult Drop(uint objectId, uint amount = 0u)
    {
        Commands.Add($"drop:{objectId}");
        return new(PluginItemCommandStatus.Started);
    }
    public PluginItemCommandResult Salvage(uint toolObjectId, IReadOnlyList<uint> itemObjectIds)
    {
        Commands.Add($"salvage:{toolObjectId}:{string.Join(',', itemObjectIds)}");
        return new(PluginItemCommandStatus.Started);
    }
    /// <summary>Only an owned item can be used this way, as in the client.</summary>
    PluginItemCommandResult IItemAutomation.Use(uint objectId)
    {
        if (!OwnedItems.Any(item => item.ObjectId == objectId))
            return new(PluginItemCommandStatus.InvalidItem);
        Commands.Add($"use:{objectId}");
        return new(PluginItemCommandStatus.Started);
    }

    // ── world objects ─────────────────────────────────────────────────────
    bool IWorldObjectAutomation.IsAvailable => IsAvailable;
    /// <summary>Landscape objects served by <see cref="CaptureObjects"/> (portals, NPCs, vendors).</summary>
    public List<PluginWorldObject> WorldObjects { get; } = [];
    public IReadOnlyList<PluginWorldObject> CaptureObjects() => WorldObjects.ToArray();
    public PluginItemCommandStatus NextUseStatus { get; set; } = PluginItemCommandStatus.Started;
    PluginItemCommandResult IWorldObjectAutomation.Use(uint objectId)
    {
        Commands.Add($"useobject:{objectId}");
        return new(NextUseStatus);
    }
    public bool TryGet(uint objectId, out PluginWorldObject value)
    {
        foreach (PluginWorldObject candidate in WorldObjects)
        {
            if (candidate.ObjectId == objectId)
            {
                value = candidate;
                return true;
            }
        }
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
    public bool IsPortalSpace { get; set; }
    PluginNavigationSnapshot INavigationAutomation.Snapshot => new(
        NavigationAvailable, IsPortalSpace, ObjectId, Position, IsMoving, false);
    public bool IsMoving { get; set; }
    public PluginMovementIntent? Intent { get; private set; }
    public float? FacedHeading { get; private set; }
    /// <summary>World positions served by <see cref="TryGetObject"/>.</summary>
    public Dictionary<uint, PluginNavigationPosition> ObjectPositions { get; } = [];
    /// <summary>Whole navigation objects (doors and the like) served by <see cref="TryGetObject"/> first.</summary>
    public Dictionary<uint, PluginNavigationObject> NavObjects { get; } = [];
    public bool TryGetObject(uint objectId, out PluginNavigationObject value)
    {
        if (NavObjects.TryGetValue(objectId, out value))
            return true;
        if (ObjectPositions.TryGetValue(objectId, out PluginNavigationPosition position))
        {
            value = new PluginNavigationObject(objectId, $"0x{objectId:X8}", position);
            return true;
        }
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
        if (intent.TurnLeft) parts.Add("turnleft");
        if (intent.TurnRight) parts.Add("turnright");
        if (intent.Jump) parts.Add("jump");
        return string.Join("+", parts);
    }

    // ── projectiles ───────────────────────────────────────────────────────
    public bool ProjectilesAvailable { get; set; } = true;
    bool IProjectileAutomation.IsAvailable => ProjectilesAvailable;
    /// <summary>Verdict per target and aim height; <see cref="DefaultPathStatus"/> otherwise.</summary>
    public Dictionary<(uint Target, PluginAttackHeight Height), PluginProjectilePathStatus> PathStatuses { get; } = [];
    public PluginProjectilePathStatus DefaultPathStatus { get; set; } = PluginProjectilePathStatus.Clear;
    public uint BlockingObjectId { get; set; } = 0x7000_0001u;
    /// <summary>Every sweep asked for, as "target:kind:height"; queries are not commands.</summary>
    public List<string> PathQueries { get; } = [];
    public int PathEvaluations => PathQueries.Count;
    public List<IReadOnlyList<PluginProjectileDebugSample>> ShownSamples { get; } = [];
    public PluginProjectilePathRequest? LastPathRequest { get; private set; }

    /// <summary>Marks a target blocked at every aim height.</summary>
    public void BlockPath(uint targetId)
    {
        foreach (PluginAttackHeight height in Enum.GetValues<PluginAttackHeight>())
            PathStatuses[(targetId, height)] = PluginProjectilePathStatus.Blocked;
    }

    public void ClearPath(uint targetId)
    {
        foreach (PluginAttackHeight height in Enum.GetValues<PluginAttackHeight>())
            PathStatuses.Remove((targetId, height));
    }

    public PluginProjectilePathResult EvaluatePath(
        uint targetObjectId, PluginProjectilePathKind kind, PluginAttackHeight targetHeight,
        float projectileRadius, float stepDistance, int maximumCollisionChecks) =>
        EvaluatePath(new PluginProjectilePathRequest(targetObjectId, kind, targetHeight));

    public PluginProjectilePathResult EvaluatePath(in PluginProjectilePathRequest request)
    {
        LastPathRequest = request;
        PathQueries.Add($"{request.TargetObjectId}:{request.Kind}:{request.TargetHeight}");
        PluginProjectilePathStatus status = PathStatuses.TryGetValue(
            (request.TargetObjectId, request.TargetHeight), out PluginProjectilePathStatus scripted)
            ? scripted
            : DefaultPathStatus;
        var result = new PluginProjectilePathResult(
            status,
            CollisionChecks: 4,
            BlockingObjectId: status == PluginProjectilePathStatus.Blocked ? BlockingObjectId : 0u);
        if (request.CaptureDiagnostics)
        {
            result = result with
            {
                DebugSamples = [new PluginProjectileDebugSample(default, status == PluginProjectilePathStatus.Clear, 0.25f)],
            };
        }
        return result;
    }

    public void ShowDebugSamples(IReadOnlyList<PluginProjectileDebugSample> samples) =>
        ShownSamples.Add(samples);

    // ── walking ───────────────────────────────────────────────────────────
    public bool MovementProbeAvailable { get; set; } = true;
    bool IMovementProbeAutomation.IsAvailable => MovementProbeAvailable;
    /// <summary>Compass headings (rounded to whole degrees) the body cannot walk along.</summary>
    public HashSet<int> BlockedWalkHeadings { get; } = [];
    public PluginWalkProbeStatus DefaultWalkStatus { get; set; } = PluginWalkProbeStatus.Clear;
    public uint WalkBlockingObjectId { get; set; } = 0x7000_0002u;
    /// <summary>Every walk asked for, as "heading:distance:target"; queries are not commands.</summary>
    public List<string> WalkQueries { get; } = [];

    public PluginWalkProbeResult ProbeWalk(in PluginWalkProbeRequest request)
    {
        int heading = (int)MathF.Round(request.HeadingDegrees) % 360;
        if (heading < 0)
            heading += 360;
        WalkQueries.Add($"{heading}:{request.DistanceMeters:0.#}:{request.TargetObjectId}");
        PluginWalkProbeStatus status = BlockedWalkHeadings.Contains(heading)
            ? PluginWalkProbeStatus.Blocked
            : DefaultWalkStatus;
        return new PluginWalkProbeResult(
            status,
            status == PluginWalkProbeStatus.Clear ? request.DistanceMeters : request.DistanceMeters * 0.25f,
            CollisionChecks: 3,
            BlockingObjectId: status == PluginWalkProbeStatus.Blocked ? WalkBlockingObjectId : 0u);
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
    IEnchantmentAutomation IAutomationSurface.Enchantments => this;
    public IProjectileAutomation Projectiles => this;
    public IMovementProbeAutomation MovementProbe => this;
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

    /// <summary>An attack spell in an explicit family, so tiers of one line share it.</summary>
    public static PluginSpellInfo AttackIn(uint id, string name, uint family, int tier) => new(
        id, name, family, tier, tier * 50, tier * 10, 0f, 0u, string.Empty, false, false)
    {
        IsOffensive = true,
        TargetMask = 0x10u,
    };

    public static PluginSpellInfo Debuff(uint id, string name, uint family, int tier) => new(
        id, name, family, tier, tier * 50, tier * 10, 300f, 0u, string.Empty, false, false)
    {
        IsDebuff = true,
        TargetMask = 0x10u,
    };
}
