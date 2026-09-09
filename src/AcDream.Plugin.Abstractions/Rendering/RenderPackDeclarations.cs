namespace AcDream.Plugin.Abstractions.Rendering;

/// <summary>Prerequisite tier reached by a pack.</summary>
public enum RenderPackTier
{
    Tier1 = 1,
    Tier2 = 2,
    Tier2Plus = 3,
}

public enum RenderCapability
{
    MainWorldColorIntermediate,
    FullscreenPasses,
    SceneDepthSampling,
    SceneNormalSampling,
    AuthoredSunDirection,
    AuthoredSunScreenPosition,
    AuthoredWeather,
    DirectionalShadowMaps,
    OutdoorDirectionalShadowCasterReplay,
    AnimatedCasterTransforms,
    AlphaCutoutShadowCasters,
    GpuTimestampQueries,
    MultiviewDirectionalShadowCascades,
    /// <summary>One renderer-selected authored sun-or-moon shadow direction.</summary>
    AuthoredCelestialDirectionalLight,
}

/// <summary>Fixed renderer-owned positions at which a declared pass may run.</summary>
public enum RenderPassHook
{
    ShadowDepthBeforeWorld,
    AtmosphereBeforeToneMap,
    ToneMap,
    AfterToneMapBeforePrivateViewports,
}

/// <summary>Immutable frame facts the renderer may bind for a pack.</summary>
public enum RenderSemanticInput
{
    WorldColor,
    SceneDepth,
    SceneNormals,
    SunDirection,
    SunScreenPosition,
    ActiveDayGroup,
    Weather,
    CameraMatrices,
    ShadowCasterTransforms,
    DirectionalShadowMaps,
    FrameTime,
    /// <summary>Selected surface-to-sun-or-moon direction for shadow work.</summary>
    SelectedCelestialDirectionalLight,
}

/// <summary>Renderer-owned replay operations available to a declaration.</summary>
public enum RenderSceneReplaySemantic
{
    OutdoorDirectionalShadowCasters,
}

[Flags]
public enum RenderCasterClass
{
    None = 0,
    Terrain = 1 << 0,
    OpaqueWorld = 1 << 1,
    AlphaCutoutWorld = 1 << 2,
    AnimatedOpaque = 1 << 3,
    AnimatedAlphaCutout = 1 << 4,
}

/// <summary>Base renderer pipeline a pack may specialize.</summary>
public enum RenderPipelineBaseSemantic
{
    Terrain,
    WorldMesh,
    EnvCell,
}

[Flags]
public enum RenderMaterialClass
{
    None = 0,
    Opaque = 1 << 0,
    AlphaCutout = 1 << 1,
    AnimatedOpaque = 1 << 2,
    AnimatedAlphaCutout = 1 << 3,
}

/// <summary>Kind of renderer-owned intermediate resource.</summary>
public enum RenderResourceKind
{
    Image2D,
    Image2DArray,
    Buffer,
}

/// <summary>Portable format families resolved by the renderer.</summary>
public enum RenderFormatClass
{
    LdrColor,
    HdrColor,
    SingleChannel,
    DirectionalDepth,
    StructuredData,
}

/// <summary>How declared image dimensions are interpreted.</summary>
public enum RenderExtentMode
{
    AbsolutePixels,
    RelativeToMainWorld,
    RelativeToOutput,
}

/// <summary>Permitted uses of a declared resource.</summary>
[Flags]
public enum RenderResourceUsage
{
    None = 0,
    Sampled = 1 << 0,
    ColorAttachment = 1 << 1,
    DepthAttachment = 1 << 2,
    Storage = 1 << 3,
    TransferSource = 1 << 4,
    TransferDestination = 1 << 5,
}

public enum RenderResourceLifetime
{
    TransientPass,
    FrameFlight,
    ActivePack,
}

public enum RenderResourceSemantic
{
    Custom,
    MainWorldHdr,
    BloomPing,
    BloomPong,
    SunOcclusionMask,
    SunRays,
    DirectionalShadowDepth,
    VolumetricShafts,
}

