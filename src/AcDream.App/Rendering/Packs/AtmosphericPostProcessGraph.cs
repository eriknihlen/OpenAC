using System.Collections.Frozen;
using System.Diagnostics;
using System.Numerics;
using System.Runtime.InteropServices;
using AcDream.App.Rendering.Gpu;
using AcDream.App.Rendering.Scene;
using AcDream.App.Rendering.Vfx;
using AcDream.App.Rendering.Wb;
using AcDream.Core.World;
using AcDream.Plugin.Abstractions.Rendering;

namespace AcDream.App.Rendering.Packs;

internal readonly record struct AtmosphericPostProcessSettings(
    float BloomStrength,
    float FilmicStrength,
    float Exposure,
    float Saturation,
    float Contrast,
    float VignetteStrength,
    float SunRayStrength)
{
    internal static AtmosphericPostProcessSettings Neutral { get; } = new(
        BloomStrength: 0f,
        FilmicStrength: 0f,
        Exposure: 1f,
        Saturation: 1f,
        Contrast: 1f,
        VignetteStrength: 0f,
        SunRayStrength: 0f);

    internal static AtmosphericPostProcessSettings FromDescriptor(
        RenderPackDescriptor descriptor,
        RenderQualityPreset preset,
        IReadOnlyDictionary<string, string>? userSettingOverrides = null)
    {
        ArgumentNullException.ThrowIfNull(descriptor);
        ArgumentNullException.ThrowIfNull(preset);
        return new AtmosphericPostProcessSettings(
            Read(descriptor, preset, userSettingOverrides, RenderSettingSemantic.BloomStrength, 0.65f),
            Read(descriptor, preset, userSettingOverrides, RenderSettingSemantic.FilmicStrength, 1f),
            Read(descriptor, preset, userSettingOverrides, RenderSettingSemantic.Exposure, 1f),
            Read(descriptor, preset, userSettingOverrides, RenderSettingSemantic.GradeSaturation, 1f),
            Read(descriptor, preset, userSettingOverrides, RenderSettingSemantic.GradeContrast, 1f),
            Read(descriptor, preset, userSettingOverrides, RenderSettingSemantic.VignetteStrength, AtmosphericPostProcessGraph.DefaultVignetteStrengthFallback),
            Read(descriptor, preset, userSettingOverrides, RenderSettingSemantic.SunRayStrength, 0.55f));
    }

    private static float Read(
        RenderPackDescriptor descriptor,
        RenderQualityPreset preset,
        IReadOnlyDictionary<string, string>? userSettingOverrides,
        RenderSettingSemantic semantic,
        float fallback)
    {
        RenderSettingDeclaration? setting = descriptor.Settings.FirstOrDefault(value =>
            value.Semantic == semantic);
        if (setting is null)
            return fallback;
        string value = RenderPackSettingResolution.Resolve(
            setting,
            preset,
            userSettingOverrides);
        return RenderPackSettingValueCodec.TryEncode(setting, value, out float encoded)
            ? encoded
            : fallback;
    }
}

internal readonly record struct FoliageWindSettings(
    bool Enabled,
    float Strength,
    float DirectionDegrees,
    float LeanMetres,
    float BranchMetres,
    float FlutterMetres,
    float CanopyHeightMetres)
{
    internal static FoliageWindSettings FromDescriptor(
        RenderPackDescriptor descriptor,
        RenderQualityPreset preset,
        IReadOnlyDictionary<string, string>? userSettingOverrides = null)
    {
        ArgumentNullException.ThrowIfNull(descriptor);
        ArgumentNullException.ThrowIfNull(preset);
        return new FoliageWindSettings(
            ReadBool(descriptor, preset, userSettingOverrides, RenderSettingSemantic.WindEnabled, true),
            Read(descriptor, preset, userSettingOverrides, RenderSettingSemantic.WindStrength, 1f),
            Read(descriptor, preset, userSettingOverrides, RenderSettingSemantic.WindDirectionDegrees, 225f),
            Read(descriptor, preset, userSettingOverrides, RenderSettingSemantic.WindLeanMetres, 0.25f),
            Read(descriptor, preset, userSettingOverrides, RenderSettingSemantic.WindBranchMetres, 0.15f),
            Read(descriptor, preset, userSettingOverrides, RenderSettingSemantic.WindFlutterMetres, 0.05f),
            Read(descriptor, preset, userSettingOverrides, RenderSettingSemantic.WindCanopyHeightMetres, 8f));
    }

    private static float Read(
        RenderPackDescriptor descriptor,
        RenderQualityPreset preset,
        IReadOnlyDictionary<string, string>? userSettingOverrides,
        RenderSettingSemantic semantic,
        float fallback)
    {
        RenderSettingDeclaration? setting = descriptor.Settings.FirstOrDefault(value =>
            value.Semantic == semantic);
        if (setting is null)
            return fallback;
        string value = RenderPackSettingResolution.Resolve(setting, preset, userSettingOverrides);
        return RenderPackSettingValueCodec.TryEncode(setting, value, out float encoded)
            ? encoded
            : fallback;
    }

    private static bool ReadBool(
        RenderPackDescriptor descriptor,
        RenderQualityPreset preset,
        IReadOnlyDictionary<string, string>? userSettingOverrides,
        RenderSettingSemantic semantic,
        bool fallback) =>
        Read(descriptor, preset, userSettingOverrides, semantic, fallback ? 1f : 0f) > 0.5f;
}

internal interface IAtmosphericWorldGraphRuntime : IRenderPackRuntime
{
    IGpuRenderTarget PrepareWorldTarget(int width, int height, int sampleCount);

    void RenderPostProcess(
        IGpuFrame frame,
        in AtmosphericFrameInputs inputs);
}

internal interface IDirectionalShadowWorldGraphRuntime :
    IAtmosphericWorldGraphRuntime
{
    IDirectionalShadowReceiverSource DirectionalShadowReceivers { get; }

    DirectionalSunShadowDiagnostics RenderDirectionalShadows(
        IGpuFrame frame,
        in RenderFrameFoundation foundation,
        in WorldRenderFrame world,
        int activeDayGroup,
        in RenderSceneQuery scene,
        WbDrawDispatcher worldMeshes,
        TerrainModernRenderer terrain);
}

