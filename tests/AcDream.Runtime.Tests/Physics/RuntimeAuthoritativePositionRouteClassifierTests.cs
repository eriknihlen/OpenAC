using System.Numerics;
using AcDream.Core.Net.Messages;
using AcDream.Core.Physics;
using AcDream.Runtime.Entities;
using AcDream.Runtime.Physics;

namespace AcDream.Runtime.Tests.Physics;

public sealed class RuntimeAuthoritativePositionRouteClassifierTests
{
    [Theory]
    [InlineData(RuntimePositionEntityKind.LocalPlayer,
        RuntimeSetPositionOperationKind.InitialLogin,
        RuntimeTeleportHookPhase.AfterEnterWorld)]
    [InlineData(RuntimePositionEntityKind.Remote,
        RuntimeSetPositionOperationKind.RemoteAuthoritative,
        RuntimeTeleportHookPhase.None)]
    [InlineData(RuntimePositionEntityKind.Projectile,
        RuntimeSetPositionOperationKind.ProjectileAuthoritative,
        RuntimeTeleportHookPhase.None)]
    internal void TopLevelCreate_AlwaysUsesRetailEnterWorldFlags(
        RuntimePositionEntityKind kind,
        RuntimeSetPositionOperationKind expectedKind,
        RuntimeTeleportHookPhase expectedHookPhase)
    {
        RuntimeAuthoritativePositionRoute route =
            RuntimeAuthoritativePositionRouteClassifier.ClassifyCreate(
                new RuntimeCreatePositionRouteRequest(
                    Authority(),
                    kind,
                    RuntimeCreateResidenceKind.TopLevel,
                    Position(),
                    new RuntimePositionPlacementFacts(
                        PhysicsStateFlags.Static
                            | PhysicsStateFlags.Hidden
                            | PhysicsStateFlags.NoDraw,
                        HasAuthoredMoverShape: false)));

        Assert.Equal(RuntimeAuthoritativePositionDisposition.SetPosition,
            route.Disposition);
        Assert.Equal(expectedKind, route.OperationKind);
        Assert.Equal(0x11u, (uint)route.SetPositionFlags);
        Assert.Equal(expectedHookPhase, route.TeleportHookPhase);
        Assert.True(route.PerformsSetPosition);
        Assert.False(route.CollisionBatchEligible);
    }

    [Theory]
    [InlineData(RuntimeCreateResidenceKind.Parented)]
    [InlineData(RuntimeCreateResidenceKind.PickedUp)]
    internal void NewParentedOrPickedUpCreate_RemainsCelllessAndAwaitsPosition(
        RuntimeCreateResidenceKind residence)
    {
        RuntimeAuthoritativePositionRoute route =
            RuntimeAuthoritativePositionRouteClassifier.ClassifyCreate(
                new RuntimeCreatePositionRouteRequest(
                    Authority(),
                    RuntimePositionEntityKind.Remote,
                    residence,
                    AcceptedWirePosition: null,
                    default));

        Assert.Equal(RuntimeAuthoritativePositionDisposition.AwaitFreshPosition,
            route.Disposition);
        Assert.False(route.LeaveWorld);
        Assert.False(route.PerformsSetPosition);
    }

    [Theory]
    [MemberData(nameof(InvalidPositions))]
    internal void CreateAndPositionRejectMalformedAuthoritativeFrames(
        CreateObject.ServerPosition invalid)
    {
        RuntimeAuthoritativePositionRoute create =
            RuntimeAuthoritativePositionRouteClassifier.ClassifyCreate(
                new RuntimeCreatePositionRouteRequest(
                    Authority(),
                    RuntimePositionEntityKind.Remote,
                    RuntimeCreateResidenceKind.TopLevel,
                    invalid,
                    default));
        RuntimeAuthoritativePositionRoute update = ClassifyRemote(
            position: invalid,
            committedCellId: Cell,
            hasContact: true,
            playerDistance: 1f);

        Assert.Equal(RuntimeAuthoritativePositionDisposition.RejectedData,
            create.Disposition);
        Assert.Equal(RuntimeAuthoritativePositionDisposition.RejectedData,
            update.Disposition);
    }

