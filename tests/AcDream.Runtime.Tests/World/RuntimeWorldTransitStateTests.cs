using System.Numerics;
using AcDream.Core.Physics;
using AcDream.Runtime.Physics;
using AcDream.Runtime.World;

namespace AcDream.Runtime.Tests.World;

public sealed class RuntimeWorldTransitStateTests
{
    private const uint OutdoorCell = 0x11340021u;
    private const uint OtherCell = 0x3032001Cu;

    [Fact]
    public void PlacementAuthority_RequiresCurrentPortalHostSequenceAndReveal()
    {
        var state = new RuntimeWorldTransitState();
        long generation = BeginPortal(state, OutdoorCell, sequence: 7);
        RuntimeWorldHostProjectionToken host =
            RegisterHost(state, generation, OutdoorCell);
        RuntimePortalPlacementAuthority authority = new(
            Present: true,
            RevealGeneration: generation,
            TeleportSequence: 7,
            Projection: host);

        Assert.True(state.IsCurrentPlacementAuthority(
            authority,
            OutdoorCell));
        Assert.True(state.IsCurrentPlacementAuthority(default, OutdoorCell));
        Assert.False(state.IsCurrentPlacementAuthority(
            authority with { TeleportSequence = 8 },
            OutdoorCell));
        Assert.False(state.IsCurrentPlacementAuthority(
            authority,
            OtherCell));

        Assert.True(state.BeginHostProjectionSupersession(host));
        Assert.False(state.IsCurrentPlacementAuthority(
            authority,
            OutdoorCell));
    }

    [Fact]
    public void PlacementAuthority_RejectsCancelledAndCompletedReveals()
    {
        var cancelled = new RuntimeWorldTransitState();
        long cancelledGeneration = BeginPortal(cancelled, OutdoorCell);
        RuntimeWorldHostProjectionToken cancelledHost =
            RegisterHost(cancelled, cancelledGeneration, OutdoorCell);
        RuntimePortalPlacementAuthority cancelledAuthority = new(
            true,
            cancelledGeneration,
            1,
            cancelledHost);
        Assert.True(cancelled.Cancel(cancelledGeneration));
        Assert.False(cancelled.IsCurrentPlacementAuthority(
            cancelledAuthority,
            OutdoorCell));

        var completed = new RuntimeWorldTransitState();
        long completedGeneration = BeginPortal(completed, OutdoorCell);
        RuntimeWorldHostProjectionToken completedHost =
            RegisterHost(completed, completedGeneration, OutdoorCell);
        RuntimePortalPlacementAuthority completedAuthority = new(
            true,
            completedGeneration,
            1,
            completedHost);
        Assert.True(completed.AcknowledgeDestinationReadiness(
            Ready(completedGeneration, OutdoorCell)));
        Assert.True(completed.AcknowledgePortalMaterialized(
            completedGeneration,
            1,
            OutdoorCell));
        Assert.True(completed.AcknowledgeWorldViewportVisible(
            completedGeneration));
        Assert.True(completed.Complete(completedGeneration));

        Assert.False(completed.IsCurrentPlacementAuthority(
            completedAuthority,
            OutdoorCell));
    }

    [Fact]
    public void BeginReveal_OwnsGenerationDestinationAndSimulationGate()
    {
        var state = new RuntimeWorldTransitState();

        long generation =
            state.BeginLoginReveal(OutdoorCell);

        Assert.Equal(1, generation);
        Assert.Equal(generation, state.Snapshot.Generation);
        Assert.Equal(RuntimePortalKind.Login, state.Snapshot.Kind);
        Assert.Equal(OutdoorCell, state.Snapshot.DestinationCell);
        Assert.False(state.IsWorldSimulationAvailable);
        Assert.False(state.Snapshot.IsReady);
    }

    [Fact]
    public void TypedReadiness_RejectsWrongGenerationAndDestination()
    {
        List<string> log = [];
        var state = new RuntimeWorldTransitState(log.Add);
        long generation =
            BeginPortal(state, OutdoorCell);

        Assert.False(state.AcknowledgeDestinationReadiness(
            Ready(generation + 1, OutdoorCell)));
        Assert.False(state.AcknowledgeDestinationReadiness(
            Ready(generation, OtherCell)));

        Assert.Equal(OutdoorCell, state.Snapshot.DestinationCell);
        Assert.False(state.Snapshot.IsReady);
        Assert.Equal(1, state.Snapshot.InvariantFailureCount);
        Assert.Equal(
            1,
            log.Count(value => value.Contains(
                "event=invariant-failure",
                StringComparison.Ordinal)));
        Assert.Single(
            log,
            value => value.Contains(
                "event=rejected",
                StringComparison.Ordinal));
    }

    [Fact]
    public void Materialization_ReleasesSimulationBeforeViewportAndCompletion()
    {
        var state = new RuntimeWorldTransitState();
        long generation =
            BeginPortal(state, OutdoorCell);
        Assert.True(state.AcknowledgeDestinationReadiness(
            Ready(generation, OutdoorCell)));

        Assert.True(state.AcknowledgePortalMaterialized(
            generation,
            1,
            OutdoorCell));

        Assert.True(state.IsWorldSimulationAvailable);
        Assert.True(state.Snapshot.Materialized);
        Assert.False(state.Snapshot.WorldViewportObserved);
        Assert.False(state.Snapshot.Completed);
        Assert.Equal(1, state.Snapshot.PortalMaterializationCount);
        Assert.False(state.AcknowledgePortalMaterialized(
            generation,
            1,
            OutdoorCell));

        Assert.True(state.AcknowledgeWorldViewportVisible(generation));
        Assert.True(state.Complete(generation));
        Assert.True(state.Snapshot.WorldViewportObserved);
        Assert.True(state.Snapshot.Completed);
        Assert.Equal(1, state.Snapshot.PortalMaterializationCount);
    }

