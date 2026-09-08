using System.Numerics;
using AcDream.App.Rendering.Gpu;
using AcDream.App.Rendering.Wb;
using AcDream.App.UI;
using AcDream.Core.Lighting;
using AcDream.Core.World;

namespace AcDream.App.Rendering;

internal interface IPrivateEntityViewportCamera : ICamera
{
    Vector3 Eye { get; }
}

internal sealed class PrivateEntityViewportRenderer :
    IUiViewportRenderer,
    IDisposable
{
    private const uint PrivateLandblockId = 0u;

    private readonly ICurrentGpuFrameSource _frames;

    private readonly IWorldPassScope _scope;

    private readonly WbDrawDispatcher _dispatcher;
    private readonly SceneLightingUboBinding _lightUbo;
    private readonly IWbMeshAdapter _meshAdapter;
    private readonly IPrivateEntityViewportCamera _camera;
    private readonly HashSet<uint> _animatedIds;
    private readonly string _diagnosticName;

    private readonly EntitySlot _mainSlot;

    private readonly EntitySlot? _backdropSlot;

    private readonly PrivateViewportFlightTargets _flightTargets;

    public PrivateEntityViewportRenderer(
        IWorldPassScope scope,
        IGpuDevice device,
        ICurrentGpuFrameSource frames,
        WbDrawDispatcher dispatcher,
        SceneLightingUboBinding lightUbo,
        IEntityTextureLifetime textureLifetime,
        IWbMeshAdapter meshAdapter,
        uint renderId,
        IPrivateEntityViewportCamera camera,
        string diagnosticName,
        uint? backdropRenderId = null)
    {
        if (renderId == 0u)
            throw new ArgumentOutOfRangeException(nameof(renderId));
        if (backdropRenderId == 0u)
            throw new ArgumentOutOfRangeException(nameof(backdropRenderId));

        _scope = scope ?? throw new ArgumentNullException(
            nameof(scope),
            "The viewport must publish a world pass scope to draw into.");
        ArgumentNullException.ThrowIfNull(device);
        _frames = frames ?? throw new ArgumentNullException(nameof(frames));
        _dispatcher = dispatcher ?? throw new ArgumentNullException(nameof(dispatcher));
        _lightUbo = lightUbo ?? throw new ArgumentNullException(nameof(lightUbo));
        _meshAdapter = meshAdapter
            ?? throw new ArgumentNullException(nameof(meshAdapter));
        _camera = camera ?? throw new ArgumentNullException(nameof(camera));
        _diagnosticName = string.IsNullOrWhiteSpace(diagnosticName)
            ? "creature viewport"
            : diagnosticName;
        _flightTargets = new PrivateViewportFlightTargets(
            device,
            _diagnosticName);

        IEntityTextureLifetime textureLifetimeChecked = textureLifetime
            ?? throw new ArgumentNullException(nameof(textureLifetime));

        _mainSlot = new EntitySlot(_meshAdapter, textureLifetimeChecked, renderId, _diagnosticName);
        _backdropSlot = backdropRenderId is uint backdropId
            ? new EntitySlot(_meshAdapter, textureLifetimeChecked, backdropId, _diagnosticName + " backdrop")
            : null;

        _animatedIds = backdropRenderId is uint animatedBackdropId
            ? [renderId, animatedBackdropId]
            : [renderId];
    }

    public bool TextureIsBottomUp => false;

    public void SetEntity(WorldEntity? entity)
    {
        _mainSlot.Set(entity);
        if (entity is null)
        {
            _flightTargets.InvalidateCompletedScenes();
        }
    }

    public bool Prepare()
    {
        if (!_mainSlot.PrepareForDraw()
            || !(_backdropSlot?.PrepareForDraw() ?? true))
        {
            return false;
        }
        WorldEntity? entity = _mainSlot.Entity;
        if (entity is null || entity.MeshRefs.Count == 0)
        {
            return false;
        }
        IReadOnlyList<WorldEntity> entities = BuildDrawEntities(
            _backdropSlot?.Entity,
            entity);
        return _dispatcher.PreparePrivateEntityResources(entities);
    }

    public void SetBackdrop(WorldEntity? entity)
    {
        if (_backdropSlot is null)
        {
            throw new InvalidOperationException(
                $"The {_diagnosticName} was not constructed with a "
                + "backdropRenderId and cannot render a second (backdrop) entity.");
        }

        _backdropSlot.Set(entity);
    }

    public uint Render(int width, int height)
    {
        if (width <= 0 || height <= 0)
            return 0u;

        IGpuFrame frame = _frames.CurrentFrame
            ?? throw new InvalidOperationException(
                $"The {_diagnosticName} requires an open IGpuFrame (see GpuDeviceFrameLifetime).");
        int frameSlot = frame.SlotIndex;

        bool mainReady = _mainSlot.PrepareForDraw();
        bool backdropReady = _backdropSlot?.PrepareForDraw() ?? true;
        if (!mainReady || !backdropReady)
        {
            return _mainSlot.Entity is not null
                    ? _flightTargets.CompletedHandle(frameSlot)
                    : 0u;
        }

        WorldEntity? entity = _mainSlot.Entity;
        if (entity is null || entity.MeshRefs.Count == 0)
            return 0u;

        IReadOnlyList<WorldEntity> drawEntities = BuildDrawEntities(
            _backdropSlot?.Entity,
            entity);
        if (!_dispatcher.PreparePrivateEntityResources(drawEntities))
            return _flightTargets.CompletedHandle(frameSlot);

        PrivateViewportFlightTargets.TargetSlot? targetSlot =
            _flightTargets.Ensure(frameSlot, width, height);
        if (targetSlot is null)
            return 0u;
        _camera.Aspect = width / (float)height;

        using IGpuPassEncoder encoder = frame.BeginPass(new GpuPassDescription
        {
            Name = _diagnosticName,
            Color = new GpuColorAttachment(
                Target: targetSlot.Target,
                Load: GpuLoadOp.Clear,
                Store: GpuStoreOp.Store,
                ClearColor: Vector4.Zero),
            Depth = new GpuDepthAttachment(
                Load: GpuLoadOp.Clear,
                Store: GpuStoreOp.DontCare,
                ClearDepth: 1f,
                ClearStencil: 0),
            SampleCount = 1,
        });

        using IDisposable publication = _scope.Publish(encoder);

        UploadCreatureLight();

        var entries =
            new (uint, Vector3, Vector3, IReadOnlyList<WorldEntity>,
                IReadOnlyDictionary<uint, WorldEntity>?)[]
            {
                (
                    PrivateLandblockId,
                    new Vector3(-1024f),
                    new Vector3(1024f),
                    drawEntities,
                    null),
            };

        _dispatcher.NextClassicDrawIsPrivatePass = true;
        _dispatcher.Draw(
            _camera,
            entries,
            frustum: null,
            neverCullLandblockId: PrivateLandblockId,
            visibleCellIds: null,
            animatedEntityIds: _animatedIds);
        targetSlot.HasRenderedScene = true;
        return UiTextureTableHandle.FromSlot(targetSlot.TextureSlot);
    }

    internal static IReadOnlyList<WorldEntity> BuildDrawEntities(WorldEntity? backdrop, WorldEntity main) =>
        backdrop is not null && backdrop.MeshRefs.Count > 0
            ? [backdrop, main]
            : [main];

    private void UploadCreatureLight()
    {
        Vector3 direction = Vector3.Normalize(new Vector3(0.3f, 1.9f, 0.65f));
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
            CameraAndTime = new Vector4(_camera.Eye, 0f),
        });
    }

    public void Dispose()
    {
        List<Exception>? failures = null;
        try
        {
            _mainSlot.Dispose();
        }
        catch (Exception error)
        {
            (failures ??= []).Add(error);
        }
        try
        {
            _backdropSlot?.Dispose();
        }
        catch (Exception error)
        {
            (failures ??= []).Add(error);
        }
        try
        {
            _flightTargets.Dispose();
        }
        catch (Exception error)
        {
            (failures ??= []).Add(error);
        }

        if (failures is not null)
        {
            throw new AggregateException(
                $"The {_diagnosticName} resources did not fully release.",
                failures);
        }
    }

    internal sealed class PrivateViewportFlightTargets : IDisposable
    {
        internal sealed class TargetSlot(
            IGpuRenderTarget target,
            GpuTextureSlot textureSlot)
        {
            internal IGpuRenderTarget Target { get; } = target;
            internal GpuTextureSlot TextureSlot { get; } = textureSlot;
            internal bool HasRenderedScene { get; set; }
        }

        private readonly IGpuDevice _device;
        private readonly string _diagnosticName;
        private readonly List<TargetSlot?> _slots = [];
        private int _width;
        private int _height;
        private bool _disposed;

        internal PrivateViewportFlightTargets(
            IGpuDevice device,
            string diagnosticName)
        {
            _device = device ?? throw new ArgumentNullException(nameof(device));
            _diagnosticName = string.IsNullOrWhiteSpace(diagnosticName)
                ? "creature viewport"
                : diagnosticName;
        }

        internal int AllocatedSlotCount =>
            _slots.Count(static slot => slot is not null);

        internal TargetSlot? Ensure(int frameSlot, int width, int height)
        {
            ObjectDisposedException.ThrowIf(_disposed, this);
            ArgumentOutOfRangeException.ThrowIfNegative(frameSlot);
            ArgumentOutOfRangeException.ThrowIfNegativeOrZero(width);
            ArgumentOutOfRangeException.ThrowIfNegativeOrZero(height);

            if (_width != 0 && (_width != width || _height != height))
                ReleaseAll();

            while (_slots.Count <= frameSlot)
                _slots.Add(null);
            if (_slots[frameSlot] is { } existing)
                return existing;

            IGpuRenderTarget target;
            try
            {
                target = _device.CreateRenderTarget(
                    new GpuRenderTargetDescription(
                        $"{_diagnosticName}-flight-{frameSlot}",
                        width,
                        height,
                        GpuTextureFormat.Rgba8UnormRenderTarget,
                        // Depth24Stencil8, as the original private viewport
                        // renderbuffer was. Nothing samples this attachment.
                        GpuTextureFormat.Depth24Stencil8,
                        SampleCount: 1));
            }
            catch (Exception failure)
            {
                Console.WriteLine(
                    $"[{_diagnosticName}] render target unavailable "
                    + $"({width}x{height}, flight {frameSlot}): {failure.Message}");
                return null;
            }

            try
            {
                // The device de-duplicates immutable samplers. Retained UI
                // blits this target through its ordinary texture-table entry.
                IGpuSampler sampler = _device.CreateSampler(
                    GpuSamplerDescription.WorldClamp);
                GpuTextureSlot textureSlot = _device.RegisterTexture(
                    target.ColorTexture,
                    sampler);
                var created = new TargetSlot(target, textureSlot);
                _slots[frameSlot] = created;
                _width = width;
                _height = height;
                return created;
            }
            catch
            {
                target.Dispose();
                throw;
            }
        }

        internal uint CompletedHandle(int frameSlot)
        {
            if ((uint)frameSlot >= (uint)_slots.Count
                || _slots[frameSlot] is not { HasRenderedScene: true } slot)
            {
                return 0u;
            }

            return UiTextureTableHandle.FromSlot(slot.TextureSlot);
        }

        internal void InvalidateCompletedScenes()
        {
            for (int i = 0; i < _slots.Count; i++)
            {
                if (_slots[i] is { } slot)
                    slot.HasRenderedScene = false;
            }
        }

        private void ReleaseAll()
        {
            List<Exception>? failures = null;
            for (int i = 0; i < _slots.Count; i++)
            {
                TargetSlot? slot = _slots[i];
                if (slot is null)
                    continue;
                try
                {
                    _device.ReleaseTextureSlot(slot.TextureSlot);
                }
                catch (Exception error)
                {
                    (failures ??= []).Add(error);
                }
                try
                {
                    slot.Target.Dispose();
                }
                catch (Exception error)
                {
                    (failures ??= []).Add(error);
                }
            }
            _slots.Clear();
            _width = 0;
            _height = 0;
            if (failures is { Count: > 0 })
                throw new AggregateException(failures);
        }

        public void Dispose()
        {
            if (_disposed)
                return;
            _disposed = true;
            ReleaseAll();
        }
    }

    internal sealed class EntitySlot
    {
        private sealed class MeshSnapshot
        {
            private readonly ulong[] _ownedIds;
            private readonly int _drawMeshCount;

            private MeshSnapshot(ulong[] ownedIds, int drawMeshCount)
            {
                _ownedIds = ownedIds;
                _drawMeshCount = drawMeshCount;
            }

            public IReadOnlyList<ulong> OwnedIds => _ownedIds;

            public static MeshSnapshot Capture(WorldEntity entity)
            {
                int drawMeshCount = entity.MeshRefs.Count;
                var ids = new ulong[drawMeshCount + entity.PartOverrides.Count];
                for (int i = 0; i < drawMeshCount; i++)
                    ids[i] = entity.MeshRefs[i].GfxObjId;
                for (int i = 0; i < entity.PartOverrides.Count; i++)
                    ids[drawMeshCount + i] = entity.PartOverrides[i].GfxObjId;
                return new MeshSnapshot(ids, drawMeshCount);
            }

            public bool Matches(WorldEntity entity)
            {
                if (entity.MeshRefs.Count != _drawMeshCount
                    || entity.PartOverrides.Count != _ownedIds.Length - _drawMeshCount)
                {
                    return false;
                }

                for (int i = 0; i < _drawMeshCount; i++)
                    if (entity.MeshRefs[i].GfxObjId != _ownedIds[i])
                        return false;
                for (int i = 0; i < entity.PartOverrides.Count; i++)
                    if (entity.PartOverrides[i].GfxObjId != _ownedIds[_drawMeshCount + i])
                        return false;
                return true;
            }

            public bool AreDrawMeshesReady(IWbMeshAdapter adapter)
            {
                for (int i = 0; i < _drawMeshCount; i++)
                {
                    ulong id = _ownedIds[i];
                    if (id == 0u || !adapter.IsRenderDataReady(id))
                        return false;
                }
                return true;
            }
        }

        private sealed record PendingEntity(
            WorldEntity Entity,
            MeshSnapshot Snapshot,
            SyntheticEntityMeshReferenceOwner MeshReferences);

        private readonly IWbMeshAdapter _meshAdapter;
        private readonly FixedEntityTextureOwnerLease _textureOwnerLease;
        private readonly string _diagnosticName;
        private readonly List<SyntheticEntityMeshReferenceOwner> _retiringMeshReferences = [];

        private SyntheticEntityMeshReferenceOwner? _meshReferences;
        private MeshSnapshot? _meshSnapshot;
        private PendingEntity? _pending;

        public EntitySlot(
            IWbMeshAdapter meshAdapter,
            IEntityTextureLifetime textureLifetime,
            uint ownerLocalId,
            string diagnosticName)
        {
            _meshAdapter = meshAdapter;
            _textureOwnerLease = new FixedEntityTextureOwnerLease(textureLifetime, ownerLocalId);
            _diagnosticName = diagnosticName;
        }

        public WorldEntity? Entity { get; private set; }

        internal bool HasPending => _pending is not null;

        public void Set(WorldEntity? entity)
        {
            ReleaseRetiringMeshReferences();

            if (entity is null)
            {
                Clear();
                return;
            }

            if (ReferenceEquals(Entity, entity) && _pending is null)
                return;

            if (ReferenceEquals(Entity, entity)
                && _meshSnapshot?.Matches(entity) == true)
            {
                ReleasePending();
                return;
            }

            if (_pending is { } pending
                && ReferenceEquals(pending.Entity, entity)
                && pending.Snapshot.Matches(entity))
            {
                return;
            }

            Stage(entity);
        }

        public bool PrepareForDraw()
        {
            ReleaseRetiringMeshReferences();

            if (_pending is { } pending)
            {
                if (!pending.Snapshot.Matches(pending.Entity))
                    Stage(pending.Entity);
            }
            else if (Entity is { } current
                && _meshSnapshot?.Matches(current) != true)
            {
                Stage(current);
            }

            pending = _pending;
            if (pending is null)
                return true;
            if (!pending.Snapshot.AreDrawMeshesReady(_meshAdapter))
                return false;

            PromotePending(pending);
            return true;
        }

        private void Stage(WorldEntity entity)
        {
            MeshSnapshot snapshot = MeshSnapshot.Capture(entity);
            var replacement = new SyntheticEntityMeshReferenceOwner(
                _meshAdapter,
                snapshot.OwnedIds);
            try
            {
                replacement.Acquire();
            }
            catch (Exception acquisitionFailure)
            {
                try
                {
                    replacement.Dispose();
                }
                catch (Exception rollbackFailure)
                {
                    _retiringMeshReferences.Add(replacement);
                    throw new AggregateException(
                        $"The {_diagnosticName} candidate mesh acquisition failed "
                        + "and its rollback did not converge.",
                        acquisitionFailure,
                        rollbackFailure);
                }

                System.Runtime.ExceptionServices.ExceptionDispatchInfo
                    .Capture(acquisitionFailure)
                    .Throw();
                throw new InvalidOperationException("Unreachable exception dispatch path.");
            }

            PendingEntity? previous = _pending;
            _pending = new PendingEntity(entity, snapshot, replacement);
            if (previous is not null)
                Retire(previous.MeshReferences);
        }

        private void PromotePending(PendingEntity pending)
        {
            _textureOwnerLease.Replace(hasReplacement: true);

            SyntheticEntityMeshReferenceOwner? previous = _meshReferences;
            _meshReferences = pending.MeshReferences;
            _meshSnapshot = pending.Snapshot;
            Entity = pending.Entity;
            _pending = null;

            if (previous is not null)
                Retire(previous);
        }

        private void Clear()
        {
            if (Entity is null && _pending is null)
                return;

            ReleasePending();
            _textureOwnerLease.Replace(hasReplacement: false);

            SyntheticEntityMeshReferenceOwner? previous = _meshReferences;
            _meshReferences = null;
            _meshSnapshot = null;
            Entity = null;
            if (previous is not null)
                Retire(previous);
        }

        private void ReleasePending()
        {
            PendingEntity? pending = _pending;
            if (pending is null)
                return;
            _pending = null;
            Retire(pending.MeshReferences);
        }

        private void Retire(SyntheticEntityMeshReferenceOwner owner)
        {
            try
            {
                owner.Dispose();
            }
            catch
            {
                _retiringMeshReferences.Add(owner);
                throw;
            }
        }

        public void Dispose()
        {
            Entity = null;
            _meshSnapshot = null;
            if (_meshReferences is { } current)
            {
                _meshReferences = null;
                _retiringMeshReferences.Add(current);
            }
            if (_pending is { } pending)
            {
                _pending = null;
                _retiringMeshReferences.Add(pending.MeshReferences);
            }

            List<Exception>? failures = null;
            try
            {
                _textureOwnerLease.Dispose();
            }
            catch (Exception error)
            {
                (failures ??= []).Add(error);
            }
            try
            {
                ReleaseRetiringMeshReferences();
            }
            catch (Exception error)
            {
                (failures ??= []).Add(error);
            }

            if (failures is not null)
            {
                throw new AggregateException(
                    $"The {_diagnosticName} resources did not fully release.",
                    failures);
            }
        }

        private void ReleaseRetiringMeshReferences()
        {
            List<Exception>? failures = null;
            for (int i = _retiringMeshReferences.Count - 1; i >= 0; i--)
            {
                SyntheticEntityMeshReferenceOwner owner =
                    _retiringMeshReferences[i];
                try
                {
                    owner.Dispose();
                    if (owner.IsDisposed)
                        _retiringMeshReferences.RemoveAt(i);
                }
                catch (Exception error)
                {
                    (failures ??= []).Add(error);
                }
            }

            if (failures is not null)
            {
                throw new AggregateException(
                    $"One or more {_diagnosticName} mesh owners remain pending.",
                    failures);
            }
        }
    }
}
