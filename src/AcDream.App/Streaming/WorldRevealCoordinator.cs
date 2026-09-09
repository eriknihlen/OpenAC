using AcDream.App.Rendering;
using AcDream.Runtime;
using AcDream.Runtime.World;

namespace AcDream.App.Streaming;

internal interface IWorldRevealStreamingScheduler
{
    void BeginDestinationReservation(
        long revealGeneration,
        uint destinationCell,
        int requiredRenderRadius);

    void EndDestinationReservation(long revealGeneration);
}

/// <summary>
/// Generation-scoped renderer-resource profile used only while the normal
/// world viewport is withheld for a login or portal destination.
/// </summary>
internal interface IWorldRevealRenderResourceScheduler
{
    void BeginDestinationReveal(long revealGeneration);
    void EndDestinationReveal(long revealGeneration);
}

internal sealed class WorldRevealCoordinator
{
    private sealed class HostProjection(
        RuntimeWorldHostProjectionToken token,
        int requiredRenderRadius,
        in WorldGenerationQuiescenceEdge quiescenceEdge)
    {
        public RuntimeWorldHostProjectionToken Token { get; } = token;

        public int RequiredRenderRadius { get; set; } = requiredRenderRadius;
        public WorldGenerationQuiescenceEdge QuiescenceEdge { get; } =
            quiescenceEdge;
        public bool QuiescenceCommitted { get; set; }
        public bool StreamingRegistered { get; set; }
        public bool RenderResourcesRegistered { get; set; }
        public bool SimulationReleaseProjected { get; set; }
        public bool StreamingReleased { get; set; }
        public bool RenderResourcesReleased { get; set; }
    }

    private readonly RuntimeWorldTransitState _transit;
    private readonly WorldRevealReadinessBarrier _readiness;
    private readonly WorldGenerationQuiescence? _quiescence;
    private readonly IWorldRevealStreamingScheduler? _streaming;
    private readonly IWorldRevealRenderResourceScheduler? _renderResources;
    private readonly List<HostProjection> _hostProjections = [];
    private readonly RevealTimingProbe? _timing;
    private bool _hostRetryActive;
    private bool _hostRetryRequested;

    public WorldRevealCoordinator(
        RuntimeWorldTransitState transit,
        Func<StreamingRevealWindow> revealWindow,
        Func<uint, int, int, bool> isRenderNeighborhoodReady,
        Func<uint, bool> isSpawnCellReady,
        Func<uint, int, bool> isTerrainNeighborhoodReady,
        Func<bool> areCompositeTexturesReady,
        Action<uint, int> prepareCompositeTextures,
        Action invalidateCompositeTextures,
        Func<uint, bool> isSpawnClaimUnhydratable,
        WorldGenerationQuiescence? quiescence = null,
        IWorldRevealStreamingScheduler? streaming = null,
        IWorldRevealRenderResourceScheduler? renderResources = null,
        Func<int>? loadedLandblockCount = null,
        IRenderFrameResourceDiagnosticsSource? renderResourceDiagnostics = null)
    {
        _transit = transit ?? throw new ArgumentNullException(nameof(transit));
        _readiness = new WorldRevealReadinessBarrier(
            revealWindow,
            isRenderNeighborhoodReady,
            isSpawnCellReady,
            isTerrainNeighborhoodReady,
            areCompositeTexturesReady,
            prepareCompositeTextures,
            invalidateCompositeTextures,
            isSpawnClaimUnhydratable);
        _quiescence = quiescence;
        _streaming = streaming;
        _renderResources = renderResources;
        if (StreamingDiagnostics.ProbeRevealTiming)
        {
            _timing = new RevealTimingProbe(
                loadedLandblockCount,
                renderResourceDiagnostics);
        }
    }

    public RuntimePortalSnapshot Snapshot => _transit.Snapshot;
    public int PortalMaterializationCount =>
        _transit.Snapshot.PortalMaterializationCount;
    public bool WaitCueShown => _transit.Snapshot.WaitCueShown;
    public RuntimeWorldTransitOwnershipSnapshot Ownership =>
        _transit.Ownership;

