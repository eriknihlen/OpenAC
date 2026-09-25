using AcDream.App.Combat;
using AcDream.App.Input;
using AcDream.App.Rendering;
using AcDream.App.Net;
using AcDream.App.Plugins;
using AcDream.Core.Net;
using AcDream.Core.Plugins;
using AcDream.Plugin.Abstractions;
using AcDream.Runtime;
using AcDream.Runtime.Chat;
using AcDream.Runtime.Gameplay;
using AcDream.Runtime.Plugins;
using AcDream.Runtime.Session;
using AcDream.UI.Abstractions.Input;
using Silk.NET.Input;

namespace AcDream.HostParity.Tests;

/// <summary>
/// The windowed client with the window taken away. Everything below the
/// presentation layer is the real thing: the same operation classes the
/// window's composition binds, into the same slots the window builds its
/// runtime with, behind the capability record the window hands the shared
/// binding pass.
///
/// What is NOT under this arm, and why:
/// * the local player's movement controller, which composition only creates
///   once a body has been placed in a drawn world, so the windowed attack
///   path's pre-attack movement push has nothing to push;
/// * the retained UI, the selection interaction controller and the input
///   dispatcher, each of which needs a presentation tree, so the seams they
///   feed stay empty here exactly as they do in a run with no window;
/// * the drawn-world teardown behind the entity deletion controller. The
///   deletion controller itself and the drawn-world entity runtime it
///   unregisters from are the real ones, lent to the runtime as its
///   authoritative-delete route the way the window lends them, so a dismissed
///   object leaves through the window's own delete; only the take-down of
///   what was drawn has nothing to take down, since nothing is;
/// * the three extras the window adds to the shared item owner -- the
///   walk-to-then-act route, which pack the inventory panel has open, and
///   how much of a stack the split control is asking for -- which need a
///   presentation tree; the item commands themselves come from the runtime
///   and are under this arm;
/// * the spell-cast operations, which the windowed host only binds once a
///   spell catalog has been read out of the installed data files.
/// Each is reported through <see cref="Warnings"/> rather than faked, and a
/// scenario that would depend on one is not run.
/// </summary>
internal sealed class WindowedArm : ParityArm
{
    private readonly CombatFeedbackSlot _feedback = new();
    /// <summary>
    /// Where a typed line goes on this client, hung the way its session
    /// composition hangs it: the bus asks the shared plugin surface what the
    /// plugins make of the line and which verbs it answers before offering
    /// the line on. Both are resolved when a line arrives rather than
    /// captured here, because the surface below does not exist yet.
    /// </summary>
    private readonly LiveSessionCommandSurface _commands;
    private readonly LiveSessionAppSource _sessionSource;
    private readonly WorldGameState _state = new();
    private readonly WorldEvents _events = new();

    /// <summary>
    /// The runtime clock this client paces its plugin tick from, built the
    /// way the window builds it.
    /// </summary>
    private readonly AcDream.Runtime.Plugins.RuntimePluginTickClock _pluginTick;
    private readonly RuntimeWorldEntityProjection _worldEntities;
    private readonly RuntimeAutomationSurface _automation;
    private readonly List<string> _warnings = [];
    private readonly List<IDisposable> _bindings = [];
    private readonly AcDream.App.Runtime.CurrentGameRuntimeAdapter _sessionCommands;
    private InputDispatcher? _dispatcher;

    /// <summary>
    /// The window's delete route: its drawn-world entity runtime over the
    /// shared entity owner, and the deletion controller that unregisters from
    /// it.
    /// </summary>
    private readonly AcDream.App.World.LiveEntityRuntime _liveEntities;
    private readonly AcDream.App.World.LiveEntityDeletionController _deletion;

    /// <summary>
    /// How many deletes the runtime has sent down the window's own delete
    /// route, so a scenario can show a dismissal took it.
    /// </summary>
    internal int WindowDeletes { get; private set; }