    [Fact]
    public void MaterializationBeforeReadiness_IsRejectedWithoutOpeningWorld()
    {
        var state = new RuntimeWorldTransitState();
        long generation =
            BeginPortal(state, OutdoorCell);

        Assert.False(state.AcknowledgePortalMaterialized(
            generation,
            1,
            OutdoorCell));

        Assert.False(state.IsWorldSimulationAvailable);
        Assert.False(state.Snapshot.Materialized);
        Assert.Equal(1, state.Snapshot.InvariantFailureCount);
        Assert.Equal(0, state.Snapshot.PortalMaterializationCount);
    }

    [Fact]
    public void LoginCompletion_ReleasesSimulationBeforeFirstVisibleFrame()
    {
        var state = new RuntimeWorldTransitState();
        long generation =
            state.BeginLoginReveal(OutdoorCell);
        state.AcknowledgeDestinationReadiness(
            Ready(generation, OutdoorCell));

        Assert.True(state.Complete(generation));
        Assert.True(state.IsWorldSimulationAvailable);
        Assert.False(state.Snapshot.WorldViewportObserved);

        Assert.True(state.AcknowledgeWorldViewportVisible(generation));
        Assert.True(state.Snapshot.WorldViewportObserved);
        Assert.Equal(0, state.Snapshot.InvariantFailureCount);
    }

    [Fact]
    public void LoginMaterialization_ReleasesSimulationBeforeViewportAndCompletion()
    {
        var state = new RuntimeWorldTransitState();
        long generation = state.BeginLoginReveal(OutdoorCell);
        Assert.True(state.AcknowledgeDestinationReadiness(
            Ready(generation, OutdoorCell)));

        Assert.True(state.AcknowledgeLoginMaterialized(generation));

        Assert.True(state.IsWorldSimulationAvailable);
        Assert.True(state.Snapshot.Materialized);
        Assert.False(state.Snapshot.WorldViewportObserved);
        Assert.False(state.Snapshot.Completed);
        Assert.Equal(0, state.Snapshot.PortalMaterializationCount);
        Assert.False(state.AcknowledgeLoginMaterialized(generation));

        Assert.True(state.AcknowledgeWorldViewportVisible(generation));
        Assert.True(state.Complete(generation));
        Assert.True(state.Snapshot.Completed);
        Assert.Equal(0, state.Snapshot.InvariantFailureCount);
    }

    [Fact]
    public void LoginMaterializationBeforeReadiness_IsRejectedWithoutOpeningWorld()
    {
        var state = new RuntimeWorldTransitState();
        long generation = state.BeginLoginReveal(OutdoorCell);

        Assert.False(state.AcknowledgeLoginMaterialized(generation));

        Assert.False(state.IsWorldSimulationAvailable);
        Assert.False(state.Snapshot.Materialized);
        Assert.Equal(1, state.Snapshot.InvariantFailureCount);
    }

    [Fact]
    public void LoginMaterialization_RejectsPortalRevealsAndStaleGenerations()
    {
        var state = new RuntimeWorldTransitState();
        long generation = BeginPortal(state, OutdoorCell);
        Assert.True(state.AcknowledgeDestinationReadiness(
            Ready(generation, OutdoorCell)));

        Assert.False(state.AcknowledgeLoginMaterialized(generation));
        Assert.False(state.IsWorldSimulationAvailable);

        // Stale/zero generations are rejected without invariant failures.
        Assert.False(state.AcknowledgeLoginMaterialized(0));
        Assert.False(state.AcknowledgeLoginMaterialized(generation + 1));
        Assert.Equal(0, state.Snapshot.InvariantFailureCount);
    }

    [Fact]
    public void EarlyViewport_IsRejectedWithoutFabricatingVisibility()
    {
        var state = new RuntimeWorldTransitState();
        long generation =
            state.BeginLoginReveal(OutdoorCell);

        Assert.False(state.AcknowledgeWorldViewportVisible(generation));

        Assert.False(state.Snapshot.IsReady);
        Assert.False(state.Snapshot.WorldViewportObserved);
        Assert.Equal(1, state.Snapshot.InvariantFailureCount);
    }

    [Fact]
    public void ReadinessShape_MustMatchDestinationCell()
    {
        var state = new RuntimeWorldTransitState();
        long generation =
            state.BeginLoginReveal(OutdoorCell);
        RuntimeDestinationReadiness invalid =
            Ready(generation, OutdoorCell) with
            {
                IsIndoor = true,
                RequiredRenderRadius = 0,
            };

        Assert.False(state.AcknowledgeDestinationReadiness(invalid));

        Assert.False(state.Snapshot.IsReady);
        Assert.Equal(1, state.Snapshot.InvariantFailureCount);
    }

    [Theory]
    [InlineData(1)]
    [InlineData(2)]
    [InlineData(5)]
    [InlineData(8)]
    [InlineData(12)]
    [InlineData(15)]
    [InlineData(25)]
    public void OutdoorReadinessShape_AcceptsAnyDerivedStreamingRadius(
        int requiredRenderRadius)
    {
        var state = new RuntimeWorldTransitState();
        long generation = state.BeginLoginReveal(OutdoorCell);
        RuntimeDestinationReadiness acknowledgement =
            Ready(generation, OutdoorCell) with
            {
                RequiredRenderRadius = requiredRenderRadius,
            };

        Assert.True(state.AcknowledgeDestinationReadiness(acknowledgement));

        Assert.Equal(0, state.Snapshot.InvariantFailureCount);
        Assert.Equal(
            requiredRenderRadius,
            state.Snapshot.Readiness.RequiredRenderRadius);
    }

