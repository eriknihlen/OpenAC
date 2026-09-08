using System.Numerics;
using AcDream.Core.Selection;
using AcDream.Core.World;
using AcDream.App.Rendering.Walk;

namespace AcDream.App.Rendering.Scene;

internal enum RenderFrameBlendClass : byte
{
    Opaque,
    Alpha,
}

internal enum RenderFrameCandidateRoute : byte
{
    LandscapeOutdoorStatic,
    LandscapeBuildingShell,
    LandscapeOutsideDynamic,
    LookInObject,
    CellStatic,
    DynamicLast,
}

internal readonly record struct RenderFrameCandidateRange(
    RenderFrameCandidateRoute Route,
    int RouteIndex,
    uint CellId,
    int Offset,
    int Count);

internal readonly record struct RenderFrameCellRange(
    uint CellId,
    int PViewOrder,
    int Offset,
    int Count);

internal readonly record struct RenderFrameTransformRecord(
    RenderProjectionId ProjectionId,
    int PartIndex,
    Matrix4x4 LocalToWorld);

internal readonly record struct RenderFrameEntityCandidate(
    RenderProjectionRecord Projection,
    int MeshPartOffset,
    int MeshPartCount,
    bool Animated);

internal readonly record struct RenderFrameMeshPart(
    RenderProjectionId ProjectionId,
    int PartIndex,
    MeshRef MeshRef);

internal readonly record struct RenderFrameClassificationRecord(
    RenderProjectionId ProjectionId,
    int CandidateIndex,
    int MeshRefIndex,
    RenderFrameBlendClass BlendClass,
    float Opacity);

internal readonly record struct RenderFrameObjectLightSet(
    RenderProjectionId ProjectionId,
    int L0,
    int L1,
    int L2,
    int L3,
    int L4,
    int L5,
    int L6,
    int L7);

internal readonly record struct RenderFrameSelectionPart(
    RenderProjectionId ProjectionId,
    uint ServerGuid,
    uint LocalEntityId,
    int PartIndex,
    uint GfxObjId,
    Matrix4x4 LocalToWorld,
    RetailSelectionMesh Mesh);

internal readonly record struct RenderFrameDiagnosticCounts(
    int OutdoorStaticCandidates,
    int CellStaticCandidates,
    int DynamicCandidates,
    int TransformCount,
    int OpaqueClassificationCount,
    int AlphaClassificationCount,
    int LightSetCount,
    int SelectionPartCount,
    int RouteCandidateCount,
    int EntityCandidateCount,
    int MeshPartCount);

internal readonly struct RenderFrameView
{
    private readonly RenderFrameArena? _arena;
    private readonly ulong _arenaEpoch;
    private readonly ulong _borrowToken;

    internal RenderFrameView(
        RenderFrameArena arena,
        ulong arenaEpoch,
        ulong borrowToken)
    {
        _arena = arena;
        _arenaEpoch = arenaEpoch;
        _borrowToken = borrowToken;
    }

    public RenderSceneGeneration Generation
    {
        get
        {
            RenderFrameArena arena = Arena;
            return arena.Generation;
        }
    }

    public ulong FrameSequence
    {
        get
        {
            RenderFrameArena arena = Arena;
            return arena.FrameSequence;
        }
    }

    public ReadOnlySpan<RenderProjectionRecord> OutdoorStaticCandidates =>
        Arena.OutdoorStaticCandidates;

    public ReadOnlySpan<RenderProjectionRecord> CellStaticCandidates =>
        Arena.CellStaticCandidates;

    public ReadOnlySpan<RenderFrameCellRange> CellRanges =>
        Arena.CellRanges;

    public ReadOnlySpan<RenderProjectionRecord> DynamicCandidates =>
        Arena.DynamicCandidates;

    public ReadOnlySpan<RenderFrameTransformRecord> Transforms =>
        Arena.Transforms;

    public ReadOnlySpan<RenderFrameEntityCandidate> EntityCandidates =>
        Arena.EntityCandidates;

    public ReadOnlySpan<RenderFrameMeshPart> MeshParts =>
        Arena.MeshParts;

    public ReadOnlySpan<RenderFrameClassificationRecord> Classifications =>
        Arena.Classifications;

    public ReadOnlySpan<RenderFrameObjectLightSet> LightSets =>
        Arena.LightSets;

    public ReadOnlySpan<RenderFrameSelectionPart> SelectionParts =>
        Arena.SelectionParts;

    public ReadOnlySpan<RenderProjectionRecord> RouteCandidates =>
        Arena.RouteCandidates;

    public ReadOnlySpan<RenderFrameCandidateRange> RouteRanges =>
        Arena.RouteRanges;

    public RenderFrameDiagnosticCounts DiagnosticCounts =>
        Arena.DiagnosticCounts;

    public RenderSceneDigest SourceDigest => Arena.SourceDigest;

    internal RenderFrameArena Owner =>
        Arena;

    internal ulong ArenaEpoch => _arenaEpoch;

    internal ulong BorrowToken => _borrowToken;

    private RenderFrameArena Arena
    {
        get
        {
            RenderFrameArena? arena = _arena;
            if (arena is null
                || !arena.IsBorrowValid(_arenaEpoch, _borrowToken))
            {
                throw new InvalidOperationException(
                    "The render-frame view is no longer borrowed from its arena.");
            }

            return arena;
        }
    }
}

