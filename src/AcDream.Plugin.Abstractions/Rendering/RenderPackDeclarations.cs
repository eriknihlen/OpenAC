namespace AcDream.Plugin.Abstractions.Rendering;

/// <summary>Prerequisite tier reached by a pack.</summary>
public enum RenderPackTier
{
    /// <summary>
    /// The pack keeps to full-screen work over the already rendered image. A
    /// pack that declares directional shadows may not claim this tier.
    /// </summary>
    Tier1 = 1,

    /// <summary>
    /// The pack reaches into the scene itself, for instance by casting
    /// directional shadows. This is the lowest tier such a pack may declare.
    /// </summary>
    Tier2 = 2,

    /// <summary>
    /// Everything a Tier 2 pack does plus further effects on top, such as
    /// volumetric light shafts.
    /// </summary>
    Tier2Plus = 3,
}

/// <summary>
/// Something the client can provide to a pack. A pack lists the ones it cannot
/// run without, and the client refuses to select the pack on a machine where
/// any of those is missing.
/// </summary>
public enum RenderCapability
{
    /// <summary>
    /// The world is drawn into an off-screen high-dynamic-range colour image
    /// that pack passes can read and write. Needs a device that supports
    /// 16-bit-float colour targets.
    /// </summary>
    MainWorldColorIntermediate,

    /// <summary>
    /// The client can run a pack's declared full-screen passes and supply the
    /// camera and frame-time facts they need.
    /// </summary>
    FullscreenPasses,

    /// <summary>Pack shaders may sample the scene's depth buffer.</summary>
    SceneDepthSampling,

    /// <summary>Pack shaders may sample the scene's surface normals.</summary>
    SceneNormalSampling,

    /// <summary>The client supplies the sun's direction for the current world time.</summary>
    AuthoredSunDirection,

    /// <summary>
    /// The client supplies where the sun sits on screen, which sun-ray effects
    /// need.
    /// </summary>
    AuthoredSunScreenPosition,

    /// <summary>
    /// The client supplies the current weather and the day grouping in effect.
    /// </summary>
    AuthoredWeather,

    /// <summary>
    /// The client can allocate and sample directional shadow depth maps. Needs
    /// a device that can sample depth images.
    /// </summary>
    DirectionalShadowMaps,

    /// <summary>
    /// The client can re-draw the outdoor scene a second time from the light's
    /// point of view to fill a shadow map.
    /// </summary>
    OutdoorDirectionalShadowCasterReplay,

    /// <summary>
    /// The client supplies per-object transforms, so moving and animated things
    /// cast shadows in the right place.
    /// </summary>
    AnimatedCasterTransforms,

    /// <summary>
    /// Cut-out surfaces such as leaves cast shadows through their alpha mask
    /// rather than as solid blocks.
    /// </summary>
    AlphaCutoutShadowCasters,

    /// <summary>
    /// The device can time GPU work, which the Automatic quality preset needs
    /// to measure what a pack costs.
    /// </summary>
    GpuTimestampQueries,

    /// <summary>
    /// The device can draw several shadow cascades in one pass instead of one
    /// pass per cascade.
    /// </summary>
    MultiviewDirectionalShadowCascades,

    /// <summary>One renderer-selected authored sun-or-moon shadow direction.</summary>
    AuthoredCelestialDirectionalLight,
}

/// <summary>Fixed renderer-owned positions at which a declared pass may run.</summary>
public enum RenderPassHook
{
    /// <summary>
    /// Before the world is drawn, while shadow depth maps are being filled.
    /// </summary>
    ShadowDepthBeforeWorld,

    /// <summary>
    /// After the world is drawn but before tone mapping, while colour is still
    /// high-dynamic-range.
    /// </summary>
    AtmosphereBeforeToneMap,

    /// <summary>
    /// The pass that turns the high-dynamic-range image into the picture shown
    /// on screen and writes the output surface.
    /// </summary>
    ToneMap,

    /// <summary>
    /// After tone mapping, before the client draws its separate inset 3-D
    /// viewports.
    /// </summary>
    AfterToneMapBeforePrivateViewports,
}

