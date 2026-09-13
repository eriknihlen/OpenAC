using System.Globalization;
using System.Text.RegularExpressions;
using AcDream.Plugin.Abstractions;

namespace AcDream.DrakBot.Meta;

/// <summary>Item property keys the expression engine reads by number (the client's PropertyInt ids).</summary>
public enum LongValueKey : uint
{
    EncumbranceVal = 5,
    ItemsCapacity = 6,
    ContainersCapacity = 7,
    CurrentWieldedLocation = 10,
    StackCount = 12,
}

/// <summary>A world object as the expression engine sees it: the fields VTank's <c>wobject*</c> functions read.</summary>
public sealed record MetaObject
{
    public int Id { get; init; }
    public string Name { get; init; } = string.Empty;
    public PluginObjectClass ObjectClass { get; init; }
    public uint WeenieClassId { get; init; }
    public int WieldedLocation { get; init; }
    public int StackCount { get; init; } = 1;
    public int ItemsCapacity { get; init; }
    public int ContainersCapacity { get; init; }
    public bool HasPosition { get; init; }
    public PluginNavigationPosition Position { get; init; }
    public bool IsLandscape { get; init; }
    public float HealthRatio { get; init; } = -1f;

    public int Values(LongValueKey key, int fallback) => key switch
    {
        LongValueKey.StackCount => StackCount,
        LongValueKey.CurrentWieldedLocation => WieldedLocation,
        LongValueKey.ItemsCapacity => ItemsCapacity,
        LongValueKey.ContainersCapacity => ContainersCapacity,
        _ => fallback,
    };
}

public sealed class QuestRecord
{
    public string Key = string.Empty;
    public int Solves;
    public int MaxSolves;
    public DateTime CompletedOn = DateTime.MinValue;
    public TimeSpan RepeatTime = TimeSpan.Zero;

    /// <summary>The quest can be done again: its timer ran out and it is not a once-only flag.</summary>
    public bool IsReady()
    {
        if (CompletedOn + RepeatTime > DateTime.UtcNow)
            return false;
        return !(MaxSolves == 1 && Solves <= 1);
    }
}

/// <summary>
/// The game as a meta sees it. The expression engine and the meta engine
/// were written against a host that reads the client's memory directly;
/// this is that host's surface, answered from <see cref="IAutomationSurface"/>
/// instead. Every <c>Has*</c> says whether the facility exists here at all,
/// so an expression that needs something the contract does not carry
/// answers the way it would on a host without it - "0" - rather than throw.
/// It also keeps the quest-flag cache fed by <c>/myquests</c> chat.
/// </summary>
public sealed class MetaWorld
{
    private static readonly Regex QuestLine = new(
        @"(?<key>\S+) \- (?<solves>\d+) solves \((?<completedOn>\d{0,11})\)""?((?<description>.*)"" (?<maxSolves>.*) (?<repeatTime>\d{0,11}))?.*$",
        RegexOptions.Compiled);

    private readonly IAutomationSurface _surface;
    private readonly IPluginStorage _storage;
    private readonly IPluginLogger _log;
    private readonly ISelectionService _selection;
    private readonly Func<double> _now;

    /// <summary>The bot clock, in seconds.</summary>
    public double Now => _now();
    private readonly Dictionary<string, QuestRecord> _quests = new(StringComparer.OrdinalIgnoreCase);
    private bool _questRefreshing;
    private bool _questGotFirst;
    private double _questLastLineAt;
    private double _questRefreshStartedAt;
    private PluginMovementIntent _motion;

    public MetaWorld(
        IAutomationSurface surface,
        IPluginStorage storage,
        IPluginLogger log,
        ISelectionService selection,
        Func<double> now)
    {
        _surface = surface ?? throw new ArgumentNullException(nameof(surface));
        _storage = storage ?? throw new ArgumentNullException(nameof(storage));
        _log = log ?? throw new ArgumentNullException(nameof(log));
        _selection = selection ?? throw new ArgumentNullException(nameof(selection));
        _now = now ?? throw new ArgumentNullException(nameof(now));
    }

