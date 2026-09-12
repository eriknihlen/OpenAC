using System.Reflection;
using System.Runtime.CompilerServices;
using AcDream.App.Audio;
using AcDream.App.Composition;
using AcDream.App.Physics;
using AcDream.App.Rendering;
using AcDream.App.Rendering.Residency;
using AcDream.App.Rendering.Vfx;
using AcDream.App.Rendering.Wb;
using AcDream.App.Spells;
using AcDream.App.Tests.Architecture;
using AcDream.Content;
using AcDream.Content.Vfx;
using AcDream.Core.Audio;
using AcDream.Core.CharGen;
using AcDream.Core.Lighting;
using AcDream.Core.Meshing;
using AcDream.Core.Physics;
using AcDream.Core.Rendering;
using AcDream.Core.Spells;
using AcDream.Core.Vfx;
using AcDream.Runtime.Gameplay;
using AcDream.Runtime.Physics;
using AcDream.Runtime.Session;
using DatReaderWriter.DBObjs;
using DatReaderWriter.Lib.IO;
using Silk.NET.Input;
using Silk.NET.OpenAL;

namespace AcDream.App.Tests.Composition;

public sealed class ContentEffectsAudioCompositionTests
{
    [Fact]
    public void ProductionPhasePublishesExactOwnersAndRegistersHooksInRetailOrder()
    {
        using var fixture = new Fixture();

        ContentEffectsAudioResult result = fixture.Phase().Compose(
            fixture.Platform,
            fixture.Host);

        Assert.Equal(
            Enum.GetValues<ContentEffectsAudioCompositionPoint>(),
            fixture.Points);
        Assert.Same(fixture.Publication.Dats, result.Dats);
        Assert.Same(fixture.Publication.PreparedAssets, result.PreparedAssets);
        Assert.Equal("test.pak", fixture.Factory.PreparedAssetPath);
        Assert.Same(result.Dats, fixture.Factory.PreparedAssetDats);
        Assert.Same(fixture.Publication.Magic, result.MagicCatalog);
        Assert.Same(fixture.Publication.Animations, result.AnimationLoader);
        Assert.Same(fixture.Publication.Particles, result.ParticleSystem);
        Assert.Same(fixture.Publication.ScriptRunner, result.ScriptRunner);
        Assert.Same(fixture.Publication.Audio, result.Audio);
        Assert.Equal(
            [
                result.ParticleSink,
                result.LightingSink,
                result.TranslucencySink,
                result.Audio!.HookSink!,
            ],
            fixture.Router.Sinks);
        Assert.Equal(4, result.HookRegistrations.ActiveCount);
    }

    // OpenAC #42 follow-up: the mixer settings come from the client settings
    // through this dependency and end up in the engine's own pool. A mixer
    // built from the defaults instead would be 32 voices whatever the player
    // chose.
    [Fact]
    public void TheAudioEngineIsBuiltWithTheMixerSettingsItWasGiven()
    {
        var mixer = new AudioMixerOptions
        {
            VoiceCount = 48,
            MaxVoicesPerWave = 6,
        };
        using var fixture = new Fixture(mixer: mixer);

        ContentEffectsAudioResult result = fixture.Phase().Compose(
            fixture.Platform,
            fixture.Host);

        Assert.Same(mixer, fixture.Factory.LastMixerOptions);
        Assert.Equal(48, result.Audio!.Engine.MixerOptions.EffectiveVoiceCount);
        Assert.Equal(6, result.Audio!.Engine.MixerOptions.EffectiveMaxVoicesPerWave);
    }

    [Fact]
    public void NoAudioAcquiresNothingFromTheOptionalFactoryPrefix()
    {
        using var fixture = new Fixture(noAudio: true);

        ContentEffectsAudioResult result = fixture.Phase().Compose(
            fixture.Platform,
            fixture.Host);

        Assert.Null(result.Audio);
        Assert.Null(fixture.Publication.Audio);
        Assert.Equal(0, fixture.Factory.AudioFactoryCalls);
        Assert.DoesNotContain(
            ContentEffectsAudioCompositionPoint.SoundCacheCreated,
            fixture.Points);
        Assert.Equal(3, fixture.Router.Sinks.Count);
    }

