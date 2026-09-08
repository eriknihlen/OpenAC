using System.Buffers.Binary;
using AcDream.Plugin.Abstractions.Rendering;

namespace AcDream.Tools.RenderPackValidator;

internal readonly record struct RenderPackSdkValidationResult(
    bool Success,
    string? Reason)
{
    internal static RenderPackSdkValidationResult Valid() => new(true, null);

    internal static RenderPackSdkValidationResult Invalid(string reason) =>
        new(false, reason);
}

internal static class RenderPackSdkValidator
{
    private const long MaximumPackBytes = 256L * 1024 * 1024;
    private const int MaximumImageDimension = 16_384;
    private const int MaximumImageLayers = 256;
    private const uint SpirvMagic = 0x0723_0203u;

    internal static RenderPackSdkValidationResult ValidateDescriptor(
        RenderPackDescriptor? descriptor)
    {
        if (descriptor is null)
            return Invalid("The pack descriptor is missing.");
        if (!StableId.IsValid(descriptor.Id))
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
                + $"{descriptor.PackApiVersion}; this SDK supports "
                + $"{RenderPackApi.MinimumSupported}..{RenderPackApi.Current}.");
        }
        if (!Enum.IsDefined(descriptor.HighestTier))
            return Invalid($"Pack '{descriptor.Id}' declares an unknown tier.");

        string? nullList = FirstNullList(descriptor);
        if (nullList is not null)
            return Invalid($"Pack '{descriptor.Id}' has a null {nullList} declaration list.");
        if (descriptor.RequiredCapabilities.Any(static value => !Enum.IsDefined(value))
            || descriptor.OptionalCapabilities.Any(static value => !Enum.IsDefined(value)))
        {
            return Invalid($"Pack '{descriptor.Id}' declares an unknown capability.");
        }

        RenderPackSdkValidationResult semanticCapabilities =
            ValidateSemanticCapabilities(descriptor);
        if (!semanticCapabilities.Success) return semanticCapabilities;
        RenderPackSdkValidationResult uniqueIds = ValidateUniqueIds(descriptor);
        if (!uniqueIds.Success) return uniqueIds;
        RenderPackSdkValidationResult resources = ValidateResources(descriptor);
        if (!resources.Success) return resources;
        RenderPackSdkValidationResult passes = ValidatePasses(descriptor);
        if (!passes.Success) return passes;
        RenderPackSdkValidationResult replays = ValidateReplays(descriptor);
        if (!replays.Success) return replays;
        RenderPackSdkValidationResult variants = ValidateVariants(descriptor);
        if (!variants.Success) return variants;
        RenderPackSdkValidationResult settings = ValidateSettings(descriptor);
        if (!settings.Success) return settings;
        RenderPackSdkValidationResult presets = ValidatePresets(descriptor);
        if (!presets.Success) return presets;
        RenderPackSdkValidationResult semantics = ValidateSemanticRoles(descriptor);
        if (!semantics.Success) return semantics;
        return ValidateAtmosphere(descriptor);
    }

    internal static RenderPackSdkValidationResult ValidateAssets(
        RenderPackDescriptor descriptor,
        IRenderPackAssets assets)
    {
        ArgumentNullException.ThrowIfNull(descriptor);
        ArgumentNullException.ThrowIfNull(assets);

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
        foreach (IGrouping<string, ShaderValidationRequest> group in
            requests.GroupBy(static request => request.Key, StringComparer.Ordinal))
        {
            string key = group.Key;
            if (!SafeRelativePath.IsValid(key))
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
                    if (destination.Length + count > RenderPackShaderAbi.MaximumShaderAssetBytes)
                    {
                        return Invalid(
                            $"Pack '{descriptor.Id}' asset '{key}' exceeds "
                            + $"the {RenderPackShaderAbi.MaximumShaderAssetBytes}-byte "
                            + "shader ceiling.");
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
            }
            catch (Exception exception) when (exception is IOException
                or UnauthorizedAccessException
                or ArgumentException
                or NotSupportedException)
            {
                return Invalid(
                    $"Pack '{descriptor.Id}' asset '{key}' could not be opened: "
                    + exception.GetBaseException().Message);
            }
        }

        return RenderPackSdkValidationResult.Valid();
    }

    private sealed record ShaderValidationRequest(
        string Key,
        RenderPackShaderStage Stage,
        RenderPassDeclaration? Pass,
        PipelineVariantDeclaration? Variant);

    private static RenderPackSdkValidationResult ValidateSemanticRoles(
        RenderPackDescriptor descriptor)
    {
        RenderPackSdkValidationResult unique = UniqueNonCustomSemantics(
            descriptor, descriptor.Resources, static value => value.Semantic,
            RenderResourceSemantic.Custom, "resource");
        if (!unique.Success) return unique;
        unique = UniqueNonCustomSemantics(
            descriptor, descriptor.Passes, static value => value.Semantic,
            RenderPassSemantic.CustomFullscreen, "pass");
        if (!unique.Success) return unique;
        unique = UniqueNonCustomSemantics(
            descriptor, descriptor.PipelineVariants, static value => value.Semantic,
            RenderPipelineVariantSemantic.Custom, "pipeline variant");
        if (!unique.Success) return unique;
        unique = UniqueNonCustomSemantics(
            descriptor, descriptor.QualityPresets, static value => value.Semantic,
            RenderQualitySemantic.Custom, "quality preset");
        if (!unique.Success) return unique;
        unique = UniqueNonCustomSemantics(
            descriptor, descriptor.Settings, static value => value.Semantic,
            RenderSettingSemantic.Custom, "setting");
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
            return RenderPackSdkValidationResult.Valid();

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

        bool usesMultiview = UsesMultiview(descriptor);
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

        if (descriptor.AtmospherePolicy?.DirectionalShadowLightElevationResponse
                is not { Count: >= 2 }
            || descriptor.AtmospherePolicy.VolumetricShaftSunElevationResponse
                is not { Count: >= 2 })
        {
            return Invalid(
                $"Pack '{descriptor.Id}' must declare directional-shadow and "
                + "volumetric-shaft elevation response curves.");
        }

        RenderPackSdkValidationResult shapes =
            ValidateAtmosphericSemanticShapes(descriptor);
        if (!shapes.Success) return shapes;
        return ValidateAtmosphericSemanticEdges(descriptor);
    }

    private static RenderPackSdkValidationResult ValidateDirectionalShadowProfile(
        RenderPackDescriptor descriptor)
    {
        if (descriptor.HighestTier < RenderPackTier.Tier2)
            return Invalid($"Pack '{descriptor.Id}' declares directional shadows below Tier2.");

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
                value.Semantic == RenderPassSemantic.DirectionalShadowDepth) != 1
            || descriptor.Resources.Count(static value =>
                value.Semantic == RenderResourceSemantic.DirectionalShadowDepth) != 1)
        {
            return Invalid(
                $"Pack '{descriptor.Id}' must declare exactly one directional-shadow "
                + "pass and resource.");
        }
        if (descriptor.SceneReplays.Count != 1
            || descriptor.SceneReplays[0].Semantic
                != RenderSceneReplaySemantic.OutdoorDirectionalShadowCasters)
        {
            return Invalid(
                $"Pack '{descriptor.Id}' must declare exactly one outdoor "
                + "directional-shadow replay.");
        }

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
        int expectedVariantCount = UsesMultiview(descriptor) ? 8 : semantics.Length;
        if (descriptor.PipelineVariants.Count != expectedVariantCount)
        {
            return Invalid(
                $"Pack '{descriptor.Id}' directional shadows require exactly {expectedVariantCount} "
                + "semantic pipeline variants.");
        }
        if (UsesMultiview(descriptor))
        {
            RenderPipelineVariantSemantic[] multiview =
            [
                RenderPipelineVariantSemantic.TerrainMultiviewDirectionalShadowCaster,
                RenderPipelineVariantSemantic.WorldOpaqueMultiviewDirectionalShadowCaster,
                RenderPipelineVariantSemantic.WorldAlphaCutoutMultiviewDirectionalShadowCaster,
            ];
            for (int i = 0; i < multiview.Length; i++)
            {
                PipelineVariantDeclaration? variant = descriptor.PipelineVariants
                    .SingleOrDefault(value => value.Semantic == multiview[i]);
                if (variant is null
                    || variant.BaseSemantic != bases[i]
                    || variant.CompatibleMaterials != materials[i]
                    || !variant.SemanticInputs.SequenceEqual(inputs[i]))
                {
                    return Invalid(
                        $"Pipeline variant semantic '{multiview[i]}' does not match the fixed "
                        + "multiview directional-shadow executor contract.");
                }
            }
        }
        for (int i = 0; i < semantics.Length; i++)
        {
            PipelineVariantDeclaration? variant = descriptor.PipelineVariants
                .SingleOrDefault(value => value.Semantic == semantics[i]);
            if (variant is null
                || variant.BaseSemantic != bases[i]
                || variant.CompatibleMaterials != materials[i]
                || !variant.SemanticInputs.SequenceEqual(inputs[i]))
            {
                return Invalid(
                    $"Pipeline variant semantic '{semantics[i]}' does not match the fixed "
                    + "directional-shadow executor contract.");
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

        RenderPassDeclaration[] outputs = descriptor.Passes
            .Where(static value => value.Hook == RenderPassHook.ToneMap
                && value.ResourceWrites.Count == 0)
            .ToArray();
        if (outputs.Length != 1
            || outputs[0].Semantic != RenderPassSemantic.CustomFullscreen
            || !outputs[0].SemanticInputs.Contains(RenderSemanticInput.WorldColor))
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
        return RenderPackSdkValidationResult.Valid();
    }

    private static RenderPackSdkValidationResult ValidateAtmosphericSemanticShapes(
        RenderPackDescriptor descriptor)
    {
        RenderPackSdkValidationResult result = Resource(
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
        if (UsesMultiview(descriptor))
        {
            result = Variant(
                RenderPipelineVariantSemantic.TerrainMultiviewDirectionalShadowCaster,
                RenderPipelineBaseSemantic.Terrain,
                RenderMaterialClass.Opaque,
                [RenderSemanticInput.CameraMatrices]);
            if (!result.Success) return result;
            result = Variant(
                RenderPipelineVariantSemantic.WorldOpaqueMultiviewDirectionalShadowCaster,
                RenderPipelineBaseSemantic.WorldMesh,
                RenderMaterialClass.Opaque | RenderMaterialClass.AnimatedOpaque,
                [RenderSemanticInput.CameraMatrices, RenderSemanticInput.ShadowCasterTransforms]);
            if (!result.Success) return result;
            result = Variant(
                RenderPipelineVariantSemantic.WorldAlphaCutoutMultiviewDirectionalShadowCaster,
                RenderPipelineBaseSemantic.WorldMesh,
                RenderMaterialClass.AlphaCutout | RenderMaterialClass.AnimatedAlphaCutout,
                [RenderSemanticInput.CameraMatrices, RenderSemanticInput.ShadowCasterTransforms]);
            if (!result.Success) return result;
        }
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

        return RenderPackSdkValidationResult.Valid();

        RenderPackSdkValidationResult Resource(
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
            return RenderPackSdkValidationResult.Valid();
        }

        RenderPackSdkValidationResult Variant(
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
            return RenderPackSdkValidationResult.Valid();
        }
    }

    private static RenderPackSdkValidationResult ValidateAtmosphericSemanticEdges(
        RenderPackDescriptor descriptor)
    {
        if (descriptor.Passes.Count != 8
            || descriptor.Resources.Count != 7
            || descriptor.PipelineVariants.Count != (UsesMultiview(descriptor) ? 8 : 5)
            || descriptor.SceneReplays.Count != 1)
        {
            return Invalid(
                $"Pack '{descriptor.Id}' requests the fixed atmospheric executor; API v1 "
                + "requires exactly 8 semantic passes, 7 semantic resources, the hinted semantic "
                + "pipeline variants, and 1 semantic scene replay.");
        }

        RenderPackSdkValidationResult Hook(RenderPassSemantic semantic, RenderPassHook hook)
        {
            RenderPassDeclaration pass = descriptor.Passes.Single(value => value.Semantic == semantic);
            return pass.Hook == hook
                ? RenderPackSdkValidationResult.Valid()
                : Invalid(
                    $"Pass semantic '{semantic}' must run at hook '{hook}', not '{pass.Hook}'.");
        }

        RenderPackSdkValidationResult result = Hook(
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
            [RenderResourceSemantic.DirectionalShadowDepth], RenderResourceSemantic.VolumetricShafts);
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

        RenderPackSdkValidationResult Edge(
            RenderPassSemantic passSemantic,
            IReadOnlyList<RenderResourceSemantic> reads,
            RenderResourceSemantic? output)
        {
            RenderPassDeclaration pass = descriptor.Passes.Single(value =>
                value.Semantic == passSemantic);
            RenderResourceSemantic[] actualReads = pass.ResourceReads
                .Select(id => descriptor.Resources.Single(resource => string.Equals(
                    resource.Id, id, StringComparison.OrdinalIgnoreCase)).Semantic)
                .ToArray();
            if (!actualReads.SequenceEqual(reads))
            {
                return Invalid(
                    $"Pass semantic '{passSemantic}' declares resource reads that do not "
                    + "match its renderer-owned execution edges.");
            }
            RenderResourceSemantic[] actualWrites = pass.ResourceWrites
                .Select(id => descriptor.Resources.Single(resource => string.Equals(
                    resource.Id, id, StringComparison.OrdinalIgnoreCase)).Semantic)
                .ToArray();
            RenderResourceSemantic[] expectedWrites = output is { } semantic ? [semantic] : [];
            return actualWrites.SequenceEqual(expectedWrites)
                ? RenderPackSdkValidationResult.Valid()
                : Invalid(
                    $"Pass semantic '{passSemantic}' declares a resource output that does "
                    + "not match its renderer-owned execution edge.");
        }
    }

    private static RenderPackSdkValidationResult UniqueNonCustomSemantics<T, TSemantic>(
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
        return RenderPackSdkValidationResult.Valid();
    }

    private static RenderPackSdkValidationResult ValidateUniqueIds(
        RenderPackDescriptor descriptor)
    {
        var ids = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        IEnumerable<(string Kind, string? Id)> declarations =
            descriptor.Resources.Select(static value => ("resource", value?.Id))
                .Concat(descriptor.Passes.Select(static value => ("pass", value?.Id)))
                .Concat(descriptor.SceneReplays.Select(
                    static value => ("scene replay", value?.Id)))
                .Concat(descriptor.PipelineVariants.Select(
                    static value => ("pipeline variant", value?.Id)))
                .Concat(descriptor.QualityPresets.Select(
                    static value => ("quality preset", value?.Id)))
                .Concat(descriptor.Settings.Select(static value => ("setting", value?.Id)));

        foreach ((string kind, string? id) in declarations)
        {
            if (!StableId.IsValid(id))
                return Invalid($"Pack '{descriptor.Id}' has an invalid {kind} id.");
            if (!ids.Add($"{kind}:{id}"))
                return Invalid($"Pack '{descriptor.Id}' declares duplicate {kind} id '{id}'.");
        }
        return RenderPackSdkValidationResult.Valid();
    }

    private static RenderPackSdkValidationResult ValidateResources(
        RenderPackDescriptor descriptor)
    {
        long declaredBytes = 0;
        foreach (RenderResourceDeclaration? resource in descriptor.Resources)
        {
            if (resource is null)
                return Invalid($"Pack '{descriptor.Id}' contains a null resource declaration.");
            if (!Enum.IsDefined(resource.Kind)
                || !Enum.IsDefined(resource.Format)
                || !Enum.IsDefined(resource.Lifetime)
                || !ValidFlags(resource.Usage, RenderResourceUsage.TransferDestination
                    | RenderResourceUsage.TransferSource
                    | RenderResourceUsage.Storage
                    | RenderResourceUsage.DepthAttachment
                    | RenderResourceUsage.ColorAttachment
                    | RenderResourceUsage.Sampled))
            {
                return Invalid($"Resource '{resource.Id}' declares an unknown enum value.");
            }
            if (resource.Kind == RenderResourceKind.Buffer
                || resource.Format == RenderFormatClass.StructuredData
                || resource.Usage.HasFlag(RenderResourceUsage.Storage))
            {
                return Invalid(
                    $"Resource '{resource.Id}' uses a buffer/structured/storage "
                    + "declaration reserved and unbindable in render-pack API v1.");
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
            if (resource.Kind == RenderResourceKind.Buffer && resource.SizeBytes <= 0)
                return Invalid($"Buffer resource '{resource.Id}' must declare positive SizeBytes.");
            if (resource.Kind != RenderResourceKind.Buffer)
            {
                if (resource.Extent is null)
                    return Invalid($"Image resource '{resource.Id}' has no extent.");
                RenderExtentDeclaration extent = resource.Extent;
                if (!Enum.IsDefined(extent.Mode)
                    || !FinitePositive(extent.Width)
                    || !FinitePositive(extent.Height)
                    || extent.Layers <= 0
                    || extent.Layers > MaximumImageLayers)
                {
                    return Invalid($"Image resource '{resource.Id}' has an invalid extent.");
                }
                if (extent.Mode == RenderExtentMode.AbsolutePixels
                    && (extent.Width > MaximumImageDimension
                        || extent.Height > MaximumImageDimension
                        || extent.Width != Math.Truncate(extent.Width)
                        || extent.Height != Math.Truncate(extent.Height)))
                {
                    return Invalid(
                        $"Image resource '{resource.Id}' exceeds or fractionalizes "
                        + "the absolute image limit.");
                }
                if (extent.Mode != RenderExtentMode.AbsolutePixels
                    && (extent.Width > 1.0 || extent.Height > 1.0))
                {
                    return Invalid(
                        $"Relative resource '{resource.Id}' must use a scale in (0, 1].");
                }
            }

            if (!TryAdd(ref declaredBytes, resource.EstimatedResidentBytes))
                return Invalid($"Pack '{descriptor.Id}' resource byte total overflows.");
        }
        if (declaredBytes > MaximumPackBytes)
        {
            return Invalid(
                $"Pack '{descriptor.Id}' declares {declaredBytes} resident bytes; "
                + $"the SDK ceiling is {MaximumPackBytes}.");
        }
        return RenderPackSdkValidationResult.Valid();
    }

    private static RenderPackSdkValidationResult ValidatePasses(
        RenderPackDescriptor descriptor)
    {
        HashSet<string> resources = descriptor.Resources
            .Select(static value => value.Id)
            .ToHashSet(StringComparer.OrdinalIgnoreCase);
        var written = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        Dictionary<string, RenderResourceDeclaration> resourceDeclarations =
            descriptor.Resources.ToDictionary(
                static value => value.Id,
                StringComparer.OrdinalIgnoreCase);
        int priorHook = int.MinValue;

        foreach (RenderPassDeclaration? pass in descriptor.Passes)
        {
            if (pass is null)
                return Invalid($"Pack '{descriptor.Id}' contains a null pass declaration.");
            if (!Enum.IsDefined(pass.Hook))
                return Invalid($"Pass '{pass.Id}' declares an unknown hook.");
            if ((int)pass.Hook < priorHook)
                return Invalid($"Pass '{pass.Id}' moves backward in renderer hook order.");
            priorHook = (int)pass.Hook;
            if (!SafeRelativePath.IsValid(pass.VertexShaderAsset)
                || !SafeRelativePath.IsValid(pass.FragmentShaderAsset))
            {
                return Invalid($"Pass '{pass.Id}' declares an unsafe shader asset key.");
            }
            if (pass.SemanticInputs is null
                || pass.ResourceReads is null
                || pass.ResourceWrites is null)
            {
                return Invalid($"Pass '{pass.Id}' contains a null binding list.");
            }
            if (pass.SemanticInputs.Any(static value => !Enum.IsDefined(value)))
                return Invalid($"Pass '{pass.Id}' declares an unknown semantic input.");
            if (pass.SemanticInputs.Count
                != pass.SemanticInputs.Distinct().Count())
            {
                return Invalid($"Pass '{pass.Id}' declares a duplicate semantic input.");
            }
            if (pass.ResourceReads.Count
                    != pass.ResourceReads.Distinct(StringComparer.OrdinalIgnoreCase).Count()
                || pass.ResourceWrites.Count
                    != pass.ResourceWrites.Distinct(StringComparer.OrdinalIgnoreCase).Count())
            {
                return Invalid($"Pass '{pass.Id}' declares a duplicate resource binding.");
            }
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
            foreach (string read in pass.ResourceReads)
            {
                if (!resources.Contains(read))
                    return Invalid($"Pass '{pass.Id}' reads unknown resource '{read}'.");
                if (!written.Contains(read))
                    return Invalid($"Pass '{pass.Id}' reads resource '{read}' before it is written.");
                if (!resourceDeclarations[read].Usage.HasFlag(RenderResourceUsage.Sampled))
                    return Invalid($"Pass '{pass.Id}' samples non-sampled resource '{read}'.");
            }
            foreach (string write in pass.ResourceWrites)
            {
                if (!resources.Contains(write))
                    return Invalid($"Pass '{pass.Id}' writes unknown resource '{write}'.");
                if (pass.ResourceReads.Contains(write, StringComparer.OrdinalIgnoreCase))
                    return Invalid($"Pass '{pass.Id}' reads and writes resource '{write}'.");
                RenderResourceDeclaration resource = resourceDeclarations[write];
                RenderResourceUsage attachment =
                    resource.Format == RenderFormatClass.DirectionalDepth
                        ? RenderResourceUsage.DepthAttachment
                        : RenderResourceUsage.ColorAttachment;
                if (!resource.Usage.HasFlag(attachment))
                    return Invalid($"Pass '{pass.Id}' writes non-attachment resource '{write}'.");
                written.Add(write);
            }

            int sampledInputs = pass.SemanticInputs.Count(static value =>
                value is RenderSemanticInput.WorldColor
                    or RenderSemanticInput.SceneDepth
                    or RenderSemanticInput.SceneNormals);
            bool shadowSemantic = pass.SemanticInputs.Contains(
                RenderSemanticInput.DirectionalShadowMaps);
            sampledInputs += pass.ResourceReads.Count(read =>
            {
                RenderResourceDeclaration resource = resourceDeclarations[read];
                return resource.Usage.HasFlag(RenderResourceUsage.Sampled)
                    && !(shadowSemantic
                        && resource.Format == RenderFormatClass.DirectionalDepth);
            });
            if (sampledInputs > RenderPackShaderAbi.SampledPassInputCapacity)
            {
                return Invalid(
                    $"Pass '{pass.Id}' requires {sampledInputs} sampled inputs; "
                    + "render-pack API v1 exposes at most four (TextureIndexA-D)."
                    + " DirectionalShadowMaps uses binding 6 and does not count.");
            }
        }
        return RenderPackSdkValidationResult.Valid();
    }

    private static RenderPackSdkValidationResult ValidateSemanticCapabilities(
        RenderPackDescriptor descriptor)
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
            if (required is { } capability
                && !descriptor.RequiredCapabilities.Contains(capability))
            {
                return Invalid(
                    $"Pack '{descriptor.Id}' declares semantic '{input}' but does not "
                    + $"require capability '{capability}'.");
            }
        }
        return RenderPackSdkValidationResult.Valid();
    }

    private static RenderPackSdkValidationResult ValidateReplays(
        RenderPackDescriptor descriptor)
    {
        const RenderCasterClass known = RenderCasterClass.Terrain
            | RenderCasterClass.OpaqueWorld
            | RenderCasterClass.AlphaCutoutWorld
            | RenderCasterClass.AnimatedOpaque
            | RenderCasterClass.AnimatedAlphaCutout;
        foreach (SceneReplayDeclaration? replay in descriptor.SceneReplays)
        {
            if (replay is null)
                return Invalid($"Pack '{descriptor.Id}' contains a null scene replay.");
            if (!Enum.IsDefined(replay.Semantic)
                || !ValidFlags(replay.CasterClasses, known))
            {
                return Invalid($"Scene replay '{replay.Id}' declares an unknown enum value.");
            }
            if (replay.ViewCount is <= 0 or > 4)
                return Invalid($"Scene replay '{replay.Id}' must request 1..4 views.");
            if (replay.CasterClasses == RenderCasterClass.None)
                return Invalid($"Scene replay '{replay.Id}' declares no caster classes.");
        }
        return RenderPackSdkValidationResult.Valid();
    }

    private static RenderPackSdkValidationResult ValidateVariants(
        RenderPackDescriptor descriptor)
    {
        const RenderMaterialClass known = RenderMaterialClass.Opaque
            | RenderMaterialClass.AlphaCutout
            | RenderMaterialClass.AnimatedOpaque
            | RenderMaterialClass.AnimatedAlphaCutout;
        foreach (PipelineVariantDeclaration? variant in descriptor.PipelineVariants)
        {
            if (variant is null)
                return Invalid($"Pack '{descriptor.Id}' contains a null pipeline variant.");
            if (!Enum.IsDefined(variant.BaseSemantic)
                || !ValidFlags(variant.CompatibleMaterials, known))
            {
                return Invalid($"Pipeline variant '{variant.Id}' declares an unknown enum value.");
            }
            if (!SafeRelativePath.IsValid(variant.VertexShaderAsset)
                || !SafeRelativePath.IsValid(variant.FragmentShaderAsset))
            {
                return Invalid(
                    $"Pipeline variant '{variant.Id}' declares an unsafe shader asset key.");
            }
            if (variant.CompatibleMaterials == RenderMaterialClass.None)
            {
                return Invalid(
                    $"Pipeline variant '{variant.Id}' declares no compatible materials.");
            }
            if (variant.SemanticInputs is null)
                return Invalid($"Pipeline variant '{variant.Id}' has a null semantic-input list.");
            if (variant.SemanticInputs.Any(static value => !Enum.IsDefined(value)))
            {
                return Invalid(
                    $"Pipeline variant '{variant.Id}' declares an unknown semantic input.");
            }
        }
        return RenderPackSdkValidationResult.Valid();
    }

    private static RenderPackSdkValidationResult ValidateSettings(
        RenderPackDescriptor descriptor)
    {
        if (descriptor.Settings.Count > RenderPackShaderAbi.PackSettingScalarCapacity)
        {
            return Invalid(
                $"Pack '{descriptor.Id}' declares {descriptor.Settings.Count} settings; "
                + "render-pack API v1 exposes at most "
                + $"{RenderPackShaderAbi.PackSettingScalarCapacity} shader setting slots.");
        }
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
            {
                return Invalid($"Setting '{setting.Id}' has invalid bounds.");
            }
            if (setting.Minimum is { } minimum
                && setting.Maximum is { } maximum
                && minimum > maximum)
            {
                return Invalid($"Setting '{setting.Id}' has an inverted range.");
            }
            if (setting.Kind == RenderSettingKind.Choice
                && (setting.Choices.Count == 0
                    || !setting.Choices.Contains(setting.DefaultValue, StringComparer.Ordinal)))
            {
                return Invalid(
                    $"Choice setting '{setting.Id}' has no choices or an unknown default.");
            }
            if (!ValidSettingValue(setting, setting.DefaultValue))
                return Invalid($"Setting '{setting.Id}' has an invalid default value.");
        }
        return RenderPackSdkValidationResult.Valid();
    }

    private static RenderPackSdkValidationResult ValidatePresets(
        RenderPackDescriptor descriptor)
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
                static value => value.Id,
                StringComparer.OrdinalIgnoreCase);

        foreach (RenderQualityPreset? preset in descriptor.QualityPresets)
        {
            if (preset is null)
                return Invalid($"Pack '{descriptor.Id}' contains a null quality preset.");
            if (string.IsNullOrWhiteSpace(preset.DisplayName))
                return Invalid($"Quality preset '{preset.Id}' has no display name.");
            if (preset.RequiredCapabilities is null
                || preset.ResourceOverrides is null
                || preset.SettingOverrides is null)
            {
                return Invalid($"Quality preset '{preset.Id}' contains a null declaration list.");
            }
            if (preset.RequiredCapabilities.Any(static value => !Enum.IsDefined(value)))
                return Invalid($"Quality preset '{preset.Id}' declares an unknown capability.");
            const RenderQualityExecutionHints supportedHints =
                RenderQualityExecutionHints.FusedAtmosphericPostProcess
                | RenderQualityExecutionHints.MultiviewDirectionalShadowCascades;
            if ((preset.ExecutionHints & ~supportedHints) != 0)
                return Invalid($"Quality preset '{preset.Id}' declares an unknown execution hint.");
            if ((preset.ExecutionHints & RenderQualityExecutionHints.MultiviewDirectionalShadowCascades) != 0
                && (preset.Semantic != RenderQualitySemantic.Low
                    || !preset.RequiredCapabilities.Contains(
                        RenderCapability.MultiviewDirectionalShadowCascades)))
            {
                return Invalid(
                    $"Quality preset '{preset.Id}' must be Low and require multiview directional-shadow capability.");
            }
            if (preset.MaxResidentGpuBytes is < 0 or > MaximumPackBytes)
                return Invalid($"Quality preset '{preset.Id}' exceeds the pack memory ceiling.");
            if (!FiniteNonNegative(preset.MaxIncrementalGpuMillisecondsP50)
                || !FiniteNonNegative(preset.MaxIncrementalGpuMillisecondsP99)
                || !FiniteNonNegative(preset.MaxIncrementalCpuMillisecondsP50)
                || !FiniteNonNegative(preset.MaxIncrementalCpuMillisecondsP99)
                || preset.MaxIncrementalGpuMillisecondsP50
                    > preset.MaxIncrementalGpuMillisecondsP99
                || preset.MaxIncrementalCpuMillisecondsP50
                    > preset.MaxIncrementalCpuMillisecondsP99)
            {
                return Invalid($"Quality preset '{preset.Id}' has invalid performance budgets.");
            }
            foreach (RenderQualityResourceOverride? value in preset.ResourceOverrides)
            {
                if (value is null)
                    return Invalid($"Quality preset '{preset.Id}' contains a null resource override.");
                if (!resources.Contains(value.ResourceId))
                {
                    return Invalid(
                        $"Quality preset '{preset.Id}' overrides unknown resource "
                        + $"'{value.ResourceId}'.");
                }
                if (value.SizeBytes < 0 || value.EstimatedResidentBytes < 0)
                    return Invalid($"Quality preset '{preset.Id}' declares negative resource bytes.");
                if (value.Extent is { } extent && !ValidExtent(extent))
                {
                    return Invalid(
                        $"Quality preset '{preset.Id}' declares an invalid "
                        + $"extent for resource '{value.ResourceId}'.");
                }
            }
            var overriddenSettings = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            foreach (RenderQualitySettingOverride? value in preset.SettingOverrides)
            {
                if (value is null)
                    return Invalid($"Quality preset '{preset.Id}' contains a null setting override.");
                if (!settings.Contains(value.SettingId))
                {
                    return Invalid(
                        $"Quality preset '{preset.Id}' overrides unknown setting "
                        + $"'{value.SettingId}'.");
                }
                if (!overriddenSettings.Add(value.SettingId))
                {
                    return Invalid(
                        $"Quality preset '{preset.Id}' overrides setting "
                        + $"'{value.SettingId}' more than once.");
                }
                if (!ValidSettingValue(
                    settingDeclarations[value.SettingId],
                    value.Value))
                {
                    return Invalid(
                        $"Quality preset '{preset.Id}' supplies an invalid value "
                        + $"for setting '{value.SettingId}'.");
                }
            }
        }
        return RenderPackSdkValidationResult.Valid();
    }

    private static RenderPackSdkValidationResult ValidateAtmosphere(
        RenderPackDescriptor descriptor)
    {
        AtmospherePolicyDeclaration? policy = descriptor.AtmospherePolicy;
        if (policy is null)
            return RenderPackSdkValidationResult.Valid();
        if (policy.SunElevationResponse is null
            || policy.ActiveDayGroupMultipliers is null
            || policy.DirectionalShadowLightElevationResponse is null
            || policy.VolumetricShaftSunElevationResponse is null)
        {
            return Invalid($"Pack '{descriptor.Id}' has a null atmosphere-policy list.");
        }

        RenderPackSdkValidationResult curve = ValidateCurve(
            policy.SunElevationResponse,
            "sun-elevation");
        if (!curve.Success) return curve;
        curve = ValidateCurve(
            policy.DirectionalShadowLightElevationResponse,
            "directional-shadow light-elevation",
            unitInterval: true);
        if (!curve.Success) return curve;
        curve = ValidateDirectionalShadowHorizon(
            policy.DirectionalShadowLightElevationResponse);
        if (!curve.Success) return curve;
        curve = ValidateCurve(
            policy.VolumetricShaftSunElevationResponse,
            "volumetric-shaft sun-elevation",
            unitInterval: true);
        if (!curve.Success) return curve;

        var groups = new HashSet<int>();
        foreach (ActiveDayGroupMultiplier? value in policy.ActiveDayGroupMultipliers)
        {
            if (value is null
                || !groups.Add(value.ActiveDayGroup)
                || !FiniteNonNegative(value.Multiplier))
            {
                return Invalid(
                    $"Pack '{descriptor.Id}' has an invalid active-day-group mapping.");
            }
        }
        return RenderPackSdkValidationResult.Valid();

        RenderPackSdkValidationResult ValidateCurve(
            IReadOnlyList<SunElevationResponsePoint> points,
            string name,
            bool unitInterval = false)
        {
            double priorElevation = double.NegativeInfinity;
            foreach (SunElevationResponsePoint? point in points)
            {
                if (point is null
                    || !double.IsFinite(point.ElevationDegrees)
                    || point.ElevationDegrees is < -90 or > 90
                    || !FiniteNonNegative(point.Multiplier)
                    || (unitInterval && point.Multiplier > 1)
                    || point.ElevationDegrees <= priorElevation)
                {
                    return Invalid(
                        $"Pack '{descriptor.Id}' has an invalid {name} response curve.");
                }
                priorElevation = point.ElevationDegrees;
            }
            return RenderPackSdkValidationResult.Valid();
        }

        RenderPackSdkValidationResult ValidateDirectionalShadowHorizon(
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
                    : RenderPackSdkValidationResult.Valid();

            RenderPackSdkValidationResult InvalidHorizon() => Invalid(
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

    private static bool FinitePositive(double value) =>
        double.IsFinite(value) && value > 0;

    private static bool FiniteNonNegative(double value) =>
        double.IsFinite(value) && value >= 0;

    private static bool UsesMultiview(RenderPackDescriptor descriptor) =>
        descriptor.QualityPresets.Any(preset =>
            (preset.ExecutionHints
                & RenderQualityExecutionHints.MultiviewDirectionalShadowCascades) != 0);

    private static bool ValidSettingValue(
        RenderSettingDeclaration setting,
        string? value) =>
        RenderPackSettingValueCodec.TryEncode(setting, value, out _);

    private static bool ValidExtent(RenderExtentDeclaration extent)
    {
        if (!Enum.IsDefined(extent.Mode)
            || !FinitePositive(extent.Width)
            || !FinitePositive(extent.Height)
            || extent.Layers is <= 0 or > MaximumImageLayers)
        {
            return false;
        }
        return extent.Mode == RenderExtentMode.AbsolutePixels
            ? extent.Width <= MaximumImageDimension
                && extent.Height <= MaximumImageDimension
                && extent.Width == Math.Truncate(extent.Width)
                && extent.Height == Math.Truncate(extent.Height)
            : extent.Width <= 1.0 && extent.Height <= 1.0;
    }

    private static bool ValidFlags<T>(T value, T known)
        where T : struct, Enum
    {
        ulong actual = unchecked((ulong)Convert.ToInt64(value));
        ulong permitted = unchecked((ulong)Convert.ToInt64(known));
        return (actual & ~permitted) == 0;
    }

    private static bool TryAdd(ref long total, long value)
    {
        if (value > long.MaxValue - total)
            return false;
        total += value;
        return true;
    }

    private static RenderPackSdkValidationResult Invalid(string reason) =>
        RenderPackSdkValidationResult.Invalid(reason);
}
