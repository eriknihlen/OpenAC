using System.Globalization;
using System.Numerics;
using AcDream.App.Input;
using AcDream.App.Rendering;
using AcDream.App.Streaming;
using AcDream.App.World;
using AcDream.Core.Physics;
using AcDream.Core.World;
using AcDream.UI.Abstractions.Input;

namespace AcDream.App.Diagnostics;

internal interface IRuntimeDiagnosticCommands
{
    bool Handle(InputAction action);

    void CycleTimeOfDay();

    void CycleWeather();

    void ToggleCollisionWireframes();
}

internal sealed class RuntimeDiagnosticCommandSlot : IRuntimeDiagnosticCommands
{
    private readonly object _gate = new();
    private IRuntimeDiagnosticCommands? _target;
    private bool _deactivated;

    public void Bind(IRuntimeDiagnosticCommands target)
    {
        ArgumentNullException.ThrowIfNull(target);
        lock (_gate)
        {
            ObjectDisposedException.ThrowIf(_deactivated, this);
            if (_target is not null && !ReferenceEquals(_target, target))
            {
                throw new InvalidOperationException(
                    "Runtime diagnostic commands are already bound.");
            }

            _target = target;
        }
    }

    public void Unbind(IRuntimeDiagnosticCommands target)
    {
        ArgumentNullException.ThrowIfNull(target);
        lock (_gate)
        {
            if (ReferenceEquals(_target, target))
                _target = null;
        }
    }

    public IDisposable BindOwned(IRuntimeDiagnosticCommands target)
    {
        ArgumentNullException.ThrowIfNull(target);
        lock (_gate)
        {
            ObjectDisposedException.ThrowIf(_deactivated, this);
            if (_target is not null)
            {
                throw new InvalidOperationException(
                    "Runtime diagnostic commands are already bound.");
            }

            _target = target;
        }

        return new Binding(this, target);
    }

    public void Deactivate()
    {
        lock (_gate)
        {
            _deactivated = true;
            _target = null;
        }
    }

    public bool Handle(InputAction action)
    {
        lock (_gate)
        {
            if (!_deactivated && _target is { } target)
                return target.Handle(action);
        }

        return RuntimeDiagnosticCommandController.IsDiagnosticAction(action);
    }

    public void CycleTimeOfDay()
    {
        lock (_gate)
        {
            if (!_deactivated)
                _target?.CycleTimeOfDay();
        }
    }

    public void CycleWeather()
    {
        lock (_gate)
        {
            if (!_deactivated)
                _target?.CycleWeather();
        }
    }

    public void ToggleCollisionWireframes()
    {
        lock (_gate)
        {
            if (!_deactivated)
                _target?.ToggleCollisionWireframes();
        }
    }

    private sealed class Binding : IDisposable
    {
        private RuntimeDiagnosticCommandSlot? _owner;
        private readonly IRuntimeDiagnosticCommands _expected;

        public Binding(
            RuntimeDiagnosticCommandSlot owner,
            IRuntimeDiagnosticCommands expected)
        {
            _owner = owner;
            _expected = expected;
        }

        public void Dispose() =>
            Interlocked.Exchange(ref _owner, null)?.Unbind(_expected);
    }
}

internal interface INearbyWorldDiagnosticSource
{
    Vector3 ObserverPosition { get; }

    int LiveCenterX { get; }

    int LiveCenterY { get; }

    int TotalShadowObjects { get; }

    IReadOnlyList<WorldEntity> WorldEntities { get; }

    IEnumerable<ShadowEntry> ShadowEntries { get; }
}

internal sealed class RuntimeNearbyWorldDiagnosticSource : INearbyWorldDiagnosticSource
{
    private readonly ILocalPlayerModeSource _mode;
    private readonly IRuntimeLocalPlayerControllerSource _player;
    private readonly CameraController _camera;
    private readonly LiveWorldOriginState _origin;
    private readonly GpuWorldState _world;
    private readonly PhysicsEngine _physics;

    public RuntimeNearbyWorldDiagnosticSource(
        ILocalPlayerModeSource mode,
        IRuntimeLocalPlayerControllerSource player,
        CameraController camera,
        LiveWorldOriginState origin,
        GpuWorldState world,
        PhysicsEngine physics)
    {
        _mode = mode ?? throw new ArgumentNullException(nameof(mode));
        _player = player ?? throw new ArgumentNullException(nameof(player));
        _camera = camera ?? throw new ArgumentNullException(nameof(camera));
        _origin = origin ?? throw new ArgumentNullException(nameof(origin));
        _world = world ?? throw new ArgumentNullException(nameof(world));
        _physics = physics ?? throw new ArgumentNullException(nameof(physics));
    }

    public Vector3 ObserverPosition
    {
        get
        {
            if (_mode.IsPlayerMode && _player.Controller is { } player)
                return player.Position;

            Matrix4x4.Invert(_camera.Active.View, out Matrix4x4 inverseView);
            return new Vector3(
                inverseView.M41,
                inverseView.M42,
                inverseView.M43);
        }
    }

    public int LiveCenterX => _origin.CenterX;

    public int LiveCenterY => _origin.CenterY;

    public int TotalShadowObjects => _physics.ShadowObjects.TotalRegistered;

    public IReadOnlyList<WorldEntity> WorldEntities => _world.Entities;

    public IEnumerable<ShadowEntry> ShadowEntries =>
        _physics.ShadowObjects.AllEntriesForDebug();
}

internal sealed class NearbyWorldDiagnosticDumper
{
    private const float NearbyDistance = 15f;
    private readonly INearbyWorldDiagnosticSource _source;
    private readonly Action<string> _log;

