using System.Numerics;
using AcDream.App.Plugins;
using AcDream.App.Rendering;
using AcDream.App.Rendering.Gpu;
using AcDream.App.Rendering.Packs;
using AcDream.App.Rendering.Scene;
using AcDream.App.Rendering.Wb;
using AcDream.App.Tests.Rendering.Gpu;
using AcDream.Core.World;
using AcDream.Plugin.Abstractions.Rendering;
using AcDream.UI.Abstractions.Panels.Settings;
using DatReaderWriter.Enums;

namespace AcDream.App.Tests.Rendering.Packs;

public sealed class RenderPackLongCycleConvergenceTests
{
    private const int LongCycleCount = 12;

    [Fact]
    public void RepeatedPackResizeFailureGenerationAndFlightCyclesConvergeExactly()
    {
        using var device = new RecordingGpuDevice();
        PrimeDeviceOwnedSamplerCache(device);
        LiveGpuLedger baseline = LiveGpuLedger.Capture(device);
        var lifetime = new RecordingRendererLifetime(device);

        try
        {
            AtmosphericPostProcessGraph low = lifetime.Activate("low", 640, 360);
            ExerciseResizeFlightAndGenerationReplacement(device, low);
            lifetime.SelectRetail(640, 360);
            AssertConverged(device, baseline, lifetime);

            device.PipelineFailure = description =>
                string.Equals(description.Name, "atmospheric-filmic", StringComparison.Ordinal)
                    ? new InvalidOperationException("injected candidate pipeline failure")
                    : null;
            RenderPackActivationSnapshot failed = lifetime.Request(
                Selection("medium") with
                {
                    SettingOverrides = RenderPackSettingOverrides.Empty.Set("exposure", "1.05"),
                },
                704,
                396);
            Assert.Equal(RenderPackActivationState.FailedToRetail, failed.State);
            Assert.Contains("injected candidate pipeline failure", failed.Reason, StringComparison.Ordinal);
            AssertConverged(device, baseline, lifetime);

            device.PipelineFailure = null;
            AtmosphericPostProcessGraph recovered = lifetime.Activate("medium", 704, 396);
            RenderOnePostFrame(device, recovered, 704, 396);
            lifetime.SelectRetail(704, 396);
            AssertConverged(device, baseline, lifetime);

            string[] presets = ["low", "medium", "high"];
            for (int cycle = 0; cycle < LongCycleCount; cycle++)
            {
                foreach (string preset in presets)
                {
                    int width = 640 + cycle % 3 * 64;
                    int height = 360 + cycle % 3 * 36;
                    AtmosphericPostProcessGraph graph = lifetime.Activate(
                        preset,
                        width,
                        height);

                    RecordingGpuRenderTarget initial = Assert.IsType<RecordingGpuRenderTarget>(
                        graph.PrepareWorldTarget(width, height, sampleCount: 1));
                    RecordingGpuRenderTarget resized = Assert.IsType<RecordingGpuRenderTarget>(
                        graph.PrepareWorldTarget(width + 16, height + 9, sampleCount: 1));
                    Assert.True(initial.IsDisposed);
                    RecordingGpuRenderTarget restored = Assert.IsType<RecordingGpuRenderTarget>(
                        graph.PrepareWorldTarget(width, height, sampleCount: 1));
                    Assert.True(resized.IsDisposed);

                    RenderOnePostFrame(device, graph, width, height);
                    lifetime.SelectRetail(width, height);
                    Assert.True(restored.IsDisposed);
                    AssertConverged(device, baseline, lifetime);
                }
            }

            _ = lifetime.Activate("high", 800, 450);
            Assert.NotEqual(baseline, LiveGpuLedger.Capture(device));
        }
        finally
        {
            lifetime.Dispose();
        }

        Assert.Equal(0, lifetime.RegisteredPackCount);
        AssertConverged(device, baseline, lifetime);
        Assert.All(
            device.CreatedBuffers.Where(static value =>
                value.Name.StartsWith("directional-shadow-", StringComparison.Ordinal)),
            static value => Assert.True(value.IsDisposed));
    }

