using System.Runtime.CompilerServices;
using System.Runtime.InteropServices;
using AcDream.App.Input;
using AcDream.App.Rendering.Scene.Arch;
using AcDream.App.World;
using AcDream.Core.World;

namespace AcDream.App.Rendering.Scene;

internal readonly record struct RenderSceneShadowComparisonSnapshot(
    bool Enabled,
    ulong RenderFrameSequence,
    ulong ComparisonCount,
    ulong SuccessfulComparisonCount,
    ulong ComparedOracleFrameSequence,
    int MatchedProjectionCount,
    long MismatchCount,
    string? FirstMismatch,
    int PendingDeltaCount,
    ulong DrainSequence,
    RenderDeltaApplyResult LastApply,
    RenderDeltaApplyResult CumulativeApply,
    RenderProjectionCounts SceneCounts,
    RenderSceneIndexCounts SceneIndexCounts,
    RenderSceneMemoryAccounting Memory,
    RenderSceneDigest Digest)
{
    public static RenderSceneShadowComparisonSnapshot Disabled { get; } =
        new(
            Enabled: false,
            RenderFrameSequence: 0,
            ComparisonCount: 0,
            SuccessfulComparisonCount: 0,
            ComparedOracleFrameSequence: 0,
            MatchedProjectionCount: 0,
            MismatchCount: 0,
            FirstMismatch: null,
            PendingDeltaCount: 0,
            DrainSequence: 0,
            LastApply: default,
            CumulativeApply: default,
            SceneCounts: default,
            SceneIndexCounts: default,
            Memory: default,
            Digest: default);
}

internal interface IRenderSceneShadowSnapshotSource
{
    RenderSceneShadowComparisonSnapshot CaptureCheckpointSnapshot();
}

internal sealed class RenderSceneShadowRuntime : IDisposable
{
    private readonly ArchRenderScene _scene;
    private readonly RenderProjectionJournal _journal;
    private readonly RenderSceneDigestBuffer _digestBuffer = new();
    private LiveRenderProjectionJournal? _live;
    private RenderDeltaApplyResult _lastApply;
    private RenderDeltaApplyResult _cumulativeApply;
    private ulong _drainSequence;
    private bool _disposed;

    public RenderSceneShadowRuntime(RenderSceneGeneration generation)
    {
        _scene = new ArchRenderScene(generation);
        _journal = new RenderProjectionJournal(generation);
        StaticProjections = new StaticRenderProjectionJournal(_journal);
    }

    public StaticRenderProjectionJournal StaticProjections { get; }

    public ILiveRenderProjectionSink? LiveProjections => _live;

    public int PendingDeltaCount => _journal.Count;

    public ulong DrainSequence => _drainSequence;

    public RenderDeltaApplyResult LastApply => _lastApply;

    public RenderDeltaApplyResult CumulativeApply => _cumulativeApply;

    public RenderProjectionCounts Counts => _scene.Counts;

    internal RenderProjectionJournal Journal => _journal;

    internal RenderSceneQuery Query => _scene.OpenQuery();

    public LiveRenderProjectionJournal BindLiveRuntime(
        LiveEntityRuntime runtime,
        IRenderTraversalOrderSource traversalOrder,
        ILocalPlayerIdentitySource? localPlayer = null)
    {
        ObjectDisposedException.ThrowIf(_disposed, this);
        ArgumentNullException.ThrowIfNull(runtime);
        ArgumentNullException.ThrowIfNull(traversalOrder);
        if (_live is not null)
        {
            throw new InvalidOperationException(
                "The shadow render scene is already bound to a live runtime.");
        }

        _live = new LiveRenderProjectionJournal(
            runtime,
            _journal,
            traversalOrder,
            localPlayer);
        return _live;
    }

    public void OnLiveResourceRegistered(WorldEntity entity)
    {
        ObjectDisposedException.ThrowIf(_disposed, this);
        ArgumentNullException.ThrowIfNull(entity);
    }

    public void OnLiveResourceUnregistered(WorldEntity entity)
    {
        ObjectDisposedException.ThrowIf(_disposed, this);
        ArgumentNullException.ThrowIfNull(entity);
        (_live ?? throw new InvalidOperationException(
            "The shadow render scene was not bound before live teardown."))
            .OnResourceUnregister(entity);
    }

    public RenderDeltaApplyResult DrainUpdateBoundary()
    {
        ObjectDisposedException.ThrowIf(_disposed, this);
        _lastApply = _journal.DrainTo(_scene);
        _cumulativeApply = Add(_cumulativeApply, _lastApply);
        _drainSequence = checked(_drainSequence + 1);
        return _lastApply;
    }