/// <summary>Shape of one image declaration.</summary>
/// <param name="Mode">Absolute pixels or a scale relative to a renderer surface.</param>
/// <param name="Width">Pixel width for absolute mode; horizontal scale otherwise.</param>
/// <param name="Height">Pixel height for absolute mode; vertical scale otherwise.</param>
/// <param name="Layers">Array layers; one for an ordinary 2-D image.</param>
public sealed record RenderExtentDeclaration(
    RenderExtentMode Mode,
    double Width,
    double Height,
    int Layers = 1);

/// <summary>One renderer-owned intermediate image or buffer.</summary>
public sealed record RenderResourceDeclaration(
    string Id,
    RenderResourceKind Kind,
    RenderFormatClass Format,
    RenderExtentDeclaration? Extent,
    long SizeBytes,
    RenderResourceUsage Usage,
    RenderResourceLifetime Lifetime,
    long EstimatedResidentBytes)
{
    public RenderResourceSemantic Semantic { get; init; } = RenderResourceSemantic.Custom;
}

/// <summary>
/// Renderer-owned execution meaning of a pass. IDs remain pack-owned stable
/// identifiers; semantic execution never depends on a particular ID string.
/// </summary>
public enum RenderPassSemantic
{
    CustomFullscreen,
    DirectionalShadowDepth,
    BloomDownsample,
    BloomBlurHorizontal,
    BloomBlurVertical,
    SunOcclusion,
    SunRays,
    VolumetricShafts,
    FilmicComposite,
}

/// <summary>One declarative full-screen, atmosphere, or tone-map pass.</summary>
public sealed record RenderPassDeclaration(
    string Id,
    RenderPassHook Hook,
    string VertexShaderAsset,
    string FragmentShaderAsset,
    IReadOnlyList<RenderSemanticInput> SemanticInputs,
    IReadOnlyList<string> ResourceReads,
    IReadOnlyList<string> ResourceWrites)
{
    public RenderPassSemantic Semantic { get; init; } = RenderPassSemantic.CustomFullscreen;
}

/// <summary>A renderer-owned replay of retained scene geometry.</summary>
public sealed record SceneReplayDeclaration(
    string Id,
    RenderSceneReplaySemantic Semantic,
    RenderCasterClass CasterClasses,
    int ViewCount);

/// <summary>A shader specialization of an existing renderer pipeline.</summary>
public sealed record PipelineVariantDeclaration(
    string Id,
    RenderPipelineBaseSemantic BaseSemantic,
    string VertexShaderAsset,
    string FragmentShaderAsset,
    RenderMaterialClass CompatibleMaterials,
    IReadOnlyList<RenderSemanticInput> SemanticInputs)
{
    public RenderPipelineVariantSemantic Semantic { get; init; } =
        RenderPipelineVariantSemantic.Custom;
}

/// <summary>Renderer-owned role of a fixed retained-scene pipeline variant.</summary>
public enum RenderPipelineVariantSemantic
{
    Custom,
    TerrainDirectionalShadowCaster,
    WorldOpaqueDirectionalShadowCaster,
    WorldAlphaCutoutDirectionalShadowCaster,
    TerrainDirectionalShadowReceiver,
    WorldDirectionalShadowReceiver,
    TerrainMultiviewDirectionalShadowCaster,
    WorldOpaqueMultiviewDirectionalShadowCaster,
    WorldAlphaCutoutMultiviewDirectionalShadowCaster,
}

/// <summary>Per-preset replacement for one resource's size.</summary>
public sealed record RenderQualityResourceOverride(
    string ResourceId,
    RenderExtentDeclaration? Extent,
    long SizeBytes,
    long EstimatedResidentBytes);

/// <summary>Per-preset value for a declared user setting.</summary>
public sealed record RenderQualitySettingOverride(
    string SettingId,
    string Value);

