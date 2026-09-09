using System.Net;
using System.Net.Sockets;
using System.Reflection;
using AcDream.Core.Net.Messages;
using AcDream.Core.Net.Packets;

namespace AcDream.Core.Net.Tests;

public sealed class WorldSessionNetReceiveLoopResilienceTests
{
    [Fact]
    public async Task NetReceiveLoop_NonTimeoutSocketException_LogsAndKeepsReceiving()
    {
        var transport = new ScriptedTransport();
        var session = new WorldSession(
            new IPEndPoint(IPAddress.Loopback, 9000),
            transport);

        MethodInfo loopMethod = typeof(WorldSession).GetMethod(
            "NetReceiveLoopAsync",
            BindingFlags.NonPublic | BindingFlags.Instance)!;

        var task = (Task)loopMethod.Invoke(session, null)!;

        await task.WaitAsync(TimeSpan.FromSeconds(5));
        bool exited = task.IsCompletedSuccessfully;
        Assert.True(exited, "NetReceiveLoop did not exit after the scripted ObjectDisposedException — " +
            "the non-timeout SocketException likely killed the loop before it reached call 3.");

        Assert.Equal(3, transport.CallCount);

        int processed = session.Tick();
        Assert.Equal(1, processed);
    }

    [Fact]
    public async Task NetReceiveLoopAsync_PreservesArrivalOrder_NoReflexAcks()
    {
        var transport = new OrderedDatagramTransport(
            BuildPacket(sequence: 41, fragmentSequence: 1, "first"),
            BuildPacket(sequence: 42, fragmentSequence: 2, "second"),
            BuildPacket(sequence: 43, fragmentSequence: 3, "third"));
        var session = new WorldSession(
            new IPEndPoint(IPAddress.Loopback, 9000),
            transport);
        var messages = new List<string>();
        session.ServerMessageReceived += m => messages.Add(m.Message);
        MethodInfo loopMethod = typeof(WorldSession).GetMethod(
            "NetReceiveLoopAsync",
            BindingFlags.NonPublic | BindingFlags.Instance)!;

        var task = (Task)loopMethod.Invoke(session, null)!;
        await task.WaitAsync(TimeSpan.FromSeconds(5));

        Assert.Equal(3, session.Tick());
        Assert.Equal(["first", "second", "third"], messages);

        Assert.Empty(transport.Sent);
    }

    private static byte[] BuildPacket(
        uint sequence,
        uint fragmentSequence,
        string text)
    {
        byte[] message = BuildServerMessage(text);
        byte[] body = new byte[MessageFragmentHeader.Size + message.Length];
        int written = GameMessageFragment.WriteSingleFragment(
            body,
            fragmentSequence,
            GameMessageGroup.UIQueue,
            message);
        return PacketCodec.Encode(
            new PacketHeader
            {
                Sequence = sequence,
                Flags = PacketHeaderFlags.BlobFragments,
            },
            body.AsSpan(0, written),
            outboundIsaac: null);
    }

    private static byte[] BuildServerMessage(string text)
    {
        var writer = new PacketWriter(64 + text.Length);
        writer.WriteUInt32(ServerMessage.Opcode); // 0xF7E0
        writer.WriteString16L(text);
        writer.WriteUInt32(1);
        return writer.ToArray();
    }

    private sealed class ScriptedTransport : IWorldSessionTransport
    {
        private int _calls;

        public int CallCount => _calls;

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
            CancellationToken cancellationToken)
        {
            int n = Interlocked.Increment(ref _calls);
            switch (n)
            {
                case 1:
                    // Simulates a non-timeout SocketException, e.g. Windows'
                    // WSAECONNRESET off a stale peer's ICMP port-unreachable.
                    throw new SocketException((int)SocketError.ConnectionReset);
                case 2:
                    new byte[] { 0xAA, 0xBB, 0xCC, 0xDD }
                        .CopyTo(destination);
                    return ValueTask.FromResult(
                        new NetReceiveResult(
                            4,
                            new IPEndPoint(
                                IPAddress.Loopback,
                                9000)));
                default:
                    // Simulates NetClient having been disposed out from
                    // under the loop during shutdown — the pre-existing,
                    // still-preserved silent-exit path.
                    throw new ObjectDisposedException(nameof(ScriptedTransport));
            }
        }

        public void Dispose() { }
    }

    private sealed class OrderedDatagramTransport(
        params byte[][] datagrams) : IWorldSessionTransport
    {
        private int _next;

        public List<byte[]> Sent { get; } = [];

        public void Send(ReadOnlySpan<byte> datagram) =>
            Sent.Add(datagram.ToArray());

        public void Send(
            IPEndPoint remote,
            ReadOnlySpan<byte> datagram) =>
            Sent.Add(datagram.ToArray());

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
            CancellationToken cancellationToken)
        {
            int index = Interlocked.Increment(ref _next) - 1;
            if (index >= datagrams.Length)
                throw new ObjectDisposedException(
                    nameof(OrderedDatagramTransport));

            byte[] source = datagrams[index];
            source.CopyTo(destination);
            return ValueTask.FromResult(
                new NetReceiveResult(
                    source.Length,
                    new IPEndPoint(IPAddress.Loopback, 9000)));
        }

        public void Dispose() { }
    }
}