    public RenderSceneMemoryAccounting CaptureMemory()
    {
        ObjectDisposedException.ThrowIf(_disposed, this);
        RenderSceneMemoryAccounting memory = _scene.Memory;
        return memory with
        {
            EstimatedJournalBufferBytes = checked(
                (long)_journal.Capacity
                * Unsafe.SizeOf<RenderProjectionDelta>()),
            EstimatedSynchronizationSourceBytes = checked(
                (long)(StaticProjections.ProjectionCount
                    + (_live?.ProjectionCount ?? 0))
                * Unsafe.SizeOf<RenderProjectionRecord>()),
        };
    }

    public RenderSceneDigest BuildDigest()
    {
        ObjectDisposedException.ThrowIf(_disposed, this);
        return _scene.BuildDigest(_digestBuffer);
    }

    public void ClearDirty()
    {
        ObjectDisposedException.ThrowIf(_disposed, this);
        _scene.ClearDirty();
    }

    public void Clear(RenderSceneGeneration replacementGeneration)
    {
        ObjectDisposedException.ThrowIf(_disposed, this);
        _live?.ResetTracking();
        StaticProjections.Clear(replacementGeneration);
        _scene.Clear(replacementGeneration);
        _lastApply = default;
        _cumulativeApply = default;
        _drainSequence = 0;
    }

    public void Dispose()
    {
        if (_disposed)
            return;
        _disposed = true;
        _live?.ResetTracking();
        _scene.Dispose();
    }

    private static RenderDeltaApplyResult Add(
        RenderDeltaApplyResult left,
        RenderDeltaApplyResult right) =>
        new(
            Applied: checked(left.Applied + right.Applied),
            Registered: checked(left.Registered + right.Registered),
            Updated: checked(left.Updated + right.Updated),
            Replaced: checked(left.Replaced + right.Replaced),
            Unregistered: checked(left.Unregistered + right.Unregistered),
            RejectedGeneration: checked(
                left.RejectedGeneration + right.RejectedGeneration),
            RejectedOutOfOrderSequence: checked(
                left.RejectedOutOfOrderSequence
                + right.RejectedOutOfOrderSequence),
            RejectedStaleIncarnation: checked(
                left.RejectedStaleIncarnation
                + right.RejectedStaleIncarnation),
            RejectedMissing: checked(
                left.RejectedMissing + right.RejectedMissing));
}

