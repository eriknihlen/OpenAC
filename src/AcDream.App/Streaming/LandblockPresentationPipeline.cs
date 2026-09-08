using AcDream.App.Rendering.Wb;
using AcDream.App.Rendering.Scene;
using AcDream.Core.Terrain;
using AcDream.Core.World;

namespace AcDream.App.Streaming;

public readonly record struct LandblockPresentationDiagnostics(
    LandblockRenderPublisherDiagnostics Render,
    LandblockPhysicsPublisherDiagnostics Physics,
    LandblockStaticPresentationDiagnostics Statics);

internal readonly record struct LandblockPublicationAdvance(
    bool Completed,
    bool Progressed);

public sealed class LandblockPresentationPipeline
{
    private enum PublicationKind : byte
    {
        Loaded,
        PromoteExisting,
        PromoteSelfContained,
        Far,
    }

    private sealed class PublicationTransaction
    {
        public required PublicationKind Kind;
        public required LandblockBuild Build;
        public required LandblockMeshData MeshData;
        public required uint LandblockId;
        public required LandblockStreamCostEstimate Cost;
        public LandblockStreamTier Tier;
        public bool PresentationCommitted;
        public LandblockRenderPublication? RenderPublication;
        public LandblockPhysicsPublication? PhysicsPublication;
        public LandblockStaticPresentationPublication? StaticPublication;
        public bool SpatialCommitted;
        public GpuLandblockSpatialPublication? SpatialPublication;
        public bool SpatialPresentationCommitted;
        public bool EnvCellReplayCommitted;
        public bool LiveRecoveryCommitted;

        public PublicationStageTimings? Timing;
    }

    private readonly Action<LandblockBuild, LandblockMeshData>?
        _publishBeforeSpatialCommit;
    private readonly LandblockRenderPublisher? _renderPublisher;

    internal LandblockRenderPublisher? RenderPublisher => _renderPublisher;
    private readonly LandblockPhysicsPublisher? _physicsPublisher;
    private readonly LandblockStaticPresentationPublisher? _staticPublisher;
    private readonly GpuWorldState _state;
    private readonly Action<uint>? _onLandblockLoaded;
    private readonly Action<EnvCellLandblockBuild>? _ensureEnvCellMeshes;
    private readonly IRenderStaticProjectionJournalSink? _staticProjectionSink;
    private readonly LandblockRetirementCoordinator _retirements;
    private readonly Dictionary<LandblockStreamResult, PublicationTransaction> _publications =
        new(ReferenceEqualityComparer.Instance);

    internal LandblockPresentationPipeline(
        Action<LandblockBuild, LandblockMeshData> publishBeforeSpatialCommit,
        GpuWorldState state,
        Action<uint>? onLandblockLoaded = null,
        Action<EnvCellLandblockBuild>? ensureEnvCellMeshes = null,
        LandblockRetirementCoordinator? retirementCoordinator = null,
        Action<uint>? removeTerrain = null,
        Action<uint>? demoteNearLayer = null,
        IRenderStaticProjectionJournalSink? staticProjectionSink = null)
    {
        ArgumentNullException.ThrowIfNull(publishBeforeSpatialCommit);
        ArgumentNullException.ThrowIfNull(state);

        _publishBeforeSpatialCommit = publishBeforeSpatialCommit;
        _state = state;
        _onLandblockLoaded = onLandblockLoaded;
        _ensureEnvCellMeshes = ensureEnvCellMeshes;
        _staticProjectionSink = staticProjectionSink;
        if (retirementCoordinator is not null
            && !retirementCoordinator.MatchesState(_state))
        {
            throw new ArgumentException(
                "The retirement coordinator must own the pipeline's world state.",
                nameof(retirementCoordinator));
        }
        _retirements = retirementCoordinator
            ?? LandblockRetirementCoordinator.CreateLegacy(
                state,
                removeTerrain,
                demoteNearLayer);
    }

