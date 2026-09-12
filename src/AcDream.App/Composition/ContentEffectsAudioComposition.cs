using AcDream.App.Audio;
using AcDream.App.Physics;
using AcDream.App.Rendering;
using AcDream.App.Rendering.Residency;
using AcDream.App.Rendering.Vfx;
using AcDream.App.Spells;
using AcDream.Content;
using AcDream.Content.Vfx;
using AcDream.Core.Audio;
using AcDream.Core.Lighting;
using AcDream.Core.Physics;
using AcDream.Core.Rendering;
using AcDream.Core.Spells;
using AcDream.Core.Vfx;
using AcDream.Core.CharGen;
using AcDream.Runtime;
using AcDream.Runtime.Gameplay;
using AcDream.Runtime.Physics;
using AcDream.Runtime.Session;
using DatReaderWriter;
using Silk.NET.Input;

namespace AcDream.App.Composition;

internal sealed record ContentAudioGraph(
    DatSoundCache SoundCache,
    OpenAlAudioEngine Engine,
    DictionaryEntitySoundTable EntitySoundTables,
    AudioHookSink? HookSink,
    UiSoundController? UiSounds,
    AmbientSoundController? Ambient);

internal sealed record ContentEffectsAudioResult(
    IDatReaderWriter Dats,
    IPreparedAssetSource PreparedAssets,
    MagicCatalog MagicCatalog,
    IAnimationLoader AnimationLoader,
    LiveEntityCollisionBuilder CollisionBuilder,
    LiveCollisionAssetPublisher CollisionAssets,
    EmitterDescRegistry EmitterRegistry,
    ParticleSystem ParticleSystem,
    ParticleHookSink ParticleSink,
    AnimationHookFrameQueue AnimationHookFrames,
    RetailPhysicsScriptLoader PhysicsScriptLoader,
    PhysicsScriptRunner ScriptRunner,
    LightingHookSink LightingSink,
    TranslucencyHookSink TranslucencySink,
    AnimationHookRegistrationSet HookRegistrations,
    ContentAudioGraph? Audio);

internal sealed record ContentEffectsAudioDependencies(
    string DatDirectory,
    string PreparedAssetPath,
    string? PreparedAssetOverlayPath,
    uint? PreparedAssetBaseRecipeVersion,
    uint? PreparedAssetEffectiveRecipeVersion,
    ResidencyBudgetOptions ResidencyBudgets,
    PhysicsDataCache PhysicsDataCache,
    bool DumpMotionEnabled,
    GameRuntime Runtime,
    AnimationHookRouter HookRouter,
    EntityEffectPoseRegistry EffectPoses,
    DeferredEntityEffectAdvanceSource EntityEffectAdvance,
    LightManager Lighting,
    TranslucencyFadeManager TranslucencyFades,
    bool NoAudio,
    Action<string> Log,
    Action<string> Error)
{
    /// <summary>How the mixer allocates its voices, from the client settings.</summary>
    public AudioMixerOptions MixerOptions { get; init; } = AudioMixerOptions.Default;

    public RuntimeCharacterState Character => Runtime.CharacterOwner;

    public LiveSessionController Session => Runtime.Session;
}

internal interface IGameWindowContentEffectsAudioPublication
{
    void PublishDatCollection(IDatReaderWriter value);
    void PublishPreparedAssetSource(IPreparedAssetSource value);
    void PublishMagicCatalog(MagicCatalog value);
    void PublishAnimationLoader(IAnimationLoader value);
    void PublishLiveEntityCollisionBuilder(LiveEntityCollisionBuilder value);
    void PublishEmitterRegistry(EmitterDescRegistry value);
    void PublishParticleSystem(ParticleSystem value);
    void PublishParticleSink(ParticleHookSink value);
    void PublishAnimationHookFrames(AnimationHookFrameQueue value);
    void PublishPhysicsScriptLoader(RetailPhysicsScriptLoader value);
    void PublishPhysicsScriptRunner(PhysicsScriptRunner value);
    void PublishLightingSink(LightingHookSink value);
    void PublishTranslucencySink(TranslucencyHookSink value);
    void PublishHookRegistrations(AnimationHookRegistrationSet value);
    void PublishAudio(ContentAudioGraph value);
}

