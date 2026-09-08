using System.Globalization;
using System.Reflection;
using AcDream.Plugin.Abstractions.Rendering;

namespace AcDream.RenderPacks.AtmosphericTier2;

public sealed class AtmosphericTier2RenderPack : IRenderPackPlugin, IRenderPackAssets
{
    private const string ShaderPrefix = "shaders/";
    private const string EmbeddedShaderPrefix =
        "AcDream.RenderPacks.AtmosphericTier2.Shaders.";

    private static RenderPackDescriptor Descriptor { get; } = new RenderPackDescriptor(
        "sample.atmospheric-tier2",
        "Atmospheric Tier 2 SDK Sample",
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
            FeatureSummary = "Filmic HDR atmosphere and moving sun-and-moon cascaded shadows "
                + "from terrain, trees, buildings, players, and monsters, with "
                + "optional volumetric shafts.",
        };

    public void Register(IRenderPackRegistry registry)
    {
        ArgumentNullException.ThrowIfNull(registry);
        registry.Register(Descriptor, this);
    }

    public Stream OpenRead(string assetKey)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(assetKey);
        if (!assetKey.StartsWith(ShaderPrefix, StringComparison.Ordinal)
            || assetKey.Length == ShaderPrefix.Length
            || assetKey.Contains('\\')
            || assetKey.Contains("..", StringComparison.Ordinal))
        {
            throw new FileNotFoundException(
                "The atmospheric sample exposes only declared embedded shader assets.",
                assetKey);
        }

        string fileName = assetKey[ShaderPrefix.Length..];
        if (fileName.Contains('/'))
            throw new FileNotFoundException("Nested shader paths are not declared.", assetKey);

