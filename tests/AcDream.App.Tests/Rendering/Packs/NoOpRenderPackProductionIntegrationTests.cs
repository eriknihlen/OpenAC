using System.Security.Cryptography;
using AcDream.App.Plugins;
using AcDream.App.Rendering;
using AcDream.App.Rendering.Gpu;
using AcDream.App.Rendering.Gpu.Vk;
using AcDream.App.Rendering.Packs;
using AcDream.App.Tests.Rendering.Gpu;
using AcDream.Plugin.Abstractions.Rendering;
using AcDream.UI.Abstractions.Panels.Settings;

namespace AcDream.App.Tests.Rendering.Packs;

public sealed class NoOpRenderPackProductionIntegrationTests
{
    private const string BaselineFramebufferSha256 =
        "790f352044549e7bb37465b061c521321128c378f65ca52e0f2761fcdb0155de";

    [Fact]
    public void PackOffDefaultPathMatchesCompleteCheckedInBaselineOracle()
    {
        DefaultPathOracleSnapshot baseline = CaptureDefaultPath(
            composeRenderPackController: false);
        DefaultPathOracleSnapshot packOff = CaptureDefaultPath(
            composeRenderPackController: true);

        Assert.Equal(baseline.PassList, packOff.PassList);
        Assert.Equal(baseline.PipelineSet, packOff.PipelineSet);
        Assert.Equal(baseline.DrawCalls, packOff.DrawCalls);
        Assert.Equal(baseline.DispatchCalls, packOff.DispatchCalls);
        Assert.Equal(baseline.FramebufferSha256, packOff.FramebufferSha256);
        Assert.Equal(baseline.Resources, packOff.Resources);
        Assert.Equal(baseline.PackResources, packOff.PackResources);
        Assert.Equal(baseline.IsRetailSelection, packOff.IsRetailSelection);
        Assert.Equal(baseline.HasActivePackRuntime, packOff.HasActivePackRuntime);
        Assert.Equal(baseline.HasPackShaderVariant, packOff.HasPackShaderVariant);

        Assert.Equal(["vk-world"], packOff.PassList);
        Assert.Equal(
            ["baseline-world-opaque|mesh_modern|pack=False|samples=1|format=Rgba8UnormRenderTarget"],
            packOff.PipelineSet);
        Assert.Equal(1, packOff.DrawCalls);
        Assert.Equal(0, packOff.DispatchCalls);
        Assert.Equal(BaselineFramebufferSha256, packOff.FramebufferSha256);
        Assert.Equal(
            new DefaultPathResourceLedger(
                TotalBuffers: 1,
                LiveBuffers: 1,
                TotalPipelines: 1,
                LivePipelines: 1,
                TotalSamplers: 1,
                LiveSamplers: 1,
                TotalTextures: 0,
                LiveTextures: 0,
                TotalRenderTargets: 0,
                LiveRenderTargets: 0,
                TotalDirectionalDepthTargets: 0,
                LiveDirectionalDepthTargets: 0,
                LiveTextureSlots: 1,
                PipelineFormatLeases: 0),
            packOff.Resources);
        Assert.True(packOff.IsRetailSelection);
        Assert.False(packOff.HasActivePackRuntime);
        Assert.False(packOff.HasPackShaderVariant);
        Assert.Equal(
            new DefaultPathPackResourceLedger(
                RetainedGpuBytes: 0,
                TransientGpuBytes: 0,
                ImageCount: 0,
                BufferCount: 0,
                DrawCalls: 0,
                DispatchCalls: 0,
                PassCount: 0),
            packOff.PackResources);
    }