/// <summary>
/// One side of the double-buffered render-frame product. Arrays grow only
/// when a new high-water mark is reached and are reused on every later frame.
/// </summary>
internal sealed class RenderFrameArena
{
    private RenderProjectionRecord[] _outdoor = [];
    private RenderProjectionRecord[] _cellStatics = [];
    private RenderFrameCellRange[] _cellRanges = [];
    private RenderProjectionRecord[] _dynamics = [];
    private RenderFrameTransformRecord[] _transforms = [];
    private RenderFrameEntityCandidate[] _entityCandidates = [];
    private RenderFrameMeshPart[] _meshParts = [];
    private RenderFrameClassificationRecord[] _classifications = [];
    private RenderFrameObjectLightSet[] _lightSets = [];
    private RenderFrameSelectionPart[] _selectionParts = [];
    private RenderProjectionRecord[] _routeCandidates = [];
    private RenderFrameCandidateRange[] _routeRanges = [];
    private int _outdoorCount;
    private int _cellStaticCount;
    private int _cellRangeCount;
    private int _dynamicCount;
    private int _transformCount;
    private int _entityCandidateCount;
    private int _meshPartCount;
    private int _classificationCount;
    private int _lightSetCount;
    private int _selectionPartCount;
    private int _routeCandidateCount;
    private int _routeRangeCount;
    private int _opaqueClassificationCount;
    private int _alphaClassificationCount;
    private ulong _epoch;
    private ulong _activeBorrowToken;
    private bool _sourceDigestSet;
    private ArenaState _state;

    public RenderSceneGeneration Generation { get; private set; }

    public ulong FrameSequence { get; private set; }

    public RenderSceneDigest SourceDigest { get; private set; }

    public RenderFrameDiagnosticCounts DiagnosticCounts =>
        new(
            _outdoorCount,
            _cellStaticCount,
            _dynamicCount,
            _transformCount,
            _opaqueClassificationCount,
            _alphaClassificationCount,
            _lightSetCount,
            _selectionPartCount,
            _routeCandidateCount,
            _entityCandidateCount,
            _meshPartCount);

    internal ReadOnlySpan<RenderProjectionRecord> OutdoorStaticCandidates =>
        _outdoor.AsSpan(0, _outdoorCount);

    internal ReadOnlySpan<RenderProjectionRecord> CellStaticCandidates =>
        _cellStatics.AsSpan(0, _cellStaticCount);

    internal ReadOnlySpan<RenderFrameCellRange> CellRanges =>
        _cellRanges.AsSpan(0, _cellRangeCount);

    internal ReadOnlySpan<RenderProjectionRecord> DynamicCandidates =>
        _dynamics.AsSpan(0, _dynamicCount);

    internal ReadOnlySpan<RenderFrameTransformRecord> Transforms =>
        _transforms.AsSpan(0, _transformCount);

