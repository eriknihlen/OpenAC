using System.Collections.ObjectModel;
using System.Diagnostics.CodeAnalysis;
using System.Numerics;
using System.Reflection;
using System.Runtime.InteropServices;
using AcDream.App.Rendering;
using AcDream.App.Rendering.Gpu;
using AcDream.App.Rendering.Gpu.Vk;
using AcDream.App.Rendering.Wb;
using AcDream.App.Rendering.Walk;
using AcDream.App.Tests.Rendering.Gpu;
using AcDream.Content;
using AcDream.Core.Meshing;
using DatReaderWriter;
using DatReaderWriter.DBObjs;
using DatReaderWriter.Enums;
using DatReaderWriter.Lib.IO;
using Microsoft.Extensions.Logging.Abstractions;

namespace AcDream.App.Tests.Rendering.Walk;

public sealed class OrderPreservingSubmitterTests
{
    private static OrderedDrawCommand MakeCommand(
        int index,
        WalkDrawStage stage = WalkDrawStage.Terrain,
        TranslucencyKind translucency = TranslucencyKind.Opaque,
        CullMode cullMode = CullMode.CounterClockwise,
        uint detailCategory = 0,
        RetailSetSurfaceMaterialState? materialState = null) =>
        new(
            Key: new GroupKey(
                FirstIndex: (uint)index * 3,
                BaseVertex: index * 4,
                IndexCount: 3,
                TextureSlot: new GpuTextureSlot((uint)index),
                TextureLayer: 0,
                Translucency: translucency,
                MaterialState: materialState ?? RetailSetSurfaceMaterialState.Opaque,
                FoliageFlags: 0,
                CullMode: cullMode),
            Transform: Matrix4x4.CreateTranslation(index, index * 2, index * 3),
            Stage: stage,
            CellId: 0x8C040100u + (uint)index,
            ClipSlot: 0,
            Lights: WbDrawDispatcher.InstanceLightSet.Disabled,
            IndoorFlag: 0,
            Alpha: 1f,
            SelectionLighting: Vector2.Zero,
            DetailCategory: detailCategory);

    private static OrderedDrawStream StreamOf(params OrderedDrawCommand[] commands)
    {
        var stream = new OrderedDrawStream();
        foreach (OrderedDrawCommand command in commands)
            stream.Append(command);
        return stream;
    }

    // ── Pure BuildOrderedMergeRuns — no GPU device ─────────────────────────

    [Fact]
    public void BuildOrderedMergeRuns_MergesThreeAdjacentSameStateCommandsIntoOneRun()
    {
        OrderedDrawStream stream = StreamOf(
            MakeCommand(0), MakeCommand(1), MakeCommand(2));

        List<WbDrawDispatcher.OrderedMergeRun> runs =
            WbDrawDispatcher.BuildOrderedMergeRuns(stream);

        WbDrawDispatcher.OrderedMergeRun run = Assert.Single(runs);
        Assert.Equal(0, run.FirstCommand);
        Assert.Equal(3, run.CommandCount);
    }

    [Fact]
    public void BuildOrderedMergeRuns_SplitsOnAPipelineBucketChange()
    {
        OrderedDrawStream stream = StreamOf(
            MakeCommand(0, translucency: TranslucencyKind.Opaque),
            MakeCommand(1, translucency: TranslucencyKind.Opaque),
            MakeCommand(2, translucency: TranslucencyKind.AlphaBlend));

        List<WbDrawDispatcher.OrderedMergeRun> runs =
            WbDrawDispatcher.BuildOrderedMergeRuns(stream);

        Assert.Equal(
            [
                new WbDrawDispatcher.OrderedMergeRun(0, 2),
                new WbDrawDispatcher.OrderedMergeRun(2, 1),
            ],
            runs);
    }

    [Fact]
    public void BuildOrderedMergeRuns_SplitsOnACullModeChange()
    {
        OrderedDrawStream stream = StreamOf(
            MakeCommand(0, cullMode: CullMode.None),
            MakeCommand(1, cullMode: CullMode.None),
            MakeCommand(2, cullMode: CullMode.Clockwise));

        List<WbDrawDispatcher.OrderedMergeRun> runs =
            WbDrawDispatcher.BuildOrderedMergeRuns(stream);

        Assert.Equal(
            [
                new WbDrawDispatcher.OrderedMergeRun(0, 2),
                new WbDrawDispatcher.OrderedMergeRun(2, 1),
            ],
            runs);
    }

    [Fact]
    public void BuildOrderedMergeRuns_SplitsOnAStageChangeEvenWithIdenticalMaterialState()
    {
        OrderedDrawStream stream = StreamOf(
            MakeCommand(0, stage: WalkDrawStage.Terrain),
            MakeCommand(1, stage: WalkDrawStage.CellStatic));

        List<WbDrawDispatcher.OrderedMergeRun> runs =
            WbDrawDispatcher.BuildOrderedMergeRuns(stream);

        Assert.Equal(
            [
                new WbDrawDispatcher.OrderedMergeRun(0, 1),
                new WbDrawDispatcher.OrderedMergeRun(1, 1),
            ],
            runs);
    }

    [Fact]
    public void BuildOrderedMergeRuns_ADetailCategoryCommandIsAlwaysSolo()
    {
        OrderedDrawStream stream = StreamOf(
            MakeCommand(0),
            MakeCommand(1, detailCategory: 1),
            MakeCommand(2));

        List<WbDrawDispatcher.OrderedMergeRun> runs =
            WbDrawDispatcher.BuildOrderedMergeRuns(stream);

        Assert.Equal(
            [
                new WbDrawDispatcher.OrderedMergeRun(0, 1),
                new WbDrawDispatcher.OrderedMergeRun(1, 1),
                new WbDrawDispatcher.OrderedMergeRun(2, 1),
            ],
            runs);
    }

    [Fact]
    public void BuildOrderedMergeRuns_ThrowsNotSupportedForAPortalPunchCommand()
    {
        OrderedDrawStream stream = StreamOf(
            MakeCommand(0, stage: WalkDrawStage.Terrain),
            MakeCommand(1, stage: WalkDrawStage.PortalPunch));

        Assert.Throws<NotSupportedException>(
            () => WbDrawDispatcher.BuildOrderedMergeRuns(stream));
    }

