using System.Buffers.Binary;
using AcDream.Plugin.Abstractions.Rendering;

namespace AcDream.App.Rendering.Packs;

internal sealed record RenderPackHostCapabilities(
    IReadOnlySet<RenderCapability> Available,
    int MaxImageDimension2D,
    int MaxImageArrayLayers,
    long MaxPackResidentBytes,
    long MaxPackTransientBytes = 512L * 1024 * 1024,
    string MemoryPolicyDescription = "API-v1 conformance ceiling")
{
    internal static RenderPackHostCapabilities Conformance { get; } = new(
        Enum.GetValues<RenderCapability>().ToHashSet(),
        MaxImageDimension2D: 16_384,
        MaxImageArrayLayers: 256,
        MaxPackResidentBytes: 256L * 1024 * 1024,
        MaxPackTransientBytes: 512L * 1024 * 1024);
}

internal readonly record struct RenderPackValidationResult(
    bool Success,
    string? Reason)
{
    internal static RenderPackValidationResult Valid() => new(true, null);

    internal static RenderPackValidationResult Invalid(string reason) =>
        new(false, reason);
}

internal static class RenderPackValidator
{
    private const long AbsolutePackByteCeiling = 256L * 1024 * 1024;
    private const int AbsoluteImageDimension2DCeiling = 16_384;
    private const int AbsoluteImageArrayLayerCeiling = 256;
    private const int MaximumShaderBytes = RenderPackShaderAbi.MaximumShaderAssetBytes;
    private const uint SpirvMagic = 0x0723_0203u;

    internal static RenderPackValidationResult ValidateDescriptor(
        RenderPackDescriptor? descriptor,
        RenderPackHostCapabilities capabilities)
    {
        ArgumentNullException.ThrowIfNull(capabilities);
        if (descriptor is null)
            return Invalid("The pack descriptor is missing.");
        if (!IsStableId(descriptor.Id))
            return Invalid("The pack id must be a stable lowercase logical id.");
        if (string.IsNullOrWhiteSpace(descriptor.DisplayName))
            return Invalid($"Pack '{descriptor.Id}' has no display name.");
        if (string.IsNullOrWhiteSpace(descriptor.FeatureSummary))
            return Invalid($"Pack '{descriptor.Id}' has no feature summary.");
        if (descriptor.PackVersion is null)
            return Invalid($"Pack '{descriptor.Id}' has no version.");
        if (!RenderPackApi.IsSupported(descriptor.PackApiVersion))
        {
            return Invalid(
                $"Pack '{descriptor.Id}' requires render-pack API "
                + $"{descriptor.PackApiVersion}; this client supports "
                + $"{RenderPackApi.MinimumSupported}..{RenderPackApi.Current}.");
        }

        string? nullList = FirstNullList(descriptor);
        if (nullList is not null)
            return Invalid($"Pack '{descriptor.Id}' has a null {nullList} declaration list.");

        foreach (RenderCapability required in descriptor.RequiredCapabilities)
        {
            if (!capabilities.Available.Contains(required))
            {
                return Invalid(
                    $"Pack '{descriptor.Id}' requires unsupported capability "
                    + $"'{required}'.");
            }
        }

        RenderPackValidationResult semanticCapabilities =
            ValidateSemanticCapabilities(descriptor, capabilities);
        if (!semanticCapabilities.Success)
            return semanticCapabilities;

        RenderPackValidationResult ids = ValidateUniqueIds(descriptor);
        if (!ids.Success)
            return ids;
        RenderPackValidationResult resources = ValidateResources(descriptor, capabilities);
        if (!resources.Success)
            return resources;
        RenderPackValidationResult passes = ValidatePasses(descriptor);
        if (!passes.Success)
            return passes;
        RenderPackValidationResult replays = ValidateReplays(descriptor);
        if (!replays.Success)
            return replays;
        RenderPackValidationResult variants = ValidateVariants(descriptor);
        if (!variants.Success)
            return variants;
        RenderPackValidationResult settings = ValidateSettings(descriptor);
        if (!settings.Success)
            return settings;
        RenderPackValidationResult presets = ValidatePresets(descriptor, capabilities);
        if (!presets.Success)
            return presets;
        RenderPackValidationResult semantics = ValidateSemanticRoles(descriptor);
        if (!semantics.Success)
            return semantics;
        return ValidateAtmosphere(descriptor);
    }

    internal static RenderPackValidationResult ValidateSelectedAssets(
        RenderPackDescriptor descriptor,
        IRenderPackAssets assets) =>
        ValidateSelectedAssets(descriptor, assets, out _);

    internal static RenderPackValidationResult ValidateSelectedAssets(
        RenderPackDescriptor descriptor,
        IRenderPackAssets assets,
        out ValidatedRenderPackShaderAssets? validatedAssets)
    {
        ArgumentNullException.ThrowIfNull(descriptor);
        ArgumentNullException.ThrowIfNull(assets);
        validatedAssets = null;

        ShaderValidationRequest[] requests = descriptor.Passes
            .SelectMany(static pass => new[]
            {
                new ShaderValidationRequest(
                    pass.VertexShaderAsset, RenderPackShaderStage.Vertex, pass, null),
                new ShaderValidationRequest(
                    pass.FragmentShaderAsset, RenderPackShaderStage.Fragment, pass, null),
            })
            .Concat(descriptor.PipelineVariants.SelectMany(static variant => new[]
            {
                new ShaderValidationRequest(
                    variant.VertexShaderAsset, RenderPackShaderStage.Vertex, null, variant),
                new ShaderValidationRequest(
                    variant.FragmentShaderAsset, RenderPackShaderStage.Fragment, null, variant),
            }))
            .ToArray();

        byte[] readBuffer = new byte[4096];
        var validated = new Dictionary<string, byte[]>(StringComparer.Ordinal);
        foreach (IGrouping<string, ShaderValidationRequest> group in
            requests.GroupBy(static request => request.Key, StringComparer.Ordinal))
        {
            string key = group.Key;
            if (!IsSafeAssetKey(key))
                return Invalid($"Pack '{descriptor.Id}' declares unsafe asset key '{key}'.");

            try
            {
                using Stream stream = assets.OpenRead(key);
                if (stream is null || !stream.CanRead)
                    return Invalid($"Pack '{descriptor.Id}' asset '{key}' is not readable.");
                using var destination = new MemoryStream();
                while (true)
                {
                    int count = stream.Read(readBuffer);
                    if (count == 0)
                        break;
                    if (destination.Length + count > MaximumShaderBytes)
                    {
                        return Invalid(
                            $"Pack '{descriptor.Id}' asset '{key}' exceeds "
                            + $"the {MaximumShaderBytes}-byte shader ceiling.");
                    }
                    destination.Write(readBuffer, 0, count);
                }

                byte[] spirv = destination.ToArray();
                if (spirv.Length < 4
                    || (spirv.Length & 3) != 0
                    || BinaryPrimitives.ReadUInt32LittleEndian(spirv) != SpirvMagic)
                {
                    return Invalid(
                        $"Pack '{descriptor.Id}' asset '{key}' is not valid SPIR-V.");
                }

                foreach (ShaderValidationRequest request in group)
                {
                    RenderPackSpirvValidationResult validation = request.Pass is not null
                        ? RenderPackSpirvValidator.ValidatePassShader(
                            spirv, request.Stage, request.Pass)
                        : RenderPackSpirvValidator.ValidatePipelineVariantShader(
                            spirv, request.Stage, request.Variant!);
                    if (!validation.Success)
                    {
                        return Invalid(
                            $"Pack '{descriptor.Id}' asset '{key}' fails render-pack shader ABI v1: "
                            + validation.Reason + ".");
                    }
                }
                validated.Add(key, spirv);
            }
            catch (Exception error) when (error is not OutOfMemoryException)
            {
                return Invalid(
                    $"Pack '{descriptor.Id}' asset '{key}' could not be opened: "
                    + error.GetBaseException().Message);
            }
        }

        validatedAssets = new ValidatedRenderPackShaderAssets(validated);
        return RenderPackValidationResult.Valid();
    }

    private sealed record ShaderValidationRequest(
        string Key,
        RenderPackShaderStage Stage,
        RenderPassDeclaration? Pass,
        PipelineVariantDeclaration? Variant);