/// <summary>Immutable frame facts the renderer may bind for a pack.</summary>
public enum RenderSemanticInput
{
    /// <summary>The rendered world colour image.</summary>
    WorldColor,

    /// <summary>The scene's per-pixel depth.</summary>
    SceneDepth,

    /// <summary>The scene's per-pixel surface normals.</summary>
    SceneNormals,

    /// <summary>The direction to the sun for the current world time.</summary>
    SunDirection,

    /// <summary>Where the sun sits on screen this frame.</summary>
    SunScreenPosition,

    /// <summary>Which authored day grouping the world is currently in.</summary>
    ActiveDayGroup,

    /// <summary>The current weather.</summary>
    Weather,

    /// <summary>The camera's view and projection matrices for this frame.</summary>
    CameraMatrices,

    /// <summary>
    /// Per-object transforms for the things drawn into a shadow map, so
    /// animated casters land correctly.
    /// </summary>
    ShadowCasterTransforms,

    /// <summary>The directional shadow depth maps and their cascade values.</summary>
    DirectionalShadowMaps,

    /// <summary>The frame clock, for effects that move over time.</summary>
    FrameTime,

    /// <summary>Selected surface-to-sun-or-moon direction for shadow work.</summary>
    SelectedCelestialDirectionalLight,
}

/// <summary>Renderer-owned replay operations available to a declaration.</summary>
public enum RenderSceneReplaySemantic
{
    /// <summary>
    /// Draw the outdoor scene again from the light's point of view to fill a
    /// directional shadow map.
    /// </summary>
    OutdoorDirectionalShadowCasters,
}

/// <summary>
/// Which kinds of geometry a scene replay draws. A replay declaration combines
/// the ones it wants.
/// </summary>
[Flags]
public enum RenderCasterClass
{
    /// <summary>Nothing; a replay may not declare this alone.</summary>
    None = 0,

    /// <summary>The landscape itself.</summary>
    Terrain = 1 << 0,

    /// <summary>Solid world geometry such as buildings and rocks.</summary>
    OpaqueWorld = 1 << 1,

    /// <summary>
    /// World geometry drawn through an alpha mask, such as leaves and fences.
    /// </summary>
    AlphaCutoutWorld = 1 << 2,

    /// <summary>Solid geometry that moves or animates, such as players and monsters.</summary>
    AnimatedOpaque = 1 << 3,

    /// <summary>Masked geometry that moves or animates.</summary>
    AnimatedAlphaCutout = 1 << 4,
}

/// <summary>Base renderer pipeline a pack may specialize.</summary>
public enum RenderPipelineBaseSemantic
{
    /// <summary>The pipeline that draws the landscape.</summary>
    Terrain,

    /// <summary>The pipeline that draws world objects and creatures.</summary>
    WorldMesh,

    /// <summary>The pipeline that draws interior cells.</summary>
    EnvCell,
}

/// <summary>
/// Which materials a pipeline variant may replace. A variant combines the ones
/// its shaders are written for.
/// </summary>
[Flags]
public enum RenderMaterialClass
{
    /// <summary>Nothing; a variant may not declare this alone.</summary>
    None = 0,

    /// <summary>Solid surfaces.</summary>
    Opaque = 1 << 0,

    /// <summary>Surfaces drawn through an alpha mask.</summary>
    AlphaCutout = 1 << 1,

    /// <summary>Solid surfaces on things that move or animate.</summary>
    AnimatedOpaque = 1 << 2,

    /// <summary>Masked surfaces on things that move or animate.</summary>
    AnimatedAlphaCutout = 1 << 3,
}

/// <summary>Kind of renderer-owned intermediate resource.</summary>
public enum RenderResourceKind
{
    /// <summary>A single two-dimensional image.</summary>
    Image2D,

    /// <summary>
    /// A stack of two-dimensional images. Reserved for directional depth maps;
    /// the client refuses a colour image array.
    /// </summary>
    Image2DArray,

    /// <summary>
    /// A plain data buffer. Reserved for a later version of the contract; the
    /// client refuses a pack that declares one today.
    /// </summary>
    Buffer,
}

/// <summary>Portable format families resolved by the renderer.</summary>
public enum RenderFormatClass
{
    /// <summary>Ordinary colour, eight bits per channel.</summary>
    LdrColor,