    public IAutomationSurface Surface => _surface;

    public void Log(string message) => _log.Info(message);

    // ── Storage ──────────────────────────────────────────────────────────

    public string? ReadStorage(string key) => _storage.IsAvailable ? _storage.ReadText(key) : null;

    public void WriteStorage(string key, string content)
    {
        if (_storage.IsAvailable)
            _storage.WriteText(key, content);
    }

    // ── Player ───────────────────────────────────────────────────────────

    public bool HasGetPlayerId => true;
    public uint GetPlayerId() => _surface.Character.ObjectId;

    public bool HasGetWorldName => true;
    public bool TryGetWorldName(out string name)
    {
        name = _surface.Character.WorldName;
        return name.Length > 0;
    }

    public bool HasGetAccountName => true;
    public bool TryGetAccountName(out string name)
    {
        name = _surface.Character.AccountName;
        return name.Length > 0;
    }

    public bool HasGetPlayerVitals => true;
    public bool TryGetPlayerVitals(out uint health, out uint maxHealth, out uint stamina, out uint maxStamina, out uint mana, out uint maxMana)
    {
        ICharacterInfo character = _surface.Character;
        health = character.CurrentHealth;
        maxHealth = character.MaxHealth;
        stamina = character.CurrentStamina;
        maxStamina = character.MaxStamina;
        mana = character.CurrentMana;
        maxMana = character.MaxMana;
        return character.IsInWorld;
    }

    /// <summary>Unbuffed vital maxima are not carried by the contract; the buffed ones stand in.</summary>
    public bool HasGetPlayerBaseVitals => true;
    public bool TryGetPlayerBaseVitals(out uint baseHealth, out uint baseStamina, out uint baseMana)
    {
        ICharacterInfo character = _surface.Character;
        baseHealth = character.MaxHealth;
        baseStamina = character.MaxStamina;
        baseMana = character.MaxMana;
        return character.IsInWorld;
    }

    public bool HasGetObjectAttribute2ndBaseLevel => true;
    public bool TryGetObjectAttribute2ndBaseLevel(uint objectId, uint vital, out uint value)
    {
        ICharacterInfo character = _surface.Character;
        value = vital switch { 1u => character.MaxHealth, 3u => character.MaxStamina, 5u => character.MaxMana, _ => 0u };
        return objectId == character.ObjectId && value != 0u;
    }

    public bool HasGetVitae => false;
    public float GetVitae(uint objectId) => 1f;

    public bool HasGetPlayerPose => true;

    /// <summary>
    /// The pose as map coordinates: x is east-west, y is north-south (the
    /// units <c>/loc</c> prints), z is elevation in physics units. Paired
    /// with <see cref="TryConvertPoseToCoords"/>, which just hands them back.
    /// </summary>
    public bool TryGetPlayerPose(out uint cellId, out float x, out float y, out float z, out float qw, out float qx, out float qy, out float qz)
    {
        PluginNavigationSnapshot navigation = _surface.Navigation.Snapshot;
        PluginNavigationPosition position = navigation.Position;
        cellId = position.CellId;
        x = (float)position.EastWest;
        y = (float)position.NorthSouth;
        z = (float)(position.Elevation * 240d);
        qw = 1f;
        qx = qy = qz = 0f;
        return navigation.IsAvailable;
    }

    public static bool TryConvertPoseToCoords(uint cellId, float x, float y, out double northSouth, out double eastWest)
    {
        northSouth = y;
        eastWest = x;
        return cellId != 0u;
    }

    public bool HasIsPortaling => true;
    public bool IsPortaling() => _surface.Navigation.Snapshot.IsPortalSpace;

    public bool HasGetObjectHeading => true;
    public bool TryGetObjectHeading(uint objectId, out float heading)
    {
        if (objectId == GetPlayerId())
        {
            heading = _surface.Navigation.Snapshot.Position.HeadingDegrees;
            return true;
        }
        heading = 0f;
        return false;
    }

