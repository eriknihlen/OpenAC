using System.Numerics;
using AcDream.Core.Net.Messages;
using AcDream.Core.Physics;
using AcDream.Runtime.Entities;
using AcDream.Runtime.Physics;
using AcDream.Runtime.Session;

namespace AcDream.Runtime.Tests.Physics;

public sealed class RuntimeRemoteFarSnapPositionTests
{
    private const uint Cell = 0x0101FFFFu;

    [Fact]
    public void OwnsFarSnap_TrueForTheRemoteFarBranch()
    {
        RuntimeAuthoritativePositionRoute far =
            Classify(hasContact: true, playerDistance: 200f);

        Assert.Equal(
            RuntimeAuthoritativePositionDisposition.SetPositionSimple,
            far.Disposition);
        Assert.True(far.StopInterpolating);
        Assert.True(RuntimeRemoteFarSnapPosition.OwnsFarSnap(far));
    }

    [Fact]
    public void OwnsFarSnap_TrueExactlyAtTheRetailBoundary()
    {
        Assert.False(RuntimeRemoteFarSnapPosition.OwnsFarSnap(
            Classify(hasContact: true, playerDistance: 95.99f)));
        Assert.True(RuntimeRemoteFarSnapPosition.OwnsFarSnap(
            Classify(hasContact: true, playerDistance: 96f)));
    }

    [Fact]
    public void OwnsFarSnap_FalseForEveryOtherRemoteClassification()
    {
        Assert.False(RuntimeRemoteFarSnapPosition.OwnsFarSnap(
            Classify(hasContact: true, playerDistance: 10f)));
        Assert.False(RuntimeRemoteFarSnapPosition.OwnsFarSnap(
            Classify(hasContact: false, playerDistance: 10f)));
        Assert.False(RuntimeRemoteFarSnapPosition.OwnsFarSnap(
            Classify(hasContact: true, playerDistance: 200f, committedCellId: 0u)));
        Assert.False(RuntimeRemoteFarSnapPosition.OwnsFarSnap(
            Classify(
                hasContact: true,
                playerDistance: 200f,
                previousTeleport: 10,
                acceptedTeleport: 11)));
        Assert.False(RuntimeRemoteFarSnapPosition.OwnsFarSnap(
            Classify(
                hasContact: true,
                playerDistance: 200f,
                disposition: PositionTimestampDisposition.Rejected)));
        Assert.False(RuntimeRemoteFarSnapPosition.OwnsFarSnap(
            Classify(hasContact: true, playerDistance: float.NaN)));
        Assert.False(RuntimeRemoteFarSnapPosition.OwnsFarSnap(null));
    }

    [Fact]
    public void OwnsFarSnap_FalseForTheLocalPlayerForcePositionRoute()
    {
        RuntimeAuthoritativePositionRoute force = ClassifyKind(
            RuntimePositionEntityKind.LocalPlayer,
            hasContact: true,
            playerDistance: 200f,
            disposition: PositionTimestampDisposition.ForcePosition);

        Assert.Equal(
            RuntimeAuthoritativePositionDisposition.SetPositionSimple,
            force.Disposition);
        Assert.Equal(
            RuntimeSetPositionOperationKind.LocalAuthoritative,
            force.OperationKind);
        Assert.False(RuntimeRemoteFarSnapPosition.OwnsFarSnap(force));
    }

    [Fact]
    public void OwnsFarSnap_FalseForARemoteTopLevelCreateRoute()
    {
        RuntimeAuthoritativePositionRoute create =
            RuntimeAuthoritativePositionRouteClassifier.ClassifyCreate(
                new RuntimeCreatePositionRouteRequest(
                    Authority(PositionTimestampDisposition.Apply, 10, 10),
                    RuntimePositionEntityKind.Remote,
                    RuntimeCreateResidenceKind.TopLevel,
                    new CreateObject.ServerPosition(
                        Cell, 10f, 20f, 30f, 1f, 0f, 0f, 0f),
                    default));

        Assert.Equal(
            RuntimeSetPositionOperationKind.RemoteAuthoritative,
            create.OperationKind);
        Assert.Equal(
            PhysicsSetPositionFlags.Placement | PhysicsSetPositionFlags.Slide,
            create.SetPositionFlags);
        Assert.False(RuntimeRemoteFarSnapPosition.OwnsFarSnap(create));
    }