    /// <summary>
    /// The parts of its own session bindings this client builds while the
    /// session host is being built, so they are made in that builder rather
    /// than in the constructor body.
    /// </summary>
    private readonly AcDream.App.Streaming.GpuWorldState _worldState = new();
    private readonly AcDream.Runtime.Session.SessionStatusWriter _status =
        new(path: null);
    private AcDream.App.Settings.RuntimeSettingsController? _settings;
    private AcDream.App.Streaming.DeferredLocalPlayerTeleportNetworkSink?
        _teleport;

    internal WindowedArm()
        : base(
            ParityHost.Windowed,
            operations =>
            GraphicalAutomationCapabilities.BuildRuntimeDependencies(
                new CombatAttackOperationsSlot(),
                new RuntimeCombatTargetOperationsSlot(),
                new RuntimeCombatModeOperationsSlot(),
                new AcDream.App.Spells.RuntimeSpellCastOperationsSlot(),
                timeSyncDiagnostic: null,
                sessionOperations: operations))
    {
        var attack = (CombatAttackOperationsSlot)Dependencies.CombatAttackOperations;
        var target = (RuntimeCombatTargetOperationsSlot)
            Dependencies.CombatTargetOperations;

        // What a plugin sees in the world, exactly as the window binds it:
        // the runtime's object directory, not what is drawn.
        _worldEntities = new RuntimeWorldEntityProjection(
            Runtime,
            _warnings.Add);
        _state.BindWorldEntities(_worldEntities);
        // The runtime decides what may be dismissed; this client lends the
        // delete route it runs for a server delete, exactly as its session
        // composition does.
        _liveEntities = new AcDream.App.World.LiveEntityRuntime(
            _worldState,
            new AcDream.App.World.DelegateLiveEntityResourceLifecycle(
                static _ => { },
                static _ => { }),
            Runtime.EntityObjects);
        _deletion = new AcDream.App.World.LiveEntityDeletionController(
            _liveEntities,
            Runtime.EntityObjects,
            new UndrawnTeardown(_warnings.Add),
            new AcDream.App.Input.LocalPlayerIdentityState(Runtime.PlayerIdentity));
        Runtime.GhostDismissalOwner.BindAuthoritativeDelete(delete =>
        {
            WindowDeletes++;
            return _deletion.Delete(delete);
        });
        // The character's contracts, exactly as the window binds them:
        // the runtime's own answer, words and all.
        _state.ContractsSource = Runtime.ContractsOwner.ProjectForPlugins;
        _events.BindWorldEntities(_worldEntities);
        _pluginTick = new AcDream.Runtime.Plugins.RuntimePluginTickClock(
            _events.FireTick,
            () => Runtime.Generation.Value);
        _commands = new LiveSessionCommandSurface(
            line => _automation?.TryHandlePluginCommand(line) == true,
            line => _automation!.InterceptChatInput(line));
        _sessionSource = new LiveSessionAppSource(Runtime.Session, _commands);
        _bindings.Add(_feedback.BindOwned(text =>
            Runtime.CommunicationOwner.AddText(
                text, AcDream.Core.Chat.RetailLogTextType.ClientLocal)));
        _bindings.Add(attack.BindOwned(new LiveCombatAttackOperations(
            Runtime.ActionOwner.Combat,
            new CombatAttackTargetSource(Runtime),
            new CharacterOptionCombatSettingsSource(Runtime.CharacterOwner.Options),
            Runtime,
            _sessionSource,
            _sessionSource,
            _feedback)));
        _bindings.Add(target.BindOwned(new LiveCombatTargetOperations(
            autoTarget: () => Runtime.CharacterOwner.Options.GetOptionBit(
                AcDream.Core.Net.Messages.CharacterOptionId.AutoTarget),
            selectClosestTarget: () =>
                RuntimeAttackTargetResolver.SelectClosest(Runtime))));

        var mode = (RuntimeCombatModeOperationsSlot)
            Dependencies.CombatModeOperations;
        _bindings.Add(mode.BindOwned(new LiveCombatModeOperations(
            new LiveSessionCombatModeAuthority(Session),
            new LocalPlayerCombatEquipmentSource(
                Runtime.EntityObjects.Objects,
                new AcDream.App.Input.LocalPlayerIdentityState(
                    Runtime.PlayerIdentity)),
            // The window tells its item owner that a stance change was the
            // player's doing, so an auto-wield in flight stands down. There
            // is no auto-wield on either arm, so neither has anything to
            // tell -- the windowless host passes its own absent controller.
            new UnwatchedCombatModeIntent())));

        // The windowed client's own command adapter, which until now could
        // not be built without a presentation tree and so was never under a
        // test. The one thing it asks a window for -- something to act on a
        // selection key with -- is absent here, as it is in a run with no
        // window, and the adapter answers that as unsupported.
        _sessionCommands = new AcDream.App.Runtime.CurrentGameRuntimeAdapter(
            Runtime,
            Session,
            _commands);
        _automation = RuntimeAutomationBindings.CreateSurface(
            GraphicalAutomationCapabilities.BuildSurfaceInputs(
                new GraphicalSurfaceInputParts
                {
                    Events = _events,
                    PeerDirectory = PeerDirectory,
                    PluginTags = ConfiguredPluginTags,
                }));
        RuntimeAutomationBindings.Apply(
            _automation,
            Runtime,
            GraphicalAutomationCapabilities.Build(new GraphicalAutomationParts
            {
                Runtime = Runtime,
                Warn = _warnings.Add,
                SessionCommands = _sessionCommands,
            }));
        // Everything a plugin reaches for that does not need a graphics card
        // is the real thing here: this client's own hotkey registry over a
        // file in this arm's scratch folder, its own world-line store, and
        // real storage on disk. Only the two that cannot exist without a
        // window are stood in for -- the panel tree and the clipboard -- and
        // those answer as this client answers when there is no window.
        Host = new AppPluginHost(
            new RecordingPluginLogger(),
            _state,
            _events,
            Runtime.ActionOwner.Selection,
            NoOpUiRegistry.Instance,
            _automation,
            storage: new AcDream.Core.Plugins.FilePluginStorage(
                Path.Combine(DataDirectory, "plugin-storage")),
            commands: _automation.PluginCommands,
            lootClassifiers:
                new AcDream.Core.Plugins.PluginLootClassifierRegistry(),
            vtankProfiles: new AcDream.Core.Plugins.FilePluginStorage(
                Path.Combine(DataDirectory, "plugin-profiles")),
            clipboard: new NoWindowClipboard(),
            hotkeys: new AcDream.App.Input.AppHotkeyRegistry(
                Path.Combine(DataDirectory, "plugin-hotkeys.json")),
            worldLines: new AcDream.App.Plugins.PluginWorldLineStore(),
            // What this client was started with for each plugin, out of the
            // same map the other arm is given.
            sessionSettings: ConfiguredPluginSettings);
    }

