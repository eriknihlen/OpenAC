using System;
using System.Collections.Generic;
using System.Numerics;

namespace AcDream.Core.Vfx;

public sealed class ParticleSystem : IParticleSystem
{
    private readonly EmitterDescRegistry _registry;
    private readonly Random _rng;
    private readonly Dictionary<int, ParticleEmitter> _byHandle = new();
    private readonly SortedSet<int> _allHandles = [];
    private readonly SortedSet<int> _simulationHandles = [];
    private readonly List<int> _worldSimulationHandles = [];
    private readonly SortedSet<int>[] _renderableHandlesByPass =
        [[], [], []];
    private readonly SortedSet<int>[] _renderableUnattachedHandlesByPass =
        [[], [], []];
    private readonly Dictionary<uint, OwnerEmitterBucket>[] _ownerHandlesByPass =
        [new(), new(), new()];
    private readonly Dictionary<uint, OwnerEmitterBucket>[] _cellHandlesByPass =
        [new(), new(), new()];
    private readonly List<int> _tickSnapshot = [];
    private readonly List<int> _scopeHandleScratch = [];

    private sealed class OwnerEmitterBucket
    {
        public int LogicalCount;
        public int FirstRenderableHandle;
        public List<int>? AdditionalRenderableHandles;

        public void AddRenderable(int handle)
        {
            if (FirstRenderableHandle == 0)
            {
                FirstRenderableHandle = handle;
                return;
            }

            if (handle < FirstRenderableHandle)
            {
                (FirstRenderableHandle, handle) = (handle, FirstRenderableHandle);
            }
            List<int> additional = AdditionalRenderableHandles ??= [];
            int index = additional.BinarySearch(handle);
            if (index < 0)
                additional.Insert(~index, handle);
        }

        public void RemoveRenderable(int handle)
        {
            if (FirstRenderableHandle == handle)
            {
                if (AdditionalRenderableHandles is { Count: > 0 } additional)
                {
                    FirstRenderableHandle = additional[0];
                    additional.RemoveAt(0);
                }
                else
                {
                    FirstRenderableHandle = 0;
                }
                return;
            }

            if (AdditionalRenderableHandles is { } others)
            {
                int index = others.BinarySearch(handle);
                if (index >= 0)
                    others.RemoveAt(index);
            }
        }

        public void CopyRenderableHandlesTo(List<int> destination)
        {
            if (FirstRenderableHandle == 0)
                return;
            destination.Add(FirstRenderableHandle);
            if (AdditionalRenderableHandles is { Count: > 0 } additional)
                destination.AddRange(additional);
        }
    }

    private int _nextHandle = 1;
    private float _time;
    private int _activeParticleCount;

    public ParticleSystem(EmitterDescRegistry registry, Random? rng = null)
    {
        _registry = registry ?? throw new ArgumentNullException(nameof(registry));
        _rng = rng ?? Random.Shared;
    }

    public int ActiveEmitterCount => _byHandle.Count;
    public int ActiveParticleCount => _activeParticleCount;

    internal int LastTickEmitterVisitCount { get; private set; }
    internal int LastRetailViewEmitterVisitCount { get; private set; }
    internal int LastRenderScopeEmitterVisitCount { get; private set; }

    public int SpawnEmitter(
        EmitterDesc desc,
        Vector3 anchor,
        Quaternion? rot = null,
        uint attachedObjectId = 0,
        int attachedPartIndex = -1,
        ParticleRenderPass renderPass = ParticleRenderPass.Scene,
        ParticleVisibilityPolicy visibilityPolicy = ParticleVisibilityPolicy.World)
    {
        ArgumentNullException.ThrowIfNull(desc);

        int handle = _nextHandle++;
        var emitter = new ParticleEmitter
        {
            Handle = handle,
            Desc = desc,
            AnchorPos = anchor,
            OwnerPosition = anchor,
            AnchorRot = rot ?? Quaternion.Identity,
            AttachedObjectId = attachedObjectId,
            AttachedPartIndex = attachedPartIndex,
            RenderPass = renderPass,
            VisibilityPolicy = visibilityPolicy,
            Particles = new Particle[Math.Max(1, desc.MaxParticles)],
            StartedAt = _time,
            LastEmitTime = _time,
            LastEmitOffset = anchor,
        };

        _byHandle[handle] = emitter;
        _allHandles.Add(handle);
        _simulationHandles.Add(handle);
        if (visibilityPolicy == ParticleVisibilityPolicy.World)
            AddWorldSimulationHandle(handle);
        AddEmitterToRenderIndexes(emitter);

        for (int i = 0; i < desc.InitialParticles; i++)
            SpawnOne(emitter, allowWhenFull: false);

        return handle;
    }

    public int SpawnEmitterById(
        uint emitterId,
        Vector3 anchor,
        Quaternion? rot = null,
        uint attachedObjectId = 0,
        int attachedPartIndex = -1,
        ParticleRenderPass renderPass = ParticleRenderPass.Scene,
        ParticleVisibilityPolicy visibilityPolicy = ParticleVisibilityPolicy.World)
    {
        var desc = _registry.Get(emitterId);
        return SpawnEmitter(
            desc,
            anchor,
            rot,
            attachedObjectId,
            attachedPartIndex,
            renderPass,
            visibilityPolicy);
    }

    public bool TrySpawnEmitterById(
        uint emitterId,
        Vector3 anchor,
        Quaternion? rot,
        uint attachedObjectId,
        int attachedPartIndex,
        ParticleRenderPass renderPass,
        ParticleVisibilityPolicy visibilityPolicy,
        out int handle)
        => TrySpawnEmitterById(
            emitterId,
            anchor,
            rot,
            attachedObjectId,
            attachedPartIndex,
            renderPass,
            visibilityPolicy,
            out handle,
            out _);

