using System.Net;
using System.Numerics;
using AcDream.Core.Combat;
using AcDream.Core.Items;
using AcDream.Core.Net;
using AcDream.Core.Net.Messages;
using AcDream.Core.Physics;
using AcDream.Core.Spells;
using AcDream.Plugin.Abstractions;
using AcDream.Runtime.Gameplay;
using AcDream.Runtime.Navigation;
using AcDream.Runtime.Session;

namespace AcDream.Runtime.Tests.Navigation;

// One walk at a time, and it belongs to whoever asked: a plugin, or the player. Another
// plugin is held rather than taking the character; the player always wins; a plugin
// that goes away takes its walk and pauses with it.
public sealed class RuntimeNavigationOwnershipTests
{
    private const uint Goal = 0x50000123u;

    [Fact]
    public void ASecondPluginIsHeldWhileAnothersWalkIsUnderWay()
    {
        using var h = new Harness();
        INavigationAutomation first = h.Navigation.ScopeTo("first.plugin");
        INavigationAutomation second = h.Navigation.ScopeTo("second.plugin");

        Assert.Equal(PluginNavigationCommandStatus.Accepted, first.GoTo(Goal, 2f));
        Assert.Equal("first.plugin", h.Navigation.WalkOwner);
        Assert.Equal("first.plugin", second.GoToReport.Owner);

        Assert.Equal(PluginNavigationCommandStatus.Held, second.GoTo(Goal, 2f));
        Assert.Equal(PluginNavigationCommandStatus.Held, second.StandOn(Goal, 2f));
        Assert.Equal(PluginNavigationCommandStatus.Held, second.Follow(Goal, 3f));
        Assert.Equal(PluginNavigationCommandStatus.Held, second.StopGoTo());
        Assert.Equal("first.plugin", h.Navigation.WalkOwner);

        Assert.Equal(PluginNavigationCommandStatus.Accepted, first.StopGoTo());
        Assert.Null(h.Navigation.WalkOwner);
        Assert.Equal(PluginNavigationCommandStatus.Accepted, second.GoTo(Goal, 2f));
        Assert.Equal("second.plugin", h.Navigation.WalkOwner);
    }

    [Fact]
    public void ThePlayerReplacesAndStopsAnyPluginsWalk()
    {
        using var h = new Harness();
        INavigationAutomation plugin = h.Navigation.ScopeTo("some.plugin");

        Assert.Equal(PluginNavigationCommandStatus.Accepted, plugin.GoTo(Goal, 2f));
        Assert.Equal(PluginNavigationCommandStatus.Accepted, h.Navigation.GoTo(Goal, 2f));
        Assert.Equal(RuntimeNavigationAutomation.PlayerOwner, h.Navigation.WalkOwner);
        Assert.Equal(RuntimeNavigationAutomation.PlayerOwner, plugin.GoToReport.Owner);

        Assert.Equal(PluginNavigationCommandStatus.Held, plugin.GoTo(Goal, 2f));
        Assert.Equal(PluginNavigationCommandStatus.Held, plugin.StopGoTo());
        Assert.Equal(PluginNavigationCommandStatus.Accepted, h.Navigation.StopGoTo());
        Assert.Null(h.Navigation.WalkOwner);
    }

    [Fact]
    public void APluginKeepsItsOwnWalkAndMayReplaceIt()
    {
        using var h = new Harness();
        INavigationAutomation plugin = h.Navigation.ScopeTo("some.plugin");

        Assert.Equal(PluginNavigationCommandStatus.Accepted, plugin.GoTo(Goal, 2f));
        Assert.Equal(PluginNavigationCommandStatus.Accepted, plugin.StandOn(Goal, 2f));
        Assert.Equal("some.plugin", h.Navigation.WalkOwner);
        Assert.Equal(PluginNavigationCommandStatus.Accepted, plugin.StopGoTo());
        Assert.Equal(PluginNavigationCommandStatus.Rejected, plugin.StopGoTo());
    }

