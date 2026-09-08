using System.Numerics;
using AcDream.App.Rendering.Wb;
using AcDream.Core.Selection;
using AcDream.Core.World;

namespace AcDream.App.Rendering.Scene;

internal readonly record struct RenderSceneHash128(ulong Low, ulong High)
{
    public static RenderSceneHash128 Empty { get; } = new(0, 0);

    public override string ToString() => $"{High:X16}{Low:X16}";
}

internal readonly record struct CurrentRenderProjectionFingerprint(
    InteriorEntityPartition.ProjectionClass ProjectionClass,
    uint LandblockId,
    uint EntityId,
    uint ServerGuid,
    uint SourceId,
    uint ParentCellId,
    uint EffectCellId,
    uint BuildingShellAnchorCellId,
    uint Flags,
    int MeshCount,
    RenderSceneHash128 Transform,
    RenderSceneHash128 Geometry,
    RenderSceneHash128 Appearance);

internal enum CurrentRenderPViewRoute : byte
{
    LandscapeOutdoorStatic,
    LandscapeBuildingShell,
    LandscapeOutsideDynamic,
    LookInObject,
    CellStatic,
    DynamicLast,
}

internal readonly record struct CurrentRenderPViewCandidateFingerprint(
    int Sequence,
    CurrentRenderPViewRoute Route,
    int RouteIndex,
    uint CellId,
    CurrentRenderProjectionFingerprint Projection);

internal readonly record struct CurrentRenderDispatcherFingerprint(
    int Sequence,
    int DrawSequence,
    WbDrawDispatcher.EntitySet Set,
    int MeshRefIndex,
    uint TupleLandblockId,
    uint CacheLandblockId,
    CurrentRenderProjectionFingerprint Projection,
    uint GfxObjId,
    Matrix4x4 PartTransform,
    RenderSceneHash128 SurfaceOverrides);

internal readonly record struct CurrentRenderDispatcherSubmission(
    int VisibleInstanceCount,
    int ImmediateInstanceCount,
    int OpaqueGroupCount,
    int TransparentGroupCount,
    bool TransparentDeferred,
    RenderSceneHash128 OpaqueDigest,
    RenderSceneHash128 TransparentDigest,
    RenderSceneHash128 TransparentSetDigest,
    RenderSceneHash128 Digest);

internal readonly record struct CurrentRenderSelectionFingerprint(
    int Sequence,
    uint ServerGuid,
    uint LocalEntityId,
    int PartIndex,
    uint GfxObjId,
    Matrix4x4 LocalToWorld,
    RenderSceneHash128 Geometry);

internal readonly record struct CurrentRenderSceneOracleSnapshot(
    bool Enabled,
    ulong CompletedFrameSequence,
    int AbortedFrames,
    int ProjectionCount,
    int OutdoorStaticCount,
    int CellStaticCount,
    int DynamicCount,
    int CellBucketCount,
    RenderSceneHash128 Digest,
    ulong CompletedPViewFrameSequence,
    int AbortedPViewFrames,
    int PViewCandidateCount,
    RenderSceneHash128 PViewDigest,
    ulong DispatcherFrameSequence,
    int AbortedDispatcherFrames,
    int DispatcherDrawCount,
    int DispatcherEntityCount,
    int DispatcherMeshRefCount,
    int DispatcherInstanceCount,
    int DispatcherOpaqueGroupCount,
    int DispatcherTransparentGroupCount,
    RenderSceneHash128 DispatcherDigest,
    ulong CompletedSelectionFrameSequence,
    int AbortedSelectionFrames,
    int SelectionPartCount,
    RenderSceneHash128 SelectionDigest)
{
    public static CurrentRenderSceneOracleSnapshot Disabled { get; } = new(
        Enabled: false,
        CompletedFrameSequence: 0,
        AbortedFrames: 0,
        ProjectionCount: 0,
        OutdoorStaticCount: 0,
        CellStaticCount: 0,
        DynamicCount: 0,
        CellBucketCount: 0,
        Digest: RenderSceneHash128.Empty,
        CompletedPViewFrameSequence: 0,
        AbortedPViewFrames: 0,
        PViewCandidateCount: 0,
        PViewDigest: RenderSceneHash128.Empty,
        DispatcherFrameSequence: 0,
        AbortedDispatcherFrames: 0,
        DispatcherDrawCount: 0,
        DispatcherEntityCount: 0,
        DispatcherMeshRefCount: 0,
        DispatcherInstanceCount: 0,
        DispatcherOpaqueGroupCount: 0,
        DispatcherTransparentGroupCount: 0,
        DispatcherDigest: RenderSceneHash128.Empty,
        CompletedSelectionFrameSequence: 0,
        AbortedSelectionFrames: 0,
        SelectionPartCount: 0,
        SelectionDigest: RenderSceneHash128.Empty);
}

internal interface ICurrentRenderSceneOracleSnapshotSource
{
    CurrentRenderSceneOracleSnapshot Snapshot { get; }
}

internal interface ICurrentRenderPViewObserver
{
    void BeginPViewFrame();