    [Theory]
    [InlineData((int)ContentEffectsAudioCompositionPoint.SoundCacheCreated)]
    [InlineData((int)ContentEffectsAudioCompositionPoint.AudioEngineCreated)]
    [InlineData((int)ContentEffectsAudioCompositionPoint.EntitySoundTablesCreated)]
    [InlineData((int)ContentEffectsAudioCompositionPoint.AudioSinkCreated)]
    public void OptionalAudioPrefixFailureDisablesAudioOnlyAfterRollbackConverges(
        int failurePointValue)
    {
        var failurePoint =
            (ContentEffectsAudioCompositionPoint)failurePointValue;
        using var fixture = new Fixture(failurePoint: failurePoint);

        ContentEffectsAudioResult result = fixture.Phase().Compose(
            fixture.Platform,
            fixture.Host);

        Assert.Null(result.Audio);
        Assert.Null(fixture.Publication.Audio);
        Assert.Equal(3, fixture.Router.Sinks.Count);
        if (fixture.Factory.LastAudioEngine is { } engine)
            Assert.True(engine.IsDisposalComplete);
        Assert.Contains(fixture.Logs, message =>
            message.Contains("audio: init failed", StringComparison.Ordinal));
    }

    [Fact]
    public void FailureAfterAtomicAudioPublicationPropagatesToLifetimeOwner()
    {
        using var fixture = new Fixture(
            failurePoint: ContentEffectsAudioCompositionPoint.AudioPublished);

        Assert.Throws<InvalidOperationException>(() => fixture.Phase().Compose(
            fixture.Platform,
            fixture.Host));
        Assert.False(
            fixture.Dependencies.Runtime.CaptureOwnership().IsDisposeRequested);

        Assert.NotNull(fixture.Publication.Audio);
        Assert.False(fixture.Publication.Audio!.Engine.IsDisposalComplete);
        Assert.Equal(3, fixture.Router.Sinks.Count);
    }

    [Theory]
    [MemberData(nameof(RequiredFailurePoints))]
    public void RequiredBoundaryFailureStopsAtTheExactPointAndNeverRunsASuffix(
        int failurePointValue)
    {
        var failurePoint =
            (ContentEffectsAudioCompositionPoint)failurePointValue;
        using var fixture = new Fixture(failurePoint: failurePoint);

        Assert.Throws<InvalidOperationException>(() => fixture.Phase().Compose(
            fixture.Platform,
            fixture.Host));
        Assert.False(
            fixture.Dependencies.Runtime.CaptureOwnership().IsDisposeRequested);

        Assert.Equal(
            Enum.GetValues<ContentEffectsAudioCompositionPoint>()
                .TakeWhile(point => point <= failurePoint),
            fixture.Points);
    }

    public static TheoryData<int> RequiredFailurePoints()
    {
        var data = new TheoryData<int>();
        foreach (ContentEffectsAudioCompositionPoint point in
                 Enum.GetValues<ContentEffectsAudioCompositionPoint>())
        {
            if (point is >= ContentEffectsAudioCompositionPoint.SoundCacheCreated
                and <= ContentEffectsAudioCompositionPoint.AmbientCreated)
            {
                continue;
            }
            data.Add((int)point);
        }
        return data;
    }

    [Fact]
    public void GameWindowUsesTheProductionPhaseAndNoLongerConstructsItsBodyInline()
    {
        IReadOnlyList<CompiledCall> calls =
            CompiledCallGraph.ReadDeclared(typeof(GameWindow));

        Assert.Single(
            calls,
            call => call.Target.DeclaringType
                    == typeof(ContentEffectsAudioCompositionPhase)
                && call.Target.IsConstructor);
        Assert.DoesNotContain(
            calls,
            call => call.Target.DeclaringType
                    == typeof(RuntimeDatCollectionFactory)
                && call.Target.Name == nameof(RuntimeDatCollectionFactory.OpenReadOnly));
        Assert.DoesNotContain(
            calls,
            call => call.Target.DeclaringType == typeof(AnimationHookRouter)
                && call.Target.Name == nameof(AnimationHookRouter.Register));
        Assert.DoesNotContain(
            calls,
            call => call.Target.DeclaringType == typeof(OpenAlAudioEngine)
                && call.Target.IsConstructor);
    }