    /// <summary>High-dynamic-range colour, sixteen-bit float per channel.</summary>
    HdrColor,

    /// <summary>
    /// A mask-style image with one meaningful channel; the client currently
    /// gives it an ordinary eight-bit-per-channel image.
    /// </summary>
    SingleChannel,

    /// <summary>Depth values for a directional shadow map.</summary>
    DirectionalDepth,

    /// <summary>
    /// Arbitrary structured data. Reserved for a later version of the contract;
    /// the client refuses a pack that declares it today.
    /// </summary>
    StructuredData,
}

/// <summary>How declared image dimensions are interpreted.</summary>
public enum RenderExtentMode
{
    /// <summary>The width and height are pixel counts.</summary>
    AbsolutePixels,

    /// <summary>
    /// The width and height are fractions of the off-screen world image, each
    /// greater than zero and at most one.
    /// </summary>
    RelativeToMainWorld,

    /// <summary>
    /// The width and height are fractions of the final output surface, each
    /// greater than zero and at most one.
    /// </summary>
    RelativeToOutput,
}

/// <summary>Permitted uses of a declared resource.</summary>
[Flags]
public enum RenderResourceUsage
{
    /// <summary>Nothing; a resource may not declare this alone.</summary>
    None = 0,

    /// <summary>Shaders may read the resource.</summary>
    Sampled = 1 << 0,

    /// <summary>A pass may draw colour into the resource.</summary>
    ColorAttachment = 1 << 1,

    /// <summary>A pass may write depth into the resource.</summary>
    DepthAttachment = 1 << 2,

    /// <summary>
    /// Shaders may write the resource directly. Reserved for a later version of
    /// the contract; the client refuses a pack that declares it today.
    /// </summary>
    Storage = 1 << 3,

    /// <summary>The resource may be copied from.</summary>
    TransferSource = 1 << 4,

    /// <summary>The resource may be copied into.</summary>
    TransferDestination = 1 << 5,
}

/// <summary>How long the renderer keeps a declared resource alive.</summary>
public enum RenderResourceLifetime
{
    /// <summary>Only for the pass that uses it.</summary>
    TransientPass,

    /// <summary>For one frame in flight.</summary>
    FrameFlight,

    /// <summary>For as long as the pack stays selected.</summary>
    ActivePack,
}

/// <summary>
/// The renderer-owned role of a resource. A pack tags a resource with the role
/// it plays so the client can drive its own code for that role; ids stay
/// pack-owned and are never interpreted.
/// </summary>
public enum RenderResourceSemantic
{
    /// <summary>A resource of the pack's own, with no meaning to the client.</summary>
    Custom,

    /// <summary>The off-screen high-dynamic-range world image.</summary>
    MainWorldHdr,

    /// <summary>The first of the two images a bloom blur ping-pongs between.</summary>
    BloomPing,

    /// <summary>The second of the two images a bloom blur ping-pongs between.</summary>
    BloomPong,

    /// <summary>The mask saying how much of the sun each pixel can see.</summary>
    SunOcclusionMask,

    /// <summary>The rendered sun rays.</summary>
    SunRays,

    /// <summary>The directional shadow depth map.</summary>
    DirectionalShadowDepth,

    /// <summary>The rendered volumetric light shafts.</summary>
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
/// <param name="Id">
/// A stable name for this resource, unique inside the pack; passes refer to it
/// by this name.
/// </param>
/// <param name="Kind">Whether it is a single image, an image stack, or a buffer.</param>
/// <param name="Format">Which kind of values the image holds.</param>
/// <param name="Extent">
/// How large the image is. Required for images and must be absent for a buffer.
/// </param>
/// <param name="SizeBytes">
/// The size of a buffer resource in bytes; zero for an image. Must not be
/// negative.
/// </param>
/// <param name="Usage">What the renderer is allowed to do with the resource.</param>
/// <param name="Lifetime">How long the renderer keeps the resource alive.</param>
/// <param name="EstimatedResidentBytes">
/// The pack author's estimate of the GPU memory this resource occupies. The
/// client adds these up and refuses a pack whose total is above the contract's
/// ceiling.
/// </param>
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
    /// <summary>
    /// The role this resource plays for the renderer. Leave it at
    /// <see cref="RenderResourceSemantic.Custom"/> for a resource only the
    /// pack's own shaders use.
    /// </summary>
    public RenderResourceSemantic Semantic { get; init; } = RenderResourceSemantic.Custom;
}

