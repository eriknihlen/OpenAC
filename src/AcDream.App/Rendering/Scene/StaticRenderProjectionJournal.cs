using AcDream.App.Rendering.Wb;
using AcDream.App.Streaming;
using AcDream.Core.World;

namespace AcDream.App.Rendering.Scene;

internal interface IRenderStaticProjectionJournalSink
{
    void Reconcile(
        LandblockBuild build,
        GpuLandblockSpatialPublication publication);

    void Retire(GpuLandblockRetirement retirement);
}

internal sealed class StaticRenderProjectionJournal :
    IRenderStaticProjectionJournalSink
{
    private const byte StaticEntityDomain = 1;
    private const byte EnvCellShellDomain = 2;

    private readonly RenderProjectionJournal _journal;
    private readonly Dictionary<uint, Dictionary<RenderProjectionId, TrackedProjection>>
        _byLandblock = [];
    private readonly Dictionary<RenderProjectionId, TrackedProjection>
        _trackedById = [];
    private readonly Dictionary<WorldEntity, RenderProjectionId> _idByEntity =
        new(ReferenceEqualityComparer.Instance);
    private readonly Dictionary<uint, List<WorldEntity>> _entitiesByLandblock =
        [];
    private readonly List<RenderProjectionRecord> _candidates = [];
    private readonly List<RenderProjectionId> _removed = [];
    private ulong _nextIncarnation = 1;

    public StaticRenderProjectionJournal(RenderProjectionJournal journal)
    {
        _journal = journal ?? throw new ArgumentNullException(nameof(journal));
    }

    public int ProjectionCount { get; private set; }
    public int LandblockCount => _byLandblock.Count;

    public void Reconcile(
        LandblockBuild build,
        GpuLandblockSpatialPublication publication)
    {
        ArgumentNullException.ThrowIfNull(build);
        ArgumentNullException.ThrowIfNull(publication);
        uint landblockId = Canonicalize(publication.LandblockId);
        if (Canonicalize(build.LandblockId) != landblockId)
        {
            throw new ArgumentException(
                "Static render projection publication belongs to another landblock.",
                nameof(build));
        }

        if (!publication.RequiresActivation)
            return;

        if (publication.RenderTraversalOrder == 0)
        {
            throw new InvalidOperationException(
                $"Landblock 0x{landblockId:X8} has no render traversal order.");
        }
        _candidates.Clear();
        for (int i = 0; i < publication.Landblock.Entities.Count; i++)
        {
            WorldEntity entity = publication.Landblock.Entities[i];
            if (entity.ServerGuid == 0)
            {
                _candidates.Add(ProjectStaticEntity(
                    landblockId,
                    entity,
                    RenderTraversalSortKey.Compose(
                        publication.RenderTraversalOrder,
                        i)));
            }
        }

        if (build.EnvCells is { } envCells)
        {
            for (int i = 0; i < envCells.Shells.Length; i++)
                _candidates.Add(ProjectEnvCellShell(landblockId, envCells.Shells[i]));
        }

        _candidates.Sort(ProjectionRecordComparer.Instance);
        if (!_byLandblock.TryGetValue(
                landblockId,
                out Dictionary<RenderProjectionId, TrackedProjection>? current))
        {
            current = [];
            _byLandblock.Add(landblockId, current);
        }

        _removed.Clear();
        foreach (RenderProjectionId id in current.Keys)
            _removed.Add(id);

        for (int i = 0; i < _candidates.Count; i++)
        {
            RenderProjectionRecord candidate = _candidates[i];
            if (current.TryGetValue(
                    candidate.Id,
                    out TrackedProjection? retained)
                && retained is not null)
            {
                _removed.Remove(candidate.Id);
                RenderProjectionRecord accepted = candidate with
                {
                    OwnerIncarnation = retained.Record.OwnerIncarnation,
                    PreviousTransform = new PreviousRenderTransform(
                        retained.Record.Transform.LocalToWorld),
                };
                if (accepted.Source.GeometryFingerprint
                        == retained.Record.Source.GeometryFingerprint
                    && accepted.Source.AppearanceFingerprint
                        == retained.Record.Source.AppearanceFingerprint
                    && accepted.EntityPayload.IsBuildingShell
                        == retained.Record.EntityPayload.IsBuildingShell
                    && accepted.EntityPayload.CasterIdentity
                        == retained.Record.EntityPayload.CasterIdentity)
                {
                    accepted = accepted with
                    {
                        EntityPayload = retained.Record.EntityPayload,
                    };
                }
                if (accepted.ProjectionClass
                    != retained.Record.ProjectionClass)
                {
                    _journal.Unregister(
                        retained.Record.Id,
                        retained.Record.OwnerIncarnation);
                    accepted = accepted with
                    {
                        OwnerIncarnation = NextIncarnation(),
                    };
                    _journal.Register(in accepted);
                    retained.Record = accepted;
                    continue;
                }

                if (accepted != retained.Record)
                {
                    _journal.AppendDifference(retained.Record, accepted);
                    retained.Record = accepted;
                }
                continue;
            }

            RenderProjectionRecord registered = candidate with
            {
                OwnerIncarnation = NextIncarnation(),
            };
            _journal.Register(in registered);
            var tracked = new TrackedProjection(registered);
            current.Add(registered.Id, tracked);
            _trackedById.Add(registered.Id, tracked);
            ProjectionCount++;
        }

        _removed.Sort(ProjectionIdComparer.Instance);
        for (int i = 0; i < _removed.Count; i++)
        {
            RenderProjectionId id = _removed[i];
            TrackedProjection omitted = current[id];
            _journal.Unregister(id, omitted.Record.OwnerIncarnation);
            current.Remove(id);
            _trackedById.Remove(id);
            ProjectionCount--;
        }

        if (current.Count == 0)
            _byLandblock.Remove(landblockId);
        ReplaceStaticEntityMap(landblockId, publication.Landblock.Entities);
    }

    public void Retire(GpuLandblockRetirement retirement)
    {
        ArgumentNullException.ThrowIfNull(retirement);
        uint landblockId = Canonicalize(retirement.LandblockId);
        if (!_byLandblock.Remove(
                landblockId,
                out Dictionary<RenderProjectionId, TrackedProjection>? current))
        {
            return;
        }

        _removed.Clear();
        foreach (RenderProjectionId id in current.Keys)
            _removed.Add(id);
        _removed.Sort(ProjectionIdComparer.Instance);
        for (int i = 0; i < _removed.Count; i++)
        {
            TrackedProjection tracked = current[_removed[i]];
            _journal.Unregister(
                tracked.Record.Id,
                tracked.Record.OwnerIncarnation);
        }

        ProjectionCount -= current.Count;
        foreach (RenderProjectionId id in current.Keys)
            _trackedById.Remove(id);
        RemoveStaticEntityMap(landblockId);
    }

    public void Clear(RenderSceneGeneration replacementGeneration)
    {
        _byLandblock.Clear();
        _trackedById.Clear();
        _idByEntity.Clear();
        _entitiesByLandblock.Clear();
        _candidates.Clear();
        _removed.Clear();
        ProjectionCount = 0;
        _journal.Clear(replacementGeneration);
    }

    public void SynchronizeActiveAnimatedSources(
        IReadOnlyList<WorldEntity> activeEntities)
    {
        ArgumentNullException.ThrowIfNull(activeEntities);
        for (int i = 0; i < activeEntities.Count; i++)
        {
            WorldEntity entity = activeEntities[i];
            if (!_idByEntity.TryGetValue(
                    entity,
                    out RenderProjectionId id)
                || !_trackedById.TryGetValue(
                    id,
                    out TrackedProjection? tracked))
            {
                continue;
            }

            RenderProjectionRecord current =
                RenderProjectionRecordFactory.ProjectEntity(
                    id,
                    RenderProjectionClass.ActiveAnimatedStatic,
                    tracked.Record.OwnerIncarnation,
                    tracked.Record.Residency.OwnerLandblockId,
                    tracked.Record.Residency.FullCellId,
                    entity,
                    spatiallyVisible: true,
                    casterIdentity: entity.IsBuildingShell
                        ? RenderCasterIdentityKind.Building
                        : RenderCasterIdentityKind.OutdoorStatic) with
                {
                    PreviousTransform = new PreviousRenderTransform(
                        tracked.Record.Transform.LocalToWorld),
                    SortKey = tracked.Record.SortKey,
                };
            if (tracked.Record.ProjectionClass
                != RenderProjectionClass.ActiveAnimatedStatic)
            {
                _journal.Register(in current);
            }
            else
            {
                _journal.AppendDifference(tracked.Record, current);
            }
            tracked.Record = current;
        }
    }

    internal bool TryGet(
        RenderProjectionId id,
        out RenderProjectionRecord record)
    {
        foreach (Dictionary<RenderProjectionId, TrackedProjection> projections
                 in _byLandblock.Values)
        {
            if (projections.TryGetValue(
                    id,
                    out TrackedProjection? tracked)
                && tracked is not null)
            {
                record = tracked.Record;
                return true;
            }
        }

        record = default;
        return false;
    }

    internal static RenderProjectionId StaticEntityId(
        uint landblockId,
        uint localEntityId) =>
        CreateProjectionId(
            StaticEntityDomain,
            Canonicalize(landblockId),
            localEntityId);

    internal static RenderProjectionId EnvCellShellId(
        uint landblockId,
        uint cellId) =>
        CreateProjectionId(
            EnvCellShellDomain,
            Canonicalize(landblockId),
            cellId);

    private RenderProjectionRecord ProjectStaticEntity(
        uint landblockId,
        WorldEntity entity,
        RenderSortKey sortKey)
    {
        RenderProjectionId id = StaticEntityId(landblockId, entity.Id);
        uint fullCellId = entity.ParentCellId ?? landblockId;
        return RenderProjectionRecordFactory.ProjectEntity(
            id,
            InteriorEntityPartition.IsIndoorCellId(entity.ParentCellId)
                ? RenderProjectionClass.IndoorCellStatic
                : RenderProjectionClass.OutdoorStatic,
            default,
            landblockId,
            fullCellId,
            entity,
            spatiallyVisible: true,
            casterIdentity: entity.IsBuildingShell
                ? RenderCasterIdentityKind.Building
                : RenderCasterIdentityKind.OutdoorStatic) with
        {
            SortKey = sortKey,
        };
    }

    private static RenderProjectionRecord ProjectEnvCellShell(
        uint landblockId,
        EnvCellShellPlacement shell)
    {
        StableRenderHash128 transformHash = StableRenderHash128.Create();
        transformHash.Add(shell.WorldPosition);
        transformHash.Add(shell.Rotation);
        transformHash.Add(1.0f);
        RenderSceneHash128 transformFingerprint = transformHash.Finish();

        StableRenderHash128 geometryHash = StableRenderHash128.Create();
        geometryHash.Add(shell.GeometryId);
        geometryHash.Add(shell.EnvironmentId);
        geometryHash.Add(shell.CellStructure);
        geometryHash.Add(shell.Surfaces.Length);
        for (int i = 0; i < shell.Surfaces.Length; i++)
            geometryHash.Add(shell.Surfaces[i]);
        RenderSceneHash128 geometryFingerprint = geometryHash.Finish();

        RenderProjectionId id = EnvCellShellId(landblockId, shell.CellId);
        return new RenderProjectionRecord(
            id,
            RenderProjectionClass.IndoorCellStatic,
            default,
            new RenderTransform(
                shell.WorldPosition,
                shell.Rotation,
                1.0f,
                shell.Transform),
            new PreviousRenderTransform(shell.Transform),
            new RenderMeshSet(
                RenderAssetHandle.FromRaw(shell.GeometryId),
                1,
                0),
            new RenderMaterialVariant(0, 0, 1.0f),
            new RenderSpatialResidency(
                RenderSpatialBucket.FromRaw(shell.CellId),
                landblockId,
                shell.CellId),
            new RenderWorldBounds(
                shell.WorldBounds.Min,
                shell.WorldBounds.Max),
            RenderProjectionFlags.Draw
                | RenderProjectionFlags.SpatiallyResident,
            default,
            id.ToSortKey(),
            RenderDirtyMask.All,
            new RenderSourceMetadata(
                shell.CellId,
                0,
                unchecked((uint)shell.GeometryId),
                shell.CellId,
                shell.CellId,
                0,
                transformFingerprint,
                geometryFingerprint,
                RenderSceneHash128.Empty));
    }

    private RenderOwnerIncarnation NextIncarnation()
    {
        if (_nextIncarnation == ulong.MaxValue)
        {
            throw new InvalidOperationException(
                "Static render-projection incarnation exhausted.");
        }

        return RenderOwnerIncarnation.FromRaw(_nextIncarnation++);
    }

    private static RenderProjectionId CreateProjectionId(
        byte domain,
        uint canonicalLandblockId,
        uint localId) =>
        RenderProjectionId.FromRaw(
            ((ulong)domain << 56)
            | ((ulong)(canonicalLandblockId >> 16) << 32)
            | localId);

    private static uint Canonicalize(uint landblockId) =>
        (landblockId & 0xFFFF0000u) | 0xFFFFu;

    private void ReplaceStaticEntityMap(
        uint landblockId,
        IReadOnlyList<WorldEntity> entities)
    {
        RemoveStaticEntityMap(landblockId);
        var retained = new List<WorldEntity>();
        for (int i = 0; i < entities.Count; i++)
        {
            WorldEntity entity = entities[i];
            if (entity.ServerGuid != 0)
                continue;
            RenderProjectionId id = StaticEntityId(landblockId, entity.Id);
            if (!_trackedById.ContainsKey(id))
                continue;
            _idByEntity.Add(entity, id);
            retained.Add(entity);
        }
        if (retained.Count > 0)
            _entitiesByLandblock.Add(landblockId, retained);
    }

    private void RemoveStaticEntityMap(uint landblockId)
    {
        if (!_entitiesByLandblock.Remove(
                landblockId,
                out List<WorldEntity>? entities))
        {
            return;
        }
        for (int i = 0; i < entities.Count; i++)
            _idByEntity.Remove(entities[i]);
    }

    private sealed class TrackedProjection(RenderProjectionRecord record)
    {
        public RenderProjectionRecord Record { get; set; } = record;
    }

    private sealed class ProjectionRecordComparer :
        IComparer<RenderProjectionRecord>
    {
        public static ProjectionRecordComparer Instance { get; } = new();

        public int Compare(RenderProjectionRecord x, RenderProjectionRecord y) =>
            x.Id.CompareTo(y.Id);
    }

    private sealed class ProjectionIdComparer : IComparer<RenderProjectionId>
    {
        public static ProjectionIdComparer Instance { get; } = new();

        public int Compare(RenderProjectionId x, RenderProjectionId y) =>
            x.CompareTo(y);
    }
}