internal sealed class AtmosphericPostProcessGraph :
    IDirectionalShadowWorldGraphRuntime,
    IRenderPackRuntimeDiagnosticsSource,
    IRenderPackRuntimePerformanceSource,
    IAtmosphericCpuStageProfileRuntime
{
    private readonly IGpuDevice _device;
    private readonly IDisposable _hdrPipelineLease;
    private readonly IGpuSampler _linearSampler;
    private readonly IGpuSampler _nearestSampler;
    private readonly IGpuPipeline _sunOcclusion;
    private readonly IGpuPipeline _sunRays;
    private readonly IGpuPipeline _bloomDownsample;
    private readonly IGpuPipeline _bloomBlur;
    private readonly IGpuPipeline _filmic;
    private readonly AtmosphericPostProcessSettings _settings;
    private readonly float _shadowStrength;
    private readonly PackSettingsUniforms _packSettings;
    private readonly FoliageWindSettings _foliageWind;
    private readonly IReadOnlySet<uint> _foliageWindExclusions;
    private float? _windClockSecondsOverride;
    private readonly System.Diagnostics.Stopwatch _windClock =
        System.Diagnostics.Stopwatch.StartNew();
    private long _windFrameSerial = -1;
    private float _windClockSeconds;
    private float _windLastAdvanceClockSeconds;
    private float _windMean;
    private float _windGust;
    private readonly DirectionalSunShadowRenderer _directionalShadows;
    private readonly VolumetricShaftRenderer? _volumetric;
    private readonly bool _fuseLowPostProcess;
    private readonly AtmosphericCpuStageProfiler? _cpuStageProfiler;
    private readonly DirectionalShadowCasterFrame _shadowCasters = new();
    private ulong _lastObservedSceneShadowRevision;
    private long _lastObservedAvailabilityVersion;
    private int _shadowRebuildDeferrals;
    private TargetSet? _targets;
    private AtmosphericFrameInputs _lastInputs;
    private DirectionalSunShadowDiagnostics _lastShadowDiagnostics;
    private int _lastShadowCasterCount;
    private int _lastShadowClassificationCalls;
    private long _lastShadowFrameSerial = -1;
    private WbDrawDispatcher? _lastShadowWorldMeshes;
    private AtmosphericCpuStageFrame _cpuStageFrame;
    private long _residentGpuBudgetBytes;
    private bool _renderedFrame;
    private bool _disposed;

    internal const float BloomThresholdLinear = 1f;
    /// <summary>Fallback when a descriptor declares no vignette setting; must equal the shipped declaration (pinned by AtmosphericColorPipelineTests).</summary>
    internal const float DefaultVignetteStrengthFallback = 0.245f;

    internal const float BloomKneeLinear = 0.73f;

    internal AtmosphericPostProcessGraph(
        IGpuDevice device,
        RenderPackDescriptor descriptor,
        IRenderPackAssets assets,
        RenderQualityPreset preset,
        AtmosphericPostProcessSettings? settings = null,
        IReadOnlyDictionary<string, string>? userSettingOverrides = null,
        float? windClockSecondsOverride = null)
        : this(
            device,
            descriptor,
            RenderPackShaderAssets.Validate(descriptor, assets),
            preset,
            settings,
            userSettingOverrides,
            windClockSecondsOverride)
    {
    }

    internal AtmosphericPostProcessGraph(
        IGpuDevice device,
        RenderPackDescriptor descriptor,
        ValidatedRenderPackShaderAssets assets,
        RenderQualityPreset preset,
        AtmosphericPostProcessSettings? settings = null,
        IReadOnlyDictionary<string, string>? userSettingOverrides = null,
        float? windClockSecondsOverride = null)
    {
        _device = device ?? throw new ArgumentNullException(nameof(device));
        Descriptor = descriptor ?? throw new ArgumentNullException(nameof(descriptor));
        ArgumentNullException.ThrowIfNull(assets);
        Preset = preset ?? throw new ArgumentNullException(nameof(preset));
        if (device is not IGpuPipelineFormatVariantHost variants)
        {
            throw new NotSupportedException(
                "The active RHI cannot prebuild HDR variants of the normal world pipelines.");
        }

        IDisposable? lease = null;
        DirectionalSunShadowRenderer? directionalShadows = null;
        VolumetricShaftRenderer? volumetric = null;
        var created = new List<IGpuPipeline>(capacity: 5);
        try
        {
            lease = variants.AcquirePipelineColorFormat(
                GpuTextureFormat.Rgba16FloatRenderTarget);
            _linearSampler = device.CreateSampler(GpuSamplerDescription.WorldClamp);
            _nearestSampler = device.CreateSampler(GpuSamplerDescription.UiNearest);
            _sunOcclusion = CreatePipeline(
                device,
                "atmospheric-sun-occlusion",
                ShaderSet(descriptor, assets, RenderPassSemantic.SunOcclusion),
                GpuTextureFormat.Rgba8UnormRenderTarget);
            created.Add(_sunOcclusion);
            _sunRays = CreatePipeline(
                device,
                "atmospheric-sun-rays",
                ShaderSet(descriptor, assets, RenderPassSemantic.SunRays),
                GpuTextureFormat.Rgba16FloatRenderTarget);
            created.Add(_sunRays);
            _bloomDownsample = CreatePipeline(
                device,
                "atmospheric-bloom-downsample",
                ShaderSet(descriptor, assets, RenderPassSemantic.BloomDownsample),
                GpuTextureFormat.Rgba16FloatRenderTarget);
            created.Add(_bloomDownsample);
            _bloomBlur = CreatePipeline(
                device,
                "atmospheric-bloom-blur",
                ShaderSet(descriptor, assets, RenderPassSemantic.BloomBlurHorizontal),
                GpuTextureFormat.Rgba16FloatRenderTarget);
            created.Add(_bloomBlur);
            _filmic = CreatePipeline(
                device,
                "atmospheric-filmic",
                ShaderSet(descriptor, assets, RenderPassSemantic.FilmicComposite),
                GpuTextureFormat.Rgba8UnormRenderTarget);
            created.Add(_filmic);
            _settings = settings
                ?? AtmosphericPostProcessSettings.FromDescriptor(
                    descriptor,
                    preset,
                    userSettingOverrides);
            _foliageWind = FoliageWindSettings.FromDescriptor(
                descriptor,
                preset,
                userSettingOverrides);
            _foliageWindExclusions =
                (descriptor.AtmospherePolicy?.FoliageExclusions
                    ?? (IReadOnlyList<uint>)[]).ToFrozenSet();
            _windClockSecondsOverride = windClockSecondsOverride;
            _packSettings = PackSettingsUniforms.Create(
                descriptor,
                preset,
                userSettingOverrides);
            _fuseLowPostProcess = (preset.ExecutionHints
                & RenderQualityExecutionHints.FusedAtmosphericPostProcess) != 0;
            _cpuStageProfiler = preset.Semantic is RenderQualitySemantic.Low
                ? new AtmosphericCpuStageProfiler()
                : null;
            _shadowStrength = ReadSemanticSetting(
                descriptor,
                preset,
                userSettingOverrides,
                RenderSettingSemantic.DirectionalShadowStrength,
                0.72f);
            directionalShadows = new DirectionalSunShadowRenderer(
                device,
                ResolveShadowQuality(
                    descriptor,
                    preset,
                    userSettingOverrides),
                atmospherePolicy:
                    RenderPackAtmospherePolicyEvaluation.NeutralDirectionalShadowElevation,
                pipelineShaders: LoadDirectionalShadowShaders(descriptor, assets),
                multiviewCascades: (preset.ExecutionHints
                    & RenderQualityExecutionHints
                        .MultiviewDirectionalShadowCascades) != 0);
            if (HasPass(descriptor, RenderPassSemantic.VolumetricShafts))
            {
                volumetric = new VolumetricShaftRenderer(
                    device,
                    descriptor,
                    assets,
                    preset,
                    userSettingOverrides);
            }
            _directionalShadows = directionalShadows;
            directionalShadows = null;
            _volumetric = volumetric;
            volumetric = null;
            _hdrPipelineLease = lease;
            lease = null;
        }
        catch
        {
            volumetric?.Dispose();
            directionalShadows?.Dispose();
            for (int i = created.Count - 1; i >= 0; i--)
                created[i].Dispose();
            lease?.Dispose();
            throw;
        }
    }

    public RenderPackDescriptor Descriptor { get; }

    public RenderQualityPreset Preset { get; }

    internal int ResourceGeneration { get; private set; }

    internal AtmosphericPostProcessSettings Settings => _settings;

    internal VolumetricShaftQuality? VolumetricQuality => _volumetric?.Quality;

    public IDirectionalShadowReceiverSource DirectionalShadowReceivers =>
        _directionalShadows;

    public DirectionalSunShadowDiagnostics RenderDirectionalShadows(
        IGpuFrame frame,
        in RenderFrameFoundation foundation,
        in WorldRenderFrame world,
        int activeDayGroup,
        in RenderSceneQuery scene,
        WbDrawDispatcher worldMeshes,
        TerrainModernRenderer terrain)
    {
        ObjectDisposedException.ThrowIf(_disposed, this);
        bool measureCpuStages = _cpuStageProfiler is not null
            && AtmosphericCpuStageProfiler.ShouldMeasure(frame.Serial);
        long stageStarted = measureCpuStages ? Stopwatch.GetTimestamp() : 0L;
        ulong sceneShadowRevision = scene.DirectionalShadowTopologyRevision;
        long availabilityVersion = worldMeshes.DirectionalShadowAvailabilityVersion;
        bool shadowInputsChanged =
            sceneShadowRevision != _lastObservedSceneShadowRevision
            || availabilityVersion != _lastObservedAvailabilityVersion;
        _lastObservedSceneShadowRevision = sceneShadowRevision;
        _lastObservedAvailabilityVersion = availabilityVersion;
        bool allowTopologyRebuild =
            !shadowInputsChanged || _shadowRebuildDeferrals >= 2;
        ulong casterSequenceBefore = _shadowCasters.BuildSequence;
        _shadowCasters.Build(in scene, allowTopologyRebuild);
        RetailLandscapeVisibilityFrame priorLandscapeVisibility =
            world.PriorLandscapeVisibility;
        _shadowCasters.Select(
            in priorLandscapeVisibility,
            world.DirectionalShadowCellMembership
                ?? EmptyDirectionalShadowCellMembership.Instance);
        if (_shadowCasters.BuildSequence != casterSequenceBefore)
            allowTopologyRebuild = true;
        _shadowRebuildDeferrals = allowTopologyRebuild
            ? 0
            : _shadowRebuildDeferrals + 1;
        long casterBuildFinished = measureCpuStages ? Stopwatch.GetTimestamp() : 0L;
        AuthoredCelestialShadowSource source = world.CelestialShadowSource;
        var environment = new DirectionalShadowEnvironmentInput(
            PackEnabled: true,
            PortalOrLoginCoverVisible: foundation.PortalViewportVisible,
            PlayerInsideCell: world.Roots.PlayerOrCameraInsideEnclosedCell,
            source,
            foundation.Atmosphere,
            ActiveDayGroupMultiplier: Math.Clamp(
                EvaluateDayGroupPolicy(activeDayGroup)
                    * RenderPackAtmospherePolicyEvaluation.DirectionalShadowFromSin(
                        Descriptor.AtmospherePolicy!
                            .DirectionalShadowLightElevationResponse,
                        source.ElevationSin)
                    * _shadowStrength,
                0f,
                1f));
        worldMeshes.FoliageWindExclusions = _foliageWindExclusions;
        bool isOutdoor = world.Roots.IsAtmosphericallyOutdoor;
        AtmosphericFrameBufferBinding shadowAtmosphericFrame =
            BuildShadowAtmosphericFrameBinding(frame, foundation.Atmosphere.Kind, isOutdoor);
        var input = new DirectionalSunShadowRenderInput(
            environment,
            world.Camera.Camera.View,
            world.Camera.Projection,
            _shadowCasters,
            ResidentMaximumReachMeters:
                world.ResidentStreamingWindow.MaximumReachMeters,
            MeasureGpuTimers: AtmosphericGpuTimerSampling.ShouldMeasure(
                Preset.Semantic,
                frame.Serial),
            MeasureCpuStages: measureCpuStages,
            AtmosphericFrame: shadowAtmosphericFrame,
            PriorLandscapeVisibility: world.PriorLandscapeVisibility,
            AllowTopologyRebuild: allowTopologyRebuild);
        long environmentFinished = measureCpuStages ? Stopwatch.GetTimestamp() : 0L;
        _lastShadowCasterCount = _shadowCasters.Stats.ActiveSelected;
        _lastShadowClassificationCalls = _shadowCasters.Stats.TopologyRebuilt ? 1 : 0;
        _lastShadowDiagnostics = _directionalShadows.Render(
            frame,
            in input,
            worldMeshes,
            terrain);
        _lastShadowWorldMeshes = worldMeshes;
        if (measureCpuStages)
        {
            DirectionalSunShadowCpuStageTicks shadow = _lastShadowDiagnostics.CpuStages;
            _cpuStageFrame = new AtmosphericCpuStageFrame(
                frame.Serial,
                casterBuildFinished - stageStarted,
                checked(environmentFinished - casterBuildFinished
                    + shadow.EnvironmentGateTicks),
                shadow.PreparedDrawsAndTransformsTicks,
                shadow.FitAndUniformTicks,
                shadow.LayeredPassRecordingTicks,
                shadow.BookkeepingTicks,
                0L,
                0L,
                0L);
        }
        else
        {
            _cpuStageFrame = default;
        }
        RequireRetainedGpuBudget();
        _lastShadowFrameSerial = frame.Serial;
        return _lastShadowDiagnostics;
    }

    private AtmosphericFrameBufferBinding BuildShadowAtmosphericFrameBinding(
        IGpuFrame frame,
        AcDream.Core.World.WeatherKind weather,
        bool isOutdoor)
    {
        (Vector4 clockWind, Vector4 windAmplitude) = ResolveFoliageWind(
            frame.Serial,
            weather,
            isOutdoor);
        GpuRingAllocation allocation = frame.AllocateRing(
            AtmosphericFrameUniforms.SizeInBytes,
            GpuRingUsage.Uniform);
        var uniforms = new AtmosphericFrameUniforms(
            Vector4.Zero,
            Vector4.Zero,
            Vector4.Zero,
            Vector4.Zero,
            Vector4.Zero,
            Vector4.Zero,
            Matrix4x4.Identity,
            clockWind,
            windAmplitude);
        MemoryMarshal.Write(allocation.Data, in uniforms);
        return new AtmosphericFrameBufferBinding(
            allocation.Buffer,
            allocation.OffsetBytes,
            (uint)AtmosphericFrameUniforms.SizeInBytes);
    }

    private (Vector4 ClockWind, Vector4 WindAmplitude) ResolveFoliageWind(
        long frameSerial,
        AcDream.Core.World.WeatherKind weather,
        bool isOutdoor)
    {
        float clockSeconds = _windClockSecondsOverride
            ?? (float)_windClock.Elapsed.TotalSeconds;
        if (_windFrameSerial != frameSerial)
        {
            (float targetMean, float targetGust) = RenderPackAtmospherePolicyEvaluation
                .FoliageWind(
                    Descriptor.AtmospherePolicy?.FoliageWindByWeather,
                    weather);
            targetMean *= _foliageWind.Strength;
            targetGust *= _foliageWind.Strength;

            if (_windFrameSerial == -1)
            {
                _windMean = targetMean;
                _windGust = targetGust;
            }
            else
            {
                float deltaSeconds = Math.Clamp(
                    clockSeconds - _windLastAdvanceClockSeconds,
                    0f,
                    1f);
                _windMean = RenderPackAtmospherePolicyEvaluation.EaseTowardTarget(
                    _windMean,
                    targetMean,
                    deltaSeconds,
                    AcDream.Core.World.WeatherSystem.TransitionSeconds);
                _windGust = RenderPackAtmospherePolicyEvaluation.EaseTowardTarget(
                    _windGust,
                    targetGust,
                    deltaSeconds,
                    AcDream.Core.World.WeatherSystem.TransitionSeconds);
            }

            _windClockSeconds = clockSeconds;
            _windLastAdvanceClockSeconds = clockSeconds;
            _windFrameSerial = frameSerial;
        }

        float gate = _foliageWind.Enabled && isOutdoor ? 1f : 0f;
        float directionRadians = _foliageWind.DirectionDegrees * (MathF.PI / 180f);
        var clockWind = new Vector4(
            _windClockSeconds,
            _windMean * gate,
            _windGust * gate,
            directionRadians);
        var windAmplitude = new Vector4(
            _foliageWind.LeanMetres,
            _foliageWind.BranchMetres,
            _foliageWind.FlutterMetres,
            _foliageWind.CanopyHeightMetres);
        return (clockWind, windAmplitude);
    }

    internal void SetWindClockSecondsOverrideForTesting(float seconds) =>
        _windClockSecondsOverride = seconds;

    public IGpuRenderTarget PrepareWorldTarget(
        int width,
        int height,
        int sampleCount)
    {
        ObjectDisposedException.ThrowIf(_disposed, this);
        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(width);
        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(height);
        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(sampleCount);
        if (_targets is { } current
            && current.Width == width
            && current.Height == height
            && current.SampleCount == sampleCount)
            return current.World;

        RenderPackHostCapabilities capabilities =
            RenderPackCapabilityResolver.Resolve(_device.Capabilities);
        RenderPackResourceBudgetPlanner.RequireWithinHost(
            Descriptor,
            Preset,
            width,
            height,
            sampleCount,
            capabilities);
        TargetSet candidate = TargetSet.Create(
            _device,
            width,
            height,
            sampleCount,
            PostScale(Descriptor, Preset),
            RayScale(Descriptor, Preset),
            allocateBloomIntermediates: !_fuseLowPostProcess,
            _linearSampler,
            _nearestSampler);
        try
        {
            _volumetric?.PrepareTarget(width, height);
        }
        catch
        {
            candidate.Dispose();
            throw;
        }
        TargetSet? previous = _targets;
        _targets = candidate;
        _residentGpuBudgetBytes = RenderPackResidentBudget.Effective(
            Preset.MaxResidentGpuBytes,
            width,
            height,
            capabilities.MaxPackResidentBytes);
        ResourceGeneration = checked(ResourceGeneration + 1);
        _cpuStageProfiler?.Reset();
        previous?.Dispose();
        return candidate.World;
    }

    public void RenderPostProcess(
        IGpuFrame frame,
        in AtmosphericFrameInputs inputs)
    {
        ObjectDisposedException.ThrowIf(_disposed, this);
        ArgumentNullException.ThrowIfNull(frame);
        TargetSet targets = _targets
            ?? throw new InvalidOperationException(
                "PrepareWorldTarget must succeed before post-processing begins.");
        if (inputs.ViewportWidth != targets.Width
            || inputs.ViewportHeight != targets.Height)
        {
            throw new InvalidOperationException(
                "Atmospheric inputs and target extent belong to different frames.");
        }
        if (_lastShadowFrameSerial != frame.Serial)
        {
            _lastShadowDiagnostics = default;
            _lastShadowCasterCount = 0;
            _lastShadowClassificationCalls = 0;
        }

        bool measureCpuStages = _cpuStageProfiler is not null
            && _cpuStageFrame.FrameSerial == frame.Serial;
        long postStarted = measureCpuStages ? Stopwatch.GetTimestamp() : 0L;

        float sunPolicy = EvaluateSunPolicy(inputs);
        float elevationPolicy = EvaluateSunElevationPolicy(inputs.SunElevationDegrees);
        float dayGroupPolicy = EvaluateDayGroupPolicy(inputs.ActiveDayGroup);
        float shadowElevationPolicy = RenderPackAtmospherePolicyEvaluation
            .DirectionalShadow(
                Descriptor.AtmospherePolicy?.DirectionalShadowLightElevationResponse,
                inputs.SunElevationDegrees,
                elevationPolicy);
        float volumetricElevationPolicy = RenderPackAtmospherePolicyEvaluation
            .VolumetricShaft(
                Descriptor.AtmospherePolicy?.VolumetricShaftSunElevationResponse,
                inputs.SunElevationDegrees);
        float rayStrength = inputs.SunIsOnScreen && inputs.IsOutdoor
            ? Math.Clamp(_settings.SunRayStrength * sunPolicy, 0f, 4f)
            : 0f;
        (Vector4 clockWind, Vector4 windAmplitude) = ResolveFoliageWind(
            frame.Serial,
            inputs.Weather,
            inputs.IsOutdoor);
        var frameUniforms = new AtmosphericFrameUniforms(
            new Vector4(
                inputs.SunScreenUv,
                rayStrength,
                inputs.SunElevationDegrees),
            new Vector4(inputs.SunColor, sunPolicy),
            new Vector4(
                targets.Width,
                targets.Height,
                1f / targets.Width,
                1f / targets.Height),
            new Vector4(
                (float)inputs.Weather,
                inputs.WeatherIntensity,
                (float)Math.Clamp(inputs.DeltaSeconds, 0d, 1d),
                inputs.IsOutdoor ? 1f : 0f),
            new Vector4(inputs.SunDirection, inputs.SunDirectionalBrightness),
            new Vector4(
                inputs.ActiveDayGroup,
                dayGroupPolicy,
                shadowElevationPolicy,
                volumetricElevationPolicy),
            inputs.InverseViewProjection,
            clockWind,
            windAmplitude);
        GpuRingAllocation frameBlock;
        GpuRingAllocation settingsBlock;
        GpuRingAllocation fusedSunPassBlock = default;
        GpuRingAllocation fusedFilmicPassBlock = default;
        if (_fuseLowPostProcess)
        {
            int alignment = checked((int)Math.Max(
                1u,
                _device.Capabilities.MinUniformBufferOffsetAlignment));
            int settingsOffset = AlignUp(
                AtmosphericFrameUniforms.SizeInBytes,
                alignment);
            int sunPassOffset = AlignUp(
                checked(settingsOffset + PackSettingsUniforms.SizeInBytes),
                alignment);
            int filmicPassOffset = AlignUp(
                checked(sunPassOffset + AtmosphericPackPassUniforms.SizeInBytes),
                alignment);
            GpuRingAllocation uniforms = frame.AllocateRing(
                checked(filmicPassOffset + AtmosphericPackPassUniforms.SizeInBytes),
                GpuRingUsage.Uniform);
            frameBlock = Slice(
                uniforms,
                offsetBytes: 0,
                AtmosphericFrameUniforms.SizeInBytes);
            settingsBlock = Slice(
                uniforms,
                settingsOffset,
                PackSettingsUniforms.SizeInBytes);
            fusedSunPassBlock = Slice(
                uniforms,
                sunPassOffset,
                AtmosphericPackPassUniforms.SizeInBytes);
            fusedFilmicPassBlock = Slice(
                uniforms,
                filmicPassOffset,
                AtmosphericPackPassUniforms.SizeInBytes);
        }
        else
        {
            frameBlock = frame.AllocateRing(
                AtmosphericFrameUniforms.SizeInBytes,
                GpuRingUsage.Uniform);
            settingsBlock = frame.AllocateRing(
                PackSettingsUniforms.SizeInBytes,
                GpuRingUsage.Uniform);
        }
        MemoryMarshal.Write(frameBlock.Data, in frameUniforms);
        PackSettingsUniforms packSettings = _packSettings;
        MemoryMarshal.Write(settingsBlock.Data, in packSettings);
        bool measureGpuTimers = AtmosphericGpuTimerSampling.ShouldMeasure(
            Preset.Semantic,
            frame.Serial);

        long sunRaysStarted = measureCpuStages ? Stopwatch.GetTimestamp() : 0L;
        if (!_fuseLowPostProcess)
        {
            DrawFullscreen(
                frame,
                "atmospheric-sun-occlusion",
                targets.SunMask,
                _sunOcclusion,
                targets.WorldDepthSlot,
                GpuTextureSlot.Unassigned,
                AtmosphericPackPassUniforms.From(Vector4.Zero),
                frameBlock,
                settingsBlock,
                GpuTextureSlot.Unassigned,
                GpuTextureSlot.Unassigned,
                measureGpuTimers);
        }
        var sunPassUniforms = new AtmosphericPackPassUniforms(
            new Vector4(0.965f, 0.24f, 0.82f, 48f),
            _fuseLowPostProcess
                ? new Vector4(
                    1f,
                    targets.SunRays.Description.Width,
                    targets.SunRays.Description.Height,
                    0f)
                : Vector4.Zero,
            Vector4.Zero,
            Vector4.Zero);
        if (_fuseLowPostProcess)
        {
            DrawFullscreenPrepared(
                frame,
                "atmospheric-sun-rays",
                targets.SunRays,
                _sunRays,
                targets.WorldDepthSlot,
                GpuTextureSlot.Unassigned,
                in sunPassUniforms,
                frameBlock,
                settingsBlock,
                fusedSunPassBlock,
                GpuTextureSlot.Unassigned,
                GpuTextureSlot.Unassigned,
                measureGpuTimers);
        }
        else
        {
            DrawFullscreen(
                frame,
                "atmospheric-sun-rays",
                targets.SunRays,
                _sunRays,
                targets.SunMaskSlot,
                GpuTextureSlot.Unassigned,
                in sunPassUniforms,
                frameBlock,
                settingsBlock,
                GpuTextureSlot.Unassigned,
                GpuTextureSlot.Unassigned,
                measureGpuTimers);
        }
        long sunRaysFinished = measureCpuStages ? Stopwatch.GetTimestamp() : 0L;
        DirectionalShadowFrameBinding shadowBinding =
            _directionalShadows.TryGetCurrentFrameBinding(frame, out var currentShadow)
                ? currentShadow
                : DirectionalShadowFrameBinding.Disabled;
        VolumetricShaftOutput volumetric = _volumetric is null
            ? default
            : _volumetric.Render(
                frame,
                in inputs,
                in shadowBinding,
                targets.WorldDepthSlot);
        if (!_fuseLowPostProcess)
        {
            DrawFullscreen(
                frame,
                "atmospheric-bloom-downsample",
                targets.BloomA,
                _bloomDownsample,
                targets.WorldColorSlot,
                targets.SunRaysSlot,
                AtmosphericPackPassUniforms.From(new Vector4(
                    _settings.BloomStrength,
                    BloomThresholdLinear,
                    BloomKneeLinear,
                    volumetric.HasTexture ? 1f : 0f)),
                frameBlock,
                settingsBlock,
                volumetric.TextureSlot,
                GpuTextureSlot.Unassigned,
                measureGpuTimers);
            DrawFullscreen(
                frame,
                "atmospheric-bloom-blur-horizontal",
                targets.BloomB,
                _bloomBlur,
                targets.BloomASlot,
                GpuTextureSlot.Unassigned,
                AtmosphericPackPassUniforms.From(new Vector4(
                    1f / targets.BloomA.Description.Width,
                    0f,
                    0f,
                    0f)),
                frameBlock,
                settingsBlock,
                GpuTextureSlot.Unassigned,
                GpuTextureSlot.Unassigned,
                measureGpuTimers);
            DrawFullscreen(
                frame,
                "atmospheric-bloom-blur-vertical",
                targets.BloomA,
                _bloomBlur,
                targets.BloomBSlot,
                GpuTextureSlot.Unassigned,
                AtmosphericPackPassUniforms.From(new Vector4(
                    0f,
                    1f / targets.BloomA.Description.Height,
                    0f,
                    0f)),
                frameBlock,
                settingsBlock,
                GpuTextureSlot.Unassigned,
                GpuTextureSlot.Unassigned,
                measureGpuTimers);
        }
        var filmicPassUniforms = new AtmosphericPackPassUniforms(
                new Vector4(
                    _settings.Exposure,
                    _settings.Saturation,
                    _settings.Contrast,
                    _settings.VignetteStrength),
                new Vector4(
                    _settings.FilmicStrength,
                    volumetric.HasTexture ? 1f : 0f,
                    _fuseLowPostProcess ? 1f : 0f,
                    0f),
                _fuseLowPostProcess
                    ? new Vector4(
                        _settings.BloomStrength,
                        BloomThresholdLinear,
                        BloomKneeLinear,
                        volumetric.HasTexture ? 1f : 0f)
                    : Vector4.Zero,
                _fuseLowPostProcess
                    ? new Vector4(
                        1f / targets.PostWidth,
                        1f / targets.PostHeight,
                        0f,
                        0f)
                    : Vector4.Zero);
        long filmicStarted = measureCpuStages ? Stopwatch.GetTimestamp() : 0L;
        if (_fuseLowPostProcess)
        {
            DrawFullscreenPrepared(
                frame,
                "atmospheric-filmic",
                target: null,
                _filmic,
                targets.WorldColorSlot,
                targets.SunRaysSlot,
                in filmicPassUniforms,
                frameBlock,
                settingsBlock,
                fusedFilmicPassBlock,
                volumetric.TextureSlot,
                GpuTextureSlot.Unassigned,
                measureGpuTimers);
        }
        else
        {
            DrawFullscreen(
                frame,
                "atmospheric-filmic",
                target: null,
                _filmic,
                targets.WorldColorSlot,
                targets.BloomASlot,
                in filmicPassUniforms,
                frameBlock,
                settingsBlock,
                targets.SunRaysSlot,
                volumetric.TextureSlot,
                measureGpuTimers);
        }
        long filmicFinished = measureCpuStages ? Stopwatch.GetTimestamp() : 0L;
        if (measureCpuStages)
        {
            _cpuStageFrame = _cpuStageFrame with
            {
                PostSetupAndOtherTicks = checked(
                    sunRaysStarted - postStarted
                    + filmicStarted - sunRaysFinished),
                PostSunRaysTicks = sunRaysFinished - sunRaysStarted,
                PostFilmicTicks = filmicFinished - filmicStarted,
            };
        }
        _lastInputs = inputs;
        _renderedFrame = true;
    }

    bool IAtmosphericCpuStageProfileRuntime.ShouldProfileCpuFrame(long frameSerial) =>
        _cpuStageProfiler is not null
        && AtmosphericCpuStageProfiler.ShouldMeasure(frameSerial);

    void IAtmosphericCpuStageProfileRuntime.CompleteCpuProfile(
        long frameSerial,
        long targetPreparationTicks,
        long measuredPackTotalTicks,
        long observeBookkeepingTicks,
        bool stableFrameBoundary)
    {
        if (!stableFrameBoundary
            || _cpuStageProfiler is null
            || _cpuStageFrame.FrameSerial != frameSerial)
        {
            return;
        }

        _cpuStageProfiler.Observe(
            in _cpuStageFrame,
            targetPreparationTicks,
            measuredPackTotalTicks,
            observeBookkeepingTicks);
    }

    public RenderPackRuntimeDiagnostics CaptureDiagnostics()
    {
        TargetSet? targets = _targets;
        if (!_renderedFrame || targets is null)
            return RenderPackRuntimeDiagnostics.Empty(Preset.Id);
        VolumetricShaftDiagnostics volumetric = _volumetric?.LastDiagnostics ?? default;
        (string Name, int DrawCalls)[] postPasses =
            (_fuseLowPostProcess, _volumetric is null) switch
            {
                (true, true) =>
                [
                    ("atmospheric-sun-rays", 1),
                    ("atmospheric-filmic", 1),
                ],
                (true, false) =>
                [
                    ("atmospheric-sun-rays", 1),
                    (VolumetricShaftRenderer.TimerName, volumetric.DrawCalls),
                    ("atmospheric-filmic", 1),
                ],
                (false, true) =>
                [
                    ("atmospheric-sun-occlusion", 1),
                    ("atmospheric-sun-rays", 1),
                    ("atmospheric-bloom-downsample", 1),
                    ("atmospheric-bloom-blur-horizontal", 1),
                    ("atmospheric-bloom-blur-vertical", 1),
                    ("atmospheric-filmic", 1),
                ],
                _ =>
                [
                    ("atmospheric-sun-occlusion", 1),
                    ("atmospheric-sun-rays", 1),
                    (VolumetricShaftRenderer.TimerName, volumetric.DrawCalls),
                    ("atmospheric-bloom-downsample", 1),
                    ("atmospheric-bloom-blur-horizontal", 1),
                    ("atmospheric-bloom-blur-vertical", 1),
                    ("atmospheric-filmic", 1),
                ],
            };
        int shadowPassCount = _directionalShadows.MultiviewCascadesEnabled
            && _lastShadowDiagnostics.CascadeCount > 0
                ? 1
                : _lastShadowDiagnostics.CascadeCount;
        const int receiverPassCount = 1;
        var passes = new RenderPackPassDiagnostics[
            receiverPassCount + postPasses.Length + shadowPassCount];
        _device.Timers.TryResolve(
            RenderPackPerformanceScopeNames.EnhancedWorldReceiver,
            out double receiverMilliseconds);
        passes[0] = new RenderPackPassDiagnostics(
            RenderPackPerformanceScopeNames.EnhancedWorldReceiver,
            receiverMilliseconds,
            DrawCalls: 0,
            DispatchCalls: 0);
        for (int i = 0; i < shadowPassCount; i++)
        {
            string name = _directionalShadows.MultiviewCascadesEnabled
                ? DirectionalSunShadowRenderer.MultiviewTimerName
                : DirectionalSunShadowRenderer.TimerName(i);
            _device.Timers.TryResolve(name, out double milliseconds);
            passes[receiverPassCount + i] = new RenderPackPassDiagnostics(
                name,
                milliseconds,
                DrawCalls: shadowPassCount == 0
                    ? 0
                    : _lastShadowDiagnostics.DrawCalls / shadowPassCount,
                DispatchCalls: 0);
        }
        for (int i = 0; i < postPasses.Length; i++)
        {
            (string name, int drawCalls) = postPasses[i];
            _device.Timers.TryResolve(name, out double milliseconds);
            passes[receiverPassCount + shadowPassCount + i] = new RenderPackPassDiagnostics(
                name,
                milliseconds,
                drawCalls,
                DispatchCalls: 0);
        }
        return new RenderPackRuntimeDiagnostics(
            Preset.Id,
            checked(
                targets.RetainedBytes
                + _directionalShadows.Quality.ApproximateDepthMapBytes
                + volumetric.RetainedGpuBytes
                + _directionalShadows.RetainedGpuBufferBytes),
            targets.TransientBytes,
            targets.ImageCount + 1 + (volumetric.RetainedGpuBytes > 0 ? 1 : 0),
            BufferCount: _directionalShadows.RetainedGpuBufferCount,
            DrawCalls: postPasses.Sum(pass => pass.DrawCalls)
                + _lastShadowDiagnostics.DrawCalls,
            DispatchCalls: 0,
            ShadowCasterCount: _lastShadowCasterCount,
            CascadeDrawCount: _lastShadowDiagnostics.CascadeCount,
            CpuClassificationCalls: _lastShadowClassificationCalls,
            _lastInputs.SunElevationDegrees,
            ActiveDayGroup: _lastInputs.ActiveDayGroup,
            _lastInputs.Weather.ToString(),
            _lastInputs.WeatherIntensity,
            _lastInputs.IsOutdoor,
            DirectionalShadowStrength: _lastShadowDiagnostics.Strength,
            passes)
        {
            CpuStages = _cpuStageProfiler?.Snapshot() ?? [],
            DirectionalShadowSourceKind = _lastShadowDiagnostics.SourceKind,
            DirectionalShadowSourceObjectIndex =
                _lastShadowDiagnostics.SourceObjectIndex,
            DirectionalShadowSourceGfxObjId =
                _lastShadowDiagnostics.SourceGfxObjId,
            DirectionalShadowSurfaceToLightDirection =
                _lastShadowDiagnostics.SurfaceToLightDirection,
            DirectionalShadowLightElevationSin =
                _lastShadowDiagnostics.LightElevationSin,
            ShadowTransformChurn = _lastShadowDiagnostics.TransformChurn,
            SharedWorldTransformUsedInstances =
                _lastShadowWorldMeshes is not null
                && _lastShadowWorldMeshes.HasDirectionalShadowTransformFrame(
                    _lastShadowFrameSerial)
                    ? _lastShadowWorldMeshes
                        .DirectionalShadowTransformFrameUsedInstances
                    : 0u,
        };
    }

    public RenderPackRuntimePerformanceMetrics CapturePerformanceMetrics()
    {
        ObjectDisposedException.ThrowIf(_disposed, this);
        TargetSet? targets = _targets;
        if (targets is null)
        {
            return new RenderPackRuntimePerformanceMetrics(
                ResourceGeneration,
                HasResolvedGpuMeasurement: false,
                InclusiveResolvedGpuMilliseconds: 0d,
                RetainedGpuBytes: _directionalShadows.Quality.ApproximateDepthMapBytes,
                TransientGpuBytes: 0L);
        }

        double gpuMilliseconds = 0d;
        bool resolved = true;
        resolved &= TryAddResolvedTimer(
            RenderPackPerformanceScopeNames.EnhancedWorldReceiver,
            ref gpuMilliseconds);
        if (!_fuseLowPostProcess)
        {
            resolved &= TryAddResolvedTimer(
                "atmospheric-sun-occlusion",
                ref gpuMilliseconds);
        }
        resolved &= TryAddResolvedTimer("atmospheric-sun-rays", ref gpuMilliseconds);
        if (!_fuseLowPostProcess)
        {
            resolved &= TryAddResolvedTimer(
                "atmospheric-bloom-downsample",
                ref gpuMilliseconds);
            resolved &= TryAddResolvedTimer(
                "atmospheric-bloom-blur-horizontal",
                ref gpuMilliseconds);
            resolved &= TryAddResolvedTimer(
                "atmospheric-bloom-blur-vertical",
                ref gpuMilliseconds);
        }
        resolved &= TryAddResolvedTimer("atmospheric-filmic", ref gpuMilliseconds);
        int shadowTimerCount = _directionalShadows.MultiviewCascadesEnabled
            && _lastShadowDiagnostics.CascadeCount > 0
                ? 1
                : _lastShadowDiagnostics.CascadeCount;
        for (int i = 0; i < shadowTimerCount; i++)
        {
            resolved &= TryAddResolvedTimer(
                _directionalShadows.MultiviewCascadesEnabled
                    ? DirectionalSunShadowRenderer.MultiviewTimerName
                    : DirectionalSunShadowRenderer.TimerName(i),
                ref gpuMilliseconds);
        }

        VolumetricShaftDiagnostics volumetric = _volumetric?.LastDiagnostics ?? default;
        if (volumetric.DrawCalls > 0)
        {
            resolved &= TryAddResolvedTimer(
                VolumetricShaftRenderer.TimerName,
                ref gpuMilliseconds);
        }

        return new RenderPackRuntimePerformanceMetrics(
            ResourceGeneration,
            resolved,
            resolved ? gpuMilliseconds : 0d,
            checked(
                targets.RetainedBytes
                + _directionalShadows.Quality.ApproximateDepthMapBytes
                + volumetric.RetainedGpuBytes
                + _directionalShadows.RetainedGpuBufferBytes),
            targets.TransientBytes);
    }

    private void RequireRetainedGpuBudget()
    {
        TargetSet? targets = _targets;
        if (targets is null)
            return;
        long total = checked(
            targets.RetainedBytes
            + _directionalShadows.Quality.ApproximateDepthMapBytes
            + (_volumetric?.LastDiagnostics.RetainedGpuBytes ?? 0L)
            + _directionalShadows.RetainedGpuBufferBytes);
        if (total <= _residentGpuBudgetBytes)
            return;
        throw new NotSupportedException(
            $"Render pack preset '{Preset.Id}' needs {total} resident GPU bytes "
            + "after materializing its scene-dependent shadow command buffers; "
            + $"the active pack budget is {_residentGpuBudgetBytes} bytes.");
    }

    public void Dispose()
    {
        if (_disposed)
            return;
        _disposed = true;
        _targets?.Dispose();
        _targets = null;
        _volumetric?.Dispose();
        _directionalShadows.Dispose();
        _filmic.Dispose();
        _bloomBlur.Dispose();
        _bloomDownsample.Dispose();
        _sunRays.Dispose();
        _sunOcclusion.Dispose();
        _hdrPipelineLease.Dispose();
    }

    internal float EvaluateSunPolicy(in AtmosphericFrameInputs inputs)
    {
        if (!inputs.IsOutdoor || !inputs.SunIsOnScreen)
            return 0f;
        return Math.Clamp(
            EvaluateSunElevationPolicy(inputs.SunElevationDegrees)
                * EvaluateDayGroupPolicy(inputs.ActiveDayGroup)
                * EvaluateWeatherPolicy(inputs.Weather, inputs.WeatherIntensity),
            0f,
            4f);
    }

    internal float EvaluateDirectionalShadowStrength(
        float sunElevationDegrees,
        int activeDayGroup) => Math.Clamp(
        RenderPackAtmospherePolicyEvaluation.DirectionalShadow(
            Descriptor.AtmospherePolicy?.DirectionalShadowLightElevationResponse,
            sunElevationDegrees)
            * EvaluateDayGroupPolicy(activeDayGroup)
            * _shadowStrength,
        0f,
        1f);

    private static float EvaluateWeatherPolicy(
        AcDream.Core.World.WeatherKind weather,
        float intensity)
    {
        float weatherTarget = weather switch
        {
            AcDream.Core.World.WeatherKind.Clear => 1f,
            AcDream.Core.World.WeatherKind.Overcast => 0.18f,
            AcDream.Core.World.WeatherKind.Rain => 0.10f,
            AcDream.Core.World.WeatherKind.Snow => 0.16f,
            AcDream.Core.World.WeatherKind.Storm => 0.06f,
            _ => 0f,
        };
        return 1f + ((weatherTarget - 1f) * Math.Clamp(intensity, 0f, 1f));
    }

    private bool TryAddResolvedTimer(string name, ref double total)
    {
        if (!_device.Timers.TryTakeResolved(name, out double milliseconds))
            return false;
        total += milliseconds;
        return true;
    }

    private float EvaluateSunElevationPolicy(float elevation)
    {
        IReadOnlyList<SunElevationResponsePoint>? points =
            Descriptor.AtmospherePolicy?.SunElevationResponse;
        return RenderPackAtmospherePolicyEvaluation.Ray(points, elevation);
    }

    private float EvaluateDayGroupPolicy(int activeDayGroup)
    {
        ActiveDayGroupMultiplier? value = Descriptor.AtmospherePolicy?
            .ActiveDayGroupMultipliers
            .FirstOrDefault(entry => entry.ActiveDayGroup == activeDayGroup);
        return value is null ? 1f : (float)value.Multiplier;
    }

    private static IGpuPipeline CreatePipeline(
        IGpuDevice device,
        string name,
        GpuShaderSet shaders,
        GpuTextureFormat colorFormat) =>
        device.CreatePipeline(new GpuPipelineDescription
        {
            Name = name,
            Shaders = shaders,
            VertexLayout = GpuVertexLayout.None,
            Blend = GpuBlendMode.None,
            Depth = GpuDepthState.Disabled,
            Cull = GpuCullMode.None,
            ColorFormat = colorFormat,
            AllowColorFormatVariants = false,
            SampleCount = 1,
            UsesRenderPackShaderAbi = true,
        });

    private static bool HasPass(
        RenderPackDescriptor descriptor,
        RenderPassSemantic semantic) =>
        descriptor.Passes.Any(pass => pass.Semantic == semantic);

    private static GpuShaderSet ShaderSet(
        RenderPackDescriptor descriptor,
        ValidatedRenderPackShaderAssets assets,
        RenderPassSemantic semantic)
    {
        RenderPassDeclaration pass = descriptor.Passes.FirstOrDefault(value =>
            value.Semantic == semantic)
            ?? throw new InvalidOperationException(
                $"Atmospheric graph requires declared pass semantic '{semantic}'.");
        return RenderPackShaderAssets.LoadPass(descriptor, assets, pass);
    }

    private static DirectionalShadowPipelineShaders LoadDirectionalShadowShaders(
        RenderPackDescriptor descriptor,
        ValidatedRenderPackShaderAssets assets)
    {
        DirectionalShadowPipelineShaders shaders = new(
            Variant(RenderPipelineVariantSemantic.TerrainDirectionalShadowCaster),
            Variant(RenderPipelineVariantSemantic.WorldOpaqueDirectionalShadowCaster),
            Variant(RenderPipelineVariantSemantic.WorldAlphaCutoutDirectionalShadowCaster),
            Variant(RenderPipelineVariantSemantic.TerrainDirectionalShadowReceiver),
            Variant(RenderPipelineVariantSemantic.WorldDirectionalShadowReceiver));
        if (descriptor.PipelineVariants.Any(value =>
                value.Semantic == RenderPipelineVariantSemantic.TerrainMultiviewDirectionalShadowCaster))
        {
            shaders = shaders with
            {
                MultiviewCasters = new DirectionalShadowMultiviewPipelineShaders(
                    Variant(RenderPipelineVariantSemantic.TerrainMultiviewDirectionalShadowCaster),
                    Variant(RenderPipelineVariantSemantic.WorldOpaqueMultiviewDirectionalShadowCaster),
                    Variant(RenderPipelineVariantSemantic.WorldAlphaCutoutMultiviewDirectionalShadowCaster)),
            };
        }
        return shaders;

        GpuShaderSet Variant(RenderPipelineVariantSemantic semantic)
        {
            PipelineVariantDeclaration variant = descriptor.PipelineVariants
                .FirstOrDefault(value => value.Semantic == semantic)
                ?? throw new InvalidOperationException(
                    $"Atmospheric graph requires declared pipeline-variant semantic '{semantic}'.");
            return RenderPackShaderAssets.LoadVariant(descriptor, assets, variant);
        }
    }

    private static DirectionalShadowPreset ShadowPreset(
        RenderQualityPreset preset) => preset.Semantic switch
        {
            RenderQualitySemantic.Low => DirectionalShadowPreset.Low,
            RenderQualitySemantic.High => DirectionalShadowPreset.High,
            _ => DirectionalShadowPreset.Medium,
        };

    private static DirectionalShadowQuality ResolveShadowQuality(
        RenderPackDescriptor descriptor,
        RenderQualityPreset preset,
        IReadOnlyDictionary<string, string>? userSettingOverrides)
    {
        DirectionalShadowQuality quality = DirectionalShadowQuality.For(
            ShadowPreset(preset));
        RenderResourceDeclaration resource = descriptor.Resources.Single(value =>
            value.Semantic == RenderResourceSemantic.DirectionalShadowDepth);
        RenderQualityResourceOverride? resourceOverride = preset.ResourceOverrides
            .FirstOrDefault(value => string.Equals(
                value.ResourceId,
                resource.Id,
                StringComparison.OrdinalIgnoreCase));
        RenderExtentDeclaration extent = resourceOverride?.Extent
            ?? resource.Extent
            ?? throw new NotSupportedException(
                "The DirectionalShadowDepth semantic resource has no image extent.");
        if (extent.Mode != RenderExtentMode.AbsolutePixels
            || extent.Width != extent.Height
            || extent.Width != Math.Truncate(extent.Width)
            || extent.Width is < 1 or > 16_384
            || extent.Layers is < 1 or > 4)
        {
            throw new NotSupportedException(
                "The DirectionalShadowDepth semantic resource must be a square "
                + "absolute 1..16384 image with 1..4 array layers.");
        }

        float reach = ReadSemanticSetting(
            descriptor,
            preset,
            userSettingOverrides,
            RenderSettingSemantic.DirectionalShadowReachMetres,
            quality.MaximumReachMeters);
        int taps = ReadShadowPcfTaps(
            descriptor,
            preset,
            userSettingOverrides,
            quality.PcfRadiusTexels switch
            {
                0 => 1,
                1 => 9,
                _ => 25,
            });
        int radius = taps switch
        {
            1 => 0,
            9 => 1,
            25 => 2,
            _ => throw new NotSupportedException(
                "DirectionalShadowPcfTaps must resolve to exactly 1, 9, or 25 samples."),
        };
        int resolution = checked((int)extent.Width);
        int cascades = extent.Layers;
        return quality with
        {
            CascadeCount = cascades,
            MapResolution = resolution,
            MaximumReachMeters = Math.Clamp(reach, 1f, 10_000f),
            PcfRadiusTexels = radius,
            ApproximateDepthMapBytes = checked(
                (long)cascades * resolution * resolution * sizeof(float)),
            IncrementalGpuP50BudgetMilliseconds = preset.MaxIncrementalGpuMillisecondsP50,
            IncrementalGpuP99BudgetMilliseconds = preset.MaxIncrementalGpuMillisecondsP99,
            IncrementalCpuP50BudgetMilliseconds = preset.MaxIncrementalCpuMillisecondsP50,
            IncrementalCpuP99BudgetMilliseconds = preset.MaxIncrementalCpuMillisecondsP99,
            PackResidentGpuByteBudget = preset.MaxResidentGpuBytes,
        };
    }

    private static int ReadShadowPcfTaps(
        RenderPackDescriptor descriptor,
        RenderQualityPreset preset,
        IReadOnlyDictionary<string, string>? userSettingOverrides,
        int fallback)
    {
        RenderSettingDeclaration? setting = descriptor.Settings.FirstOrDefault(value =>
            value.Semantic == RenderSettingSemantic.DirectionalShadowPcfTaps);
        if (setting is null)
            return fallback;

        string value = RenderPackSettingResolution.Resolve(
            setting,
            preset,
            userSettingOverrides);
        return int.TryParse(
            value,
            System.Globalization.NumberStyles.Integer,
            System.Globalization.CultureInfo.InvariantCulture,
            out int taps)
                ? taps
                : throw new NotSupportedException(
                    "DirectionalShadowPcfTaps must resolve to an integer sample count.");
    }

    private static float ReadSemanticSetting(
        RenderPackDescriptor descriptor,
        RenderQualityPreset preset,
        IReadOnlyDictionary<string, string>? userSettingOverrides,
        RenderSettingSemantic semantic,
        float fallback)
    {
        RenderSettingDeclaration? setting = descriptor.Settings.FirstOrDefault(value =>
            value.Semantic == semantic);
        if (setting is null)
            return fallback;
        string value = RenderPackSettingResolution.Resolve(
            setting,
            preset,
            userSettingOverrides);
        return RenderPackSettingValueCodec.TryEncode(setting, value, out float encoded)
            && float.IsFinite(encoded)
                ? encoded
                : fallback;
    }

    private static void DrawFullscreen(
        IGpuFrame frame,
        string name,
        IGpuRenderTarget? target,
        IGpuPipeline pipeline,
        GpuTextureSlot textureA,
        GpuTextureSlot textureB,
        in AtmosphericPackPassUniforms passUniforms,
        GpuRingAllocation frameBlock,
        GpuRingAllocation settingsBlock,
        GpuTextureSlot textureC,
        GpuTextureSlot textureD,
        bool measureGpuTimers)
    {
        GpuRingAllocation passBlock = frame.AllocateRing(
            AtmosphericPackPassUniforms.SizeInBytes,
            GpuRingUsage.Uniform);
        DrawFullscreenPrepared(
            frame,
            name,
            target,
            pipeline,
            textureA,
            textureB,
            in passUniforms,
            frameBlock,
            settingsBlock,
            passBlock,
            textureC,
            textureD,
            measureGpuTimers);
    }

    private static void DrawFullscreenPrepared(
        IGpuFrame frame,
        string name,
        IGpuRenderTarget? target,
        IGpuPipeline pipeline,
        GpuTextureSlot textureA,
        GpuTextureSlot textureB,
        in AtmosphericPackPassUniforms passUniforms,
        GpuRingAllocation frameBlock,
        GpuRingAllocation settingsBlock,
        GpuRingAllocation passBlock,
        GpuTextureSlot textureC,
        GpuTextureSlot textureD,
        bool measureGpuTimers)
    {
        using IGpuPassEncoder encoder = frame.BeginPass(new GpuPassDescription
        {
            Name = name,
            Color = new GpuColorAttachment(
                target,
                GpuLoadOp.Clear,
                GpuStoreOp.Store,
                Vector4.Zero),
            Depth = null,
            SampleCount = 1,
        });
        using IDisposable? timer = measureGpuTimers
            ? encoder.BeginTimerScope(name)
            : null;
        encoder.BindPipeline(pipeline);
        encoder.BindUniformBuffer(
            GpuBindingModel.UniformAtmosphericFrame,
            frameBlock.Buffer,
            frameBlock.OffsetBytes,
            AtmosphericFrameUniforms.SizeInBytes);
        MemoryMarshal.Write(passBlock.Data, in passUniforms);
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
        GpuPushConstants constants = GpuPushConstants.Default;
        constants.TextureIndexA = textureA.IsAssigned
            ? textureA.Index
            : GpuTextureSlot.Unassigned.Index;
        constants.TextureIndexB = textureB.IsAssigned
            ? textureB.Index
            : GpuTextureSlot.Unassigned.Index;
        constants.ParamA = BitConverter.UInt32BitsToSingle(
            textureC.IsAssigned ? textureC.Index : GpuTextureSlot.Unassigned.Index);
        constants.ParamB = BitConverter.UInt32BitsToSingle(
            textureD.IsAssigned ? textureD.Index : GpuTextureSlot.Unassigned.Index);
        encoder.SetPushConstants(in constants);
        encoder.Draw(3, 1, 0, 0);
    }

    private static GpuRingAllocation Slice(
        GpuRingAllocation allocation,
        int offsetBytes,
        int sizeBytes) => new(
        allocation.Buffer,
        checked(allocation.OffsetBytes + (uint)offsetBytes),
        allocation.Data.Slice(offsetBytes, sizeBytes));

    private static int AlignUp(int value, int alignment)
    {
        int remainder = value % alignment;
        return remainder == 0
            ? value
            : checked(value + alignment - remainder);
    }

    private static float PostScale(
        RenderPackDescriptor descriptor,
        RenderQualityPreset preset)
    {
        RenderQualityResourceOverride? value = preset.ResourceOverrides
            .FirstOrDefault(overrideValue =>
                ResourceSemantic(
                    descriptor,
                    overrideValue,
                    RenderResourceSemantic.BloomPing));
        if (value?.Extent is { } extent
            && extent.Mode == RenderExtentMode.RelativeToMainWorld)
            return (float)Math.Clamp(extent.Width, 0.125, 1.0);
        return preset.Semantic == RenderQualitySemantic.Low
            ? 0.25f
            : 0.5f;
    }

    private static float RayScale(
        RenderPackDescriptor descriptor,
        RenderQualityPreset preset)
    {
        RenderQualityResourceOverride? value = preset.ResourceOverrides
            .FirstOrDefault(overrideValue =>
                ResourceSemantic(
                    descriptor,
                    overrideValue,
                    RenderResourceSemantic.SunRays));
        if (value?.Extent is { } extent
            && extent.Mode == RenderExtentMode.RelativeToMainWorld)
            return (float)Math.Clamp(extent.Width, 0.125, 1.0);
        return preset.Semantic == RenderQualitySemantic.Low
            ? 0.25f
            : 0.5f;
    }

    private static bool ResourceSemantic(
        RenderPackDescriptor descriptor,
        RenderQualityResourceOverride value,
        RenderResourceSemantic semantic) =>
        descriptor.Resources.FirstOrDefault(resource => string.Equals(
            resource.Id,
            value.ResourceId,
            StringComparison.OrdinalIgnoreCase))?.Semantic == semantic;

    private sealed class TargetSet : IDisposable
    {
        private readonly IGpuDevice _device;
        private readonly GpuTextureSlot[] _slots;
        private bool _disposed;

        private TargetSet(
            IGpuDevice device,
            int width,
            int height,
            int sampleCount,
            IGpuRenderTarget world,
            IGpuRenderTarget? bloomA,
            IGpuRenderTarget? bloomB,
            IGpuRenderTarget sunMask,
            IGpuRenderTarget sunRays,
            int postWidth,
            int postHeight,
            GpuTextureSlot worldColorSlot,
            GpuTextureSlot worldDepthSlot,
            GpuTextureSlot bloomASlot,
            GpuTextureSlot bloomBSlot,
            GpuTextureSlot sunMaskSlot,
            GpuTextureSlot sunRaysSlot)
        {
            _device = device;
            Width = width;
            Height = height;
            SampleCount = sampleCount;
            World = world;
            BloomAOrNull = bloomA;
            BloomBOrNull = bloomB;
            SunMask = sunMask;
            SunRays = sunRays;
            PostWidth = postWidth;
            PostHeight = postHeight;
            WorldColorSlot = worldColorSlot;
            WorldDepthSlot = worldDepthSlot;
            BloomASlot = bloomASlot;
            BloomBSlot = bloomBSlot;
            SunMaskSlot = sunMaskSlot;
            SunRaysSlot = sunRaysSlot;
            _slots = bloomA is null
                ? [worldColorSlot, worldDepthSlot, sunMaskSlot, sunRaysSlot]
                : [worldColorSlot, worldDepthSlot, bloomASlot, bloomBSlot,
                    sunMaskSlot, sunRaysSlot];
        }

        internal int Width { get; }
        internal int Height { get; }
        internal int SampleCount { get; }
        internal IGpuRenderTarget World { get; }
        private IGpuRenderTarget? BloomAOrNull { get; }
        private IGpuRenderTarget? BloomBOrNull { get; }
        internal IGpuRenderTarget BloomA => BloomAOrNull
            ?? throw new InvalidOperationException(
                "The fused Low graph has no bloom ping intermediate.");
        internal IGpuRenderTarget BloomB => BloomBOrNull
            ?? throw new InvalidOperationException(
                "The fused Low graph has no bloom pong intermediate.");
        internal IGpuRenderTarget SunMask { get; }
        internal IGpuRenderTarget SunRays { get; }
        internal int PostWidth { get; }
        internal int PostHeight { get; }
        internal GpuTextureSlot WorldColorSlot { get; }
        internal GpuTextureSlot WorldDepthSlot { get; }
        internal GpuTextureSlot BloomASlot { get; }
        internal GpuTextureSlot BloomBSlot { get; }
        internal GpuTextureSlot SunMaskSlot { get; }
        internal GpuTextureSlot SunRaysSlot { get; }
        internal long RetainedBytes =>
            checked(
                (long)Width * Height * 12L
                + (BloomAOrNull is null
                    ? 0L
                    : (long)PostWidth * PostHeight * 16L)
                + ((long)SunMask.Description.Width * SunMask.Description.Height * 4L)
                + ((long)SunRays.Description.Width * SunRays.Description.Height * 8L));
        internal long TransientBytes => SampleCount > 1
            ? checked((long)Width * Height * 12L * SampleCount)
            : 0L;
        internal int ImageCount => (BloomAOrNull is null ? 4 : 6)
            + (SampleCount > 1 ? 2 : 0);

        internal static TargetSet Create(
            IGpuDevice device,
            int width,
            int height,
            int sampleCount,
            float postScale,
            float rayScale,
            bool allocateBloomIntermediates,
            IGpuSampler linear,
            IGpuSampler nearest)
        {
            var targets = new List<IGpuRenderTarget>(capacity: 5);
            var slots = new List<GpuTextureSlot>(capacity: 6);
            try
            {
                IGpuRenderTarget world = CreateTarget(
                    device,
                    "atmospheric-world-hdr",
                    width,
                    height,
                    GpuTextureFormat.Rgba16FloatRenderTarget,
                    GpuTextureFormat.Depth24Stencil8,
                    sampleCount,
                    sampleableDepth: true);
                targets.Add(world);
                int postWidth = Math.Max(1, (int)MathF.Ceiling(width * postScale));
                int postHeight = Math.Max(1, (int)MathF.Ceiling(height * postScale));
                int rayWidth = Math.Max(1, (int)MathF.Ceiling(width * rayScale));
                int rayHeight = Math.Max(1, (int)MathF.Ceiling(height * rayScale));
                IGpuRenderTarget? bloomA = null;
                IGpuRenderTarget? bloomB = null;
                if (allocateBloomIntermediates)
                {
                    bloomA = CreateTarget(
                        device, "atmospheric-bloom-a", postWidth, postHeight,
                        GpuTextureFormat.Rgba16FloatRenderTarget, null, 1, false);
                    targets.Add(bloomA);
                    bloomB = CreateTarget(
                        device, "atmospheric-bloom-b", postWidth, postHeight,
                        GpuTextureFormat.Rgba16FloatRenderTarget, null, 1, false);
                    targets.Add(bloomB);
                }
                IGpuRenderTarget sunMask = CreateTarget(
                    device, "atmospheric-sun-mask", rayWidth, rayHeight,
                    GpuTextureFormat.Rgba8UnormRenderTarget, null, 1, false);
                targets.Add(sunMask);
                IGpuRenderTarget sunRays = CreateTarget(
                    device, "atmospheric-sun-rays", rayWidth, rayHeight,
                    GpuTextureFormat.Rgba16FloatRenderTarget, null, 1, false);
                targets.Add(sunRays);

                GpuTextureSlot worldColor = Register(device, world.ColorTexture, linear, slots);
                GpuTextureSlot worldDepth = Register(
                    device,
                    world.DepthTexture
                        ?? throw new InvalidOperationException("The HDR world target exposed no sampled depth."),
                    nearest,
                    slots);
                GpuTextureSlot bloomASlot = bloomA is null
                    ? GpuTextureSlot.Unassigned
                    : Register(device, bloomA.ColorTexture, linear, slots);
                GpuTextureSlot bloomBSlot = bloomB is null
                    ? GpuTextureSlot.Unassigned
                    : Register(device, bloomB.ColorTexture, linear, slots);
                GpuTextureSlot sunMaskSlot = Register(device, sunMask.ColorTexture, linear, slots);
                GpuTextureSlot sunRaysSlot = Register(device, sunRays.ColorTexture, linear, slots);
                return new TargetSet(
                    device,
                    width,
                    height,
                    sampleCount,
                    world,
                    bloomA,
                    bloomB,
                    sunMask,
                    sunRays,
                    postWidth,
                    postHeight,
                    worldColor,
                    worldDepth,
                    bloomASlot,
                    bloomBSlot,
                    sunMaskSlot,
                    sunRaysSlot);
            }
            catch
            {
                for (int i = slots.Count - 1; i >= 0; i--)
                    device.ReleaseTextureSlot(slots[i]);
                for (int i = targets.Count - 1; i >= 0; i--)
                    targets[i].Dispose();
                throw;
            }
        }

        public void Dispose()
        {
            if (_disposed)
                return;
            _disposed = true;
            for (int i = _slots.Length - 1; i >= 0; i--)
                _device.ReleaseTextureSlot(_slots[i]);
            SunRays.Dispose();
            SunMask.Dispose();
            BloomBOrNull?.Dispose();
            BloomAOrNull?.Dispose();
            World.Dispose();
        }

        private static IGpuRenderTarget CreateTarget(
            IGpuDevice device,
            string name,
            int width,
            int height,
            GpuTextureFormat color,
            GpuTextureFormat? depth,
            int samples,
            bool sampleableDepth) =>
            device.CreateRenderTarget(new GpuRenderTargetDescription(
                name,
                width,
                height,
                color,
                depth,
                samples,
                sampleableDepth));

        private static GpuTextureSlot Register(
            IGpuDevice device,
            IGpuTexture texture,
            IGpuSampler sampler,
            List<GpuTextureSlot> slots)
        {
            GpuTextureSlot slot = device.RegisterTexture(texture, sampler);
            slots.Add(slot);
            return slot;
        }
    }
}