    private static RenderPackValidationResult ValidateSemanticRoles(
        RenderPackDescriptor descriptor)
    {
        RenderPackValidationResult unique = UniqueNonCustomSemantics(
            descriptor,
            descriptor.Resources,
            static value => value.Semantic,
            RenderResourceSemantic.Custom,
            "resource");
        if (!unique.Success) return unique;
        unique = UniqueNonCustomSemantics(
            descriptor,
            descriptor.Passes,
            static value => value.Semantic,
            RenderPassSemantic.CustomFullscreen,
            "pass");
        if (!unique.Success) return unique;
        unique = UniqueNonCustomSemantics(
            descriptor,
            descriptor.PipelineVariants,
            static value => value.Semantic,
            RenderPipelineVariantSemantic.Custom,
            "pipeline variant");
        if (!unique.Success) return unique;
        unique = UniqueNonCustomSemantics(
            descriptor,
            descriptor.QualityPresets,
            static value => value.Semantic,
            RenderQualitySemantic.Custom,
            "quality preset");
        if (!unique.Success) return unique;
        unique = UniqueNonCustomSemantics(
            descriptor,
            descriptor.Settings,
            static value => value.Semantic,
            RenderSettingSemantic.Custom,
            "setting");
        if (!unique.Success) return unique;

        RenderSettingDeclaration? automaticSetting = descriptor.Settings.FirstOrDefault(
            static value => value.Semantic == RenderSettingSemantic.AutomaticQuality);
        if (automaticSetting is not null && automaticSetting.Kind != RenderSettingKind.Boolean)
        {
            return Invalid(
                $"Pack '{descriptor.Id}' AutomaticQuality setting must be Boolean.");
        }
        if (automaticSetting is not null
            || descriptor.QualityPresets.Any(static value =>
                value.Semantic == RenderQualitySemantic.Automatic))
        {
            foreach (RenderQualitySemantic semantic in new[]
            {
                RenderQualitySemantic.Low,
                RenderQualitySemantic.Medium,
                RenderQualitySemantic.High,
            })
            {
                if (!descriptor.QualityPresets.Any(value =>
                        value.Semantic == semantic && value.AutoEligible))
                {
                    return Invalid(
                        $"Pack '{descriptor.Id}' declares Automatic quality but has no "
                        + $"AutoEligible '{semantic}' semantic preset.");
                }
            }
        }

        bool atmosphericExecutor = descriptor.Passes.Any(static value =>
            value.Semantic != RenderPassSemantic.CustomFullscreen);
        if (!atmosphericExecutor)
            return RenderPackValidationResult.Valid();

        bool directionalShadowOnly = descriptor.Passes.Any(static value =>
                value.Semantic == RenderPassSemantic.DirectionalShadowDepth)
            && descriptor.Passes.All(static value =>
                value.Semantic is RenderPassSemantic.CustomFullscreen
                    or RenderPassSemantic.DirectionalShadowDepth);
        if (directionalShadowOnly)
            return ValidateDirectionalShadowProfile(descriptor);

        RenderPassSemantic[] requiredPasses =
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
        foreach (RenderPassSemantic semantic in requiredPasses)
        {
            if (!descriptor.Passes.Any(value => value.Semantic == semantic))
            {
                return Invalid(
                    $"Pack '{descriptor.Id}' requests the atmospheric executor but "
                    + $"does not declare required pass semantic '{semantic}'.");
            }
        }

        RenderResourceSemantic[] requiredResources =
        [
            RenderResourceSemantic.MainWorldHdr,
            RenderResourceSemantic.BloomPing,
            RenderResourceSemantic.BloomPong,
            RenderResourceSemantic.SunOcclusionMask,
            RenderResourceSemantic.SunRays,
            RenderResourceSemantic.DirectionalShadowDepth,
            RenderResourceSemantic.VolumetricShafts,
        ];
        foreach (RenderResourceSemantic semantic in requiredResources)
        {
            if (!descriptor.Resources.Any(value => value.Semantic == semantic))
            {
                return Invalid(
                    $"Pack '{descriptor.Id}' requests the atmospheric executor but "
                    + $"does not declare required resource semantic '{semantic}'.");
            }
        }

        if (descriptor.SceneReplays.Count(value =>
                value.Semantic == RenderSceneReplaySemantic.OutdoorDirectionalShadowCasters) != 1)
        {
            return Invalid(
                $"Pack '{descriptor.Id}' must declare exactly one outdoor directional-shadow replay.");
        }

        bool usesMultiview = descriptor.QualityPresets.Any(preset =>
            (preset.ExecutionHints & RenderQualityExecutionHints.MultiviewDirectionalShadowCascades) != 0);
        List<RenderPipelineVariantSemantic> requiredVariants =
        [
            RenderPipelineVariantSemantic.TerrainDirectionalShadowCaster,
            RenderPipelineVariantSemantic.WorldOpaqueDirectionalShadowCaster,
            RenderPipelineVariantSemantic.WorldAlphaCutoutDirectionalShadowCaster,
            RenderPipelineVariantSemantic.TerrainDirectionalShadowReceiver,
            RenderPipelineVariantSemantic.WorldDirectionalShadowReceiver,
        ];
        if (usesMultiview)
        {
            requiredVariants.Add(RenderPipelineVariantSemantic.TerrainMultiviewDirectionalShadowCaster);
            requiredVariants.Add(RenderPipelineVariantSemantic.WorldOpaqueMultiviewDirectionalShadowCaster);
            requiredVariants.Add(RenderPipelineVariantSemantic.WorldAlphaCutoutMultiviewDirectionalShadowCaster);
        }
        foreach (RenderPipelineVariantSemantic semantic in requiredVariants)
        {
            if (!descriptor.PipelineVariants.Any(value => value.Semantic == semantic))
            {
                return Invalid(
                    $"Pack '{descriptor.Id}' does not declare required pipeline-variant "
                    + $"semantic '{semantic}'.");
            }
        }

        RenderSettingSemantic[] requiredSettings =
        [
            RenderSettingSemantic.BloomStrength,
            RenderSettingSemantic.FilmicStrength,
            RenderSettingSemantic.Exposure,
            RenderSettingSemantic.GradeSaturation,
            RenderSettingSemantic.GradeContrast,
            RenderSettingSemantic.VignetteStrength,
            RenderSettingSemantic.SunRayStrength,
            RenderSettingSemantic.DirectionalShadowStrength,
            RenderSettingSemantic.DirectionalShadowReachMetres,
            RenderSettingSemantic.DirectionalShadowPcfTaps,
            RenderSettingSemantic.VolumetricStrength,
            RenderSettingSemantic.VolumetricRayMarchSteps,
        ];
        foreach (RenderSettingSemantic semantic in requiredSettings)
        {
            if (!descriptor.Settings.Any(value => value.Semantic == semantic))
            {
                return Invalid(
                    $"Pack '{descriptor.Id}' does not declare required atmospheric "
                    + $"setting semantic '{semantic}'.");
            }
        }
        if (descriptor.AtmospherePolicy is null)
        {
            return Invalid(
                $"Pack '{descriptor.Id}' must declare its visible sun/day-group "
                + "atmosphere policy.");
        }
        if (descriptor.AtmospherePolicy.DirectionalShadowLightElevationResponse is not { Count: >= 2 })
        {
            return Invalid(
                $"Pack '{descriptor.Id}' must declare a directional-shadow "
                + "light-elevation response curve.");
        }
        if (descriptor.AtmospherePolicy.VolumetricShaftSunElevationResponse is not { Count: >= 2 })
        {
            return Invalid(
                $"Pack '{descriptor.Id}' must declare a volumetric-shaft "
                + "sun-elevation response curve.");
        }

        RenderPackValidationResult shapes = ValidateAtmosphericSemanticShapes(descriptor);
        if (!shapes.Success) return shapes;
        return ValidateAtmosphericSemanticEdges(descriptor);
    }

