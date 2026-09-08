using System.Net;
using AcDream.Core.Net;
using AcDream.Core.Net.Messages;
using AcDream.Runtime.Session;

namespace AcDream.Runtime.Tests.Session;

public sealed class RuntimeLiveSessionNoWindowTests
{
    [Fact]
    public void RuntimeOnlyHostEntersAndTearsDownWithoutPresentationAssemblies()
    {
        var calls = new List<string>();
        var operations = new TestOperations(calls);
        using var controller = new LiveSessionController(operations);
        var host = new LiveSessionHost(
            controller,
            new LiveSessionHostBindings(
                new LiveSessionRoutingFactories(
                    _ => new EventRoute(calls),
                    _ => new CommandRoute(calls)),
                _ => calls.Add("reset"),
                new LiveSessionSelectionBindings(
                    id => calls.Add($"player:{id}"),
                    _ => { },
                    _ => { },
                    _ => { },
                    _ => { },
                    () => { }),
                new LiveSessionEnteredWorldBindings(
                    name => calls.Add($"entered:{name}"),
                    () => { },
                    () => { },
                    _ => { },
                    () => { }),
                (_, _, _) => calls.Add("connecting"),
                () => calls.Add("connected"),
                _ => calls.Add("roster"),
                selection => calls.Add($"character-entered:{selection.CharacterId}")),
            new LiveSessionConnectOptions(
                true,
                "127.0.0.1",
                9000,
                "test",
                "password"));

        RuntimeSessionStartResult start =
            host.Start(RuntimeGenerationToken.Initial);
        RuntimeTeardownAcknowledgement stop = host.Stop(start.Generation);

        Assert.Equal(RuntimeSessionStartStatus.Connected, start.Status);
        Assert.Equal(0x50000001u, start.CharacterId);
        Assert.True(stop.IsComplete);
        Assert.Null(controller.CurrentSession);
        Assert.False(controller.IsInWorld);
        Assert.Equal(
            [
                "reset",
                "resolve",
                "create",
                "attach-events",
                "connecting",
                "connect",
                "connected",
                "characters",
                "roster",
                "player:1342177281",
                "enter:0",
                "activate-commands",
                "entered:Runtime",
                "character-entered:1342177281",
                "deactivate-commands",
                "detach-events",
                "dispose-session",
                "reset",
            ],
            calls);

        string[] loaded = AppDomain.CurrentDomain.GetAssemblies()
            .Select(static assembly => assembly.GetName().Name ?? string.Empty)
            .ToArray();
        Assert.DoesNotContain(loaded, static name =>
            name.StartsWith("AcDream.App", StringComparison.OrdinalIgnoreCase)
            || name.StartsWith("AcDream.UI.", StringComparison.OrdinalIgnoreCase)
            || name.StartsWith("Silk.NET", StringComparison.OrdinalIgnoreCase)
            || name.StartsWith("OpenAL", StringComparison.OrdinalIgnoreCase)
            || name.StartsWith("Arch", StringComparison.OrdinalIgnoreCase)
            || name.StartsWith("ImGui", StringComparison.OrdinalIgnoreCase));
    }

    private sealed class TestOperations(List<string> calls)
        : ILiveSessionOperations
    {
        public IPEndPoint ResolveEndpoint(string host, int port)
        {
            calls.Add("resolve");
            return new IPEndPoint(IPAddress.Loopback, port);
        }

        public WorldSession CreateSession(IPEndPoint endpoint)
        {
            calls.Add("create");
            return new WorldSession(endpoint, new TestTransport());
        }

        public void Connect(WorldSession session, string user, string password) =>
            calls.Add("connect");

        public CharacterList.Parsed GetCharacters(WorldSession session)
        {
            calls.Add("characters");
            return new CharacterList.Parsed(
                0u,
                [new CharacterList.Character(0x50000001u, "Runtime", 0u)],
                [],
                11,
                "NoWindow",
                true,
                true);
        }

        public void EnterWorld(WorldSession session, int activeCharacterIndex) =>
            calls.Add($"enter:{activeCharacterIndex}");

        public void Tick(WorldSession session) { }

        public void DisposeSession(WorldSession session)
        {
            calls.Add("dispose-session");
            session.Dispose();
        }
    }

    private sealed class EventRoute(List<string> calls)
        : ILiveSessionEventRouting
    {
        public void Attach() => calls.Add("attach-events");
        public void Dispose() => calls.Add("detach-events");
    }

    private sealed class CommandRoute(List<string> calls)
        : ILiveSessionCommandRouting
    {
        public void Activate() => calls.Add("activate-commands");
        public void Dispose() => calls.Add("deactivate-commands");
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