    [Fact]
    public void OutdoorReadinessShape_RejectsAZeroRadius()
    {
        var state = new RuntimeWorldTransitState();
        long generation = state.BeginLoginReveal(OutdoorCell);
        RuntimeDestinationReadiness invalid =
            Ready(generation, OutdoorCell) with { RequiredRenderRadius = 0 };

        Assert.False(state.AcknowledgeDestinationReadiness(invalid));

        Assert.False(state.Snapshot.IsReady);
        Assert.Equal(1, state.Snapshot.InvariantFailureCount);
    }

    [Fact]
    public void IndoorReadinessShape_RejectsANonZeroRadius()
    {
        const uint indoorCell = 0x11340100u;
        var state = new RuntimeWorldTransitState();
        long generation = state.BeginLoginReveal(indoorCell);
        RuntimeDestinationReadiness invalid =
            Ready(generation, indoorCell) with
            {
                IsIndoor = true,
                RequiredRenderRadius = 1,
            };

        Assert.False(state.AcknowledgeDestinationReadiness(invalid));

        Assert.False(state.Snapshot.IsReady);
        Assert.Equal(1, state.Snapshot.InvariantFailureCount);
    }

    [Fact]
    public void IndoorReadinessShape_AcceptsTheZeroRadiusEnvCellArm()
    {
        const uint indoorCell = 0x11340100u;
        var state = new RuntimeWorldTransitState();
        long generation = state.BeginLoginReveal(indoorCell);
        RuntimeDestinationReadiness acknowledgement =
            Ready(generation, indoorCell) with
            {
                IsIndoor = true,
                RequiredRenderRadius = 0,
            };

        Assert.True(state.AcknowledgeDestinationReadiness(acknowledgement));

        Assert.Equal(0, state.Snapshot.InvariantFailureCount);
    }

    [Fact]
    public void AcceptedReadiness_IsGenerationSticky()
    {
        var state = new RuntimeWorldTransitState();
        long generation =
            BeginPortal(state, OutdoorCell);
        RuntimeDestinationReadiness accepted =
            Ready(generation, OutdoorCell);
        Assert.True(state.AcknowledgeDestinationReadiness(accepted));

        RuntimeDestinationReadiness regressed = accepted with
        {
            IsCollisionReady = false,
        };
        Assert.False(state.AcknowledgeDestinationReadiness(regressed));

        Assert.Equal(accepted, state.Snapshot.Readiness);
        Assert.True(state.Snapshot.IsReady);
        Assert.Equal(0, state.Snapshot.InvariantFailureCount);
    }

    [Fact]
    public void Completion_RejectsReorderedLoginAndPortalEdges()
    {
        var state = new RuntimeWorldTransitState();
        long login =
            state.BeginLoginReveal(OutdoorCell);

        Assert.False(state.Complete(login));
        Assert.False(state.Snapshot.Completed);

        long portal =
            BeginPortal(state, OtherCell, sequence: 2);
        Assert.True(state.AcknowledgeDestinationReadiness(
            Ready(portal, OtherCell)));
        Assert.False(state.Complete(portal));
        Assert.False(state.Snapshot.Completed);
        Assert.Equal(2, state.Snapshot.InvariantFailureCount);
    }

    [Fact]
    public void Supersession_RejectsOldAcknowledgementsAndKeepsLatestDestination()
    {
        var state = new RuntimeWorldTransitState();
        long first =
            state.BeginLoginReveal(OutdoorCell);
        long second =
            BeginPortal(state, OtherCell);

        Assert.True(second > first);
        Assert.False(state.AcknowledgeDestinationReadiness(
            Ready(first, OutdoorCell)));
        Assert.False(state.AcknowledgePortalMaterialized(
            first,
            1,
            OutdoorCell));

        Assert.Equal(second, state.Snapshot.Generation);
        Assert.Equal(OtherCell, state.Snapshot.DestinationCell);
        Assert.False(state.Snapshot.IsReady);
        Assert.False(state.IsWorldSimulationAvailable);
        Assert.Equal(0, state.Snapshot.InvariantFailureCount);
    }

    [Fact]
    public void Cancel_ReleasesSimulationAndRejectsLateHostWork()
    {
        var state = new RuntimeWorldTransitState();
        long generation =
            BeginPortal(state, OutdoorCell);

        Assert.True(state.Cancel(generation));
        Assert.True(state.IsWorldSimulationAvailable);
        Assert.True(state.Snapshot.Cancelled);

        Assert.False(state.AcknowledgeDestinationReadiness(
            Ready(generation, OutdoorCell)));
        Assert.False(state.AcknowledgePortalMaterialized(
            generation,
            1,
            OutdoorCell));
        Assert.False(state.Complete(generation));
        Assert.False(state.Snapshot.Materialized);
        Assert.False(state.Snapshot.Completed);
    }

