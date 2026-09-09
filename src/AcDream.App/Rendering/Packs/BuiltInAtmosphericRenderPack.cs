using AcDream.Plugin.Abstractions.Rendering;

namespace AcDream.App.Rendering.Packs;

internal static class BuiltInAtmosphericRenderPack
{
    internal const string Id = "acdream.atmospheric";

    internal static RenderPackDescriptor Descriptor { get; } = new RenderPackDescriptor(
        Id,
        "Atmospheric Rendering",
        new Version(1, 0, 0),
        RenderPackApi.Current,
        RenderPackTier.Tier2Plus,
        [
            RenderCapability.MainWorldColorIntermediate,
            RenderCapability.FullscreenPasses,
            RenderCapability.SceneDepthSampling,
            RenderCapability.AuthoredSunDirection,
            RenderCapability.AuthoredSunScreenPosition,
            RenderCapability.AuthoredWeather,
            RenderCapability.DirectionalShadowMaps,
            RenderCapability.OutdoorDirectionalShadowCasterReplay,
            RenderCapability.AnimatedCasterTransforms,
            RenderCapability.AlphaCutoutShadowCasters,
            RenderCapability.AuthoredCelestialDirectionalLight,
        ],
        [RenderCapability.GpuTimestampQueries],
        Resources(),
        Passes(),
        SceneReplays(),
        PipelineVariants(),
        QualityPresets(),
        Settings(),
        AtmospherePolicy())
        {
            FeatureSummary = "Filmic HDR atmosphere, moving sun-and-moon shadows from terrain, "
                + "trees, buildings, players, and monsters, plus optional volumetric shafts.",
        };

    internal static IRenderPackAssets CreateAssets(string shaderDirectory) =>
        new DirectoryRenderPackAssets(shaderDirectory);

    private static IReadOnlyList<RenderResourceDeclaration> Resources() =>
    [
        Image("world-hdr", RenderResourceSemantic.MainWorldHdr,
            RenderFormatClass.HdrColor, 1.0, 1.0, 32L * 1024 * 1024),
        Image("bloom-a", RenderResourceSemantic.BloomPing,
            RenderFormatClass.HdrColor, 0.5, 0.5, 8L * 1024 * 1024),
        Image("bloom-b", RenderResourceSemantic.BloomPong,
            RenderFormatClass.HdrColor, 0.5, 0.5, 8L * 1024 * 1024),
        Image("sun-mask", RenderResourceSemantic.SunOcclusionMask,
            RenderFormatClass.SingleChannel, 0.25, 0.25, 2L * 1024 * 1024),
        Image("sun-rays", RenderResourceSemantic.SunRays,
            RenderFormatClass.HdrColor, 0.25, 0.25, 2L * 1024 * 1024),
        new RenderResourceDeclaration(
            "directional-shadow-depth",
            RenderResourceKind.Image2DArray,
            RenderFormatClass.DirectionalDepth,
            new RenderExtentDeclaration(RenderExtentMode.AbsolutePixels, 1024, 1024, Layers: 2),
            SizeBytes: 0,
            RenderResourceUsage.Sampled | RenderResourceUsage.DepthAttachment,
            RenderResourceLifetime.ActivePack,
            EstimatedResidentBytes: 8L * 1024 * 1024)
            with { Semantic = RenderResourceSemantic.DirectionalShadowDepth },
        Image("volumetric", RenderResourceSemantic.VolumetricShafts,
            RenderFormatClass.HdrColor, 0.25, 0.25, 2L * 1024 * 1024),
    ];

