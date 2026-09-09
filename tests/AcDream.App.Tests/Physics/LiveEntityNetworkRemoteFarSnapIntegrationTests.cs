using System.Numerics;
using AcDream.App.Physics;
using AcDream.Core.Net.Messages;
using AcDream.Core.Physics;
using AcDream.Runtime;
using AcDream.Runtime.Entities;
using AcDream.Runtime.Physics;
using AcDream.Runtime.Session;

namespace AcDream.App.Tests.Physics;

public sealed class LiveEntityNetworkRemoteFarSnapIntegrationTests
{
    private static readonly Vector3 Destination = new(12f, 14f, 7f);

    private static readonly Vector3 DecoyWirePose = new(-70f, -80f, -90f);

    [Fact]
    public void FarSnap_PlacesTheBodyFromTheCanonicalDestination_NotTheCallersWirePose()
    {
        using var fixture = new RemotePlacementDriveFixture();
        fixture.PublishDestinationCollision();
        fixture.AllowDestination();
        (RuntimeEntityRecord record, RemoteMotion remote, PhysicsBody body) =
            fixture.AddRemote(0x70004001u, Destination);

        LiveEntityNetworkUpdateController.RemoteContactRouting routing =
            LiveEntityNetworkUpdateController.ApplyRemoteContactRouting(
                fixture.Drive,
                record,
                remote,
                Classify(hasContact: true, playerDistance: 200f),
                DecoyWirePose,
                Quaternion.Identity,
                willBeDrTicked: true,
                runTeleportHook: () => true);

        Assert.Equal(
            LiveEntityNetworkUpdateController.RemoteContactArm.FarSnapPlacement,
            routing.Arm);
        Assert.Equal(
            RuntimeRemotePlacementExecutionStatus.Committed,
            routing.Placement);
        Assert.Equal(
            Destination + RemotePlacementDriveFixture.DestinationWorldOffset,
            body.Position);
        Assert.NotEqual(DecoyWirePose, body.Position);
        Assert.Equal(RemotePlacementDriveFixture.DestinationCell, record.FullCellId);

        fixture.DrainPlacementFifo();
        Assert.Equal(0, fixture.LiveOperationCount);
        Assert.Equal(0, fixture.RemotePlacementLedger);
    }

    [Fact]
    public void FarSnap_ClearsTheInterpolationQueue()
    {
        using var fixture = new RemotePlacementDriveFixture();
        fixture.PublishDestinationCollision();
        fixture.AllowDestination();
        (RuntimeEntityRecord record, RemoteMotion remote, PhysicsBody body) =
            fixture.AddRemote(0x70004002u, Destination);
        remote.Interp.Enqueue(
            new Vector3(40f, 40f, 7f),
            Quaternion.Identity,
            isMovingTo: false,
            currentBodyPosition: body.Position,
            currentBodyOrientation: body.Orientation);
        Assert.True(remote.Interp.IsActive);

        _ = LiveEntityNetworkUpdateController.ApplyRemoteContactRouting(
            fixture.Drive,
            record,
            remote,
            Classify(hasContact: true, playerDistance: 200f),
            DecoyWirePose,
            Quaternion.Identity,
            willBeDrTicked: true,
            runTeleportHook: () => true);

        Assert.False(remote.Interp.IsActive);
        fixture.DrainPlacementFifo();
    }

    [Fact]
    public void FarSnap_RefusedDestination_StillAdvancesTheBodyToTheAcceptedDestination()
    {
        using var fixture = new RemotePlacementDriveFixture();
        fixture.PublishDestinationCollision();
        // Deliberately NOT AllowDestination().
        (RuntimeEntityRecord record, RemoteMotion remote, PhysicsBody body) =
            fixture.AddRemote(0x70004003u, Destination);
        Vector3 before = body.Position;

        LiveEntityNetworkUpdateController.RemoteContactRouting routing =
            LiveEntityNetworkUpdateController.ApplyRemoteContactRouting(
                fixture.Drive,
                record,
                remote,
                Classify(hasContact: true, playerDistance: 200f),
                DecoyWirePose,
                Quaternion.Identity,
                willBeDrTicked: true,
                runTeleportHook: () => true);

        Assert.Equal(
            LiveEntityNetworkUpdateController.RemoteContactArm.FarSnapPlacement,
            routing.Arm);
        Assert.Equal(
            RuntimeRemotePlacementExecutionStatus.Refused,
            routing.Placement);
        Assert.Equal(
            Destination + RemotePlacementDriveFixture.DestinationWorldOffset,
            body.Position);
        Assert.NotEqual(before, body.Position);
        Assert.NotEqual(DecoyWirePose, body.Position);
        Assert.Equal(RemotePlacementDriveFixture.SourceCell, record.FullCellId);
        Assert.True(body.InWorld);
        Assert.True(record.ObjectClock.IsActive);
        Assert.Equal(0, fixture.LiveOperationCount);
        Assert.Equal(0, fixture.RemotePlacementLedger);
    }

