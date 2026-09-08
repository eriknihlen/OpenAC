using AcDream.App.Rendering.Scene;

namespace AcDream.App.Tests.Rendering;

public sealed class RenderFrameRouteOwnerSelectorTests
{
    private static readonly RenderSceneGeneration Generation =
        RenderSceneGeneration.FromRaw(1);

    [Fact]
    public void ReplaceAndUnionUseExactRouteIdentityAndDeduplicateOwners()
    {
        var exchange = new RenderFrameExchange();
        PublishRoutes(exchange, sequence: 1);
        RenderFrameView view = exchange.BorrowLatest(Generation, 1);
        var owners = new HashSet<uint> { 999 };

        RenderFrameRouteOwnerSelector.Replace(
            owners,
            in view,
            RenderFrameCandidateRoute.LandscapeOutdoorStatic,
            routeIndex: 0,
            cellId: 0);
        RenderFrameRouteOwnerSelector.Union(
            owners,
            in view,
            RenderFrameCandidateRoute.LandscapeOutsideDynamic,
            routeIndex: 0,
            cellId: 0);

        Assert.Equal([10u, 20u, 30u], owners.Order());
        exchange.Release(in view);
    }

    [Fact]
    public void ExceptRouteRemovesOwnersAcrossEveryRangeOfThatRoute()
    {
        var exchange = new RenderFrameExchange();
        PublishRoutes(exchange, sequence: 1);
        RenderFrameView view = exchange.BorrowLatest(Generation, 1);
        var owners = new HashSet<uint>();

        RenderFrameRouteOwnerSelector.Replace(
            owners,
            in view,
            RenderFrameCandidateRoute.DynamicLast,
            routeIndex: 0,
            cellId: 0);
        RenderFrameRouteOwnerSelector.ExceptRoute(
            owners,
            in view,
            RenderFrameCandidateRoute.LandscapeOutsideDynamic);

        Assert.Equal([50u], owners);
        exchange.Release(in view);
    }

    [Fact]
    public void MissingRouteClearsOnReplaceAndDoesNotMutateOnUnion()
    {
        var exchange = new RenderFrameExchange();
        PublishRoutes(exchange, sequence: 1);
        RenderFrameView view = exchange.BorrowLatest(Generation, 1);
        var owners = new HashSet<uint> { 10, 20 };

        RenderFrameRouteOwnerSelector.Union(
            owners,
            in view,
            RenderFrameCandidateRoute.LookInObject,
            routeIndex: 9,
            cellId: 0x01010009);
        Assert.Equal([10u, 20u], owners.Order());

        RenderFrameRouteOwnerSelector.Replace(
            owners,
            in view,
            RenderFrameCandidateRoute.LookInObject,
            routeIndex: 9,
            cellId: 0x01010009);
        Assert.Empty(owners);
        exchange.Release(in view);
    }

    [Fact]
    public void WarmSelectionAllocatesNothing()
    {
        var exchange = new RenderFrameExchange();
        PublishRoutes(exchange, sequence: 1);
        RenderFrameView view = exchange.BorrowLatest(Generation, 1);
        var owners = new HashSet<uint>();
        owners.EnsureCapacity(16);

        RenderFrameView captured = view;
        ZeroAllocationProbe.AssertAllocatesNothing(
            "RenderFrameRouteOwnerSelector.SelectOwners",
            () => SelectOwners(owners, in captured));

        Assert.Equal([50u], owners);
        exchange.Release(in view);
    }

    private static void SelectOwners(
        HashSet<uint> owners,
        in RenderFrameView view)
    {
        RenderFrameRouteOwnerSelector.Replace(
            owners,
            in view,
            RenderFrameCandidateRoute.DynamicLast,
            routeIndex: 0,
            cellId: 0);
        RenderFrameRouteOwnerSelector.ExceptRoute(
            owners,
            in view,
            RenderFrameCandidateRoute.LandscapeOutsideDynamic);
    }

    private static void PublishRoutes(
        RenderFrameExchange exchange,
        ulong sequence)
    {
        RenderProjectionRecord owner10 = Projection(1, 10);
        RenderProjectionRecord owner20 = Projection(2, 20);
        RenderProjectionRecord owner30 = Projection(3, 30);
        RenderProjectionRecord owner40 = Projection(4, 40);
        RenderProjectionRecord owner50 = Projection(5, 50);
        RenderProjectionRecord anonymous = Projection(6, 0);
        RenderFrameWriter writer =
            exchange.BeginBuild(Generation, sequence);
        writer.AddRouteRange(
            RenderFrameCandidateRoute.LandscapeOutdoorStatic,
            0,
            0,
            [owner10, owner20, anonymous]);
        writer.AddRouteRange(
            RenderFrameCandidateRoute.LandscapeOutsideDynamic,
            0,
            0,
            [owner20, owner30]);
        writer.AddRouteRange(
            RenderFrameCandidateRoute.LandscapeOutsideDynamic,
            1,
            0,
            [owner40]);
        writer.AddRouteRange(
            RenderFrameCandidateRoute.DynamicLast,
            0,
            0,
            [owner20, owner40, owner50]);
        writer.SetSourceDigest(
            new RenderSceneDigest(
                Generation,
                default,
                default));
        writer.Publish();
    }

    private static RenderProjectionRecord Projection(
        ulong projectionId,
        uint localEntityId) =>
        new()
        {
            Id = RenderProjectionId.FromRaw(projectionId),
            OwnerIncarnation = RenderOwnerIncarnation.FromRaw(1),
            Source = new RenderSourceMetadata(
                LocalEntityId: localEntityId,
                ServerGuid: 0,
                SourceId: 0,
                ParentCellId: 0,
                EffectCellId: 0,
                BuildingShellAnchorCellId: 0,
                TransformFingerprint: default,
                GeometryFingerprint: default,
                AppearanceFingerprint: default),
        };
}