    public bool TrySpawnEmitterById(
        uint emitterId,
        Vector3 anchor,
        Quaternion? rot,
        uint attachedObjectId,
        int attachedPartIndex,
        ParticleRenderPass renderPass,
        ParticleVisibilityPolicy visibilityPolicy,
        out int handle,
        out EmitterDescResolutionFailure failure)
    {
        if (!_registry.TryGet(emitterId, out EmitterDesc? desc, out failure))
        {
            handle = 0;
            return false;
        }

        handle = SpawnEmitter(
            desc,
            anchor,
            rot,
            attachedObjectId,
            attachedPartIndex,
            renderPass,
            visibilityPolicy);
        failure = EmitterDescResolutionFailure.None;
        return true;
    }

    public void PlayScript(uint scriptId, uint targetObjectId, float modifier = 1f)
    {
        // Full PhysicsScript scheduling lives in PhysicsScriptRunner.
    }

    public void StopEmitter(int handle, bool fadeOut)
    {
        if (!_byHandle.TryGetValue(handle, out var em))
            return;

        em.Finished = true;
        if (!fadeOut)
        {
            for (int i = 0; i < em.Particles.Length; i++)
                em.Particles[i].Alive = false;
            em.ActiveCount = 0;
            RemoveEmitter(handle, em);
        }
    }

    public void UpdateEmitterAnchor(int handle, Vector3 anchor, Quaternion? rot = null)
    {
        if (!_byHandle.TryGetValue(handle, out var em))
            return;
        em.AnchorPos = anchor;
        if (!em.SimulationEnabled)
            em.LastEmitOffset = anchor;
        if (rot.HasValue)
            em.AnchorRot = rot.Value;
    }

    public void UpdateEmitterOwnerPosition(int handle, Vector3 ownerPosition)
    {
        if (_byHandle.TryGetValue(handle, out ParticleEmitter? emitter))
            emitter.OwnerPosition = ownerPosition;
    }

    public void UpdateEmitterOwnerCell(int handle, uint ownerCellId)
    {
        if (!_byHandle.TryGetValue(handle, out ParticleEmitter? emitter)
            || emitter.OwnerCellId == ownerCellId)
        {
            return;
        }

        int passIndex = RenderPassIndex(emitter.RenderPass);
        bool isRenderable = IsRenderable(emitter);
        Dictionary<uint, OwnerEmitterBucket> cellBuckets = _cellHandlesByPass[passIndex];

        if (cellBuckets.TryGetValue(emitter.OwnerCellId, out OwnerEmitterBucket? oldBucket))
        {
            if (isRenderable)
                oldBucket.RemoveRenderable(handle);
            oldBucket.LogicalCount--;
            if (oldBucket.LogicalCount == 0)
                cellBuckets.Remove(emitter.OwnerCellId);
        }

        emitter.OwnerCellId = ownerCellId;

        if (!cellBuckets.TryGetValue(ownerCellId, out OwnerEmitterBucket? newBucket))
        {
            newBucket = new OwnerEmitterBucket();
            cellBuckets.Add(ownerCellId, newBucket);
        }
        newBucket.LogicalCount++;
        if (isRenderable)
            newBucket.AddRenderable(handle);
    }

    public void SetEmitterVisibilityPolicy(
        int handle,
        ParticleVisibilityPolicy visibilityPolicy)
    {
        if (!_byHandle.TryGetValue(handle, out ParticleEmitter? emitter)
            || emitter.VisibilityPolicy == visibilityPolicy)
        {
            return;
        }

        bool wasRenderable = IsRenderable(emitter);
        if (emitter.VisibilityPolicy == ParticleVisibilityPolicy.World)
            _worldSimulationHandles.Remove(handle);

        emitter.VisibilityPolicy = visibilityPolicy;
        if (visibilityPolicy == ParticleVisibilityPolicy.World)
        {
            if (emitter.SimulationEnabled)
                AddWorldSimulationHandle(handle);
        }
        else
        {
            // Examination and dedicated-pass objects bypass world PView.
            emitter.ViewEligible = true;
        }
        RefreshRenderableIndex(emitter, wasRenderable);
    }

    public void ApplyRetailView(
        Vector3 viewerPosition,
        IReadOnlySet<uint> visibleLandscapeCellIds,
        bool hasCompletedView,
        float rangeMultiplier = 1f)
    {
        ArgumentNullException.ThrowIfNull(visibleLandscapeCellIds);
        if (!float.IsFinite(rangeMultiplier) || rangeMultiplier <= 0f)
            rangeMultiplier = 1f;

        LastRetailViewEmitterVisitCount = 0;
        for (int i = 0; i < _worldSimulationHandles.Count; i++)
        {
            int handle = _worldSimulationHandles[i];
            if (!_byHandle.TryGetValue(handle, out ParticleEmitter? emitter))
                continue;
            LastRetailViewEmitterVisitCount++;
            bool wasRenderable = IsRenderable(emitter);
            if (!hasCompletedView)
            {
                emitter.ViewEligible = false;
                RefreshRenderableIndex(emitter, wasRenderable);
                continue;
            }

            float maxDistance = emitter.Desc.MaxDegradeDistance * rangeMultiplier;
            float distance = RetailDistance(emitter.OwnerPosition, viewerPosition);
            uint ownerCellLow = emitter.OwnerCellId & 0xFFFFu;
            bool ownerCellInView = ownerCellLow >= 0x0100u
                || (ownerCellLow > 0u
                    && visibleLandscapeCellIds.Contains(emitter.OwnerCellId));
            emitter.ViewEligible = emitter.OwnerCellId != 0
                && ownerCellInView
                && (float.IsNaN(distance)
                    || float.IsNaN(maxDistance)
                    || distance <= maxDistance);
            RefreshRenderableIndex(emitter, wasRenderable);
        }
    }