    /// <summary>
    /// The clipboard belongs to the window: this client reads and writes it
    /// through the one it opened, so a run without one has none and says so,
    /// which is the same answer the client with no window gives.
    /// </summary>
    private sealed class NoWindowClipboard : IPluginClipboard
    {
        public bool TrySetText(string text) => false;
    }

    internal override IPluginHost Host { get; }

    internal override IReadOnlyList<string> Warnings => _warnings;

    /// <summary>Where the window raises it: the shared plugin surface.</summary>
    internal override void ShowConfirmation(PluginConfirmation confirmation) =>
        _automation.RaiseConfirmationRequested(confirmation);

    /// <summary>
    /// The windowed client's own session bindings, from its own builder. The
    /// parts a drawn world owns are stood in for and reported: there is no
    /// vitals bar, no panel tree, no paperdoll and no mixer, and the login
    /// tunnel is a presentation effect with nothing to show. Everything else
    /// is the real thing -- this client's settings store on disk, its
    /// player-mode arming, its persistent-drawable bookkeeping.
    /// </summary>
    protected override LiveSessionHostBindings CreateSessionHostBindings(
        LiveSessionRoutingFactories routing,
        Action<RuntimeGenerationToken> reset)
    {
        _settings = new AcDream.App.Settings.RuntimeSettingsController(
            new AcDream.App.Settings.JsonRuntimeSettingsStorage(
                Path.Combine(DataDirectory, "settings")),
            log: _warnings.Add);
        _teleport = new AcDream.App.Streaming
            .DeferredLocalPlayerTeleportNetworkSink();
        _teleport.Bind(new UndrawnTeleportPresentation(_warnings.Add));
        return GraphicalAutomationCapabilities.BuildSessionHostBindings(
            new GraphicalSessionHostParts
            {
                CreateEvents = routing.CreateEvents,
                CreateCommands = routing.CreateCommands,
                Reset = reset,
                Identity = new AcDream.App.Input.LocalPlayerIdentityState(
                    Runtime.PlayerIdentity),
                Communication = Runtime.CommunicationOwner,
                Combat = Runtime.ActionOwner.Combat,
                Settings = _settings,
                PlayerModeAutoEntry = new AcDream.App.Input.PlayerModeAutoEntry(
                    isLiveInWorld: () => Runtime.Lifecycle.State
                        == RuntimeLifecycleState.InWorld,
                    isPlayerEntityPresent: () => HasLiveBody,
                    isPlayerControllerReady: () => HasLiveBody,
                    // Player mode is about where the camera goes and what the
                    // keyboard steers, so a run with nothing drawn never
                    // reaches it. It is armed all the same, from the real
                    // class, so the arming itself is under the harness.
                    isWorldReady: () => false,
                    enterPlayerMode: () => _warnings.Add(
                        "player mode: nothing is drawn, so there is no camera "
                        + "to put behind the character")),
                WorldState = _worldState,
                Teleport = _teleport,
                StatusWriter = _status,
                SessionId = Name,
                // The four a drawn client lends its session host, absent here
                // for the same reason the census says they are.
                Vitals = null,
                RetainedUi = null,
                Paperdoll = null,
                WorldAudio = null,
                LoginCommands = null,
                Warn = _warnings.Add,
            });
    }

