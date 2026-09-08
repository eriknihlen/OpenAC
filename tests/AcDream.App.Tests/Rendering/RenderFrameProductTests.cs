using System.Numerics;
using AcDream.App.Rendering;
using AcDream.App.Rendering.Scene;
using AcDream.Core.Selection;
using AcDream.Core.World;

namespace AcDream.App.Tests.Rendering;

public sealed class RenderFrameProductTests
{
    private static readonly RenderSceneGeneration Generation =
        RenderSceneGeneration.FromRaw(7);

    [Fact]
    public void PublishesAndBorrowsTheFrameBuiltForTheSameSequence()
    {
        var exchange = new RenderFrameExchange();
        RenderFrameWriter writer = exchange.BeginBuild(Generation, 41);
        RenderProjectionRecord outdoor = Projection(1);
        RenderProjectionRecord cellA = Projection(2);
        RenderProjectionRecord cellB = Projection(3);
        RenderProjectionRecord dynamic = Projection(4);
        writer.AddOutdoor(in outdoor);
        writer.AddCellRange(0x12340102, 5, [cellA, cellB]);
        writer.AddDynamic(in dynamic);
        writer.AddTransform(new RenderFrameTransformRecord(
            dynamic.Id,
            0,
            Matrix4x4.CreateTranslation(1, 2, 3)));
        writer.AddEntityCandidate(in dynamic, animated: true);
        writer.AddClassification(new RenderFrameClassificationRecord(
            outdoor.Id,
            0,
            0,
            RenderFrameBlendClass.Opaque,
            1f));
        writer.AddClassification(new RenderFrameClassificationRecord(
            dynamic.Id,
            1,
            0,
            RenderFrameBlendClass.Alpha,
            0.5f));
        writer.AddLightSet(new RenderFrameObjectLightSet(
            dynamic.Id,
            0, 1, 2, 3, 4, 5, 6, 7));
        writer.AddSelectionPart(new RenderFrameSelectionPart(
            dynamic.Id,
            0x50000001,
            4,
            0,
            0x01000001,
            Matrix4x4.Identity,
            SelectionMesh()));
        writer.AddRouteRange(
            RenderFrameCandidateRoute.LandscapeOutdoorStatic,
            0,
            0,
            [outdoor]);
        var digest = new RenderSceneDigest(
            Generation,
            new RenderProjectionCounts(Total: 4, 1, 2, 1, 0, 0),
            new RenderSceneHash128(11, 12));
        writer.SetSourceDigest(in digest);
        writer.Publish();

        RenderFrameView view = exchange.BorrowLatest(Generation, 41);

        Assert.Equal((ulong)41, view.FrameSequence);
        Assert.Equal(Generation, view.Generation);
        Assert.Equal(outdoor.Id, view.OutdoorStaticCandidates[0].Id);
        Assert.Equal([cellA.Id, cellB.Id],
            view.CellStaticCandidates.ToArray().Select(static item => item.Id));
        Assert.Equal(new RenderFrameCellRange(0x12340102, 5, 0, 2),
            view.CellRanges[0]);
        Assert.Equal(dynamic.Id, view.DynamicCandidates[0].Id);
        Assert.Equal(1, view.Transforms.Length);
        Assert.Equal(2, view.Classifications.Length);
        Assert.Equal(1, view.LightSets.Length);
        Assert.Equal(1, view.SelectionParts.Length);
        Assert.Equal(outdoor.Id, view.RouteCandidates[0].Id);
        Assert.Equal(
            new RenderFrameCandidateRange(
                RenderFrameCandidateRoute.LandscapeOutdoorStatic,
                0,
                0,
                0,
                1),
            view.RouteRanges[0]);
        Assert.Equal(
            new RenderFrameDiagnosticCounts(
                1, 2, 1, 1, 1, 1, 1, 1, 1, 1, 1),
            view.DiagnosticCounts);
        RenderFrameEntityCandidate packed = Assert.Single(
            view.EntityCandidates.ToArray());
        Assert.Equal(dynamic.Id, packed.Projection.Id);
        Assert.True(packed.Animated);
        RenderFrameMeshPart part = Assert.Single(view.MeshParts.ToArray());
        Assert.Equal(dynamic.Id, part.ProjectionId);
        Assert.Equal(0, part.PartIndex);
        Assert.Equal(digest, view.SourceDigest);

        exchange.Release(in view);
    }

    [Fact]
    public void BuildsInOtherArenaWhilePriorFrameRemainsBorrowed()
    {
        var exchange = new RenderFrameExchange();
        PublishEmpty(exchange, Generation, 1);
        RenderFrameView first = exchange.BorrowLatest(Generation, 1);

        PublishEmpty(exchange, Generation, 2);

        Assert.Equal((ulong)1, first.FrameSequence);
        RenderFrameView second = exchange.BorrowLatest(Generation, 2);
        Assert.Equal((ulong)2, second.FrameSequence);
        Assert.Throws<InvalidOperationException>(
            () => exchange.BeginBuild(Generation, 3));

        exchange.Release(in first);
        exchange.Release(in second);
    }

    [Fact]
    public void ReuseInvalidatesEveryCopyOfReleasedView()
    {
        var exchange = new RenderFrameExchange();
        PublishEmpty(exchange, Generation, 1);
        RenderFrameView first = exchange.BorrowLatest(Generation, 1);
        RenderFrameView copy = first;
        exchange.Release(in first);

        Assert.Throws<InvalidOperationException>(() => _ = copy.FrameSequence);

        PublishEmpty(exchange, Generation, 2);
        RenderFrameView second = exchange.BorrowLatest(Generation, 2);
        exchange.Release(in second);
        PublishEmpty(exchange, Generation, 3);

        Assert.Throws<InvalidOperationException>(() => _ = copy.Generation);
    }