    public void SetEmitterPresentationVisible(int handle, bool visible)
    {
        if (!_byHandle.TryGetValue(handle, out ParticleEmitter? emitter)
            || emitter.PresentationVisible == visible)
        {
            return;
        }

        bool wasRenderable = IsRenderable(emitter);
        emitter.PresentationVisible = visible;
        RefreshRenderableIndex(emitter, wasRenderable);
    }

    public void SetEmitterSimulationEnabled(int handle, bool enabled)
    {
        if (!_byHandle.TryGetValue(handle, out ParticleEmitter? emitter)
            || emitter.SimulationEnabled == enabled)
        {
            return;
        }

        if (!enabled)
        {
            bool wasRenderable = IsRenderable(emitter);
            emitter.SimulationEnabled = false;
            _simulationHandles.Remove(handle);
            _worldSimulationHandles.Remove(handle);
            if (emitter.VisibilityPolicy == ParticleVisibilityPolicy.World)
            {
                emitter.ViewEligible = false;
                RefreshRenderableIndex(emitter, wasRenderable);
            }
            return;
        }

        if (emitter.Desc.Birthrate <= 0f && emitter.Desc.EmitRate > 0f)
        {
            emitter.LastEmitTime = _time;
            emitter.EmittedAccumulator = 0f;
        }
        emitter.SimulationEnabled = true;
        _simulationHandles.Add(handle);
        if (emitter.VisibilityPolicy == ParticleVisibilityPolicy.World)
            AddWorldSimulationHandle(handle);
        else
            emitter.ViewEligible = true;
    }

    /// <summary>True when the given handle still maps to a live emitter.</summary>
    public bool IsEmitterAlive(int handle) => _byHandle.ContainsKey(handle);

    public event Action<int>? EmitterDied;

    public void Tick(float dt)
    {
        if (dt <= 0f)
            return;

        _time += dt;
        _activeParticleCount = 0;
        LastTickEmitterVisitCount = 0;

        _tickSnapshot.Clear();
        foreach (int handle in _simulationHandles)
            _tickSnapshot.Add(handle);

        for (int i = 0; i < _tickSnapshot.Count; i++)
        {
            int handle = _tickSnapshot[i];
            if (!_byHandle.TryGetValue(handle, out var em))
                continue;
            if (!em.SimulationEnabled)
                continue;
            LastTickEmitterVisitCount++;
            bool wasFinishedBeforeUpdate = em.Finished;

            if (em.ViewEligible)
            {
                em.DegradedOut = false;
                AdvanceEmitter(em);
            }
            else
            {
                em.DegradedOut = true;
                AdvanceDegradedEmitter(em, dt);
            }
            int live = em.ActiveCount;
            _activeParticleCount += live;

            if (em.Desc.TotalDuration > 0f && (_time - em.StartedAt) > em.Desc.TotalDuration)
                em.Finished = true;

            if (em.Desc.TotalParticles > 0 && em.TotalEmitted >= em.Desc.TotalParticles)
                em.Finished = true;

            if (em.Finished && live == 0 && wasFinishedBeforeUpdate)
            {
                RemoveEmitter(handle, em);
            }
        }
    }

    public LiveParticleEnumerable EnumerateLive() => new(this);

    public LiveEmitterEnumerable EnumerateEmitters() => new(this);

    /// <summary>
    /// Enumerates only presentation-visible, view-eligible emitters in the
    /// requested pass, retaining global spawn order. Renderers should use this
    /// workset instead of rejecting every logical emitter on every pass.
    /// </summary>
    public RenderableEmitterEnumerable EnumerateRenderableEmitters(
        ParticleRenderPass renderPass) => new(this, renderPass);

    public void CopyRenderableEmittersForOwners(
        ParticleRenderPass renderPass,
        IReadOnlySet<uint> attachedOwnerIds,
        bool includeUnattached,
        List<ParticleEmitter> destination,
        IReadOnlySet<uint>? excludedAttachedOwnerIds = null,
        UnattachedEmitterCellScope unattachedCellScope = UnattachedEmitterCellScope.Any)
    {
        ArgumentNullException.ThrowIfNull(attachedOwnerIds);
        ArgumentNullException.ThrowIfNull(destination);
        destination.Clear();
        LastRenderScopeEmitterVisitCount = 0;
        int passIndex = RenderPassIndex(renderPass);

        if (includeUnattached)
        {
            foreach (int handle in _renderableUnattachedHandlesByPass[passIndex])
            {
                LastRenderScopeEmitterVisitCount++;
                if (_byHandle.TryGetValue(handle, out ParticleEmitter? emitter)
                    && MatchesUnattachedCellScope(emitter, unattachedCellScope))
                {
                    destination.Add(emitter);
                }
            }
        }

        Dictionary<uint, OwnerEmitterBucket> owners = _ownerHandlesByPass[passIndex];
        foreach (uint ownerId in attachedOwnerIds)
        {
            if (excludedAttachedOwnerIds?.Contains(ownerId) == true
                || !owners.TryGetValue(ownerId, out OwnerEmitterBucket? bucket))
            {
                continue;
            }

            _scopeHandleScratch.Clear();
            bucket.CopyRenderableHandlesTo(_scopeHandleScratch);
            LastRenderScopeEmitterVisitCount += _scopeHandleScratch.Count;
            foreach (int handle in _scopeHandleScratch)
            {
                if (_byHandle.TryGetValue(handle, out ParticleEmitter? emitter))
                    destination.Add(emitter);
            }
        }

        destination.Sort(static (left, right) => left.Handle.CompareTo(right.Handle));
    }