internal interface IContentEffectsAudioCompositionFactory
{
    IDatReaderWriter OpenDatCollection(string datDirectory);
    IPreparedAssetSource OpenPreparedAssetSource(
        string path,
        string? overlayPath,
        uint? baseRecipeVersion,
        uint? effectiveRecipeVersion,
        IDatReaderWriter dats,
        Action<string> diagnostic);
    MagicCatalog LoadMagicCatalog(IDatReaderWriter dats);
    void InstallSpellMetadata(
        RuntimeCharacterState character,
        MagicCatalog catalog);
    int GetSpellCount(MagicCatalog catalog);
    ChargenOptions LoadChargenOptions(IDatReaderWriter dats);
    void InstallChargenOptions(LiveSessionController session, ChargenOptions options);
    IAnimationLoader CreateAnimationLoader(
        IDatReaderWriter dats,
        long maximumEstimatedBytes,
        int maximumEntries);
    LiveEntityCollisionBuilder CreateCollisionBuilder(
        PhysicsDataCache physicsData,
        IDatReaderWriter dats,
        IAnimationLoader animationLoader,
        bool dumpMotionEnabled);
    LiveCollisionAssetPublisher CreateLiveCollisionAssetPublisher(
        PhysicsDataCache physicsData,
        IPreparedAssetSource preparedAssets);
    EmitterDescRegistry CreateEmitterRegistry(IDatReaderWriter dats);
    ParticleSystem CreateParticleSystem(EmitterDescRegistry emitters);
    ParticleHookSink CreateParticleSink(
        ParticleSystem particles,
        EntityEffectPoseRegistry poses);
    AnimationHookFrameQueue CreateAnimationHookFrames(
        AnimationHookRouter router,
        EntityEffectPoseRegistry poses);
    RetailPhysicsScriptLoader CreatePhysicsScriptLoader(IDatReaderWriter dats);
    PhysicsScriptRunner CreatePhysicsScriptRunner(
        RetailPhysicsScriptLoader loader,
        AnimationHookRouter router,
        IEntityEffectAdvanceSource advanceSource);
    LightingHookSink CreateLightingSink(
        LightManager lighting,
        EntityEffectPoseRegistry poses);
    TranslucencyHookSink CreateTranslucencySink(TranslucencyFadeManager fades);
    AnimationHookRegistrationSet CreateHookRegistrations(AnimationHookRouter router);
    DatSoundCache CreateSoundCache(
        IDatReaderWriter dats,
        long maximumDecodedBytes);
    OpenAlAudioEngine CreateAudioEngine(AudioMixerOptions mixer);
    DictionaryEntitySoundTable CreateEntitySoundTables();
    AudioHookSink CreateAudioSink(
        OpenAlAudioEngine engine,
        DatSoundCache cache,
        DictionaryEntitySoundTable entitySoundTables);
    UiSoundController CreateUiSounds(
        OpenAlAudioEngine engine,
        DatSoundCache cache,
        IDatReaderWriter dats);
    AmbientSoundController CreateAmbient(
        OpenAlAudioEngine engine,
        DatSoundCache cache);
}