        string resourceName = EmbeddedShaderPrefix + fileName;
        return Assembly.GetExecutingAssembly().GetManifestResourceStream(resourceName)
            ?? throw new FileNotFoundException("The declared shader asset is missing.", assetKey);
    }

    private static IReadOnlyList<RenderResourceDeclaration> Resources() =>
    [
        Image("cinematic-world-buffer", RenderResourceSemantic.MainWorldHdr,
            RenderFormatClass.HdrColor, 1.0, 32L * 1024 * 1024),
        Image("glow-stage-one", RenderResourceSemantic.BloomPing,
            RenderFormatClass.HdrColor, 0.5, 8L * 1024 * 1024),
        Image("glow-stage-two", RenderResourceSemantic.BloomPong,
            RenderFormatClass.HdrColor, 0.5, 8L * 1024 * 1024),
        Image("solar-visibility", RenderResourceSemantic.SunOcclusionMask,
            RenderFormatClass.SingleChannel, 0.25, 2L * 1024 * 1024),
        Image("scattered-sunlight", RenderResourceSemantic.SunRays,
            RenderFormatClass.HdrColor, 0.25, 2L * 1024 * 1024),
        new RenderResourceDeclaration(
            "directional-shadow-depth",
            RenderResourceKind.Image2DArray,
            RenderFormatClass.DirectionalDepth,
            new RenderExtentDeclaration(
                RenderExtentMode.AbsolutePixels,
                1024,
                1024,
                Layers: 2),
            SizeBytes: 0,
            RenderResourceUsage.Sampled | RenderResourceUsage.DepthAttachment,
            RenderResourceLifetime.ActivePack,
            EstimatedResidentBytes: 8L * 1024 * 1024)
            { Semantic = RenderResourceSemantic.DirectionalShadowDepth },
        Image("participating-air", RenderResourceSemantic.VolumetricShafts,
            RenderFormatClass.HdrColor, 0.25, 2L * 1024 * 1024),
    ];

    private static IReadOnlyList<RenderPassDeclaration> Passes() =>
    [
        Pass(
            "record-directional-shadow-casters",
            RenderPassSemantic.DirectionalShadowDepth,
            RenderPassHook.ShadowDepthBeforeWorld,
            "shadow-pass.vert.spv",
            "shadow-pass.frag.spv",
            [
                RenderSemanticInput.CameraMatrices,
                RenderSemanticInput.SelectedCelestialDirectionalLight,
                RenderSemanticInput.ShadowCasterTransforms,
                RenderSemanticInput.ActiveDayGroup,
                RenderSemanticInput.Weather,
            ],
            [],
            ["directional-shadow-depth"]),
        Pass(
            "measure-solar-visibility",
            RenderPassSemantic.SunOcclusion,
            RenderPassHook.AtmosphereBeforeToneMap,
            "solar-visibility.vert.spv",
            "solar-visibility.frag.spv",
            [
                RenderSemanticInput.SceneDepth,
                RenderSemanticInput.SunScreenPosition,
                RenderSemanticInput.ActiveDayGroup,
                RenderSemanticInput.Weather,
            ],
            [],
            ["solar-visibility"]),
        Pass(
            "scatter-visible-sunlight",
            RenderPassSemantic.SunRays,
            RenderPassHook.AtmosphereBeforeToneMap,
            "sun-scatter.vert.spv",
            "sun-scatter.frag.spv",
            [RenderSemanticInput.SunScreenPosition, RenderSemanticInput.FrameTime],
            ["solar-visibility"],
            ["scattered-sunlight"]),
        Pass(
            "integrate-lit-air",
            RenderPassSemantic.VolumetricShafts,
            RenderPassHook.AtmosphereBeforeToneMap,
            "lit-air.vert.spv",
            "lit-air.frag.spv",
            [
                RenderSemanticInput.SceneDepth,
                RenderSemanticInput.CameraMatrices,
                RenderSemanticInput.SunDirection,
                RenderSemanticInput.DirectionalShadowMaps,
                RenderSemanticInput.ActiveDayGroup,
                RenderSemanticInput.Weather,
            ],
            ["directional-shadow-depth"],
            ["participating-air"]),
        Pass(
            "extract-highlight-energy",
            RenderPassSemantic.BloomDownsample,
            RenderPassHook.AtmosphereBeforeToneMap,
            "highlight-extract.vert.spv",
            "highlight-extract.frag.spv",
            [RenderSemanticInput.WorldColor],
            ["scattered-sunlight", "participating-air"],
            ["glow-stage-one"]),
        Pass(
            "spread-glow-sideways",
            RenderPassSemantic.BloomBlurHorizontal,
            RenderPassHook.AtmosphereBeforeToneMap,
            "glow-filter.vert.spv",
            "glow-filter.frag.spv",
            [RenderSemanticInput.FrameTime],
            ["glow-stage-one"],
            ["glow-stage-two"]),
        Pass(
            "spread-glow-upwards",
            RenderPassSemantic.BloomBlurVertical,
            RenderPassHook.AtmosphereBeforeToneMap,
            "glow-filter.vert.spv",
            "glow-filter.frag.spv",
            [RenderSemanticInput.FrameTime],
            ["glow-stage-two"],
            ["glow-stage-one"]),
        Pass(
            "compose-cinematic-colour",
            RenderPassSemantic.FilmicComposite,
            RenderPassHook.ToneMap,
            "cinematic-composite.vert.spv",
            "cinematic-composite.frag.spv",
            [RenderSemanticInput.WorldColor, RenderSemanticInput.FrameTime],
            ["glow-stage-one", "scattered-sunlight", "participating-air"],
            []),
    ];

    private static IReadOnlyList<SceneReplayDeclaration> SceneReplays() =>
    [
        new SceneReplayDeclaration(
            "replay-every-outdoor-shadow-caster",
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
        Variant(
            "landscape-shadow-writer",
            RenderPipelineVariantSemantic.TerrainDirectionalShadowCaster,
            RenderPipelineBaseSemantic.Terrain,
            "landscape-shadow.vert.spv",
            "landscape-shadow.frag.spv",
            RenderMaterialClass.Opaque,
            [RenderSemanticInput.CameraMatrices]),
        Variant(
            "solid-object-shadow-writer",
            RenderPipelineVariantSemantic.WorldOpaqueDirectionalShadowCaster,
            RenderPipelineBaseSemantic.WorldMesh,
            "solid-shadow.vert.spv",
            "solid-shadow.frag.spv",
            RenderMaterialClass.Opaque | RenderMaterialClass.AnimatedOpaque,
            [RenderSemanticInput.CameraMatrices, RenderSemanticInput.ShadowCasterTransforms]),
        Variant(
            "cutout-object-shadow-writer",
            RenderPipelineVariantSemantic.WorldAlphaCutoutDirectionalShadowCaster,
            RenderPipelineBaseSemantic.WorldMesh,
            "cutout-shadow.vert.spv",
            "cutout-shadow.frag.spv",
            RenderMaterialClass.AlphaCutout | RenderMaterialClass.AnimatedAlphaCutout,
            [RenderSemanticInput.CameraMatrices, RenderSemanticInput.ShadowCasterTransforms]),
        Variant(
            "landscape-shadow-reader",
            RenderPipelineVariantSemantic.TerrainDirectionalShadowReceiver,
            RenderPipelineBaseSemantic.Terrain,
            "landscape-lit.vert.spv",
            "landscape-lit.frag.spv",
            RenderMaterialClass.Opaque,
            [RenderSemanticInput.DirectionalShadowMaps,
                RenderSemanticInput.SelectedCelestialDirectionalLight]),
        Variant(
            "object-shadow-reader",
            RenderPipelineVariantSemantic.WorldDirectionalShadowReceiver,
            RenderPipelineBaseSemantic.WorldMesh,
            "object-lit.vert.spv",
            "object-lit.frag.spv",
            RenderMaterialClass.Opaque
                | RenderMaterialClass.AlphaCutout
                | RenderMaterialClass.AnimatedOpaque
                | RenderMaterialClass.AnimatedAlphaCutout,
            [RenderSemanticInput.DirectionalShadowMaps,
                RenderSemanticInput.SelectedCelestialDirectionalLight]),
    ];

    private static IReadOnlyList<RenderQualityPreset> QualityPresets() =>
    [
        Preset(
            "economy",
            "Economy",
            RenderQualitySemantic.Low,
            maxMiB: 64,
            gpuP50: 2.0,
            gpuP99: 3.0,
            cpuP50: 0.15,
            cpuP99: 0.50,
            shadowResolution: 1024,
            cascades: 2,
            shadowReachMetres: 72,
            postScale: 0.25),
        Preset(
            "balanced",
            "Balanced",
            RenderQualitySemantic.Medium,
            maxMiB: 128,
            gpuP50: 3.25,
            gpuP99: 4.50,
            cpuP50: 0.25,
            cpuP99: 0.75,
            shadowResolution: 1536,
            cascades: 3,
            shadowReachMetres: 144,
            postScale: 0.5),
        Preset(
            "cinematic",
            "Cinematic",
            RenderQualitySemantic.High,
            maxMiB: 256,
            gpuP50: 4.50,
            gpuP99: 6.00,
            cpuP50: 0.35,
            cpuP99: 1.00,
            shadowResolution: 2048,
            cascades: 4,
            shadowReachMetres: 240,
            postScale: 0.5),
        Preset(
            "adaptive",
            "Adaptive",
            RenderQualitySemantic.Automatic,
            maxMiB: 128,
            gpuP50: 3.25,
            gpuP99: 4.50,
            cpuP50: 0.25,
            cpuP99: 0.75,
            shadowResolution: 1536,
            cascades: 3,
            shadowReachMetres: 144,
            postScale: 0.5)
            with
            {
                SettingOverrides =
                [
                    new RenderQualitySettingOverride("quality-governor", "true"),
                    new RenderQualitySettingOverride("air-shaft-strength", "0.35"),
                    new RenderQualitySettingOverride("air-march-steps", "40"),
                    new RenderQualitySettingOverride("moving-shadow-opacity", "0.72"),
                    new RenderQualitySettingOverride("moving-shadow-range", "144"),
                    new RenderQualitySettingOverride("shadow-filter-samples", "9"),
                    new RenderQualitySettingOverride("sun-scatter-strength", "0.55"),
                ],
                AutoEligible = false,
            },
    ];

    private static IReadOnlyList<RenderSettingDeclaration> Settings() =>
    [
        Float("highlight-glow", "Highlight glow", RenderSettingSemantic.BloomStrength,
            0.65, 0, 2, 0.05),
        Float("filmic-mix", "Filmic tone-map mix", RenderSettingSemantic.FilmicStrength,
            1.0, 0, 1, 0.05),
        Float("scene-exposure", "Scene exposure", RenderSettingSemantic.Exposure,
            1.0, 0.25, 4, 0.05),
        Float("colour-saturation", "Colour saturation", RenderSettingSemantic.GradeSaturation,
            1.0, 0, 2, 0.05),
        Float("colour-contrast", "Colour contrast", RenderSettingSemantic.GradeContrast,
            1.0, 0.5, 2, 0.05),
        Float("frame-vignette", "Frame vignette", RenderSettingSemantic.VignetteStrength,
            0.12, 0, 1, 0.01),
        Float("sun-scatter-strength", "Sun-scatter strength", RenderSettingSemantic.SunRayStrength,
            0.55, 0, 2, 0.05),
        Float("moving-shadow-opacity", "Moving-shadow opacity",
            RenderSettingSemantic.DirectionalShadowStrength, 0.72, 0, 1, 0.02),
        Integer("moving-shadow-range", "Moving-shadow range (metres)",
            RenderSettingSemantic.DirectionalShadowReachMetres, 240, 16, 240, 1),
        Choice("shadow-filter-samples", "Shadow filter samples",
            RenderSettingSemantic.DirectionalShadowPcfTaps, "9", ["1", "9", "25"]),
        Float("air-shaft-strength", "Volumetric-air strength",
            RenderSettingSemantic.VolumetricStrength, 0.35, 0, 1, 0.01),
        Integer("air-march-steps", "Volumetric ray-march steps",
            RenderSettingSemantic.VolumetricRayMarchSteps, 40, 8, 64, 8),
        new RenderSettingDeclaration(
            "quality-governor",
            "Automatic quality",
            RenderSettingKind.Boolean,
            "false",
            Minimum: null,
            Maximum: null,
            Step: null,
            Choices: [])
            { Semantic = RenderSettingSemantic.AutomaticQuality },
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
        };

    private static RenderResourceDeclaration Image(
        string id,
        RenderResourceSemantic semantic,
        RenderFormatClass format,
        double scale,
        long estimatedBytes) => new(
            id,
            RenderResourceKind.Image2D,
            format,
            new RenderExtentDeclaration(RenderExtentMode.RelativeToMainWorld, scale, scale),
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
        IReadOnlyList<RenderSemanticInput> semanticInputs,
        IReadOnlyList<string> reads,
        IReadOnlyList<string> writes) => new(
            id,
            hook,
            ShaderPrefix + vertex,
            ShaderPrefix + fragment,
            semanticInputs,
            reads,
            writes)
            { Semantic = semantic };

    private static PipelineVariantDeclaration Variant(
        string id,
        RenderPipelineVariantSemantic semantic,
        RenderPipelineBaseSemantic baseSemantic,
        string vertex,
        string fragment,
        RenderMaterialClass materials,
        IReadOnlyList<RenderSemanticInput> inputs) => new(
            id,
            baseSemantic,
            ShaderPrefix + vertex,
            ShaderPrefix + fragment,
            materials,
            inputs)
            { Semantic = semantic };

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
        double postScale) => new(
            id,
            displayName,
            [RenderCapability.DirectionalShadowMaps],
            [
                AbsoluteOverride(
                    "directional-shadow-depth",
                    shadowResolution,
                    cascades,
                    4L * shadowResolution * shadowResolution * cascades),
                RelativeOverride("glow-stage-one", postScale),
                RelativeOverride("glow-stage-two", postScale),
                RelativeOverride("solar-visibility", id == "economy" ? 0.25 : 0.5),
                RelativeOverride("scattered-sunlight", id == "economy" ? 0.25 : 0.5),
                RelativeOverride("participating-air", id == "cinematic" ? 0.5 : 0.25),
            ],
            [
                new RenderQualitySettingOverride("quality-governor", "false"),
                new RenderQualitySettingOverride(
                    "air-shaft-strength",
                    id == "economy" ? "0" : "0.35"),
                new RenderQualitySettingOverride(
                    "air-march-steps",
                    semantic switch
                    {
                        RenderQualitySemantic.Low => "24",
                        RenderQualitySemantic.High => "56",
                        _ => "40",
                    }),
                new RenderQualitySettingOverride("moving-shadow-opacity", "0.72"),
                new RenderQualitySettingOverride(
                    "moving-shadow-range",
                    shadowReachMetres.ToString(CultureInfo.InvariantCulture)),
                new RenderQualitySettingOverride(
                    "shadow-filter-samples",
                    semantic switch
                    {
                        RenderQualitySemantic.Low => "1",
                        RenderQualitySemantic.High => "25",
                        _ => "9",
                    }),
                new RenderQualitySettingOverride(
                    "sun-scatter-strength",
                    id == "economy" ? "0.4" : "0.55"),
            ],
            maxMiB * 1024 * 1024,
            gpuP50,
            gpuP99,
            cpuP50,
            cpuP99)
            { Semantic = semantic };

    private static RenderQualityResourceOverride AbsoluteOverride(
        string id,
        int resolution,
        int layers,
        long bytes) => new(
            id,
            new RenderExtentDeclaration(
                RenderExtentMode.AbsolutePixels,
                resolution,
                resolution,
                layers),
            SizeBytes: 0,
            EstimatedResidentBytes: bytes);

    private static RenderQualityResourceOverride RelativeOverride(string id, double scale) => new(
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
        double step) => new(
            id,
            displayName,
            RenderSettingKind.Float,
            defaultValue.ToString(CultureInfo.InvariantCulture),
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
        int step) => new(
            id,
            displayName,
            RenderSettingKind.Integer,
            defaultValue.ToString(CultureInfo.InvariantCulture),
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
        IReadOnlyList<string> choices) => new(
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
