using AcDream.Plugin.Abstractions;

namespace AcDream.Plugins.MossTank;

internal sealed class CombatModeGate
{
    public const uint CasterItemType = 0x00008000u;

    public const string NoWandNotice =
        "You must add at least one wand to your profile.";

    public const string BuggedCombatStateWarning =
        "Warning: Macro detected bugged combat state. Attempting to wield an "
        + "item to clear it.";

    private const double ModeConfirmationSeconds = 0.6d;

    private const uint WeaponReadyMask = 0x03500000u;

    private readonly IPluginHost _host;
    private readonly CombatSettings _settings;
    private readonly VitalSettings _vitalSettings;
    private readonly Action<string> _stopMacro;

    private int _dropToPeaceRetries;

    private double _sinceModeRequest = ModeConfirmationSeconds;
    private PluginCombatMode _modeBeforeRequest = PluginCombatMode.Unknown;
    private bool _modeRequestInFlight;
    private bool _noWandNoticePosted;

    internal double SinceModeRequestSecondsForTests => _sinceModeRequest;

    /// <summary>VTank's <c>ah.m_a</c> (<c>ah.cs:6</c>).</summary>
    private readonly HashSet<string> _postedWarnings = new(StringComparer.Ordinal);

    public CombatModeGate(
        IPluginHost host,
        CombatSettings settings,
        VitalSettings vitalSettings,
        Action<string> stopMacro)
    {
        _host = host ?? throw new ArgumentNullException(nameof(host));
        _settings = settings ?? throw new ArgumentNullException(nameof(settings));
        _vitalSettings = vitalSettings
            ?? throw new ArgumentNullException(nameof(vitalSettings));
        _stopMacro = stopMacro ?? throw new ArgumentNullException(nameof(stopMacro));
    }

    public string Status { get; private set; } = string.Empty;

    public Action<MacroLogChannel, string>? Log { get; set; }

    public Func<uint, MonsterDamageType, bool>? AmmunitionStale { get; set; }

    public Func<MonsterDamageType, bool>? WieldAmmunition { get; set; }

    public static bool IsCaster(in PluginEquipmentItem item) =>
        (item.ItemType & CasterItemType) != 0u;

    public static PluginCombatMode ModeFor(in PluginEquipmentItem item)
    {
        if (IsCaster(in item))
            return PluginCombatMode.Magic;
        if (item.AmmoType != 0u)
            return PluginCombatMode.Missile;
        if (item.Damage > 0 || item.WeaponSkill != 0)
            return PluginCombatMode.Melee;
        return PluginCombatMode.Magic;
    }

    public void ResetOncePerRunWarnings()
    {
        _noWandNoticePosted = false;
        _postedWarnings.Clear();
    }

    private void PostWarningOnce(string text)
    {
        if (!_postedWarnings.Add(text))
            return;
        _host.Automation.Chat.PostSystemMessage("[MossTank] " + text);
    }

    public void Reset()
    {
        _dropToPeaceRetries = 0;
        _sinceModeRequest = ModeConfirmationSeconds;
        _modeBeforeRequest = PluginCombatMode.Unknown;
        _modeRequestInFlight = false;
        _noWandNoticePosted = false;
        _postedWarnings.Clear();
        Status = string.Empty;
    }

    public void AdvancePass(double elapsedSeconds) =>
        _sinceModeRequest += Math.Max(0d, elapsedSeconds);

