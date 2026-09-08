using System.Numerics;
using System.Reflection;
using System.Runtime.CompilerServices;
using System.Runtime.InteropServices;
using AcDream.App.Rendering;
using AcDream.App.Rendering.Gpu;
using AcDream.App.Rendering.Gpu.Vk;
using AcDream.App.Rendering.Wb;
using AcDream.App.Tests.Rendering.Gpu;
using AcDream.Content;
using AcDream.Core.Meshing;
using Chorizite.Core.Render.Enums;
using DatReaderWriter.Enums;
using Microsoft.Extensions.Logging.Abstractions;
using CullMode = DatReaderWriter.Enums.CullMode;

namespace AcDream.App.Tests.Rendering;

public sealed class EnvCellAlphaDrawSourceTests
{
    private static readonly Vector4 MaterialBase = new(0.31f, 0.57f, 0.83f, 0.19f);
    private static readonly Vector3 MaterialDiffuse = new(0.73f, 0.41f, 0.67f);
    private static readonly Vector4 MaterialDetail = new(0.91f, 0.23f, 0.49f, 0.62f);
    private static readonly Vector4 MaterialDestination = new(0.17f, 0.37f, 0.71f, 0.29f);

    [Fact]
    public void RetainedPerBatchMask_RoutesClipAndAlphaExactly()
    {
        var clip = new ObjectRenderBatch { IsTransparent = true, RetailSurfaceMask = 0x08 };
        var alpha = new ObjectRenderBatch { IsTransparent = true, RetailSurfaceMask = 0x02 };

        Assert.Equal(
            EnvCellTransparentRoute.Clip,
            EnvCellRenderer.RouteTransparentBatch(clip, detailSurfaceActive: false));
        Assert.Equal(
            EnvCellTransparentRoute.Alpha,
            EnvCellRenderer.RouteTransparentBatch(alpha, detailSurfaceActive: false));
    }

    [Fact]
    public void WholeLeaf_MixedCellDrawsOpaqueAtTurnThenClipAndAlphaAtDrain()
    {
        using var fixture = new ProductionEnvCellFixture(
            detailSurfaceActive: false,
            BatchSpec.Opaque,
            BatchSpec.ClipDds,
            BatchSpec.Alpha);

        fixture.Queue.BeginFrame();
        fixture.Leaf.DrawCellShell(ProductionEnvCellFixture.CellId);

        GpuRecordedMultiDrawIndirect turnDraw = Assert.Single(
            fixture.Device.Calls.OfType<GpuRecordedMultiDrawIndirect>());
        Assert.Equal(1u, turnDraw.DrawCount);
        Assert.Equal(["envcell-opaque"], DrawPipelineNames(fixture.Device));
        Assert.Equal(1, fixture.Queue.ClipCount);
        Assert.Equal(1, fixture.Queue.AlphaCount);
        Assert.False(Assert.Single(ClipEntries(fixture.Queue)).OverrideClipmap);

        fixture.Queue.EndFrame();

        GpuRecordedMultiDrawIndirect[] draws =
            [.. fixture.Device.Calls.OfType<GpuRecordedMultiDrawIndirect>()];
        Assert.Equal(3, draws.Length);
        Assert.All(draws, static draw => Assert.Equal(1u, draw.DrawCount));
        Assert.Equal(
            ["envcell-opaque", "envcell-clip", "envcell-alpha"],
            DrawPipelineNames(fixture.Device));
    }