    void ObservePViewBucket(
        CurrentRenderPViewRoute route,
        int routeIndex,
        uint cellId,
        IReadOnlyList<WorldEntity> entities);

    void CompletePViewFrame();

    void AbortPViewFrame();
}

internal interface ICurrentRenderDispatcherObserver
{
    void BeginDispatcherFrame();

    void ObserveDispatcherDraw(
        WbDrawDispatcher.EntitySet set,
        int entitiesWalked,
        IReadOnlyList<(
            WorldEntity Entity,
            int MeshRefIndex,
            uint LandblockId)> accepted);

    void ObserveDispatcherSubmission(
        in CurrentRenderDispatcherSubmission submission);

    void AbortDispatcherFrame();
}

internal interface ICurrentRenderSelectionObserver
{
    void BeginSelectionFrame();

    void ObserveSelectionPart(
        uint serverGuid,
        uint localEntityId,
        int partIndex,
        uint gfxObjId,
        Matrix4x4 localToWorld,
        RetailSelectionMesh mesh);

    void CompleteSelectionFrame();

    void AbortSelectionFrame();
}

internal sealed class CurrentRenderSceneOracle :
    InteriorEntityPartition.IObserver,
    ICurrentRenderPViewObserver,
    ICurrentRenderDispatcherObserver,
    ICurrentRenderSelectionObserver,
    ICurrentRenderSceneOracleSnapshotSource
{
    private readonly List<CurrentRenderProjectionFingerprint> _projections = [];
    private readonly Dictionary<WorldEntity, CurrentRenderProjectionFingerprint>
        _projectionByEntity =
            new(ReferenceEqualityComparer.Instance);
    private readonly List<CurrentRenderPViewCandidateFingerprint>
        _pviewCandidates = [];
    private readonly List<CurrentRenderDispatcherFingerprint>
        _dispatcherCandidates = [];
    private readonly List<CurrentRenderDispatcherSubmission>
        _dispatcherSubmissions = [];
    private readonly Dictionary<WorldEntity, CurrentRenderProjectionFingerprint>
        _dispatcherProjectionByEntity =
            new(ReferenceEqualityComparer.Instance);
    private readonly List<CurrentRenderSelectionFingerprint>
        _selectionParts = [];
    private readonly Dictionary<uint, SelectionGeometryCacheEntry>
        _selectionGeometryByGfxObj = [];
    private ulong _frameSequence;
    private int _abortedFrames;
    private int _outdoorStaticCount;
    private int _cellStaticCount;
    private int _dynamicCount;
    private bool _frameOpen;
    private ulong _pviewFrameSequence;
    private int _abortedPViewFrames;
    private bool _pviewFrameOpen;
    private StableRenderHash128 _pviewHash;
    private ulong _dispatcherFrameSequence;
    private int _abortedDispatcherFrames;
    private int _dispatcherDrawCount;
    private int _dispatcherEntityCount;
    private int _dispatcherInstanceCount;
    private int _dispatcherOpaqueGroupCount;
    private int _dispatcherTransparentGroupCount;
    private bool _dispatcherFrameOpen;
    private StableRenderHash128 _dispatcherHash;
    private ulong _selectionFrameSequence;
    private int _abortedSelectionFrames;
    private bool _selectionFrameOpen;
    private StableRenderHash128 _selectionHash;

    public CurrentRenderSceneOracleSnapshot Snapshot { get; private set; } =
        CurrentRenderSceneOracleSnapshot.Disabled with { Enabled = true };

    internal IReadOnlyList<CurrentRenderProjectionFingerprint> Projections =>
        _projections;
    internal IReadOnlyList<CurrentRenderPViewCandidateFingerprint>
        PViewCandidates => _pviewCandidates;
    internal IReadOnlyList<CurrentRenderDispatcherFingerprint>
        DispatcherCandidates => _dispatcherCandidates;
    internal IReadOnlyList<CurrentRenderDispatcherSubmission>
        DispatcherSubmissions => _dispatcherSubmissions;
    internal IReadOnlyList<CurrentRenderSelectionFingerprint>
        SelectionParts => _selectionParts;

    public void BeginFrame()
    {
        if (_frameOpen)
        {
            throw new InvalidOperationException(
                "The current render-scene oracle cannot begin a second frame before completing or aborting the first.");
        }

        _frameOpen = true;
        _projections.Clear();
        _projectionByEntity.Clear();
        _outdoorStaticCount = 0;
        _cellStaticCount = 0;
        _dynamicCount = 0;
    }

    public void Observe(
        uint landblockId,
        WorldEntity entity,
        InteriorEntityPartition.ProjectionClass projectionClass)
    {
        if (!_frameOpen)
        {
            throw new InvalidOperationException(
                "The current render-scene oracle received a projection outside an open frame.");
        }
        ArgumentNullException.ThrowIfNull(entity);

        switch (projectionClass)
        {
            case InteriorEntityPartition.ProjectionClass.OutdoorStatic:
                _outdoorStaticCount++;
                break;
            case InteriorEntityPartition.ProjectionClass.CellStatic:
                _cellStaticCount++;
                break;
            case InteriorEntityPartition.ProjectionClass.Dynamic:
                _dynamicCount++;
                break;
            default:
                throw new ArgumentOutOfRangeException(
                    nameof(projectionClass),
                    projectionClass,
                    "Unknown current render projection class.");
        }

        CurrentRenderProjectionFingerprint fingerprint = CreateFingerprint(
            landblockId,
            entity,
            projectionClass);
        _projections.Add(fingerprint);
        if (!_projectionByEntity.TryAdd(entity, fingerprint))
        {
            throw new InvalidOperationException(
                $"The current render partition observed entity 0x{entity.Id:X8} more than once.");
        }
    }

    public void Complete(InteriorEntityPartition.Result result)
    {
        ArgumentNullException.ThrowIfNull(result);
        if (!_frameOpen)
        {
            throw new InvalidOperationException(
                "The current render-scene oracle cannot complete without an open frame.");
        }

        int cellStaticCount = 0;
        foreach (List<WorldEntity> entities in result.ByCell.Values)
            cellStaticCount = checked(cellStaticCount + entities.Count);
        if (result.OutdoorStatic.Count != _outdoorStaticCount
            || cellStaticCount != _cellStaticCount
            || result.Dynamics.Count != _dynamicCount
            || _projections.Count
                != checked(_outdoorStaticCount + _cellStaticCount + _dynamicCount))
        {
            throw new InvalidOperationException(
                "The current render-scene oracle diverged from the partition it observed.");
        }

        _projections.Sort(CurrentRenderProjectionFingerprintComparer.Instance);
        StableRenderHash128 aggregate = StableRenderHash128.Create();
        aggregate.Add(_projections.Count);
        foreach (CurrentRenderProjectionFingerprint projection in _projections)
            AddProjection(ref aggregate, in projection);

        _frameOpen = false;
        Snapshot = new CurrentRenderSceneOracleSnapshot(
            Enabled: true,
            CompletedFrameSequence: checked(++_frameSequence),
            AbortedFrames: _abortedFrames,
            ProjectionCount: _projections.Count,
            OutdoorStaticCount: _outdoorStaticCount,
            CellStaticCount: _cellStaticCount,
            DynamicCount: _dynamicCount,
            CellBucketCount: result.ByCell.Count,
            Digest: aggregate.Finish(),
            CompletedPViewFrameSequence: Snapshot.CompletedPViewFrameSequence,
            AbortedPViewFrames: _abortedPViewFrames,
            PViewCandidateCount: Snapshot.PViewCandidateCount,
            PViewDigest: Snapshot.PViewDigest,
            DispatcherFrameSequence: Snapshot.DispatcherFrameSequence,
            AbortedDispatcherFrames: _abortedDispatcherFrames,
            DispatcherDrawCount: Snapshot.DispatcherDrawCount,
            DispatcherEntityCount: Snapshot.DispatcherEntityCount,
            DispatcherMeshRefCount: Snapshot.DispatcherMeshRefCount,
            DispatcherInstanceCount: Snapshot.DispatcherInstanceCount,
            DispatcherOpaqueGroupCount: Snapshot.DispatcherOpaqueGroupCount,
            DispatcherTransparentGroupCount:
                Snapshot.DispatcherTransparentGroupCount,
            DispatcherDigest: Snapshot.DispatcherDigest,
            CompletedSelectionFrameSequence:
                Snapshot.CompletedSelectionFrameSequence,
            AbortedSelectionFrames: _abortedSelectionFrames,
            SelectionPartCount: Snapshot.SelectionPartCount,
            SelectionDigest: Snapshot.SelectionDigest);
    }

    public void AbortFrame()
    {
        if (!_frameOpen)
            return;

        _frameOpen = false;
        _projections.Clear();
        _outdoorStaticCount = 0;
        _cellStaticCount = 0;
        _dynamicCount = 0;
        _abortedFrames = checked(_abortedFrames + 1);
        Snapshot = Snapshot with { AbortedFrames = _abortedFrames };
    }

    public void BeginPViewFrame()
    {
        if (_pviewFrameOpen)
        {
            throw new InvalidOperationException(
                "The current render-scene oracle cannot begin a second PView frame before completing or aborting the first.");
        }

        _pviewFrameOpen = true;
        _pviewCandidates.Clear();
        _pviewHash = StableRenderHash128.Create();
    }

    public void ObservePViewBucket(
        CurrentRenderPViewRoute route,
        int routeIndex,
        uint cellId,
        IReadOnlyList<WorldEntity> entities)
    {
        if (!_pviewFrameOpen)
        {
            throw new InvalidOperationException(
                "The current render-scene oracle received a PView bucket outside an open frame.");
        }
        ArgumentNullException.ThrowIfNull(entities);

        _pviewHash.Add((byte)0xB1);
        _pviewHash.Add((byte)route);
        _pviewHash.Add(routeIndex);
        _pviewHash.Add(cellId);
        _pviewHash.Add(entities.Count);
        foreach (WorldEntity entity in entities)
        {
            if (!_projectionByEntity.TryGetValue(entity, out var projection))
            {
                throw new InvalidOperationException(
                    $"PView routed entity 0x{entity.Id:X8} that was absent from the current partition.");
            }

            var candidate = new CurrentRenderPViewCandidateFingerprint(
                Sequence: _pviewCandidates.Count,
                Route: route,
                RouteIndex: routeIndex,
                CellId: cellId,
                Projection: projection);
            _pviewCandidates.Add(candidate);
            AddPViewCandidate(ref _pviewHash, in candidate);
        }
    }

    public void CompletePViewFrame()
    {
        if (!_pviewFrameOpen)
        {
            throw new InvalidOperationException(
                "The current render-scene oracle cannot complete PView without an open frame.");
        }

        _pviewFrameOpen = false;
        Snapshot = Snapshot with
        {
            CompletedPViewFrameSequence = checked(++_pviewFrameSequence),
            AbortedPViewFrames = _abortedPViewFrames,
            PViewCandidateCount = _pviewCandidates.Count,
            PViewDigest = _pviewHash.Finish(),
        };
    }

    public void AbortPViewFrame()
    {
        if (!_pviewFrameOpen)
            return;

        _pviewFrameOpen = false;
        _pviewCandidates.Clear();
        _abortedPViewFrames = checked(_abortedPViewFrames + 1);
        Snapshot = Snapshot with
        {
            AbortedPViewFrames = _abortedPViewFrames,
        };
    }

    public void BeginDispatcherFrame()
    {
        _dispatcherFrameOpen = true;
        _dispatcherCandidates.Clear();
        _dispatcherSubmissions.Clear();
        _dispatcherProjectionByEntity.Clear();
        _dispatcherDrawCount = 0;
        _dispatcherEntityCount = 0;
        _dispatcherInstanceCount = 0;
        _dispatcherOpaqueGroupCount = 0;
        _dispatcherTransparentGroupCount = 0;
        _dispatcherHash = StableRenderHash128.Create();
        Snapshot = Snapshot with
        {
            DispatcherFrameSequence = checked(++_dispatcherFrameSequence),
            AbortedDispatcherFrames = _abortedDispatcherFrames,
            DispatcherDrawCount = 0,
            DispatcherEntityCount = 0,
            DispatcherMeshRefCount = 0,
            DispatcherInstanceCount = 0,
            DispatcherOpaqueGroupCount = 0,
            DispatcherTransparentGroupCount = 0,
            DispatcherDigest = _dispatcherHash.Finish(),
        };
    }

    public void ObserveDispatcherDraw(
        WbDrawDispatcher.EntitySet set,
        int entitiesWalked,
        IReadOnlyList<(
            WorldEntity Entity,
            int MeshRefIndex,
            uint LandblockId)> accepted)
    {
        if (!_dispatcherFrameOpen)
        {
            throw new InvalidOperationException(
                "The current render-scene oracle received dispatcher input outside an open frame.");
        }
        ArgumentNullException.ThrowIfNull(accepted);

        int drawSequence = _dispatcherDrawCount++;
        _dispatcherEntityCount = checked(_dispatcherEntityCount + entitiesWalked);
        _dispatcherHash.Add((byte)0xD1);
        _dispatcherHash.Add(drawSequence);
        _dispatcherHash.Add((int)set);
        _dispatcherHash.Add(entitiesWalked);
        _dispatcherHash.Add(accepted.Count);

        foreach (var tuple in accepted)
        {
            uint cacheLandblockId =
                WbDrawDispatcher.ResolveCacheLandblockHint(
                    tuple.Entity,
                    tuple.LandblockId);
            if (!_dispatcherProjectionByEntity.TryGetValue(
                    tuple.Entity,
                    out CurrentRenderProjectionFingerprint projection))
            {
                if (!_projectionByEntity.TryGetValue(
                        tuple.Entity,
                        out projection))
                {
                    projection = CreateFingerprint(
                        cacheLandblockId,
                        tuple.Entity,
                        ProjectionClassOf(tuple.Entity));
                }
                _dispatcherProjectionByEntity[tuple.Entity] = projection;
            }
            var candidate = new CurrentRenderDispatcherFingerprint(
                Sequence: _dispatcherCandidates.Count,
                DrawSequence: drawSequence,
                Set: set,
                MeshRefIndex: tuple.MeshRefIndex,
                TupleLandblockId: tuple.LandblockId,
                CacheLandblockId: cacheLandblockId,
                Projection: projection,
                GfxObjId: tuple.Entity.MeshRefs[tuple.MeshRefIndex].GfxObjId,
                PartTransform:
                    tuple.Entity.MeshRefs[tuple.MeshRefIndex].PartTransform,
                SurfaceOverrides: CreateSurfaceOverrideFingerprint(
                    tuple.Entity.MeshRefs[tuple.MeshRefIndex]
                        .SurfaceOverrides));
            _dispatcherCandidates.Add(candidate);
            AddDispatcherCandidate(ref _dispatcherHash, in candidate);
        }

        Snapshot = Snapshot with
        {
            DispatcherFrameSequence = _dispatcherFrameSequence,
            AbortedDispatcherFrames = _abortedDispatcherFrames,
            DispatcherDrawCount = _dispatcherDrawCount,
            DispatcherEntityCount = _dispatcherEntityCount,
            DispatcherMeshRefCount = _dispatcherCandidates.Count,
            DispatcherInstanceCount = _dispatcherInstanceCount,
            DispatcherOpaqueGroupCount = _dispatcherOpaqueGroupCount,
            DispatcherTransparentGroupCount =
                _dispatcherTransparentGroupCount,
            DispatcherDigest = _dispatcherHash.Finish(),
        };
    }

    public void ObserveDispatcherSubmission(
        in CurrentRenderDispatcherSubmission submission)
    {
        if (!_dispatcherFrameOpen)
        {
            throw new InvalidOperationException(
                "The current render-scene oracle received dispatcher submission outside an open frame.");
        }
        if (_dispatcherDrawCount == 0)
        {
            throw new InvalidOperationException(
                "A dispatcher submission cannot precede its accepted candidate set.");
        }

        _dispatcherInstanceCount = checked(
            _dispatcherInstanceCount + submission.VisibleInstanceCount);
        _dispatcherSubmissions.Add(submission);
        _dispatcherOpaqueGroupCount = checked(
            _dispatcherOpaqueGroupCount + submission.OpaqueGroupCount);
        _dispatcherTransparentGroupCount = checked(
            _dispatcherTransparentGroupCount
            + submission.TransparentGroupCount);
        _dispatcherHash.Add((byte)0xD2);
        _dispatcherHash.Add(_dispatcherDrawCount - 1);
        _dispatcherHash.Add(submission.VisibleInstanceCount);
        _dispatcherHash.Add(submission.ImmediateInstanceCount);
        _dispatcherHash.Add(submission.OpaqueGroupCount);
        _dispatcherHash.Add(submission.TransparentGroupCount);
        _dispatcherHash.Add(submission.TransparentDeferred);
        _dispatcherHash.Add(submission.Digest.Low);
        _dispatcherHash.Add(submission.Digest.High);

        Snapshot = Snapshot with
        {
            DispatcherInstanceCount = _dispatcherInstanceCount,
            DispatcherOpaqueGroupCount = _dispatcherOpaqueGroupCount,
            DispatcherTransparentGroupCount =
                _dispatcherTransparentGroupCount,
            DispatcherDigest = _dispatcherHash.Finish(),
        };
    }

    public void AbortDispatcherFrame()
    {
        if (!_dispatcherFrameOpen)
            return;

        _dispatcherFrameOpen = false;
        _dispatcherCandidates.Clear();
        _dispatcherSubmissions.Clear();
        _dispatcherProjectionByEntity.Clear();
        _abortedDispatcherFrames = checked(_abortedDispatcherFrames + 1);
        Snapshot = Snapshot with
        {
            AbortedDispatcherFrames = _abortedDispatcherFrames,
        };
    }

    public void BeginSelectionFrame()
    {
        if (_selectionFrameOpen)
        {
            throw new InvalidOperationException(
                "The current render-scene oracle cannot begin a second selection frame before completing or aborting the first.");
        }

        _selectionFrameOpen = true;
        _selectionParts.Clear();
        _selectionHash = StableRenderHash128.Create();
    }

    public void ObserveSelectionPart(
        uint serverGuid,
        uint localEntityId,
        int partIndex,
        uint gfxObjId,
        Matrix4x4 localToWorld,
        RetailSelectionMesh mesh)
    {
        if (!_selectionFrameOpen)
        {
            throw new InvalidOperationException(
                "The current render-scene oracle received a selection part outside an open frame.");
        }
        ArgumentNullException.ThrowIfNull(mesh);

        if (!_selectionGeometryByGfxObj.TryGetValue(
                gfxObjId,
                out SelectionGeometryCacheEntry cachedGeometry)
            || !ReferenceEquals(cachedGeometry.Mesh, mesh))
        {
            cachedGeometry = new SelectionGeometryCacheEntry(
                mesh,
                FingerprintSelectionGeometry(mesh));
            _selectionGeometryByGfxObj[gfxObjId] = cachedGeometry;
        }

        var fingerprint = new CurrentRenderSelectionFingerprint(
            Sequence: _selectionParts.Count,
            ServerGuid: serverGuid,
            LocalEntityId: localEntityId,
            PartIndex: partIndex,
            GfxObjId: gfxObjId,
            LocalToWorld: localToWorld,
            Geometry: cachedGeometry.Fingerprint);
        _selectionParts.Add(fingerprint);
        _selectionHash.Add(fingerprint.Sequence);
        _selectionHash.Add(fingerprint.ServerGuid);
        _selectionHash.Add(fingerprint.LocalEntityId);
        _selectionHash.Add(fingerprint.PartIndex);
        _selectionHash.Add(fingerprint.GfxObjId);
        _selectionHash.Add(fingerprint.LocalToWorld);
        _selectionHash.Add(fingerprint.Geometry.Low);
        _selectionHash.Add(fingerprint.Geometry.High);
    }

    public void CompleteSelectionFrame()
    {
        if (!_selectionFrameOpen)
        {
            throw new InvalidOperationException(
                "The current render-scene oracle cannot complete selection without an open frame.");
        }

        _selectionFrameOpen = false;
        Snapshot = Snapshot with
        {
            CompletedSelectionFrameSequence =
                checked(++_selectionFrameSequence),
            AbortedSelectionFrames = _abortedSelectionFrames,
            SelectionPartCount = _selectionParts.Count,
            SelectionDigest = _selectionHash.Finish(),
        };
    }

    public void AbortSelectionFrame()
    {
        if (!_selectionFrameOpen)
            return;

        _selectionFrameOpen = false;
        _selectionParts.Clear();
        _abortedSelectionFrames = checked(_abortedSelectionFrames + 1);
        Snapshot = Snapshot with
        {
            AbortedSelectionFrames = _abortedSelectionFrames,
        };
    }

    internal static CurrentRenderProjectionFingerprint
        CreateProjectionFingerprint(
            uint landblockId,
            WorldEntity entity) =>
        CreateFingerprint(
            landblockId,
            entity,
            ProjectionClassOf(entity));

    private static CurrentRenderProjectionFingerprint CreateFingerprint(
        uint landblockId,
        WorldEntity entity,
        InteriorEntityPartition.ProjectionClass projectionClass)
    {
        StableRenderHash128 transform = StableRenderHash128.Create();
        transform.Add(entity.Position);
        transform.Add(entity.Rotation);
        transform.Add(entity.Scale);

        StableRenderHash128 geometry = StableRenderHash128.Create();
        geometry.Add(entity.MeshRefs.Count);
        for (int meshIndex = 0;
             meshIndex < entity.MeshRefs.Count;
             meshIndex++)
        {
            MeshRef mesh = entity.MeshRefs[meshIndex];
            geometry.Add(mesh.GfxObjId);
            geometry.Add(mesh.PartTransform);
            AddSurfaceOverrides(ref geometry, mesh.SurfaceOverrides);
        }

        StableRenderHash128 appearance = StableRenderHash128.Create();
        if (entity.PaletteOverride is { } palette)
        {
            appearance.Add(true);
            appearance.Add(palette.BasePaletteId);
            appearance.Add(palette.SubPalettes.Count);
            for (int rangeIndex = 0;
                 rangeIndex < palette.SubPalettes.Count;
                 rangeIndex++)
            {
                PaletteOverride.SubPaletteRange range =
                    palette.SubPalettes[rangeIndex];
                appearance.Add(range.SubPaletteId);
                appearance.Add(range.Offset);
                appearance.Add(range.Length);
            }
        }
        else
        {
            appearance.Add(false);
        }

        appearance.Add(entity.PartOverrides.Count);
        for (int partIndex = 0;
             partIndex < entity.PartOverrides.Count;
             partIndex++)
        {
            PartOverride part = entity.PartOverrides[partIndex];
            appearance.Add(part.PartIndex);
            appearance.Add(part.GfxObjId);
        }
        appearance.Add(entity.HiddenPartsMask);

        uint flags = 0;
        if (entity.IsDrawVisible)
            flags |= 1u << 0;
        if (entity.IsAncestorDrawVisible)
            flags |= 1u << 1;
        if (entity.IsBuildingShell)
            flags |= 1u << 2;
        if (entity.ParentCellId.HasValue)
            flags |= 1u << 3;
        if (entity.EffectCellId.HasValue)
            flags |= 1u << 4;
        if (entity.BuildingShellAnchorCellId.HasValue)
            flags |= 1u << 5;

        return new CurrentRenderProjectionFingerprint(
            ProjectionClass: projectionClass,
            LandblockId: landblockId,
            EntityId: entity.Id,
            ServerGuid: entity.ServerGuid,
            SourceId: entity.SourceGfxObjOrSetupId,
            ParentCellId: entity.ParentCellId ?? 0,
            EffectCellId: entity.EffectCellId ?? 0,
            BuildingShellAnchorCellId: entity.BuildingShellAnchorCellId ?? 0,
            Flags: flags,
            MeshCount: entity.MeshRefs.Count,
            Transform: transform.Finish(),
            Geometry: geometry.Finish(),
            Appearance: appearance.Finish());
    }

    private static void AddSurfaceOverrides(
        ref StableRenderHash128 geometry,
        IReadOnlyDictionary<uint, uint>? overrides)
    {
        RenderSceneHash128 fingerprint =
            CreateSurfaceOverrideFingerprint(overrides);
        geometry.Add(fingerprint.Low);
        geometry.Add(fingerprint.High);
    }

    internal static RenderSceneHash128
        CreateDirectionalShadowTopologyFingerprint(WorldEntity entity)
    {
        ArgumentNullException.ThrowIfNull(entity);
        StableRenderHash128 topology = StableRenderHash128.Create();
        topology.Add(entity.MeshRefs.Count);
        for (int meshIndex = 0; meshIndex < entity.MeshRefs.Count; meshIndex++)
        {
            MeshRef mesh = entity.MeshRefs[meshIndex];
            topology.Add(mesh.GfxObjId);
            AddSurfaceOverrides(ref topology, mesh.SurfaceOverrides);
        }

        return topology.Finish();
    }

    internal static RenderSceneHash128 CreateSurfaceOverrideFingerprint(
        IReadOnlyDictionary<uint, uint>? overrides)
    {
        if (overrides is null)
        {
            StableRenderHash128 missing = StableRenderHash128.Create();
            missing.Add(-1);
            return missing.Finish();
        }

        ulong xorLow = 0;
        ulong xorHigh = 0;
        ulong sumLow = 0;
        ulong sumHigh = 0;
        if (overrides is Dictionary<uint, uint> dictionary)
        {
            // Production hydration owns Dictionary instances. Enumerating
            // through IReadOnlyDictionary boxes Dictionary.Enumerator for
            // every live mesh on both projection-sync phases.
            foreach (KeyValuePair<uint, uint> pair in dictionary)
            {
                Accumulate(
                    pair.Key,
                    pair.Value,
                    ref xorLow,
                    ref xorHigh,
                    ref sumLow,
                    ref sumHigh);
            }
        }
        else
        {
            foreach ((uint surfaceId, uint replacementId) in overrides)
            {
                Accumulate(
                    surfaceId,
                    replacementId,
                    ref xorLow,
                    ref xorHigh,
                    ref sumLow,
                    ref sumHigh);
            }
        }

        StableRenderHash128 geometry = StableRenderHash128.Create();
        geometry.Add(overrides.Count);
        geometry.Add(xorLow);
        geometry.Add(xorHigh);
        geometry.Add(sumLow);
        geometry.Add(sumHigh);
        return geometry.Finish();

        static void Accumulate(
            uint surfaceId,
            uint replacementId,
            ref ulong xorLow,
            ref ulong xorHigh,
            ref ulong sumLow,
            ref ulong sumHigh)
        {
            StableRenderHash128 pairHash = StableRenderHash128.Create();
            pairHash.Add(surfaceId);
            pairHash.Add(replacementId);
            RenderSceneHash128 value = pairHash.Finish();
            xorLow ^= value.Low;
            xorHigh ^= value.High;
            unchecked
            {
                sumLow += value.Low;
                sumHigh += value.High;
            }
        }
    }

    internal static RenderSceneHash128 FingerprintSelectionGeometry(
        RetailSelectionMesh mesh)
    {
        StableRenderHash128 geometry = StableRenderHash128.Create();
        geometry.Add(mesh.SphereCenter);
        geometry.Add(mesh.SphereRadius);
        geometry.Add(mesh.Polygons.Count);
        for (int polygonIndex = 0;
             polygonIndex < mesh.Polygons.Count;
             polygonIndex++)
        {
            RetailSelectionPolygon polygon = mesh.Polygons[polygonIndex];
            geometry.Add(polygon.SingleSided);
            geometry.Add(polygon.Vertices.Count);
            for (int vertexIndex = 0;
                 vertexIndex < polygon.Vertices.Count;
                 vertexIndex++)
            {
                geometry.Add(polygon.Vertices[vertexIndex]);
            }
        }

        return geometry.Finish();
    }

    private static InteriorEntityPartition.ProjectionClass ProjectionClassOf(
        WorldEntity entity) =>
        entity.ServerGuid != 0
            ? InteriorEntityPartition.ProjectionClass.Dynamic
            : InteriorEntityPartition.IsIndoorCellId(entity.ParentCellId)
                ? InteriorEntityPartition.ProjectionClass.CellStatic
                : InteriorEntityPartition.ProjectionClass.OutdoorStatic;

    private readonly record struct SelectionGeometryCacheEntry(
        RetailSelectionMesh Mesh,
        RenderSceneHash128 Fingerprint);

    private static void AddPViewCandidate(
        ref StableRenderHash128 hash,
        in CurrentRenderPViewCandidateFingerprint candidate)
    {
        hash.Add(candidate.Sequence);
        hash.Add((byte)candidate.Route);
        hash.Add(candidate.RouteIndex);
        hash.Add(candidate.CellId);
        CurrentRenderProjectionFingerprint projection = candidate.Projection;
        AddProjection(ref hash, in projection);
    }

    private static void AddDispatcherCandidate(
        ref StableRenderHash128 hash,
        in CurrentRenderDispatcherFingerprint candidate)
    {
        hash.Add(candidate.Sequence);
        hash.Add(candidate.DrawSequence);
        hash.Add((int)candidate.Set);
        hash.Add(candidate.MeshRefIndex);
        hash.Add(candidate.TupleLandblockId);
        hash.Add(candidate.CacheLandblockId);
        hash.Add(candidate.GfxObjId);
        hash.Add(candidate.PartTransform);
        hash.Add(candidate.SurfaceOverrides.Low);
        hash.Add(candidate.SurfaceOverrides.High);
        CurrentRenderProjectionFingerprint projection = candidate.Projection;
        AddProjection(ref hash, in projection);
    }

    private static void AddProjection(
        ref StableRenderHash128 hash,
        in CurrentRenderProjectionFingerprint projection)
    {
        hash.Add((byte)projection.ProjectionClass);
        hash.Add(projection.LandblockId);
        hash.Add(projection.EntityId);
        hash.Add(projection.ServerGuid);
        hash.Add(projection.SourceId);
        hash.Add(projection.ParentCellId);
        hash.Add(projection.EffectCellId);
        hash.Add(projection.BuildingShellAnchorCellId);
        hash.Add(projection.Flags);
        hash.Add(projection.MeshCount);
        hash.Add(projection.Transform.Low);
        hash.Add(projection.Transform.High);
        hash.Add(projection.Geometry.Low);
        hash.Add(projection.Geometry.High);
        hash.Add(projection.Appearance.Low);
        hash.Add(projection.Appearance.High);
    }

    private sealed class CurrentRenderProjectionFingerprintComparer :
        IComparer<CurrentRenderProjectionFingerprint>
    {
        public static CurrentRenderProjectionFingerprintComparer Instance { get; } =
            new();

        public int Compare(
            CurrentRenderProjectionFingerprint x,
            CurrentRenderProjectionFingerprint y)
        {
            int value = ((int)x.ProjectionClass).CompareTo((int)y.ProjectionClass);
            if (value != 0) return value;
            value = x.LandblockId.CompareTo(y.LandblockId);
            if (value != 0) return value;
            value = x.ParentCellId.CompareTo(y.ParentCellId);
            if (value != 0) return value;
            value = x.EntityId.CompareTo(y.EntityId);
            if (value != 0) return value;
            value = x.ServerGuid.CompareTo(y.ServerGuid);
            if (value != 0) return value;
            value = x.SourceId.CompareTo(y.SourceId);
            if (value != 0) return value;
            value = x.EffectCellId.CompareTo(y.EffectCellId);
            if (value != 0) return value;
            value = x.BuildingShellAnchorCellId.CompareTo(
                y.BuildingShellAnchorCellId);
            if (value != 0) return value;
            value = x.Flags.CompareTo(y.Flags);
            if (value != 0) return value;
            value = x.MeshCount.CompareTo(y.MeshCount);
            if (value != 0) return value;
            value = x.Transform.High.CompareTo(y.Transform.High);
            if (value != 0) return value;
            value = x.Transform.Low.CompareTo(y.Transform.Low);
            if (value != 0) return value;
            value = x.Geometry.High.CompareTo(y.Geometry.High);
            if (value != 0) return value;
            value = x.Geometry.Low.CompareTo(y.Geometry.Low);
            if (value != 0) return value;
            value = x.Appearance.High.CompareTo(y.Appearance.High);
            if (value != 0) return value;
            return x.Appearance.Low.CompareTo(y.Appearance.Low);
        }
    }
}