    [Fact]
    public void BuildOrderedMergeRuns_EveryCommandBelongsToExactlyOneRunInOrderWithNoGaps()
    {
        OrderedDrawStream stream = StreamOf(
            MakeCommand(0, stage: WalkDrawStage.Terrain, translucency: TranslucencyKind.Opaque, cullMode: CullMode.None),
            MakeCommand(1, stage: WalkDrawStage.Terrain, translucency: TranslucencyKind.Opaque, cullMode: CullMode.None),
            MakeCommand(2, stage: WalkDrawStage.Terrain, translucency: TranslucencyKind.AlphaBlend, cullMode: CullMode.None),
            MakeCommand(3, stage: WalkDrawStage.Terrain, translucency: TranslucencyKind.AlphaBlend, cullMode: CullMode.Clockwise),
            MakeCommand(4, stage: WalkDrawStage.CellStatic, translucency: TranslucencyKind.AlphaBlend, cullMode: CullMode.Clockwise),
            MakeCommand(5, stage: WalkDrawStage.CellStatic, translucency: TranslucencyKind.AlphaBlend, cullMode: CullMode.Clockwise, detailCategory: 1),
            MakeCommand(6, stage: WalkDrawStage.CellStatic, translucency: TranslucencyKind.AlphaBlend, cullMode: CullMode.Clockwise));

        List<WbDrawDispatcher.OrderedMergeRun> runs =
            WbDrawDispatcher.BuildOrderedMergeRuns(stream);

        int coveredThrough = 0;
        int totalCommands = 0;
        foreach (WbDrawDispatcher.OrderedMergeRun run in runs)
        {
            Assert.Equal(coveredThrough, run.FirstCommand);
            Assert.True(run.CommandCount > 0);
            coveredThrough = run.FirstCommand + run.CommandCount;
            totalCommands += run.CommandCount;
        }
        Assert.Equal(stream.Count, coveredThrough);
        Assert.Equal(stream.Count, totalCommands);
    }


    private static void PrepareAndDrawWhole(WbDrawDispatcher dispatcher, DrawScope draw, OrderedDrawStream stream)
    {
        dispatcher.PrepareOrderedStream(draw.Frame, stream, Matrix4x4.Identity);
        if (stream.Count > 0)
            dispatcher.DrawOrderedRange(draw.Pass, 0, stream.Count);
    }

    [Fact]
    public void PrepareThenDraw_AlternatingStateCommandsRecordOneDrawEachInOrder()
    {
        using var fx = new DispatcherFixture();
        using DrawScope draw = fx.BeginDraw();

        OrderedDrawStream stream = StreamOf(
            MakeCommand(0, translucency: TranslucencyKind.Opaque),
            MakeCommand(1, translucency: TranslucencyKind.AlphaBlend),
            MakeCommand(2, translucency: TranslucencyKind.Opaque),
            MakeCommand(3, translucency: TranslucencyKind.AlphaBlend));

        PrepareAndDrawWhole(fx.Dispatcher, draw, stream);

        List<(int Start, int Count)> ranges = DecodeDrawRanges(fx.Device);
        Assert.Equal([(0, 1), (1, 1), (2, 1), (3, 1)], ranges);
    }

    [Fact]
    public void PrepareThenDraw_MergesAdjacentSameStateCommandsIntoOneMultiDrawIndirect()
    {
        using var fx = new DispatcherFixture();
        using DrawScope draw = fx.BeginDraw();

        OrderedDrawStream stream = StreamOf(
            MakeCommand(0), MakeCommand(1), MakeCommand(2));

        PrepareAndDrawWhole(fx.Dispatcher, draw, stream);

        List<(int Start, int Count)> ranges = DecodeDrawRanges(fx.Device);
        Assert.Equal([(0, 3)], ranges);
    }

    [Fact]
    public void PrepareThenDraw_CullModeChangeRecordsSeparateCullCallsAndSplitsTheDraw()
    {
        using var fx = new DispatcherFixture();
        using DrawScope draw = fx.BeginDraw();

        OrderedDrawStream stream = StreamOf(
            MakeCommand(0, cullMode: CullMode.None),
            MakeCommand(1, cullMode: CullMode.None),
            MakeCommand(2, cullMode: CullMode.Clockwise));

        PrepareAndDrawWhole(fx.Dispatcher, draw, stream);

        Assert.Equal([(0, 2), (2, 1)], DecodeDrawRanges(fx.Device));

        List<GpuCullMode> cullCalls =
            [.. fx.Device.Calls.OfType<GpuRecordedCullMode>().Select(c => c.CullMode)];
        Assert.Equal([GpuCullMode.None, GpuCullMode.Front], cullCalls);
    }

    [Fact]
    public void PrepareThenDraw_StageChangeSplitsTheDrawEvenWithIdenticalMaterialState()
    {
        using var fx = new DispatcherFixture();
        using DrawScope draw = fx.BeginDraw();

        OrderedDrawStream stream = StreamOf(
            MakeCommand(0, stage: WalkDrawStage.Terrain),
            MakeCommand(1, stage: WalkDrawStage.CellStatic));

        PrepareAndDrawWhole(fx.Dispatcher, draw, stream);

        Assert.Equal([(0, 1), (1, 1)], DecodeDrawRanges(fx.Device));
    }

    [Theory]
    [InlineData(false, 1)]
    [InlineData(false, 4)]
    [InlineData(true, 1)]
    [InlineData(true, 4)]
    public void PrepareThenDraw_OrdinaryBuildingClipBuildingOrdinary_ArmsOnePassWithoutOpaqueCoverage(
        bool atmospheric,
        int sampleCount)
    {
        foreach (bool paletted in new[] { false, true })
        {
            using var fx = new DispatcherFixture(
                detailAvailable: true,
                detailEnabled: true,
                atmospheric: atmospheric,
                sampleCount: sampleCount);
            using DrawScope draw = fx.BeginDraw();

            OrderedDrawStream stream = StreamOf(
                MakeCommand(0),
                MakeCommand(1, detailCategory: 1),
                MakeCommand(
                    2,
                    translucency: TranslucencyKind.ClipMap,
                    detailCategory: 1,
                    materialState: RetailSetSurfaceMaterialState.Resolve(
                        SurfaceType.Base1ClipMap,
                        texturePresent: true,
                        textureHasPalette: paletted)),
                MakeCommand(3));

            PrepareAndDrawWhole(fx.Dispatcher, draw, stream);

            string prefix = atmospheric ? "wb-mesh-atmospheric" : "wb-mesh";
            string suffix = sampleCount == 1 ? "-1x" : string.Empty;
            string opaque = $"{prefix}-opaque{suffix}";
            string opaqueA2c = $"{prefix}-opaque-a2c{suffix}";
            List<(GpuPushConstants Constants, int Start, int Count)> runs = DecodeRuns(fx.Device);
            Assert.Equal([(0, 1), (1, 1), (2, 1), (3, 1)],
                runs.Select(run => (run.Start, run.Count)).ToList());
            Assert.Equal((0u, 0f, 0f), DetailFields(runs[0].Constants));
            Assert.Equal((77u, 3.5f, 0f), DetailFields(runs[1].Constants));
            Assert.Equal(
                (77u, 3.5f, paletted ? 100f / 255f : 200f / 255f),
                DetailFields(runs[2].Constants));
            Assert.Equal((0u, 0f, 0f), DetailFields(runs[3].Constants));
            Assert.Equal(
                [opaque, opaqueA2c, opaque, opaqueA2c, opaqueA2c],
                fx.Device.Calls.OfType<GpuRecordedPipelineBind>()
                    .Select(call => call.PipelineName)
                    .ToArray());
            Assert.Equal(4, fx.Device.Calls.OfType<GpuRecordedMultiDrawIndirect>().Count());

            AssertOpaquePipeline(fx.Device, opaque, atmospheric, sampleCount, alphaToCoverage: false);
            AssertOpaquePipeline(fx.Device, opaqueA2c, atmospheric, sampleCount, alphaToCoverage: true);
        }
    }