internal sealed class RenderSceneShadowComparisonController :
    IRenderFramePostDiagnosticsPhase,
    IRenderSceneShadowSnapshotSource
{
    private const ulong ComparisonCadenceFrames = 30;
    private const byte StaticProjectionDomain = 1;
    private const byte EnvCellProjectionDomain = 2;
    private const byte LiveProjectionDomain = 3;

    private readonly RenderSceneShadowRuntime _shadow;
    private readonly CurrentRenderSceneOracle _oracle;
    private readonly Action<string>? _log;
    private readonly bool _acknowledgeDirty;
    private readonly List<RenderProjectionRecord> _sceneRecords = [];
    private readonly HashSet<RenderProjectionId> _expectedIds = [];
    private ulong _renderFrameSequence;
    private ulong _comparisonCount;
    private ulong _successfulComparisonCount;
    private long _mismatchCount;
    private string? _firstMismatch;
    private int _matchedProjectionCount;
    private ulong _comparedOracleFrameSequence;

    public RenderSceneShadowComparisonController(
        RenderSceneShadowRuntime shadow,
        CurrentRenderSceneOracle oracle,
        Action<string>? log = null,
        bool acknowledgeDirty = true)
    {
        _shadow = shadow ?? throw new ArgumentNullException(nameof(shadow));
        _oracle = oracle ?? throw new ArgumentNullException(nameof(oracle));
        _log = log;
        _acknowledgeDirty = acknowledgeDirty;
        Snapshot = BuildSnapshot();
    }

    public RenderSceneShadowComparisonSnapshot Snapshot { get; private set; }

    public void Process(RenderFrameInput input, RenderFrameOutcome outcome)
    {
        _renderFrameSequence = checked(_renderFrameSequence + 1);
        if (outcome.World.NormalWorldDrawn
            && _renderFrameSequence % ComparisonCadenceFrames == 0)
        {
            Compare(force: false);
        }
        else
        {
            Snapshot = BuildSnapshot();
        }
    }

    public RenderSceneShadowComparisonSnapshot CaptureCheckpointSnapshot()
    {
        Compare(force: true);
        return Snapshot;
    }

    internal void CompareNow() => Compare(force: true);

    private void Compare(bool force)
    {
        CurrentRenderSceneOracleSnapshot current = _oracle.Snapshot;
        if (current.CompletedFrameSequence == 0
            || (!force
                && current.CompletedFrameSequence
                    == _comparedOracleFrameSequence))
        {
            Snapshot = BuildSnapshot();
            return;
        }

        _comparisonCount = checked(_comparisonCount + 1);
        _comparedOracleFrameSequence = current.CompletedFrameSequence;
        _matchedProjectionCount = 0;
        string? mismatch = CompareCurrentProjection();
        if (mismatch is null)
        {
            _successfulComparisonCount =
                checked(_successfulComparisonCount + 1);
        }
        else
        {
            _mismatchCount = checked(_mismatchCount + 1);
            if (_firstMismatch is null)
            {
                _firstMismatch = mismatch;
                _log?.Invoke(
                    "[render-scene-shadow] first mismatch: " + mismatch);
            }
        }

        if (_acknowledgeDirty && mismatch is null)
            _shadow.ClearDirty();
        Snapshot = BuildSnapshot();
    }

    private string? CompareCurrentProjection()
    {
        if (_shadow.PendingDeltaCount != 0)
        {
            return $"sourceChannel=journal pendingDeltas={_shadow.PendingDeltaCount}";
        }
        if (_shadow.CumulativeApply.Rejected != 0)
        {
            return "sourceChannel=journal rejectedDeltas="
                + _shadow.CumulativeApply.Rejected;
        }

        RenderSceneQuery query = _shadow.Query;
        _expectedIds.Clear();
        IReadOnlyList<CurrentRenderProjectionFingerprint> expected =
            _oracle.Projections;
        for (int i = 0; i < expected.Count; i++)
        {
            CurrentRenderProjectionFingerprint fingerprint = expected[i];
            RenderProjectionId id = ExpectedId(in fingerprint);
            _expectedIds.Add(id);
            if (!query.TryGet(id, out RenderProjectionRecord actual))
            {
                return $"sourceChannel={Channel(id)} id={id} field=presence "
                    + "expected=present actual=missing "
                    + $"expectedClass={fingerprint.ProjectionClass} "
                    + $"serverGuid=0x{fingerprint.ServerGuid:X8} "
                    + $"localEntityId=0x{fingerprint.EntityId:X8} "
                    + $"parentCell=0x{fingerprint.ParentCellId:X8}";
            }

            string? mismatch = CompareProjection(
                in fingerprint,
                in actual);
            if (mismatch is not null)
                return mismatch;
            _matchedProjectionCount++;
        }

        int total = query.Counts.Total;
        if (_sceneRecords.Capacity < total)
            _sceneRecords.Capacity = total;
        CollectionsMarshal.SetCount(_sceneRecords, total);
        int copied = query.CopyTo(CollectionsMarshal.AsSpan(_sceneRecords));
        if (copied != total)
        {
            return $"sourceChannel=scene field=count expected={total} "
                + $"actual={copied}";
        }

        for (int i = 0; i < copied; i++)
        {
            RenderProjectionRecord record = _sceneRecords[i];
            if (_expectedIds.Contains(record.Id)
                || Domain(record.Id) == EnvCellProjectionDomain)
            {
                continue;
            }

            bool indoorStatic =
                record.ProjectionClass
                    is RenderProjectionClass.IndoorCellStatic
                || (record.ProjectionClass
                        is RenderProjectionClass.ActiveAnimatedStatic
                    && InteriorEntityPartition.IsIndoorCellId(
                        record.Source.ParentCellId));
            if (indoorStatic
                || (record.Flags & RenderProjectionFlags.Draw) == 0)
            {
                continue;
            }

            return $"sourceChannel={Channel(record.Id)} id={record.Id} "
                + "field=presence expected=absent actual=drawn";
        }

        return null;
    }

    private static string? CompareProjection(
        in CurrentRenderProjectionFingerprint expected,
        in RenderProjectionRecord actual)
    {
        RenderProjectionId id = actual.Id;
        string channel = Channel(id);
        string? classMismatch = CompareClass(
            expected.ProjectionClass,
            in actual);
        if (classMismatch is not null)
            return Prefix(channel, id, "projectionClass", classMismatch);
        if (actual.Residency.OwnerLandblockId != expected.LandblockId)
        {
            return Prefix(
                channel,
                id,
                "landblock",
                $"{expected.LandblockId:X8}/{actual.Residency.OwnerLandblockId:X8}");
        }
        if (actual.Source.LocalEntityId != expected.EntityId)
            return Values(channel, id, "entityId", expected.EntityId, actual.Source.LocalEntityId);
        if (actual.Source.ServerGuid != expected.ServerGuid)
            return Values(channel, id, "serverGuid", expected.ServerGuid, actual.Source.ServerGuid);
        if (actual.Source.SourceId != expected.SourceId)
            return Values(channel, id, "sourceId", expected.SourceId, actual.Source.SourceId);
        if (actual.Source.ParentCellId != expected.ParentCellId)
            return Values(channel, id, "parentCell", expected.ParentCellId, actual.Source.ParentCellId);
        if (actual.Source.EffectCellId != expected.EffectCellId)
            return Values(channel, id, "effectCell", expected.EffectCellId, actual.Source.EffectCellId);
        if (actual.Source.BuildingShellAnchorCellId
            != expected.BuildingShellAnchorCellId)
        {
            return Values(
                channel,
                id,
                "buildingShellAnchor",
                expected.BuildingShellAnchorCellId,
                actual.Source.BuildingShellAnchorCellId);
        }
        if (actual.Source.CurrentProjectionFlags != expected.Flags)
            return Values(channel, id, "flags", expected.Flags, actual.Source.CurrentProjectionFlags);
        if (actual.MeshSet.MeshCount != expected.MeshCount)
            return Values(channel, id, "meshCount", expected.MeshCount, actual.MeshSet.MeshCount);
        if (actual.Source.TransformFingerprint != expected.Transform)
            return Prefix(channel, id, "transform", $"{expected.Transform}/{actual.Source.TransformFingerprint}");
        if (actual.Source.GeometryFingerprint != expected.Geometry)
            return Prefix(channel, id, "geometry", $"{expected.Geometry}/{actual.Source.GeometryFingerprint}");
        if (actual.Source.AppearanceFingerprint != expected.Appearance)
            return Prefix(channel, id, "appearance", $"{expected.Appearance}/{actual.Source.AppearanceFingerprint}");
        return null;
    }

    private static string? CompareClass(
        InteriorEntityPartition.ProjectionClass expected,
        in RenderProjectionRecord actual)
    {
        bool matches = expected switch
        {
            InteriorEntityPartition.ProjectionClass.OutdoorStatic =>
                actual.ProjectionClass
                    is RenderProjectionClass.OutdoorStatic
                    or RenderProjectionClass.ActiveAnimatedStatic
                && !InteriorEntityPartition.IsIndoorCellId(
                    actual.Source.ParentCellId),
            InteriorEntityPartition.ProjectionClass.CellStatic =>
                actual.ProjectionClass
                    is RenderProjectionClass.IndoorCellStatic
                    or RenderProjectionClass.ActiveAnimatedStatic
                && InteriorEntityPartition.IsIndoorCellId(
                    actual.Source.ParentCellId),
            InteriorEntityPartition.ProjectionClass.Dynamic =>
                actual.ProjectionClass
                    is RenderProjectionClass.LiveDynamicRoot
                    or RenderProjectionClass.EquippedChild,
            _ => false,
        };
        return matches
            ? null
            : $"{expected}/{actual.ProjectionClass}";
    }

    private RenderSceneShadowComparisonSnapshot BuildSnapshot()
    {
        RenderSceneQuery query = _shadow.Query;
        return new RenderSceneShadowComparisonSnapshot(
            Enabled: true,
            RenderFrameSequence: _renderFrameSequence,
            ComparisonCount: _comparisonCount,
            SuccessfulComparisonCount: _successfulComparisonCount,
            ComparedOracleFrameSequence: _comparedOracleFrameSequence,
            MatchedProjectionCount: _matchedProjectionCount,
            MismatchCount: _mismatchCount,
            FirstMismatch: _firstMismatch,
            PendingDeltaCount: _shadow.PendingDeltaCount,
            DrainSequence: _shadow.DrainSequence,
            LastApply: _shadow.LastApply,
            CumulativeApply: _shadow.CumulativeApply,
            SceneCounts: query.Counts,
            SceneIndexCounts: query.IndexCounts,
            Memory: _shadow.CaptureMemory(),
            Digest: _shadow.BuildDigest());
    }

    private static RenderProjectionId ExpectedId(
        in CurrentRenderProjectionFingerprint fingerprint) =>
        fingerprint.ServerGuid == 0
            ? StaticRenderProjectionJournal.StaticEntityId(
                fingerprint.LandblockId,
                fingerprint.EntityId)
            : LiveRenderProjectionJournal.ProjectionId(
                fingerprint.EntityId);

    private static byte Domain(RenderProjectionId id) =>
        id.Domain;

    private static string Channel(RenderProjectionId id) =>
        Domain(id) switch
        {
            StaticProjectionDomain => "static",
            EnvCellProjectionDomain => "envCell",
            LiveProjectionDomain => "live",
            byte domain => $"unknown:{domain}",
        };

    private static string Values<T>(
        string channel,
        RenderProjectionId id,
        string field,
        T expected,
        T actual) =>
        Prefix(channel, id, field, $"{expected}/{actual}");

    private static string Prefix(
        string channel,
        RenderProjectionId id,
        string field,
        string values) =>
        $"sourceChannel={channel} id={id} field={field} "
        + $"expected/actual={values}";
}
