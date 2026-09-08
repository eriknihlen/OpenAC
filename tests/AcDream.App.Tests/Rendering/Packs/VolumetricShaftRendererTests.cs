using System.Numerics;
using AcDream.App.Rendering;
using AcDream.App.Rendering.Gpu;
using AcDream.App.Rendering.Packs;
using AcDream.App.Tests.Rendering.Gpu;
using AcDream.Core.World;
using AcDream.Plugin.Abstractions.Rendering;

namespace AcDream.App.Tests.Rendering.Packs;

public sealed class VolumetricShaftRendererTests
{
    [Fact]
    public void MediumConsumesCurrentB5B6B8AndWritesQuarterResolutionHdr()
    {
        var device = new RecordingGpuDevice();
        using var renderer = Renderer(device, "medium");
        GpuTextureSlot depth = TextureSlot(device, "scene-depth");
        using IGpuFrame frame = device.BeginFrame();
        DirectionalShadowFrameBinding shadow = Shadow(frame, device.DefaultTextureSlot);
        AtmosphericFrameInputs inputs = Inputs(800, 600);
        device.Clear();

        VolumetricShaftOutput output = renderer.Render(frame, in inputs, in shadow, depth);
        frame.End();

        Assert.True(output.HasTexture);
        Assert.Equal(VolumetricShaftGateReason.Rendered, output.Diagnostics.GateReason);
        Assert.Equal(200, output.Diagnostics.Width);
        Assert.Equal(150, output.Diagnostics.Height);
        Assert.Equal(40, output.Diagnostics.RayMarchSteps);
        Assert.Equal(200L * 150L * 8L, output.Diagnostics.RetainedGpuBytes);
        Assert.Equal(
            [
                GpuBindingModel.UniformAtmosphericFrame,
                GpuBindingModel.UniformDirectionalShadow,
                GpuBindingModel.UniformPackPass,
                GpuBindingModel.UniformPackSettings,
            ],
            device.OfKind<GpuRecordedUniformBind>().Select(call => call.Binding));
        Assert.Equal(depth.Index,
            Assert.Single(device.OfKind<GpuRecordedPushConstants>()).Constants.TextureIndexA);
        Assert.Equal(1, renderer.Performance.CpuSampleCount);
        Assert.Equal(0, renderer.Performance.GpuSampleCount);
    }

    [Fact]
    public void LowDefaultsOffWithoutAllocatingTargetOrRecordingPass()
    {
        var device = new RecordingGpuDevice();
        using var renderer = Renderer(device, "low");
        using IGpuFrame frame = device.BeginFrame();
        DirectionalShadowFrameBinding shadow = Shadow(frame, device.DefaultTextureSlot);
        AtmosphericFrameInputs inputs = Inputs(800, 600);
        int targets = device.CreatedRenderTargets.Count;

        VolumetricShaftOutput output = renderer.Render(
            frame,
            in inputs,
            in shadow,
            device.DefaultTextureSlot);
        frame.End();

        Assert.False(output.HasTexture);
        Assert.Equal(VolumetricShaftGateReason.DisabledByPreset, output.Diagnostics.GateReason);
        Assert.Equal(targets, device.CreatedRenderTargets.Count);
        Assert.Empty(device.OfKind<GpuRecordedPassBegin>());
        Assert.Equal(default, renderer.Performance);
    }

    [Fact]
    public void LowUserOverrideEnablesQuarterResolutionTwentyFourStepShafts()
    {
        var device = new RecordingGpuDevice();
        RenderPackDescriptor descriptor = BuiltInAtmosphericRenderPack.Descriptor;
        RenderQualityPreset low = Assert.Single(
            descriptor.QualityPresets,
            value => string.Equals(value.Id, "low", StringComparison.Ordinal));
        using var renderer = new VolumetricShaftRenderer(
            device,
            descriptor,
            Assets(),
            low,
            new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase)
            {
                ["volumetric-strength"] = "0.25",
            });
        GpuTextureSlot depth = TextureSlot(device, "depth");

        RenderOne(renderer, device, Inputs(800, 600), depth);