    public static TheoryData<SurfaceType, bool, string, object, float, bool> ExactMaterialRows() => new()
    {
        { SurfaceType.Base1Image, false, "envcell-opaque", GpuBlendMode.None, 0f, true },
        { SurfaceType.Base1Image | SurfaceType.Alpha, false, "envcell-alpha", GpuBlendMode.StraightAlpha, 0f, true },
        { SurfaceType.Base1Image | SurfaceType.Alpha | SurfaceType.Additive, false, "envcell-additive", GpuBlendMode.Additive, 0f, false },
        { SurfaceType.Base1Image | SurfaceType.Additive, false, "envcell-raw-additive", GpuBlendMode.RawAdditive, 0f, false },
        { SurfaceType.Base1Image | SurfaceType.InvAlpha, false, "envcell-inverse", GpuBlendMode.InverseAlpha, 0f, true },
        { SurfaceType.Base1Image | SurfaceType.InvAlpha | SurfaceType.Additive, false, "envcell-inverse-additive", GpuBlendMode.InverseAdditive, 0f, false },
        { SurfaceType.Translucent, false, "envcell-alpha", GpuBlendMode.StraightAlpha, 0f, true },
        { SurfaceType.Translucent | SurfaceType.Additive, false, "envcell-raw-additive", GpuBlendMode.RawAdditive, 0f, false },
        { SurfaceType.Translucent | SurfaceType.InvAlpha, false, "envcell-inverse", GpuBlendMode.InverseAlpha, 0f, true },
        { SurfaceType.Base1Image | SurfaceType.Base1ClipMap, false, "envcell-clip", GpuBlendMode.PremultipliedAlpha, 200f / 255f, true },
        { SurfaceType.Base1Image | SurfaceType.Base1ClipMap, true, "envcell-clip", GpuBlendMode.PremultipliedAlpha, 100f / 255f, true },
        { SurfaceType.Base1Image | SurfaceType.Alpha | SurfaceType.Base1ClipMap, false, "envcell-alpha-depth-write", GpuBlendMode.StraightAlpha, 200f / 255f, true },
        { SurfaceType.Base1Image | SurfaceType.Alpha | SurfaceType.Base1ClipMap, true, "envcell-alpha-depth-write", GpuBlendMode.StraightAlpha, 100f / 255f, true },
        { SurfaceType.Base1Image | SurfaceType.Alpha | SurfaceType.Additive | SurfaceType.Base1ClipMap, false, "envcell-additive-depth-write", GpuBlendMode.Additive, 200f / 255f, false },
        { SurfaceType.Base1Image | SurfaceType.Alpha | SurfaceType.Additive | SurfaceType.Base1ClipMap, true, "envcell-additive-depth-write", GpuBlendMode.Additive, 100f / 255f, false },
        { SurfaceType.Base1Image | SurfaceType.Additive | SurfaceType.Base1ClipMap, false, "envcell-raw-additive-depth-write", GpuBlendMode.RawAdditive, 200f / 255f, false },
        { SurfaceType.Base1Image | SurfaceType.Additive | SurfaceType.Base1ClipMap, true, "envcell-raw-additive-depth-write", GpuBlendMode.RawAdditive, 100f / 255f, false },
        { SurfaceType.Base1Image | SurfaceType.InvAlpha | SurfaceType.Base1ClipMap, false, "envcell-inverse-depth-write", GpuBlendMode.InverseAlpha, 200f / 255f, true },
        { SurfaceType.Base1Image | SurfaceType.InvAlpha | SurfaceType.Base1ClipMap, true, "envcell-inverse-depth-write", GpuBlendMode.InverseAlpha, 100f / 255f, true },
        { SurfaceType.Base1Image | SurfaceType.InvAlpha | SurfaceType.Additive | SurfaceType.Base1ClipMap, false, "envcell-inverse-additive-depth-write", GpuBlendMode.InverseAdditive, 200f / 255f, false },
        { SurfaceType.Base1Image | SurfaceType.InvAlpha | SurfaceType.Additive | SurfaceType.Base1ClipMap, true, "envcell-inverse-additive-depth-write", GpuBlendMode.InverseAdditive, 100f / 255f, false },
        { SurfaceType.Translucent | SurfaceType.Base1ClipMap | SurfaceType.Additive, false, "envcell-alpha", GpuBlendMode.StraightAlpha, 0f, false },
        { SurfaceType.Translucent | SurfaceType.Base1ClipMap | SurfaceType.Additive, true, "envcell-alpha", GpuBlendMode.StraightAlpha, 0f, false },
    };

