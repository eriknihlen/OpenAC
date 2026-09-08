using System.Net;
using AcDream.App.Rendering;
using AcDream.Core.Net;
using AcDream.Core.Net.Messages;
using AcDream.Runtime;
using AcDream.Runtime.Session;

namespace AcDream.App.Tests.Net;

public sealed class LiveSessionShutdownIntegrationTests
{
    [Fact]
    public void ReentrantShutdownRetainsDependentsUntilRuntimeDisposeCompletes()
    {
        var operations = new TestOperations();
        var host = new TestHost();
        var controller = new LiveSessionController(operations);
        controller.Start(
            new LiveSessionConnectOptions(
                true,
                "127.0.0.1",
                9000,
                "user",
                "password"),
            host);
        bool dependentDisposed = false;
        var shutdown = new ResourceShutdownTransaction(
            new ResourceShutdownStage("session lifetime",
            [
                new("live session", () =>
                {
                    controller.Dispose();
                    if (!controller.IsDisposalComplete)
                    {
                        throw new InvalidOperationException(
                            "live-session disposal is still deferred");
                    }
                }),
            ]),
            new ResourceShutdownStage("session dependents",
            [
                new("streamer", () => dependentDisposed = true),
            ]));
        operations.OnTick = () =>
        {
            Assert.Throws<AggregateException>(shutdown.CompleteOrThrow);
            Assert.Equal(0, shutdown.CurrentStage);
            Assert.False(dependentDisposed);
        };

        controller.Tick();

        Assert.True(controller.IsDisposalComplete);
        Assert.False(dependentDisposed);
        shutdown.CompleteOrThrow();
        Assert.True(shutdown.IsComplete);
        Assert.True(dependentDisposed);
    }

    private sealed class TestOperations : ILiveSessionOperations
    {
        public Action? OnTick { get; set; }

        public IPEndPoint ResolveEndpoint(string host, int port) =>
            new(IPAddress.Loopback, port);

        public WorldSession CreateSession(IPEndPoint endpoint) =>
            new(endpoint, new TestTransport());

        public void Connect(WorldSession session, string user, string password) { }

        public CharacterList.Parsed GetCharacters(WorldSession session) =>
            new(
                0u,
                [new CharacterList.Character(0x50000001u, "Ready", 0u)],
                [],
                11,
                "Runtime",
                true,
                true);

        public void EnterWorld(WorldSession session, int activeCharacterIndex) { }
        public void Tick(WorldSession session) => OnTick?.Invoke();
        public void DisposeSession(WorldSession session) => session.Dispose();
    }

    private sealed class TestHost : ILiveSessionLifecycleHost
    {
        public LiveSessionBinding BindSession(WorldSession session) =>
            new(
                session,
                activateCommands: static () => { },
                deactivateCommands: static () => { },
                detachEvents: static () => { });

        public void ResetSessionState(
            RuntimeGenerationToken retiringGeneration) { }
        public void ReportConnecting(string host, int port, string user) { }
        public void ReportConnected() { }
        public void ReportRoster(LiveSessionRosterReport roster) { }
        public void ApplySelectedCharacter(LiveSessionCharacterSelection selection) { }
        public void ApplyEnteredWorld(LiveSessionCharacterSelection selection) { }
        public void DetachSession(WorldSession session) { }
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
