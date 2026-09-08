using System.Net;
using System.Net.Sockets;
using System.Reflection;
using AcDream.Core.Net;

namespace AcDream.Core.Net.Tests;

public class NetClientTests
{
    [Fact]
    public void Construction_AppliesWindowsConnResetMitigationWithoutThrowing()
    {
        using var client = new NetClient(new IPEndPoint(IPAddress.Loopback, 0));
        Assert.NotNull(client.LocalEndPoint);
    }

    [Fact]
    public void Receive_RepeatedSameTimeout_CachesLastAppliedValue()
    {
        using var client = new NetClient(new IPEndPoint(IPAddress.Loopback, 1));

        client.Receive(TimeSpan.FromMilliseconds(50), out _);
        client.Receive(TimeSpan.FromMilliseconds(50), out _);

        FieldInfo cacheField = typeof(NetClient).GetField(
            "_lastAppliedReceiveTimeoutMs", BindingFlags.NonPublic | BindingFlags.Instance)!;
        Assert.Equal(50, (int)cacheField.GetValue(client)!);

        FieldInfo udpField = typeof(NetClient).GetField(
            "_udp", BindingFlags.NonPublic | BindingFlags.Instance)!;
        var udp = (UdpClient)udpField.GetValue(client)!;
        Assert.Equal(50, udp.Client.ReceiveTimeout);
    }

    [Fact]
    public void Receive_DifferingTimeouts_UpdatesCachedValueEachTime()
    {
        using var client = new NetClient(new IPEndPoint(IPAddress.Loopback, 1));

        client.Receive(TimeSpan.FromMilliseconds(40), out _);
        client.Receive(TimeSpan.FromMilliseconds(120), out _);

        FieldInfo cacheField = typeof(NetClient).GetField(
            "_lastAppliedReceiveTimeoutMs", BindingFlags.NonPublic | BindingFlags.Instance)!;
        Assert.Equal(120, (int)cacheField.GetValue(client)!);

        FieldInfo udpField = typeof(NetClient).GetField(
            "_udp", BindingFlags.NonPublic | BindingFlags.Instance)!;
        var udp = (UdpClient)udpField.GetValue(client)!;
        Assert.Equal(120, udp.Client.ReceiveTimeout);
    }

    [Fact]
    public void SendReceive_LoopbackSelfTest_EchoRoundTrip()
    {
        // Two NetClients on loopback that send messages to each other.
        // Proves Send + Receive actually work end-to-end over real UDP
        // without depending on any remote server.
        using var serverSide = new NetClient(new IPEndPoint(IPAddress.Loopback, 0));
        using var clientSide = new NetClient(new IPEndPoint(IPAddress.Loopback, serverSide.LocalEndPoint.Port));

        byte[] outbound = { 0xDE, 0xAD, 0xBE, 0xEF };
        clientSide.Send(outbound);

        var received = serverSide.Receive(TimeSpan.FromSeconds(2), out var from);

        Assert.NotNull(received);
        Assert.Equal(outbound, received);
        Assert.NotNull(from);
        Assert.Equal(IPAddress.Loopback, from!.Address);
        Assert.Equal(clientSide.LocalEndPoint.Port, from.Port);
    }

    [Fact]
    public void Receive_Timeout_ReturnsNullAndNoException()
    {
        using var client = new NetClient(new IPEndPoint(IPAddress.Loopback, 1));
        var result = client.Receive(TimeSpan.FromMilliseconds(100), out var from);
        Assert.Null(result);
        Assert.Null(from);
    }

    [Fact]
    public async Task ReceiveAsync_LoopbackWritesIntoCallerOwnedMemory()
    {
        using var serverSide =
            new NetClient(new IPEndPoint(IPAddress.Loopback, 0));
        using var clientSide =
            new NetClient(new IPEndPoint(
                IPAddress.Loopback,
                serverSide.LocalEndPoint.Port));
        byte[] outbound = Enumerable.Range(0, 1_500)
            .Select(static value => (byte)value)
            .ToArray();
        byte[] destination = new byte[2_048];
        using var timeout =
            new CancellationTokenSource(TimeSpan.FromSeconds(2));

        clientSide.Send(outbound);
        NetReceiveResult result = await serverSide.ReceiveAsync(
            destination,
            timeout.Token);

        Assert.Equal(outbound.Length, result.Length);
        Assert.Equal(
            outbound,
            destination.AsSpan(0, result.Length).ToArray());
        Assert.Equal(
            clientSide.LocalEndPoint.Port,
            result.RemoteEndPoint.Port);
    }

    [Fact]
    public async Task ReceiveAsync_IdleCancellationStopsOutstandingReceive()
    {
        using var client =
            new NetClient(new IPEndPoint(IPAddress.Loopback, 1));
        byte[] destination = new byte[32];
        using var cancellation = new CancellationTokenSource();

        ValueTask<NetReceiveResult> receive =
            client.ReceiveAsync(destination, cancellation.Token);
        cancellation.Cancel();

        await Assert.ThrowsAnyAsync<OperationCanceledException>(
            async () => await receive);
    }
}