    [Theory]
    [MemberData(nameof(ExactMaterialRows))]
    public void DetailOn_EveryEnvCellFamilyDrawsOnceInPlaceWithAuthoredOpacity(
        SurfaceType surfaceType,
        bool paletted,
        string expectedPipeline,
        object expectedBlendValue,
        float expectedReference,
        bool expectedFog)
    {
        var expectedBlend = (GpuBlendMode)expectedBlendValue;
        BatchSpec spec = new(surfaceType, paletted, PositiveStippling: false);
        using var fixture = new ProductionEnvCellFixture(
            detailSurfaceActive: true,
            spec);
        fixture.Queue.BeginFrame();

        fixture.Leaf.DrawCellShell(ProductionEnvCellFixture.CellId);

        Assert.Single(fixture.Device.Calls.OfType<GpuRecordedMultiDrawIndirect>());
        Assert.Equal(
            [expectedPipeline],
            DrawPipelineNames(fixture.Device));
        Assert.All(
            DrawPushConstants(fixture.Device),
            constants => Assert.Equal(expectedReference, constants.ParamB));
        GpuPushConstants armed = Assert.Single(
            DrawPushConstants(fixture.Device),
            constants => constants.ParamA != 0f);
        Assert.NotEqual(0u, armed.TextureIndexA);
        Assert.Equal(
            !expectedFog,
            (armed.RenderPass & RetailDetailTextureContract.NoFogRenderPassFlag) != 0);
        RetailSetSurfaceMaterialState resolved = RetailSetSurfaceMaterialState.Resolve(
            surfaceType, texturePresent: true, textureHasPalette: paletted);
        GpuPipelineDescription selected = fixture.Device.CreatedPipelines
            .Single(pipeline => pipeline.Description.Name == expectedPipeline)
            .Description;
        Assert.Equal(expectedBlend, selected.Blend);
        Assert.True(selected.Depth.Test);
        Assert.Equal(
            resolved.Blend == RetailSetSurfaceBlend.Opaque || resolved.AlphaTestEnabled,
            selected.Depth.Write);
        Assert.Equal(WorldDepthContract.WorldCompare, selected.Depth.Compare);
        Assert.Equal(0, fixture.Queue.PendingCount);
        GpuRecordedStorageBind batchBind = fixture.Device.Calls
            .OfType<GpuRecordedStorageBind>()
            .Last(call => call.Binding == GpuBindingModel.StorageBatches);
        ReadOnlySpan<ModernBatchData> gpuBatches = MemoryMarshal.Cast<byte, ModernBatchData>(
            fixture.Device.RingBytes.Slice((int)batchBind.OffsetBytes, (int)batchBind.SizeBytes));
        Assert.Equal(0.25f, Assert.Single(gpuBatches.ToArray()).SurfaceOpacity);
        Vector4 source = RetailDetailTextureContract.Combine(
            MaterialBase, MaterialDiffuse, MaterialDetail, 0.75f, 0.4f);
        AssertVector(new Vector4(0.3534682f, 0.2330118f, 0.5438054f, 0.11532f), source);
        RetailDetailTextureContract.FramebufferFamily family = expectedBlend switch
        {
            GpuBlendMode.None => RetailDetailTextureContract.FramebufferFamily.Opaque,
            GpuBlendMode.StraightAlpha => RetailDetailTextureContract.FramebufferFamily.Alpha,
            GpuBlendMode.Additive => RetailDetailTextureContract.FramebufferFamily.AlphaAdditive,
            GpuBlendMode.RawAdditive => RetailDetailTextureContract.FramebufferFamily.Additive,
            GpuBlendMode.InverseAlpha => RetailDetailTextureContract.FramebufferFamily.InverseAlpha,
            GpuBlendMode.InverseAdditive => RetailDetailTextureContract.FramebufferFamily.InverseAlphaAdditive,
            GpuBlendMode.PremultipliedAlpha => RetailDetailTextureContract.FramebufferFamily.Clip,
            _ => throw new ArgumentOutOfRangeException(nameof(expectedBlend)),
        };
        AssertVector(
            IndependentComposite(source, MaterialDestination, family),
            RetailDetailTextureContract.Composite(source, MaterialDestination, family));
        if (resolved.AlphaTestEnabled)
        {
            Assert.False(RetailDetailTextureContract.SurvivesClip(
                MathF.BitDecrement(expectedReference), expectedReference));
            Assert.True(RetailDetailTextureContract.SurvivesClip(expectedReference, expectedReference));
            Assert.True(RetailDetailTextureContract.SurvivesClip(
                MathF.BitIncrement(expectedReference), expectedReference));
        }
        fixture.Queue.EndFrame();
        Assert.Single(fixture.Device.Calls.OfType<GpuRecordedMultiDrawIndirect>());
    }

    public static TheoryData<SurfaceType, bool, string, float> DetailOffRows() => new()
    {
        { SurfaceType.Base1Image, false, "envcell-opaque", 0f },
        { SurfaceType.Alpha, false, "envcell-alpha", 0f },
        { SurfaceType.Alpha | SurfaceType.Additive, false, "envcell-additive", 0f },
        { SurfaceType.Additive, false, "envcell-additive", 0f },
        { SurfaceType.InvAlpha, false, "envcell-alpha", 0f },
        { SurfaceType.InvAlpha | SurfaceType.Additive, false, "envcell-additive", 0f },
        { SurfaceType.Base1ClipMap, false, "envcell-clip", 200f / 255f },
        { SurfaceType.Base1ClipMap, true, "envcell-clip", 100f / 255f },
        { SurfaceType.Alpha | SurfaceType.Base1ClipMap, true, "envcell-alpha", 0f },
        { SurfaceType.InvAlpha | SurfaceType.Base1ClipMap, false, "envcell-alpha", 0f },
        { SurfaceType.Translucent, false, "envcell-alpha", 0f },
        { SurfaceType.Translucent | SurfaceType.Additive, false, "envcell-additive", 0f },
        { SurfaceType.Translucent | SurfaceType.InvAlpha, false, "envcell-alpha", 0f },
        { SurfaceType.Translucent | SurfaceType.Base1ClipMap | SurfaceType.Additive, true, "envcell-additive", 0f },
    };