    internal ReadOnlySpan<RenderFrameEntityCandidate> EntityCandidates =>
        _entityCandidates.AsSpan(0, _entityCandidateCount);

    internal ReadOnlySpan<RenderFrameMeshPart> MeshParts =>
        _meshParts.AsSpan(0, _meshPartCount);

    internal ReadOnlySpan<RenderFrameClassificationRecord> Classifications =>
        _classifications.AsSpan(0, _classificationCount);

    internal ReadOnlySpan<RenderFrameObjectLightSet> LightSets =>
        _lightSets.AsSpan(0, _lightSetCount);

    internal ReadOnlySpan<RenderFrameSelectionPart> SelectionParts =>
        _selectionParts.AsSpan(0, _selectionPartCount);

    internal ReadOnlySpan<RenderProjectionRecord> RouteCandidates =>
        _routeCandidates.AsSpan(0, _routeCandidateCount);

    internal ReadOnlySpan<RenderFrameCandidateRange> RouteRanges =>
        _routeRanges.AsSpan(0, _routeRangeCount);

    internal ulong BeginWrite(
        RenderSceneGeneration generation,
        ulong frameSequence)
    {
        if (_state is ArenaState.Building or ArenaState.Borrowed)
        {
            throw new InvalidOperationException(
                "A render-frame arena cannot be reused while building or borrowed.");
        }
        if (frameSequence == 0)
            throw new ArgumentOutOfRangeException(nameof(frameSequence));

        ClearReferenceStorage();
        _outdoorCount = 0;
        _cellStaticCount = 0;
        _cellRangeCount = 0;
        _dynamicCount = 0;
        _transformCount = 0;
        _entityCandidateCount = 0;
        _meshPartCount = 0;
        _classificationCount = 0;
        _lightSetCount = 0;
        _selectionPartCount = 0;
        _routeCandidateCount = 0;
        _routeRangeCount = 0;
        _opaqueClassificationCount = 0;
        _alphaClassificationCount = 0;
        SourceDigest = default;
        _sourceDigestSet = false;
        Generation = generation;
        FrameSequence = frameSequence;
        _activeBorrowToken = 0;
        _epoch = checked(_epoch + 1);
        _state = ArenaState.Building;
        return _epoch;
    }

    internal void SetSourceDigest(ulong epoch, in RenderSceneDigest digest)
    {
        EnsureBuilding(epoch);
        if (digest.Generation != Generation)
        {
            throw new InvalidOperationException(
                $"The source digest belongs to {digest.Generation}, "
                + $"but the frame arena is building {Generation}.");
        }

        SourceDigest = digest;
        _sourceDigestSet = true;
    }

    internal void AddOutdoor(
        ulong epoch,
        in RenderProjectionRecord record)
    {
        EnsureBuilding(epoch);
        EnsureCapacity(ref _outdoor, _outdoorCount + 1);
        _outdoor[_outdoorCount++] = record;
    }

    internal void AddCellRange(
        ulong epoch,
        uint cellId,
        int pviewOrder,
        ReadOnlySpan<RenderProjectionRecord> records)
    {
        EnsureBuilding(epoch);
        if (pviewOrder < 0)
            throw new ArgumentOutOfRangeException(nameof(pviewOrder));

        int offset = _cellStaticCount;
        EnsureCapacity(ref _cellStatics, checked(offset + records.Length));
        records.CopyTo(_cellStatics.AsSpan(offset));
        _cellStaticCount += records.Length;
        EnsureCapacity(ref _cellRanges, _cellRangeCount + 1);
        _cellRanges[_cellRangeCount++] = new RenderFrameCellRange(
            cellId,
            pviewOrder,
            offset,
            records.Length);
    }

    internal void AddCellRange(
        ulong epoch,
        uint cellId,
        int pviewOrder,
        in RenderProjectionRecord record)
    {
        EnsureBuilding(epoch);
        if (pviewOrder < 0)
            throw new ArgumentOutOfRangeException(nameof(pviewOrder));

        int offset = _cellStaticCount;
        EnsureCapacity(ref _cellStatics, offset + 1);
        _cellStatics[_cellStaticCount++] = record;
        EnsureCapacity(ref _cellRanges, _cellRangeCount + 1);
        _cellRanges[_cellRangeCount++] = new RenderFrameCellRange(
            cellId,
            pviewOrder,
            offset,
            1);
    }