    public bool HasGetObjectPosition => true;
    public bool TryGetObjectPosition(uint objectId, out uint cellId, out float x, out float y, out float z)
    {
        if (objectId == GetPlayerId())
            return TryGetPlayerPose(out cellId, out x, out y, out z, out _, out _, out _, out _);
        if (_surface.Navigation.TryGetObject(objectId, out PluginNavigationObject value))
        {
            cellId = value.Position.CellId;
            x = (float)value.Position.EastWest;
            y = (float)value.Position.NorthSouth;
            z = (float)(value.Position.Elevation * 240d);
            return true;
        }
        cellId = 0u;
        x = y = z = 0f;
        return false;
    }

    // ── Skills, attributes, spells ───────────────────────────────────────

    public bool HasGetObjectSkill => true;
    public bool TryGetObjectSkill(uint objectId, uint skillId, out int level, out int training)
    {
        if (objectId == GetPlayerId() && _surface.Character.TryGetSkill(skillId, out PluginSkillInfo skill))
        {
            level = (int)skill.Current;
            training = (int)skill.Training;
            return true;
        }
        level = training = 0;
        return false;
    }

    public bool HasGetObjectSkillBuffed => true;
    public bool TryGetObjectSkillLevel(uint objectId, uint skillId, int baseOnly, out int level)
    {
        if (objectId == GetPlayerId() && _surface.Character.TryGetSkill(skillId, out PluginSkillInfo skill))
        {
            level = (int)(baseOnly != 0 ? skill.Base : skill.Current);
            return true;
        }
        level = 0;
        return false;
    }

    public bool HasGetObjectAttribute => true;
    public bool TryGetObjectAttribute(uint objectId, uint attributeId, int baseOnly, out uint value)
    {
        if (objectId == GetPlayerId())
        {
            foreach (PluginAttributeInfo attribute in _surface.Character.Attributes)
            {
                if ((uint)attribute.Kind != attributeId)
                    continue;
                value = baseOnly != 0 ? attribute.Base : attribute.Current;
                return true;
            }
        }
        value = 0u;
        return false;
    }

    public bool HasIsSpellKnown => true;
    public bool IsSpellKnown(uint objectId, uint spellId, out bool known)
    {
        known = _surface.Spells.IsKnown(spellId);
        return true;
    }

    public bool HasCastSpell => true;
    public void CastSpell(uint targetObjectId, int spellId)
    {
        if (targetObjectId == 0u || targetObjectId == GetPlayerId())
            _surface.Magic.RequestCast((uint)spellId);
        else
            _surface.Magic.RequestCast((uint)spellId, targetObjectId);
    }

    public bool HasGetServerTime => true;
    /// <summary>Enchantments below carry seconds remaining rather than expiry stamps, so "now" is zero.</summary>
    public double GetServerTime() => 1d;

    public bool HasReadPlayerEnchantments => true;
    public int ReadPlayerEnchantments(uint[] spellIds, double[] expiry, int max)
    {
        int count = 0;
        foreach (PluginActiveEnchantment enchantment in _surface.Character.ActiveEnchantments)
        {
            if (count >= max)
                break;
            spellIds[count] = enchantment.SpellId;
            expiry[count] = 1d + enchantment.SecondsRemaining;
            count++;
        }
        return count;
    }

    public bool HasReadObjectEnchantments => true;
    public int ReadObjectEnchantments(uint objectId, uint[] spellIds, double[] expiry, int max)
    {
        if (objectId == GetPlayerId())
            return ReadPlayerEnchantments(spellIds, expiry, max);
        int count = 0;
        foreach (PluginTrackedEnchantment enchantment in _surface.Enchantments.Capture(objectId))
        {
            if (count >= max)
                break;
            spellIds[count] = enchantment.SpellId;
            expiry[count] = 1d + enchantment.SecondsRemaining;
            count++;
        }
        return count;
    }

