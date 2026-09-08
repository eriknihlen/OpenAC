using AcDream.Core.Plugins;
using AcDream.App.Composition;
using AcDream.App.Input;
using AcDream.App.Physics;
using AcDream.App.Rendering.Gpu;
using AcDream.App.Rendering.Scene;
using AcDream.App.Rendering.Wb;
using AcDream.App.Settings;
using AcDream.App.Platform;
using AcDream.App.World;
using AcDream.Content;
using AcDream.Platform;
using AcDream.Runtime;
using AcDream.Runtime.Entities;
using AcDream.Runtime.Gameplay;
using AcDream.Runtime.Session;
using DatReaderWriter;
using Silk.NET.Input;
using Silk.NET.Maths;
using Silk.NET.Windowing;
using AcDream.UI.Abstractions.Input;

namespace AcDream.App.Rendering;

public sealed class GameWindow :
    IDisposable,
    IGameWindowPlatformPublication<GameWindowGraphics, IInputContext>,
    IGameWindowHostInputCameraPublication,
    IGameWindowContentEffectsAudioPublication,
    IGameWindowWorldRenderPublication,
    IGameWindowInteractionRetainedUiPublication,
    IGameWindowLivePresentationPublication,
    IGameWindowSessionPlayerPublication,
    IGameWindowFrameRootPublication
{
    private static double ClientTimerNow() =>
        System.Diagnostics.Stopwatch.GetTimestamp()
        / (double)System.Diagnostics.Stopwatch.Frequency;

    internal static WindowOptions CreateStartupWindowOptions(
        bool exactAutomationFramebuffer,
        string persistedResolution,
        bool useVSync)
    {
        WindowOptions defaults = WindowOptions.DefaultVulkan;
        Vector2D<int> size = new(1280, 720);
        WindowBorder border = defaults.WindowBorder;
        if (exactAutomationFramebuffer)
        {
            if (!SilkRuntimeDisplayWindowTarget.TryParseResolution(
                    persistedResolution,
                    out int width,
                    out int height))
            {
                throw new InvalidOperationException(
                    "Exact automation framebuffer requires a valid persisted resolution.");
            }
            size = new Vector2D<int>(width, height);
            border = WindowBorder.Hidden;
        }

        return defaults with
        {
            Size = size,
            Title = "acdream — Vulkan",
            VSync = useVSync,
            WindowBorder = border,
            IsVisible = !exactAutomationFramebuffer,
        };
    }

    private readonly AcDream.App.RuntimeOptions _options;
    private readonly SessionStatusWriter _statusWriter;
    private readonly AnimationPresentationDiagnostics _animationDiagnostics;
    private readonly string _datDir;
    private readonly WorldGameState _worldGameState;
    private readonly WorldEvents _worldEvents;
    private readonly HostQuiescenceGate _hostQuiescence = new();
    private IWindow? _window;
    private bool _renderLoopArmed;
    private bool _nativeCloseRequested;
    private bool _nativeRunReturned;
    private SilkWindowCallbackBinding? _windowCallbacks;
    private GameWindowGraphics? _graphics;
    private AcDream.App.Rendering.Gpu.Vk.VulkanGraphicsContext? _vulkanGraphics;
    private FramePacingPolicy _startupPacing;
    private AcDream.UI.Abstractions.Settings.QualitySettings _startupQuality =
        AcDream.UI.Abstractions.Settings.QualitySettings.From(
            AcDream.UI.Abstractions.Settings.QualityPreset.High);
    private IInputContext? _input;
    private TerrainModernRenderer? _terrain;
    private CameraController? _cameraController;
    private IDatReaderWriter? _dats;
    private IPreparedAssetSource? _preparedAssets;
    private readonly AcDream.App.Input.PointerPositionState _pointerPosition = new();
    private AcDream.App.Input.CameraPointerInputController? _cameraPointerInput;
    private TextureCache? _textureCache;
    private AcDream.App.Rendering.Wb.WbMeshAdapter? _wbMeshAdapter;
    private AcDream.App.Rendering.Wb.EntitySpawnAdapter? _wbEntitySpawnAdapter;
    private AcDream.App.Rendering.Vfx.EntityScriptActivator? _entityScriptActivator;
    private RetailStaticAnimatingObjectScheduler? _staticAnimationScheduler;
    private RenderSceneShadowRuntime? _renderSceneShadow;
    private AcDream.App.Rendering.Wb.WbDrawDispatcher? _wbDrawDispatcher;
    private AcDream.App.Rendering.Selection.RetailSelectionScene? _retailSelectionScene;
    private AcDream.App.Interaction.WorldSelectionQuery? _worldSelectionQuery;
    private AcDream.App.Interaction.SelectionInteractionController? _selectionInteractions;
    private DebugLineRenderer? _debugLines;
    private readonly AcDream.App.Rendering.WorldSceneDebugState
        _worldSceneDebugState = new();

    private TextRenderer? _textRenderer;
    private BitmapFont? _debugFont;
    private readonly bool _frameDiag = string.Equals(
        System.Environment.GetEnvironmentVariable("ACDREAM_WB_DIAG"),
        "1",
        System.StringComparison.Ordinal);

    private readonly AcDream.App.Diagnostics.FrameProfiler _frameProfiler = new();
    private readonly AcDream.App.Rendering.IRenderFrameDiagnosticLog
        _renderDiagnosticLog =
            new AcDream.App.Rendering.ConsoleRenderFrameDiagnosticLog();
    private readonly AcDream.App.Rendering.DebugVmRenderFactsPublisher
        _debugVmRenderFacts = new();
    private AcDream.App.Rendering.RenderFrameDiagnosticsController?
        _renderFrameDiagnostics;
    private LivePresentationRuntimeBindings? _livePresentationBindings;
    private SessionPlayerRuntimeBindings? _sessionPlayerBindings;
    private AcDream.App.Diagnostics.FrameScreenshotController? _frameScreenshots;
    private FrameRootRuntimeBindings? _frameRootBindings;
    private IDisposable? _frameGraphPublication;
    private AcDream.App.Rendering.GpuFrameFlightController? _gpuFrameFlights;
    private IGpuDevice? _gpuDevice;
    private GpuDeviceFrameLifetime? _gpuFrameLifetime;
    private readonly AcDream.App.Rendering.GameFrameGraphSlot _frameGraphs = new();
    private readonly AcDream.App.Rendering.GameRenderResourceLifetime
        _renderResourceLifetime = new();
    private readonly AcDream.App.Rendering.ResourceConstructionCleanupLedger
        _constructionCleanup = new();
    private readonly AcDream.App.World.WorldEnvironmentController _worldEnvironment;
    private readonly GameWindowLifetime _lifetime = new();
    private Exception? _runFailure;
    private readonly DisplayFramePacingController _displayFramePacing;
    private readonly RuntimeSettingsController _runtimeSettings;
    private readonly BuildingDegradeController _buildingDegrades;

    private AcDream.App.Streaming.LandblockStreamer? _streamer;
    private AcDream.App.Streaming.GpuWorldState _worldState = new();
    private AcDream.App.Streaming.LandblockPresentationPipeline?
        _landblockPresentationPipeline;
    private AcDream.App.Rendering.EquippedChildRenderController? _equippedChildRenderer;
    private AcDream.App.Streaming.StreamingController? _streamingController;
    private AcDream.App.Streaming.StreamingOriginRecenterCoordinator?
        _streamingOriginRecenter;
    private AcDream.App.Streaming.WorldRevealCoordinator? _worldReveal;
    private AcDream.App.Streaming.DatSpawnClaimHydrationClassifier?
        _spawnClaimHydration;
    private readonly AcDream.App.Streaming.DeferredLocalPlayerTeleportNetworkSink
        _localPlayerTeleportSink = new();
    private AcDream.App.Streaming.LocalPlayerTeleportController?
        _localPlayerTeleport;
    private readonly AcDream.App.Rendering.WorldRenderRangeState _renderRange =
        new(nearRadius: 4, farRadius: 12);
    private AcDream.Core.Physics.PhysicsEngine _physicsEngine =>
        _runtimeEntityObjects.Physics.Engine;

    private AcDream.Core.Physics.PhysicsDataCache _physicsDataCache =>
        _runtimeEntityObjects.Physics.DataCache;

    private AcDream.Core.Physics.ObjectInfoState GetMoverPvpState(uint serverGuid) =>
        AcDream.Core.Physics.EntityCollisionFlagsExt.ResolveMoverPvpState(
            _runtimeEntityObjects.Objects,
            serverGuid);

    private readonly AcDream.App.Physics.RemotePhysicsUpdater _remotePhysicsUpdater;
    private readonly AcDream.App.Physics.RemoteInboundMotionDispatcher
        _remoteInboundMotion;
    private readonly AcDream.App.World.RetailInboundEventDispatcher
        _inboundEntityEvents = new();
    private LiveEntityAnimationScheduler _liveAnimationScheduler = null!;
    private LiveEntityAnimationPresenter _animationPresenter = null!;
    private readonly AcDream.App.Input.MovementTruthDiagnosticController
        _movementTruthDiagnostics;
    private readonly LocalPlayerOutboundController _localPlayerOutbound;
    private readonly AcDream.App.Update.UpdateFrameClock _updateFrameClock;
    private AcDream.App.Physics.ProjectileController? _projectileController;
    private AcDream.App.World.LiveEntityProjectionWithdrawalController?
        _liveEntityProjectionWithdrawal;
    private AcDream.App.World.LiveEntityHydrationController? _liveEntityHydration;
    private AcDream.App.Physics.LiveEntityNetworkUpdateController? _liveEntityNetworkUpdates;
    private AcDream.App.Net.LiveEntitySessionController? _liveEntitySessionEvents;
    private readonly AcDream.App.Physics.DeferredLiveEntityMotionRuntimeBindings
        _liveEntityMotionBindings = new();

    private readonly CellVisibility _cellVisibility = new();

    private readonly object _datLock = new();

    private float[]? _heightTable;
    private AcDream.Core.Terrain.TerrainBlendingContext? _blendCtx;
    private System.Collections.Concurrent.ConcurrentDictionary<uint, AcDream.Core.Terrain.SurfaceInfo>? _surfaceCache;

    private AcDream.App.Rendering.Wb.EnvCellRenderer? _envCellRenderer;
    private AcDream.App.Rendering.Wb.WbFrustum? _envCellFrustum;

    // R1 (render redesign): portal-view draw owners are retained transitively
    // by the frame orchestrator rather than duplicated as GameWindow roots.
    private AcDream.App.Rendering.PortalDepthMaskRenderer? _portalDepthMask;
    private readonly AcDream.App.Rendering.TransferableResourceSlot<
        AcDream.App.Rendering.PortalTunnelPresentation> _portalTunnelFallback = new();

    private ClipFrame? _clipFrame;

    private readonly LiveEntityAnimationRuntimeView<LiveEntityAnimationState> _animatedEntities;
    private readonly AcDream.App.World.LiveEntityRuntimeSlot _liveEntityRuntimeSlot = new();

    private readonly AcDream.App.Rendering.Wb.EntityClassificationCache _classificationCache = new();

    private AcDream.Core.Physics.IAnimationLoader? _animLoader;
    private AcDream.Runtime.Physics.LiveEntityCollisionBuilder? _liveEntityCollisionBuilder;

    private readonly AcDream.Core.Physics.AnimationHookRouter _hookRouter = new();
    private AcDream.App.Composition.AnimationHookRegistrationSet?
        _hookRegistrations;
    private readonly AcDream.App.Rendering.Vfx.DeferredEntityEffectAdvanceSource
        _entityEffectAdvance = new();

    private AcDream.App.Audio.OpenAlAudioEngine? _audioEngine;
    private AcDream.Core.Audio.DatSoundCache? _soundCache;
    private AcDream.App.Audio.DictionaryEntitySoundTable? _entitySoundTables;
    private AcDream.App.Audio.AudioHookSink? _audioSink;

    private AcDream.Core.Vfx.EmitterDescRegistry? _emitterRegistry;
    private AcDream.Core.Vfx.ParticleSystem? _particleSystem;
    private AcDream.Core.Vfx.ParticleHookSink? _particleSink;
    private readonly AcDream.App.Rendering.Vfx.ParticleVisibilityController _particleVisibility = new();
    private readonly AcDream.App.Rendering.Vfx.EntityEffectPoseRegistry _effectPoses = new();
    private AcDream.App.Rendering.Vfx.AnimationHookFrameQueue? _animationHookFrames;
    private AcDream.Core.Vfx.PhysicsScriptRunner? _scriptRunner;
    private AcDream.Content.Vfx.RetailPhysicsScriptLoader? _physicsScriptLoader;
    private AcDream.App.Rendering.Vfx.EntityEffectController? _entityEffects;
    private AcDream.App.Rendering.ParticleRenderer? _particleRenderer;
    private readonly AcDream.App.Rendering.RetailAlphaQueue _retailAlphaQueue;
    private readonly AcDream.App.Physics.RemoteMovementObservationTracker
        _remoteMovementObservations = new();


    private AcDream.App.World.LiveEntityRuntime? _liveEntities;
    private AcDream.App.World.LiveEntityLivenessController? _liveEntityLiveness;

    private readonly AcDream.App.Plugins.AppAutomationSurface? _automation;
    private readonly GameRuntime _runtime;
    private readonly IDisposable _runtimeHostLease;
    private RuntimeCommunicationState _runtimeCommunication =>
        _runtime.CommunicationOwner;
    private RuntimeActionState _runtimeActions => _runtime.ActionOwner;
    public AcDream.Core.Selection.SelectionState Selection =>
        _runtimeActions.Selection;
    internal SessionStatusWriter StatusWriter => _statusWriter;
    public AcDream.Core.Chat.ChatLog Chat => _runtimeCommunication.Chat;
    public AcDream.Core.Chat.TurbineChatState TurbineChat =>
        _runtimeCommunication.TurbineChat;
    public AcDream.Core.Social.FriendsState Friends =>
        _runtimeCommunication.Friends;
    public AcDream.Core.Social.SquelchState Squelch =>
        _runtimeCommunication.Squelch;
    public AcDream.Core.Combat.CombatState Combat =>
        _runtimeActions.Combat;
    private RuntimeEntityObjectLifetime _runtimeEntityObjects =>
        _runtime.EntityObjects;
    private RuntimeInventoryState _runtimeInventory =>
        _runtime.InventoryOwner;
    private RuntimeCharacterState _runtimeCharacter =>
        _runtime.CharacterOwner;
    public AcDream.Core.Items.ItemManaState ItemMana =>
        _runtimeInventory.ItemMana;
    public AcDream.Core.Spells.SpellTable SpellTable => SpellBook.Metadata;
    public AcDream.Core.Spells.Spellbook SpellBook =>
        _runtimeCharacter.Spellbook;
    /// <summary>Persisted hotbar shortcuts from the last PlayerDescription (D.5.1 toolbar source).</summary>
    public IReadOnlyList<AcDream.Core.Items.ShortcutEntry> Shortcuts =>
        _runtimeInventory.Shortcuts.Items;
    public AcDream.Core.Player.LocalPlayerState LocalPlayer =>
        _runtimeCharacter.LocalPlayer;

    private AcDream.UI.Abstractions.Panels.Chat.ChatVM? _retailChatVm;
    private AcDream.App.UI.UiHost? _uiHost;
    private AcDream.App.UI.RetailUiRuntime? _retailUiRuntime;
    private readonly AcDream.App.UI.RetailUiRuntimeLease _retailUiLease = new();
    private InteractionUiLateBindings? _interactionUiLateBindings;
    private readonly DeferredRenderFrameDiagnosticsSource _uiFrameDiagnostics = new();
    private readonly AcDream.App.Rendering.Packs.DeferredRenderPackDiagnosticsSource
        _renderPackDiagnostics = new();
    private readonly AcDream.App.Combat.CombatAttackOperationsSlot
        _combatAttackOperations = new();
    private readonly AcDream.App.Combat.RuntimeCombatTargetOperationsSlot
        _combatTargetOperations = new();
    private readonly AcDream.App.Combat.RuntimeCombatModeOperationsSlot
        _combatModeOperations = new();
    private readonly AcDream.App.Spells.RuntimeSpellCastOperationsSlot
        _spellCastOperations = new();
    private readonly AcDream.App.Combat.CombatFeedbackSlot
        _combatFeedback = new();
    private RuntimeCombatAttackState? _combatAttackController;
    private AcDream.App.UI.ItemInteractionController? _itemInteractionController;
    private AcDream.App.World.ExternalContainerLifecycleController? _externalContainerLifecycle;
    private AcDream.App.Spells.MagicRuntime? _magicRuntime;
    private MagicCatalog? _magicCatalog;
    private readonly AcDream.Core.Items.StackSplitQuantityState _stackSplitQuantity = new();
    private AcDream.App.Rendering.PaperdollViewportRenderer? _paperdollViewportRenderer;
    private AcDream.App.Rendering.PaperdollFramePresenter? _paperdollFramePresenter;
    private AcDream.App.Rendering.CreatureAppraisalViewportRenderer?
        _creatureAppraisalViewportRenderer;
    private AcDream.App.Rendering.CreatureAppraisalFramePresenter?
        _creatureAppraisalFramePresenter;
    private AcDream.App.Rendering.ChargenPreviewRenderer? _chargenPreviewRenderer;
    private AcDream.App.Rendering.ChargenPreviewController? _chargenPreviewController;
    private AcDream.App.Rendering.ChargenPreviewRenderer? _summaryPreviewRenderer;
    private AcDream.App.Rendering.ChargenPreviewController? _summaryPreviewController;
    private readonly AcDream.App.Plugins.BufferedUiRegistry? _uiRegistry;
    private readonly AcDream.App.Plugins.BufferedRenderPackRegistry? _renderPackRegistry;
    private AcDream.App.Plugins.GraphicalPluginSession? _pluginSession;
    private const bool DevToolsEnabled = false;

    public readonly AcDream.Core.World.WorldTimeService WorldTime;
    public readonly AcDream.Core.Lighting.LightManager Lighting = new();

    public readonly AcDream.Core.World.WeatherSystem Weather;
    // Wired into the hook router in OnLoad so SetLightHook fires
    // from the animation pipeline flip the matching LightSource.IsLit.
    private AcDream.Core.Lighting.LightingHookSink? _lightingSink;
    private AcDream.App.Rendering.Vfx.LiveEntityLightController? _liveEntityLights;
    private AcDream.App.World.LiveEntityPresentationController? _liveEntityPresentation;

    private readonly AcDream.Core.Rendering.TranslucencyFadeManager _translucencyFades = new();
    private AcDream.Core.Rendering.TranslucencyHookSink? _translucencySink;

    private AcDream.App.Rendering.SceneLightingUboBinding? _sceneLightingUbo;
    private AcDream.App.Rendering.Sky.SkyRenderer? _skyRenderer;
    private RuntimeLocalPlayerMovementState _playerControllerSlot =>
        _runtime.MovementOwner;
    private PlayerMovementController? _playerController
        => _playerControllerSlot.Controller;
    private readonly AcDream.App.Input.ChaseCameraInputState _chaseCameraInput = new();
    private AcDream.App.Rendering.ChaseCamera? _chaseCamera
        => _chaseCameraInput.Legacy;
    private AcDream.App.Rendering.RetailChaseCamera? _retailChaseCamera
        => _chaseCameraInput.Retail;
    private readonly AcDream.App.Input.LocalPlayerModeState _localPlayerMode = new();
    private bool _playerMode
        => _localPlayerMode.IsPlayerMode;
    private readonly AcDream.App.Input.LocalPlayerIdentityState
        _localPlayerIdentity;
    private uint _playerServerGuid
    {
        get => _localPlayerIdentity.ServerGuid;
        set => _localPlayerIdentity.ServerGuid = value;
    }
    private readonly AcDream.App.Physics.LocalPlayerShadowState _localPlayerShadow = new();

    private readonly AcDream.App.Input.ViewportAspectState _viewportAspect = new();
    private readonly FramebufferResizeController _framebufferResize;
    private AcDream.App.Input.PlayerModeController? _playerModeController;
    private readonly AcDream.App.Interaction.PlayerApproachCompletionState
        _playerApproachCompletions = new();
    private AcDream.App.Input.LocalPlayerAnimationController?
        _localPlayerAnimation;
    private AcDream.App.Physics.LocalPlayerShadowSynchronizer?
        _localPlayerShadowSynchronizer;
    private AcDream.App.UI.Layout.CharacterSheetProvider? _characterSheetProvider;
    private AcDream.App.Input.PlayerModeAutoEntry? _playerModeAutoEntry;

    private AcDream.App.Input.SilkKeyboardSource? _kbSource;
    private AcDream.App.Input.SilkMouseSource? _mouseSource;
    private AcDream.UI.Abstractions.Input.InputDispatcher? _inputDispatcher;
    private readonly AcDream.App.Input.RetainedUiInputCaptureSlot _retainedInputCapture;
    private readonly AcDream.App.Input.CompositeInputCaptureSource _inputCapture;
    private readonly AcDream.App.Input.DispatcherMovementInputSource _movementInput;
    private readonly AcDream.App.Input.DispatcherCameraInputSource _cameraInput = new();
    private AcDream.App.Input.IMouseLookCursor? _mouseLookCursor;
    private AcDream.App.Input.GameplayInputFrameController? _gameplayInputFrame;
    private AcDream.App.Input.GameplayInputActionRouter? _gameplayInputActions;
    private AcDream.App.Input.RetainedUiGameplayBinding? _retainedUiGameplayBinding;
    private readonly AcDream.App.Diagnostics.RuntimeDiagnosticCommandSlot
        _runtimeDiagnosticCommands = new();
    private readonly AcDream.App.Combat.LiveCombatModeCommandSlot
        _liveCombatModeCommands = new();
    private readonly AcDream.UI.Abstractions.Input.KeyBindings _keyBindings;
    private bool _keyBindingsPersisted;
    private readonly GraphicalHostPlatformServices _platformServices;
    private readonly ApplicationPathSet _applicationPaths;

    private static AcDream.UI.Abstractions.Input.KeyBindings LoadStartupKeyBindings(
        string path)
    {
        var bindings = AcDream.App.Input.RetailKeymapProfileStore.LoadActiveOrJson(
            path, out string profileName);
        Console.WriteLine(
            $"keybinds: loaded {bindings.All.Count} bindings; active retail profile "
            + $"'{profileName}', JSON mirror {path}");
        return bindings;
    }

    private LiveSessionHost? _liveSessionHost;
    private AcDream.Core.Net.WorldSession? LiveSession =>
        _liveSessionHost?.CurrentSession;
    private readonly AcDream.App.World.LiveWorldOriginState _liveWorldOrigin = new();
    private int _liveCenterX => _liveWorldOrigin.CenterX;
    private int _liveCenterY => _liveWorldOrigin.CenterY;
    private static readonly IReadOnlyDictionary<uint, AcDream.Core.Net.WorldSession.EntitySpawn> EmptyLiveSpawnMap =
        new Dictionary<uint, AcDream.Core.Net.WorldSession.EntitySpawn>();
    private IReadOnlyDictionary<uint, AcDream.Core.Net.WorldSession.EntitySpawn> LastSpawns =>
        _liveEntities?.Snapshots ?? EmptyLiveSpawnMap;
    private readonly AcDream.App.Input.LocalPlayerPhysicsHostSlot
        _playerHostSlot = new();
    private EntityPhysicsHost? _playerHost
        => _playerHostSlot.Host;


    public GameWindow(
        AcDream.App.RuntimeOptions options,
        WorldGameState worldGameState,
        WorldEvents worldEvents,
        AcDream.App.Plugins.BufferedUiRegistry? uiRegistry = null)
        : this(
            options,
            worldGameState,
            worldEvents,
            uiRegistry,
            GraphicalHostPlatformServices.Resolve())
    {
    }

    internal GameWindow(
        AcDream.App.RuntimeOptions options,
        WorldGameState worldGameState,
        WorldEvents worldEvents,
        AcDream.App.Plugins.BufferedUiRegistry? uiRegistry,
        GraphicalHostPlatformServices platformServices,
        AcDream.App.Plugins.AppAutomationSurface? automation = null,
        AcDream.App.Plugins.BufferedRenderPackRegistry? renderPackRegistry = null)
    {
        _options = options ?? throw new System.ArgumentNullException(nameof(options));
        AcDream.Core.Rendering.RenderingDiagnostics.DumpWalkTranscriptEnabled =
            options.DumpWalkTranscript;
        _automation = automation;
        _statusWriter = new SessionStatusWriter(options.StatusFilePath);
        _platformServices = platformServices
            ?? throw new ArgumentNullException(nameof(platformServices));
        _applicationPaths = _platformServices.Paths;
        _keyBindings = LoadStartupKeyBindings(
            _applicationPaths.KeyBindingsFile);
        _runtime = new GameRuntime(new GameRuntimeDependencies(
            _combatAttackOperations,
            _combatTargetOperations,
            _combatModeOperations,
            _spellCastOperations,
            Log: Console.WriteLine,
            TimeSyncDiagnostic:
                options.DumpSky ? Console.WriteLine : null));
        _runtimeHostLease = _runtime.AcquireHostLease(
            "graphical GameWindow");
        _automation?.Bind(_runtime, _runtime.CharacterOwner, _runtime.ActionOwner.SpellCast);
        _automation?.BindProjectileCollision(_physicsEngine);
        _localPlayerIdentity = new AcDream.App.Input.LocalPlayerIdentityState(
            _runtime.PlayerIdentity);
        _updateFrameClock = new AcDream.App.Update.UpdateFrameClock(
            _runtime.Clock);
        _worldEnvironment = new AcDream.App.World.WorldEnvironmentController(
            _runtime.EnvironmentOwner,
            options.ForcedDayGroupIndex,
            Console.WriteLine,
            options.PinnedWorldDayFraction);
        var alphaScratchBudgets =
            AcDream.App.Rendering.Residency.AlphaScratchBudgetProfile.Create(
                _options.ResidencyBudgets.AlphaScratchBytes);
        _retailAlphaQueue = new AcDream.App.Rendering.RetailAlphaQueue(
            alphaScratchBudgets.QueueBytes);
        WorldTime = _worldEnvironment.WorldTime;
        Weather = _worldEnvironment.Weather;
        Weather.DisableDistanceFogSource = () =>
            _runtime.CharacterOwner.Options.GetOptionBit(
                AcDream.Core.Net.Messages.CharacterOptionId.DisableDistanceFog);
        _runtime.CommunicationOwner.DisplayTimestampsSource = () =>
            _runtime.CharacterOwner.Options.GetOptionBit(
                AcDream.Core.Net.Messages.CharacterOptionId.DisplayTimeStamps);
        _retainedInputCapture = new AcDream.App.Input.RetainedUiInputCaptureSlot();
        _inputCapture = new AcDream.App.Input.CompositeInputCaptureSource(
            new AcDream.App.Input.DevToolsInputCaptureSource(options.DevTools),
            _retainedInputCapture);
        _movementInput = new AcDream.App.Input.DispatcherMovementInputSource(
            _playerControllerSlot,
            _inputCapture);
        _playerControllerSlot.RunAsDefaultMovementSource = () =>
            _runtime.CharacterOwner.Options.GetOptionBit(
                AcDream.Core.Net.Messages.CharacterOptionId.ToggleRun);
        _framebufferResize = new FramebufferResizeController(_viewportAspect);
        _datDir = options.DatDir;
        _worldGameState = worldGameState;
        _worldEvents = worldEvents;
        _displayFramePacing = new DisplayFramePacingController(
            options.UncappedRendering,
            _frameProfiler,
            _platformServices.FramePacingWaiters);
        _runtimeSettings = new RuntimeSettingsController(
            new JsonRuntimeSettingsStorage(
                _applicationPaths.SettingsFile),
            log: Console.WriteLine,
            characterOptionValue: _runtime.CharacterOwner.Options.GetOptionBit);
        _buildingDegrades = new BuildingDegradeController(
            () => _runtimeSettings.DisplayPreview);
        _animationDiagnostics = AnimationPresentationDiagnostics.FromEnvironment();
        _uiRegistry = uiRegistry;
        _renderPackRegistry = renderPackRegistry;
        _animatedEntities = new LiveEntityAnimationRuntimeView<LiveEntityAnimationState>(
            _liveEntityRuntimeSlot);
        _remotePhysicsUpdater = new AcDream.App.Physics.RemotePhysicsUpdater(
            _runtimeEntityObjects.Physics,
            _liveEntityMotionBindings.GetSetupCylinder,
            _liveEntityMotionBindings.GetSetupMoverShape,
            AcDream.App.Physics.RemoteServerControlledVelocityCycle.Apply,
            GetMoverPvpState);
        _remoteInboundMotion = new AcDream.App.Physics.RemoteInboundMotionDispatcher(
            (movement, cellId, update) =>
                _liveEntityMotionBindings.RouteServerMoveTo(
                    movement, cellId, update),
            (host, targetGuid) =>
                _liveEntityMotionBindings.StickToObjectFromWire(host, targetGuid));
        _movementTruthDiagnostics =
            new AcDream.App.Input.MovementTruthDiagnosticController(
                options.DumpMoveTruth,
                _playerControllerSlot,
                _localPlayerIdentity);
        _localPlayerOutbound = new LocalPlayerOutboundController(
            _movementTruthDiagnostics);
    }

    internal void StartPluginHosting(
        AcDream.App.Plugins.GraphicalPluginSession pluginSession)
    {
        ArgumentNullException.ThrowIfNull(pluginSession);
        if (_pluginSession is not null)
        {
            throw new InvalidOperationException(
                "The graphical plugin session is already attached.");
        }

        _pluginSession = pluginSession;
        pluginSession.Start();
    }

    public void Run()
    {
        _platformServices.ConfigureWindowBackend();
        RuntimeSettingsSnapshot startup = _runtimeSettings.Startup;
        if (_options.VulkanCapabilityProbe)
        {
            using var probe = new AcDream.App.Rendering.Gpu.Vk.VulkanBringUpHost(
                _options,
                _platformServices,
                startup.Display.VSync);
            probe.Run();
            return;
        }

        FramePacingPolicy startupPacing =
            _displayFramePacing.InitializeStartup(startup.Display.VSync);
        WindowOptions options = CreateStartupWindowOptions(
            _options.ExactAutomationFramebuffer,
            startup.Display.Resolution,
            startupPacing.UseVSync);
        _startupPacing = startupPacing;
        _startupQuality = startup.Quality;

        _window = Window.Create(options);
        IWindow window = _window;
        _lifetime.PublishNativeWindow(
            window,
            isRenderLoopArmed: () => _renderLoopArmed,
            requestClose: window.Close);
        _displayFramePacing.BindSurface(
            new SilkDisplayFramePacingSurface(_window));
        // The fixed binding preserves main Render before post-render pacing,
        // owns rollback/reverse-detach, and gates every native entry point.
        _windowCallbacks = SilkWindowCallbackBinding.Create(
            _window,
            new WindowCallbackTargets(
                OnLoad,
                OnUpdate,
                OnRender,
                OnClosing,
                OnFocusChanged,
                OnFramebufferResize),
            _displayFramePacing,
            _hostQuiescence);
        _windowCallbacks.Attach();
        try
        {
            _window.Run();
            _nativeRunReturned = true;
            CompleteShutdown(releaseNativeWindow: false);
        }
        catch (Exception failure)
        {
            _constructionCleanup.RetainFrom(failure);
            _runFailure = failure;
            try
            {
                Diagnostics.LocalCrashReportWriter.TryWrite(
                    failure, _applicationPaths.DiagnosticsDirectory,
                    CaptureLocalCrashReportContext, Console.Error.WriteLine);
            }
            catch { /* Diagnostics must never mask the frame-loop failure. */ }
            throw;
        }
    }

    private Diagnostics.LocalCrashReportContext CaptureLocalCrashReportContext()
    {
        var graphics = _vulkanGraphics;
        var capabilities = graphics?.Capabilities;
        uint? width = graphics?.Width;
        uint? height = graphics?.Height;
        var gpu = graphics is null ? null : new Diagnostics.LocalCrashGpu(
            capabilities?.DeviceName, capabilities?.DriverInfo,
            capabilities?.InstanceApiVersion, capabilities?.DeviceApiVersion,
            capabilities?.DeviceApiVersionPacked,
            width is > 0 ? width : null, height is > 0 ? height : null,
            graphics.SampleCount);

        var controller = _playerController;
        Diagnostics.LocalCrashWorld? world = null;
        if (controller is not null)
        {
            var position = controller.CellPosition;
            world = new Diagnostics.LocalCrashWorld(
                position.ObjCellId, position.Frame.Origin.X,
                position.Frame.Origin.Y, position.Frame.Origin.Z, controller.State.ToString());
        }
        return new Diagnostics.LocalCrashReportContext(gpu, world);
    }

    void IGameWindowPlatformPublication<GameWindowGraphics, IInputContext>.PublishGraphics(
        GameWindowGraphics graphics) =>
        PublishCompositionOwner(ref _graphics, graphics, "graphics API");

    void IGameWindowPlatformPublication<GameWindowGraphics, IInputContext>.PublishInput(
        IInputContext input) =>
        PublishCompositionOwner(ref _input, input, "input context");

    void IGameWindowHostInputCameraPublication.PublishGpuFrameFlights(
        GpuFrameFlightController? value)
    {
        // Null on a backend whose RHI device owns its own frame flights.
        if (value is not null)
            PublishCompositionOwner(ref _gpuFrameFlights, value, "GPU frame flights");
    }

    void IGameWindowHostInputCameraPublication.PublishGpuDevice(
        IGpuDevice value) =>
        PublishCompositionOwner(ref _gpuDevice, value, "GPU device (RHI)");

    void IGameWindowHostInputCameraPublication.PublishGpuFrameLifetime(
        GpuDeviceFrameLifetime value) =>
        PublishCompositionOwner(ref _gpuFrameLifetime, value, "GPU frame lifetime");

    void IGameWindowHostInputCameraPublication.PublishKeyboardSource(
        AcDream.App.Input.SilkKeyboardSource value) =>
        PublishCompositionOwner(ref _kbSource, value, "keyboard source");

    void IGameWindowHostInputCameraPublication.PublishMouseSource(
        AcDream.App.Input.SilkMouseSource value) =>
        PublishCompositionOwner(ref _mouseSource, value, "mouse source");

    void IGameWindowHostInputCameraPublication.PublishMouseLookCursor(
        AcDream.App.Input.IMouseLookCursor value) =>
        PublishCompositionOwner(ref _mouseLookCursor, value, "mouse-look cursor");

    void IGameWindowHostInputCameraPublication.PublishInputDispatcher(
        AcDream.UI.Abstractions.Input.InputDispatcher value) =>
        PublishCompositionOwner(ref _inputDispatcher, value, "input dispatcher");

    void IGameWindowHostInputCameraPublication.PublishCameraController(
        CameraController value) =>
        PublishCompositionOwner(ref _cameraController, value, "camera controller");

    void IGameWindowHostInputCameraPublication.PublishCameraPointerInput(
        AcDream.App.Input.CameraPointerInputController value) =>
        PublishCompositionOwner(ref _cameraPointerInput, value, "camera pointer input");

    void IGameWindowContentEffectsAudioPublication.PublishDatCollection(
        IDatReaderWriter value)
    {
        PublishCompositionOwner(ref _dats, value, "DAT collection");

        if (_automation is null)
            return;
        _automation.BindSpeciesNameResolver(
            AcDream.App.UI.Layout.CreatureDisplayNameResolver.Load(value).Resolve);
        _automation.BindPaletteColorResolver(
            new AcDream.Content.CharGen.ChargenAppearanceCatalog(value));
        if (!value.TryGet<DatReaderWriter.DBObjs.SkillTable>(0x0E000004u, out var skillTable)
            || skillTable is null)
        {
            Console.Error.WriteLine(
                "plugin automation: retail SkillTable 0x0E000004 missing; "
                + "plugins will see unnamed skills");
            return;
        }

        var names = new Dictionary<uint, string>(skillTable.Skills.Count);
        var icons = new Dictionary<uint, uint>(skillTable.Skills.Count);
        foreach (var entry in skillTable.Skills)
        {
            names[(uint)entry.Key] = entry.Value.Name;
            icons[(uint)entry.Key] = entry.Value.IconId;
        }
        _automation.BindSkillNames(names);
        _automation.BindSkillIcons(icons);
    }

    void IGameWindowContentEffectsAudioPublication.PublishPreparedAssetSource(
        IPreparedAssetSource value) =>
        PublishCompositionOwner(
            ref _preparedAssets,
            value,
            "prepared asset source");

    void IGameWindowContentEffectsAudioPublication.PublishMagicCatalog(
        MagicCatalog value)
    {
        PublishCompositionOwner(ref _magicCatalog, value, "magic catalog");
        _automation?.BindMagicCatalog(value);
    }

    void IGameWindowContentEffectsAudioPublication.PublishAnimationLoader(
        AcDream.Core.Physics.IAnimationLoader value) =>
        PublishCompositionOwner(ref _animLoader, value, "animation loader");

    void IGameWindowContentEffectsAudioPublication.PublishLiveEntityCollisionBuilder(
        AcDream.Runtime.Physics.LiveEntityCollisionBuilder value) =>
        PublishCompositionOwner(
            ref _liveEntityCollisionBuilder,
            value,
            "live-entity collision builder");

    void IGameWindowContentEffectsAudioPublication.PublishEmitterRegistry(
        AcDream.Core.Vfx.EmitterDescRegistry value) =>
        PublishCompositionOwner(ref _emitterRegistry, value, "emitter registry");

    void IGameWindowContentEffectsAudioPublication.PublishParticleSystem(
        AcDream.Core.Vfx.ParticleSystem value) =>
        PublishCompositionOwner(ref _particleSystem, value, "particle system");

    void IGameWindowContentEffectsAudioPublication.PublishParticleSink(
        AcDream.Core.Vfx.ParticleHookSink value) =>
        PublishCompositionOwner(ref _particleSink, value, "particle hook sink");

    void IGameWindowContentEffectsAudioPublication.PublishAnimationHookFrames(
        AcDream.App.Rendering.Vfx.AnimationHookFrameQueue value) =>
        PublishCompositionOwner(
            ref _animationHookFrames,
            value,
            "animation-hook frame queue");

    void IGameWindowContentEffectsAudioPublication.PublishPhysicsScriptLoader(
        AcDream.Content.Vfx.RetailPhysicsScriptLoader value) =>
        PublishCompositionOwner(
            ref _physicsScriptLoader,
            value,
            "physics-script loader");

    void IGameWindowContentEffectsAudioPublication.PublishPhysicsScriptRunner(
        AcDream.Core.Vfx.PhysicsScriptRunner value) =>
        PublishCompositionOwner(ref _scriptRunner, value, "physics-script runner");

    void IGameWindowContentEffectsAudioPublication.PublishLightingSink(
        AcDream.Core.Lighting.LightingHookSink value) =>
        PublishCompositionOwner(ref _lightingSink, value, "lighting hook sink");

    void IGameWindowContentEffectsAudioPublication.PublishTranslucencySink(
        AcDream.Core.Rendering.TranslucencyHookSink value) =>
        PublishCompositionOwner(
            ref _translucencySink,
            value,
            "translucency hook sink");

    void IGameWindowContentEffectsAudioPublication.PublishHookRegistrations(
        AcDream.App.Composition.AnimationHookRegistrationSet value) =>
        PublishCompositionOwner(
            ref _hookRegistrations,
            value,
            "animation-hook registrations");

    void IGameWindowContentEffectsAudioPublication.PublishAudio(
        ContentAudioGraph value)
    {
        ArgumentNullException.ThrowIfNull(value);
        if (_soundCache is not null
            || _audioEngine is not null
            || _entitySoundTables is not null
            || _audioSink is not null)
        {
            throw new InvalidOperationException(
                "The GameWindow composition shell already owns audio state.");
        }

        _soundCache = value.SoundCache;
        _audioEngine = value.Engine;
        _entitySoundTables = value.EntitySoundTables;
        _audioSink = value.HookSink;
    }

    void IGameWindowWorldRenderPublication.PublishSceneLighting(
        SceneLightingUboBinding value) =>
        PublishCompositionOwner(
            ref _sceneLightingUbo,
            value,
            "scene lighting");

    void IGameWindowWorldRenderPublication.PublishDebugLines(
        DebugLineRenderer value) =>
        PublishCompositionOwner(ref _debugLines, value, "debug lines");

    void IGameWindowWorldRenderPublication.PublishHudResources(
        BitmapFont font,
        TextRenderer text)
    {
        ArgumentNullException.ThrowIfNull(font);
        ArgumentNullException.ThrowIfNull(text);
        if (_debugFont is not null || _textRenderer is not null)
        {
            throw new InvalidOperationException(
                "The GameWindow composition shell already owns world HUD resources.");
        }
        _debugFont = font;
        _textRenderer = text;
    }

    void IGameWindowWorldRenderPublication.PublishTerrain(
        TerrainModernRenderer value) =>
        PublishCompositionOwner(ref _terrain, value, "terrain renderer");

    void IGameWindowWorldRenderPublication.PublishTerrainBuildState(
        float[] heightTable,
        AcDream.Core.Terrain.TerrainBlendingContext blending,
        System.Collections.Concurrent.ConcurrentDictionary<
            uint,
            AcDream.Core.Terrain.SurfaceInfo> surfaceCache)
    {
        ArgumentNullException.ThrowIfNull(heightTable);
        ArgumentNullException.ThrowIfNull(blending);
        ArgumentNullException.ThrowIfNull(surfaceCache);
        if (_heightTable is not null || _blendCtx is not null || _surfaceCache is not null)
        {
            throw new InvalidOperationException(
                "The GameWindow composition shell already owns terrain build state.");
        }
        _heightTable = heightTable;
        _blendCtx = blending;
        _surfaceCache = surfaceCache;
    }

    void IGameWindowWorldRenderPublication.PublishWbMeshAdapter(
        WbMeshAdapter value) =>
        PublishCompositionOwner(ref _wbMeshAdapter, value, "WB mesh adapter");

    void IGameWindowWorldRenderPublication.PublishTextureCache(
        TextureCache value) =>
        PublishCompositionOwner(ref _textureCache, value, "texture cache");

    void IGameWindowInteractionRetainedUiPublication.PublishInteractionRetainedUi(
        InteractionRetainedUiResult result)
    {
        ArgumentNullException.ThrowIfNull(result);
        if (_combatAttackController is not null
            || _externalContainerLifecycle is not null
            || _itemInteractionController is not null
            || _interactionUiLateBindings is not null
            || _uiHost is not null
            || _retailUiRuntime is not null
            || _retailChatVm is not null
            || _characterSheetProvider is not null
            || _magicRuntime is not null
            || _frameScreenshots is not null)
        {
            throw new InvalidOperationException(
                "The GameWindow composition shell already owns interaction/UI state.");
        }

        _combatAttackController = result.CombatAttack;
        _externalContainerLifecycle = result.ExternalContainerLifecycle;
        _itemInteractionController = result.ItemInteraction;
        _automation?.BindEquipment(
            (itemId, requestedLocation) =>
                result.ItemInteraction.TryWieldItem(
                    itemId,
                    (AcDream.Core.Items.EquipMask)requestedLocation),
            () => result.ItemInteraction.IsAutoWieldBusy);
        _automation?.BindItems(
            result.ItemInteraction.TryUseItemForAutomation,
            result.ItemInteraction.TryApplyItem,
            result.ItemInteraction.TryMoveItemForAutomation,
            result.ItemInteraction.TryMergeItemsForAutomation,
            result.ItemInteraction.TryDropItemForAutomation,
            result.ItemInteraction.TryGiveItemForAutomation,
            result.ItemInteraction.PlaceWorldItemInBackpack,
            result.ItemInteraction.TryAppraiseForAutomation,
            result.ItemInteraction.TrySalvageItemsForAutomation,
            (vendorId, itemId, amount) => result.ItemInteraction.TrySell(
                vendorId,
                [(amount, itemId)]));
        _interactionUiLateBindings = result.LateBindings;
        _magicRuntime = result.Magic;
        if (result.RetainedUi is { } retained)
        {
            _uiHost = retained.Host;
            _retailUiRuntime = retained.Runtime;
            _retailChatVm = retained.Chat;
            _characterSheetProvider = retained.CharacterSheet;
            _frameScreenshots = retained.Screenshots;
            retained.Runtime.AttachNativeCursorWindow(_window?.Native?.Glfw ?? 0);
        }
    }

    void IGameWindowLivePresentationPublication.PublishLivePresentation(
        LivePresentationResult result)
    {
        ArgumentNullException.ThrowIfNull(result);
        if (_liveEntities is not null
            || _wbEntitySpawnAdapter is not null
            || _entityScriptActivator is not null
            || _staticAnimationScheduler is not null
            || _renderSceneShadow is not null
            || _wbDrawDispatcher is not null
            || _retailSelectionScene is not null
            || _worldSelectionQuery is not null
            || _selectionInteractions is not null
            || _retainedUiGameplayBinding is not null
            || _equippedChildRenderer is not null
            || _entityEffects is not null
            || _liveEntityPresentation is not null
            || _projectileController is not null
            || _liveEntityProjectionWithdrawal is not null
            || _liveEntityLights is not null
            || _liveAnimationScheduler is not null
            || _animationPresenter is not null
            || _paperdollViewportRenderer is not null
            || _paperdollFramePresenter is not null
            || _creatureAppraisalViewportRenderer is not null
            || _creatureAppraisalFramePresenter is not null
            || _chargenPreviewRenderer is not null
            || _chargenPreviewController is not null
            || _envCellRenderer is not null
            || _envCellFrustum is not null
            || _landblockPresentationPipeline is not null
            || _portalDepthMask is not null
            || _clipFrame is not null
            || _skyRenderer is not null
            || _particleRenderer is not null
            || _renderFrameDiagnostics is not null
            || _livePresentationBindings is not null)
        {
            throw new InvalidOperationException(
                "The GameWindow composition shell already owns live presentation state.");
        }

        _wbEntitySpawnAdapter = result.EntitySpawnAdapter;
        _entityScriptActivator = result.EntityScriptActivator;
        _staticAnimationScheduler = result.StaticAnimationScheduler;
        _worldState = result.WorldState;
        _renderSceneShadow = result.RenderSceneShadow;
        _liveEntities = result.LiveEntities;
        _projectileController = result.ProjectileController;
        _liveEntityProjectionWithdrawal = result.ProjectionWithdrawal;
        _liveEntityLights = result.Lights;
        _liveAnimationScheduler = result.AnimationScheduler;
        _animationPresenter = result.AnimationPresenter;
        _equippedChildRenderer = result.EquippedChildren;
        _entityEffects = result.EntityEffects;
        _liveEntityPresentation = result.Presentation;
        _wbDrawDispatcher = result.DrawDispatcher;
        _retailSelectionScene = result.SelectionScene;
        _worldSelectionQuery = result.SelectionQuery;
        _selectionInteractions = result.SelectionInteractions;
        _automation?.BindSelectionActions(action =>
            result.SelectionInteractions.HandleInputAction(action switch
            {
                AcDream.Plugin.Abstractions.PluginSelectionAction.PreviousSelection =>
                    InputAction.SelectionPreviousSelection,
                AcDream.Plugin.Abstractions.PluginSelectionAction.PreviousPlayer =>
                    InputAction.SelectionPreviousPlayer,
                AcDream.Plugin.Abstractions.PluginSelectionAction.NextPlayer =>
                    InputAction.SelectionNextPlayer,
                _ => InputAction.None,
            }));
        _retainedUiGameplayBinding = result.RetainedGameplay;
        _paperdollViewportRenderer = result.PaperdollRenderer;
        _paperdollFramePresenter = result.PaperdollPresenter;
        _creatureAppraisalViewportRenderer = result.CreatureAppraisalRenderer;
        _creatureAppraisalFramePresenter = result.CreatureAppraisalPresenter;
        _chargenPreviewRenderer = result.ChargenPreviewRenderer;
        _chargenPreviewController = result.ChargenPreviewController;
        _summaryPreviewRenderer = result.SummaryPreviewRenderer;
        _summaryPreviewController = result.SummaryPreviewController;
        _envCellFrustum = result.EnvCellFrustum;
        _envCellRenderer = result.EnvCellRenderer;
        _landblockPresentationPipeline = result.LandblockPipeline;
        _clipFrame = result.ClipFrame;
        _portalDepthMask = result.PortalDepthMask;
        _skyRenderer = result.SkyRenderer;
        _particleRenderer = result.ParticleRenderer;
        _renderFrameDiagnostics = result.FrameDiagnostics;
        _livePresentationBindings = result.RuntimeBindings;
    }

    void IGameWindowSessionPlayerPublication.PublishSessionPlayer(
        SessionPlayerResult result)
    {
        ArgumentNullException.ThrowIfNull(result);
        if (_streamer is not null
            || _streamingController is not null
            || _streamingOriginRecenter is not null
            || _worldReveal is not null
            || _spawnClaimHydration is not null
            || _liveEntityHydration is not null
            || _liveEntityNetworkUpdates is not null
            || _liveEntityLiveness is not null
            || _liveEntitySessionEvents is not null
            || _gameplayInputFrame is not null
            || _localPlayerAnimation is not null
            || _localPlayerShadowSynchronizer is not null
            || _playerModeController is not null
            || _playerModeAutoEntry is not null
            || _localPlayerTeleport is not null
            || _liveSessionHost is not null
            || _gameplayInputActions is not null
            || _sessionPlayerBindings is not null)
        {
            throw new InvalidOperationException(
                "The GameWindow composition shell already owns session/player state.");
        }

        _streamer = result.Streamer;
        _streamingController = result.Streaming;
        _streamingOriginRecenter = result.StreamingOriginRecenter;
        _worldReveal = result.WorldReveal;
        _spawnClaimHydration = result.SpawnClaimHydration;
        _liveEntityHydration = result.Hydration;
        _automation?.BindGhostDeletion(result.Deletion.DeleteClientGhost);
        _liveEntityNetworkUpdates = result.NetworkUpdates;
        _liveEntityLiveness = result.Liveness;
        _liveEntitySessionEvents = result.SessionEvents;
        _gameplayInputFrame = result.GameplayInput;
        _localPlayerAnimation = result.LocalPlayerAnimation;
        _localPlayerShadowSynchronizer = result.LocalPlayerShadow;
        _playerModeController = result.PlayerMode;
        _playerModeAutoEntry = result.PlayerModeAutoEntry;
        _localPlayerTeleport = result.LocalTeleport;
        _liveSessionHost = result.SessionHost;
        _automation?.BindSessionCommands(result.GameRuntime);
        _gameplayInputActions = result.GameplayActions;
        _sessionPlayerBindings = result.RuntimeBindings;
    }

    void IGameWindowFrameRootPublication.PublishFrameRoots(
        FrameRootResult result)
    {
        ArgumentNullException.ThrowIfNull(result);
        if (_frameRootBindings is not null
            || _frameGraphPublication is not null)
        {
            throw new InvalidOperationException(
                "The GameWindow composition shell already owns frame roots.");
        }

        _frameRootBindings = result.RuntimeBindings;
        _frameGraphPublication = result.FrameGraphPublication;
    }

    private static void PublishCompositionOwner<T>(
        ref T? destination,
        T value,
        string name)
        where T : class
    {
        ArgumentNullException.ThrowIfNull(value);
        if (destination is not null)
            throw new InvalidOperationException(
                $"The GameWindow composition shell already owns {name}.");
        destination = value;
    }

    [System.Diagnostics.CodeAnalysis.MemberNotNull(nameof(_graphics), nameof(_input))]
    private GameWindowPlatformResult<GameWindowGraphics, IInputContext> AcquirePlatform()
    {
        GameWindowPlatformResult<GameWindowGraphics, IInputContext> platform =
            GameWindowPlatformAcquisition.Acquire(
                CreateGraphics,
                static graphics => graphics.Dispose(),
                () => _window!.CreateInput(),
                static input => input.Dispose(),
                this);
        if (_graphics is null || _input is null)
            throw new InvalidOperationException(
                "Platform acquisition returned without publishing both owners.");
        return platform;
    }

    private static Func<int, int, byte[]> CreateBackbufferReader(
        GameWindowGraphics graphics,
        IGpuDevice device) =>
        (width, height) =>
            AcDream.App.Diagnostics.FrameScreenshotController.FlipRows(
                device.CaptureBackbuffer(width, height),
                width,
                height);

    private GameWindowGraphics CreateGraphics()
    {
        AcDream.App.Rendering.Gpu.Vk.VulkanGraphicsContext vulkan =
            AcDream.App.Rendering.Gpu.Vk.VulkanGraphicsContext.Acquire(
                _window!,
                _options,
                _platformServices,
                _startupPacing,
                _startupQuality.MsaaSamples,
                Console.WriteLine);
        _vulkanGraphics = vulkan;
        return new VulkanGameWindowGraphics(vulkan);
    }

    private void OnLoad()
    {

        GameWindowPlatformResult<GameWindowGraphics, IInputContext> platform = AcquirePlatform();

        DisplayModeCatalog.InstallFromWindow(_window!);

        WindowIconLoader.Apply(_window!);

        GameWindowCompositionPipeline.Run<
            GameWindowPlatformResult<GameWindowGraphics, IInputContext>,
            HostInputCameraResult,
            ContentEffectsAudioResult,
            SettingsDevToolsResult,
            WorldRenderResult,
            InteractionRetainedUiResult,
            LivePresentationResult,
            SessionPlayerResult,
            FrameRootResult>(
            platform,
            platformResult => new HostInputCameraCompositionPhase(
                new HostInputCameraDependencies(
                    _framebufferResize,
                    _window!.FramebufferSize,
                    _hostQuiescence,
                    _inputCapture,
                    _keyBindings,
                    _movementInput,
                    _cameraInput,
                    _localPlayerMode,
                    _chaseCameraInput,
                    _pointerPosition,
                    _renderDiagnosticLog,
                    _options.InitialOrbitDistanceMeters,
                    _options.InitialOrbitYawDegrees,
                    _options.InitialOrbitPitchDegrees),
                this).Compose(platformResult),
            (platformResult, hostInputCamera) =>
                new ContentEffectsAudioCompositionPhase(
                new ContentEffectsAudioDependencies(
                    _datDir,
                    _options.PreparedAssetPath,
                    _options.PreparedAssetOverlayPath,
                    _options.PreparedAssetBaseRecipeVersion,
                    _options.PreparedAssetEffectiveRecipeVersion,
                    _options.ResidencyBudgets,
                    _physicsDataCache,
                    _animationDiagnostics.DumpMotionEnabled,
                    _runtime,
                    _hookRouter,
                    _effectPoses,
                    _entityEffectAdvance,
                    Lighting,
                    _translucencyFades,
                    _options.NoAudio,
                    Console.WriteLine,
                    Console.Error.WriteLine),
                this).Compose(platformResult, hostInputCamera),
            (platformResult, hostInputCamera, contentEffectsAudio) =>
                new SettingsDevToolsCompositionPhase(
                    new SettingsDevToolsDependencies(
                        _runtimeSettings,
                        new RuntimeSettingsStartupTargets(
                            new SilkRuntimeDisplayWindowTarget(_window!),
                            _displayFramePacing,
                            hostInputCamera.CameraController,
                            contentEffectsAudio.Audio?.Engine))
                    {
                        RenderPacks = _renderPackRegistry,
                        GpuDevice = hostInputCamera.GpuDevice,
                    })
                    .Compose(platformResult, hostInputCamera, contentEffectsAudio),
            (platformResult, contentEffectsAudio, settingsDevTools) =>
            {
                const uint initialCenterLandblockId = 0xA9B4FFFFu;
                WorldRenderResult worldRender = new WorldRenderCompositionPhase(
                    new WorldRenderDependencies(
                        _worldEnvironment,
                        _renderResourceLifetime,
                        _gpuDevice!.Retirement,
                        _options.ResidencyBudgets,
                        initialCenterLandblockId,
                        _applicationPaths.DiagnosticsDirectory,
                        Console.WriteLine,
                        _gpuDevice!,
                        _gpuFrameLifetime!),
                    this).Compose(platformResult, contentEffectsAudio, settingsDevTools);
                Console.WriteLine(
                    $"loading world view centered on " +
                    $"0x{worldRender.TerrainBuild.InitialCenterLandblockId:X8}");
                return worldRender;
            },
            (platformResult, hostInputCamera, contentEffectsAudio, settingsDevTools, worldRender) =>
            {
                Action<string>? compositionToast = null;
                return new InteractionRetainedUiCompositionPhase(
                    new InteractionRetainedUiDependencies(
                    _options,
                    platformResult.Graphics,
                    CreateBackbufferReader(
                        platformResult.Graphics,
                        hostInputCamera.GpuDevice),
                    _window!,
                    platformResult.Input,
                    worldRender.Foundation.ShadersDirectory,
                    contentEffectsAudio.Dats,
                    _datLock,
                    worldRender.Foundation.TextureCache,
                    worldRender.Foundation.DebugFont,
                    _hostQuiescence,
                    _retainedInputCapture,
                    hostInputCamera.InputDispatcher,
                    _localPlayerTeleportSink,
                    _applicationPaths.KeyBindingsFile,
                    _runtimeSettings,
                    _buildingDegrades,
                    _runtime,
                    _combatAttackOperations,
                    _combatTargetOperations,
                    _spellCastOperations,
                    contentEffectsAudio.MagicCatalog,
                    _stackSplitQuantity,
                    _uiRegistry,
                    _liveCombatModeCommands,
                    _localPlayerIdentity,
                    _localPlayerMode,
                    viewPlane => new SelectionCameraSource(
                        hostInputCamera.CameraController,
                        _window!,
                        viewPlane),
                    _uiFrameDiagnostics,
                    ExistingVitals: null,
                    compositionToast,
                    ClientTimerNow,
                    Console.WriteLine,
                    hostInputCamera.GpuDevice,
                    hostInputCamera.GpuFrameLifetime,
                    () => WorldTime.CurrentCalendar,
                    settingsDevTools.RenderPacks,
                    _renderPackDiagnostics.CaptureDiagnostics,
                    _applicationPaths.ScreenshotsDirectory,
                    _automation,
                    GameplayInputFrame: () => _gameplayInputFrame),
                _retailUiLease,
                this).Compose(
                    platformResult,
                    hostInputCamera,
                    contentEffectsAudio,
                    settingsDevTools,
                    worldRender);
            },
            (platformResult,
                hostInputCamera,
                contentEffectsAudio,
                settingsDevTools,
                worldRender,
                interactionUi) =>
            {
                Action<string>? compositionToast = null;
                return new LivePresentationCompositionPhase(
                    new LivePresentationDependencies(
                    _options,
                    platformResult.Graphics,
                    _window!,
                    _datLock,
                    _runtimeSettings,
                    _hostQuiescence,
                    _physicsEngine,
                    _physicsDataCache,
                    _worldGameState,
                    _worldEvents,
                    _runtime,
                    _liveEntityRuntimeSlot,
                    _liveEntityMotionBindings,
                    _entityEffectAdvance,
                    _effectPoses,
                    _remotePhysicsUpdater,
                    _localPlayerShadow,
                    _animatedEntities,
                    _animationDiagnostics,
                    _classificationCache,
                    _translucencyFades,
                    _retailAlphaQueue,
                    _cellVisibility,
                    _liveWorldOrigin,
                    _localPlayerIdentity,
                    _chaseCameraInput,
                    _pointerPosition,
                    _playerApproachCompletions,
                    _renderResourceLifetime,
                    _portalTunnelFallback,
                    _hookRouter,
                    _renderDiagnosticLog,
                    WorldTime,
                    DevWorldEntities: null,
                    DevFrameDiagnostics: null,
                    _uiFrameDiagnostics,
                    Console.WriteLine,
                    compositionToast,
                    _renderPackDiagnostics),
                this).Compose(
                    platformResult,
                    hostInputCamera,
                    contentEffectsAudio,
                    settingsDevTools,
                    worldRender,
                    interactionUi);
            },
            (hostInputCamera,
                contentEffectsAudio,
                settingsDevTools,
                worldRender,
                interactionUi,
                livePresentation) =>
                new SessionPlayerCompositionPhase(
                new SessionPlayerDependencies(
                    _options,
                    _runtime,
                    _window!,
                    _datLock,
                    _runtimeSettings,
                    settingsDevTools,
                    _worldEnvironment,
                    _worldSceneDebugState,
                    _runtimeDiagnosticCommands,
                    _liveCombatModeCommands,
                    _combatModeOperations,
                    _hostQuiescence,
                    _physicsEngine,
                    _physicsDataCache,
                    _worldGameState,
                    _worldEvents,
                    _classificationCache,
                    _liveEntityRuntimeSlot,
                    _animatedEntities,
                    _remoteMovementObservations,
                    _remotePhysicsUpdater,
                    _remoteInboundMotion,
                    _inboundEntityEvents,
                    _liveEntityMotionBindings,
                    _localPlayerIdentity,
                    _playerHostSlot,
                    _localPlayerMode,
                    _chaseCameraInput,
                    _localPlayerOutbound,
                    _localPlayerTeleportSink,
                    _liveWorldOrigin,
                    _renderRange,
                    _localPlayerShadow,
                    _viewportAspect,
                    _playerApproachCompletions,
                    _pointerPosition,
                    _movementInput,
                    _inputCapture,
                    _particleVisibility,
                    _translucencyFades,
                    _effectPoses,
                    _updateFrameClock,
                    _movementTruthDiagnostics,
                    _combatAttackOperations,
                    _combatFeedback,
                    _portalTunnelFallback,
                    Console.WriteLine,
                    _automation is null
                        ? null
                        : _automation.TryHandlePluginCommand,
                    _statusWriter),
                this).Compose(
                    hostInputCamera,
                    contentEffectsAudio,
                    settingsDevTools,
                    worldRender,
                    interactionUi,
                    livePresentation),
            (platformResult,
                hostInputCamera,
                contentEffectsAudio,
                settingsDevTools,
                worldRender,
                interactionUi,
                livePresentation,
                sessionPlayer) => new FrameRootCompositionPhase(
                    new FrameRootDependencies(
                        _options,
                        _runtime,
                        platformResult.Graphics,
                        _window!,
                        platformResult.Input,
                        WorldTime,
                        Weather,
                        Lighting,
                        _worldEnvironment,
                        _physicsEngine,
                        _cellVisibility,
                        _localPlayerMode,
                        _localPlayerIdentity,
                        _chaseCameraInput,
                        _liveWorldOrigin,
                        _particleVisibility,
                        _effectPoses,
                        _renderRange,
                        _runtimeSettings,
                        _buildingDegrades,
                        _displayFramePacing,
                        _worldSceneDebugState,
                        _retailAlphaQueue,
                        _frameProfiler,
                        _frameDiag,
                        _renderDiagnosticLog,
                        _debugVmRenderFacts,
                        _inputCapture,
                        _cameraInput,
                        _animatedEntities,
                        _updateFrameClock,
                        _frameGraphs,
                        Console.WriteLine,
                        _renderPackDiagnostics),
                    this).Compose(
                        platformResult,
                        hostInputCamera,
                        contentEffectsAudio,
                        settingsDevTools,
                        worldRender,
                        interactionUi,
                        livePresentation,
                        sessionPlayer),
            frameRoots => new SessionStartCompositionPhase(
                new SessionStartDependencies(
                    Console.WriteLine))
                .Start(frameRoots));
    }

    private void OnUpdate(double dt)
    {
        _renderLoopArmed = true;
        using var _updStage = _frameProfiler.BeginStage(
            AcDream.App.Diagnostics.FrameStage.Update);
        _frameGraphs.Tick(new AcDream.App.Update.UpdateFrameInput(dt));
        _worldEvents.FireTick(dt);
        _renderLoopArmed = false;
    }

    private void OnRender(double deltaSeconds)
    {
        _renderLoopArmed = true;
        if (_nativeCloseRequested)
        {
            _renderLoopArmed = false;
            return;
        }
        Vector2D<int> size = _window!.Size;
        if (_vulkanGraphics is { } vulkan && !vulkan.PrepareFrame())
        {
            _renderLoopArmed = false;
            return;
        }
        AcDream.App.Rendering.RenderFrameOutcome outcome;
        try
        {
            _frameGraphs.Render(
                new AcDream.App.Rendering.RenderFrameInput(
                    deltaSeconds,
                    size.X,
                    size.Y),
                out outcome);
        }
        catch (AcDream.App.Rendering.Gpu.Vk.VulkanSwapchainOutOfDateException)
            when (_vulkanGraphics is not null)
        {
            _vulkanGraphics.RequestRecreate();
            _renderLoopArmed = false;
            return;
        }

        if (!outcome.SkippedZeroArea)
            _vulkanGraphics?.NoteFrameClosed();
        _renderLoopArmed = false;
    }



    private void OnFramebufferResize(Silk.NET.Maths.Vector2D<int> newSize)
        => _framebufferResize.Resize(newSize);

    private void OnClosing() => CompleteShutdown(releaseNativeWindow: false);

    private void CompleteShutdown(bool releaseNativeWindow)
    {
        if (!releaseNativeWindow && !_nativeRunReturned)
        {
            _nativeCloseRequested = true;
            return;
        }

        if (!_lifetime.HasShutdownRoots)
        {
            PersistKeyBindingsAtShutdown();
            if (_runtime.Session.IsInWorld)
                _statusWriter.Disconnected(_options.SessionId ?? "app", "stopped");
            _lifetime.PublishShutdownRoots(CaptureShutdownRoots());
        }

        GameWindowLifetimeReport report = releaseNativeWindow
            ? _lifetime.CompleteAndReleaseNativeWindow()
            : _lifetime.TryComplete();
        if (report.Status == GameWindowLifetimeStatus.Complete)
        {
            if (releaseNativeWindow)
                ReportExited(report);
            return;
        }

        Console.Error.WriteLine(
            $"[shutdown] status={report.Status}, blocked={report.BlockedStage ?? "none"}");
        foreach (ResourceShutdownCleanupFailure cleanup in report.CleanupFailures)
        {
            Console.Error.WriteLine(
                $"[shutdown] cleanup '{cleanup.Operation}' in '{cleanup.Stage}': " +
                cleanup.Error);
        }

        if (report.Error is not null)
            Console.Error.WriteLine($"[shutdown] {report.Error}");

        if (releaseNativeWindow)
            ReportExited(report);
    }

    private void PersistKeyBindingsAtShutdown()
    {
        if (_keyBindingsPersisted || _inputDispatcher is null) return;
        _keyBindingsPersisted = true;
        try
        {
            KeyBindings current = _inputDispatcher.Bindings;
            var profiles = new AcDream.App.Input.RetailKeymapProfileStore(
                _applicationPaths.KeyBindingsFile);
            RetailKeymapSaveResult saved = profiles.SaveActive(current);
            if (saved.Status != RetailKeymapSaveStatus.Saved)
            {
                Console.WriteLine(
                    $"keymap: shutdown save failed ({saved.Status}): {saved.Error}");
                return;
            }
            current.SaveToFile(_applicationPaths.KeyBindingsFile);
        }
        catch (Exception failure)
        {
            Console.WriteLine($"keymap: shutdown persistence failed: {failure.Message}");
        }
    }

    private void ReportExited(GameWindowLifetimeReport report)
    {
        string sessionId = _options.SessionId ?? "app";
        if (_runFailure is not null)
        {
            _statusWriter.Exited(sessionId, 1, "crashed");
            return;
        }

        if (report.Status == GameWindowLifetimeStatus.Complete)
            _statusWriter.Exited(sessionId, 0, "graceful");
        else
            _statusWriter.Exited(sessionId, 1, "shutdown-incomplete");
    }

    private GameWindowShutdownRoots CaptureShutdownRoots() => new(
        new IngressShutdownRoots(
            _hostQuiescence,
            _liveCombatModeCommands,
            _runtimeDiagnosticCommands,
            _retainedUiGameplayBinding,
            _gameplayInputActions,
            _cameraPointerInput,
            _inputDispatcher,
            _mouseSource,
            _kbSource,
            _retailUiLease,
            _uiHost,
            _pluginSession,
            _runtime,
            _movementInput,
            _cameraInput,
            _windowCallbacks),
        new FrameShutdownRoots(
            _frameGraphPublication,
            _frameRootBindings,
            _sessionPlayerBindings,
            _interactionUiLateBindings),
        new LiveShutdownRoots(
            _cameraPointerInput,
            _retailUiLease,
            _magicRuntime,
            _itemInteractionController,
            _externalContainerLifecycle,
            _streamer,
            _equippedChildRenderer,
            _liveEntities,
            _runtime,
            _runtimeHostLease,
            _renderSceneShadow,
            _livePresentationBindings,
            _entityEffectAdvance,
            _entityEffects,
            _hookRegistrations,
            _liveEntityLights,
            _liveEntityPresentation,
            _animationHookFrames,
            _effectPoses,
            _audioEngine),
        new RenderShutdownRoots(
            _gpuFrameFlights,
            _gpuDevice,
            _localPlayerTeleport,
            _portalTunnelFallback,
            _paperdollViewportRenderer,
            _creatureAppraisalViewportRenderer,
            _chargenPreviewRenderer,
            _chargenPreviewController,
            _summaryPreviewRenderer,
            _summaryPreviewController,
            _wbDrawDispatcher,
            _envCellRenderer,
            _portalDepthMask,
            _clipFrame,
            _skyRenderer,
            _particleRenderer,
            _textureCache,
            _wbMeshAdapter,
            _terrain,
            _sceneLightingUbo,
            _debugLines,
            _textRenderer,
            _debugFont,
            _displayFramePacing,
            _frameProfiler,
            _renderResourceLifetime,
            _constructionCleanup),
        new PlatformShutdownRoots(
            _dats,
            _preparedAssets,
            _input,
            _graphics));
    private void OnFocusChanged(bool focused)
        => _cameraPointerInput?.HandleFocusChanged(focused);

    public void Dispose()
    {
        CompleteShutdown(releaseNativeWindow: true);
        _window = null;
    }

}