    /// <summary>
    /// The windowed client's own character bindings. The skill formulas are
    /// the real resolver over no installed data files, which is what this
    /// client does before it has read them; the confirmation hooks belong to
    /// the panel tree and are absent.
    /// </summary>
    internal override LiveCharacterSessionBindings
        CreateCharacterSessionBindings() =>
        GraphicalAutomationCapabilities.BuildCharacterSessionBindings(
            new GraphicalCharacterSessionParts
            {
                Character = Runtime.CharacterOwner,
                Combat = Runtime.ActionOwner.Combat,
                Settings = _settings!,
                MovementStats = Runtime.MovementStats,
                ResolveSkillFormulaBonus =
                    new AcDream.Content.Skills.LiveSkillCreditResolver(
                        skillTable: null).Resolve,
                ClientTime = () => Runtime.Clock.SimulationTimeSeconds,
                // Neither arm starts from a session document, so nothing is
                // declared for the seeder to compare against.
                NoteOptionsSeeded = () => { },
                RetainedUi = null,
                Warn = _warnings.Add,
            });

    /// <summary>
    /// What the windowed client does about a teleport when nothing is drawn.
    /// The real sink is the drawn world's; every call is reported rather than
    /// faked, so a scenario that depends on one is visible as a warning
    /// instead of quietly passing.
    /// </summary>
    private sealed class UndrawnTeleportPresentation(Action<string> report)
        : AcDream.App.Streaming.ILocalPlayerTeleportNetworkSink
    {
        public void OnTeleportStarted(uint sequence) =>
            report($"teleport {sequence}: nothing is drawn");

        public void OfferDestination(
            AcDream.Runtime.RuntimeTeleportDestination destination,
            bool teleportTimestampAdvanced) =>
            report("teleport destination: nothing is drawn");

        public void OnLocalPlayerFirstEntryCompleted() =>
            report("first entry: nothing is drawn");

        public void ArmLoginTunnel() =>
            report("login tunnel: nothing is drawn");

        public void RequestLogout() => report("logout: nothing is drawn");

        public bool TryRequestLogout()
        {
            report("logout: nothing is drawn");
            return false;
        }

        public void ResetSession() => report("session reset: nothing is drawn");

        public void ResetGenerationPresentation() =>
            report("generation reset: nothing is drawn");
    }