    public bool HasRenderableEmittersInCell(ParticleRenderPass renderPass, uint cellId)
    {
        int passIndex = RenderPassIndex(renderPass);
        return _cellHandlesByPass[passIndex].TryGetValue(cellId, out OwnerEmitterBucket? bucket)
            && bucket.FirstRenderableHandle != 0;
    }

    public void CopyRenderableEmittersInCell(
        ParticleRenderPass renderPass,
        uint cellId,
        List<ParticleEmitter> destination)
    {
        ArgumentNullException.ThrowIfNull(destination);
        destination.Clear();
        LastRenderScopeEmitterVisitCount = 0;
        int passIndex = RenderPassIndex(renderPass);
        if (!_cellHandlesByPass[passIndex].TryGetValue(cellId, out OwnerEmitterBucket? bucket))
            return;

        _scopeHandleScratch.Clear();
        bucket.CopyRenderableHandlesTo(_scopeHandleScratch);
        LastRenderScopeEmitterVisitCount = _scopeHandleScratch.Count;
        foreach (int handle in _scopeHandleScratch)
        {
            if (_byHandle.TryGetValue(handle, out ParticleEmitter? emitter))
                destination.Add(emitter);
        }
    }

    private static bool MatchesUnattachedCellScope(
        ParticleEmitter emitter,
        UnattachedEmitterCellScope scope)
    {
        if (scope == UnattachedEmitterCellScope.Any)
            return true;
        uint low = emitter.OwnerCellId & 0xFFFFu;
        return scope == UnattachedEmitterCellScope.OutdoorCells
            ? low != 0 && low < 0x0100u
            : low >= 0x0100u;
    }

    public readonly struct LiveEmitterEnumerable : IEnumerable<ParticleEmitter>
    {
        private readonly ParticleSystem _owner;
        internal LiveEmitterEnumerable(ParticleSystem owner) => _owner = owner;

        public Enumerator GetEnumerator() => new(_owner);

        IEnumerator<ParticleEmitter> IEnumerable<ParticleEmitter>.GetEnumerator()
            => EnumerateBoxed(_owner).GetEnumerator();

        System.Collections.IEnumerator System.Collections.IEnumerable.GetEnumerator()
            => EnumerateBoxed(_owner).GetEnumerator();

        private static IEnumerable<ParticleEmitter> EnumerateBoxed(ParticleSystem owner)
        {
            foreach (int handle in owner._allHandles)
            {
                if (owner._byHandle.TryGetValue(handle, out ParticleEmitter? emitter))
                    yield return emitter;
            }
        }

        public struct Enumerator
        {
            private readonly ParticleSystem _owner;
            private SortedSet<int>.Enumerator _handles;

            internal Enumerator(ParticleSystem owner)
            {
                _owner = owner;
                _handles = owner._allHandles.GetEnumerator();
                Current = null!;
            }

            public ParticleEmitter Current { get; private set; }

            public bool MoveNext()
            {
                while (_handles.MoveNext())
                {
                    if (_owner._byHandle.TryGetValue(_handles.Current, out ParticleEmitter? emitter))
                    {
                        Current = emitter;
                        return true;
                    }
                }
                return false;
            }
        }
    }

    public readonly struct RenderableEmitterEnumerable : IEnumerable<ParticleEmitter>
    {
        private readonly ParticleSystem _owner;
        private readonly int _passIndex;

        internal RenderableEmitterEnumerable(ParticleSystem owner, ParticleRenderPass renderPass)
        {
            _owner = owner;
            _passIndex = RenderPassIndex(renderPass);
        }

        public Enumerator GetEnumerator() => new(_owner, _passIndex);

        IEnumerator<ParticleEmitter> IEnumerable<ParticleEmitter>.GetEnumerator()
            => EnumerateBoxed(_owner, _passIndex).GetEnumerator();

        System.Collections.IEnumerator System.Collections.IEnumerable.GetEnumerator()
            => EnumerateBoxed(_owner, _passIndex).GetEnumerator();

        private static IEnumerable<ParticleEmitter> EnumerateBoxed(
            ParticleSystem owner,
            int passIndex)
        {
            foreach (int handle in owner._renderableHandlesByPass[passIndex])
            {
                if (owner._byHandle.TryGetValue(handle, out ParticleEmitter? emitter))
                    yield return emitter;
            }
        }

        public struct Enumerator
        {
            private readonly ParticleSystem _owner;
            private SortedSet<int>.Enumerator _handles;

            internal Enumerator(ParticleSystem owner, int passIndex)
            {
                _owner = owner;
                _handles = owner._renderableHandlesByPass[passIndex].GetEnumerator();
                Current = null!;
            }

            public ParticleEmitter Current { get; private set; }

            public bool MoveNext()
            {
                while (_handles.MoveNext())
                {
                    if (_owner._byHandle.TryGetValue(_handles.Current, out ParticleEmitter? emitter))
                    {
                        Current = emitter;
                        return true;
                    }
                }
                return false;
            }
        }
    }

