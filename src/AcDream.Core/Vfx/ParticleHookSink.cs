using System.Numerics;
using AcDream.Core.Physics;
using DatReaderWriter.Types;

namespace AcDream.Core.Vfx;

public sealed class ParticleHookSink : IAnimationHookSink
{
    private readonly ParticleSystem _system;
    private readonly IEntityEffectPoseSource _poses;
    private readonly IEntityEffectCellSource? _cells;
    private readonly IEntityEffectPoseChangeSource? _poseChanges;

    private readonly Dictionary<(uint EntityId, uint EmitterId), int> _handlesByKey = new();
    private readonly Dictionary<uint, HashSet<int>> _handlesByEntity = new();
    private readonly Dictionary<int, EmitterBinding> _bindingsByHandle = new();
    private readonly Dictionary<uint, ParticleRenderPass> _renderPassByEntity = new();
    private readonly HashSet<uint> _examinationOwners = [];
    private readonly HashSet<uint> _hiddenPresentationOwners = [];
    private readonly HashSet<uint> _stoppingOwners = [];
    private readonly HashSet<uint> _reportedEmitterResolutionFailures = [];

    private readonly HashSet<uint> _spatialOwners = [];
    private readonly HashSet<uint> _dirtyOwnerSet = [];
    private readonly List<uint> _dirtyOwnerOrder = [];
    private readonly List<uint> _refreshOwnerSnapshot = [];
    public ParticleHookSink(ParticleSystem system, IEntityEffectPoseSource poses)
    {
        _system = system ?? throw new ArgumentNullException(nameof(system));
        _poses = poses ?? throw new ArgumentNullException(nameof(poses));
        _cells = poses as IEntityEffectCellSource;
        _poseChanges = poses as IEntityEffectPoseChangeSource;
        if (_poseChanges is not null)
            _poseChanges.EffectPoseChanged += OnEffectPoseChanged;
        _system.EmitterDied += OnEmitterDied;
    }

    public Action<string>? DiagnosticSink { get; set; }

    public int ActiveBindingCount => _bindingsByHandle.Count;
    public int LogicalEmitterCount => _handlesByKey.Count;
    public int TrackedOwnerCount => _handlesByEntity.Count;
    public int RenderPassOwnerCount => _renderPassByEntity.Count;
    public int ExaminationOwnerCount => _examinationOwners.Count;
    public int HiddenPresentationOwnerCount => _hiddenPresentationOwners.Count;
    internal int LastRefreshOwnerVisitCount { get; private set; }
    internal int LastRefreshBindingVisitCount { get; private set; }

    private void OnEmitterDied(int handle)
    {
        if (!_bindingsByHandle.Remove(handle, out EmitterBinding binding))
            return;

        if (binding.LogicalId != 0
            && _handlesByKey.TryGetValue((binding.OwnerLocalId, binding.LogicalId), out int current)
            && current == handle)
        {
            _handlesByKey.Remove((binding.OwnerLocalId, binding.LogicalId));
        }
        if (_handlesByEntity.TryGetValue(binding.OwnerLocalId, out var handles))
        {
            handles.Remove(handle);
            if (handles.Count == 0)
            {
                _handlesByEntity.Remove(binding.OwnerLocalId);
                _spatialOwners.Remove(binding.OwnerLocalId);
            }
        }
    }

    public void OnHook(uint entityId, Vector3 entityWorldPosition, AnimationHook hook)
    {
        switch (hook)
        {
            case RetailCreateBlockingParticleHook blocking:
                SpawnFromHook(
                    entityId,
                    (uint)blocking.EmitterInfoId,
                    blocking.Offset,
                    unchecked((int)blocking.PartIndex),
                    blocking.EmitterId,
                    isBlocking: true);
                break;

            case CreateParticleHook create:
                SpawnFromHook(
                    entityId,
                    (uint)create.EmitterInfoId,
                    create.Offset,
                    unchecked((int)create.PartIndex),
                    create.EmitterId,
                    isBlocking: false);
                break;

            case CreateBlockingParticleHook:
                DiagnosticSink?.Invoke(
                    $"CreateBlockingParticle for owner 0x{entityId:X8} has no retail payload.");
                break;

            case DestroyParticleHook destroy:
                DestroyLogical(entityId, destroy.EmitterId, fadeOut: false);
                break;

            case StopParticleHook stop:
                DestroyLogical(entityId, stop.EmitterId, fadeOut: true);
                break;
        }
    }

