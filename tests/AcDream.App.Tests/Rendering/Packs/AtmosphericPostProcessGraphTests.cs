using System.Globalization;
using System.Numerics;
using System.Runtime.InteropServices;
using System.Text.RegularExpressions;
using AcDream.App.Plugins;
using AcDream.App.Rendering;
using AcDream.App.Rendering.Gpu;
using AcDream.App.Rendering.Packs;
using AcDream.App.Rendering.Wb;
using AcDream.App.Tests.Rendering.Gpu;
using AcDream.Core.World;
using AcDream.Plugin.Abstractions.Rendering;
using AcDream.UI.Abstractions.Panels.Settings;

namespace AcDream.App.Tests.Rendering.Packs;

public sealed class AtmosphericPostProcessGraphTests
{
    [Fact]
    public void LowSamplesEveryPostTimerTogetherOnEveryFourthFrame()
    {
        var device = new RecordingGpuDevice();
        using var graph = Graph(device, "low");
        IGpuRenderTarget world = graph.PrepareWorldTarget(1280, 720, 1);
        AtmosphericFrameInputs inputs = Inputs(1280, 720);

        for (int serial = 1; serial <= 4; serial++)
        {
            device.Clear();
            using IGpuFrame frame = device.BeginFrame();
            RecordWorldPass(frame, world);
            graph.RenderPostProcess(frame, in inputs);
            frame.End();

            string[] measured = device.OfKind<GpuRecordedTimerScope>()
                .Select(static scope => scope.Name)
                .Where(static name => name.StartsWith(
                    "atmospheric-",
                    StringComparison.Ordinal))
                .ToArray();
            if (serial < AtmosphericGpuTimerSampling.LowIntervalFrames)
            {
                Assert.Empty(measured);
            }
            else
            {
                Assert.Equal(
                    [
                        "atmospheric-sun-occlusion",
                        "atmospheric-sun-rays",
                        "atmospheric-bloom-downsample",
                        "atmospheric-bloom-blur-horizontal",
                        "atmospheric-bloom-blur-vertical",
                        "atmospheric-filmic",
                    ],
                    measured);
            }
        }
    }

    [Fact]
    public void LowUsesQuarterResolutionSeparableBloomWithoutDroppingHeadlineInputs()
    {
        var device = new RecordingGpuDevice();
        using var graph = Graph(device, "low");
        Assert.True(Assert.IsType<DirectionalSunShadowRenderer>(
            graph.DirectionalShadowReceivers).MultiviewCascadesEnabled);
        IGpuRenderTarget world = graph.PrepareWorldTarget(1280, 720, 1);
        device.Clear();

        using IGpuFrame frame = device.BeginFrame();
        RecordWorldPass(frame, world);
        AtmosphericFrameInputs inputs = Inputs(1280, 720);
        graph.RenderPostProcess(frame, in inputs);
        frame.End();

        Assert.Equal(
            [
                "test-world-hdr",
                "atmospheric-sun-occlusion",
                "atmospheric-sun-rays",
                "atmospheric-bloom-downsample",
                "atmospheric-bloom-blur-horizontal",
                "atmospheric-bloom-blur-vertical",
                "atmospheric-filmic",
            ],
            device.OfKind<GpuRecordedPassBegin>().Select(call => call.Name));
        GpuRecordedUniformBind[] passBlocks = device
            .OfKind<GpuRecordedUniformBind>()
            .Where(call => call.Binding == GpuBindingModel.UniformPackPass)
            .ToArray();
        Assert.Equal(6, passBlocks.Length);
        Assert.All(
            device.OfKind<GpuRecordedUniformBind>().Where(call =>
                call.Binding is GpuBindingModel.UniformAtmosphericFrame
                    or GpuBindingModel.UniformPackPass
                    or GpuBindingModel.UniformPackSettings),
            call => Assert.Equal(
                0u,
                call.OffsetBytes
                    % device.Capabilities.MinUniformBufferOffsetAlignment));

        AtmosphericPackPassUniforms rays = ReadPass(device, passBlocks[1]);
        Assert.Equal(Vector4.Zero, rays.Params1);
        AtmosphericPackPassUniforms bloom = ReadPass(device, passBlocks[2]);
        Assert.Equal(
            new Vector4(
                graph.Settings.BloomStrength,
                AtmosphericPostProcessGraph.BloomThresholdLinear,
                AtmosphericPostProcessGraph.BloomKneeLinear,
                0f),
            bloom.Params0);
        AtmosphericPackPassUniforms horizontal = ReadPass(device, passBlocks[3]);
        Assert.Equal(new Vector4(1f / 320f, 0f, 0f, 0f), horizontal.Params0);
        AtmosphericPackPassUniforms vertical = ReadPass(device, passBlocks[4]);
        Assert.Equal(new Vector4(0f, 1f / 180f, 0f, 0f), vertical.Params0);
        AtmosphericPackPassUniforms filmic = ReadPass(device, passBlocks[5]);
        Assert.Equal(0f, filmic.Params1.Z);
        Assert.Equal(Vector4.Zero, filmic.Params2);
        Assert.Equal(Vector4.Zero, filmic.Params3);
        Assert.Contains(device.CreatedRenderTargets, target =>
            target.Description.Name == "atmospheric-bloom-a"
                && target.Description.Width == 320
                && target.Description.Height == 180);
        Assert.Contains(device.CreatedRenderTargets, target =>
            target.Description.Name == "atmospheric-bloom-b"
                && target.Description.Width == 320
                && target.Description.Height == 180);

        GpuRecordedPushConstants[] pushes = device
            .OfKind<GpuRecordedPushConstants>()
            .ToArray();
        Assert.Equal(6, pushes.Length);
        Assert.All(pushes, push =>
            Assert.NotEqual(uint.MaxValue, push.Constants.TextureIndexA));
        Assert.NotEqual(uint.MaxValue, pushes[2].Constants.TextureIndexB);
        Assert.NotEqual(uint.MaxValue, pushes[5].Constants.TextureIndexB);

        RenderPackRuntimeDiagnostics diagnostics = graph.CaptureDiagnostics();
        Assert.Equal(6, diagnostics.DrawCalls);
        Assert.Equal(7, diagnostics.ImageCount);
        Assert.Contains(diagnostics.Passes, pass =>
            pass.PassId == "atmospheric-sun-occlusion" && pass.DrawCalls == 1);
        Assert.Contains(diagnostics.Passes, pass =>
            pass.PassId == "atmospheric-sun-rays" && pass.DrawCalls == 1);
        Assert.Contains(diagnostics.Passes, pass =>
            pass.PassId == "atmospheric-bloom-downsample" && pass.DrawCalls == 1);
        Assert.Contains(diagnostics.Passes, pass =>
            pass.PassId == "atmospheric-bloom-blur-horizontal" && pass.DrawCalls == 1);
        Assert.Contains(diagnostics.Passes, pass =>
            pass.PassId == "atmospheric-bloom-blur-vertical" && pass.DrawCalls == 1);
        Assert.Contains(diagnostics.Passes, pass =>
            pass.PassId == "atmospheric-filmic" && pass.DrawCalls == 1);
    }

    [Theory]
    [InlineData(
        AuthoredCelestialShadowSourceKind.Sun,
        5,
        0x01001348u,
        0.25f,
        -0.5f,
        0.8291562f,
        0.8291562f)]
    [InlineData(
        AuthoredCelestialShadowSourceKind.DominantMoon,
        3,
        0x01001F6Au,
        -0.6f,
        0.2f,
        0.7745967f,
        0.7745967f)]
    [InlineData(
        AuthoredCelestialShadowSourceKind.SecondaryMoon,
        2,
        0x01001F67u,
        0.4f,
        0.8f,
        0.4472136f,
        0.4472136f)]
    [InlineData(
        AuthoredCelestialShadowSourceKind.None,
        -1,
        0u,
        0f,
        0f,
        1f,
        0f)]
    internal void CaptureDiagnosticsPreservesSunMoonAndNoneSourceMetadata(
        AuthoredCelestialShadowSourceKind sourceKind,
        int sourceObjectIndex,
        uint sourceGfxObjId,
        float directionX,
        float directionY,
        float directionZ,
        float elevationSin)
    {
        var device = new RecordingGpuDevice();
        using var graph = Graph(device, "medium");
        IGpuRenderTarget world = graph.PrepareWorldTarget(1280, 720, 1);
        using IGpuFrame frame = device.BeginFrame();
        RecordWorldPass(frame, world);
        AtmosphericFrameInputs inputs = Inputs(1280, 720);
        graph.RenderPostProcess(frame, in inputs);
        frame.End();
        var direction = new Vector3(directionX, directionY, directionZ);

        SetLastShadowDiagnostics(
            graph,
            new DirectionalSunShadowDiagnostics(
                GateReason: sourceKind is AuthoredCelestialShadowSourceKind.None
                    ? DirectionalShadowGateReason.NoVisibleCelestial
                    : DirectionalShadowGateReason.Enabled,
                Strength: sourceKind is AuthoredCelestialShadowSourceKind.None
                    ? 0f
                    : 0.75f,
                CascadeCount: 0,
                DrawCalls: 0,
                WorldOpaqueCommands: 0,
                WorldAlphaCutoutCommands: 0,
                TerrainCommands: 0,
                WorldPreparationSequence: 0,
                TerrainPreparationSequence: 0,
                CpuMilliseconds: 0,
                LastResolvedGpuMilliseconds: 0,
                HasResolvedGpuMeasurement: false,
                ResidentDepthBytes: 0,
                SourceKind: sourceKind,
                SourceObjectIndex: sourceObjectIndex,
                SourceGfxObjId: sourceGfxObjId,
                SurfaceToLightDirection: direction,
                LightElevationSin: elevationSin));

        RenderPackRuntimeDiagnostics diagnostics = graph.CaptureDiagnostics();

        Assert.Equal(sourceKind, diagnostics.DirectionalShadowSourceKind);
        Assert.Equal(
            sourceObjectIndex,
            diagnostics.DirectionalShadowSourceObjectIndex);
        Assert.Equal(sourceGfxObjId, diagnostics.DirectionalShadowSourceGfxObjId);
        Assert.Equal(
            direction,
            diagnostics.DirectionalShadowSurfaceToLightDirection);
        Assert.Equal(
            elevationSin,
            diagnostics.DirectionalShadowLightElevationSin);
    }