    [Fact]
    public void AbortDoesNotPublishIncompleteFrame()
    {
        var exchange = new RenderFrameExchange();
        PublishEmpty(exchange, Generation, 1);
        RenderFrameWriter writer = exchange.BeginBuild(Generation, 2);
        RenderProjectionRecord record = Projection(8);
        writer.AddOutdoor(in record);
        writer.Abort();

        RenderFrameView prior = exchange.BorrowLatest(Generation, 1);
        Assert.Equal(0, prior.OutdoorStaticCandidates.Length);
        exchange.Release(in prior);

        PublishEmpty(exchange, Generation, 2);
        RenderFrameView replacement = exchange.BorrowLatest(Generation, 2);
        Assert.Equal(0, replacement.OutdoorStaticCandidates.Length);
        exchange.Release(in replacement);
    }

    [Fact]
    public void BorrowRequiresExactGenerationAndFrameSequence()
    {
        var exchange = new RenderFrameExchange();
        PublishEmpty(exchange, Generation, 9);

        Assert.Throws<InvalidOperationException>(
            () => exchange.BorrowLatest(
                RenderSceneGeneration.FromRaw(8),
                9));
        Assert.Throws<InvalidOperationException>(
            () => exchange.BorrowLatest(Generation, 10));

        RenderFrameView view = exchange.BorrowLatest(Generation, 9);
        exchange.Release(in view);
    }

    [Fact]
    public void SourceDigestMustMatchArenaGeneration()
    {
        var exchange = new RenderFrameExchange();
        RenderFrameWriter writer = exchange.BeginBuild(Generation, 1);
        var digest = new RenderSceneDigest(
            RenderSceneGeneration.FromRaw(8),
            default,
            default);

        Assert.Throws<InvalidOperationException>(
            () => writer.SetSourceDigest(in digest));
        writer.Abort();
    }

    [Fact]
    public void PublishRequiresSourceDigest()
    {
        var exchange = new RenderFrameExchange();
        RenderFrameWriter writer = exchange.BeginBuild(Generation, 1);

        Assert.Throws<InvalidOperationException>(writer.Publish);
        writer.Abort();
    }

    [Fact]
    public void WarmProductBuildAndBorrowAllocateNothing()
    {
        var exchange = new RenderFrameExchange();
        RenderProjectionRecord record = Projection(1);
        RenderFrameSelectionPart selection = new(
            record.Id,
            1,
            1,
            0,
            1,
            Matrix4x4.Identity,
            SelectionMesh());

        ulong nextSequence = 1;
        ZeroAllocationProbe.AssertAllocatesNothing(
            "RenderFrameExchange build and borrow",
            () => BuildPopulated(exchange, nextSequence++, in record, in selection));
    }

    private static void BuildPopulated(
        RenderFrameExchange exchange,
        ulong sequence,
        in RenderProjectionRecord record,
        in RenderFrameSelectionPart selection)
    {
        RenderFrameWriter writer = exchange.BeginBuild(Generation, sequence);
        writer.AddOutdoor(in record);
        writer.AddCellRange(0x01010001, 0, in record);
        writer.AddDynamic(in record);
        writer.AddTransform(new RenderFrameTransformRecord(
            record.Id,
            0,
            Matrix4x4.Identity));
        writer.AddEntityCandidate(in record, animated: false);
        writer.AddClassification(new RenderFrameClassificationRecord(
            record.Id,
            0,
            0,
            RenderFrameBlendClass.Opaque,
            1f));
        writer.AddLightSet(new RenderFrameObjectLightSet(
            record.Id,
            0, 1, 2, 3, 4, 5, 6, 7));
        writer.AddSelectionPart(in selection);
        writer.SetSourceDigest(new RenderSceneDigest(
            Generation,
            new RenderProjectionCounts(3, 1, 1, 1, 0, 0),
            default));
        writer.Publish();
        RenderFrameView view = exchange.BorrowLatest(Generation, sequence);
        _ = view.DiagnosticCounts;
        exchange.Release(in view);
    }

    private static void PublishEmpty(
        RenderFrameExchange exchange,
        RenderSceneGeneration generation,
        ulong sequence)
    {
        RenderFrameWriter writer = exchange.BeginBuild(generation, sequence);
        writer.SetSourceDigest(new RenderSceneDigest(
            generation,
            default,
            default));
        writer.Publish();
    }

    private static RenderProjectionRecord Projection(ulong value) =>
        new RenderProjectionRecord() with
        {
            Id = RenderProjectionId.FromRaw(value),
            OwnerIncarnation = RenderOwnerIncarnation.FromRaw(1),
            MeshSet = new RenderMeshSet(
                RenderAssetHandle.FromRaw(value),
                1,
                1),
            Material = new RenderMaterialVariant(0, 0, 1),
            Transform = new RenderTransform(Matrix4x4.Identity),
            PreviousTransform = new PreviousRenderTransform(Matrix4x4.Identity),
            Flags = RenderProjectionFlags.Draw,
            EntityPayload = new RenderEntityPayload(
                [new MeshRef((uint)value, Matrix4x4.Identity)],
                PaletteOverride: null,
                IsBuildingShell: false),
        };

    private static RetailSelectionMesh SelectionMesh() =>
        new(
            Vector3.Zero,
            1f,
            [new RetailSelectionPolygon(
                [Vector3.Zero, Vector3.UnitX, Vector3.UnitY],
                SingleSided: false)]);
}