    [Fact]
    public void MaterializedThenCancelled_RemainsAvailableAndCannotComplete()
    {
        var state = new RuntimeWorldTransitState();
        long generation =
            BeginPortal(state, OutdoorCell);
        state.AcknowledgeDestinationReadiness(
            Ready(generation, OutdoorCell));
        state.AcknowledgePortalMaterialized(
            generation,
            1,
            OutdoorCell);

        Assert.True(state.Cancel(generation));
        Assert.True(state.IsWorldSimulationAvailable);
        Assert.True(state.Snapshot.Materialized);
        Assert.True(state.Snapshot.Cancelled);
        Assert.False(state.Complete(generation));
        Assert.False(state.Snapshot.Completed);
        Assert.Equal(1, state.Snapshot.PortalMaterializationCount);
    }

    [Fact]
    public void WaitCue_UsesRetailFiveSecondEdgeWithoutCompletingReveal()
    {
        var state = new RuntimeWorldTransitState();
        long generation =
            BeginPortal(state, OutdoorCell);

        Assert.False(state.ObserveWait(
            generation,
            RuntimeWorldTransitState.RetailWaitCueDelay
                - TimeSpan.FromMilliseconds(1)));
        Assert.True(state.ObserveWait(
            generation,
            RuntimeWorldTransitState.RetailWaitCueDelay));
        Assert.True(state.ObserveWait(
            generation,
            RuntimeWorldTransitState.RetailWaitCueDelay
                + TimeSpan.FromSeconds(20)));

        Assert.True(state.Snapshot.WaitCueShown);
        Assert.False(state.Snapshot.Completed);
        Assert.False(state.IsWorldSimulationAvailable);
    }

    [Fact]
    public void UnhydratableDestination_IsAnExplicitReadyAcknowledgement()
    {
        var state = new RuntimeWorldTransitState();
        long generation =
            BeginPortal(state, OutdoorCell);
        var acknowledgement = new RuntimeDestinationReadiness(
            generation,
            OutdoorCell,
            IsIndoor: false,
            IsUnhydratable: true,
            RequiredRenderRadius: 1,
            IsRenderNeighborhoodReady: false,
            AreCompositeTexturesReady: false,
            IsCollisionReady: false);

        Assert.True(state.AcknowledgeDestinationReadiness(acknowledgement));
        Assert.True(state.Snapshot.IsReady);
        Assert.True(state.AcknowledgePortalMaterialized(
            generation,
            1,
            OutdoorCell));
    }

    [Fact]
    public void ResetSession_ClearsVisibleStateButNeverReusesGeneration()
    {
        var state = new RuntimeWorldTransitState();
        long first =
            BeginPortal(state, OutdoorCell);
        state.ResetSession();

        Assert.Equal(RuntimePortalSnapshot.Idle, state.Snapshot);
        Assert.True(state.IsWorldSimulationAvailable);

        long second =
            state.BeginLoginReveal(OtherCell);
        Assert.True(second > first);
        Assert.False(state.AcknowledgeDestinationReadiness(
            Ready(first, OutdoorCell)));
        Assert.Equal(second, state.Snapshot.Generation);
        Assert.Equal(OtherCell, state.Snapshot.DestinationCell);
    }

    [Fact]
    public void Instances_IsolateGenerationReadinessAndMaterializationCounts()
    {
        var first = new RuntimeWorldTransitState();
        var second = new RuntimeWorldTransitState();
        long firstGeneration =
            BeginPortal(first, OutdoorCell);
        long secondGeneration =
            second.BeginLoginReveal(OtherCell);
        RuntimeWorldHostProjectionToken firstHost =
            RegisterHost(first, firstGeneration, OutdoorCell);
        RuntimeWorldHostProjectionToken secondHost =
            RegisterHost(second, secondGeneration, OtherCell);
        Assert.True(Acknowledge(
            first,
            firstHost,
            RuntimeWorldHostAcknowledgementStage.ProjectionRegistered));
        Assert.True(Acknowledge(
            second,
            secondHost,
            RuntimeWorldHostAcknowledgementStage.ProjectionRegistered));
        first.AcknowledgeDestinationReadiness(
            Ready(firstGeneration, OutdoorCell));
        first.AcknowledgePortalMaterialized(
            firstGeneration,
            1,
            OutdoorCell);

        Assert.Equal(1, first.Snapshot.PortalMaterializationCount);
        Assert.Equal(0, second.Snapshot.PortalMaterializationCount);
        Assert.True(first.IsWorldSimulationAvailable);
        Assert.False(second.IsWorldSimulationAvailable);
        Assert.Equal(OtherCell, second.Snapshot.DestinationCell);
        Assert.Equal(1, secondGeneration);
        Assert.False(Acknowledge(
            second,
            firstHost,
            RuntimeWorldHostAcknowledgementStage
                .SimulationReleaseProjected));
        Assert.Equal(1, first.Ownership.PendingHostAcknowledgementCount);
        Assert.Equal(0, second.Ownership.PendingHostAcknowledgementCount);
    }

    [Fact]
    public void DiagnosticCallbackFailure_CannotInterruptCanonicalTransition()
    {
        var state = new RuntimeWorldTransitState(
            _ => throw new InvalidOperationException("diagnostic failed"));

        long generation =
            BeginPortal(state, OutdoorCell);
        Assert.True(state.AcknowledgeDestinationReadiness(
            Ready(generation, OutdoorCell)));
        Assert.True(state.AcknowledgePortalMaterialized(
            generation,
            1,
            OutdoorCell));
        Assert.True(state.Complete(generation));

        Assert.True(state.Snapshot.Completed);
        Assert.True(state.IsWorldSimulationAvailable);
        Assert.True(state.DiagnosticFailureCount >= 4);
        Assert.IsType<InvalidOperationException>(
            state.LastDiagnosticFailure);
    }