    [Fact]
    public void ReleasingAPluginStopsItsWalkAndDropsItsPausesButNotAnothers()
    {
        using var h = new Harness();
        INavigationAutomation going = h.Navigation.ScopeTo("going.plugin");
        INavigationAutomation staying = h.Navigation.ScopeTo("staying.plugin");
        Assert.Equal(PluginNavigationCommandStatus.Accepted, going.GoTo(Goal, 2f));
        IDisposable goingPause = going.PauseGoToWhile(() => "the going plugin is fighting");
        IDisposable stayingPause = staying.PauseGoToWhile(() => "the staying plugin is looting");
        Assert.Equal("the going plugin is fighting", h.Navigation.PauseReason());

        h.Navigation.Release("going.plugin");

        Assert.Null(h.Navigation.WalkOwner);
        Assert.Null(going.GoToReport.Owner);
        Assert.Equal("the staying plugin is looting", h.Navigation.PauseReason());
        goingPause.Dispose();
        stayingPause.Dispose();
        Assert.Null(h.Navigation.PauseReason());
        Assert.Equal(PluginNavigationCommandStatus.Accepted, staying.GoTo(Goal, 2f));
    }

    /// <summary>
    /// The snapshot handlers a plugin added go with it; another plugin's stay.
    /// Mutation (2026-09-26): passing the handler straight to the shared
    /// event, as before, kept calling the released plugin.
    /// </summary>
    [Fact]
    public void ReleasingAPluginRemovesItsSnapshotHandlersButNotAnothers()
    {
        using var h = new Harness();
        INavigationAutomation going = h.Navigation.ScopeTo("going.plugin");
        INavigationAutomation staying = h.Navigation.ScopeTo("staying.plugin");
        int goingCalls = 0;
        int stayingCalls = 0;
        going.SnapshotChanged += _ => goingCalls++;
        staying.SnapshotChanged += _ => stayingCalls++;

        h.Navigation.Release("going.plugin");
        h.Navigation.PublishSnapshotChanged();

        Assert.Equal(0, goingCalls);
        Assert.Equal(1, stayingCalls);
    }

    [Fact]
    public void ReleasingAPluginThatOwnsNothingChangesNothing()
    {
        using var h = new Harness();
        INavigationAutomation owner = h.Navigation.ScopeTo("owner.plugin");
        Assert.Equal(PluginNavigationCommandStatus.Accepted, owner.GoTo(Goal, 2f));

        h.Navigation.Release("bystander.plugin");

        Assert.Equal("owner.plugin", h.Navigation.WalkOwner);
    }

    [Fact]
    public void ArgumentsAreStillCheckedBeforeOwnership()
    {
        using var h = new Harness();
        INavigationAutomation first = h.Navigation.ScopeTo("first.plugin");
        INavigationAutomation second = h.Navigation.ScopeTo("second.plugin");
        Assert.Equal(PluginNavigationCommandStatus.Accepted, first.GoTo(Goal, 2f));

        Assert.Equal(PluginNavigationCommandStatus.Rejected, second.GoTo(0u, 2f));
        Assert.Equal(PluginNavigationCommandStatus.Rejected, second.GoTo(Goal, 0f));
    }

    [Fact]
    public void AWalkThatReachedTheControllerAnyOtherWayIsThePlayers()
    {
        using var h = new Harness();
        INavigationAutomation plugin = h.Navigation.ScopeTo("some.plugin");
        Assert.Equal(PluginNavigationCommandStatus.Accepted, plugin.GoTo(Goal, 2f));

        // A route preview from chat goes to the controller without passing through the
        // navigation API; it is the player's, and the plugin's stale record does not outrank it.
        _ = h.Walk.RouteTo(Goal, 2f);

        Assert.Equal(RuntimeNavigationAutomation.PlayerOwner, h.Navigation.WalkOwner);
        Assert.Equal(RuntimeNavigationAutomation.PlayerOwner, plugin.GoToReport.Owner);
        Assert.Equal(PluginNavigationCommandStatus.Held, plugin.GoTo(Goal, 2f));
        Assert.Equal(PluginNavigationCommandStatus.Held, plugin.StopGoTo());
        Assert.Equal(PluginNavigationCommandStatus.Accepted, h.Navigation.StopGoTo());
    }