internal struct StableRenderHash128
{
    private const ulong LowOffset = 14695981039346656037UL;
    private const ulong LowPrime = 1099511628211UL;
    private const ulong HighOffset = 7809847782465536322UL;
    private const ulong HighPrime = 14029467366897019727UL;

    private ulong _low;
    private ulong _high;

    public static StableRenderHash128 Create() => new()
    {
        _low = LowOffset,
        _high = HighOffset,
    };

    public readonly RenderSceneHash128 Finish() => new(_low, _high);

    public void Add(bool value) => Add(value ? (byte)1 : (byte)0);

    public void Add(byte value) => Add((ulong)value);

    public void Add(int value) => Add(unchecked((uint)value));

    public void Add(uint value) => Add((ulong)value);

    public void Add(float value) => Add(BitConverter.SingleToUInt32Bits(value));

    public void Add(ulong value)
    {
        _low = unchecked((_low ^ value) * LowPrime);
        ulong folded = value ^ BitOperations.RotateLeft(value, 29);
        _high = unchecked((_high ^ folded) * HighPrime);
    }

    public void Add(Vector3 value)
    {
        Add(value.X);
        Add(value.Y);
        Add(value.Z);
    }

    public void Add(Vector2 value)
    {
        Add(value.X);
        Add(value.Y);
    }

    public void Add(Quaternion value)
    {
        Add(value.X);
        Add(value.Y);
        Add(value.Z);
        Add(value.W);
    }

    public void Add(Matrix4x4 value)
    {
        Add(value.M11); Add(value.M12); Add(value.M13); Add(value.M14);
        Add(value.M21); Add(value.M22); Add(value.M23); Add(value.M24);
        Add(value.M31); Add(value.M32); Add(value.M33); Add(value.M34);
        Add(value.M41); Add(value.M42); Add(value.M43); Add(value.M44);
    }
}
