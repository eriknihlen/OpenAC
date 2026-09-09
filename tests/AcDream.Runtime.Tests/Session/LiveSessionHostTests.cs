using System.Net;
using AcDream.Core.Net;
using AcDream.Core.Net.Messages;
using AcDream.Runtime.Chat;
using AcDream.Runtime.Session;

namespace AcDream.Runtime.Tests.Session;

public sealed class LiveSessionHostTests
{
    [Fact]
    public void HostOwnsRouteSelectionAndEntryOrderingWithoutMirroringSession()
    {
        var calls = new List<string>();
        var operations = new TestOperations(calls);
        var controller = new LiveSessionController(operations);
        var events = new TestEventRouting(calls);
        var commands = new TestCommandRouting(calls);
        LiveSessionHost host = CreateHost(
            controller,
            calls,
            _ =>
            {
                calls.Add("events");
                return events;
            },
            _ =>
            {
                calls.Add("commands");
                return commands;
            });

        LiveSessionStartResult result = host.Start(LiveOptions());

        Assert.Equal(LiveSessionStartStatus.Connected, result.Status);
        Assert.Equal(
            [
                "reset", "resolve", "create", "events", "attach-events", "commands",
                "connecting", "connect", "connected", "roster:Canonical",
                "player:1342177282", "vitals:1342177282",
                "chat:1342177282", "persistent:1342177282",
                "vanish:1342177282", "clear-combat", "enter:1",
                "activate", "active:Ready", "restore-layout",
                "sync-toolbar", "load-settings:Ready", "arm-auto-entry",
                "character-entered:1342177282",
            ],
            calls);
        Assert.Same(controller.CurrentSession, host.CurrentSession);
        Assert.True(host.IsInWorld);

        controller.Dispose();
    }

    [Fact]
    public void DisabledAndMissingCredentialStartsExecuteTheCanonicalResetPlan()
    {
        var calls = new List<string>();
        var operations = new TestOperations(calls);
        var controller = new LiveSessionController(operations);
        LiveSessionHost host = CreateHost(
            controller,
            calls,
            _ => throw new InvalidOperationException("must not bind"),
            _ => throw new InvalidOperationException("must not bind"));

        Assert.Equal(
            LiveSessionStartStatus.Disabled,
            host.Start(LiveOptions(live: false)).Status);
        Assert.Equal(
            LiveSessionStartStatus.MissingCredentials,
            host.Start(LiveOptions(user: null)).Status);

        Assert.Equal(["reset", "reset"], calls);
        Assert.Null(host.CurrentSession);
        Assert.False(host.IsInWorld);
    }

    [Fact]
    public void LoginSequenceStartsAfterTheEnteredWorldObservation()
    {
        var calls = new List<string>();
        var operations = new TestOperations(calls);
        var controller = new LiveSessionController(operations);
        var bus = new LiveCommandBus();
        bus.Register<SendChatCmd>(command => calls.Add($"login:{command.Text}"));
        var loginCommands = new LoginCommandSequence(
            ["ready"],
            TimeSpan.Zero,
            new TestFeedback(),
            bus);
        LiveSessionHost host = CreateHost(
            controller,
            calls,
            _ => new TestEventRouting(calls),
            _ => new TestCommandRouting(calls),
            loginCommands);

        Assert.Equal(
            LiveSessionStartStatus.Connected,
            host.Start(LiveOptions()).Status);

        int entered = calls.IndexOf("character-entered:1342177282");
        int login = calls.IndexOf("login:ready");
        Assert.True(entered >= 0);
        Assert.Equal(entered + 1, login);
        controller.Dispose();
    }

    [Fact]
    public void CommandFactoryFailureRollsBackTheAlreadyAttachedEventRoute()
    {
        var calls = new List<string>();
        var operations = new TestOperations(calls);
        var controller = new LiveSessionController(operations);
        var events = new TestEventRouting(calls);
        LiveSessionHost host = CreateHost(
            controller,
            calls,
            _ => events,
            _ => throw new InvalidOperationException("command failure"));

        LiveSessionStartResult result = host.Start(LiveOptions());

        Assert.Equal(LiveSessionStartStatus.Failed, result.Status);
        Assert.IsType<InvalidOperationException>(result.Error);
        Assert.Equal(1, events.DisposeCount);
        Assert.Contains("dispose-events", calls);
        Assert.Null(host.CurrentSession);
    }

