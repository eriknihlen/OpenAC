using System;
using System.Collections.Generic;
using System.Diagnostics;
using AcDream.App.Rendering.Wb;
using AcDream.Core.Terrain;
using AcDream.Core.World;

namespace AcDream.App.Streaming;

public sealed class StreamingController
    : IStreamingFrameBackend,
      IWorldRevealStreamingScheduler
{
    private readonly record struct DestinationReservation(
        long RevealGeneration,
        uint LandblockId,
        int Radius);

    private sealed class OriginRecenterRetirement
    {
        public bool RadiiConverged;
        public bool GenerationAdvanced;
        public bool PendingLoadsCleared;
        public bool CompletionQueueCleared;
        public bool PendingPublicationsCleared;
        public bool RegionCleared;
        public bool SpatialGenerationDetached;
        public bool PreparationCommitted;
        public (int X, int Y, bool IsSealedDungeon)? Destination;
        public bool DestinationConfigured;
        public bool DestinationLoadEnqueued;
    }

    private sealed class FullWindowRetirement
    {
        public bool GenerationAdvanced;
        public bool PendingLoadsCleared;
        public bool CompletionQueueCleared;
        public bool PendingPublicationsCleared;
        public bool RegionCleared;
        public List<uint>? ResidentIds;
        public IEnumerator<uint>? ResidentEnumerator;
        public int RetirementCursor;
        public bool PreparationCommitted;
    }

    private readonly Action<uint, LandblockStreamJobKind, ulong> _enqueueLoad;
    private readonly Action<uint, ulong> _enqueueUnload;
    private readonly ILandblockCompletionSource _completionSource;
    private readonly Action? _clearPendingLoads;
    private readonly LandblockPresentationPipeline _presentation;
    private readonly Func<LandblockStreamResult, bool>
        _isPublicationBlockedByRetirement;
    private readonly StreamingWorkBudgetOptions _configuredWorkBudgetOptions;
    private StreamingWorkBudget _workBudget;
    private readonly Func<long> _workTimestamp;
    private readonly long _workTimestampFrequency;
    private StreamingWorkMeter? _activeWorkMeter;
    private StreamingWorkMeterSnapshot _lastWorkMeter;
    private readonly StreamingCompletionQueue _completionQueue = new();
    private long _nextCompletionSequence;
    private int _legacyCompletionProfile = 4;
    private DestinationReservation? _destinationReservation;
    private long _lifetimeWorkOverruns;
    private long _lifetimeOversizedProgress;
    private double _maximumWorkFrameMilliseconds;
    private string? _maximumWorkFrameStage;
    private double _maximumWorkOperationMilliseconds;
    private string? _maximumWorkOperationStage;
    private readonly GpuWorldState _state;
    private StreamingRegion? _region;
    private RadiiReconfiguration? _pendingRadiiReconfiguration;
    private bool _advancingRadiiReconfiguration;
    private (int NearRadius, int FarRadius)? _deferredRadiiRequest;
    private OriginRecenterRetirement? _originRecenterRetirement;
    private FullWindowRetirement? _fullWindowRetirement;
    private bool _advancingOriginRecenter;
    private ulong _generation;

    private bool _collapsed;

    private uint _collapsedCenter;

    public int NearRadius { get; private set; }

    /// <summary>
    /// Far-tier radius (LBs from observer that load terrain only).
    /// </summary>
    public int FarRadius { get; private set; }

    public int MaxCompletionsPerFrame
    {
        get => _legacyCompletionProfile;
        set
        {
            StreamingWorkBudgetOptions profile =
                _configuredWorkBudgetOptions
                    .ScaleForLegacyCompletionCount(value);
            _legacyCompletionProfile = value;
            _workBudget = profile.ToBudget();
        }
    }

    internal long ActiveRevealGeneration =>
        _destinationReservation?.RevealGeneration ?? 0L;

    internal uint DestinationLandblockId =>
        _destinationReservation?.LandblockId ?? 0u;

    internal int DestinationRadius =>
        _destinationReservation?.Radius ?? 0;

    internal void BeginDestinationReservation(
        long revealGeneration,
        uint destinationCell,
        int requiredRenderRadius)
    {
        if (revealGeneration <= 0)
            throw new ArgumentOutOfRangeException(nameof(revealGeneration));
        if (destinationCell == 0u)
            throw new ArgumentOutOfRangeException(nameof(destinationCell));
        if (requiredRenderRadius < 0)
            throw new ArgumentOutOfRangeException(nameof(requiredRenderRadius));

        _destinationReservation = new DestinationReservation(
            revealGeneration,
            (destinationCell & 0xFFFF0000u) | 0xFFFFu,
            requiredRenderRadius);
    }

    internal void EndDestinationReservation(long revealGeneration)
    {
        if (_destinationReservation is { } active
            && active.RevealGeneration == revealGeneration)
        {
            _destinationReservation = null;
        }
    }

    void IWorldRevealStreamingScheduler.BeginDestinationReservation(
        long revealGeneration,
        uint destinationCell,
        int requiredRenderRadius) =>
        BeginDestinationReservation(
            revealGeneration,
            destinationCell,
            requiredRenderRadius);

    void IWorldRevealStreamingScheduler.EndDestinationReservation(
        long revealGeneration) =>
        EndDestinationReservation(revealGeneration);

    public int DeferredApplyBacklog => _completionQueue.Count;
    public int PendingRetirementCount => _presentation.PendingRetirementCount;
    public StreamingWorkDiagnostics WorkDiagnostics
    {
        get
        {
            StreamingCompletionQueueSnapshot queued =
                _completionQueue.CaptureSnapshot();
            return new StreamingWorkDiagnostics(
                _lastWorkMeter,
                _lifetimeWorkOverruns,
                _lifetimeOversizedProgress,
                _maximumWorkFrameMilliseconds,
                _maximumWorkFrameStage,
                _maximumWorkOperationMilliseconds,
                _maximumWorkOperationStage,
                queued.Count,
                queued.RetainedCpuBytes,
                queued.OldestAgeMilliseconds,
                _presentation.PendingPublicationCount,
                _presentation.PendingRetirementCount,
                _completionSource.BacklogCount,
                queued.Destination,
                queued.Control,
                queued.Unload,
                queued.Near,
                queued.Far);
        }
    }
    internal bool IsCollapsedToDungeon => _collapsed;

    internal bool IsLandblockPresentationReady(uint landblockId) =>
        _presentation.IsLandblockPresentationReady(landblockId);

    public bool IsRenderNeighborhoodResident(
        uint cellOrLandblockId,
        int nearRadius,
        int farRadius)
    {
        if (nearRadius < 0)
            throw new ArgumentOutOfRangeException(nameof(nearRadius));
        if (farRadius < nearRadius)
        {
            throw new ArgumentOutOfRangeException(
                nameof(farRadius),
                "Far radius must be greater than or equal to near radius.");
        }

        if (!AcDream.Core.Physics.LandDefs.InboundValidCellId(cellOrLandblockId))
            return false;
        int cx = (int)((cellOrLandblockId >> 24) & 0xFFu);
        int cy = (int)((cellOrLandblockId >> 16) & 0xFFu);
        for (int dx = -farRadius; dx <= farRadius; dx++)
        for (int dy = -farRadius; dy <= farRadius; dy++)
        {
            int nx = cx + dx;
            int ny = cy + dy;
            if (nx < 0 || nx > 254 || ny < 0 || ny > 254)
                continue;

            uint canonical = ((uint)nx << 24) | ((uint)ny << 16) | 0xFFFFu;
            if (!_presentation.IsLandblockPresentationReady(canonical))
                return false;
            if (!_state.IsRenderReady(canonical))
                return false;
            bool isInnerRing = Math.Abs(dx) <= nearRadius
                && Math.Abs(dy) <= nearRadius;
            if (isInnerRing && !_state.IsNearTier(canonical))
                return false;
        }

        return true;
    }

    public int FullWindowRetirementCount { get; private set; }
    public int LastFullWindowRetirementLandblockCount { get; private set; }

    internal StreamingController(
        Action<uint, LandblockStreamJobKind, ulong> enqueueLoad,
        Action<uint, ulong> enqueueUnload,
        Func<int, IReadOnlyList<LandblockStreamResult>> drainCompletions,
        Action<LandblockBuild, LandblockMeshData> applyTerrain,
        GpuWorldState state,
        int nearRadius,
        int farRadius,
        Action<uint>? removeTerrain = null,
        Action<uint>? demoteNearLayer = null,
        Action? clearPendingLoads = null,
        Action<uint>? onLandblockLoaded = null,
        Action<EnvCellLandblockBuild>? ensureEnvCellMeshes = null,
        LandblockRetirementCoordinator? retirementCoordinator = null,
        StreamingWorkBudgetOptions? workBudgetOptions = null)
        : this(
            enqueueLoad,
            enqueueUnload,
            drainCompletions,
            state,
            nearRadius,
            farRadius,
            new LandblockPresentationPipeline(
                applyTerrain,
                state,
                onLandblockLoaded,
                ensureEnvCellMeshes,
                retirementCoordinator,
                removeTerrain,
                demoteNearLayer),
            clearPendingLoads,
            workBudgetOptions)
    {
    }

    internal StreamingController(
        Action<uint, LandblockStreamJobKind, ulong> enqueueLoad,
        Action<uint, ulong> enqueueUnload,
        Func<int, IReadOnlyList<LandblockStreamResult>> drainCompletions,
        GpuWorldState state,
        int nearRadius,
        int farRadius,
        LandblockPresentationPipeline presentationPipeline,
        Action? clearPendingLoads = null,
        StreamingWorkBudgetOptions? workBudgetOptions = null)
        : this(
            enqueueLoad,
            enqueueUnload,
            new DelegateLandblockCompletionSource(drainCompletions),
            state,
            nearRadius,
            farRadius,
            presentationPipeline,
            clearPendingLoads,
            workBudgetOptions)
    {
    }

    public StreamingController(
        Action<uint, LandblockStreamJobKind, ulong> enqueueLoad,
        Action<uint, ulong> enqueueUnload,
        ILandblockCompletionSource completionSource,
        GpuWorldState state,
        int nearRadius,
        int farRadius,
        LandblockPresentationPipeline presentationPipeline,
        Action? clearPendingLoads = null,
        StreamingWorkBudgetOptions? workBudgetOptions = null)
        : this(
            enqueueLoad,
            enqueueUnload,
            completionSource,
            state,
            nearRadius,
            farRadius,
            presentationPipeline,
            Stopwatch.GetTimestamp,
            Stopwatch.Frequency,
            clearPendingLoads,
            workBudgetOptions)
    {
    }

    internal StreamingController(
        Action<uint, LandblockStreamJobKind, ulong> enqueueLoad,
        Action<uint, ulong> enqueueUnload,
        ILandblockCompletionSource completionSource,
        GpuWorldState state,
        int nearRadius,
        int farRadius,
        LandblockPresentationPipeline presentationPipeline,
        Func<long> workTimestamp,
        long workTimestampFrequency,
        Action? clearPendingLoads = null,
        StreamingWorkBudgetOptions? workBudgetOptions = null)
    {
        _enqueueLoad = enqueueLoad;
        _enqueueUnload = enqueueUnload;
        _completionSource = completionSource
            ?? throw new ArgumentNullException(nameof(completionSource));
        _clearPendingLoads = clearPendingLoads;
        _state = state;
        _presentation = presentationPipeline
            ?? throw new ArgumentNullException(nameof(presentationPipeline));
        _isPublicationBlockedByRetirement =
            IsPublicationBlockedByRetirement;
        _configuredWorkBudgetOptions =
            workBudgetOptions ?? StreamingWorkBudgetOptions.Default;
        _workBudget = _configuredWorkBudgetOptions.ToBudget();
        _workTimestamp = workTimestamp
            ?? throw new ArgumentNullException(nameof(workTimestamp));
        if (workTimestampFrequency <= 0)
            throw new ArgumentOutOfRangeException(nameof(workTimestampFrequency));
        _workTimestampFrequency = workTimestampFrequency;
        if (!_presentation.MatchesState(_state))
        {
            throw new ArgumentException(
                "The presentation pipeline must own the controller's world state.",
                nameof(presentationPipeline));
        }
        NearRadius = nearRadius;
        FarRadius  = farRadius;
    }

    public void ReconfigureRadii(int nearRadius, int farRadius)
    {
        if (nearRadius < 0)
            throw new ArgumentOutOfRangeException(nameof(nearRadius));
        if (farRadius < nearRadius)
            throw new ArgumentOutOfRangeException(
                nameof(farRadius),
                "Far radius must be greater than or equal to near radius.");

        if (_originRecenterRetirement is not null)
        {
            _deferredRadiiRequest = (nearRadius, farRadius);
            return;
        }

        if (_advancingRadiiReconfiguration)
        {
            _deferredRadiiRequest = (nearRadius, farRadius);
            return;
        }

        if (_pendingRadiiReconfiguration is not null)
            AdvanceRadiiReconfiguration();

        _deferredRadiiRequest = null;

        if (nearRadius == NearRadius && farRadius == FarRadius)
            return;

        if (!ConvergePendingPublications())
        {
            _deferredRadiiRequest = (nearRadius, farRadius);
            return;
        }

        if (_collapsed || _region is null)
        {
            NearRadius = nearRadius;
            FarRadius = farRadius;
            return;
        }

        var rebuilt = new StreamingRegion(
            _region.CenterX,
            _region.CenterY,
            nearRadius,
            farRadius);
        TwoTierDiff bootstrap = rebuilt.ComputeFirstTickDiff();
        var desiredNear = new HashSet<uint>(bootstrap.ToLoadNear);
        var desiredFar = new HashSet<uint>(bootstrap.ToLoadFar);
        var mutations = new List<RadiiMutation>();

        uint[] loaded = [.. _state.LoadedLandblockIds];
        for (int i = 0; i < loaded.Length; i++)
        {
            uint id = loaded[i];
            if (desiredNear.Contains(id))
            {
                if (!_state.IsNearTier(id))
                    mutations.Add(new RadiiMutation(
                        () => EnqueueLoad(id, LandblockStreamJobKind.PromoteToNear)));
            }
            else if (desiredFar.Contains(id))
            {
                if (_state.IsNearTier(id))
                    mutations.Add(new RadiiMutation(() => DemoteLandblock(id)));
            }
            else
            {
                mutations.Add(new RadiiMutation(() => EnqueueUnload(id)));
            }
        }

        foreach (uint id in desiredNear)
            if (!_state.IsLoaded(id))
                mutations.Add(new RadiiMutation(
                    () => EnqueueLoad(id, LandblockStreamJobKind.LoadNear)));
        foreach (uint id in desiredFar)
            if (!_state.IsLoaded(id))
                mutations.Add(new RadiiMutation(
                    () => EnqueueLoad(id, LandblockStreamJobKind.LoadFar)));

        _pendingRadiiReconfiguration = new RadiiReconfiguration(
            nearRadius,
            farRadius,
            rebuilt,
            mutations);
        AdvanceRadiiReconfiguration();
    }

    internal StreamingController(
        Action<uint, LandblockStreamJobKind> enqueueLoad,
        Action<uint> enqueueUnload,
        Func<int, IReadOnlyList<LandblockStreamResult>> drainCompletions,
        Action<LandblockBuild, LandblockMeshData> applyTerrain,
        GpuWorldState state,
        int nearRadius,
        int farRadius,
        Action<uint>? removeTerrain = null,
        Action<uint>? demoteNearLayer = null,
        Action? clearPendingLoads = null,
        Action<uint>? onLandblockLoaded = null,
        Action<EnvCellLandblockBuild>? ensureEnvCellMeshes = null,
        LandblockRetirementCoordinator? retirementCoordinator = null,
        StreamingWorkBudgetOptions? workBudgetOptions = null)
        : this(
            (id, kind, _) => enqueueLoad(id, kind),
            (id, _) => enqueueUnload(id),
            drainCompletions,
            applyTerrain,
            state,
            nearRadius,
            farRadius,
            removeTerrain,
            demoteNearLayer,
            clearPendingLoads,
            onLandblockLoaded,
            ensureEnvCellMeshes,
            retirementCoordinator,
            workBudgetOptions)
    {
    }

    private void EnqueueLoad(uint id, LandblockStreamJobKind kind) =>
        _enqueueLoad(id, kind, _generation);

    private void EnqueueUnload(uint id) => _enqueueUnload(id, _generation);

    private void DemoteLandblock(uint id)
    {
        uint canonical = (id & 0xFFFF0000u) | 0xFFFFu;
        _presentation.EnqueueNearLayerRetirement(canonical);
    }

    private bool AdvanceGeneration()
    {
        if (!ConvergePendingPublications())
            return false;
        _generation = unchecked(_generation + 1);
        return true;
    }

    private bool ConvergePendingPublications(
        bool preferDestination = false)
    {
        IReadOnlyList<LandblockStreamResult> pending =
            _presentation.GetPendingPublicationResults();
        if (pending.Count == 0)
            return true;
        if (_activeWorkMeter is null)
        {
            return false;
        }

        try
        {
            bool progressed = false;
            bool destinationPending = false;
            if (preferDestination)
            {
                for (int i = 0; i < pending.Count; i++)
                {
                    if (!IsDestinationWork(pending[i].LandblockId))
                        continue;
                    destinationPending = true;
                    break;
                }
            }
            for (int destinationPass = 1; destinationPass >= 0; destinationPass--)
            {
                bool requireDestination = destinationPass != 0;
                for (int i = 0; i < pending.Count; i++)
                {
                    LandblockStreamResult result = pending[i];
                    bool isDestination = IsDestinationWork(result.LandblockId);
                    if (destinationPending && !isDestination)
                        continue;
                    if (isDestination != requireDestination)
                        continue;

                    using StreamingWorkMeter.LaneScope lane =
                        _activeWorkMeter.EnterLane(
                            isDestination
                                ? StreamingWorkLane.Destination
                                : StreamingWorkLane.NonDestination);
                    LandblockPublicationAdvance advance =
                        _presentation.ResumePublication(
                            result,
                            _activeWorkMeter,
                            ensureProgress: !progressed);
                    progressed |= advance.Progressed;
                    if (!advance.Completed)
                        return false;
                }
            }
            return true;
        }
        finally
        {
            _completionQueue.RemoveResults(
                pending,
                result => !_presentation.HasPendingPublication(result));
        }
    }

    public void Tick(int observerCx, int observerCy, bool insideDungeon = false)
    {
        if (_activeWorkMeter is not null)
            throw new InvalidOperationException(
                "StreamingController.Tick cannot be reentered.");

        bool destinationHold = _destinationReservation is not null;
        var meter = new StreamingWorkMeter(
            destinationHold
                ? _workBudget.WidenForDestinationHold(
                    _configuredWorkBudgetOptions
                        .HoldDestinationCeilingMilliseconds)
                : _workBudget,
            _workTimestamp,
            _workTimestampFrequency,
            destinationReservationActive: destinationHold);
        _activeWorkMeter = meter;
        try
        {
            if (_fullWindowRetirement is not null)
            {
                bool retirementWasPending =
                    _presentation.PendingRetirementCount != 0;
                TryAdvanceFullWindowRetirement(meter);
                AdvanceDestinationRetirementDependency(meter);
                if (_presentation.UsesBudgetedRetirementSteps
                    || retirementWasPending)
                {
                    _presentation.AdvanceRetirements(meter);
                }
                if (_fullWindowRetirement is
                    {
                        PreparationCommitted: true,
                    }
                    && _presentation.PendingRetirementCount == 0)
                {
                    _fullWindowRetirement = null;
                }
                return;
            }

            if (_originRecenterRetirement is not null)
            {
                bool retirementWasPending =
                    _presentation.PendingRetirementCount != 0;
                TryAdvanceOriginRecenterPreparation(meter);
                AdvanceDestinationRetirementDependency(meter);
                if (_presentation.UsesBudgetedRetirementSteps
                    || retirementWasPending)
                {
                    _presentation.AdvanceRetirements(meter);
                }
                return;
            }

            if (_pendingRadiiReconfiguration is not null)
                AdvanceRadiiReconfiguration();
            else if (_deferredRadiiRequest is { } deferred)
            {
                _deferredRadiiRequest = null;
                ReconfigureRadii(deferred.NearRadius, deferred.FarRadius);
            }

            bool retirementWasPendingAtFrameStart =
                _presentation.PendingRetirementCount != 0;
            AdvanceDestinationRetirementDependency(meter);
            _presentation.AdvanceRetirements(meter);
            bool destinationPublicationIncomplete =
                _destinationReservation is not null
                && !IsRenderNeighborhoodResident(
                    DestinationLandblockId,
                    Math.Min(NearRadius, DestinationRadius),
                    DestinationRadius);
            if (!ConvergePendingPublications(
                    preferDestination: destinationPublicationIncomplete))
                return;

            uint centerId = StreamingRegion.EncodeLandblockId(observerCx, observerCy);

            if (_collapsed)
            {
                if (insideDungeon && centerId != _collapsedCenter)
                    EnterDungeonCollapse(observerCx, observerCy, centerId);
                else if (!insideDungeon && ChebyshevLandblocks(centerId, _collapsedCenter) > 1)
                    ExitDungeonExpand(observerCx, observerCy);
                else
                    SweepCollapsed();
            }
            else if (insideDungeon)
            {
                EnterDungeonCollapse(observerCx, observerCy, centerId);
            }
            else
            {
                NormalTick(observerCx, observerCy);
            }

            DrainAndApply(
                preferDestination: destinationPublicationIncomplete);
            if (_presentation.UsesBudgetedRetirementSteps
                && !retirementWasPendingAtFrameStart)
                _presentation.AdvanceRetirements(meter);
        }
        finally
        {
            meter.FinishFrame();
            StreamingWorkMeterSnapshot snapshot = meter.Snapshot;
            ObserveWorkLifetime(snapshot);
            _lastWorkMeter = snapshot;
            _activeWorkMeter = null;
            if (PublicationTimingProbe.Enabled)
            {
                PublicationTimingProbe.ObserveStreamingTick(
                    snapshot,
                    _completionSource.BacklogCount,
                    _completionQueue.Count);
            }
        }
    }

    private void AdvanceDestinationRetirementDependency(
        StreamingWorkMeter meter)
    {
        if (_destinationReservation is null
            || !_presentation.IsRetirementPending(DestinationLandblockId))
        {
            return;
        }

        using StreamingWorkMeter.LaneScope lane =
            meter.EnterLane(StreamingWorkLane.Destination);
        _presentation.AdvancePriorityRetirement(
            DestinationLandblockId,
            meter);
    }

    private void ObserveWorkLifetime(StreamingWorkMeterSnapshot snapshot)
    {
        _lifetimeWorkOverruns = SaturatingAdd(
            _lifetimeWorkOverruns,
            snapshot.OverrunCount);
        _lifetimeOversizedProgress = SaturatingAdd(
            _lifetimeOversizedProgress,
            snapshot.OversizedProgressCount);

        if (_maximumWorkFrameStage is null
            || snapshot.ElapsedMilliseconds > _maximumWorkFrameMilliseconds)
        {
            _maximumWorkFrameMilliseconds = snapshot.ElapsedMilliseconds;
            _maximumWorkFrameStage = snapshot.LastStage;
        }

        if (_maximumWorkOperationStage is null
            || snapshot.MaximumOperationMilliseconds
                > _maximumWorkOperationMilliseconds)
        {
            _maximumWorkOperationMilliseconds =
                snapshot.MaximumOperationMilliseconds;
            _maximumWorkOperationStage = snapshot.MaximumOperationStage;
        }
    }

    private static long SaturatingAdd(long left, int right) =>
        left > long.MaxValue - right ? long.MaxValue : left + right;

    private void AdvanceRadiiReconfiguration()
    {
        if (_advancingRadiiReconfiguration)
            return;

        RadiiReconfiguration pending = _pendingRadiiReconfiguration
            ?? throw new InvalidOperationException("No radii reconfiguration is pending.");
        var failures = new List<Exception>();
        _advancingRadiiReconfiguration = true;
        try
        {

            if (!pending.GenerationAdvanced)
            {
                if (!AdvanceGeneration())
                    return;
                pending.GenerationAdvanced = true;
            }

            if (!pending.PendingLoadsCleared)
            {
                try
                {
                    _clearPendingLoads?.Invoke();
                    pending.PendingLoadsCleared = true;
                }
                catch (Exception error)
                {
                    if (error is StreamingMutationException { MutationCommitted: true })
                        pending.PendingLoadsCleared = true;
                    failures.Add(error);
                }
            }

            for (int i = 0; pending.PendingLoadsCleared && i < pending.Mutations.Count; i++)
            {
                RadiiMutation mutation = pending.Mutations[i];
                if (mutation.Completed)
                    continue;
                try
                {
                    mutation.Apply();
                    mutation.Completed = true;
                }
                catch (Exception error)
                {
                    if (error is StreamingMutationException { MutationCommitted: true })
                        mutation.Completed = true;
                    failures.Add(error);
                }
            }

            bool converged = pending.PendingLoadsCleared
                && pending.Mutations.All(static mutation => mutation.Completed);
            if (converged)
            {
                pending.Region.MarkResidentFromBootstrap();
                _region = pending.Region;
                NearRadius = pending.NearRadius;
                FarRadius = pending.FarRadius;
                _completionQueue.Clear();
                _pendingRadiiReconfiguration = null;
            }
        }
        finally
        {
            _advancingRadiiReconfiguration = false;
        }

        if (failures.Count != 0)
        {
            throw new AggregateException(
                "Streaming quality reconfiguration did not complete cleanly.",
                failures);
        }

        if (_pendingRadiiReconfiguration is null
            && _deferredRadiiRequest is { } deferred)
        {
            _deferredRadiiRequest = null;
            ReconfigureRadii(deferred.NearRadius, deferred.FarRadius);
        }
    }

    private sealed class RadiiReconfiguration
    {
        public RadiiReconfiguration(
            int nearRadius,
            int farRadius,
            StreamingRegion region,
            List<RadiiMutation> mutations)
        {
            NearRadius = nearRadius;
            FarRadius = farRadius;
            Region = region;
            Mutations = mutations;
        }

        public int NearRadius { get; }
        public int FarRadius { get; }
        public StreamingRegion Region { get; }
        public List<RadiiMutation> Mutations { get; }
        public bool GenerationAdvanced { get; set; }
        public bool PendingLoadsCleared { get; set; }
    }

    private sealed class RadiiMutation
    {
        public RadiiMutation(Action apply) => Apply = apply;

        public Action Apply { get; }
        public bool Completed { get; set; }
    }

    public void InitializeKnownLoginCenter(int cx, int cy, bool isSealedDungeon)
    {
        if (isSealedDungeon)
            PreCollapseToDungeon(cx, cy);
    }

    /// <summary>
    /// Pins streaming to a sealed dungeon before the first normal Tick.
    /// </summary>
    public void PreCollapseToDungeon(int cx, int cy)
    {
        uint centerId = StreamingRegion.EncodeLandblockId(cx, cy);
        if (_collapsed && _collapsedCenter == centerId) return;
        EnterDungeonCollapse(cx, cy, centerId);
    }

    /// <summary>
    /// Outdoor / building-interior streaming — the original two-tier model.
    /// </summary>
    private void NormalTick(int observerCx, int observerCy)
    {
        if (_region is null)
        {
            _region = new StreamingRegion(observerCx, observerCy, NearRadius, FarRadius);
            var bootstrap = _region.ComputeFirstTickDiff();
            EnqueueLoadsByRevealPriority(
                bootstrap.ToLoadNear,
                LandblockStreamJobKind.LoadNear);
            foreach (var id in bootstrap.ToLoadFar)  EnqueueLoad(id, LandblockStreamJobKind.LoadFar);
            _region.MarkResidentFromBootstrap();
        }
        else if (_region.CenterX != observerCx || _region.CenterY != observerCy)
        {
            var diff = _region.RecenterTo(observerCx, observerCy);
            EnqueueLoadsByRevealPriority(
                diff.ToPromote,
                LandblockStreamJobKind.PromoteToNear);
            EnqueueLoadsByRevealPriority(
                diff.ToLoadNear,
                LandblockStreamJobKind.LoadNear);
            foreach (var id in diff.ToLoadFar)  EnqueueLoad(id, LandblockStreamJobKind.LoadFar);
            foreach (var id in diff.ToDemote)   DemoteLandblock(id);
            foreach (var id in diff.ToUnload)   EnqueueUnload(id);
        }
    }

    private void EnqueueLoadsByRevealPriority(
        IReadOnlyList<uint> landblockIds,
        LandblockStreamJobKind kind,
        bool skipLoaded = false)
    {
        for (int destinationPass = 1; destinationPass >= 0; destinationPass--)
        {
            bool requireDestination = destinationPass != 0;
            for (int i = 0; i < landblockIds.Count; i++)
            {
                uint id = landblockIds[i];
                if ((!skipLoaded || !_state.IsLoaded(id))
                    && IsDestinationWork(id) == requireDestination)
                {
                    EnqueueLoad(id, kind);
                }
            }
        }
    }

    private void EnterDungeonCollapse(int cx, int cy, uint centerId)
    {
        bool logTransition = !_collapsed || _collapsedCenter != centerId;
        if (logTransition)
            Console.WriteLine($"streaming: dungeon collapse -> 0x{centerId:X8}");
        if (!AdvanceGeneration())
            return;
        _collapsed = true;
        _collapsedCenter = centerId;
        _clearPendingLoads?.Invoke();
        _completionQueue.Clear();
        _region = null;

        foreach (var id in _state.LoadedLandblockIds)
            if (id != centerId) EnqueueUnload(id);

        _region = new StreamingRegion(cx, cy, 0, 0);
        _region.MarkResidentFromBootstrap();

        if (!_state.IsLoaded(centerId))
            EnqueueLoad(centerId, LandblockStreamJobKind.LoadNear);
        else if (!_state.IsNearTier(centerId))
            EnqueueLoad(centerId, LandblockStreamJobKind.PromoteToNear);
    }

    private void SweepCollapsed()
    {
        foreach (var id in _state.LoadedLandblockIds)
            if (id != _collapsedCenter) EnqueueUnload(id);
    }

    private static int ChebyshevLandblocks(uint a, uint b)
    {
        int ax = (int)((a >> 24) & 0xFFu), ay = (int)((a >> 16) & 0xFFu);
        int bx = (int)((b >> 24) & 0xFFu), by = (int)((b >> 16) & 0xFFu);
        return Math.Max(Math.Abs(ax - bx), Math.Abs(ay - by));
    }

    private bool IsDestinationWork(uint id)
        => _destinationReservation is { } reservation
           && ChebyshevLandblocks(id, reservation.LandblockId)
                <= reservation.Radius;

    private void ExitDungeonExpand(int observerCx, int observerCy)
    {
        Console.WriteLine(
            $"streaming: dungeon EXIT-expand -> ({observerCx},{observerCy}) " +
            $"(was collapsed on 0x{_collapsedCenter:X8})");
        if (!AdvanceGeneration())
            return;
        _collapsed = false;
        var rebuilt = new StreamingRegion(observerCx, observerCy, NearRadius, FarRadius);

        foreach (var id in _state.LoadedLandblockIds)
            if (!rebuilt.Resident.Contains(id)) EnqueueUnload(id);

        var boot = rebuilt.ComputeFirstTickDiff();
        EnqueueLoadsByRevealPriority(
            boot.ToLoadNear,
            LandblockStreamJobKind.LoadNear,
            skipLoaded: true);
        foreach (var id in boot.ToLoadFar)
            if (!_state.IsLoaded(id)) EnqueueLoad(id, LandblockStreamJobKind.LoadFar);
        rebuilt.MarkResidentFromBootstrap();
        _region = rebuilt;
    }

    public void ForceReloadWindow()
    {
        BeginFullWindowRetirement();
    }

    internal void BeginOriginRecenter()
    {
        _originRecenterRetirement ??= new OriginRecenterRetirement();
    }

    internal bool IsOriginRecenterRetirementComplete()
    {
        if (_originRecenterRetirement is null)
            throw new InvalidOperationException(
                "No streaming-origin recenter transaction is pending.");

        return _originRecenterRetirement.PreparationCommitted;
    }

    internal bool TryCommitOriginRecenter(
        int destinationX,
        int destinationY,
        bool isSealedDungeon)
    {
        if (_advancingOriginRecenter)
            return false;

        _advancingOriginRecenter = true;
        try
        {
            return TryCommitOriginRecenterCore(
                destinationX,
                destinationY,
                isSealedDungeon);
        }
        finally
        {
            _advancingOriginRecenter = false;
        }
    }

    private bool TryCommitOriginRecenterCore(
        int destinationX,
        int destinationY,
        bool isSealedDungeon)
    {
        OriginRecenterRetirement transaction = _originRecenterRetirement
            ?? throw new InvalidOperationException(
                "No streaming-origin recenter transaction is pending.");
        if (!transaction.PreparationCommitted)
            throw new InvalidOperationException(
                "Streaming-origin retirement preparation has not completed.");
        var destination = (destinationX, destinationY, isSealedDungeon);
        if (transaction.Destination is { } retained && retained != destination)
        {
            throw new InvalidOperationException(
                "A recenter transaction cannot commit two different destinations.");
        }
        transaction.Destination ??= destination;

        if (!transaction.DestinationConfigured)
        {
            _collapsed = isSealedDungeon;
            _collapsedCenter = isSealedDungeon
                ? StreamingRegion.EncodeLandblockId(destinationX, destinationY)
                : 0u;
            if (isSealedDungeon)
            {
                _region = new StreamingRegion(
                    destinationX,
                    destinationY,
                    nearRadius: 0,
                    farRadius: 0);
                _region.MarkResidentFromBootstrap();
            }
            else
            {
                _region = null;
            }
            transaction.DestinationConfigured = true;
        }

        if (isSealedDungeon && !transaction.DestinationLoadEnqueued)
        {
            try
            {
                EnqueueLoad(
                    StreamingRegion.EncodeLandblockId(destinationX, destinationY),
                    LandblockStreamJobKind.LoadNear);
                transaction.DestinationLoadEnqueued = true;
            }
            catch (StreamingMutationException error) when (error.MutationCommitted)
            {
                transaction.DestinationLoadEnqueued = true;
                Console.WriteLine(
                    $"streaming: committed dungeon recenter enqueue reported failure: {error}");
                return false;
            }
            catch (Exception error)
            {
                Console.WriteLine(
                    $"streaming: dungeon recenter enqueue will resume: {error}");
                return false;
            }
        }

        _originRecenterRetirement = null;
        return true;
    }

    /// <summary>
    /// Releases a fully retired origin transaction at a session boundary
    /// without bootstrapping a destination from the ending session.
    /// </summary>
    internal bool TryCancelOriginRecenter()
    {
        if (_originRecenterRetirement is not { PreparationCommitted: true })
            return false;

        _collapsed = false;
        _collapsedCenter = 0u;
        _region = null;
        _originRecenterRetirement = null;
        return true;
    }

    private bool TryAdvanceOriginRecenterPreparation(StreamingWorkMeter meter)
    {
        ArgumentNullException.ThrowIfNull(meter);
        OriginRecenterRetirement transaction = _originRecenterRetirement
            ?? throw new InvalidOperationException(
                "No streaming-origin recenter transaction is pending.");
        if (transaction.PreparationCommitted)
            return true;
        if (_advancingOriginRecenter)
            return false;

        _advancingOriginRecenter = true;
        try
        {
            if (!transaction.RadiiConverged)
            {
                if (_pendingRadiiReconfiguration is not null
                    && !TryRunStreamingWork(
                        meter,
                        new StreamingWorkCost(
                            EntityOperations: Math.Max(
                                1,
                                _pendingRadiiReconfiguration.Mutations.Count)),
                        "recenter-radii-convergence",
                        () =>
                        {
                            AdvanceRadiiReconfiguration();
                            return _pendingRadiiReconfiguration is null;
                        }))
                {
                    return false;
                }
                if (_pendingRadiiReconfiguration is not null)
                    return false;
                transaction.RadiiConverged = true;
            }
            if (!transaction.GenerationAdvanced)
            {
                if (!AdvanceGeneration())
                    return false;
                transaction.GenerationAdvanced = true;
            }
            if (!transaction.PendingLoadsCleared)
            {
                bool ClearPendingLoads()
                {
                    try
                    {
                        _clearPendingLoads?.Invoke();
                        transaction.PendingLoadsCleared = true;
                        return true;
                    }
                    catch (StreamingMutationException error) when (error.MutationCommitted)
                    {
                        transaction.PendingLoadsCleared = true;
                        Console.WriteLine(
                            $"streaming: committed pending-load clear reported failure: {error}");
                        return false;
                    }
                }

                if (!TryRunStreamingWork(
                        meter,
                        new StreamingWorkCost(EntityOperations: 1),
                        "recenter-clear-worker-inbox",
                        ClearPendingLoads))
                    return false;
            }
            if (!transaction.CompletionQueueCleared)
            {
                while (_completionQueue.Count != 0)
                {
                    StreamingWorkAdmission admission = meter.TryReserve(
                        new StreamingWorkCost(EntityOperations: 1),
                        "recenter-release-completion");
                    if (admission == StreamingWorkAdmission.Yielded)
                        return false;

                    if (!_completionQueue.TryRemoveOne())
                    {
                        meter.Fail();
                        throw new InvalidOperationException(
                            "Completion queue count changed during recenter release.");
                    }
                    meter.Complete();
                }
                transaction.CompletionQueueCleared = true;
            }
            if (!transaction.RegionCleared)
            {
                if (!transaction.PendingPublicationsCleared)
                {
                    bool cancelled = false;
                    if (!TryRunStreamingWork(
                            meter,
                            new StreamingWorkCost(EntityOperations: 1),
                            "recenter-cancel-publications",
                            () =>
                            {
                                cancelled =
                                    _presentation.CancelPendingPublications();
                                return true;
                            }))
                    {
                        return false;
                    }
                    transaction.PendingPublicationsCleared = cancelled;
                    if (!cancelled)
                        return false;
                }
                if (!TryRunStreamingWork(
                        meter,
                        new StreamingWorkCost(EntityOperations: 1),
                        "recenter-clear-region",
                        () =>
                        {
                            _collapsed = false;
                            _region = null;
                            return true;
                        }))
                {
                    return false;
                }
                transaction.RegionCleared = true;
            }
            if (!transaction.SpatialGenerationDetached)
            {
                StreamingWorkAdmission admission = meter.TryReserve(
                    new StreamingWorkCost(
                        EntityOperations:
                            _state.OriginRecenterSpatialOperationCount),
                    "recenter-detach-spatial-generation",
                    ensureProgress: true);
                if (admission == StreamingWorkAdmission.Yielded)
                    return false;

                try
                {
                    GpuWorldRecenterRetirement detached =
                        _presentation.DetachAllForOriginRecenter();
                    transaction.SpatialGenerationDetached = true;
                    FullWindowRetirementCount++;
                    LastFullWindowRetirementLandblockCount =
                        detached.Landblocks.Count;
                    meter.Complete();
                    if (detached.ObserverFailure is not null)
                    {
                        Console.WriteLine(
                            "streaming: committed origin-recenter spatial " +
                            $"generation reported failure: {detached.ObserverFailure}");
                        return false;
                    }
                }
                catch
                {
                    meter.Fail();
                    throw;
                }
            }

            transaction.PreparationCommitted = true;
            return true;
        }
        catch (StreamingMutationException error) when (error.MutationCommitted)
        {
            throw;
        }
        catch (Exception error)
        {
            Console.WriteLine(
                $"streaming: origin-recenter preparation will resume: {error}");
            return false;
        }
        finally
        {
            _advancingOriginRecenter = false;
        }
    }

    private void BeginFullWindowRetirement()
    {
        _fullWindowRetirement ??= new FullWindowRetirement();
    }

    private bool TryAdvanceFullWindowRetirement(StreamingWorkMeter meter)
    {
        FullWindowRetirement transaction = _fullWindowRetirement
            ?? throw new InvalidOperationException(
                "No full-window retirement transaction is pending.");
        if (transaction.PreparationCommitted)
            return true;

        try
        {
            if (!transaction.GenerationAdvanced)
            {
                if (!AdvanceGeneration())
                {
                    return false;
                }
                transaction.GenerationAdvanced = true;
            }

            if (!transaction.PendingLoadsCleared)
            {
                bool ClearPendingLoads()
                {
                    try
                    {
                        _clearPendingLoads?.Invoke();
                        transaction.PendingLoadsCleared = true;
                        return true;
                    }
                    catch (StreamingMutationException error) when (error.MutationCommitted)
                    {
                        transaction.PendingLoadsCleared = true;
                        Console.WriteLine(
                            $"streaming: committed reload pending-load clear reported failure: {error}");
                        return false;
                    }
                }

                if (!TryRunStreamingWork(
                        meter,
                        new StreamingWorkCost(EntityOperations: 1),
                        "reload-clear-worker-inbox",
                        ClearPendingLoads))
                {
                    return false;
                }
            }

            if (!transaction.CompletionQueueCleared)
            {
                while (_completionQueue.Count != 0)
                {
                    StreamingWorkAdmission admission = meter.TryReserve(
                        new StreamingWorkCost(EntityOperations: 1),
                        "reload-release-completion");
                    if (admission == StreamingWorkAdmission.Yielded)
                        return false;

                    if (!_completionQueue.TryRemoveOne())
                    {
                        meter.Fail();
                        throw new InvalidOperationException(
                            "Completion queue count changed during reload release.");
                    }
                    meter.Complete();
                }
                transaction.CompletionQueueCleared = true;
            }

            if (!transaction.RegionCleared)
            {
                if (!transaction.PendingPublicationsCleared)
                {
                    bool cancelled = false;
                    if (!TryRunStreamingWork(
                            meter,
                            new StreamingWorkCost(EntityOperations: 1),
                            "reload-cancel-publications",
                            () =>
                            {
                                cancelled =
                                    _presentation.CancelPendingPublications();
                                return true;
                            }))
                    {
                        return false;
                    }
                    transaction.PendingPublicationsCleared = cancelled;
                    if (!cancelled)
                        return false;
                }
                if (!TryRunStreamingWork(
                        meter,
                        new StreamingWorkCost(EntityOperations: 1),
                        "reload-clear-region",
                        () =>
                        {
                            _collapsed = false;
                            _region = null;
                            return true;
                        }))
                {
                    return false;
                }
                transaction.RegionCleared = true;
            }

            if (transaction.ResidentIds is null)
            {
                transaction.ResidentIds = [];
                transaction.ResidentEnumerator =
                    _state.LoadedLandblockIds.GetEnumerator();
            }
            while (transaction.ResidentEnumerator is { } residentEnumerator)
            {
                StreamingWorkAdmission admission = meter.TryReserve(
                    new StreamingWorkCost(EntityOperations: 1),
                    "reload-capture-resident-id");
                if (admission == StreamingWorkAdmission.Yielded)
                    return false;

                bool moved;
                try
                {
                    moved = residentEnumerator.MoveNext();
                    if (moved)
                        transaction.ResidentIds.Add(residentEnumerator.Current);
                    else
                    {
                        residentEnumerator.Dispose();
                        transaction.ResidentEnumerator = null;
                        FullWindowRetirementCount++;
                        LastFullWindowRetirementLandblockCount =
                            transaction.ResidentIds.Count;
                    }
                    meter.Complete();
                }
                catch
                {
                    meter.Fail();
                    throw;
                }
            }

            List<uint> residentIds = transaction.ResidentIds
                ?? throw new InvalidOperationException(
                    "Full-window resident capture did not commit.");
            while (transaction.RetirementCursor < residentIds.Count)
            {
                uint id = residentIds[transaction.RetirementCursor];
                int entityCount = _state.TryGetLandblock(
                        id,
                        out LoadedLandblock? loaded)
                    ? loaded!.Entities.Count
                    : 0;
                StreamingWorkAdmission admission = meter.TryReserve(
                    new StreamingWorkCost(
                        EntityOperations: Math.Max(1, entityCount)),
                    $"reload-detach-0x{id:X8}");
                if (admission == StreamingWorkAdmission.Yielded)
                    return false;

                try
                {
                    _presentation.EnqueueFullRetirement(id);
                    transaction.RetirementCursor++;
                    meter.Complete();
                }
                catch (Exception error)
                {
                    if (!_state.IsLoaded(id))
                        transaction.RetirementCursor++;
                    meter.Fail();
                    Console.WriteLine(
                        $"streaming: full-window retirement for 0x{id:X8} " +
                        $"will resume: {error}");
                    return false;
                }
            }

            transaction.PreparationCommitted = true;
            return true;
        }
        catch (Exception error)
        {
            Console.WriteLine(
                $"streaming: full-window preparation will resume: {error}");
            return false;
        }
    }

    private void DrainAndApply(bool preferDestination = false)
    {
        StreamingWorkMeter meter = _activeWorkMeter
            ?? throw new InvalidOperationException(
                "Completion scheduling requires an active frame meter.");

        AdmitCompletions(meter);
        bool destinationQueued =
            preferDestination
            && _completionQueue.HasPriority(
                StreamingCompletionPriority.Destination);
        bool executed = false;
        while (_completionQueue.TryPeekNext(
            _isPublicationBlockedByRetirement,
            out StreamingQueuedCompletion? completion,
            destinationQueued
                ? StreamingCompletionPriority.Unload
                : StreamingCompletionPriority.Far))
        {
            StreamingQueuedCompletion work = completion
                ?? throw new InvalidOperationException(
                    "The completion queue returned a null head.");
            try
            {
                using StreamingWorkMeter.LaneScope lane = meter.EnterLane(
                    work.Priority == StreamingCompletionPriority.Destination
                        && work.RevealGeneration == ActiveRevealGeneration
                            ? StreamingWorkLane.Destination
                            : StreamingWorkLane.NonDestination);
                LandblockPublicationAdvance advance = ApplyResult(
                    work,
                    meter,
                    ensureProgress: !executed);
                executed |= advance.Progressed;
                if (!advance.Completed)
                    break;
                _completionQueue.RemoveHead(work);
            }
            catch
            {
                if (!_presentation.HasPendingPublication(work.Result))
                    _completionQueue.RemoveHead(work);
                throw;
            }
        }
    }

    private static bool TryRunStreamingWork(
        StreamingWorkMeter meter,
        StreamingWorkCost cost,
        string stage,
        Func<bool> operation)
    {
        StreamingWorkAdmission admission = meter.TryReserve(cost, stage);
        if (admission == StreamingWorkAdmission.Yielded)
            return false;

        try
        {
            bool completed = operation();
            if (completed)
                meter.Complete();
            else
                meter.Fail();
            return completed;
        }
        catch
        {
            meter.Fail();
            throw;
        }
    }

    private void AdmitCompletions(StreamingWorkMeter meter)
    {
        while (_completionSource.TryPeek(out LandblockStreamResult? peeked))
        {
            LandblockStreamResult result = peeked
                ?? throw new InvalidOperationException(
                    "The completion source returned a null peek.");
            bool stale = IsStaleGeneration(result)
                && !_presentation.HasPendingPublication(result);
            StreamingCompletionPriority priority = stale
                ? StreamingCompletionPriority.Control
                : ClassifyCompletion(result);
            LandblockStreamCostEstimate estimate =
                LandblockStreamResultCost.Estimate(result);
            StreamingWorkCost admissionCost = stale
                ? default
                : new StreamingWorkCost(
                    CompletionAdmissions:
                        estimate.Work.CompletionAdmissions,
                    AdoptedCpuBytes:
                        estimate.Work.AdoptedCpuBytes);
            using StreamingWorkMeter.LaneScope lane = meter.EnterLane(
                priority == StreamingCompletionPriority.Destination
                    ? StreamingWorkLane.Destination
                    : StreamingWorkLane.NonDestination);
            StreamingWorkAdmission admission = meter.TryReserve(
                admissionCost,
                "completion-admission");
            if (admission == StreamingWorkAdmission.Yielded)
                return;

            try
            {
                if (!_completionSource.TryRead(
                        out LandblockStreamResult? consumed)
                    || !ReferenceEquals(result, consumed))
                {
                    throw new InvalidOperationException(
                        "The single-consumer completion source changed between peek and read.");
                }

                if (!stale)
                {
                    _completionQueue.Enqueue(new StreamingQueuedCompletion(
                        result,
                        estimate,
                        priority,
                        priority == StreamingCompletionPriority.Destination
                            ? ActiveRevealGeneration
                            : 0L,
                        result.Generation,
                        checked(_nextCompletionSequence++),
                        Stopwatch.GetTimestamp()));
                }
                meter.Complete();
            }
            catch
            {
                meter.Fail();
                throw;
            }
        }
    }

    private StreamingCompletionPriority ClassifyCompletion(
        LandblockStreamResult result)
    {
        if (result is LandblockStreamResult.Loaded
                or LandblockStreamResult.Promoted
            && IsDestinationWork(result.LandblockId))
        {
            return StreamingCompletionPriority.Destination;
        }

        return result switch
        {
            LandblockStreamResult.Failed
                or LandblockStreamResult.WorkerCrashed =>
                StreamingCompletionPriority.Control,
            LandblockStreamResult.Unloaded =>
                StreamingCompletionPriority.Unload,
            LandblockStreamResult.Promoted
                or LandblockStreamResult.Loaded
                {
                    Tier: LandblockStreamTier.Near,
                } =>
                StreamingCompletionPriority.Near,
            _ => StreamingCompletionPriority.Far,
        };
    }

    private static LandblockPublicationAdvance RunSimpleResultOperation(
        StreamingWorkMeter meter,
        string stage,
        bool ensureProgress,
        Action operation)
    {
        StreamingWorkAdmission admission = meter.TryReserve(
            default,
            stage,
            ensureProgress);
        if (admission == StreamingWorkAdmission.Yielded)
            return new LandblockPublicationAdvance(false, false);

        try
        {
            operation();
            meter.Complete();
            return new LandblockPublicationAdvance(true, true);
        }
        catch
        {
            meter.Fail();
            throw;
        }
    }

    private LandblockPublicationAdvance ApplyResult(
        StreamingQueuedCompletion work,
        StreamingWorkMeter meter,
        bool ensureProgress)
    {
        LandblockStreamResult result = work.Result;
        if (_presentation.HasPendingPublication(result))
        {
            LandblockPublicationAdvance resumed =
                _presentation.ResumePublication(
                    result,
                    meter,
                    ensureProgress);
            if (resumed.Completed)
                ReconcileCompletedPendingPublication(result.LandblockId);
            return resumed;
        }

        if (IsStaleGeneration(result))
        {
            return RunSimpleResultOperation(
                meter,
                "execute-stale-generation",
                ensureProgress,
                static () => { });
        }

        if (result is LandblockStreamResult.Unloaded
            && _region?.TryGetDesiredTier(result.LandblockId, out _) == true)
        {
            return RunSimpleResultOperation(
                meter,
                "execute-reowned-unload",
                ensureProgress,
                static () => { });
        }

        if (result is LandblockStreamResult.Loaded
                or LandblockStreamResult.Promoted)
        {
            if (_region is null
                || !_region.TryGetDesiredTier(result.LandblockId, out var desiredTier))
            {
                return RunSimpleResultOperation(
                    meter,
                    "execute-undesired-load",
                    ensureProgress,
                    static () => { });
            }

            bool isNearCompletion = result is LandblockStreamResult.Promoted
                or LandblockStreamResult.Loaded { Tier: LandblockStreamTier.Near };
            if (isNearCompletion
                && _state.IsNearTier(result.LandblockId)
                && !_presentation.HasPendingPublication(result))
            {
                return RunSimpleResultOperation(
                    meter,
                    "execute-duplicate-near",
                    ensureProgress,
                    static () => { });
            }
            if (desiredTier == LandblockStreamTier.Far && isNearCompletion)
            {
                if (!_state.IsLoaded(result.LandblockId))
                {
                    switch (result)
                    {
                        case LandblockStreamResult.Loaded loaded:
                            return _presentation.PublishAsFar(
                                loaded,
                                loaded.Build,
                                loaded.MeshData,
                                meter,
                                ensureProgress);
                        case LandblockStreamResult.Promoted promoted:
                            return _presentation.PublishAsFar(
                                promoted,
                                promoted.Build,
                                promoted.MeshData,
                                meter,
                                ensureProgress);
                    }
                }
                return RunSimpleResultOperation(
                    meter,
                    "execute-already-far",
                    ensureProgress,
                    static () => { });
            }
        }

        switch (result)
        {
            case LandblockStreamResult.Loaded loaded:
                if (loaded.Tier == LandblockStreamTier.Far
                    && _state.IsNearTier(loaded.LandblockId))
                {
                    return RunSimpleResultOperation(
                        meter,
                        "execute-stale-far-tier",
                        ensureProgress,
                        static () => { });
                }
                return _presentation.PublishLoaded(
                    loaded,
                    work.Estimate,
                    meter,
                    ensureProgress);
            case LandblockStreamResult.Promoted promoted:
                return _presentation.PublishPromoted(
                    promoted,
                    mergeIntoExistingLandblock:
                        _state.IsLoaded(promoted.LandblockId),
                    work.Estimate,
                    meter,
                    ensureProgress);
            case LandblockStreamResult.Unloaded unloaded:
                return RunSimpleResultOperation(
                    meter,
                    "execute-unload",
                    ensureProgress,
                    () => _presentation.EnqueueFullRetirement(
                        unloaded.LandblockId));
            case LandblockStreamResult.Failed failed:
                return RunSimpleResultOperation(
                    meter,
                    "execute-load-failure",
                    ensureProgress,
                    () => Console.WriteLine(
                        $"streaming: load failed for 0x{failed.LandblockId:X8}: {failed.Error}"));
            case LandblockStreamResult.WorkerCrashed crashed:
                return RunSimpleResultOperation(
                    meter,
                    "execute-worker-crash",
                    ensureProgress,
                    () => Console.WriteLine(
                        $"streaming: worker CRASHED: {crashed.Error}"));
            default:
                throw new InvalidOperationException(
                    $"Unsupported streaming result {result.GetType().Name}.");
        }
    }

    private void ReconcileCompletedPendingPublication(uint landblockId)
    {
        if (_region is null
            || !_region.TryGetDesiredTier(landblockId, out LandblockStreamTier desiredTier))
        {
            if (_state.IsLoaded(landblockId))
                _presentation.EnqueueFullRetirement(landblockId);
            return;
        }

        if (desiredTier == LandblockStreamTier.Far
            && _state.IsNearTier(landblockId))
        {
            _presentation.EnqueueNearLayerRetirement(landblockId);
        }
    }

    private bool IsStaleGeneration(LandblockStreamResult result) =>
        result is not LandblockStreamResult.WorkerCrashed
        && result.Generation != _generation;

    private bool IsPublicationBlockedByRetirement(LandblockStreamResult result) =>
        result is LandblockStreamResult.Loaded or LandblockStreamResult.Promoted
        && _presentation.IsRetirementPending(result.LandblockId);

    private static uint ResultLandblockId(LandblockStreamResult result) => result.LandblockId;
}
