using System.Numerics;
using AcDream.App.Rendering.Vfx;
using AcDream.App.World;
using AcDream.Core.Physics;
using AcDream.Core.Physics.Motion;
using AcDream.Core.World;
using DatReaderWriter.DBObjs;
using DatReaderWriter.Types;

namespace AcDream.App.Rendering;

internal sealed class RetailStaticAnimatingObjectScheduler : ILiveStaticPartFrameSource
{
    private const double FrameEpsilon = 0.000199999995;
    private const float MaximumElapsed = 2f;

    private const double DatStaticOmegaReferenceHz = 30d;

    private sealed class Owner
    {
        public required WorldEntity Entity;
        public required Setup Setup;
        public required AnimationSequencer? Sequencer;
        public required uint[] PartGfxIds;
        public required IReadOnlyDictionary<uint, uint>?[] SurfaceOverrides;
        public required bool[] PartAvailable;
        public PhysicsBody? Body;

        public Vector3 Omega;
        public AnimationSequencer? PendingProcessHooks;
        public ulong PendingResidencyVersion;
        public readonly List<PartTransform> PreparedLivePartFrames = new();
        public bool HasPreparedLivePartFrames;
        public AnimationSequencer? PreparedFrameSequencer;
        public ulong PreparedPresentationRevision;
        public LiveEntityAnimationState? LiveAnimation;
        public double ElapsedSinceUpdate;
        public readonly Frame RootFrameScratch = new();
        public readonly List<MeshRef> MeshRefs = new();
        public readonly List<Matrix4x4> PartPoses = new();
        public readonly List<Matrix4x4> VisualPartPoses = new();
    }

    private readonly IAnimationLoader _animationLoader;
    private readonly Action<uint, AnimationSequencer> _captureHooks;
    private readonly Action<WorldEntity, IReadOnlyList<Matrix4x4>, IReadOnlyList<bool>>
        _publishPartPoses;
    private readonly Func<WorldEntity, bool> _isResident;
    private readonly Action<WorldEntity, PhysicsBody> _commitLiveRoot;
    private readonly Func<WorldEntity, ulong> _residencyVersion;
    private readonly Func<WorldEntity,
        (LiveEntityAnimationState Animation, PhysicsBody Body)?>?
        _resolveLiveOwner;
    private readonly Dictionary<uint, Owner> _owners = new();
    private readonly List<Owner> _snapshot = new();
    private readonly List<Owner> _hookSnapshot = new();

    public RetailStaticAnimatingObjectScheduler(
        IAnimationLoader animationLoader,
        Action<uint, AnimationSequencer> captureHooks,
        Action<WorldEntity, IReadOnlyList<Matrix4x4>, IReadOnlyList<bool>> publishPartPoses,
        Func<WorldEntity, bool>? isResident = null,
        Action<WorldEntity, PhysicsBody>? commitLiveRoot = null,
        Func<WorldEntity, ulong>? residencyVersion = null,
        Func<WorldEntity,
            (LiveEntityAnimationState Animation, PhysicsBody Body)?>?
            resolveLiveOwner = null)
    {
        _animationLoader = animationLoader
            ?? throw new ArgumentNullException(nameof(animationLoader));
        _captureHooks = captureHooks
            ?? throw new ArgumentNullException(nameof(captureHooks));
        _publishPartPoses = publishPartPoses
            ?? throw new ArgumentNullException(nameof(publishPartPoses));
        _isResident = isResident ?? (_ => true);
        _commitLiveRoot = commitLiveRoot ?? ((_, _) => { });
        _residencyVersion = residencyVersion ?? (_ => 0UL);
        _resolveLiveOwner = resolveLiveOwner;
    }

    internal int Count => _owners.Count;

    public bool Register(WorldEntity entity, ScriptActivationInfo info)
    {
        ArgumentNullException.ThrowIfNull(entity);
        ArgumentNullException.ThrowIfNull(info);
        if (!info.UsesStaticAnimationWorkset
            || info.Setup is not { } setup
            || info.DefaultAnimationId == 0)
        {
            return false;
        }

        if (_owners.TryGetValue(entity.Id, out Owner? retained))
        {
            if (ReferenceEquals(retained.Entity, entity))
                return true;
            throw new InvalidOperationException(
                $"DAT-static animation owner 0x{entity.Id:X8} is already registered.");
        }

        AnimationSequencer? sequencer = null;
        if (entity.ServerGuid == 0)
        {
            sequencer = new AnimationSequencer(
                setup,
                new MotionTable(),
                _animationLoader);
            if (!sequencer.HasCurrentNode)
                return false;
        }

        int partCount = setup.Parts.Count;
        uint[] gfxIds = BuildPartGfxIds(setup, entity);
        bool[] available = new bool[partCount];
        if (info.PartAvailability is { } supplied)
        {
            for (int i = 0; i < partCount && i < supplied.Count; i++)
                available[i] = supplied[i];
        }
        var surfaces = MatchSurfaceOverrides(entity, gfxIds, available);
        _owners.Add(entity.Id, new Owner
        {
            Entity = entity,
            Setup = setup,
            Sequencer = sequencer,
            PartGfxIds = gfxIds,
            SurfaceOverrides = surfaces,
            PartAvailable = available,
        });
        return true;
    }

