using AcDream.Plugin.Abstractions;

namespace AcDream.App.Rendering.Immediate;

/// <summary>
/// The draw callbacks plugins register through <see cref="IImmediateUiHost"/>.
/// Created with the window so plugins can register before the overlay
/// exists; the overlay binds later and drains the list every frame.
/// </summary>
internal sealed class ImGuiDrawerRegistry(bool enabled) : IImmediateUiHost
{
    private readonly object _gate = new();
    private readonly List<Drawer> _drawers = [];
    private Drawer[] _snapshot = [];
    private volatile bool _available = enabled;

    /// <summary>
    /// True while this host intends to draw an overlay. Plugins enable before
    /// the renderer is up, so this answers "will my drawer run", not "is it
    /// running now"; it turns false only if the overlay fails to come up.
    /// </summary>
    public bool IsAvailable => _available;

    public int Count
    {
        get
        {
            lock (_gate)
                return _drawers.Count;
        }
    }

    public IDisposable Register(string name, Action draw)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(name);
        ArgumentNullException.ThrowIfNull(draw);
        var drawer = new Drawer(this, name, draw);
        lock (_gate)
        {
            _drawers.Add(drawer);
            _snapshot = _drawers.ToArray();
        }
        return drawer;
    }

    internal void MarkUnavailable() => _available = false;

    /// <summary>Runs every live drawer. One that throws is logged and removed.</summary>
    internal void DrawAll(Action<string, Exception> onFailure)
    {
        Drawer[] drawers = _snapshot;
        foreach (Drawer drawer in drawers)
        {
            if (drawer.Faulted)
                continue;
            try
            {
                drawer.Draw();
            }
            catch (Exception error)
            {
                drawer.Faulted = true;
                onFailure(drawer.Name, error);
                drawer.Dispose();
            }
        }
    }

    private void Remove(Drawer drawer)
    {
        lock (_gate)
        {
            if (_drawers.Remove(drawer))
                _snapshot = _drawers.ToArray();
        }
    }

    private sealed class Drawer(ImGuiDrawerRegistry owner, string name, Action draw) : IDisposable
    {
        private ImGuiDrawerRegistry? _owner = owner;

        public string Name { get; } = name;

        public Action Draw { get; } = draw;

        public bool Faulted { get; set; }

        public void Dispose() => Interlocked.Exchange(ref _owner, null)?.Remove(this);
    }
}