    /// <summary>
    /// The window drives the surface's own bookkeeping off the plugin event
    /// tick rather than off the session tick, so the arm does the same.
    /// </summary>
    protected override void OnAdvanced(double hostDeltaSeconds)
    {
        // The window ticks the attack owner from its gameplay input frame
        // once a frame; the plugin tick is paced by the runtime's clock off
        // however long the frame took, which is what the window does.
        Runtime.ActionOwner.CombatAttack.Tick();
        _pluginTick.Feed(hostDeltaSeconds);
    }

    internal override AcDream.Runtime.Chat.IPluginCommandBus Commands =>
        _commands;

    /// <summary>
    /// The windowed client's own outbound command route, over the same
    /// bindings its session factory builds -- the chat route, the client
    /// commands and every other send it offers. The two things a window
    /// lends its client commands are absent here, as they are in a run with
    /// no window, so those commands answer that they need one.
    /// </summary>
    protected override ILiveSessionCommandRouting CreateCommandRoute(
        WorldSession session) =>
        _commands.Attach(new LiveSessionCommandRouter(
            LiveSessionCommandBindingFactory.Create(
                Runtime,
                session,
                RuntimeClientCommandBindings.Build(Runtime, session))));

    /// <summary>
    /// The windowed client's own frame host, built from the real classes:
    /// its camera, its player-mode state, its chase-camera slot, its input
    /// source over a real detached dispatcher, the runtime's movement owner
    /// and the shared outbound owner. Two things it asks for are the drawn
    /// world's and are stood in for here -- what the world says about the
    /// player's body (which drawable, out of sight, clock running) and the
    /// projection of the result onto that drawable -- because nothing is
    /// drawn. Both are reported, and the first is the difference the entity
    /// projection work has to close.
    /// </summary>
    protected override RuntimeLocalPlayerFrameController
        CreateFrameController()
    {
        var camera = new CameraController(new OrbitCamera(), new FlyCamera());
        var mode = new AcDream.App.Input.LocalPlayerModeState
        {
            IsPlayerMode = true,
        };
        var chase = new AcDream.App.Input.ChaseCameraInputState
        {
            Legacy = new ChaseCamera(),
        };
        var input = new DispatcherMovementInputSource(Runtime.MovementOwner);
        _dispatcher = InputDispatcher.CreateDetached(
            new SilentKeyboard(),
            new SilentMouse(),
            new KeyBindings());
        input.Bind(_dispatcher);
        var frameRuntime = new AcDream.App.Input.LiveLocalPlayerFrameRuntime(
            camera,
            mode,
            Runtime.MovementOwner,
            chase,
            input,
            new RuntimeDirectoryWorldFacts(Runtime),
            new AcDream.App.Input.LocalPlayerIdentityState(
                Runtime.PlayerIdentity),
            new AcDream.App.Input.LocalPlayerPhysicsHostSlot(),
            new AcDream.App.Input.LocalPlayerProjectionController(
                new UndrawnProjectionRuntime()),
            new LocalPlayerOutboundController(
                static (_, _, _, _, _, _) => { }),
            _sessionSource);
        // What this client hangs off the character's own locomotion: the
        // same one cycle advance, with the poses of its parts built along the
        // way, and the taking of the points that advance reached. Nothing is
        // drawn here, so the pose work ends at the poses that advance hands
        // back, which is as far as this path goes without a graphics card;
        // the points, though, are taken exactly where the drawing client
        // takes them, because what is done with them decides whether the
        // character's next movement is allowed to begin.
        Runtime.LocalPlayerMotion.BindPresentation(
            new RuntimeLocalPlayerMotionPresentation(
                AdvanceRootAndBuildPoses,
                TakeReachedPoints));
        return Runtime.CreateLocalPlayerFrameController(frameRuntime, input);
    }

