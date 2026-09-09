using System.Buffers.Binary;
using System.Net;
using System.Net.Sockets;
using AcDream.Launcher.Core.Status;

namespace AcDream.Launcher.Core.Tests.Status;

public sealed class ServerHealthServiceTests
{
    [Fact]
    public void PopulationMatchesHostnameWhenDisplayNameIsAnAddress()
    {
        var counts = ServerHealthService.ParsePlayerCounts("""[{"server":"Coldeve","count":807}]""");
        Assert.Equal(807, ServerHealthService.FindPlayerCount(counts, "play.coldeve.ac", "play.coldeve.ac"));
        Assert.Equal(807, ServerHealthService.FindPlayerCount(counts, "My favourite", "play.coldeve.ac"));
        Assert.Null(ServerHealthService.FindPlayerCount(counts, "Local", "localhost"));
        Assert.Null(ServerHealthService.FindPlayerCount(counts, "Another", "notcoldeve.example"));
    }

    [Fact]
    public async Task UdpProbeUsesPasswordlessStatusIdentityAndWaitsForValidReply()
    {
        using var server = new UdpClient(new IPEndPoint(IPAddress.Loopback, 0));
        int port = ((IPEndPoint)server.Client.LocalEndPoint!).Port;
        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(5));
        Task<double?> pending = new UdpServerReachabilityProbe().ProbeAsync("127.0.0.1", port, timeout.Token);
        var request = await server.ReceiveAsync(timeout.Token);
        Assert.Equal(1u, BinaryPrimitives.ReadUInt32LittleEndian(request.Buffer.AsSpan(32)));
        Assert.Equal("acservertracker:jj9h26hcsggc", System.Text.Encoding.ASCII.GetString(request.Buffer, 46, 28));
        await server.SendAsync(new byte[] { 1, 2, 3 }, request.RemoteEndPoint, timeout.Token);
        var response = new byte[52];
        BinaryPrimitives.WriteUInt32LittleEndian(response.AsSpan(4), 0x40000);
        BinaryPrimitives.WriteUInt16LittleEndian(response.AsSpan(16), 32);
        await server.SendAsync(response, request.RemoteEndPoint, timeout.Token);
        Assert.NotNull(await pending);
    }

    [Fact]
    public async Task SharesPopulationFetchAndPreservesZeroAndMissingCounts()
    {
        var handler = new ResponseHandler();
        using var http = new HttpClient(handler);
        var service = new ServerHealthService(http, new FixedProbe());
        var snapshots = await Task.WhenAll(
            service.CheckAsync("localhost", 9000, "Empty"),
            service.CheckAsync("localhost", 9000, "Unknown"),
            service.CheckAsync("localhost", 9000, "Busy"));
        Assert.Equal(1, handler.Requests);
        Assert.Equal(0, snapshots[0].PlayerCount);
        Assert.Null(snapshots[1].PlayerCount);
        Assert.Equal(27, snapshots[2].PlayerCount);
        Assert.All(snapshots, snapshot => Assert.True(snapshot.IsReachable));
    }

    [Fact]
    public async Task FailedRefreshMarksPreviousCountStaleAndNoReplyOffline()
    {
        var time = new TestClock();
        var handler = new ResponseHandler();
        using var http = new HttpClient(handler);
        var service = new ServerHealthService(http, new FixedProbe(null), time);
        var first = await service.CheckAsync("localhost", 9000, "Busy");
        Assert.False(first.IsPlayerCountStale);
        time.Now += TimeSpan.FromMinutes(2);
        handler.Fail = true;
        var stale = await service.CheckAsync("localhost", 9000, "Busy");
        Assert.Equal(27, stale.PlayerCount);
        Assert.True(stale.IsPlayerCountStale);
        Assert.False(stale.IsReachable);
    }

    [Fact]
    public async Task CallerCancellationPropagates()
    {
        using var http = new HttpClient(new ResponseHandler());
        var service = new ServerHealthService(http, new FixedProbe());
        using var cancellation = new CancellationTokenSource();
        cancellation.Cancel();
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() =>
            service.CheckAsync("localhost", 9000, "Busy", cancellation.Token));
    }

    [Fact]
    public void IgnoresInvalidPopulationValues()
    {
        var counts = ServerHealthService.ParsePlayerCounts("""
            [{"server":"Empty","count":0},{"server":"Bad","count":-1},
             {"server":"String","count":"12"},{"server":"Overflow","count":99999999999},null]
            """);
        Assert.Single(counts);
        Assert.Equal(0, counts["empty"]);
    }

    [Fact]
    public void AcceptsChallengeAndRejectsTruncatedOrUnrelatedDatagrams()
    {
        var packet = new byte[52];
        BinaryPrimitives.WriteUInt32LittleEndian(packet.AsSpan(4), 0x40000);
        BinaryPrimitives.WriteUInt16LittleEndian(packet.AsSpan(16), 32);
        Assert.True(UdpServerReachabilityProbe.IsStatusReply(packet));
        Assert.False(UdpServerReachabilityProbe.IsStatusReply(packet.AsSpan(0, 24)));
        packet[4] = 4;
        Assert.False(UdpServerReachabilityProbe.IsStatusReply(packet));
        Assert.False(UdpServerReachabilityProbe.IsStatusReply([1, 2, 3]));
    }

    private sealed class TestClock : TimeProvider
    {
        public DateTimeOffset Now { get; set; } = DateTimeOffset.UtcNow;
        public override DateTimeOffset GetUtcNow() => Now;
    }

    private sealed class FixedProbe(double? latency = 12) : IServerReachabilityProbe
    {
        public Task<double?> ProbeAsync(string host, int port, CancellationToken cancellationToken)
            => Task.FromResult(latency);
    }

    private sealed class ResponseHandler : HttpMessageHandler
    {
        public int Requests { get; private set; }
        public bool Fail { get; set; }
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            Requests++;
            if (Fail) throw new HttpRequestException("Unavailable");
            return Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK)
            {
                Content = new StringContent("""[{"server":"Empty","count":0},{"server":"Busy","count":27}]"""),
            });
        }
    }
}