    private static RenderPackValidationResult ValidateDirectionalShadowProfile(
        RenderPackDescriptor descriptor)
    {
        if (descriptor.HighestTier < RenderPackTier.Tier2)
        {
            return Invalid(
                $"Pack '{descriptor.Id}' declares directional shadows below Tier2.");
        }

        RenderCapability[] requiredCapabilities =
        [
            RenderCapability.MainWorldColorIntermediate,
            RenderCapability.FullscreenPasses,
            RenderCapability.AuthoredCelestialDirectionalLight,
            RenderCapability.AuthoredWeather,
            RenderCapability.DirectionalShadowMaps,
            RenderCapability.OutdoorDirectionalShadowCasterReplay,
            RenderCapability.AnimatedCasterTransforms,
            RenderCapability.AlphaCutoutShadowCasters,
        ];
        foreach (RenderCapability capability in requiredCapabilities)
        {
            if (!descriptor.RequiredCapabilities.Contains(capability))
            {
                return Invalid(
                    $"Pack '{descriptor.Id}' directional shadows must explicitly require "
                    + $"capability '{capability}'.");
            }
        }

        if (descriptor.Passes.Count(static value =>
                value.Semantic == RenderPassSemantic.DirectionalShadowDepth) != 1)
        {
            return Invalid(
                $"Pack '{descriptor.Id}' must declare exactly one directional-shadow pass.");
        }
        if (descriptor.Resources.Count(static value =>
                value.Semantic == RenderResourceSemantic.DirectionalShadowDepth) != 1)
        {
            return Invalid(
                $"Pack '{descriptor.Id}' must declare exactly one directional-shadow resource.");
        }
        if (descriptor.SceneReplays.Count != 1
            || descriptor.SceneReplays[0].Semantic
                != RenderSceneReplaySemantic.OutdoorDirectionalShadowCasters)
        {
            return Invalid(
                $"Pack '{descriptor.Id}' must declare exactly one outdoor "
                + "directional-shadow replay.");
        }

        bool usesMultiview = descriptor.QualityPresets.Any(preset =>
            (preset.ExecutionHints & RenderQualityExecutionHints.MultiviewDirectionalShadowCascades) != 0);
        List<RenderPipelineVariantSemantic> requiredVariants =
        [
            RenderPipelineVariantSemantic.TerrainDirectionalShadowCaster,
            RenderPipelineVariantSemantic.WorldOpaqueDirectionalShadowCaster,
            RenderPipelineVariantSemantic.WorldAlphaCutoutDirectionalShadowCaster,
            RenderPipelineVariantSemantic.TerrainDirectionalShadowReceiver,
            RenderPipelineVariantSemantic.WorldDirectionalShadowReceiver,
        ];
        if (usesMultiview)
        {
            requiredVariants.Add(RenderPipelineVariantSemantic.TerrainMultiviewDirectionalShadowCaster);
            requiredVariants.Add(RenderPipelineVariantSemantic.WorldOpaqueMultiviewDirectionalShadowCaster);
            requiredVariants.Add(RenderPipelineVariantSemantic.WorldAlphaCutoutMultiviewDirectionalShadowCaster);
        }
        if (descriptor.PipelineVariants.Count != requiredVariants.Count)
        {
            return Invalid(
                $"Pack '{descriptor.Id}' directional shadows require exactly {requiredVariants.Count} "
                + "semantic pipeline variants for its execution hints.");
        }
        foreach (RenderPipelineVariantSemantic semantic in requiredVariants)
        {
            if (!descriptor.PipelineVariants.Any(value => value.Semantic == semantic))
            {
                return Invalid(
                    $"Pack '{descriptor.Id}' does not declare required pipeline-variant "
                    + $"semantic '{semantic}'.");
            }
        }

        foreach (RenderSettingSemantic semantic in new[]
        {
            RenderSettingSemantic.DirectionalShadowStrength,
            RenderSettingSemantic.DirectionalShadowReachMetres,
            RenderSettingSemantic.DirectionalShadowPcfTaps,
        })
        {
            if (!descriptor.Settings.Any(value => value.Semantic == semantic))
            {
                return Invalid(
                    $"Pack '{descriptor.Id}' does not declare required directional-shadow "
                    + $"setting semantic '{semantic}'.");
            }
        }

        if (descriptor.AtmospherePolicy?.DirectionalShadowLightElevationResponse
            is not { Count: >= 2 })
        {
            return Invalid(
                $"Pack '{descriptor.Id}' must declare a directional-shadow "
                + "light-elevation response curve.");
        }

        RenderPassDeclaration shadow = descriptor.Passes.Single(value =>
            value.Semantic == RenderPassSemantic.DirectionalShadowDepth);
        RenderResourceDeclaration depth = descriptor.Resources.Single(value =>
            value.Semantic == RenderResourceSemantic.DirectionalShadowDepth);
        if (shadow.Hook != RenderPassHook.ShadowDepthBeforeWorld
            || shadow.ResourceReads.Count != 0
            || shadow.ResourceWrites.Count != 1
            || !string.Equals(
                shadow.ResourceWrites[0], depth.Id, StringComparison.OrdinalIgnoreCase))
        {
            return Invalid(
                $"Pack '{descriptor.Id}' directional-shadow pass must run before the world, "
                + "read no declared resource, and write its directional-depth resource.");
        }
        if (depth.Kind != RenderResourceKind.Image2DArray
            || depth.Format != RenderFormatClass.DirectionalDepth
            || depth.Extent?.Mode != RenderExtentMode.AbsolutePixels
            || depth.Usage != (RenderResourceUsage.Sampled | RenderResourceUsage.DepthAttachment)
            || depth.Lifetime != RenderResourceLifetime.ActivePack)
        {
            return Invalid(
                $"Pack '{descriptor.Id}' directional-shadow resource does not match the "
                + "host executor's array-depth contract.");
        }

        RenderPackValidationResult shapes = ValidateDirectionalShadowShapes(descriptor);
        if (!shapes.Success)
            return shapes;

        RenderPassDeclaration[] outputPasses = descriptor.Passes
            .Where(static value => value.Hook == RenderPassHook.ToneMap
                && value.ResourceWrites.Count == 0)
            .ToArray();
        if (outputPasses.Length != 1
            || outputPasses[0].Semantic != RenderPassSemantic.CustomFullscreen
            || !outputPasses[0].SemanticInputs.Contains(RenderSemanticInput.WorldColor))
        {
            return Invalid(
                $"Pack '{descriptor.Id}' directional shadows require exactly one custom "
                + "ToneMap output-copy pass sampling WorldColor.");
        }
        if (descriptor.Passes.Any(value =>
                value.Semantic == RenderPassSemantic.CustomFullscreen
                && value.Hook is RenderPassHook.ShadowDepthBeforeWorld
                    or RenderPassHook.AfterToneMapBeforePrivateViewports))
        {
            return Invalid(
                $"Pack '{descriptor.Id}' uses an unsupported custom pass hook in its "
                + "directional-shadow graph.");
        }
        return RenderPackValidationResult.Valid();
    }

    private static RenderPackValidationResult ValidateDirectionalShadowShapes(
        RenderPackDescriptor descriptor)
    {
        RenderPipelineVariantSemantic[] semantics =
        [
            RenderPipelineVariantSemantic.TerrainDirectionalShadowCaster,
            RenderPipelineVariantSemantic.WorldOpaqueDirectionalShadowCaster,
            RenderPipelineVariantSemantic.WorldAlphaCutoutDirectionalShadowCaster,
            RenderPipelineVariantSemantic.TerrainDirectionalShadowReceiver,
            RenderPipelineVariantSemantic.WorldDirectionalShadowReceiver,
        ];
        RenderPipelineBaseSemantic[] bases =
        [
            RenderPipelineBaseSemantic.Terrain,
            RenderPipelineBaseSemantic.WorldMesh,
            RenderPipelineBaseSemantic.WorldMesh,
            RenderPipelineBaseSemantic.Terrain,
            RenderPipelineBaseSemantic.WorldMesh,
        ];
        RenderMaterialClass[] materials =
        [
            RenderMaterialClass.Opaque,
            RenderMaterialClass.Opaque | RenderMaterialClass.AnimatedOpaque,
            RenderMaterialClass.AlphaCutout | RenderMaterialClass.AnimatedAlphaCutout,
            RenderMaterialClass.Opaque,
            RenderMaterialClass.Opaque | RenderMaterialClass.AlphaCutout
                | RenderMaterialClass.AnimatedOpaque
                | RenderMaterialClass.AnimatedAlphaCutout,
        ];
        RenderSemanticInput[][] inputs =
        [
            [RenderSemanticInput.CameraMatrices],
            [RenderSemanticInput.CameraMatrices, RenderSemanticInput.ShadowCasterTransforms],
            [RenderSemanticInput.CameraMatrices, RenderSemanticInput.ShadowCasterTransforms],
            [RenderSemanticInput.DirectionalShadowMaps,
                RenderSemanticInput.SelectedCelestialDirectionalLight],
            [RenderSemanticInput.DirectionalShadowMaps,
                RenderSemanticInput.SelectedCelestialDirectionalLight],
        ];
        for (int i = 0; i < semantics.Length; i++)
        {
            PipelineVariantDeclaration variant = descriptor.PipelineVariants.Single(value =>
                value.Semantic == semantics[i]);
            if (variant.BaseSemantic != bases[i]
                || variant.CompatibleMaterials != materials[i]
                || !variant.SemanticInputs.SequenceEqual(inputs[i]))
            {
                return Invalid(
                    $"Pipeline variant semantic '{semantics[i]}' does not match the fixed "
                    + "directional-shadow executor contract.");
            }
        }

        if (descriptor.QualityPresets.Any(preset =>
                (preset.ExecutionHints & RenderQualityExecutionHints.MultiviewDirectionalShadowCascades) != 0))
        {
            RenderPipelineVariantSemantic[] multiview =
            [
                RenderPipelineVariantSemantic.TerrainMultiviewDirectionalShadowCaster,
                RenderPipelineVariantSemantic.WorldOpaqueMultiviewDirectionalShadowCaster,
                RenderPipelineVariantSemantic.WorldAlphaCutoutMultiviewDirectionalShadowCaster,
            ];
            for (int i = 0; i < multiview.Length; i++)
            {
                PipelineVariantDeclaration variant = descriptor.PipelineVariants.Single(value =>
                    value.Semantic == multiview[i]);
                if (variant.BaseSemantic != bases[i]
                    || variant.CompatibleMaterials != materials[i]
                    || !variant.SemanticInputs.SequenceEqual(inputs[i]))
                {
                    return Invalid(
                        $"Pipeline variant semantic '{multiview[i]}' does not match the fixed "
                        + "multiview directional-shadow executor contract.");
                }
            }
        }

        SceneReplayDeclaration replay = descriptor.SceneReplays[0];
        const RenderCasterClass requiredCasters = RenderCasterClass.Terrain
            | RenderCasterClass.OpaqueWorld
            | RenderCasterClass.AlphaCutoutWorld
            | RenderCasterClass.AnimatedOpaque
            | RenderCasterClass.AnimatedAlphaCutout;
        if (replay.CasterClasses != requiredCasters || replay.ViewCount != 4)
        {
            return Invalid(
                $"Pack '{descriptor.Id}' outdoor directional-shadow replay must declare "
                + "all five headline caster classes and four maximum cascade views.");
        }
        return RenderPackValidationResult.Valid();
    }