    internal void AddDynamic(
        ulong epoch,
        in RenderProjectionRecord record)
    {
        EnsureBuilding(epoch);
        EnsureCapacity(ref _dynamics, _dynamicCount + 1);
        _dynamics[_dynamicCount++] = record;
    }

    internal void AddTransform(
        ulong epoch,
        in RenderFrameTransformRecord transform)
    {
        EnsureBuilding(epoch);
        EnsureCapacity(ref _transforms, _transformCount + 1);
        _transforms[_transformCount++] = transform;
    }

    internal void AddEntityCandidate(
        ulong epoch,
        in RenderProjectionRecord projection,
        bool animated)
    {
        EnsureBuilding(epoch);
        IReadOnlyList<MeshRef>? meshRefs =
            projection.EntityPayload.MeshRefs;
        int meshPartCount = meshRefs?.Count ?? 0;
        if (meshPartCount != projection.MeshSet.MeshCount)
        {
            throw new InvalidOperationException(
                $"Projection {projection.Id} declares "
                + $"{projection.MeshSet.MeshCount} mesh parts but its "
                + $"presentation payload contains {meshPartCount}.");
        }

        int meshPartOffset = _meshPartCount;
        EnsureCapacity(
            ref _meshParts,
            checked(meshPartOffset + meshPartCount));
        for (int partIndex = 0; partIndex < meshPartCount; partIndex++)
        {
            _meshParts[_meshPartCount++] = new RenderFrameMeshPart(
                projection.Id,
                partIndex,
                meshRefs![partIndex]);
        }

        EnsureCapacity(
            ref _entityCandidates,
            _entityCandidateCount + 1);
        _entityCandidates[_entityCandidateCount++] =
            new RenderFrameEntityCandidate(
                projection,
                meshPartOffset,
                meshPartCount,
                animated);
    }

    internal void AddClassification(
        ulong epoch,
        in RenderFrameClassificationRecord classification)
    {
        EnsureBuilding(epoch);
        EnsureCapacity(ref _classifications, _classificationCount + 1);
        _classifications[_classificationCount++] = classification;
        if (classification.BlendClass is RenderFrameBlendClass.Alpha)
            _alphaClassificationCount++;
        else
            _opaqueClassificationCount++;
    }

    internal void AddLightSet(
        ulong epoch,
        in RenderFrameObjectLightSet lightSet)
    {
        EnsureBuilding(epoch);
        EnsureCapacity(ref _lightSets, _lightSetCount + 1);
        _lightSets[_lightSetCount++] = lightSet;
    }

    internal void AddSelectionPart(
        ulong epoch,
        in RenderFrameSelectionPart part)
    {
        EnsureBuilding(epoch);
        ArgumentNullException.ThrowIfNull(part.Mesh);
        EnsureCapacity(ref _selectionParts, _selectionPartCount + 1);
        _selectionParts[_selectionPartCount++] = part;
    }

    internal void AddRouteRange(
        ulong epoch,
        RenderFrameCandidateRoute route,
        int routeIndex,
        uint cellId,
        ReadOnlySpan<RenderProjectionRecord> records)
    {
        EnsureBuilding(epoch);
        if (routeIndex < 0)
            throw new ArgumentOutOfRangeException(nameof(routeIndex));
        if (records.Length == 0)
            return;

        int offset = _routeCandidateCount;
        EnsureCapacity(
            ref _routeCandidates,
            checked(offset + records.Length));
        records.CopyTo(_routeCandidates.AsSpan(offset));
        _routeCandidateCount += records.Length;
        EnsureCapacity(ref _routeRanges, _routeRangeCount + 1);
        _routeRanges[_routeRangeCount++] = new RenderFrameCandidateRange(
            route,
            routeIndex,
            cellId,
            offset,
            records.Length);
    }

    internal void Publish(ulong epoch)
    {
        EnsureBuilding(epoch);
        if (!_sourceDigestSet)
        {
            throw new InvalidOperationException(
                "A render-frame arena cannot publish without its source digest.");
        }
        _state = ArenaState.Published;
    }