    [Fact]
    public void GraphRunsTheDeclaredHdrPassOrderAndBindsStablePackAbi()
    {
        var device = new RecordingGpuDevice();
        using var graph = Graph(device, "medium");
        IGpuRenderTarget world = graph.PrepareWorldTarget(1280, 720, 4);
        device.Clear();

        using IGpuFrame frame = device.BeginFrame();
        RecordWorldPass(frame, world);
        AtmosphericFrameInputs inputs = Inputs(1280, 720);
        graph.RenderPostProcess(frame, in inputs);
        frame.End();

        Assert.Equal(
            [
                "test-world-hdr",
                "atmospheric-sun-occlusion",
                "atmospheric-sun-rays",
                "atmospheric-bloom-downsample",
                "atmospheric-bloom-blur-horizontal",
                "atmospheric-bloom-blur-vertical",
                "atmospheric-filmic",
            ],
            device.OfKind<GpuRecordedPassBegin>().Select(call => call.Name));
        Assert.Equal(
            6,
            device.OfKind<GpuRecordedUniformBind>().Count(call =>
                call.Binding == GpuBindingModel.UniformAtmosphericFrame
                && call.SizeBytes == AtmosphericFrameUniforms.SizeInBytes));
        Assert.Equal(
            6,
            device.OfKind<GpuRecordedUniformBind>().Count(call =>
                call.Binding == GpuBindingModel.UniformPackPass
                && call.SizeBytes == AtmosphericPackPassUniforms.SizeInBytes));
        Assert.Equal(
            6,
            device.OfKind<GpuRecordedUniformBind>().Count(call =>
                call.Binding == GpuBindingModel.UniformPackSettings
                && call.SizeBytes == PackSettingsUniforms.SizeInBytes));
        GpuRecordedPushConstants[] pushes = device.OfKind<GpuRecordedPushConstants>().ToArray();
        Assert.All(pushes[..^1], call =>
        {
            Assert.True(call.Constants.TextureIndexA != uint.MaxValue);
            Assert.Equal(uint.MaxValue, BitConverter.SingleToUInt32Bits(call.Constants.ParamA));
            Assert.Equal(uint.MaxValue, BitConverter.SingleToUInt32Bits(call.Constants.ParamB));
        });
        Assert.NotEqual(uint.MaxValue, BitConverter.SingleToUInt32Bits(pushes[^1].Constants.ParamA));
        Assert.Equal(uint.MaxValue, BitConverter.SingleToUInt32Bits(pushes[^1].Constants.ParamB));
        Assert.NotEqual(uint.MaxValue, pushes[2].Constants.TextureIndexB);
        Assert.Equal(uint.MaxValue, BitConverter.SingleToUInt32Bits(pushes[2].Constants.ParamA));

        RenderPackRuntimeDiagnostics diagnostics = graph.CaptureDiagnostics();
        Assert.Equal(10, diagnostics.ImageCount);
        Assert.Equal(6, diagnostics.DrawCalls);
        Assert.Equal(0, diagnostics.ShadowCasterCount);
        Assert.Equal(0, diagnostics.CascadeDrawCount);
        Assert.Equal(0, diagnostics.CpuClassificationCalls);
        Assert.Equal(8, diagnostics.Passes.Count);
        Assert.Contains(diagnostics.Passes, pass =>
            pass.PassId == RenderPackPerformanceScopeNames.EnhancedWorldReceiver);
        Assert.Contains(diagnostics.Passes, pass =>
            pass.PassId == VolumetricShaftRenderer.TimerName
            && pass.DrawCalls == 0);
        Assert.True(
            diagnostics.RetainedGpuBytes
            >= DirectionalShadowQuality.For(DirectionalShadowPreset.Medium)
                .ApproximateDepthMapBytes);
    }

    [Fact]
    public void CurrentShadowRunsShaftsBeforeBloomAndFeedsBloomAndFilmicComposition()
    {
        var device = new RecordingGpuDevice();
        using var graph = Graph(device, "medium");
        IGpuRenderTarget world = graph.PrepareWorldTarget(1280, 720, 1);

        using IGpuFrame frame = device.BeginFrame();
        PublishCurrentShadow(graph, frame);
        device.Clear();
        RecordWorldPass(frame, world);
        AtmosphericFrameInputs inputs = Inputs(1280, 720);
        graph.RenderPostProcess(frame, in inputs);
        frame.End();

        Assert.Equal(
            [
                "test-world-hdr",
                "atmospheric-sun-occlusion",
                "atmospheric-sun-rays",
                VolumetricShaftRenderer.TimerName,
                "atmospheric-bloom-downsample",
                "atmospheric-bloom-blur-horizontal",
                "atmospheric-bloom-blur-vertical",
                "atmospheric-filmic",
            ],
            device.OfKind<GpuRecordedPassBegin>().Select(call => call.Name));
        RenderPassSemantic[] declaredOrder = graph.Descriptor.Passes
            .Where(pass => pass.Hook is RenderPassHook.AtmosphereBeforeToneMap
                or RenderPassHook.ToneMap)
            .Select(pass => pass.Semantic)
            .ToArray();
        RenderPassSemantic[] executedOrder = device.OfKind<GpuRecordedPassBegin>()
            .Skip(1)
            .Select(call => call.Name switch
            {
                "atmospheric-sun-occlusion" => RenderPassSemantic.SunOcclusion,
                "atmospheric-sun-rays" => RenderPassSemantic.SunRays,
                VolumetricShaftRenderer.TimerName => RenderPassSemantic.VolumetricShafts,
                "atmospheric-bloom-downsample" => RenderPassSemantic.BloomDownsample,
                "atmospheric-bloom-blur-horizontal" => RenderPassSemantic.BloomBlurHorizontal,
                "atmospheric-bloom-blur-vertical" => RenderPassSemantic.BloomBlurVertical,
                "atmospheric-filmic" => RenderPassSemantic.FilmicComposite,
                _ => throw new InvalidOperationException($"Unexpected atmospheric pass '{call.Name}'."),
            })
            .ToArray();
        Assert.Equal(declaredOrder, executedOrder);
        RenderPassDeclaration bloom = Assert.Single(
            graph.Descriptor.Passes,
            value => value.Semantic == RenderPassSemantic.BloomDownsample);
        RenderResourceSemantic[] bloomReads = bloom.ResourceReads
            .Select(id => Assert.Single(
                graph.Descriptor.Resources,
                resource => string.Equals(resource.Id, id, StringComparison.OrdinalIgnoreCase)).Semantic)
            .ToArray();
        Assert.Equal(
            [RenderResourceSemantic.SunRays, RenderResourceSemantic.VolumetricShafts],
            bloomReads);
        GpuRecordedPushConstants[] pushes = device
            .OfKind<GpuRecordedPushConstants>()
            .ToArray();
        Assert.Equal(7, pushes.Length);
        Assert.NotEqual(uint.MaxValue, pushes[2].Constants.TextureIndexA);
        Assert.Equal(uint.MaxValue, pushes[2].Constants.TextureIndexB);
        Assert.Equal(uint.MaxValue, BitConverter.SingleToUInt32Bits(pushes[2].Constants.ParamA));
        Assert.Equal(uint.MaxValue, BitConverter.SingleToUInt32Bits(pushes[2].Constants.ParamB));
        Assert.NotEqual(uint.MaxValue, pushes[3].Constants.TextureIndexA);
        Assert.NotEqual(uint.MaxValue, pushes[3].Constants.TextureIndexB);
        Assert.NotEqual(uint.MaxValue, BitConverter.SingleToUInt32Bits(pushes[3].Constants.ParamA));
        Assert.Equal(uint.MaxValue, BitConverter.SingleToUInt32Bits(pushes[3].Constants.ParamB));
        Assert.Equal(
            pushes[3].Constants.TextureIndexB,
            BitConverter.SingleToUInt32Bits(pushes[^1].Constants.ParamA));
        Assert.Equal(
            BitConverter.SingleToUInt32Bits(pushes[3].Constants.ParamA),
            BitConverter.SingleToUInt32Bits(pushes[^1].Constants.ParamB));

        RenderPackRuntimeDiagnostics diagnostics = graph.CaptureDiagnostics();
        Assert.Contains(diagnostics.Passes, pass =>
            pass.PassId == VolumetricShaftRenderer.TimerName
            && pass.DrawCalls == 1);
        Assert.Equal(7, diagnostics.DrawCalls);
        Assert.Equal(8, diagnostics.ImageCount);
    }

    [Fact]
    public void NeutralSettingsReachShaderBlocksWithoutHiddenResidualEffects()
    {
        var device = new RecordingGpuDevice();
        using var graph = Graph(
            device,
            "medium",
            AtmosphericPostProcessSettings.Neutral);
        IGpuRenderTarget world = graph.PrepareWorldTarget(800, 600, 1);
        device.Clear();

        using IGpuFrame frame = device.BeginFrame();
        RecordWorldPass(frame, world);
        AtmosphericFrameInputs inputs = Inputs(800, 600);
        graph.RenderPostProcess(frame, in inputs);
        frame.End();

        GpuRecordedUniformBind[] passBlocks = device
            .OfKind<GpuRecordedUniformBind>()
            .Where(call => call.Binding == GpuBindingModel.UniformPackPass)
            .ToArray();
        AtmosphericPackPassUniforms bloom = ReadPass(device, passBlocks[2]);
        AtmosphericPackPassUniforms filmic = ReadPass(device, passBlocks[^1]);
        Assert.Equal(0f, bloom.Params0.X);
        Assert.Equal(new Vector4(1f, 1f, 1f, 0f), filmic.Params0);
        Assert.Equal(Vector4.Zero, filmic.Params1);

        GpuRecordedUniformBind frameBlock = device
            .OfKind<GpuRecordedUniformBind>()
            .First(call => call.Binding == GpuBindingModel.UniformAtmosphericFrame);
        AtmosphericFrameUniforms atmospheric = MemoryMarshal.Read<AtmosphericFrameUniforms>(
            device.RingBytes.Slice((int)frameBlock.OffsetBytes, AtmosphericFrameUniforms.SizeInBytes));
        Assert.Equal(0f, atmospheric.SunScreen.Z);
    }

