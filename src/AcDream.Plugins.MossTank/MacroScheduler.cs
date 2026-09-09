namespace AcDream.Plugins.MossTank;

internal readonly record struct MacroPassContext(
    double ElapsedSeconds,
    bool CanAct);

internal interface IMacroRule
{
    /// <summary>VTank's <c>FriendlyName</c>; the scheduler's status/log name.</summary>
    string Name { get; }

    bool ValidNow(in MacroPassContext context);

    bool Running { get; set; }
}

internal sealed class MacroRuleSentinel : IMacroRule
{
    public MacroRuleSentinel(string marker)
    {
        Marker = marker ?? throw new ArgumentNullException(nameof(marker));
        Name = "Sentinel " + marker;
    }

    public string Marker { get; }

    public string Name { get; }

    public bool ValidNow(in MacroPassContext context) => false;

    public bool Running
    {
        get => false;
        set { }
    }
}

internal sealed class MacroRulePreChain : IMacroRule
{
    private readonly IMacroRule _primary;
    private readonly IReadOnlyList<IMacroRule> _fallbacks;
    private readonly IReadOnlyList<Func<bool>> _gates;

    public MacroRulePreChain(
        IMacroRule primary,
        IReadOnlyList<Func<bool>>? gates = null,
        IReadOnlyList<IMacroRule>? fallbacks = null)
    {
        _primary = primary ?? throw new ArgumentNullException(nameof(primary));
        _gates = gates ?? Array.Empty<Func<bool>>();
        _fallbacks = fallbacks ?? Array.Empty<IMacroRule>();
    }

    public string Name => _primary.Name;

    public bool ValidNow(in MacroPassContext context)
    {
        foreach (Func<bool> gate in _gates)
        {
            if (!gate())
                return false;
        }
        return _primary.ValidNow(in context);
    }

    public bool Running
    {
        get
        {
            if (_primary.Running)
                return true;
            foreach (IMacroRule fallback in _fallbacks)
            {
                if (fallback.Running)
                    return true;
            }
            return false;
        }
        set
        {
            if (!value)
            {
                foreach (IMacroRule fallback in _fallbacks)
                    fallback.Running = false;
                _primary.Running = false;
                return;
            }

            var context = new MacroPassContext(0d, CanAct: true);
            foreach (IMacroRule fallback in _fallbacks)
            {
                if (!fallback.ValidNow(in context))
                    continue;
                fallback.Running = true;
                return;
            }
            _primary.Running = true;
        }
    }
}

internal sealed class MacroScheduler
{
    public const double HeartbeatSeconds = 0.293d;

    private readonly List<IMacroRule> _main;
    private readonly List<IMacroRule> _independent;

    private double _untilPass;
    private double _sincePass;
    private bool _poked;
    private int _suspension;

    public MacroScheduler(
        IReadOnlyList<IMacroRule> mainRules,
        IReadOnlyList<IMacroRule>? independentRules = null)
    {
        ArgumentNullException.ThrowIfNull(mainRules);
        _main = [.. mainRules];
        _independent = independentRules is null
            ? []
            : [.. independentRules];
    }

    /// <summary>VTank's <c>dz.o.c</c> — the single "the macro is running" flag.</summary>
    public bool IsRunning { get; private set; }

    public bool IsSuspended => _suspension > 0 || ExternalSuspension;

    public bool ExternalSuspension { get; set; }

    public IMacroRule? LastExecutedRule { get; private set; }

    public IReadOnlyList<IMacroRule> MainRules => _main;

    public IReadOnlyList<IMacroRule> IndependentRules => _independent;

    public Action<double>? MetaPass { get; set; }

    public long PassCount { get; private set; }

    private double _suspendedMetaSeconds;

    public Action<MacroLogChannel, string>? Log { get; set; }

    public Func<string>? LockStateSuffix { get; set; }

    /// <summary>VTank's <c>ga.h()</c> (<c>ga.cs:116-119</c>).</summary>
    public void Suspend() => _suspension++;

    public void Resume()
    {
        _suspension--;
        if (_suspension > 0)
            return;
        _suspension = 0;
        Poke();
    }

    public void Poke()
    {
        if (IsRunning)
            _poked = true;
    }

    public void Start()
    {
        IsRunning = true;
        _suspension = 0;
        _untilPass = 0d;
        _poked = true;
        LastExecutedRule = null;
        _suspendedMetaSeconds = 0d;
    }

    public void Stop()
    {
        foreach (IMacroRule rule in _main)
            rule.Running = false;
        foreach (IMacroRule rule in _independent)
            rule.Running = false;
        IsRunning = false;
        LastExecutedRule = null;
        _poked = false;
        _untilPass = HeartbeatSeconds;
    }

    public bool Advance(double elapsedSeconds)
    {
        double elapsed = Math.Max(0d, elapsedSeconds);
        _sincePass += elapsed;
        if (!IsRunning)
        {
            _sincePass = 0d;
            return false;
        }

        _untilPass -= elapsed;
        if (_poked)
        {
            _poked = false;
            _untilPass = 0d;
        }
        else if (_untilPass > 0d)
        {
            return false;
        }

        _untilPass = HeartbeatSeconds;
        double passElapsed = _sincePass;
        _sincePass = 0d;
        RunPass(passElapsed);
        return true;
    }

    public void RunPass(double elapsedSeconds)
    {
        double elapsed = Math.Max(0d, elapsedSeconds);

        if (IsSuspended)
        {
            _suspendedMetaSeconds += elapsed;
        }
        else
        {
            MetaPass?.Invoke(elapsed + _suspendedMetaSeconds);
            _suspendedMetaSeconds = 0d;
        }

        var independentContext = new MacroPassContext(elapsed, CanAct: true);
        foreach (IMacroRule rule in _independent)
            rule.Running = rule.ValidNow(in independentContext);

        if (IsSuspended)
            return;

        Log?.Invoke(
            MacroLogChannel.ActiveRule,
            $"----------- Primary logic loop started ({_main.Count} rules) -----------");

        IMacroRule? winner = null;
        int winnerIndex = -1;
        int index = 0;
        foreach (IMacroRule rule in _main)
        {
            var context = new MacroPassContext(elapsed, CanAct: winner is null);
            if (rule.ValidNow(in context) && winner is null)
            {
                winner = rule;
                winnerIndex = index;
            }
            index++;
        }

        string suffix = LockStateSuffix?.Invoke() ?? string.Empty;
        Log?.Invoke(
            MacroLogChannel.ActiveRule,
            winner is not null
                ? $"Picked {winner.Name} P: {winnerIndex}{suffix}"
                : $"All rules inactive.{suffix}");

        foreach (IMacroRule rule in _main)
        {
            if (!ReferenceEquals(rule, winner))
                rule.Running = false;
        }
        if (winner is not null)
        {
            winner.Running = true;

            Log?.Invoke(MacroLogChannel.RuleInfo, $"({winner.Name}) Running");
        }

        LastExecutedRule = winner;
        PassCount++;
    }
}