    private static IReadOnlyList<RenderPassDeclaration> Passes() =>
    [
        Pass(
            "directional-shadow-depth",
            RenderPassSemantic.DirectionalShadowDepth,
            RenderPassHook.ShadowDepthBeforeWorld,
            "directional_shadow_world_opaque.vert.spv",
            "directional_shadow_world_opaque.frag.spv",
            [RenderSemanticInput.CameraMatrices,
                RenderSemanticInput.SelectedCelestialDirectionalLight,
                RenderSemanticInput.ShadowCasterTransforms, RenderSemanticInput.ActiveDayGroup,
                RenderSemanticInput.Weather],
            [],
            ["directional-shadow-depth"]),
        Pass(
            "sun-occlusion",
            RenderPassSemantic.SunOcclusion,
            RenderPassHook.AtmosphereBeforeToneMap,
            "atmospheric_sun_occlusion.vert.spv",
            "atmospheric_sun_occlusion.frag.spv",
            [RenderSemanticInput.SceneDepth, RenderSemanticInput.SunScreenPosition,
                RenderSemanticInput.ActiveDayGroup, RenderSemanticInput.Weather],
            [],
            ["sun-mask"]),
        Pass(
            "sun-rays",
            RenderPassSemantic.SunRays,
            RenderPassHook.AtmosphereBeforeToneMap,
            "atmospheric_sun_rays.vert.spv",
            "atmospheric_sun_rays.frag.spv",
            [RenderSemanticInput.SunScreenPosition, RenderSemanticInput.FrameTime],
            ["sun-mask"],
            ["sun-rays"]),
        Pass(
            "volumetric-shafts",
            RenderPassSemantic.VolumetricShafts,
            RenderPassHook.AtmosphereBeforeToneMap,
            "atmospheric_volumetric.vert.spv",
            "atmospheric_volumetric.frag.spv",
            [RenderSemanticInput.SceneDepth, RenderSemanticInput.CameraMatrices,
                RenderSemanticInput.SunDirection, RenderSemanticInput.DirectionalShadowMaps,
                RenderSemanticInput.ActiveDayGroup, RenderSemanticInput.Weather],
            ["directional-shadow-depth"],
            ["volumetric"]),
        Pass(
            "bloom-downsample",
            RenderPassSemantic.BloomDownsample,
            RenderPassHook.AtmosphereBeforeToneMap,
            "atmospheric_bloom_downsample.vert.spv",
            "atmospheric_bloom_downsample.frag.spv",
            [RenderSemanticInput.WorldColor],
            ["sun-rays", "volumetric"],
            ["bloom-a"]),
        Pass(
            "bloom-blur-horizontal",
            RenderPassSemantic.BloomBlurHorizontal,
            RenderPassHook.AtmosphereBeforeToneMap,
            "atmospheric_bloom_blur.vert.spv",
            "atmospheric_bloom_blur.frag.spv",
            [RenderSemanticInput.FrameTime],
            ["bloom-a"],
            ["bloom-b"]),
        Pass(
            "bloom-blur-vertical",
            RenderPassSemantic.BloomBlurVertical,
            RenderPassHook.AtmosphereBeforeToneMap,
            "atmospheric_bloom_blur.vert.spv",
            "atmospheric_bloom_blur.frag.spv",
            [RenderSemanticInput.FrameTime],
            ["bloom-b"],
            ["bloom-a"]),
        Pass(
            "filmic-composite",
            RenderPassSemantic.FilmicComposite,
            RenderPassHook.ToneMap,
            "atmospheric_filmic.vert.spv",
            "atmospheric_filmic.frag.spv",
            [RenderSemanticInput.WorldColor, RenderSemanticInput.FrameTime],
            ["bloom-a", "sun-rays", "volumetric"],
            []),
    ];

    private static IReadOnlyList<SceneReplayDeclaration> SceneReplays() =>
    [
        new SceneReplayDeclaration(
            "outdoor-directional-shadow-casters",
            RenderSceneReplaySemantic.OutdoorDirectionalShadowCasters,
            RenderCasterClass.Terrain
                | RenderCasterClass.OpaqueWorld
                | RenderCasterClass.AlphaCutoutWorld
                | RenderCasterClass.AnimatedOpaque
                | RenderCasterClass.AnimatedAlphaCutout,
            ViewCount: 4),
    ];