    public LandblockPresentationPipeline(
        LandblockRenderPublisher renderPublisher,
        LandblockPhysicsPublisher physicsPublisher,
        LandblockStaticPresentationPublisher staticPublisher,
        GpuWorldState state,
        LandblockPresentationRetirementOwner retirementOwner,
        Action<uint>? onLandblockLoaded = null,
        Action<EnvCellLandblockBuild>? ensureEnvCellMeshes = null)
        : this(
            renderPublisher,
            physicsPublisher,
            staticPublisher,
            state,
            retirementOwner,
            onLandblockLoaded,
            ensureEnvCellMeshes,
            staticProjectionSink: null)
    {
    }

    internal LandblockPresentationPipeline(
        LandblockRenderPublisher renderPublisher,
        LandblockPhysicsPublisher physicsPublisher,
        LandblockStaticPresentationPublisher staticPublisher,
        GpuWorldState state,
        LandblockPresentationRetirementOwner retirementOwner,
        Action<uint>? onLandblockLoaded,
        Action<EnvCellLandblockBuild>? ensureEnvCellMeshes,
        IRenderStaticProjectionJournalSink? staticProjectionSink)
    {
        _renderPublisher = renderPublisher
            ?? throw new ArgumentNullException(nameof(renderPublisher));
        _physicsPublisher = physicsPublisher
            ?? throw new ArgumentNullException(nameof(physicsPublisher));
        _staticPublisher = staticPublisher
            ?? throw new ArgumentNullException(nameof(staticPublisher));
        _state = state ?? throw new ArgumentNullException(nameof(state));
        if (!_renderPublisher.MatchesState(_state))
        {
            throw new ArgumentException(
                "The render publisher must own the pipeline's world state.",
                nameof(state));
        }
        ArgumentNullException.ThrowIfNull(retirementOwner);
        if (!retirementOwner.Matches(
                _renderPublisher,
                _physicsPublisher,
                _staticPublisher))
        {
            throw new ArgumentException(
                "The retirement owner must reference the pipeline's concrete publishers.",
                nameof(retirementOwner));
        }
        _onLandblockLoaded = onLandblockLoaded;
        _ensureEnvCellMeshes = ensureEnvCellMeshes;
        _staticProjectionSink = staticProjectionSink;
        _retirements = LandblockRetirementCoordinator.CreateBudgeted(
            state,
            retirementOwner.AdvanceOne,
            retirementOwner.Advance,
            staticProjectionSink is null
                ? null
                : staticProjectionSink.Retire);
    }

    /// <summary>
    /// Building visibility registries published by the render owner. Legacy
    /// test pipelines have no render owner and therefore expose an empty view.
    /// </summary>
    public IReadOnlyCollection<BuildingRegistry> BuildingRegistries =>
        _renderPublisher?.BuildingRegistries ?? Array.Empty<BuildingRegistry>();

    public LandblockPresentationDiagnostics Diagnostics => new(
        _renderPublisher?.Diagnostics ?? default,
        _physicsPublisher?.Diagnostics ?? default,
        _staticPublisher?.Diagnostics ?? default);

    public int PendingRetirementCount => _retirements.PendingCount;
    internal bool UsesBudgetedRetirementSteps => _retirements.UsesBudgetedSteps;
    public int PendingPublicationCount => _publications.Count;

    internal bool MatchesState(GpuWorldState state) =>
        ReferenceEquals(_state, state);

    internal bool IsLandblockPresentationReady(uint landblockId)
    {
        uint canonical = (landblockId & 0xFFFF0000u) | 0xFFFFu;
        foreach (PublicationTransaction transaction in _publications.Values)
        {
            if ((transaction.LandblockId & 0xFFFF0000u) ==
                    (canonical & 0xFFFF0000u)
                && (!transaction.PresentationCommitted
                    || !transaction.SpatialPresentationCommitted))
            {
                return false;
            }
        }
        return true;
    }

    public bool IsRetirementPending(uint landblockId) =>
        _retirements.IsPending(landblockId);

    public bool HasPendingPublication(LandblockStreamResult result)
    {
        ArgumentNullException.ThrowIfNull(result);
        return _publications.ContainsKey(result);
    }

    public IReadOnlyList<LandblockStreamResult> GetPendingPublicationResults() =>
        _publications.Keys.ToArray();