    [Fact]
    public void OwnsFarSnap_IsAStrictSubsetOfThePlacementOwnersPredicate()
    {
        foreach (RuntimeAuthoritativePositionRoute route in new[]
        {
            Classify(hasContact: true, playerDistance: 200f),
            Classify(hasContact: true, playerDistance: 10f),
            Classify(hasContact: false, playerDistance: 10f),
            Classify(hasContact: true, playerDistance: 200f, committedCellId: 0u),
            Classify(hasContact: true, playerDistance: float.NaN),
            ClassifyKind(
                RuntimePositionEntityKind.LocalPlayer,
                hasContact: true,
                playerDistance: 200f,
                disposition: PositionTimestampDisposition.ForcePosition),
        })
        {
            if (RuntimeRemoteFarSnapPosition.OwnsFarSnap(route))
            {
                Assert.True(
                    RuntimeRemotePlacementDriveController.OwnsPlacement(route));
            }
        }

        RuntimeAuthoritativePositionRoute mismatched =
            Classify(hasContact: true, playerDistance: 200f) with
            {
                SetPositionFlags = PhysicsSetPositionFlags.Placement
                    | PhysicsSetPositionFlags.Slide,
            };
        Assert.False(
            RuntimeRemotePlacementDriveController.OwnsPlacement(mismatched));
        Assert.False(RuntimeRemoteFarSnapPosition.OwnsFarSnap(mismatched));
    }

    // ── Arm selection is total, and the leftovers are named ─────────────────

    [Fact]
    public void ResolveArm_MapsEveryRemoteClassificationToExactlyOneArm()
    {
        Assert.Equal(
            RuntimeRemoteAcceptedPositionArm.AirborneNoOperation,
            RuntimeRemoteFarSnapPosition.ResolveArm(
                Classify(hasContact: false, playerDistance: 10f)));
        Assert.Equal(
            RuntimeRemoteAcceptedPositionArm.NearInterpolate,
            RuntimeRemoteFarSnapPosition.ResolveArm(
                Classify(hasContact: true, playerDistance: 10f)));
        Assert.Equal(
            RuntimeRemoteAcceptedPositionArm.FarSnapPlacement,
            RuntimeRemoteFarSnapPosition.ResolveArm(
                Classify(hasContact: true, playerDistance: 200f)));

        Assert.Equal(
            RuntimeRemoteAcceptedPositionArm.UnroutedCatchUp,
            RuntimeRemoteFarSnapPosition.ResolveArm(
                Classify(
                    hasContact: true,
                    playerDistance: 10f,
                    disposition: PositionTimestampDisposition.Rejected)));
        Assert.Equal(
            RuntimeRemoteAcceptedPositionArm.UnroutedCatchUp,
            RuntimeRemoteFarSnapPosition.ResolveArm(
                Classify(hasContact: true, playerDistance: float.NaN)));
        Assert.Equal(
            RuntimeRemoteAcceptedPositionArm.UnroutedCatchUp,
            RuntimeRemoteFarSnapPosition.ResolveArm(null));
    }

    private static RuntimeAuthoritativePositionAuthority Authority(
        PositionTimestampDisposition disposition,
        ushort previousTeleport,
        ushort acceptedTeleport) =>
        new(
            new RuntimeGenerationToken(7),
            new RuntimeEntityKey(0x70000001u, 3),
            PositionAuthorityVersion: 11UL,
            AcceptedPositionSequence: 20,
            previousTeleport,
            acceptedTeleport,
            disposition);

    private static RuntimeAuthoritativePositionRoute Classify(
        bool hasContact,
        float playerDistance,
        uint committedCellId = Cell,
        ushort previousTeleport = 10,
        ushort acceptedTeleport = 10,
        PositionTimestampDisposition disposition =
            PositionTimestampDisposition.Apply) =>
        ClassifyKind(
            RuntimePositionEntityKind.Remote,
            hasContact,
            playerDistance,
            committedCellId,
            previousTeleport,
            acceptedTeleport,
            disposition);

    private static RuntimeAuthoritativePositionRoute ClassifyKind(
        RuntimePositionEntityKind kind,
        bool hasContact,
        float playerDistance,
        uint committedCellId = Cell,
        ushort previousTeleport = 10,
        ushort acceptedTeleport = 10,
        PositionTimestampDisposition disposition =
            PositionTimestampDisposition.Apply) =>
        RuntimeAuthoritativePositionRouteClassifier.ClassifyAcceptedPosition(
            new RuntimeAcceptedPositionRouteRequest(
                Authority(disposition, previousTeleport, acceptedTeleport),
                kind,
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