    [Fact]
    public void AWalkThatEndedByItselfLeavesNoOwner()
    {
        using var h = new Harness();
        INavigationAutomation first = h.Navigation.ScopeTo("first.plugin");
        INavigationAutomation second = h.Navigation.ScopeTo("second.plugin");
        Assert.Equal(PluginNavigationCommandStatus.Accepted, first.GoTo(Goal, 2f));

        // With no body to sample the walk ends on its first frame.
        h.Walk.Tick(0.016d);

        Assert.Null(h.Navigation.WalkOwner);
        Assert.Null(first.GoToReport.Owner);
        Assert.Equal(PluginNavigationCommandStatus.Rejected, second.StopGoTo());
        Assert.Equal(PluginNavigationCommandStatus.Accepted, second.GoTo(Goal, 2f));
        Assert.Equal("second.plugin", h.Navigation.WalkOwner);
    }

    [Fact]
    public void APluginsViewForwardsEveryMemberItself()
    {
        using var h = new Harness();
        Type view = h.Navigation.ScopeTo("some.plugin").GetType();
        System.Reflection.InterfaceMapping map = view.GetInterfaceMap(typeof(INavigationAutomation));

        // A member left to the interface default would silently do nothing for the plugin.
        foreach (System.Reflection.MethodInfo target in map.TargetMethods)
            Assert.Equal(view, target.DeclaringType);
    }

    [Fact]
    public void ThroughTheScopedHostAPluginOwnsItsWalkAndLosesItWhenTheHostGoes()
    {
        using var h = new Harness();
        var scoped = new AcDream.Core.Plugins.ScopedPluginHost(new HostOver(h.Navigation), "example.plugin", "Example");
        INavigationAutomation navigation = scoped.Automation.Navigation;

        Assert.Equal(PluginNavigationCommandStatus.Accepted, navigation.GoTo(Goal, 2f));
        Assert.Equal("example.plugin", h.Navigation.WalkOwner);
        Assert.Equal("example.plugin", navigation.GoToReport.Owner);
        IDisposable pause = navigation.PauseGoToWhile(() => "the example plugin is busy");
        Assert.Equal("the example plugin is busy", h.Navigation.PauseReason());

        scoped.Dispose();

        Assert.Null(h.Navigation.WalkOwner);
        Assert.Null(h.Navigation.PauseReason());
        // After the plugin is gone, its host hands it nothing it could drive with.
        Assert.Equal(PluginNavigationCommandStatus.Unavailable, scoped.Automation.Navigation.GoTo(Goal, 2f));
        Assert.Null(h.Navigation.WalkOwner);
        pause.Dispose();
    }

    [Fact]
    public async Task ScopedPluginHostPreviewDoesNotChangeWalkOwnership()
    {
        using var h = new Harness();
        using var scoped = new AcDream.Core.Plugins.ScopedPluginHost(
            new HostOver(h.Navigation), "example.plugin", "Example");
        IPluginHost pluginHost = scoped;
        INavigationAutomation navigation = pluginHost.Automation.Navigation;

        Assert.Equal(PluginNavigationCommandStatus.Accepted, navigation.GoTo(Goal, 2f));
        long sequence = navigation.GoToReport.Sequence;
        PluginNavigationPlan plan = await navigation.PreviewPathAsync(Goal, 2f);

        Assert.Equal(PluginNavigationPlanStatus.NoRoute, plan.Status);
        Assert.Equal("example.plugin", h.Navigation.WalkOwner);
        Assert.Equal(sequence, navigation.GoToReport.Sequence);
    }