    public bool TryPrepare(
        PluginCombatMode wanted,
        uint overrideItemId = 0u,
        bool autoSelect = true,
        MonsterDamageType element = MonsterDamageType.None)
    {
        IAutomationSurface automation = _host.Automation;
        IEquipmentAutomation equipment = automation.Equipment;

        if (equipment.IsBusy || automation.Items.IsBusy || automation.Magic.IsCasting)
        {
            Status = "Busy";
            return false;
        }

        if (!equipment.IsAvailable)
            return TryPrepareMode(wielded: null, wanted);

        IReadOnlyList<PluginEquipmentItem> items =
            equipment.CaptureOwnedEquipment();
        PluginEquipmentItem? wielded = FindWielded(items);

        uint primary = overrideItemId;
        if (primary != 0u)
        {
            PluginEquipmentItem? requested = FindById(items, primary);
            if (requested is not { } candidate
                || (candidate.ValidLocations & WeaponReadyMask) == 0u)
            {
                if (requested is { } named)
                {
                    Status = $"Warning: FCM ignoring item {named.Name} because "
                        + "it cannot currently be wielded.";
                    PostWarningOnce(Status);
                }
                primary = 0u;
            }
        }

        if ((autoSelect || overrideItemId == 0u)
            && wielded is { } worn
            && ModeFor(in worn) == wanted)
        {
            primary = worn.ObjectId;
        }

        // ga.cs:1466-1475 — otherwise the first wand on the Items page.
        if (primary == 0u)
        {
            if (FindFirstProfiledWand(items) is not { } fallback)
            {
                PostNoWandNoticeAndStop();
                return false;
            }
            primary = fallback.ObjectId;
        }

        bool flag2 = AmmunitionStale?.Invoke(primary, element) == true;
        if (flag2 || wielded?.ObjectId != primary)
        {
            if (wielded?.ObjectId != primary)
            {
                PluginEquipmentItem? target = FindById(items, primary);
                string name = target?.Name ?? "caster";

                // ga.cs:1508-1528 — the drop-to-peace branch, with the retry
                // budget and the stuck-state recovery.
                if (!TryDropToPeace(items, name))
                    return false;

                Log?.Invoke(MacroLogChannel.BusyState, $"(FCM) equip {name}");
                PluginEquipmentCommandResult equip = equipment.Equip(primary);
                if (equip.Status == PluginEquipmentCommandStatus.Refused)
                {
                    Status = equip.Notice ?? $"Cannot equip {name}.";
                    return false;
                }
                Status = $"Equipping {name}";
                return false;
            }

            // ga.cs:1550-1554 — reached only once the weapon already matches.
            if (flag2 && WieldAmmunition?.Invoke(element) == true)
                return false;
        }

        return TryPrepareMode(wielded, wanted);
    }

    /// <summary>
    /// <c>ga.cs:1556-1565</c> — recompute the mode the wielded item implies
    /// and ask for it if it differs. Returns true only once they agree
    /// (<c>ga.cs:1566</c>).
    /// </summary>
    private bool TryPrepareMode(
        PluginEquipmentItem? wielded,
        PluginCombatMode wanted)
    {
        PluginCombatMode implied = wielded is { } equipped
            ? ModeFor(in equipped)
            : wanted;

        if (_host.Automation.Combat.Snapshot.Mode != implied
            && !RequestMode(implied))
        {
            return false;
        }

        Status = "Ready";
        return true;
    }

    public bool TryDropToPeace(
        IReadOnlyList<PluginEquipmentItem> items,
        string forItemName)
    {
        ArgumentNullException.ThrowIfNull(items);

        PluginCombatMode mode = EffectiveMode();

        if (mode == PluginCombatMode.Unknown)
        {
            Status = "Waiting for the combat mode";
            return false;
        }

        if (mode == PluginCombatMode.Peace)
        {
            _dropToPeaceRetries = 0; // ga.cs:1529
            return true;
        }

        _dropToPeaceRetries++;
        if (_dropToPeaceRetries >= _vitalSettings.DropToPeaceModeRetryCount)
        {
            _dropToPeaceRetries = 0;
            if (FindFirstProfiledWand(items) is not { } recovery)
            {
                PostNoWandNoticeAndStop();
                return false;
            }

            PluginItemCommandResult use =
                _host.Automation.Items.Use(recovery.ObjectId);
            Status = use.Status == PluginItemCommandStatus.Started
                ? BuggedCombatStateWarning
                : $"Combat-state recovery with {recovery.Name}: {use.Status}";
            if (use.Status == PluginItemCommandStatus.Started)
                _host.Automation.Chat.PostSystemMessage("[MossTank] " + BuggedCombatStateWarning);
            return false;
        }

        if (RequestMode(PluginCombatMode.Peace))
        {
            _dropToPeaceRetries = 0;
            return true;
        }
        if (Status.Length == 0 || Status.StartsWith("Entering", StringComparison.Ordinal))
            Status = $"Entering peace mode to equip {forItemName}";
        return false;
    }