        Assert.Equal(VolumetricShaftGateReason.Rendered, renderer.LastDiagnostics.GateReason);
        Assert.Equal(200, renderer.LastDiagnostics.Width);
        Assert.Equal(150, renderer.LastDiagnostics.Height);
        Assert.Equal(24, renderer.LastDiagnostics.RayMarchSteps);
        Assert.True(renderer.LastDiagnostics.Strength > 0f);
    }

    [Fact]
    public void StaleShadowBindingAndIndoorFrameFailClosedWithoutSamplingOldOutput()
    {
        var device = new RecordingGpuDevice();
        using var renderer = Renderer(device, "high");
        AtmosphericFrameInputs inputs = Inputs(1280, 720);
        using IGpuFrame frame = device.BeginFrame();
        var stale = new DirectionalShadowFrameBinding(
            frame.Serial - 1,
            true,
            device.RingBuffer,
            0,
            DirectionalShadowUniforms.SizeInBytes,
            device.DefaultTextureSlot,
            4);

        VolumetricShaftOutput staleOutput = renderer.Render(
            frame,
            in inputs,
            in stale,
            device.DefaultTextureSlot);
        DirectionalShadowFrameBinding current = Shadow(frame, device.DefaultTextureSlot);
        AtmosphericFrameInputs indoor = inputs with { IsOutdoor = false };
        VolumetricShaftOutput indoorOutput = renderer.Render(
            frame,
            in indoor,
            in current,
            device.DefaultTextureSlot);
        frame.End();

        Assert.Equal(VolumetricShaftGateReason.NoCurrentDirectionalShadow,
            staleOutput.Diagnostics.GateReason);
        Assert.Equal(VolumetricShaftGateReason.Indoor, indoorOutput.Diagnostics.GateReason);
        Assert.False(staleOutput.HasTexture);
        Assert.False(indoorOutput.HasTexture);
        Assert.Empty(device.CreatedRenderTargets);
    }

    [Fact]
    public void DisabledShadowContentBindingStillGatesToNoCurrentDirectionalShadow()
    {
        var device = new RecordingGpuDevice();
        using var renderer = Renderer(device, "high");
        GpuTextureSlot depth = TextureSlot(device, "scene-depth");
        using IGpuFrame frame = device.BeginFrame();
        GpuRingAllocation allocation = frame.AllocateRing(
            DirectionalShadowUniforms.SizeInBytes,
            GpuRingUsage.Uniform);
        allocation.Data.Clear();
        var disabledContent = new DirectionalShadowFrameBinding(
            frame.Serial,
            Enabled: false,
            allocation.Buffer,
            allocation.OffsetBytes,
            DirectionalShadowUniforms.SizeInBytes,
            GpuTextureSlot.Unassigned,
            CascadeCount: 0);
        AtmosphericFrameInputs inputs = Inputs(1280, 720);

        VolumetricShaftOutput output = renderer.Render(
            frame,
            in inputs,
            in disabledContent,
            depth);
        frame.End();

        Assert.True(disabledContent.IsBindableFor(frame));
        Assert.False(disabledContent.IsValidFor(frame));
        Assert.Equal(
            VolumetricShaftGateReason.NoCurrentDirectionalShadow,
            output.Diagnostics.GateReason);
        Assert.False(output.HasTexture);
    }

    [Fact]
    public void ResizeAtomicallyReplacesTargetAndResetsMixedResolutionPerformanceWindow()
    {
        var device = new RecordingGpuDevice();
        using var renderer = Renderer(device, "high");
        GpuTextureSlot depth = TextureSlot(device, "depth");

        RenderOne(renderer, device, Inputs(800, 600), depth);
        RecordingGpuRenderTarget first = Assert.Single(device.CreatedRenderTargets);
        Assert.Equal(400, first.Description.Width);
        Assert.Equal(1, renderer.Performance.CpuSampleCount);

        RenderOne(renderer, device, Inputs(1200, 800), depth);

        Assert.True(first.IsDisposed);
        Assert.Equal(600, device.CreatedRenderTargets[^1].Description.Width);
        Assert.Equal(400, device.CreatedRenderTargets[^1].Description.Height);
        Assert.Equal(1, renderer.Performance.CpuSampleCount);
    }

    [Fact]
    public void TargetFailureRollsBackAndRetryPublishesOneOwnedTexture()
    {
        var device = new RecordingGpuDevice();
        using var renderer = Renderer(device, "medium");
        GpuTextureSlot depth = TextureSlot(device, "depth");
        int baselineSlots = device.LiveTextureSlotCount;
        device.RenderTargetFailure = _ => new InvalidOperationException("volumetric allocation failed");

        Assert.Throws<InvalidOperationException>(() => RenderOne(
            renderer,
            device,
            Inputs(800, 600),
            depth));
        Assert.Equal(baselineSlots, device.LiveTextureSlotCount);
        Assert.Empty(device.CreatedRenderTargets);

        device.RenderTargetFailure = null;
        RenderOne(renderer, device, Inputs(800, 600), depth);
        Assert.Equal(baselineSlots + 1, device.LiveTextureSlotCount);
        Assert.Single(device.CreatedRenderTargets);
    }

    [Fact]
    public void DisposeReleasesOutputSlotTargetAndPipeline()
    {
        var device = new RecordingGpuDevice();
        GpuTextureSlot depth = TextureSlot(device, "depth");
        int baselineSlots = device.LiveTextureSlotCount;
        var renderer = Renderer(device, "medium");
        RenderOne(renderer, device, Inputs(800, 600), depth);
        RecordingGpuRenderTarget target = Assert.Single(device.CreatedRenderTargets);
        RecordingGpuPipeline pipeline = Assert.Single(device.CreatedPipelines);

        renderer.Dispose();

        Assert.Equal(baselineSlots, device.LiveTextureSlotCount);
        Assert.True(target.IsDisposed);
        Assert.True(pipeline.IsDisposed);
    }

    [Fact]
    public void AuthoredWeatherAndSunElevationContinuouslyScaleTheSameFramePolicy()
    {
        var device = new RecordingGpuDevice();
        using var renderer = Renderer(device, "medium");
        GpuTextureSlot depth = TextureSlot(device, "depth");

        RenderOne(renderer, device, Inputs(800, 600), depth);
        float clearLowSun = renderer.LastDiagnostics.Strength;
        AtmosphericFrameInputs overcast = Inputs(800, 600) with
        {
            Weather = WeatherKind.Overcast,
            WeatherIntensity = 1f,
        };
        RenderOne(renderer, device, overcast, depth);
        float overcastLowSun = renderer.LastDiagnostics.Strength;
        AtmosphericFrameInputs noon = Inputs(800, 600) with
        {
            SunElevationDegrees = 70f,
        };
        RenderOne(renderer, device, noon, depth);

        Assert.True(clearLowSun > overcastLowSun);
        Assert.True(clearLowSun > renderer.LastDiagnostics.Strength);
        Assert.True(overcastLowSun > 0f);
    }

    [Fact]
    public void DeclaredActiveDayGroupMultiplierScalesAuthoredPolicy()
    {
        var device = new RecordingGpuDevice();
        using var renderer = Renderer(device, "medium");
        GpuTextureSlot depth = TextureSlot(device, "depth");

        RenderOne(renderer, device, Inputs(800, 600) with { ActiveDayGroup = 0 }, depth);
        float groupZero = renderer.LastDiagnostics.Strength;
        RenderOne(renderer, device, Inputs(800, 600) with { ActiveDayGroup = 1 }, depth);
        float groupOne = renderer.LastDiagnostics.Strength;

        Assert.Equal(groupZero * 0.35f, groupOne, 5);
    }

    [Fact]
    public void DeclaredVolumetricElevationCurveControlsShaftStrength()
    {
        var device = new RecordingGpuDevice();
        RenderPackDescriptor source = BuiltInAtmosphericRenderPack.Descriptor;
        RenderPackDescriptor changed = source with
        {
            AtmospherePolicy = source.AtmospherePolicy! with
            {
                VolumetricShaftSunElevationResponse =
                [
                    new SunElevationResponsePoint(-90, 0.25),
                    new SunElevationResponsePoint(90, 0.25),
                ],
            },
        };
        RenderQualityPreset medium = Assert.Single(changed.QualityPresets, value =>
            value.Semantic == RenderQualitySemantic.Medium);
        using var renderer = new VolumetricShaftRenderer(
            device,
            changed,
            Assets(),
            medium);
        GpuTextureSlot depth = TextureSlot(device, "depth");

        RenderOne(renderer, device, Inputs(800, 600), depth);

        Assert.Equal(0.25f * 0.35f, renderer.LastDiagnostics.Strength, 5);
    }

    [Fact]
    public void ShaderUsesVulkanYFlipWorldMetreBiasAndShadowStrengthMix()
    {
        string source = File.ReadAllText(Path.Combine(
            RepositoryRoot(),
            "src", "AcDream.App", "Rendering", "Shaders", "atmospheric_volumetric.frag"));

        Assert.Contains("0.5 - ndc.y * 0.5", source, StringComparison.Ordinal);
        Assert.Contains("surfaceToSun * max(uShadowBiasMeters.x, 0.0)", source,
            StringComparison.Ordinal);
        Assert.Contains("mix(1.0, visible, clamp(uShadowControl.x, 0.0, 1.0))", source,
            StringComparison.Ordinal);
        Assert.DoesNotContain("ndc.z -", source, StringComparison.Ordinal);
    }

    private static void RenderOne(
        VolumetricShaftRenderer renderer,
        RecordingGpuDevice device,
        AtmosphericFrameInputs inputs,
        GpuTextureSlot depth)
    {
        using IGpuFrame frame = device.BeginFrame();
        DirectionalShadowFrameBinding shadow = Shadow(frame, device.DefaultTextureSlot);
        renderer.Render(frame, in inputs, in shadow, depth);
        frame.End();
    }

    private static DirectionalShadowFrameBinding Shadow(
        IGpuFrame frame,
        GpuTextureSlot shadowTexture)
    {
        GpuRingAllocation allocation = frame.AllocateRing(
            DirectionalShadowUniforms.SizeInBytes,
            GpuRingUsage.Uniform);
        allocation.Data.Clear();
        return new DirectionalShadowFrameBinding(
            frame.Serial,
            true,
            allocation.Buffer,
            allocation.OffsetBytes,
            DirectionalShadowUniforms.SizeInBytes,
            shadowTexture,
            3);
    }

    private static GpuTextureSlot TextureSlot(RecordingGpuDevice device, string name)
    {
        IGpuTexture texture = device.CreateTexture(new GpuTextureDescription(
            name,
            GpuTextureKind.Texture2D,
            GpuTextureFormat.Rgba8Unorm,
            1,
            1,
            1,
            1));
        return device.RegisterTexture(texture, device.CreateSampler(GpuSamplerDescription.WorldClamp));
    }

    private static VolumetricShaftRenderer Renderer(
        RecordingGpuDevice device,
        string presetId)
    {
        RenderPackDescriptor descriptor = BuiltInAtmosphericRenderPack.Descriptor;
        RenderQualityPreset preset = Assert.Single(
            descriptor.QualityPresets,
            value => string.Equals(value.Id, presetId, StringComparison.Ordinal));
        return new VolumetricShaftRenderer(device, descriptor, Assets(), preset);
    }

    private static AtmosphericFrameInputs Inputs(int width, int height) => new(
        new Vector2(0.5f, 0.4f),
        true,
        12f,
        new Vector3(1f, 0.85f, 0.7f),
        Vector3.Normalize(new Vector3(0.2f, 0.5f, 0.8f)),
        1f,
        Matrix4x4.Identity,
        0,
        WeatherKind.Clear,
        0f,
        1d / 60d,
        width,
        height,
        true);

    private static IRenderPackAssets Assets() =>
        BuiltInAtmosphericRenderPack.CreateAssets(Path.Combine(
            RepositoryRoot(),
            "src", "AcDream.App", "Rendering", "Shaders", "spv"));

    private static string RepositoryRoot()
    {
        var directory = new DirectoryInfo(AppContext.BaseDirectory);
        while (directory is not null
            && !File.Exists(Path.Combine(directory.FullName, "AcDream.slnx")))
            directory = directory.Parent;
        return directory?.FullName
            ?? throw new InvalidOperationException("Could not locate repository root.");
    }
}