    [Fact]
    public void PerformanceSourceSumsOnlyResolvedPackPassTimersWithoutAllocatingDiagnostics()
    {
        var device = new RecordingGpuDevice();
        using var graph = Graph(device, "medium");
        IGpuRenderTarget world = graph.PrepareWorldTarget(800, 600, 1);
        using IGpuFrame frame = device.BeginFrame();
        RecordWorldPass(frame, world);
        AtmosphericFrameInputs inputs = Inputs(800, 600);
        graph.RenderPostProcess(frame, in inputs);
        frame.End();
        string[] names =
        [
            RenderPackPerformanceScopeNames.EnhancedWorldReceiver,
            "atmospheric-sun-occlusion",
            "atmospheric-sun-rays",
            "atmospheric-bloom-downsample",
            "atmospheric-bloom-blur-horizontal",
            "atmospheric-bloom-blur-vertical",
            "atmospheric-filmic",
        ];
        for (int i = 0; i < names.Length; i++)
            device.RecordingTimers.SetResolved(names[i], i + 1);

        RenderPackRuntimePerformanceMetrics metrics =
            graph.CapturePerformanceMetrics();

        Assert.Equal(1, metrics.ResourceGeneration);
        Assert.True(metrics.HasResolvedGpuMeasurement);
        Assert.Equal(28, metrics.InclusiveResolvedGpuMilliseconds);
        Assert.True(metrics.RetainedGpuBytes > 0);
        Assert.Equal(0, metrics.TransientGpuBytes);
        Assert.False(graph.CapturePerformanceMetrics().HasResolvedGpuMeasurement);

        graph.PrepareWorldTarget(1024, 768, 4);
        Assert.Equal(2, graph.CapturePerformanceMetrics().ResourceGeneration);
    }

    [Fact]
    public void ResizePublishesOneCompleteReplacementAndRetiresTheOldSet()
    {
        var device = new RecordingGpuDevice();
        var graph = Graph(device, "medium");
        int baselineSlots = device.LiveTextureSlotCount;

        IGpuRenderTarget first = graph.PrepareWorldTarget(1280, 720, 4);
        Assert.Equal(1, graph.ResourceGeneration);
        Assert.Same(first, graph.PrepareWorldTarget(1280, 720, 4));
        Assert.Equal(1, graph.ResourceGeneration);
        Assert.Equal(baselineSlots + 7, device.LiveTextureSlotCount);
        RecordingGpuRenderTarget[] firstSet = device.CreatedRenderTargets.ToArray();

        IGpuRenderTarget second = graph.PrepareWorldTarget(1920, 1080, 1);

        Assert.NotSame(first, second);
        Assert.Equal(2, graph.ResourceGeneration);
        Assert.All(firstSet, target => Assert.True(target.IsDisposed));
        Assert.Equal(baselineSlots + 7, device.LiveTextureSlotCount);
        graph.Dispose();
        Assert.Equal(baselineSlots - 1, device.LiveTextureSlotCount);
        Assert.Empty(device.PipelineFormatLeases);
    }

    [Theory]
    [InlineData("low", 320, 180)]
    [InlineData("medium", 640, 360)]
    [InlineData("high", 640, 360)]
    public void PresetDeclaredRayScaleControlsMaskAndRayTargets(
        string presetId,
        int expectedWidth,
        int expectedHeight)
    {
        var device = new RecordingGpuDevice();
        using var graph = Graph(device, presetId);

        graph.PrepareWorldTarget(1280, 720, 1);

        RecordingGpuRenderTarget mask = Assert.Single(device.CreatedRenderTargets, target =>
            target.Description.Name == "atmospheric-sun-mask");
        RecordingGpuRenderTarget rays = Assert.Single(device.CreatedRenderTargets, target =>
            target.Description.Name == "atmospheric-sun-rays");
        Assert.Equal(expectedWidth, mask.Description.Width);
        Assert.Equal(expectedHeight, mask.Description.Height);
        Assert.Equal(expectedWidth, rays.Description.Width);
        Assert.Equal(expectedHeight, rays.Description.Height);
    }

    [Fact]
    public void PartialTargetAllocationFailureRollsBackAndCanBuildFreshCandidate()
    {
        var device = new RecordingGpuDevice();
        using var graph = Graph(device, "medium");
        int baselineSlots = device.LiveTextureSlotCount;
        int allocation = 0;
        device.RenderTargetFailure = _ => ++allocation == 3
            ? new InvalidOperationException("injected target failure")
            : null;

        InvalidOperationException failure = Assert.Throws<InvalidOperationException>(
            () => graph.PrepareWorldTarget(1024, 768, 4));

        Assert.Equal("injected target failure", failure.Message);
        Assert.Equal(0, graph.ResourceGeneration);
        Assert.Equal(baselineSlots, device.LiveTextureSlotCount);
        Assert.All(device.CreatedRenderTargets, target => Assert.True(target.IsDisposed));

        device.RenderTargetFailure = null;
        IGpuRenderTarget recovered = graph.PrepareWorldTarget(1024, 768, 4);
        Assert.Equal(GpuTextureFormat.Rgba16FloatRenderTarget, recovered.Description.ColorFormat);
        Assert.Equal(1, graph.ResourceGeneration);
        Assert.Equal(baselineSlots + 7, device.LiveTextureSlotCount);
    }

    [Fact]
    public void DescriptorPassAssetsAndPresetOverridesDriveTheRuntime()
    {
        var device = new RecordingGpuDevice();
        using var graph = Graph(device, "low");

        Assert.Equal(0.4f, graph.Settings.SunRayStrength);
        Assert.Equal(
            [
                "acdream.atmospheric:sun-occlusion",
                "acdream.atmospheric:sun-rays",
                "acdream.atmospheric:bloom-downsample",
                "acdream.atmospheric:bloom-blur-horizontal",
                "acdream.atmospheric:filmic-composite",
                "acdream.atmospheric:terrain-shadow-caster",
                "acdream.atmospheric:world-shadow-opaque",
                "acdream.atmospheric:world-shadow-cutout",
                "acdream.atmospheric:terrain-shadow-caster-multiview",
                "acdream.atmospheric:world-shadow-opaque-multiview",
                "acdream.atmospheric:world-shadow-cutout-multiview",
                "acdream.atmospheric:volumetric-shafts",
            ],
            device.CreatedPipelines.Select(pipeline => pipeline.Description.Shaders.Name));
        Assert.All(
            device.CreatedPipelines,
            pipeline => Assert.True(pipeline.Description.Shaders.HasEmbeddedSpirv));
        Assert.Equal(1, device.PipelineFormatLeases[GpuTextureFormat.Rgba16FloatRenderTarget]);
    }

    [Fact]
    public void BuiltInSampleCountDeclarationsMatchTheExecutorExactly()
    {
        RenderPackDescriptor descriptor = BuiltInAtmosphericRenderPack.Descriptor;
        RenderSettingDeclaration pcf = Assert.Single(descriptor.Settings, value =>
            value.Semantic == RenderSettingSemantic.DirectionalShadowPcfTaps);
        RenderSettingDeclaration volumetric = Assert.Single(descriptor.Settings, value =>
            value.Semantic == RenderSettingSemantic.VolumetricRayMarchSteps);

        Assert.Equal(RenderSettingKind.Choice, pcf.Kind);
        Assert.Equal(["1", "9", "25"], pcf.Choices);
        Assert.Equal("9", pcf.DefaultValue);
        Assert.Equal(RenderSettingKind.Integer, volumetric.Kind);
        Assert.Equal(8, volumetric.Minimum);
        Assert.Equal(64, volumetric.Maximum);
        Assert.Equal(8, volumetric.Step);
    }

    [Fact]
    public void ShadowFilterRejectsAnUndeclaredIntermediateSampleCount()
    {
        var device = new RecordingGpuDevice();
        RenderPackDescriptor descriptor = BuiltInAtmosphericRenderPack.Descriptor;
        RenderQualityPreset preset = Assert.Single(descriptor.QualityPresets, value =>
            value.Semantic == RenderQualitySemantic.Medium);
        string settingId = Assert.Single(descriptor.Settings, value =>
            value.Semantic == RenderSettingSemantic.DirectionalShadowPcfTaps).Id;

        NotSupportedException error = Assert.Throws<NotSupportedException>(() =>
            new AtmosphericPostProcessGraph(
                device,
                descriptor,
                BuiltInAssets(),
                preset,
                userSettingOverrides: new Dictionary<string, string>
                {
                    [settingId] = "3",
                }));

        Assert.Contains("exactly 1, 9, or 25", error.Message, StringComparison.Ordinal);
    }

    [Fact]
    public void SemanticPresetResourcesAndSettingsDriveShadowAndVolumetricQuality()
    {
        var device = new RecordingGpuDevice();
        RenderPackDescriptor source = BuiltInAtmosphericRenderPack.Descriptor;
        RenderResourceDeclaration shadowResource = Assert.Single(
            source.Resources,
            value => value.Semantic == RenderResourceSemantic.DirectionalShadowDepth);
        RenderResourceDeclaration volumetricResource = Assert.Single(
            source.Resources,
            value => value.Semantic == RenderResourceSemantic.VolumetricShafts);
        RenderQualityPreset original = Assert.Single(
            source.QualityPresets,
            value => value.Semantic == RenderQualitySemantic.Medium);
        RenderQualityPreset preset = original with
        {
            ResourceOverrides = original.ResourceOverrides.Select(value =>
                string.Equals(value.ResourceId, shadowResource.Id, StringComparison.OrdinalIgnoreCase)
                    ? value with
                    {
                        Extent = new RenderExtentDeclaration(
                            RenderExtentMode.AbsolutePixels,
                            768,
                            768,
                            Layers: 2),
                        EstimatedResidentBytes = 2L * 768 * 768 * sizeof(float),
                    }
                    : string.Equals(value.ResourceId, volumetricResource.Id,
                        StringComparison.OrdinalIgnoreCase)
                        ? value with
                        {
                            Extent = new RenderExtentDeclaration(
                                RenderExtentMode.RelativeToMainWorld,
                                0.375,
                                0.375),
                        }
                        : value).ToArray(),
        };
        string SettingId(RenderSettingSemantic semantic) => Assert.Single(
            source.Settings,
            value => value.Semantic == semantic).Id;
        var overrides = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase)
        {
            [SettingId(RenderSettingSemantic.DirectionalShadowReachMetres)] = "96",
            [SettingId(RenderSettingSemantic.DirectionalShadowPcfTaps)] = "25",
            [SettingId(RenderSettingSemantic.VolumetricRayMarchSteps)] = "64",
        };

