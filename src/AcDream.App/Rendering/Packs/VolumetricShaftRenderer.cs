using System.Diagnostics;
using System.Numerics;
using System.Runtime.InteropServices;
using AcDream.App.Rendering.Gpu;
using AcDream.Plugin.Abstractions.Rendering;

namespace AcDream.App.Rendering.Packs;

internal enum VolumetricShaftGateReason : byte
{
    Rendered,
    DisabledByPreset,
    NoCurrentDirectionalShadow,
    NoSceneDepth,
    Indoor,
    SunOffScreen,
    SunBelowHorizon,
    AtmosphereSuppressed,
}

internal readonly record struct VolumetricShaftDiagnostics(
    VolumetricShaftGateReason GateReason,
    int Width,
    int Height,
    int RayMarchSteps,
    float Density,
    float Strength,
    long RetainedGpuBytes,
    double LastResolvedGpuMilliseconds,
    bool HasResolvedGpuMeasurement,
    int DrawCalls);

internal readonly record struct VolumetricShaftOutput(
    GpuTextureSlot TextureSlot,
    VolumetricShaftDiagnostics Diagnostics)
{
    internal bool HasTexture => TextureSlot.IsAssigned;
}

internal sealed class VolumetricShaftRenderer : IDisposable
{
    internal const string TimerName = "atmospheric-volumetric-shafts";

    private readonly IGpuDevice _device;
    private readonly VolumetricShaftQuality _quality;
    private readonly float _declaredStrength;
    private readonly AtmospherePolicyDeclaration _atmospherePolicy;
    private readonly IReadOnlyDictionary<int, float> _dayGroupMultipliers;
    private readonly IGpuSampler _sampler;
    private readonly IGpuPipeline _pipeline;
    private readonly PackSettingsUniforms _settings;
    private readonly RenderPackPerformanceWindow _performance = new();
    private Target? _target;
    private bool _disposed;

    internal VolumetricShaftRenderer(
        IGpuDevice device,
        RenderPackDescriptor descriptor,
        IRenderPackAssets assets,
        RenderQualityPreset preset,
        IReadOnlyDictionary<string, string>? userSettingOverrides = null)
        : this(
            device,
            descriptor,
            RenderPackShaderAssets.Validate(descriptor, assets),
            preset,
            userSettingOverrides)
    {
    }

    internal VolumetricShaftRenderer(
        IGpuDevice device,
        RenderPackDescriptor descriptor,
        ValidatedRenderPackShaderAssets assets,
        RenderQualityPreset preset,
        IReadOnlyDictionary<string, string>? userSettingOverrides = null)
    {
        _device = device ?? throw new ArgumentNullException(nameof(device));
        ArgumentNullException.ThrowIfNull(descriptor);
        ArgumentNullException.ThrowIfNull(assets);
        ArgumentNullException.ThrowIfNull(preset);
        _quality = ResolveQuality(
            descriptor,
            preset,
            userSettingOverrides);
        _declaredStrength = ReadSetting(
            descriptor,
            preset,
            userSettingOverrides,
            RenderSettingSemantic.VolumetricStrength,
            0.35f);
        _atmospherePolicy = descriptor.AtmospherePolicy
            ?? throw new NotSupportedException(
                $"Pack '{descriptor.Id}' declares no atmosphere policy.");
        if (_atmospherePolicy.VolumetricShaftSunElevationResponse.Count < 2)
        {
            throw new NotSupportedException(
                $"Pack '{descriptor.Id}' declares no volumetric-shaft elevation curve.");
        }
        _dayGroupMultipliers = _atmospherePolicy.ActiveDayGroupMultipliers
            .ToDictionary(value => value.ActiveDayGroup, value => (float)value.Multiplier);
        _settings = PackSettingsUniforms.Create(descriptor, preset, userSettingOverrides);
        RenderPassDeclaration pass = descriptor.Passes.FirstOrDefault(value =>
            value.Semantic == RenderPassSemantic.VolumetricShafts)
            ?? throw new NotSupportedException(
                $"Pack '{descriptor.Id}' declares no VolumetricShafts pass semantic.");

        _sampler = device.CreateSampler(GpuSamplerDescription.WorldClamp);
        _pipeline = device.CreatePipeline(new GpuPipelineDescription
        {
            Name = $"render-pack-{descriptor.Id}-volumetric-shafts",
            Shaders = RenderPackShaderAssets.LoadPass(descriptor, assets, pass),
            VertexLayout = GpuVertexLayout.None,
            Blend = GpuBlendMode.None,
            Depth = GpuDepthState.Disabled,
            Cull = GpuCullMode.None,
            ColorFormat = GpuTextureFormat.Rgba16FloatRenderTarget,
            AllowColorFormatVariants = false,
            SampleCount = 1,
            UsesRenderPackShaderAbi = true,
        });
        LastDiagnostics = Disabled(VolumetricShaftGateReason.DisabledByPreset);
    }