    private static RenderPackValidationResult ValidateAtmosphericSemanticShapes(
        RenderPackDescriptor descriptor)
    {
        RenderPackValidationResult result = Resource(
            RenderResourceSemantic.MainWorldHdr,
            RenderResourceKind.Image2D,
            RenderFormatClass.HdrColor,
            RenderExtentMode.RelativeToMainWorld,
            RenderResourceUsage.Sampled | RenderResourceUsage.ColorAttachment);
        if (!result.Success) return result;
        foreach (RenderResourceSemantic semantic in new[]
        {
            RenderResourceSemantic.BloomPing,
            RenderResourceSemantic.BloomPong,
            RenderResourceSemantic.SunRays,
            RenderResourceSemantic.VolumetricShafts,
        })
        {
            result = Resource(
                semantic,
                RenderResourceKind.Image2D,
                RenderFormatClass.HdrColor,
                RenderExtentMode.RelativeToMainWorld,
                RenderResourceUsage.Sampled | RenderResourceUsage.ColorAttachment);
            if (!result.Success) return result;
        }
        result = Resource(
            RenderResourceSemantic.SunOcclusionMask,
            RenderResourceKind.Image2D,
            RenderFormatClass.SingleChannel,
            RenderExtentMode.RelativeToMainWorld,
            RenderResourceUsage.Sampled | RenderResourceUsage.ColorAttachment);
        if (!result.Success) return result;
        result = Resource(
            RenderResourceSemantic.DirectionalShadowDepth,
            RenderResourceKind.Image2DArray,
            RenderFormatClass.DirectionalDepth,
            RenderExtentMode.AbsolutePixels,
            RenderResourceUsage.Sampled | RenderResourceUsage.DepthAttachment);
        if (!result.Success) return result;

        result = Variant(
            RenderPipelineVariantSemantic.TerrainDirectionalShadowCaster,
            RenderPipelineBaseSemantic.Terrain,
            RenderMaterialClass.Opaque,
            [RenderSemanticInput.CameraMatrices]);
        if (!result.Success) return result;
        result = Variant(
            RenderPipelineVariantSemantic.WorldOpaqueDirectionalShadowCaster,
            RenderPipelineBaseSemantic.WorldMesh,
            RenderMaterialClass.Opaque | RenderMaterialClass.AnimatedOpaque,
            [RenderSemanticInput.CameraMatrices, RenderSemanticInput.ShadowCasterTransforms]);
        if (!result.Success) return result;
        result = Variant(
            RenderPipelineVariantSemantic.WorldAlphaCutoutDirectionalShadowCaster,
            RenderPipelineBaseSemantic.WorldMesh,
            RenderMaterialClass.AlphaCutout | RenderMaterialClass.AnimatedAlphaCutout,
            [RenderSemanticInput.CameraMatrices, RenderSemanticInput.ShadowCasterTransforms]);
        if (!result.Success) return result;
        result = Variant(
            RenderPipelineVariantSemantic.TerrainDirectionalShadowReceiver,
            RenderPipelineBaseSemantic.Terrain,
            RenderMaterialClass.Opaque,
            [RenderSemanticInput.DirectionalShadowMaps,
                RenderSemanticInput.SelectedCelestialDirectionalLight]);
        if (!result.Success) return result;
        result = Variant(
            RenderPipelineVariantSemantic.WorldDirectionalShadowReceiver,
            RenderPipelineBaseSemantic.WorldMesh,
            RenderMaterialClass.Opaque | RenderMaterialClass.AlphaCutout
                | RenderMaterialClass.AnimatedOpaque
                | RenderMaterialClass.AnimatedAlphaCutout,
            [RenderSemanticInput.DirectionalShadowMaps,
                RenderSemanticInput.SelectedCelestialDirectionalLight]);
        if (!result.Success) return result;

        SceneReplayDeclaration replay = descriptor.SceneReplays.Single(value =>
            value.Semantic == RenderSceneReplaySemantic.OutdoorDirectionalShadowCasters);
        const RenderCasterClass requiredCasters = RenderCasterClass.Terrain
            | RenderCasterClass.OpaqueWorld
            | RenderCasterClass.AlphaCutoutWorld
            | RenderCasterClass.AnimatedOpaque
            | RenderCasterClass.AnimatedAlphaCutout;
        if (replay.CasterClasses != requiredCasters || replay.ViewCount != 4)
        {
            return Invalid(
                $"Pack '{descriptor.Id}' outdoor directional-shadow replay must declare "
                + "all five headline caster classes and four maximum cascade views.");
        }

        return RenderPackValidationResult.Valid();

        RenderPackValidationResult Resource(
            RenderResourceSemantic semantic,
            RenderResourceKind kind,
            RenderFormatClass format,
            RenderExtentMode extentMode,
            RenderResourceUsage usage)
        {
            RenderResourceDeclaration resource = descriptor.Resources.Single(value =>
                value.Semantic == semantic);
            if (resource.Kind != kind
                || resource.Format != format
                || resource.Extent?.Mode != extentMode
                || resource.Usage != usage
                || resource.Lifetime != RenderResourceLifetime.ActivePack)
            {
                return Invalid(
                    $"Resource semantic '{semantic}' does not match the fixed atmospheric "
                    + "executor's kind, format, extent, usage, and lifetime contract.");
            }
            return RenderPackValidationResult.Valid();
        }

        RenderPackValidationResult Variant(
            RenderPipelineVariantSemantic semantic,
            RenderPipelineBaseSemantic baseSemantic,
            RenderMaterialClass materials,
            IReadOnlyList<RenderSemanticInput> inputs)
        {
            PipelineVariantDeclaration variant = descriptor.PipelineVariants.Single(value =>
                value.Semantic == semantic);
            if (variant.BaseSemantic != baseSemantic
                || variant.CompatibleMaterials != materials
                || !variant.SemanticInputs.SequenceEqual(inputs))
            {
                return Invalid(
                    $"Pipeline variant semantic '{semantic}' does not match the fixed "
                    + "atmospheric executor's base, material, and input contract.");
            }
            return RenderPackValidationResult.Valid();
        }
    }

