using AcDream.Core.Net;
using AcDream.Core.Net.Messages;
using System.Net;
using Xunit;

namespace AcDream.Core.Net.Tests;

public sealed class WorldSessionLinkStatusTests
{
    [Theory]
    [InlineData(WorldSession.State.Disconnected)]
    [InlineData(WorldSession.State.Failed)]
    public void DisconnectedStates_DoNotPublishPacketAge(WorldSession.State state)
    {
        LinkStatusSnapshot snapshot = WorldSession.BuildLinkStatus(
            state, lastInboundPacketTicks: 100, nowTicks: 900, frequency: 100);

        Assert.False(snapshot.Connected);
        Assert.Equal(0d, snapshot.SecondsSinceLastPacket);
    }

    [Theory]
    [InlineData(WorldSession.State.Handshaking)]
    [InlineData(WorldSession.State.InCharacterSelect)]
    [InlineData(WorldSession.State.EnteringWorld)]
    [InlineData(WorldSession.State.InWorld)]
    public void ConnectedStates_PublishMonotonicPacketAge(WorldSession.State state)
    {
        LinkStatusSnapshot snapshot = WorldSession.BuildLinkStatus(
            state, lastInboundPacketTicks: 100, nowTicks: 850, frequency: 100);

        Assert.True(snapshot.Connected);
        Assert.Equal(7.5d, snapshot.SecondsSinceLastPacket);
    }

    [Fact]
    public void ClockRegression_ClampsPacketAgeToZero()
    {
        LinkStatusSnapshot snapshot = WorldSession.BuildLinkStatus(
            WorldSession.State.InWorld,
            lastInboundPacketTicks: 200,
            nowTicks: 100,
            frequency: 100);

        Assert.True(snapshot.Connected);
        Assert.Equal(0d, snapshot.SecondsSinceLastPacket);
    }

    [Fact]
    public void ConnectedSnapshot_CarriesPacketLossAndRoundTripTelemetry()
    {
        LinkStatusSnapshot snapshot = WorldSession.BuildLinkStatus(
            WorldSession.State.InWorld,
            lastInboundPacketTicks: 100,
            nowTicks: 200,
            frequency: 100,
            roundTripSeconds: 0.125d,
            packetLossPercentage: 2.5d);

        Assert.Equal(2.5d, snapshot.PacketLossPercentage);
        Assert.Equal(0.125d, snapshot.RoundTripSeconds);
    }

    [Fact]
    public void RequestAndEmptyPingResponse_MeasureMonotonicRoundTrip()
    {
        using var session = new WorldSession(
            new IPEndPoint(IPAddress.Loopback, 65000));
        byte[]? action = null;
        session.GameActionCapture = bytes => action = bytes;

        session.RequestLinkStatusPing();
        session.GameEvents.Dispatch(new GameEventEnvelope(
            PlayerGuid: 0u,
            Sequence: 1u,
            EventType: GameEventType.PingResponse,
            Payload: ReadOnlyMemory<byte>.Empty));

        Assert.NotNull(action);
        Assert.Equal(12, action.Length);
        Assert.NotNull(session.PingRoundTripSeconds);
        Assert.True(session.PingRoundTripSeconds >= 0d);
    }
}