    public void Rebind(WorldEntity entity, ScriptActivationInfo info)
    {
        ArgumentNullException.ThrowIfNull(entity);
        ArgumentNullException.ThrowIfNull(info);
        if (!_owners.TryGetValue(entity.Id, out Owner? owner))
        {
            throw new InvalidOperationException(
                $"DAT-static animation owner 0x{entity.Id:X8} is not registered.");
        }
        if (ReferenceEquals(owner.Entity, entity))
            return;
        if (entity.ServerGuid != 0
            || !info.UsesStaticAnimationWorkset
            || info.Setup is null
            || info.DefaultAnimationId == 0)
        {
            throw new InvalidOperationException(
                $"DAT-static animation owner 0x{entity.Id:X8} cannot be rebound to incompatible Setup data.");
        }

        int partCount = owner.Setup.Parts.Count;
        uint[] gfxIds = BuildPartGfxIds(owner.Setup, entity);
        bool[] available = new bool[partCount];
        if (info.PartAvailability is { } supplied)
        {
            for (int i = 0; i < partCount && i < supplied.Count; i++)
                available[i] = supplied[i];
        }

        WorldEntity displaced = owner.Entity;
        if (ReferenceEquals(displaced.MeshRefs, owner.MeshRefs))
            displaced.MeshRefs = owner.MeshRefs.ToArray();
        if (ReferenceEquals(displaced.IndexedPartTransforms, owner.PartPoses)
            || ReferenceEquals(displaced.IndexedPartAvailable, owner.PartAvailable))
        {
            displaced.SetIndexedPartPoses(
                owner.PartPoses.ToArray(),
                owner.PartAvailable.ToArray());
        }

        owner.Entity = entity;
        owner.PartGfxIds = gfxIds;
        owner.SurfaceOverrides = MatchSurfaceOverrides(entity, gfxIds, available);
        owner.PartAvailable = available;
        owner.HasPreparedLivePartFrames = false;
        owner.PreparedLivePartFrames.Clear();
    }

    public bool BindLiveOwner(
        WorldEntity entity,
        LiveEntityAnimationState animation,
        PhysicsBody body)
    {
        ArgumentNullException.ThrowIfNull(entity);
        ArgumentNullException.ThrowIfNull(animation);
        ArgumentNullException.ThrowIfNull(body);
        if (!_owners.TryGetValue(entity.Id, out Owner? owner))
            return false;
        if (!ReferenceEquals(owner.Entity, entity) || entity.ServerGuid == 0)
        {
            throw new InvalidOperationException(
                $"Static animation owner 0x{entity.Id:X8} does not match this live incarnation.");
        }
        if (animation.Sequencer is not { HasCurrentNode: true } sequencer)
            return false;

        InvalidatePending(owner);
        owner.Sequencer = sequencer;
        owner.LiveAnimation = animation;
        owner.Body = body;
        return true;
    }