    [Theory]
    [InlineData(false, 1)]
    [InlineData(false, 4)]
    [InlineData(true, 1)]
    [InlineData(true, 4)]
    public void ClassicGrouped_OrdinaryBuildingClipBuildingOrdinary_ArmsOnePassWithoutOpaqueCoverage(
        bool atmospheric,
        int sampleCount)
    {
        using var fx = new DispatcherFixture(
            detailAvailable: true,
            detailEnabled: true,
            atmospheric: atmospheric,
            sampleCount: sampleCount);
        using DrawScope draw = fx.BeginDraw();

        ExecuteClassicGroups(
            fx,
            MakeClassicGroup(0, detailCategory: 0),
            MakeClassicGroup(1, detailCategory: 1),
            MakeClassicGroup(
                2,
                detailCategory: 1,
                translucency: TranslucencyKind.ClipMap,
                materialState: RetailSetSurfaceMaterialState.Resolve(
                    SurfaceType.Base1ClipMap,
                    texturePresent: true,
                    textureHasPalette: false)),
            MakeClassicGroup(3, detailCategory: 0));

        string prefix = atmospheric ? "wb-mesh-atmospheric" : "wb-mesh";
        string suffix = sampleCount == 1 ? "-1x" : string.Empty;
        string opaque = $"{prefix}-opaque{suffix}";
        string opaqueA2c = $"{prefix}-opaque-a2c{suffix}";
        var transcript = DecodePipelineRuns(fx.Device);
        Assert.Equal([opaqueA2c, opaque, opaqueA2c, opaqueA2c],
            transcript.Select(entry => entry.Pipeline));
        Assert.All(transcript, entry => Assert.Equal(1u, entry.Draw.DrawCount));
        Assert.Equal((0u, 0f, 0f), DetailFields(transcript[0].Constants));
        Assert.Equal((77u, 3.5f, 0f), DetailFields(transcript[1].Constants));
        Assert.Equal((77u, 3.5f, 200f / 255f), DetailFields(transcript[2].Constants));
        Assert.Equal((0u, 0f, 0f), DetailFields(transcript[3].Constants));
        AssertOpaquePipeline(fx.Device, opaque, atmospheric, sampleCount, alphaToCoverage: false);
        AssertOpaquePipeline(fx.Device, opaqueA2c, atmospheric, sampleCount, alphaToCoverage: true);
    }

    [Theory]
    [InlineData(false, 1)]
    [InlineData(false, 4)]
    [InlineData(true, 1)]
    [InlineData(true, 4)]
    public void ClassicGrouped_MixedOrdinaryAndBuildingInstances_UsesNonCoverageForWholeCommand(
        bool atmospheric,
        int sampleCount)
    {
        using var fx = new DispatcherFixture(
            detailAvailable: true,
            detailEnabled: true,
            atmospheric: atmospheric,
            sampleCount: sampleCount);
        using DrawScope draw = fx.BeginDraw();
        WbDrawDispatcher.InstanceGroup mixed = MakeClassicGroup(0, detailCategory: 0);
        AppendClassicInstance(mixed, index: 1, detailCategory: 1);

        ExecuteClassicGroups(fx, mixed);

        string prefix = atmospheric ? "wb-mesh-atmospheric" : "wb-mesh";
        string suffix = sampleCount == 1 ? "-1x" : string.Empty;
        var entry = Assert.Single(DecodePipelineRuns(fx.Device));
        Assert.Equal($"{prefix}-opaque{suffix}", entry.Pipeline);
        Assert.Equal(1u, entry.Draw.DrawCount);
        Assert.Equal((77u, 3.5f, 0f), DetailFields(entry.Constants));
        Assert.Equal([0u, 1u], mixed.DetailCategories);
        Assert.Equal(2, mixed.Matrices.Count);
    }

    [Theory]
    [InlineData(false, true, true)]
    [InlineData(true, false, true)]
    [InlineData(true, true, false)]
    public void PrepareThenDraw_DetailUnavailableDisabledOrQualityA2cFalsePreservesSelection(
        bool detailAvailable,
        bool detailEnabled,
        bool alphaToCoverage)
    {
        using var fx = new DispatcherFixture(
            detailAvailable: detailAvailable,
            detailEnabled: detailEnabled,
            sampleCount: 4,
            alphaToCoverage: alphaToCoverage);
        using DrawScope draw = fx.BeginDraw();
        OrderedDrawStream stream = StreamOf(
            MakeCommand(0),
            MakeCommand(1, detailCategory: 1));

        PrepareAndDrawWhole(fx.Dispatcher, draw, stream);

        bool detailActive = detailAvailable && detailEnabled;
        string selected = alphaToCoverage ? "wb-mesh-opaque-a2c" : "wb-mesh-opaque";
        Assert.Equal(
            ["wb-mesh-opaque", selected, detailActive ? "wb-mesh-opaque" : selected],
            fx.Device.Calls.OfType<GpuRecordedPipelineBind>()
                .Select(call => call.PipelineName));
        List<(GpuPushConstants Constants, int Start, int Count)> runs = DecodeRuns(fx.Device);
        Assert.Equal([(0, 1), (1, 1)], runs.Select(run => (run.Start, run.Count)));
        Assert.Equal((0u, 0f, 0f), DetailFields(runs[0].Constants));
        Assert.Equal(
            detailActive ? (77u, 3.5f, 0f) : (0u, 0f, 0f),
            DetailFields(runs[1].Constants));
    }

    [Theory]
    [InlineData(false, true, true)]
    [InlineData(true, false, true)]
    [InlineData(true, true, false)]
    public void ClassicGrouped_DetailUnavailableDisabledOrQualityA2cFalsePreservesSelection(
        bool detailAvailable,
        bool detailEnabled,
        bool alphaToCoverage)
    {
        using var fx = new DispatcherFixture(
            detailAvailable: detailAvailable,
            detailEnabled: detailEnabled,
            sampleCount: 4,
            alphaToCoverage: alphaToCoverage);
        using DrawScope draw = fx.BeginDraw();

        ExecuteClassicGroups(fx, MakeClassicGroup(0, detailCategory: 1));

        bool detailActive = detailAvailable && detailEnabled;
        var entry = Assert.Single(DecodePipelineRuns(fx.Device));
        Assert.Equal(alphaToCoverage && !detailActive
            ? "wb-mesh-opaque-a2c"
            : "wb-mesh-opaque", entry.Pipeline);
        Assert.Equal(1u, entry.Draw.DrawCount);
        Assert.Equal(
            detailActive ? (77u, 3.5f, 0f) : (0u, 0f, 0f),
            DetailFields(entry.Constants));
    }