    internal bool CancelPendingPublications()
    {
        if (_publications.Count == 0)
            return true;
        LandblockStreamResult[] pending = [.. _publications.Keys];
        bool completed = true;
        for (int index = 0; index < pending.Length; index++)
        {
            LandblockStreamResult result = pending[index];
            if (!_publications.TryGetValue(
                    result,
                    out PublicationTransaction? transaction))
            {
                continue;
            }
            bool cancelled = transaction.PhysicsPublication is not { } physics
                || physics.TryCancel();
            if (cancelled)
                _publications.Remove(result);
            else
                completed = false;
        }
        return completed && _publications.Count == 0;
    }

    public void ResumePublication(LandblockStreamResult result)
    {
        ArgumentNullException.ThrowIfNull(result);
        if (!_publications.TryGetValue(result, out PublicationTransaction? transaction))
            return;
        Advance(result, transaction, meter: null, ensureProgress: false);
    }

    internal LandblockPublicationAdvance ResumePublication(
        LandblockStreamResult result,
        StreamingWorkMeter meter,
        bool ensureProgress)
    {
        ArgumentNullException.ThrowIfNull(result);
        ArgumentNullException.ThrowIfNull(meter);
        if (!_publications.TryGetValue(result, out PublicationTransaction? transaction))
            return new LandblockPublicationAdvance(Completed: true, Progressed: false);
        return Advance(result, transaction, meter, ensureProgress);
    }

    public void AdvanceRetirements()
    {
        _retirements.Advance();
    }

    public void AdvanceRetirements(StreamingWorkMeter meter)
    {
        _retirements.Advance(meter);
    }

    internal void AdvancePriorityRetirement(
        uint landblockId,
        StreamingWorkMeter meter) =>
        _retirements.AdvancePriority(landblockId, meter);

    public void BeginFullRetirement(uint landblockId)
    {
        _retirements.BeginFull(landblockId);
        if (_retirements.UsesBudgetedSteps)
        {
            _retirements.Advance();
            if (_retirements.IsPending(landblockId)
                && (_physicsPublisher?.CanContinueMutationSynchronously()
                    ?? true))
                _retirements.Advance();
        }
    }

    public void BeginNearLayerRetirement(uint landblockId)
    {
        _retirements.BeginNearLayer(landblockId);
        if (_retirements.UsesBudgetedSteps)
        {
            _retirements.Advance();
            if (_retirements.IsPending(landblockId)
                && (_physicsPublisher?.CanContinueMutationSynchronously()
                    ?? true))
                _retirements.Advance();
        }
    }

    internal void EnqueueFullRetirement(uint landblockId) =>
        _retirements.BeginFull(landblockId);

    internal void EnqueueNearLayerRetirement(uint landblockId) =>
        _retirements.BeginNearLayer(landblockId);

    internal GpuWorldRecenterRetirement DetachAllForOriginRecenter()
    {
        GpuWorldRecenterRetirement detached =
            _state.DetachAllForOriginRecenter();
        Exception? adoptionFailure;
        try
        {
            adoptionFailure =
                _retirements.AdoptDetachedFull(detached.Landblocks);
        }
        catch (Exception error)
        {
            throw new StreamingMutationException(
                "Origin-recenter retirement receipt adoption failed after " +
                "the spatial generation detached.",
                mutationCommitted: true,
                error);
        }
        Exception? failure = (detached.ObserverFailure, adoptionFailure) switch
        {
            (null, null) => null,
            ({ } observer, null) => observer,
            (null, { } adoption) => adoption,
            ({ } observer, { } adoption) => new AggregateException(
                "Origin-recenter spatial observers and retirement adoption failed.",
                observer,
                adoption),
        };
        return detached with { ObserverFailure = failure };
    }

    public void PublishLoaded(LandblockStreamResult.Loaded loaded)
    {
        ArgumentNullException.ThrowIfNull(loaded);
        PublicationTransaction transaction = GetOrCreate(
            loaded,
            PublicationKind.Loaded,
            loaded.Build,
            loaded.MeshData,
            loaded.LandblockId,
            loaded.Tier,
            estimate: null);
        Advance(loaded, transaction, meter: null, ensureProgress: false);
    }

