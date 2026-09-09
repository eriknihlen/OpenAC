using System.Collections.Concurrent;
using AcDream.App.Rendering;
using AcDream.App.Rendering.Wb;
using AcDream.App.Rendering.Residency;
using AcDream.App.World;
using AcDream.Content;
using AcDream.Content.Vfx;
using AcDream.Core.Audio;
using AcDream.Core.Physics;
using AcDream.Core.Terrain;
using AcDream.UI.Abstractions.Settings;
using DatReaderWriter;
using DatReaderWriter.DBObjs;
using Microsoft.Extensions.Logging.Abstractions;
using Silk.NET.Input;

namespace AcDream.App.Composition;

internal sealed record WorldRegionData(Region Region, float[] HeightTable);

internal sealed record WorldTerrainBuildContext(
    uint InitialCenterLandblockId,
    int InitialCenterX,
    int InitialCenterY,
    float[] HeightTable,
    TerrainBlendingContext Blending,
    ConcurrentDictionary<uint, SurfaceInfo> SurfaceCache);

internal sealed record WorldRenderFoundation(
    string ShadersDirectory,
    TerrainAtlas? TerrainAtlas,
    SceneLightingUboBinding? SceneLighting,
    DebugLineRenderer DebugLines,
    BitmapFont? DebugFont,
    TextRenderer? TextRenderer,
    TerrainModernRenderer? Terrain,
    WbMeshAdapter? MeshAdapter,
    TextureCache TextureCache,
    ResidencyManager Residency);

internal sealed record WorldRenderResult(
    WorldTerrainBuildContext TerrainBuild,
    WorldRenderFoundation Foundation);

internal sealed record WorldRenderDependencies(
    WorldEnvironmentController Environment,
    IGameRenderResourceLifetime RenderResources,
    IGpuResourceRetirementQueue ResourceRetirement,
    ResidencyBudgetOptions ResidencyBudgets,
    uint InitialCenterLandblockId,
    string DiagnosticsDirectory,
    Action<string> Log,
    AcDream.App.Rendering.Gpu.IGpuDevice GpuDevice,
    AcDream.App.Rendering.ICurrentGpuFrameSource GpuFrameSource);

internal interface IGameWindowWorldRenderPublication
{
    void PublishSceneLighting(SceneLightingUboBinding value);
    void PublishDebugLines(DebugLineRenderer value);
    void PublishHudResources(BitmapFont font, TextRenderer text);
    void PublishTerrain(TerrainModernRenderer value);
    void PublishTerrainBuildState(
        float[] heightTable,
        TerrainBlendingContext blending,
        ConcurrentDictionary<uint, SurfaceInfo> surfaceCache);
    void PublishWbMeshAdapter(WbMeshAdapter value);
    void PublishTextureCache(TextureCache value);
}

internal interface IWorldRenderCompositionFactory
{
    WorldRegionData LoadRegion(IDatReaderWriter dats);
    void InitializeEnvironment(
        WorldEnvironmentController environment,
        Region region,
        IDatReaderWriter dats);
    TerrainAtlas AcquireBackendNeutralTerrainAtlas(
        IGameRenderResourceLifetime lifetime,
        AcDream.App.Rendering.Gpu.IGpuDevice device,
        IDatReaderWriter dats);

    void ExerciseBackendNeutralWorldTextures(
        AcDream.App.Rendering.Gpu.IGpuDevice device,
        Action<string> log);

    void SetTerrainAnisotropic(TerrainAtlas atlas, int level);
    SceneLightingUboBinding CreateBackendNeutralSceneLighting(
        ICurrentGpuFrameSource frameSource,
        IWorldPassScope scope);
    DebugLineRenderer CreateDebugLines(
        AcDream.App.Rendering.Gpu.IGpuDevice device,
        ICurrentGpuFrameSource frameSource,
        string shadersDirectory);
    byte[]? TryLoadDebugFont();
    BitmapFont CreateDebugFont(AcDream.App.Rendering.Gpu.IGpuDevice device, byte[] bytes);
    TextRenderer CreateTextRenderer(
        AcDream.App.Rendering.Gpu.IGpuDevice device,
        ICurrentGpuFrameSource frameSource,
        string shadersDirectory);
    TerrainModernRenderer CreateBackendNeutralTerrain(
        AcDream.App.Rendering.Gpu.IGpuDevice gpuDevice,
        ICurrentGpuFrameSource frameSource,
        IWorldPassScope scope,
        TerrainAtlas atlas,
        IGpuResourceRetirementQueue retirement);
    WorldTerrainBuildContext CreateTerrainBuildContext(
        uint initialCenterLandblockId,
        float[] heightTable,
        TerrainAtlas? atlas);
    WbMeshAdapter CreateMeshAdapter(
        AcDream.App.Rendering.Gpu.IGpuDevice device,
        IDatReaderWriter dats,
        IPreparedAssetSource preparedAssets,
        IGpuResourceRetirementQueue retirement,
        ResidencyBudgetOptions budgets);
    TextureCache CreateTextureCache(
        AcDream.App.Rendering.Gpu.IGpuDevice device,
        IDatReaderWriter dats,
        IGpuResourceRetirementQueue retirement,
        string diagnosticsDirectory,
        ResidencyBudgetOptions budgets);
    void RegisterResidencySources(
        ResidencyManager manager,
        WbMeshAdapter? meshes,
        TextureCache textures,
        IPreparedAssetSource preparedAssets,
        IAnimationLoader animations,
        DatSoundCache? audio);
    void Release(IDisposable resource);
}

