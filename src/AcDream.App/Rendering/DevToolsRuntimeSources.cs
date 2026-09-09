using System.Numerics;
using AcDream.App.Input;
using AcDream.App.Physics;
using AcDream.App.Streaming;
using AcDream.App.World;
using AcDream.Core.Lighting;
using AcDream.Core.Physics;
using AcDream.Core.Vfx;

namespace AcDream.App.Rendering;

internal interface IRenderFrameDiagnosticsSnapshotSource
{
    RenderFrameDiagnosticsSnapshot Snapshot { get; }
}

internal sealed class DeferredRenderFrameDiagnosticsSource
    : IRenderFrameDiagnosticsSnapshotSource
{
    private IRenderFrameDiagnosticsSnapshotSource? _target;
    private bool _deactivated;

    public RenderFrameDiagnosticsSnapshot Snapshot =>
        !_deactivated && _target is { } target
            ? target.Snapshot
            : RenderFrameDiagnosticsSnapshot.Initial;

    public void Bind(IRenderFrameDiagnosticsSnapshotSource target)
    {
        ArgumentNullException.ThrowIfNull(target);
        ObjectDisposedException.ThrowIf(_deactivated, this);
        if (_target is not null && !ReferenceEquals(_target, target))
            throw new InvalidOperationException(
                "Render-frame diagnostics are already bound.");
        _target = target;
    }

    public IDisposable BindOwned(IRenderFrameDiagnosticsSnapshotSource target)
    {
        Bind(target);
        return new Binding(this, target);
    }

    public void Unbind(IRenderFrameDiagnosticsSnapshotSource target)
    {
        ArgumentNullException.ThrowIfNull(target);
        if (ReferenceEquals(_target, target))
            _target = null;
    }

    public void Deactivate()
    {
        _deactivated = true;
        _target = null;
    }

    private sealed class Binding : IDisposable
    {
        private DeferredRenderFrameDiagnosticsSource? _owner;
        private readonly IRenderFrameDiagnosticsSnapshotSource _target;

        public Binding(
            DeferredRenderFrameDiagnosticsSource owner,
            IRenderFrameDiagnosticsSnapshotSource target)
        {
            _owner = owner;
            _target = target;
        }

        public void Dispose() =>
            Interlocked.Exchange(ref _owner, null)?.Unbind(_target);
    }
}

internal interface ICanonicalWorldEntityCountSource
{
    int EntityCount { get; }
}

internal sealed class DeferredCanonicalWorldEntityCountSource
    : ICanonicalWorldEntityCountSource
{
    private GpuWorldState? _target;
    private bool _deactivated;

    public int EntityCount => !_deactivated ? _target?.Entities.Count ?? 0 : 0;

    public void Bind(GpuWorldState target)
    {
        ArgumentNullException.ThrowIfNull(target);
        ObjectDisposedException.ThrowIf(_deactivated, this);
        if (_target is not null && !ReferenceEquals(_target, target))
            throw new InvalidOperationException(
                "The canonical world entity-count source is already bound.");
        _target = target;
    }

    public IDisposable BindOwned(GpuWorldState target)
    {
        Bind(target);
        return new Binding(this, target);
    }

    public void Unbind(GpuWorldState target)
    {
        ArgumentNullException.ThrowIfNull(target);
        if (ReferenceEquals(_target, target))
            _target = null;
    }

    public void Deactivate()
    {
        _deactivated = true;
        _target = null;
    }

    private sealed class Binding : IDisposable
    {
        private DeferredCanonicalWorldEntityCountSource? _owner;
        private readonly GpuWorldState _target;

        public Binding(
            DeferredCanonicalWorldEntityCountSource owner,
            GpuWorldState target)
        {
            _owner = owner;
            _target = target;
        }

        public void Dispose() =>
            Interlocked.Exchange(ref _owner, null)?.Unbind(_target);
    }
}

internal interface IDevToolsPlayerModeCommands
{
    void ToggleFlyOrChase();
}

internal interface IDevToolsPlayerModeTarget
{
    void ToggleFlyOrChase();
}