    private static IReadOnlyList<PipelineVariantDeclaration> PipelineVariants() =>
    [
        Variant("terrain-shadow-caster", RenderPipelineVariantSemantic.TerrainDirectionalShadowCaster,
            RenderPipelineBaseSemantic.Terrain,
            "directional_shadow_terrain.vert.spv", "directional_shadow_terrain.frag.spv",
            RenderMaterialClass.Opaque,
            [RenderSemanticInput.CameraMatrices]),
        Variant("world-shadow-opaque", RenderPipelineVariantSemantic.WorldOpaqueDirectionalShadowCaster,
            RenderPipelineBaseSemantic.WorldMesh,
            "directional_shadow_world_opaque.vert.spv", "directional_shadow_world_opaque.frag.spv",
            RenderMaterialClass.Opaque | RenderMaterialClass.AnimatedOpaque,
            [RenderSemanticInput.CameraMatrices, RenderSemanticInput.ShadowCasterTransforms]),
        Variant("world-shadow-cutout", RenderPipelineVariantSemantic.WorldAlphaCutoutDirectionalShadowCaster,
            RenderPipelineBaseSemantic.WorldMesh,
            "directional_shadow_world_cutout.vert.spv", "directional_shadow_world_cutout.frag.spv",
            RenderMaterialClass.AlphaCutout | RenderMaterialClass.AnimatedAlphaCutout,
            [RenderSemanticInput.CameraMatrices, RenderSemanticInput.ShadowCasterTransforms]),
        Variant("terrain-shadow-caster-multiview", RenderPipelineVariantSemantic.TerrainMultiviewDirectionalShadowCaster,
            RenderPipelineBaseSemantic.Terrain,
            "directional_shadow_terrain_multiview.vert.spv", "directional_shadow_terrain_multiview.frag.spv",
            RenderMaterialClass.Opaque,
            [RenderSemanticInput.CameraMatrices]),
        Variant("world-shadow-opaque-multiview", RenderPipelineVariantSemantic.WorldOpaqueMultiviewDirectionalShadowCaster,
            RenderPipelineBaseSemantic.WorldMesh,
            "directional_shadow_world_opaque_multiview.vert.spv", "directional_shadow_world_opaque_multiview.frag.spv",
            RenderMaterialClass.Opaque | RenderMaterialClass.AnimatedOpaque,
            [RenderSemanticInput.CameraMatrices, RenderSemanticInput.ShadowCasterTransforms]),
        Variant("world-shadow-cutout-multiview", RenderPipelineVariantSemantic.WorldAlphaCutoutMultiviewDirectionalShadowCaster,
            RenderPipelineBaseSemantic.WorldMesh,
            "directional_shadow_world_cutout_multiview.vert.spv", "directional_shadow_world_cutout_multiview.frag.spv",
            RenderMaterialClass.AlphaCutout | RenderMaterialClass.AnimatedAlphaCutout,
            [RenderSemanticInput.CameraMatrices, RenderSemanticInput.ShadowCasterTransforms]),
        Variant("terrain-shadow-receiver", RenderPipelineVariantSemantic.TerrainDirectionalShadowReceiver,
            RenderPipelineBaseSemantic.Terrain,
            "terrain_atmospheric.vert.spv", "terrain_atmospheric.frag.spv",
            RenderMaterialClass.Opaque,
            [RenderSemanticInput.DirectionalShadowMaps,
                RenderSemanticInput.SelectedCelestialDirectionalLight]),
        Variant("world-shadow-receiver", RenderPipelineVariantSemantic.WorldDirectionalShadowReceiver,
            RenderPipelineBaseSemantic.WorldMesh,
            "mesh_atmospheric.vert.spv", "mesh_atmospheric.frag.spv",
            RenderMaterialClass.Opaque | RenderMaterialClass.AlphaCutout
                | RenderMaterialClass.AnimatedOpaque | RenderMaterialClass.AnimatedAlphaCutout,
            [RenderSemanticInput.DirectionalShadowMaps,
                RenderSemanticInput.SelectedCelestialDirectionalLight]),
    ];