internal sealed class RetailWorldRenderCompositionFactory
    : IWorldRenderCompositionFactory
{
    public WorldRegionData LoadRegion(IDatReaderWriter dats)
    {
        ArgumentNullException.ThrowIfNull(dats);
        Region region = dats.Get<Region>(0x13000000u)
            ?? throw new InvalidOperationException(
                "Region dat id 0x13000000 missing");
        float[]? heightTable = region.LandDefs.LandHeightTable;
        if (heightTable is null || heightTable.Length < 256)
        {
            throw new InvalidOperationException(
                "Region.LandDefs.LandHeightTable missing or truncated");
        }
        return new WorldRegionData(region, heightTable);
    }

    public void InitializeEnvironment(
        WorldEnvironmentController environment,
        Region region,
        IDatReaderWriter dats)
    {
        ArgumentNullException.ThrowIfNull(environment);
        ArgumentNullException.ThrowIfNull(region);
        ArgumentNullException.ThrowIfNull(dats);
        environment.Initialize(region, dats);
    }

    public TerrainAtlas AcquireBackendNeutralTerrainAtlas(
        IGameRenderResourceLifetime lifetime,
        AcDream.App.Rendering.Gpu.IGpuDevice device,
        IDatReaderWriter dats)
    {
        ArgumentNullException.ThrowIfNull(lifetime);
        return lifetime.AcquireTerrainAtlas(
            () => TerrainAtlas.BuildBackendNeutral(device, dats));
    }

    public void ExerciseBackendNeutralWorldTextures(
        AcDream.App.Rendering.Gpu.IGpuDevice device,
        Action<string> log) =>
        BackendNeutralWorldTextures.Exercise(device, log);

    public void SetTerrainAnisotropic(TerrainAtlas atlas, int level) =>
        atlas.SetAnisotropic(level);

    public SceneLightingUboBinding CreateBackendNeutralSceneLighting(
        ICurrentGpuFrameSource frameSource,
        IWorldPassScope scope) =>
        new(frameSource, scope.Sections);

    public DebugLineRenderer CreateDebugLines(
        AcDream.App.Rendering.Gpu.IGpuDevice device,
        ICurrentGpuFrameSource frameSource,
        string shadersDirectory) =>
        new(device, frameSource, shadersDirectory);

    public byte[]? TryLoadDebugFont() =>
        BitmapFont.TryLoadSystemMonospaceFont();

    public BitmapFont CreateDebugFont(AcDream.App.Rendering.Gpu.IGpuDevice device, byte[] bytes) =>
        new(device, bytes, pixelHeight: 15f, atlasSize: 512);

    public TextRenderer CreateTextRenderer(
        AcDream.App.Rendering.Gpu.IGpuDevice device,
        ICurrentGpuFrameSource frameSource,
        string shadersDirectory) =>
        new(device, frameSource, shadersDirectory);

    public TerrainModernRenderer CreateBackendNeutralTerrain(
        AcDream.App.Rendering.Gpu.IGpuDevice gpuDevice,
        ICurrentGpuFrameSource frameSource,
        IWorldPassScope scope,
        TerrainAtlas atlas,
        IGpuResourceRetirementQueue retirement) =>
        new(gpuDevice, frameSource, scope, atlas, retirement);

    public WorldTerrainBuildContext CreateTerrainBuildContext(
        uint initialCenterLandblockId,
        float[] heightTable,
        TerrainAtlas? atlas)
    {
        int centerX = (int)((initialCenterLandblockId >> 24) & 0xFFu);
        int centerY = (int)((initialCenterLandblockId >> 16) & 0xFFu);
        if (atlas is null)
        {
            return new WorldTerrainBuildContext(
                initialCenterLandblockId,
                centerX,
                centerY,
                heightTable,
                new TerrainBlendingContext(
                    TerrainTypeToLayer: new Dictionary<uint, byte>(),
                    RoadLayer: SurfaceInfo.None,
                    CornerAlphaLayers: [],
                    SideAlphaLayers: [],
                    RoadAlphaLayers: [],
                    CornerAlphaTCodes: [],
                    SideAlphaTCodes: [],
                    RoadAlphaRCodes: []),
                new ConcurrentDictionary<uint, SurfaceInfo>());
        }

        var layers = new Dictionary<uint, byte>(atlas.TerrainTypeToLayer.Count);
        foreach ((uint terrainType, uint layer) in atlas.TerrainTypeToLayer)
            layers[terrainType] = (byte)layer;

        const uint RoadTypeEnumValue = 0x20;
        byte roadLayer = layers.TryGetValue(RoadTypeEnumValue, out byte road)
            ? road
            : SurfaceInfo.None;
        var blending = new TerrainBlendingContext(
            TerrainTypeToLayer: layers,
            RoadLayer: roadLayer,
            CornerAlphaLayers: atlas.CornerAlphaLayers,
            SideAlphaLayers: atlas.SideAlphaLayers,
            RoadAlphaLayers: atlas.RoadAlphaLayers,
            CornerAlphaTCodes: atlas.CornerAlphaTCodes,
            SideAlphaTCodes: atlas.SideAlphaTCodes,
            RoadAlphaRCodes: atlas.RoadAlphaRCodes);
        return new WorldTerrainBuildContext(
            initialCenterLandblockId,
            centerX,
            centerY,
            heightTable,
            blending,
            new ConcurrentDictionary<uint, SurfaceInfo>());
    }

    public WbMeshAdapter CreateMeshAdapter(
        AcDream.App.Rendering.Gpu.IGpuDevice device,
        IDatReaderWriter dats,
        IPreparedAssetSource preparedAssets,
        IGpuResourceRetirementQueue retirement,
        ResidencyBudgetOptions budgets) =>
        new(
            device,
            dats,
            preparedAssets,
            NullLogger<WbMeshAdapter>.Instance,
            retirement,
            budgets);

    public TextureCache CreateTextureCache(
        AcDream.App.Rendering.Gpu.IGpuDevice device,
        IDatReaderWriter dats,
        IGpuResourceRetirementQueue retirement,
        string diagnosticsDirectory,
        ResidencyBudgetOptions budgets) =>
        new(
            device,
            dats,
            retirement,
            diagnosticsDirectory,
            budgets);

    public void RegisterResidencySources(
        ResidencyManager manager,
        WbMeshAdapter? meshes,
        TextureCache textures,
        IPreparedAssetSource preparedAssets,
        IAnimationLoader animations,
        DatSoundCache? audio)
    {
        meshes?.RegisterResidencySources(manager);
        textures.RegisterResidencySources(manager);
        manager.RegisterDomainSource(new DelegateResidencyDomainSource(
            ResidencyDomain.PreparedPackage,
            () => new ResidencyDomainSnapshot(
                ResidencyDomain.PreparedPackage,
                EntryCount: 1,
                OwnerCount: 1,
                Charges: new ResidencyCharges(
                    MappedVirtualBytes:
                        preparedAssets.MappedVirtualBytes))));
        if (animations is RetailAnimationLoader retailAnimations)
        {
            manager.RegisterDomainSource(new DelegateResidencyDomainSource(
                ResidencyDomain.Animations,
                () =>
                {
                    AnimationCacheDiagnostics diagnostics =
                        retailAnimations.Diagnostics;
                    return new ResidencyDomainSnapshot(
                        ResidencyDomain.Animations,
                        EntryCount: diagnostics.Count,
                        OwnerCount: 0,
                        Charges: new ResidencyCharges(
                            DecodedBytes:
                                diagnostics.EstimatedBytes),
                        BudgetBytes: diagnostics.BudgetBytes,
                        Hits: diagnostics.Stats.Hits,
                        Misses: diagnostics.Stats.Misses,
                        Evictions: diagnostics.Stats.Evictions);
                }));
        }
        if (audio is not null)
        {
            manager.RegisterDomainSource(new DelegateResidencyDomainSource(
                ResidencyDomain.Audio,
                () =>
                {
                    DatSoundCacheDiagnostics diagnostics = audio.Diagnostics;
                    return new ResidencyDomainSnapshot(
                        ResidencyDomain.Audio,
                        EntryCount: diagnostics.CachedWaveCount,
                        OwnerCount: 0,
                        Charges: new ResidencyCharges(
                            DecodedBytes: diagnostics.ResidentWaveBytes),
                        BudgetBytes: diagnostics.BudgetBytes,
                        Hits: diagnostics.Hits,
                        Misses: diagnostics.Misses,
                        Evictions: diagnostics.Evictions);
                }));
        }
    }

    public void Release(IDisposable resource) => resource.Dispose();
}

