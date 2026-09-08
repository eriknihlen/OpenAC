using System.Numerics;
using AcDream.Core.Net.Messages;
using AcDream.Core.Physics;
using AcDream.Core.Physics.Motion;
using AcDream.Runtime.Entities;
using AcDream.Runtime.Physics;

namespace AcDream.Runtime.Tests.Physics;

public sealed class RuntimeRemoteSteadyStatePositionTests
{
    private const uint Cell = 0x0101FFFFu;

    private static RemoteMotion MakeSampledRemote(Vector3 bodyPosition)
    {
        var remote = new RemoteMotion();
        remote.Body.Position = bodyPosition;
        remote.Body.Orientation = Quaternion.Identity;
        remote.LastServerPosTime = 1_700_000_000d;
        return remote;
    }

    private static (RemoteMotion Remote, EntityPhysicsHost Host) MakeRemoteWithHost(
        Vector3 bodyPosition)
    {
        RemoteMotion remote = MakeSampledRemote(bodyPosition);
        EntityPhysicsHost host = new(
            id: 0x70000001u,
            getPosition: () => new Position(0x0001u, remote.Body.Position, remote.Body.Orientation),
            getVelocity: () => remote.Body.Velocity,
            getRadius: () => 0.48f,
            inContact: () => remote.Body.InContact,
            minterpMaxSpeed: () => null,
            curTime: () => 0d,
            physicsTimerTime: () => 0d,
            getObjectA: _ => null,
            handleUpdateTarget: _ => { },
            interruptCurrentMovement: () => { });
        remote.BindCanonicalRuntime(
            () => host,
            () => 0x0001u,
            _ => { });
        remote.MarkFullPhysicsHostBound();
        return (remote, host);
    }


    [Fact]
    public void ApplyInterpolate_BodyFarFromTarget_SnapsRatherThanEnqueues()
    {
        RemoteMotion remote = MakeSampledRemote(new Vector3(0f, 0f, 0f));
        var target = new Vector3(50f, 0f, 0f); // 50 m away — far beyond 4 m.

        RuntimeRemoteSteadyStatePosition.Action action =
            RuntimeRemoteSteadyStatePosition.ApplyInterpolate(
                remote,
                target,
                Quaternion.Identity,
                isMovingTo: false,
                willBeDrTicked: true);

        Assert.Equal(RuntimeRemoteSteadyStatePosition.Action.Snapped, action);
        Assert.Equal(target, remote.Body.Position);
    }

    [Fact]
    public void ApplyInterpolate_NotDrTicked_SnapsEvenWhenClose()
    {
        RemoteMotion remote = MakeSampledRemote(new Vector3(10f, 10f, 5f));
        var target = new Vector3(10.5f, 10f, 5f); // 0.5 m — well within 4 m.

        RuntimeRemoteSteadyStatePosition.Action action =
            RuntimeRemoteSteadyStatePosition.ApplyInterpolate(
                remote,
                target,
                Quaternion.Identity,
                isMovingTo: false,
                willBeDrTicked: false);

        Assert.Equal(RuntimeRemoteSteadyStatePosition.Action.Snapped, action);
        Assert.Equal(target, remote.Body.Position);
    }

    [Fact]
    public void ApplyInterpolate_FirstUpBeforeAnyServerSample_Snaps()
    {
        var remote = new RemoteMotion();
        remote.Body.Position = new Vector3(10f, 10f, 5f);
        remote.Body.Orientation = Quaternion.Identity;
        Assert.Equal(0d, remote.LastServerPosTime);
        var target = new Vector3(10.5f, 10f, 5f);

        RuntimeRemoteSteadyStatePosition.Action action =
            RuntimeRemoteSteadyStatePosition.ApplyInterpolate(
                remote,
                target,
                Quaternion.Identity,
                isMovingTo: false,
                willBeDrTicked: true);

        Assert.Equal(RuntimeRemoteSteadyStatePosition.Action.Snapped, action);
        Assert.Equal(target, remote.Body.Position);
    }