    private static IReadOnlyList<RenderQualityPreset> QualityPresets() =>
    [
        Preset("low", "Low", RenderQualitySemantic.Low,
            64, 2.0, 3.0, 0.15, 0.50, 768, 2, 72, 0.25) with
            {
                ExecutionHints =
                    RenderQualityExecutionHints.MultiviewDirectionalShadowCascades,
            },
        Preset("medium", "Medium", RenderQualitySemantic.Medium,
            128, 3.25, 4.50, 0.25, 0.75, 1536, 3, 144, 0.5),
        Preset("high", "High", RenderQualitySemantic.High,
            256, 4.50, 6.00, 0.35, 1.00, 2048, 4, 240, 0.5),
        Preset("auto", "Auto", RenderQualitySemantic.Automatic,
            128, 3.25, 4.50, 0.25, 0.75, 1536, 3, 144, 0.5)
            with
            {
                SettingOverrides =
                [
                    new RenderQualitySettingOverride("automatic-quality", "true"),
                    new RenderQualitySettingOverride("volumetric-strength", "0.35"),
                    new RenderQualitySettingOverride("volumetric-ray-steps", "40"),
                    new RenderQualitySettingOverride("sun-shadow-strength", "0.72"),
                    new RenderQualitySettingOverride("sun-shadow-reach-metres", "144"),
                    new RenderQualitySettingOverride("sun-shadow-pcf-taps", "9"),
                    new RenderQualitySettingOverride("sun-ray-strength", "0.55"),
                ],
                AutoEligible = false,
            },
    ];

    private static IReadOnlyList<RenderSettingDeclaration> Settings() =>
    [
        Float("bloom-strength", "Bloom strength", RenderSettingSemantic.BloomStrength,
            0.65, 0, 2, 0.05),
        Float("filmic-strength", "Filmic tonemap strength", RenderSettingSemantic.FilmicStrength,
            1.0, 0, 1, 0.05),
        Float("exposure", "Exposure", RenderSettingSemantic.Exposure,
            0.80, 0.25, 4, 0.05),
        Float("grade-saturation", "Colour saturation", RenderSettingSemantic.GradeSaturation,
            1.0, 0, 2, 0.05),
        Float("grade-contrast", "Colour contrast", RenderSettingSemantic.GradeContrast,
            1.0, 0.5, 2, 0.05),
        Float("vignette-strength", "Vignette strength", RenderSettingSemantic.VignetteStrength,
            0.245, 0, 1, 0.005),
        Float("sun-ray-strength", "Sun-ray strength", RenderSettingSemantic.SunRayStrength,
            0.55, 0, 2, 0.05),
        Float("sun-shadow-strength", "Directional-shadow strength",
            RenderSettingSemantic.DirectionalShadowStrength, 0.72, 0, 1, 0.02),
        Integer("sun-shadow-reach-metres", "Directional-shadow reach (metres)",
            RenderSettingSemantic.DirectionalShadowReachMetres, 240, 16, 240, 1),
        Choice("sun-shadow-pcf-taps", "Directional-shadow filter taps",
            RenderSettingSemantic.DirectionalShadowPcfTaps, "9", ["1", "9", "25"]),
        Float("volumetric-strength", "Volumetric-shaft strength",
            RenderSettingSemantic.VolumetricStrength, 0.35, 0, 1, 0.01),
        Integer("volumetric-ray-steps", "Volumetric ray-march steps",
            RenderSettingSemantic.VolumetricRayMarchSteps, 40, 8, 64, 8),
        new RenderSettingDeclaration(
            "automatic-quality",
            "Automatic quality",
            RenderSettingKind.Boolean,
            "false",
            null,
            null,
            null,
            [])
            with { Semantic = RenderSettingSemantic.AutomaticQuality },
        new RenderSettingDeclaration(
            "wind-enabled",
            "Foliage wind",
            RenderSettingKind.Boolean,
            "true",
            null,
            null,
            null,
            [])
            with { Semantic = RenderSettingSemantic.WindEnabled },
        Float("wind-strength", "Foliage wind strength", RenderSettingSemantic.WindStrength,
            1.0, 0, 2, 0.05),
        Float("wind-direction-degrees", "Foliage wind direction (degrees)",
            RenderSettingSemantic.WindDirectionDegrees, 225, 0, 360, 5),
        Float("wind-lean-metres", "Foliage lean amplitude (metres)",
            RenderSettingSemantic.WindLeanMetres, 0.25, 0, 1, 0.01),
        Float("wind-branch-metres", "Foliage branch-swing amplitude (metres)",
            RenderSettingSemantic.WindBranchMetres, 0.15, 0, 1, 0.01),
        Float("wind-flutter-metres", "Foliage flutter amplitude (metres)",
            RenderSettingSemantic.WindFlutterMetres, 0.05, 0, 0.5, 0.005),
        Float("wind-canopy-height-metres", "Foliage canopy height (metres)",
            RenderSettingSemantic.WindCanopyHeightMetres, 8, 2, 30, 0.5),
    ];