    private static RenderPackValidationResult ValidateAtmosphericSemanticEdges(
        RenderPackDescriptor descriptor)
    {
        if (descriptor.Passes.Count != 8
            || descriptor.Resources.Count != 7
            || descriptor.PipelineVariants.Count != (descriptor.QualityPresets.Any(preset =>
                    (preset.ExecutionHints & RenderQualityExecutionHints.MultiviewDirectionalShadowCascades) != 0)
                ? 8 : 5)
            || descriptor.SceneReplays.Count != 1)
        {
            return Invalid(
                $"Pack '{descriptor.Id}' requests the fixed atmospheric executor; API v1 "
                + "requires exactly 8 semantic passes, 7 semantic resources, and the declared semantic "
                + "pipeline variants, and 1 semantic scene replay.");
        }

        RenderPackValidationResult Hook(RenderPassSemantic semantic, RenderPassHook hook)
        {
            RenderPassDeclaration pass = descriptor.Passes.Single(value =>
                value.Semantic == semantic);
            return pass.Hook == hook
                ? RenderPackValidationResult.Valid()
                : Invalid(
                    $"Pass semantic '{semantic}' must run at hook '{hook}', not '{pass.Hook}'.");
        }

        RenderPackValidationResult result = Hook(
            RenderPassSemantic.DirectionalShadowDepth,
            RenderPassHook.ShadowDepthBeforeWorld);
        if (!result.Success) return result;
        foreach (RenderPassSemantic semantic in new[]
        {
            RenderPassSemantic.SunOcclusion,
            RenderPassSemantic.SunRays,
            RenderPassSemantic.VolumetricShafts,
            RenderPassSemantic.BloomDownsample,
            RenderPassSemantic.BloomBlurHorizontal,
            RenderPassSemantic.BloomBlurVertical,
        })
        {
            result = Hook(semantic, RenderPassHook.AtmosphereBeforeToneMap);
            if (!result.Success) return result;
        }
        result = Hook(RenderPassSemantic.FilmicComposite, RenderPassHook.ToneMap);
        if (!result.Success) return result;

        RenderPassSemantic[] declaredOrder = descriptor.Passes
            .Where(static pass => pass.Hook is RenderPassHook.AtmosphereBeforeToneMap
                or RenderPassHook.ToneMap)
            .Select(static pass => pass.Semantic)
            .ToArray();
        RenderPassSemantic[] requiredOrder =
        [
            RenderPassSemantic.SunOcclusion,
            RenderPassSemantic.SunRays,
            RenderPassSemantic.VolumetricShafts,
            RenderPassSemantic.BloomDownsample,
            RenderPassSemantic.BloomBlurHorizontal,
            RenderPassSemantic.BloomBlurVertical,
            RenderPassSemantic.FilmicComposite,
        ];
        if (!declaredOrder.SequenceEqual(requiredOrder))
        {
            return Invalid(
                $"Pack '{descriptor.Id}' atmospheric pass order does not match the "
                + "renderer-owned semantic execution order.");
        }

        result = Edge(RenderPassSemantic.DirectionalShadowDepth, [],
            RenderResourceSemantic.DirectionalShadowDepth);
        if (!result.Success) return result;
        result = Edge(RenderPassSemantic.SunOcclusion, [],
            RenderResourceSemantic.SunOcclusionMask);
        if (!result.Success) return result;
        result = Edge(RenderPassSemantic.SunRays,
            [RenderResourceSemantic.SunOcclusionMask], RenderResourceSemantic.SunRays);
        if (!result.Success) return result;
        result = Edge(RenderPassSemantic.VolumetricShafts,
            [RenderResourceSemantic.DirectionalShadowDepth],
            RenderResourceSemantic.VolumetricShafts);
        if (!result.Success) return result;
        result = Edge(RenderPassSemantic.BloomDownsample,
            [RenderResourceSemantic.SunRays, RenderResourceSemantic.VolumetricShafts],
            RenderResourceSemantic.BloomPing);
        if (!result.Success) return result;
        result = Edge(RenderPassSemantic.BloomBlurHorizontal,
            [RenderResourceSemantic.BloomPing], RenderResourceSemantic.BloomPong);
        if (!result.Success) return result;
        result = Edge(RenderPassSemantic.BloomBlurVertical,
            [RenderResourceSemantic.BloomPong], RenderResourceSemantic.BloomPing);
        if (!result.Success) return result;
        return Edge(RenderPassSemantic.FilmicComposite,
            [RenderResourceSemantic.BloomPing, RenderResourceSemantic.SunRays,
                RenderResourceSemantic.VolumetricShafts],
            output: null);

        RenderPackValidationResult Edge(
            RenderPassSemantic passSemantic,
            IReadOnlyList<RenderResourceSemantic> reads,
            RenderResourceSemantic? output)
        {
            RenderPassDeclaration pass = descriptor.Passes.Single(value =>
                value.Semantic == passSemantic);
            RenderResourceSemantic[] actualReads = pass.ResourceReads
                .Select(id => descriptor.Resources.Single(resource => string.Equals(
                    resource.Id,
                    id,
                    StringComparison.OrdinalIgnoreCase)).Semantic)
                .ToArray();
            if (!actualReads.SequenceEqual(reads))
            {
                return Invalid(
                    $"Pass semantic '{passSemantic}' declares resource reads that do not "
                    + "match its renderer-owned execution edges.");
            }
            RenderResourceSemantic[] actualWrites = pass.ResourceWrites
                .Select(id => descriptor.Resources.Single(resource => string.Equals(
                    resource.Id,
                    id,
                    StringComparison.OrdinalIgnoreCase)).Semantic)
                .ToArray();
            RenderResourceSemantic[] expectedWrites = output is { } semantic
                ? [semantic]
                : [];
            return actualWrites.SequenceEqual(expectedWrites)
                ? RenderPackValidationResult.Valid()
                : Invalid(
                    $"Pass semantic '{passSemantic}' declares a resource output that does "
                    + "not match its renderer-owned execution edge.");
        }
    }

    private static RenderPackValidationResult UniqueNonCustomSemantics<T, TSemantic>(
        RenderPackDescriptor descriptor,
        IEnumerable<T?> values,
        Func<T, TSemantic> select,
        TSemantic custom,
        string kind)
        where T : class
        where TSemantic : struct, Enum
    {
        var seen = new HashSet<TSemantic>();
        foreach (T? value in values)
        {
            if (value is null)
                continue;
            TSemantic semantic = select(value);
            if (!Enum.IsDefined(semantic))
                return Invalid($"Pack '{descriptor.Id}' declares an unknown {kind} semantic.");
            if (!EqualityComparer<TSemantic>.Default.Equals(semantic, custom)
                && !seen.Add(semantic))
            {
                return Invalid(
                    $"Pack '{descriptor.Id}' declares duplicate {kind} semantic '{semantic}'.");
            }
        }
        return RenderPackValidationResult.Valid();
    }

    internal static RenderPackValidationResult ValidatePresetCompatibility(
        RenderPackDescriptor descriptor,
        RenderQualityPreset preset,
        RenderPackHostCapabilities capabilities)
    {
        ArgumentNullException.ThrowIfNull(descriptor);
        ArgumentNullException.ThrowIfNull(preset);
        ArgumentNullException.ThrowIfNull(capabilities);
        foreach (RenderCapability required in preset.RequiredCapabilities)
        {
            if (!capabilities.Available.Contains(required))
            {
                return Invalid(
                    $"Preset '{preset.Id}' requires unsupported capability '{required}'.");
            }
        }
        if (preset.Semantic == RenderQualitySemantic.Automatic
            && !capabilities.Available.Contains(RenderCapability.GpuTimestampQueries))
        {
            return Invalid(
                $"Preset '{preset.Id}' requires asynchronous GPU timestamp queries "
                + "because Auto evaluates the complete CPU/GPU pack cost; explicit "
                + "Low remains available when its resource limits fit.");
        }
        if (preset.Semantic == RenderQualitySemantic.Automatic)
            return RenderPackValidationResult.Valid();

        if (preset.MaxResidentGpuBytes > capabilities.MaxPackResidentBytes)
        {
            return Invalid(
                $"Preset '{preset.Id}' declares a {preset.MaxResidentGpuBytes}-byte "
                + $"resident GPU ceiling, but this host permits "
                + $"{capabilities.MaxPackResidentBytes} bytes under its "
                + $"{capabilities.MemoryPolicyDescription} policy.");
        }

        Dictionary<string, RenderQualityResourceOverride> overrides = preset.ResourceOverrides
            .ToDictionary(value => value.ResourceId, StringComparer.OrdinalIgnoreCase);
        foreach (RenderResourceDeclaration resource in descriptor.Resources)
        {
            RenderExtentDeclaration? extent = overrides.TryGetValue(
                    resource.Id,
                    out RenderQualityResourceOverride? resourceOverride)
                ? resourceOverride.Extent ?? resource.Extent
                : resource.Extent;
            if (extent is null)
                continue;
            if (extent.Layers > capabilities.MaxImageArrayLayers)
            {
                return Invalid(
                    $"Preset '{preset.Id}' resource '{resource.Id}' needs "
                    + $"{extent.Layers} image-array layers; this device provides "
                    + $"{capabilities.MaxImageArrayLayers}.");
            }
            if (extent.Mode == RenderExtentMode.AbsolutePixels
                && (extent.Width > capabilities.MaxImageDimension2D
                    || extent.Height > capabilities.MaxImageDimension2D))
            {
                return Invalid(
                    $"Preset '{preset.Id}' resource '{resource.Id}' needs "
                    + $"{extent.Width:G}x{extent.Height:G}; this device's maximum "
                    + $"2-D image edge is {capabilities.MaxImageDimension2D}.");
            }
        }
        return RenderPackValidationResult.Valid();
    }