    [Theory]
    [InlineData(RuntimeAcceptedPositionSource.PositionEvent)]
    [InlineData(RuntimeAcceptedPositionSource.SameIncarnationCreate)]
    internal void AcceptedPosition_UnparentsAndAppliesDefaultZeroPlacementBeforeRoute(
        RuntimeAcceptedPositionSource source)
    {
        RuntimeAuthoritativePositionRoute route = ClassifyRemote(
            source: source,
            placementFrame: null,
            committedCellId: Cell,
            hasContact: true,
            playerDistance: 10f);

        Assert.Equal(RuntimeAuthoritativePositionDisposition.Interpolate,
            route.Disposition);
        Assert.True(route.UnparentBeforeRouting);
        Assert.True(route.ApplyPlacementFrameBeforeRouting);
        Assert.Equal(0u, route.PlacementFrame);
    }

    [Fact]
    public void AnimatedAcceptedPosition_UnparentsButSkipsPlacementFrameReset()
    {
        RuntimeAuthoritativePositionRoute route = ClassifyRemote(
            committedCellId: Cell,
            hasContact: true,
            playerDistance: 10f,
            hasAnimations: true);

        Assert.Equal(RuntimeAuthoritativePositionDisposition.Interpolate,
            route.Disposition);
        Assert.True(route.UnparentBeforeRouting);
        Assert.False(route.ApplyPlacementFrameBeforeRouting);
    }

    [Theory]
    [InlineData(95.999f, RuntimeAuthoritativePositionDisposition.Interpolate)]
    [InlineData(96f, RuntimeAuthoritativePositionDisposition.SetPositionSimple)]
    internal void SameIncarnationCreate_ForcesContactRoute(
        float distance,
        RuntimeAuthoritativePositionDisposition expected)
    {
        RuntimeAuthoritativePositionRoute route = ClassifyRemote(
            source: RuntimeAcceptedPositionSource.SameIncarnationCreate,
            committedCellId: Cell,
            hasContact: false,
            playerDistance: distance);

        Assert.Equal(expected, route.Disposition);
        Assert.False(route.ConstrainBeforeRouting);
        Assert.True(route.ConstrainAfterRouting);
    }

    [Theory]
    [InlineData(ushort.MaxValue, 0)]
    [InlineData(0x8000, 0)]
    internal void RemoteFreshTeleport_UsesWrapSafeTimestampAndRetailHook(
        ushort previousTeleport,
        ushort acceptedTeleport)
    {
        RuntimeAuthoritativePositionRoute route = ClassifyRemote(
            authority: Authority(previousTeleport, acceptedTeleport),
            committedCellId: Cell,
            hasContact: false,
            playerDistance: float.NaN);

        Assert.Equal(RuntimeAuthoritativePositionDisposition.SetPosition,
            route.Disposition);
        Assert.Equal(0x1012u, (uint)route.SetPositionFlags);
        Assert.Equal(RuntimeTeleportHookPhase.BeforePositionOperation,
            route.TeleportHookPhase);
        Assert.True(route.ConstrainAfterRouting);
    }

    [Fact]
    public void RemoteCellless_UsesCommittedResidenceNotAcceptedWireCell()
    {
        CreateObject.ServerPosition accepted = Position(Cell, 200f);

        RuntimeAuthoritativePositionRoute route = ClassifyRemote(
            position: accepted,
            committedCellId: null,
            hasContact: true,
            playerDistance: 1f);

        Assert.Equal(RuntimeAuthoritativePositionDisposition.SetPosition,
            route.Disposition);
        Assert.Equal(RuntimeTeleportHookPhase.BeforePositionOperation,
            route.TeleportHookPhase);
        Assert.Equal(Cell, accepted.LandblockId);
    }

    [Fact]
    public void RemoteNoContact_PerformsNoPositionOperationOrConstraint()
    {
        RuntimeAuthoritativePositionRoute route = ClassifyRemote(
            committedCellId: Cell,
            hasContact: false,
            playerDistance: float.NaN);

        Assert.Equal(RuntimeAuthoritativePositionDisposition.NoPositionOperation,
            route.Disposition);
        Assert.False(route.ConstrainAfterRouting);
        Assert.False(route.PerformsSetPosition);
    }

    [Theory]
    [InlineData(0f, RuntimeAuthoritativePositionDisposition.Interpolate, false)]
    [InlineData(95.999f, RuntimeAuthoritativePositionDisposition.Interpolate, false)]
    [InlineData(96f, RuntimeAuthoritativePositionDisposition.SetPositionSimple, true)]
    [InlineData(500f, RuntimeAuthoritativePositionDisposition.SetPositionSimple, true)]
    internal void RemoteContact_UsesExactNinetySixBoundary(
        float distance,
        RuntimeAuthoritativePositionDisposition expected,
        bool stopInterpolating)
    {
        RuntimeAuthoritativePositionRoute route = ClassifyRemote(
            committedCellId: Cell,
            hasContact: true,
            playerDistance: distance);

        Assert.Equal(expected, route.Disposition);
        Assert.Equal(stopInterpolating, route.StopInterpolating);
        Assert.True(route.ConstrainAfterRouting);
        Assert.Equal(stopInterpolating ? 0x1012u : 0u,
            (uint)route.SetPositionFlags);
    }

