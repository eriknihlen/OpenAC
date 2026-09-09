using AcDream.Plugin.Abstractions;

namespace AcDream.Plugins.MossTank;

internal sealed class ControllerMacroRule : IMacroRule
{
    private readonly Func<MacroPassContext, bool> _tick;
    private readonly Func<bool>? _gate;
    private readonly Action? _onLostTurn;
    private readonly bool _bookkeepWhenBlocked;
    private bool _running;

    public ControllerMacroRule(
        string name,
        Func<MacroPassContext, bool> tick,
        Func<bool>? gate = null,
        Action? onLostTurn = null,
        bool bookkeepWhenBlocked = true)
    {
        Name = name ?? throw new ArgumentNullException(nameof(name));
        _tick = tick ?? throw new ArgumentNullException(nameof(tick));
        _gate = gate;
        _onLostTurn = onLostTurn;
        _bookkeepWhenBlocked = bookkeepWhenBlocked;
    }

    public string Name { get; }

    public bool ValidNow(in MacroPassContext context)
    {
        bool gateOpen = _gate is null || _gate();
        if (!gateOpen && !_bookkeepWhenBlocked)
            return false;
        bool allowed = context.CanAct && gateOpen;
        bool claimed = _tick(new MacroPassContext(context.ElapsedSeconds, allowed));
        return allowed && claimed;
    }

    public bool Running
    {
        get => _running;
        set
        {
            if (_running == value)
                return;
            _running = value;
            if (!value)
                _onLostTurn?.Invoke();
        }
    }
}

internal sealed class AbsentMacroRule : IMacroRule
{
    public AbsentMacroRule(string name, string reason)
    {
        Name = name ?? throw new ArgumentNullException(nameof(name));
        Reason = reason ?? throw new ArgumentNullException(nameof(reason));
    }

    public string Reason { get; }

    public string Name { get; }

    public bool ValidNow(in MacroPassContext context) => false;

    public bool Running
    {
        get => false;
        set { }
    }
}

internal sealed class IdlePeaceRule : IMacroRule
{
    private readonly IPluginHost _host;
    private readonly CombatSettings _settings;
    private bool _running;

    public IdlePeaceRule(IPluginHost host, CombatSettings settings)
    {
        _host = host ?? throw new ArgumentNullException(nameof(host));
        _settings = settings ?? throw new ArgumentNullException(nameof(settings));
    }

    public string Name => "IdlePeace";

    /// <summary>The last request's outcome, surfaced on the panel status line.</summary>
    public string? Status { get; private set; }

    public bool ValidNow(in MacroPassContext context)
    {
        if (!context.CanAct)
            return false;
        if (!_settings.IdlePeaceMode)
            return false;
        PluginCombatMode mode = _host.Automation.Combat.Snapshot.Mode;

        return mode is not (PluginCombatMode.Peace or PluginCombatMode.Unknown);
    }

    public bool Running
    {
        get => _running;
        set
        {
            _running = value;
            if (!value)
            {
                Status = null;
                return;
            }

            PluginCombatCommandResult result =
                _host.Automation.Combat.EnterMode(PluginCombatMode.Peace);
            Status = result.Status == PluginCombatCommandStatus.Refused
                ? result.Notice ?? "Cannot enter peace mode"
                : "Entering peace mode";
        }
    }
}
