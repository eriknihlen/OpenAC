using AcDream.App.Plugins;
using AcDream.App.Rendering;
using AcDream.App.Rendering.Gpu;
using AcDream.App.Rendering.Gpu.Vk;
using AcDream.App.Rendering.Packs;
using AcDream.App.Rendering.Wb;
using AcDream.App.Tests.Architecture;
using AcDream.App.Tests.Rendering.Gpu;
using AcDream.Plugin.Abstractions.Rendering;
using AcDream.UI.Abstractions.Panels.Settings;
using Silk.NET.Vulkan;

namespace AcDream.App.Tests.Rendering.Packs;

public sealed class RenderPackRuntimeFailureRecoveryTests
{
    [Fact]
    public void DirectionalShadowFailureCancelsBorrowedTransformsBeforePackRetirementAndRetailReplay()
    {
        var render = typeof(VulkanWorldScenePhase).GetMethod(
            nameof(VulkanWorldScenePhase.Render))!;
        string[] lifetimeCalls = CompiledCallGraph.Read(render)
            .Where(call =>
                (call.Target.DeclaringType == typeof(WbDrawDispatcher)
                    && call.Target.Name == nameof(
                        WbDrawDispatcher.CancelDirectionalShadowTransformFrame))
                || (call.Target.DeclaringType == typeof(RenderPackController)
                    && call.Target.Name == nameof(RenderPackController.OnRuntimeFailure))
                || (call.Target.DeclaringType == typeof(VulkanWorldScenePhase)
                    && call.Target.Name == "RenderRetail"))
            .Select(call => call.Target.Name)
            .ToArray();

        bool hasSafeLateFailureHandoff = Enumerable.Range(
                0,
                Math.Max(0, lifetimeCalls.Length - 2))
            .Any(index =>
                lifetimeCalls[index]
                    == nameof(WbDrawDispatcher.CancelDirectionalShadowTransformFrame)
                && lifetimeCalls[index + 1]
                    == nameof(RenderPackController.OnRuntimeFailure)
                && lifetimeCalls[index + 2] == "RenderRetail");

        Assert.True(
            hasSafeLateFailureHandoff,
            "A post-publication directional-shadow failure must cancel the "
            + "borrowed transform frame before retiring the pack-owned buffer "
            + "and replaying the frame through retail rendering.");
    }

    [Fact]
    public void EnhancedWorldFailureQuarantinesPackAndNextFrameUsesDefaultRenderer()
    {
        using var rig = new FailureRig(failEnhancedWorld: true, failPostProcess: false);

        WorldRenderFrameOutcome failedFrame = rig.RenderFrame();

        Assert.Equal(default, failedFrame);
        Assert.Equal(RenderPackActivationState.FailedToRetail, rig.Controller.Snapshot.State);
        Assert.Contains(
            "Atmospheric world rendering failed: enhanced world failed",
            rig.Controller.Snapshot.Reason,
            StringComparison.Ordinal);
        Assert.Null(rig.Controller.ActiveRuntime);
        Assert.True(rig.Factory.Runtime.Disposed);

        WorldRenderFrameOutcome recovered = rig.RenderFrame();

        Assert.Equal(FailingWorldPhase.Success, recovered);
        Assert.Equal(2, rig.World.RenderCount);
        Assert.Contains(
            rig.Device.Calls.OfType<GpuRecordedPassBegin>(),
            call => call.Name == "vk-world");
        Assert.Equal(RenderPackActivationState.FailedToRetail, rig.Controller.Snapshot.State);
        Assert.Contains("enhanced world failed", rig.Controller.Snapshot.Reason);

        rig.Controller.Request(rig.Selection);
        Assert.Equal(FailingWorldPhase.Success, rig.RenderFrame());
        Assert.Equal(1, rig.Factory.BuildCount);
        Assert.Contains("will not be retried", rig.Controller.Snapshot.Reason);
    }

