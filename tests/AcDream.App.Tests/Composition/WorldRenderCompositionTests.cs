using System.Collections.Concurrent;
using System.Runtime.CompilerServices;
using AcDream.App.Composition;
using AcDream.App.Rendering;
using AcDream.App.Rendering.Gpu;
using AcDream.App.Rendering.Wb;
using AcDream.App.Rendering.Residency;
using AcDream.App.World;
using AcDream.App.Tests.Architecture;
using AcDream.App.Tests.Rendering.Gpu;
using AcDream.Content;
using AcDream.Core.Physics;
using AcDream.Core.Terrain;
using AcDream.UI.Abstractions.Settings;
using DatReaderWriter.DBObjs;
using Silk.NET.Input;

namespace AcDream.App.Tests.Composition;

public sealed class WorldRenderCompositionTests
{
    [Fact]
    public void SuccessPublishesExactModernFoundationInFrozenOrder()
    {
        ResidencyBudgetOptions budgets =
            ResidencyBudgetOptions.Default with
            {
                ObjectMeshGpuBytes = 321,
                CompositePhysicalBytes = 654,
            };
        var fixture = new Fixture(budgets: budgets);

        WorldRenderResult result = fixture.Compose();

        Assert.Equal(Enum.GetValues<WorldRenderCompositionPoint>(), fixture.Points);
        Assert.Same(fixture.Publication.Terrain, result.Foundation.Terrain);
        Assert.Same(fixture.Publication.MeshAdapter, result.Foundation.MeshAdapter);
        Assert.Same(fixture.Publication.TextureCache, result.Foundation.TextureCache);
        Assert.Same(fixture.Lifetime.Atlas, result.Foundation.TerrainAtlas);
        Assert.Equal(QualitySettings.From(QualityPreset.High).AnisotropicLevel,
            fixture.Factory.AnisotropicLevel);
        Assert.Same(budgets, result.Foundation.Residency.Budgets);
        Assert.Same(budgets, fixture.Factory.MeshBudgets);
        Assert.Same(budgets, fixture.Factory.TextureBudgets);
        Assert.Same(result.Foundation.Residency, fixture.Factory.RegisteredResidency);
        Assert.Equal(1, fixture.Lifetime.AcquireCalls);
        Assert.Empty(fixture.Factory.Releases);
    }

    [Fact]
    public void MissingDebugFontSkipsOnlyTheOptionalHudPrefix()
    {
        var fixture = new Fixture(hasFont: false);

        WorldRenderResult result = fixture.Compose();

        Assert.Null(result.Foundation.DebugFont);
        Assert.Null(result.Foundation.TextRenderer);
        Assert.DoesNotContain(WorldRenderCompositionPoint.DebugFontCreated, fixture.Points);
        Assert.DoesNotContain(WorldRenderCompositionPoint.TextRendererCreated, fixture.Points);
        Assert.DoesNotContain(WorldRenderCompositionPoint.HudResourcesPublished, fixture.Points);
        Assert.Contains(WorldRenderCompositionPoint.HudResourcesCompleted, fixture.Points);
    }

    [Theory]
    [MemberData(nameof(FailurePoints))]
    public void FaultAfterEachBoundaryStopsTheExactSuffix(int pointValue)
    {
        var point = (WorldRenderCompositionPoint)pointValue;
        var fixture = new Fixture(failurePoint: point);

        Assert.Throws<InvalidOperationException>(fixture.Compose);

        Assert.Equal(
            Enum.GetValues<WorldRenderCompositionPoint>()
                .TakeWhile(candidate => candidate <= point),
            fixture.Points);
    }

    public static TheoryData<int> FailurePoints()
    {
        var data = new TheoryData<int>();
        foreach (WorldRenderCompositionPoint point in
                 Enum.GetValues<WorldRenderCompositionPoint>())
        {
            data.Add((int)point);
        }
        return data;
    }

    [Theory]
    [InlineData("scene lighting", "scene lighting")]
    [InlineData("debug lines", "debug lines")]
    [InlineData("HUD", "text renderer|debug font")]
    [InlineData("terrain", "terrain")]
    [InlineData("WB mesh adapter", "WB mesh adapter")]
    [InlineData("texture cache", "texture cache")]
    public void FailedPublicationRollsBackOnlyItsUnpublishedResourcePrefix(
        string publication,
        string expectedReleaseOrder)
    {
        var fixture = new Fixture(publicationFailure: publication);

        Assert.Throws<InvalidOperationException>(fixture.Compose);

        Assert.Equal(
            expectedReleaseOrder.Split('|'),
            fixture.Factory.Releases);
    }