    private static AtmospherePolicyDeclaration AtmospherePolicy() => new(
        [
            new SunElevationResponsePoint(-90, 0),
            new SunElevationResponsePoint(-3, 0),
            new SunElevationResponsePoint(4, 1),
            new SunElevationResponsePoint(22, 0.75),
            new SunElevationResponsePoint(55, 0),
            new SunElevationResponsePoint(90, 0),
        ],
        [
            new ActiveDayGroupMultiplier(0, 1.0),
            new ActiveDayGroupMultiplier(1, 0.35),
            new ActiveDayGroupMultiplier(2, 0.20),
        ])
        {
            DirectionalShadowLightElevationResponse =
            [
                new SunElevationResponsePoint(-90, 0),
                new SunElevationResponsePoint(1, 0),
                new SunElevationResponsePoint(12, 1),
                new SunElevationResponsePoint(90, 1),
            ],
            VolumetricShaftSunElevationResponse =
            [
                new SunElevationResponsePoint(-90, 0),
                new SunElevationResponsePoint(0, 0),
                new SunElevationResponsePoint(6, 1),
                new SunElevationResponsePoint(18, 1),
                new SunElevationResponsePoint(70, 0),
                new SunElevationResponsePoint(90, 0),
            ],
            FoliageWindByWeather =
            [
                new FoliageWindWeatherPoint("Clear", 0.25, 0.15),
                new FoliageWindWeatherPoint("Overcast", 0.60, 0.35),
                new FoliageWindWeatherPoint("Rain", 0.85, 0.60),
                new FoliageWindWeatherPoint("Snow", 0.35, 0.20),
                new FoliageWindWeatherPoint("Storm", 1.00, 0.75),
            ],
        };

    private static RenderResourceDeclaration Image(
        string id,
        RenderResourceSemantic semantic,
        RenderFormatClass format,
        double widthScale,
        double heightScale,
        long estimatedBytes) => new RenderResourceDeclaration(
            id,
            RenderResourceKind.Image2D,
            format,
            new RenderExtentDeclaration(
                RenderExtentMode.RelativeToMainWorld,
                widthScale,
                heightScale),
            SizeBytes: 0,
            RenderResourceUsage.Sampled | RenderResourceUsage.ColorAttachment,
            RenderResourceLifetime.ActivePack,
            estimatedBytes)
            { Semantic = semantic };

    private static RenderPassDeclaration Pass(
        string id,
        RenderPassSemantic semantic,
        RenderPassHook hook,
        string vertex,
        string fragment,
        IReadOnlyList<RenderSemanticInput> semantics,
        IReadOnlyList<string> reads,
        IReadOnlyList<string> writes) =>
        new(id, hook, vertex, fragment, semantics, reads, writes)
        {
            Semantic = semantic,
        };

    private static PipelineVariantDeclaration Variant(
        string id,
        RenderPipelineVariantSemantic variantSemantic,
        RenderPipelineBaseSemantic semantic,
        string vertex,
        string fragment,
        RenderMaterialClass materials,
        IReadOnlyList<RenderSemanticInput> inputs) =>
        new(id, semantic, vertex, fragment, materials, inputs)
        {
            Semantic = variantSemantic,
        };