        using var graph = new AtmosphericPostProcessGraph(
            device,
            source,
            BuiltInAssets(),
            preset,
            userSettingOverrides: overrides);
        var shadows = Assert.IsType<DirectionalSunShadowRenderer>(
            graph.DirectionalShadowReceivers);
        Assert.Equal(2, shadows.Quality.CascadeCount);
        Assert.Equal(768, shadows.Quality.MapResolution);
        Assert.Equal(96f, shadows.Quality.MaximumReachMeters);
        Assert.Equal(2, shadows.Quality.PcfRadiusTexels);
        Assert.Equal(64, graph.VolumetricQuality?.RayMarchSteps);
        Assert.Equal(0.375f, graph.VolumetricQuality?.ResolutionScale);

        graph.PrepareWorldTarget(800, 600, 1);
        RecordingGpuRenderTarget volumetric = Assert.Single(
            device.CreatedRenderTargets,
            value => value.Description.Name == "atmospheric-volumetric");
        Assert.Equal(300, volumetric.Description.Width);
        Assert.Equal(225, volumetric.Description.Height);
    }

    [Fact]
    public void AuthoredElevationDayGroupAndVisibilityGateSunEffects()
    {
        var device = new RecordingGpuDevice();
        using var graph = Graph(device, "medium");

        AtmosphericFrameInputs dawn = Inputs(1280, 720, elevation: 4f, activeDayGroup: 0);
        AtmosphericFrameInputs dusk = dawn with { ActiveDayGroup = 1 };
        AtmosphericFrameInputs noon = dawn with { SunElevationDegrees = 55f };
        AtmosphericFrameInputs behindCamera = dawn with { SunIsOnScreen = false };
        AtmosphericFrameInputs overcast = dawn with
        {
            Weather = WeatherKind.Overcast,
            WeatherIntensity = 1f,
        };
        AtmosphericFrameInputs indoor = dawn with { IsOutdoor = false };

        Assert.Equal(1f, graph.EvaluateSunPolicy(in dawn), 3);
        Assert.Equal(0.35f, graph.EvaluateSunPolicy(in dusk), 3);
        Assert.Equal(0f, graph.EvaluateSunPolicy(in noon));
        Assert.Equal(0f, graph.EvaluateSunPolicy(in behindCamera));
        Assert.Equal(0.18f, graph.EvaluateSunPolicy(in overcast), 3);
        Assert.Equal(0f, graph.EvaluateSunPolicy(in indoor));
    }

    [Fact]
    public void VolumetricPipelineFailureRollsBackGraphCandidate()
    {
        var device = new RecordingGpuDevice();
        int baselineSlots = device.LiveTextureSlotCount;
        device.PipelineFailure = description => description.Name.Contains(
            "volumetric-shafts",
            StringComparison.Ordinal)
                ? new InvalidOperationException("volumetric pipeline failed")
                : null;

        InvalidOperationException failure = Assert.Throws<InvalidOperationException>(() =>
            Graph(device, "medium"));

        Assert.Equal("volumetric pipeline failed", failure.Message);
        Assert.Equal(baselineSlots, device.LiveTextureSlotCount);
        Assert.Empty(device.PipelineFormatLeases);
        Assert.All(device.CreatedPipelines, pipeline => Assert.True(pipeline.IsDisposed));
        Assert.All(device.CreatedDirectionalDepthTargets, target => Assert.True(target.IsDisposed));
    }

    [Fact]
    public void ExternalTierTwoPackCanRenameEveryOwnedIdAndShaderAsset()
    {
        var device = new RecordingGpuDevice();
        RenderPackDescriptor external = RenamedExternalTierTwoDescriptor();
        Assert.True(
            RenderPackValidator.ValidateDescriptor(
                external,
                RenderPackHostCapabilities.Conformance).Success);
        RenderQualityPreset preset = Assert.Single(
            external.QualityPresets,
            value => value.Semantic == RenderQualitySemantic.Medium);
        var factory = new AtmosphericRenderPackRuntimeFactory(device);

        using IRenderPackRuntime runtime = factory.Build(
            external,
            new RenamedShaderAssets(BuiltInAssets()),
            preset,
            RenderPackSettingOverrides.Empty);

        AtmosphericPostProcessGraph graph = Assert.IsType<AtmosphericPostProcessGraph>(runtime);
        _ = graph.PrepareWorldTarget(1280, 720, 1);
        Assert.All(device.CreatedPipelines, pipeline =>
        {
            Assert.StartsWith("example.external-atmosphere:", pipeline.Description.Shaders.Name);
            Assert.True(pipeline.Description.Shaders.HasEmbeddedSpirv);
        });
        Assert.DoesNotContain(external.Passes, value =>
            BuiltInAtmosphericRenderPack.Descriptor.Passes.Any(original =>
                string.Equals(original.Id, value.Id, StringComparison.Ordinal)));
        Assert.All(external.Passes, value => Assert.StartsWith("external/", value.VertexShaderAsset));
        Assert.All(external.PipelineVariants, value =>
            Assert.StartsWith("external/", value.FragmentShaderAsset));
        Assert.Same(external, graph.Descriptor);
    }

    [Fact]
    public void ExternalNoOpPackNeedsNoRendererPrivateRuntime()
    {
        var device = new RecordingGpuDevice();
        RenderPackDescriptor descriptor = BuiltInAtmosphericRenderPack.Descriptor with
        {
            Id = "example.no-op",
            Passes = [],
            SceneReplays = [],
            PipelineVariants = [],
        };
        RenderQualityPreset preset = descriptor.QualityPresets[0];
        var factory = new AtmosphericRenderPackRuntimeFactory(device);

        using IRenderPackRuntime runtime = factory.Build(
            descriptor,
            new RejectingAssets(),
            preset,
            RenderPackSettingOverrides.Empty);

        Assert.IsType<NoOpRenderPackRuntime>(runtime);
        Assert.Empty(device.CreatedPipelines);
        Assert.Empty(device.PipelineFormatLeases);
    }

    [Fact]
    public void ExternalTierOneFullscreenGraphSchedulesArbitraryDeclaredPassIdsAndResource()
    {
        var device = new RecordingGpuDevice();
        RenderPackDescriptor descriptor = ExternalTierOneDescriptor();
        RenderQualityPreset preset = Assert.Single(descriptor.QualityPresets);
        var factory = new AtmosphericRenderPackRuntimeFactory(device);
        using IRenderPackRuntime runtime = factory.Build(
            descriptor,
            BuiltInAssets(),
            preset,
            RenderPackSettingOverrides.Empty);
        var graph = Assert.IsType<DeclaredFullscreenRenderPackGraph>(runtime);
        IGpuRenderTarget world = graph.PrepareWorldTarget(1000, 600, 1);
        device.Clear();

        using IGpuFrame frame = device.BeginFrame();
        RecordWorldPass(frame, world);
        AtmosphericFrameInputs inputs = Inputs(1000, 600);
        graph.RenderPostProcess(frame, in inputs);
        frame.End();

        Assert.Equal(
            ["test-world-hdr", "render-pack-example.generic-tier1-my-threshold",
                "render-pack-example.generic-tier1-my-output"],
            device.OfKind<GpuRecordedPassBegin>().Select(call => call.Name));
        Assert.All(device.CreatedPipelines, pipeline =>
            Assert.True(pipeline.Description.Shaders.HasEmbeddedSpirv));
        Assert.Contains(device.CreatedRenderTargets, target =>
            target.Description.Name.EndsWith("custom-half", StringComparison.Ordinal)
            && target.Description.Width == 500
            && target.Description.Height == 300);

        device.RecordingTimers.SetResolved(
            "render-pack-example.generic-tier1-my-threshold",
            1.25);
        device.RecordingTimers.SetResolved(
            "render-pack-example.generic-tier1-my-output",
            2.75);
        RenderPackRuntimePerformanceMetrics metrics = graph.CapturePerformanceMetrics();
        Assert.Equal(1, metrics.ResourceGeneration);
        Assert.True(metrics.HasResolvedGpuMeasurement);
        Assert.Equal(4, metrics.InclusiveResolvedGpuMilliseconds);
        Assert.True(metrics.RetainedGpuBytes > 0);
        Assert.False(graph.CapturePerformanceMetrics().HasResolvedGpuMeasurement);
    }

    [Fact]
    public void ExternalTierOnePolicyAndCompleteRuntimeDiagnosticsReachTheController()
    {
        var policy = new AtmospherePolicyDeclaration(
            [
                new SunElevationResponsePoint(-10, 0.2),
                new SunElevationResponsePoint(10, 0.8),
            ],
            [new ActiveDayGroupMultiplier(7, 0.4)]);
        RenderPackDescriptor descriptor = ExternalTierOneDescriptor(policy);
        var device = new RecordingGpuDevice();
        using var registry = new BufferedRenderPackRegistry();
        using IDisposable registration = registry.Register(descriptor, BuiltInAssets());
        using var controller = new RenderPackController(
            () => RenderPackCatalog.Build(
                registry.Snapshot(),
                RenderPackHostCapabilities.Conformance),
            new AtmosphericRenderPackRuntimeFactory(device),
            preparationScheduler: InlineRenderPackPreparationScheduler.Instance);
        controller.Request(new RenderPackSelectionSettings(
            descriptor.Id,
            descriptor.PackVersion.ToString(),
            "default"));

        RenderPackActivationSnapshot activation = controller.ApplyAtFrameBoundary(
            new RenderPackActivationExtent(1000, 600, 1));
        var graph = Assert.IsType<DeclaredFullscreenRenderPackGraph>(
            controller.ActiveRuntime);
        IGpuRenderTarget world = graph.PrepareWorldTarget(1000, 600, 1);
        device.Clear();

        using IGpuFrame frame = device.BeginFrame();
        RecordWorldPass(frame, world);
        AtmosphericFrameInputs inputs = Inputs(
            1000,
            600,
            elevation: 0f,
            activeDayGroup: 7);
        graph.RenderPostProcess(frame, in inputs);
        frame.End();

        GpuRecordedUniformBind frameBlock = device
            .OfKind<GpuRecordedUniformBind>()
            .First(call => call.Binding == GpuBindingModel.UniformAtmosphericFrame);
        AtmosphericFrameUniforms atmosphere = MemoryMarshal.Read<AtmosphericFrameUniforms>(
            device.RingBytes.Slice(
                (int)frameBlock.OffsetBytes,
                AtmosphericFrameUniforms.SizeInBytes));
        Assert.Equal(new Vector4(7f, 0.4f, 0.5f, 0f), atmosphere.Policy);
        Assert.Equal(0.2f, atmosphere.SunScreen.Z, 3);
        Assert.Equal(0.2f, atmosphere.SunColor.W, 3);

        device.RecordingTimers.SetResolved(
            "render-pack-example.generic-tier1-my-threshold",
            1.25);
        device.RecordingTimers.SetResolved(
            "render-pack-example.generic-tier1-my-output",
            2.75);
        RenderPackDiagnosticsSnapshot diagnostics = controller.CaptureDiagnostics();

        Assert.Equal(RenderPackActivationState.Active, activation.State);
        Assert.Equal("example.generic-tier1", diagnostics.PackId);
        Assert.Equal("default", diagnostics.EffectiveQuality);
        Assert.Equal(8_400_000L, diagnostics.RetainedGpuBytes);
        Assert.Equal(0L, diagnostics.TransientGpuBytes);
        Assert.Equal(3, diagnostics.ImageCount);
        Assert.Equal(0, diagnostics.BufferCount);
        Assert.Equal(2, diagnostics.DrawCalls);
        Assert.Equal(0, diagnostics.DispatchCalls);
        Assert.Equal(0, diagnostics.ShadowCasterCount);
        Assert.Equal(0, diagnostics.CascadeDrawCount);
        Assert.Equal(0, diagnostics.CpuClassificationCalls);
        Assert.Equal(0, diagnostics.SunElevationDegrees);
        Assert.Equal(7, diagnostics.ActiveDayGroup);
        Assert.Equal(WeatherKind.Clear.ToString(), diagnostics.Weather);
        Assert.Equal(0, diagnostics.WeatherIntensity);
        Assert.True(diagnostics.Outdoor);
        Assert.Equal(0, diagnostics.DirectionalShadowStrength);
        Assert.Collection(
            diagnostics.Passes,
            pass =>
            {
                Assert.Equal("my-threshold", pass.PassId);
                Assert.Equal(1.25, pass.GpuMilliseconds);
                Assert.Equal(1, pass.DrawCalls);
                Assert.Equal(0, pass.DispatchCalls);
            },
            pass =>
            {
                Assert.Equal("my-output", pass.PassId);
                Assert.Equal(2.75, pass.GpuMilliseconds);
                Assert.Equal(1, pass.DrawCalls);
                Assert.Equal(0, pass.DispatchCalls);
            });
        string formatted = RenderPackDiagnosticsFormatter.Format(diagnostics);
        Assert.Contains("resources=3i/0b", formatted, StringComparison.Ordinal);
        Assert.Contains("worldTransforms=0used", formatted, StringComparison.Ordinal);
        Assert.Contains("my-threshold:1.250ms/1d/0c", formatted, StringComparison.Ordinal);
        Assert.Contains("atmosphere=0.00deg/day7/Clear:0.000/outdoor=True", formatted,
            StringComparison.Ordinal);
        RenderPackRuntimePerformanceMetrics performance = graph.CapturePerformanceMetrics();
        Assert.True(performance.HasResolvedGpuMeasurement);
        Assert.Equal(4, performance.InclusiveResolvedGpuMilliseconds);
    }

    [Fact]
    public void ExternalShadowsOnlyTierTwoValidatesAndExecutesDeclaredMovingCelestialGraph()
    {
        var policy = new AtmospherePolicyDeclaration(
            [
                new SunElevationResponsePoint(-90, 1),
                new SunElevationResponsePoint(90, 1),
            ],
            [new ActiveDayGroupMultiplier(7, 0.4)])
        {
            DirectionalShadowLightElevationResponse =
            [
                new SunElevationResponsePoint(-90, 0),
                new SunElevationResponsePoint(0, 0),
                new SunElevationResponsePoint(10, 0.1),
                new SunElevationResponsePoint(20, 0.3),
                new SunElevationResponsePoint(90, 0.3),
            ],
            VolumetricShaftSunElevationResponse =
            [
                new SunElevationResponsePoint(-90, 0),
                new SunElevationResponsePoint(10, 0.8),
                new SunElevationResponsePoint(20, 0.4),
                new SunElevationResponsePoint(90, 0),
            ],
        };
        RenderPackDescriptor descriptor = ExternalShadowsOnlyTierTwoDescriptor(policy);
        RenderPackValidationResult validation = RenderPackValidator.ValidateDescriptor(
            descriptor,
            RenderPackHostCapabilities.Conformance);
        Assert.True(validation.Success, validation.Reason);
        RenderQualityPreset preset = Assert.Single(descriptor.QualityPresets);
        var device = new RecordingGpuDevice();
        var factory = new AtmosphericRenderPackRuntimeFactory(device);

        using IRenderPackRuntime runtime = factory.Build(
            descriptor,
            BuiltInAssets(),
            preset,
            RenderPackSettingOverrides.Empty);
        var graph = Assert.IsType<DeclaredDirectionalShadowRenderPackGraph>(runtime);
        Assert.False(Assert.IsType<DirectionalSunShadowRenderer>(
            graph.DirectionalShadowReceivers).MultiviewCascadesEnabled);
        IGpuRenderTarget world = graph.PrepareWorldTarget(1000, 600, 1);
        device.Clear();

        using IGpuFrame frame = device.BeginFrame();
        PublishCurrentShadow(graph.DirectionalShadowReceivers, frame);
        RecordWorldPass(frame, world);
        AtmosphericFrameInputs inputs = Inputs(
            1000,
            600,
            elevation: 15f,
            activeDayGroup: 7);
        graph.RenderPostProcess(frame, in inputs);
        frame.End();

        GpuRecordedUniformBind frameBlock = device
            .OfKind<GpuRecordedUniformBind>()
            .First(call => call.Binding == GpuBindingModel.UniformAtmosphericFrame);
        AtmosphericFrameUniforms atmosphere = MemoryMarshal.Read<AtmosphericFrameUniforms>(
            device.RingBytes.Slice(
                (int)frameBlock.OffsetBytes,
                AtmosphericFrameUniforms.SizeInBytes));
        Assert.Equal(7f, atmosphere.Policy.X);
        Assert.Equal(0.4f, atmosphere.Policy.Y, 3);
        Assert.Equal(0.20117f, atmosphere.Policy.Z, 5);
        Assert.Equal(0.6f, atmosphere.Policy.W, 3);
        Assert.Equal(0.4f, atmosphere.SunScreen.Z, 3);
        Assert.Equal(0.4f, atmosphere.SunColor.W, 3);
        Assert.Single(descriptor.Passes, value =>
            value.Semantic == RenderPassSemantic.DirectionalShadowDepth);
        Assert.DoesNotContain(descriptor.Passes, value => value.Semantic is
            RenderPassSemantic.BloomDownsample
                or RenderPassSemantic.BloomBlurHorizontal
                or RenderPassSemantic.BloomBlurVertical
                or RenderPassSemantic.SunOcclusion
                or RenderPassSemantic.SunRays
                or RenderPassSemantic.VolumetricShafts
                or RenderPassSemantic.FilmicComposite);
        Assert.Contains(device.OfKind<GpuRecordedPassBegin>(), value =>
            value.Name == "render-pack-example.shadows-only-output-copy");
    }

    [Fact]
    public void ExternalShadowsOnlyTierTwoFailsSafeWithoutCasterReplayOrDeclaredCurve()
    {
        AtmospherePolicyDeclaration policy = BuiltInAtmosphericRenderPack.Descriptor
            .AtmospherePolicy!;
        RenderPackDescriptor descriptor = ExternalShadowsOnlyTierTwoDescriptor(policy);

        RenderPackValidationResult missingReplay = RenderPackValidator.ValidateDescriptor(
            descriptor with { SceneReplays = [] },
            RenderPackHostCapabilities.Conformance);
        RenderPackValidationResult missingCurve = RenderPackValidator.ValidateDescriptor(
            descriptor with
            {
                AtmospherePolicy = policy with
                {
                    DirectionalShadowLightElevationResponse = [],
                },
            },
            RenderPackHostCapabilities.Conformance);

        Assert.False(missingReplay.Success);
        Assert.Contains("exactly one outdoor directional-shadow replay", missingReplay.Reason,
            StringComparison.Ordinal);
        Assert.False(missingCurve.Success);
        Assert.Contains("directional-shadow light-elevation response curve", missingCurve.Reason,
            StringComparison.Ordinal);
    }

    [Fact]
    public void ExternalDirectionalShadowCurveMustRemainZeroAtAndBelowAuthoredHorizon()
    {
        AtmospherePolicyDeclaration policy = BuiltInAtmosphericRenderPack.Descriptor
            .AtmospherePolicy!;
        RenderPackDescriptor descriptor = ExternalShadowsOnlyTierTwoDescriptor(policy);
        Assert.True(RenderPackValidator.ValidateDescriptor(
            descriptor,
            RenderPackHostCapabilities.Conformance).Success);

        IReadOnlyList<SunElevationResponsePoint>[] invalidCurves =
        [
            [
                new SunElevationResponsePoint(-90, 0.1),
                new SunElevationResponsePoint(0, 0),
                new SunElevationResponsePoint(90, 1),
            ],
            [
                new SunElevationResponsePoint(-90, 0),
                new SunElevationResponsePoint(90, 1),
            ],
            [
                new SunElevationResponsePoint(0, 0.1),
                new SunElevationResponsePoint(90, 1),
            ],
        ];

        foreach (IReadOnlyList<SunElevationResponsePoint> invalidCurve in invalidCurves)
        {
            RenderPackValidationResult result = RenderPackValidator.ValidateDescriptor(
                descriptor with
                {
                    AtmospherePolicy = policy with
                    {
                        DirectionalShadowLightElevationResponse = invalidCurve,
                    },
                },
                RenderPackHostCapabilities.Conformance);

            Assert.False(result.Success);
            Assert.Contains("zero at and below the 0-degree authored horizon", result.Reason,
                StringComparison.Ordinal);
        }

        RenderPackValidationResult clampedZero = RenderPackValidator.ValidateDescriptor(
            descriptor with
            {
                AtmospherePolicy = policy with
                {
                    DirectionalShadowLightElevationResponse =
                    [
                        new SunElevationResponsePoint(1, 0),
                        new SunElevationResponsePoint(12, 1),
                        new SunElevationResponsePoint(90, 1),
                    ],
                },
            },
            RenderPackHostCapabilities.Conformance);
        Assert.True(clampedZero.Success, clampedZero.Reason);
    }

    [Fact]
    public void BuiltInShadowStrengthUsesTheDescriptorCurveExactlyOnce()
    {
        RenderPackDescriptor source = BuiltInAtmosphericRenderPack.Descriptor;
        RenderQualityPreset medium = Assert.Single(source.QualityPresets, value =>
            value.Semantic == RenderQualitySemantic.Medium);
        using var baseline = new AtmosphericPostProcessGraph(
            new RecordingGpuDevice(),
            source,
            BuiltInAssets(),
            medium);
        const float elevation = 6f;
        float expectedLegacyElevation =
            (MathF.Sin(elevation * MathF.PI / 180f) - MathF.Sin(MathF.PI / 180f))
            / (MathF.Sin(12f * MathF.PI / 180f) - MathF.Sin(MathF.PI / 180f));
        Assert.Equal(
            expectedLegacyElevation * 0.72f,
            baseline.EvaluateDirectionalShadowStrength(elevation, activeDayGroup: 0),
            5);

        RenderPackDescriptor changed = source with
        {
            AtmospherePolicy = source.AtmospherePolicy! with
            {
                DirectionalShadowLightElevationResponse =
                [
                    new SunElevationResponsePoint(-90, 0.25),
                    new SunElevationResponsePoint(90, 0.25),
                ],
            },
        };
        using var declared = new AtmosphericPostProcessGraph(
            new RecordingGpuDevice(),
            changed,
            BuiltInAssets(),
            medium);

        Assert.Equal(
            0.25f * 0.72f,
            declared.EvaluateDirectionalShadowStrength(elevation, activeDayGroup: 0),
            5);
    }

    [Fact]
    public void FilmicAndBloomDownsampleShadersKeepTheirColourSpaceConversions()
    {
        string shaderRoot = Path.Combine(
            RepositoryRoot(),
            "src",
            "AcDream.App",
            "Rendering",
            "Shaders");
        string common = File.ReadAllText(Path.Combine(shaderRoot, "atmospheric_common.glsl"));
        string filmic = File.ReadAllText(Path.Combine(shaderRoot, "atmospheric_filmic.frag"));
        string downsample = File.ReadAllText(
            Path.Combine(shaderRoot, "atmospheric_bloom_downsample.frag"));

        int encodeInMainOutput = CountOccurrences(
            filmic[filmic.IndexOf("void main()", StringComparison.Ordinal)..],
            "acdreamEncodeDisplay(");
        Assert.Equal(1, encodeInMainOutput);
        Assert.Contains(
            "oColor = vec4(acdreamEncodeDisplay(clamp(color, 0.0, 1.0)), 1.0);",
            filmic,
            StringComparison.Ordinal);

        // Three in lowFusedScene (A/B/C) plus three in main's non-fused
        // branch (A/C/D — sampleBloom's B is already linear).
        Assert.Equal(6, CountOccurrences(filmic, "acdreamDecodeDisplay("));

        int decodesInDownsample = CountOccurrences(downsample, "acdreamDecodeDisplay(");
        Assert.True(
            decodesInDownsample >= 3,
            $"expected at least 3 acdreamDecodeDisplay( calls in atmospheric_bloom_downsample.frag, found {decodesInDownsample}");

        string gamma = AtmosphericColorPipeline.DisplayGamma.ToString(CultureInfo.InvariantCulture);
        Assert.Contains($"vec3({gamma})", common, StringComparison.Ordinal);
        Assert.Contains($"vec3(1.0 / {gamma})", common, StringComparison.Ordinal);
        string pivot = AtmosphericColorPipeline.LinearMidGrey.ToString(CultureInfo.InvariantCulture);
        Assert.Contains($"const float LinearMidGrey = {pivot}", filmic, StringComparison.Ordinal);

        Assert.Equal(0.73f, AtmosphericPostProcessGraph.BloomKneeLinear);
        Assert.Equal(1f, AtmosphericPostProcessGraph.BloomThresholdLinear);
    }


    [Fact]
    public void ReceiverVertexShadersAlwaysUseTheExactUnnormalizedPlainLightDirection()
    {
        string shaderRoot = Path.Combine(
            RepositoryRoot(),
            "src",
            "AcDream.App",
            "Rendering",
            "Shaders");
        string meshAtmospheric = StripLineComments(
            File.ReadAllText(Path.Combine(shaderRoot, "mesh_atmospheric.vert")));
        string meshModern = StripLineComments(
            File.ReadAllText(Path.Combine(shaderRoot, "mesh_modern.vert")));
        string terrainAtmospheric = StripLineComments(
            File.ReadAllText(Path.Combine(shaderRoot, "terrain_atmospheric.vert")));
        string terrainModern = StripLineComments(
            File.ReadAllText(Path.Combine(shaderRoot, "terrain_modern.vert")));

        Assert.DoesNotContain("shadowGatedOff", meshAtmospheric, StringComparison.Ordinal);
        Assert.DoesNotContain("shadowGatedOff", terrainAtmospheric, StringComparison.Ordinal);
        Assert.DoesNotContain("uShadowTextureAndFlags", meshAtmospheric, StringComparison.Ordinal);
        Assert.DoesNotContain("uShadowTextureAndFlags", terrainAtmospheric, StringComparison.Ordinal);
        Assert.DoesNotContain("uShadowLightDirectionAndSource", meshAtmospheric, StringComparison.Ordinal);
        Assert.DoesNotContain("uShadowLightDirectionAndSource", terrainAtmospheric, StringComparison.Ordinal);

        const string meshPlainDirection = "-uLights[i].dirAndRange.xyz";
        Assert.Contains(meshPlainDirection, meshModern, StringComparison.Ordinal);
        Assert.Contains(meshPlainDirection, meshAtmospheric, StringComparison.Ordinal);

        // terrain_modern.vert reads the same uniform into `sunDir` and then
        // negates it; the atmospheric receiver spells that identical value
        // inline so its split directional varying remains unchanged.
        const string terrainLightUniform = "uLights[0].dirAndRange.xyz";
        Assert.Contains(terrainLightUniform, terrainModern, StringComparison.Ordinal);
        Assert.Contains(
            "-" + terrainLightUniform,
            terrainAtmospheric,
            StringComparison.Ordinal);

        var meshDirectionShape = new Regex(
            @"vec3\s+Ldir\s*=\s*-uLights\[i\]\.dirAndRange\.xyz\s*;\s*float\s+ndl\s*=\s*max\(0\.0,\s*dot\(N,\s*Ldir\)\)",
            RegexOptions.Singleline);
        Assert.True(
            meshDirectionShape.IsMatch(meshAtmospheric),
            "mesh_atmospheric.vert must feed the exact unnormalized -uLights[i] direction directly to N dot L.");

        var terrainDirectionShape = new Regex(
            @"vec3\s+surfaceToLight\s*=\s*-uLights\[0\]\.dirAndRange\.xyz\s*;\s*vec3\s+sunCol[\s\S]*?float\s+L\s*=\s*max\(dot\(vWorldNormal,\s*surfaceToLight\),\s*MIN_FACTOR\)",
            RegexOptions.Singleline);
        Assert.True(
            terrainDirectionShape.IsMatch(terrainAtmospheric),
            "terrain_atmospheric.vert must feed the exact unnormalized -uLights[0] direction directly to N dot L.");
    }

    private static string StripLineComments(string glsl)
    {
        string[] lines = glsl.Split('\n');
        for (int i = 0; i < lines.Length; i++)
        {
            int index = lines[i].IndexOf("//", StringComparison.Ordinal);
            if (index >= 0)
                lines[i] = lines[i][..index];
        }
        return string.Join('\n', lines);
    }
    [Fact]
    public void FirstAdvanceSnapsExactlyToTheWeatherTargetInsteadOfEasingFromZero()
    {
        var device = new RecordingGpuDevice();
        using var graph = Graph(device, "medium", windClockSecondsOverride: 12f);
        graph.PrepareWorldTarget(640, 480, 1);
        AtmosphericFrameInputs outdoors = Inputs(640, 480) with { Weather = WeatherKind.Storm };

        AtmosphericFrameUniforms atmospheric = RenderAndReadFrameBlock(device, graph, outdoors);

        // Storm: mean 1.00, gust 0.75 (FoliageWindByWeather's built-in
        // row), times the declared default wind-strength (1.0x) — exact,
        // not a fraction of the way there.
        Assert.Equal(1.00f, atmospheric.ClockWind.Y, 5);
        Assert.Equal(0.75f, atmospheric.ClockWind.Z, 5);
    }

    [Fact]
    public void SecondAdvanceStillEasesTowardTheNewTargetFromTheFirstAdvancesSnappedValue()
    {
        var device = new RecordingGpuDevice();
        using var graph = Graph(device, "medium", windClockSecondsOverride: 0f);
        graph.PrepareWorldTarget(640, 480, 1);
        AtmosphericFrameInputs clearInputs = Inputs(640, 480) with { Weather = WeatherKind.Clear };

        AtmosphericFrameUniforms first = RenderAndReadFrameBlock(device, graph, clearInputs);
        Assert.Equal(0.25f, first.ClockWind.Y, 5);
        Assert.Equal(0.15f, first.ClockWind.Z, 5);

        graph.SetWindClockSecondsOverrideForTesting(1f);
        AtmosphericFrameInputs stormInputs = Inputs(640, 480) with { Weather = WeatherKind.Storm };
        AtmosphericFrameUniforms second = RenderAndReadFrameBlock(device, graph, stormInputs);

        const float rate = 1f / 10f; // deltaSeconds(1) / TransitionSeconds(10)
        float expectedMean = 0.25f + ((1.00f - 0.25f) * rate);
        float expectedGust = 0.15f + ((0.75f - 0.15f) * rate);
        Assert.Equal(expectedMean, second.ClockWind.Y, 5);
        Assert.Equal(expectedGust, second.ClockWind.Z, 5);
    }

    [Fact]
    public void IndoorGatesWindOutputToExactZeroRegardlessOfSmoothedState()
    {
        var device = new RecordingGpuDevice();
        using var graph = Graph(device, "medium", windClockSecondsOverride: 12f);
        graph.PrepareWorldTarget(640, 480, 1);
        // Storm is the highest declared mean/gust — the exact target does
        // not matter here, only that the gate still zeroes the output
        // despite a nonzero smoothed target.
        AtmosphericFrameInputs indoors = Inputs(640, 480) with
        {
            Weather = WeatherKind.Storm,
            IsOutdoor = false,
        };

        AtmosphericFrameUniforms atmospheric = RenderAndReadFrameBlock(device, graph, indoors);

        Assert.Equal(0f, atmospheric.ClockWind.Y); // mean
        Assert.Equal(0f, atmospheric.ClockWind.Z); // gust
    }

    [Fact]
    public void WindEnabledFalseGatesWindOutputToExactZero()
    {
        var device = new RecordingGpuDevice();
        using var graph = Graph(
            device,
            "medium",
            windClockSecondsOverride: 12f,
            userSettingOverrides: new Dictionary<string, string> { ["wind-enabled"] = "false" });
        graph.PrepareWorldTarget(640, 480, 1);
        AtmosphericFrameInputs outdoors = Inputs(640, 480) with { Weather = WeatherKind.Storm };

        AtmosphericFrameUniforms atmospheric = RenderAndReadFrameBlock(device, graph, outdoors);

        Assert.Equal(0f, atmospheric.ClockWind.Y);
        Assert.Equal(0f, atmospheric.ClockWind.Z);
    }

    [Fact]
    public void WindAmplitudeReflectsTheDeclaredSettingDefaults()
    {
        var device = new RecordingGpuDevice();
        using var graph = Graph(device, "medium", windClockSecondsOverride: 5f);
        graph.PrepareWorldTarget(640, 480, 1);
        AtmosphericFrameInputs outdoors = Inputs(640, 480);

        AtmosphericFrameUniforms atmospheric = RenderAndReadFrameBlock(device, graph, outdoors);

        Assert.Equal(new Vector4(0.25f, 0.15f, 0.05f, 8f), atmospheric.WindAmplitude);
        // 225 degrees, the declared default direction.
        Assert.Equal(225f * (MathF.PI / 180f), atmospheric.ClockWind.W, 5);
    }

    [Fact]
    public void RepeatedCallsOnTheSameFrameSerialProduceIdenticalWindBytes()
    {
        var device = new RecordingGpuDevice();
        using var graph = Graph(device, "medium");
        IGpuRenderTarget world = graph.PrepareWorldTarget(640, 480, 1);
        using IGpuFrame frame = device.BeginFrame();
        RecordWorldPass(frame, world);
        AtmosphericFrameInputs inputs = Inputs(640, 480) with { Weather = WeatherKind.Storm };

        graph.RenderPostProcess(frame, in inputs);
        AtmosphericFrameUniforms first = ReadLastFrameBlock(device);
        graph.RenderPostProcess(frame, in inputs);
        AtmosphericFrameUniforms second = ReadLastFrameBlock(device);
        frame.End();

        Assert.Equal(first.ClockWind, second.ClockWind);
        Assert.Equal(first.WindAmplitude, second.WindAmplitude);
    }

    [Fact]
    public void BuiltInAndDeclaredShadowGraphsUseTheSameTypedPriorVisibilitySelector()
    {
        string renderingRoot = Path.Combine(
            RepositoryRoot(),
            "src",
            "AcDream.App",
            "Rendering",
            "Packs");
        string builtIn = File.ReadAllText(
            Path.Combine(renderingRoot, "AtmosphericPostProcessGraph.cs"));
        string declared = File.ReadAllText(
            Path.Combine(renderingRoot, "DeclaredFullscreenRenderPackGraph.cs"));

        AssertGraphUsesTypedSelection(builtIn, "RenderDirectionalShadows(");
        AssertGraphUsesTypedSelection(declared, "RenderDeclaredDirectionalShadows(");

        static void AssertGraphUsesTypedSelection(string source, string methodName)
        {
            int start = source.IndexOf(methodName, StringComparison.Ordinal);
            Assert.True(start >= 0, $"missing {methodName}");
            int end = source.IndexOf("public IGpuRenderTarget PrepareWorldTarget", start,
                StringComparison.Ordinal);
            Assert.True(end > start, $"could not bound {methodName}");
            string method = source[start..end];
            Assert.Contains(
                "RetailLandscapeVisibilityFrame priorLandscapeVisibility =",
                method,
                StringComparison.Ordinal);
            Assert.Contains("world.PriorLandscapeVisibility", method,
                StringComparison.Ordinal);
            Assert.Contains("_shadowCasters.Select(", method, StringComparison.Ordinal);
            Assert.Contains("world.DirectionalShadowCellMembership", method,
                StringComparison.Ordinal);
            Assert.Contains("EmptyDirectionalShadowCellMembership.Instance", method,
                StringComparison.Ordinal);
            Assert.Contains("PriorLandscapeVisibility: world.PriorLandscapeVisibility", method,
                StringComparison.Ordinal);
        }
    }

    private static AtmosphericFrameUniforms RenderAndReadFrameBlock(
        RecordingGpuDevice device,
        AtmosphericPostProcessGraph graph,
        in AtmosphericFrameInputs inputs)
    {
        IGpuRenderTarget world = graph.PrepareWorldTarget(
            inputs.ViewportWidth,
            inputs.ViewportHeight,
            1);
        using IGpuFrame frame = device.BeginFrame();
        RecordWorldPass(frame, world);
        graph.RenderPostProcess(frame, in inputs);
        frame.End();
        return ReadLastFrameBlock(device);
    }

    private static AtmosphericFrameUniforms ReadLastFrameBlock(RecordingGpuDevice device)
    {
        GpuRecordedUniformBind frameBlock = device
            .OfKind<GpuRecordedUniformBind>()
            .Last(call => call.Binding == GpuBindingModel.UniformAtmosphericFrame);
        return MemoryMarshal.Read<AtmosphericFrameUniforms>(
            device.RingBytes.Slice(
                (int)frameBlock.OffsetBytes,
                AtmosphericFrameUniforms.SizeInBytes));
    }

    private static int CountOccurrences(string haystack, string needle)
    {
        int count = 0;
        int index = 0;
        while ((index = haystack.IndexOf(needle, index, StringComparison.Ordinal)) >= 0)
        {
            count++;
            index += needle.Length;
        }
        return count;
    }

    private static AtmosphericPostProcessGraph Graph(
        RecordingGpuDevice device,
        string presetId,
        AtmosphericPostProcessSettings? settings = null,
        float? windClockSecondsOverride = null,
        IReadOnlyDictionary<string, string>? userSettingOverrides = null)
    {
        RenderPackDescriptor descriptor = BuiltInAtmosphericRenderPack.Descriptor;
        RenderQualityPreset preset = Assert.Single(
            descriptor.QualityPresets,
            value => string.Equals(value.Id, presetId, StringComparison.Ordinal));
        return new AtmosphericPostProcessGraph(
            device,
            descriptor,
            BuiltInAssets(),
            preset,
            settings,
            userSettingOverrides,
            windClockSecondsOverride);
    }

    private static RenderPackDescriptor ExternalTierOneDescriptor(
        AtmospherePolicyDeclaration? policy = null)
    {
        var intermediate = new RenderResourceDeclaration(
            "custom-half",
            RenderResourceKind.Image2D,
            RenderFormatClass.HdrColor,
            new RenderExtentDeclaration(RenderExtentMode.RelativeToMainWorld, 0.5, 0.5),
            SizeBytes: 0,
            RenderResourceUsage.Sampled | RenderResourceUsage.ColorAttachment,
            RenderResourceLifetime.ActivePack,
            EstimatedResidentBytes: 8 * 1024 * 1024);
        RenderPassDeclaration[] passes =
        [
            new RenderPassDeclaration(
                "my-threshold",
                RenderPassHook.AtmosphereBeforeToneMap,
                "atmospheric_bloom_blur.vert.spv",
                "atmospheric_bloom_blur.frag.spv",
                [
                    RenderSemanticInput.WorldColor,
                    RenderSemanticInput.SunScreenPosition,
                    RenderSemanticInput.ActiveDayGroup,
                    RenderSemanticInput.Weather,
                ],
                [],
                [intermediate.Id]),
            new RenderPassDeclaration(
                "my-output",
                RenderPassHook.ToneMap,
                "atmospheric_bloom_blur.vert.spv",
                "atmospheric_bloom_blur.frag.spv",
                [],
                [intermediate.Id],
                []),
        ];
        var preset = new RenderQualityPreset(
            "default", "Default", [], [], [],
            32 * 1024 * 1024, 2, 3, 0.1, 0.2);
        return BuiltInAtmosphericRenderPack.Descriptor with
        {
            Id = "example.generic-tier1",
            DisplayName = "Generic Tier 1",
            HighestTier = RenderPackTier.Tier1,
            RequiredCapabilities =
            [
                RenderCapability.MainWorldColorIntermediate,
                RenderCapability.FullscreenPasses,
                RenderCapability.AuthoredSunScreenPosition,
                RenderCapability.AuthoredWeather,
            ],
            OptionalCapabilities = [],
            Resources = [intermediate],
            Passes = passes,
            SceneReplays = [],
            PipelineVariants = [],
            QualityPresets = [preset],
            Settings = [],
            AtmospherePolicy = policy,
        };
    }

    private static RenderPackDescriptor ExternalShadowsOnlyTierTwoDescriptor(
        AtmospherePolicyDeclaration policy)
    {
        RenderPackDescriptor source = BuiltInAtmosphericRenderPack.Descriptor;
        RenderResourceDeclaration shadowResource = source.Resources.Single(value =>
            value.Semantic == RenderResourceSemantic.DirectionalShadowDepth) with
        {
            Id = "external-shadow-map",
        };
        RenderPassDeclaration shadowPass = source.Passes.Single(value =>
            value.Semantic == RenderPassSemantic.DirectionalShadowDepth) with
        {
            Id = "external-shadow-depth",
            ResourceWrites = [shadowResource.Id],
        };
        var outputCopy = new RenderPassDeclaration(
            "output-copy",
            RenderPassHook.ToneMap,
            "atmospheric_bloom_blur.vert.spv",
            "atmospheric_bloom_blur.frag.spv",
            [RenderSemanticInput.WorldColor],
            [],
            []);
        RenderSettingSemantic[] settingSemantics =
        [
            RenderSettingSemantic.DirectionalShadowStrength,
            RenderSettingSemantic.DirectionalShadowReachMetres,
            RenderSettingSemantic.DirectionalShadowPcfTaps,
        ];
        RenderSettingDeclaration[] settings = source.Settings
            .Where(value => settingSemantics.Contains(value.Semantic))
            .ToArray();
        var preset = new RenderQualityPreset(
            "medium",
            "Medium",
            [],
            [],
            [],
            MaxResidentGpuBytes: 64L * 1024 * 1024,
            MaxIncrementalGpuMillisecondsP50: 2.0,
            MaxIncrementalGpuMillisecondsP99: 3.0,
            MaxIncrementalCpuMillisecondsP50: 0.2,
            MaxIncrementalCpuMillisecondsP99: 0.5)
        {
            Semantic = RenderQualitySemantic.Medium,
        };
        return new RenderPackDescriptor(
            "example.shadows-only",
            "External Shadows Only",
            new Version(1, 0, 0),
            RenderPackApi.Current,
            RenderPackTier.Tier2,
            [
                RenderCapability.MainWorldColorIntermediate,
                RenderCapability.FullscreenPasses,
                RenderCapability.AuthoredSunDirection,
                RenderCapability.AuthoredCelestialDirectionalLight,
                RenderCapability.AuthoredWeather,
                RenderCapability.DirectionalShadowMaps,
                RenderCapability.OutdoorDirectionalShadowCasterReplay,
                RenderCapability.AnimatedCasterTransforms,
                RenderCapability.AlphaCutoutShadowCasters,
            ],
            [RenderCapability.GpuTimestampQueries],
            [shadowResource],
            [shadowPass, outputCopy],
            source.SceneReplays,
            source.PipelineVariants.Where(value => value.Semantic is
                RenderPipelineVariantSemantic.TerrainDirectionalShadowCaster
                or RenderPipelineVariantSemantic.WorldOpaqueDirectionalShadowCaster
                or RenderPipelineVariantSemantic.WorldAlphaCutoutDirectionalShadowCaster
                or RenderPipelineVariantSemantic.TerrainDirectionalShadowReceiver
                or RenderPipelineVariantSemantic.WorldDirectionalShadowReceiver).ToArray(),
            [preset],
            settings,
            policy)
        {
            FeatureSummary = "Selected-celestial shadows with an HDR output copy and no post stack.",
        };
    }

    private static void RecordWorldPass(IGpuFrame frame, IGpuRenderTarget world)
    {
        using IGpuPassEncoder _ = frame.BeginPass(new GpuPassDescription
        {
            Name = "test-world-hdr",
            Color = new GpuColorAttachment(
                world,
                GpuLoadOp.Clear,
                world.Description.SampleCount > 1 ? GpuStoreOp.Resolve : GpuStoreOp.Store,
                Vector4.Zero),
            Depth = new GpuDepthAttachment(
                GpuLoadOp.Clear,
                GpuStoreOp.Store,
                1f,
                0),
            SampleCount = world.Description.SampleCount,
        });
    }

    private static void PublishCurrentShadow(
        AtmosphericPostProcessGraph graph,
        IGpuFrame frame) => PublishCurrentShadow(
            graph.DirectionalShadowReceivers,
            frame);

    private static void PublishCurrentShadow(
        IDirectionalShadowReceiverSource receiverSource,
        IGpuFrame frame)
    {
        var renderer = Assert.IsType<DirectionalSunShadowRenderer>(
            receiverSource);
        GpuRingAllocation transformAllocation = frame.AllocateRing(
            checked((int)WorldTransformCapacityPolicy.InitialBindingSizeBytes),
            GpuRingUsage.Storage);
        var sharedTransforms = new WorldTransformFrameSlice(
            frame.Serial,
            transformAllocation.Buffer,
            transformAllocation.OffsetBytes,
            WorldTransformCapacityPolicy.InitialBindingSizeBytes,
            FirstInstance: 0,
            InstanceCount: 0);
        DirectionalSunShadowDiagnostics diagnostics = renderer.RenderPrepared(
            frame,
            new DirectionalShadowEnvironmentState(
                DirectionalShadowGateReason.Enabled,
                Vector3.Normalize(new Vector3(0.2f, 0.3f, 1f)),
                LightElevationSin: 0.94f,
                Strength: 0.8f,
                SoftnessMultiplier: 1.25f,
                SourceKind: AuthoredCelestialShadowSourceKind.Sun),
            Matrix4x4.Identity,
            Matrix4x4.CreatePerspectiveFieldOfView(
                MathF.PI / 3f,
                16f / 9f,
                0.1f,
                500f),
            cameraNearMeters: 0.1f,
            casterDepthPaddingMeters: 48f,
            worldDraws: new DirectionalShadowPreparedDraws(),
            terrainDraws: new DirectionalShadowTerrainPreparedDraws(),
            worldGeometry: null,
            terrainGeometry: null,
            sharedTransforms);
        Assert.True(diagnostics.CascadeCount > 0);
    }

    private static void SetLastShadowDiagnostics(
        AtmosphericPostProcessGraph graph,
        DirectionalSunShadowDiagnostics diagnostics)
    {
        System.Reflection.FieldInfo field = typeof(AtmosphericPostProcessGraph)
            .GetField(
                "_lastShadowDiagnostics",
                System.Reflection.BindingFlags.Instance
                    | System.Reflection.BindingFlags.NonPublic)
            ?? throw new InvalidOperationException(
                "Atmospheric graph no longer owns its directional-shadow diagnostics.");
        field.SetValue(graph, diagnostics);
    }

    private static AtmosphericFrameInputs Inputs(
        int width,
        int height,
        float elevation = 4f,
        int activeDayGroup = 0) => new(
        new Vector2(0.5f, 0.35f),
        SunIsOnScreen: true,
        elevation,
        new Vector3(1f, 0.85f, 0.65f),
        Vector3.Normalize(new Vector3(0.2f, 0.5f, 0.8f)),
        SunDirectionalBrightness: 1f,
        Matrix4x4.Identity,
        activeDayGroup,
        WeatherKind.Clear,
        WeatherIntensity: 0f,
        DeltaSeconds: 1d / 60d,
        width,
        height,
        IsOutdoor: true);

    private static AtmosphericPackPassUniforms ReadPass(
        RecordingGpuDevice device,
        GpuRecordedUniformBind binding) =>
        MemoryMarshal.Read<AtmosphericPackPassUniforms>(device.RingBytes.Slice(
            (int)binding.OffsetBytes,
            AtmosphericPackPassUniforms.SizeInBytes));

    private static IRenderPackAssets BuiltInAssets() =>
        BuiltInAtmosphericRenderPack.CreateAssets(Path.Combine(
            RepositoryRoot(),
            "src",
            "AcDream.App",
            "Rendering",
            "Shaders",
            "spv"));

    private static RenderPackDescriptor RenamedExternalTierTwoDescriptor()
    {
        RenderPackDescriptor source = BuiltInAtmosphericRenderPack.Descriptor;
        Dictionary<string, string> resources = source.Resources
            .Select((value, index) => (value.Id, Renamed: $"external-resource-{index}"))
            .ToDictionary(static value => value.Id, static value => value.Renamed,
                StringComparer.OrdinalIgnoreCase);
        Dictionary<string, string> settings = source.Settings
            .Select((value, index) => (value.Id, Renamed: $"external-setting-{index}"))
            .ToDictionary(static value => value.Id, static value => value.Renamed,
                StringComparer.OrdinalIgnoreCase);
        string Shader(string asset) => $"external/{asset}";

        return source with
        {
            Id = "example.external-atmosphere",
            Resources = source.Resources.Select(value => value with
            {
                Id = resources[value.Id],
            }).ToArray(),
            Passes = source.Passes.Select((value, index) => value with
            {
                Id = $"external-pass-{index}",
                VertexShaderAsset = Shader(value.VertexShaderAsset),
                FragmentShaderAsset = Shader(value.FragmentShaderAsset),
                ResourceReads = value.ResourceReads.Select(id => resources[id]).ToArray(),
                ResourceWrites = value.ResourceWrites.Select(id => resources[id]).ToArray(),
            }).ToArray(),
            SceneReplays = source.SceneReplays.Select((value, index) => value with
            {
                Id = $"external-replay-{index}",
            }).ToArray(),
            PipelineVariants = source.PipelineVariants.Select((value, index) => value with
            {
                Id = $"external-variant-{index}",
                VertexShaderAsset = Shader(value.VertexShaderAsset),
                FragmentShaderAsset = Shader(value.FragmentShaderAsset),
            }).ToArray(),
            QualityPresets = source.QualityPresets.Select((value, index) => value with
            {
                Id = $"external-quality-{index}",
                ResourceOverrides = value.ResourceOverrides.Select(resource => resource with
                {
                    ResourceId = resources[resource.ResourceId],
                }).ToArray(),
                SettingOverrides = value.SettingOverrides.Select(setting => setting with
                {
                    SettingId = settings[setting.SettingId],
                }).ToArray(),
            }).ToArray(),
            Settings = source.Settings.Select((value, index) => value with
            {
                Id = settings[value.Id],
            }).ToArray(),
        };
    }

    private static string RepositoryRoot()
    {
        var directory = new DirectoryInfo(AppContext.BaseDirectory);
        while (directory is not null
            && !File.Exists(Path.Combine(directory.FullName, "AcDream.slnx")))
            directory = directory.Parent;
        return directory?.FullName
            ?? throw new InvalidOperationException("Could not locate repository root.");
    }

    private sealed class RejectingAssets : IRenderPackAssets
    {
        public Stream OpenRead(string assetKey) =>
            throw new InvalidOperationException("A no-op pack must not open shader assets.");
    }

    private sealed class RenamedShaderAssets(IRenderPackAssets inner) : IRenderPackAssets
    {
        public Stream OpenRead(string assetKey)
        {
            const string prefix = "external/";
            if (!assetKey.StartsWith(prefix, StringComparison.Ordinal))
                throw new InvalidOperationException($"Unexpected external asset '{assetKey}'.");
            return inner.OpenRead(assetKey[prefix.Length..]);
        }
    }
}
