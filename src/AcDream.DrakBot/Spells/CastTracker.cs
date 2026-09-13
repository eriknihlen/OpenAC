using AcDream.Plugin.Abstractions;

namespace AcDream.DrakBot.Spells;

/// <summary>
/// Spells that recently failed and when they may be tried again. Shared by
/// every behavior so a spell that fizzled for one is not immediately retried
/// by another, and consulted by <see cref="SpellSelector"/> so a lower tier
/// is picked in the meantime.
/// </summary>
public sealed class CastCooldowns(IBotClock clock)
{
    public const double FailureBackoffSeconds = 5d;

    private readonly Dictionary<uint, double> _retryAfter = [];

    public bool IsOnCooldown(uint spellId) =>
        _retryAfter.TryGetValue(spellId, out double until) && clock.Now < until;

    public void MarkFailed(uint spellId) =>
        _retryAfter[spellId] = clock.Now + FailureBackoffSeconds;
}

/// <summary>
/// One behavior's in-flight cast. Lets the behavior tell "still casting",
/// "completed" and "the host never answered" apart without confusing its
/// request with another behavior's.
/// </summary>
public sealed class CastTracker(IMagicCommands magic, IBotClock clock, CastCooldowns cooldowns)
{
    public const double RequestTimeoutSeconds = 8d;

    private long _completionRevisionAtRequest;
    private double _requestedAt = double.NegativeInfinity;

    public CastTracker(IMagicCommands magic, IBotClock clock)
        : this(magic, clock, new CastCooldowns(clock))
    {
    }

    public CastCooldowns Cooldowns => cooldowns;

    public uint PendingSpellId { get; private set; }

    public bool HasPendingRequest => PendingSpellId != 0u;

    public bool IsOnCooldown(uint spellId) => cooldowns.IsOnCooldown(spellId);

    public PluginCastRequestResult Request(uint spellId, uint targetObjectId = 0u)
    {
        _completionRevisionAtRequest = magic.LastCompletion.Revision;
        PluginCastRequestResult result = targetObjectId == 0u
            ? magic.RequestCast(spellId)
            : magic.RequestCast(spellId, targetObjectId);
        if (result == PluginCastRequestResult.Sent)
        {
            PendingSpellId = spellId;
            _requestedAt = clock.Now;
        }
        else
        {
            cooldowns.MarkFailed(spellId);
        }
        return result;
    }

    /// <summary>Resolves the pending request; null while it is still in flight.</summary>
    public CastOutcome? Poll()
    {
        if (!HasPendingRequest)
            return null;

        PluginCastCompletion completion = magic.LastCompletion;
        if (completion.Revision != _completionRevisionAtRequest
            && completion.SpellId == PendingSpellId)
        {
            uint spellId = PendingSpellId;
            Clear();
            if (completion.IsSuccess)
                return CastOutcome.Succeeded;
            cooldowns.MarkFailed(spellId);
            return CastOutcome.Failed;
        }

        if (magic.IsCasting)
            return null;

        if (clock.Now - _requestedAt > RequestTimeoutSeconds)
        {
            cooldowns.MarkFailed(PendingSpellId);
            Clear();
            return CastOutcome.TimedOut;
        }
        return null;
    }

    public void Clear()
    {
        PendingSpellId = 0u;
        _requestedAt = double.NegativeInfinity;
    }
}

public enum CastOutcome
{
    Succeeded,
    Failed,
    TimedOut,
}
