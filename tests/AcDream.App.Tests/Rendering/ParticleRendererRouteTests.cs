using System.Collections;
using System.Numerics;
using System.Reflection;
using System.Runtime.CompilerServices;
using AcDream.App.Rendering;
using AcDream.App.Rendering.Gpu;
using AcDream.App.Rendering.Gpu.Vk;
using AcDream.App.Rendering.Wb;
using AcDream.App.Tests.Rendering.Gpu;
using AcDream.Content;
using AcDream.Core.Meshing;
using AcDream.Core.Vfx;
using Chorizite.Core.Render.Enums;
using Microsoft.Extensions.Logging.Abstractions;

namespace AcDream.App.Tests.Rendering;

public sealed class ParticleRendererRouteTests
{
    [Fact]
    public void OpaqueClassifiedMeshBatch_WithMaterialAlpha_DefersAndDrawsOnlyAtFlush()
    {
        var queue = new RetailAlphaQueue();
        var source = new RecordingSource();
        int reserveCount = 0;
        int immediateCount = 0;
        queue.BeginFrame();

        const uint colorArgbWithAlpha = 0x80FFFFFFu;
        RetailAlphaMeshDecision decision = ParticleRenderer.DeferToRetailAlphaQueue(
            ParticleSubmissionKind.Mesh,
            TranslucencyKind.Opaque,
            colorArgbWithAlpha,
            queue,
            source,
            reserveDeferred: () => reserveCount++,
            drawImmediate: (_, _, _, _) => immediateCount++,
            Matrix4x4.Identity,
            drawIndex: 7);

        Assert.Equal(RetailAlphaMeshAction.Append, decision.Action);
        Assert.Equal(RetailAlphaList.Alpha, decision.List);
        Assert.Equal(1, reserveCount);
        Assert.Equal(1, queue.AlphaCount);
        Assert.Equal(0, source.DrawCount);
        Assert.Equal(0, immediateCount);

        queue.EndFrame();

        Assert.Equal(1, source.DrawCount);
        Assert.Equal(0, immediateCount);
    }

    [Fact]
    public void OpaqueClassifiedMeshBatch_WithNoMaterialAlpha_DrawsImmediateOnOpaqueDepthState()
    {
        var queue = new RetailAlphaQueue();
        var source = new RecordingSource();
        int reserveCount = 0;
        var immediate = new List<(ParticleSubmissionKind Kind, int Index, bool Opaque)>();
        queue.BeginFrame();

        const uint fullyOpaqueColorArgb = 0xFFFFFFFFu;
        RetailAlphaMeshDecision decision = ParticleRenderer.DeferToRetailAlphaQueue(
            ParticleSubmissionKind.Mesh,
            TranslucencyKind.Opaque,
            fullyOpaqueColorArgb,
            queue,
            source,
            reserveDeferred: () => reserveCount++,
            drawImmediate: (_, kind, index, opaque) => immediate.Add((kind, index, opaque)),
            Matrix4x4.Identity,
            drawIndex: 11);

        Assert.Equal(RetailAlphaMeshAction.Immediate, decision.Action);
        Assert.Equal(0, reserveCount);
        Assert.Equal(0, queue.PendingCount);
        Assert.Equal(
            [(ParticleSubmissionKind.Mesh, 11, true)],
            immediate);

        ParticleRenderer.MeshParticlePipelineState state =
            ParticleRenderer.ResolveMeshParticlePipelineState(
                TranslucencyKind.Opaque,
                opaqueDepthState: immediate[0].Opaque);
        Assert.Equal(GpuBlendMode.None, state.Blend);
        Assert.True(state.Depth.Test);
        Assert.True(state.Depth.Write);
        Assert.Equal(WorldDepthContract.WorldCompare, state.Depth.Compare);

        queue.EndFrame();
        Assert.Equal(0, source.PrepareCount);
        Assert.Equal(0, source.DrawCount);
    }

