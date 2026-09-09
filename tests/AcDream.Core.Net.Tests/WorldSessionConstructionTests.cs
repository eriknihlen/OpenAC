using System.Net;

namespace AcDream.Core.Net.Tests;

public sealed class WorldSessionConstructionTests
{
    [Fact]
    public void InvalidEndpointIsRejectedBeforeTransportAllocation()
    {
        int allocationCount = 0;
        IWorldSessionTransport Allocate(IPEndPoint _)
        {
            allocationCount++;
            return new TestTransport();
        }

        Assert.Throws<ArgumentNullException>(
            () => new WorldSession(null!, Allocate));
        Assert.Throws<ArgumentOutOfRangeException>(
            () => new WorldSession(
                new IPEndPoint(IPAddress.Loopback, ushort.MaxValue),
                Allocate));

        Assert.Equal(0, allocationCount);
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