    public readonly struct LiveParticleEnumerable : IEnumerable<(ParticleEmitter Emitter, int Index)>
    {
        private readonly ParticleSystem _owner;
        internal LiveParticleEnumerable(ParticleSystem owner) => _owner = owner;

        public Enumerator GetEnumerator() => new(_owner);

        IEnumerator<(ParticleEmitter Emitter, int Index)> IEnumerable<(ParticleEmitter Emitter, int Index)>.GetEnumerator()
            => EnumerateLiveBoxed(_owner).GetEnumerator();

        System.Collections.IEnumerator System.Collections.IEnumerable.GetEnumerator()
            => EnumerateLiveBoxed(_owner).GetEnumerator();

        private static IEnumerable<(ParticleEmitter Emitter, int Index)> EnumerateLiveBoxed(ParticleSystem owner)
        {
            foreach (var handle in owner._allHandles)
            {
                if (!owner._byHandle.TryGetValue(handle, out var em))
                    continue;

                for (int i = 0; i < em.Particles.Length; i++)
                {
                    if (em.Particles[i].Alive)
                        yield return (em, i);
                }
            }
        }

        /// <summary>Zero-allocation struct enumerator for the `foreach` fast path.</summary>
        public struct Enumerator
        {
            private readonly ParticleSystem _owner;
            private SortedSet<int>.Enumerator _handles;
            private ParticleEmitter? _currentEmitter;
            private int _particleIdx;

            internal Enumerator(ParticleSystem owner)
            {
                _owner = owner;
                _handles = owner._allHandles.GetEnumerator();
                _currentEmitter = null;
                _particleIdx = -1;
            }

            public (ParticleEmitter Emitter, int Index) Current => (_currentEmitter!, _particleIdx);

            public bool MoveNext()
            {
                while (true)
                {
                    if (_currentEmitter is not null)
                    {
                        for (_particleIdx++; _particleIdx < _currentEmitter.Particles.Length; _particleIdx++)
                        {
                            if (_currentEmitter.Particles[_particleIdx].Alive)
                                return true;
                        }
                        _currentEmitter = null;
                    }

                    if (!_handles.MoveNext())
                        return false;

                    if (!_owner._byHandle.TryGetValue(_handles.Current, out var em))
                        continue;

                    _currentEmitter = em;
                    _particleIdx = -1;
                }
            }
        }
    }

    private void RemoveEmitter(int handle, ParticleEmitter emitter)
    {
        if (!_byHandle.Remove(handle))
            return;

        _allHandles.Remove(handle);
        _simulationHandles.Remove(handle);
        _worldSimulationHandles.Remove(handle);
        int passIndex = RenderPassIndex(emitter.RenderPass);
        _renderableHandlesByPass[passIndex].Remove(handle);
        if (emitter.AttachedObjectId == 0)
        {
            _renderableUnattachedHandlesByPass[passIndex].Remove(handle);
        }
        else if (_ownerHandlesByPass[passIndex].TryGetValue(
                     emitter.AttachedObjectId,
                     out OwnerEmitterBucket? bucket))
        {
            bucket.RemoveRenderable(handle);
            bucket.LogicalCount--;
            if (bucket.LogicalCount == 0)
                _ownerHandlesByPass[passIndex].Remove(emitter.AttachedObjectId);
        }
        if (_cellHandlesByPass[passIndex].TryGetValue(
                emitter.OwnerCellId,
                out OwnerEmitterBucket? cellBucket))
        {
            cellBucket.RemoveRenderable(handle);
            cellBucket.LogicalCount--;
            if (cellBucket.LogicalCount == 0)
                _cellHandlesByPass[passIndex].Remove(emitter.OwnerCellId);
        }
        NotifyEmitterDied(handle);
    }

    private void NotifyEmitterDied(int handle)
    {
        Action<int>? callbacks = EmitterDied;
        if (callbacks is null)
            return;

        List<Exception>? failures = null;
        foreach (Action<int> callback in callbacks.GetInvocationList().Cast<Action<int>>())
        {
            try
            {
                callback(handle);
            }
            catch (Exception error)
            {
                (failures ??= []).Add(error);
            }
        }

        if (failures is not null)
            throw new AggregateException(
                $"One or more emitter-death callbacks failed for handle {handle}.",
                failures);
    }

    private void AddEmitterToRenderIndexes(ParticleEmitter emitter)
    {
        int passIndex = RenderPassIndex(emitter.RenderPass);
        OwnerEmitterBucket? ownerBucket = null;
        if (emitter.AttachedObjectId != 0)
        {
            if (!_ownerHandlesByPass[passIndex].TryGetValue(
                    emitter.AttachedObjectId,
                    out ownerBucket))
            {
                ownerBucket = new OwnerEmitterBucket();
                _ownerHandlesByPass[passIndex].Add(emitter.AttachedObjectId, ownerBucket);
            }
            ownerBucket.LogicalCount++;
        }

        Dictionary<uint, OwnerEmitterBucket> cellBuckets = _cellHandlesByPass[passIndex];
        if (!cellBuckets.TryGetValue(emitter.OwnerCellId, out OwnerEmitterBucket? cellBucket))
        {
            cellBucket = new OwnerEmitterBucket();
            cellBuckets.Add(emitter.OwnerCellId, cellBucket);
        }
        cellBucket.LogicalCount++;

        if (IsRenderable(emitter))
        {
            _renderableHandlesByPass[passIndex].Add(emitter.Handle);
            if (emitter.AttachedObjectId == 0)
                _renderableUnattachedHandlesByPass[passIndex].Add(emitter.Handle);
            else
                ownerBucket!.AddRenderable(emitter.Handle);
            cellBucket.AddRenderable(emitter.Handle);
        }
    }