internal sealed class DeferredDevToolsPlayerModeCommands
    : IDevToolsPlayerModeCommands
{
    private IDevToolsPlayerModeTarget? _target;
    private bool _deactivated;

    public void Bind(IDevToolsPlayerModeTarget target)
    {
        ArgumentNullException.ThrowIfNull(target);
        ObjectDisposedException.ThrowIf(_deactivated, this);
        if (_target is not null && !ReferenceEquals(_target, target))
            throw new InvalidOperationException(
                "Developer player-mode commands are already bound.");
        _target = target;
    }

    public IDisposable BindOwned(IDevToolsPlayerModeTarget target)
    {
        ArgumentNullException.ThrowIfNull(target);
        ObjectDisposedException.ThrowIf(_deactivated, this);
        if (_target is not null)
        {
            throw new InvalidOperationException(
                "Developer player-mode commands are already bound.");
        }

        _target = target;
        return new Binding(this, target);
    }

    public void Unbind(IDevToolsPlayerModeTarget target)
    {
        ArgumentNullException.ThrowIfNull(target);
        if (ReferenceEquals(_target, target))
            _target = null;
    }

    public void Deactivate()
    {
        _deactivated = true;
        _target = null;
    }

    public void ToggleFlyOrChase()
    {
        if (!_deactivated)
            _target?.ToggleFlyOrChase();
    }

    private sealed class Binding : IDisposable
    {
        private DeferredDevToolsPlayerModeCommands? _owner;
        private readonly IDevToolsPlayerModeTarget _expected;

        public Binding(
            DeferredDevToolsPlayerModeCommands owner,
            IDevToolsPlayerModeTarget expected)
        {
            _owner = owner;
            _expected = expected;
        }

        public void Dispose() =>
            Interlocked.Exchange(ref _owner, null)?.Unbind(_expected);
    }
}

internal interface IDevToolsRuntimeFacts
{
    Vector3 PlayerPosition { get; }
    float PlayerHeadingDegrees { get; }
    uint PlayerCellId { get; }
    bool PlayerOnGround { get; }
    bool InPlayerMode { get; }
    bool InFlyMode { get; }
    float VerticalVelocity { get; }
    int EntityCount { get; }
    int AnimatedCount { get; }
    int VisibleLandblocks { get; }
    int TotalLandblocks { get; }
    int ShadowObjectCount { get; }
    float NearestObjectDistance { get; }
    string NearestObjectLabel { get; }
    bool Colliding { get; }
    bool CollisionWireframesVisible { get; }
    int StreamingRadius { get; }
    float MouseSensitivity { get; }
    float ChaseDistance { get; }
    bool RmbOrbitHeld { get; }
    string HourName { get; }
    float DayFraction { get; }
    string Weather { get; }
    int ActiveLights { get; }
    int RegisteredLights { get; }
    int ParticleCount { get; }
    float Fps { get; }
    float FrameMilliseconds { get; }
}

internal sealed class DevToolsRuntimeFacts : IDevToolsRuntimeFacts
{
    private readonly ILocalPlayerModeSource _mode;
    private readonly IRuntimeLocalPlayerControllerSource _player;
    private readonly CameraController _camera;
    private readonly ICanonicalWorldEntityCountSource _world;
    private readonly LiveEntityAnimationRuntimeView<LiveEntityAnimationState> _animations;
    private readonly IDebugVmRenderFactsSource _renderFacts;
    private readonly PhysicsEngine _physics;
    private readonly IWorldSceneDebugStateSource _sceneDebug;
    private readonly IWorldRenderRangeSource _renderRange;
    private readonly CameraPointerInputController? _pointer;
    private readonly WorldEnvironmentController _environment;
    private readonly LightManager _lights;
    private readonly ParticleSystem _particles;
    private readonly IRenderFrameDiagnosticsSnapshotSource _frame;