    [Theory]
    [MemberData(nameof(DetailOffRows))]
    public void DetailOff_EveryRawStateRetainsThePreFixLogicalPath(
        SurfaceType surfaceType,
        bool paletted,
        string expectedPipeline,
        float expectedReference)
    {
        using var fixture = new ProductionEnvCellFixture(
            detailSurfaceActive: false,
            new BatchSpec(surfaceType, paletted, PositiveStippling: false));
        fixture.Queue.BeginFrame();
        fixture.Leaf.DrawCellShell(ProductionEnvCellFixture.CellId);
        fixture.Queue.EndFrame();

        Assert.Single(fixture.Device.Calls.OfType<GpuRecordedMultiDrawIndirect>());
        Assert.Equal([expectedPipeline], DrawPipelineNames(fixture.Device));
        GpuPushConstants constants = Assert.Single(DrawPushConstants(fixture.Device));
        Assert.Equal(0u, constants.TextureIndexA);
        Assert.Equal(0f, constants.ParamA);
        Assert.Equal(expectedReference, constants.ParamB);
        Assert.Equal(0, constants.RenderPass & RetailDetailTextureContract.NoFogRenderPassFlag);
    }

    [Fact]
    public void ParticleAppendedBetweenTwoCellTokens_KeepsItsPositionInTheCombinedDrain()
    {
        var log = new List<string>();
        var clipSource = Source(EnvCellTransparentRoute.Clip, log);
        var cellSource = Source(EnvCellTransparentRoute.Alpha, log);
        var particleSource = new RecordingSource("particle", log);
        var queue = new RetailAlphaQueue();

        queue.BeginFrame();
        RetailPViewPassExecutor.DispatchTransparentCellShell(
            0x100u, EnvCellTransparentRoute.Alpha, false, queue,
            clipSource, cellSource, (_, _, _) => throw new InvalidOperationException());
        Assert.True(queue.TryAppend(RetailAlphaList.Alpha, particleSource, 7, false));
        RetailPViewPassExecutor.DispatchTransparentCellShell(
            0x200u, EnvCellTransparentRoute.Alpha, false, queue,
            clipSource, cellSource, (_, _, _) => throw new InvalidOperationException());
        queue.EndFrame();

        Assert.Equal(new[] { "Alpha:00000100", "particle:7", "Alpha:00000200" }, log);
    }

    [Fact]
    public void ProductionWholeLeaf_WarmedScanSubmitRhiAndFilteredReplayDoNotAllocate()
    {
        using var fixture = new ProductionEnvCellFixture(
            detailSurfaceActive: false,
            BatchSpec.Opaque,
            BatchSpec.ClipDds,
            BatchSpec.Alpha,
            ringCapacityBytes: 64 * 1024 * 1024);

        fixture.Device.Clear();
        fixture.Device.RecordingEnabled = false;
        long allocated = ZeroAllocationProbe.MeasureWarmed(
            fixture.RunWholeLeafFrame,
            batchSize: 256,
            warmupBatches: 2,
            samples: 4);

        Assert.Equal(0, allocated);
    }

    [Theory]
    [InlineData(false, 200f / 255f)]
    [InlineData(true, 100f / 255f)]
    public void WholeLeaf_ClipDrainBindsExactStateAndTextureClassReference(
        bool paletted,
        float expectedReference)
    {
        BatchSpec clip = paletted ? BatchSpec.ClipPaletted : BatchSpec.ClipDds;
        using var fixture = new ProductionEnvCellFixture(
            detailSurfaceActive: false,
            clip);

        fixture.Queue.BeginFrame();
        fixture.Leaf.DrawCellShell(ProductionEnvCellFixture.CellId);
        Assert.Empty(fixture.Device.Calls.OfType<GpuRecordedMultiDrawIndirect>());
        Assert.False(Assert.Single(ClipEntries(fixture.Queue)).OverrideClipmap);
        fixture.Queue.EndFrame();

        Assert.Equal(["envcell-clip"], DrawPipelineNames(fixture.Device));
        Assert.Equal(
            expectedReference,
            Assert.Single(DrawPushConstants(fixture.Device)).ParamB);

        GpuPipelineDescription clipPipeline = fixture.Device.CreatedPipelines
            .Single(static pipeline => pipeline.Description.Name == "envcell-clip")
            .Description;
        Assert.Equal(GpuBlendMode.PremultipliedAlpha, clipPipeline.Blend);
        Assert.True(clipPipeline.Depth.Test);
        Assert.True(clipPipeline.Depth.Write);
        Assert.Equal(WorldDepthContract.WorldCompare, clipPipeline.Depth.Compare);
        Assert.False(clipPipeline.AlphaToCoverage);

        GpuPipelineDescription alphaPipeline = fixture.Device.CreatedPipelines
            .Single(static pipeline => pipeline.Description.Name == "envcell-alpha")
            .Description;
        Assert.Equal(GpuBlendMode.StraightAlpha, alphaPipeline.Blend);
        Assert.True(alphaPipeline.Depth.Test);
        Assert.False(alphaPipeline.Depth.Write);
    }