/// <summary>
/// Renderer-owned execution meaning of a pass. IDs remain pack-owned stable
/// identifiers; semantic execution never depends on a particular ID string.
/// </summary>
public enum RenderPassSemantic
{
    /// <summary>
    /// A full-screen pass of the pack's own, which the client runs without
    /// interpreting it.
    /// </summary>
    CustomFullscreen,

    /// <summary>Fills the directional shadow depth map.</summary>
    DirectionalShadowDepth,

    /// <summary>Shrinks the bright parts of the image for the bloom blur.</summary>
    BloomDownsample,

    /// <summary>Blurs the bloom image sideways.</summary>
    BloomBlurHorizontal,

    /// <summary>Blurs the bloom image up and down.</summary>
    BloomBlurVertical,

    /// <summary>Works out how much of the sun each pixel can see.</summary>
    SunOcclusion,

    /// <summary>Draws the rays streaming out from the sun.</summary>
    SunRays,

    /// <summary>Draws shafts of light through the air.</summary>
    VolumetricShafts,

    /// <summary>
    /// Combines everything and maps the high-dynamic-range image down to the
    /// picture shown on screen.
    /// </summary>
    FilmicComposite,
}

/// <summary>One declarative full-screen, atmosphere, or tone-map pass.</summary>
/// <param name="Id">A stable name for this pass, unique inside the pack.</param>
/// <param name="Hook">Where in the frame the pass runs.</param>
/// <param name="VertexShaderAsset">
/// The asset key of the pass's compiled vertex shader, as the pack's asset
/// source spells it.
/// </param>
/// <param name="FragmentShaderAsset">
/// The asset key of the pass's compiled fragment shader.
/// </param>
/// <param name="SemanticInputs">
/// Which frame facts the client should bind for this pass. Each one needs its
/// matching capability.
/// </param>
/// <param name="ResourceReads">
/// The ids of declared resources this pass samples. Each must be sampleable and
/// must already have been written by an earlier pass.
/// </param>
/// <param name="ResourceWrites">
/// The ids of declared resources this pass draws into. Each must allow being
/// drawn into.
/// </param>
public sealed record RenderPassDeclaration(
    string Id,
    RenderPassHook Hook,
    string VertexShaderAsset,
    string FragmentShaderAsset,
    IReadOnlyList<RenderSemanticInput> SemanticInputs,
    IReadOnlyList<string> ResourceReads,
    IReadOnlyList<string> ResourceWrites)
{
    /// <summary>
    /// What this pass means to the renderer. Leave it at
    /// <see cref="RenderPassSemantic.CustomFullscreen"/> for a pass the client
    /// should simply run.
    /// </summary>
    public RenderPassSemantic Semantic { get; init; } = RenderPassSemantic.CustomFullscreen;
}

/// <summary>A renderer-owned replay of retained scene geometry.</summary>
/// <param name="Id">A stable name for this replay, unique inside the pack.</param>
/// <param name="Semantic">Which renderer replay operation to run.</param>
/// <param name="CasterClasses">
/// Which kinds of geometry the replay draws; at least one must be set.
/// </param>
/// <param name="ViewCount">
/// How many views the replay draws, such as one per shadow cascade. Must be
/// between one and four.
/// </param>
public sealed record SceneReplayDeclaration(
    string Id,
    RenderSceneReplaySemantic Semantic,
    RenderCasterClass CasterClasses,
    int ViewCount);