    [Fact]
    public void PartialHudConstructionRollsBackFontWhenTextCreationFails()
    {
        var fixture = new Fixture(
            failurePoint: WorldRenderCompositionPoint.DebugFontCreated);

        Assert.Throws<InvalidOperationException>(fixture.Compose);

        Assert.Equal(["debug font"], fixture.Factory.Releases);
    }

    [Fact]
    public void GameWindowUsesPhaseAndNoLongerBuildsWorldFoundationInline()
    {
        IReadOnlyList<CompiledCall> calls =
            CompiledCallGraph.ReadDeclared(typeof(GameWindow));

        Assert.Single(
            calls,
            call => call.Target.DeclaringType
                    == typeof(WorldRenderCompositionPhase)
                && call.Target.IsConstructor);
        Assert.DoesNotContain(
            calls,
            call => call.Target.IsConstructor
                && call.Target.DeclaringType is { } type
                && (type == typeof(TerrainModernRenderer)
                    || type == typeof(WbMeshAdapter)
                    || type == typeof(TextureCache)));
    }

    private sealed class Fixture
    {
        private readonly WorldRenderCompositionPoint? _failurePoint;
        private readonly ResidencyBudgetOptions _budgets;
        private readonly IGpuDevice _gpuDevice = new RecordingGpuDevice();

        public Fixture(
            bool hasFont = true,
            WorldRenderCompositionPoint? failurePoint = null,
            string? publicationFailure = null,
            ResidencyBudgetOptions? budgets = null)
        {
            _failurePoint = failurePoint;
            _budgets = budgets ?? ResidencyBudgetOptions.Default;
            Factory = new Factory(hasFont);
            Publication = new Publication(publicationFailure);
            Lifetime = new RenderLifetime(Factory.Atlas);
            var content = (ContentEffectsAudioResult)
                RuntimeHelpers.GetUninitializedObject(typeof(ContentEffectsAudioResult));
            Content = content;
        }

        public Factory Factory { get; }
        public Publication Publication { get; }
        public RenderLifetime Lifetime { get; }
        public List<WorldRenderCompositionPoint> Points { get; } = [];
        public ContentEffectsAudioResult Content { get; }

        public WorldRenderResult Compose() =>
            new WorldRenderCompositionPhase(
                new WorldRenderDependencies(
                    new WorldEnvironmentController(),
                    Lifetime,
                    ImmediateGpuResourceRetirementQueue.Instance,
                    _budgets,
                    0xA9B4FFFFu,
                    Path.Combine(Path.GetTempPath(), "acdream-tests"),
                    _ => { },
                    _gpuDevice,
                    new GpuDeviceFrameLifetime(_gpuDevice)),
                Publication,
                Factory,
                point =>
                {
                    Points.Add(point);
                    if (point == _failurePoint)
                        throw new InvalidOperationException($"fault at {point}");
                }).Compose(
                    new GameWindowPlatformResult<GameWindowGraphics, IInputContext>(TestGameWindowGraphics.Instance, null!),
                    Content,
                    new SettingsDevToolsResult(
                        QualitySettings.From(QualityPreset.High)));
    }

    private sealed class RenderLifetime(TerrainAtlas atlas)
        : IGameRenderResourceLifetime
    {
        public TerrainAtlas Atlas { get; } = atlas;
        public int AcquireCalls { get; private set; }

        public TerrainAtlas AcquireTerrainAtlas(Func<TerrainAtlas> factory)
        {
            AcquireCalls++;
            return Atlas;
        }
    }

    private sealed class Factory(bool hasFont) : IWorldRenderCompositionFactory
    {
        private readonly Dictionary<IDisposable, string> _names =
            new(ReferenceEqualityComparer.Instance);

        public TerrainAtlas Atlas { get; } = Stub<TerrainAtlas>();
        public int AnisotropicLevel { get; private set; }
        public List<string> Releases { get; } = [];
        public ResidencyBudgetOptions? MeshBudgets { get; private set; }
        public ResidencyBudgetOptions? TextureBudgets { get; private set; }
        public ResidencyManager? RegisteredResidency { get; private set; }

        public WorldRegionData LoadRegion(IDatReaderWriter dats) =>
            new(Stub<Region>(), new float[256]);

        public void InitializeEnvironment(
            WorldEnvironmentController environment,
            Region region,
            IDatReaderWriter dats) { }

        public TerrainAtlas AcquireBackendNeutralTerrainAtlas(
            IGameRenderResourceLifetime lifetime,
            IGpuDevice device,
            IDatReaderWriter dats) =>
            lifetime.AcquireTerrainAtlas(() => Atlas);

