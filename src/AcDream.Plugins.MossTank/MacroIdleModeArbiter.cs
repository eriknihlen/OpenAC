using AcDream.Plugin.Abstractions;

namespace AcDream.Plugins.MossTank;

internal sealed class MacroIdleModeArbiter
{
    private const double IdleRetrySeconds = 1.0;

    private readonly IPluginHost _host;
    private readonly CombatSettings _settings;

    /// <summary>Starts armed so the first idle tick can request immediately.</summary>
    private double _sinceLastRequest = IdleRetrySeconds;

    public MacroIdleModeArbiter(IPluginHost host, CombatSettings settings)
    {
        _host = host ?? throw new ArgumentNullException(nameof(host));
        _settings = settings ?? throw new ArgumentNullException(nameof(settings));
    }

    /// <summary>Last request's outcome, or null while the arbiter is inactive.</summary>
    public string? Status { get; private set; }

    public void Tick(double elapsedSeconds, bool macroRunning, bool idle)
    {
        _sinceLastRequest += Math.Max(0d, elapsedSeconds);

        if (!macroRunning || !_settings.IdlePeaceMode || !idle)
        {
            Status = null;
            return;
        }

        PluginCombatMode mode = _host.Automation.Combat.Snapshot.Mode;
        if (mode is PluginCombatMode.Peace or PluginCombatMode.Unknown)
        {
            Status = null;
            return;
        }

        if (_sinceLastRequest < IdleRetrySeconds)
            return;

        _sinceLastRequest = 0d;
        PluginCombatCommandResult result =
            _host.Automation.Combat.EnterMode(PluginCombatMode.Peace);
        Status = result.Status == PluginCombatCommandStatus.Refused
            ? result.Notice ?? "Cannot enter peace mode"
            : "Entering peace mode";
    }
}