    private void RefreshRenderableIndex(ParticleEmitter emitter, bool wasRenderable)
    {
        bool isRenderable = IsRenderable(emitter);
        if (wasRenderable == isRenderable)
            return;

        SortedSet<int> index =
            _renderableHandlesByPass[RenderPassIndex(emitter.RenderPass)];
        if (isRenderable)
            index.Add(emitter.Handle);
        else
            index.Remove(emitter.Handle);

        int passIndex = RenderPassIndex(emitter.RenderPass);
        if (emitter.AttachedObjectId == 0)
        {
            if (isRenderable)
                _renderableUnattachedHandlesByPass[passIndex].Add(emitter.Handle);
            else
                _renderableUnattachedHandlesByPass[passIndex].Remove(emitter.Handle);
        }
        else if (_ownerHandlesByPass[passIndex].TryGetValue(
                     emitter.AttachedObjectId,
                     out OwnerEmitterBucket? bucket))
        {
            if (isRenderable)
                bucket.AddRenderable(emitter.Handle);
            else
                bucket.RemoveRenderable(emitter.Handle);
        }

        if (_cellHandlesByPass[passIndex].TryGetValue(
                emitter.OwnerCellId,
                out OwnerEmitterBucket? cellBucket))
        {
            if (isRenderable)
                cellBucket.AddRenderable(emitter.Handle);
            else
                cellBucket.RemoveRenderable(emitter.Handle);
        }
    }

    private void AddWorldSimulationHandle(int handle)
    {
        int index = _worldSimulationHandles.BinarySearch(handle);
        if (index < 0)
            _worldSimulationHandles.Insert(~index, handle);
    }

    private static bool IsRenderable(ParticleEmitter emitter)
        => emitter.PresentationVisible && emitter.ViewEligible;

    private static int RenderPassIndex(ParticleRenderPass renderPass)
    {
        int index = (int)renderPass;
        if ((uint)index >= 3u)
            throw new ArgumentOutOfRangeException(nameof(renderPass));
        return index;
    }

    private void AdvanceEmitter(ParticleEmitter em)
    {
        for (int i = 0; i < em.Particles.Length; i++)
        {
            ref var p = ref em.Particles[i];
            if (!p.Alive)
                continue;

            p.Age = EffectiveParticleAge(em, p);
            if (p.Lifetime <= 0f || p.Age >= p.Lifetime)
            {
                p.Alive = false;
                em.ActiveCount--;
                continue;
            }

            p.Position = ComputePosition(em, p);
            float tLife = Math.Clamp(p.Age / p.Lifetime, 0f, 1f);
            p.Size = Lerp(p.StartSize, p.EndSize, tLife);
            p.Rotation = Lerp(em.Desc.StartRotation, em.Desc.EndRotation, tLife);
            float alpha = Lerp(p.StartAlpha, p.EndAlpha, tLife);
            p.ColorArgb = Color32(alpha, em.Desc.StartColorArgb, em.Desc.EndColorArgb, tLife);
        }

        if (em.Finished || _time < em.StartedAt + em.Desc.StartDelay)
            return;

        while (ShouldEmitParticle(em))
        {
            if (!SpawnOne(em, allowWhenFull: false))
                break;
        }

        if (em.Desc.Birthrate <= 0f && em.Desc.EmitRate > 0f)
        {
            float dt = _time - em.LastEmitTime;
            em.EmittedAccumulator += dt * em.Desc.EmitRate;
            em.LastEmitTime = _time;
            while (em.EmittedAccumulator >= 1f)
            {
                em.EmittedAccumulator -= 1f;
                if (!SpawnOne(em, allowWhenFull: false))
                    break;
            }
        }
    }

    private void AdvanceDegradedEmitter(ParticleEmitter em, float dt)
    {
        bool infinite = em.Desc.TotalParticles == 0 && em.Desc.TotalDuration == 0f;
        if (infinite)
        {
            em.FrozenTime += dt;
            if (em.Desc.Birthrate <= 0f && em.Desc.EmitRate > 0f)
            {
                em.LastEmitTime = _time;
                em.EmittedAccumulator = 0f;
            }
            return;
        }

        for (int i = 0; i < em.Particles.Length; i++)
        {
            ref Particle particle = ref em.Particles[i];
            if (!particle.Alive)
                continue;

            particle.Age = _time - particle.SpawnedAt;
            if (particle.Lifetime <= 0f || particle.Age >= particle.Lifetime)
            {
                particle.Alive = false;
                em.ActiveCount--;
            }
        }

        if (!em.Finished && ShouldEmitParticle(em))
        {
            em.ActiveCount++;
            em.TotalEmitted++;
            em.LastEmitTime = _time;
            em.LastEmitOffset = em.AnchorPos;
        }
    }

    private bool ShouldEmitParticle(ParticleEmitter em)
    {
        var desc = em.Desc;
        if (desc.TotalParticles > 0 && em.TotalEmitted >= desc.TotalParticles)
            return false;

        if (em.ActiveCount >= desc.MaxParticles)
            return false;

        if (desc.Birthrate <= 0f)
            return false;

        return desc.EmitterKind switch
        {
            ParticleEmitterKind.BirthratePerSec => (_time - em.LastEmitTime) > desc.Birthrate,
            ParticleEmitterKind.BirthratePerMeter =>
                Vector3.DistanceSquared(em.AnchorPos, em.LastEmitOffset) > desc.Birthrate * desc.Birthrate,
            _ => false,
        };
    }

