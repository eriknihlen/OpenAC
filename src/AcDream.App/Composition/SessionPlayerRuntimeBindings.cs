using AcDream.App.Rendering;
using AcDream.App.World;

namespace AcDream.App.Composition;

internal sealed class SessionPlayerRuntimeBindings : IDisposable
{
    private readonly List<(string Name, IDisposable Binding)> _bindings = [];
    private bool _deactivationStarted;

    public void Adopt(string name, IDisposable binding)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(name);
        ArgumentNullException.ThrowIfNull(binding);
        ObjectDisposedException.ThrowIf(_deactivationStarted, this);
        _bindings.Add((name, binding));
    }

    public void BindEntityReady(
        EquippedChildRenderController source,
        Action<LiveEntityReadyCandidate> handler)
    {
        ArgumentNullException.ThrowIfNull(source);
        ArgumentNullException.ThrowIfNull(handler);
        source.EntityReady += handler;
        Adopt(
            "equipped-child entity ready",
            new DelegateBinding(() => source.EntityReady -= handler));
    }

    public void BindAppearanceApplied(
        LiveEntityHydrationController source,
        Action<uint> handler)
    {
        ArgumentNullException.ThrowIfNull(source);
        ArgumentNullException.ThrowIfNull(handler);
        source.AppearanceApplied += handler;
        Adopt(
            "live appearance applied",
            new DelegateBinding(() => source.AppearanceApplied -= handler));
    }

    public void Dispose()
    {
        if (_deactivationStarted && _bindings.Count == 0)
            return;
        _deactivationStarted = true;

        List<Exception>? failures = null;
        for (int i = _bindings.Count - 1; i >= 0; i--)
        {
            (string name, IDisposable binding) = _bindings[i];
            try
            {
                binding.Dispose();
                _bindings.RemoveAt(i);
            }
            catch (Exception failure)
            {
                (failures ??= []).Add(new InvalidOperationException(
                    $"Session/player binding '{name}' did not detach.",
                    failure));
            }
        }

        if (failures is not null)
        {
            throw new AggregateException(
                "Session/player binding cleanup remains incomplete.",
                failures);
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
