namespace AcDream.App.World;

public sealed class LiveEntityAnimationRuntimeView<TAnimation>
    : IEnumerable<KeyValuePair<uint, TAnimation>>
    where TAnimation : class, ILiveEntityAnimationRuntime
{
    private readonly ILiveEntityRuntimeSource _runtime;
    private readonly List<KeyValuePair<uint, TAnimation>> _iterationSnapshot = new();

    internal LiveEntityAnimationRuntimeView(ILiveEntityRuntimeSource runtime) =>
        _runtime = runtime ?? throw new ArgumentNullException(nameof(runtime));

    public int Count => _runtime.Current?.SpatialAnimationRuntimeCount ?? 0;

    public TAnimation this[uint localEntityId]
    {
        set
        {
            LiveEntityRuntime runtime = _runtime.Current
                ?? throw new InvalidOperationException("Live entity runtime is not initialized.");
            if (!runtime.TryGetServerGuid(localEntityId, out uint guid))
                throw new InvalidOperationException($"No live entity owns local id 0x{localEntityId:X8}.");
            runtime.SetAnimationRuntime(guid, value);
        }
    }

    public bool TryGetValue(uint localEntityId, out TAnimation animation)
    {
        if (_runtime.Current?.TryGetAnimationRuntime(localEntityId, out var found) == true
            && found is TAnimation typed)
        {
            animation = typed;
            return true;
        }

        animation = null!;
        return false;
    }

    public bool Remove(uint localEntityId)
    {
        LiveEntityRuntime? runtime = _runtime.Current;
        return runtime is not null
            && runtime.TryGetServerGuid(localEntityId, out uint guid)
            && runtime.ClearAnimationRuntime(guid);
    }

    internal bool Remove(LiveEntityRecord record)
    {
        ArgumentNullException.ThrowIfNull(record);
        return _runtime.Current?.ClearAnimationRuntime(record) == true;
    }

    public void CopySpatialIdsTo(HashSet<uint> destination)
    {
        ArgumentNullException.ThrowIfNull(destination);
        LiveEntityRuntime? runtime = _runtime.Current;
        if (runtime is null)
            destination.Clear();
        else
            runtime.CopySpatialAnimationLocalIdsTo(destination);
    }

    public Enumerator GetEnumerator()
    {
        LiveEntityRuntime? runtime = _runtime.Current;
        if (runtime is null)
            _iterationSnapshot.Clear();
        else
            runtime.CopySpatialAnimationRuntimesTo(_iterationSnapshot);

        return new Enumerator(runtime, _iterationSnapshot);
    }

    IEnumerator<KeyValuePair<uint, TAnimation>> IEnumerable<KeyValuePair<uint, TAnimation>>.GetEnumerator() =>
        GetEnumerator();

    System.Collections.IEnumerator System.Collections.IEnumerable.GetEnumerator() => GetEnumerator();

    public struct Enumerator : IEnumerator<KeyValuePair<uint, TAnimation>>
    {
        private readonly LiveEntityRuntime? _runtime;
        private readonly List<KeyValuePair<uint, TAnimation>> _snapshot;
        private int _index;

        internal Enumerator(
            LiveEntityRuntime? runtime,
            List<KeyValuePair<uint, TAnimation>> snapshot)
        {
            _runtime = runtime;
            _snapshot = snapshot;
            _index = -1;
            Current = default;
        }

        public KeyValuePair<uint, TAnimation> Current { get; private set; }
        object System.Collections.IEnumerator.Current => Current;

        public bool MoveNext()
        {
            while (++_index < _snapshot.Count)
            {
                KeyValuePair<uint, TAnimation> pair = _snapshot[_index];
                if (_runtime?.IsCurrentSpatialAnimation(
                        pair.Key,
                        pair.Value) == true)
                {
                    Current = pair;
                    return true;
                }
            }

            Current = default;
            return false;
        }

        public void Dispose() { }

        void System.Collections.IEnumerator.Reset() =>
            throw new NotSupportedException();
    }
}