    public bool TryTakeLivePartFrames(
        LiveEntityRecord record,
        WorldEntity entity,
        LiveEntityAnimationState animation,
        ulong objectClockEpoch,
        ulong projectionMutationVersion,
        ulong presentationRevision,
        out IReadOnlyList<PartTransform> frames)
    {
        ArgumentNullException.ThrowIfNull(record);
        ArgumentNullException.ThrowIfNull(entity);
        ArgumentNullException.ThrowIfNull(animation);
        uint ownerId = entity.Id;
        if (!_owners.TryGetValue(ownerId, out Owner? owner))
        {
            frames = Array.Empty<PartTransform>();
            return false;
        }

        bool exactOwner = ReferenceEquals(record.WorldEntity, entity)
            && ReferenceEquals(record.AnimationRuntime, animation)
            && record.ObjectClockEpoch == objectClockEpoch
            && record.ProjectionMutationVersion == projectionMutationVersion
            && animation.PresentationRevision == presentationRevision
            && ReferenceEquals(owner.Entity, entity)
            && ReferenceEquals(owner.LiveAnimation, animation)
            && ReferenceEquals(owner.Sequencer, animation.Sequencer)
            && owner.Entity.ServerGuid != 0;
        if (!exactOwner)
        {
            InvalidatePending(owner);
            frames = Array.Empty<PartTransform>();
            return false;
        }

        if (!owner.HasPreparedLivePartFrames)
        {
            frames = Array.Empty<PartTransform>();
            return false;
        }

        if (!ReferenceEquals(owner.PreparedFrameSequencer, animation.Sequencer)
            || !IsResidentAtVersion(owner, owner.PendingResidencyVersion))
        {
            InvalidatePending(owner);
            frames = Array.Empty<PartTransform>();
            return false;
        }

        if (owner.PreparedPresentationRevision != presentationRevision)
        {
            // Appearance rebinding invalidates only the prepared visual pose.
            // The exact live owner and sequencer still own process_hooks from
            // this quantum, including MotionDone, so retain that semantic tail.
            InvalidatePreparedFrame(owner);
            frames = Array.Empty<PartTransform>();
            return false;
        }

        owner.HasPreparedLivePartFrames = false;
        frames = owner.PreparedLivePartFrames;
        return true;
    }

    internal bool TryTakePreparedFramesForTest(
        uint ownerId,
        out IReadOnlyList<PartTransform> frames)
    {
        if (!_owners.TryGetValue(ownerId, out Owner? owner))
        {
            frames = Array.Empty<PartTransform>();
            return false;
        }
        if (!owner.HasPreparedLivePartFrames)
        {
            frames = Array.Empty<PartTransform>();
            return false;
        }
        if (!IsResidentAtVersion(owner, owner.PendingResidencyVersion))
        {
            InvalidatePending(owner);
            frames = Array.Empty<PartTransform>();
            return false;
        }
        owner.HasPreparedLivePartFrames = false;
        frames = owner.PreparedLivePartFrames;
        return true;
    }

    public void Unregister(uint ownerId) => _owners.Remove(ownerId);

    public void CopyAnimatedEntityIdsTo(HashSet<uint> destination)
    {
        ArgumentNullException.ThrowIfNull(destination);
        foreach ((uint ownerId, Owner owner) in _owners)
        {
            if (owner.Sequencer is not null)
                destination.Add(ownerId);
        }
    }

    internal void CopyActiveDatStaticEntitiesTo(List<WorldEntity> destination)
    {
        ArgumentNullException.ThrowIfNull(destination);
        destination.Clear();
        foreach (Owner owner in _owners.Values)
        {
            if (owner.Entity.ServerGuid == 0
                && owner.Sequencer is not null
                && _isResident(owner.Entity))
            {
                destination.Add(owner.Entity);
            }
        }
    }