    [Fact]
    public void PostProcessFailureKeepsWorldOutcomeAndNextFrameUsesDefaultRenderer()
    {
        using var rig = new FailureRig(failEnhancedWorld: false, failPostProcess: true);

        WorldRenderFrameOutcome failedFrame = rig.RenderFrame();

        Assert.Equal(FailingWorldPhase.Success, failedFrame);
        Assert.Equal(RenderPackActivationState.FailedToRetail, rig.Controller.Snapshot.State);
        Assert.Contains(
            "Atmospheric post-processing failed: post process failed",
            rig.Controller.Snapshot.Reason,
            StringComparison.Ordinal);
        Assert.Null(rig.Controller.ActiveRuntime);
        Assert.True(rig.Factory.Runtime.Disposed);

        WorldRenderFrameOutcome recovered = rig.RenderFrame();

        Assert.Equal(FailingWorldPhase.Success, recovered);
        Assert.Equal(2, rig.World.RenderCount);
        Assert.Contains(
            rig.Device.Calls.OfType<GpuRecordedPassBegin>(),
            call => call.Name == "vk-world");
        Assert.Equal(RenderPackActivationState.FailedToRetail, rig.Controller.Snapshot.State);
        Assert.Contains("post process failed", rig.Controller.Snapshot.Reason);
    }

    [Theory]
    [InlineData(Result.ErrorDeviceLost)]
    [InlineData(Result.ErrorOutOfHostMemory)]
    [InlineData(Result.ErrorOutOfDeviceMemory)]
    public void FatalVulkanPostProcessFailureIsRethrownWithoutPretendingRetailCanRecover(
        Result result)
    {
        var failure = new VulkanCallException("pack post process", result);
        using var rig = new FailureRig(
            enhancedWorldFailure: null,
            postProcessFailure: failure);

        VulkanCallException thrown = Assert.Throws<VulkanCallException>(
            () => rig.RenderFrame());

        Assert.Same(failure, thrown);
        Assert.Equal(RenderPackActivationState.Active, rig.Controller.Snapshot.State);
        Assert.False(rig.Factory.Runtime.Disposed);
    }

    [Fact]
    public void NonTerminalVulkanPostProcessFailureQuarantinesPackAndFallsBack()
    {
        using var rig = new FailureRig(
            enhancedWorldFailure: null,
            postProcessFailure: new VulkanCallException(
                "pack post process",
                Result.ErrorFormatNotSupported));

        WorldRenderFrameOutcome failedFrame = rig.RenderFrame();

        Assert.Equal(FailingWorldPhase.Success, failedFrame);
        Assert.Equal(RenderPackActivationState.FailedToRetail, rig.Controller.Snapshot.State);
        Assert.Null(rig.Controller.ActiveRuntime);
        Assert.True(rig.Factory.Runtime.Disposed);
        Assert.Contains("ErrorFormatNotSupported", rig.Controller.Snapshot.Reason,
            StringComparison.Ordinal);
    }

    private sealed class FailureRig : IDisposable
    {
        private readonly BufferedRenderPackRegistry _registry = new();
        private readonly IDisposable _registration;
        private readonly GpuDeviceFrameLifetime _lifetime;
        private readonly VulkanWorldScenePhase _phase;

        internal FailureRig(bool failEnhancedWorld, bool failPostProcess)
            : this(
                failEnhancedWorld
                    ? new InvalidOperationException("enhanced world failed")
                    : null,
                failPostProcess
                    ? new InvalidOperationException("post process failed")
                    : null)
        {
        }

        internal FailureRig(
            Exception? enhancedWorldFailure,
            Exception? postProcessFailure)
        {
            RenderPackDescriptor descriptor = Descriptor();
            _registration = _registry.Register(descriptor, EmptyAssets.Instance);
            Device = new RecordingGpuDevice();
            Factory = new FailingGraphFactory(Device, postProcessFailure);
            Controller = new RenderPackController(
                () => RenderPackCatalog.Build(
                    _registry.Snapshot(),
                    RenderPackHostCapabilities.Conformance),
                Factory,
                preparationScheduler: InlineRenderPackPreparationScheduler.Instance);
            Selection = new RenderPackSelectionSettings(
                descriptor.Id,
                descriptor.PackVersion.ToString(),
                "default");
            Controller.Request(Selection);
            _lifetime = new GpuDeviceFrameLifetime(Device);
            var scope = new VulkanWorldPassScope(sampleCount: 1);
            World = new FailingWorldPhase(enhancedWorldFailure);
            _phase = new VulkanWorldScenePhase(
                _lifetime,
                new VulkanBackbufferClearState(),
                sampleCount: static () => 1,
                scope,
                World,
                Controller,
                new AtmosphericFrameInputState());
        }