    internal void Abort(ulong epoch)
    {
        EnsureBuilding(epoch);
        ClearReferenceStorage();
        SourceDigest = default;
        _sourceDigestSet = false;
        _state = ArenaState.Available;
    }

    internal ulong Borrow(ulong epoch, ulong borrowToken)
    {
        if (_state is not ArenaState.Published || _epoch != epoch)
        {
            throw new InvalidOperationException(
                "Only the exact published render-frame arena may be borrowed.");
        }
        if (borrowToken == 0)
            throw new ArgumentOutOfRangeException(nameof(borrowToken));

        _activeBorrowToken = borrowToken;
        _state = ArenaState.Borrowed;
        return _activeBorrowToken;
    }

    internal void Release(ulong epoch, ulong borrowToken)
    {
        if (!IsBorrowValid(epoch, borrowToken))
        {
            throw new InvalidOperationException(
                "The render-frame borrow is stale or has already been released.");
        }

        _activeBorrowToken = 0;
        _state = ArenaState.Published;
    }

    internal bool IsBorrowValid(ulong epoch, ulong borrowToken) =>
        _state is ArenaState.Borrowed
        && _epoch == epoch
        && borrowToken != 0
        && _activeBorrowToken == borrowToken;

    internal bool CanBuild => _state is ArenaState.Available or ArenaState.Published;

    internal ulong Epoch => _epoch;

    private void EnsureBuilding(ulong epoch)
    {
        if (_state is not ArenaState.Building || _epoch != epoch)
        {
            throw new InvalidOperationException(
                "The render-frame writer no longer owns this arena.");
        }
    }

    private void ClearReferenceStorage()
    {
        if (_selectionPartCount > 0)
            Array.Clear(_selectionParts, 0, _selectionPartCount);
        if (_entityCandidateCount > 0)
            Array.Clear(_entityCandidates, 0, _entityCandidateCount);
        if (_meshPartCount > 0)
            Array.Clear(_meshParts, 0, _meshPartCount);
    }

    private static void EnsureCapacity<T>(ref T[] values, int required)
    {
        if (values.Length >= required)
            return;

        int capacity = values.Length == 0 ? 4 : values.Length;
        while (capacity < required)
            capacity = checked(capacity * 2);
        Array.Resize(ref values, capacity);
    }

    private enum ArenaState : byte
    {
        Available,
        Building,
        Published,
        Borrowed,
    }
}

internal readonly struct RenderFrameWriter
{
    private readonly RenderFrameExchange? _owner;
    private readonly RenderFrameArena? _arena;
    private readonly int _arenaIndex;
    private readonly ulong _epoch;

    internal RenderFrameWriter(
        RenderFrameExchange owner,
        RenderFrameArena arena,
        int arenaIndex,
        ulong epoch)
    {
        _owner = owner;
        _arena = arena;
        _arenaIndex = arenaIndex;
        _epoch = epoch;
    }

    public void SetSourceDigest(in RenderSceneDigest digest) =>
        Arena.SetSourceDigest(_epoch, in digest);

    public void AddOutdoor(in RenderProjectionRecord record) =>
        Arena.AddOutdoor(_epoch, in record);

    public void AddCellRange(
        uint cellId,
        int pviewOrder,
        ReadOnlySpan<RenderProjectionRecord> records) =>
        Arena.AddCellRange(_epoch, cellId, pviewOrder, records);

    public void AddCellRange(
        uint cellId,
        int pviewOrder,
        in RenderProjectionRecord record) =>
        Arena.AddCellRange(_epoch, cellId, pviewOrder, in record);

    public void AddDynamic(in RenderProjectionRecord record) =>
        Arena.AddDynamic(_epoch, in record);

    public void AddTransform(in RenderFrameTransformRecord transform) =>
        Arena.AddTransform(_epoch, in transform);

    public void AddEntityCandidate(
        in RenderProjectionRecord projection,
        bool animated) =>
        Arena.AddEntityCandidate(_epoch, in projection, animated);

    public void AddClassification(
        in RenderFrameClassificationRecord classification) =>
        Arena.AddClassification(_epoch, in classification);

    public void AddLightSet(in RenderFrameObjectLightSet lightSet) =>
        Arena.AddLightSet(_epoch, in lightSet);

    public void AddSelectionPart(in RenderFrameSelectionPart part) =>
        Arena.AddSelectionPart(_epoch, in part);

    public void AddRouteRange(
        RenderFrameCandidateRoute route,
        int routeIndex,
        uint cellId,
        ReadOnlySpan<RenderProjectionRecord> records) =>
        Arena.AddRouteRange(
            _epoch,
            route,
            routeIndex,
            cellId,
            records);

    public void Publish() =>
        Owner.Publish(_arenaIndex, _epoch);

    public void Abort() =>
        Owner.Abort(_arenaIndex, _epoch);

    private RenderFrameExchange Owner =>
        _owner
        ?? throw new InvalidOperationException(
            "The render-frame writer is uninitialized.");

    private RenderFrameArena Arena =>
        _arena
        ?? throw new InvalidOperationException(
            "The render-frame writer is uninitialized.");
}