    [Theory]
    [InlineData(float.NaN)]
    [InlineData(float.PositiveInfinity)]
    [InlineData(float.NegativeInfinity)]
    [InlineData(-0.001f)]
    internal void RemoteContact_RejectsMalformedDerivedDistance(float distance)
    {
        RuntimeAuthoritativePositionRoute route = ClassifyRemote(
            committedCellId: Cell,
            hasContact: true,
            playerDistance: distance);

        Assert.Equal(RuntimeAuthoritativePositionDisposition.RejectedData,
            route.Disposition);
    }

    [Fact]
    public void RemotePositionPackVelocity_IsDeadRoutingInput()
    {
        RuntimeAcceptedPositionRouteRequest request = RemoteRequest(
            committedCellId: Cell,
            hasContact: true,
            playerDistance: 10f) with
        {
            PositionPackVelocity = new Vector3(float.NaN, float.PositiveInfinity, 7f),
        };

        RuntimeAuthoritativePositionRoute route =
            RuntimeAuthoritativePositionRouteClassifier
                .ClassifyAcceptedPosition(request);

        Assert.Equal(RuntimeAuthoritativePositionDisposition.Interpolate,
            route.Disposition);
        Assert.True(route.Accepted);
    }

    [Fact]
    public void LocalFreshForce_PreservesHeadingBlipsAndAcknowledgesImmediately()
    {
        RuntimeAuthoritativePositionRoute route = ClassifyLocal(
            Authority(
                previousTeleport: 10,
                acceptedTeleport: 10,
                disposition: PositionTimestampDisposition.ForcePosition));

        Assert.Equal(RuntimeAuthoritativePositionDisposition.SetPositionSimple,
            route.Disposition);
        Assert.Equal(0x1012u, (uint)route.SetPositionFlags);
        Assert.True(route.PreserveHeading);
        Assert.True(route.SendPositionImmediately);
        Assert.False(route.UnparentBeforeRouting);
        Assert.False(route.ApplyPlacementFrameBeforeRouting);
        Assert.False(route.ConstrainAfterRouting);
    }

    [Fact]
    public void ForcePosition_AcceptsLocalNonRegressedTeleportAndRejectsRemoteOrRegressed()
    {
        RuntimeAuthoritativePositionAuthority force = Authority(
            10, 10, PositionTimestampDisposition.ForcePosition);
        RuntimeAuthoritativePositionRoute remote = ClassifyRemote(authority: force);
        RuntimeAuthoritativePositionRoute newer = ClassifyLocal(
            Authority(10, 11, PositionTimestampDisposition.ForcePosition));
        RuntimeAuthoritativePositionRoute regressed = ClassifyLocal(
            Authority(11, 10, PositionTimestampDisposition.ForcePosition));

        Assert.Equal(RuntimeAuthoritativePositionDisposition.RejectedAuthority,
            remote.Disposition);
        Assert.Equal(RuntimeAuthoritativePositionDisposition.SetPositionSimple,
            newer.Disposition);
        Assert.False(newer.ZeroVelocity);
        Assert.Equal(RuntimeTeleportHookPhase.None, newer.TeleportHookPhase);
        Assert.Equal(RuntimeAuthoritativePositionDisposition.RejectedAuthority,
            regressed.Disposition);
    }

    [Fact]
    public void LocalFreshTeleport_SetsPositionConstrainsAndZerosVelocity()
    {
        RuntimeAuthoritativePositionRoute route = ClassifyLocal(
            Authority(ushort.MaxValue, 0));

        Assert.Equal(RuntimeAuthoritativePositionDisposition.SetPositionSimple,
            route.Disposition);
        Assert.Equal(0x1012u, (uint)route.SetPositionFlags);
        Assert.True(route.UnparentBeforeRouting);
        Assert.True(route.ApplyPlacementFrameBeforeRouting);
        Assert.True(route.ConstrainAfterRouting);
        Assert.True(route.ZeroVelocity);
        Assert.Equal(RuntimeTeleportHookPhase.AfterPositionOperation,
            route.TeleportHookPhase);
    }

