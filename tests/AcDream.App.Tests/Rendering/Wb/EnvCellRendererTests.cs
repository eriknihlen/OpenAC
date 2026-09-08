
using System.Collections.Generic;
using System.Numerics;
using System.Reflection;
using System.Runtime.InteropServices;
using System.Threading;
using AcDream.App.Rendering;
using AcDream.App.Rendering.Gpu;
using AcDream.App.Rendering.Gpu.Vk;
using AcDream.App.Rendering.Wb;
using AcDream.App.Tests.Rendering.Gpu;
using AcDream.Content;
using AcDream.Core.Lighting;
using AcDream.Core.Meshing;
using Microsoft.Extensions.Logging.Abstractions;
using Xunit;
using CullMode = DatReaderWriter.Enums.CullMode;

namespace AcDream.App.Tests.Rendering.Wb;

public class EnvCellRendererTests
{
    [Fact]
    public void SubmitRhi_BindsTwoCompleteFortySevenIndexCellSetsInCurrentGeneration()
    {
        const uint cellA = 0x8C040101u;
        const uint cellB = 0x8C040102u;
        using var device = new RecordingGpuDevice();
        using var meshManager = CreateMeshManager(device);
        var frameLifetime = new GpuDeviceFrameLifetime(device);
        var scope = new VulkanWorldPassScope(sampleCount: 1);
        using var renderer = new EnvCellRenderer(
            device,
            frameLifetime,
            scope,
            meshManager,
            new WbFrustum());
        LightSource[] lights = Enumerable.Range(0, LightManager.MaxLightsPerEnvCell)
            .Select(index => new LightSource
            {
                Kind = LightKind.Point,
                WorldPosition = new Vector3(index, 0f, 0f),
                RankingOrigin = new Vector3(index, 0f, 0f),
                Range = 10f,
                IsDynamic = index < LightManager.MaxDynamicPointLights,
            })
            .ToArray();
        renderer.SetPointSnapshot(lights);

        frameLifetime.BeginFrame();
        IGpuFrame frame = frameLifetime.CurrentFrame!;
        IGpuPassEncoder pass = frame.BeginPass(
            GpuPassDescription.BackbufferClear(
                "envcell-47-index-binding",
                Vector4.Zero,
                sampleCount: 1));
        IDisposable publication = scope.Publish(pass);
        renderer.BeginFrame(frame.SlotIndex);
        SeedLightingSubmission(renderer, instanceCount: 2);
        device.Clear();
        InvokeSubmitRhi(renderer,
        [
            new InstanceData { Transform = Matrix4x4.Identity, CellId = cellA },
            new InstanceData { Transform = Matrix4x4.Identity, CellId = cellB },
        ], instanceCount: 2);

        GpuRecordedStorageBind bind = Assert.Single(
            device.Calls.OfType<GpuRecordedStorageBind>(),
            call => call.Binding == GpuBindingModel.StorageInstanceLightSets);
        Assert.Equal((uint)(2 * LightManager.MaxLightsPerEnvCell * sizeof(int)), bind.SizeBytes);
        int[] actual = MemoryMarshal.Cast<byte, int>(device.RingBytes.Slice(
            (int)bind.OffsetBytes,
            (int)bind.SizeBytes)).ToArray();
        int[] expected = Enumerable.Range(0, LightManager.MaxLightsPerEnvCell).ToArray();
        Assert.Equal(expected, actual.AsSpan(0, 47).ToArray());
        Assert.Equal(expected, actual.AsSpan(47, 47).ToArray());
        Assert.Equal(8, GpuBindingModel.MaxLightsPerObject);
        Assert.Equal(47, GpuBindingModel.MaxLightsPerEnvCell);

        publication.Dispose();
        pass.Dispose();
        frameLifetime.EndFrame();

        // A shorter next-generation snapshot must overwrite every binding-5
        // slot. No index from the prior 47-entry generation may survive.
        renderer.SetPointSnapshot(lights.Take(2).ToArray());
        frameLifetime.BeginFrame();
        frame = frameLifetime.CurrentFrame!;
        using IGpuPassEncoder nextPass = frame.BeginPass(
            GpuPassDescription.BackbufferClear(
                "envcell-47-index-binding-next-generation",
                Vector4.Zero,
                sampleCount: 1));
        using IDisposable nextPublication = scope.Publish(nextPass);
        renderer.BeginFrame(frame.SlotIndex);
        SeedLightingSubmission(renderer, instanceCount: 2);
        device.Clear();
        InvokeSubmitRhi(renderer,
        [
            new InstanceData { Transform = Matrix4x4.Identity, CellId = cellA },
            new InstanceData { Transform = Matrix4x4.Identity, CellId = cellB },
        ], instanceCount: 2);

        bind = Assert.Single(
            device.Calls.OfType<GpuRecordedStorageBind>(),
            call => call.Binding == GpuBindingModel.StorageInstanceLightSets);
        actual = MemoryMarshal.Cast<byte, int>(device.RingBytes.Slice(
            (int)bind.OffsetBytes,
            (int)bind.SizeBytes)).ToArray();
        int[] expectedShort = [0, 1, .. Enumerable.Repeat(-1, 45)];
        Assert.Equal(expectedShort, actual.AsSpan(0, 47).ToArray());
        Assert.Equal(expectedShort, actual.AsSpan(47, 47).ToArray());
    }