    private sealed class HostOver(INavigationAutomation navigation) : IPluginHost
    {
        public bool HasUi => false;
        public IPluginLogger Log { get; } = new SilentLogger();
        public IGameState State { get; } = new EmptyGameState();
        public IEvents Events { get; } = new AcDream.Core.Plugins.WorldEvents();
        public ISelectionService Selection { get; } = new InertSelection();
        public IUiRegistry Ui => NoOpUiRegistry.Instance;
        public IPluginStorage Storage => NoOpPluginStorage.Instance;
        public IPluginStorage VtankProfiles => NoOpPluginStorage.Instance;
        public IPluginCommandRegistry Commands => NoOpPluginCommandRegistry.Instance;
        public IAutomationSurface Automation { get; } = new Surface(navigation);

        private sealed class Surface(INavigationAutomation navigation) : IAutomationSurface
        {
            public bool IsAvailable => true;
            public ICharacterInfo Character => NoOpAutomationSurface.Instance.Character;
            public ISpellCatalog Spells => NoOpAutomationSurface.Instance.Spells;
            public IMagicCommands Magic => NoOpAutomationSurface.Instance.Magic;
            public IPluginChat Chat => NoOpAutomationSurface.Instance.Chat;
            public INavigationAutomation Navigation => navigation;
        }

        private sealed class SilentLogger : IPluginLogger
        {
            public void Info(string message) { }
            public void Warn(string message) { }
            public void Error(string message, Exception? error = null) { }
        }

        private sealed class EmptyGameState : IGameState
        {
            public IReadOnlyList<WorldEntitySnapshot> Entities { get; } = [];
        }

        private sealed class InertSelection : ISelectionService
        {
            public uint? SelectedObjectId => null;
            public uint? PreviousObjectId => null;
            public event Action<SelectionChangedEvent>? Changed;
            public bool Select(uint objectId)
            {
                Changed?.Invoke(default);
                return false;
            }
            public bool Clear() => false;
        }
    }

    private sealed class Harness : IDisposable
    {
        internal readonly RuntimeNavigationAutomation Navigation;
        internal readonly NavigationWalkController Walk;
        private readonly GameRuntime _runtime;

        internal Harness()
        {
            var gameplay = new InertOperations();
            _runtime = new GameRuntime(new GameRuntimeDependencies(
                gameplay,
                gameplay,
                gameplay,
                gameplay,
                SessionOperations: new RealSessionOperations()));
            var session = new LiveSessionHost(
                _runtime.Session,
                new LiveSessionHostBindings(
                    new LiveSessionRoutingFactories(
                        _ => new NoOpEventRoute(),
                        _ => new NoOpCommandRoute()),
                    _ => { },
                    new LiveSessionSelectionBindings(
                        id => _runtime.PlayerIdentity.ServerGuid = id,
                        _ => { },
                        _ => { },
                        _ => { },
                        _ => { },
                        () => { }),
                    new LiveSessionEnteredWorldBindings(
                        _ => { },
                        () => { },
                        () => { },
                        _ => { },
                        () => { }),
                    (_, _, _) => { },
                    () => { },
                    _ => { },
                    _ => { }),
                new LiveSessionConnectOptions(
                    true,
                    "127.0.0.1",
                    9000,
                    "user",
                    "password"),
                runtime: _runtime);
            var commands = new DirectGameRuntimeCommandAdapter(_runtime, session);
            _ = commands.Start(_runtime.Generation);
            Assert.True(_runtime.Session.IsInWorld);

            Navigation = new RuntimeNavigationAutomation();
            Navigation.Bind(_runtime);
            Walk = new NavigationWalkController(new PhysicsEngine(), new NoWalkBody(), new NoWalkGoals());
            Navigation.BindWalk(Walk);
        }