    internal VolumetricShaftDiagnostics LastDiagnostics { get; private set; }

    internal VolumetricShaftQuality Quality => _quality;

    internal RenderPackPerformanceSnapshot Performance => _performance.Snapshot();

    internal void PrepareTarget(int outputWidth, int outputHeight)
    {
        ObjectDisposedException.ThrowIf(_disposed, this);
        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(outputWidth);
        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(outputHeight);
        if (_declaredStrength > 0f)
            _ = Prepare(outputWidth, outputHeight);
    }

    internal VolumetricShaftOutput Render(
        IGpuFrame frame,
        in AtmosphericFrameInputs inputs,
        in DirectionalShadowFrameBinding shadow,
        GpuTextureSlot sceneDepth)
    {
        ObjectDisposedException.ThrowIf(_disposed, this);
        ArgumentNullException.ThrowIfNull(frame);
        VolumetricShaftGateReason reason = Gate(frame, inputs, shadow, sceneDepth);
        if (reason != VolumetricShaftGateReason.Rendered)
        {
            LastDiagnostics = Disabled(reason);
            return new VolumetricShaftOutput(GpuTextureSlot.Unassigned, LastDiagnostics);
        }

        (float density, float strength) = Parameters(inputs);
        if (strength <= 1e-4f)
        {
            LastDiagnostics = Disabled(VolumetricShaftGateReason.AtmosphereSuppressed);
            return new VolumetricShaftOutput(GpuTextureSlot.Unassigned, LastDiagnostics);
        }

        Target target = Prepare(inputs.ViewportWidth, inputs.ViewportHeight);
        long started = Stopwatch.GetTimestamp();
        AtmosphericFrameUniforms atmospheric = FrameUniforms(inputs, strength);
        GpuRingAllocation frameBlock = frame.AllocateRing(
            AtmosphericFrameUniforms.SizeInBytes,
            GpuRingUsage.Uniform);
        MemoryMarshal.Write(frameBlock.Data, in atmospheric);
        GpuRingAllocation passBlock = frame.AllocateRing(
            AtmosphericPackPassUniforms.SizeInBytes,
            GpuRingUsage.Uniform);
        var passValues = new AtmosphericPackPassUniforms(
            new Vector4(density, strength, _quality.RayMarchSteps, 1f),
            Vector4.Zero,
            Vector4.Zero,
            Vector4.Zero);
        MemoryMarshal.Write(passBlock.Data, in passValues);
        GpuRingAllocation settingsBlock = frame.AllocateRing(
            PackSettingsUniforms.SizeInBytes,
            GpuRingUsage.Uniform);
        PackSettingsUniforms settings = _settings;
        MemoryMarshal.Write(settingsBlock.Data, in settings);

        using (IGpuPassEncoder encoder = frame.BeginPass(new GpuPassDescription
        {
            Name = TimerName,
            Color = new GpuColorAttachment(
                target.RenderTarget,
                GpuLoadOp.Clear,
                GpuStoreOp.Store,
                Vector4.Zero),
            Depth = null,
            SampleCount = 1,
        }))
        using (encoder.BeginTimerScope(TimerName))
        {
            encoder.BindPipeline(_pipeline);
            encoder.BindUniformBuffer(
                GpuBindingModel.UniformAtmosphericFrame,
                frameBlock.Buffer,
                frameBlock.OffsetBytes,
                AtmosphericFrameUniforms.SizeInBytes);
            encoder.BindUniformBuffer(
                GpuBindingModel.UniformDirectionalShadow,
                shadow.Buffer!,
                shadow.OffsetBytes,
                shadow.SizeBytes);
            encoder.BindUniformBuffer(
                GpuBindingModel.UniformPackPass,
                passBlock.Buffer,
                passBlock.OffsetBytes,
                AtmosphericPackPassUniforms.SizeInBytes);
            encoder.BindUniformBuffer(
                GpuBindingModel.UniformPackSettings,
                settingsBlock.Buffer,
                settingsBlock.OffsetBytes,
                PackSettingsUniforms.SizeInBytes);
            GpuPushConstants push = GpuPushConstants.Default;
            push.TextureIndexA = sceneDepth.Index;
            push.TextureIndexB = GpuTextureSlot.Unassigned.Index;
            push.ParamA = BitConverter.UInt32BitsToSingle(GpuTextureSlot.Unassigned.Index);
            push.ParamB = BitConverter.UInt32BitsToSingle(GpuTextureSlot.Unassigned.Index);
            encoder.SetPushConstants(in push);
            encoder.Draw(3, 1, 0, 0);
        }

        bool hasGpu = _device.Timers.TryResolve(TimerName, out double milliseconds);
        LastDiagnostics = new VolumetricShaftDiagnostics(
            VolumetricShaftGateReason.Rendered,
            target.RenderTarget.Description.Width,
            target.RenderTarget.Description.Height,
            _quality.RayMarchSteps,
            density,
            strength,
            target.RetainedBytes,
            milliseconds,
            hasGpu,
            DrawCalls: 1);
        _performance.Observe(
            Stopwatch.GetElapsedTime(started).TotalMilliseconds,
            absoluteReceiverCpuMilliseconds: 0d,
            hasGpu,
            milliseconds,
            target.RetainedBytes,
            transientGpuBytes: 0);
        return new VolumetricShaftOutput(target.TextureSlot, LastDiagnostics);
    }

