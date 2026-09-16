using System.Buffers;
using System.Globalization;
using System.Text.Json;
using AcDream.DrakBot.Behaviors;
using AcDream.DrakBot.Meta;
using AcDream.DrakBot.Navigation;
using AcDream.DrakBot.Profiles;
using AcDream.Plugin.Abstractions;

namespace AcDream.DrakBot.Remote;

/// <summary>
/// What the phone sees of this client: one status document, the same
/// shape for every client so a phone can line a multi-box up side by side.
/// The cheap fields (vitals, the bot's state, the target, the position, new
/// chat) are read every build; the dear ones (the pack, gear appraisals,
/// the profile folders) are refreshed every few seconds and served from a
/// cache in between. Built on the plugin tick only.
/// </summary>
internal sealed class RemoteStatusBuilder
{
    public const string Schema = "acdream.drakbot.remote/1";

    /// <summary>How often the pack and the profile folders are looked at.</summary>
    public const double SlowRefreshSeconds = 3d;

    public const int ChatLines = 60;

    // The appraisal properties the phone's gear view shows.
    private const uint IntArmorLevel = 28u;
    private const uint IntValue = 19u;
    private const uint IntEncumbrance = 5u;
    private const uint IntWorkmanship = 105u;
    private const uint IntMaterialType = 131u;
    private const uint IntItemMaxMana = 108u;
    private const uint IntItemCurMana = 107u;
    private const uint IntDamage = 44u;
    private const uint IntDamageType = 45u;
    private const uint FloatArmorModVsSlash = 13u; // ..19: pierce, bludgeon, cold, fire, acid, electric
    private const uint FloatDamageVariance = 22u;
    private const uint FloatWeaponDefense = 29u;
    private const uint FloatWeaponMagicDefense = 150u;
    private const uint FloatElementalDamageMod = 152u;
    private const uint FloatWeaponMissileDefense = 157u;
    private const uint StringLongDesc = 16u;

    private readonly BotController _controller;
    private readonly IAutomationSurface _surface;
    private readonly RemoteTelemetry _telemetry;
    private readonly RemoteHostServices _services;
    private readonly RemoteCapabilities _capabilities;
    private readonly ArrayBufferWriter<byte> _buffer = new(16 * 1024);
    private readonly Queue<PluginChatMessage> _chat = new(ChatLines + 1);
    private ulong _chatSequence;
    private double _slowRefreshedAt = double.NegativeInfinity;
    private IReadOnlyList<GearItem> _equipment = [];
    private IReadOnlyList<(string Name, int Count)> _scarabs = [];
    private int _scarabTotal = -1;
    private int _tapers = -1;
    private double _burdenPercent;
    private IReadOnlyList<string> _profiles = [];
    private IReadOnlyList<string> _navProfiles = [];
    private IReadOnlyList<string> _lootProfiles = [];
    private IReadOnlyList<string> _metaProfiles = [];
    private IReadOnlyList<BuffStatus> _buffPlan = [];
    private ulong _lastContentHash;

    public RemoteStatusBuilder(
        BotController controller,
        IAutomationSurface surface,
        RemoteTelemetry telemetry,
        RemoteHostServices services,
        RemoteCapabilities capabilities)
    {
        _controller = controller ?? throw new ArgumentNullException(nameof(controller));
        _surface = surface ?? throw new ArgumentNullException(nameof(surface));
        _telemetry = telemetry ?? throw new ArgumentNullException(nameof(telemetry));
        _services = services ?? throw new ArgumentNullException(nameof(services));
        _capabilities = capabilities ?? throw new ArgumentNullException(nameof(capabilities));
    }

    public bool UiHidden { get; set; }

    /// <summary>
    /// Whether the last <see cref="Build"/> differed from the one before
    /// it in anything but its timestamp, so a publisher can spare the
    /// phone a push that says nothing new.
    /// </summary>
    public bool Changed { get; private set; } = true;