/// <summary>A shader specialization of an existing renderer pipeline.</summary>
/// <param name="Id">A stable name for this variant, unique inside the pack.</param>
/// <param name="BaseSemantic">Which renderer pipeline the variant replaces.</param>
/// <param name="VertexShaderAsset">
/// The asset key of the variant's compiled vertex shader.
/// </param>
/// <param name="FragmentShaderAsset">
/// The asset key of the variant's compiled fragment shader.
/// </param>
/// <param name="CompatibleMaterials">
/// Which material kinds these shaders can draw; at least one must be set.
/// </param>
/// <param name="SemanticInputs">
/// Which frame facts the client should bind for the variant. Each one needs its
/// matching capability.
/// </param>
public sealed record PipelineVariantDeclaration(
    string Id,
    RenderPipelineBaseSemantic BaseSemantic,
    string VertexShaderAsset,
    string FragmentShaderAsset,
    RenderMaterialClass CompatibleMaterials,
    IReadOnlyList<RenderSemanticInput> SemanticInputs)
{
    /// <summary>
    /// The fixed role this variant plays. Leave it at
    /// <see cref="RenderPipelineVariantSemantic.Custom"/> for a specialization
    /// the client should simply use in place of its own shaders.
    /// </summary>
    public RenderPipelineVariantSemantic Semantic { get; init; } =
        RenderPipelineVariantSemantic.Custom;
}

/// <summary>Renderer-owned role of a fixed retained-scene pipeline variant.</summary>
public enum RenderPipelineVariantSemantic
{
    /// <summary>A specialization with no fixed role of its own.</summary>
    Custom,

    /// <summary>Draws the landscape into the directional shadow map.</summary>
    TerrainDirectionalShadowCaster,

    /// <summary>Draws solid world geometry into the directional shadow map.</summary>
    WorldOpaqueDirectionalShadowCaster,

    /// <summary>Draws masked world geometry into the directional shadow map.</summary>
    WorldAlphaCutoutDirectionalShadowCaster,

    /// <summary>Draws the landscape with directional shadows applied to it.</summary>
    TerrainDirectionalShadowReceiver,

    /// <summary>Draws world geometry with directional shadows applied to it.</summary>
    WorldDirectionalShadowReceiver,

    /// <summary>
    /// Draws the landscape into every shadow cascade in one pass.
    /// </summary>
    TerrainMultiviewDirectionalShadowCaster,

    /// <summary>
    /// Draws solid world geometry into every shadow cascade in one pass.
    /// </summary>
    WorldOpaqueMultiviewDirectionalShadowCaster,

    /// <summary>
    /// Draws masked world geometry into every shadow cascade in one pass.
    /// </summary>
    WorldAlphaCutoutMultiviewDirectionalShadowCaster,
}

/// <summary>Per-preset replacement for one resource's size.</summary>
/// <param name="ResourceId">The id of the declared resource being resized.</param>
/// <param name="Extent">
/// The size to use at this preset instead of the resource's own; null keeps the
/// declared size.
/// </param>
/// <param name="SizeBytes">
/// The buffer size in bytes at this preset; zero for an image. Must not be
/// negative.
/// </param>
/// <param name="EstimatedResidentBytes">
/// The pack author's estimate of the GPU memory the resource occupies at this
/// preset. Must not be negative.
/// </param>
public sealed record RenderQualityResourceOverride(
    string ResourceId,
    RenderExtentDeclaration? Extent,
    long SizeBytes,
    long EstimatedResidentBytes);

/// <summary>Per-preset value for a declared user setting.</summary>
/// <param name="SettingId">The id of the declared setting being set.</param>
/// <param name="Value">
/// The value in the same text form the setting's default uses; it must be valid
/// for that setting, and a preset may set each setting only once.
/// </param>
public sealed record RenderQualitySettingOverride(
    string SettingId,
    string Value);