    [Fact]
    public void ProductionRendererConsumesOnlyThePublishedPreparedAssetSource()
    {
        MethodInfo openPrepared = typeof(RetailContentEffectsAudioCompositionFactory)
            .GetMethod(nameof(
                RetailContentEffectsAudioCompositionFactory.OpenPreparedAssetSource))!;
        Assert.Contains(
            CompiledCallGraph.Read(openPrepared),
            call => call.Target.DeclaringType == typeof(PakPreparedAssetSource)
                && call.Target.IsConstructor);

        MethodInfo compose = typeof(WorldRenderCompositionPhase)
            .GetMethod(nameof(WorldRenderCompositionPhase.Compose))!;
        IReadOnlyList<CompiledCall> worldCalls = CompiledCallGraph.Read(compose);
        Assert.Contains(
            worldCalls,
            call => call.Target.DeclaringType == typeof(ContentEffectsAudioResult)
                && call.Target.Name == "get_PreparedAssets");

        FieldInfo preparedAssets = Assert.Single(
            typeof(ObjectMeshManager).GetFields(
                BindingFlags.Instance | BindingFlags.NonPublic),
            field => field.FieldType == typeof(IPreparedAssetSource));
        Assert.Equal("_preparedAssets", preparedAssets.Name);
        Assert.DoesNotContain(
            typeof(ObjectMeshManager).GetConstructors(),
            constructor => constructor.GetParameters().Any(parameter =>
                parameter.ParameterType == typeof(IDatReaderWriter)));
        Assert.Contains(
            CompiledCallGraph.ReadDeclared(typeof(ObjectMeshManager)),
            call => call.Target.DeclaringType == typeof(IPreparedAssetSource)
                && call.Target.Name == nameof(IPreparedAssetSource.Read));
        Assert.DoesNotContain(
            CompiledCallGraph.ReadDeclared(typeof(WbMeshAdapter)),
            call => call.Target.DeclaringType == typeof(GfxObjMesh)
                && call.Target.Name == nameof(GfxObjMesh.Build));

        MethodInfo shutdown = typeof(GameWindowShutdownManifest).GetMethod(
            nameof(GameWindowShutdownManifest.Create))!;
        List<string> labels =
            CompiledCallGraph.ReadStringLiterals(shutdown).ToList();
        int meshStage = labels.IndexOf("mesh adapter");
        int preparedRelease = labels.IndexOf("prepared asset source");
        int datRelease = labels.IndexOf("DAT collection");
        Assert.True(meshStage >= 0);
        Assert.True(preparedRelease > meshStage);
        Assert.True(datRelease > preparedRelease);
    }

    private sealed class Fixture : IDisposable
    {
        private readonly ContentEffectsAudioCompositionPoint? _failurePoint;

        public Fixture(
            bool noAudio = false,
            ContentEffectsAudioCompositionPoint? failurePoint = null,
            AudioMixerOptions? mixer = null)
        {
            _failurePoint = failurePoint;
            Router = new AnimationHookRouter();
            Poses = new EntityEffectPoseRegistry();
            Factory = new Factory();
            Publication = new Publication();
            Platform = new GameWindowPlatformResult<GameWindowGraphics, IInputContext>(TestGameWindowGraphics.Instance, null!);
            Host = (HostInputCameraResult)RuntimeHelpers.GetUninitializedObject(
                typeof(HostInputCameraResult));
            Dependencies = new ContentEffectsAudioDependencies(
                "test-dat",
                "test.pak",
                null,
                null,
                null,
                ResidencyBudgetOptions.Default,
                new PhysicsDataCache(),
                false,
                GameRuntimeTestFactory.Create(),
                Router,
                Poses,
                new DeferredEntityEffectAdvanceSource(),
                new LightManager(),
                new TranslucencyFadeManager(),
                noAudio,
                Logs.Add,
                Logs.Add)
            {
                MixerOptions = mixer ?? AudioMixerOptions.Default,
            };
        }