    [Fact]
    public void ImmediateOpaqueMesh_UsesProductionParticleMeshOpaquePipelineDescription()
    {
        using var device = new RecordingGpuDevice();
        using var manager = new ObjectMeshManager(
            new VulkanMeshPipelineDevice(device.Retirement),
            device,
            new NullPreparedAssetSource(),
            NullLogger<ObjectMeshManager>.Instance);
        var adapter = (WbMeshAdapter)RuntimeHelpers.GetUninitializedObject(
            typeof(WbMeshAdapter));
        typeof(WbMeshAdapter).GetField(
            "_meshManager",
            BindingFlags.Instance | BindingFlags.NonPublic)!
            .SetValue(adapter, manager);
        var frames = new GpuDeviceFrameLifetime(device);
        var scope = new VulkanWorldPassScope(sampleCount: 1);
        using var renderer = new ParticleRenderer(
            device,
            frames,
            scope,
            new ParticleSystem(new EmitterDescRegistry(), new Random(42)),
            meshAdapter: adapter);
        MethodInfo select = typeof(ParticleRenderer).GetMethod(
            "PipelineForMeshBlend",
            BindingFlags.Instance | BindingFlags.NonPublic)!;

        var selected = (IGpuPipeline)select.Invoke(
            renderer,
            [TranslucencyKind.Opaque, true])!;

        Assert.Same(
            device.CreatedPipelines.Single(
                static pipeline => pipeline.Description.Name == "particle-mesh-opaque"),
            selected);
        GpuPipelineDescription description = selected.Description;
        Assert.Equal("particle_mesh", description.Shaders.Name);
        Assert.Equal(GpuBlendMode.None, description.Blend);
        Assert.True(description.Depth.Test);
        Assert.True(description.Depth.Write);
        Assert.Equal(WorldDepthContract.WorldCompare, description.Depth.Compare);
        Assert.Equal(GpuCullMode.None, description.Cull);
        Assert.Equal(GpuFrontFace.Clockwise, description.FrontFace);
        Assert.False(description.AlphaToCoverage);

    }

    [Fact]
    public void Billboard_AlwaysRoutesToAlphaAppend()
    {
        RetailAlphaMeshDecision decision = ParticleRenderer.RouteParticleSubmission(
            ParticleSubmissionKind.Billboard, default, default);

        Assert.Equal(RetailAlphaMeshAction.Append, decision.Action);
        Assert.Equal(RetailAlphaList.Alpha, decision.List);
    }

    [Fact]
    public void PreparedCellCap_DropsOnlyRetentionAndPreservesRow2ImmediateDuplicate()
    {
        int retainedClip = RetailAlphaQueue.ListCapacity;
        int retainedAlpha = 0;
        var decision = new RetailAlphaMeshDecision(
            RetailAlphaMeshAction.AppendClipAndImmediate,
            RetailAlphaList.Clip,
            OverrideClipmap: true);

        ParticleRenderer.PreparedCellAlphaActions actions =
            ParticleRenderer.ResolvePreparedCellAlphaActions(
                decision,
                ref retainedClip,
                ref retainedAlpha);

        Assert.False(actions.Retain);
        Assert.True(actions.DrawImmediate);
        Assert.Equal(RetailAlphaQueue.ListCapacity, retainedClip);
        Assert.Equal(0, retainedAlpha);
    }