    private bool SpawnOne(ParticleEmitter em, bool allowWhenFull)
    {
        int slot = FindFreeSlot(em);
        if (slot < 0 && allowWhenFull)
            slot = FindOldestSlot(em);
        if (slot < 0)
            return false;

        ref var particle = ref em.Particles[slot];
        bool replacesLiveParticle = particle.Alive;
        particle = default;
        particle.Alive = true;
        particle.SpawnedAt = _time;
        particle.FrozenTimeAtSpawn = em.FrozenTime;
        particle.Lifetime = RandomLifespan(em.Desc);
        particle.EmissionOrigin = em.AnchorPos;
        particle.SpawnRotation = em.AnchorRot;

        Vector3 localOffset = RandomOffset(em.Desc);
        Vector3 localA = RandomVector(em.Desc.A, em.Desc.MinA, em.Desc.MaxA);
        Vector3 localB = RandomVector(em.Desc.B, em.Desc.MinB, em.Desc.MaxB);
        Vector3 localC = RandomVector(em.Desc.C, em.Desc.MinC, em.Desc.MaxC);

        if (localA == Vector3.Zero && em.Desc.InitialVelocity != Vector3.Zero)
        {
            localA = em.Desc.InitialVelocity;
            if (em.Desc.VelocityJitter > 0f)
            {
                localA += new Vector3(
                    RandomCentered(em.Desc.VelocityJitter),
                    RandomCentered(em.Desc.VelocityJitter),
                    RandomCentered(em.Desc.VelocityJitter));
            }
        }
        if (localB == Vector3.Zero && em.Desc.Gravity != Vector3.Zero)
            localB = em.Desc.Gravity;

        InitParticleVectors(em, ref particle, localOffset, localA, localB, localC);

        particle.Velocity = particle.A;
        particle.StartSize = RandomScale(em.Desc.StartSize, em.Desc.ScaleRand);
        particle.EndSize = RandomScale(em.Desc.EndSize, em.Desc.ScaleRand);
        particle.StartAlpha = RandomTrans(em.Desc.StartAlpha, em.Desc.TransRand);
        particle.EndAlpha = RandomTrans(em.Desc.EndAlpha, em.Desc.TransRand);
        particle.Size = particle.StartSize;
        particle.ColorArgb = Color32(particle.StartAlpha, em.Desc.StartColorArgb, em.Desc.EndColorArgb, 0f);
        particle.Position = ComputePosition(em, particle);

        em.TotalEmitted++;
        if (!replacesLiveParticle)
            em.ActiveCount++;
        em.LastEmitTime = _time;
        em.LastEmitOffset = em.AnchorPos;
        return true;
    }

    private float EffectiveParticleAge(ParticleEmitter emitter, in Particle particle)
        => _time - particle.SpawnedAt
           - (emitter.FrozenTime - particle.FrozenTimeAtSpawn);

    private Vector3 ComputePosition(ParticleEmitter em, Particle p)
    {
        float t = p.Age;
        Vector3 origin = (em.Desc.Flags & EmitterFlags.AttachLocal) != 0
            ? em.AnchorPos
            : p.EmissionOrigin;
        Vector3 offset = p.Offset;
        Vector3 a = p.A;
        Vector3 b = p.B;
        Vector3 c = p.C;

        return em.Desc.Type switch
        {
            ParticleType.Still => origin + offset,
            ParticleType.LocalVelocity or ParticleType.GlobalVelocity =>
                origin + offset + t * a,
            ParticleType.ParabolicLVGA or ParticleType.ParabolicLVLA or ParticleType.ParabolicGVGA =>
                origin + offset + t * a + 0.5f * t * t * b,
            ParticleType.ParabolicLVGAGR or ParticleType.ParabolicLVLALR or ParticleType.ParabolicGVGAGR =>
                origin + offset + t * a + 0.5f * t * t * b,
            ParticleType.Swarm =>
                origin + offset + t * a + new Vector3(
                    MathF.Cos(t * b.X) * c.X,
                    MathF.Sin(t * b.Y) * c.Y,
                    MathF.Cos(t * b.Z) * c.Z),
            ParticleType.Explode =>
                origin + offset + new Vector3(
                    (t * b.X + c.X * a.X) * t,
                    (t * b.Y + c.Y * a.X) * t,
                    (t * b.Z + c.Z * a.X + a.Z) * t),
            ParticleType.Implode =>
                origin + offset + MathF.Cos(a.X * t) * c + t * t * b,
            _ => origin + offset + t * a,
        };
    }

    private void InitParticleVectors(
        ParticleEmitter em,
        ref Particle particle,
        Vector3 localOffset,
        Vector3 localA,
        Vector3 localB,
        Vector3 localC)
    {
        particle.Offset = ToSpawnWorld(em, localOffset);
        particle.A = localA;
        particle.B = localB;
        particle.C = localC;

        switch (em.Desc.Type)
        {
            case ParticleType.LocalVelocity:
            case ParticleType.ParabolicLVGA:
                particle.A = ToSpawnWorld(em, localA);
                break;

            case ParticleType.ParabolicLVLA:
                particle.A = ToSpawnWorld(em, localA);
                particle.B = ToSpawnWorld(em, localB);
                break;

            case ParticleType.ParabolicLVGAGR:
                particle.A = ToSpawnWorld(em, localA);
                particle.C = localC;
                break;

            case ParticleType.Swarm:
                particle.A = ToSpawnWorld(em, localA);
                break;

            case ParticleType.Explode:
                particle.A = localA;
                particle.B = localB;
                particle.C = RandomExplodeDirection(localC);
                break;

            case ParticleType.Implode:
                particle.A = localA;
                particle.B = localB;
                particle.Offset = new Vector3(
                    particle.Offset.X * localC.X,
                    particle.Offset.Y * localC.Y,
                    particle.Offset.Z * localC.Z);
                particle.C = particle.Offset;
                break;

            case ParticleType.ParabolicLVLALR:
                particle.A = ToSpawnWorld(em, localA);
                particle.B = ToSpawnWorld(em, localB);
                particle.C = ToSpawnWorld(em, localC);
                break;

            case ParticleType.ParabolicGVGAGR:
                particle.C = localC;
                break;
        }
    }

    private static Vector3 ToSpawnWorld(ParticleEmitter em, Vector3 value)
        => em.AnchorRot == Quaternion.Identity ? value : Vector3.Transform(value, em.AnchorRot);