    [Fact]
    public void ApplyInterpolate_NearAndDrTicked_EnqueuesWithoutTouchingBodyPosition()
    {
        RemoteMotion remote = MakeSampledRemote(new Vector3(10f, 10f, 5f));
        var target = new Vector3(10.5f, 10f, 5f);

        RuntimeRemoteSteadyStatePosition.Action action =
            RuntimeRemoteSteadyStatePosition.ApplyInterpolate(
                remote,
                target,
                Quaternion.Identity,
                isMovingTo: false,
                willBeDrTicked: true);

        Assert.Equal(RuntimeRemoteSteadyStatePosition.Action.Enqueued, action);
        Assert.Equal(new Vector3(10f, 10f, 5f), remote.Body.Position);
    }

    [Fact]
    public void ApplyInterpolate_IsIndifferentToTheStickyLease()
    {
        (RemoteMotion remote, EntityPhysicsHost host) = MakeRemoteWithHost(
            new Vector3(0f, 0f, 0f));
        host.PositionManager.StickTo(objectId: 0x70000002u, radius: 1f, height: 1f);
        Assert.NotEqual(0u, host.PositionManager.GetStickyObjectId());

        RuntimeRemoteSteadyStatePosition.Action action =
            RuntimeRemoteSteadyStatePosition.ApplyInterpolate(
                remote,
                new Vector3(50f, 0f, 0f),
                Quaternion.Identity,
                isMovingTo: false,
                willBeDrTicked: true);

        Assert.Equal(RuntimeRemoteSteadyStatePosition.Action.Snapped, action);
    }


    [Fact]
    public void TwoRemotesInTheSameTick_ApplyIndependentlyWithNoCrossContamination()
    {
        RemoteMotion farRemote = MakeSampledRemote(new Vector3(0f, 0f, 0f)); // will snap.
        RemoteMotion nearRemote = MakeSampledRemote(new Vector3(10f, 10f, 5f)); // will enqueue.

        RuntimeRemoteSteadyStatePosition.Action farAction =
            RuntimeRemoteSteadyStatePosition.ApplyInterpolate(
                farRemote,
                new Vector3(50f, 0f, 0f),
                Quaternion.Identity,
                isMovingTo: false,
                willBeDrTicked: true);
        RuntimeRemoteSteadyStatePosition.Action nearAction =
            RuntimeRemoteSteadyStatePosition.ApplyInterpolate(
                nearRemote,
                new Vector3(10.5f, 10f, 5f),
                Quaternion.Identity,
                isMovingTo: false,
                willBeDrTicked: true);

        Assert.Equal(RuntimeRemoteSteadyStatePosition.Action.Snapped, farAction);
        Assert.Equal(RuntimeRemoteSteadyStatePosition.Action.Enqueued, nearAction);
        Assert.Equal(new Vector3(50f, 0f, 0f), farRemote.Body.Position);
        // nearRemote's body is untouched by farRemote's snap — no shared state.
        Assert.Equal(new Vector3(10f, 10f, 5f), nearRemote.Body.Position);
    }

    // ── Ownership: exactly two dispositions, everything else falls through ──

    [Fact]
    public void OwnsSteadyState_IsTrueForExactlyTheTwoNoPlacementDispositions()
    {
        Assert.True(RuntimeRemoteSteadyStatePosition.OwnsSteadyState(
            Classify(hasContact: false, playerDistance: 1f)));
        Assert.True(RuntimeRemoteSteadyStatePosition.OwnsSteadyState(
            Classify(hasContact: true, playerDistance: 10f)));
    }