    /// <summary>The profile lists as the phone indexes them, for the picker commands.</summary>
    public IReadOnlyList<string> Profiles => _profiles;
    public IReadOnlyList<string> NavProfiles => _navProfiles;
    public IReadOnlyList<string> LootProfiles => _lootProfiles;
    public IReadOnlyList<string> MetaProfiles => _metaProfiles;

    /// <summary>Builds the status document at <paramref name="now"/> (seconds on the bot's clock).</summary>
    public byte[] Build(double now)
    {
        ICharacterInfo character = _surface.Character;
        bool inWorld = _surface.IsAvailable && character.IsInWorld;
        if (now - _slowRefreshedAt >= SlowRefreshSeconds)
        {
            _slowRefreshedAt = now;
            RefreshSlow(inWorld);
        }
        _telemetry.Update(_controller, _surface, inWorld);
        _telemetry.UpdateLastIssue(_controller.Log);
        CaptureChat();

        BotEngine engine = _controller.Engine;
        BotProfile profile = _controller.Profile;
        MetaEngine? meta = _controller.Meta;
        Route? route = _controller.Navigation.Route;
        PluginNavigationSnapshot navigation = _surface.Navigation.Snapshot;
        double fps = _services.FramesPerSecond?.Invoke() ?? _telemetry.TicksPerSecond;
        int ticks = _telemetry.TicksPerSecond;
        bool running = engine.IsRunning;
        (string state, bool healthy) = Classify(inWorld, fps, running, ticks);

        _buffer.Clear();
        using var json = new Utf8JsonWriter(_buffer);
        json.WriteStartObject();
        json.WriteString("schema", Schema);
        json.WriteString("host", Environment.MachineName);
        json.WriteString("agentVersion", DrakBotRemotePlugin.Version);
        json.WritePropertyName("generatedAtUtc");
        json.Flush();
        int stampStart = _buffer.WrittenCount;
        json.WriteStringValue(DateTimeOffset.UtcNow);
        json.Flush();
        int stampEnd = _buffer.WrittenCount;
        json.WriteNumber("clientCount", 1);
        json.WritePropertyName("capabilities");
        // The renderer binds after the plugins start, so video is answered live rather than at start-up.
        bool video = _services.Frames?.IsAvailable == true;
        (_capabilities with { Video = video, VideoMinimized = video }).Write(json);
        json.WritePropertyName("clients");
        json.WriteStartArray();
        json.WriteStartObject();

        json.WriteNumber("pid", Environment.ProcessId);
        json.WriteString("host", Environment.MachineName);
        json.WriteString("account", character.AccountName);
        json.WriteString("character", character.Name);
        json.WriteString("server", character.WorldName);
        json.WriteString("state", state);
        json.WriteBoolean("healthy", healthy);
        json.WriteNumber("ageSec", 0);
        json.WriteString("source", "status-file");
        json.WriteNumber("uptimeSec", (long)_telemetry.UptimeSeconds);
        json.WriteNumber("fps", (int)Math.Round(fps));
        json.WriteNumber("pluginTicksPerSec", ticks);
        json.WriteNumber("workingSetMB", Environment.WorkingSet / (1024L * 1024L));
        json.WriteBoolean("inWorld", inWorld);

        json.WriteBoolean("macroRunning", running);
        json.WriteString("currentState", meta is { Rules.Count: > 0 } ? meta.CurrentState : string.Empty);
        string activity = running ? engine.ActiveBehaviorName : "Idle";
        json.WriteString("botAction", activity == "idle" ? "Idle" : Capitalize(activity));
        json.WriteString("botReason", engine.LastReason);
        json.WriteString("profile", profile.Name);
        json.WriteString("navProfile", route?.Name ?? "None");
        json.WriteString("lootProfile", profile.Loot.UtlProfile.Length == 0 ? "None" : profile.Loot.UtlProfile);
        json.WriteString("metaProfile", meta is { MetaName.Length: > 0 } ? meta.MetaName : "None");
        bool hasTarget = TryCurrentTarget(out PluginCombatTarget target);
        json.WriteString("target", hasTarget ? target.Name : string.Empty);
        json.WriteNumber("targetHealthPct", hasTarget && target.IsHealthKnown ? Math.Round(target.HealthFraction * 100d) : -1d);
        json.WriteNumber("targetDistance", hasTarget ? Math.Round(target.Distance, 1) : -1d);

        json.WriteBoolean("combatEnabled", profile.Combat.Enabled);
        json.WriteBoolean("buffingEnabled", profile.Buffs.Enabled);
        json.WriteBoolean("navigationEnabled", profile.Navigation.Enabled);
        json.WriteBoolean("lootingEnabled", profile.Loot.Enabled);
        json.WriteBoolean("metaEnabled", profile.Meta.Enabled);
        WriteStrings(json, "profiles", _profiles);
        WriteStrings(json, "navProfiles", _navProfiles);
        WriteStrings(json, "lootProfiles", _lootProfiles);
        WriteStrings(json, "metaProfiles", _metaProfiles);
        json.WriteNumber("selectedProfileIdx", IndexOf(_profiles, profile.Name));
        json.WriteNumber("selectedNavIdx", route is null ? -1 : IndexOf(_navProfiles, route.Name));
        json.WriteNumber("selectedLootIdx", IndexOf(_lootProfiles, profile.Loot.UtlProfile));
        json.WriteNumber("selectedMetaIdx", meta is null ? -1 : IndexOf(_metaProfiles, meta.MetaName));
        json.WriteBoolean("forceRebuffPending", _controller.Buffs.IsForceRebuffPending);
        json.WritePropertyName("buffing");
        json.WriteStartObject();
        json.WriteBoolean("enabled", profile.Buffs.Enabled);
        json.WriteNumber("rebuffWhenRemainingSeconds", profile.Buffs.RebuffWhenRemainingSeconds);
        json.WriteBoolean("buffWeapon", profile.Buffs.BuffWeapon);
        json.WriteBoolean("buffArmor", profile.Buffs.BuffArmor);
        json.WriteBoolean("forcePending", _controller.Buffs.IsForceRebuffPending);
        json.WriteEndObject();
        // Everything in force on the character, by time left; the phone counts these down.
        json.WritePropertyName("enchantments");
        json.WriteStartArray();
        if (inWorld)
        {
            foreach (PluginActiveEnchantment active in character.ActiveEnchantments.OrderBy(static e => e.SecondsRemaining))
            {
                bool known = _surface.Spells.TryGet(active.SpellId, out PluginSpellInfo info);
                json.WriteStartObject();
                json.WriteNumber("spellId", active.SpellId);
                json.WriteString("name", known ? info.Name : "Spell " + active.SpellId.ToString(CultureInfo.InvariantCulture));
                json.WriteNumber("family", active.Family);
                json.WriteNumber("tier", active.Tier);
                json.WriteNumber("secondsRemaining", Math.Round(active.SecondsRemaining));
                json.WriteBoolean("beneficial", !known || info.IsBeneficial);
                json.WriteNumber("school", known ? info.School : 0u);
                json.WriteNumber("iconId", known ? PluginIcons.Normalize(info.IconId) : 0u);
                json.WriteEndObject();
            }
        }
        json.WriteEndArray();
        // The bot's own list: each buff the profile asks for and how it stands, refreshed with the pack.
        json.WritePropertyName("buffPlan");
        json.WriteStartArray();
        foreach (BuffStatus buff in _buffPlan)
        {
            json.WriteStartObject();
            json.WriteString("configured", buff.Configured);
            json.WriteString("kind", buff.Kind);
            if (buff.SpellName is not null) json.WriteString("spell", buff.SpellName); else json.WriteNull("spell");
            json.WriteNumber("spellId", buff.SpellId);
            json.WriteNumber("family", buff.Family);
            json.WriteNumber("tier", buff.Tier);
            json.WriteNumber("secondsRemaining", buff.IsUp ? Math.Round(buff.SecondsRemaining) : -1d);
            // An armor buff the appraisal shows on the piece, with no time to it: up, secondsRemaining -1.
            json.WriteBoolean("upUntimed", buff.IsUpUntimed);
            json.WriteBoolean("due", buff.Due);
            json.WriteBoolean("onCooldown", buff.OnCooldown);
            if (buff.Problem is not null) json.WriteString("problem", buff.Problem); else json.WriteNull("problem");
            if (buff.ItemName is not null) json.WriteString("item", buff.ItemName); else json.WriteNull("item");
            json.WriteNumber("itemId", buff.ItemId);
            json.WriteEndObject();
        }
        json.WriteEndArray();

        json.WritePropertyName("player");
        json.WriteStartObject();
        json.WriteNumber("hp", character.CurrentHealth);
        json.WriteNumber("maxHp", character.MaxHealth);
        json.WriteNumber("st", character.CurrentStamina);
        json.WriteNumber("maxSt", character.MaxStamina);
        json.WriteNumber("mn", character.CurrentMana);
        json.WriteNumber("maxMn", character.MaxMana);
        json.WriteEndObject();
        json.WriteNumber("level", character.Level);

        json.WriteNumber("queueDropped", 0);
        json.WriteNumber("reconciles", 0);
        json.WriteNumber("forceClears", 0);

        json.WriteNumber("deaths", _telemetry.Deaths);
        json.WriteNumber("deathsSession", _telemetry.DeathsSession);
        json.WriteNumber("vitaePct", 0);
        json.WriteNumber("killsPerHour", Math.Round(_telemetry.KillsPerHour, 1));
        json.WriteNumber("xpPerHour", Math.Round(_telemetry.XpPerHour));
        json.WriteNumber("luminancePerHour", Math.Round(_telemetry.LuminancePerHour));
        json.WriteNumber("xpSession", _telemetry.XpSession);
        json.WriteNumber("burdenPct", Math.Round(_burdenPercent, 1));
        WritePosition(json, navigation, inWorld);
        json.WriteNumber("sessionKills", _telemetry.SessionKills);
        json.WriteNumber("secsSinceLastKill", _telemetry.SecondsSinceLastKill);
        json.WriteNumber("freeSlots", inWorld ? character.MainPackFreeSlots : -1);
        json.WriteBoolean("uiHidden", UiHidden);
        json.WriteBoolean("isMinimized", _services.Frames?.IsMinimized == true);
        json.WriteNumber("scarabs", _scarabTotal);
        json.WriteNumber("tapers", _tapers);
        json.WritePropertyName("scarabsByType");
        json.WriteStartArray();
        foreach ((string name, int count) in _scarabs)
        {
            json.WriteStartObject();
            json.WriteString("name", name);
            json.WriteNumber("count", count);
            json.WriteEndObject();
        }
        json.WriteEndArray();
        json.WritePropertyName("equipment");
        json.WriteStartArray();
        foreach (GearItem gear in _equipment)
            gear.Write(json);
        json.WriteEndArray();
        json.WriteNumber("scanTotal", engine.LastBoard?.Hostiles.Count ?? -1);
        json.WriteNumber("scanRing", -1);
        json.WriteNumber("scanPossible", -1);
        json.WriteNumber("scanLosBlocked", -1);
        json.WriteNumber("sessionAttackCasts", 0);
        json.WriteNumber("castsSinceLastKill", 0);
        json.WriteNumber("castsPerKill", 0);
        json.WritePropertyName("recentChat");
        json.WriteStartArray();
        foreach (PluginChatMessage line in _chat)
        {
            json.WriteStartObject();
            json.WriteNumber("s", line.Sequence);   // so a phone can append only what it has not seen
            json.WriteString("t", string.IsNullOrEmpty(line.Sender) ? line.Text : line.Sender + ": " + line.Text);
            json.WriteNumber("c", line.Kind);
            json.WriteEndObject();
        }
        json.WriteEndArray();
        if (_telemetry.LastIssue is { } issue)
            json.WriteString("lastIssue", issue.Text);
        else
            json.WriteNull("lastIssue");
        json.WriteNumber("lastIssueAgeSec", _telemetry.LastIssueAgeSeconds);

        json.WriteEndObject();
        json.WriteEndArray();
        json.WriteEndObject();
        json.Flush();
        ReadOnlySpan<byte> written = _buffer.WrittenSpan;
        ulong hash = Fnv1a(written[..stampStart], Fnv1a(written[stampEnd..], 14695981039346656037ul));
        Changed = hash != _lastContentHash;
        _lastContentHash = hash;
        return written.ToArray();
    }