    public bool HasGetObjectSpellIds => true;
    public int GetObjectSpellIds(uint objectId, uint[] buffer, int max)
    {
        IReadOnlyList<uint> ids = Array.Empty<uint>();
        if (_surface.Objects.TryGet(objectId, out PluginWorldObject value))
            ids = value.SpellIds;
        int count = Math.Min(ids.Count, max);
        for (int index = 0; index < count; index++)
            buffer[index] = ids[index];
        return count;
    }

    public string SpellName(uint spellId) =>
        _surface.Spells.TryGet(spellId, out PluginSpellInfo info) ? info.Name : spellId.ToString(CultureInfo.InvariantCulture);

    public string ComponentName(uint componentId) =>
        _surface.Spells.TryGetComponent(componentId, out PluginSpellComponentInfo info) ? info.Name : componentId.ToString(CultureInfo.InvariantCulture);

    // ── Objects ──────────────────────────────────────────────────────────

    public bool HasGetObjectName => true;
    public bool TryGetObjectName(uint objectId, out string name)
    {
        if (objectId == GetPlayerId())
        {
            name = _surface.Character.Name;
            return name.Length > 0;
        }
        if (_surface.Objects.TryGet(objectId, out PluginWorldObject value))
        {
            name = value.Name;
            return true;
        }
        name = string.Empty;
        return false;
    }

    public bool HasGetObjectWcid => true;
    public bool TryGetObjectWcid(uint objectId, out uint weenieClassId)
    {
        if (_surface.Objects.TryGet(objectId, out PluginWorldObject value))
        {
            weenieClassId = value.WeenieClassId;
            return true;
        }
        weenieClassId = 0u;
        return false;
    }

    public bool HasGetObjectBitfield => false;
    public bool TryGetObjectBitfield(uint objectId, out uint bitfield)
    {
        bitfield = 0u;
        return false;
    }

    public bool HasGetObjectState => true;
    /// <summary>Only the open-door bit (0x4, ethereal) is modelled.</summary>
    public bool TryGetObjectState(uint objectId, out uint state)
    {
        if (_surface.Objects.TryGet(objectId, out PluginWorldObject value))
        {
            state = value.IsDoorOpen ? 0x4u : 0u;
            return true;
        }
        state = 0u;
        return false;
    }

    public bool HasObjectIsAttackable => true;
    public bool ObjectIsAttackable(uint objectId)
    {
        foreach (PluginCombatTarget target in _surface.Combat.CaptureHostileTargets(float.MaxValue))
        {
            if (target.ObjectId == objectId)
                return true;
        }
        return false;
    }

    public bool HasHasAppraisalData => true;
    public bool HasAppraisalData(uint objectId) =>
        _surface.Objects.TryGet(objectId, out PluginWorldObject value) && value.HasAppraisalData;

    public bool HasRequestId => true;
    public void RequestId(uint objectId) => _surface.Objects.Identify(objectId);

    public bool HasGetLastIdTime => true;
    public int GetLastIdTime(uint objectId) =>
        _surface.Objects.TryGet(objectId, out PluginWorldObject value) ? value.LastIdTime : 0;

    public bool HasGetObjectIntProperty => true;
    public bool TryGetObjectIntProperty(uint objectId, uint key, out int value)
    {
        if (objectId == GetPlayerId())
        {
            ICharacterInfo character = _surface.Character;
            switch (key)
            {
                case 25u: value = character.Level; return true;                     // Level
                case 5u: value = Burden(); return true;                             // EncumbranceVal
                case 96u: value = BurdenCapacity(); return true;                    // EncumbranceCapacity
            }
        }
        if (_surface.Objects.TryCaptureProperties(objectId, out PluginItemProperties properties)
            && properties.Ints.TryGetValue(key, out value))
        {
            return true;
        }
        if (_surface.Objects.TryGet(objectId, out PluginWorldObject item))
        {
            switch (key)
            {
                case 12u: value = item.StackSize; return true;                      // StackSize
                case 6u: value = item.ItemsCapacity; return true;                  // ItemsCapacity
                case 7u: value = item.ContainersCapacity; return true;             // ContainersCapacity
                case 1u: value = (int)item.ItemType; return true;                  // ItemType
            }
        }
        value = 0;
        return false;
    }