    [Fact]
    public void WholeLeaf_PositiveStippleClipMaskUsesClipPipelineAndDdsReference()
    {
        using var fixture = new ProductionEnvCellFixture(
            detailSurfaceActive: false,
            BatchSpec.ClipPositiveStippleDds);

        fixture.Queue.BeginFrame();
        fixture.Leaf.DrawCellShell(ProductionEnvCellFixture.CellId);

        Assert.Empty(fixture.Device.Calls.OfType<GpuRecordedMultiDrawIndirect>());
        Assert.Equal(1, fixture.Queue.ClipCount);
        Assert.Equal(0, fixture.Queue.AlphaCount);
        Assert.False(Assert.Single(ClipEntries(fixture.Queue)).OverrideClipmap);

        fixture.Queue.EndFrame();

        Assert.Equal(["envcell-clip"], DrawPipelineNames(fixture.Device));
        Assert.Equal(
            200f / 255f,
            Assert.Single(DrawPushConstants(fixture.Device)).ParamB);
    }

    [Fact]
    public void ClipShaders_UseGreaterEqualForThePerRangeReference()
    {
        string root = RepositoryRoot();
        string modern = File.ReadAllText(Path.Combine(
            root, "src", "AcDream.App", "Rendering", "Shaders", "mesh_modern.frag"));
        string atmospheric = File.ReadAllText(Path.Combine(
            root, "src", "AcDream.App", "Rendering", "Shaders", "mesh_atmospheric.frag"));

        foreach (string shader in new[] { modern, atmospheric })
        {
            Assert.Contains(
                "? isRetailClipReference(uParamB) && alpha < uParamB",
                shader,
                StringComparison.Ordinal);
            Assert.Contains("(uRenderPass & 0x200) == 0", shader, StringComparison.Ordinal);
            Assert.DoesNotContain("alpha <= alphaCutoff", shader, StringComparison.Ordinal);
            Assert.DoesNotContain("color.a <= alphaCutoff", shader, StringComparison.Ordinal);
            Assert.Contains("isRetailClipReference(uParamB) ? uParamB : 0.05", shader, StringComparison.Ordinal);
            Assert.Contains("abs(value - (100.0 / 255.0)) < 0.000001", shader, StringComparison.Ordinal);
            Assert.Contains("abs(value - (200.0 / 255.0)) < 0.000001", shader, StringComparison.Ordinal);
            Assert.DoesNotContain("uParamB > 0.0", shader, StringComparison.Ordinal);
            Assert.DoesNotContain("value - 0.5", shader, StringComparison.Ordinal);
        }
    }

    [Theory]
    [InlineData(0)] // Flush
    [InlineData(1)] // EndFrame
    [InlineData(2)] // AbortFrame
    public void RejectedEnvCellStorm_RollsBackPayloadAndStillResetsFirstUseSource(int completion)
    {
        const int retainedGeometricBound = 4096;
        int rejectedResetCount = 0;
        int rejectedDrawCount = 0;
        var accepted = new RetailPViewPassExecutor.EnvCellAlphaDrawSource(
            static (_, _, _) => { },
            EnvCellTransparentRoute.Clip);
        var rejected = new RetailPViewPassExecutor.EnvCellAlphaDrawSource(
            (_, _, _) => rejectedDrawCount++,
            EnvCellTransparentRoute.Clip,
            () => rejectedResetCount++);
        var unusedAlpha = new RetailPViewPassExecutor.EnvCellAlphaDrawSource(
            static (_, _, _) => { },
            EnvCellTransparentRoute.Alpha);
        var queue = new RetailAlphaQueue();
        RetailPViewPassExecutor.RenderImmediateEnvCellRoute neverImmediate =
            static (_, _, _) => throw new InvalidOperationException();

        queue.BeginFrame();
        for (int i = 0; i < RetailAlphaQueue.ListCapacity; i++)
        {
            RetailPViewPassExecutor.DispatchTransparentCellShell(
                (uint)i,
                EnvCellTransparentRoute.Clip,
                detailSurfaceActive: false,
                queue,
                accepted,
                unusedAlpha,
                neverImmediate);
        }
        for (int i = 0; i < RetailAlphaQueue.ListCapacity * 3; i++)
        {
            RetailPViewPassExecutor.DispatchTransparentCellShell(
                0xF4180104u,
                EnvCellTransparentRoute.Clip,
                detailSurfaceActive: false,
                queue,
                rejected,
                unusedAlpha,
                neverImmediate);
        }

        Assert.Equal(RetailAlphaQueue.ListCapacity, queue.ClipCount);
        Assert.Equal(RetailAlphaQueue.ListCapacity, accepted.PendingCount);
        Assert.Equal(0, rejected.PendingCount);
        Assert.InRange(accepted.PendingCapacity, RetailAlphaQueue.ListCapacity, retainedGeometricBound);
        Assert.InRange(rejected.PendingCapacity, 0, retainedGeometricBound);

        switch (completion)
        {
            case 0:
                queue.Flush(RetailAlphaFlushSite.DrawBuilding, 0f);
                queue.AbortFrame();
                break;
            case 1:
                queue.EndFrame();
                break;
            case 2:
                queue.AbortFrame();
                break;
            default:
                throw new ArgumentOutOfRangeException(nameof(completion));
        }

        Assert.Equal(1, rejectedResetCount);
        Assert.Equal(0, rejectedDrawCount);
        Assert.Equal(0, rejected.PendingCount);
        Assert.InRange(accepted.PendingCapacity, 0, retainedGeometricBound);
        Assert.InRange(accepted.PreparedCapacity, 0, retainedGeometricBound);
        Assert.InRange(accepted.DrawCapacity, 0, retainedGeometricBound);
        Assert.InRange(rejected.PendingCapacity, 0, retainedGeometricBound);
        Assert.InRange(rejected.PreparedCapacity, 0, retainedGeometricBound);
        Assert.InRange(rejected.DrawCapacity, 0, retainedGeometricBound);
    }