/// <summary>
/// One quality level a player can pick for a pack, with the sizes, setting
/// values, and cost limits that go with it. Every pack declares at least one.
/// </summary>
/// <param name="Id">A stable name for this preset, unique inside the pack.</param>
/// <param name="DisplayName">The name shown to the player.</param>
/// <param name="RequiredCapabilities">
/// What the machine must support for this preset specifically; the preset is
/// offered only where they are all present.
/// </param>
/// <param name="ResourceOverrides">Resource sizes that differ at this preset.</param>
/// <param name="SettingOverrides">Setting values that differ at this preset.</param>
/// <param name="MaxResidentGpuBytes">
/// How much GPU memory the pack may hold at this preset, expressed for a
/// 1920-by-1080 view; the client scales it up for larger views and caps it at
/// the share the machine allows.
/// </param>
/// <param name="MaxIncrementalGpuMillisecondsP50">
/// The typical extra GPU time per frame this preset is allowed to cost, in
/// milliseconds. Must not exceed the P99 figure.
/// </param>
/// <param name="MaxIncrementalGpuMillisecondsP99">
/// The worst-case extra GPU time per frame this preset is allowed to cost, in
/// milliseconds.
/// </param>
/// <param name="MaxIncrementalCpuMillisecondsP50">
/// The typical extra CPU time per frame this preset is allowed to cost, in
/// milliseconds. Must not exceed the P99 figure.
/// </param>
/// <param name="MaxIncrementalCpuMillisecondsP99">
/// The worst-case extra CPU time per frame this preset is allowed to cost, in
/// milliseconds.
/// </param>
/// <param name="AutoEligible">
/// Whether automatic quality may switch to this preset on its own. Automatic
/// quality needs one eligible Low, Medium, and High preset to work.
/// </param>
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
    /// <summary>
    /// Which of the standard quality steps this preset is, so automatic quality
    /// can move between them. Leave it at
    /// <see cref="RenderQualitySemantic.Custom"/> for a step of the pack's own.
    /// </summary>
    public RenderQualitySemantic Semantic { get; init; } = RenderQualitySemantic.Custom;

    /// <summary>
    /// Optional renderer-owned execution optimizations whose shader ABI the
    /// pack explicitly implements. The host never infers these from a pack ID.
    /// </summary>
    public RenderQualityExecutionHints ExecutionHints { get; init; } =
        RenderQualityExecutionHints.None;
}

/// <summary>
/// Faster renderer paths a preset opts into. Each one has its own shader
/// requirements, so a pack only sets a flag whose shaders it actually ships.
/// </summary>
[Flags]
public enum RenderQualityExecutionHints
{
    /// <summary>No optimization; the passes run as declared.</summary>
    None = 0,

    /// <summary>
    /// Run the standard atmosphere passes as one combined pass. Allowed only on
    /// a Low preset of a pack that declares the complete standard pass set.
    /// </summary>
    FusedAtmosphericPostProcess = 1 << 0,

    /// <summary>
    /// Fill every shadow cascade in a single pass. Allowed only on a Low preset
    /// that also requires the matching device capability.
    /// </summary>
    MultiviewDirectionalShadowCascades = 1 << 1,
}

/// <summary>Which standard quality step a preset stands for.</summary>
public enum RenderQualitySemantic
{
    /// <summary>A step of the pack's own, outside the standard ladder.</summary>
    Custom,

    /// <summary>The cheapest step, and automatic quality's safe fallback.</summary>
    Low,

    /// <summary>The middle step.</summary>
    Medium,

    /// <summary>The most expensive step.</summary>
    High,

    /// <summary>
    /// The step that lets the client move between Low, Medium, and High by
    /// itself from measured frame cost. It needs GPU timing on the machine.
    /// </summary>
    Automatic,
}

/// <summary>Storage and presentation kind for a pack-defined setting.</summary>
public enum RenderSettingKind
{
    /// <summary>An on-or-off switch, written as true or false.</summary>
    Boolean,

    /// <summary>A whole number, kept inside the declared bounds and step.</summary>
    Integer,

    /// <summary>A fractional number, kept inside the declared bounds and step.</summary>
    Float,

    /// <summary>One of the declared choices, matched exactly.</summary>
    Choice,
}