    [Fact]
    public void PrepareThenDraw_PureClipBuildingDetailOffRetainsAp240A2cAndNeutralDetailState()
    {
        using var fx = new DispatcherFixture(detailAvailable: true, detailEnabled: false);
        using DrawScope draw = fx.BeginDraw();
        OrderedDrawStream stream = StreamOf(
            MakeCommand(0),
            MakeCommand(
                1,
                translucency: TranslucencyKind.ClipMap,
                detailCategory: 1,
                materialState: RetailSetSurfaceMaterialState.Resolve(
                    SurfaceType.Base1ClipMap,
                    texturePresent: true,
                    textureHasPalette: true)),
            MakeCommand(2));

        PrepareAndDrawWhole(fx.Dispatcher, draw, stream);

        List<(GpuPushConstants Constants, int Start, int Count)> runs = DecodeRuns(fx.Device);
        Assert.Equal([(0, 1), (1, 1), (2, 1)],
            runs.Select(run => (run.Start, run.Count)).ToList());
        Assert.Equal(0u, runs[1].Constants.TextureIndexA);
        Assert.Equal(0f, runs[1].Constants.ParamA);
        Assert.Equal(0f, runs[1].Constants.ParamB);
        Assert.Equal(0, runs[1].Constants.RenderPass & RetailDetailTextureContract.NoFogRenderPassFlag);
        Assert.Contains(fx.Device.Calls.OfType<GpuRecordedPipelineBind>(),
            call => call.PipelineName == "wb-mesh-opaque-a2c-1x");
    }

    [Fact]
    public void AtmosphericReceiver_AdjacentOrdinaryDetailOrdinaryCommandsDoNotLeakExactState()
    {
        using var fx = new DispatcherFixture(
            detailAvailable: true,
            detailEnabled: true,
            atmospheric: true);
        using DrawScope draw = fx.BeginDraw();
        OrderedDrawStream stream = StreamOf(
            MakeCommand(0, translucency: TranslucencyKind.Additive),
            MakeCommand(
                1,
                translucency: TranslucencyKind.Additive,
                detailCategory: 1,
                materialState: RetailSetSurfaceMaterialState.Resolve(
                    SurfaceType.Additive | SurfaceType.Base1ClipMap,
                    texturePresent: true,
                    textureHasPalette: false)),
            MakeCommand(2, translucency: TranslucencyKind.Additive));

        PrepareAndDrawWhole(fx.Dispatcher, draw, stream);

        List<(GpuPushConstants Constants, int Start, int Count)> runs = DecodeRuns(fx.Device);
        Assert.Equal([(0, 1), (1, 1), (2, 1)],
            runs.Select(run => (run.Start, run.Count)).ToList());
        Assert.Equal((0u, 0f, 0f, 0), DetailState(runs[0].Constants));
        Assert.Equal((77u, 3.5f, 200f / 255f, RetailDetailTextureContract.NoFogRenderPassFlag),
            DetailState(runs[1].Constants));
        Assert.Equal((0u, 0f, 0f, 0), DetailState(runs[2].Constants));
        Assert.Contains(fx.Device.Calls.OfType<GpuRecordedPipelineBind>(),
            call => call.PipelineName == "wb-mesh-atmospheric-raw-additive-depth-write-1x");
        Assert.Contains(fx.Device.Calls.OfType<GpuRecordedPipelineBind>(),
            call => call.PipelineName == "wb-mesh-atmospheric-additive-1x");
        GpuPipelineDescription selected = fx.Device.CreatedPipelines
            .Single(pipeline => pipeline.Description.Name == "wb-mesh-atmospheric-raw-additive-depth-write-1x")
            .Description;
        Assert.Equal("mesh_atmospheric", selected.Shaders.Name);
        Assert.Equal(GpuBlendMode.RawAdditive, selected.Blend);
        Assert.True(selected.Depth.Write);
        GpuPipelineDescription adjacent = fx.Device.CreatedPipelines
            .Single(pipeline => pipeline.Description.Name == "wb-mesh-atmospheric-additive-1x")
            .Description;
        Assert.Equal(GpuBlendMode.Additive, adjacent.Blend);
        Assert.False(adjacent.Depth.Write);
    }

    private static (uint Slot, float Tiling, float Reference, int NoFog) DetailState(
        GpuPushConstants constants) =>
        (constants.TextureIndexA, constants.ParamA, constants.ParamB,
            constants.RenderPass & RetailDetailTextureContract.NoFogRenderPassFlag);

    [Theory]
    [InlineData(false, "wb-mesh")]
    [InlineData(true, "wb-mesh-atmospheric")]
    public void SetSurfacePipelineFamilies_CarryDepthWritePairsAcrossBothSampleCountsAndDispose(
        bool atmospheric,
        string prefix)
    {
        var fx = new DispatcherFixture(atmospheric: atmospheric, sampleCount: 4);
        var families = new (string Name, GpuBlendMode Blend)[]
        {
            ("alpha", GpuBlendMode.StraightAlpha),
            ("additive", GpuBlendMode.Additive),
            ("raw-additive", GpuBlendMode.RawAdditive),
            ("inverse", GpuBlendMode.InverseAlpha),
            ("inverse-additive", GpuBlendMode.InverseAdditive),
        };
        var retained = new List<RecordingGpuPipeline>();

        foreach ((string name, GpuBlendMode blend) in families)
        {
            foreach ((string suffix, int samples) in new[] { (string.Empty, 4), ("-1x", 1) })
            {
                RecordingGpuPipeline depthOff = fx.Device.CreatedPipelines.Single(
                    pipeline => pipeline.Description.Name == $"{prefix}-{name}{suffix}");
                RecordingGpuPipeline depthOn = fx.Device.CreatedPipelines.Single(
                    pipeline => pipeline.Description.Name == $"{prefix}-{name}-depth-write{suffix}");
                retained.Add(depthOff);
                retained.Add(depthOn);
                Assert.Equal(blend, depthOff.Description.Blend);
                Assert.Equal(blend, depthOn.Description.Blend);
                Assert.Equal(samples, depthOff.Description.SampleCount);
                Assert.Equal(samples, depthOn.Description.SampleCount);
                Assert.True(depthOff.Description.Depth.Test);
                Assert.True(depthOn.Description.Depth.Test);
                Assert.False(depthOff.Description.Depth.Write);
                Assert.True(depthOn.Description.Depth.Write);
                Assert.Equal(WorldDepthContract.WorldCompare, depthOff.Description.Depth.Compare);
                Assert.Equal(WorldDepthContract.WorldCompare, depthOn.Description.Depth.Compare);
            }
        }

        fx.Dispose();

        Assert.All(retained, static pipeline => Assert.True(pipeline.IsDisposed));
    }

    [Fact]
    public void SetSurfacePipelineConstructionFailureRollsBackBothSampleCountOwners()
    {
        using var device = new RecordingGpuDevice();
        device.PipelineFailure = description =>
            description.Name == "wb-mesh-inverse-depth-write-1x"
                ? new InvalidOperationException("injected pipeline failure")
                : null;

        Assert.Throws<InvalidOperationException>(() =>
            new DispatcherFixture(sampleCount: 4, device: device));

        Assert.Equal(21, device.CreatedPipelines.Count);
        Assert.All(device.CreatedPipelines, static pipeline => Assert.True(pipeline.IsDisposed));
    }