/// <summary>
/// Owns exactly two reusable arenas. It permits frame N to build in one arena
/// while frame N-1 remains borrowed from the other, but never permits mutation
/// of a borrowed arena or publication of an incomplete frame.
/// </summary>
internal sealed class RenderFrameExchange
{
    private readonly RenderFrameArena[] _arenas =
        [new RenderFrameArena(), new RenderFrameArena()];
    private int _buildingIndex = -1;
    private int _publishedIndex = -1;
    private ulong _nextBorrowToken;

    public RenderFrameWriter BeginBuild(
        RenderSceneGeneration generation,
        ulong frameSequence)
    {
        if (_buildingIndex >= 0)
        {
            throw new InvalidOperationException(
                "A render-frame build is already in progress.");
        }

        int index = _publishedIndex < 0
            ? 0
            : 1 - _publishedIndex;
        if (!_arenas[index].CanBuild)
            index = -1;
        if (index < 0)
        {
            throw new InvalidOperationException(
                "The non-published render-frame arena is still borrowed.");
        }

        RenderFrameArena arena = _arenas[index];
        ulong epoch = arena.BeginWrite(generation, frameSequence);
        _buildingIndex = index;
        return new RenderFrameWriter(this, arena, index, epoch);
    }

    public RenderFrameView BorrowLatest(
        RenderSceneGeneration expectedGeneration,
        ulong expectedFrameSequence)
    {
        if (_publishedIndex < 0)
        {
            throw new InvalidOperationException(
                "No completed render frame has been published.");
        }

        RenderFrameArena arena = _arenas[_publishedIndex];
        if (arena.Generation != expectedGeneration
            || arena.FrameSequence != expectedFrameSequence)
        {
            throw new InvalidOperationException(
                $"Published frame {arena.Generation}/{arena.FrameSequence} "
                + $"does not match requested {expectedGeneration}/{expectedFrameSequence}.");
        }

        ulong token = checked(++_nextBorrowToken);
        arena.Borrow(arena.Epoch, token);
        return new RenderFrameView(arena, arena.Epoch, token);
    }

    public void Release(in RenderFrameView view)
    {
        RenderFrameArena arena = view.Owner;
        if (!ReferenceEquals(arena, _arenas[0])
            && !ReferenceEquals(arena, _arenas[1]))
        {
            throw new InvalidOperationException(
                "The render-frame view belongs to another exchange.");
        }

        arena.Release(view.ArenaEpoch, view.BorrowToken);
    }

    internal void Publish(int index, ulong epoch)
    {
        EnsureWriter(index);
        RenderFrameArena arena = _arenas[index];
        arena.Publish(epoch);
        _publishedIndex = index;
        _buildingIndex = -1;
    }

    internal void Abort(int index, ulong epoch)
    {
        EnsureWriter(index);
        _arenas[index].Abort(epoch);
        _buildingIndex = -1;
    }

    private void EnsureWriter(int index)
    {
        if (_buildingIndex != index)
        {
            throw new InvalidOperationException(
                "The render-frame writer is stale or has already completed.");
        }
    }
}