    [Fact]
    public void FarSnap_RepeatedRefusals_KeepTrackingEveryPacket()
    {
        using var fixture = new RemotePlacementDriveFixture();
        fixture.PublishDestinationCollision();
        (RuntimeEntityRecord record, RemoteMotion remote, PhysicsBody body) =
            fixture.AddRemote(0x7000400Cu, Destination);

        foreach (Vector3 step in new[]
        {
            new Vector3(20f, 14f, 7f),
            new Vector3(28f, 14f, 7f),
            new Vector3(36f, 14f, 7f),
        })
        {
            record.Snapshot = record.Snapshot with
            {
                Position = new CreateObject.ServerPosition(
                    RemotePlacementDriveFixture.DestinationCell,
                    step.X,
                    step.Y,
                    step.Z,
                    1f,
                    0f,
                    0f,
                    0f),
            };

            LiveEntityNetworkUpdateController.RemoteContactRouting routing =
                LiveEntityNetworkUpdateController.ApplyRemoteContactRouting(
                    fixture.Drive,
                    record,
                    remote,
                    Classify(hasContact: true, playerDistance: 200f),
                    DecoyWirePose,
                    Quaternion.Identity,
                    willBeDrTicked: true,
                    runTeleportHook: () => true);

            Assert.Equal(
                RuntimeRemotePlacementExecutionStatus.Refused,
                routing.Placement);
            Assert.Equal(
                step + RemotePlacementDriveFixture.DestinationWorldOffset,
                body.Position);
        }

        Assert.Equal(0, fixture.LiveOperationCount);
        Assert.Equal(0, fixture.RemotePlacementLedger);
    }


    [Fact]
    public void NoClassificationAtAll_StillTracksTheServer()
    {
        using var fixture = new RemotePlacementDriveFixture();
        (RuntimeEntityRecord record, RemoteMotion remote, PhysicsBody body) =
            fixture.AddRemote(0x70004004u, Destination);
        var target = new Vector3(60f, 10f, 7f); // 50 m from the body.

        LiveEntityNetworkUpdateController.RemoteContactRouting routing =
            LiveEntityNetworkUpdateController.ApplyRemoteContactRouting(
                fixture.Drive,
                record,
                remote,
                route: null,
                target,
                Quaternion.Identity,
                willBeDrTicked: true,
                runTeleportHook: () => true);

        Assert.Equal(
            LiveEntityNetworkUpdateController.RemoteContactArm.UnroutedCatchUp,
            routing.Arm);
        Assert.Equal(target, body.Position);
        Assert.Equal(0, fixture.LiveOperationCount);
    }

    [Fact]
    public void NoClassificationAtAll_NearAndTicked_EnqueuesInsteadOfSnapping()
    {
        using var fixture = new RemotePlacementDriveFixture();
        (RuntimeEntityRecord record, RemoteMotion remote, PhysicsBody body) =
            fixture.AddRemote(0x70004005u, Destination);
        Vector3 before = body.Position;
        var target = before + new Vector3(0.5f, 0f, 0f);

        LiveEntityNetworkUpdateController.RemoteContactRouting routing =
            LiveEntityNetworkUpdateController.ApplyRemoteContactRouting(
                fixture.Drive,
                record,
                remote,
                route: null,
                target,
                Quaternion.Identity,
                willBeDrTicked: true,
                runTeleportHook: () => true);

        Assert.Equal(
            LiveEntityNetworkUpdateController.RemoteContactArm.UnroutedCatchUp,
            routing.Arm);
        Assert.Equal(before, body.Position);
        Assert.True(remote.Interp.IsActive);
    }

