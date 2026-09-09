using AcDream.App.Rendering;
using AcDream.App.World;

namespace AcDream.App.Composition;

internal sealed class LivePresentationRuntimeBindings : IDisposable
{
    private sealed record Entry(string Name, IDisposable Binding);

    private readonly List<Entry> _bindings = [];
    private bool _deactivationStarted;

    public void Adopt(string name, IDisposable binding)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(name);
        ArgumentNullException.ThrowIfNull(binding);
        ObjectDisposedException.ThrowIf(_deactivationStarted, this);
        _bindings.Add(new Entry(name, binding));
    }

    public IDisposable AdoptOwned(string name, IDisposable binding)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(name);
        ArgumentNullException.ThrowIfNull(binding);
        ObjectDisposedException.ThrowIf(_deactivationStarted, this);
        var entry = new Entry(name, binding);
        _bindings.Add(entry);
        return new Adoption(this, entry);
    }

    public void AdoptRelease(string name, Action release) =>
        Adopt(name, new DelegateBinding(release));

    public void BindProjectionVisibility(
        LiveEntityRuntime source,
        Action<LiveEntityRecord, bool> handler,
        string name)
    {
        ArgumentNullException.ThrowIfNull(source);
        ArgumentNullException.ThrowIfNull(handler);
        source.ProjectionVisibilityChanged += handler;
        Adopt(name, new DelegateBinding(
            () => source.ProjectionVisibilityChanged -= handler));
    }

    public void BindProjectionPoseReady(
        EquippedChildRenderController source,
        Action<uint> handler)
    {
        ArgumentNullException.ThrowIfNull(source);
        ArgumentNullException.ThrowIfNull(handler);
        source.ProjectionPoseReady += handler;
        Adopt("equipped-child projection pose", new DelegateBinding(
            () => source.ProjectionPoseReady -= handler));
    }

    public void BindProjectionRemoved(
        EquippedChildRenderController source,
        Action<LiveEntityRecord> handler)
    {
        ArgumentNullException.ThrowIfNull(source);
        ArgumentNullException.ThrowIfNull(handler);
        source.ProjectionRemoved += handler;
        Adopt("equipped-child projection removal", new DelegateBinding(
            () => source.ProjectionRemoved -= handler));
    }

    public void Dispose()
    {
        if (_deactivationStarted && _bindings.Count == 0)
            return;
        _deactivationStarted = true;

        List<Exception>? failures = null;
        for (int i = _bindings.Count - 1; i >= 0; i--)
        {
            Entry entry = _bindings[i];
            try
            {
                entry.Binding.Dispose();
                _bindings.RemoveAt(i);
            }
            catch (Exception failure)
            {
                (failures ??= []).Add(new InvalidOperationException(
                    $"Live-presentation binding '{entry.Name}' did not detach.",
                    failure));
            }
        }

        if (failures is not null)
        {
            throw new AggregateException(
                "Live-presentation binding cleanup remains incomplete.",
                failures);
        }
    }

    private void ReleaseAdoption(Entry expected)
    {
        int index = _bindings.IndexOf(expected);
        if (index < 0)
            return;
        expected.Binding.Dispose();
        _bindings.RemoveAt(index);
    }

    private sealed class Adoption : IDisposable
    {
        private LivePresentationRuntimeBindings? _owner;
        private readonly Entry _expected;

        public Adoption(
            LivePresentationRuntimeBindings owner,
            Entry expected)
        {
            _owner = owner;
            _expected = expected;
        }

        public void Dispose()
        {
            LivePresentationRuntimeBindings? owner = _owner;
            if (owner is null)
                return;
            owner.ReleaseAdoption(_expected);
            _owner = null;
        }
    }

    private sealed class DelegateBinding : IDisposable
    {
        private Action? _release;

        public DelegateBinding(Action release) =>
            _release = release ?? throw new ArgumentNullException(nameof(release));

        public void Dispose()
        {
            Action? release = _release;
            if (release is null)
                return;
            release();
            _release = null;
        }
    }
}