    public bool HasGetObjectDoubleProperty => true;
    public bool TryGetObjectDoubleProperty(uint objectId, uint key, out double value)
    {
        if (_surface.Objects.TryCaptureProperties(objectId, out PluginItemProperties properties)
            && properties.Floats.TryGetValue(key, out value))
        {
            return true;
        }
        value = 0d;
        return false;
    }

    public bool HasGetObjectQuadProperty => true;
    public bool TryGetObjectQuadProperty(uint objectId, uint key, out long value)
    {
        if (_surface.Objects.TryCaptureProperties(objectId, out PluginItemProperties properties)
            && properties.Int64s.TryGetValue(key, out value))
        {
            return true;
        }
        value = 0L;
        return false;
    }

    public bool HasGetObjectBoolProperty => true;
    public bool TryGetObjectBoolProperty(uint objectId, uint key, out bool value)
    {
        if (_surface.Objects.TryCaptureProperties(objectId, out PluginItemProperties properties)
            && properties.Bools.TryGetValue(key, out value))
        {
            return true;
        }
        value = false;
        return false;
    }

    public bool HasGetObjectStringProperty => true;
    public bool TryGetObjectStringProperty(uint objectId, uint key, out string? value)
    {
        if (key == 1u && TryGetObjectName(objectId, out string name))
        {
            value = name;
            return true;
        }
        if (_surface.Objects.TryCaptureProperties(objectId, out PluginItemProperties properties)
            && properties.Strings.TryGetValue(key, out string? text))
        {
            value = text;
            return true;
        }
        value = null;
        return false;
    }

    public bool HasGetObjectWielderInfo => true;
    public bool TryGetObjectWielderInfo(uint objectId, out uint wielder, out uint location)
    {
        if (_surface.Objects.TryGet(objectId, out PluginWorldObject value))
        {
            wielder = value.WielderObjectId;
            location = 0u;
            return true;
        }
        wielder = location = 0u;
        return false;
    }

    public bool HasGetContainerContents => true;
    public int GetContainerContents(uint containerId, uint[] buffer)
    {
        int count = 0;
        foreach (PluginInventoryItem item in _surface.Items.CaptureOwnedItems())
        {
            if (item.ContainerObjectId != containerId || count >= buffer.Length)
                continue;
            buffer[count++] = item.ObjectId;
        }
        return count;
    }

    public bool HasGetNumContainedItems => true;
    public int GetNumContainedItems(uint containerId)
    {
        int count = 0;
        foreach (PluginInventoryItem item in _surface.Items.CaptureOwnedItems())
        {
            if (item.ContainerObjectId == containerId && item.ObjectClass != PluginObjectClass.Container)
                count++;
        }
        return count;
    }

    public bool HasGetNumContainedContainers => true;
    public int GetNumContainedContainers(uint containerId)
    {
        int count = 0;
        foreach (PluginInventoryItem item in _surface.Items.CaptureOwnedItems())
        {
            if (item.ContainerObjectId == containerId && item.ObjectClass == PluginObjectClass.Container)
                count++;
        }
        return count;
    }

    public bool HasGetGroundContainerId => true;
    public uint GetGroundContainerId() => _surface.Objects.OpenContainerObjectId;

    public bool HasGetSelectedItemId => true;
    public uint GetSelectedItemId() => _selection.SelectedObjectId ?? _surface.Combat.Snapshot.SelectedObjectId;

    public bool HasSelectItem => true;
    public void SelectItem(uint objectId) => _selection.Select(objectId);

    public bool HasUseObject => true;
    public void UseObject(uint objectId)
    {
        PluginItemCommandResult result = _surface.Items.Use(objectId);
        if (result.Status is PluginItemCommandStatus.InvalidItem or PluginItemCommandStatus.Unavailable)
            _surface.Objects.Use(objectId);
    }

    public bool HasUseObjectOn => true;
    public bool UseObjectOn(uint objectId, uint targetObjectId) =>
        _surface.Items.Apply(objectId, targetObjectId).Status == PluginItemCommandStatus.Started;

