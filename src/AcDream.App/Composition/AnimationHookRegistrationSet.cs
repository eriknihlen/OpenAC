using AcDream.App.Rendering;
using AcDream.Core.Physics;

namespace AcDream.App.Composition;

internal sealed class AnimationHookRegistrationSet : IDisposable,
    IRetryableResourceCleanup
{
    private sealed class Entry(IAnimationHookSink sink)
    {
        public IAnimationHookSink Sink { get; } = sink;
        public bool Removed { get; set; }
    }

    private readonly Action<IAnimationHookSink> _register;
    private readonly Action<IAnimationHookSink> _unregister;
    private readonly List<Entry> _entries = [];
    private bool _disposing;
    private bool _closed;

    public AnimationHookRegistrationSet(AnimationHookRouter router)
        : this(
            (router ?? throw new ArgumentNullException(nameof(router))).Register,
            router.Unregister)
    {
    }

    internal AnimationHookRegistrationSet(
        Action<IAnimationHookSink> register,
        Action<IAnimationHookSink> unregister)
    {
        _register = register ?? throw new ArgumentNullException(nameof(register));
        _unregister = unregister ?? throw new ArgumentNullException(nameof(unregister));
    }

    public bool IsCleanupComplete =>
        _entries.All(static entry => entry.Removed);

    public int ActiveCount =>
        _entries.Count(static entry => !entry.Removed);

    public void Register(IAnimationHookSink sink)
    {
        ArgumentNullException.ThrowIfNull(sink);
        ObjectDisposedException.ThrowIf(_closed, this);
        if (_entries.Any(entry =>
                !entry.Removed && ReferenceEquals(entry.Sink, sink)))
        {
            return;
        }

        _register(sink);
        _entries.Add(new Entry(sink));
    }

    public IDisposable RegisterOwned(IAnimationHookSink sink)
    {
        ArgumentNullException.ThrowIfNull(sink);
        ObjectDisposedException.ThrowIf(_closed, this);
        if (_entries.Any(entry =>
                !entry.Removed && ReferenceEquals(entry.Sink, sink)))
        {
            throw new InvalidOperationException(
                "The animation-hook sink is already registered and cannot be adopted twice.");
        }
        _register(sink);
        _entries.Add(new Entry(sink));
        return new Binding(this, sink);
    }

    private void UnregisterOwned(IAnimationHookSink sink)
    {
        Entry? entry = _entries.LastOrDefault(candidate =>
            !candidate.Removed && ReferenceEquals(candidate.Sink, sink));
        if (entry is null)
            return;
        _unregister(entry.Sink);
        entry.Removed = true;
    }

    public void Dispose()
    {
        _closed = true;
        RetryCleanup();
    }

    public void RetryCleanup()
    {
        if (_disposing || IsCleanupComplete)
            return;

        _closed = true;
        _disposing = true;
        List<Exception>? failures = null;
        try
        {
            for (int i = _entries.Count - 1; i >= 0; i--)
            {
                Entry entry = _entries[i];
                if (entry.Removed)
                    continue;
                try
                {
                    _unregister(entry.Sink);
                    entry.Removed = true;
                }
                catch (Exception failure)
                {
                    (failures ??= []).Add(new InvalidOperationException(
                        $"Animation-hook sink '{entry.Sink.GetType().Name}' could not be unregistered.",
                        failure));
                }
            }
        }
        finally
        {
            _disposing = false;
        }

        if (failures is not null)
            throw new AggregateException(
                "Animation-hook registration cleanup remains incomplete.",
                failures);
    }

    private sealed class Binding : IDisposable
    {
        private AnimationHookRegistrationSet? _owner;
        private readonly IAnimationHookSink _sink;

        public Binding(AnimationHookRegistrationSet owner, IAnimationHookSink sink)
        {
            _owner = owner;
            _sink = sink;
        }

        public void Dispose()
        {
            AnimationHookRegistrationSet? owner = _owner;
            if (owner is null)
                return;
            owner.UnregisterOwned(_sink);
            _owner = null;
        }
    }
}