        internal RecordingGpuDevice Device { get; }

        internal FailingGraphFactory Factory { get; }

        internal RenderPackController Controller { get; }

        internal RenderPackSelectionSettings Selection { get; }

        internal FailingWorldPhase World { get; }

        internal WorldRenderFrameOutcome RenderFrame()
        {
            _lifetime.BeginFrame();
            try
            {
                return _phase.Render(new RenderFrameInput(1.0 / 60.0, 1280, 720));
            }
            finally
            {
                _lifetime.EndFrame();
            }
        }

        public void Dispose()
        {
            Controller.Dispose();
            _registration.Dispose();
            _registry.Dispose();
            Device.Dispose();
        }
    }

    private sealed class FailingGraphFactory(
        RecordingGpuDevice device,
        Exception? postProcessFailure) : IRenderPackRuntimeFactory
    {
        internal FailingGraphRuntime Runtime { get; private set; } = null!;

        internal int BuildCount { get; private set; }

        public IRenderPackRuntime Build(
            RenderPackDescriptor descriptor,
            ValidatedRenderPackShaderAssets assets,
            RenderQualityPreset preset,
            IReadOnlyDictionary<string, string> userSettingOverrides)
        {
            BuildCount++;
            Runtime = new FailingGraphRuntime(device, descriptor, preset, postProcessFailure);
            return Runtime;
        }
    }

    private sealed class FailingGraphRuntime : IAtmosphericWorldGraphRuntime
    {
        private readonly RecordingGpuDevice _device;
        private readonly Exception? _postProcessFailure;
        private IGpuRenderTarget? _target;

        internal FailingGraphRuntime(
            RecordingGpuDevice device,
            RenderPackDescriptor descriptor,
            RenderQualityPreset preset,
            Exception? postProcessFailure)
        {
            _device = device;
            Descriptor = descriptor;
            Preset = preset;
            _postProcessFailure = postProcessFailure;
        }

        public RenderPackDescriptor Descriptor { get; }

        public RenderQualityPreset Preset { get; }

        internal bool Disposed { get; private set; }

        public IGpuRenderTarget PrepareWorldTarget(int width, int height, int sampleCount) =>
            _target ??= _device.CreateRenderTarget(new GpuRenderTargetDescription(
                "failing-pack-target",
                width,
                height,
                GpuTextureFormat.Rgba16FloatRenderTarget,
                GpuTextureFormat.Depth24Stencil8,
                sampleCount));

        public void RenderPostProcess(IGpuFrame frame, in AtmosphericFrameInputs inputs)
        {
            if (_postProcessFailure is not null)
                throw _postProcessFailure;
        }

        public void Dispose()
        {
            if (Disposed)
                return;
            Disposed = true;
            _target?.Dispose();
            _target = null;
        }
    }

    private sealed class FailingWorldPhase(Exception? firstFailure) : IWorldSceneFramePhase
    {
        internal static WorldRenderFrameOutcome Success { get; } = new(5, 9, true);

        internal int RenderCount { get; private set; }

        public WorldRenderFrameOutcome Render(RenderFrameInput input)
        {
            RenderCount++;
            if (firstFailure is not null && RenderCount == 1)
                throw firstFailure;
            return Success;
        }
    }

    private static RenderPackDescriptor Descriptor() => new(
        "failure.pack",
        "Failure pack",
        new Version(1, 0, 0),
        RenderPackApi.Current,
        RenderPackTier.Tier1,
        [],
        [],
        [],
        [],
        [],
        [],
        [new RenderQualityPreset("default", "Default", [], [], [], 0, 0, 0, 0, 0)],
        [],
        null)
    {
        FeatureSummary = "Runtime failure test pack.",
    };

    private sealed class EmptyAssets : IRenderPackAssets
    {
        internal static EmptyAssets Instance { get; } = new();

        public Stream OpenRead(string assetKey) =>
            throw new InvalidOperationException("The failure test pack declares no assets.");
    }
}