    public void Dispose()
    {
        if (_disposed)
            return;
        _disposed = true;
        _target?.Dispose();
        _target = null;
        _pipeline.Dispose();
    }

    private Target Prepare(int outputWidth, int outputHeight)
    {
        int width = Math.Max(1, (int)MathF.Ceiling(outputWidth * _quality.ResolutionScale));
        int height = Math.Max(1, (int)MathF.Ceiling(outputHeight * _quality.ResolutionScale));
        if (_target is { } current
            && current.RenderTarget.Description.Width == width
            && current.RenderTarget.Description.Height == height)
            return current;

        IGpuRenderTarget? renderTarget = null;
        GpuTextureSlot slot = GpuTextureSlot.Unassigned;
        try
        {
            renderTarget = _device.CreateRenderTarget(new GpuRenderTargetDescription(
                "atmospheric-volumetric",
                width,
                height,
                GpuTextureFormat.Rgba16FloatRenderTarget,
                DepthFormat: null,
                SampleCount: 1));
            slot = _device.RegisterTexture(renderTarget.ColorTexture, _sampler);
            var candidate = new Target(_device, renderTarget, slot);
            renderTarget = null;
            slot = GpuTextureSlot.Unassigned;
            Target? prior = _target;
            _target = candidate;
            prior?.Dispose();
            _performance.Reset();
            return candidate;
        }
        catch
        {
            if (slot.IsAssigned)
                _device.ReleaseTextureSlot(slot);
            renderTarget?.Dispose();
            throw;
        }
    }

    private VolumetricShaftGateReason Gate(
        IGpuFrame frame,
        in AtmosphericFrameInputs inputs,
        in DirectionalShadowFrameBinding shadow,
        GpuTextureSlot sceneDepth)
    {
        if (_declaredStrength <= 0f)
            return VolumetricShaftGateReason.DisabledByPreset;
        if (!shadow.IsValidFor(frame))
            return VolumetricShaftGateReason.NoCurrentDirectionalShadow;
        if (!sceneDepth.IsAssigned)
            return VolumetricShaftGateReason.NoSceneDepth;
        if (!inputs.IsOutdoor)
            return VolumetricShaftGateReason.Indoor;
        if (!inputs.SunIsOnScreen)
            return VolumetricShaftGateReason.SunOffScreen;
        return VolumetricShaftGateReason.Rendered;
    }

    private (float Density, float Strength) Parameters(in AtmosphericFrameInputs inputs)
    {
        float weatherTarget = inputs.Weather switch
        {
            AcDream.Core.World.WeatherKind.Clear => 1f,
            AcDream.Core.World.WeatherKind.Overcast => 0.18f,
            AcDream.Core.World.WeatherKind.Rain => 0.10f,
            AcDream.Core.World.WeatherKind.Snow => 0.16f,
            AcDream.Core.World.WeatherKind.Storm => 0.06f,
            _ => 0f,
        };
        float weatherBlend = Math.Clamp(inputs.WeatherIntensity, 0f, 1f);
        float weather = 1f + ((weatherTarget - 1f) * weatherBlend);
        float elevation = RenderPackAtmospherePolicyEvaluation.VolumetricShaft(
            _atmospherePolicy.VolumetricShaftSunElevationResponse,
            inputs.SunElevationDegrees);
        float authoredEnergy = Math.Clamp(inputs.SunDirectionalBrightness, 0f, 4f);
        float dayGroup = _dayGroupMultipliers.TryGetValue(
            inputs.ActiveDayGroup,
            out float declaredDayGroup)
                ? Math.Clamp(declaredDayGroup, 0f, 4f)
                : 1f;
        float strength = Math.Clamp(
            _declaredStrength * weather * elevation * authoredEnergy * dayGroup,
            0f,
            1f);
        return (0.035f * strength, strength);
    }

