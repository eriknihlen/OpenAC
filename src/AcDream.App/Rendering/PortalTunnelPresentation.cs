using System.Numerics;
using AcDream.App.Rendering.Gpu;
using AcDream.App.Rendering.Wb;
using AcDream.App.Streaming;
using AcDream.App.UI;
using AcDream.Content.Vfx;
using AcDream.Core.Lighting;
using AcDream.Core.Meshing;
using AcDream.Core.Physics;
using AcDream.Core.Physics.Motion;
using AcDream.Core.World;
using DatReaderWriter;
using AcDream.Content;
using DatReaderWriter.DBObjs;
using DatReaderWriter.Types;

namespace AcDream.App.Rendering;

public sealed class PortalTunnelPresentation : IDisposable
{
    public const uint SetupClientEnum = 0x10000001u;
    public const uint AnimationClientEnum = 0x10000002u;
    public const uint ClientEnumCategory = 7u;

    internal static readonly Vector4 RetailPortalSpaceClearColor = new(0f, 0f, 0f, 1f);

    private const uint SyntheticEntityId = 0xFFFF_FF01u;
    private const uint SyntheticLandblockId = 0u;
    private const float RotationDurationMin = 0.6f;
    private const float RotationDurationMax = 1.8f;

    private static readonly HashSet<uint> AnimatedIds = new() { SyntheticEntityId };

    private readonly IWorldPassScope _scope;

    /// <summary>The frame this presentation's own pass is opened on.</summary>
    private readonly ICurrentGpuFrameSource _frames;

    private readonly WbDrawDispatcher _dispatcher;
    private readonly SceneLightingUboBinding _lightUbo;
    private readonly Setup _setup;
    private readonly uint _setupDid;
    private readonly uint _animationDid;
    private readonly CSequence _sequence;
    private readonly PortalAnimationHookQueue _animationHooks;
    private readonly WorldEntity _entity;
    private readonly PortalTunnelCamera _camera = new();
    private readonly Random _random;
    private readonly Action<string>? _displayNotice;
    private readonly SyntheticEntityMeshReferenceOwner _meshReferences;

    private bool _visible;
    private double _rotationElapsed;
    private double _rotationDuration;
    private float _rotationStartAngle;
    private float _rotationEndAngle;
    private float _rotationCurrentAngle;
    private bool _waitCueVisible;
    private bool _probeFreezeReached;
    private bool _disposeRequested;
    private bool _disposing;
    private bool _disposed;

    private PortalTunnelPresentation(
        IWorldPassScope scope,
        ICurrentGpuFrameSource frames,
        WbDrawDispatcher dispatcher,
        SceneLightingUboBinding lightUbo,
        IWbMeshAdapter meshAdapter,
        Setup setup,
        uint setupDid,
        uint animationDid,
        IAnimationLoader animationLoader,
        IAnimationHookSink hookSink,
        Random random,
        Action<string>? displayNotice)
    {
        _scope = scope;
        _frames = frames;
        _dispatcher = dispatcher;
        _lightUbo = lightUbo;
        _setup = setup;
        _setupDid = setupDid;
        _animationDid = animationDid;
        _sequence = new CSequence(animationLoader);
        _animationHooks = new PortalAnimationHookQueue(hookSink, SyntheticEntityId);
        _sequence.HookObj = _animationHooks;
        _random = random;
        _displayNotice = displayNotice;

        _entity = new WorldEntity
        {
            Id = SyntheticEntityId,
            SourceGfxObjOrSetupId = setupDid,
            Position = Vector3.Zero,
            Rotation = Quaternion.Identity,
            MeshRefs = SetupMesh.Flatten(setup),
        };

        _meshReferences = new SyntheticEntityMeshReferenceOwner(
            meshAdapter,
            setup.Parts.Select(part => (ulong)(uint)part));
    }