    public long BeginLogin(uint destinationCell)
    {
        if (destinationCell == 0u)
            throw new ArgumentOutOfRangeException(nameof(destinationCell));

        WithdrawHostForReplacement();
        WorldGenerationQuiescenceEdge quiescenceEdge =
            _quiescence?.CaptureBegin() ?? default;
        _readiness.Begin();
        long generation = _transit.BeginLoginReveal(destinationCell);
        _timing?.Begin(
            "login",
            generation,
            destinationCell,
            _readiness.RequiredWindow(destinationCell));
        BeginHostLifetime(
            generation,
            destinationCell,
            quiescenceEdge);
        return generation;
    }

    public bool TryBeginPortal(
        ushort teleportSequence,
        uint destinationCell,
        out long generation)
    {
        if (destinationCell == 0u)
            throw new ArgumentOutOfRangeException(nameof(destinationCell));
        if (!_transit.CanBeginPortalReveal(
                teleportSequence,
                destinationCell))
        {
            generation = 0;
            return false;
        }

        WithdrawHostForReplacement();
        WorldGenerationQuiescenceEdge quiescenceEdge =
            _quiescence?.CaptureBegin() ?? default;
        if (!_transit.TryBeginPortalReveal(
                teleportSequence,
                destinationCell,
                out generation))
        {
            return false;
        }

        _readiness.Begin();
        _timing?.Begin(
            "portal",
            generation,
            destinationCell,
            _readiness.RequiredWindow(destinationCell));
        BeginHostLifetime(
            generation,
            destinationCell,
            quiescenceEdge);
        return true;
    }

    private void BeginHostLifetime(
        long generation,
        uint destinationCell,
        in WorldGenerationQuiescenceEdge quiescenceEdge)
    {
        if (!_transit.TryRegisterHostProjection(
                generation,
                destinationCell,
                out RuntimeWorldHostProjectionToken token))
        {
            throw new InvalidOperationException(
                "Runtime rejected the exact reveal host projection.");
        }

        _hostProjections.Add(new HostProjection(
            token,
            _readiness.RequiredRenderRadius(destinationCell),
            quiescenceEdge));
        RetryPendingHostWork();
    }

    public WorldRevealReadinessSnapshot PrepareAndEvaluate(uint destinationCell)
    {
        _readiness.Prepare(destinationCell);
        return Evaluate(destinationCell);
    }

    public WorldRevealReadinessSnapshot Evaluate(uint destinationCell)
    {
        RetryPendingHostWork();
        WorldRevealReadinessSnapshot snapshot = _readiness.Evaluate(destinationCell);
        ReconcileDestinationReservationRadius(snapshot);
        RuntimePortalSnapshot portal = _transit.Snapshot;
        _timing?.Observe(snapshot, portal);
        if (portal.Generation != 0)
        {
            _transit.AcknowledgeDestinationReadiness(
                new RuntimeDestinationReadiness(
                    portal.Generation,
                    snapshot.DestinationCell,
                    snapshot.IsIndoor,
                    snapshot.IsUnhydratable,
                    snapshot.RequiredRenderRadius,
                    snapshot.IsRenderNeighborhoodReady,
                    snapshot.AreCompositeTexturesReady,
                    snapshot.IsCollisionReady));
        }
        return snapshot;
    }

    public bool CanPlacePortalDestination(
        long generation,
        ushort teleportSequence,
        uint destinationCell) =>
        _transit.CanPlacePortalDestination(
            generation,
            teleportSequence,
            destinationCell);

    public bool ObserveMaterialized(
        long generation,
        ushort teleportSequence,
        uint destinationCell)
    {
        bool acknowledged = _transit.AcknowledgePortalMaterialized(
            generation,
            teleportSequence,
            destinationCell);
        RetryPendingHostWork();
        return acknowledged;
    }

    public bool ObserveLoginMaterialized(long generation)
    {
        bool acknowledged =
            _transit.AcknowledgeLoginMaterialized(generation);
        RetryPendingHostWork();
        return acknowledged;
    }

    public void ObserveWorldViewportVisible()
    {
        RetryPendingHostWork();
        long generation = _transit.Snapshot.Generation;
        if (generation != 0)
            _transit.AcknowledgeWorldViewportVisible(generation);
    }

    public bool ObserveWait(TimeSpan elapsed)
    {
        long generation = _transit.Snapshot.Generation;
        return generation != 0
            && _transit.ObserveWait(generation, elapsed);
    }

