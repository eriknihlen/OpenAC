using System.Reflection;
using AcDream.Plugin.Abstractions.Rendering;

namespace AcDream.RenderPacks.ShadowsOnlyTier2;

public sealed class ShadowsOnlyTier2RenderPack : IRenderPackPlugin, IRenderPackAssets
{
    private const string ShaderPrefix = "shaders/";
    private const string EmbeddedPrefix =
        "AcDream.RenderPacks.ShadowsOnlyTier2.Shaders.";

    private static RenderPackDescriptor Descriptor { get; } = new(
        "sample.shadows-only-tier2",
        "Shadows-Only Tier 2 SDK Sample",
        new Version(1, 0, 0),
        RenderPackApi.Current,
        RenderPackTier.Tier2,
        [
            RenderCapability.MainWorldColorIntermediate,
            RenderCapability.FullscreenPasses,
            RenderCapability.AuthoredWeather,
            RenderCapability.DirectionalShadowMaps,
            RenderCapability.OutdoorDirectionalShadowCasterReplay,
            RenderCapability.AnimatedCasterTransforms,
            RenderCapability.AlphaCutoutShadowCasters,
            RenderCapability.AuthoredCelestialDirectionalLight,
        ],
        [RenderCapability.GpuTimestampQueries],
        [ShadowResource()],
        Passes(),
        [CasterReplay()],
        Variants(),
        [Preset()],
        Settings(),
        Policy())
    {
        FeatureSummary = "Moving sun-and-moon shadows from terrain, trees, buildings, players, "
            + "and monsters without bloom, rays, grading, vignette, or volumetric shafts.",
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
            throw new FileNotFoundException("Only declared embedded shaders are available.", assetKey);
        }
        string fileName = assetKey[ShaderPrefix.Length..];
        if (fileName.Contains('/'))
            throw new FileNotFoundException("Nested shader paths are not declared.", assetKey);
        return Assembly.GetExecutingAssembly().GetManifestResourceStream(EmbeddedPrefix + fileName)
            ?? throw new FileNotFoundException("The declared shader asset is missing.", assetKey);
    }

    private static RenderResourceDeclaration ShadowResource() => new(
        "directional-shadow-depth",
        RenderResourceKind.Image2DArray,
        RenderFormatClass.DirectionalDepth,
        new RenderExtentDeclaration(RenderExtentMode.AbsolutePixels, 1024, 1024, Layers: 2),
        SizeBytes: 0,
        RenderResourceUsage.Sampled | RenderResourceUsage.DepthAttachment,
        RenderResourceLifetime.ActivePack,
        EstimatedResidentBytes: 8L * 1024 * 1024)
    {
        Semantic = RenderResourceSemantic.DirectionalShadowDepth,
    };

    private static IReadOnlyList<RenderPassDeclaration> Passes() =>
    [
        new RenderPassDeclaration(
            "record-directional-shadow-casters",
            RenderPassHook.ShadowDepthBeforeWorld,
            Shader("shadow-pass.vert.spv"),
            Shader("shadow-pass.frag.spv"),
            [
                RenderSemanticInput.CameraMatrices,
                RenderSemanticInput.SelectedCelestialDirectionalLight,
                RenderSemanticInput.ShadowCasterTransforms,
                RenderSemanticInput.ActiveDayGroup,
                RenderSemanticInput.Weather,
            ],
            [],
            ["directional-shadow-depth"])
        {
            Semantic = RenderPassSemantic.DirectionalShadowDepth,
        },
        new RenderPassDeclaration(
            "copy-hdr-world-to-output",
            RenderPassHook.ToneMap,
            Shader("glow-filter.vert.spv"),
            Shader("glow-filter.frag.spv"),
            [RenderSemanticInput.WorldColor],
            [],
            []),
    ];

    private static SceneReplayDeclaration CasterReplay() => new(
        "replay-all-headline-casters",
        RenderSceneReplaySemantic.OutdoorDirectionalShadowCasters,
        RenderCasterClass.Terrain
            | RenderCasterClass.OpaqueWorld
            | RenderCasterClass.AlphaCutoutWorld
            | RenderCasterClass.AnimatedOpaque
            | RenderCasterClass.AnimatedAlphaCutout,
        ViewCount: 4);

    private static IReadOnlyList<PipelineVariantDeclaration> Variants() =>
    [
        Variant("terrain-caster", RenderPipelineVariantSemantic.TerrainDirectionalShadowCaster,
            RenderPipelineBaseSemantic.Terrain, "landscape-shadow",
            RenderMaterialClass.Opaque, [RenderSemanticInput.CameraMatrices]),
        Variant("opaque-caster", RenderPipelineVariantSemantic.WorldOpaqueDirectionalShadowCaster,
            RenderPipelineBaseSemantic.WorldMesh, "solid-shadow",
            RenderMaterialClass.Opaque | RenderMaterialClass.AnimatedOpaque,
            [RenderSemanticInput.CameraMatrices, RenderSemanticInput.ShadowCasterTransforms]),
        Variant("cutout-caster", RenderPipelineVariantSemantic.WorldAlphaCutoutDirectionalShadowCaster,
            RenderPipelineBaseSemantic.WorldMesh, "cutout-shadow",
            RenderMaterialClass.AlphaCutout | RenderMaterialClass.AnimatedAlphaCutout,
            [RenderSemanticInput.CameraMatrices, RenderSemanticInput.ShadowCasterTransforms]),
        Variant("terrain-receiver", RenderPipelineVariantSemantic.TerrainDirectionalShadowReceiver,
            RenderPipelineBaseSemantic.Terrain, "landscape-lit",
            RenderMaterialClass.Opaque,
            [RenderSemanticInput.DirectionalShadowMaps,
                RenderSemanticInput.SelectedCelestialDirectionalLight]),
        Variant("world-receiver", RenderPipelineVariantSemantic.WorldDirectionalShadowReceiver,
            RenderPipelineBaseSemantic.WorldMesh, "object-lit",
            RenderMaterialClass.Opaque | RenderMaterialClass.AlphaCutout
                | RenderMaterialClass.AnimatedOpaque | RenderMaterialClass.AnimatedAlphaCutout,
            [RenderSemanticInput.DirectionalShadowMaps,
                RenderSemanticInput.SelectedCelestialDirectionalLight]),
    ];

    private static PipelineVariantDeclaration Variant(
        string id,
        RenderPipelineVariantSemantic semantic,
        RenderPipelineBaseSemantic baseSemantic,
        string shaderStem,
        RenderMaterialClass materials,
        IReadOnlyList<RenderSemanticInput> inputs) => new(
            id,
            baseSemantic,
            Shader(shaderStem + ".vert.spv"),
            Shader(shaderStem + ".frag.spv"),
            materials,
            inputs)
        {
            Semantic = semantic,
        };

    private static RenderQualityPreset Preset() => new(
        "balanced",
        "Balanced",
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

    private static IReadOnlyList<RenderSettingDeclaration> Settings() =>
    [
        Float("shadow-opacity", "Shadow opacity",
            RenderSettingSemantic.DirectionalShadowStrength, "0.72", 0, 1, 0.02),
        Integer("shadow-reach", "Shadow reach (metres)",
            RenderSettingSemantic.DirectionalShadowReachMetres, "144", 16, 240, 1),
        new RenderSettingDeclaration(
            "shadow-filter", "Shadow filter samples", RenderSettingKind.Choice,
            "9", null, null, null, ["1", "9", "25"])
        {
            Semantic = RenderSettingSemantic.DirectionalShadowPcfTaps,
        },
    ];

    private static RenderSettingDeclaration Float(
        string id, string displayName, RenderSettingSemantic semantic,
        string value, double minimum, double maximum, double step) => new(
            id, displayName, RenderSettingKind.Float, value,
            minimum, maximum, step, [])
        {
            Semantic = semantic,
        };

    private static RenderSettingDeclaration Integer(
        string id, string displayName, RenderSettingSemantic semantic,
        string value, double minimum, double maximum, double step) => new(
            id, displayName, RenderSettingKind.Integer, value,
            minimum, maximum, step, [])
        {
            Semantic = semantic,
        };

    private static AtmospherePolicyDeclaration Policy() => new(
        [
            new SunElevationResponsePoint(-90, 1),
            new SunElevationResponsePoint(90, 1),
        ],
        [
            new ActiveDayGroupMultiplier(0, 1),
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
    };

    private static string Shader(string fileName) => ShaderPrefix + fileName;
}