        public List<ContentEffectsAudioCompositionPoint> Points { get; } = [];
        public List<string> Logs { get; } = [];
        public AnimationHookRouter Router { get; }
        public EntityEffectPoseRegistry Poses { get; }
        public Factory Factory { get; }
        public Publication Publication { get; }
        public ContentEffectsAudioDependencies Dependencies { get; }
        public GameWindowPlatformResult<GameWindowGraphics, IInputContext> Platform { get; }
        public HostInputCameraResult Host { get; }

        public ContentEffectsAudioCompositionPhase Phase() => new(
            Dependencies,
            Publication,
            Factory,
            point =>
            {
                Points.Add(point);
                if (_failurePoint == point)
                    throw new InvalidOperationException($"fault at {point}");
            });

        public void Dispose()
        {
            Publication.Dispose();
            Dependencies.Runtime.Dispose();
        }
    }

    private sealed class Publication :
        IGameWindowContentEffectsAudioPublication,
        IDisposable
    {
        public IDatReaderWriter? Dats { get; private set; }
        public IPreparedAssetSource? PreparedAssets { get; private set; }
        public MagicCatalog? Magic { get; private set; }
        public IAnimationLoader? Animations { get; private set; }
        public LiveEntityCollisionBuilder? Collision { get; private set; }
        public EmitterDescRegistry? Emitters { get; private set; }
        public ParticleSystem? Particles { get; private set; }
        public ParticleHookSink? ParticleSink { get; private set; }
        public AnimationHookFrameQueue? HookFrames { get; private set; }
        public RetailPhysicsScriptLoader? ScriptLoader { get; private set; }
        public PhysicsScriptRunner? ScriptRunner { get; private set; }
        public LightingHookSink? LightingSink { get; private set; }
        public TranslucencyHookSink? TranslucencySink { get; private set; }
        public AnimationHookRegistrationSet? Registrations { get; private set; }
        public ContentAudioGraph? Audio { get; private set; }

        public void PublishDatCollection(IDatReaderWriter value) => Dats = Once(Dats, value);
        public void PublishPreparedAssetSource(IPreparedAssetSource value) =>
            PreparedAssets = Once(PreparedAssets, value);
        public void PublishMagicCatalog(MagicCatalog value) => Magic = Once(Magic, value);
        public void PublishAnimationLoader(IAnimationLoader value) =>
            Animations = Once(Animations, value);
        public void PublishLiveEntityCollisionBuilder(LiveEntityCollisionBuilder value) =>
            Collision = Once(Collision, value);
        public void PublishEmitterRegistry(EmitterDescRegistry value) =>
            Emitters = Once(Emitters, value);
        public void PublishParticleSystem(ParticleSystem value) =>
            Particles = Once(Particles, value);
        public void PublishParticleSink(ParticleHookSink value) =>
            ParticleSink = Once(ParticleSink, value);
        public void PublishAnimationHookFrames(AnimationHookFrameQueue value) =>
            HookFrames = Once(HookFrames, value);
        public void PublishPhysicsScriptLoader(RetailPhysicsScriptLoader value) =>
            ScriptLoader = Once(ScriptLoader, value);
        public void PublishPhysicsScriptRunner(PhysicsScriptRunner value) =>
            ScriptRunner = Once(ScriptRunner, value);
        public void PublishLightingSink(LightingHookSink value) =>
            LightingSink = Once(LightingSink, value);
        public void PublishTranslucencySink(TranslucencyHookSink value) =>
            TranslucencySink = Once(TranslucencySink, value);
        public void PublishHookRegistrations(AnimationHookRegistrationSet value) =>
            Registrations = Once(Registrations, value);
        public void PublishAudio(ContentAudioGraph value) => Audio = Once(Audio, value);

        public void Dispose()
        {
            Registrations?.Dispose();
            Registrations = null;
            Audio?.Engine.Dispose();
            Audio = null;
            PreparedAssets?.Dispose();
            PreparedAssets = null;
            Dats?.Dispose();
            Dats = null;
        }

        private static T Once<T>(T? current, T value) where T : class
        {
            if (current is not null)
                throw new InvalidOperationException("duplicate publication");
            return value;
        }
    }