    private Vector3 RandomExplodeDirection(Vector3 localC)
    {
        float yaw = RandomRange(-MathF.PI, MathF.PI);
        float pitch = RandomRange(-MathF.PI, MathF.PI);
        float cosPitch = MathF.Cos(pitch);
        Vector3 c = new(
            MathF.Cos(yaw) * localC.X * cosPitch,
            MathF.Sin(yaw) * localC.Y * cosPitch,
            MathF.Sin(pitch) * localC.Z);

        return NormalizeCheckSmall(ref c) ? Vector3.Zero : c;
    }

    private int FindFreeSlot(ParticleEmitter em)
    {
        for (int i = 0; i < em.Particles.Length; i++)
        {
            if (!em.Particles[i].Alive)
                return i;
        }

        return -1;
    }

    private static int FindOldestSlot(ParticleEmitter em)
    {
        int slot = -1;
        float best = -1f;
        for (int i = 0; i < em.Particles.Length; i++)
        {
            ref var p = ref em.Particles[i];
            float r = p.Lifetime > 0f ? p.Age / p.Lifetime : 1f;
            if (r > best)
            {
                best = r;
                slot = i;
            }
        }

        return slot;
    }

    private static float RetailDistance(Vector3 a, Vector3 b)
    {
        float dx = a.X - b.X;
        float dy = a.Y - b.Y;
        float dz = a.Z - b.Z;
        float scale = MathF.Max(MathF.Abs(dx), MathF.Max(MathF.Abs(dy), MathF.Abs(dz)));
        if (float.IsNaN(scale) || float.IsInfinity(scale) || scale == 0f)
            return scale;

        dx /= scale;
        dy /= scale;
        dz /= scale;
        return scale * MathF.Sqrt(dx * dx + dy * dy + dz * dz);
    }

    private float RandomLifespan(EmitterDesc desc)
    {
        float lifespan = desc.Lifespan > 0f ? desc.Lifespan : (desc.LifetimeMin + desc.LifetimeMax) * 0.5f;
        float rand = desc.LifespanRand > 0f ? desc.LifespanRand : MathF.Abs(desc.LifetimeMax - desc.LifetimeMin) * 0.5f;
        float value = lifespan + RandomCentered(rand);
        if (value <= 0f && desc.LifetimeMax > 0f)
            value = Lerp(desc.LifetimeMin, desc.LifetimeMax, (float)_rng.NextDouble());
        return MathF.Max(0f, value);
    }

    private Vector3 RandomOffset(EmitterDesc desc)
    {
        float min = MathF.Min(desc.MinOffset, desc.MaxOffset);
        float max = MathF.Max(desc.MinOffset, desc.MaxOffset);
        if (max <= 0f)
            return Vector3.Zero;

        Vector3 axis = NormalizeOrZero(desc.OffsetDir);
        Vector3 v = new(
            RandomCentered(1f),
            RandomCentered(1f),
            RandomCentered(1f));

        if (axis != Vector3.Zero)
            v -= axis * Vector3.Dot(v, axis);

        if (v.LengthSquared() < 1e-8f)
            v = axis != Vector3.Zero ? Perpendicular(axis) : Vector3.UnitX;
        else
            v = Vector3.Normalize(v);

        return v * Lerp(min, max, (float)_rng.NextDouble());
    }

    private Vector3 RandomVector(Vector3 direction, float min, float max)
    {
        if (direction == Vector3.Zero)
            return Vector3.Zero;

        if (max < min)
            (min, max) = (max, min);

        return direction * Lerp(min, max, (float)_rng.NextDouble());
    }

    private float RandomScale(float baseValue, float rand)
        => Math.Clamp(baseValue + RandomCentered(rand), 0.1f, 10f);

    private float RandomTrans(float baseValue, float rand)
        => Math.Clamp(baseValue + RandomCentered(rand), 0f, 1f);

    private float RandomCentered(float halfWidth)
        => ((float)_rng.NextDouble() - 0.5f) * 2f * halfWidth;

    private float RandomRange(float min, float max)
        => Lerp(min, max, (float)_rng.NextDouble());

    private static float Lerp(float a, float b, float t) => a + (b - a) * t;

    private static Vector3 NormalizeOrZero(Vector3 v)
        => v.LengthSquared() > 1e-8f ? Vector3.Normalize(v) : Vector3.Zero;

    private static bool NormalizeCheckSmall(ref Vector3 v)
    {
        float length = v.Length();
        if (length < 1e-8f)
            return true;

        v /= length;
        return false;
    }

    private static Vector3 Perpendicular(Vector3 v)
    {
        Vector3 basis = MathF.Abs(v.X) < 0.9f ? Vector3.UnitX : Vector3.UnitY;
        return Vector3.Normalize(Vector3.Cross(v, basis));
    }

    private static uint Color32(float alpha, uint startArgb, uint endArgb, float t)
    {
        byte sr = (byte)((startArgb >> 16) & 0xFF);
        byte sg = (byte)((startArgb >> 8) & 0xFF);
        byte sb = (byte)(startArgb & 0xFF);
        byte er = (byte)((endArgb >> 16) & 0xFF);
        byte eg = (byte)((endArgb >> 8) & 0xFF);
        byte eb = (byte)(endArgb & 0xFF);

        byte r = (byte)Math.Clamp(sr + (er - sr) * t, 0f, 255f);
        byte g = (byte)Math.Clamp(sg + (eg - sg) * t, 0f, 255f);
        byte b = (byte)Math.Clamp(sb + (eb - sb) * t, 0f, 255f);
        byte a = (byte)Math.Clamp(alpha * 255f, 0f, 255f);
        return ((uint)a << 24) | ((uint)r << 16) | ((uint)g << 8) | b;
    }
}