    public DevToolsRuntimeFacts(
        ILocalPlayerModeSource mode,
        IRuntimeLocalPlayerControllerSource player,
        CameraController camera,
        ICanonicalWorldEntityCountSource world,
        LiveEntityAnimationRuntimeView<LiveEntityAnimationState> animations,
        IDebugVmRenderFactsSource renderFacts,
        PhysicsEngine physics,
        IWorldSceneDebugStateSource sceneDebug,
        IWorldRenderRangeSource renderRange,
        CameraPointerInputController? pointer,
        WorldEnvironmentController environment,
        LightManager lights,
        ParticleSystem particles,
        IRenderFrameDiagnosticsSnapshotSource frame)
    {
        _mode = mode ?? throw new ArgumentNullException(nameof(mode));
        _player = player ?? throw new ArgumentNullException(nameof(player));
        _camera = camera ?? throw new ArgumentNullException(nameof(camera));
        _world = world ?? throw new ArgumentNullException(nameof(world));
        _animations = animations ?? throw new ArgumentNullException(nameof(animations));
        _renderFacts = renderFacts ?? throw new ArgumentNullException(nameof(renderFacts));
        _physics = physics ?? throw new ArgumentNullException(nameof(physics));
        _sceneDebug = sceneDebug ?? throw new ArgumentNullException(nameof(sceneDebug));
        _renderRange = renderRange ?? throw new ArgumentNullException(nameof(renderRange));
        _pointer = pointer;
        _environment = environment ?? throw new ArgumentNullException(nameof(environment));
        _lights = lights ?? throw new ArgumentNullException(nameof(lights));
        _particles = particles ?? throw new ArgumentNullException(nameof(particles));
        _frame = frame ?? throw new ArgumentNullException(nameof(frame));
    }

    public Vector3 PlayerPosition
    {
        get
        {
            if (_mode.IsPlayerMode && _player.Controller is { } player)
                return player.Position;
            Matrix4x4.Invert(_camera.Active.View, out Matrix4x4 inverse);
            return new Vector3(inverse.M41, inverse.M42, inverse.M43);
        }
    }

    public float PlayerHeadingDegrees
    {
        get
        {
            float degrees;
            if (_mode.IsPlayerMode && _player.Controller is { } player)
            {
                degrees = player.Yaw * (180f / MathF.PI);
            }
            else
            {
                Matrix4x4.Invert(_camera.Active.View, out Matrix4x4 inverse);
                var forward = new Vector3(-inverse.M31, -inverse.M32, -inverse.M33);
                degrees = MathF.Atan2(forward.Y, forward.X) * (180f / MathF.PI);
            }

            degrees %= 360f;
            return degrees < 0f ? degrees + 360f : degrees;
        }
    }

    public uint PlayerCellId =>
        _mode.IsPlayerMode ? _player.Controller?.CellId ?? 0u : 0u;
    public bool PlayerOnGround =>
        _mode.IsPlayerMode && _player.Controller is { IsAirborne: false };
    public bool InPlayerMode => _mode.IsPlayerMode;
    public bool InFlyMode => _camera.IsFlyMode;
    public float VerticalVelocity => _player.Controller?.VerticalVelocity ?? 0f;
    public int EntityCount => _world.EntityCount;
    public int AnimatedCount => _animations.Count;
    public int VisibleLandblocks => _renderFacts.DebugVmFacts.VisibleLandblocks;
    public int TotalLandblocks => _renderFacts.DebugVmFacts.TotalLandblocks;
    public int ShadowObjectCount => _physics.ShadowObjects.TotalRegistered;
    public float NearestObjectDistance => _renderFacts.DebugVmFacts.NearestObjectDistance;
    public string NearestObjectLabel => _renderFacts.DebugVmFacts.NearestObjectLabel;
    public bool Colliding => _renderFacts.DebugVmFacts.Colliding;
    public bool CollisionWireframesVisible => _sceneDebug.CollisionWireframesVisible;
    public int StreamingRadius => _renderRange.NearRadius;
    public float MouseSensitivity => _pointer?.ActiveSensitivity ?? 1f;
    public float ChaseDistance => _camera.Chase?.Distance ?? 0f;
    public bool RmbOrbitHeld => _pointer?.RmbOrbitHeld ?? false;
    public string HourName => _environment.WorldTime.CurrentCalendar.Hour.ToString();
    public float DayFraction => (float)_environment.WorldTime.DayFraction;
    public string Weather => _environment.Weather.Kind.ToString();
    public int ActiveLights => _lights.ActiveCount;
    public int RegisteredLights => _lights.RegisteredCount;
    public int ParticleCount => _particles.ActiveParticleCount;
    public float Fps => (float)_frame.Snapshot.Fps;
    public float FrameMilliseconds => (float)_frame.Snapshot.FrameMilliseconds;
}