    public void SetEntityRenderPass(uint entityId, ParticleRenderPass renderPass)
    {
        _renderPassByEntity[entityId] = renderPass;
        RefreshOwnerVisibilityPolicy(entityId);
    }

    public void ClearEntityRenderPass(uint entityId)
    {
        _renderPassByEntity.Remove(entityId);
        RefreshOwnerVisibilityPolicy(entityId);
    }

    public void SetEntityExaminationObject(uint entityId, bool isExaminationObject)
    {
        if (isExaminationObject)
            _examinationOwners.Add(entityId);
        else
            _examinationOwners.Remove(entityId);

        RefreshOwnerVisibilityPolicy(entityId);
    }

    public void SetEntityPresentationVisible(uint entityId, bool visible)
    {
        if (visible)
            _hiddenPresentationOwners.Remove(entityId);
        else
            _hiddenPresentationOwners.Add(entityId);

        if (visible)
        {
            if (_handlesByEntity.ContainsKey(entityId))
                _spatialOwners.Add(entityId);
            MarkOwnerDirty(entityId);
        }
        else
        {
            _spatialOwners.Remove(entityId);
        }

        if (!visible && _handlesByEntity.TryGetValue(entityId, out var handles))
        {
            foreach (int handle in handles)
            {
                _system.SetEmitterPresentationVisible(handle, false);
                _system.SetEmitterSimulationEnabled(handle, false);
            }
        }
    }

    public void RefreshAttachedEmitters()
    {
        LastRefreshOwnerVisitCount = 0;
        LastRefreshBindingVisitCount = 0;
        _refreshOwnerSnapshot.Clear();
        if (_poseChanges is null)
        {
            foreach (uint ownerLocalId in _handlesByEntity.Keys)
                _refreshOwnerSnapshot.Add(ownerLocalId);
        }
        else
        {
            _refreshOwnerSnapshot.AddRange(_dirtyOwnerOrder);
            _dirtyOwnerOrder.Clear();
            _dirtyOwnerSet.Clear();
        }

        for (int ownerIndex = 0; ownerIndex < _refreshOwnerSnapshot.Count; ownerIndex++)
        {
            uint ownerLocalId = _refreshOwnerSnapshot[ownerIndex];
            if (!_handlesByEntity.TryGetValue(ownerLocalId, out HashSet<int>? handles))
            {
                continue;
            }

            LastRefreshOwnerVisitCount++;
            bool presentationVisible =
                !_hiddenPresentationOwners.Contains(ownerLocalId);
            foreach (int handle in handles)
            {
                if (!_bindingsByHandle.TryGetValue(handle, out EmitterBinding binding))
                    continue;

                LastRefreshBindingVisitCount++;
                if (TryResolveAnchor(ownerLocalId, binding.PartIndex,
                        binding.HookOffsetOrigin,
                        out Vector3 anchor,
                        out Quaternion rotation,
                        out Vector3 ownerPosition))
                {
                    _system.UpdateEmitterAnchor(handle, anchor, rotation);
                    _system.UpdateEmitterOwnerPosition(handle, ownerPosition);
                    _system.UpdateEmitterOwnerCell(
                        handle,
                        _cells is not null && _cells.TryGetCellId(ownerLocalId, out uint cellId)
                            ? cellId
                            : 0u);
                    _system.SetEmitterPresentationVisible(handle, presentationVisible);
                    _system.SetEmitterSimulationEnabled(handle, presentationVisible);
                }
                else
                {
                    _system.SetEmitterPresentationVisible(handle, false);
                    _system.SetEmitterSimulationEnabled(handle, false);
                }
            }
        }
    }

