using System.Net;
using AcDream.Core.Net;
using AcDream.Runtime.Session;

namespace AcDream.Runtime.Tests.Session;

public sealed class LiveSessionLifecycleHostTests
{
    [Fact]
    public void HostRoutesLifecycleAndReleasesOnlyTheExactBoundSession()
    {
        var calls = new List<string>();
        using var sessionA = CreateSession(9000);
        using var sessionB = CreateSession(9001);
        var host = CreateHost(calls);

        LiveSessionBinding binding = host.BindSession(sessionA);
        host.ResetSessionState(RuntimeGenerationToken.Initial);
        host.ReportConnecting("host", 9000, "user");
        host.ReportConnected();
        host.ReportRoster(new LiveSessionRosterReport("account", 11, []));
        var selection = new LiveSessionCharacterSelection(2, 3u, "toon", "account");
        host.ApplySelectedCharacter(selection);
        binding.ActivateCommands();
        host.ApplyEnteredWorld(selection);

        Assert.Throws<InvalidOperationException>(() => host.DetachSession(sessionB));
        binding.Dispose();
        host.DetachSession(sessionA);
        LiveSessionBinding replacement = host.BindSession(sessionB);

        Assert.Equal(
            [
                "bind", "reset", "connecting:host:9000:user",
                "connected", "roster:account", "selected:toon", "activate",
                "entered:toon", "deactivate", "detach-events", "bind",
            ],
            calls);
        replacement.Dispose();
        host.DetachSession(sessionB);
    }

    [Fact]
    public void CharacterCreatedAndCreationFailed_ForwardToTheOptionalBindings()
    {
        var calls = new List<string>();
        var host = new LiveSessionLifecycleHost(new LiveSessionLifecycleBindings(
            Bind: session => CreateBinding(session, calls),
            Reset: _ => { },
            Connecting: (_, _, _) => { },
            Connected: () => { },
            Roster: _ => { },
            Selected: _ => { },
            Entered: _ => { },
            CharacterCreated: identity => calls.Add($"created:{identity.Guid:X8}:{identity.Name}"),
            CreationFailed: rejection => calls.Add($"failed:{rejection.Reason}")));

        host.ApplyCharacterCreated(new RuntimeCharacterCreationIdentity(0x50000001u, "Toon"));
        host.ApplyCreationFailed(new RuntimeCharacterCreationRejection(
            3u,
            AcDream.Core.Net.Messages.CharGenVerificationResponse.Code.NameInUse,
            "NameInUse",
            "Toon"));

        Assert.Equal(["created:50000001:Toon", "failed:NameInUse"], calls);
    }

    [Fact]
    public void CharacterCreatedAndCreationFailed_DefaultToNoOp_WhenBindingsOmitThem()
    {
        var calls = new List<string>();
        var host = new LiveSessionLifecycleHost(new LiveSessionLifecycleBindings(
            Bind: session => CreateBinding(session, calls),
            Reset: _ => { },
            Connecting: (_, _, _) => { },
            Connected: () => { },
            Roster: _ => { },
            Selected: _ => { },
            Entered: _ => { }));

        host.ApplyCharacterCreated(new RuntimeCharacterCreationIdentity(1u, "Toon"));
        host.ApplyCreationFailed(new RuntimeCharacterCreationRejection(
            3u,
            AcDream.Core.Net.Messages.CharGenVerificationResponse.Code.NameInUse,
            "NameInUse",
            "Toon"));

        Assert.Empty(calls);
    }

    [Fact]
    public void FailedBindingFactoryDoesNotClaimTheHost()
    {
        var calls = new List<string>();
        using var session = CreateSession(9000);
        bool fail = true;
        LiveSessionLifecycleHost host = CreateHost(calls, _ =>
        {
            if (fail)
            {
                fail = false;
                throw new InvalidOperationException("bind failure");
            }
            return CreateBinding(session, calls);
        });

        Assert.Throws<InvalidOperationException>(() => host.BindSession(session));
        LiveSessionBinding retry = host.BindSession(session);

        retry.Dispose();
        host.DetachSession(session);
    }

    private static LiveSessionLifecycleHost CreateHost(
        List<string> calls,
        Func<WorldSession, LiveSessionBinding>? bind = null) =>
        new(new LiveSessionLifecycleBindings(
            Bind: bind ?? (session => CreateBinding(session, calls)),
            Reset: _ => calls.Add("reset"),
            Connecting: (host, port, user) =>
                calls.Add($"connecting:{host}:{port}:{user}"),
            Connected: () => calls.Add("connected"),
            Roster: roster => calls.Add($"roster:{roster.AccountName}"),
            Selected: selection => calls.Add($"selected:{selection.CharacterName}"),
            Entered: selection => calls.Add($"entered:{selection.CharacterName}")));

    private static LiveSessionBinding CreateBinding(
        WorldSession session,
        List<string> calls)
    {
        calls.Add("bind");
        return new LiveSessionBinding(
            session,
            activateCommands: () => calls.Add("activate"),
            deactivateCommands: () => calls.Add("deactivate"),
            detachEvents: () => calls.Add("detach-events"));
    }

    private static WorldSession CreateSession(int port) =>
        new(
            new IPEndPoint(IPAddress.Loopback, port),
            new TestTransport());

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