    [Fact]
    public void SelectedNoOpPackRemainsActiveWhileProductionUsesDefaultWorldPath()
    {
        RenderPackDescriptor descriptor = new(
            "sample.no-op-render-pack",
            "No-op Render Pack Sample",
            new Version(1, 0, 0),
            RenderPackApi.Current,
            RenderPackTier.Tier1,
            [],
            [],
            [],
            [],
            [],
            [],
            [new RenderQualityPreset(
                "conformance", "Conformance", [], [], [], 0, 0, 0, 0, 0)],
            [],
            null)
        {
            FeatureSummary = "Conformance-only default-path selection.",
        };
        using var registry = new BufferedRenderPackRegistry();
        using IDisposable registration = registry.Register(
            descriptor,
            RejectingAssets.Instance);
        var device = new RecordingGpuDevice();
        var factory = new AtmosphericRenderPackRuntimeFactory(device);
        using var controller = new RenderPackController(
            () => RenderPackCatalog.Build(
                registry.Snapshot(),
                RenderPackHostCapabilities.Conformance),
            factory,
            preparationScheduler: InlineRenderPackPreparationScheduler.Instance);
        controller.Request(new RenderPackSelectionSettings(
            descriptor.Id,
            descriptor.PackVersion.ToString(),
            "conformance"));

        var lifetime = new GpuDeviceFrameLifetime(device);
        var clear = new VulkanBackbufferClearState();
        var scope = new VulkanWorldPassScope(sampleCount: 1);
        var world = new DefaultWorldPhase(scope);
        var phase = new VulkanWorldScenePhase(
            lifetime,
            clear,
            sampleCount: static () => 1,
            scope,
            world,
            controller);

        lifetime.BeginFrame();
        WorldRenderFrameOutcome outcome;
        try
        {
            outcome = phase.Render(new RenderFrameInput(1.0 / 60.0, 1280, 720));
        }
        finally
        {
            lifetime.EndFrame();
        }

        Assert.Equal(DefaultWorldPhase.Expected, outcome);
        Assert.Equal(1, world.RenderCount);
        Assert.True(
            controller.Snapshot.State == RenderPackActivationState.Active,
            controller.Snapshot.Reason);
        Assert.Equal(descriptor.Id, controller.Snapshot.Selection.PackId);
        Assert.IsAssignableFrom<IDefaultWorldPathRenderPackRuntime>(controller.ActiveRuntime);
        Assert.Single(device.Calls.OfType<GpuRecordedPassBegin>(), value => value.Name == "vk-world");
        Assert.Empty(device.CreatedPipelines);
        Assert.Null(scope.CurrentEncoder);
    }

    private static DefaultPathOracleSnapshot CaptureDefaultPath(
        bool composeRenderPackController)
    {
        using var device = new RecordingGpuDevice();
        using IGpuPipeline pipeline = device.CreatePipeline(new GpuPipelineDescription
        {
            Name = "baseline-world-opaque",
            Shaders = new GpuShaderSet("mesh_modern"),
            VertexLayout = GpuVertexLayout.WorldMesh,
        });
        using IGpuBuffer vertices = device.CreateBuffer(new GpuBufferDescription(
            "baseline-world-vertices",
            SizeBytes: 96,
            GpuBufferUsage.Vertex,
            GpuMemoryResidency.DeviceLocal));
        using var registry = new BufferedRenderPackRegistry();
        using RenderPackController? controller = composeRenderPackController
            ? new RenderPackController(
                () => RenderPackCatalog.Build(
                    registry.Snapshot(),
                    RenderPackHostCapabilities.Conformance),
                new AtmosphericRenderPackRuntimeFactory(device),
                preparationScheduler: InlineRenderPackPreparationScheduler.Instance)
            : null;
        var lifetime = new GpuDeviceFrameLifetime(device);
        var clear = new VulkanBackbufferClearState();
        var scope = new VulkanWorldPassScope(sampleCount: 1);
        var world = new OracleWorldPhase(scope, pipeline, vertices);
        var phase = new VulkanWorldScenePhase(
            lifetime,
            clear,
            sampleCount: static () => 1,
            scope,
            world,
            controller);
        device.Clear();

        lifetime.BeginFrame();
        WorldRenderFrameOutcome outcome;
        try
        {
            outcome = phase.Render(new RenderFrameInput(1.0 / 60.0, 1280, 720));
        }
        finally
        {
            lifetime.EndFrame();
        }

        Assert.Equal(OracleWorldPhase.Expected, outcome);
        Assert.Equal(1, world.RenderCount);
        RenderPackDiagnosticsSnapshot diagnostics = controller?.CaptureDiagnostics()
            ?? RenderPackDiagnosticsSnapshot.Retail;
        return new DefaultPathOracleSnapshot(
            device.Calls.OfType<GpuRecordedPassBegin>()
                .Select(call => call.Name)
                .ToArray(),
            device.CreatedPipelines
                .Select(created =>
                    $"{created.Description.Name}|{created.Description.Shaders.Name}"
                    + $"|pack={created.Description.UsesRenderPackShaderAbi}"
                    + $"|samples={created.Description.SampleCount}"
                    + $"|format={created.Description.ColorFormat}")
                .ToArray(),
            DrawCalls: device.Calls.Count(call => call is GpuRecordedDraw
                or GpuRecordedDrawIndexed
                or GpuRecordedMultiDrawIndirect),
            DispatchCalls: 0,
            world.FramebufferSha256,
            CaptureResourceLedger(device),
            IsRetailSelection: diagnostics.IsRetail,
            HasActivePackRuntime: controller?.ActiveRuntime is not null,
            HasPackShaderVariant: device.CreatedPipelines.Any(created =>
                created.Description.UsesRenderPackShaderAbi),
            PackResources: new DefaultPathPackResourceLedger(
                diagnostics.RetainedGpuBytes,
                diagnostics.TransientGpuBytes,
                diagnostics.ImageCount,
                diagnostics.BufferCount,
                diagnostics.DrawCalls,
                diagnostics.DispatchCalls,
                diagnostics.Passes.Count));
    }

