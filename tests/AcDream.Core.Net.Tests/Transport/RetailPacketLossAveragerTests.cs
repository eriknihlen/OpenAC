using AcDream.Core.Net.Transport;

namespace AcDream.Core.Net.Tests.Transport;

public sealed class RetailPacketLossAveragerTests
{
    [Fact]
    public void Sweep_BeforeRetailHeartbeat_DoesNotPublishPartialSample()
    {
        long now = 0;
        var clock = new TransportClock(() => now, frequency: 10);
        var stats = new TransportStats { PacketsSent = 5, NakIdsSent = 1 };
        var sut = new RetailPacketLossAverager(clock, stats);

        stats.PacketsSent += 10;
        stats.NakIdsSent += 2;
        now = 19;
        sut.Sweep(now, stats);

        Assert.Equal(0d, sut.Percentage);
    }

    [Fact]
    public void Sweep_UsesRetailNakPlusRetransmitOverReceivedPlusSentFormula()
    {
        long now = 0;
        var clock = new TransportClock(() => now, frequency: 10);
        var stats = new TransportStats();
        var sut = new RetailPacketLossAverager(clock, stats);

        stats.PacketsSent = 60;
        stats.PacketsReceived = 40;
        stats.NakIdsSent = 2;
        stats.ResendsSent = 3;
        now = 20;
        sut.Sweep(now, stats);

        Assert.Equal(5d, sut.Percentage);
    }

    [Fact]
    public void Sweep_EvictsTheOldestSampleAfterFortyHeartbeats()
    {
        long now = 0;
        var clock = new TransportClock(() => now, frequency: 10);
        var stats = new TransportStats();
        var sut = new RetailPacketLossAverager(clock, stats);

        stats.PacketsSent = 10;
        stats.NakIdsSent = 10;
        now = 20;
        sut.Sweep(now, stats);

        for (int i = 0; i < RetailPacketLossAverager.WindowSize; i++)
        {
            stats.PacketsSent += 10;
            now += 20;
            sut.Sweep(now, stats);
        }

        Assert.Equal(0d, sut.Percentage);
    }

    [Fact]
    public void Sweep_ZeroTrafficReportsZero()
    {
        long now = 0;
        var clock = new TransportClock(() => now, frequency: 10);
        var stats = new TransportStats();
        var sut = new RetailPacketLossAverager(clock, stats);

        now = 20;
        sut.Sweep(now, stats);

        Assert.Equal(0d, sut.Percentage);
    }
}