    [Fact]
    public void PrepareThenDraw_OpaqueRunUsesRenderPassZeroAndAlphaBlendRunUsesRenderPassOne()
    {
        using var fx = new DispatcherFixture();
        using DrawScope draw = fx.BeginDraw();

        OrderedDrawStream stream = StreamOf(
            MakeCommand(0, translucency: TranslucencyKind.Opaque),
            MakeCommand(1, translucency: TranslucencyKind.AlphaBlend));

        PrepareAndDrawWhole(fx.Dispatcher, draw, stream);

        List<(GpuPushConstants Constants, int Start, int Count)> runs = DecodeRuns(fx.Device);
        Assert.Equal(2, runs.Count);
        Assert.Equal(0, runs[0].Constants.RenderPass);
        Assert.Equal(1, runs[1].Constants.RenderPass);
    }

    [Fact]
    public void PrepareOrderedStream_ThrowsNotSupportedForAPortalPunchCommandBeforeAnyDraw()
    {
        using var fx = new DispatcherFixture();
        using DrawScope draw = fx.BeginDraw();

        OrderedDrawStream stream = StreamOf(
            MakeCommand(0, stage: WalkDrawStage.PortalPunch));

        Assert.Throws<NotSupportedException>(
            () => fx.Dispatcher.PrepareOrderedStream(draw.Frame, stream, Matrix4x4.Identity));

        Assert.Empty(fx.Device.Calls.OfType<GpuRecordedMultiDrawIndirect>());
        Assert.Empty(fx.Device.Calls.OfType<GpuRecordedStorageBind>());
    }

    [Fact]
    public void PrepareOrderedStream_EmptyStreamRecordsNoDraws()
    {
        using var fx = new DispatcherFixture();
        using DrawScope draw = fx.BeginDraw();

        fx.Dispatcher.PrepareOrderedStream(draw.Frame, new OrderedDrawStream(), Matrix4x4.Identity);

        Assert.Empty(fx.Device.Calls.OfType<GpuRecordedMultiDrawIndirect>());
    }


    [Fact]
    public void DrawOrderedRange_AsTwoRangesAtASegmentBoundary_MatchesOneRangeOverTheWholeStream()
    {
        OrderedDrawStream stream = StreamOf(
            MakeCommand(0, translucency: TranslucencyKind.Opaque),
            MakeCommand(1, translucency: TranslucencyKind.Opaque),
            MakeCommand(2, translucency: TranslucencyKind.Opaque),
            MakeCommand(3, translucency: TranslucencyKind.Opaque));

        using var wholeFx = new DispatcherFixture();
        using (DrawScope draw = wholeFx.BeginDraw())
        {
            wholeFx.Dispatcher.PrepareOrderedStream(draw.Frame, stream, Matrix4x4.Identity);
            wholeFx.Dispatcher.DrawOrderedRange(draw.Pass, 0, stream.Count);
        }
        List<(int Start, int Count)> wholeRanges = DecodeDrawRanges(wholeFx.Device);

        using var splitFx = new DispatcherFixture();
        using (DrawScope draw = splitFx.BeginDraw())
        {
            splitFx.Dispatcher.PrepareOrderedStream(
                draw.Frame, stream, Matrix4x4.Identity, forcedBreaksAscending: [2]);
            splitFx.Dispatcher.DrawOrderedRange(draw.Pass, 0, 2);
            splitFx.Dispatcher.DrawOrderedRange(draw.Pass, 2, 2);
        }
        List<(int Start, int Count)> splitRanges = DecodeDrawRanges(splitFx.Device);

        Assert.Equal([(0, 4)], wholeRanges);
        Assert.Equal([(0, 2), (2, 2)], splitRanges);
    }

    [Fact]
    public void DrawOrderedRange_EveryCallRebindsTheStorageSections()
    {
        using var fx = new DispatcherFixture();
        using DrawScope draw = fx.BeginDraw();

        OrderedDrawStream stream = StreamOf(
            MakeCommand(0, stage: WalkDrawStage.Terrain),
            MakeCommand(1, stage: WalkDrawStage.CellStatic));

        fx.Dispatcher.PrepareOrderedStream(draw.Frame, stream, Matrix4x4.Identity);
        fx.Dispatcher.DrawOrderedRange(draw.Pass, 0, 1);
        int boundAfterFirst = fx.Device.Calls.OfType<GpuRecordedStorageBind>().Count();
        Assert.True(boundAfterFirst > 0);

        fx.Dispatcher.DrawOrderedRange(draw.Pass, 1, 1);
        int boundAfterSecond = fx.Device.Calls.OfType<GpuRecordedStorageBind>().Count();

        Assert.Equal(boundAfterFirst * 2, boundAfterSecond);
        Assert.Equal([(0, 1), (1, 1)], DecodeDrawRanges(fx.Device));
    }

    [Fact]
    public void DrawOrderedRange_RangeExceedingThePreparedPayload_Throws()
    {
        using var fx = new DispatcherFixture();
        using DrawScope draw = fx.BeginDraw();

        OrderedDrawStream stream = StreamOf(MakeCommand(0));
        fx.Dispatcher.PrepareOrderedStream(draw.Frame, stream, Matrix4x4.Identity);

        Assert.Throws<ArgumentOutOfRangeException>(
            () => fx.Dispatcher.DrawOrderedRange(draw.Pass, 0, 2));
        Assert.Throws<ArgumentOutOfRangeException>(
            () => fx.Dispatcher.DrawOrderedRange(draw.Pass, 1, 1));
    }

    [Fact]
    public void DrawOrderedRange_BeforeAnyPrepareCallThisFrame_Throws()
    {
        using var fx = new DispatcherFixture();
        using DrawScope draw = fx.BeginDraw();

        Assert.Throws<ArgumentOutOfRangeException>(
            () => fx.Dispatcher.DrawOrderedRange(draw.Pass, 0, 1));
    }

    [Fact]
    public void DrawOrderedRange_RangeStraddlingAMergeRun_Throws()
    {
        using var fx = new DispatcherFixture();
        using DrawScope draw = fx.BeginDraw();

        OrderedDrawStream stream = StreamOf(
            MakeCommand(0), MakeCommand(1), MakeCommand(2), MakeCommand(3));
        fx.Dispatcher.PrepareOrderedStream(draw.Frame, stream, Matrix4x4.Identity);

        Assert.Throws<InvalidOperationException>(
            () => fx.Dispatcher.DrawOrderedRange(draw.Pass, 0, 2));
    }