internal enum WorldRenderCompositionPoint
{
    RegionLoaded,
    EnvironmentInitialized,
    TerrainAtlasAcquired,
    SceneLightingPublished,
    DebugLinesPublished,
    DebugFontCreated,
    TextRendererCreated,
    HudResourcesPublished,
    HudResourcesCompleted,
    TerrainPublished,
    TerrainBuildStatePublished,
    MeshAdapterPublished,
    TextureCachePublished,
}

internal sealed class WorldRenderCompositionPhase
    : IWorldRenderCompositionPhase<
        GameWindowPlatformResult<GameWindowGraphics, IInputContext>,
        ContentEffectsAudioResult,
        SettingsDevToolsResult,
        WorldRenderResult>
{
    private readonly WorldRenderDependencies _dependencies;
    private readonly IGameWindowWorldRenderPublication _publication;
    private readonly IWorldRenderCompositionFactory _factory;
    private readonly Action<WorldRenderCompositionPoint>? _faultInjection;

    public WorldRenderCompositionPhase(
        WorldRenderDependencies dependencies,
        IGameWindowWorldRenderPublication publication,
        IWorldRenderCompositionFactory? factory = null,
        Action<WorldRenderCompositionPoint>? faultInjection = null)
    {
        _dependencies = dependencies
            ?? throw new ArgumentNullException(nameof(dependencies));
        _publication = publication
            ?? throw new ArgumentNullException(nameof(publication));
        _factory = factory ?? new RetailWorldRenderCompositionFactory();
        _faultInjection = faultInjection;
    }

    public WorldRenderResult Compose(
        GameWindowPlatformResult<GameWindowGraphics, IInputContext> platform,
        ContentEffectsAudioResult content,
        SettingsDevToolsResult settings)
    {
        ArgumentNullException.ThrowIfNull(platform);
        ArgumentNullException.ThrowIfNull(content);
        ArgumentNullException.ThrowIfNull(settings);

        var scope = new CompositionAcquisitionScope();
        try
        {
            var residency = new ResidencyManager(
                _dependencies.ResidencyBudgets);

            WorldRegionData region = _factory.LoadRegion(content.Dats);
            Fault(WorldRenderCompositionPoint.RegionLoaded);
            _factory.InitializeEnvironment(
                _dependencies.Environment,
                region.Region,
                content.Dats);
            Fault(WorldRenderCompositionPoint.EnvironmentInitialized);

            TerrainAtlas terrainAtlas = _factory.AcquireBackendNeutralTerrainAtlas(
                _dependencies.RenderResources,
                _dependencies.GpuDevice,
                content.Dats);
            _factory.SetTerrainAnisotropic(
                terrainAtlas,
                settings.ResolvedQuality.AnisotropicLevel);
            Fault(WorldRenderCompositionPoint.TerrainAtlasAcquired);

            _factory.ExerciseBackendNeutralWorldTextures(
                _dependencies.GpuDevice,
                _dependencies.Log);

            string shadersDirectory = Path.Combine(
                AppContext.BaseDirectory,
                "Rendering",
                "Shaders");
            IWorldPassScope worldPassScope = platform.Graphics.WorldPassScope
                ?? throw new InvalidOperationException(
                    "The graphics backend must publish a world pass scope.");
            SceneLightingUboBinding sceneLighting = AcquireAndPublish(
                scope,
                "scene lighting",
                () => _factory.CreateBackendNeutralSceneLighting(
                    _dependencies.GpuFrameSource,
                    worldPassScope),
                _publication.PublishSceneLighting,
                WorldRenderCompositionPoint.SceneLightingPublished);
            DebugLineRenderer debugLines = AcquireAndPublish(
                scope,
                "debug lines",
                () => _factory.CreateDebugLines(
                    _dependencies.GpuDevice,
                    _dependencies.GpuFrameSource,
                    shadersDirectory),
                _publication.PublishDebugLines,
                WorldRenderCompositionPoint.DebugLinesPublished);

            (BitmapFont? debugFont, TextRenderer? textRenderer) =
                ComposeOptionalHudResources(scope, shadersDirectory);

            TerrainModernRenderer terrain = AcquireAndPublish(
                scope,
                "terrain renderer",
                () => _factory.CreateBackendNeutralTerrain(
                    _dependencies.GpuDevice,
                    _dependencies.GpuFrameSource,
                    worldPassScope,
                    terrainAtlas,
                    _dependencies.ResourceRetirement),
                _publication.PublishTerrain,
                WorldRenderCompositionPoint.TerrainPublished);

            WorldTerrainBuildContext terrainBuild =
                _factory.CreateTerrainBuildContext(
                    _dependencies.InitialCenterLandblockId,
                    region.HeightTable,
                    terrainAtlas);
            _publication.PublishTerrainBuildState(
                terrainBuild.HeightTable,
                terrainBuild.Blending,
                terrainBuild.SurfaceCache);
            Fault(WorldRenderCompositionPoint.TerrainBuildStatePublished);

            WbMeshAdapter meshAdapter = AcquireAndPublish(
                scope,
                "WB mesh adapter",
                () => _factory.CreateMeshAdapter(
                    _dependencies.GpuDevice,
                    content.Dats,
                    content.PreparedAssets,
                    _dependencies.ResourceRetirement,
                    residency.Budgets),
                _publication.PublishWbMeshAdapter,
                WorldRenderCompositionPoint.MeshAdapterPublished);
            TextureCache textureCache = AcquireAndPublish(
                scope,
                "texture cache",
                () => _factory.CreateTextureCache(
                    _dependencies.GpuDevice,
                    content.Dats,
                    _dependencies.ResourceRetirement,
                    _dependencies.DiagnosticsDirectory,
                    residency.Budgets),
                _publication.PublishTextureCache,
                WorldRenderCompositionPoint.TextureCachePublished);
            _factory.RegisterResidencySources(
                residency,
                meshAdapter,
                textureCache,
                content.PreparedAssets,
                content.AnimationLoader,
                content.Audio?.SoundCache);

            scope.Complete();
            _dependencies.Log(
                "[N.4+N.5] WB foundation + modern path active — " +
                "routing all content through ObjectMeshManager.");
            return new WorldRenderResult(
                terrainBuild,
                new WorldRenderFoundation(
                    shadersDirectory,
                    terrainAtlas,
                    sceneLighting,
                    debugLines,
                    debugFont,
                    textRenderer,
                    terrain,
                    meshAdapter,
                    textureCache,
                    residency));
        }
        catch (Exception failure)
        {
            scope.RollbackAndThrow(failure);
            throw new System.Diagnostics.UnreachableException();
        }
    }

    private (BitmapFont? Font, TextRenderer? Text) ComposeOptionalHudResources(
        CompositionAcquisitionScope scope,
        string shadersDirectory)
    {
        byte[]? fontBytes = _factory.TryLoadDebugFont();
        if (fontBytes is null)
        {
            _dependencies.Log("world-hud font: no system monospace font found");
            Fault(WorldRenderCompositionPoint.HudResourcesCompleted);
            return (null, null);
        }

        var fontLease = scope.Acquire(
            "world HUD font",
            () => _factory.CreateDebugFont(_dependencies.GpuDevice, fontBytes),
            _factory.Release);
        BitmapFont font = fontLease.Resource;
        Fault(WorldRenderCompositionPoint.DebugFontCreated);
        var textLease = scope.Acquire(
            "world HUD text renderer",
            () => _factory.CreateTextRenderer(
                _dependencies.GpuDevice,
                _dependencies.GpuFrameSource,
                shadersDirectory),
            _factory.Release);
        TextRenderer text = textLease.Resource;
        Fault(WorldRenderCompositionPoint.TextRendererCreated);

        _publication.PublishHudResources(font, text);
        fontLease.Transfer();
        textLease.Transfer();
        Fault(WorldRenderCompositionPoint.HudResourcesPublished);
        _dependencies.Log(
            $"world-hud font: loaded {fontBytes.Length / 1024}KB, " +
            $"atlas {font.AtlasWidth}x{font.AtlasHeight}, " +
            $"lineHeight={font.LineHeight:F1}px (reserved for D.6 HUD)");
        Fault(WorldRenderCompositionPoint.HudResourcesCompleted);
        return (font, text);
    }

    private T AcquireAndPublish<T>(
        CompositionAcquisitionScope scope,
        string name,
        Func<T> factory,
        Action<T> publish,
        WorldRenderCompositionPoint point)
        where T : class, IDisposable
    {
        T value = scope.Acquire(name, factory, _factory.Release).Publish(publish);
        Fault(point);
        return value;
    }

    private void Fault(WorldRenderCompositionPoint point) =>
        _faultInjection?.Invoke(point);
}