/// <summary>A bounded, user-visible pack setting.</summary>
/// <param name="Id">A stable name for this setting, unique inside the pack.</param>
/// <param name="DisplayName">The label shown to the player.</param>
/// <param name="Kind">What sort of value the setting holds.</param>
/// <param name="DefaultValue">
/// The value used when neither a preset nor the player has set one. It is
/// written as text and must be valid for this setting.
/// </param>
/// <param name="Minimum">The smallest value allowed, or null for no lower bound.</param>
/// <param name="Maximum">The largest value allowed, or null for no upper bound.</param>
/// <param name="Step">
/// The spacing of allowed values, counted from the minimum, or null when any
/// value inside the bounds is allowed.
/// </param>
/// <param name="Choices">
/// The allowed values for a choice setting, in the order they are offered;
/// empty for the other kinds.
/// </param>
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
    /// <summary>
    /// Which known knob this setting is, so the client's own code can read it
    /// rather than only the pack's shaders. Leave it at
    /// <see cref="RenderSettingSemantic.Custom"/> for a setting only the
    /// shaders use.
    /// </summary>
    public RenderSettingSemantic Semantic { get; init; } = RenderSettingSemantic.Custom;
}

/// <summary>
/// The known knobs a pack setting can stand for. Every declared setting also
/// reaches the shaders; a semantic additionally lets the client's own code use
/// the value.
/// </summary>
public enum RenderSettingSemantic
{
    /// <summary>A setting of the pack's own, meaningful only to its shaders.</summary>
    Custom,

    /// <summary>How strong the glow around bright areas is.</summary>
    BloomStrength,

    /// <summary>How strongly the filmic tone curve is applied.</summary>
    FilmicStrength,

    /// <summary>Overall image brightness before tone mapping.</summary>
    Exposure,

    /// <summary>How colourful the graded image is.</summary>
    GradeSaturation,

    /// <summary>How much contrast the graded image has.</summary>
    GradeContrast,

    /// <summary>How much the image darkens towards its corners.</summary>
    VignetteStrength,

    /// <summary>How strong the rays streaming from the sun are.</summary>
    SunRayStrength,

    /// <summary>How dark directional shadows are.</summary>
    DirectionalShadowStrength,

    /// <summary>How far from the camera directional shadows still fall, in meters.</summary>
    DirectionalShadowReachMetres,

    /// <summary>
    /// How many samples soften a shadow edge; more taps give a smoother edge
    /// and cost more.
    /// </summary>
    DirectionalShadowPcfTaps,

    /// <summary>How strong the shafts of light through the air are.</summary>
    VolumetricStrength,

    /// <summary>
    /// How many steps the volumetric shafts are sampled along; more steps look
    /// smoother and cost more.
    /// </summary>
    VolumetricRayMarchSteps,

    /// <summary>
    /// Whether the client may change quality step by itself from measured
    /// frame cost. Must be a boolean setting.
    /// </summary>
    AutomaticQuality,

    /// <summary>Whether foliage sways in the wind at all.</summary>
    WindEnabled,

    /// <summary>An overall multiplier on how hard the wind blows.</summary>
    WindStrength,

    /// <summary>The wind's compass direction, in degrees.</summary>
    WindDirectionDegrees,

    /// <summary>How far a whole plant leans in the wind, in meters.</summary>
    WindLeanMetres,

    /// <summary>How far individual branches swing, in meters.</summary>
    WindBranchMetres,

    /// <summary>How far leaves flutter, in meters.</summary>
    WindFlutterMetres,

    /// <summary>
    /// The height in meters treated as a full canopy, which scales how much
    /// higher parts of a plant move.
    /// </summary>
    WindCanopyHeightMetres,
}

/// <summary>
/// One point on a curve that maps how high the sun or moon stands to an effect
/// multiplier. Points are listed from low elevation to high and the client
/// interpolates between them.
/// </summary>
/// <param name="ElevationDegrees">
/// The light's height above the horizon in degrees, from -90 to 90.
/// </param>
/// <param name="Multiplier">
/// The effect strength at that elevation. Must not be negative, and for some
/// curves must not exceed one.
/// </param>
public sealed record SunElevationResponsePoint(
    double ElevationDegrees,
    double Multiplier);

/// <summary>Explicit mapping from an authored AC day group to an effect multiplier.</summary>
/// <param name="ActiveDayGroup">
/// The day grouping this entry applies to; each group may appear only once.
/// </param>
/// <param name="Multiplier">
/// The effect strength while that grouping is in effect. Must not be negative.
/// </param>
public sealed record ActiveDayGroupMultiplier(
    int ActiveDayGroup,
    double Multiplier);