    [Fact]
    public void AttachFailureRetainsThePublishedRouteUntilRollbackConverges()
    {
        var calls = new List<string>();
        var operations = new TestOperations(calls);
        var controller = new LiveSessionController(operations);
        var firstEvents = new TestEventRouting(calls)
        {
            AttachFailuresRemaining = 1,
            FailuresRemaining = 1,
        };
        int eventFactoryCount = 0;
        int commandFactoryCount = 0;
        LiveSessionHost host = CreateHost(
            controller,
            calls,
            _ => eventFactoryCount++ == 0
                ? firstEvents
                : new TestEventRouting(calls),
            _ =>
            {
                commandFactoryCount++;
                return new TestCommandRouting(calls);
            });

        LiveSessionStartResult failed = host.Start(LiveOptions());

        Assert.Equal(LiveSessionStartStatus.Failed, failed.Status);
        Assert.Equal(1, firstEvents.AttachCount);
        Assert.Equal(2, firstEvents.DisposeCount);
        Assert.Equal(0, commandFactoryCount);

        LiveSessionStartResult retry = host.Start(LiveOptions());

        Assert.Equal(LiveSessionStartStatus.Connected, retry.Status);
        Assert.Equal(2, eventFactoryCount);
        Assert.Equal(1, commandFactoryCount);
        controller.Dispose();
    }

    [Fact]
    public void TransientRollbackFailureIsReportedRetriedAndConvergesBeforeNextStart()
    {
        var calls = new List<string>();
        var operations = new TestOperations(calls);
        var controller = new LiveSessionController(operations);
        var firstEvents = new TestEventRouting(calls) { FailuresRemaining = 1 };
        int eventFactoryCount = 0;
        bool failCommands = true;
        LiveSessionHost host = CreateHost(
            controller,
            calls,
            _ => eventFactoryCount++ == 0
                ? firstEvents
                : new TestEventRouting(calls),
            _ =>
            {
                if (failCommands)
                {
                    failCommands = false;
                    throw new InvalidOperationException("command failure");
                }
                return new TestCommandRouting(calls);
            });

        LiveSessionStartResult failed = host.Start(LiveOptions());

        AggregateException error = Assert.IsType<AggregateException>(failed.Error);
        Assert.Equal(2, error.InnerExceptions.Count);
        Assert.Contains(error.InnerExceptions, e => e.Message == "command failure");
        Assert.Contains(error.InnerExceptions, e => e.Message == "event cleanup failure");
        Assert.Equal(2, firstEvents.DisposeCount);

        LiveSessionStartResult retry = host.Start(LiveOptions());

        Assert.Equal(LiveSessionStartStatus.Connected, retry.Status);
        Assert.Equal(2, eventFactoryCount);
        Assert.Equal(2, operations.Sessions.Count);
        controller.Dispose();
    }

    [Fact]
    public void PersistentRollbackFailureBlocksEveryLaterGeneration()
    {
        var calls = new List<string>();
        var operations = new TestOperations(calls);
        var controller = new LiveSessionController(operations);
        var events = new TestEventRouting(calls)
        {
            FailuresRemaining = int.MaxValue,
        };
        int eventFactoryCount = 0;
        int commandFactoryCount = 0;
        LiveSessionHost host = CreateHost(
            controller,
            calls,
            _ =>
            {
                eventFactoryCount++;
                return events;
            },
            _ =>
            {
                commandFactoryCount++;
                throw new InvalidOperationException("command failure");
            });

        LiveSessionStartResult first = host.Start(LiveOptions());
        LiveSessionStartResult second = host.Start(LiveOptions());

        Assert.Equal(LiveSessionStartStatus.Failed, first.Status);
        Assert.Equal(LiveSessionStartStatus.Failed, second.Status);
        Assert.Equal(3, events.DisposeCount);
        Assert.Equal(1, eventFactoryCount);
        Assert.Equal(1, commandFactoryCount);
        Assert.Single(operations.Sessions);
        Assert.Null(host.CurrentSession);
    }