    [Theory]
    [InlineData(CullMode.Landblock)]
    [InlineData(CullMode.None)]
    [InlineData(CullMode.Clockwise)]
    [InlineData(CullMode.CounterClockwise)]
    public void CellShellCullPolicy_UsesRetailConstructedMeshClockwiseCull(
        CullMode sourceSidesType)
    {
        Assert.Equal(
            CullMode.Clockwise,
            EnvCellRenderer.ResolveRetailCellShellCullMode(sourceSidesType));
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

    private static ObjectMeshManager CreateMeshManager(RecordingGpuDevice device) =>
        new(
            new VulkanMeshPipelineDevice(device.Retirement),
            device,
            new NullPreparedAssetSource(),
            NullLogger<ObjectMeshManager>.Instance);

    private static EnvCellRenderer CreateRenderer(ObjectMeshManager? meshManager = null)
    {
        var device = new RecordingGpuDevice();
        return new EnvCellRenderer(
            device,
            new GpuDeviceFrameLifetime(device),
            new VulkanWorldPassScope(sampleCount: 1),
            meshManager ?? CreateMeshManager(device),
            new WbFrustum());
    }

    [Fact]
    public void EnvironmentDetailCategory_BindsValidStorageDescriptorNine()
    {
        using var device = new RecordingGpuDevice();
        device.Clear();

        using IGpuFrame frame = device.BeginFrame();
        using IGpuPassEncoder pass = frame.BeginPass(
            GpuPassDescription.BackbufferClear(
                "envcell-detail-binding",
                Vector4.Zero,
                sampleCount: 1));

        using EnvCellRenderer renderer = CreateRenderer();
        renderer.BindEnvironmentDetailCategory(pass, frame, instanceCount: 3);

        GpuRecordedStorageBind storageBind = Assert.Single(
            device.Calls.OfType<GpuRecordedStorageBind>());
        Assert.Equal(GpuBindingModel.StorageInstanceDetailCategory, storageBind.Binding);
        Assert.Equal((uint)(3 * sizeof(uint)), storageBind.SizeBytes);
        ReadOnlySpan<uint> categories = MemoryMarshal.Cast<byte, uint>(
            device.RingBytes.Slice((int)storageBind.OffsetBytes, (int)storageBind.SizeBytes));
        Assert.Equal([1u, 1u, 1u], categories.ToArray());
    }

    [Theory]
    [InlineData(WbRenderPass.Opaque)]
    [InlineData(WbRenderPass.Transparent)]
    public void SubmitRhi_BindsConstantOneInstanceAlphaBeforeAnyDrawInThePass(
        WbRenderPass renderPass)
    {
        const int instanceCount = 5;

        using var device = new RecordingGpuDevice();
        using var meshManager = CreateMeshManager(device);
        var frameLifetime = new GpuDeviceFrameLifetime(device);
        var scope = new VulkanWorldPassScope(sampleCount: 1);
        using var renderer = new EnvCellRenderer(
            device,
            frameLifetime,
            scope,
            meshManager,
            new WbFrustum());

        frameLifetime.BeginFrame();
        IGpuFrame frame = frameLifetime.CurrentFrame!;
        using IGpuPassEncoder pass = frame.BeginPass(
            GpuPassDescription.BackbufferClear(
                "envcell-submit-alpha-binding",
                Vector4.Zero,
                sampleCount: 1));
        using IDisposable publication = scope.Publish(pass);

        Type rendererType = typeof(EnvCellRenderer);
        FieldInfo commandsField = rendererType.GetField(
            "_commands", BindingFlags.NonPublic | BindingFlags.Instance)!;
        commandsField.SetValue(renderer, new[]
        {
            new DrawElementsIndirectCommand
            {
                Count = 3,
                InstanceCount = (uint)instanceCount,
                FirstIndex = 0,
                BaseVertex = 0,
                BaseInstance = 0,
            },
        });
        FieldInfo batchesField = rendererType.GetField(
            "_modernBatches", BindingFlags.NonPublic | BindingFlags.Instance)!;
        batchesField.SetValue(renderer, new ModernBatchData[] { default });
        FieldInfo rangesField = rendererType.GetField(
            "_mdiDrawRanges", BindingFlags.NonPublic | BindingFlags.Instance)!;
        var ranges = (List<EnvCellRenderer.MdiDrawRange>)rangesField.GetValue(renderer)!;
        ranges.Clear();
        EnvCellRenderer.AppendMdiDrawRange(ranges, groupIndex: 0, firstCommand: 0, commandCount: 1,
            RetailSetSurfaceMaterialState.Opaque);

        var allInstances = new List<InstanceData>();
        for (int i = 0; i < instanceCount; i++)
        {
            allInstances.Add(new InstanceData
            {
                Transform = Matrix4x4.Identity,
                CellId = 0x8C040100u + (uint)i,
            });
        }

        device.Clear();

        MethodInfo submitRhi = rendererType.GetMethod(
            "SubmitRhi", BindingFlags.NonPublic | BindingFlags.Instance)!;
        submitRhi.Invoke(
            renderer,
            new object[] { allInstances, renderPass, 1, instanceCount });

        IReadOnlyList<GpuRecordedCall> calls = device.Calls;
        int alphaBindIndex = -1;
        int firstDrawIndex = -1;
        for (int i = 0; i < calls.Count; i++)
        {
            if (alphaBindIndex < 0
                && calls[i] is GpuRecordedStorageBind bind
                && bind.Binding == GpuBindingModel.StorageInstanceAlpha)
            {
                alphaBindIndex = i;
            }

            if (firstDrawIndex < 0 && calls[i] is GpuRecordedMultiDrawIndirect)
                firstDrawIndex = i;
        }

        Assert.True(alphaBindIndex >= 0, "StorageInstanceAlpha was never bound.");
        Assert.True(firstDrawIndex >= 0, "The seeded draw command was never recorded.");
        Assert.True(
            alphaBindIndex < firstDrawIndex,
            "StorageInstanceAlpha must be bound before the pass's draw call, "
                + "not left to whatever a prior renderer's bind left in slot 7.");

        var alphaBind = (GpuRecordedStorageBind)calls[alphaBindIndex];
        Assert.Equal((uint)(instanceCount * sizeof(float)), alphaBind.SizeBytes);
        ReadOnlySpan<float> alphaValues = MemoryMarshal.Cast<byte, float>(
            device.RingBytes.Slice((int)alphaBind.OffsetBytes, (int)alphaBind.SizeBytes));
        Assert.Equal(instanceCount, alphaValues.Length);
        foreach (float value in alphaValues)
            Assert.Equal(1.0f, value);
    }

    [Fact]
    public void DetailUsesOnlySetSurfaceShellPipelinesAndNoReplayPipeline()
    {
        using var device = new RecordingGpuDevice();
        using var meshManager = CreateMeshManager(device);
        using var renderer = new EnvCellRenderer(
            device,
            new GpuDeviceFrameLifetime(device),
            new VulkanWorldPassScope(sampleCount: 1),
            meshManager,
            new WbFrustum());

        Assert.Equal(12, device.CreatedPipelines.Count);
        Assert.DoesNotContain(device.CreatedPipelines,
            pipeline => pipeline.Description.Name.Contains("detail", StringComparison.Ordinal));
    }

    [Fact]
    public void SetSurfacePipelineConstructionFailureDisposesEveryCompletedVariant()
    {
        using var device = new RecordingGpuDevice();
        using var meshManager = CreateMeshManager(device);
        device.PipelineFailure = description =>
            description.Name == "envcell-inverse-depth-write"
                ? new InvalidOperationException("injected pipeline failure")
                : null;

        Assert.Throws<InvalidOperationException>(() => new EnvCellRenderer(
            device,
            new GpuDeviceFrameLifetime(device),
            new VulkanWorldPassScope(sampleCount: 1),
            meshManager,
            new WbFrustum()));

        Assert.Equal(9, device.CreatedPipelines.Count);
        Assert.All(device.CreatedPipelines, static pipeline => Assert.True(pipeline.IsDisposed));
    }

    [Fact]
    public void DisposeReleasesEverySetSurfacePipelineVariant()
    {
        using var device = new RecordingGpuDevice();
        using var meshManager = CreateMeshManager(device);
        var renderer = new EnvCellRenderer(
            device,
            new GpuDeviceFrameLifetime(device),
            new VulkanWorldPassScope(sampleCount: 1),
            meshManager,
            new WbFrustum());
        RecordingGpuPipeline[] pipelines = device.CreatedPipelines.ToArray();

        renderer.Dispose();

        Assert.Equal(12, pipelines.Length);
        Assert.All(pipelines, static pipeline => Assert.True(pipeline.IsDisposed));
    }

    [Fact]
    public void OrderedMdiRanges_CoalesceAdjacentCellsWithIdenticalState()
    {
        var ranges = new List<EnvCellRenderer.MdiDrawRange>();

        EnvCellRenderer.AppendMdiDrawRange(ranges, groupIndex: 2, firstCommand: 0, commandCount: 3,
            RetailSetSurfaceMaterialState.Opaque);
        EnvCellRenderer.AppendMdiDrawRange(ranges, groupIndex: 2, firstCommand: 3, commandCount: 4,
            RetailSetSurfaceMaterialState.Opaque);

        Assert.Equal(
            [new EnvCellRenderer.MdiDrawRange(GroupIndex: 2, FirstCommand: 0, CommandCount: 7,
                RetailSetSurfaceMaterialState.Opaque)],
            ranges);
    }

    [Fact]
    public void OrderedMdiRanges_PreserveStateAndCommandGapsAsBoundaries()
    {
        var ranges = new List<EnvCellRenderer.MdiDrawRange>();

        EnvCellRenderer.AppendMdiDrawRange(ranges, groupIndex: 2, firstCommand: 0, commandCount: 3,
            RetailSetSurfaceMaterialState.Opaque);
        EnvCellRenderer.AppendMdiDrawRange(ranges, groupIndex: 6, firstCommand: 3, commandCount: 2,
            RetailSetSurfaceMaterialState.Opaque);
        EnvCellRenderer.AppendMdiDrawRange(ranges, groupIndex: 2, firstCommand: 5, commandCount: 1,
            RetailSetSurfaceMaterialState.Opaque);
        EnvCellRenderer.AppendMdiDrawRange(ranges, groupIndex: 2, firstCommand: 9, commandCount: 2,
            RetailSetSurfaceMaterialState.Opaque);

        Assert.Equal(
            [
                new EnvCellRenderer.MdiDrawRange(2, 0, 3, RetailSetSurfaceMaterialState.Opaque),
                new EnvCellRenderer.MdiDrawRange(6, 3, 2, RetailSetSurfaceMaterialState.Opaque),
                new EnvCellRenderer.MdiDrawRange(2, 5, 1, RetailSetSurfaceMaterialState.Opaque),
                new EnvCellRenderer.MdiDrawRange(2, 9, 2, RetailSetSurfaceMaterialState.Opaque),
            ],
            ranges);
    }

    [Fact]
    public void OrderedMdiRanges_IgnoreEmptyCellRanges()
    {
        var ranges = new List<EnvCellRenderer.MdiDrawRange>();

        EnvCellRenderer.AppendMdiDrawRange(ranges, groupIndex: 2, firstCommand: 0, commandCount: 0,
            RetailSetSurfaceMaterialState.Opaque);

        Assert.Empty(ranges);
    }


    [Fact]
    public void GetEnvCellGeomId_DedupBitSet()
    {
        var id = EnvCellRenderer.GetEnvCellGeomId(0x42, 7, new List<ushort> { 1, 2, 3 });
        Assert.NotEqual(0UL, id & 0x2_0000_0000UL);
    }

    [Fact]
    public void GetEnvCellGeomId_Deterministic()
    {
        var s = new List<ushort> { 1, 2, 3 };
        var a = EnvCellRenderer.GetEnvCellGeomId(0x42, 7, s);
        var b = EnvCellRenderer.GetEnvCellGeomId(0x42, 7, s);
        Assert.Equal(a, b);
    }

    [Fact]
    public void GetEnvCellGeomId_DiffersByEnvironmentId()
    {
        var a = EnvCellRenderer.GetEnvCellGeomId(0x42, 7, new List<ushort> { 1 });
        var b = EnvCellRenderer.GetEnvCellGeomId(0x43, 7, new List<ushort> { 1 });
        Assert.NotEqual(a, b);
    }

    [Fact]
    public void GetEnvCellGeomId_DiffersByCellStructure()
    {
        var a = EnvCellRenderer.GetEnvCellGeomId(0x42, 7, new List<ushort> { 1 });
        var b = EnvCellRenderer.GetEnvCellGeomId(0x42, 8, new List<ushort> { 1 });
        Assert.NotEqual(a, b);
    }

    [Fact]
    public void GetEnvCellGeomId_DiffersBySurfaces()
    {
        var a = EnvCellRenderer.GetEnvCellGeomId(0x42, 7, new List<ushort> { 1 });
        var b = EnvCellRenderer.GetEnvCellGeomId(0x42, 7, new List<ushort> { 2 });
        Assert.NotEqual(a, b);
    }


    [Fact]
    public void NewRenderer_NeedsPrepareIsTrue()
    {
        var r = CreateRenderer();
        Assert.True(r.NeedsPrepare);
    }

    [Fact]
    public void NewRenderer_NotDisposed()
    {
        var r = CreateRenderer();
        Assert.False(r.IsDisposed);
    }

    // -----------------------------------------------------------------------
    // RemoveLandblock â€” pure data path
    // -----------------------------------------------------------------------

    [Fact]
    public void RemoveLandblock_NonExistent_DoesNotThrow()
    {
        var r = CreateRenderer();
        // Should silently no-op.
        r.RemoveLandblock(0xA9B40000u);
        Assert.True(r.NeedsPrepare);
    }


    [Fact]
    public void GetEnvCellGeomId_EmptySurfaces_Deterministic()
    {
        var a = EnvCellRenderer.GetEnvCellGeomId(1, 0, new List<ushort>());
        var b = EnvCellRenderer.GetEnvCellGeomId(1, 0, new List<ushort>());
        Assert.Equal(a, b);
        Assert.NotEqual(0UL, a & 0x2_0000_0000UL);
    }

    [Fact]
    public void GetEnvCellGeomId_SurfaceOrderMatters()
    {
        var a = EnvCellRenderer.GetEnvCellGeomId(1, 1, new List<ushort> { 10, 20 });
        var b = EnvCellRenderer.GetEnvCellGeomId(1, 1, new List<ushort> { 20, 10 });
        // The hash is order-sensitive (matches WB's foreach loop), so
        // swapped order should produce a different id.
        Assert.NotEqual(a, b);
    }


    [Fact]
    public void GpuInstanceUpload_UsesMeshModernMat4Stride()
    {
        Assert.Equal(64, Marshal.SizeOf<Matrix4x4>());
        Assert.Equal(80, Marshal.SizeOf<InstanceData>());

        var field = typeof(EnvCellRenderer).GetField("_gpuInstanceTransforms",
            System.Reflection.BindingFlags.NonPublic | System.Reflection.BindingFlags.Instance);
        Assert.NotNull(field);
        Assert.Equal(typeof(Matrix4x4[]), field!.FieldType);
    }


    [Fact]
    public void Snapshot_PostPreparePoolIndex_IsInitSettable()
    {
        var s = new EnvCellVisibilitySnapshot { PostPreparePoolIndex = 42 };
        Assert.Equal(42, s.PostPreparePoolIndex);
    }

    [Fact]
    public void Snapshot_PostPreparePoolIndex_DefaultsToZero()
    {
        var s = new EnvCellVisibilitySnapshot();
        Assert.Equal(0, s.PostPreparePoolIndex);
    }

    [Fact]
    public void GetPooledList_ReusedList_IsClearedBeforeReturn()
    {
        var r = CreateRenderer();

        var type = typeof(EnvCellRenderer);
        var getPooledListMethod = type.GetMethod("GetPooledList",
            System.Reflection.BindingFlags.NonPublic | System.Reflection.BindingFlags.Instance);
        Assert.NotNull(getPooledListMethod);
        var poolIndexField = type.GetField("_poolIndex",
            System.Reflection.BindingFlags.NonPublic | System.Reflection.BindingFlags.Instance);
        Assert.NotNull(poolIndexField);

        var first = (List<InstanceData>)getPooledListMethod!.Invoke(r, null)!;
        first.Add(new InstanceData());
        first.Add(new InstanceData());
        Assert.Equal(2, first.Count);

        poolIndexField!.SetValue(r, 0);

        var second = (List<InstanceData>)getPooledListMethod.Invoke(r, null)!;
        Assert.Same(first, second);            // reuses the same instance
        Assert.Empty(second);                  // and the data is gone
    }

    [Fact]
    public void GetPooledList_FreshList_IsAlwaysEmpty()
    {
        var r = CreateRenderer();

        var type = typeof(EnvCellRenderer);
        var getPooledListMethod = type.GetMethod("GetPooledList",
            System.Reflection.BindingFlags.NonPublic | System.Reflection.BindingFlags.Instance);

        var a = (List<InstanceData>)getPooledListMethod!.Invoke(r, null)!;
        var b = (List<InstanceData>)getPooledListMethod.Invoke(r, null)!;

        Assert.NotSame(a, b);
        Assert.Empty(a);
        Assert.Empty(b);
    }

    private static void SeedLightingSubmission(
        EnvCellRenderer renderer,
        int instanceCount)
    {
        Type rendererType = typeof(EnvCellRenderer);
        rendererType.GetField(
            "_commands", BindingFlags.NonPublic | BindingFlags.Instance)!
            .SetValue(renderer, new[]
            {
                new DrawElementsIndirectCommand
                {
                    Count = 3,
                    InstanceCount = (uint)instanceCount,
                    FirstIndex = 0,
                    BaseVertex = 0,
                    BaseInstance = 0,
                },
            });
        rendererType.GetField(
            "_modernBatches", BindingFlags.NonPublic | BindingFlags.Instance)!
            .SetValue(renderer, new ModernBatchData[] { default });
        var ranges = (List<EnvCellRenderer.MdiDrawRange>)rendererType.GetField(
            "_mdiDrawRanges", BindingFlags.NonPublic | BindingFlags.Instance)!
            .GetValue(renderer)!;
        ranges.Clear();
        EnvCellRenderer.AppendMdiDrawRange(
            ranges,
            groupIndex: 0,
            firstCommand: 0,
            commandCount: 1,
            RetailSetSurfaceMaterialState.Opaque);
    }

    private static void InvokeSubmitRhi(
        EnvCellRenderer renderer,
        List<InstanceData> instances,
        int instanceCount)
    {
        typeof(EnvCellRenderer).GetMethod(
            "SubmitRhi", BindingFlags.NonPublic | BindingFlags.Instance)!
            .Invoke(
                renderer,
                new object[]
                {
                    instances,
                    WbRenderPass.Opaque,
                    1,
                    instanceCount,
                });
    }


    private static Matrix4x4 ViewProjectionFor(Vector3 eye, Vector3 forward)
    {
        var view = Matrix4x4.CreateLookAt(eye, eye + forward, Vector3.UnitZ);
        var proj = Matrix4x4.CreatePerspectiveFieldOfView(
            fieldOfView: 1.2f, aspectRatio: 16f / 9f, nearPlaneDistance: 0.1f, farPlaneDistance: 2000f);
        return view * proj;
    }

    [Fact]
    public void CameraApproximatelyEqual_IdenticalCamera_True()
    {
        var eye = new Vector3(120f, 80f, 10f);
        var vp = ViewProjectionFor(eye, Vector3.UnitX);
        Assert.True(EnvCellRenderer.CameraApproximatelyEqual(vp, eye, vp, eye));
    }

    [Fact]
    public void CameraApproximatelyEqual_RestJitter_True()
    {
        // The ~36 Âµm eye rest jitter must NOT dirty the gate.
        var eye = new Vector3(120.34f, 87.91f, 10.2f);
        var jittered = eye + new Vector3(36e-6f, -36e-6f, 36e-6f);
        var a = ViewProjectionFor(eye, Vector3.UnitX);
        var b = ViewProjectionFor(jittered, Vector3.UnitX);
        Assert.True(EnvCellRenderer.CameraApproximatelyEqual(a, eye, b, jittered));
    }

    [Fact]
    public void CameraApproximatelyEqual_SmallRealRotation_False()
    {
        // 0.05Â° of yaw â€” far below one frame of real mouse motion â€” must dirty.
        var eye = new Vector3(120.34f, 87.91f, 10.2f);
        float yaw = 0.05f * MathF.PI / 180f;
        var a = ViewProjectionFor(eye, Vector3.UnitX);
        var b = ViewProjectionFor(eye, new Vector3(MathF.Cos(yaw), MathF.Sin(yaw), 0f));
        Assert.False(EnvCellRenderer.CameraApproximatelyEqual(a, eye, b, eye));
    }

    [Fact]
    public void CameraApproximatelyEqual_SmallRealTranslation_False()
    {
        // 5 cm of movement must dirty the gate.
        var eye = new Vector3(120.34f, 87.91f, 10.2f);
        var moved = eye + new Vector3(0.05f, 0f, 0f);
        var a = ViewProjectionFor(eye, Vector3.UnitX);
        var b = ViewProjectionFor(moved, Vector3.UnitX);
        Assert.False(EnvCellRenderer.CameraApproximatelyEqual(a, eye, b, moved));
    }

    [Fact]
    public void CameraApproximatelyEqual_GlobalScaleCoordinates_TranslationStillDirties()
    {
        var eye = new Vector3(40120.34f, 45087.91f, 110.2f);
        var moved = eye + new Vector3(0.07f, 0f, 0f); // one walking frame
        var a = ViewProjectionFor(eye, Vector3.UnitX);
        var b = ViewProjectionFor(moved, Vector3.UnitX);
        Assert.False(EnvCellRenderer.CameraApproximatelyEqual(a, eye, b, moved));

        var jittered = eye + new Vector3(36e-6f, 0f, 0f);
        var c = ViewProjectionFor(jittered, Vector3.UnitX);
        Assert.True(EnvCellRenderer.CameraApproximatelyEqual(a, eye, c, jittered));
    }

    [Fact]
    public void NewRenderer_SnapshotGenerationStartsAtZero()
    {
        var r = CreateRenderer();
        Assert.Equal(0, r.SnapshotGeneration);
    }
}
