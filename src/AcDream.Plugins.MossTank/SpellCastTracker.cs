using AcDream.Plugin.Abstractions;

namespace AcDream.Plugins.MossTank;

internal enum SpellCastTrackerState
{
    /// <summary><c>gj.b.a</c> — nothing in flight.</summary>
    Idle,

    AwaitingLaunch,

    AwaitingResult,
}

internal enum SpellCastOutcome
{
    None,

    /// <summary><c>gj.cs:442-453</c> — a success line matched.</summary>
    Success,

    /// <summary><c>gj.cs:395-398</c> — a kill line matched.</summary>
    Kill,

    /// <summary><c>gj.cs:419-422</c> — a fizzle/resist line matched.</summary>
    Fail,

    /// <summary><c>gj.cs:409-413</c> — a permanent-fail line matched.</summary>
    PermanentFail,

    Rejected,

    /// <summary>
    /// <c>gj.cs:324-327</c> — the 5.000 s attempt budget expired with no
    /// acknowledgement.
    /// </summary>
    LaunchTimeout,

    ResultTimeout,
}

internal readonly record struct SpellCastOutcomeInfo(
    SpellCastOutcome Outcome,
    uint SpellId,
    string SpellName,
    uint TargetObjectId,
    string TargetName,
    bool HitsMultipleTargets,
    uint WeenieError,
    string Text);

internal sealed class SpellCastTracker
{
    public const double LaunchTimeoutSeconds = 5.0d;

    /// <summary><c>gj.cs:254</c> — <c>j.a(907)</c>, the result timer's tick.</summary>
    public const double ResultTickSeconds = 0.907d;

    /// <summary>
    /// <c>gj.cs:255</c> — <c>q = 4500 / j.e()</c>. <c>ey.e()</c> returns the
    /// timer INTERVAL (<c>ey.cs:219-222</c>), so this is an integer tick
    /// budget: <c>4500 / 907 = 4</c>.
    /// </summary>
    public const int ResultTickBudget = 4;

    public const double ResultTimeoutSeconds = ResultTickBudget * ResultTickSeconds;

    public const double WorstCaseBusySeconds =
        LaunchTimeoutSeconds + ResultTimeoutSeconds;

    internal const int LocalSpeechChatKind = 0;

    private SpellCastTrackerState _state;
    private uint _spellId;
    private string _spellName = string.Empty;
    private uint _targetObjectId;

    private string _targetName = string.Empty;

    private bool _hitsMultipleTargets;
    private string _saying = string.Empty;
    private long _issueRevision;
    private long _observedCompletionRevision;
    private ulong _observedChatSequence;
    private double _launchElapsed;
    private double _resultElapsed;
    private SpellCastOutcomeInfo _outcome;
    private bool _hasOutcome;

    public event Action<SpellCastOutcomeInfo>? Completed;

    public SpellCastTrackerState State => _state;

    public bool IsBusy => _state != SpellCastTrackerState.Idle;

    public uint SpellId => _spellId;

    public uint TargetObjectId => _targetObjectId;

    public bool HitsMultipleTargets => _hitsMultipleTargets;

    public static bool HitsMultipleTargetsFor(in PluginSpellInfo spell) =>
        (spell.FormulaComponentIds.Count > 0
            && spell.FormulaComponentIds[0] == 110u)
        || string.Equals(spell.Saying, "tugakquati", StringComparison.Ordinal)
        || spell.Family == 638u;

    public void Begin(
        uint spellId,
        string spellName,
        uint targetObjectId,
        string targetName,
        bool hitsMultipleTargets,
        long issueRevision,
        string saying = "")
    {
        _state = SpellCastTrackerState.AwaitingLaunch;
        _saying = Normalize(saying);
        _spellId = spellId;
        _spellName = spellName ?? string.Empty;
        _targetObjectId = targetObjectId;
        _targetName = targetName ?? string.Empty;
        _hitsMultipleTargets = hitsMultipleTargets;
        _issueRevision = issueRevision;
        _observedCompletionRevision = issueRevision;
        _launchElapsed = 0d;
        _resultElapsed = 0d;
        _hasOutcome = false;
        _outcome = default;
    }

    public void Reset()
    {
        _state = SpellCastTrackerState.Idle;
        _spellId = 0u;
        _spellName = string.Empty;
        _targetObjectId = 0u;
        _targetName = string.Empty;
        _hitsMultipleTargets = false;
        _saying = string.Empty;
        _issueRevision = 0;
        _observedCompletionRevision = 0;
        _observedChatSequence = 0uL;
        _launchElapsed = 0d;
        _resultElapsed = 0d;
        _hasOutcome = false;
        _outcome = default;
    }

