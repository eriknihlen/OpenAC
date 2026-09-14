using System.Numerics;
using AcDream.App.World;
using AcDream.Plugin.Abstractions;
using ImGuiNET;

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
    private IWorldFrameCameraSource? _camera;
    private LiveWorldOriginState? _origin;
    private Matrix4x4 _viewProjection;
    private int _centerX;
    private int _centerY;
    private bool _worldFrameValid;

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

    /// <summary>The camera and world origin the overlay projects map positions with.</summary>
    internal void BindWorld(IWorldFrameCameraSource camera, LiveWorldOriginState origin)
    {
        _camera = camera;
        _origin = origin;
    }

    /// <summary>Resolves the frame's camera once, before the drawers run.</summary>
    internal void BeginWorldFrame()
    {
        _worldFrameValid = false;
        if (_camera is null || _origin is null || !_origin.IsKnown)
            return;
        try
        {
            WorldCameraFrame frame = _camera.Resolve();
            _viewProjection = frame.ViewProjection;
            _centerX = _origin.CenterX;
            _centerY = _origin.CenterY;
            _worldFrameValid = true;
        }
        catch (InvalidOperationException)
        {
            // No active camera yet (login screen); nothing projects this frame.
        }
    }

    /// <summary>
    /// Map coordinates (landblock units east/north, elevation) to the render
    /// world - landblocks are 192 m, the map origin sits at block 127 offset
    /// 84 m, the render frame is centred on the world origin's landblock -
    /// then through the frame's view-projection to overlay pixels.
    /// </summary>
    public bool TryProjectToScreen(in PluginNavigationPosition position, out float screenX, out float screenY)
    {
        screenX = 0f;
        screenY = 0f;
        if (!_worldFrameValid)
            return false;
        double worldX = position.EastWest * 240d + 84d + (127 - _centerX) * 192d;
        double worldY = position.NorthSouth * 240d + 84d + (127 - _centerY) * 192d;
        double worldZ = position.Elevation * 240d;
        Vector4 clip = Vector4.Transform(new Vector4((float)worldX, (float)worldY, (float)worldZ, 1f), _viewProjection);
        if (clip.W <= 0.001f)
            return false;
        Vector2 display = ImGui.GetIO().DisplaySize;
        float ndcX = clip.X / clip.W;
        float ndcY = clip.Y / clip.W;
        screenX = (ndcX * 0.5f + 0.5f) * display.X;
        screenY = (1f - (ndcY * 0.5f + 0.5f)) * display.Y;
        return float.IsFinite(screenX) && float.IsFinite(screenY);
    }

    /// <summary>The world geometry plugins submitted; the world pass drains it each frame.</summary>
    internal WorldMarkerQueue WorldMarkers { get; } = new();

    public bool WorldGeometryAvailable => _available && WorldMarkers.HasRenderer;

    public void AddWorldRing(in PluginNavigationPosition center, float radiusMeters, float thicknessMeters, float heightMeters, Vector4 color)
    {
        if (_available)
            WorldMarkers.AddRing(center, radiusMeters, thicknessMeters, heightMeters, color);
    }

    public void AddWorldLine(in PluginNavigationPosition from, in PluginNavigationPosition to, float thicknessMeters, Vector4 color)
    {
        if (_available)
            WorldMarkers.AddLine(from, to, thicknessMeters, color);
    }

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