internal sealed class RetailContentEffectsAudioCompositionFactory
    : IContentEffectsAudioCompositionFactory
{
    public IDatReaderWriter OpenDatCollection(string datDirectory) =>
        RuntimeDatCollectionFactory.OpenReadOnly(datDirectory);

    public IPreparedAssetSource OpenPreparedAssetSource(
        string path,
        string? overlayPath,
        uint? baseRecipeVersion,
        uint? effectiveRecipeVersion,
        IDatReaderWriter dats,
        Action<string> diagnostic)
    {
        if (string.IsNullOrWhiteSpace(overlayPath))
        {
            return new PakPreparedAssetSource(path, dats, diagnostic);
        }

        if (baseRecipeVersion is not > 0
            || effectiveRecipeVersion is not > 0)
        {
            throw new InvalidDataException(
                "Layered prepared content is missing its recipe identities.");
        }

        if (effectiveRecipeVersion
            != AcDream.Content.Pak.PakFormat.CurrentBakeToolVersion)
        {
            throw new InvalidDataException(
                $"Prepared content recipe {effectiveRecipeVersion} does not "
                + $"match client recipe "
                + $"{AcDream.Content.Pak.PakFormat.CurrentBakeToolVersion}.");
        }

        PakPreparedAssetSource? baseSource = null;
        PakPreparedAssetSource? overlaySource = null;
        try
        {
            baseSource = new PakPreparedAssetSource(
                path,
                PreparedAssetCatalogIdentity.From(dats, baseRecipeVersion.Value),
                diagnostic);
            overlaySource = new PakPreparedAssetSource(
                overlayPath,
                PreparedAssetCatalogIdentity.From(dats, effectiveRecipeVersion.Value),
                diagnostic);
            return new LayeredPreparedAssetSource(baseSource, overlaySource);
        }
        catch
        {
            overlaySource?.Dispose();
            baseSource?.Dispose();
            throw;
        }
    }

    public MagicCatalog LoadMagicCatalog(IDatReaderWriter dats) =>
        MagicCatalog.Load(dats);

    public void InstallSpellMetadata(
        RuntimeCharacterState character,
        MagicCatalog catalog) =>
        character.InstallSpellMetadata(catalog.SpellTable);

    public int GetSpellCount(MagicCatalog catalog) => catalog.SpellTable.Count;

    public ChargenOptions LoadChargenOptions(IDatReaderWriter dats) =>
        AcDream.Content.CharGen.ChargenTableReader.Load(dats);

    public void InstallChargenOptions(LiveSessionController session, ChargenOptions options) =>
        session.CharacterCreationState.InstallOptions(options);

    public IAnimationLoader CreateAnimationLoader(
        IDatReaderWriter dats,
        long maximumEstimatedBytes,
        int maximumEntries) =>
        new RetailAnimationLoader(
            dats,
            maximumEstimatedBytes,
            maximumEntries);

    public LiveEntityCollisionBuilder CreateCollisionBuilder(
        PhysicsDataCache physicsData,
        IDatReaderWriter dats,
        IAnimationLoader animationLoader,
        bool dumpMotionEnabled) =>
        new(
            physicsData,
            new LiveEntityDefaultPoseResolver(
                id => dats.Get<DatReaderWriter.DBObjs.MotionTable>(id),
                animationLoader,
                dumpMotionEnabled));

    public LiveCollisionAssetPublisher CreateLiveCollisionAssetPublisher(
        PhysicsDataCache physicsData,
        IPreparedAssetSource preparedAssets) =>
        new(
            physicsData,
            preparedAssets as IPreparedCollisionSource
                ?? throw new NotSupportedException(
                    "Production prepared assets must expose collision data."));

    public EmitterDescRegistry CreateEmitterRegistry(IDatReaderWriter dats) =>
        new(dats);

    public ParticleSystem CreateParticleSystem(EmitterDescRegistry emitters) =>
        new(emitters);

    public ParticleHookSink CreateParticleSink(
        ParticleSystem particles,
        EntityEffectPoseRegistry poses) =>
        new(particles, poses);

    public AnimationHookFrameQueue CreateAnimationHookFrames(
        AnimationHookRouter router,
        EntityEffectPoseRegistry poses) =>
        new(router, poses);

    public RetailPhysicsScriptLoader CreatePhysicsScriptLoader(IDatReaderWriter dats) =>
        new(dats);

    public PhysicsScriptRunner CreatePhysicsScriptRunner(
        RetailPhysicsScriptLoader loader,
        AnimationHookRouter router,
        IEntityEffectAdvanceSource advanceSource) =>
        new(
            loader.LoadPhysicsScript,
            router,
            canAdvanceOwner: advanceSource.CanAdvanceOwner);

    public LightingHookSink CreateLightingSink(
        LightManager lighting,
        EntityEffectPoseRegistry poses) =>
        new(lighting, poses);

    public TranslucencyHookSink CreateTranslucencySink(
        TranslucencyFadeManager fades) =>
        new(fades);

    public AnimationHookRegistrationSet CreateHookRegistrations(
        AnimationHookRouter router) =>
        new(router);

    public DatSoundCache CreateSoundCache(
        IDatReaderWriter dats,
        long maximumDecodedBytes) =>
        new(dats, maximumDecodedBytes);

    public OpenAlAudioEngine CreateAudioEngine(AudioMixerOptions mixer) =>
        new(mixer);

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
        new(engine, cache, UiSoundTableResolver.Resolve(dats));

    public AmbientSoundController CreateAmbient(
        OpenAlAudioEngine engine,
        DatSoundCache cache) =>
        new(engine, cache);
}