    public void RevealWorldViewport()
    {
        HostProjection? host = FindCurrentHostProjection();
        if (host is null)
            return;

        _transit.RequireDestinationReservationRelease(host.Token);
        RetryPendingHostWork();
    }

    public void Complete()
    {
        long generation = _transit.Snapshot.Generation;
        if (generation == 0)
            return;

        _transit.Complete(generation);
        RetryPendingHostWork();
    }

    public void Cancel()
    {
        long generation = _transit.Snapshot.Generation;
        if (generation == 0)
            return;

        _transit.Cancel(generation);
        RetryPendingHostWork();
    }

    public void ResetSession()
    {
        ResetHostSession();
        _transit.ResetSession();
    }

    internal void ResetHostSession()
    {
        long generation = _transit.Snapshot.Generation;
        if (generation != 0)
            _transit.Cancel(generation);
        RetryPendingHostWork();
    }

    internal void RetryPendingHostWork()
    {
        _hostRetryRequested = true;
        if (_hostRetryActive)
            return;

        _hostRetryActive = true;
        try
        {
            do
            {
                _hostRetryRequested = false;
                int index = 0;
                while (index < _hostProjections.Count)
                {
                    HostProjection host = _hostProjections[index];
                    DrainHostProjection(host);
                    if (index < _hostProjections.Count
                        && ReferenceEquals(
                            _hostProjections[index],
                            host))
                    {
                        index++;
                    }
                }
            }
            while (_hostRetryRequested);
        }
        finally
        {
            _hostRetryActive = false;
        }
    }

    private void DrainHostProjection(HostProjection host)
    {
        if (!TryGetHostProjection(host, out var projection))
            return;

        RuntimeWorldHostAcknowledgementStage pending =
            projection.PendingAcknowledgements;
        if ((pending & RuntimeWorldHostAcknowledgementStage
                .ProjectionRegistered) != 0)
        {
            CompleteHostRegistration(host);
        }

        if (!TryGetHostProjection(host, out projection))
            return;
        pending = projection.PendingAcknowledgements;
        if ((pending & RuntimeWorldHostAcknowledgementStage
                .SimulationReleaseProjected) != 0)
        {
            CompleteSimulationRelease(host);
        }

        if (!TryGetHostProjection(host, out projection))
            return;
        pending = projection.PendingAcknowledgements;
        if ((pending & RuntimeWorldHostAcknowledgementStage
                .DestinationReservationReleased) != 0)
        {
            CompleteDestinationReservationRelease(host);
        }

        if (!TryGetHostProjection(host, out projection))
            return;
        if ((projection.PendingAcknowledgements
                & RuntimeWorldHostAcknowledgementStage.TerminalProjected)
            == 0)
        {
            return;
        }

        Acknowledge(
            host,
            RuntimeWorldHostAcknowledgementStage.TerminalProjected);
        RemoveHostProjection(host);
    }

    private void CompleteHostRegistration(HostProjection host)
    {
        if (!host.QuiescenceCommitted)
        {
            _quiescence?.CommitBegin(host.QuiescenceEdge);
            host.QuiescenceCommitted = true;
        }
        if (!IsStagePending(
                host,
                RuntimeWorldHostAcknowledgementStage
                    .ProjectionRegistered))
        {
            return;
        }
        if (!host.StreamingRegistered)
        {
            _streaming?.BeginDestinationReservation(
                host.Token.Generation,
                host.Token.DestinationCell,
                host.RequiredRenderRadius);
            host.StreamingRegistered = true;
        }
        if (!IsStagePending(
                host,
                RuntimeWorldHostAcknowledgementStage
                    .ProjectionRegistered))
        {
            return;
        }
        if (!host.RenderResourcesRegistered)
        {
            _renderResources?.BeginDestinationReveal(
                host.Token.Generation);
            host.RenderResourcesRegistered = true;
        }
        if (!IsStagePending(
                host,
                RuntimeWorldHostAcknowledgementStage
                    .ProjectionRegistered))
        {
            return;
        }

        Acknowledge(
            host,
            RuntimeWorldHostAcknowledgementStage.ProjectionRegistered);
    }