    private static RetailPViewPassExecutor.EnvCellAlphaDrawSource Source(
        EnvCellTransparentRoute expectedRoute,
        List<string> log) =>
        new(
            (cells, route, detail) =>
            {
                Assert.Equal(expectedRoute, route);
                Assert.False(detail);
                for (int i = 0; i < cells.Count; i++)
                    log.Add($"{route}:{cells[i]:X8}");
            },
            expectedRoute);

    private static List<RetailAlphaEntry> ClipEntries(RetailAlphaQueue queue) =>
        (List<RetailAlphaEntry>)typeof(RetailAlphaQueue)
            .GetField("_clip", BindingFlags.NonPublic | BindingFlags.Instance)!
            .GetValue(queue)!;

    private static string[] DrawPipelineNames(RecordingGpuDevice device)
    {
        var names = new List<string>();
        IReadOnlyList<GpuRecordedCall> calls = device.Calls;
        for (int i = 0; i < calls.Count; i++)
        {
            if (calls[i] is not GpuRecordedMultiDrawIndirect)
                continue;
            for (int prior = i - 1; prior >= 0; prior--)
            {
                if (calls[prior] is GpuRecordedPipelineBind bind)
                {
                    names.Add(bind.PipelineName);
                    break;
                }
            }
        }
        return [.. names];
    }

    private static GpuPushConstants[] DrawPushConstants(RecordingGpuDevice device)
    {
        var constants = new List<GpuPushConstants>();
        IReadOnlyList<GpuRecordedCall> calls = device.Calls;
        for (int i = 0; i < calls.Count; i++)
        {
            if (calls[i] is not GpuRecordedMultiDrawIndirect)
                continue;
            for (int prior = i - 1; prior >= 0; prior--)
            {
                if (calls[prior] is GpuRecordedPushConstants push)
                {
                    constants.Add(push.Constants);
                    break;
                }
            }
        }
        return [.. constants];
    }

    private static void AssertVector(Vector4 expected, Vector4 actual)
    {
        Assert.Equal(expected.X, actual.X, 6);
        Assert.Equal(expected.Y, actual.Y, 6);
        Assert.Equal(expected.Z, actual.Z, 6);
        Assert.Equal(expected.W, actual.W, 6);
    }

    private static Vector4 IndependentComposite(
        Vector4 source,
        Vector4 destination,
        RetailDetailTextureContract.FramebufferFamily family)
    {
        float x = source.W;
        return family switch
        {
            RetailDetailTextureContract.FramebufferFamily.Opaque => source,
            RetailDetailTextureContract.FramebufferFamily.Alpha =>
                source * x + destination * (1f - x),
            RetailDetailTextureContract.FramebufferFamily.AlphaAdditive =>
                source * x + destination,
            RetailDetailTextureContract.FramebufferFamily.Additive => source + destination,
            RetailDetailTextureContract.FramebufferFamily.InverseAlpha =>
                source * (1f - x) + destination * x,
            RetailDetailTextureContract.FramebufferFamily.InverseAlphaAdditive =>
                source * (1f - x) + destination,
            RetailDetailTextureContract.FramebufferFamily.Clip =>
                source + destination * new Vector4(1f - x),
            _ => throw new ArgumentOutOfRangeException(nameof(family)),
        };
    }

    private static string RepositoryRoot()
    {
        DirectoryInfo? cursor = new(AppContext.BaseDirectory);
        while (cursor is not null && !File.Exists(Path.Combine(cursor.FullName, "AcDream.slnx")))
            cursor = cursor.Parent;
        return cursor?.FullName
            ?? throw new DirectoryNotFoundException("Could not locate AcDream.slnx.");
    }

