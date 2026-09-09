using AcDream.Plugins.MossTank.Expressions;

namespace AcDream.Plugins.MossTank;

internal sealed partial class MossTankPanel
{
    private bool _autostartAppliedForCurrentSession;

    internal void TickAutostart()
    {
        bool available = _host.Automation.IsAvailable;
        if (!available)
        {
            // Dropped out of world (disconnect/reconnect/logout): the next
            // false→true edge is a new generation, so allow autostart to run
            // again exactly once for it.
            _autostartAppliedForCurrentSession = false;
            return;
        }
        if (_autostartAppliedForCurrentSession)
            return;
        _autostartAppliedForCurrentSession = true;

        IReadOnlyDictionary<string, string> settings = _host.SessionSettings;
        if (settings.Count == 0)
            return;
        ApplyAutostart(settings);
    }

    private void ApplyAutostart(IReadOnlyDictionary<string, string> settings)
    {
        ApplyNamedProfile(
            settings, "settingsProfile", "settings profile",
            _profiles.Exists, SelectProfile);
        ApplyNamedProfile(
            settings, "metaProfile", "Meta profile",
            _metaProfiles.Exists, SelectMetaProfileCore);
        ApplyNamedProfile(
            settings, "navProfile", "navigation profile",
            _routeProfiles.Exists, SelectRouteProfileCore);
        ApplyNamedProfile(
            settings, "lootProfile", "loot profile",
            _lootProfiles.Exists, SelectLootProfileCore);

        if (settings.TryGetValue("enableMeta", out string? enableMetaRaw)
            && bool.TryParse(enableMetaRaw, out bool enableMeta))
        {
            try
            {
                SetMetaOption("enablemeta", ExpressionValue.Boolean(enableMeta));
            }
            catch (Exception error)
            {
                _host.Log.Error("Autostart: failed to set EnableMeta.", error);
            }
        }

        if (settings.TryGetValue("startMacro", out string? startMacroRaw)
            && bool.TryParse(startMacroRaw, out bool startMacro)
            && startMacro)
        {
            try
            {
                if (!_combat.Enabled)
                    SetMacroRunning(true);
            }
            catch (Exception error)
            {
                _host.Log.Error("Autostart: failed to start the macro.", error);
            }
        }
    }

    private void ApplyNamedProfile(
        IReadOnlyDictionary<string, string> settings,
        string key,
        string label,
        Func<string, bool> probe,
        Action<string> applyThroughTab)
    {
        if (!settings.TryGetValue(key, out string? name)
            || string.IsNullOrWhiteSpace(name))
        {
            return;
        }

        try
        {
            if (!probe(name))
            {
                _host.Log.Error($"Autostart: {label} '{name}' was not found.");
                return;
            }
            applyThroughTab(name);
        }
        catch (Exception error)
        {
            _host.Log.Error(
                $"Autostart: failed to select {label} '{name}'.",
                error);
        }
    }
}