    /// <summary>
    /// One step of the character's cycle, poses and all, the way the client
    /// with a window takes it.
    /// </summary>
    private void AdvanceRootAndBuildPoses(
        float deltaSeconds,
        AcDream.Core.Physics.Motion.MotionDeltaFrame output)
    {
        output.Reset();
        if (Runtime.EntityObjects.Physics.EntityRemoteAnimation(
                Runtime.PlayerIdentity.ServerGuid)
            is not { Sequencer: { } sequencer } animation)
        {
            return;
        }
        if (sequencer.MotionDoneTarget is null)
        {
            sequencer.MotionDoneTarget =
                Runtime.MovementOwner.Controller!.Motion.MotionDone;
        }
        DatReaderWriter.Types.Frame root = animation.RootMotionScratch;
        root.Origin = System.Numerics.Vector3.Zero;
        root.Orientation = System.Numerics.Quaternion.Identity;
        LastPartPoses = sequencer.Advance(deltaSeconds, root);
        output.Origin = root.Origin;
        output.Orientation = root.Orientation;
    }

    /// <summary>
    /// The points the step's cycle reached, taken the way the client with a
    /// window takes them: in the order they were reached, reporting each
    /// cycle that ended to the character's own motion state.
    /// </summary>
    private void TakeReachedPoints()
    {
        if (Runtime.EntityObjects.Physics.EntityRemoteAnimation(
                Runtime.PlayerIdentity.ServerGuid)
            is not { Sequencer: { } sequencer })
        {
            return;
        }
        IReadOnlyList<DatReaderWriter.Types.AnimationHook> reached =
            sequencer.ConsumePendingHooks();
        for (int i = 0; i < reached.Count; i++)
        {
            if (reached[i] is DatReaderWriter.Types.AnimationDoneHook)
                sequencer.Manager.AnimationDone(success: true);
        }
    }

    /// <summary>
    /// The poses the last step left. Nothing looks at them here; they exist
    /// so that the pose work this client really does is really done.
    /// </summary>
    internal IReadOnlyList<AcDream.Core.Physics.PartTransform>? LastPartPoses
    {
        get;
        private set;
    }

    /// <summary>
    /// What the world says about the player's body when nothing is drawing
    /// it. The windowed client asks its drawn-entity owner these three
    /// questions and the windowless one asks the entity directory; until that
    /// is one owner, an arm with no window has to read the directory.
    /// </summary>
    private sealed class RuntimeDirectoryWorldFacts(GameRuntime runtime)
        : AcDream.App.Input.ILocalPlayerWorldFacts
    {
        public uint ResolveLocalEntityId(uint serverGuid) =>
            serverGuid != 0u
            && runtime.EntityObjects.Entities.TryGetActive(
                serverGuid,
                out var record)
                ? record.LocalEntityId ?? 0u
                : 0u;

        public bool IsHidden(uint serverGuid) =>
            serverGuid != 0u
            && runtime.EntityObjects.Entities.TryGetActive(
                serverGuid,
                out var record)
            && (record.FinalPhysicsState
                & AcDream.Core.Physics.PhysicsStateFlags.Hidden) != 0;

        public AcDream.Core.Physics.RetailObjectClockDisposition
            GetRootObjectClockDisposition(uint serverGuid)
        {
            if (serverGuid == 0u
                || !runtime.EntityObjects.Entities.TryGetActive(
                    serverGuid,
                    out var record)
                || record.FullCellId == 0u
                || (record.FinalPhysicsState
                    & (AcDream.Core.Physics.PhysicsStateFlags.Frozen
                        | AcDream.Core.Physics.PhysicsStateFlags.Static)) != 0)
            {
                return AcDream.Core.Physics.RetailObjectClockDisposition
                    .Suspend;
            }
            return AcDream.Core.Physics.RetailObjectClockDisposition.Advance;
        }
    }