    [Theory]
    [InlineData(false, false, false, RuntimeAuthoritativePositionDisposition.NoPositionOperation)]
    [InlineData(false, true, true, RuntimeAuthoritativePositionDisposition.NoPositionOperation)]
    [InlineData(true, false, true, RuntimeAuthoritativePositionDisposition.NoPositionOperation)]
    [InlineData(true, true, false, RuntimeAuthoritativePositionDisposition.Interpolate)]
    [InlineData(true, true, true, RuntimeAuthoritativePositionDisposition.Interpolate)]
    internal void LocalOrdinary_AlwaysConstrainsAndInterpolatesOnlyByOptionAndContact(
        bool usePositionFromServer,
        bool hasContact,
        bool hasVelocity,
        RuntimeAuthoritativePositionDisposition expected)
    {
        RuntimeAuthoritativePositionRoute route = ClassifyLocal(
            Authority(),
            usePositionFromServer,
            hasContact,
            hasVelocity
                ? new Vector3(float.NaN, float.PositiveInfinity, 1f)
                : null);

        Assert.Equal(expected, route.Disposition);
        Assert.True(route.ConstrainBeforeRouting);
        Assert.False(route.ConstrainAfterRouting);
    }

    [Fact]
    public void HiddenAndNoDrawDoNotGatePlacement_ButReportingRemainsSeparate()
    {
        RuntimePositionPlacementFacts visible = new(
            PhysicsStateFlags.None,
            HasAuthoredMoverShape: true);
        RuntimePositionPlacementFacts noDraw = visible with
        {
            PhysicsState = PhysicsStateFlags.NoDraw,
        };
        RuntimePositionPlacementFacts hidden = visible with
        {
            PhysicsState = PhysicsStateFlags.Hidden,
        };
        RuntimePositionPlacementFacts ignored = visible with
        {
            PhysicsState = PhysicsStateFlags.IgnoreCollisions,
        };

        RuntimeAuthoritativePositionRoute visibleRoute =
            RuntimeAuthoritativePositionRouteClassifier.ClassifyCreate(
                new RuntimeCreatePositionRouteRequest(
                    Authority(),
                    RuntimePositionEntityKind.Remote,
                    RuntimeCreateResidenceKind.TopLevel,
                    Position(),
                    visible));
        RuntimeAuthoritativePositionRoute noDrawRoute =
            RuntimeAuthoritativePositionRouteClassifier.ClassifyCreate(
                new RuntimeCreatePositionRouteRequest(
                    Authority(),
                    RuntimePositionEntityKind.Remote,
                    RuntimeCreateResidenceKind.TopLevel,
                    Position(),
                    noDraw));
        RuntimeAuthoritativePositionRoute hiddenRoute =
            RuntimeAuthoritativePositionRouteClassifier.ClassifyCreate(
                new RuntimeCreatePositionRouteRequest(
                    Authority(),
                    RuntimePositionEntityKind.Remote,
                    RuntimeCreateResidenceKind.TopLevel,
                    Position(),
                    hidden));
        RuntimeAuthoritativePositionRoute ignoredRoute =
            RuntimeAuthoritativePositionRouteClassifier.ClassifyCreate(
                new RuntimeCreatePositionRouteRequest(
                    Authority(),
                    RuntimePositionEntityKind.Remote,
                    RuntimeCreateResidenceKind.TopLevel,
                    Position(),
                    ignored));

        Assert.All([visibleRoute, noDrawRoute, hiddenRoute, ignoredRoute], route =>
            Assert.Equal(RuntimeAuthoritativePositionDisposition.SetPosition,
                route.Disposition));
        Assert.True(visibleRoute.CollisionBatchEligible);
        Assert.True(noDrawRoute.CollisionBatchEligible);
        Assert.False(hiddenRoute.CollisionBatchEligible);
        Assert.True(ignoredRoute.CollisionBatchEligible);
    }

    [Fact]
    public void ProjectilePosition_UsesRemoteMoveOrTeleportClassification()
    {
        RuntimeAcceptedPositionRouteRequest request = RemoteRequest(
            committedCellId: Cell,
            hasContact: true,
            playerDistance: 96f) with
        {
            EntityKind = RuntimePositionEntityKind.Projectile,
        };

        RuntimeAuthoritativePositionRoute route =
            RuntimeAuthoritativePositionRouteClassifier
                .ClassifyAcceptedPosition(request);

        Assert.Equal(RuntimeSetPositionOperationKind.ProjectileAuthoritative,
            route.OperationKind);
        Assert.Equal(RuntimeAuthoritativePositionDisposition.SetPositionSimple,
            route.Disposition);
    }