    [Fact]
    public void PrepareThenDraw_TotalRecordedDrawCountAlwaysEqualsTheStreamCount()
    {
        using var fx = new DispatcherFixture();
        using DrawScope draw = fx.BeginDraw();

        var stream = new OrderedDrawStream();
        var stages = new[] { WalkDrawStage.Terrain, WalkDrawStage.CellStatic, WalkDrawStage.BuildingShell };
        var blends = new[]
        {
            TranslucencyKind.Opaque, TranslucencyKind.AlphaBlend,
            TranslucencyKind.Additive, TranslucencyKind.InvAlpha,
        };
        var culls = new[] { CullMode.None, CullMode.Clockwise, CullMode.CounterClockwise };
        const int commandCount = 11;
        for (int i = 0; i < commandCount; i++)
        {
            stream.Append(MakeCommand(
                i,
                stage: stages[i % stages.Length],
                translucency: blends[i % blends.Length],
                cullMode: culls[i % culls.Length],
                detailCategory: i == 5 ? 1u : 0u));
        }

        PrepareAndDrawWhole(fx.Dispatcher, draw, stream);

        List<(int Start, int Count)> ranges = DecodeDrawRanges(fx.Device);
        int sum = ranges.Sum(r => r.Count);
        Assert.Equal(commandCount, sum);

        int coveredThrough = 0;
        foreach ((int start, int count) in ranges)
        {
            Assert.Equal(coveredThrough, start);
            coveredThrough += count;
        }
        Assert.Equal(commandCount, coveredThrough);
    }

    // ── Decode helpers ──────────────────────────────────────────────────────

    [Theory]
    [InlineData(false, "mesh_modern")]
    [InlineData(true, "mesh_atmospheric")]
    public void SharedTransformPrefix_RecordsAbsoluteCommandsAndZeroBasedLocalSidecars(
        bool receiverBindingAvailable,
        string expectedShader)
    {
        using var fx = new DispatcherFixture(
            atmospheric: true,
            receiverBindingAvailable: receiverBindingAvailable);
        using DrawScope draw = fx.BeginDraw();

        Matrix4x4[] shadowPrefix =
        [
            Matrix4x4.CreateTranslation(101f, 102f, 103f),
            Matrix4x4.CreateTranslation(201f, 202f, 203f),
            Matrix4x4.CreateTranslation(301f, 302f, 303f),
        ];
        WorldTransformFrameSlice shared =
            fx.Dispatcher.BeginDirectionalShadowTransformFrame(draw.Frame, shadowPrefix);
        Assert.Equal(3u, shared.InstanceCount);

        OrderedDrawCommand first = MakeCommand(0) with
        {
            Transform = Matrix4x4.CreateTranslation(-0.5f, 1.25f, 2.5f),
            ClipSlot = 11u,
            Lights = new WbDrawDispatcher.InstanceLightSet(0, 2, 4, 6, -1, -1, -1, -1),
            IndoorFlag = 0u,
            Alpha = 0.25f,
            SelectionLighting = new Vector2(0.125f, 0.375f),
            DetailCategory = 7u,
        };
        OrderedDrawCommand second = MakeCommand(1) with
        {
            Transform = Matrix4x4.CreateTranslation(0.75f, -1.5f, 3.25f),
            ClipSlot = 22u,
            Lights = new WbDrawDispatcher.InstanceLightSet(1, 3, 5, 7, -1, -1, -1, -1),
            IndoorFlag = 1u,
            Alpha = 0.75f,
            SelectionLighting = new Vector2(0.625f, 0.875f),
            DetailCategory = 9u,
        };
        OrderedDrawStream stream = StreamOf(first, second);

        PrepareAndDrawWhole(fx.Dispatcher, draw, stream);

        Assert.Contains(
            fx.Device.Calls.OfType<GpuRecordedPipelineBind>(),
            call => fx.Device.CreatedPipelines.Single(
                pipeline => pipeline.Description.Name == call.PipelineName)
                .Description.Shaders.Name == expectedShader);

        GpuRecordedMultiDrawIndirect[] drawCalls =
            fx.Device.Calls.OfType<GpuRecordedMultiDrawIndirect>().ToArray();
        Assert.Equal(2, drawCalls.Length);
        Assert.All(drawCalls, call => Assert.Equal(1u, call.DrawCount));
        Assert.Equal(drawCalls[0].OffsetBytes + drawCalls[0].StrideBytes, drawCalls[1].OffsetBytes);
        GpuRecordedMultiDrawIndirect drawCall = drawCalls[0];
        ReadOnlySpan<DrawElementsIndirectCommand> commands = MemoryMarshal.Cast<byte, DrawElementsIndirectCommand>(
            fx.Device.RingBytes.Slice((int)drawCall.OffsetBytes, checked((int)drawCall.StrideBytes * 2)));
        Assert.Equal([3u, 4u], commands.ToArray().Select(command => command.BaseInstance).ToArray());

        GpuRecordedPushConstants pushed = fx.Device.Calls
            .TakeWhile(call => !ReferenceEquals(call, drawCall))
            .OfType<GpuRecordedPushConstants>()
            .Last();
        Assert.Equal(3u, pushed.Constants.TextureIndexB);

        AssertLocalSection(GpuBindingModel.StorageClipSlots, [11u, 22u]);
        AssertLocalSection(
            GpuBindingModel.StorageInstanceLightSets,
            [0, 2, 4, 6, -1, -1, -1, -1, 1, 3, 5, 7, -1, -1, -1, -1]);
        Assert.Equal(
            (uint)(2 * AcDream.Core.Lighting.LightManager.MaxLightsPerObject * sizeof(int)),
            LastBind(GpuBindingModel.StorageInstanceLightSets).SizeBytes);
        AssertLocalSection(GpuBindingModel.StorageInstanceIndoor, [0u, 1u]);
        AssertLocalSection(GpuBindingModel.StorageInstanceAlpha, [0.25f, 0.75f]);
        AssertLocalSection(
            GpuBindingModel.StorageInstanceSelectionLighting,
            [new Vector2(0.125f, 0.375f), new Vector2(0.625f, 0.875f)]);
        AssertLocalSection(GpuBindingModel.StorageInstanceDetailCategory, [7u, 9u]);
        Assert.Equal(
            6,
            new uint[] { 3, 5, 6, 7, 8, 9 }
                .Select(binding => LastBind(binding).OffsetBytes)
                .Distinct()
                .Count());

        GpuRecordedStorageBind transformBind = LastBind(GpuBindingModel.StorageInstances);
        Assert.Equal(shared.BaseOffsetBytes, transformBind.OffsetBytes);
        ReadOnlySpan<Matrix4x4> transforms = MemoryMarshal.Cast<byte, Matrix4x4>(
            fx.Device.RingBytes.Slice(
                (int)transformBind.OffsetBytes,
                checked((int)(5u * WorldTransformCapacityPolicy.MatrixBytes))));
        Assert.Equal(first.Transform, transforms[3]);
        Assert.Equal(second.Transform, transforms[4]);

        void AssertLocalSection<T>(uint binding, T[] expected) where T : unmanaged
        {
            GpuRecordedStorageBind bound = LastBind(binding);
            int byteCount = checked(expected.Length * Marshal.SizeOf<T>());
            Assert.Equal((uint)byteCount, bound.SizeBytes);
            Assert.Equal(
                expected,
                MemoryMarshal.Cast<byte, T>(
                    fx.Device.RingBytes.Slice((int)bound.OffsetBytes, byteCount)).ToArray());
        }

        GpuRecordedStorageBind LastBind(uint binding) => fx.Device.Calls
            .TakeWhile(call => !ReferenceEquals(call, drawCall))
            .OfType<GpuRecordedStorageBind>()
            .Last(call => call.Binding == binding);
    }