/// <summary>How hard foliage sways during one kind of weather.</summary>
/// <param name="WeatherKind">
/// The weather this entry applies to, spelled exactly as the client names it;
/// each kind may appear only once.
/// </param>
/// <param name="Mean">The steady wind strength, which must not be negative.</param>
/// <param name="Gust">
/// How much the wind rises and falls above the steady strength. Must not be
/// negative.
/// </param>
public sealed record FoliageWindWeatherPoint(
    string WeatherKind,
    double Mean,
    double Gust);

/// <summary>Visible authored-atmosphere interpretation owned by the pack.</summary>
/// <param name="SunElevationResponse">
/// How strong the pack's sun-driven effects are at each sun elevation.
/// </param>
/// <param name="ActiveDayGroupMultipliers">
/// How strong those effects are during each authored day grouping.
/// </param>
public sealed record AtmospherePolicyDeclaration(
    IReadOnlyList<SunElevationResponsePoint> SunElevationResponse,
    IReadOnlyList<ActiveDayGroupMultiplier> ActiveDayGroupMultipliers)
{
    /// <summary>
    /// How strong directional shadows are at each elevation of the light that
    /// casts them. Multipliers stay between zero and one. Empty when the pack
    /// says nothing about it.
    /// </summary>
    public IReadOnlyList<SunElevationResponsePoint> DirectionalShadowLightElevationResponse
        { get; init; } = [];

    /// <summary>
    /// How strong volumetric light shafts are at each sun elevation.
    /// Multipliers stay between zero and one. Empty when the pack says nothing
    /// about it.
    /// </summary>
    public IReadOnlyList<SunElevationResponsePoint> VolumetricShaftSunElevationResponse
        { get; init; } = [];

    /// <summary>
    /// The wind strength for each kind of weather. When the current weather is
    /// not listed the client falls back to the clear-weather entry, and to no
    /// wind at all when there is none. Empty when the pack says nothing about it.
    /// </summary>
    public IReadOnlyList<FoliageWindWeatherPoint> FoliageWindByWeather
        { get; init; } = [];

    /// <summary>
    /// Ids of graphics objects in the game's data files that should never sway,
    /// however windy it is. Empty when the pack excludes nothing.
    /// </summary>
    public IReadOnlyList<uint> FoliageExclusions { get; init; } = [];
}

/// <summary>
/// Everything one render pack declares about itself: what it needs from the
/// machine, the images it works in, the passes and shaders it runs, the quality
/// steps and settings it offers, and how it reads the sky. The client validates
/// the whole declaration before a player can select the pack.
/// </summary>
/// <param name="Id">
/// A stable lowercase id for the pack, unique among registered packs; it is
/// also how a player's selection is remembered.
/// </param>
/// <param name="DisplayName">The pack's name as shown to the player.</param>
/// <param name="PackVersion">The pack's own version.</param>
/// <param name="PackApiVersion">
/// The render-pack contract version the pack was written against, normally
/// <see cref="RenderPackApi.Current"/>. The client refuses a version it does
/// not support.
/// </param>
/// <param name="HighestTier">How far into the renderer this pack reaches.</param>
/// <param name="RequiredCapabilities">
/// What the machine must support. A pack is not offered where any of these is
/// missing.
/// </param>
/// <param name="OptionalCapabilities">
/// What the pack can make use of when present but does not need; a missing one
/// never disqualifies the pack.
/// </param>
/// <param name="Resources">The intermediate images the pack works in.</param>
/// <param name="Passes">The passes the pack runs and where in the frame.</param>
/// <param name="SceneReplays">The extra scene draws the pack asks for.</param>
/// <param name="PipelineVariants">
/// The renderer pipelines the pack replaces with its own shaders.
/// </param>
/// <param name="QualityPresets">
/// The quality steps offered to the player; at least one is required.
/// </param>
/// <param name="Settings">The pack's user-visible settings.</param>
/// <param name="AtmospherePolicy">
/// How the pack reads the sky, or null when it has no opinion.
/// </param>
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
    /// <summary>
    /// One or two sentences describing what the pack changes, shown to the
    /// player beside its name. A pack must supply one.
    /// </summary>
    public string FeatureSummary { get; init; } = string.Empty;
}