    internal LandblockPublicationAdvance PublishLoaded(
        LandblockStreamResult.Loaded loaded,
        LandblockStreamCostEstimate estimate,
        StreamingWorkMeter meter,
        bool ensureProgress)
    {
        ArgumentNullException.ThrowIfNull(loaded);
        ArgumentNullException.ThrowIfNull(meter);
        PublicationTransaction transaction = GetOrCreate(
            loaded,
            PublicationKind.Loaded,
            loaded.Build,
            loaded.MeshData,
            loaded.LandblockId,
            loaded.Tier,
            estimate);
        return Advance(loaded, transaction, meter, ensureProgress);
    }

    public void PublishPromoted(
        LandblockStreamResult.Promoted promoted,
        bool mergeIntoExistingLandblock)
    {
        ArgumentNullException.ThrowIfNull(promoted);
        PublicationKind kind = mergeIntoExistingLandblock
            ? PublicationKind.PromoteExisting
            : PublicationKind.PromoteSelfContained;
        PublicationTransaction transaction = GetOrCreate(
            promoted,
            kind,
            promoted.Build,
            promoted.MeshData,
            promoted.LandblockId,
            LandblockStreamTier.Near,
            estimate: null);
        Advance(promoted, transaction, meter: null, ensureProgress: false);
    }

    internal LandblockPublicationAdvance PublishPromoted(
        LandblockStreamResult.Promoted promoted,
        bool mergeIntoExistingLandblock,
        LandblockStreamCostEstimate estimate,
        StreamingWorkMeter meter,
        bool ensureProgress)
    {
        ArgumentNullException.ThrowIfNull(promoted);
        ArgumentNullException.ThrowIfNull(meter);
        PublicationKind kind = mergeIntoExistingLandblock
            ? PublicationKind.PromoteExisting
            : PublicationKind.PromoteSelfContained;
        PublicationTransaction transaction = GetOrCreate(
            promoted,
            kind,
            promoted.Build,
            promoted.MeshData,
            promoted.LandblockId,
            LandblockStreamTier.Near,
            estimate);
        return Advance(promoted, transaction, meter, ensureProgress);
    }

    public void PublishAsFar(
        LandblockStreamResult acceptedResult,
        LandblockBuild completedBuild,
        LandblockMeshData meshData)
    {
        ArgumentNullException.ThrowIfNull(acceptedResult);
        ArgumentNullException.ThrowIfNull(completedBuild);
        ArgumentNullException.ThrowIfNull(meshData);

        if (!_publications.TryGetValue(acceptedResult, out PublicationTransaction? transaction))
        {
            LoadedLandblock completed = completedBuild.Landblock;
            var farLandblock = new LoadedLandblock(
                completed.LandblockId,
                completed.Heightmap,
                Array.Empty<WorldEntity>(),
                PhysicsDatBundle.Empty);
            LandblockBuild farBuild = new(
                farLandblock,
                Origin: completedBuild.Origin,
                TerrainBounds: completedBuild.TerrainBounds);
            transaction = new PublicationTransaction
            {
                Kind = PublicationKind.Far,
                Build = farBuild,
                MeshData = meshData,
                LandblockId = farLandblock.LandblockId,
                Cost = LandblockStreamResultCost.Estimate(
                    farBuild,
                    meshData),
                Tier = LandblockStreamTier.Far,
                Timing = PublicationTimingProbe.CreateTimings(),
            };
            _publications.Add(acceptedResult, transaction);
        }
        else if (transaction.Kind != PublicationKind.Far)
        {
            throw new InvalidOperationException(
                $"Landblock publication 0x{transaction.LandblockId:X8} changed kind while pending.");
        }

        Advance(
            acceptedResult,
            transaction,
            meter: null,
            ensureProgress: false);
    }