    public bool HasMoveItemExternal => true;
    public bool MoveItemExternal(uint objectId, uint targetObjectId, int amount) =>
        _surface.Items.Give(objectId, targetObjectId, (uint)Math.Max(0, amount)).Status == PluginItemCommandStatus.Started;

    public bool HasSalvagePanel => false;
    public void SalvagePanelAddItem(uint objectId) { }
    public void SalvagePanelExecute() { }

    // ── Object cache views ───────────────────────────────────────────────

    public MetaObject? this[int objectId]
    {
        get
        {
            uint id = unchecked((uint)objectId);
            if (id == GetPlayerId())
            {
                return new MetaObject
                {
                    Id = objectId,
                    Name = _surface.Character.Name,
                    ObjectClass = PluginObjectClass.Player,
                    HasPosition = _surface.Navigation.Snapshot.IsAvailable,
                    Position = _surface.Navigation.Snapshot.Position,
                };
            }
            foreach (PluginInventoryItem item in _surface.Items.CaptureOwnedItems())
            {
                if (item.ObjectId == id)
                    return FromItem(item);
            }
            return _surface.Objects.TryGet(id, out PluginWorldObject value) ? FromWorld(value) : null;
        }
    }

    /// <summary>Everything the character carries, wielded or packed, at any depth.</summary>
    public IEnumerable<MetaObject> GetInventory()
    {
        foreach (PluginInventoryItem item in _surface.Items.CaptureOwnedItems())
            yield return FromItem(item);
    }

    /// <summary>The main pack's own contents plus wielded items: what fills the pack slots.</summary>
    public IEnumerable<MetaObject> GetDirectInventory()
    {
        uint player = GetPlayerId();
        foreach (PluginInventoryItem item in _surface.Items.CaptureOwnedItems())
        {
            if (item.ContainerObjectId == player || item.WielderObjectId == player)
                yield return FromItem(item);
        }
    }

    public IEnumerable<MetaObject> GetLandscape() => GetLandscapeObjects();

    public IEnumerable<MetaObject> GetLandscapeObjects()
    {
        Dictionary<uint, float>? health = null;
        foreach (PluginWorldObject value in _surface.Objects.CaptureObjects())
        {
            if (value.IsOwned)
                continue;
            MetaObject entry = FromWorld(value);
            if (value.ObjectClass == PluginObjectClass.Monster)
            {
                health ??= HostileHealth();
                if (health.TryGetValue(value.ObjectId, out float ratio))
                    entry = entry with { HealthRatio = ratio };
            }
            yield return entry;
        }
    }

    public double Distance(int fromId, int toId)
    {
        if (!TryPosition(unchecked((uint)fromId), out PluginNavigationPosition from)
            || !TryPosition(unchecked((uint)toId), out PluginNavigationPosition to))
        {
            return double.MaxValue;
        }
        return from.HorizontalDistanceMeters(to);
    }

    public float GetHealthRatio(int objectId)
    {
        uint id = unchecked((uint)objectId);
        if (id == GetPlayerId())
        {
            ICharacterInfo character = _surface.Character;
            return character.MaxHealth == 0u ? -1f : (float)character.CurrentHealth / character.MaxHealth;
        }
        return HostileHealth().TryGetValue(id, out float ratio) ? ratio : -1f;
    }

    private Dictionary<uint, float> HostileHealth()
    {
        var health = new Dictionary<uint, float>();
        foreach (PluginCombatTarget target in _surface.Combat.CaptureHostileTargets(float.MaxValue))
            health[target.ObjectId] = target.IsHealthKnown ? target.HealthFraction : -1f;
        return health;
    }

    private bool TryPosition(uint objectId, out PluginNavigationPosition position)
    {
        if (objectId == GetPlayerId())
        {
            PluginNavigationSnapshot navigation = _surface.Navigation.Snapshot;
            position = navigation.Position;
            return navigation.IsAvailable;
        }
        if (_surface.Navigation.TryGetObject(objectId, out PluginNavigationObject value))
        {
            position = value.Position;
            return true;
        }
        position = default;
        return false;
    }

