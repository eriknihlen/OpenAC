using AcDream.Core.Net;
using AcDream.Headless.Hosting;
using AcDream.Runtime.Chat;
using AcDream.Headless.Plugins;
using AcDream.Plugin.Abstractions;
using AcDream.Runtime;
using AcDream.Runtime.Gameplay;
using AcDream.Runtime.Session;

namespace AcDream.HostParity.Tests;

/// <summary>
/// The windowless client, built the way a bot session builds it: the real
/// gameplay operations bound to the runtime, the real plugin host, and the
/// same shared binding pass over the record the host really makes.
///
/// What is NOT under this arm, and why:
/// * installed content, so the spell catalog, the skill names and the walk
///   controller are absent, which is what a content-less bot run looks like;
/// * the item automation and the logout automation, whose wiring belongs to
///   the session host rather than to the plugin host;
/// * the session's own scheduler, whose catch-up behaviour is a difference in
///   its own right and not one a scenario should smuggle in: the arm steps at
///   the same fixed rate as the other.
/// </summary>
internal sealed class WindowlessArm : ParityArm
{
    private readonly HeadlessGameplayOperations _gameplay;
    private readonly HeadlessPluginHost _host;

    /// <summary>
    /// The runtime clock this client paces its plugin tick from, built the
    /// way its session host builds it.
    /// </summary>
    private readonly AcDream.Runtime.Plugins.RuntimePluginTickClock _pluginTick;
    private readonly RecordingPluginLogger _log = new();
    private readonly AcDream.Runtime.Session.SessionStatusWriter _status =
        new(path: null);

    /// <summary>
    /// Where a typed line goes on this client, exactly as its session host
    /// hangs it: the shared chat command route behind the surface the host
    /// hands its console and its plugins.
    /// </summary>
    private readonly LiveChatCommandSurface _chatCommands;
    private DirectGameRuntimeCommandAdapter _commands = null!;

    internal WindowlessArm()
        : this(new HeadlessGameplayOperations())
    {
    }

    private WindowlessArm(HeadlessGameplayOperations gameplay)
        : base(
            ParityHost.Windowless,
            operations =>
            HeadlessAutomationCapabilities.BuildRuntimeDependencies(
                gameplay,
                TimeProvider.System,
                static _ => { },
                sessionOperations: operations))
    {
        _gameplay = gameplay;
        // The bus asks this client's plugin host what its plugins make of a
        // typed line and which verbs it answers, exactly as the session host
        // hangs it. The verb lookup is resolved when a line arrives rather
        // than captured now, because the host below does not exist yet.
        _chatCommands = new LiveChatCommandSurface(
            line => _host?.TryHandlePluginCommand(line) == true,
            line => _host!.InterceptChatInput(line));
        _gameplay.Bind(Runtime, catalog: null, accountName: () => "parity");
        // The windowless client's own command adapter, the one its session
        // host really builds, over this arm's session.
        _commands = new DirectGameRuntimeCommandAdapter(
            Runtime,
            new ParitySessionCommands(Session));
        _host = new HeadlessPluginHost(
            Runtime,
            _log,
            // Real storage on disk, as a bot session is given, under this
            // arm's own scratch folder.
            storage: new AcDream.Core.Plugins.FilePluginStorage(
                Path.Combine(DataDirectory, "plugin-storage")),
            vtankProfiles: new AcDream.Core.Plugins.FilePluginStorage(
                Path.Combine(DataDirectory, "plugin-profiles")),
            // Where a line a plugin submits, or one another client on this
            // machine broadcast, goes in: this client's own chat entry, over
            // its own route, exactly as its session host hands it.
            submitChatText: SubmitChatText,
            sessionCommands: _commands,
            peerDirectory: PeerDirectory,
            pluginTags: ConfiguredPluginTags,
            sessionSettings: ConfiguredPluginSettings);
        _pluginTick = new AcDream.Runtime.Plugins.RuntimePluginTickClock(
            _host.FireTick,
            () => Runtime.Generation.Value);
    }

    /// <summary>
    /// Submitting a line the way this client's session host does: through
    /// the shared router, over this client's own chat command surface, with
    /// its own feedback.
    /// </summary>
    private bool SubmitChatText(string text) =>
        ChatCommandRouter.Submit(
            text,
            new RuntimeChatCommandFeedback(Runtime.CommunicationOwner),
            _chatCommands,
            ChatChannelKind.Say)
        is not (SubmitOutcome.Empty
            or SubmitOutcome.UnknownCommand
            or SubmitOutcome.Dropped);

    internal override IPluginHost Host => _host;