    private AtmosphericFrameUniforms FrameUniforms(
        in AtmosphericFrameInputs inputs,
        float strength) => new(
        new Vector4(inputs.SunScreenUv, strength, inputs.SunElevationDegrees),
        new Vector4(inputs.SunColor, strength),
        new Vector4(inputs.ViewportWidth, inputs.ViewportHeight,
            1f / inputs.ViewportWidth, 1f / inputs.ViewportHeight),
        new Vector4((float)inputs.Weather, inputs.WeatherIntensity,
            (float)Math.Clamp(inputs.DeltaSeconds, 0d, 1d), inputs.IsOutdoor ? 1f : 0f),
        new Vector4(inputs.SunDirection, inputs.SunDirectionalBrightness),
        new Vector4(
            inputs.ActiveDayGroup,
            _dayGroupMultipliers.TryGetValue(inputs.ActiveDayGroup, out float dayGroup)
                ? dayGroup
                : 1f,
            RenderPackAtmospherePolicyEvaluation.DirectionalShadow(
                _atmospherePolicy.DirectionalShadowLightElevationResponse,
                inputs.SunElevationDegrees),
            RenderPackAtmospherePolicyEvaluation.VolumetricShaft(
                _atmospherePolicy.VolumetricShaftSunElevationResponse,
                inputs.SunElevationDegrees)),
        inputs.InverseViewProjection,
        Vector4.Zero,
        Vector4.Zero);

    private VolumetricShaftDiagnostics Disabled(VolumetricShaftGateReason reason) => new(
        reason,
        0,
        0,
        _quality.RayMarchSteps,
        0f,
        0f,
        _target?.RetainedBytes ?? 0L,
        0d,
        false,
        0);

    private static DirectionalShadowPreset PresetOf(RenderQualityPreset preset) =>
        preset.Semantic switch
        {
            RenderQualitySemantic.Low => DirectionalShadowPreset.Low,
            RenderQualitySemantic.High => DirectionalShadowPreset.High,
            _ => DirectionalShadowPreset.Medium,
        };

    private static VolumetricShaftQuality ResolveQuality(
        RenderPackDescriptor descriptor,
        RenderQualityPreset preset,
        IReadOnlyDictionary<string, string>? userSettingOverrides)
    {
        VolumetricShaftQuality quality = VolumetricShaftQuality.For(PresetOf(preset));
        RenderResourceDeclaration resource = descriptor.Resources.Single(value =>
            value.Semantic == RenderResourceSemantic.VolumetricShafts);
        RenderQualityResourceOverride? resourceOverride = preset.ResourceOverrides
            .FirstOrDefault(value => string.Equals(
                value.ResourceId,
                resource.Id,
                StringComparison.OrdinalIgnoreCase));
        RenderExtentDeclaration extent = resourceOverride?.Extent
            ?? resource.Extent
            ?? throw new NotSupportedException(
                "The VolumetricShafts semantic resource has no image extent.");
        if (extent.Mode is not RenderExtentMode.RelativeToMainWorld
            and not RenderExtentMode.RelativeToOutput)
        {
            throw new NotSupportedException(
                "The VolumetricShafts semantic resource must use a relative extent.");
        }
        int steps = checked((int)MathF.Round(ReadSetting(
            descriptor,
            preset,
            userSettingOverrides,
            RenderSettingSemantic.VolumetricRayMarchSteps,
            quality.RayMarchSteps)));
        return quality with
        {
            ResolutionScale = (float)Math.Clamp(extent.Width, 0.0625, 1.0),
            RayMarchSteps = Math.Clamp(steps, 8, 64),
        };
    }

    private static float ReadSetting(
        RenderPackDescriptor descriptor,
        RenderQualityPreset preset,
        IReadOnlyDictionary<string, string>? userSettingOverrides,
        RenderSettingSemantic semantic,
        float fallback)
    {
        RenderSettingDeclaration? setting = descriptor.Settings.FirstOrDefault(candidate =>
            candidate.Semantic == semantic);
        if (setting is null)
            return fallback;
        string value = RenderPackSettingResolution.Resolve(
            setting,
            preset,
            userSettingOverrides);
        return RenderPackSettingValueCodec.TryEncode(setting, value, out float encoded)
            ? Math.Max(0f, encoded)
            : fallback;
    }

    private sealed class Target(
        IGpuDevice device,
        IGpuRenderTarget renderTarget,
        GpuTextureSlot textureSlot) : IDisposable
    {
        internal IGpuRenderTarget RenderTarget { get; } = renderTarget;
        internal GpuTextureSlot TextureSlot { get; } = textureSlot;
        internal long RetainedBytes => checked(
            (long)RenderTarget.Description.Width * RenderTarget.Description.Height * 8L);

        public void Dispose()
        {
            device.ReleaseTextureSlot(TextureSlot);
            RenderTarget.Dispose();
        }
    }
}