    private readonly record struct BatchSpec(
        SurfaceType Type,
        bool Paletted,
        bool PositiveStippling)
    {
        internal static BatchSpec Opaque { get; } = new(
            SurfaceType.Base1Image, Paletted: false, PositiveStippling: false);

        internal static BatchSpec ClipDds { get; } = new(
            SurfaceType.Base1Image | SurfaceType.Base1ClipMap,
            Paletted: false, PositiveStippling: false);

        internal static BatchSpec ClipPaletted { get; } = new(
            SurfaceType.Base1Image | SurfaceType.Base1ClipMap,
            Paletted: true, PositiveStippling: false);

        internal static BatchSpec ClipPositiveStippleDds { get; } = new(
            SurfaceType.Base1Image | SurfaceType.Base1ClipMap,
            Paletted: false, PositiveStippling: true);

        internal static BatchSpec Alpha { get; } = new(
            SurfaceType.Base1Image | SurfaceType.Alpha,
            Paletted: false, PositiveStippling: false);

        internal static BatchSpec Additive { get; } = new(
            SurfaceType.Base1Image | SurfaceType.Additive,
            Paletted: false, PositiveStippling: false);
    }

    private sealed class ProductionEnvCellFixture : IDisposable
    {
        public const uint CellId = 0xF4180104u;
        private const ulong MeshId = 0x2_F4180104UL;

        private readonly GpuDeviceFrameLifetime _frames;
        private readonly VulkanWorldPassScope _scope;
        private readonly ObjectMeshManager _meshManager;
        private readonly IGpuPassEncoder _pass;
        private readonly IDisposable _publication;

        public ProductionEnvCellFixture(
            bool detailSurfaceActive,
            BatchSpec firstBatch,
            BatchSpec secondBatch = default,
            BatchSpec thirdBatch = default,
            int ringCapacityBytes = 8 * 1024 * 1024)
        {
            Device = new RecordingGpuDevice(ringCapacityBytes);
            _frames = new GpuDeviceFrameLifetime(Device);
            _scope = new VulkanWorldPassScope(sampleCount: 1);
            _meshManager = new ObjectMeshManager(
                new VulkanMeshPipelineDevice(Device.Retirement),
                Device,
                new NullPreparedAssetSource(),
                NullLogger<ObjectMeshManager>.Instance);

            var mesh = new ObjectMeshData
            {
                ObjectId = MeshId,
                Vertices =
                [
                    new VertexPositionNormalTexture { Position = new Vector3(0, 0, 0) },
                    new VertexPositionNormalTexture { Position = new Vector3(1, 0, 0) },
                    new VertexPositionNormalTexture { Position = new Vector3(0, 1, 0) },
                ],
            };
            BatchSpec[] specs = secondBatch == default
                ? [firstBatch]
                : thirdBatch == default
                    ? [firstBatch, secondBatch]
                    : [firstBatch, secondBatch, thirdBatch];
            var batches = new List<TextureBatchData>(specs.Length);
            for (int i = 0; i < specs.Length; i++)
            {
                BatchSpec spec = specs[i];
                TranslucencyKind translucency =
                    TranslucencyKindExtensions.FromSurfaceType(spec.Type);
                bool alphaFamily = (spec.Type
                    & (SurfaceType.Alpha | SurfaceType.InvAlpha | SurfaceType.Additive)) != 0;
                byte mask = RetailAlphaMeshRouter.ConstructSubsetMask(
                    alphaFamily,
                    (spec.Type & SurfaceType.Base1ClipMap) != 0,
                    (spec.Type & SurfaceType.Translucent) != 0,
                    spec.PositiveStippling);
                uint paletteId = spec.Paletted ? 0x04000001u : 0u;
                batches.Add(new TextureBatchData
                {
                    Key = new TextureKey
                    {
                        SurfaceId = 0x08000BFFu + (uint)i,
                        PaletteId = paletteId,
                    },
                    TextureData = new byte[8 * 8 * 4],
                    Indices = [0, 1, 2],
                    IsTransparent = translucency != TranslucencyKind.Opaque,
                    IsAdditive = (spec.Type & SurfaceType.Additive) != 0,
                    Translucency = translucency,
                    MaterialState = RetailSetSurfaceMaterialState.Resolve(
                        spec.Type,
                        texturePresent: true,
                        textureHasPalette: spec.Paletted),
                    SurfaceOpacity = 0.25f,
                    RetailSurfaceMask = mask,
                    CullMode = CullMode.Clockwise,
                    IsCellShell = true,
                    SourceSurfaceIndex = i,
                });
            }
            mesh.TextureBatches[(8, 8, TextureFormat.RGBA8)] = batches;
            ObjectRenderData uploaded = Assert.IsType<ObjectRenderData>(
                _meshManager.UploadMeshData(mesh));
            Assert.Equal(
                batches.Select(static batch => batch.MaterialState),
                uploaded.Batches.Select(static batch => batch.MaterialState));

            Renderer = new EnvCellRenderer(
                Device,
                _frames,
                _scope,
                _meshManager,
                new WbFrustum(),
                detailSurfaceActive
                    ? new TerrainAtlas.RetailDetailTextureBinding(
                        new GpuTextureSlot(99),
                        Tiling: 2f,
                        SurfaceTextureId: 1,
                        RenderSurfaceId: 2,
                        Width: 4,
                        Height: 4)
                    : default,
                detailSurfaceActive ? DetailOn : DetailOff);
            typeof(EnvCellRenderer)
                .GetField("_activeSnapshot", System.Reflection.BindingFlags.NonPublic
                    | System.Reflection.BindingFlags.Instance)!
                .SetValue(Renderer, new EnvCellVisibilitySnapshot
                {
                    BatchedByCell = new Dictionary<uint, Dictionary<ulong, List<InstanceData>>>
                    {
                        [CellId] = new Dictionary<ulong, List<InstanceData>>
                        {
                            [MeshId] =
                            [
                                new InstanceData
                                {
                                    Transform = Matrix4x4.Identity,
                                    CellId = CellId,
                                },
                            ],
                        },
                    },
                });
            ((HashSet<uint>)typeof(EnvCellRenderer)
                .GetField("_transparentCellIds", BindingFlags.NonPublic | BindingFlags.Instance)!
                .GetValue(Renderer)!)
                .Add(CellId);

            Queue = new RetailAlphaQueue();
            ClipSource = new RetailPViewPassExecutor.EnvCellAlphaDrawSource(
                Renderer.RenderTransparentOrdered,
                EnvCellTransparentRoute.Clip);
            AlphaSource = new RetailPViewPassExecutor.EnvCellAlphaDrawSource(
                Renderer.RenderTransparentOrdered,
                EnvCellTransparentRoute.Alpha);
            var passes = new RetailPViewPassExecutor(
                new NullWorldPassSurface(),
                NullRenderFrameGlState.Instance,
                ClipFrame.NoClip(),
                terrain: null,
                Renderer,
                (WbDrawDispatcher)RuntimeHelpers.GetUninitializedObject(typeof(WbDrawDispatcher)),
                sky: null,
                particles: null,
                particleRenderer: null,
                portalDepthMask: null,
                Queue,
                (TerrainDrawDiagnosticsController)RuntimeHelpers.GetUninitializedObject(
                    typeof(TerrainDrawDiagnosticsController)));
            Leaf = new WalkProductionLeafRenderer(
                passes,
                new RetailPViewFrameInput(),
                new ClipFrameAssembly(),
                static () => { },
                static () => { },
                static () => 0);

            _frames.BeginFrame();
            IGpuFrame frame = _frames.CurrentFrame!;
            _pass = frame.BeginPass(
                GpuPassDescription.BackbufferClear(
                    "s4-c2-envcell-production",
                    Vector4.Zero,
                    sampleCount: 1));
            _publication = _scope.Publish(_pass);
            Renderer.BeginFrame(frameSlot: 0);
            Device.Clear();
        }