    [Fact]
    public void DeviceRecreationIsFullRendererTeardownThenANewContextAndDevice()
    {
        RecordingGpuDevice firstDevice = new();
        PrimeDeviceOwnedSamplerCache(firstDevice);
        LiveGpuLedger firstBaseline = LiveGpuLedger.Capture(firstDevice);
        var firstLifetime = new RecordingRendererLifetime(firstDevice);
        AtmosphericPostProcessGraph firstGraph = firstLifetime.Activate("low", 640, 360);
        RenderOnePostFrame(firstDevice, firstGraph, 640, 360);
        Assert.Equal(1, firstLifetime.ActivationGeneration);
        RecordingGpuPipeline firstPipeline = Assert.Single(
            firstDevice.CreatedPipelines,
            static value => string.Equals(
                value.Description.Name,
                "atmospheric-filmic",
                StringComparison.Ordinal));

        firstLifetime.Dispose();
        Assert.Equal(0, firstLifetime.RegisteredPackCount);
        AssertConverged(firstDevice, firstBaseline, firstLifetime);
        Assert.True(firstPipeline.IsDisposed);
        firstDevice.Dispose();
        Assert.Throws<ObjectDisposedException>(() => firstDevice.BeginFrame());

        using var secondDevice = new RecordingGpuDevice();
        PrimeDeviceOwnedSamplerCache(secondDevice);
        LiveGpuLedger secondBaseline = LiveGpuLedger.Capture(secondDevice);
        var secondLifetime = new RecordingRendererLifetime(secondDevice);
        try
        {
            AtmosphericPostProcessGraph secondGraph = secondLifetime.Activate(
                "low",
                640,
                360);
            RenderOnePostFrame(secondDevice, secondGraph, 640, 360);
            Assert.Equal(1, secondLifetime.ActivationGeneration);
            RecordingGpuPipeline secondPipeline = Assert.Single(
                secondDevice.CreatedPipelines,
                static value => string.Equals(
                    value.Description.Name,
                    "atmospheric-filmic",
                    StringComparison.Ordinal));
            Assert.NotSame(firstPipeline, secondPipeline);
            Assert.Equal(1, secondBaseline.TextureSlots);
            Assert.True(secondDevice.LiveTextureSlotCount > secondBaseline.TextureSlots);
        }
        finally
        {
            secondLifetime.Dispose();
        }

        Assert.Equal(0, secondLifetime.RegisteredPackCount);
        AssertConverged(secondDevice, secondBaseline, secondLifetime);
    }

    private static void ExerciseResizeFlightAndGenerationReplacement(
        RecordingGpuDevice device,
        AtmosphericPostProcessGraph graph)
    {
        var retainedTransforms = new DirectionalShadowTransformBufferSet(device);
        DirectionalShadowPreparedDraws world = CreateWorldDraws(
            device.DefaultTextureSlot,
            RenderSceneGeneration.FromRaw(1),
            casterBuildSequence: 1);
        DirectionalShadowTerrainPreparedDraws terrain = CreateTerrainDraws(frameSequence: 1);
        using IGpuBuffer worldVertices = Buffer(device, "lifetime-world-v", GpuBufferUsage.Vertex);
        using IGpuBuffer worldIndices = Buffer(device, "lifetime-world-i", GpuBufferUsage.Index);
        using IGpuBuffer terrainVertices = Buffer(device, "lifetime-terrain-v", GpuBufferUsage.Vertex);
        using IGpuBuffer terrainIndices = Buffer(device, "lifetime-terrain-i", GpuBufferUsage.Index);
        var worldGeometry = new DirectionalShadowMeshGeometry(worldVertices, worldIndices);
        var terrainGeometry = new DirectionalShadowTerrainGeometry(
            terrainVertices,
            terrainIndices);

        RenderShadowFrame(
            device,
            graph,
            retainedTransforms,
            world,
            terrain,
            worldGeometry,
            terrainGeometry,
            640,
            360);
        RenderShadowFrame(
            device,
            graph,
            retainedTransforms,
            world,
            terrain,
            worldGeometry,
            terrainGeometry,
            640,
            360);

        RecordingGpuBuffer[] firstTopologyBuffers = device.CreatedBuffers
            .Where(static value => value.Name.StartsWith(
                "directional-shadow-",
                StringComparison.Ordinal))
            .ToArray();
        Assert.Equal(5, firstTopologyBuffers.Length);
        Assert.All(firstTopologyBuffers, static value => Assert.False(value.IsDisposed));

        RebuildWorldDraws(
            world,
            device.DefaultTextureSlot,
            RenderSceneGeneration.FromRaw(2),
            casterBuildSequence: 2);
        RebuildTerrainDraws(terrain, frameSequence: 2);
        RenderShadowFrame(
            device,
            graph,
            retainedTransforms,
            world,
            terrain,
            worldGeometry,
            terrainGeometry,
            640,
            360);
        RenderShadowFrame(
            device,
            graph,
            retainedTransforms,
            world,
            terrain,
            worldGeometry,
            terrainGeometry,
            640,
            360);

        Assert.All(firstTopologyBuffers, static value => Assert.True(value.IsDisposed));
        Assert.Equal(
            5,
            device.CreatedBuffers.Count(static value =>
                value.Name.StartsWith("directional-shadow-", StringComparison.Ordinal)
                && !value.IsDisposed));
        retainedTransforms.Dispose();
        Assert.All(
            device.CreatedBuffers.Where(static value => value.Name.Contains(
                "directional-shadow-transforms-",
                StringComparison.Ordinal)),
            static value => Assert.True(value.IsDisposed));
    }