    [Fact]
    public void ProductionImmediateMesh_WarmedDrawImmediateParticleSubmissionRhiDoesNotAllocate()
    {
        using var device = new RecordingGpuDevice(64 * 1024 * 1024);
        using var manager = new ObjectMeshManager(
            new VulkanMeshPipelineDevice(device.Retirement),
            device,
            new NullPreparedAssetSource(),
            NullLogger<ObjectMeshManager>.Instance);
        ObjectRenderData renderData = Assert.IsType<ObjectRenderData>(manager.UploadMeshData(
            new ObjectMeshData
            {
                ObjectId = 0x010001ECu,
                Vertices =
                [
                    new VertexPositionNormalTexture { Position = Vector3.Zero },
                    new VertexPositionNormalTexture { Position = Vector3.UnitX },
                    new VertexPositionNormalTexture { Position = Vector3.UnitY },
                ],
                TextureBatches =
                {
                    [(8, 8, TextureFormat.RGBA8)] =
                    [
                        new TextureBatchData
                        {
                            Key = new TextureKey { SurfaceId = 0x08000015u },
                            TextureData = new byte[8 * 8 * 4],
                            Indices = [0, 1, 2],
                            Translucency = TranslucencyKind.Opaque,
                            CullMode = DatReaderWriter.Enums.CullMode.Clockwise,
                        },
                    ],
                },
            }));
        var adapter = (WbMeshAdapter)RuntimeHelpers.GetUninitializedObject(typeof(WbMeshAdapter));
        typeof(WbMeshAdapter).GetField(
            "_meshManager",
            BindingFlags.Instance | BindingFlags.NonPublic)!
            .SetValue(adapter, manager);
        var frames = new GpuDeviceFrameLifetime(device);
        var scope = new VulkanWorldPassScope(sampleCount: 1);
        using var renderer = new ParticleRenderer(
            device,
            frames,
            scope,
            new ParticleSystem(new EmitterDescRegistry(), new Random(42)),
            meshAdapter: adapter);

        Type keyType = typeof(ParticleRenderer).GetNestedType(
            "MeshBatchKey", BindingFlags.NonPublic)!;
        Type instanceType = typeof(ParticleRenderer).GetNestedType(
            "MeshParticleInstance", BindingFlags.NonPublic)!;
        Type drawType = typeof(ParticleRenderer).GetNestedType(
            "MeshParticleDraw", BindingFlags.NonPublic)!;
        object key = Activator.CreateInstance(
            keyType,
            BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic,
            binder: null,
            args: [0x010001ECu, 0],
            culture: null)!;
        object instance = Activator.CreateInstance(
            instanceType,
            BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic,
            binder: null,
            args: [Matrix4x4.Identity, 0xFFFFFFFFu, 0f, 0u],
            culture: null)!;
        object draw = Activator.CreateInstance(
            drawType,
            BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic,
            binder: null,
            args: [key, renderData.Batches[0], instance],
            culture: null)!;
        var drawList = (IList)typeof(ParticleRenderer).GetField(
            "_meshDrawListScratch",
            BindingFlags.Instance | BindingFlags.NonPublic)!
            .GetValue(renderer)!;
        drawList.Add(draw);
        var immediate = (ParticleRenderer.DrawImmediateParticle)typeof(ParticleRenderer)
            .GetField("_drawImmediateParticle", BindingFlags.Instance | BindingFlags.NonPublic)!
            .GetValue(renderer)!;

        frames.BeginFrame();
        renderer.BeginFrame(frameSlot: 0);
        IGpuFrame frame = frames.CurrentFrame!;
        using IGpuPassEncoder pass = frame.BeginPass(GpuPassDescription.BackbufferClear(
            "s4-c2-particle-allocation",
            Vector4.Zero,
            sampleCount: 1));
        using IDisposable publication = scope.Publish(pass);

        device.Clear();
        device.RecordingEnabled = false;
        long allocated = ZeroAllocationProbe.MeasureWarmed(
            () => immediate(
                Matrix4x4.Identity,
                ParticleSubmissionKind.Mesh,
                drawIndex: 0,
                opaqueDepthState: true),
            batchSize: 256,
            warmupBatches: 2,
            samples: 4);

        Assert.Equal(0, allocated);
    }

    private sealed class RecordingSource : IRetailAlphaDrawSource
    {
        public int PrepareCount { get; private set; }
        public int DrawCount { get; private set; }

        public void PrepareAlphaDraws(ReadOnlySpan<int> tokens) => PrepareCount++;

        public void DrawPreparedAlphaBatch(int firstPreparedDraw, int drawCount) =>
            DrawCount += drawCount;

        public void ResetAlphaSubmissions()
        {
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
}