    /// <summary>There is no drawable to move, so the projection has nothing to do.</summary>
    private sealed class UndrawnProjectionRuntime
        : AcDream.App.Input.ILocalPlayerProjectionRuntime
    {
        public AcDream.Core.World.WorldEntity? ResolveEntity() => null;
        public int LiveCenterX => 0;
        public int LiveCenterY => 0;
        public void SyncShadow(
            AcDream.Core.World.WorldEntity entity, uint cellId)
        {
        }

        public void Rebucket(uint serverGuid, uint landblockId)
        {
        }

        public bool IsCurrentVisibleProjection(
            AcDream.Core.World.WorldEntity entity) => false;

        public void SuspendShadow(AcDream.Core.World.WorldEntity entity)
        {
        }
    }

    /// <summary>A keyboard nobody is typing on.</summary>
    private sealed class SilentKeyboard : IKeyboardSource
    {
#pragma warning disable CS0067
        public event Action<Key, ModifierMask>? KeyDown;
        public event Action<Key, ModifierMask>? KeyUp;
#pragma warning restore CS0067
        public bool IsHeld(Key key) => false;
        public ModifierMask CurrentModifiers => ModifierMask.None;
    }

    /// <summary>A mouse nobody is holding.</summary>
    private sealed class SilentMouse : IMouseSource
    {
#pragma warning disable CS0067
        public event Action<MouseButton, ModifierMask>? MouseDown;
        public event Action<MouseButton, ModifierMask>? MouseUp;
        public event Action<float, float>? MouseMove;
        public event Action<float>? Scroll;
#pragma warning restore CS0067
        public bool WantCaptureKeyboard { get; set; }
        public bool WantCaptureMouse { get; set; }
        public bool IsHeld(MouseButton button) => false;
    }


    protected override void DisposeHost()
    {
        for (int index = _bindings.Count - 1; index >= 0; index--)
            _bindings[index].Dispose();
        _worldEntities.Dispose();
        _automation.Dispose();
        _sessionCommands.Dispose();
        _dispatcher?.Dispose();
    }

    /// <summary>
    /// The take-down of what was drawn for an object, after the deletion
    /// controller has unregistered it. Nothing is drawn here, so no object
    /// ever has a drawn record to take down -- a call would mean one did, and
    /// is reported -- and an object without one owns no drawn effects to
    /// forget.
    /// </summary>
    private sealed class UndrawnTeardown(Action<string> report)
        : AcDream.App.World.ILiveEntityTeardownCoordinator
    {
        public void TearDown(AcDream.App.World.LiveEntityRecord record) =>
            report($"teardown 0x{record.ServerGuid:X8}: nothing is drawn");

        public void ForgetUnknownOwner(uint serverGuid)
        {
        }
    }

    /// <summary>
    /// Nothing is listening for "the player asked for this stance": there is
    /// no auto-wield controller on either arm.
    /// </summary>
    private sealed class UnwatchedCombatModeIntent : IExplicitCombatModeIntentSink
    {
        public void NotifyExplicitCombatModeRequest()
        {
        }
    }
}