    /// <summary>The document's content apart from its timestamp, as one number, for the change check.</summary>
    private static ulong Fnv1a(ReadOnlySpan<byte> bytes, ulong hash)
    {
        foreach (byte value in bytes)
            hash = (hash ^ value) * 1099511628211ul;
        return hash;
    }

    /// <summary>
    /// The phone's state light, the way the RynthCore agent classified a
    /// client: loading until in the world, hung with no frames, idle with
    /// the bot stopped, wedged when it runs but the plugin tick has stalled,
    /// otherwise botting.
    /// </summary>
    internal static (string State, bool Healthy) Classify(bool inWorld, double fps, bool running, int ticks)
    {
        if (!inWorld)
            return ("loading", true);
        if (fps <= 0d)
            return ("hung", false);
        if (!running)
            return ("idle", true);
        if (ticks <= 0)
            return ("wedged", false);
        return ("botting", true);
    }

    private void RefreshSlow(bool inWorld)
    {
        _profiles = _controller.Store.ProfileNames();
        _navProfiles = _controller.Store.RouteNames()
            .Concat(_controller.NavFileNames().Select(static file => file[..^4]))
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .ToArray();
        _lootProfiles = _controller.UtlFileNames().Select(static file => file[..^4]).ToArray();
        _metaProfiles = _controller.MetaFileNames().Select(static file => file[..file.LastIndexOf('.')]).ToArray();
        if (!inWorld)
        {
            _equipment = [];
            _scarabs = [];
            _scarabTotal = -1;
            _tapers = -1;
            _burdenPercent = 0d;
            _buffPlan = [];
            return;
        }
        _buffPlan = _controller.Engine.LastBoard is { } board
            ? _controller.Buffs.Report(board)
            : [];

        IReadOnlyList<PluginInventoryItem> owned = _surface.Items.CaptureOwnedItems();
        var equipment = new List<GearItem>();
        var scarabs = new SortedDictionary<string, int>(StringComparer.OrdinalIgnoreCase);
        int tapers = 0;
        int burden = 0;
        foreach (PluginInventoryItem item in owned)
        {
            burden += item.Burden;
            if (item.Name.Contains("Scarab", StringComparison.OrdinalIgnoreCase) && item.ObjectClass == PluginObjectClass.SpellComponent)
                scarabs[item.Name] = scarabs.GetValueOrDefault(item.Name) + Math.Max(1, item.StackSize);
            else if (item.Name.Contains("Prismatic Taper", StringComparison.OrdinalIgnoreCase))
                tapers += Math.Max(1, item.StackSize);
            if (item.IsEquipped)
                equipment.Add(GearItem.From(item, _surface.Objects, _surface.Spells));
        }
        equipment.Sort(static (left, right) => left.Slot.CompareTo(right.Slot));
        _equipment = equipment;
        _scarabs = scarabs.Select(static pair => (pair.Key, pair.Value)).ToArray();
        _scarabTotal = _scarabs.Sum(static pair => pair.Count);
        _tapers = tapers;
        uint strength = 0u;
        foreach (PluginAttributeInfo attribute in _surface.Character.Attributes)
        {
            if (attribute.Kind == 0)
                strength = attribute.Current;
        }
        int capacity = (int)strength * 150;
        _burdenPercent = capacity <= 0 ? 0d : Math.Clamp(burden * 100d / capacity, 0d, 999d);
    }