    internal LandblockPublicationAdvance PublishAsFar(
        LandblockStreamResult acceptedResult,
        LandblockBuild completedBuild,
        LandblockMeshData meshData,
        StreamingWorkMeter meter,
        bool ensureProgress)
    {
        ArgumentNullException.ThrowIfNull(acceptedResult);
        ArgumentNullException.ThrowIfNull(completedBuild);
        ArgumentNullException.ThrowIfNull(meshData);
        ArgumentNullException.ThrowIfNull(meter);

        if (!_publications.TryGetValue(
                acceptedResult,
                out PublicationTransaction? transaction))
        {
            LoadedLandblock completed = completedBuild.Landblock;
            var farLandblock = new LoadedLandblock(
                completed.LandblockId,
                completed.Heightmap,
                Array.Empty<WorldEntity>(),
                PhysicsDatBundle.Empty);
            LandblockBuild farBuild = new(
                farLandblock,
                Origin: completedBuild.Origin,
                TerrainBounds: completedBuild.TerrainBounds);
            transaction = new PublicationTransaction
            {
                Kind = PublicationKind.Far,
                Build = farBuild,
                MeshData = meshData,
                LandblockId = farLandblock.LandblockId,
                Cost = LandblockStreamResultCost.Estimate(farBuild, meshData),
                Tier = LandblockStreamTier.Far,
                Timing = PublicationTimingProbe.CreateTimings(),
            };
            _publications.Add(acceptedResult, transaction);
        }
        else if (transaction.Kind != PublicationKind.Far)
        {
            throw new InvalidOperationException(
                $"Landblock publication 0x{transaction.LandblockId:X8} changed kind while pending.");
        }

        return Advance(acceptedResult, transaction, meter, ensureProgress);
    }

    private PublicationTransaction GetOrCreate(
        LandblockStreamResult result,
        PublicationKind kind,
        LandblockBuild build,
        LandblockMeshData meshData,
        uint landblockId,
        LandblockStreamTier tier,
        LandblockStreamCostEstimate? estimate)
    {
        if (_publications.TryGetValue(result, out PublicationTransaction? existing))
        {
            if (existing.Kind != kind
                && !(existing.Kind is PublicationKind.PromoteExisting
                    or PublicationKind.PromoteSelfContained
                    && kind is PublicationKind.PromoteExisting
                        or PublicationKind.PromoteSelfContained))
            {
                throw new InvalidOperationException(
                    $"Landblock publication 0x{existing.LandblockId:X8} changed kind while pending.");
            }
            return existing;
        }

        var created = new PublicationTransaction
        {
            Kind = kind,
            Build = build,
            MeshData = meshData,
            LandblockId = landblockId,
            Cost = estimate
                ?? LandblockStreamResultCost.Estimate(build, meshData),
            Tier = tier,
            Timing = PublicationTimingProbe.CreateTimings(),
        };
        _publications.Add(result, created);
        return created;
    }