    private PluginEquipmentItem? FindFirstProfiledWand(
        IReadOnlyList<PluginEquipmentItem> items)
    {
        foreach (string name in _settings.CombatItemOrder)
        {
            foreach (PluginEquipmentItem item in items)
            {
                if (IsCaster(in item)
                    && item.Name.Equals(name, StringComparison.Ordinal))
                {
                    return item;
                }
            }
        }

        PluginEquipmentItem? best = null;
        foreach (PluginEquipmentItem item in items)
        {
            if (!IsCaster(in item) || !IsProfiled(in item))
                continue;
            if (_settings.CombatItemOrder.Contains(item.Name))
                continue;
            if (best is null
                || string.CompareOrdinal(item.Name, best.Value.Name) < 0
                || (string.Equals(item.Name, best.Value.Name, StringComparison.Ordinal)
                    && item.ObjectId < best.Value.ObjectId))
            {
                best = item;
            }
        }
        return best;
    }

    private bool IsProfiled(in PluginEquipmentItem item) =>
        _settings.CombatItemObjectIds.Contains(item.ObjectId)
        || _settings.CombatItemNames.Contains(item.Name);

    private static PluginEquipmentItem? FindById(
        IReadOnlyList<PluginEquipmentItem> items,
        uint objectId)
    {
        foreach (PluginEquipmentItem item in items)
        {
            if (item.ObjectId == objectId)
                return item;
        }
        return null;
    }

    internal static PluginEquipmentItem? FindWielded(
        IReadOnlyList<PluginEquipmentItem> items)
    {
        foreach (PluginEquipmentItem item in items)
        {
            if (item.IsEquipped && (item.ValidLocations & WeaponReadyMask) != 0u)
                return item;
        }
        return null;
    }

    private PluginCombatMode EffectiveMode()
    {
        PluginCombatMode live = _host.Automation.Combat.Snapshot.Mode;

        if (_modeRequestInFlight && live != _modeBeforeRequest)
        {
            _modeRequestInFlight = false;
            _sinceModeRequest = 0d;
        }

        // f9.cs:331-344 — g() is purely time-based; BOTH of its arms are the
        // same `m_h + 600 ms > now` expression, so m_i never affects the
        // answer. f9.cs:358-368 — e() returns the saved mode inside the
        // window and the live one outside it.
        return _sinceModeRequest < ModeConfirmationSeconds
            ? _modeBeforeRequest
            : live;
    }

    private bool RequestMode(PluginCombatMode mode)
    {
        _modeBeforeRequest = _host.Automation.Combat.Snapshot.Mode;
        _sinceModeRequest = 0d;
        _modeRequestInFlight = true;
        Log?.Invoke(MacroLogChannel.BusyState, $"(FCM) requesting {mode}");
        PluginCombatCommandResult result =
            _host.Automation.Combat.EnterMode(mode);
        if (result.Status == PluginCombatCommandStatus.Unavailable)
        {
            _modeRequestInFlight = false;
            return true;
        }
        Status = result.Status == PluginCombatCommandStatus.Refused
            ? result.Notice ?? $"Cannot enter {mode} mode"
            : $"Entering {mode} mode";
        return false;
    }

    private void PostNoWandNoticeAndStop()
    {
        Status = NoWandNotice;
        if (!_noWandNoticePosted)
        {
            _host.Automation.Chat.PostSystemMessage("[MossTank] " + NoWandNotice);
            _noWandNoticePosted = true;
        }
        _stopMacro(NoWandNotice);
    }
}