    private void CaptureChat()
    {
        foreach (PluginChatMessage message in _surface.Chat.CaptureMessages(_chatSequence))
        {
            if (message.Sequence <= _chatSequence)
                continue;
            _chatSequence = message.Sequence;
            _chat.Enqueue(message);
            if (_chat.Count > ChatLines)
                _chat.Dequeue();
        }
    }

    private bool TryCurrentTarget(out PluginCombatTarget target)
    {
        target = default;
        Blackboard? board = _controller.Engine.LastBoard;
        if (board is null)
            return false;
        CombatBehavior? combat = _controller.Engine.Behaviors.OfType<CombatBehavior>().FirstOrDefault();
        uint targetId = combat?.CurrentTargetId ?? 0u;
        if (targetId == 0u)
            targetId = board.Combat.SelectedObjectId;
        foreach (PluginCombatTarget hostile in board.Hostiles)
        {
            if (hostile.ObjectId == targetId)
            {
                target = hostile;
                return true;
            }
        }
        return false;
    }

    private static void WritePosition(Utf8JsonWriter json, PluginNavigationSnapshot navigation, bool inWorld)
    {
        if (!inWorld || !navigation.IsAvailable)
        {
            json.WriteString("area", string.Empty);
            json.WriteString("landblock", string.Empty);
            json.WriteBoolean("indoor", false);
            json.WriteNumber("wx", 0);
            json.WriteNumber("wy", 0);
            json.WriteNumber("pz", 0);
            return;
        }
        PluginNavigationPosition position = navigation.Position;
        uint landblock = position.CellId & 0xFFFF0000u;
        json.WriteString("area", position.IsOutdoor ? Coordinates(position) : "Dungeon " + (landblock >> 16).ToString("X4", CultureInfo.InvariantCulture));
        json.WriteString("landblock", landblock.ToString("X8", CultureInfo.InvariantCulture));
        json.WriteBoolean("indoor", !position.IsOutdoor);
        json.WriteNumber("wx", Math.Round(position.EastWest * 240d, 2));
        json.WriteNumber("wy", Math.Round(position.NorthSouth * 240d, 2));
        json.WriteNumber("pz", Math.Round(position.Elevation * 240d, 2));
        json.WriteNumber("heading", Math.Round(position.HeadingDegrees, 1));
    }

