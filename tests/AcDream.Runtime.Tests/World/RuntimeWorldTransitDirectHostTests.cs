using System.Numerics;
using AcDream.Core.Physics;
using AcDream.Runtime.World;

namespace AcDream.Runtime.Tests.World;

public sealed class RuntimeWorldTransitDirectHostTests
{
    private static readonly string[] ForbiddenHostAssemblyPrefixes =
    [
        "AcDream.App",
        "AcDream.UI.",
        "Silk.NET",
        "OpenAL",
        "Arch",
        "ImGui",
    ];

    private const uint OutdoorCell = 0x11340021u;
    private const uint IndoorCell = 0x8A020164u;

    [Fact]
    public void DirectHost_CompletesLoginPortalCancellationAndReset()
    {
        string[] loadedAssemblies = AppDomain.CurrentDomain.GetAssemblies()
            .Select(static assembly =>
                assembly.GetName().Name ?? string.Empty)
            .ToArray();
        Assert.DoesNotContain(
            loadedAssemblies,
            name => ForbiddenHostAssemblyPrefixes.Any(
                prefix => name.StartsWith(
                    prefix,
                    StringComparison.OrdinalIgnoreCase)));

        var state = new RuntimeWorldTransitState();
        var host = new DirectHost(state);

        host.CompleteLogin(OutdoorCell);
        Assert.True(state.Snapshot.Completed);
        Assert.True(state.Snapshot.WorldViewportObserved);
        Assert.Equal(0, state.Ownership.HostProjectionCount);

        state.ResetSession();
        host.CompletePortal(IndoorCell, sequence: 1);
        Assert.True(state.Snapshot.Completed);
        Assert.True(state.Snapshot.Materialized);
        Assert.Equal(1, state.Snapshot.PortalMaterializationCount);
        Assert.Equal(0, state.Ownership.PendingHostAcknowledgementCount);

        state.EndTeleport();
        state.ResetSession();
        host.CancelLogin(OutdoorCell);
        Assert.True(state.Snapshot.Cancelled);
        Assert.Equal(0, state.Ownership.HostProjectionCount);

        state.ResetSession();
        Assert.Equal(RuntimePortalSnapshot.Idle, state.Snapshot);
        Assert.True(state.Ownership.IsSessionIdle);
    }

    private sealed class DirectHost(RuntimeWorldTransitState state)
    {
        public void CompleteLogin(uint cell)
        {
            long generation = state.BeginLoginReveal(cell);
            RuntimeWorldHostProjectionToken host =
                Register(generation, cell);
            Acknowledge(
                host,
                RuntimeWorldHostAcknowledgementStage.ProjectionRegistered);
            Assert.True(state.AcknowledgeDestinationReadiness(
                Ready(generation, cell)));
            Assert.True(state.Complete(generation));
            DrainPending(host);
            Assert.True(state.AcknowledgeWorldViewportVisible(generation));
        }

        public void CompletePortal(uint cell, ushort sequence)
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
            RuntimeWorldHostProjectionToken host =
                Register(generation, cell);
            Acknowledge(
                host,
                RuntimeWorldHostAcknowledgementStage.ProjectionRegistered);
            Assert.True(state.AcknowledgeDestinationReadiness(
                Ready(generation, cell)));
            Assert.True(state.AcknowledgePortalMaterialized(
                generation,
                sequence,
                cell));
            DrainPending(host);
            Assert.True(state.RequireDestinationReservationRelease(host));
            DrainPending(host);
            Assert.True(state.AcknowledgeWorldViewportVisible(generation));
            Assert.True(state.Complete(generation));
            DrainPending(host);
        }

        public void CancelLogin(uint cell)
        {
            long generation = state.BeginLoginReveal(cell);
            RuntimeWorldHostProjectionToken host =
                Register(generation, cell);
            Acknowledge(
                host,
                RuntimeWorldHostAcknowledgementStage.ProjectionRegistered);
            Assert.True(state.Cancel(generation));
            DrainPending(host);
        }

        private RuntimeWorldHostProjectionToken Register(
            long generation,
            uint cell)
        {
            Assert.True(state.TryRegisterHostProjection(
                generation,
                cell,
                out RuntimeWorldHostProjectionToken host));
            return host;
        }

        private void DrainPending(RuntimeWorldHostProjectionToken host)
        {
            while (state.TryGetHostProjection(
                host,
                out RuntimeWorldHostProjectionSnapshot projection))
            {
                RuntimeWorldHostAcknowledgementStage pending =
                    projection.PendingAcknowledgements;
                if ((pending & RuntimeWorldHostAcknowledgementStage
                        .SimulationReleaseProjected) != 0)
                {
                    Acknowledge(
                        host,
                        RuntimeWorldHostAcknowledgementStage
                            .SimulationReleaseProjected);
                    continue;
                }
                if ((pending & RuntimeWorldHostAcknowledgementStage
                        .DestinationReservationReleased) != 0)
                {
                    Acknowledge(
                        host,
                        RuntimeWorldHostAcknowledgementStage
                            .DestinationReservationReleased);
                    continue;
                }
                if ((pending & RuntimeWorldHostAcknowledgementStage
                        .TerminalProjected) != 0)
                {
                    Acknowledge(
                        host,
                        RuntimeWorldHostAcknowledgementStage
                            .TerminalProjected);
                    continue;
                }
                break;
            }
        }

        private void Acknowledge(
            RuntimeWorldHostProjectionToken host,
            RuntimeWorldHostAcknowledgementStage stage) =>
            Assert.True(state.AcknowledgeHostProjection(
                new RuntimeWorldHostAcknowledgement(host, stage)));
    }

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

    private static RuntimeTeleportDestination Destination(
        uint cell,
        ushort sequence) =>
        new(
            EntityGuid: 0x50000001u,
            InstanceSequence: 1,
            PositionSequence: 1,
            TeleportSequence: sequence,
            ForcePositionSequence: 1,
            Position: new Position(
                cell,
                Vector3.Zero,
                Quaternion.Identity));
}