        public void ExerciseBackendNeutralWorldTextures(
            IGpuDevice device,
            Action<string> log) =>
            WorldTextureExerciseCount++;

        public int WorldTextureExerciseCount { get; private set; }

        public void SetTerrainAnisotropic(TerrainAtlas atlas, int level) =>
            AnisotropicLevel = level;

        public SceneLightingUboBinding CreateBackendNeutralSceneLighting(
            ICurrentGpuFrameSource frameSource,
            IWorldPassScope scope) =>
            Resource<SceneLightingUboBinding>("scene lighting");

        public DebugLineRenderer CreateDebugLines(
            IGpuDevice device, ICurrentGpuFrameSource frameSource, string shadersDirectory) =>
            Resource<DebugLineRenderer>("debug lines");

        public byte[]? TryLoadDebugFont() => hasFont ? [1] : null;

        public BitmapFont CreateDebugFont(IGpuDevice device, byte[] bytes) =>
            Resource<BitmapFont>("debug font");

        public TextRenderer CreateTextRenderer(
            IGpuDevice device, ICurrentGpuFrameSource frameSource, string shadersDirectory) =>
            Resource<TextRenderer>("text renderer");

        public TerrainModernRenderer CreateBackendNeutralTerrain(
            IGpuDevice gpuDevice,
            ICurrentGpuFrameSource frameSource,
            IWorldPassScope scope,
            TerrainAtlas atlas,
            IGpuResourceRetirementQueue retirement) =>
            Resource<TerrainModernRenderer>("terrain");

        public WorldTerrainBuildContext CreateTerrainBuildContext(
            uint initialCenterLandblockId,
            float[] heightTable,
            TerrainAtlas? atlas) =>
            new(
                initialCenterLandblockId,
                0xA9,
                0xB4,
                heightTable,
                Stub<TerrainBlendingContext>(),
                new ConcurrentDictionary<uint, SurfaceInfo>());

        public WbMeshAdapter CreateMeshAdapter(
            IGpuDevice device,
            IDatReaderWriter dats,
            IPreparedAssetSource preparedAssets,
            IGpuResourceRetirementQueue retirement,
            ResidencyBudgetOptions budgets)
        {
            MeshBudgets = budgets;
            return Resource<WbMeshAdapter>("WB mesh adapter");
        }

        public TextureCache CreateTextureCache(
            IGpuDevice device,
            IDatReaderWriter dats,
            IGpuResourceRetirementQueue retirement,
            string diagnosticsDirectory,
            ResidencyBudgetOptions budgets)
        {
            TextureBudgets = budgets;
            return Resource<TextureCache>("texture cache");
        }

        public void RegisterResidencySources(
            ResidencyManager manager,
            WbMeshAdapter? meshes,
            TextureCache textures,
            IPreparedAssetSource preparedAssets,
            IAnimationLoader animations,
            AcDream.Core.Audio.DatSoundCache? audio)
        {
            RegisteredResidency = manager;
        }

        public void Release(IDisposable resource)
        {
            Releases.Add(_names[resource]);
        }

        private T Resource<T>(string name)
            where T : class, IDisposable
        {
            T value = Stub<T>();
            _names.Add(value, name);
            return value;
        }
    }

    private sealed class Publication(string? failure) : IGameWindowWorldRenderPublication
    {
        public TerrainModernRenderer? Terrain { get; private set; }
        public WbMeshAdapter? MeshAdapter { get; private set; }
        public TextureCache? TextureCache { get; private set; }

        public void PublishSceneLighting(SceneLightingUboBinding value) =>
            Fail("scene lighting");
        public void PublishDebugLines(DebugLineRenderer value) =>
            Fail("debug lines");
        public void PublishHudResources(BitmapFont font, TextRenderer text) =>
            Fail("HUD");

        public void PublishTerrain(TerrainModernRenderer value)
        {
            Fail("terrain");
            Terrain = value;
        }

        public void PublishTerrainBuildState(
            float[] heightTable,
            TerrainBlendingContext blending,
            ConcurrentDictionary<uint, SurfaceInfo> surfaceCache) =>
            Fail("terrain build state");

        public void PublishWbMeshAdapter(WbMeshAdapter value)
        {
            Fail("WB mesh adapter");
            MeshAdapter = value;
        }

        public void PublishTextureCache(TextureCache value)
        {
            Fail("texture cache");
            TextureCache = value;
        }

        private void Fail(string point)
        {
            if (string.Equals(failure, point, StringComparison.Ordinal))
                throw new InvalidOperationException($"publication failed at {point}");
        }
    }

    private static T Stub<T>() where T : class =>
        (T)RuntimeHelpers.GetUninitializedObject(typeof(T));

}