    private sealed class Factory : IContentEffectsAudioCompositionFactory
    {
        private readonly IDatReaderWriter _dats =
            DispatchProxy.Create<IDatReaderWriter, NullProxy>();
        private readonly MagicCatalog _magic =
            (MagicCatalog)RuntimeHelpers.GetUninitializedObject(typeof(MagicCatalog));
        private readonly IAnimationLoader _animations = new NullAnimationLoader();
        private readonly IPreparedAssetSource _preparedAssets =
            new NullPreparedAssetSource();

        public int AudioFactoryCalls { get; private set; }
        public OpenAlAudioEngine? LastAudioEngine { get; private set; }
        public string? PreparedAssetPath { get; private set; }
        public IDatReaderWriter? PreparedAssetDats { get; private set; }

        public IDatReaderWriter OpenDatCollection(string datDirectory) => _dats;
        public IPreparedAssetSource OpenPreparedAssetSource(
            string path,
            string? overlayPath,
            uint? baseRecipeVersion,
            uint? effectiveRecipeVersion,
            IDatReaderWriter dats,
            Action<string> diagnostic)
        {
            PreparedAssetPath = path;
            PreparedAssetDats = dats;
            return _preparedAssets;
        }
        public MagicCatalog LoadMagicCatalog(IDatReaderWriter dats) => _magic;
        public void InstallSpellMetadata(
            RuntimeCharacterState character,
            MagicCatalog catalog) { }
        public int GetSpellCount(MagicCatalog catalog) => 0;
        public ChargenOptions LoadChargenOptions(IDatReaderWriter dats) =>
            ChargenOptions.Empty;
        public void InstallChargenOptions(
            LiveSessionController session,
            ChargenOptions options) { }
        public IAnimationLoader CreateAnimationLoader(
            IDatReaderWriter dats,
            long maximumEstimatedBytes,
            int maximumEntries)
        {
            Assert.Equal(
                ResidencyBudgetOptions.Default.AnimationBytes,
                maximumEstimatedBytes);
            Assert.Equal(
                ResidencyBudgetOptions.Default.AnimationEntries,
                maximumEntries);
            return _animations;
        }

        public LiveEntityCollisionBuilder CreateCollisionBuilder(
            PhysicsDataCache physicsData,
            IDatReaderWriter dats,
            IAnimationLoader animationLoader,
            bool dumpMotionEnabled) =>
            (LiveEntityCollisionBuilder)RuntimeHelpers.GetUninitializedObject(
                typeof(LiveEntityCollisionBuilder));

        public LiveCollisionAssetPublisher CreateLiveCollisionAssetPublisher(
            PhysicsDataCache physicsData,
            IPreparedAssetSource preparedAssets) =>
            (LiveCollisionAssetPublisher)RuntimeHelpers.GetUninitializedObject(
                typeof(LiveCollisionAssetPublisher));

        public EmitterDescRegistry CreateEmitterRegistry(IDatReaderWriter dats) => new();
        public ParticleSystem CreateParticleSystem(EmitterDescRegistry emitters) => new(emitters);
        public ParticleHookSink CreateParticleSink(
            ParticleSystem particles,
            EntityEffectPoseRegistry poses) => new(particles, poses);
        public AnimationHookFrameQueue CreateAnimationHookFrames(
            AnimationHookRouter router,
            EntityEffectPoseRegistry poses) => new(router, poses);
        public AnimationHookRegistrationSet CreateHookRegistrations(
            AnimationHookRouter router) => new(router);
        public RetailPhysicsScriptLoader CreatePhysicsScriptLoader(IDatReaderWriter dats) =>
            (RetailPhysicsScriptLoader)RuntimeHelpers.GetUninitializedObject(
                typeof(RetailPhysicsScriptLoader));
        public PhysicsScriptRunner CreatePhysicsScriptRunner(
            RetailPhysicsScriptLoader loader,
            AnimationHookRouter router,
            IEntityEffectAdvanceSource advanceSource) =>
            new(_ => null, router, canAdvanceOwner: advanceSource.CanAdvanceOwner);
        public LightingHookSink CreateLightingSink(
            LightManager lighting,
            EntityEffectPoseRegistry poses) => new(lighting, poses);
        public TranslucencyHookSink CreateTranslucencySink(
            TranslucencyFadeManager fades) => new(fades);
        public DatSoundCache CreateSoundCache(
            IDatReaderWriter dats,
            long maximumDecodedBytes)
        {
            Assert.Equal(
                ResidencyBudgetOptions.Default.AudioBytes,
                maximumDecodedBytes);
            return new DatSoundCache(dats, maximumDecodedBytes);
        }