    private static MetaObject FromItem(in PluginInventoryItem item) => new()
    {
        Id = unchecked((int)item.ObjectId),
        Name = item.Name,
        ObjectClass = item.ObjectClass,
        WeenieClassId = item.WeenieClassId,
        WieldedLocation = (int)item.EquippedLocation,
        StackCount = item.StackSize,
        ItemsCapacity = item.ItemsCapacity,
        ContainersCapacity = item.ContainersCapacity,
    };

    private static MetaObject FromWorld(in PluginWorldObject value) => new()
    {
        Id = unchecked((int)value.ObjectId),
        Name = value.Name,
        ObjectClass = value.ObjectClass,
        WeenieClassId = value.WeenieClassId,
        StackCount = value.StackSize,
        ItemsCapacity = value.ItemsCapacity,
        ContainersCapacity = value.ContainersCapacity,
        HasPosition = value.HasPosition,
        Position = value.Position,
        IsLandscape = value.IsLandscape,
    };

    private int Burden()
    {
        int burden = 0;
        foreach (PluginInventoryItem item in _surface.Items.CaptureOwnedItems())
            burden += item.Burden;
        return burden;
    }

    private int BurdenCapacity()
    {
        // The retail rule: capacity is 150 per point of buffed Strength.
        return TryGetObjectAttribute(GetPlayerId(), 1u, 0, out uint strength) ? (int)strength * 150 : 0;
    }

    // ── Combat and motion ────────────────────────────────────────────────

    public bool HasGetCurrentCombatMode => true;
    /// <summary>VTank's numbering: 1 peace, 2 melee, 4 missile, 8 magic.</summary>
    public int GetCurrentCombatMode() => _surface.Combat.Snapshot.Mode switch
    {
        PluginCombatMode.Melee => 2,
        PluginCombatMode.Missile => 4,
        PluginCombatMode.Magic => 8,
        _ => 1,
    };

    public bool HasChangeCombatMode => true;
    public bool ChangeCombatMode(int mode)
    {
        PluginCombatMode wanted = mode switch
        {
            2 => PluginCombatMode.Melee,
            4 => PluginCombatMode.Missile,
            8 => PluginCombatMode.Magic,
            _ => PluginCombatMode.Peace,
        };
        return _surface.Combat.EnterMode(wanted).Accepted;
    }

    public bool HasGetBusyState => true;
    public int GetBusyState()
    {
        PluginCombatSnapshot combat = _surface.Combat.Snapshot;
        return combat.BuildInProgress || combat.RequestInProgress || combat.ServerResponsePending || _surface.Magic.IsCasting ? 1 : 0;
    }

    public bool HasCancelAttack => true;
    public void CancelAttack() => _surface.Combat.AbortPhysicalAttack();

    public bool HasStopCompletely => true;
    public void StopCompletely()
    {
        _motion = default;
        _surface.Navigation.ClearMovementIntent();
    }

    public bool HasSetMotion => true;
    /// <summary>The UtilityBelt motion ids; the held keys are re-sent as one intent.</summary>
    public bool SetMotion(uint motion, bool on)
    {
        _motion = motion switch
        {
            0x45000005u => _motion with { Forward = on },
            0x45000006u => _motion with { Backward = on },
            0x6500000Du => _motion with { TurnRight = on },
            0x6500000Eu => _motion with { TurnLeft = on },
            0x6500000Fu => _motion with { StrafeRight = on },
            0x65000010u => _motion with { StrafeLeft = on },
            0x11112222u => _motion with { Run = !on },
            _ => _motion,
        };
        bool anyKey = _motion.Forward || _motion.Backward || _motion.TurnLeft || _motion.TurnRight
            || _motion.StrafeLeft || _motion.StrafeRight;
        PluginNavigationCommandStatus status = anyKey
            ? _surface.Navigation.SetMovementIntent(_motion)
            : _surface.Navigation.ClearMovementIntent();
        return status == PluginNavigationCommandStatus.Accepted;
    }