    internal static PortalTunnelPresentation CreateRequired(
        IWorldPassScope scope,
        ICurrentGpuFrameSource frames,
        IDatReaderWriter dats,
        IAnimationLoader animationLoader,
        IAnimationHookSink hookSink,
        WbDrawDispatcher dispatcher,
        SceneLightingUboBinding lightUbo,
        IWbMeshAdapter meshAdapter,
        Action<string>? displayNotice = null,
        Random? random = null)
    {
        ArgumentNullException.ThrowIfNull(scope);
        ArgumentNullException.ThrowIfNull(frames);
        ArgumentNullException.ThrowIfNull(dats);
        ArgumentNullException.ThrowIfNull(animationLoader);
        ArgumentNullException.ThrowIfNull(hookSink);
        ArgumentNullException.ThrowIfNull(dispatcher);
        ArgumentNullException.ThrowIfNull(lightUbo);
        ArgumentNullException.ThrowIfNull(meshAdapter);

        uint setupDid = RetailDataIdResolver.Resolve(dats, SetupClientEnum, ClientEnumCategory);
        uint animationDid = RetailDataIdResolver.Resolve(dats, AnimationClientEnum, ClientEnumCategory);
        Setup? setup = setupDid == 0u ? null : dats.Get<Setup>(setupDid);
        Animation? animation = animationDid == 0u
            ? null
            : animationLoader.LoadAnimation(animationDid);

        EnsureRequiredAssets(
            setupDid,
            setup is not null,
            animationDid,
            animation is not null);

        return new PortalTunnelPresentation(
            scope,
            frames,
            dispatcher,
            lightUbo,
            meshAdapter,
            setup!,
            setupDid,
            animationDid,
            animationLoader,
            hookSink,
            random ?? Random.Shared,
            displayNotice);
    }

    internal static void EnsureRequiredAssets(
        uint setupDid,
        bool setupLoaded,
        uint animationDid,
        bool animationLoaded)
    {
        if (setupLoaded && animationLoaded)
            return;

        throw new InvalidOperationException(
            "[portal-space] required retail DAT assets unavailable: "
            + $"setup=0x{setupDid:X8} ({(setupLoaded ? "ok" : "missing")}), "
            + $"animation=0x{animationDid:X8} ({(animationLoaded ? "ok" : "missing")})");
    }

    public bool IsVisible => _visible;
    public int CurrentAnimationFrame => _sequence.GetCurrFrameNumber();
    public uint SetupDid => _setupDid;
    public uint AnimationDid => _animationDid;

    /// <summary>
    /// Publishes the synthetic scene's mesh ownership after the presentation
    /// itself has been stored by <c>GameWindow</c>. A partial acquisition stays
    /// reachable and is resumed or released by the same lifetime owner.
    /// </summary>
    internal void PrepareResources()
    {
        ThrowIfDisposed();
        _meshReferences.Acquire();
    }

    public void Enter()
    {
        ThrowIfDisposed();
        _animationHooks.Clear();
        _sequence.ClearAnimations();
        _sequence.AppendAnimation(new AnimData
        {
            AnimId = (QualifiedDataId<Animation>)_animationDid,
            LowFrame = 1,
            HighFrame = -1,
            Framerate = TeleportAnimSequencer.TunnelFramesPerSecond,
        });

        _rotationElapsed = 0.0;
        _rotationDuration = 0.0;
        _rotationStartAngle = 0f;
        _rotationEndAngle = 0f;
        _rotationCurrentAngle = 0f;
        _camera.DirectionDegrees = 0f;
        _waitCueVisible = false;
        _probeFreezeReached = false;
        _visible = true;
        RebuildPose();
    }

    public void Exit()
    {
        if (_disposed)
            return;
        _visible = false;
        _waitCueVisible = false;
        _probeFreezeReached = false;
        _animationHooks.Clear();
        _sequence.ClearAnimations();
    }

    public void Tick(float dt)
    {
        if (!_visible || dt < 0f)
            return;
        if (_probeFreezeReached)
            return;

        _sequence.Update(dt, frame: null);
        RebuildPose();
        _animationHooks.Drain(Vector3.Zero);
        TickRotation(dt);

        if (StreamingDiagnostics.TunnelFreezeFrame is not { } freezeFrame
            || CurrentAnimationFrame < freezeFrame)
        {
            return;
        }

        _probeFreezeReached = true;
        Console.WriteLine(
            $"[tunnel-freeze] frame={CurrentAnimationFrame} "
            + $"target={freezeFrame} setup=0x{_setupDid:X8} "
            + $"animation=0x{_animationDid:X8} state=held");
    }

    public void SetWaitCue(bool visible) => _waitCueVisible = visible;

    public void Draw(int width, int height, Matrix4x4 smartBoxProjection)
    {
        if (!_visible || width <= 0 || height <= 0 || _entity.MeshRefs.Count == 0)
            return;

        _camera.Aspect = width / (float)height;
        _camera.UseSmartBoxFov(smartBoxProjection);

        DrawRhi();
    }