    [Fact]
    public void ReplacementRejectsDelayedPriorDestinationAndAcceptsCurrentAdvance()
    {
        var state = new RuntimeWorldTransitState();
        Assert.True(state.TryQueueTeleportStart(10));
        Assert.True(state.ActivateQueuedTeleport());
        state.EndTeleport();
        Assert.True(state.TryQueueTeleportStart(11));
        Assert.True(state.ActivateQueuedTeleport());

        Assert.False(state.OfferTeleportDestination(
            Destination(OutdoorCell, 10, x: 10f),
            teleportTimestampAdvanced: true));
        Assert.True(state.OfferTeleportDestination(
            Destination(OtherCell, 11, x: 11f),
            teleportTimestampAdvanced: true));
        Assert.True(state.TryGetAcceptedTeleportDestination(
            out RuntimeTeleportDestination accepted));
        Assert.Equal(11f, accepted.Position.Frame.Origin.X);
        Assert.Equal(OtherCell, accepted.CellId);
    }

    [Fact]
    public void PositionBeforeF751IsBufferedAndEqualFollowupCannotReplaceIt()
    {
        var state = new RuntimeWorldTransitState();

        Assert.False(state.OfferTeleportDestination(
            Destination(OutdoorCell, 11, x: 110f),
            teleportTimestampAdvanced: true));
        Assert.Equal(1, state.BufferedTeleportDestinationCount);
        Assert.True(state.TryQueueTeleportStart(11));
        Assert.True(state.ActivateQueuedTeleport());
        Assert.True(state.TryGetAcceptedTeleportDestination(
            out RuntimeTeleportDestination buffered));
        Assert.Equal(110f, buffered.Position.Frame.Origin.X);

        Assert.False(state.OfferTeleportDestination(
            Destination(OtherCell, 11, x: 111f),
            teleportTimestampAdvanced: false));
        Assert.True(state.TryGetAcceptedTeleportDestination(out buffered));
        Assert.Equal(OutdoorCell, buffered.CellId);
    }

    [Fact]
    public void InactiveOrdinaryPositionIsNeverBuffered()
    {
        var state = new RuntimeWorldTransitState();

        Assert.False(state.OfferTeleportDestination(
            Destination(OutdoorCell, 11),
            teleportTimestampAdvanced: false));
        Assert.Equal(0, state.BufferedTeleportDestinationCount);
        Assert.True(state.TryQueueTeleportStart(11));
        Assert.True(state.ActivateQueuedTeleport());
        Assert.False(state.HasAcceptedTeleportDestination);
    }

    [Fact]
    public void DuplicateStartIsRejectedButSequenceWrapIsFresh()
    {
        var state = new RuntimeWorldTransitState();
        Assert.True(state.TryQueueTeleportStart(ushort.MaxValue));
        Assert.True(state.ActivateQueuedTeleport());
        state.EndTeleport();

        Assert.False(state.CanQueueTeleportStart(ushort.MaxValue));
        Assert.False(state.TryQueueTeleportStart(ushort.MaxValue));
        Assert.True(state.CanQueueTeleportStart(0));
        Assert.True(state.TryQueueTeleportStart(0));
        Assert.True(state.HasPendingTeleportStart);
    }

    [Fact]
    public void ExactHalfRangeUsesRetailLowerStampTieBreak()
    {
        var state = new RuntimeWorldTransitState();
        Assert.True(state.TryQueueTeleportStart(0x8000));
        Assert.True(state.ActivateQueuedTeleport());
        state.EndTeleport();

        Assert.True(state.CanQueueTeleportStart(0x0000));
        Assert.True(state.TryQueueTeleportStart(0x0000));
    }

    [Fact]
    public void PendingStartAcceptsEqualPositionUntilHostCanActivate()
    {
        var state = new RuntimeWorldTransitState();
        Assert.True(state.TryQueueTeleportStart(11));

        Assert.False(state.OfferTeleportDestination(
            Destination(OutdoorCell, 11, x: 110f),
            teleportTimestampAdvanced: false));
        Assert.True(state.ActivateQueuedTeleport());
        Assert.True(state.TryGetAcceptedTeleportDestination(
            out RuntimeTeleportDestination buffered));

        Assert.Equal(110f, buffered.Position.Frame.Origin.X);
        Assert.True(state.IsTeleportActive);
    }

    [Fact]
    public void AdvancingPositionPrunesSkippedDestination()
    {
        var state = new RuntimeWorldTransitState();

        Assert.False(state.OfferTeleportDestination(
            Destination(OutdoorCell, 11),
            teleportTimestampAdvanced: true));
        Assert.False(state.OfferTeleportDestination(
            Destination(OtherCell, 12),
            teleportTimestampAdvanced: true));
        Assert.Equal(1, state.BufferedTeleportDestinationCount);
        Assert.True(state.TryQueueTeleportStart(12));
        Assert.True(state.ActivateQueuedTeleport());
        Assert.True(state.TryGetAcceptedTeleportDestination(
            out RuntimeTeleportDestination buffered));

        Assert.Equal(OtherCell, buffered.CellId);
    }