    public void StopAllForEntity(uint entityId, bool fadeOut)
    {
        if (!_stoppingOwners.Add(entityId))
            return;

        try
        {
            _spatialOwners.Remove(entityId);
            List<Exception>? failures = null;
            if (_handlesByEntity.TryGetValue(entityId, out HashSet<int>? handles))
            {
                int[] snapshot = [.. handles];
                for (int i = 0; i < snapshot.Length; i++)
                {
                    int handle = snapshot[i];
                    if (_bindingsByHandle.TryGetValue(handle, out EmitterBinding binding)
                        && binding.LogicalId != 0)
                    {
                        _handlesByKey.Remove((entityId, binding.LogicalId));
                    }

                    bool stopCommitted = false;
                    try
                    {
                        if (fadeOut)
                            _system.SetEmitterSimulationEnabled(handle, true);
                        _system.StopEmitter(handle, fadeOut);
                        stopCommitted = true;
                    }
                    catch (Exception error)
                    {
                        stopCommitted = !_system.IsEmitterAlive(handle);
                        (failures ??= []).Add(error);
                    }

                    if (!stopCommitted)
                        continue;

                    handles.Remove(handle);
                    _bindingsByHandle.Remove(handle);
                }

                if (handles.Count == 0
                    && _handlesByEntity.TryGetValue(entityId, out HashSet<int>? current)
                    && ReferenceEquals(current, handles))
                {
                    _handlesByEntity.Remove(entityId);
                }
            }
            ClearEntityRenderPass(entityId);
            _examinationOwners.Remove(entityId);
            _hiddenPresentationOwners.Remove(entityId);

            if (failures is not null)
            {
                throw new AggregateException(
                    $"One or more particle emitters for owner 0x{entityId:X8} failed to stop cleanly.",
                    failures);
            }
        }
        finally
        {
            _stoppingOwners.Remove(entityId);
        }
    }

    private void DestroyLogical(uint ownerLocalId, uint logicalId, bool fadeOut)
    {
        if (logicalId == 0
            || !_handlesByKey.TryGetValue((ownerLocalId, logicalId), out int handle))
        {
            return;
        }

        if (!fadeOut)
            _handlesByKey.Remove((ownerLocalId, logicalId));
        if (fadeOut)
            _system.SetEmitterSimulationEnabled(handle, true);
        _system.StopEmitter(handle, fadeOut);
    }

    private void SpawnFromHook(
        uint ownerLocalId,
        uint emitterInfoId,
        Frame offset,
        int partIndex,
        uint logicalId,
        bool isBlocking)
    {
        if (_stoppingOwners.Contains(ownerLocalId))
        {
            DiagnosticSink?.Invoke(
                $"Particle creation for owner 0x{ownerLocalId:X8} was ignored while its emitters were stopping.");
            return;
        }

        if (logicalId != 0
            && _handlesByKey.TryGetValue((ownerLocalId, logicalId), out int existing))
        {
            if (isBlocking && _system.IsEmitterAlive(existing))
                return;

            _handlesByKey.Remove((ownerLocalId, logicalId));
            _system.StopEmitter(existing, fadeOut: false);
        }

        Vector3 offsetOrigin = offset?.Origin ?? Vector3.Zero;
        Quaternion offsetOrientation = offset?.Orientation ?? Quaternion.Identity;
        if (!TryResolveAnchor(ownerLocalId, partIndex, offsetOrigin,
                out Vector3 anchor,
                out Quaternion rotation,
                out Vector3 ownerPosition))
        {
            DiagnosticSink?.Invoke(
                $"No live effect pose for owner 0x{ownerLocalId:X8}, part {partIndex}; " +
                $"emitter 0x{emitterInfoId:X8} was not created.");
            return;
        }

        ParticleRenderPass renderPass = _renderPassByEntity.TryGetValue(ownerLocalId, out var pass)
            ? pass
            : ParticleRenderPass.Scene;
        ParticleVisibilityPolicy visibilityPolicy = ResolveVisibilityPolicy(ownerLocalId);
        if (!_system.TrySpawnEmitterById(
                emitterInfoId,
                anchor,
                rotation,
                ownerLocalId,
                partIndex,
                renderPass,
                visibilityPolicy,
                out int handle,
                out EmitterDescResolutionFailure failure))
        {
            if (_reportedEmitterResolutionFailures.Add(emitterInfoId))
            {
                DiagnosticSink?.Invoke(FormatEmitterResolutionDiagnostic(
                    emitterInfoId,
                    ownerLocalId,
                    failure));
            }
            return;
        }
        _system.UpdateEmitterOwnerPosition(handle, ownerPosition);
        uint ownerCellId = 0u;
        bool haveOwnerCell =
            _cells is not null && _cells.TryGetCellId(ownerLocalId, out ownerCellId);
        _system.UpdateEmitterOwnerCell(handle, haveOwnerCell ? ownerCellId : 0u);
        if (_hiddenPresentationOwners.Contains(ownerLocalId))
        {
            _system.SetEmitterPresentationVisible(handle, false);
            _system.SetEmitterSimulationEnabled(handle, false);
        }

        var binding = new EmitterBinding(
            ownerLocalId,
            partIndex,
            offsetOrigin,
            offsetOrientation,
            logicalId,
            renderPass);
        _bindingsByHandle[handle] = binding;
        if (!_handlesByEntity.TryGetValue(ownerLocalId, out HashSet<int>? handles))
        {
            handles = [];
            _handlesByEntity.Add(ownerLocalId, handles);
        }
        handles.Add(handle);
        if (!_hiddenPresentationOwners.Contains(ownerLocalId))
            _spatialOwners.Add(ownerLocalId);
        if (logicalId != 0)
            _handlesByKey[(ownerLocalId, logicalId)] = handle;
    }