        public OpenAlAudioEngine CreateAudioEngine(AudioMixerOptions mixer)
        {
            AudioFactoryCalls++;
            LastMixerOptions = mixer;
            LastAudioEngine = new OpenAlAudioEngine(
                new AudioApiFactory(new FakeAudioApi()),
                mixer);
            return LastAudioEngine;
        }

        internal AudioMixerOptions? LastMixerOptions { get; private set; }

        public DictionaryEntitySoundTable CreateEntitySoundTables() => new();
        public AudioHookSink CreateAudioSink(
            OpenAlAudioEngine engine,
            DatSoundCache cache,
            DictionaryEntitySoundTable entitySoundTables) =>
            new(engine, cache, entitySoundTables);

        public UiSoundController CreateUiSounds(
            OpenAlAudioEngine engine,
            DatSoundCache cache,
            IDatReaderWriter dats) =>
            new(engine, cache, tableDid: 0u);

        public AmbientSoundController CreateAmbient(
            OpenAlAudioEngine engine,
            DatSoundCache cache) =>
            new(engine, cache);
    }

    public class NullProxy : DispatchProxy
    {
        protected override object? Invoke(MethodInfo? targetMethod, object?[]? args)
        {
            if (targetMethod?.ReturnType == typeof(void))
                return null;
            Type returnType = targetMethod?.ReturnType ?? typeof(void);
            return returnType.IsValueType
                ? Activator.CreateInstance(returnType)
                : null;
        }
    }

    private sealed class NullAnimationLoader : IAnimationLoader
    {
        public Animation? LoadAnimation(uint id) => null;
    }

    private sealed class NullPreparedAssetSource : IPreparedAssetSource
    {
        public PreparedAssetSourceStats Stats => default;
        public CacheStats DecodedTextureCacheStats => default;

        public PreparedAssetPresence Probe(
            AcDream.Content.Pak.PakAssetType type,
            uint sourceFileId) =>
            PreparedAssetPresence.Missing;

        public PreparedAssetReadResult Read(
            in PreparedAssetRequest request,
            CancellationToken cancellationToken = default) =>
            PreparedAssetReadResult.Missing;

        public void Dispose()
        {
        }
    }

    private sealed class AudioApiFactory(IOpenAlResourceApi api)
        : IOpenAlResourceApiFactory
    {
        public IOpenAlResourceApi Create() => api;
    }

    private sealed class FakeAudioApi : IOpenAlResourceApi
    {
        private uint _nextSource = 1;
        public AL? AudioApi => null;
        public ALContext? ContextApi => null;
        public nint OpenDevice() => 1;
        public bool SupportsOutputLimiterControl(nint device) => false;
        public nint CreateContext(nint device, int[]? attributes) => 2;
        public int? ReadOutputLimiterState(nint device) => OpenAlContextAttributes.Off;
        public bool MakeContextCurrent(nint context) => true;
        public uint GenerateSource() => _nextSource++;
        public void Configure3DSource(uint source) { }
        public void DisableAlDistanceAttenuation() { }
        public void StopSource(uint source) { }
        public void DeleteSource(uint source) { }
        public void DeleteBuffer(uint buffer) { }
        public void DestroyContext(nint context) { }
        public void CloseDevice(nint device) { }
    }

}