    [Fact]
    public void RejectedData_TakesTheUnroutedCatchUp_NotTheFarSnap()
    {
        using var fixture = new RemotePlacementDriveFixture();
        fixture.PublishDestinationCollision();
        fixture.AllowDestination();
        (RuntimeEntityRecord record, RemoteMotion remote, PhysicsBody body) =
            fixture.AddRemote(0x70004006u, Destination);
        var target = new Vector3(60f, 10f, 7f);

        RuntimeAuthoritativePositionRoute rejected =
            Classify(hasContact: true, playerDistance: float.NaN);
        Assert.Equal(
            RuntimeAuthoritativePositionDisposition.RejectedData,
            rejected.Disposition);

        LiveEntityNetworkUpdateController.RemoteContactRouting routing =
            LiveEntityNetworkUpdateController.ApplyRemoteContactRouting(
                fixture.Drive,
                record,
                remote,
                rejected,
                target,
                Quaternion.Identity,
                willBeDrTicked: true,
                runTeleportHook: () => true);

        Assert.Equal(
            LiveEntityNetworkUpdateController.RemoteContactArm.UnroutedCatchUp,
            routing.Arm);
        Assert.Equal(target, body.Position);
        Assert.Equal(0, fixture.LiveOperationCount);
    }

    [Fact]
    public void CellLessRemote_TakesTheTeleportArm_RunsTheHookAndPlacesCanonically()
    {
        using var fixture = new RemotePlacementDriveFixture();
        fixture.PublishDestinationCollision();
        fixture.AllowDestination();
        (RuntimeEntityRecord record, RemoteMotion remote, PhysicsBody body) =
            fixture.AddRemote(0x70004007u, Destination);
        var target = new Vector3(60f, 10f, 7f);

        RuntimeAuthoritativePositionRoute cellLess = Classify(
            hasContact: true,
            playerDistance: 200f,
            committedCellId: 0u);
        Assert.Equal(
            RuntimeAuthoritativePositionDisposition.SetPosition,
            cellLess.Disposition);

        int hookCalls = 0;
        LiveEntityNetworkUpdateController.RemoteContactRouting routing =
            LiveEntityNetworkUpdateController.ApplyRemoteContactRouting(
                fixture.Drive,
                record,
                remote,
                cellLess,
                target,
                Quaternion.Identity,
                willBeDrTicked: true,
                runTeleportHook: () =>
                {
                    hookCalls++;
                    return true;
                });

        Assert.Equal(1, hookCalls);
        Assert.Equal(
            LiveEntityNetworkUpdateController.RemoteContactArm.TeleportPlacement,
            routing.Arm);
        Assert.Equal(
            RuntimeRemotePlacementExecutionStatus.Committed,
            routing.Placement);
        Assert.Equal(
            Destination + RemotePlacementDriveFixture.DestinationWorldOffset,
            body.Position);
        Assert.NotEqual(target, body.Position);
        Assert.Equal(RemotePlacementDriveFixture.DestinationCell, record.FullCellId);

        fixture.DrainPlacementFifo();
        Assert.Equal(0, fixture.LiveOperationCount);
        Assert.Equal(0, fixture.RemotePlacementLedger);
    }

    [Fact]
    public void AirborneBody_OutranksTheFarSnap()
    {
        using var fixture = new RemotePlacementDriveFixture();
        fixture.PublishDestinationCollision();
        fixture.AllowDestination();
        (RuntimeEntityRecord record, RemoteMotion remote, PhysicsBody body) =
            fixture.AddRemote(0x70004008u, Destination);
        remote.Airborne = true;
        remote.Body.TransientState = TransientStateFlags.Active;
        var target = new Vector3(60f, 10f, 7f);

        LiveEntityNetworkUpdateController.RemoteContactRouting routing =
            LiveEntityNetworkUpdateController.ApplyRemoteContactRouting(
                fixture.Drive,
                record,
                remote,
                Classify(hasContact: true, playerDistance: 200f),
                target,
                Quaternion.Identity,
                willBeDrTicked: true,
                runTeleportHook: () => true);

        Assert.Equal(
            LiveEntityNetworkUpdateController.RemoteContactArm.AirborneSnap,
            routing.Arm);
        Assert.Equal(target, body.Position);
        Assert.Equal(0, fixture.LiveOperationCount);
    }