    [Fact]
    public void ObsoleteDestinationCannotSurviveSequenceWrap()
    {
        var state = new RuntimeWorldTransitState();

        Assert.False(state.OfferTeleportDestination(
            Destination(OutdoorCell, ushort.MaxValue),
            teleportTimestampAdvanced: true));
        Assert.False(state.OfferTeleportDestination(
            Destination(OtherCell, 0),
            teleportTimestampAdvanced: true));
        Assert.True(state.TryQueueTeleportStart(0));
        Assert.True(state.ActivateQueuedTeleport());
        Assert.True(state.TryGetAcceptedTeleportDestination(
            out RuntimeTeleportDestination wrapped));
        Assert.Equal(OtherCell, wrapped.CellId);
        state.EndTeleport();

        Assert.True(state.TryQueueTeleportStart(0x7FFF));
        Assert.True(state.ActivateQueuedTeleport());
        state.EndTeleport();
        Assert.True(state.TryQueueTeleportStart(0xFFFE));
        Assert.True(state.ActivateQueuedTeleport());
        state.EndTeleport();
        Assert.True(state.TryQueueTeleportStart(ushort.MaxValue));
        Assert.True(state.ActivateQueuedTeleport());

        Assert.False(state.HasAcceptedTeleportDestination);
    }

    [Fact]
    public void PortalRevealClaimsOnlyExactAcceptedSequenceAndCell()
    {
        var state = new RuntimeWorldTransitState();
        Assert.True(state.TryQueueTeleportStart(7));
        Assert.True(state.ActivateQueuedTeleport());
        Assert.True(state.OfferTeleportDestination(
            Destination(OutdoorCell, 7),
            teleportTimestampAdvanced: true));

        Assert.False(state.TryBeginPortalReveal(
            8,
            OutdoorCell,
            out _));
        Assert.False(state.TryBeginPortalReveal(
            7,
            OtherCell,
            out _));
        Assert.Equal(RuntimePortalSnapshot.Idle.Generation, state.Snapshot.Generation);
        Assert.True(state.HasAcceptedTeleportDestination);

        Assert.True(state.TryBeginPortalReveal(
            7,
            OutdoorCell,
            out long generation));
        Assert.True(generation > 0);
        Assert.False(state.HasAcceptedTeleportDestination);
        Assert.True(state.CanPlacePortalDestination(
            generation,
            7,
            OutdoorCell));
        Assert.False(state.CanPlacePortalDestination(
            generation,
            8,
            OutdoorCell));
        Assert.False(state.CanPlacePortalDestination(
            generation,
            7,
            OtherCell));
    }

    [Fact]
    public void MaterializationRequiresExactGenerationSequenceAndCell()
    {
        var state = new RuntimeWorldTransitState();
        long generation = BeginPortal(state, OutdoorCell, sequence: 17);
        Assert.True(state.AcknowledgeDestinationReadiness(
            Ready(generation, OutdoorCell)));

        Assert.False(state.AcknowledgePortalMaterialized(
            generation + 1,
            17,
            OutdoorCell));
        Assert.False(state.AcknowledgePortalMaterialized(
            generation,
            18,
            OutdoorCell));
        Assert.False(state.AcknowledgePortalMaterialized(
            generation,
            17,
            OtherCell));
        Assert.False(state.Snapshot.Materialized);

        Assert.True(state.AcknowledgePortalMaterialized(
            generation,
            17,
            OutdoorCell));
        Assert.True(state.Snapshot.Materialized);
    }

    [Fact]
    public void ResetSessionClearsCorrelationHistoryAndBufferedDestinations()
    {
        var state = new RuntimeWorldTransitState();
        Assert.False(state.OfferTeleportDestination(
            Destination(OutdoorCell, 7),
            teleportTimestampAdvanced: true));
        Assert.True(state.TryQueueTeleportStart(8));

        state.ResetSession();

        Assert.False(state.IsTeleportActive);
        Assert.False(state.HasPendingTeleportStart);
        Assert.False(state.HasAcceptedTeleportDestination);
        Assert.Equal(0, state.BufferedTeleportDestinationCount);
        Assert.True(state.TryQueueTeleportStart(8));
    }

    [Fact]
    public void HostProjection_TracksExactGenerationScopedAcknowledgementSuffix()
    {
        var state = new RuntimeWorldTransitState();
        long generation = BeginPortal(state, OutdoorCell);
        RuntimeWorldHostProjectionToken host =
            RegisterHost(state, generation, OutdoorCell);

        Assert.Equal(
            new RuntimeWorldTransitOwnershipSnapshot(
                BufferedTeleportDestinationCount: 0,
                PendingTeleportStartCount: 0,
                ActiveTeleportCount: 1,
                AcceptedTeleportDestinationCount: 0,
                ActiveRevealCount: 1,
                PendingDestinationReadinessCount: 1,
                HostProjectionCount: 1,
                PendingHostAcknowledgementCount: 1),
            state.Ownership);
        Assert.False(Acknowledge(
            state,
            host with { Generation = generation + 1 },
            RuntimeWorldHostAcknowledgementStage.ProjectionRegistered));
        Assert.False(Acknowledge(
            state,
            host with { DestinationCell = OtherCell },
            RuntimeWorldHostAcknowledgementStage.ProjectionRegistered));
        Assert.False(Acknowledge(
            state,
            host,
            RuntimeWorldHostAcknowledgementStage.None));
        Assert.False(Acknowledge(
            state,
            host,
            RuntimeWorldHostAcknowledgementStage.ProjectionRegistered
            | RuntimeWorldHostAcknowledgementStage
                .DestinationReservationReleased));
        Assert.True(Acknowledge(
            state,
            host,
            RuntimeWorldHostAcknowledgementStage.ProjectionRegistered));

        Assert.True(state.AcknowledgeDestinationReadiness(
            Ready(generation, OutdoorCell)));
        Assert.True(state.AcknowledgePortalMaterialized(
            generation,
            1,
            OutdoorCell));
        Assert.Equal(1, state.Ownership.PendingHostAcknowledgementCount);
        Assert.True(Acknowledge(
            state,
            host,
            RuntimeWorldHostAcknowledgementStage
                .SimulationReleaseProjected));

        Assert.True(state.RequireDestinationReservationRelease(host));
        Assert.True(Acknowledge(
            state,
            host,
            RuntimeWorldHostAcknowledgementStage
                .DestinationReservationReleased));
        Assert.True(state.AcknowledgeWorldViewportVisible(generation));
        Assert.True(state.Complete(generation));
        Assert.Equal(1, state.Ownership.PendingHostAcknowledgementCount);
        Assert.True(Acknowledge(
            state,
            host,
            RuntimeWorldHostAcknowledgementStage.TerminalProjected));
        Assert.Equal(0, state.Ownership.HostProjectionCount);
        Assert.Equal(0, state.Ownership.PendingHostAcknowledgementCount);

        state.EndTeleport();
        state.ResetSession();
        Assert.True(state.Ownership.IsSessionIdle);
    }