    /// <summary>
    /// <c>gj.cs:267</c> — a deleted object ends the wait only when it is the
    /// awaited target.
    /// </summary>
    public void ResetForTarget(uint objectId)
    {
        if (IsBusy && objectId != 0u && objectId == _targetObjectId)
            Reset();
    }

    public void ObserveCompletion(in PluginCastCompletion completion)
    {
        if (!IsBusy)
            return;
        if (completion.Revision == _observedCompletionRevision)
            return;
        if (completion.Revision == _issueRevision || completion.SpellId != _spellId)
            return;
        _observedCompletionRevision = completion.Revision;
        if (completion.WeenieError != 0u)
        {
            Complete(SpellCastOutcome.Rejected, completion.WeenieError, string.Empty);
            return;
        }
        if (_state != SpellCastTrackerState.AwaitingLaunch)
            return;

        // gj.cs:227-228 — the b -> c edge resets r and starts the result
        // timer. The busy latch is NOT re-raised (m_d is already true).
        _state = SpellCastTrackerState.AwaitingResult;
        _resultElapsed = 0d;
    }

    public void ObserveChat(ulong sequence, string text, bool ownSpeech = false)
    {
        if (!IsBusy || string.IsNullOrEmpty(text))
            return;
        if (sequence != 0uL && sequence <= _observedChatSequence)
            return;
        if (sequence != 0uL)
            _observedChatSequence = sequence;

        if (_state == SpellCastTrackerState.AwaitingLaunch
            && _saying.Length > 0
            && ownSpeech)
        {
            if (string.Equals(Normalize(text), _saying, StringComparison.Ordinal))
            {
                // gj.cs:355 — a(gj.b.c). The busy latch is NOT re-raised.
                _state = SpellCastTrackerState.AwaitingResult;
                _resultElapsed = 0d;
            }
            else
            {
                // gj.cs:359 — a(gj.b.a).
                Reset();
            }
            return;
        }

        CombatResultTextClass result = CombatResultText.Classify(
            text,
            out string spellName,
            out string targetName);
        if (result == CombatResultTextClass.None)
            return;

        if (spellName.Length > 0
            && _spellName.Length > 0
            && !spellName.Equals(_spellName, StringComparison.Ordinal))
        {
            return;
        }

        // gj.cs:439-440 — likewise for the target name, `m_e`.
        if (targetName.Length > 0
            && _targetName.Length > 0
            && !targetName.Equals(_targetName, StringComparison.OrdinalIgnoreCase))
        {
            return;
        }

        switch (result)
        {
            case CombatResultTextClass.Kill:
                Complete(SpellCastOutcome.Kill, 0u, text);
                return;
            case CombatResultTextClass.PermanentFail:
                if (_hitsMultipleTargets)
                    return;
                Complete(SpellCastOutcome.PermanentFail, 0u, text);
                return;
            case CombatResultTextClass.Fail:
                Complete(SpellCastOutcome.Fail, 0u, text);
                return;
            case CombatResultTextClass.Success:
                Complete(SpellCastOutcome.Success, 0u, text);
                return;
        }
    }

    public void Advance(double elapsedSeconds)
    {
        double elapsed = Math.Max(0d, elapsedSeconds);
        switch (_state)
        {
            case SpellCastTrackerState.AwaitingLaunch:
                _launchElapsed += elapsed;
                if (_launchElapsed >= LaunchTimeoutSeconds)
                    Complete(SpellCastOutcome.LaunchTimeout, 0u, string.Empty);
                return;
            case SpellCastTrackerState.AwaitingResult:
                _resultElapsed += elapsed;
                if (_resultElapsed >= ResultTimeoutSeconds)
                    Complete(SpellCastOutcome.ResultTimeout, 0u, string.Empty);
                return;
        }
    }

    public bool TryConsumeOutcome(out SpellCastOutcomeInfo outcome)
    {
        outcome = _outcome;
        bool had = _hasOutcome;
        _hasOutcome = false;
        _outcome = default;
        return had;
    }

    /// <summary><c>gj.cs:352</c> — <c>ToLowerInvariant().Replace(" ", "")</c>.</summary>
    private static string Normalize(string text) =>
        string.IsNullOrEmpty(text)
            ? string.Empty
            : text.Replace(" ", string.Empty, StringComparison.Ordinal)
                .ToLowerInvariant();

    private void Complete(SpellCastOutcome outcome, uint weenieError, string text)
    {
        var info = new SpellCastOutcomeInfo(
            outcome,
            _spellId,
            _spellName,
            _targetObjectId,
            _targetName,
            _hitsMultipleTargets,
            weenieError,
            text);

        _state = SpellCastTrackerState.Idle;
        _launchElapsed = 0d;
        _resultElapsed = 0d;
        _outcome = info;
        _hasOutcome = true;
        Completed?.Invoke(info);
    }
}