    private static void RenderShadowFrame(
        RecordingGpuDevice device,
        AtmosphericPostProcessGraph graph,
        DirectionalShadowTransformBufferSet retainedTransforms,
        DirectionalShadowPreparedDraws world,
        DirectionalShadowTerrainPreparedDraws terrain,
        DirectionalShadowMeshGeometry worldGeometry,
        DirectionalShadowTerrainGeometry terrainGeometry,
        int width,
        int height)
    {
        using IGpuFrame frame = device.BeginFrame();
        WorldTransformFrameSlice transforms = retainedTransforms.Publish(
            frame,
            world.BuildSequence,
            world.Transforms,
            world.DynamicTransformSlots,
            world.AllDynamicTransformSlots);
        var shadows = Assert.IsType<DirectionalSunShadowRenderer>(
            graph.DirectionalShadowReceivers);
        DirectionalSunShadowDiagnostics diagnostics = shadows.RenderPrepared(
            frame,
            EnabledEnvironment(),
            Matrix4x4.Identity,
            Matrix4x4.CreatePerspectiveFieldOfView(1f, 16f / 9f, 0.1f, 500f),
            cameraNearMeters: 0.1f,
            casterDepthPaddingMeters: 48f,
            world,
            terrain,
            worldGeometry,
            terrainGeometry,
            transforms);
        Assert.Equal(2, diagnostics.CascadeCount);

        IGpuRenderTarget target = graph.PrepareWorldTarget(width, height, sampleCount: 1);
        RecordWorldPass(frame, target);
        AtmosphericFrameInputs inputs = Inputs(width, height, isOutdoor: true);
        graph.RenderPostProcess(frame, in inputs);
    }

    private static void RenderOnePostFrame(
        RecordingGpuDevice device,
        AtmosphericPostProcessGraph graph,
        int width,
        int height)
    {
        using IGpuFrame frame = device.BeginFrame();
        IGpuRenderTarget target = graph.PrepareWorldTarget(width, height, sampleCount: 1);
        RecordWorldPass(frame, target);
        AtmosphericFrameInputs inputs = Inputs(width, height, isOutdoor: false);
        graph.RenderPostProcess(frame, in inputs);
    }

    private static AtmosphericFrameInputs Inputs(
        int width,
        int height,
        bool isOutdoor) => new(
        new Vector2(0.5f, 0.35f),
        SunIsOnScreen: true,
        SunElevationDegrees: isOutdoor ? 20f : -10f,
        new Vector3(1f, 0.85f, 0.65f),
        Vector3.Normalize(new Vector3(0.2f, 0.5f, 0.8f)),
        SunDirectionalBrightness: 1f,
        Matrix4x4.Identity,
        ActiveDayGroup: isOutdoor ? 0 : -1,
        WeatherKind.Clear,
        WeatherIntensity: 0f,
        DeltaSeconds: 1d / 60d,
        width,
        height,
        IsOutdoor: isOutdoor);

    private static void RecordWorldPass(IGpuFrame frame, IGpuRenderTarget world)
    {
        using IGpuPassEncoder _ = frame.BeginPass(new GpuPassDescription
        {
            Name = "lifetime-world-hdr",
            Color = new GpuColorAttachment(
                world,
                GpuLoadOp.Clear,
                GpuStoreOp.Store,
                Vector4.Zero),
            Depth = new GpuDepthAttachment(
                GpuLoadOp.Clear,
                GpuStoreOp.Store,
                1f,
                0),
            SampleCount = 1,
        });
    }

    private static DirectionalShadowPreparedDraws CreateWorldDraws(
        GpuTextureSlot cutoutSlot,
        RenderSceneGeneration generation,
        ulong casterBuildSequence)
    {
        var draws = new DirectionalShadowPreparedDraws();
        RebuildWorldDraws(draws, cutoutSlot, generation, casterBuildSequence);
        return draws;
    }