    private void DrawRhi()
    {
        IGpuFrame frame = _frames.CurrentFrame
            ?? throw new InvalidOperationException(
                "Portal space requires an open IGpuFrame (see GpuDeviceFrameLifetime).");

        using IGpuPassEncoder encoder = frame.BeginPass(
            GpuPassDescription.BackbufferClear(
                "portal-space",
                RetailPortalSpaceClearColor,
                _scope.SampleCount));
        // Published AFTER the pass opens and BEFORE the light upload: publishing
        // resets the frame-global sections, and the distant light installed below
        // is the one this scene wants rather than the world's.
        using IDisposable publication = _scope.Publish(encoder);
        DrawScene();
    }

    private void DrawScene()
    {
        UploadRetailLight();
        _dispatcher.SetSceneLights(null);

        var entries = new (uint, Vector3, Vector3, IReadOnlyList<WorldEntity>, IReadOnlyDictionary<uint, WorldEntity>?)[]
        {
            (SyntheticLandblockId,
                new Vector3(-16f, -16f, -16f),
                new Vector3(16f, 16f, 16f),
                new WorldEntity[] { _entity },
                null),
        };

        _dispatcher.Draw(
            _camera,
            entries,
            frustum: null,
            neverCullLandblockId: SyntheticLandblockId,
            visibleCellIds: null,
            animatedEntityIds: AnimatedIds);
    }

    private void TickRotation(float dt)
    {
        _rotationElapsed += dt;
        if (_rotationElapsed >= _rotationDuration)
        {
            _rotationCurrentAngle = _rotationEndAngle;
            _rotationElapsed = 0.0;
            _rotationDuration = NextDouble(RotationDurationMin, RotationDurationMax);
            _rotationStartAngle = _rotationCurrentAngle;
            _rotationEndAngle = (float)NextDouble(0.0, 360.0);
            _displayNotice?.Invoke("In Portal Space - Please Wait...");
        }
        else
        {
            float t = _rotationDuration <= 0.0
                ? 1f
                : (float)(_rotationElapsed / _rotationDuration);
            float level = TeleportAnimSequencer.GetRetailAnimationLevel(t) / 1024f;
            _rotationCurrentAngle = _rotationStartAngle
                + ((_rotationEndAngle - _rotationStartAngle) * level);
        }

        _camera.DirectionDegrees = _rotationCurrentAngle;
    }

    private double NextDouble(double min, double max) => min + (_random.NextDouble() * (max - min));

    private void RebuildPose()
    {
        AnimationFrame? frame = _sequence.GetCurrAnimframe();
        _entity.MeshRefs = SetupMesh.Flatten(_setup, frame);
    }

    private void UploadRetailLight()
    {
        Vector3 direction = Vector3.Normalize(new Vector3(0.3f, -1.9f, 0.65f));
        _lightUbo.Upload(new SceneLightingUbo
        {
            Light0 = new UboLight
            {
                PosAndKind = Vector4.Zero,
                DirAndRange = new Vector4(direction, 1e9f),
                ColorAndIntensity = new Vector4(1f, 1f, 1f, 2f),
                ConeAngleEtc = Vector4.Zero,
            },
            CellAmbient = new Vector4(0.3f, 0.3f, 0.3f, 1f),
            FogParams = new Vector4(1e9f, 1e9f, 0f, 0f),
            FogColor = Vector4.Zero,
            CameraAndTime = new Vector4(PortalTunnelCamera.RetailEye, 0f),
        });
    }

    private void ThrowIfDisposed() =>
        ObjectDisposedException.ThrowIf(_disposeRequested || _disposed, this);

    private sealed class PortalAnimationHookQueue : IAnimHookQueue
    {
        private readonly IAnimationHookSink _sink;
        private readonly uint _ownerId;
        private readonly List<AnimationHook> _pending = new();

        public PortalAnimationHookQueue(IAnimationHookSink sink, uint ownerId)
        {
            _sink = sink;
            _ownerId = ownerId;
        }

        public void AddAnimHook(AnimationHook hook) => _pending.Add(hook);

        public void AddAnimDoneHook()
        {
        }

        public void Drain(Vector3 worldPosition)
        {
            for (int i = 0; i < _pending.Count; i++)
                _sink.OnHook(_ownerId, worldPosition, _pending[i]);
            _pending.Clear();
        }

        public void Clear() => _pending.Clear();
    }

    public void Dispose()
    {
        if (_disposed || _disposing)
            return;

        _disposeRequested = true;
        _disposing = true;
        try
        {
            _visible = false;
            _waitCueVisible = false;
            _animationHooks.Clear();
            _sequence.ClearAnimations();
            _meshReferences.Dispose();
            if (_meshReferences.IsDisposed)
            {
                _disposed = true;
            }
        }
        finally
        {
            _disposing = false;
        }
    }
}