        public RecordingGpuDevice Device { get; }

        public EnvCellRenderer Renderer { get; }

        public RetailAlphaQueue Queue { get; }

        public RetailPViewPassExecutor.EnvCellAlphaDrawSource ClipSource { get; }

        public RetailPViewPassExecutor.EnvCellAlphaDrawSource AlphaSource { get; }

        public WalkProductionLeafRenderer Leaf { get; }

        public void RunWholeLeafFrame()
        {
            Queue.BeginFrame();
            Leaf.DrawCellShell(CellId);
            Queue.EndFrame();
        }

        private static bool DetailOn() => true;

        private static bool DetailOff() => false;

        public void Dispose()
        {
            _publication.Dispose();
            _pass.Dispose();
            Renderer.Dispose();
            _meshManager.Dispose();
            Device.Dispose();
        }
    }

    private sealed class NullWorldPassSurface : IWorldPassSurface
    {
        public void PrepareClipFrame() { }

        public void EnableClipDistances() { }

        public void DisableClipDistances() { }

        public void ClearInteriorDepth() => throw new InvalidOperationException();
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

    private sealed class RecordingSource(string name, List<string> log) : IRetailAlphaDrawSource
    {
        private int[] _prepared = [];

        public void PrepareAlphaDraws(ReadOnlySpan<int> tokens) => _prepared = tokens.ToArray();

        public void DrawPreparedAlphaBatch(int firstPreparedDraw, int drawCount)
        {
            for (int i = 0; i < drawCount; i++)
                log.Add($"{name}:{_prepared[firstPreparedDraw + i]}");
        }

        public void ResetAlphaSubmissions()
        {
        }
    }

}