    private static RenderQualityPreset Preset(
        string id,
        string displayName,
        RenderQualitySemantic semantic,
        long maxMiB,
        double gpuP50,
        double gpuP99,
        double cpuP50,
        double cpuP99,
        int shadowResolution,
        int cascades,
        int shadowReachMetres,
        double postScale) => new RenderQualityPreset(
            id,
            displayName,
            semantic == RenderQualitySemantic.Low
                ? [RenderCapability.DirectionalShadowMaps,
                    RenderCapability.MultiviewDirectionalShadowCascades]
                : [RenderCapability.DirectionalShadowMaps],
            [
                Override("directional-shadow-depth", shadowResolution, shadowResolution, cascades,
                    4L * shadowResolution * shadowResolution * cascades),
                RelativeOverride("bloom-a", postScale),
                RelativeOverride("bloom-b", postScale),
                RelativeOverride("sun-mask", id == "low" ? 0.25 : 0.5),
                RelativeOverride("sun-rays", id == "low" ? 0.25 : 0.5),
                RelativeOverride("volumetric", id == "high" ? 0.5 : 0.25),
            ],
            [
                new RenderQualitySettingOverride("automatic-quality", "false"),
                new RenderQualitySettingOverride("volumetric-strength", id == "low" ? "0" : "0.35"),
                new RenderQualitySettingOverride(
                    "volumetric-ray-steps",
                    semantic switch
                    {
                        RenderQualitySemantic.Low => "24",
                        RenderQualitySemantic.High => "56",
                        _ => "40",
                    }),
                new RenderQualitySettingOverride("sun-shadow-strength", "0.72"),
                new RenderQualitySettingOverride("sun-shadow-reach-metres", shadowReachMetres.ToString()),
                new RenderQualitySettingOverride(
                    "sun-shadow-pcf-taps",
                    semantic switch
                    {
                        RenderQualitySemantic.Low => "1",
                        RenderQualitySemantic.High => "25",
                        _ => "9",
                    }),
                new RenderQualitySettingOverride("sun-ray-strength", id == "low" ? "0.4" : "0.55"),
                new RenderQualitySettingOverride("wind-flutter-metres", id == "low" ? "0" : "0.05"),
            ],
            maxMiB * 1024 * 1024,
            gpuP50,
            gpuP99,
            cpuP50,
            cpuP99)
            { Semantic = semantic };

    private static RenderQualityResourceOverride Override(
        string id,
        int width,
        int height,
        int layers,
        long bytes) => new(
            id,
            new RenderExtentDeclaration(RenderExtentMode.AbsolutePixels, width, height, layers),
            SizeBytes: 0,
            EstimatedResidentBytes: bytes);

    private static RenderQualityResourceOverride RelativeOverride(string id, double scale) =>
        new(
            id,
            new RenderExtentDeclaration(RenderExtentMode.RelativeToMainWorld, scale, scale),
            SizeBytes: 0,
            EstimatedResidentBytes: 0);

    private static RenderSettingDeclaration Float(
        string id,
        string displayName,
        RenderSettingSemantic semantic,
        double defaultValue,
        double min,
        double max,
        double step) => new RenderSettingDeclaration(
            id,
            displayName,
            RenderSettingKind.Float,
            defaultValue.ToString(System.Globalization.CultureInfo.InvariantCulture),
            min,
            max,
            step,
            [])
            { Semantic = semantic };

    private static RenderSettingDeclaration Integer(
        string id,
        string displayName,
        RenderSettingSemantic semantic,
        int defaultValue,
        int min,
        int max,
        int step) => new RenderSettingDeclaration(
            id,
            displayName,
            RenderSettingKind.Integer,
            defaultValue.ToString(System.Globalization.CultureInfo.InvariantCulture),
            min,
            max,
            step,
            [])
            { Semantic = semantic };

    private static RenderSettingDeclaration Choice(
        string id,
        string displayName,
        RenderSettingSemantic semantic,
        string defaultValue,
        IReadOnlyList<string> choices) => new RenderSettingDeclaration(
            id,
            displayName,
            RenderSettingKind.Choice,
            defaultValue,
            null,
            null,
            null,
            choices)
            { Semantic = semantic };
}

internal sealed class DirectoryRenderPackAssets : IRenderPackAssets
{
    private readonly string _root;

    internal DirectoryRenderPackAssets(string root)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(root);
        _root = Path.GetFullPath(root);
    }

    public Stream OpenRead(string assetKey)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(assetKey);
        string normalized = assetKey.Replace('/', Path.DirectorySeparatorChar);
        string path = Path.GetFullPath(Path.Combine(_root, normalized));
        string relative = Path.GetRelativePath(_root, path);
        if (Path.IsPathRooted(relative)
            || relative == ".."
            || relative.StartsWith($"..{Path.DirectorySeparatorChar}", StringComparison.Ordinal))
            throw new UnauthorizedAccessException("The asset key escapes the render-pack root.");
        return File.Open(path, FileMode.Open, FileAccess.Read, FileShare.Read);
    }
}