    private ParticleVisibilityPolicy ResolveVisibilityPolicy(uint ownerLocalId)
    {
        if (_examinationOwners.Contains(ownerLocalId))
            return ParticleVisibilityPolicy.Examination;

        return _renderPassByEntity.TryGetValue(ownerLocalId, out ParticleRenderPass pass)
               && pass != ParticleRenderPass.Scene
            ? ParticleVisibilityPolicy.PassOwned
            : ParticleVisibilityPolicy.World;
    }

    private void RefreshOwnerVisibilityPolicy(uint ownerLocalId)
    {
        if (!_handlesByEntity.TryGetValue(ownerLocalId, out var handles))
            return;

        ParticleVisibilityPolicy policy = ResolveVisibilityPolicy(ownerLocalId);
        foreach (int handle in handles)
            _system.SetEmitterVisibilityPolicy(handle, policy);
    }

    private void OnEffectPoseChanged(uint ownerLocalId)
        => MarkOwnerDirty(ownerLocalId);

    private void MarkOwnerDirty(uint ownerLocalId)
    {
        if (!_handlesByEntity.ContainsKey(ownerLocalId)
            || !_dirtyOwnerSet.Add(ownerLocalId))
        {
            return;
        }

        _dirtyOwnerOrder.Add(ownerLocalId);
    }

    private bool TryResolveAnchor(
        uint ownerLocalId,
        int partIndex,
        Vector3 offsetOrigin,
        out Vector3 anchor,
        out Quaternion rotation,
        out Vector3 ownerPosition)
    {
        if (!_poses.TryGetRootPose(ownerLocalId, out Matrix4x4 rootWorld))
        {
            anchor = default;
            rotation = default;
            ownerPosition = default;
            return false;
        }

        ownerPosition = new Vector3(rootWorld.M41, rootWorld.M42, rootWorld.M43);

        Matrix4x4 ownerFrame = rootWorld;
        if (partIndex != -1)
        {
            if (!_poses.TryGetPartPose(ownerLocalId, partIndex, out Matrix4x4 partLocal))
            {
                anchor = default;
                rotation = default;
                ownerPosition = default;
                return false;
            }
            ownerFrame = partLocal * rootWorld;
        }

        anchor = Vector3.Transform(offsetOrigin, ownerFrame);
        if (!Matrix4x4.Decompose(ownerFrame, out _, out rotation, out _))
        {
            anchor = default;
            rotation = default;
            ownerPosition = default;
            return false;
        }
        rotation = Quaternion.Normalize(rotation);
        return true;
    }

    private static string FormatEmitterResolutionDiagnostic(
        uint emitterInfoId,
        uint ownerLocalId,
        EmitterDescResolutionFailure failure) =>
        failure.Kind switch
        {
            EmitterDescResolutionFailureKind.InvalidHardwareGfxObjId =>
                $"ParticleEmitterInfo 0x{emitterInfoId:X8} has no " +
                $"hardware GfxObj; the emitter was skipped " +
                $"(first owner 0x{ownerLocalId:X8}).",
            EmitterDescResolutionFailureKind.MissingHardwareGfxObj =>
                $"ParticleEmitterInfo 0x{emitterInfoId:X8} references missing " +
                $"hardware GfxObj 0x{failure.RelatedDatId:X8}; " +
                $"the emitter was skipped " +
                $"(first owner 0x{ownerLocalId:X8}).",
            _ =>
                $"ParticleEmitterInfo 0x{emitterInfoId:X8} for owner " +
                $"0x{ownerLocalId:X8} was not found; no fallback was created.",
        };

    private readonly record struct EmitterBinding(
        uint OwnerLocalId,
        int PartIndex,
        Vector3 HookOffsetOrigin,
        Quaternion HookOffsetOrientation,
        uint LogicalId,
        ParticleRenderPass RenderPass);
}