    private static RenderPackValidationResult ValidateUniqueIds(RenderPackDescriptor descriptor)
    {
        var ids = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        IEnumerable<(string Kind, string? Id)> declarations =
            descriptor.Resources.Select(static value => ("resource", value?.Id))
                .Concat(descriptor.Passes.Select(static value => ("pass", value?.Id)))
                .Concat(descriptor.SceneReplays.Select(static value => ("scene replay", value?.Id)))
                .Concat(descriptor.PipelineVariants.Select(static value => ("pipeline variant", value?.Id)))
                .Concat(descriptor.QualityPresets.Select(static value => ("quality preset", value?.Id)))
                .Concat(descriptor.Settings.Select(static value => ("setting", value?.Id)));

        foreach ((string kind, string? id) in declarations)
        {
            if (!IsStableId(id))
                return Invalid($"Pack '{descriptor.Id}' has an invalid {kind} id.");
            if (!ids.Add($"{kind}:{id}"))
                return Invalid($"Pack '{descriptor.Id}' declares duplicate {kind} id '{id}'.");
        }
        return RenderPackValidationResult.Valid();
    }

    private static RenderPackValidationResult ValidateResources(
        RenderPackDescriptor descriptor,
        RenderPackHostCapabilities capabilities)
    {
        long declaredBytes = 0;
        foreach (RenderResourceDeclaration? resource in descriptor.Resources)
        {
            if (resource is null)
                return Invalid($"Pack '{descriptor.Id}' contains a null resource declaration.");
            if (resource.Kind == RenderResourceKind.Buffer
                || resource.Format == RenderFormatClass.StructuredData
                || resource.Usage.HasFlag(RenderResourceUsage.Storage))
            {
                return Invalid(
                    $"Resource '{resource.Id}' uses a buffer/storage declaration reserved "
                    + "for a future render-pack API; API v1 binds image resources only.");
            }
            if (resource.Kind == RenderResourceKind.Image2DArray
                && resource.Format != RenderFormatClass.DirectionalDepth)
            {
                return Invalid(
                    $"Resource '{resource.Id}' uses a color image array; render-pack API "
                    + "v1 reserves image arrays for directional depth maps.");
            }
            if (resource.EstimatedResidentBytes < 0 || resource.SizeBytes < 0)
                return Invalid($"Resource '{resource.Id}' declares negative bytes.");
            if (resource.Usage == RenderResourceUsage.None)
                return Invalid($"Resource '{resource.Id}' declares no usage.");
            if (resource.Kind == RenderResourceKind.Buffer && resource.Extent is not null)
                return Invalid($"Buffer resource '{resource.Id}' must not declare an image extent.");
            if (resource.Kind != RenderResourceKind.Buffer)
            {
                if (resource.Extent is null)
                    return Invalid($"Image resource '{resource.Id}' has no extent.");
                RenderExtentDeclaration extent = resource.Extent;
                if (!IsFinitePositive(extent.Width)
                    || !IsFinitePositive(extent.Height)
                    || extent.Layers <= 0
                    || extent.Layers > AbsoluteImageArrayLayerCeiling)
                {
                    return Invalid($"Image resource '{resource.Id}' has an invalid extent.");
                }
                if (extent.Mode == RenderExtentMode.AbsolutePixels
                    && (extent.Width > AbsoluteImageDimension2DCeiling
                        || extent.Height > AbsoluteImageDimension2DCeiling))
                {
                    return Invalid($"Image resource '{resource.Id}' exceeds the device image limit.");
                }
                if (extent.Mode != RenderExtentMode.AbsolutePixels
                    && (extent.Width > 1.0 || extent.Height > 1.0))
                {
                    return Invalid($"Relative resource '{resource.Id}' must use a scale in (0, 1].");
                }
            }

            if (!TryAdd(ref declaredBytes, resource.EstimatedResidentBytes))
                return Invalid($"Pack '{descriptor.Id}' resource byte total overflows.");
        }

        if (declaredBytes > AbsolutePackByteCeiling)
        {
            return Invalid(
                $"Pack '{descriptor.Id}' declares {declaredBytes} resident bytes; "
                + $"the render-pack API ceiling is {AbsolutePackByteCeiling}.");
        }
        return RenderPackValidationResult.Valid();
    }

    private static RenderPackValidationResult ValidatePasses(RenderPackDescriptor descriptor)
    {
        Dictionary<string, RenderResourceDeclaration> resources = descriptor.Resources
            .ToDictionary(static value => value.Id, StringComparer.OrdinalIgnoreCase);
        var written = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

        foreach (RenderPassDeclaration? pass in descriptor.Passes)
        {
            if (pass is null)
                return Invalid($"Pack '{descriptor.Id}' contains a null pass declaration.");
            if (!IsSafeAssetKey(pass.VertexShaderAsset)
                || !IsSafeAssetKey(pass.FragmentShaderAsset))
                return Invalid($"Pass '{pass.Id}' declares an unsafe shader asset key.");
            if (pass.SemanticInputs is null || pass.ResourceReads is null || pass.ResourceWrites is null)
                return Invalid($"Pass '{pass.Id}' contains a null binding list.");
            if (pass.ResourceWrites.Count > 1)
            {
                return Invalid(
                    $"Pass '{pass.Id}' writes {pass.ResourceWrites.Count} resources; "
                    + "render-pack API v1 supports one attachment per declared pass.");
            }
            if (pass.ResourceWrites.Count == 0
                && pass.Hook is not RenderPassHook.ToneMap
                    and not RenderPassHook.AfterToneMapBeforePrivateViewports)
            {
                return Invalid(
                    $"Pass '{pass.Id}' has no declared output at hook '{pass.Hook}'.");
            }
            int sampledInputs = pass.SemanticInputs.Count(static semantic =>
                semantic is RenderSemanticInput.WorldColor
                    or RenderSemanticInput.SceneDepth
                    or RenderSemanticInput.SceneNormals);
            foreach (string read in pass.ResourceReads)
            {
                if (!resources.TryGetValue(read, out RenderResourceDeclaration? resource))
                    return Invalid($"Pass '{pass.Id}' reads unknown resource '{read}'.");
                if (!written.Contains(read))
                    return Invalid($"Pass '{pass.Id}' reads resource '{read}' before it is written.");
                if (!resource.Usage.HasFlag(RenderResourceUsage.Sampled))
                    return Invalid($"Pass '{pass.Id}' samples non-sampled resource '{read}'.");
                bool dedicatedDirectionalSlot =
                    resource.Format == RenderFormatClass.DirectionalDepth
                    && pass.SemanticInputs.Contains(RenderSemanticInput.DirectionalShadowMaps);
                if (!dedicatedDirectionalSlot)
                    sampledInputs++;
            }
            if (sampledInputs > 4)
            {
                return Invalid(
                    $"Pass '{pass.Id}' needs {sampledInputs} ordinary sampled images; "
                    + "render-pack API v1 provides four ordered texture slots (A..D).");
            }
            foreach (string write in pass.ResourceWrites)
            {
                if (!resources.TryGetValue(write, out RenderResourceDeclaration? resource))
                    return Invalid($"Pass '{pass.Id}' writes unknown resource '{write}'.");
                RenderResourceUsage attachment = resource.Format == RenderFormatClass.DirectionalDepth
                    ? RenderResourceUsage.DepthAttachment
                    : RenderResourceUsage.ColorAttachment;
                if (!resource.Usage.HasFlag(attachment))
                    return Invalid($"Pass '{pass.Id}' writes non-attachment resource '{write}'.");
                written.Add(write);
            }
        }
        return RenderPackValidationResult.Valid();
    }

    private static RenderPackValidationResult ValidateSemanticCapabilities(
        RenderPackDescriptor descriptor,
        RenderPackHostCapabilities capabilities)
    {
        foreach (RenderPassDeclaration shadowPass in descriptor.Passes.Where(static pass =>
                     pass.Semantic == RenderPassSemantic.DirectionalShadowDepth))
        {
            if (!shadowPass.SemanticInputs.Contains(
                    RenderSemanticInput.SelectedCelestialDirectionalLight)
                || shadowPass.SemanticInputs.Contains(RenderSemanticInput.SunDirection))
            {
                return Invalid(
                    $"Directional-shadow pass '{shadowPass.Id}' must declare "
                    + $"'{RenderSemanticInput.SelectedCelestialDirectionalLight}' and must not "
                    + "alias the sun-specific atmospheric direction.");
            }
        }

        IEnumerable<RenderSemanticInput> inputs = descriptor.Passes
            .SelectMany(static pass => pass?.SemanticInputs ?? [])
            .Concat(descriptor.PipelineVariants.SelectMany(
                static variant => variant?.SemanticInputs ?? []));
        foreach (RenderSemanticInput input in inputs.Distinct())
        {
            RenderCapability? required = input switch
            {
                RenderSemanticInput.WorldColor =>
                    RenderCapability.MainWorldColorIntermediate,
                RenderSemanticInput.SceneDepth =>
                    RenderCapability.SceneDepthSampling,
                RenderSemanticInput.SceneNormals =>
                    RenderCapability.SceneNormalSampling,
                RenderSemanticInput.SunDirection =>
                    RenderCapability.AuthoredSunDirection,
                RenderSemanticInput.SelectedCelestialDirectionalLight =>
                    RenderCapability.AuthoredCelestialDirectionalLight,
                RenderSemanticInput.SunScreenPosition =>
                    RenderCapability.AuthoredSunScreenPosition,
                RenderSemanticInput.ActiveDayGroup or RenderSemanticInput.Weather =>
                    RenderCapability.AuthoredWeather,
                RenderSemanticInput.CameraMatrices or RenderSemanticInput.FrameTime =>
                    RenderCapability.FullscreenPasses,
                RenderSemanticInput.ShadowCasterTransforms =>
                    RenderCapability.AnimatedCasterTransforms,
                RenderSemanticInput.DirectionalShadowMaps =>
                    RenderCapability.DirectionalShadowMaps,
                _ => null,
            };
            if (input is RenderSemanticInput.SelectedCelestialDirectionalLight
                && required is { } declaredCapability
                && !descriptor.RequiredCapabilities.Contains(declaredCapability))
            {
                return Invalid(
                    $"Pack '{descriptor.Id}' declares semantic '{input}' but does not "
                    + $"require capability '{declaredCapability}'.");
            }
            if (required is { } capability
                && !capabilities.Available.Contains(capability))
            {
                return Invalid(
                    $"Pack '{descriptor.Id}' declares semantic '{input}' but the host "
                    + $"does not provide capability '{capability}'.");
            }
        }
        return RenderPackValidationResult.Valid();
    }

