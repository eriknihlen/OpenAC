using AcDream.Core.Physics;

namespace AcDream.App.Interaction;

internal interface IPlayerApproachCompletionSink
{
    void PublishNaturalCompletion();
    void PublishCancellation(WeenieError error);
}

internal interface IPlayerApproachCompletionLifetimeOwner
{
    IPlayerApproachCompletionSink BeginControllerLifetime();
    void RetireControllerLifetime(IPlayerApproachCompletionSink lifetime);
}

internal interface IPlayerApproachTokenSource
{
    bool TryBeginApproach(out PlayerApproachToken token);
}

internal readonly record struct PlayerApproachToken(
    ulong ControllerLifetime,
    ulong ApproachGeneration);

internal readonly record struct PlayerApproachCompletion(
    PlayerApproachToken Token,
    bool IsNatural,
    WeenieError Error);

internal sealed class PlayerApproachCompletionState
    : IPlayerApproachCompletionLifetimeOwner,
      IPlayerApproachTokenSource
{
    private readonly Queue<PlayerApproachCompletion> _pending = new();
    private ControllerLifetime? _active;
    private ulong _nextLifetime;

    public IPlayerApproachCompletionSink BeginControllerLifetime()
    {
        if (_active is not null)
            throw new InvalidOperationException(
                "A player approach-completion lifetime is already active.");

        var lifetime = new ControllerLifetime(this, ++_nextLifetime);
        _active = lifetime;
        return lifetime;
    }

    public void RetireControllerLifetime(IPlayerApproachCompletionSink lifetime)
    {
        ArgumentNullException.ThrowIfNull(lifetime);
        if (!ReferenceEquals(_active, lifetime)
            || lifetime is not ControllerLifetime retiring)
            return;

        PurgeLifetime(retiring.LifetimeId);
        if (retiring.TryGetCurrentToken(out PlayerApproachToken token))
        {
            _pending.Enqueue(new PlayerApproachCompletion(
                token,
                IsNatural: false,
                WeenieError.ActionCancelled));
        }
        _active = null;
    }

    public bool TryBeginApproach(out PlayerApproachToken token)
    {
        if (_active is null)
        {
            token = default;
            return false;
        }

        token = _active.BeginApproach();
        return true;
    }

    public bool TryTake(out PlayerApproachCompletion completion) =>
        _pending.TryDequeue(out completion);

    public void Clear()
    {
        _active = null;
        _pending.Clear();
    }

    private void Publish(
        ControllerLifetime lifetime,
        bool isNatural,
        WeenieError error)
    {
        if (!ReferenceEquals(_active, lifetime)
            || !lifetime.TryGetCurrentToken(out PlayerApproachToken token))
        {
            return;
        }

        _pending.Enqueue(new PlayerApproachCompletion(token, isNatural, error));
    }

    private void PurgeLifetime(ulong lifetimeId)
    {
        int retained = _pending.Count;
        for (int i = 0; i < retained; i++)
        {
            PlayerApproachCompletion completion = _pending.Dequeue();
            if (completion.Token.ControllerLifetime != lifetimeId)
                _pending.Enqueue(completion);
        }
    }

    private sealed class ControllerLifetime(
        PlayerApproachCompletionState owner,
        ulong lifetimeId) : IPlayerApproachCompletionSink
    {
        private ulong _approachGeneration;
        public ulong LifetimeId => lifetimeId;

        public PlayerApproachToken BeginApproach() =>
            new(lifetimeId, ++_approachGeneration);

        public bool TryGetCurrentToken(out PlayerApproachToken token)
        {
            token = new PlayerApproachToken(lifetimeId, _approachGeneration);
            return _approachGeneration != 0;
        }

        public void PublishNaturalCompletion() =>
            owner.Publish(this, isNatural: true, WeenieError.None);

        public void PublishCancellation(WeenieError error) =>
            owner.Publish(this, isNatural: false, error);
    }
}
