using AcDream.Core.World;

namespace AcDream.App.World;

internal sealed class CompositeLiveEntityResourceLifecycle : ILiveEntityResourceLifecycle
{
    internal readonly record struct Owner(
        Action<WorldEntity> Register,
        Action<WorldEntity> Unregister);

    private sealed class OwnerState(int count)
    {
        public bool[] Held { get; } = new bool[count];
        public bool DesiredRegistered { get; set; }
        public bool Reconciling { get; set; }
    }

    private sealed record ReconcileFailures(
        List<Exception> Registration,
        List<Exception> Release);

    private readonly Owner[] _owners;
    private readonly Dictionary<WorldEntity, OwnerState> _states =
        new(ReferenceEqualityComparer.Instance);

    public CompositeLiveEntityResourceLifecycle(params Owner[] owners)
    {
        ArgumentNullException.ThrowIfNull(owners);
        if (owners.Length == 0)
            throw new ArgumentException("At least one resource owner is required.", nameof(owners));
        _owners = [.. owners];
    }

    public void Register(WorldEntity entity)
    {
        ArgumentNullException.ThrowIfNull(entity);
        if (!_states.TryGetValue(entity, out OwnerState? state))
        {
            state = new OwnerState(_owners.Length);
            _states.Add(entity, state);
        }

        state.DesiredRegistered = true;
        ReconcileFailures? failures = Reconcile(entity, state);
        if (failures is null)
            return;

        if (failures.Registration.Count == 1 && failures.Release.Count == 0)
            System.Runtime.ExceptionServices.ExceptionDispatchInfo
                .Capture(failures.Registration[0])
                .Throw();

        throw new AggregateException(
            "Live entity resource registration and owner rollback failed.",
            failures.Registration.Concat(failures.Release));
    }

    public void Unregister(WorldEntity entity)
    {
        ArgumentNullException.ThrowIfNull(entity);
        if (!_states.TryGetValue(entity, out OwnerState? state))
            return;

        state.DesiredRegistered = false;
        ReconcileFailures? failures = Reconcile(entity, state);
        if (failures is not null)
            throw new AggregateException(
                "One or more live entity resource owners failed to release.",
                failures.Registration.Concat(failures.Release));
    }

    private ReconcileFailures? Reconcile(WorldEntity entity, OwnerState state)
    {
        if (state.Reconciling)
            return null;

        state.Reconciling = true;
        List<Exception>? registrationFailures = null;
        List<Exception>? releaseFailures = null;
        try
        {
            while (true)
            {
                bool directionChanged = false;
                if (state.DesiredRegistered)
                {
                    for (int i = 0; i < _owners.Length; i++)
                    {
                        if (!state.DesiredRegistered)
                        {
                            directionChanged = true;
                            break;
                        }
                        if (state.Held[i])
                            continue;

                        state.Held[i] = true;
                        try
                        {
                            _owners[i].Register(entity);
                        }
                        catch (Exception error)
                        {
                            (registrationFailures ??= []).Add(error);
                            state.DesiredRegistered = false;
                            directionChanged = true;
                            break;
                        }
                    }
                }

                if (!state.DesiredRegistered)
                {
                    for (int i = _owners.Length - 1; i >= 0; i--)
                    {
                        if (state.DesiredRegistered)
                        {
                            directionChanged = true;
                            break;
                        }
                        if (!state.Held[i])
                            continue;
                        try
                        {
                            _owners[i].Unregister(entity);
                            state.Held[i] = false;
                        }
                        catch (Exception error)
                        {
                            (releaseFailures ??= []).Add(error);
                        }
                    }

                    if (state.DesiredRegistered)
                        directionChanged = true;
                }

                if (!directionChanged)
                    break;
            }
        }
        finally
        {
            state.Reconciling = false;
            if (!state.DesiredRegistered && !state.Held.Contains(true))
                _states.Remove(entity);
        }

        return registrationFailures is null && releaseFailures is null
            ? null
            : new ReconcileFailures(
                registrationFailures ?? [],
                releaseFailures ?? []);
    }
}
