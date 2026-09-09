using System.Numerics;
using System.Runtime.InteropServices;
using AcDream.App.Rendering.Gpu;
using AcDream.App.Rendering.Scene;
using AcDream.App.Rendering.Vfx;
using AcDream.App.Rendering.Wb;
using AcDream.Core.World;
using AcDream.Plugin.Abstractions.Rendering;

namespace AcDream.App.Rendering.Packs;

/// <summary>
/// API-v1 executor for declaration-only fullscreen graphs. It supports the
/// portable Tier-1 hooks/resources without recognizing a pack id or shader
/// filename. Scene replay and renderer-pipeline variants remain separate host
/// facilities and are rejected by the factory before this runtime is built.
/// </summary>
internal class DeclaredFullscreenRenderPackGraph :
    IAtmosphericWorldGraphRuntime,
    IRenderPackRuntimePerformanceSource,
    IRenderPackRuntimeDiagnosticsSource
{
    private readonly IGpuDevice _device;
    private readonly IDisposable _hdrLease;
    private readonly IGpuSampler _sampler;
    private readonly Node[] _nodes;
    private readonly IReadOnlyDictionary<string, RenderResourceDeclaration> _resources;
    private readonly PackSettingsUniforms _settings;
    private readonly DirectionalSunShadowRenderer? _directionalShadows;
    private readonly DirectionalShadowCasterFrame _shadowCasters = new();
    private readonly RenderPassDeclaration? _shadowPass;
    private readonly float _shadowStrength;
    private TargetSet? _targets;
    private RenderPackResourceBudget _resourceBudget;
    private long _resourceGeneration;
    private long _residentGpuBudgetBytes;
    private AtmosphericFrameInputs _lastInputs;
    private DirectionalSunShadowDiagnostics _lastShadowDiagnostics;
    private int _lastShadowCasterCount;
    private int _lastShadowClassificationCalls;
    private WbDrawDispatcher? _lastShadowWorldMeshes;
    private long _lastShadowFrameSerial = -1;
    private bool _renderedFrame;
    private bool _disposed;

    internal DeclaredFullscreenRenderPackGraph(
        IGpuDevice device,
        RenderPackDescriptor descriptor,
        IRenderPackAssets assets,
        RenderQualityPreset preset,
        IReadOnlyDictionary<string, string> userSettingOverrides)
        : this(
            device,
            descriptor,
            RenderPackShaderAssets.Validate(descriptor, assets),
            preset,
            userSettingOverrides)
    {
    }

    internal DeclaredFullscreenRenderPackGraph(
        IGpuDevice device,
        RenderPackDescriptor descriptor,
        ValidatedRenderPackShaderAssets assets,
        RenderQualityPreset preset,
        IReadOnlyDictionary<string, string> userSettingOverrides)
    {
        _device = device ?? throw new ArgumentNullException(nameof(device));
        Descriptor = descriptor ?? throw new ArgumentNullException(nameof(descriptor));
        ArgumentNullException.ThrowIfNull(assets);
        Preset = preset ?? throw new ArgumentNullException(nameof(preset));
        ArgumentNullException.ThrowIfNull(userSettingOverrides);
        if (device is not IGpuPipelineFormatVariantHost variants)
            throw new NotSupportedException("The active RHI cannot build an HDR world intermediate.");

        _resources = descriptor.Resources.ToDictionary(value => value.Id, StringComparer.OrdinalIgnoreCase);
        RenderPassDeclaration[] passes = descriptor.Passes
            .OrderBy(value => value.Hook)
            .ToArray();
        RenderPassDeclaration[] fullscreenPasses = passes
            .Where(static value =>
                value.Semantic != RenderPassSemantic.DirectionalShadowDepth)
            .ToArray();
        if (!fullscreenPasses.Any(value => value.Hook == RenderPassHook.ToneMap
                && value.ResourceWrites.Count == 0))
        {
            throw new NotSupportedException(
                $"Fullscreen pack '{descriptor.Id}' must declare a ToneMap pass that writes the output surface.");
        }

        IDisposable? lease = null;
        DirectionalSunShadowRenderer? directionalShadows = null;
        var nodes = new List<Node>(fullscreenPasses.Length);
        try
        {
            lease = variants.AcquirePipelineColorFormat(GpuTextureFormat.Rgba16FloatRenderTarget);
            _sampler = device.CreateSampler(GpuSamplerDescription.WorldClamp);
            foreach (RenderPassDeclaration pass in fullscreenPasses)
            {
                if (pass.Hook is not RenderPassHook.AtmosphereBeforeToneMap
                    and not RenderPassHook.ToneMap)
                    throw new NotSupportedException($"Fullscreen executor does not support hook '{pass.Hook}'.");
                RenderSemanticInput? unsupported = pass.SemanticInputs.FirstOrDefault(value =>
                    value is RenderSemanticInput.SceneNormals
                        or RenderSemanticInput.ShadowCasterTransforms
                        or RenderSemanticInput.DirectionalShadowMaps);
                if (unsupported is RenderSemanticInput.SceneNormals
                    or RenderSemanticInput.ShadowCasterTransforms
                    or RenderSemanticInput.DirectionalShadowMaps)
                {
                    throw new NotSupportedException(
                        $"Tier-1 fullscreen pass '{pass.Id}' requires unsupported semantic '{unsupported}'.");
                }
                if (pass.ResourceWrites.Count > 1)
                    throw new NotSupportedException($"Pass '{pass.Id}' writes more than one colour target.");
                GpuTextureFormat format = pass.ResourceWrites.Count == 0
                    ? GpuTextureFormat.Rgba8UnormRenderTarget
                    : ValidateOutput(Resource(pass.ResourceWrites[0]));
                var pipeline = device.CreatePipeline(new GpuPipelineDescription
                {
                    Name = $"render-pack-{descriptor.Id}-{pass.Id}",
                    Shaders = RenderPackShaderAssets.LoadPass(descriptor, assets, pass),
                    VertexLayout = GpuVertexLayout.None,
                    Blend = GpuBlendMode.None,
                    Depth = GpuDepthState.Disabled,
                    Cull = GpuCullMode.None,
                    ColorFormat = format,
                    AllowColorFormatVariants = false,
                    SampleCount = 1,
                    UsesRenderPackShaderAbi = true,
                });
                string timerName = $"render-pack-{descriptor.Id}-{pass.Id}";
                nodes.Add(new Node(
                    pass,
                    pipeline,
                    [.. RenderPackTextureBindingResolver.Resolve(pass, _resources)],
                    timerName));
            }
            _nodes = [.. nodes];
            _settings = PackSettingsUniforms.Create(
                descriptor,
                preset,
                userSettingOverrides);
            _shadowPass = passes.SingleOrDefault(static value =>
                value.Semantic == RenderPassSemantic.DirectionalShadowDepth);
            _shadowStrength = _shadowPass is null
                ? 0f
                : ReadSemanticSetting(
                    descriptor,
                    preset,
                    userSettingOverrides,
                    RenderSettingSemantic.DirectionalShadowStrength);
            if (_shadowPass is not null)
            {
                directionalShadows = new DirectionalSunShadowRenderer(
                    device,
                    ResolveShadowQuality(
                        descriptor,
                        preset,
                        userSettingOverrides),
                    RenderPackAtmospherePolicyEvaluation.NeutralDirectionalShadowElevation,
                    LoadDirectionalShadowShaders(descriptor, assets),
                    multiviewCascades: (preset.ExecutionHints
                        & RenderQualityExecutionHints
                            .MultiviewDirectionalShadowCascades) != 0);
            }
            _directionalShadows = directionalShadows;
            directionalShadows = null;
            _hdrLease = lease;
            lease = null;
        }
        catch
        {
            directionalShadows?.Dispose();
            for (int i = nodes.Count - 1; i >= 0; i--)
                nodes[i].Pipeline.Dispose();
            lease?.Dispose();
            throw;
        }
    }

    public RenderPackDescriptor Descriptor { get; }

    public RenderQualityPreset Preset { get; }

    internal IDirectionalShadowReceiverSource DeclaredDirectionalShadowReceivers =>
        _directionalShadows
        ?? throw new InvalidOperationException(
            $"Pack '{Descriptor.Id}' has no declared directional-shadow executor.");

    internal DirectionalSunShadowDiagnostics RenderDeclaredDirectionalShadows(
        IGpuFrame frame,
        in RenderFrameFoundation foundation,
        in WorldRenderFrame world,
        int activeDayGroup,
        in RenderSceneQuery scene,
        WbDrawDispatcher worldMeshes,
        TerrainModernRenderer terrain)
    {
        ObjectDisposedException.ThrowIf(_disposed, this);
        DirectionalSunShadowRenderer renderer = _directionalShadows
            ?? throw new InvalidOperationException(
                $"Pack '{Descriptor.Id}' has no declared directional-shadow executor.");
        _shadowCasters.Build(in scene);
        RetailLandscapeVisibilityFrame priorLandscapeVisibility =
            world.PriorLandscapeVisibility;
        _shadowCasters.Select(
            in priorLandscapeVisibility,
            world.DirectionalShadowCellMembership
                ?? EmptyDirectionalShadowCellMembership.Instance);
        AuthoredCelestialShadowSource source = world.CelestialShadowSource;
        float elevationStrength = RenderPackAtmospherePolicyEvaluation
            .DirectionalShadowFromSin(
            Descriptor.AtmospherePolicy!.DirectionalShadowLightElevationResponse,
            source.ElevationSin,
            fallback: 0f);
        var environment = new DirectionalShadowEnvironmentInput(
            PackEnabled: true,
            PortalOrLoginCoverVisible: foundation.PortalViewportVisible,
            PlayerInsideCell: world.Roots.PlayerOrCameraInsideEnclosedCell,
            source,
            foundation.Atmosphere,
            ActiveDayGroupMultiplier: Math.Clamp(
                EvaluateDayGroupPolicy(activeDayGroup)
                    * elevationStrength
                    * _shadowStrength,
                0f,
                1f));
        var input = new DirectionalSunShadowRenderInput(
            environment,
            world.Camera.Camera.View,
            world.Camera.Projection,
            _shadowCasters,
            ResidentMaximumReachMeters:
                world.ResidentStreamingWindow.MaximumReachMeters,
            PriorLandscapeVisibility: world.PriorLandscapeVisibility);
        _lastShadowCasterCount = _shadowCasters.Stats.ActiveSelected;
        _lastShadowClassificationCalls = _shadowCasters.Stats.TopologyRebuilt ? 1 : 0;
        _lastShadowDiagnostics = renderer.Render(
            frame,
            in input,
            worldMeshes,
            terrain);
        _lastShadowWorldMeshes = worldMeshes;
        _lastShadowFrameSerial = frame.Serial;
        RequireRetainedGpuBudget(renderer);
        return _lastShadowDiagnostics;
    }

    public IGpuRenderTarget PrepareWorldTarget(int width, int height, int sampleCount)
    {
        ObjectDisposedException.ThrowIf(_disposed, this);
        if (_targets is { } current
            && current.Width == width
            && current.Height == height
            && current.SampleCount == sampleCount)
            return current.World;
        RenderPackHostCapabilities capabilities =
            RenderPackCapabilityResolver.Resolve(_device.Capabilities);
        RenderPackResourceBudget budget = RenderPackResourceBudgetPlanner.RequireWithinHost(
            Descriptor,
            Preset,
            width,
            height,
            sampleCount,
            capabilities);
        TargetSet candidate = TargetSet.Create(
            _device,
            Descriptor,
            Preset,
            _sampler,
            width,
            height,
            sampleCount);
        TargetSet? prior = _targets;
        _targets = candidate;
        _resourceBudget = budget;
        _residentGpuBudgetBytes = RenderPackResidentBudget.Effective(
            Preset.MaxResidentGpuBytes,
            width,
            height,
            capabilities.MaxPackResidentBytes);
        _resourceGeneration = checked(_resourceGeneration + 1);
        _renderedFrame = false;
        prior?.Dispose();
        return candidate.World;
    }

    public void RenderPostProcess(IGpuFrame frame, in AtmosphericFrameInputs inputs)
    {
        ObjectDisposedException.ThrowIf(_disposed, this);
        TargetSet targets = _targets
            ?? throw new InvalidOperationException("PrepareWorldTarget must run before the fullscreen graph.");
        if (inputs.ViewportWidth != targets.Width || inputs.ViewportHeight != targets.Height)
            throw new InvalidOperationException("Fullscreen graph inputs and targets belong to different frames.");

        float elevationPolicy = EvaluateSunElevationPolicy(inputs.SunElevationDegrees);
        float dayGroupPolicy = EvaluateDayGroupPolicy(inputs.ActiveDayGroup);
        IReadOnlyList<SunElevationResponsePoint> shadowCurve =
            Descriptor.AtmospherePolicy?.DirectionalShadowLightElevationResponse ?? [];
        IReadOnlyList<SunElevationResponsePoint> volumetricCurve =
            Descriptor.AtmospherePolicy?.VolumetricShaftSunElevationResponse ?? [];
        float shadowElevationPolicy = shadowCurve.Count == 0
            ? elevationPolicy
            : RenderPackAtmospherePolicyEvaluation.DirectionalShadow(
                shadowCurve,
                inputs.SunElevationDegrees,
                elevationPolicy);
        float volumetricElevationPolicy = volumetricCurve.Count == 0
            ? 0f
            : RenderPackAtmospherePolicyEvaluation.VolumetricShaft(
                volumetricCurve,
                inputs.SunElevationDegrees,
                0f);
        float sunPolicy = EvaluateSunPolicy(
            inputs,
            elevationPolicy,
            dayGroupPolicy);
        var frameValues = new AtmosphericFrameUniforms(
            new Vector4(inputs.SunScreenUv, sunPolicy, inputs.SunElevationDegrees),
            new Vector4(inputs.SunColor, sunPolicy),
            new Vector4(targets.Width, targets.Height, 1f / targets.Width, 1f / targets.Height),
            new Vector4((float)inputs.Weather, inputs.WeatherIntensity,
                (float)Math.Clamp(inputs.DeltaSeconds, 0d, 1d), inputs.IsOutdoor ? 1f : 0f),
            new Vector4(inputs.SunDirection, inputs.SunDirectionalBrightness),
            new Vector4(
                inputs.ActiveDayGroup,
                dayGroupPolicy,
                shadowElevationPolicy,
                volumetricElevationPolicy),
            inputs.InverseViewProjection,
            Vector4.Zero,
            Vector4.Zero);
        GpuRingAllocation frameBlock = frame.AllocateRing(AtmosphericFrameUniforms.SizeInBytes, GpuRingUsage.Uniform);
        MemoryMarshal.Write(frameBlock.Data, in frameValues);
        GpuRingAllocation settingsBlock = frame.AllocateRing(PackSettingsUniforms.SizeInBytes, GpuRingUsage.Uniform);
        PackSettingsUniforms settings = _settings;
        MemoryMarshal.Write(settingsBlock.Data, in settings);

        foreach (Node node in _nodes)
            Draw(frame, node, targets, frameBlock, settingsBlock);
        _lastInputs = inputs;
        _renderedFrame = true;
    }

    public RenderPackRuntimeDiagnostics CaptureDiagnostics()
    {
        ObjectDisposedException.ThrowIf(_disposed, this);
        TargetSet? targets = _targets;
        if (!_renderedFrame || targets is null)
            return RenderPackRuntimeDiagnostics.Empty(Preset.Id);

        int shadowPassCount = _shadowPass is null ? 0 : 1;
        var passes = new RenderPackPassDiagnostics[_nodes.Length + shadowPassCount];
        int passIndex = 0;
        if (_shadowPass is not null)
        {
            passes[passIndex++] = new RenderPackPassDiagnostics(
                _shadowPass.Id,
                _lastShadowDiagnostics.LastResolvedGpuMilliseconds,
                _lastShadowDiagnostics.DrawCalls,
                DispatchCalls: 0);
        }
        for (int i = 0; i < _nodes.Length; i++)
        {
            Node node = _nodes[i];
            _device.Timers.TryResolve(node.TimerName, out double milliseconds);
            passes[passIndex++] = new RenderPackPassDiagnostics(
                node.Pass.Id,
                milliseconds,
                DrawCalls: 1,
                DispatchCalls: 0);
        }

        return new RenderPackRuntimeDiagnostics(
            Preset.Id,
            checked(
                _resourceBudget.RetainedGpuBytes
                + (_directionalShadows?.RetainedGpuBufferBytes ?? 0L)),
            _resourceBudget.MultisampleGpuBytes,
            targets.ImageCount + shadowPassCount,
            BufferCount: _directionalShadows?.RetainedGpuBufferCount ?? 0,
            DrawCalls: _nodes.Length + _lastShadowDiagnostics.DrawCalls,
            DispatchCalls: 0,
            ShadowCasterCount: _lastShadowCasterCount,
            CascadeDrawCount: _lastShadowDiagnostics.CascadeCount,
            CpuClassificationCalls: _lastShadowClassificationCalls,
            _lastInputs.SunElevationDegrees,
            _lastInputs.ActiveDayGroup,
            _lastInputs.Weather.ToString(),
            _lastInputs.WeatherIntensity,
            _lastInputs.IsOutdoor,
            DirectionalShadowStrength: _lastShadowDiagnostics.Strength,
            passes)
        {
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
        double gpuMilliseconds = 0d;
        bool resolved = _targets is not null;
        for (int i = 0; i < _nodes.Length; i++)
        {
            if (!_device.Timers.TryTakeResolved(
                    _nodes[i].TimerName,
                    out double milliseconds))
            {
                resolved = false;
            }
            else
            {
                gpuMilliseconds += milliseconds;
            }
        }
        if (_directionalShadows is not null)
        {
            if (!_device.Timers.TryTakeResolved(
                    RenderPackPerformanceScopeNames.EnhancedWorldReceiver,
                    out double receiverMilliseconds))
            {
                resolved = false;
            }
            else
            {
                gpuMilliseconds += receiverMilliseconds;
            }
            int shadowTimerCount = _directionalShadows.MultiviewCascadesEnabled
                && _lastShadowDiagnostics.CascadeCount > 0
                    ? 1
                    : _lastShadowDiagnostics.CascadeCount;
            for (int i = 0; i < shadowTimerCount; i++)
            {
                if (!_device.Timers.TryTakeResolved(
                        _directionalShadows.MultiviewCascadesEnabled
                            ? DirectionalSunShadowRenderer.MultiviewTimerName
                            : DirectionalSunShadowRenderer.TimerName(i),
                        out double milliseconds))
                {
                    resolved = false;
                }
                else
                {
                    gpuMilliseconds += milliseconds;
                }
            }
        }
        return new RenderPackRuntimePerformanceMetrics(
            _resourceGeneration,
            resolved,
            resolved ? gpuMilliseconds : 0d,
            checked(
                _resourceBudget.RetainedGpuBytes
                + (_directionalShadows?.RetainedGpuBufferBytes ?? 0L)),
            _resourceBudget.MultisampleGpuBytes);
    }

    private void RequireRetainedGpuBudget(
        DirectionalSunShadowRenderer renderer)
    {
        long total = checked(
            _resourceBudget.RetainedGpuBytes
            + renderer.RetainedGpuBufferBytes);
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
        _directionalShadows?.Dispose();
        for (int i = _nodes.Length - 1; i >= 0; i--)
            _nodes[i].Pipeline.Dispose();
        _hdrLease.Dispose();
    }

    private void Draw(
        IGpuFrame frame,
        Node node,
        TargetSet targets,
        GpuRingAllocation frameBlock,
        GpuRingAllocation settingsBlock)
    {
        IGpuRenderTarget? output = node.Pass.ResourceWrites.Count == 0
            ? null
            : targets.Resource(node.Pass.ResourceWrites[0]).Target;
        using IGpuPassEncoder encoder = frame.BeginPass(new GpuPassDescription
        {
            Name = node.TimerName,
            Color = new GpuColorAttachment(output, GpuLoadOp.Clear, GpuStoreOp.Store, Vector4.Zero),
            Depth = null,
            SampleCount = 1,
        });
        using IDisposable timer = encoder.BeginTimerScope(node.TimerName);
        encoder.BindPipeline(node.Pipeline);
        encoder.BindUniformBuffer(GpuBindingModel.UniformAtmosphericFrame,
            frameBlock.Buffer, frameBlock.OffsetBytes, AtmosphericFrameUniforms.SizeInBytes);
        GpuRingAllocation passBlock = frame.AllocateRing(
            AtmosphericPackPassUniforms.SizeInBytes,
            GpuRingUsage.Uniform);
        var zero = AtmosphericPackPassUniforms.From(Vector4.Zero);
        MemoryMarshal.Write(passBlock.Data, in zero);
        encoder.BindUniformBuffer(GpuBindingModel.UniformPackPass,
            passBlock.Buffer, passBlock.OffsetBytes, AtmosphericPackPassUniforms.SizeInBytes);
        encoder.BindUniformBuffer(GpuBindingModel.UniformPackSettings,
            settingsBlock.Buffer, settingsBlock.OffsetBytes, PackSettingsUniforms.SizeInBytes);

        Span<GpuTextureSlot> slots = stackalloc GpuTextureSlot[4];
        slots.Fill(GpuTextureSlot.Unassigned);
        for (int i = 0; i < node.Inputs.Length; i++)
            slots[i] = Resolve(node.Inputs[i], targets);
        GpuPushConstants push = GpuPushConstants.Default;
        push.TextureIndexA = slots[0].Index;
        push.TextureIndexB = slots[1].Index;
        push.ParamA = BitConverter.UInt32BitsToSingle(slots[2].Index);
        push.ParamB = BitConverter.UInt32BitsToSingle(slots[3].Index);
        encoder.SetPushConstants(in push);
        encoder.Draw(3, 1, 0, 0);
    }

    private static GpuTextureSlot Resolve(RenderPackTextureInput input, TargetSet targets)
    {
        if (input.Semantic is { } semantic)
        {
            return semantic switch
            {
                RenderSemanticInput.WorldColor => targets.WorldColor,
                RenderSemanticInput.SceneDepth => targets.WorldDepth,
                _ => throw new NotSupportedException($"Texture semantic '{semantic}' is unsupported by Tier-1."),
            };
        }
        return targets.Resource(input.ResourceId!).Slot;
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

    private static float EvaluateSunPolicy(
        in AtmosphericFrameInputs inputs,
        float elevationPolicy,
        float dayGroupPolicy)
    {
        if (!inputs.IsOutdoor || !inputs.SunIsOnScreen)
            return 0f;
        return Math.Clamp(
            elevationPolicy
                * dayGroupPolicy
                * EvaluateWeatherPolicy(inputs.Weather, inputs.WeatherIntensity),
            0f,
            4f);
    }

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
                .Single(value => value.Semantic == semantic);
            return RenderPackShaderAssets.LoadVariant(descriptor, assets, variant);
        }
    }

    private static DirectionalShadowQuality ResolveShadowQuality(
        RenderPackDescriptor descriptor,
        RenderQualityPreset preset,
        IReadOnlyDictionary<string, string> userSettingOverrides)
    {
        DirectionalShadowPreset shadowPreset = preset.Semantic switch
        {
            RenderQualitySemantic.Low => DirectionalShadowPreset.Low,
            RenderQualitySemantic.High => DirectionalShadowPreset.High,
            _ => DirectionalShadowPreset.Medium,
        };
        DirectionalShadowQuality quality = DirectionalShadowQuality.For(shadowPreset);
        RenderResourceDeclaration resource = descriptor.Resources.Single(value =>
            value.Semantic == RenderResourceSemantic.DirectionalShadowDepth);
        RenderExtentDeclaration extent = preset.ResourceOverrides.FirstOrDefault(value =>
                string.Equals(
                    value.ResourceId,
                    resource.Id,
                    StringComparison.OrdinalIgnoreCase))?.Extent
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
            RenderSettingSemantic.DirectionalShadowReachMetres);
        int taps = ReadShadowPcfTaps(descriptor, preset, userSettingOverrides);
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
        IReadOnlyDictionary<string, string> userSettingOverrides)
    {
        RenderSettingDeclaration setting = descriptor.Settings.Single(value =>
            value.Semantic == RenderSettingSemantic.DirectionalShadowPcfTaps);
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
        IReadOnlyDictionary<string, string> userSettingOverrides,
        RenderSettingSemantic semantic)
    {
        RenderSettingDeclaration setting = descriptor.Settings.Single(value =>
            value.Semantic == semantic);
        string value = RenderPackSettingResolution.Resolve(
            setting,
            preset,
            userSettingOverrides);
        if (!RenderPackSettingValueCodec.TryEncode(setting, value, out float encoded)
            || !float.IsFinite(encoded))
        {
            throw new NotSupportedException(
                $"Setting semantic '{semantic}' did not resolve to a finite value.");
        }
        return encoded;
    }

    private RenderResourceDeclaration Resource(string id) =>
        _resources.TryGetValue(id, out RenderResourceDeclaration? value)
            ? value
            : throw new InvalidOperationException($"Unknown render-pack resource '{id}'.");

    private static GpuTextureFormat FormatOf(RenderResourceDeclaration resource) => resource.Format switch
    {
        RenderFormatClass.HdrColor => GpuTextureFormat.Rgba16FloatRenderTarget,
        RenderFormatClass.LdrColor or RenderFormatClass.SingleChannel =>
            GpuTextureFormat.Rgba8UnormRenderTarget,
        _ => throw new NotSupportedException(
            $"Fullscreen resource '{resource.Id}' has unsupported format '{resource.Format}'."),
    };

    private static GpuTextureFormat ValidateOutput(RenderResourceDeclaration resource)
    {
        if (resource.Kind != RenderResourceKind.Image2D
            || (resource.Usage & RenderResourceUsage.ColorAttachment) == 0
            || resource.Extent is null)
        {
            throw new NotSupportedException(
                $"Fullscreen output '{resource.Id}' must be an extent-declared colour Image2D.");
        }
        return FormatOf(resource);
    }

    private sealed record Node(
        RenderPassDeclaration Pass,
        IGpuPipeline Pipeline,
        RenderPackTextureInput[] Inputs,
        string TimerName);

    private sealed class TargetSet : IDisposable
    {
        private readonly IGpuDevice _device;
        private readonly Dictionary<string, ResourceTarget> _resources;
        private readonly GpuTextureSlot[] _slots;
        private readonly string? _mainWorldResourceId;

        private TargetSet(
            IGpuDevice device,
            int width,
            int height,
            int sampleCount,
            IGpuRenderTarget world,
            GpuTextureSlot worldColor,
            GpuTextureSlot worldDepth,
            Dictionary<string, ResourceTarget> resources,
            GpuTextureSlot[] slots,
            string? mainWorldResourceId)
        {
            _device = device;
            Width = width;
            Height = height;
            SampleCount = sampleCount;
            World = world;
            WorldColor = worldColor;
            WorldDepth = worldDepth;
            _resources = resources;
            _slots = slots;
            _mainWorldResourceId = mainWorldResourceId;
        }

        internal int Width { get; }
        internal int Height { get; }
        internal int SampleCount { get; }
        internal IGpuRenderTarget World { get; }
        internal GpuTextureSlot WorldColor { get; }
        internal GpuTextureSlot WorldDepth { get; }
        internal int ImageCount => checked(
            2
            + _resources.Count
            + (SampleCount > 1 ? (WorldDepth.IsAssigned ? 2 : 1) : 0));

        internal ResourceTarget Resource(string id) =>
            string.Equals(id, _mainWorldResourceId, StringComparison.OrdinalIgnoreCase)
                ? new ResourceTarget(World, WorldColor)
                : _resources.TryGetValue(id, out ResourceTarget? value)
                    ? value
                    : throw new InvalidOperationException($"Resource '{id}' has no produced image.");

        internal static TargetSet Create(
            IGpuDevice device,
            RenderPackDescriptor descriptor,
            RenderQualityPreset preset,
            IGpuSampler sampler,
            int width,
            int height,
            int samples)
        {
            var targets = new List<IGpuRenderTarget>();
            var slots = new List<GpuTextureSlot>();
            try
            {
                bool needsDepth = descriptor.Passes.Any(pass =>
                    pass.SemanticInputs.Contains(RenderSemanticInput.SceneDepth));
                IGpuRenderTarget world = device.CreateRenderTarget(new GpuRenderTargetDescription(
                    $"render-pack-{descriptor.Id}-world-hdr", width, height,
                    GpuTextureFormat.Rgba16FloatRenderTarget,
                    GpuTextureFormat.Depth24Stencil8,
                    samples,
                    needsDepth));
                targets.Add(world);
                GpuTextureSlot worldColor = Register(device, world.ColorTexture, sampler, slots);
                GpuTextureSlot worldDepth = needsDepth
                    ? Register(device, world.DepthTexture!, sampler, slots)
                    : GpuTextureSlot.Unassigned;
                var resources = new Dictionary<string, ResourceTarget>(StringComparer.OrdinalIgnoreCase);
                string? mainWorldResourceId = descriptor.Resources.SingleOrDefault(resource =>
                    resource.Semantic == RenderResourceSemantic.MainWorldHdr)?.Id;
                HashSet<string> written = descriptor.Passes
                    .SelectMany(pass => pass.ResourceWrites)
                    .ToHashSet(StringComparer.OrdinalIgnoreCase);
                foreach (RenderResourceDeclaration resource in descriptor.Resources)
                {
                    if (!written.Contains(resource.Id)
                        || resource.Semantic is RenderResourceSemantic.MainWorldHdr
                            or RenderResourceSemantic.DirectionalShadowDepth)
                        continue;
                    if (resource.Kind != RenderResourceKind.Image2D
                        || (resource.Usage & RenderResourceUsage.ColorAttachment) == 0)
                        throw new NotSupportedException($"Fullscreen resource '{resource.Id}' is not a colour image.");
                    (int resourceWidth, int resourceHeight) = Extent(resource, preset, width, height);
                    IGpuRenderTarget target = device.CreateRenderTarget(new GpuRenderTargetDescription(
                        $"render-pack-{descriptor.Id}-{resource.Id}", resourceWidth, resourceHeight,
                        FormatOf(resource), null, 1));
                    targets.Add(target);
                    resources.Add(resource.Id, new ResourceTarget(
                        target,
                        Register(device, target.ColorTexture, sampler, slots)));
                }
                return new TargetSet(
                    device, width, height, samples, world, worldColor, worldDepth,
                    resources, [.. slots], mainWorldResourceId);
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
            for (int i = _slots.Length - 1; i >= 0; i--)
                _device.ReleaseTextureSlot(_slots[i]);
            foreach (ResourceTarget resource in _resources.Values.Reverse())
                resource.Target.Dispose();
            World.Dispose();
        }

        private static (int Width, int Height) Extent(
            RenderResourceDeclaration resource,
            RenderQualityPreset preset,
            int width,
            int height)
        {
            RenderExtentDeclaration extent = preset.ResourceOverrides.FirstOrDefault(value =>
                    string.Equals(value.ResourceId, resource.Id, StringComparison.OrdinalIgnoreCase))?.Extent
                ?? resource.Extent
                ?? throw new NotSupportedException($"Image resource '{resource.Id}' has no extent.");
            return extent.Mode switch
            {
                RenderExtentMode.AbsolutePixels =>
                    (checked((int)extent.Width), checked((int)extent.Height)),
                RenderExtentMode.RelativeToMainWorld or RenderExtentMode.RelativeToOutput =>
                    (Math.Max(1, (int)Math.Ceiling(width * extent.Width)),
                     Math.Max(1, (int)Math.Ceiling(height * extent.Height))),
                _ => throw new NotSupportedException($"Resource '{resource.Id}' has unsupported extent mode."),
            };
        }

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

    internal sealed record ResourceTarget(IGpuRenderTarget Target, GpuTextureSlot Slot);
}

internal sealed class DeclaredDirectionalShadowRenderPackGraph :
    DeclaredFullscreenRenderPackGraph,
    IDirectionalShadowWorldGraphRuntime
{
    internal DeclaredDirectionalShadowRenderPackGraph(
        IGpuDevice device,
        RenderPackDescriptor descriptor,
        IRenderPackAssets assets,
        RenderQualityPreset preset,
        IReadOnlyDictionary<string, string> userSettingOverrides)
        : base(device, descriptor, assets, preset, userSettingOverrides)
    {
    }

    internal DeclaredDirectionalShadowRenderPackGraph(
        IGpuDevice device,
        RenderPackDescriptor descriptor,
        ValidatedRenderPackShaderAssets assets,
        RenderQualityPreset preset,
        IReadOnlyDictionary<string, string> userSettingOverrides)
        : base(device, descriptor, assets, preset, userSettingOverrides)
    {
    }

    public IDirectionalShadowReceiverSource DirectionalShadowReceivers =>
        DeclaredDirectionalShadowReceivers;

    public DirectionalSunShadowDiagnostics RenderDirectionalShadows(
        IGpuFrame frame,
        in RenderFrameFoundation foundation,
        in WorldRenderFrame world,
        int activeDayGroup,
        in RenderSceneQuery scene,
        WbDrawDispatcher worldMeshes,
        TerrainModernRenderer terrain) => RenderDeclaredDirectionalShadows(
            frame,
            in foundation,
            in world,
            activeDayGroup,
            in scene,
            worldMeshes,
            terrain);
}
