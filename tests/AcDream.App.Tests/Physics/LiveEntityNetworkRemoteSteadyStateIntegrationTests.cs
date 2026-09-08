using System.Numerics;
using AcDream.App.Physics;
using AcDream.Core.Net.Messages;
using AcDream.Core.Physics;
using AcDream.Core.Physics.Motion;
using AcDream.Core.World;
using AcDream.Runtime;
using AcDream.Runtime.Entities;
using AcDream.Runtime.Physics;

namespace AcDream.App.Tests.Physics;

public sealed class LiveEntityNetworkRemoteSteadyStateIntegrationTests
{
    private const uint OldCell = 0x01010001u;
    private const uint WireCell = 0x01010002u;
    private static readonly Vector3 OldPosition = new(5f, 5f, 5f);
    private static readonly Vector3 WirePosition = new(6f, 7f, 8f);

    [Fact]
    public void NoPositionOperation_LeavesTheRenderEntityPoseAndCellUntouched()
    {
        RuntimeAuthoritativePositionRoute route =
            Classify(hasContact: false, playerDistance: 1f);
        Assert.Equal(
            RuntimeAuthoritativePositionDisposition.NoPositionOperation,
            route.Disposition);

        AssertRenderPoseSuppressed(route);
    }

    [Fact]
    public void Interpolate_LeavesTheRenderEntityPoseAndCellUntouched()
    {
        RuntimeAuthoritativePositionRoute route =
            Classify(hasContact: true, playerDistance: 10f);
        Assert.Equal(
            RuntimeAuthoritativePositionDisposition.Interpolate,
            route.Disposition);

        AssertRenderPoseSuppressed(route);
    }

    [Fact]
    public void FarSetPositionSimple_StillTakesTheLegacyRenderPoseWrite()
    {
        RuntimeAuthoritativePositionRoute route =
            Classify(hasContact: true, playerDistance: 200f);
        Assert.Equal(
            RuntimeAuthoritativePositionDisposition.SetPositionSimple,
            route.Disposition);

        AssertRenderPoseWritten(route);
    }

    [Fact]
    public void CellLessRemote_StillTakesTheLegacyRenderPoseWrite()
    {
        RuntimeAuthoritativePositionRoute route = Classify(
            hasContact: false,
            playerDistance: 1f,
            committedCellId: 0u);
        Assert.Equal(
            RuntimeAuthoritativePositionDisposition.SetPosition,
            route.Disposition);

        AssertRenderPoseWritten(route);
    }

    [Fact]
    public void RejectedAuthority_StillTakesTheLegacyRenderPoseWrite()
    {
        RuntimeAuthoritativePositionRoute route = Classify(
            hasContact: true,
            playerDistance: 10f,
            disposition: PositionTimestampDisposition.Rejected);
        Assert.Equal(
            RuntimeAuthoritativePositionDisposition.RejectedAuthority,
            route.Disposition);

        AssertRenderPoseWritten(route);
    }

    [Fact]
    public void RejectedData_StillTakesTheLegacyRenderPoseWrite()
    {
        RuntimeAuthoritativePositionRoute route =
            Classify(hasContact: true, playerDistance: float.NaN);
        Assert.Equal(
            RuntimeAuthoritativePositionDisposition.RejectedData,
            route.Disposition);

        AssertRenderPoseWritten(route);
    }

    [Fact]
    public void NoClassificationAtAll_StillTakesTheLegacyRenderPoseWrite()
    {
        AssertRenderPoseWritten(null);
    }

    // ── Ordering: the airborne snap outranks route 4a's near branch ─────────

    [Fact]
    public void AirborneBodyWithAContactPacket_SnapsInsteadOfEnqueuing()
    {
        RuntimeAuthoritativePositionRoute route =
            Classify(hasContact: true, playerDistance: 10f);
        Assert.Equal(
            RuntimeAuthoritativePositionDisposition.Interpolate,
            route.Disposition);

        using var fixture = new RemotePlacementDriveFixture();
        (RuntimeEntityRecord record, RemoteMotion remote, _) =
            fixture.AddRemote(0x70005001u, new Vector3(12f, 14f, 7f));
        remote.Body.Position = new Vector3(10f, 10f, 5f);
        remote.Airborne = true;
        remote.Body.TransientState = TransientStateFlags.Active;
        var landing = new Vector3(10.5f, 10f, 5f); // 0.5 m — well within 4 m.

        LiveEntityNetworkUpdateController.RemoteContactRouting routing =
            LiveEntityNetworkUpdateController.ApplyRemoteContactRouting(
                fixture.Drive,
                record,
                remote,
                route,
                landing,
                Quaternion.Identity,
                willBeDrTicked: true,
                runTeleportHook: () => true);

        Assert.Equal(
            LiveEntityNetworkUpdateController.RemoteContactArm.AirborneSnap,
            routing.Arm);
        Assert.Equal(landing, remote.Body.Position);
    }