    [Theory]
    // Far (>=96 m) — SetPositionSimple, route 4b.
    [InlineData(true, 200f, Cell, 10, 10, PositionTimestampDisposition.Apply)]
    [InlineData(true, 10f, 0u, 10, 10, PositionTimestampDisposition.Apply)]
    // Fresh TELEPORT_TS — SetPosition, route 4b.
    [InlineData(true, 10f, Cell, 10, 11, PositionTimestampDisposition.Apply)]
    // Rejected admission — RejectedAuthority.
    [InlineData(true, 10f, Cell, 10, 10, PositionTimestampDisposition.Rejected)]
    // Nonfinite derived distance — RejectedData.
    [InlineData(true, float.NaN, Cell, 10, 10, PositionTimestampDisposition.Apply)]
    public void OwnsSteadyState_IsFalseForEveryOtherClassification(
        bool hasContact,
        float playerDistance,
        uint committedCellId,
        ushort previousTeleport,
        ushort acceptedTeleport,
        PositionTimestampDisposition disposition)
    {
        RuntimeAuthoritativePositionRoute route = Classify(
            hasContact,
            playerDistance,
            committedCellId,
            previousTeleport,
            acceptedTeleport,
            disposition);

        Assert.NotEqual(
            RuntimeAuthoritativePositionDisposition.Interpolate,
            route.Disposition);
        Assert.NotEqual(
            RuntimeAuthoritativePositionDisposition.NoPositionOperation,
            route.Disposition);
        Assert.False(RuntimeRemoteSteadyStatePosition.OwnsSteadyState(route));
    }

    [Fact]
    public void OwnsSteadyState_IsFalseWhenNothingWasClassified()
    {
        Assert.False(RuntimeRemoteSteadyStatePosition.OwnsSteadyState(null));
        Assert.False(RuntimeRemoteSteadyStatePosition.IsAirborneNoOperation(null));
        Assert.False(RuntimeRemoteSteadyStatePosition.IsNearInterpolate(null));
    }


    [Fact]
    public void ArmConstraintAfterOperation_AnchorsToTheHostsCurrentLivePosition()
    {
        (RemoteMotion remote, EntityPhysicsHost host) = MakeRemoteWithHost(
            new Vector3(1f, 2f, 3f));

        remote.Body.Position = new Vector3(9f, 9f, 9f);

        RuntimeRemoteSteadyStatePosition.ArmConstraintAfterOperation(host);

        ConstraintManager? constraint = host.PositionManager.Constraint;
        Assert.NotNull(constraint);
        Assert.True(constraint!.IsConstrained);
        Assert.Equal(new Vector3(9f, 9f, 9f), constraint.ConstraintPos.Frame.Origin);
        Assert.Equal(0f, constraint.ConstraintPosOffset, 3);
    }

    [Theory]
    [InlineData("TeleportPlacement", true)]
    [InlineData("FarSnapPlacement", true)]
    [InlineData("NearInterpolate", true)]
    [InlineData("UnroutedCatchUp", true)]
    [InlineData("AirborneNoOperation", false)]
    public void TryArmConstraintAfterOperation_MatchesTheCompletePartition(
        string armName,
        bool expectedArmed)
    {
        // RuntimeRemoteAcceptedPositionArm is internal; xUnit's [InlineData]
        // requires public-visible argument types, so the arm travels as its
        // name and is parsed back here.
        var arm = (RuntimeRemoteAcceptedPositionArm)Enum.Parse(
            typeof(RuntimeRemoteAcceptedPositionArm), armName);
        (RemoteMotion remote, EntityPhysicsHost host) = MakeRemoteWithHost(
            new Vector3(1f, 2f, 3f));

        bool armed = RuntimeRemoteSteadyStatePosition
            .TryArmConstraintAfterOperation(arm, remote);

        Assert.Equal(expectedArmed, armed);
        Assert.Equal(
            expectedArmed,
            host.PositionManager.Constraint?.IsConstrained == true);
    }

    private static RuntimeAuthoritativePositionRoute Classify(
        bool hasContact,
        float playerDistance,
        uint committedCellId = Cell,
        ushort previousTeleport = 10,
        ushort acceptedTeleport = 10,
        PositionTimestampDisposition disposition =
            PositionTimestampDisposition.Apply) =>
        RuntimeAuthoritativePositionRouteClassifier.ClassifyAcceptedPosition(
            new RuntimeAcceptedPositionRouteRequest(
                new RuntimeAuthoritativePositionAuthority(
                    new RuntimeGenerationToken(7),
                    new RuntimeEntityKey(0x70000001u, 3),
                    PositionAuthorityVersion: 11UL,
                    AcceptedPositionSequence: 20,
                    previousTeleport,
                    acceptedTeleport,
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