    private LandblockPublicationAdvance Advance(
        LandblockStreamResult result,
        PublicationTransaction transaction,
        StreamingWorkMeter? meter,
        bool ensureProgress)
    {
        bool progressed = false;
        void RunTimed(string stage, Action operation)
        {
            if (transaction.Timing is not { } timing)
            {
                operation();
                return;
            }

            long start = System.Diagnostics.Stopwatch.GetTimestamp();
            try
            {
                operation();
            }
            finally
            {
                timing.Add(
                    stage,
                    System.Diagnostics.Stopwatch.GetTimestamp() - start);
            }
        }
        bool TryRun(
            StreamingWorkCost cost,
            string stage,
            Action operation)
        {
            if (meter is null)
            {
                RunTimed(stage, operation);
                progressed = true;
                return true;
            }

            StreamingWorkAdmission admission = meter.TryReserve(
                cost,
                stage,
                ensureProgress && !progressed);
            if (admission == StreamingWorkAdmission.Yielded)
                return false;
            try
            {
                RunTimed(stage, operation);
                meter.Complete();
                progressed = true;
                return true;
            }
            catch
            {
                meter.Fail();
                throw;
            }
        }

        if (!transaction.PresentationCommitted)
        {
            if (_renderPublisher is not null
                && _physicsPublisher is not null
                && _staticPublisher is not null)
            {
                while (transaction.RenderPublication is null)
                {
                    if (!TryRun(
                            new StreamingWorkCost(EntityOperations: 1),
                            "publication-prepare-render",
                            () => transaction.RenderPublication =
                                _renderPublisher.PreparePublication(
                                    transaction.Build,
                                    transaction.MeshData)))
                    {
                        return new LandblockPublicationAdvance(false, progressed);
                    }
                }
                while (transaction.PhysicsPublication is null)
                {
                    if (!TryRun(
                            default,
                            "publication-prepare-physics",
                            () => transaction.PhysicsPublication =
                                _physicsPublisher.CreatePublication(
                                    transaction.RenderPublication)))
                    {
                        return new LandblockPublicationAdvance(false, progressed);
                    }
                }
                while (!transaction.PhysicsPublication.PreparationCommitted)
                {
                    int entityOperations =
                        transaction.PhysicsPublication.PreparationCursor
                        < transaction.Build.Landblock.Entities.Count
                            ? 1
                            : 0;
                    if (!TryRun(
                            new StreamingWorkCost(
                                EntityOperations: entityOperations),
                            "publication-index-physics",
                            () => _physicsPublisher.AdvancePreparationOne(
                                transaction.PhysicsPublication)))
                    {
                        return new LandblockPublicationAdvance(false, progressed);
                    }
                }
                while (transaction.StaticPublication is null)
                {
                    if (!TryRun(
                            default,
                            "publication-prepare-statics",
                            () => transaction.StaticPublication =
                                _staticPublisher.CreatePublication(
                                    transaction.PhysicsPublication)))
                    {
                        return new LandblockPublicationAdvance(false, progressed);
                    }
                }
                while (!transaction.StaticPublication.PreparationCommitted)
                {
                    int entityOperations =
                        transaction.StaticPublication.PreparationCursor
                        < transaction.Build.Landblock.Entities.Count
                            ? 1
                            : 0;
                    if (!TryRun(
                            new StreamingWorkCost(
                                EntityOperations: entityOperations),
                            "publication-validate-statics",
                            () => _staticPublisher.AdvancePreparationOne(
                                transaction.StaticPublication)))
                    {
                        return new LandblockPublicationAdvance(false, progressed);
                    }
                }

                while (!transaction.RenderPublication.BeginCommitted)
                {
                    StreamingWorkCost cost =
                        !transaction.RenderPublication.TerrainCommitted
                            ? new StreamingWorkCost(
                                GpuUploadBytes:
                                    transaction.Cost.TerrainPayloadBytes)
                            : !transaction.RenderPublication.VisibilityCommitted
                                ? new StreamingWorkCost(
                                    EntityOperations:
                                        transaction.Cost.VisibilityCells)
                                : default;
                    if (!TryRun(
                            cost,
                            "publication-render-prefix",
                            () => _renderPublisher.AdvanceBeginOne(
                                transaction.RenderPublication)))
                    {
                        return new LandblockPublicationAdvance(false, progressed);
                    }
                }
                while (!transaction.PhysicsPublication.BeginCommitted)
                {
                    int entityOperations =
                        transaction.PhysicsPublication.TerrainSurface is not null
                        && transaction.PhysicsPublication.Build.Landblock.PhysicsDats?
                            .Info is { } info
                        && transaction.PhysicsPublication.CellCursor < info.NumCells
                            ? 1
                            : transaction.PhysicsPublication.BuildingCursor
                                < transaction.PhysicsPublication.Buildings.Length
                                ? 1
                                : 0;
                    if (!TryRun(
                            new StreamingWorkCost(
                                EntityOperations: entityOperations),
                            "publication-physics-prefix",
                            () => _physicsPublisher.AdvanceBeginOne(
                                transaction.PhysicsPublication)))
                    {
                        return new LandblockPublicationAdvance(false, progressed);
                    }
                }

                while (!transaction.RenderPublication.CompletionCommitted)
                {
                    int entityOperations =
                        transaction.RenderPublication.BuildingPublication is
                            { PreparationCommitted: false } buildingPublication
                        && buildingPublication.BuildingCursor
                            < buildingPublication.Buildings.Length
                            ? 1
                            : transaction.RenderPublication.EnvCellPublication is
                                { PreparationCommitted: false } envCellPublication
                            && envCellPublication.ShellCursor
                                < envCellPublication.Build.Shells.Length
                                ? 1
                            : !transaction.RenderPublication.EnvCellsCommitted
                                && transaction.RenderPublication
                                    .EnvCellPublication is null
                                    ? transaction.Cost.EnvCellShells
                                : 0;
                    if (!TryRun(
                            new StreamingWorkCost(
                                EntityOperations: entityOperations),
                            "publication-render-suffix",
                            () => _renderPublisher.AdvanceCompleteOne(
                                transaction.RenderPublication)))
                    {
                        return new LandblockPublicationAdvance(false, progressed);
                    }
                }

                while (!transaction.StaticPublication.BeginCommitted)
                {
                    int entityOperations =
                        transaction.StaticPublication.PriorCleanupCursor
                        < transaction.StaticPublication
                            .OrderedPreviouslyActiveIds.Count
                            ? 1
                            : 0;
                    if (!TryRun(
                            new StreamingWorkCost(
                                EntityOperations: entityOperations),
                            "publication-static-cleanup",
                            () => _staticPublisher.AdvanceBeginOne(
                                transaction.StaticPublication)))
                    {
                        return new LandblockPublicationAdvance(false, progressed);
                    }
                }
                while (!transaction.PhysicsPublication.CompletionCommitted)
                {
                    if (transaction.PhysicsPublication.EngineMutationCommitted
                        && transaction.PhysicsPublication.RuntimeMutationPending
                        && !transaction.SpatialPresentationCommitted
                        && !TryRun(
                            default,
                            "publication-spatial-commit",
                            () => CommitSpatialPresentation(transaction)))
                    {
                        return new LandblockPublicationAdvance(false, progressed);
                    }
                    int entityOperations =
                        transaction.PhysicsPublication.GfxCursor
                            < transaction.PhysicsPublication.GfxObjectIds.Length
                            ? 1
                            : transaction.PhysicsPublication.PriorStaticCursor
                                < transaction.PhysicsPublication
                                    .PriorStaticOwnerIds.Length
                                ? 1
                                : transaction.PhysicsPublication.StaticCursor
                                    < transaction.Build.Landblock.Entities.Count
                                    ? 1
                                    : transaction.PhysicsPublication
                                            .RefloodOwnerIds is null
                                    || transaction.PhysicsPublication
                                        .RefloodCursor
                                        < transaction.PhysicsPublication
                                            .RefloodOwnerIds.Count
                                    || !transaction.PhysicsPublication
                                        .SealCommitted
                                        ? 1
                                        : 0;
                    if (!TryRun(
                            new StreamingWorkCost(
                                EntityOperations: entityOperations),
                            "publication-physics-statics",
                            () => _physicsPublisher.AdvanceCompleteOne(
                                transaction.PhysicsPublication,
                                entity =>
                                    _staticPublisher
                                        .PrepareEntityBeforeCollision(
                                            transaction.StaticPublication,
                                            entity))))
                    {
                        return new LandblockPublicationAdvance(false, progressed);
                    }
                    if (transaction.PhysicsPublication.RuntimeMutationPending)
                    {
                        if (transaction.PhysicsPublication.EngineMutationCommitted
                            && !transaction.SpatialPresentationCommitted
                            && !TryRun(
                                default,
                                "publication-spatial-commit",
                                () => CommitSpatialPresentation(transaction)))
                        {
                            return new LandblockPublicationAdvance(false, progressed);
                        }
                        if (!_physicsPublisher
                                .CanContinueMutationSynchronously())
                        {
                            return new LandblockPublicationAdvance(
                                false,
                                progressed);
                        }
                    }
                }
                while (!transaction.StaticPublication.CompletionCommitted)
                {
                    int entityOperations =
                        transaction.StaticPublication.PluginCursor
                        < transaction.StaticPublication.OrderedSnapshots.Count
                            ? 1
                            : 0;
                    if (!TryRun(
                            new StreamingWorkCost(
                                EntityOperations: entityOperations),
                            "publication-plugin-projection",
                            () => _staticPublisher.AdvanceCompleteOne(
                                transaction.StaticPublication)))
                    {
                        return new LandblockPublicationAdvance(false, progressed);
                    }
                }
            }
            else
            {
                StreamingWorkCost legacyCost = new(
                    EntityOperations: transaction.Cost.Entities,
                    GpuUploadBytes: transaction.Cost.TerrainPayloadBytes);
                bool ran = TryRun(
                    legacyCost,
                    "publication-legacy-presentation",
                    () =>
                    {
                        try
                        {
                            (_publishBeforeSpatialCommit
                                ?? throw new InvalidOperationException(
                                    "The presentation pipeline has no publication owners."))(
                                transaction.Build,
                                transaction.MeshData);
                        }
                        catch (Exception error)
                        {
                            if (error is StreamingMutationException
                                {
                                    MutationCommitted: true,
                                })
                            {
                                _publications.Remove(result);
                            }
                            throw;
                        }
                    });
                if (!ran)
                {
                    return new LandblockPublicationAdvance(false, progressed);
                }
            }
            transaction.PresentationCommitted = true;
        }

        if (!transaction.SpatialPresentationCommitted)
        {
            if (!TryRun(
                    default,
                    "publication-spatial-commit",
                    () => CommitSpatialPresentation(transaction)))
            {
                return new LandblockPublicationAdvance(false, progressed);
            }
        }

        if (!transaction.EnvCellReplayCommitted)
        {
            if (!TryRun(
                    new StreamingWorkCost(
                        EntityOperations: transaction.Cost.EnvCellShells),
                    "publication-envcell-replay",
                    () =>
                    {
                        if (transaction.Kind != PublicationKind.Far
                            && transaction.Build.EnvCells is
                                {
                                    Shells.Length: > 0,
                                } envCells)
                        {
                            _ensureEnvCellMeshes?.Invoke(envCells);
                        }
                        transaction.EnvCellReplayCommitted = true;
                    }))
            {
                return new LandblockPublicationAdvance(false, progressed);
            }
        }

        if (!transaction.LiveRecoveryCommitted)
        {
            if (!TryRun(
                    default,
                    "publication-live-recovery",
                    () =>
                    {
                        _onLandblockLoaded?.Invoke(transaction.LandblockId);
                        transaction.LiveRecoveryCommitted = true;
                    }))
            {
                return new LandblockPublicationAdvance(false, progressed);
            }
        }

        _publications.Remove(result);
        if (transaction.Timing is { } completedTiming)
        {
            PublicationTimingProbe.EmitPublication(
                transaction.LandblockId,
                transaction.Kind.ToString(),
                completedTiming);
        }
        return new LandblockPublicationAdvance(true, progressed);
    }