    private static RenderPackValidationResult ValidateReplays(RenderPackDescriptor descriptor)
    {
        foreach (SceneReplayDeclaration? replay in descriptor.SceneReplays)
        {
            if (replay is null)
                return Invalid($"Pack '{descriptor.Id}' contains a null scene replay.");
            if (replay.ViewCount <= 0 || replay.ViewCount > 4)
                return Invalid($"Scene replay '{replay.Id}' must request 1..4 views.");
            if (replay.CasterClasses == RenderCasterClass.None)
                return Invalid($"Scene replay '{replay.Id}' declares no caster classes.");
        }
        return RenderPackValidationResult.Valid();
    }

    private static RenderPackValidationResult ValidateVariants(RenderPackDescriptor descriptor)
    {
        foreach (PipelineVariantDeclaration? variant in descriptor.PipelineVariants)
        {
            if (variant is null)
                return Invalid($"Pack '{descriptor.Id}' contains a null pipeline variant.");
            if (!IsSafeAssetKey(variant.VertexShaderAsset)
                || !IsSafeAssetKey(variant.FragmentShaderAsset))
                return Invalid($"Pipeline variant '{variant.Id}' declares an unsafe shader asset key.");
            if (variant.CompatibleMaterials == RenderMaterialClass.None)
                return Invalid($"Pipeline variant '{variant.Id}' declares no compatible materials.");
            if (variant.SemanticInputs is null)
                return Invalid($"Pipeline variant '{variant.Id}' has a null semantic-input list.");
        }
        return RenderPackValidationResult.Valid();
    }

    private static RenderPackValidationResult ValidateSettings(RenderPackDescriptor descriptor)
    {
        if (descriptor.Settings.Count > RenderPackShaderAbi.PackSettingScalarCapacity)
            return Invalid(
                $"Pack '{descriptor.Id}' exceeds the "
                + $"{RenderPackShaderAbi.PackSettingScalarCapacity}-setting API-v1 ceiling.");
        foreach (RenderSettingDeclaration? setting in descriptor.Settings)
        {
            if (setting is null)
                return Invalid($"Pack '{descriptor.Id}' contains a null setting.");
            if (!Enum.IsDefined(setting.Kind))
                return Invalid($"Setting '{setting.Id}' declares an unknown kind.");
            if (string.IsNullOrWhiteSpace(setting.DisplayName))
                return Invalid($"Setting '{setting.Id}' has no display name.");
            if (setting.DefaultValue is null || setting.Choices is null)
                return Invalid($"Setting '{setting.Id}' contains a null value list.");
            if (setting.Minimum is { } min && !double.IsFinite(min)
                || setting.Maximum is { } max && !double.IsFinite(max)
                || setting.Step is { } step && (!double.IsFinite(step) || step <= 0))
                return Invalid($"Setting '{setting.Id}' has invalid bounds.");
            if (setting.Minimum is { } minimum
                && setting.Maximum is { } maximum
                && minimum > maximum)
                return Invalid($"Setting '{setting.Id}' has an inverted range.");
            if (setting.Kind == RenderSettingKind.Choice
                && (setting.Choices is null || setting.Choices.Count == 0))
                return Invalid($"Choice setting '{setting.Id}' declares no choices.");
            if (!RenderPackSettingValueCodec.TryEncode(
                    setting,
                    setting.DefaultValue,
                    out _))
            {
                return Invalid($"Setting '{setting.Id}' has an invalid default value.");
            }
        }
        return RenderPackValidationResult.Valid();
    }

    private static RenderPackValidationResult ValidatePresets(
        RenderPackDescriptor descriptor,
        RenderPackHostCapabilities capabilities)
    {
        if (descriptor.QualityPresets.Count == 0)
            return Invalid($"Pack '{descriptor.Id}' declares no quality presets.");
        HashSet<string> resources = descriptor.Resources
            .Select(static value => value.Id)
            .ToHashSet(StringComparer.OrdinalIgnoreCase);
        HashSet<string> settings = descriptor.Settings
            .Select(static value => value.Id)
            .ToHashSet(StringComparer.OrdinalIgnoreCase);
        Dictionary<string, RenderSettingDeclaration> settingDeclarations =
            descriptor.Settings.ToDictionary(
                value => value.Id,
                StringComparer.OrdinalIgnoreCase);
        const long ceiling = AbsolutePackByteCeiling;

        foreach (RenderQualityPreset? preset in descriptor.QualityPresets)
        {
            if (preset is null)
                return Invalid($"Pack '{descriptor.Id}' contains a null quality preset.");
            if (string.IsNullOrWhiteSpace(preset.DisplayName))
                return Invalid($"Quality preset '{preset.Id}' has no display name.");
            if (preset.MaxResidentGpuBytes < 0 || preset.MaxResidentGpuBytes > ceiling)
                return Invalid($"Quality preset '{preset.Id}' exceeds the pack memory ceiling.");
            const RenderQualityExecutionHints supportedExecutionHints =
                RenderQualityExecutionHints.FusedAtmosphericPostProcess
                | RenderQualityExecutionHints.MultiviewDirectionalShadowCascades;
            if ((preset.ExecutionHints & ~supportedExecutionHints) != 0)
                return Invalid($"Quality preset '{preset.Id}' declares an unknown execution hint.");
            if ((preset.ExecutionHints
                    & RenderQualityExecutionHints.FusedAtmosphericPostProcess) != 0
                && preset.Semantic != RenderQualitySemantic.Low)
            {
                return Invalid(
                    $"Quality preset '{preset.Id}' may only use fused atmospheric post-processing "
                    + "with the Low quality semantic.");
            }
            if ((preset.ExecutionHints
                    & RenderQualityExecutionHints.MultiviewDirectionalShadowCascades) != 0
                && preset.Semantic != RenderQualitySemantic.Low)
            {
                return Invalid(
                    $"Quality preset '{preset.Id}' may only use multiview directional-shadow "
                    + "cascades with the Low quality semantic.");
            }
            if ((preset.ExecutionHints
                    & RenderQualityExecutionHints.MultiviewDirectionalShadowCascades) != 0
                && descriptor.Passes.Count(pass =>
                    pass.Semantic == RenderPassSemantic.DirectionalShadowDepth) != 1)
            {
                return Invalid(
                    $"Quality preset '{preset.Id}' requests multiview directional-shadow "
                    + "cascades without the directional-shadow graph.");
            }
            if ((preset.ExecutionHints
                    & RenderQualityExecutionHints.MultiviewDirectionalShadowCascades) != 0
                && !preset.RequiredCapabilities.Contains(
                    RenderCapability.MultiviewDirectionalShadowCascades))
            {
                return Invalid(
                    $"Quality preset '{preset.Id}' must require multiview directional-shadow capability.");
            }
            if ((preset.ExecutionHints
                    & RenderQualityExecutionHints.FusedAtmosphericPostProcess) != 0)
            {
                RenderPassSemantic[] requiredFusedPasses =
                [
                    RenderPassSemantic.SunOcclusion,
                    RenderPassSemantic.SunRays,
                    RenderPassSemantic.BloomDownsample,
                    RenderPassSemantic.BloomBlurHorizontal,
                    RenderPassSemantic.BloomBlurVertical,
                    RenderPassSemantic.FilmicComposite,
                ];
                if (requiredFusedPasses.Any(semantic =>
                        descriptor.Passes.Count(pass => pass.Semantic == semantic) != 1))
                {
                    return Invalid(
                        $"Quality preset '{preset.Id}' requests fused atmospheric "
                        + "post-processing without the complete standard pass graph.");
                }
            }
            if (!IsFiniteNonNegative(preset.MaxIncrementalGpuMillisecondsP50)
                || !IsFiniteNonNegative(preset.MaxIncrementalGpuMillisecondsP99)
                || !IsFiniteNonNegative(preset.MaxIncrementalCpuMillisecondsP50)
                || !IsFiniteNonNegative(preset.MaxIncrementalCpuMillisecondsP99)
                || preset.MaxIncrementalGpuMillisecondsP50 > preset.MaxIncrementalGpuMillisecondsP99
                || preset.MaxIncrementalCpuMillisecondsP50 > preset.MaxIncrementalCpuMillisecondsP99)
                return Invalid($"Quality preset '{preset.Id}' has invalid performance budgets.");
            foreach (RenderQualityResourceOverride value in preset.ResourceOverrides)
            {
                if (!resources.Contains(value.ResourceId))
                    return Invalid($"Quality preset '{preset.Id}' overrides unknown resource '{value.ResourceId}'.");
                if (value.SizeBytes < 0 || value.EstimatedResidentBytes < 0)
                    return Invalid($"Quality preset '{preset.Id}' declares negative resource bytes.");
            }
            var overriddenSettings = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            foreach (RenderQualitySettingOverride value in preset.SettingOverrides)
            {
                if (!settings.Contains(value.SettingId))
                    return Invalid($"Quality preset '{preset.Id}' overrides unknown setting '{value.SettingId}'.");
                if (!overriddenSettings.Add(value.SettingId))
                    return Invalid($"Quality preset '{preset.Id}' overrides setting '{value.SettingId}' more than once.");
                if (!RenderPackSettingValueCodec.TryEncode(
                        settingDeclarations[value.SettingId],
                        value.Value,
                        out _))
                {
                    return Invalid(
                        $"Quality preset '{preset.Id}' supplies an invalid value "
                        + $"for setting '{value.SettingId}'.");
                }
            }
        }
        return RenderPackValidationResult.Valid();
    }