    [Fact]
    public void AirborneBody_TeleportOutranksAirborneSnap()
    {
        using var fixture = new RemotePlacementDriveFixture();
        fixture.PublishDestinationCollision();
        fixture.AllowDestination();
        (RuntimeEntityRecord record, RemoteMotion remote, PhysicsBody body) =
            fixture.AddRemote(0x7000400Cu, Destination);
        remote.Airborne = true;
        remote.Body.TransientState = TransientStateFlags.Active;
        var target = new Vector3(60f, 10f, 7f);

        RuntimeAuthoritativePositionRoute cellLess = Classify(
            hasContact: false,
            playerDistance: 200f,
            committedCellId: 0u);
        Assert.Equal(
            RuntimeAuthoritativePositionDisposition.SetPosition,
            cellLess.Disposition);

        int hookCalls = 0;
        LiveEntityNetworkUpdateController.RemoteContactRouting routing =
            LiveEntityNetworkUpdateController.ApplyRemoteContactRouting(
                fixture.Drive,
                record,
                remote,
                cellLess,
                target,
                Quaternion.Identity,
                willBeDrTicked: true,
                runTeleportHook: () =>
                {
                    hookCalls++;
                    return true;
                });

        Assert.Equal(1, hookCalls);
        Assert.Equal(
            LiveEntityNetworkUpdateController.RemoteContactArm.TeleportPlacement,
            routing.Arm);
        Assert.Equal(
            RuntimeRemotePlacementExecutionStatus.Committed,
            routing.Placement);
        Assert.Equal(
            Destination + RemotePlacementDriveFixture.DestinationWorldOffset,
            body.Position);
        Assert.NotEqual(target, body.Position);
        Assert.Equal(RemotePlacementDriveFixture.DestinationCell, record.FullCellId);

        fixture.DrainPlacementFifo();
        Assert.Equal(0, fixture.LiveOperationCount);
        Assert.Equal(0, fixture.RemotePlacementLedger);
    }

    [Fact]
    public void TwoRemotesInTheSameTick_FarAndNear_DoNotContaminateEachOther()
    {
        using var fixture = new RemotePlacementDriveFixture();
        fixture.PublishDestinationCollision();
        fixture.AllowDestination();
        (RuntimeEntityRecord farRecord, RemoteMotion farRemote,
            PhysicsBody farBody) = fixture.AddRemote(0x70004009u, Destination);
        (RuntimeEntityRecord nearRecord, RemoteMotion nearRemote,
            PhysicsBody nearBody) = fixture.AddRemote(0x7000400Au, Destination);
        Vector3 nearBefore = nearBody.Position;

        Assert.Equal(
            LiveEntityNetworkUpdateController.RemoteContactArm.FarSnapPlacement,
            LiveEntityNetworkUpdateController.ApplyRemoteContactRouting(
                fixture.Drive,
                farRecord,
                farRemote,
                Classify(hasContact: true, playerDistance: 200f),
                DecoyWirePose,
                Quaternion.Identity,
                willBeDrTicked: true,
                runTeleportHook: () => true).Arm);
        Assert.Equal(
            LiveEntityNetworkUpdateController.RemoteContactArm
                .SteadyStateInterpolate,
            LiveEntityNetworkUpdateController.ApplyRemoteContactRouting(
                fixture.Drive,
                nearRecord,
                nearRemote,
                Classify(hasContact: true, playerDistance: 10f),
                nearBefore + new Vector3(0.5f, 0f, 0f),
                Quaternion.Identity,
                willBeDrTicked: true,
                runTeleportHook: () => true).Arm);

        Assert.Equal(
            Destination + RemotePlacementDriveFixture.DestinationWorldOffset,
            farBody.Position);
        Assert.Equal(nearBefore, nearBody.Position);

        fixture.DrainPlacementFifo();
        Assert.Equal(0, fixture.LiveOperationCount);
        Assert.Equal(0, fixture.RemotePlacementLedger);
    }