    [Fact]
    public void GroundedBodyWithANearContactPacket_TakesRoute4asInterpolateBranch()
    {
        using var fixture = new RemotePlacementDriveFixture();
        (RuntimeEntityRecord record, RemoteMotion remote, _) =
            fixture.AddRemote(0x70005002u, new Vector3(12f, 14f, 7f));
        remote.Body.Position = new Vector3(10f, 10f, 5f);
        remote.Airborne = false;
        var target = new Vector3(10.5f, 10f, 5f);

        LiveEntityNetworkUpdateController.RemoteContactRouting routing =
            LiveEntityNetworkUpdateController.ApplyRemoteContactRouting(
                fixture.Drive,
                record,
                remote,
                Classify(hasContact: true, playerDistance: 10f),
                target,
                Quaternion.Identity,
                willBeDrTicked: true,
                runTeleportHook: () => true);

        Assert.Equal(
            LiveEntityNetworkUpdateController.RemoteContactArm
                .SteadyStateInterpolate,
            routing.Arm);
        Assert.Equal(new Vector3(10f, 10f, 5f), remote.Body.Position);
    }


    [Fact]
    public void SteepContactBody_InterpolatesInsteadOfSnapping()
    {
        using var fixture = new RemotePlacementDriveFixture();
        (RuntimeEntityRecord record, RemoteMotion remote, _) =
            fixture.AddRemote(0x70005003u, new Vector3(12f, 14f, 7f));
        var before = new Vector3(10f, 10f, 5f);
        remote.Body.Position = before;
        remote.Body.TransientState =
            TransientStateFlags.Active | TransientStateFlags.Contact;
        Assert.True(remote.Body.InContact);
        Assert.False(remote.Body.OnWalkable);
        remote.Airborne = !remote.Body.OnWalkable;
        Assert.True(remote.Airborne);

        LiveEntityNetworkUpdateController.RemoteContactRouting routing =
            LiveEntityNetworkUpdateController.ApplyRemoteContactRouting(
                fixture.Drive,
                record,
                remote,
                Classify(hasContact: true, playerDistance: 10f),
                before + new Vector3(0.5f, 0f, 0f),
                Quaternion.Identity,
                willBeDrTicked: true,
                runTeleportHook: () => true);

        Assert.Equal(
            LiveEntityNetworkUpdateController.RemoteContactArm
                .SteadyStateInterpolate,
            routing.Arm);
        Assert.Equal(before, remote.Body.Position);
    }

    [Fact]
    public void FreeFlightBodyWithNoContact_StillSnaps()
    {
        using var fixture = new RemotePlacementDriveFixture();
        (RuntimeEntityRecord record, RemoteMotion remote, _) =
            fixture.AddRemote(0x70005004u, new Vector3(12f, 14f, 7f));
        remote.Body.Position = new Vector3(10f, 10f, 5f);
        remote.Body.TransientState = TransientStateFlags.Active;
        Assert.False(remote.Body.InContact);
        remote.Airborne = !remote.Body.OnWalkable;
        var landing = new Vector3(10.5f, 10f, 5f);

        LiveEntityNetworkUpdateController.RemoteContactRouting routing =
            LiveEntityNetworkUpdateController.ApplyRemoteContactRouting(
                fixture.Drive,
                record,
                remote,
                Classify(hasContact: true, playerDistance: 10f),
                landing,
                Quaternion.Identity,
                willBeDrTicked: true,
                runTeleportHook: () => true);

        Assert.Equal(
            LiveEntityNetworkUpdateController.RemoteContactArm.AirborneSnap,
            routing.Arm);
        Assert.Equal(landing, remote.Body.Position);
    }

