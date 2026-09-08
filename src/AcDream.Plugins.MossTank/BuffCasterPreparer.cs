using AcDream.Plugin.Abstractions;

namespace AcDream.Plugins.MossTank;

internal sealed class BuffCasterPreparer
{
    private const uint CasterItemType = 0x00008000u;

    private const double ModeRetrySeconds = 2.0;

    private readonly IPluginHost _host;
    private readonly CombatSettings _settings;
    private readonly VitalSettings _vitalSettings;

    private PluginCombatMode? _pendingRequestedMode;
    private double _modeWaitElapsed;
    private int _modeRetryCount;

    private bool _noCasterNoticePosted;

    public BuffCasterPreparer(
        IPluginHost host,
        CombatSettings settings,
        VitalSettings? vitalSettings = null)
    {
        _host = host ?? throw new ArgumentNullException(nameof(host));
        _settings = settings ?? throw new ArgumentNullException(nameof(settings));
        _vitalSettings = vitalSettings ?? new VitalSettings();
    }

    /// <summary>The buff queue may run once this is true.</summary>
    public bool Ready { get; private set; }

    public bool Stopped { get; private set; }

    public string Status { get; private set; } = string.Empty;

    public void Tick(double elapsedSeconds)
    {
        if (Ready || Stopped)
            return;

        IAutomationSurface automation = _host.Automation;
        IEquipmentAutomation equipment = automation.Equipment;

        if (equipment.IsAvailable)
        {
            if (equipment.IsBusy)
            {
                Status = "Equipping caster";
                return;
            }

            if (!TryResolveCaster(equipment.CaptureOwnedEquipment(), out PluginEquipmentItem caster))
            {
                StopWithNoCasterNotice();
                return;
            }

            if (!caster.IsEquipped)
            {
                PluginCombatMode wieldMode = automation.Combat.Snapshot.Mode;
                if (wieldMode != PluginCombatMode.Peace)
                {
                    RequestMode(
                        PluginCombatMode.Peace,
                        elapsedSeconds,
                        "could not enter peace mode to equip a caster");
                    return;
                }

                ResetModeWait();
                PluginEquipmentCommandResult equip = equipment.Equip(caster.ObjectId);
                if (equip.Status == PluginEquipmentCommandStatus.Refused)
                {
                    Stop(equip.Notice ?? $"Cannot equip {caster.Name}.");
                    return;
                }
                Status = $"Equipping {caster.Name}";
                return;
            }
        }

        PluginCombatMode currentMode = automation.Combat.Snapshot.Mode;
        if (currentMode != PluginCombatMode.Magic)
        {
            RequestMode(PluginCombatMode.Magic, elapsedSeconds, "could not enter magic mode");
            return;
        }

        Ready = true;
        Status = "Ready to buff";
    }

    /// <summary>Reset on Stop, on session end, and when the macro stops.</summary>
    public void Reset()
    {
        Ready = false;
        Stopped = false;
        Status = string.Empty;
        _pendingRequestedMode = null;
        _modeWaitElapsed = 0d;
        _modeRetryCount = 0;
        _noCasterNoticePosted = false;
    }

    private bool TryResolveCaster(
        IReadOnlyList<PluginEquipmentItem> items,
        out PluginEquipmentItem caster)
    {
        foreach (PluginEquipmentItem item in items)
        {
            if ((item.ItemType & CasterItemType) != 0u && item.IsEquipped)
            {
                caster = item;
                return true;
            }
        }

        PluginEquipmentItem? best = null;
        foreach (PluginEquipmentItem item in items)
        {
            if ((item.ItemType & CasterItemType) == 0u)
                continue;
            if (!_settings.CombatItemObjectIds.Contains(item.ObjectId)
                && !_settings.CombatItemNames.Contains(item.Name))
            {
                continue;
            }
            if (best is null
                || string.CompareOrdinal(item.Name, best.Value.Name) < 0
                || (string.Equals(item.Name, best.Value.Name, StringComparison.Ordinal)
                    && item.ObjectId < best.Value.ObjectId))
            {
                best = item;
            }
        }

        if (best is { } selected)
        {
            caster = selected;
            return true;
        }
        caster = default;
        return false;
    }

    private void StopWithNoCasterNotice()
    {
        const string notice = "You must add at least one wand to your Items profile.";
        Stopped = true;
        Status = notice;
        if (_noCasterNoticePosted)
            return;
        _host.Automation.Chat.PostSystemMessage("[MossTank] " + notice);
        _noCasterNoticePosted = true;
    }

    private void Stop(string status)
    {
        Stopped = true;
        Status = status;
    }

    private void ResetModeWait()
    {
        _pendingRequestedMode = null;
        _modeWaitElapsed = 0d;
        _modeRetryCount = 0;
    }

    private void RequestMode(PluginCombatMode mode, double elapsedSeconds, string exhaustedStatus)
    {
        if (_pendingRequestedMode != mode)
        {
            _pendingRequestedMode = mode;
            _modeWaitElapsed = 0d;
            _modeRetryCount = 1;
            IssueModeRequest(mode);
            return;
        }

        _modeWaitElapsed += Math.Max(0d, elapsedSeconds);
        if (_modeWaitElapsed < ModeRetrySeconds)
        {
            Status = $"Entering {mode} mode";
            return;
        }

        _modeWaitElapsed = 0d;
        _modeRetryCount++;
        if (_modeRetryCount > _vitalSettings.DropToPeaceModeRetryCount)
        {
            Stop(exhaustedStatus);
            return;
        }
        IssueModeRequest(mode);
    }

    private void IssueModeRequest(PluginCombatMode mode)
    {
        PluginCombatCommandResult result = _host.Automation.Combat.EnterMode(mode);
        if (result.Status == PluginCombatCommandStatus.Unavailable)
        {
            Ready = true;
            Status = "Ready to buff";
            return;
        }
        Status = result.Status == PluginCombatCommandStatus.Refused
            ? result.Notice ?? $"Cannot enter {mode} mode"
            : $"Entering {mode} mode";
    }
}