    [Fact]
    public void Cancel_ReplacesIncompleteRegistrationWithRetryableWithdrawal()
    {
        var state = new RuntimeWorldTransitState();
        long generation = state.BeginLoginReveal(OutdoorCell);
        RuntimeWorldHostProjectionToken host =
            RegisterHost(state, generation, OutdoorCell);

        Assert.True(state.Cancel(generation));
        Assert.True(state.TryGetHostProjection(
            host,
            out RuntimeWorldHostProjectionSnapshot projection));
        Assert.Equal(
            RuntimeWorldHostAcknowledgementStage
                .SimulationReleaseProjected
            | RuntimeWorldHostAcknowledgementStage
                .DestinationReservationReleased
            | RuntimeWorldHostAcknowledgementStage.TerminalProjected,
            projection.PendingAcknowledgements);
        Assert.False(Acknowledge(
            state,
            host,
            RuntimeWorldHostAcknowledgementStage.TerminalProjected));
        Assert.True(Acknowledge(
            state,
            host,
            RuntimeWorldHostAcknowledgementStage
                .SimulationReleaseProjected));
        Assert.True(Acknowledge(
            state,
            host,
            RuntimeWorldHostAcknowledgementStage
                .DestinationReservationReleased));
        Assert.True(Acknowledge(
            state,
            host,
            RuntimeWorldHostAcknowledgementStage.TerminalProjected));

        state.ResetSession();
        Assert.True(state.Ownership.IsSessionIdle);
    }

    [Fact]
    public void Supersession_PreservesOldHostWithdrawalByExactGeneration()
    {
        var state = new RuntimeWorldTransitState();
        long first = state.BeginLoginReveal(OutdoorCell);
        RuntimeWorldHostProjectionToken firstHost =
            RegisterHost(state, first, OutdoorCell);
        Assert.True(Acknowledge(
            state,
            firstHost,
            RuntimeWorldHostAcknowledgementStage.ProjectionRegistered));

        long second = BeginPortal(state, OtherCell, sequence: 2);

        Assert.True(state.TryGetHostProjection(
            firstHost,
            out RuntimeWorldHostProjectionSnapshot superseded));
        Assert.True(superseded.IsSuperseding);
        Assert.Equal(
            RuntimeWorldHostAcknowledgementStage
                .DestinationReservationReleased
            | RuntimeWorldHostAcknowledgementStage.TerminalProjected,
            superseded.PendingAcknowledgements);
        Assert.False(Acknowledge(
            state,
            firstHost,
            RuntimeWorldHostAcknowledgementStage.TerminalProjected));
        Assert.True(Acknowledge(
            state,
            firstHost,
            RuntimeWorldHostAcknowledgementStage
                .DestinationReservationReleased));
        Assert.True(Acknowledge(
            state,
            firstHost,
            RuntimeWorldHostAcknowledgementStage.TerminalProjected));
        Assert.Equal(second, state.Snapshot.Generation);
        Assert.Equal(0, state.Ownership.HostProjectionCount);
    }

    [Fact]
    public void ResetSession_RejectsUnacknowledgedHostProjection()
    {
        var state = new RuntimeWorldTransitState();
        long generation = state.BeginLoginReveal(OutdoorCell);
        RuntimeWorldHostProjectionToken host =
            RegisterHost(state, generation, OutdoorCell);

        InvalidOperationException error =
            Assert.Throws<InvalidOperationException>(state.ResetSession);
        Assert.Contains("hosts=1", error.Message);
        Assert.Equal(generation, state.Snapshot.Generation);

        Assert.True(state.Cancel(generation));
        Assert.True(Acknowledge(
            state,
            host,
            RuntimeWorldHostAcknowledgementStage
                .SimulationReleaseProjected));
        Assert.True(Acknowledge(
            state,
            host,
            RuntimeWorldHostAcknowledgementStage
                .DestinationReservationReleased));
        Assert.True(Acknowledge(
            state,
            host,
            RuntimeWorldHostAcknowledgementStage.TerminalProjected));
        state.ResetSession();
    }