internal sealed class AtmosphericRenderPackRuntimeFactory(
    IGpuDevice device,
    float? skyPhaseSecondsOverride = null) :
    IRenderPackRuntimeFactory
{
    private readonly IGpuDevice _device = device
        ?? throw new ArgumentNullException(nameof(device));

    private readonly float? _skyPhaseSecondsOverride = skyPhaseSecondsOverride;

    public IRenderPackRuntime Build(
        RenderPackDescriptor descriptor,
        IRenderPackAssets assets,
        RenderQualityPreset preset,
        IReadOnlyDictionary<string, string> userSettingOverrides) =>
        Build(
            descriptor,
            RenderPackShaderAssets.Validate(descriptor, assets),
            preset,
            userSettingOverrides);

    public IRenderPackRuntime Build(
        RenderPackDescriptor descriptor,
        ValidatedRenderPackShaderAssets assets,
        RenderQualityPreset preset,
        IReadOnlyDictionary<string, string> userSettingOverrides)
    {
        ArgumentNullException.ThrowIfNull(descriptor);
        ArgumentNullException.ThrowIfNull(assets);
        ArgumentNullException.ThrowIfNull(preset);
        ArgumentNullException.ThrowIfNull(userSettingOverrides);
        if (descriptor.Passes.Count == 0)
        {
            if (descriptor.SceneReplays.Count != 0 || descriptor.PipelineVariants.Count != 0)
            {
                throw new NotSupportedException(
                    $"Pack '{descriptor.Id}' declares scene replay or pipeline variants without an executable pass.");
            }
            return new NoOpRenderPackRuntime(descriptor, preset);
        }

        RenderPassSemantic[] atmosphericSemanticPasses =
        [
            RenderPassSemantic.DirectionalShadowDepth,
            RenderPassSemantic.SunOcclusion,
            RenderPassSemantic.SunRays,
            RenderPassSemantic.VolumetricShafts,
            RenderPassSemantic.BloomDownsample,
            RenderPassSemantic.BloomBlurHorizontal,
            RenderPassSemantic.BloomBlurVertical,
            RenderPassSemantic.FilmicComposite,
        ];
        bool standardAtmosphericGraph = atmosphericSemanticPasses.All(required =>
            descriptor.Passes.Count(pass => pass.Semantic == required) == 1);
        if (standardAtmosphericGraph)
        {
            return new AtmosphericPostProcessGraph(
                _device,
                descriptor,
                assets,
                preset,
                userSettingOverrides: userSettingOverrides,
                windClockSecondsOverride: _skyPhaseSecondsOverride);
        }

        bool declaredDirectionalShadowGraph = descriptor.Passes.Count(pass =>
                pass.Semantic == RenderPassSemantic.DirectionalShadowDepth) == 1
            && descriptor.Passes.All(pass =>
                pass.Semantic is RenderPassSemantic.CustomFullscreen
                    or RenderPassSemantic.DirectionalShadowDepth);
        if (declaredDirectionalShadowGraph)
        {
            return new DeclaredDirectionalShadowRenderPackGraph(
                _device,
                descriptor,
                assets,
                preset,
                userSettingOverrides);
        }

        if (descriptor.SceneReplays.Count != 0 || descriptor.PipelineVariants.Count != 0)
        {
            throw new NotSupportedException(
                $"Pack '{descriptor.Id}' uses scene replay or renderer-pipeline variants "
                + "without a host semantic executor.");
        }
        if (descriptor.Passes.Any(pass => pass.Hook is
            RenderPassHook.ShadowDepthBeforeWorld or
            RenderPassHook.AfterToneMapBeforePrivateViewports))
        {
            throw new NotSupportedException(
                $"Pack '{descriptor.Id}' uses a pass hook outside the API-v1 Tier-1 fullscreen executor.");
        }
        return new DeclaredFullscreenRenderPackGraph(
            _device,
            descriptor,
            assets,
            preset,
            userSettingOverrides);
    }
}

internal sealed class NoOpRenderPackRuntime(
    RenderPackDescriptor descriptor,
    RenderQualityPreset preset) : IDefaultWorldPathRenderPackRuntime
{
    public RenderPackDescriptor Descriptor { get; } = descriptor
        ?? throw new ArgumentNullException(nameof(descriptor));

    public RenderQualityPreset Preset { get; } = preset
        ?? throw new ArgumentNullException(nameof(preset));

    public void Dispose()
    {
    }
}