        public void Dispose() => _runtime.Dispose();
    }

    private sealed class NoWalkBody : INavigationWalkBody
    {
        public bool TrySample(out NavigationWalkBodySample sample)
        {
            sample = default;
            return false;
        }

        public bool BeginMove(in RuntimeMoveRequest request) => false;
        public bool StopMove(RuntimeMoveChannel channel) => false;
    }

    private sealed class NoWalkGoals : INavigationGoalSource
    {
        public bool TryLocate(uint objectId, out Vector3 position)
        {
            position = default;
            return false;
        }
    }

    private sealed class RealSessionOperations : ILiveSessionOperations
    {
        public IPEndPoint ResolveEndpoint(string host, int port) => new(IPAddress.Loopback, port);
        public WorldSession CreateSession(IPEndPoint endpoint)
        {
            var session = new WorldSession(endpoint, new NoOpTransport());
            // Never negotiated: a client that has arrived in the world sends
            // -- it asks the server about the allegiance -- and a reliable
            // send here has no cipher to go out under, so it is taken.
            session.GameMessageCapture = (_, _) => { };
            return session;
        }
        public void Connect(WorldSession session, string user, string password) { }
        public CharacterList.Parsed GetCharacters(WorldSession session) =>
            new(0u, [new CharacterList.Character(0x50000001u, "Fixture", 0u)], [], 11, "account", true, true);
        public void EnterWorld(WorldSession session, int activeCharacterIndex) { }
        public void Tick(WorldSession session) { }
        public void DisposeSession(WorldSession session) => session.Dispose();
    }

    private sealed class NoOpTransport : IWorldSessionTransport
    {
        public void Send(ReadOnlySpan<byte> datagram) { }
        public void Send(IPEndPoint remote, ReadOnlySpan<byte> datagram) { }
        public int Receive(Span<byte> destination, TimeSpan timeout, out IPEndPoint? from)
        {
            from = null;
            return -1;
        }
        public ValueTask<NetReceiveResult> ReceiveAsync(Memory<byte> destination, CancellationToken cancellationToken) =>
            throw new OperationCanceledException(cancellationToken);
        public void Dispose() { }
    }

    private sealed class NoOpEventRoute : ILiveSessionEventRouting
    {
        public void Attach() { }
        public void Dispose() { }
    }

    private sealed class NoOpCommandRoute : ILiveSessionCommandRouting
    {
        public void Activate() { }
        public void Dispose() { }
    }

    private sealed class InertOperations
        : IRuntimeCombatAttackOperations,
          IRuntimeCombatTargetOperations,
          IRuntimeCombatModeOperations,
          IRuntimeSpellCastOperations
    {
        public bool CanStartAttack(bool allowAutoTarget) => false;
        public void PrepareAttackRequest() { }
        public bool SendAttack(AttackHeight height, float power, bool allowAutoTarget) => false;
        public void SendCancelAttack() { }
        public bool IsDualWield => false;
        public bool PlayerReadyForAttack => false;
        public bool AutoRepeatAttack => false;
        public bool AutoTarget => false;
        public uint? SelectClosestTarget() => null;
        public bool IsInWorld => false;
        public IReadOnlyList<ClientObject> GetOrderedEquipment() => [];
        public void NotifyExplicitCombatModeRequest() { }
        public void SendChangeCombatMode(CombatMode mode) { }
        public uint LocalPlayerId => 0u;
        public bool CanSend => false;
        public bool HasRequiredComponents(uint spellId) => false;
        public bool IsTargetCompatible(uint targetId, SpellMetadata spell, bool showMessage) => false;
        public void StopCompletely() { }
        public void SendUntargeted(uint spellId) { }
        public void SendTargeted(uint targetId, uint spellId) { }
        public void DisplayMessage(string message) { }
        public void IncrementBusy() { }
    }
}