    private void CommitSpatialPresentation(PublicationTransaction transaction)
    {
        using GpuWorldState.MutationBatch mutation = _state.BeginMutationBatch();
        if (!transaction.SpatialCommitted)
        {
            IEnumerable<ulong>? renderIds =
                transaction.Build.EnvCells?.Shells.Select(
                    static shell => shell.GeometryId);
            IEnumerable<ulong>? ordinaryRenderIds =
                transaction.Build.EnvCells?.WalkBuildingMeshDependencies;
            transaction.SpatialPublication = transaction.Kind switch
            {
                PublicationKind.Loaded
                    or PublicationKind.PromoteSelfContained
                    or PublicationKind.Far =>
                    _state.CommitLandblockSpatial(
                        transaction.Build.Landblock,
                        renderIds,
                        transaction.Tier,
                        ordinaryRenderIds),
                PublicationKind.PromoteExisting =>
                    _state.CommitEntitiesToExistingLandblockSpatial(
                        transaction.LandblockId,
                        transaction.Build.Landblock.Entities,
                        renderIds,
                        ordinaryRenderIds),
                _ => throw new InvalidOperationException(
                    $"Unknown landblock publication kind {transaction.Kind}."),
            };
            transaction.SpatialCommitted = true;
        }

        _state.ActivateLandblockPresentation(
            transaction.SpatialPublication
            ?? throw new InvalidOperationException(
                "A committed spatial publication has no activation receipt."));
        if (transaction.SpatialPublication.RequiresActivation)
        {
            _staticProjectionSink?.Reconcile(
                transaction.Build,
                transaction.SpatialPublication);
        }
        transaction.SpatialPresentationCommitted = true;
    }
}