    [Fact]
    public void LogoutLifecycle_RequestHoldPresentationConfirmComplete()
    {
        var state = new RuntimeWorldTransitState();
        Assert.Equal(RuntimeLogoutStage.None, state.LogoutStage);

        Assert.True(state.TryBeginLogoutRequest(isPlayerKiller: false));
        Assert.Equal(RuntimeLogoutStage.Requested, state.LogoutStage);
        Assert.Equal(1, state.CaptureOwnership().ActiveLogoutCount);
        Assert.False(state.CaptureOwnership().IsSessionIdle);

        // A second request while one is in flight refuses.
        Assert.False(state.TryBeginLogoutRequest(isPlayerKiller: false));

        Assert.False(state.AdvanceLogoutHold(2.9d));
        Assert.Equal(RuntimeLogoutStage.Requested, state.LogoutStage);
        Assert.True(state.AdvanceLogoutHold(0.2d));
        Assert.Equal(
            RuntimeLogoutStage.PresentationActive,
            state.LogoutStage);
        // The begin edge fires exactly once.
        Assert.False(state.AdvanceLogoutHold(1.0d));

        Assert.True(state.AcknowledgeLogoutConfirmed());
        Assert.Equal(RuntimeLogoutStage.Confirmed, state.LogoutStage);
        Assert.False(state.AcknowledgeLogoutConfirmed());

        Assert.True(state.CompleteLogout());
        Assert.Equal(RuntimeLogoutStage.None, state.LogoutStage);
        Assert.False(state.CompleteLogout());
        Assert.True(state.CaptureOwnership().IsSessionIdle);
    }

    [Fact]
    public void LogoutHold_PlayerKillerAddsTwentySeconds()
    {
        var state = new RuntimeWorldTransitState();
        Assert.True(state.TryBeginLogoutRequest(isPlayerKiller: true));

        Assert.False(state.AdvanceLogoutHold(3.5d));
        Assert.False(state.AdvanceLogoutHold(19.0d));
        Assert.True(state.AdvanceLogoutHold(0.6d));
        Assert.Equal(
            RuntimeLogoutStage.PresentationActive,
            state.LogoutStage);
    }

    [Fact]
    public void LogoutConfirmation_LegalFromTheRequestHold()
    {
        var state = new RuntimeWorldTransitState();
        Assert.True(state.TryBeginLogoutRequest(isPlayerKiller: false));
        Assert.True(state.AcknowledgeLogoutConfirmed());
        Assert.Equal(RuntimeLogoutStage.Confirmed, state.LogoutStage);
        Assert.False(state.AdvanceLogoutHold(10.0d));
        Assert.True(state.CompleteLogout());
    }

    [Fact]
    public void LogoutRequest_RefusedDuringTeleportAndCancelRollsBack()
    {
        var state = new RuntimeWorldTransitState();
        _ = BeginPortal(state, OutdoorCell, sequence: 3);
        Assert.False(state.TryBeginLogoutRequest(isPlayerKiller: false));

        var idle = new RuntimeWorldTransitState();
        Assert.True(idle.TryBeginLogoutRequest(isPlayerKiller: false));
        Assert.True(idle.CancelLogoutRequest());
        Assert.Equal(RuntimeLogoutStage.None, idle.LogoutStage);
        Assert.True(idle.CaptureOwnership().IsSessionIdle);
        Assert.False(idle.CancelLogoutRequest());
    }

    [Fact]
    public void LogoutLifecycle_ClearsOnSessionReset()
    {
        var state = new RuntimeWorldTransitState();
        Assert.True(state.TryBeginLogoutRequest(isPlayerKiller: false));
        state.ResetSession();
        Assert.Equal(RuntimeLogoutStage.None, state.LogoutStage);
        Assert.True(state.CaptureOwnership().IsSessionIdle);
        // A fresh session can log out again.
        Assert.True(state.TryBeginLogoutRequest(isPlayerKiller: false));
    }

    private static long BeginPortal(
        RuntimeWorldTransitState state,
        uint cell,
        ushort sequence = 1)
    {
        Assert.True(state.TryQueueTeleportStart(sequence));
        Assert.True(state.ActivateQueuedTeleport());
        Assert.True(state.OfferTeleportDestination(
            Destination(cell, sequence),
            teleportTimestampAdvanced: true));
        Assert.True(state.TryBeginPortalReveal(
            sequence,
            cell,
            out long generation));
        return generation;
    }

    private static RuntimeWorldHostProjectionToken RegisterHost(
        RuntimeWorldTransitState state,
        long generation,
        uint cell)
    {
        Assert.True(state.TryRegisterHostProjection(
            generation,
            cell,
            out RuntimeWorldHostProjectionToken host));
        return host;
    }

    private static bool Acknowledge(
        RuntimeWorldTransitState state,
        RuntimeWorldHostProjectionToken host,
        RuntimeWorldHostAcknowledgementStage stage) =>
        state.AcknowledgeHostProjection(
            new RuntimeWorldHostAcknowledgement(host, stage));

    private static RuntimeTeleportDestination Destination(
        uint cell,
        ushort sequence,
        float x = 1f) =>
        new(
            EntityGuid: 0x50000001u,
            InstanceSequence: 1,
            PositionSequence: 1,
            TeleportSequence: sequence,
            ForcePositionSequence: 1,
            Position: new Position(
                cell,
                new Vector3(x, 2f, 3f),
                Quaternion.Identity));

    private static RuntimeDestinationReadiness Ready(
        long generation,
        uint cell) =>
        new(
            generation,
            cell,
            IsIndoor: (cell & 0xFFFFu) >= 0x0100u,
            IsUnhydratable: false,
            RequiredRenderRadius: (cell & 0xFFFFu) >= 0x0100u ? 0 : 1,
            IsRenderNeighborhoodReady: true,
            AreCompositeTexturesReady: true,
            IsCollisionReady: true);
}