    private static void RebuildWorldDraws(
        DirectionalShadowPreparedDraws draws,
        GpuTextureSlot cutoutSlot,
        RenderSceneGeneration generation,
        ulong casterBuildSequence)
    {
        Assert.True(draws.TryBegin(generation, casterBuildSequence, estimatedInstances: 2));
        Matrix4x4 opaque = Matrix4x4.CreateTranslation(1f, 2f, 3f);
        Matrix4x4 cutout = Matrix4x4.CreateRotationZ(0.3f)
            * Matrix4x4.CreateTranslation(4f, 5f, 6f);
        draws.Add(
            0,
            0,
            6,
            GpuTextureSlot.Unassigned,
            0,
            CullMode.CounterClockwise,
            DirectionalShadowCasterMaterial.Opaque,
            in opaque);
        draws.Add(
            6,
            4,
            12,
            cutoutSlot,
            2,
            CullMode.None,
            DirectionalShadowCasterMaterial.AlphaCutout,
            in cutout);
        DirectionalShadowPreparationStats stats = default;
        draws.Complete(generation, casterBuildSequence, in stats);
    }

    private static DirectionalShadowTerrainPreparedDraws CreateTerrainDraws(
        long frameSequence)
    {
        var draws = new DirectionalShadowTerrainPreparedDraws();
        RebuildTerrainDraws(draws, frameSequence);
        return draws;
    }

    private static void RebuildTerrainDraws(
        DirectionalShadowTerrainPreparedDraws draws,
        long frameSequence)
    {
        Assert.True(draws.TryBegin(frameSequence, estimatedCommands: 1));
        var range = new DirectionalShadowTerrainRange(20, 60);
        draws.Add(in range);
        draws.Complete(frameSequence);
    }

    private static DirectionalShadowEnvironmentState EnabledEnvironment() => new(
        DirectionalShadowGateReason.Enabled,
        Vector3.Normalize(new Vector3(0.2f, 0.3f, 1f)),
        LightElevationSin: 0.94f,
        Strength: 0.8f,
        SoftnessMultiplier: 1.25f,
        SourceKind: AuthoredCelestialShadowSourceKind.Sun);

    private static IGpuBuffer Buffer(
        RecordingGpuDevice device,
        string name,
        GpuBufferUsage usage) => device.CreateBuffer(new GpuBufferDescription(
        name,
        4096,
        usage | GpuBufferUsage.TransferDestination,
        GpuMemoryResidency.DeviceLocal));

    private static RenderPackSelectionSettings Selection(string preset) => new(
        BuiltInAtmosphericRenderPack.Descriptor.Id,
        BuiltInAtmosphericRenderPack.Descriptor.PackVersion.ToString(),
        preset);

    private static void AssertConverged(
        RecordingGpuDevice device,
        LiveGpuLedger baseline,
        RecordingRendererLifetime lifetime)
    {
        Assert.Equal(baseline, LiveGpuLedger.Capture(device));
        Assert.Equal(0, device.OpenFrameCount);
        Assert.Empty(device.PipelineFormatLeases);
        Assert.Equal(0, lifetime.LiveReceiverCandidates);
        Assert.Equal(lifetime.IsDisposed ? 0 : 1, lifetime.RegisteredPackCount);
        Assert.Null(lifetime.ActiveRuntime);
    }

    private static void PrimeDeviceOwnedSamplerCache(RecordingGpuDevice device)
    {
        _ = device.CreateSampler(GpuSamplerDescription.WorldClamp);
    }

    private static IRenderPackAssets BuiltInAssets() =>
        BuiltInAtmosphericRenderPack.CreateAssets(Path.Combine(
            RepositoryRoot(),
            "src",
            "AcDream.App",
            "Rendering",
            "Shaders",
            "spv"));

    private static string RepositoryRoot()
    {
        var directory = new DirectoryInfo(AppContext.BaseDirectory);
        while (directory is not null
            && !File.Exists(Path.Combine(directory.FullName, "AcDream.slnx")))
        {
            directory = directory.Parent;
        }
        return directory?.FullName
            ?? throw new InvalidOperationException("Could not locate repository root.");
    }

    private readonly record struct LiveGpuLedger(
        int Buffers,
        int Pipelines,
        int Samplers,
        int Textures,
        int RenderTargets,
        int DirectionalDepthTargets,
        int TextureSlots,
        int PipelineFormatLeases)
    {
        internal static LiveGpuLedger Capture(RecordingGpuDevice device) => new(
            device.CreatedBuffers.Count(static value => !value.IsDisposed),
            device.CreatedPipelines.Count(static value => !value.IsDisposed),
            device.CreatedSamplers.Count(static value => !value.IsDisposed),
            device.CreatedTextures.Count(static value => !value.IsDisposed),
            device.CreatedRenderTargets.Count(static value => !value.IsDisposed),
            device.CreatedDirectionalDepthTargets.Count(static value => !value.IsDisposed),
            device.LiveTextureSlotCount,
            device.PipelineFormatLeases.Values.Sum());
    }