    /// <summary>The game's own notation, "41.5N 34.2E".</summary>
    internal static string Coordinates(in PluginNavigationPosition position) =>
        string.Create(CultureInfo.InvariantCulture,
            $"{Math.Abs(position.NorthSouth):0.0}{(position.NorthSouth >= 0 ? 'N' : 'S')} {Math.Abs(position.EastWest):0.0}{(position.EastWest >= 0 ? 'E' : 'W')}");

    private static void WriteStrings(Utf8JsonWriter json, string name, IReadOnlyList<string> values)
    {
        json.WritePropertyName(name);
        json.WriteStartArray();
        foreach (string value in values)
            json.WriteStringValue(value);
        json.WriteEndArray();
    }

    private static int IndexOf(IReadOnlyList<string> values, string name)
    {
        if (name.Length == 0)
            return -1;
        for (int index = 0; index < values.Count; index++)
        {
            if (string.Equals(values[index], name, StringComparison.OrdinalIgnoreCase))
                return index;
        }
        return -1;
    }

    private static string Capitalize(string value) =>
        value.Length == 0 ? value : char.ToUpperInvariant(value[0]) + value[1..];

    /// <summary>One worn item with what its appraisal says, as the phone's gear card shows it.</summary>
    internal sealed record GearItem(
        string Name,
        uint Id,
        int Slot,
        int ArmorLevel,
        double[]? Resist,
        int Value,
        int Burden,
        int Workmanship,
        int Material,
        int MaxMana,
        int CurMana,
        int Damage,
        int DamageType,
        double WeaponDef,
        double MissileDef,
        double MagicDef,
        double Variance,
        double ElementalMod,
        IReadOnlyList<string> Spells,
        string LongDesc)
    {
        public static GearItem From(in PluginInventoryItem item, IWorldObjectAutomation objects, ISpellCatalog catalog)
        {
            bool appraised = objects.TryCaptureProperties(item.ObjectId, out PluginItemProperties properties);
            int Int(uint key, int fallback) => appraised && properties.Ints.TryGetValue(key, out int value) ? value : fallback;
            double Float(uint key, double fallback) => appraised && properties.Floats.TryGetValue(key, out double value) ? value : fallback;
            double[]? resist = null;
            if (appraised && properties.Floats.ContainsKey(FloatArmorModVsSlash))
            {
                resist = new double[7];
                for (uint offset = 0u; offset < 7u; offset++)
                    resist[offset] = Float(FloatArmorModVsSlash + offset, 1d);
            }
            var spells = new List<string>();
            foreach (uint spellId in item.AppraisedSpellIds)
                spells.Add(catalog.TryGet(spellId, out PluginSpellInfo spell) ? spell.Name : spellId.ToString(CultureInfo.InvariantCulture));
            return new GearItem(
                item.Name,
                item.ObjectId,
                (int)item.EquippedLocation,
                Int(IntArmorLevel, 0),
                resist,
                Int(IntValue, item.Value),
                Int(IntEncumbrance, item.Burden),
                Int(IntWorkmanship, (int)Math.Round(item.Workmanship)),
                Int(IntMaterialType, (int)item.MaterialType),
                Int(IntItemMaxMana, item.ItemMaximumMana),
                Int(IntItemCurMana, item.ItemCurrentMana),
                Int(IntDamage, item.Damage),
                Int(IntDamageType, item.DamageType),
                Float(FloatWeaponDefense, 0d),
                Float(FloatWeaponMissileDefense, 0d),
                Float(FloatWeaponMagicDefense, 0d),
                Float(FloatDamageVariance, item.DamageVariance),
                Float(FloatElementalDamageMod, 0d),
                spells,
                appraised && properties.Strings.TryGetValue(StringLongDesc, out string? desc) ? desc : string.Empty);
        }

        public void Write(Utf8JsonWriter json)
        {
            json.WriteStartObject();
            json.WriteString("name", Name);
            json.WriteNumber("id", Id);
            json.WriteNumber("slot", Slot);
            json.WriteNumber("armorLevel", ArmorLevel);
            if (Resist is not null)
            {
                json.WritePropertyName("resist");
                json.WriteStartArray();
                foreach (double value in Resist)
                    json.WriteNumberValue(Math.Round(value, 3));
                json.WriteEndArray();
            }
            json.WriteNumber("value", Value);
            json.WriteNumber("burden", Burden);
            json.WriteNumber("workmanship", Workmanship);
            json.WriteNumber("material", Material);
            json.WriteNumber("maxMana", MaxMana);
            json.WriteNumber("curMana", CurMana);
            json.WriteNumber("damage", Damage);
            json.WriteNumber("damageType", DamageType);
            json.WriteNumber("weaponDef", Math.Round(WeaponDef, 3));
            json.WriteNumber("missileDef", Math.Round(MissileDef, 3));
            json.WriteNumber("magicDef", Math.Round(MagicDef, 3));
            json.WriteNumber("variance", Math.Round(Variance, 3));
            json.WriteNumber("elementalMod", Math.Round(ElementalMod, 3));
            json.WritePropertyName("spells");
            json.WriteStartArray();
            foreach (string spell in Spells)
                json.WriteStringValue(spell);
            json.WriteEndArray();
            json.WriteString("longDesc", LongDesc);
            json.WriteEndObject();
        }
    }
}