    [Fact]
    public void AirborneNoOperationClassification_IsRejectedByTheRoutingSeam()
    {
        using var fixture = new RemotePlacementDriveFixture();
        (RuntimeEntityRecord record, RemoteMotion remote, PhysicsBody body) =
            fixture.AddRemote(0x7000400Du, Destination);
        Vector3 before = body.Position;

        RuntimeAuthoritativePositionRoute airborne =
            Classify(hasContact: false, playerDistance: 200f);
        Assert.Equal(
            RuntimeAuthoritativePositionDisposition.NoPositionOperation,
            airborne.Disposition);

        Assert.Throws<InvalidOperationException>(() =>
            LiveEntityNetworkUpdateController.ApplyRemoteContactRouting(
                fixture.Drive,
                record,
                remote,
                airborne,
                new Vector3(60f, 10f, 7f),
                Quaternion.Identity,
                willBeDrTicked: true,
                runTeleportHook: () => true));

        Assert.Equal(before, body.Position);
        Assert.False(remote.Interp.IsActive);
        Assert.Equal(0, fixture.LiveOperationCount);
    }


    [Fact]
    public void WireCellAdoption_IsSuppressedAfterAFarSnapAndRunsForEveryOtherArm()
    {
        using var fixture = new RemotePlacementDriveFixture();
        (RuntimeEntityRecord record, RemoteMotion remote, _) =
            fixture.AddRemote(0x7000400Bu, Destination);
        uint resolved = remote.CellId;
        Assert.Equal(RemotePlacementDriveFixture.SourceCell, resolved);

        Assert.False(
            LiveEntityNetworkUpdateController.TryAdoptWireCellAfterRouting(
                remote,
                LiveEntityNetworkUpdateController.RemoteContactArm
                    .FarSnapPlacement,
                RemotePlacementDriveFixture.DestinationCell));
        Assert.Equal(resolved, remote.CellId);
        Assert.Equal(resolved, record.FullCellId);

        foreach (LiveEntityNetworkUpdateController.RemoteContactArm arm in
            new[]
            {
                LiveEntityNetworkUpdateController.RemoteContactArm.AirborneSnap,
                LiveEntityNetworkUpdateController.RemoteContactArm
                    .SteadyStateInterpolate,
                LiveEntityNetworkUpdateController.RemoteContactArm
                    .UnroutedCatchUp,
            })
        {
            remote.CellId = RemotePlacementDriveFixture.SourceCell;
            Assert.True(
                LiveEntityNetworkUpdateController.TryAdoptWireCellAfterRouting(
                    remote,
                    arm,
                    RemotePlacementDriveFixture.DestinationCell));
            Assert.Equal(
                RemotePlacementDriveFixture.DestinationCell, remote.CellId);
        }
    }

    private static RuntimeAuthoritativePositionRoute Classify(
        bool hasContact,
        float playerDistance,
        uint committedCellId = RemotePlacementDriveFixture.SourceCell) =>
        RuntimeAuthoritativePositionRouteClassifier.ClassifyAcceptedPosition(
            new RuntimeAcceptedPositionRouteRequest(
                new RuntimeAuthoritativePositionAuthority(
                    new RuntimeGenerationToken(7),
                    new RuntimeEntityKey(0x70000001u, 3),
                    PositionAuthorityVersion: 11UL,
                    AcceptedPositionSequence: 20,
                    PreviousTeleportSequence: 10,
                    AcceptedTeleportSequence: 10,
                    PositionTimestampDisposition.Apply),
                RuntimePositionEntityKind.Remote,
                RuntimeAcceptedPositionSource.PositionEvent,
                new CreateObject.ServerPosition(
                    RemotePlacementDriveFixture.DestinationCell,
                    Destination.X,
                    Destination.Y,
                    Destination.Z,
                    1f,
                    0f,
                    0f,
                    0f),
                PlacementFrame: 0u,
                PositionPackVelocity: Vector3.Zero,
                committedCellId,
                hasContact,
                playerDistance,
                UsePositionFromServer: false,
                HasAnimations: false,
                default));
}