    public NearbyWorldDiagnosticDumper(
        INearbyWorldDiagnosticSource source,
        Action<string>? log = null)
    {
        _source = source ?? throw new ArgumentNullException(nameof(source));
        _log = log ?? (_ => { });
    }

    public void Dump()
    {
        Vector3 position = _source.ObserverPosition;
        int centerX = _source.LiveCenterX;
        int centerY = _source.LiveCenterY;
        int landblockX = centerX + (int)MathF.Floor(position.X / 192f);
        int landblockY = centerY + (int)MathF.Floor(position.Y / 192f);

        _log(string.Create(
            CultureInfo.InvariantCulture,
            $"=== F3 DEBUG DUMP ===\n" +
            $"  player pos=({position.X:F2},{position.Y:F2},{position.Z:F2})\n" +
            $"  landblock=0x{(uint)((landblockX << 24) | (landblockY << 16) | 0xFFFF):X8} " +
            $"local=({position.X - (landblockX - centerX) * 192f:F2}," +
            $"{position.Y - (landblockY - centerY) * 192f:F2})\n" +
            $"  total shadow objects: {_source.TotalShadowObjects}"));

        List<WorldEntity> visibleNearby = _source.WorldEntities
            .Where(entity => HorizontalDistanceSquared(entity.Position, position)
                < NearbyDistance * NearbyDistance)
            .OrderBy(entity => (entity.Position - position).Length())
            .ToList();
        _log($"  VISIBLE entities within 15m: {visibleNearby.Count}");
        foreach (WorldEntity entity in visibleNearby.Take(12))
        {
            float distance = (entity.Position - position).Length();
            _log(
                $"    VIS  id=0x{entity.Id:X8} src=0x{entity.SourceGfxObjOrSetupId:X8} " +
                $"pos=({entity.Position.X:F2},{entity.Position.Y:F2},{entity.Position.Z:F2}) " +
                $"dist={distance:F2} scale={entity.Scale:F2}");
        }

        List<(ShadowEntry Entry, float Distance)> shadows = _source.ShadowEntries
            .Select(entry =>
            {
                float dx = entry.Position.X - position.X;
                float dy = entry.Position.Y - position.Y;
                return (Entry: entry, Distance: MathF.Sqrt(dx * dx + dy * dy));
            })
            .Where(item => item.Distance < NearbyDistance)
            .OrderBy(item => item.Distance)
            .ToList();
        _log($"  SHADOW objects within 15m: {shadows.Count}");
        foreach ((ShadowEntry entry, float distance) in shadows.Take(12))
        {
            _log(
                $"    SHAD id=0x{entry.EntityId:X8} {entry.CollisionType} " +
                $"r={entry.Radius:F2} h={entry.CylHeight:F2} " +
                $"pos=({entry.Position.X:F2},{entry.Position.Y:F2},{entry.Position.Z:F2}) " +
                $"dist={distance:F2}");
        }
    }

    private static float HorizontalDistanceSquared(Vector3 left, Vector3 right)
    {
        float dx = left.X - right.X;
        float dy = left.Y - right.Y;
        return dx * dx + dy * dy;
    }
}

internal sealed class RuntimeDiagnosticCommandController : IRuntimeDiagnosticCommands
{
    private readonly WorldEnvironmentController _environment;
    private readonly WorldSceneDebugState _sceneDebug;
    private readonly IPointerSensitivityCommands? _pointer;
    private readonly NearbyWorldDiagnosticDumper _nearby;
    private readonly Action<string>? _toast;

    public RuntimeDiagnosticCommandController(
        WorldEnvironmentController environment,
        WorldSceneDebugState sceneDebug,
        IPointerSensitivityCommands? pointer,
        NearbyWorldDiagnosticDumper nearby,
        Action<string>? toast = null)
    {
        _environment = environment ?? throw new ArgumentNullException(nameof(environment));
        _sceneDebug = sceneDebug ?? throw new ArgumentNullException(nameof(sceneDebug));
        _pointer = pointer;
        _nearby = nearby ?? throw new ArgumentNullException(nameof(nearby));
        _toast = toast;
    }

    public bool Handle(InputAction action)
    {
        switch (action)
        {
            case InputAction.AcdreamToggleCollisionWires:
                ToggleCollisionWireframes();
                return true;
            case InputAction.AcdreamDumpNearby:
                _nearby.Dump();
                return true;
            case InputAction.AcdreamCycleTimeOfDay:
                CycleTimeOfDay();
                return true;
            case InputAction.AcdreamSensitivityDown:
                if (_pointer is not null)
                {
                    string message = _pointer.AdjustSensitivity(1f / 1.2f);
                    _toast?.Invoke(message);
                }
                return true;
            case InputAction.AcdreamSensitivityUp:
                if (_pointer is not null)
                {
                    string message = _pointer.AdjustSensitivity(1.2f);
                    _toast?.Invoke(message);
                }
                return true;
            case InputAction.AcdreamCycleWeather:
                CycleWeather();
                return true;
            default:
                return false;
        }
    }

    public void CycleTimeOfDay()
    {
        string message = _environment.CycleTimeOfDay();
        _toast?.Invoke(message);
    }

    public void CycleWeather()
    {
        string message = _environment.CycleWeather();
        _toast?.Invoke(message);
    }

    public void ToggleCollisionWireframes()
    {
        bool visible = _sceneDebug.ToggleCollisionWireframes();
        _toast?.Invoke($"Collision wireframes {(visible ? "ON" : "OFF")}");
    }

    internal static bool IsDiagnosticAction(InputAction action) => action is
        InputAction.AcdreamToggleCollisionWires
        or InputAction.AcdreamDumpNearby
        or InputAction.AcdreamCycleTimeOfDay
        or InputAction.AcdreamSensitivityDown
        or InputAction.AcdreamSensitivityUp
        or InputAction.AcdreamCycleWeather;
}