    private sealed class RecordingRendererLifetime : IDisposable
    {
        private readonly BufferedRenderPackRegistry _registry = new();
        private readonly IDisposable _registration;
        private readonly RecordingReceiverCoordinator _receivers = new();
        private bool _disposed;

        internal RecordingRendererLifetime(RecordingGpuDevice device)
        {
            _registration = _registry.Register(
                BuiltInAtmosphericRenderPack.Descriptor,
                BuiltInAssets());
            Controller = new RenderPackController(
                () => RenderPackCatalog.Build(
                    _registry.Snapshot(),
                    RenderPackCapabilityResolver.Resolve(device.Capabilities)),
                new AtmosphericRenderPackRuntimeFactory(device),
                _receivers,
                InlineRenderPackPreparationScheduler.Instance);
        }

        private RenderPackController Controller { get; }

        internal long ActivationGeneration => Controller.Snapshot.ActivationGeneration;

        internal IRenderPackRuntime? ActiveRuntime => Controller.ActiveRuntime;

        internal int LiveReceiverCandidates => _receivers.LiveCandidateCount;

        internal bool IsDisposed => _disposed;

        internal int RegisteredPackCount => _disposed ? 0 : _registry.Snapshot().Count;

        internal AtmosphericPostProcessGraph Activate(
            string preset,
            int width,
            int height)
        {
            RenderPackActivationSnapshot snapshot = Request(
                Selection(preset),
                width,
                height);
            Assert.Equal(RenderPackActivationState.Active, snapshot.State);
            Assert.Null(snapshot.Reason);
            Assert.Equal(1, LiveReceiverCandidates);
            return Assert.IsType<AtmosphericPostProcessGraph>(Controller.ActiveRuntime);
        }

        internal RenderPackActivationSnapshot Request(
            RenderPackSelectionSettings selection,
            int width,
            int height)
        {
            Controller.Request(selection);
            return Controller.ApplyAtFrameBoundary(
                new RenderPackActivationExtent(width, height, 1));
        }

        internal void SelectRetail(int width, int height)
        {
            RenderPackActivationSnapshot snapshot = Request(
                RenderPackSelectionSettings.Retail,
                width,
                height);
            Assert.Equal(RenderPackActivationState.Retail, snapshot.State);
            Assert.True(snapshot.Selection.IsRetail);
        }

        public void Dispose()
        {
            if (_disposed)
                return;
            Controller.Dispose();
            _registration.Dispose();
            Assert.Empty(_registry.Snapshot());
            _registry.Dispose();
            _disposed = true;
        }
    }

    private sealed class RecordingReceiverCoordinator :
        IRenderPackReceiverPipelineCoordinator
    {
        private Candidate? _active;

        internal int LiveCandidateCount { get; private set; }

        public IRenderPackReceiverPipelineCandidate Prepare(
            IDirectionalShadowReceiverSource? source,
            int sampleCount)
        {
            ArgumentOutOfRangeException.ThrowIfNegativeOrZero(sampleCount);
            var candidate = new Candidate(this, source is not null);
            LiveCandidateCount++;
            return candidate;
        }

        public void Publish(IRenderPackReceiverPipelineCandidate candidate)
        {
            if (candidate is not Candidate prepared
                || !ReferenceEquals(prepared.Owner, this))
            {
                throw new ArgumentException(
                    "Receiver candidate belongs to another coordinator.",
                    nameof(candidate));
            }
            prepared.Publish();
            _active?.Dispose();
            _active = prepared;
        }

        public void Clear()
        {
            _active?.Dispose();
            _active = null;
        }

        private void Released() => LiveCandidateCount--;

        private sealed class Candidate(
            RecordingReceiverCoordinator owner,
            bool hasDirectionalSource) : IRenderPackReceiverPipelineCandidate
        {
            private bool _disposed;
            private bool _published;

            internal RecordingReceiverCoordinator Owner { get; } = owner;

            internal void Publish()
            {
                ObjectDisposedException.ThrowIf(_disposed, this);
                if (!hasDirectionalSource)
                {
                    throw new InvalidOperationException(
                        "The atmospheric candidate lost its directional receiver source.");
                }
                if (_published)
                    throw new InvalidOperationException("Receiver candidate was published twice.");
                _published = true;
            }

            public void Dispose()
            {
                if (_disposed)
                    return;
                _disposed = true;
                Owner.Released();
            }
        }
    }
}