    internal override AcDream.Runtime.Chat.IPluginCommandBus Commands =>
        _chatCommands;

    internal override IReadOnlyList<string> Warnings => _log.Lines;

    /// <summary>Where this client's session host raises it.</summary>
    internal override void ShowConfirmation(PluginConfirmation confirmation) =>
        _host.RaiseConfirmationRequested(confirmation);

    /// <summary>
    /// The windowless client's own session bindings, from its own builder.
    /// Every part is real: this client's identity owner, its chat identity,
    /// its console lines and its status file. There is nothing it needs a
    /// window for.
    /// </summary>
    protected override LiveSessionHostBindings CreateSessionHostBindings(
        LiveSessionRoutingFactories routing,
        Action<RuntimeGenerationToken> reset) =>
        HeadlessAutomationCapabilities.BuildSessionHostBindings(
            new HeadlessSessionHostParts
            {
                CreateEvents = routing.CreateEvents,
                CreateCommands = routing.CreateCommands,
                Reset = reset,
                Identity = Runtime.PlayerIdentity,
                Communication = Runtime.CommunicationOwner,
                Combat = Runtime.ActionOwner.Combat,
                NoteActiveCharacter = name =>
                    _log.Info($"active character: {name}"),
                Diagnostic = _log.Info,
                StatusWriter = _status,
                SessionId = Name,
                NoteConnected = () => _log.Info("connected"),
                LoginCommands = null,
                Warn = _log.Warn,
            });

    /// <summary>
    /// The windowless client's own character bindings. A bot session with no
    /// lease on the installed data files has no skill formulas, which is the
    /// difference the census carries; everything else is this client's.
    /// </summary>
    internal override LiveCharacterSessionBindings
        CreateCharacterSessionBindings() =>
        HeadlessAutomationCapabilities.BuildCharacterSessionBindings(
            new HeadlessCharacterSessionParts
            {
                Character = Runtime.CharacterOwner,
                Combat = Runtime.ActionOwner.Combat,
                ResolveSkillFormulaBonus = null,
                ClientTime = () => Runtime.EffectClock.NowSeconds,
                OnConfirmationRequest = request =>
                    _host.RaiseConfirmationRequested(
                        new PluginConfirmation(
                            request.ContextId,
                            (int)request.Type,
                            request.Message)),
                OnConfirmationDone = _ => { },
                MovementStats = Runtime.MovementStats,
                // Neither arm starts from a session document, so nothing is
                // declared for the seeder to compare against.
                NoteOptionsSeeded = () => { },
                Warn = _log.Warn,
            });

    /// <summary>
    /// The windowless host drives the surface's bookkeeping and its plugin
    /// ticks from the session tick.
    /// </summary>
    protected override void OnAdvanced(double hostDeltaSeconds)
    {
        // The windowless session host ticks the attack owner once per
        // session tick; the plugin tick is paced by the runtime's clock off
        // however long the turn took, which is what that host does.
        Runtime.ActionOwner.CombatAttack.Tick();
        _pluginTick.Feed(hostDeltaSeconds);
    }

    /// <summary>
    /// This client takes hold of a world connection twice over: once for its
    /// gameplay operations and once for its command adapter, which answers
    /// nothing until it has been handed the connection.
    /// </summary>
    protected override ILiveSessionCommandRouting CreateCommandRoute(
        WorldSession session) =>
        new EveryRoute(
            _gameplay.CreateRoute(session),
            _commands.CreateRoute(session),
            // The third is the chat command route, which this client's
            // session host hangs beside the other two: without it a typed
            // line reaches nothing and every chat scenario compares silence.
            _chatCommands.Attach(new LiveChatCommandRoute(
                RuntimeChatCommandBindings.Create(Runtime, session))));

    private sealed class EveryRoute(
        params ILiveSessionCommandRouting[] routes)
        : ILiveSessionCommandRouting
    {
        public void Activate()
        {
            foreach (ILiveSessionCommandRouting route in routes)
                route.Activate();
        }

        public void Dispose()
        {
            for (int index = routes.Length - 1; index >= 0; index--)
                routes[index].Dispose();
        }
    }

    /// <summary>
    /// The windowless client's own frame host, unchanged: it reads the
    /// character's body and the world's facts about it off the runtime, and
    /// sends position through the shared outbound owner.
    /// </summary>
    protected override RuntimeLocalPlayerFrameController
        CreateFrameController() =>
        Runtime.CreateLocalPlayerFrameController(
            new HeadlessLocalPlayerFrameHost(Runtime, Session),
            new HeadlessMovementInputSource(Runtime.MovementOwner));

    protected override void DisposeHost() => _host.Dispose();

}