    private static LiveSessionHost CreateHost(
        LiveSessionController controller,
        List<string> calls,
        Func<WorldSession, ILiveSessionEventRouting> createEvents,
        Func<WorldSession, ILiveSessionCommandRouting> createCommands,
        LoginCommandSequence? loginCommands = null) =>
        new(controller, new LiveSessionHostBindings(
            Routing: new(createEvents, createCommands),
            Reset: _ => calls.Add("reset"),
            Selection: new(
                id => calls.Add($"player:{id}"),
                id => calls.Add($"vitals:{id}"),
                id => calls.Add($"chat:{id}"),
                id => calls.Add($"persistent:{id}"),
                id => calls.Add($"vanish:{id}"),
                () => calls.Add("clear-combat")),
            EnteredWorld: new(
                name => calls.Add($"active:{name}"),
                () => calls.Add("restore-layout"),
                () => calls.Add("sync-toolbar"),
                name => calls.Add($"load-settings:{name}"),
                () => calls.Add("arm-auto-entry")),
            Connecting: (_, _, _) => calls.Add("connecting"),
            Connected: () => calls.Add("connected"),
            Roster: roster => calls.Add($"roster:{roster.AccountName}"),
            CharacterEntered: selection =>
                calls.Add($"character-entered:{selection.CharacterId}"),
            LoginCommands: loginCommands));

    private static LiveSessionConnectOptions LiveOptions(
        bool live = true,
        string? user = "user") =>
        new(
            live,
            "127.0.0.1",
            9000,
            user ?? string.Empty,
            "password");

    private sealed class TestEventRouting(List<string> calls)
        : ILiveSessionEventRouting
    {
        public int FailuresRemaining { get; set; }
        public int AttachFailuresRemaining { get; set; }
        public int AttachCount { get; private set; }
        public int DisposeCount { get; private set; }

        public void Attach()
        {
            AttachCount++;
            calls.Add("attach-events");
            if (AttachFailuresRemaining > 0)
            {
                AttachFailuresRemaining--;
                throw new InvalidOperationException("event attach failure");
            }
        }

        public void Dispose()
        {
            DisposeCount++;
            calls.Add("dispose-events");
            if (FailuresRemaining > 0)
            {
                FailuresRemaining--;
                throw new InvalidOperationException("event cleanup failure");
            }
        }
    }

    private sealed class TestCommandRouting(List<string> calls)
        : ILiveSessionCommandRouting
    {
        public void Activate() => calls.Add("activate");
        public void Publish<T>(T command) where T : notnull { }
        public void Dispose() => calls.Add("dispose-commands");
    }

    private sealed class TestFeedback : IChatCommandFeedback
    {
        public string? LastIncomingTellSender => null;
        public string? LastOutgoingTellTarget => null;
        public void ShowInterfaceText(string text) { }
        public void ShowSystemMessage(string text) { }
    }

    private sealed class TestOperations(List<string> calls) : ILiveSessionOperations
    {
        public List<WorldSession> Sessions { get; } = [];

        public IPEndPoint ResolveEndpoint(string host, int port)
        {
            calls.Add("resolve");
            return new IPEndPoint(IPAddress.Loopback, port);
        }

        public WorldSession CreateSession(IPEndPoint endpoint)
        {
            calls.Add("create");
            var session = new WorldSession(endpoint, new TestTransport());
            Sessions.Add(session);
            return session;
        }

        public void Connect(WorldSession session, string user, string password) =>
            calls.Add("connect");

        public CharacterList.Parsed? GetCharacters(WorldSession session) => new(
            0u,
            [
                new CharacterList.Character(0x50000001u, "Grey", 10u),
                new CharacterList.Character(0x50000002u, "Ready", 0u),
            ],
            [],
            11,
            "Canonical",
            true,
            true);

        public void EnterWorld(WorldSession session, int activeCharacterIndex) =>
            calls.Add($"enter:{activeCharacterIndex}");

        public void Tick(WorldSession session) { }

        public void DisposeSession(WorldSession session)
        {
            calls.Add("dispose-session");
            session.Dispose();
        }
    }

    private sealed class TestTransport : IWorldSessionTransport
    {
        public void Send(ReadOnlySpan<byte> datagram) { }
        public void Send(IPEndPoint remote, ReadOnlySpan<byte> datagram) { }

        public int Receive(
            Span<byte> destination,
            TimeSpan timeout,
            out IPEndPoint? from)
        {
            from = null;
            return -1;
        }

        public ValueTask<NetReceiveResult> ReceiveAsync(
            Memory<byte> destination,
            CancellationToken cancellationToken) =>
            throw new OperationCanceledException(cancellationToken);

        public void Dispose() { }
    }
}