    // ── Chat ─────────────────────────────────────────────────────────────

    public bool HasInvokeChatParser => true;
    public void InvokeChatParser(string text)
    {
        if (!_surface.Chat.Submit(text))
            _log.Warn($"meta: chat line was not accepted: {text}");
    }

    public bool HasWriteToChat => true;
    public bool WriteToChat(string text, int color)
    {
        _surface.Chat.PostSystemMessage(text);
        return true;
    }

    // ── Fellowship ───────────────────────────────────────────────────────

    public bool IsInFellowship => _surface.Fellowship.IsInFellowship;
    public string FellowshipName => _surface.Fellowship.Name;
    public int MemberCount => _surface.Fellowship.MemberCount;
    public uint LeaderId => _surface.Fellowship.LeaderObjectId;
    public bool IsLeader => IsInFellowship && LeaderId == GetPlayerId();
    public bool IsOpen => _surface.Fellowship.IsOpen;
    public bool IsLocked => _surface.Fellowship.IsLocked;

    public int GetMemberId(int index)
    {
        IReadOnlyList<PluginFellowMember> members = _surface.Fellowship.CaptureRoster();
        return index >= 0 && index < members.Count ? unchecked((int)members[index].ObjectId) : 0;
    }

    public string GetMemberName(int index)
    {
        IReadOnlyList<PluginFellowMember> members = _surface.Fellowship.CaptureRoster();
        return index >= 0 && index < members.Count ? members[index].Name : string.Empty;
    }

    public IEnumerable<string> GetMemberNames()
    {
        foreach (PluginFellowMember member in _surface.Fellowship.CaptureRoster())
            yield return member.Name;
    }

    // ── Quests ───────────────────────────────────────────────────────────

    public bool IsRefreshing => _questRefreshing;

    /// <summary>Asks the server for the quest list; the reply lines feed <see cref="OnChatLine"/>.</summary>
    public void Refresh()
    {
        if (_questRefreshing)
            return;
        _questRefreshing = true;
        _questGotFirst = false;
        _questLastLineAt = _now();
        _questRefreshStartedAt = _now();
        InvokeChatParser("/myquests");
    }

    public void OnChatLine(string text)
    {
        if (!_questRefreshing)
            return;
        if (text.Contains("Quest list is empty", StringComparison.Ordinal)
            || text.Contains("The command \"myquests\" is not currently enabled", StringComparison.Ordinal)
            || text.Contains("This command may only be run once every", StringComparison.Ordinal))
        {
            _questRefreshing = false;
            return;
        }
        Match match = QuestLine.Match(text);
        if (!match.Success)
            return;
        _questGotFirst = true;
        _questLastLineAt = _now();
        var record = new QuestRecord { Key = match.Groups["key"].Value.ToLowerInvariant() };
        int.TryParse(match.Groups["solves"].Value, out record.Solves);
        int.TryParse(match.Groups["maxSolves"].Value, out record.MaxSolves);
        if (double.TryParse(match.Groups["completedOn"].Value, NumberStyles.Any, CultureInfo.InvariantCulture, out double stamp) && stamp > 0)
            record.CompletedOn = DateTimeOffset.FromUnixTimeSeconds((long)stamp).UtcDateTime;
        if (double.TryParse(match.Groups["repeatTime"].Value, NumberStyles.Any, CultureInfo.InvariantCulture, out double repeat) && repeat > 0)
            record.RepeatTime = TimeSpan.FromSeconds(repeat);
        _quests[record.Key] = record;
    }

    /// <summary>Ends a refresh by silence: a second after the last quest line, or 15 s with none.</summary>
    public void Tick()
    {
        if (!_questRefreshing)
            return;
        double now = _now();
        if (_questGotFirst ? now - _questLastLineAt >= 1d : now - _questRefreshStartedAt >= 15d)
            _questRefreshing = false;
    }

    public bool HasFlag(string key) => _quests.ContainsKey(key);

    public bool TryGetFlag(string key, out QuestRecord record) => _quests.TryGetValue(key, out record!);
}