    [Fact]
    public void StaleOrMalformedAuthorityIsRejectedWithoutRouting()
    {
        RuntimeAuthoritativePositionRoute stale = ClassifyRemote(
            authority: Authority(previousTeleport: 0, acceptedTeleport: ushort.MaxValue));
        RuntimeAuthoritativePositionRoute wrongGeneration = ClassifyRemote(
            authority: Authority() with { Generation = default });
        RuntimeAuthoritativePositionRoute wrongEntity = ClassifyRemote(
            authority: Authority() with { Entity = default });
        RuntimeAuthoritativePositionRoute wrongVersion = ClassifyRemote(
            authority: Authority() with { PositionAuthorityVersion = 0UL });

        Assert.All([stale, wrongGeneration, wrongEntity, wrongVersion], route =>
            Assert.Equal(RuntimeAuthoritativePositionDisposition.RejectedAuthority,
                route.Disposition));
    }

    public static TheoryData<CreateObject.ServerPosition> InvalidPositions => new()
    {
        Position(cell: 0u),
        Position(x: float.NaN),
        Position(x: float.PositiveInfinity),
        Position(rotationW: 0f),
        Position(rotationW: float.NaN),
    };

    private const uint Cell = 0x0101FFFFu;

    private static RuntimeAuthoritativePositionAuthority Authority(
        ushort previousTeleport = 10,
        ushort acceptedTeleport = 10,
        PositionTimestampDisposition disposition = PositionTimestampDisposition.Apply) =>
        new(
            new RuntimeGenerationToken(7),
            new RuntimeEntityKey(0x70000001u, 3),
            PositionAuthorityVersion: 11UL,
            AcceptedPositionSequence: 20,
            previousTeleport,
            acceptedTeleport,
            disposition);

    private static RuntimeAuthoritativePositionRoute ClassifyRemote(
        RuntimeAuthoritativePositionAuthority? authority = null,
        RuntimeAcceptedPositionSource source = RuntimeAcceptedPositionSource.PositionEvent,
        CreateObject.ServerPosition? position = null,
        uint? placementFrame = 0u,
        uint? committedCellId = Cell,
        bool hasContact = true,
        float playerDistance = 10f,
        bool hasAnimations = false) =>
        RuntimeAuthoritativePositionRouteClassifier.ClassifyAcceptedPosition(
            RemoteRequest(
                authority,
                source,
                position,
                placementFrame,
                committedCellId,
                hasContact,
                playerDistance,
                hasAnimations));

    private static RuntimeAcceptedPositionRouteRequest RemoteRequest(
        RuntimeAuthoritativePositionAuthority? authority = null,
        RuntimeAcceptedPositionSource source = RuntimeAcceptedPositionSource.PositionEvent,
        CreateObject.ServerPosition? position = null,
        uint? placementFrame = 0u,
        uint? committedCellId = Cell,
        bool hasContact = true,
        float playerDistance = 10f,
        bool hasAnimations = false) =>
        new(
            authority ?? Authority(),
            RuntimePositionEntityKind.Remote,
            source,
            position ?? Position(),
            placementFrame,
            PositionPackVelocity: Vector3.One,
            committedCellId,
            hasContact,
            playerDistance,
            UsePositionFromServer: false,
            hasAnimations,
            default);

    private static RuntimeAuthoritativePositionRoute ClassifyLocal(
        RuntimeAuthoritativePositionAuthority authority,
        bool usePositionFromServer = false,
        bool hasContact = true,
        Vector3? velocity = null) =>
        RuntimeAuthoritativePositionRouteClassifier.ClassifyAcceptedPosition(
            new RuntimeAcceptedPositionRouteRequest(
                authority,
                RuntimePositionEntityKind.LocalPlayer,
                RuntimeAcceptedPositionSource.PositionEvent,
                Position(),
                PlacementFrame: null,
                PositionPackVelocity: velocity,
                CommittedCellId: Cell,
                hasContact,
                PlayerDistance: 0f,
                usePositionFromServer,
                HasAnimations: false,
                default));

    private static CreateObject.ServerPosition Position(
        uint cell = Cell,
        float x = 10f,
        float rotationW = 1f) =>
        new(
            cell,
            x,
            20f,
            30f,
            rotationW,
            0f,
            0f,
            0f);
}