    private static List<(int Start, int Count)> DecodeDrawRanges(RecordingGpuDevice device) =>
        [.. DecodeRuns(device).Select(r => (r.Start, r.Count))];

    private static (uint TextureIndexA, float ParamA, float ParamB) DetailFields(
        GpuPushConstants constants) =>
        (constants.TextureIndexA, constants.ParamA, constants.ParamB);

    private static void AssertOpaquePipeline(
        RecordingGpuDevice device,
        string name,
        bool atmospheric,
        int sampleCount,
        bool alphaToCoverage)
    {
        GpuPipelineDescription description = device.CreatedPipelines
            .Single(pipeline => pipeline.Description.Name == name)
            .Description;
        Assert.Equal(atmospheric ? "mesh_atmospheric" : "mesh_modern", description.Shaders.Name);
        Assert.Equal(GpuBlendMode.None, description.Blend);
        Assert.Equal(alphaToCoverage, description.AlphaToCoverage);
        Assert.Equal(sampleCount, description.SampleCount);
        Assert.True(description.Depth.Test);
        Assert.True(description.Depth.Write);
        Assert.Equal(WorldDepthContract.WorldCompare, description.Depth.Compare);
    }

    private static WbDrawDispatcher.InstanceGroup MakeClassicGroup(
        int index,
        uint detailCategory,
        TranslucencyKind translucency = TranslucencyKind.Opaque,
        RetailSetSurfaceMaterialState? materialState = null)
    {
        var group = new WbDrawDispatcher.InstanceGroup
        {
            FirstIndex = (uint)index * 3,
            BaseVertex = index * 4,
            IndexCount = 3,
            TextureSlot = new GpuTextureSlot((uint)index),
            TextureLayer = 0,
            Translucency = translucency,
            MaterialState = materialState ?? RetailSetSurfaceMaterialState.Opaque,
            SurfaceOpacity = 1f,
            CullMode = CullMode.CounterClockwise,
            FoliageFlags = 0,
        };
        group.Matrices.Add(Matrix4x4.CreateTranslation(index, index * 2, index * 3));
        group.SubmissionOrders.Add(index);
        group.Slots.Add(0);
        group.LightSets.Add(WbDrawDispatcher.InstanceLightSet.Disabled);
        group.IndoorFlags.Add(0);
        group.DetailCategories.Add(detailCategory);
        group.Opacities.Add(1f);
        group.SelectionLighting.Add(Vector2.Zero);
        return group;
    }

    private static void AppendClassicInstance(
        WbDrawDispatcher.InstanceGroup group,
        int index,
        uint detailCategory)
    {
        group.Matrices.Add(Matrix4x4.CreateTranslation(index, index * 2, index * 3));
        group.SubmissionOrders.Add(index);
        group.Slots.Add(0);
        group.LightSets.Add(WbDrawDispatcher.InstanceLightSet.Disabled);
        group.IndoorFlags.Add(0);
        group.DetailCategories.Add(detailCategory);
        group.Opacities.Add(1f);
        group.SelectionLighting.Add(Vector2.Zero);
    }

    private static void ExecuteClassicGroups(
        DispatcherFixture fixture,
        params WbDrawDispatcher.InstanceGroup[] groups)
    {
        fixture.Dispatcher.BeginFrame(frameSlot: 0);
        MethodInfo execute = typeof(WbDrawDispatcher).GetMethod(
            "ExecuteClassifiedGroups",
            BindingFlags.Instance | BindingFlags.NonPublic)!;
        execute.Invoke(
            fixture.Dispatcher,
            [
                Matrix4x4.Identity,
                Vector3.Zero,
                0u,
                groups,
                WbDrawDispatcher.EntitySet.All,
                groups.Length,
                groups.Length,
                false,
                false,
            ]);
    }

    private static List<(
        string Pipeline,
        GpuPushConstants Constants,
        GpuRecordedMultiDrawIndirect Draw)> DecodePipelineRuns(RecordingGpuDevice device)
    {
        var transcript = new List<(
            string Pipeline,
            GpuPushConstants Constants,
            GpuRecordedMultiDrawIndirect Draw)>();
        IReadOnlyList<GpuRecordedCall> calls = device.Calls;
        for (int i = 0; i < calls.Count; i++)
        {
            if (calls[i] is not GpuRecordedMultiDrawIndirect draw)
                continue;

            string? pipeline = null;
            GpuPushConstants? constants = null;
            for (int prior = i - 1; prior >= 0 && (pipeline is null || constants is null); prior--)
            {
                if (pipeline is null && calls[prior] is GpuRecordedPipelineBind bind)
                    pipeline = bind.PipelineName;
                if (constants is null && calls[prior] is GpuRecordedPushConstants push)
                    constants = push.Constants;
            }

            Assert.NotNull(pipeline);
            Assert.NotNull(constants);
            transcript.Add((pipeline!, constants!.Value, draw));
        }

        return transcript;
    }

    private static List<(GpuPushConstants Constants, int Start, int Count)> DecodeRuns(
        RecordingGpuDevice device)
    {
        GpuPushConstants? lastConstants = null;
        uint? commandBase = null;
        var result = new List<(GpuPushConstants, int, int)>();
        foreach (var call in device.Calls)
        {
            if (call is GpuRecordedPushConstants pc)
            {
                lastConstants = pc.Constants;
            }
            else if (call is GpuRecordedMultiDrawIndirect mdi)
            {
                Assert.Equal((uint)WbDrawDispatcher.DrawCommandStride, mdi.StrideBytes);
                commandBase ??= mdi.OffsetBytes;
                int start = (int)((mdi.OffsetBytes - commandBase.Value) / mdi.StrideBytes);
                Assert.NotNull(lastConstants);
                result.Add((lastConstants!.Value, start, (int)mdi.DrawCount));
            }
        }
        return result;
    }

    // ── Fixture: a real WbDrawDispatcher against RecordingGpuDevice ─────────

    private readonly struct DrawScope : IDisposable
    {
        private readonly IDisposable _publication;
        private readonly IGpuPassEncoder _pass;

        public DrawScope(IGpuFrame frame, IGpuPassEncoder pass, IDisposable publication)
        {
            Frame = frame;
            _pass = pass;
            _publication = publication;
        }

        public IGpuFrame Frame { get; }

        public IGpuPassEncoder Pass => _pass;

        public void Dispose()
        {
            _publication.Dispose();
            _pass.Dispose();
        }
    }

    private sealed class DispatcherFixture : IDisposable
    {
        private readonly WbMeshAdapter _meshAdapter;
        private readonly TextureCache _textures;