    private void CompleteSimulationRelease(HostProjection host)
    {
        if (!host.SimulationReleaseProjected)
        {
            _quiescence?.ObserveReleased();
            host.SimulationReleaseProjected = true;
        }
        if (!IsStagePending(
                host,
                RuntimeWorldHostAcknowledgementStage
                    .SimulationReleaseProjected))
        {
            return;
        }

        Acknowledge(
            host,
            RuntimeWorldHostAcknowledgementStage
                .SimulationReleaseProjected);
    }

    private void CompleteDestinationReservationRelease(HostProjection host)
    {
        if (!host.StreamingReleased)
        {
            if (host.StreamingRegistered)
            {
                _streaming?.EndDestinationReservation(
                    host.Token.Generation);
            }
            host.StreamingReleased = true;
        }
        if (!IsStagePending(
                host,
                RuntimeWorldHostAcknowledgementStage
                    .DestinationReservationReleased))
        {
            return;
        }
        if (!host.RenderResourcesReleased)
        {
            if (host.RenderResourcesRegistered)
            {
                _renderResources?.EndDestinationReveal(
                    host.Token.Generation);
            }
            host.RenderResourcesReleased = true;
        }
        if (!IsStagePending(
                host,
                RuntimeWorldHostAcknowledgementStage
                    .DestinationReservationReleased))
        {
            return;
        }

        Acknowledge(
            host,
            RuntimeWorldHostAcknowledgementStage
                .DestinationReservationReleased);
    }

    private void ReconcileDestinationReservationRadius(
        in WorldRevealReadinessSnapshot snapshot)
    {
        if (_streaming is null || !snapshot.HasDestination)
            return;

        HostProjection? host = FindCurrentHostProjection();
        if (host is null
            || !host.StreamingRegistered
            || host.StreamingReleased
            || host.Token.DestinationCell != snapshot.DestinationCell
            || host.RequiredRenderRadius == snapshot.RequiredRenderRadius)
        {
            return;
        }

        _streaming.EndDestinationReservation(host.Token.Generation);
        _streaming.BeginDestinationReservation(
            host.Token.Generation,
            host.Token.DestinationCell,
            snapshot.RequiredRenderRadius);
        host.RequiredRenderRadius = snapshot.RequiredRenderRadius;
    }

    private void WithdrawHostForReplacement()
    {
        HostProjection? host = FindCurrentHostProjection();
        if (host is null)
            return;

        if (!_transit.BeginHostProjectionSupersession(host.Token))
        {
            throw new InvalidOperationException(
                "Runtime rejected reveal-host supersession.");
        }
        RetryPendingHostWork();
    }

    private HostProjection? FindCurrentHostProjection()
    {
        long generation = _transit.Snapshot.Generation;
        for (int i = 0; i < _hostProjections.Count; i++)
        {
            HostProjection candidate = _hostProjections[i];
            if (candidate.Token.Generation == generation)
                return candidate;
        }

        return null;
    }

    private bool TryGetHostProjection(
        HostProjection host,
        out RuntimeWorldHostProjectionSnapshot projection)
    {
        if (_transit.TryGetHostProjection(host.Token, out projection))
            return true;

        RemoveHostProjection(host);
        return false;
    }

    private bool IsStagePending(
        HostProjection host,
        RuntimeWorldHostAcknowledgementStage stage) =>
        _transit.TryGetHostProjection(
            host.Token,
            out RuntimeWorldHostProjectionSnapshot projection)
        && (projection.PendingAcknowledgements & stage) != 0;

    private void RemoveHostProjection(HostProjection host)
    {
        for (int i = 0; i < _hostProjections.Count; i++)
        {
            if (!ReferenceEquals(_hostProjections[i], host))
                continue;

            _hostProjections.RemoveAt(i);
            return;
        }
    }

    private void Acknowledge(
        HostProjection host,
        RuntimeWorldHostAcknowledgementStage stage)
    {
        if (!_transit.AcknowledgeHostProjection(
                new RuntimeWorldHostAcknowledgement(
                    host.Token,
                    stage)))
        {
            throw new InvalidOperationException(
                $"Runtime rejected reveal host acknowledgement {stage} "
                + $"for generation {host.Token.Generation}.");
        }
    }
}