internal enum ContentEffectsAudioCompositionPoint
{
    DatCollectionPublished,
    PreparedAssetSourcePublished,
    MagicCatalogPublished,
    SpellMetadataInstalled,
    ChargenOptionsInstalled,
    AnimationLoaderPublished,
    CollisionBuilderPublished,
    EmitterRegistryPublished,
    ParticleSystemPublished,
    ParticleSinkPublished,
    AnimationHookFramesPublished,
    HookRegistrationsPublished,
    ParticleHookRegistered,
    PhysicsScriptLoaderPublished,
    PhysicsScriptRunnerPublished,
    LightingSinkPublished,
    LightingHookRegistered,
    TranslucencySinkPublished,
    TranslucencyHookRegistered,
    SoundCacheCreated,
    AudioEngineCreated,
    EntitySoundTablesCreated,
    AudioSinkCreated,
    UiSoundsCreated,
    AmbientCreated,
    AudioPublished,
    AudioHookRegistered,
}

internal sealed class ContentEffectsAudioCompositionPhase :
    IContentEffectsAudioCompositionPhase<
        GameWindowPlatformResult<GameWindowGraphics, IInputContext>,
        HostInputCameraResult,
        ContentEffectsAudioResult>
{
    private readonly ContentEffectsAudioDependencies _dependencies;
    private readonly IGameWindowContentEffectsAudioPublication _publication;
    private readonly IContentEffectsAudioCompositionFactory _factory;
    private readonly Action<ContentEffectsAudioCompositionPoint>? _faultInjection;

    public ContentEffectsAudioCompositionPhase(
        ContentEffectsAudioDependencies dependencies,
        IGameWindowContentEffectsAudioPublication publication,
        IContentEffectsAudioCompositionFactory? factory = null,
        Action<ContentEffectsAudioCompositionPoint>? faultInjection = null)
    {
        _dependencies = dependencies
            ?? throw new ArgumentNullException(nameof(dependencies));
        _publication = publication
            ?? throw new ArgumentNullException(nameof(publication));
        _factory = factory ?? new RetailContentEffectsAudioCompositionFactory();
        _faultInjection = faultInjection;
    }

    public ContentEffectsAudioResult Compose(
        GameWindowPlatformResult<GameWindowGraphics, IInputContext> platform,
        HostInputCameraResult host)
    {
        ArgumentNullException.ThrowIfNull(platform);
        ArgumentNullException.ThrowIfNull(host);

        var mainScope = new CompositionAcquisitionScope();
        try
        {
            IDatReaderWriter dats = mainScope.Acquire(
                "DAT collection",
                () => _factory.OpenDatCollection(_dependencies.DatDirectory),
                static value => value.Dispose()).Publish(
                    _publication.PublishDatCollection);
            Fault(ContentEffectsAudioCompositionPoint.DatCollectionPublished);

            IPreparedAssetSource preparedAssets = mainScope.Acquire(
                "prepared asset source",
                () => _factory.OpenPreparedAssetSource(
                    _dependencies.PreparedAssetPath,
                    _dependencies.PreparedAssetOverlayPath,
                    _dependencies.PreparedAssetBaseRecipeVersion,
                    _dependencies.PreparedAssetEffectiveRecipeVersion,
                    dats,
                    _dependencies.Error),
                static value => value.Dispose()).Publish(
                    _publication.PublishPreparedAssetSource);
            Fault(ContentEffectsAudioCompositionPoint.PreparedAssetSourcePublished);
            _dependencies.Log(
                $"prepared assets: opened '{_dependencies.PreparedAssetPath}'");

            MagicCatalog magic = _factory.LoadMagicCatalog(dats);
            _publication.PublishMagicCatalog(magic);
            Fault(ContentEffectsAudioCompositionPoint.MagicCatalogPublished);
            _factory.InstallSpellMetadata(_dependencies.Character, magic);
            _dependencies.Log(
                $"spells: loaded {_factory.GetSpellCount(magic)} entries from portal.dat");
            Fault(ContentEffectsAudioCompositionPoint.SpellMetadataInstalled);

            ChargenOptions chargen = _factory.LoadChargenOptions(dats);
            _factory.InstallChargenOptions(_dependencies.Session, chargen);
            _dependencies.Log(
                $"chargen: loaded {chargen.HeritagesById.Count} heritage(s) from portal.dat");
            Fault(ContentEffectsAudioCompositionPoint.ChargenOptionsInstalled);

            IAnimationLoader animations = _factory.CreateAnimationLoader(
                dats,
                _dependencies.ResidencyBudgets.AnimationBytes,
                _dependencies.ResidencyBudgets.AnimationEntries);
            _publication.PublishAnimationLoader(animations);
            Fault(ContentEffectsAudioCompositionPoint.AnimationLoaderPublished);

            LiveEntityCollisionBuilder collision = _factory.CreateCollisionBuilder(
                _dependencies.PhysicsDataCache,
                dats,
                animations,
                _dependencies.DumpMotionEnabled);
            _publication.PublishLiveEntityCollisionBuilder(collision);
            Fault(ContentEffectsAudioCompositionPoint.CollisionBuilderPublished);
            LiveCollisionAssetPublisher collisionAssets =
                _factory.CreateLiveCollisionAssetPublisher(
                    _dependencies.PhysicsDataCache,
                    preparedAssets);

            EmitterDescRegistry emitters = _factory.CreateEmitterRegistry(dats);
            _publication.PublishEmitterRegistry(emitters);
            Fault(ContentEffectsAudioCompositionPoint.EmitterRegistryPublished);

            ParticleSystem particles = _factory.CreateParticleSystem(emitters);
            _publication.PublishParticleSystem(particles);
            Fault(ContentEffectsAudioCompositionPoint.ParticleSystemPublished);

            ParticleHookSink particleSink = _factory.CreateParticleSink(
                particles,
                _dependencies.EffectPoses);
            particleSink.DiagnosticSink = message =>
                _dependencies.Error($"vfx: {message}");
            _publication.PublishParticleSink(particleSink);
            Fault(ContentEffectsAudioCompositionPoint.ParticleSinkPublished);

            AnimationHookFrameQueue hookFrames =
                _factory.CreateAnimationHookFrames(
                    _dependencies.HookRouter,
                    _dependencies.EffectPoses);
            _publication.PublishAnimationHookFrames(hookFrames);
            Fault(ContentEffectsAudioCompositionPoint.AnimationHookFramesPublished);

            AnimationHookRegistrationSet registrations = mainScope.Acquire(
                "animation hook registrations",
                () => _factory.CreateHookRegistrations(_dependencies.HookRouter),
                static value => value.Dispose()).Publish(
                    _publication.PublishHookRegistrations);
            Fault(ContentEffectsAudioCompositionPoint.HookRegistrationsPublished);
            registrations.Register(particleSink);
            Fault(ContentEffectsAudioCompositionPoint.ParticleHookRegistered);

            RetailPhysicsScriptLoader scriptLoader =
                _factory.CreatePhysicsScriptLoader(dats);
            _publication.PublishPhysicsScriptLoader(scriptLoader);
            Fault(ContentEffectsAudioCompositionPoint.PhysicsScriptLoaderPublished);

            PhysicsScriptRunner scriptRunner = _factory.CreatePhysicsScriptRunner(
                scriptLoader,
                _dependencies.HookRouter,
                _dependencies.EntityEffectAdvance);
            _publication.PublishPhysicsScriptRunner(scriptRunner);
            Fault(ContentEffectsAudioCompositionPoint.PhysicsScriptRunnerPublished);

            LightingHookSink lightingSink = _factory.CreateLightingSink(
                _dependencies.Lighting,
                _dependencies.EffectPoses);
            _publication.PublishLightingSink(lightingSink);
            Fault(ContentEffectsAudioCompositionPoint.LightingSinkPublished);
            registrations.Register(lightingSink);
            Fault(ContentEffectsAudioCompositionPoint.LightingHookRegistered);

            TranslucencyHookSink translucencySink =
                _factory.CreateTranslucencySink(_dependencies.TranslucencyFades);
            _publication.PublishTranslucencySink(translucencySink);
            Fault(ContentEffectsAudioCompositionPoint.TranslucencySinkPublished);
            registrations.Register(translucencySink);
            Fault(ContentEffectsAudioCompositionPoint.TranslucencyHookRegistered);

            ContentAudioGraph? audio = _dependencies.NoAudio
                ? null
                : ComposeOptionalAudio(dats, registrations);

            mainScope.Complete();
            return new ContentEffectsAudioResult(
                dats,
                preparedAssets,
                magic,
                animations,
                collision,
                collisionAssets,
                emitters,
                particles,
                particleSink,
                hookFrames,
                scriptLoader,
                scriptRunner,
                lightingSink,
                translucencySink,
                registrations,
                audio);
        }
        catch (Exception failure)
        {
            mainScope.RollbackAndThrow(failure);
            throw new System.Diagnostics.UnreachableException();
        }
    }

    private ContentAudioGraph? ComposeOptionalAudio(
        IDatReaderWriter dats,
        AnimationHookRegistrationSet registrations)
    {
        var audioScope = new CompositionAcquisitionScope();
        ContentAudioGraph graph;
        CompositionAcquisitionScope.CompositionAcquisitionLease<OpenAlAudioEngine>
            engineLease;
        try
        {
            DatSoundCache cache = _factory.CreateSoundCache(
                dats,
                _dependencies.ResidencyBudgets.AudioBytes);
            Fault(ContentEffectsAudioCompositionPoint.SoundCacheCreated);
            engineLease = audioScope.Acquire(
                "OpenAL audio engine",
                () => _factory.CreateAudioEngine(_dependencies.MixerOptions),
                static value => value.Dispose());
            OpenAlAudioEngine engine = engineLease.Resource;
            Fault(ContentEffectsAudioCompositionPoint.AudioEngineCreated);
            DictionaryEntitySoundTable soundTables =
                _factory.CreateEntitySoundTables();
            Fault(ContentEffectsAudioCompositionPoint.EntitySoundTablesCreated);
            AudioHookSink? sink = null;
            UiSoundController? uiSounds = null;
            AmbientSoundController? ambient = null;
            if (engine.IsAvailable)
            {
                sink = _factory.CreateAudioSink(engine, cache, soundTables);
                Fault(ContentEffectsAudioCompositionPoint.AudioSinkCreated);
                uiSounds = _factory.CreateUiSounds(engine, cache, dats);
                _dependencies.Log(
                    uiSounds.TableDid == 0
                        ? "audio: UI sound bank unresolved (no enum chain in dats) "
                          + "- interface cues silent"
                        : $"audio: UI sound bank = 0x{uiSounds.TableDid:X8}");
                Fault(ContentEffectsAudioCompositionPoint.UiSoundsCreated);
                ambient = _factory.CreateAmbient(engine, cache);
                Fault(ContentEffectsAudioCompositionPoint.AmbientCreated);
            }

            graph = new ContentAudioGraph(
                cache, engine, soundTables, sink, uiSounds, ambient);
        }
        catch (Exception failure)
        {
            try
            {
                audioScope.RollbackAndThrow(failure);
            }
            catch (Exception rolledBack) when (!HasIncompleteCleanup(rolledBack))
            {
                _dependencies.Log(
                    $"audio: init failed: {rolledBack.Message} â€” audio disabled");
                return null;
            }

            throw new System.Diagnostics.UnreachableException();
        }

        try
        {
            _publication.PublishAudio(graph);
            engineLease.Transfer();
            audioScope.Complete();
        }
        catch (Exception failure)
        {
            audioScope.RollbackAndThrow(failure);
            throw new System.Diagnostics.UnreachableException();
        }

        Fault(ContentEffectsAudioCompositionPoint.AudioPublished);
        if (graph.HookSink is { } audioSink)
        {
            registrations.Register(audioSink);
            Fault(ContentEffectsAudioCompositionPoint.AudioHookRegistered);
            _dependencies.Log("audio: OpenAL engine ready (16 voices, 3D positional)");
        }
        else
        {
            _dependencies.Log(
                "audio: OpenAL unavailable (driver missing / headless) â€” audio disabled");
        }

        return graph;
    }

    private static bool HasIncompleteCleanup(Exception failure)
    {
        if (failure is IRetryableResourceCleanup cleanup
            && !cleanup.IsCleanupComplete)
        {
            return true;
        }
        if (failure is AggregateException aggregate)
            return aggregate.InnerExceptions.Any(HasIncompleteCleanup);
        return failure.InnerException is { } inner && HasIncompleteCleanup(inner);
    }

    private void Fault(ContentEffectsAudioCompositionPoint point) =>
        _faultInjection?.Invoke(point);
}