    private static DefaultPathResourceLedger CaptureResourceLedger(
        RecordingGpuDevice device) => new(
        device.CreatedBuffers.Count,
        device.CreatedBuffers.Count(resource => !resource.IsDisposed),
        device.CreatedPipelines.Count,
        device.CreatedPipelines.Count(resource => !resource.IsDisposed),
        device.CreatedSamplers.Count,
        device.CreatedSamplers.Count(resource => !resource.IsDisposed),
        device.CreatedTextures.Count,
        device.CreatedTextures.Count(resource => !resource.IsDisposed),
        device.CreatedRenderTargets.Count,
        device.CreatedRenderTargets.Count(resource => !resource.IsDisposed),
        device.CreatedDirectionalDepthTargets.Count,
        device.CreatedDirectionalDepthTargets.Count(resource => !resource.IsDisposed),
        device.LiveTextureSlotCount,
        device.PipelineFormatLeases.Values.Sum());

    private sealed class OracleWorldPhase(
        VulkanWorldPassScope scope,
        IGpuPipeline pipeline,
        IGpuBuffer vertices) : IWorldSceneFramePhase
    {
        private static readonly byte[] AcceptedFramebufferRgba =
        [
            0x12, 0x2b, 0x45, 0xff,
            0x3a, 0x56, 0x70, 0xff,
            0x7f, 0x93, 0xa4, 0xff,
            0xd4, 0xc1, 0x91, 0xff,
        ];

        internal static WorldRenderFrameOutcome Expected { get; } = new(1, 0, true);

        internal int RenderCount { get; private set; }

        internal string FramebufferSha256 { get; private set; } = string.Empty;

        public WorldRenderFrameOutcome Render(RenderFrameInput input)
        {
            IGpuPassEncoder encoder = Assert.IsAssignableFrom<IGpuPassEncoder>(
                scope.CurrentEncoder);
            encoder.BindPipeline(pipeline);
            encoder.BindVertexBuffer(binding: 0, vertices, offsetBytes: 0);
            encoder.SetViewport(0, 0, input.ViewportWidth, input.ViewportHeight);
            encoder.SetScissor(0, 0, input.ViewportWidth, input.ViewportHeight);
            encoder.Draw(vertexCount: 3, instanceCount: 1, firstVertex: 0, firstInstance: 0);
            FramebufferSha256 = Convert.ToHexStringLower(
                SHA256.HashData(AcceptedFramebufferRgba));
            RenderCount++;
            return Expected;
        }
    }

    private sealed record DefaultPathOracleSnapshot(
        string[] PassList,
        string[] PipelineSet,
        int DrawCalls,
        int DispatchCalls,
        string FramebufferSha256,
        DefaultPathResourceLedger Resources,
        bool IsRetailSelection,
        bool HasActivePackRuntime,
        bool HasPackShaderVariant,
        DefaultPathPackResourceLedger PackResources);

    private readonly record struct DefaultPathPackResourceLedger(
        long RetainedGpuBytes,
        long TransientGpuBytes,
        int ImageCount,
        int BufferCount,
        int DrawCalls,
        int DispatchCalls,
        int PassCount);

    private readonly record struct DefaultPathResourceLedger(
        int TotalBuffers,
        int LiveBuffers,
        int TotalPipelines,
        int LivePipelines,
        int TotalSamplers,
        int LiveSamplers,
        int TotalTextures,
        int LiveTextures,
        int TotalRenderTargets,
        int LiveRenderTargets,
        int TotalDirectionalDepthTargets,
        int LiveDirectionalDepthTargets,
        int LiveTextureSlots,
        int PipelineFormatLeases);

    private sealed class DefaultWorldPhase(VulkanWorldPassScope scope) : IWorldSceneFramePhase
    {
        internal static WorldRenderFrameOutcome Expected { get; } = new(4, 7, true);

        internal int RenderCount { get; private set; }

        public WorldRenderFrameOutcome Render(RenderFrameInput input)
        {
            Assert.NotNull(scope.CurrentEncoder);
            RenderCount++;
            return Expected;
        }
    }

    private sealed class RejectingAssets : IRenderPackAssets
    {
        internal static RejectingAssets Instance { get; } = new();

        public Stream OpenRead(string assetKey) =>
            throw new InvalidOperationException("A no-op pack has no shader assets.");
    }
}