public sealed record RenderQualityPreset(
    string Id,
    string DisplayName,
    IReadOnlyList<RenderCapability> RequiredCapabilities,
    IReadOnlyList<RenderQualityResourceOverride> ResourceOverrides,
    IReadOnlyList<RenderQualitySettingOverride> SettingOverrides,
    long MaxResidentGpuBytes,
    double MaxIncrementalGpuMillisecondsP50,
    double MaxIncrementalGpuMillisecondsP99,
    double MaxIncrementalCpuMillisecondsP50,
    double MaxIncrementalCpuMillisecondsP99,
    bool AutoEligible = true)
{
    public RenderQualitySemantic Semantic { get; init; } = RenderQualitySemantic.Custom;

    /// <summary>
    /// Optional renderer-owned execution optimizations whose shader ABI the
    /// pack explicitly implements. The host never infers these from a pack ID.
    /// </summary>
    public RenderQualityExecutionHints ExecutionHints { get; init; } =
        RenderQualityExecutionHints.None;
}

[Flags]
public enum RenderQualityExecutionHints
{
    None = 0,

    FusedAtmosphericPostProcess = 1 << 0,

    MultiviewDirectionalShadowCascades = 1 << 1,
}

public enum RenderQualitySemantic
{
    Custom,
    Low,
    Medium,
    High,
    Automatic,
}

/// <summary>Storage and presentation kind for a pack-defined setting.</summary>
public enum RenderSettingKind
{
    Boolean,
    Integer,
    Float,
    Choice,
}

/// <summary>A bounded, user-visible pack setting.</summary>
public sealed record RenderSettingDeclaration(
    string Id,
    string DisplayName,
    RenderSettingKind Kind,
    string DefaultValue,
    double? Minimum,
    double? Maximum,
    double? Step,
    IReadOnlyList<string> Choices)
{
    public RenderSettingSemantic Semantic { get; init; } = RenderSettingSemantic.Custom;
}

public enum RenderSettingSemantic
{
    Custom,
    BloomStrength,
    FilmicStrength,
    Exposure,
    GradeSaturation,
    GradeContrast,
    VignetteStrength,
    SunRayStrength,
    DirectionalShadowStrength,
    DirectionalShadowReachMetres,
    DirectionalShadowPcfTaps,
    VolumetricStrength,
    VolumetricRayMarchSteps,
    AutomaticQuality,
    WindEnabled,
    WindStrength,
    WindDirectionDegrees,
    WindLeanMetres,
    WindBranchMetres,
    WindFlutterMetres,
    WindCanopyHeightMetres,
}

public sealed record SunElevationResponsePoint(
    double ElevationDegrees,
    double Multiplier);

/// <summary>Explicit mapping from an authored AC day group to an effect multiplier.</summary>
public sealed record ActiveDayGroupMultiplier(
    int ActiveDayGroup,
    double Multiplier);

public sealed record FoliageWindWeatherPoint(
    string WeatherKind,
    double Mean,
    double Gust);

/// <summary>Visible authored-atmosphere interpretation owned by the pack.</summary>
public sealed record AtmospherePolicyDeclaration(
    IReadOnlyList<SunElevationResponsePoint> SunElevationResponse,
    IReadOnlyList<ActiveDayGroupMultiplier> ActiveDayGroupMultipliers)
{
    public IReadOnlyList<SunElevationResponsePoint> DirectionalShadowLightElevationResponse
        { get; init; } = [];

    public IReadOnlyList<SunElevationResponsePoint> VolumetricShaftSunElevationResponse
        { get; init; } = [];

    public IReadOnlyList<FoliageWindWeatherPoint> FoliageWindByWeather
        { get; init; } = [];

    public IReadOnlyList<uint> FoliageExclusions { get; init; } = [];
}

public sealed record RenderPackDescriptor(
    string Id,
    string DisplayName,
    Version PackVersion,
    int PackApiVersion,
    RenderPackTier HighestTier,
    IReadOnlyList<RenderCapability> RequiredCapabilities,
    IReadOnlyList<RenderCapability> OptionalCapabilities,
    IReadOnlyList<RenderResourceDeclaration> Resources,
    IReadOnlyList<RenderPassDeclaration> Passes,
    IReadOnlyList<SceneReplayDeclaration> SceneReplays,
    IReadOnlyList<PipelineVariantDeclaration> PipelineVariants,
    IReadOnlyList<RenderQualityPreset> QualityPresets,
    IReadOnlyList<RenderSettingDeclaration> Settings,
    AtmospherePolicyDeclaration? AtmospherePolicy)
{
    public string FeatureSummary { get; init; } = string.Empty;
}