    [Fact]
    public void WalkableGroundedBody_IsUnchangedAndStillInterpolates()
    {
        using var fixture = new RemotePlacementDriveFixture();
        (RuntimeEntityRecord record, RemoteMotion remote, _) =
            fixture.AddRemote(0x70005005u, new Vector3(12f, 14f, 7f));
        var before = new Vector3(10f, 10f, 5f);
        remote.Body.Position = before;
        remote.Body.TransientState = TransientStateFlags.Active
            | TransientStateFlags.Contact
            | TransientStateFlags.OnWalkable;
        remote.Airborne = !remote.Body.OnWalkable;
        Assert.False(remote.Airborne);

        LiveEntityNetworkUpdateController.RemoteContactRouting routing =
            LiveEntityNetworkUpdateController.ApplyRemoteContactRouting(
                fixture.Drive,
                record,
                remote,
                Classify(hasContact: true, playerDistance: 10f),
                before + new Vector3(0.5f, 0f, 0f),
                Quaternion.Identity,
                willBeDrTicked: true,
                runTeleportHook: () => true);

        Assert.Equal(
            LiveEntityNetworkUpdateController.RemoteContactArm
                .SteadyStateInterpolate,
            routing.Arm);
        Assert.Equal(before, remote.Body.Position);
    }

    private static void AssertRenderPoseSuppressed(
        RuntimeAuthoritativePositionRoute? route)
    {
        WorldEntity entity = MakeEntity();
        var beforeRotation = entity.Rotation;

        bool written = LiveEntityNetworkUpdateController
            .TryApplyGenericRemoteRenderPose(
                entity,
                route,
                WirePosition,
                WireCell,
                Quaternion.CreateFromAxisAngle(Vector3.UnitZ, 1.1f));

        Assert.False(written);
        Assert.Equal(OldPosition, entity.Position);
        Assert.Equal(OldCell, entity.ParentCellId);
        Assert.Equal(beforeRotation, entity.Rotation);
    }

    private static void AssertRenderPoseWritten(
        RuntimeAuthoritativePositionRoute? route)
    {
        WorldEntity entity = MakeEntity();
        var wireRotation = Quaternion.CreateFromAxisAngle(Vector3.UnitZ, 1.1f);

        bool written = LiveEntityNetworkUpdateController
            .TryApplyGenericRemoteRenderPose(
                entity,
                route,
                WirePosition,
                WireCell,
                wireRotation);

        Assert.True(written);
        Assert.Equal(WirePosition, entity.Position);
        Assert.Equal(WireCell, entity.ParentCellId);
        Assert.Equal(wireRotation, entity.Rotation);
    }

    private static WorldEntity MakeEntity() => new()
    {
        Id = 1u,
        SourceGfxObjOrSetupId = 1u,
        Position = OldPosition,
        Rotation = Quaternion.Identity,
        MeshRefs = Array.Empty<MeshRef>(),
        ParentCellId = OldCell,
    };

    private const uint Cell = 0x0101FFFFu;

    private static RuntimeAuthoritativePositionRoute Classify(
        bool hasContact,
        float playerDistance,
        uint committedCellId = OldCell,
        PositionTimestampDisposition disposition =
            PositionTimestampDisposition.Apply) =>
        RuntimeAuthoritativePositionRouteClassifier.ClassifyAcceptedPosition(
            new RuntimeAcceptedPositionRouteRequest(
                new RuntimeAuthoritativePositionAuthority(
                    new RuntimeGenerationToken(7),
                    new RuntimeEntityKey(0x70000001u, 3),
                    PositionAuthorityVersion: 11UL,
                    AcceptedPositionSequence: 20,
                    PreviousTeleportSequence: 10,
                    AcceptedTeleportSequence: 10,
                    disposition),
                RuntimePositionEntityKind.Remote,
                RuntimeAcceptedPositionSource.PositionEvent,
                new CreateObject.ServerPosition(
                    Cell, 10f, 20f, 30f, 1f, 0f, 0f, 0f),
                PlacementFrame: 0u,
                PositionPackVelocity: Vector3.Zero,
                committedCellId,
                hasContact,
                playerDistance,
                UsePositionFromServer: false,
                HasAnimations: false,
                default));
}