        public DispatcherFixture(
            bool detailAvailable = false,
            bool detailEnabled = false,
            bool atmospheric = false,
            bool receiverBindingAvailable = true,
            int sampleCount = 1,
            bool alphaToCoverage = true,
            RecordingGpuDevice? device = null)
        {
            Device = device ?? new RecordingGpuDevice();
            FrameLifetime = new GpuDeviceFrameLifetime(Device);
            Scope = new VulkanWorldPassScope(sampleCount);
            _textures = new TextureCache(Device, new NoopDatReaderWriter());
            _meshAdapter = new WbMeshAdapter(
                Device,
                new NoopDatReaderWriter(),
                new NullPreparedAssetSource(),
                NullLogger<WbMeshAdapter>.Instance,
                Device.Retirement);
            var entitySpawnAdapter = new EntitySpawnAdapter(
                _textures,
                _ => throw new NotSupportedException(
                    "Not exercised by SubmitOrderedStream tests."));

            Dispatcher = new WbDrawDispatcher(
                Device,
                FrameLifetime,
                Scope,
                _textures,
                _meshAdapter,
                entitySpawnAdapter,
                new EntityClassificationCache(),
                new AcDream.Core.Rendering.TranslucencyFadeManager(),
                buildingDetail: detailAvailable
                    ? new TerrainAtlas.RetailDetailTextureBinding(
                        new GpuTextureSlot(77), 3.5f, 0x05000001, 0x08000001, 16, 16)
                    : default,
                buildingDetailEnabled: () => detailEnabled);
            Dispatcher.AlphaToCoverage = alphaToCoverage;
            if (atmospheric)
            {
                var source = new BindableAtmosphericSource(receiverBindingAvailable);
                WbDrawDispatcher.DirectionalShadowReceiverPipelineState candidate =
                    Assert.IsType<WbDrawDispatcher.DirectionalShadowReceiverPipelineState>(
                        Dispatcher.PrepareDirectionalShadowReceiver(source, sampleCount));
                Assert.Null(Dispatcher.SwapDirectionalShadowReceiver(candidate));
            }
            Atmospheric = atmospheric;
        }

        public RecordingGpuDevice Device { get; }

        public GpuDeviceFrameLifetime FrameLifetime { get; }

        public VulkanWorldPassScope Scope { get; }

        public WbDrawDispatcher Dispatcher { get; }

        public bool Atmospheric { get; }

        public DrawScope BeginDraw()
        {
            FrameLifetime.BeginFrame();
            IGpuFrame frame = FrameLifetime.CurrentFrame!;
            IGpuPassEncoder pass = frame.BeginPass(
                GpuPassDescription.BackbufferClear(
                    Atmospheric
                        ? DirectionalShadowReceiverPolicy.AtmosphericWorldPassName
                        : "fw2-ordered-stream-test",
                    Vector4.Zero,
                    Scope.SampleCount));
            IDisposable publication = Scope.Publish(pass);
            Device.Clear();
            return new DrawScope(frame, pass, publication);
        }

        public void Dispose()
        {
            Dispatcher.Dispose();
            _meshAdapter.Dispose();
            _textures.Dispose();
            Device.Dispose();
        }
    }

    private sealed class BindableAtmosphericSource(bool bindingAvailable) : IDirectionalShadowReceiverSource
    {
        public DirectionalShadowPipelineShaders PipelineShaders =>
            DirectionalShadowPipelineShaders.Local;

        public bool TryGetCurrentFrameBinding(
            IGpuFrame frame,
            out DirectionalShadowFrameBinding binding)
        {
            if (!bindingAvailable)
            {
                binding = DirectionalShadowFrameBinding.Disabled;
                return false;
            }
            GpuRingAllocation allocation = frame.AllocateRing(
                checked((int)DirectionalShadowUniforms.SizeInBytes),
                GpuRingUsage.Uniform);
            binding = new DirectionalShadowFrameBinding(
                frame.Serial,
                Enabled: false,
                allocation.Buffer,
                allocation.OffsetBytes,
                DirectionalShadowUniforms.SizeInBytes,
                GpuTextureSlot.Unassigned,
                CascadeCount: 0);
            return true;
        }
    }

    private sealed class NullPreparedAssetSource : IPreparedAssetSource
    {
        public PreparedAssetSourceStats Stats => default;

        public CacheStats DecodedTextureCacheStats => default;

        public PreparedAssetPresence Probe(
            AcDream.Content.Pak.PakAssetType type,
            uint sourceFileId) =>
            PreparedAssetPresence.Missing;

        public PreparedAssetReadResult Read(
            in PreparedAssetRequest request,
            CancellationToken cancellationToken = default) =>
            PreparedAssetReadResult.Missing;

        public void Dispose()
        {
        }
    }

    private sealed class NoopDatReaderWriter : IDatReaderWriter
    {
        private readonly StubDatabase _portal = new();
        private readonly StubDatabase _highRes = new();
        private readonly StubDatabase _language = new();
        private readonly StubDatabase _cell = new();

        public string SourceDirectory => string.Empty;

        public IDatDatabase Portal => _portal;

        public IDatDatabase Cell => _cell;

        public ReadOnlyDictionary<uint, IDatDatabase> CellRegions { get; } =
            new(new Dictionary<uint, IDatDatabase>());

        public IDatDatabase HighRes => _highRes;

        public IDatDatabase Language => _language;

        public IDatDatabase Local => _language;

        public ReadOnlyDictionary<uint, uint> RegionFileMap { get; } =
            new(new Dictionary<uint, uint>());

        public int PortalIteration => 0;

        public int CellIteration => 0;

        public int HighResIteration => 0;

        public int LanguageIteration => 0;

        public bool TryGetFileBytes(
            uint regionId,
            uint fileId,
            ref byte[] bytes,
            out int bytesRead)
        {
            bytesRead = 0;
            return false;
        }

        public IEnumerable<uint> GetAllIdsOfType<T>() where T : IDBObj =>
            Array.Empty<uint>();

        public IEnumerable<IDatReaderWriter.IdResolution> ResolveId(uint id) =>
            Array.Empty<IDatReaderWriter.IdResolution>();

        public bool TrySave<T>(T obj, int iteration = 0) where T : IDBObj =>
            throw new NotSupportedException();

        public bool TrySave<T>(
            uint regionId,
            T obj,
            int iteration = 0) where T : IDBObj =>
            throw new NotSupportedException();

        [return: MaybeNull]
        public T Get<T>(uint fileId) where T : IDBObj => default;

        public bool TryGet<T>(
            uint fileId,
            [MaybeNullWhen(false)] out T value) where T : IDBObj
        {
            value = default;
            return false;
        }

        public void Dispose()
        {
        }

        private sealed class StubDatabase : IDatDatabase
        {
            public DatDatabase Db => throw new NotSupportedException();

            public int Iteration => 0;

            public IEnumerable<uint> GetAllIdsOfType<T>() where T : IDBObj =>
                Array.Empty<uint>();

            public bool TryGet<T>(
                uint fileId,
                [MaybeNullWhen(false)] out T value) where T : IDBObj
            {
                value = default;
                return false;
            }

            public bool TryGetFileBytes(
                uint fileId,
                [MaybeNullWhen(false)] out byte[] value)
            {
                value = null;
                return false;
            }

            public bool TryGetFileBytes(
                uint fileId,
                ref byte[] bytes,
                out int bytesRead)
            {
                bytesRead = 0;
                return false;
            }

            public bool TrySave<T>(T obj, int iteration = 0) where T : IDBObj =>
                throw new NotSupportedException();

            public void Dispose()
            {
            }
        }
    }
}