    public void Tick(float elapsedSeconds)
    {
        if (!float.IsFinite(elapsedSeconds)
            || elapsedSeconds <= 0f
            || _owners.Count == 0)
        {
            return;
        }

        _snapshot.Clear();
        _snapshot.AddRange(_owners.Values);
        foreach (Owner owner in _snapshot)
        {
            if (!_owners.TryGetValue(owner.Entity.Id, out Owner? current)
                || !ReferenceEquals(current, owner))
            {
                continue;
            }

            if (owner.Sequencer is null
                && owner.Entity.ServerGuid != 0
                && _resolveLiveOwner?.Invoke(owner.Entity) is { } binding)
            {
                _ = BindLiveOwner(
                    owner.Entity,
                    binding.Animation,
                    binding.Body);
            }
            if (owner.Sequencer is not { } sequencer)
                continue;

            owner.ElapsedSinceUpdate += elapsedSeconds;
            if (!_isResident(owner.Entity))
            {
                InvalidatePending(owner);
                continue;
            }
            ulong residencyVersion = _residencyVersion(owner.Entity);

            double ownerElapsed = owner.ElapsedSinceUpdate;
            owner.ElapsedSinceUpdate = 0d;
            if (ownerElapsed <= FrameEpsilon
                || ownerElapsed > MaximumElapsed)
            {
                continue;
            }

            IReadOnlyList<PartTransform> frames = sequencer.Advance((float)ownerElapsed);
            owner.PreparedLivePartFrames.Clear();
            for (int i = 0; i < frames.Count; i++)
                owner.PreparedLivePartFrames.Add(frames[i]);

            if (owner.Body is { } body)
            {
                Quaternion previousOrientation = body.Orientation;
                owner.RootFrameScratch.Origin = body.Position;
                owner.RootFrameScratch.Orientation = previousOrientation;
                FrameOps.GRotate(owner.RootFrameScratch, body.Omega);
                body.SetFrameInCurrentCell(
                    body.Position,
                    owner.RootFrameScratch.Orientation);
                owner.Entity.Rotation = body.Orientation;
                if (body.Orientation != previousOrientation)
                {
                    _commitLiveRoot(owner.Entity, body);
                    if (!_owners.TryGetValue(owner.Entity.Id, out current)
                        || !ReferenceEquals(current, owner)
                        || !IsResidentAtVersion(owner, residencyVersion))
                    {
                        InvalidatePending(owner);
                        continue;
                    }
                }
            }

            else if (owner.Omega != Vector3.Zero)
            {
                owner.RootFrameScratch.Origin = owner.Entity.Position;
                owner.RootFrameScratch.Orientation = owner.Entity.Rotation;
                float omegaScale = (float)(
                    ownerElapsed * DatStaticOmegaReferenceHz);
                FrameOps.GRotate(
                    owner.RootFrameScratch,
                    owner.Omega * omegaScale);
                owner.Entity.Rotation = owner.RootFrameScratch.Orientation;
            }

            if (!IsResidentAtVersion(owner, residencyVersion))
            {
                InvalidatePending(owner);
                continue;
            }

            if (owner.Entity.ServerGuid != 0)
            {
                owner.HasPreparedLivePartFrames = true;
                owner.PreparedFrameSequencer = sequencer;
                owner.PreparedPresentationRevision =
                    owner.LiveAnimation?.PresentationRevision ?? 0UL;
            }
            else
            {
                Compose(owner, owner.PreparedLivePartFrames);
                _publishPartPoses(owner.Entity, owner.PartPoses, owner.PartAvailable);
                if (!_owners.TryGetValue(owner.Entity.Id, out current)
                    || !ReferenceEquals(current, owner)
                    || !IsResidentAtVersion(owner, residencyVersion))
                {
                    InvalidatePending(owner);
                    continue;
                }
            }

            owner.PendingProcessHooks = sequencer;
            owner.PendingResidencyVersion = residencyVersion;
        }
    }

    public void ProcessHooks()
    {
        _hookSnapshot.Clear();
        foreach (Owner owner in _owners.Values)
        {
            if (owner.PendingProcessHooks is not null)
                _hookSnapshot.Add(owner);
        }

        foreach (Owner owner in _hookSnapshot)
        {
            if (!_owners.TryGetValue(owner.Entity.Id, out Owner? current)
                || !ReferenceEquals(current, owner)
                || owner.PendingProcessHooks is not { } sequencer
                || !ReferenceEquals(owner.Sequencer, sequencer)
                || !IsResidentAtVersion(owner, owner.PendingResidencyVersion))
            {
                InvalidatePending(owner);
                continue;
            }

            ApplyOmegaHooks(owner, sequencer.PendingHooks);

            owner.PendingProcessHooks = null;
            _captureHooks(owner.Entity.Id, sequencer);
        }
    }

    private static void ApplyOmegaHooks(Owner owner, IReadOnlyList<AnimationHook> hooks)
    {
        if (!TryResolveOmega(hooks, out Vector3 omega))
            return;
        owner.Omega = omega;
        if (owner.Body is { } body)
            body.Omega = omega;
    }

    internal static bool TryResolveOmega(
        IReadOnlyList<AnimationHook> hooks, out Vector3 omega)
    {
        omega = default;
        bool found = false;
        for (int i = 0; i < hooks.Count; i++)
        {
            if (hooks[i] is not SetOmegaHook set)
                continue;
            omega = new Vector3(set.Axis.X, set.Axis.Y, set.Axis.Z);
            found = true;
        }
        return found;
    }

    private bool IsResidentAtVersion(Owner owner, ulong version) =>
        _isResident(owner.Entity)
        && _residencyVersion(owner.Entity) == version;

    private static void InvalidatePending(Owner owner)
    {
        InvalidatePreparedFrame(owner);
        owner.PendingProcessHooks = null;
        owner.PendingResidencyVersion = 0UL;
    }

    private static void InvalidatePreparedFrame(Owner owner)
    {
        owner.PreparedLivePartFrames.Clear();
        owner.HasPreparedLivePartFrames = false;
        owner.PreparedFrameSequencer = null;
        owner.PreparedPresentationRevision = 0UL;
    }