    private static RenderPackValidationResult ValidateAtmosphere(RenderPackDescriptor descriptor)
    {
        AtmospherePolicyDeclaration? policy = descriptor.AtmospherePolicy;
        if (policy is null)
            return RenderPackValidationResult.Valid();
        if (policy.SunElevationResponse is null
            || policy.ActiveDayGroupMultipliers is null
            || policy.DirectionalShadowLightElevationResponse is null
            || policy.VolumetricShaftSunElevationResponse is null)
            return Invalid($"Pack '{descriptor.Id}' has a null atmosphere-policy list.");

        RenderPackValidationResult curve = ValidateCurve(
            policy.SunElevationResponse,
            "sun-elevation");
        if (!curve.Success)
            return curve;
        curve = ValidateCurve(
            policy.DirectionalShadowLightElevationResponse,
            "directional-shadow light-elevation",
            unitInterval: true);
        if (!curve.Success)
            return curve;
        curve = ValidateDirectionalShadowHorizon(
            policy.DirectionalShadowLightElevationResponse);
        if (!curve.Success)
            return curve;
        curve = ValidateCurve(
            policy.VolumetricShaftSunElevationResponse,
            "volumetric-shaft sun-elevation",
            unitInterval: true);
        if (!curve.Success)
            return curve;

        var groups = new HashSet<int>();
        foreach (ActiveDayGroupMultiplier value in policy.ActiveDayGroupMultipliers)
        {
            if (!groups.Add(value.ActiveDayGroup)
                || !IsFiniteNonNegative(value.Multiplier))
                return Invalid($"Pack '{descriptor.Id}' has an invalid active-day-group mapping.");
        }

        if (policy.FoliageWindByWeather is null)
            return Invalid($"Pack '{descriptor.Id}' has a null foliage-wind weather-point list.");
        var weatherKinds = new HashSet<string>(StringComparer.Ordinal);
        foreach (FoliageWindWeatherPoint point in policy.FoliageWindByWeather)
        {
            if (!Enum.TryParse(
                    point.WeatherKind,
                    ignoreCase: false,
                    out AcDream.Core.World.WeatherKind parsedKind)
                || !Enum.IsDefined(parsedKind))
            {
                return Invalid(
                    $"Pack '{descriptor.Id}' declares an unknown foliage-wind weather "
                    + $"kind '{point.WeatherKind}'.");
            }
            if (!weatherKinds.Add(point.WeatherKind))
            {
                return Invalid(
                    $"Pack '{descriptor.Id}' declares the foliage-wind weather kind "
                    + $"'{point.WeatherKind}' more than once.");
            }
            if (!IsFiniteNonNegative(point.Mean) || !IsFiniteNonNegative(point.Gust))
            {
                return Invalid(
                    $"Pack '{descriptor.Id}' has an invalid foliage-wind mean/gust value "
                    + $"for weather kind '{point.WeatherKind}'.");
            }
        }
        return RenderPackValidationResult.Valid();

        RenderPackValidationResult ValidateCurve(
            IReadOnlyList<SunElevationResponsePoint> points,
            string name,
            bool unitInterval = false)
        {
            double priorElevation = double.NegativeInfinity;
            foreach (SunElevationResponsePoint point in points)
            {
                if (point is null
                    || !double.IsFinite(point.ElevationDegrees)
                    || point.ElevationDegrees < -90
                    || point.ElevationDegrees > 90
                    || !IsFiniteNonNegative(point.Multiplier)
                    || (unitInterval && point.Multiplier > 1)
                    || point.ElevationDegrees <= priorElevation)
                {
                    return Invalid(
                        $"Pack '{descriptor.Id}' has an invalid {name} response curve.");
                }
                priorElevation = point.ElevationDegrees;
            }
            return RenderPackValidationResult.Valid();
        }

        RenderPackValidationResult ValidateDirectionalShadowHorizon(
            IReadOnlyList<SunElevationResponsePoint> points)
        {
            bool hasExactHorizonPoint = false;
            SunElevationResponsePoint? firstAboveHorizon = null;
            foreach (SunElevationResponsePoint point in points)
            {
                if (point.ElevationDegrees <= 0d)
                {
                    if (point.Multiplier != 0d)
                        return InvalidHorizon();
                    hasExactHorizonPoint |= point.ElevationDegrees == 0d;
                    continue;
                }

                firstAboveHorizon = point;
                break;
            }

            return !hasExactHorizonPoint
                && firstAboveHorizon is { Multiplier: not 0d }
                    ? InvalidHorizon()
                    : RenderPackValidationResult.Valid();

            RenderPackValidationResult InvalidHorizon() => Invalid(
                $"Pack '{descriptor.Id}' directional-shadow light-elevation "
                + "curve must resolve to zero at and below the 0-degree "
                + "authored horizon.");
        }
    }

    private static string? FirstNullList(RenderPackDescriptor value)
    {
        if (value.RequiredCapabilities is null) return "required-capability";
        if (value.OptionalCapabilities is null) return "optional-capability";
        if (value.Resources is null) return "resource";
        if (value.Passes is null) return "pass";
        if (value.SceneReplays is null) return "scene-replay";
        if (value.PipelineVariants is null) return "pipeline-variant";
        if (value.QualityPresets is null) return "quality-preset";
        if (value.Settings is null) return "setting";
        return null;
    }

    private static bool IsStableId(string? value)
    {
        if (string.IsNullOrWhiteSpace(value) || value.Length > 128)
            return false;
        if (value[0] is < 'a' or > 'z')
            return false;
        foreach (char character in value)
        {
            if (character is >= 'a' and <= 'z'
                || character is >= '0' and <= '9'
                || character is '.' or '-' or '_')
                continue;
            return false;
        }
        return true;
    }

    private static bool IsSafeAssetKey(string? value)
    {
        if (string.IsNullOrWhiteSpace(value) || value.Length > 512)
            return false;
        if (Path.IsPathRooted(value) || value.Contains('\\'))
            return false;
        string[] segments = value.Split('/');
        return segments.All(static segment =>
            segment.Length > 0 && segment is not "." and not "..");
    }

    private static bool IsFinitePositive(double value) =>
        double.IsFinite(value) && value > 0;

    private static bool IsFiniteNonNegative(double value) =>
        double.IsFinite(value) && value >= 0;

    private static bool TryAdd(ref long total, long value)
    {
        if (value > long.MaxValue - total)
            return false;
        total += value;
        return true;
    }

    private static RenderPackValidationResult Invalid(string reason) =>
        RenderPackValidationResult.Invalid(reason);
}