    private static void Compose(Owner owner, IReadOnlyList<PartTransform> frames)
    {
        EnsureRetainedPoses(owner);
        owner.MeshRefs.Clear();
        Matrix4x4 objectScale = owner.Entity.Scale == 1f
            ? Matrix4x4.Identity
            : Matrix4x4.CreateScale(owner.Entity.Scale);

        int partCount = owner.Setup.Parts.Count;
        for (int i = 0; i < partCount; i++)
        {
            if (i < frames.Count)
            {
                Vector3 origin = frames[i].Origin;
                Quaternion orientation = frames[i].Orientation;
                Vector3 defaultScale = i < owner.Setup.DefaultScale.Count
                    ? owner.Setup.DefaultScale[i]
                    : Vector3.One;

                Matrix4x4 visual = Matrix4x4.CreateScale(defaultScale)
                    * Matrix4x4.CreateFromQuaternion(orientation)
                    * Matrix4x4.CreateTranslation(origin);
                if (owner.Entity.Scale != 1f)
                    visual *= objectScale;
                owner.VisualPartPoses[i] = visual;
                owner.PartPoses[i] = Matrix4x4.CreateFromQuaternion(orientation)
                    * Matrix4x4.CreateTranslation(origin * owner.Entity.Scale);
            }
            if (!owner.PartAvailable[i])
                continue;

            owner.MeshRefs.Add(new MeshRef(owner.PartGfxIds[i], owner.VisualPartPoses[i])
            {
                SurfaceOverrides = owner.SurfaceOverrides[i],
            });
        }

        owner.Entity.MeshRefs = owner.MeshRefs;
        owner.Entity.SetIndexedPartPoses(owner.PartPoses, owner.PartAvailable);
    }

    private static void EnsureRetainedPoses(Owner owner)
    {
        int partCount = owner.Setup.Parts.Count;
        if (owner.VisualPartPoses.Count == partCount
            && owner.PartPoses.Count == partCount)
        {
            return;
        }

        owner.VisualPartPoses.Clear();
        owner.PartPoses.Clear();
        bool[] consumed = new bool[owner.Entity.MeshRefs.Count];
        for (int i = 0; i < partCount; i++)
        {
            Matrix4x4 rigid = i < owner.Entity.IndexedPartTransforms.Count
                ? owner.Entity.IndexedPartTransforms[i]
                : Matrix4x4.Identity;
            owner.PartPoses.Add(rigid);

            Matrix4x4 visual = default;
            bool found = false;
            for (int meshIndex = 0; meshIndex < owner.Entity.MeshRefs.Count; meshIndex++)
            {
                MeshRef mesh = owner.Entity.MeshRefs[meshIndex];
                if (consumed[meshIndex] || mesh.GfxObjId != owner.PartGfxIds[i])
                    continue;
                consumed[meshIndex] = true;
                visual = mesh.PartTransform;
                found = true;
                break;
            }
            if (!found)
            {
                Vector3 defaultScale = i < owner.Setup.DefaultScale.Count
                    ? owner.Setup.DefaultScale[i]
                    : Vector3.One;
                visual = Matrix4x4.CreateScale(defaultScale * owner.Entity.Scale)
                    * rigid;
            }
            owner.VisualPartPoses.Add(visual);
        }
    }

    private static uint[] BuildPartGfxIds(Setup setup, WorldEntity entity)
    {
        var result = new uint[setup.Parts.Count];
        for (int i = 0; i < result.Length; i++)
            result[i] = (uint)setup.Parts[i];
        foreach (PartOverride replacement in entity.PartOverrides)
        {
            if (replacement.PartIndex < result.Length)
                result[replacement.PartIndex] = replacement.GfxObjId;
        }
        return result;
    }

    private static IReadOnlyDictionary<uint, uint>?[] MatchSurfaceOverrides(
        WorldEntity entity,
        uint[] gfxIds,
        bool[] available)
    {
        var result = new IReadOnlyDictionary<uint, uint>?[gfxIds.Length];
        var consumed = new bool[entity.MeshRefs.Count];
        for (int partIndex = 0; partIndex < gfxIds.Length; partIndex++)
        {
            for (int meshIndex = 0; meshIndex < entity.MeshRefs.Count; meshIndex++)
            {
                if (consumed[meshIndex]
                    || entity.MeshRefs[meshIndex].GfxObjId != gfxIds[partIndex])
                {
                    continue;
                }

                consumed[meshIndex] = true;
                available[partIndex] = true;
                result[partIndex] = entity.MeshRefs[meshIndex].SurfaceOverrides;
                break;
            }
        }
        return result;
    }
}
