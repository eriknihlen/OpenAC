using AcDream.Core.Net;
using AcDream.Core.Net.Messages;
using AcDream.Core.Net.Packets;
using System.Diagnostics;
using System.Threading.Channels;

namespace AcDream.Core.Net.Tests;

public sealed class WorldSessionShutdownTests
{
    [Theory]
    [InlineData(WorldSession.State.Disconnected)]
    [InlineData(WorldSession.State.Handshaking)]
    public void PreNegotiationShutdown_OnlyReleasesLocalTransport(
        WorldSession.State state)
    {
        WorldSession.SessionShutdownPlan plan = WorldSession.BuildShutdownPlan(
            state,
            transportNegotiated: false,
            activeCharacterId: 0);

        Assert.False(plan.RequestCharacterLogOff);
        Assert.False(plan.SendTransportDisconnect);
    }

    [Theory]
    [InlineData(WorldSession.State.Handshaking)]
    [InlineData(WorldSession.State.InCharacterSelect)]
    [InlineData(WorldSession.State.EnteringWorld)]
    [InlineData(WorldSession.State.Failed)]
    [InlineData(WorldSession.State.Disconnected)]
    public void EveryNegotiatedNonWorldState_SendsTransportDisconnectOnly(
        WorldSession.State state)
    {
        WorldSession.SessionShutdownPlan plan = WorldSession.BuildShutdownPlan(
            state,
            transportNegotiated: true,
            activeCharacterId: 0x50000001u);

        Assert.False(plan.RequestCharacterLogOff);
        Assert.True(plan.SendTransportDisconnect);
    }

    [Fact]
    public void NegotiatedInWorldState_RequestsCharacterLogoffThenDisconnect()
    {
        WorldSession.SessionShutdownPlan plan = WorldSession.BuildShutdownPlan(
            WorldSession.State.InWorld,
            transportNegotiated: true,
            activeCharacterId: 0x50000001u);

        Assert.True(plan.RequestCharacterLogOff);
        Assert.True(plan.SendTransportDisconnect);
    }

    [Fact]
    public void InWorldWithoutActiveCharacter_DoesNotSendMalformedF653Request()
    {
        WorldSession.SessionShutdownPlan plan = WorldSession.BuildShutdownPlan(
            WorldSession.State.InWorld,
            transportNegotiated: true,
            activeCharacterId: 0);

        Assert.False(plan.RequestCharacterLogOff);
        Assert.True(plan.SendTransportDisconnect);
    }

    [Fact]
    public void ImpossibleUnnegotiatedInWorldState_DoesNotAttemptWireMessages()
    {
        WorldSession.SessionShutdownPlan plan = WorldSession.BuildShutdownPlan(
            WorldSession.State.InWorld,
            transportNegotiated: false,
            activeCharacterId: 0x50000001u);

        Assert.False(plan.RequestCharacterLogOff);
        Assert.False(plan.SendTransportDisconnect);
    }

    [Fact]
    public void ExecuteShutdownWire_SendsExactRequestWaitThenNegotiatedDisconnect()
    {
        var order = new List<string>();
        byte[]? request = null;
        byte[]? disconnect = null;
        WorldSession.SessionShutdownPlan plan = WorldSession.BuildShutdownPlan(
            WorldSession.State.InWorld,
            transportNegotiated: true,
            activeCharacterId: 0x5000000Au);

        WorldSession.ShutdownExecutionResult result = WorldSession.ExecuteShutdownWire(
            plan,
            activeCharacterId: 0x5000000Au,
            sessionClientId: 0x1234,
            sessionIteration: 7,
            sendGameMessage: body =>
            {
                order.Add("F653");
                request = body;
            },
            waitForConfirmation: timeout =>
            {
                order.Add("wait");
                Assert.Equal(TimeSpan.FromSeconds(35), timeout);
                return true;
            },
            sendTransportDatagram: datagram =>
            {
                order.Add("disconnect");
                disconnect = datagram;
            },
            confirmationTimeout: TimeSpan.FromSeconds(35));

        Assert.Equal(["F653", "wait", "disconnect"], order);
        Assert.Equal(CharacterLogOff.BuildRequestBody(0x5000000Au), request);
        PacketCodec.PacketDecodeResult decoded =
            PacketCodec.TryDecode(disconnect!, inboundIsaac: null);
        Assert.True(decoded.IsOk, decoded.Error.ToString());
        Assert.True(decoded.Packet!.Header.HasFlag(PacketHeaderFlags.Disconnect));
        Assert.Equal((ushort)0x1234, decoded.Packet.Header.Id);
        Assert.Equal((ushort)7, decoded.Packet.Header.Iteration);
        Assert.True(result.CharacterLogOffSent);
        Assert.True(result.ConfirmationReceived);
        Assert.True(result.TransportDisconnectSent);
        Assert.Null(result.CharacterLogOffError);
        Assert.Null(result.TransportDisconnectError);
    }

    [Fact]
    public void ExecuteShutdownWire_RequestFailureStillSendsDisconnect()
    {
        var order = new List<string>();
        WorldSession.SessionShutdownPlan plan = WorldSession.BuildShutdownPlan(
            WorldSession.State.InWorld,
            transportNegotiated: true,
            activeCharacterId: 0x50000001u);

        WorldSession.ShutdownExecutionResult result = WorldSession.ExecuteShutdownWire(
            plan,
            activeCharacterId: 0x50000001u,
            sessionClientId: 1,
            sessionIteration: 2,
            sendGameMessage: _ =>
            {
                order.Add("F653");
                throw new IOException("send failed");
            },
            waitForConfirmation: _ =>
            {
                order.Add("wait");
                return true;
            },
            sendTransportDatagram: _ => order.Add("disconnect"),
            confirmationTimeout: TimeSpan.FromSeconds(1));

        Assert.Equal(["F653", "disconnect"], order);
        Assert.False(result.CharacterLogOffSent);
        Assert.False(result.ConfirmationReceived);
        Assert.True(result.TransportDisconnectSent);
        Assert.IsType<IOException>(result.CharacterLogOffError);
    }

    [Fact]
    public void WaitForConfirmation_CompletedEmptyChannelReturnsWithoutSpinning()
    {
        Channel<byte[]> channel = Channel.CreateUnbounded<byte[]>();
        channel.Writer.TryComplete();
        var stopwatch = Stopwatch.StartNew();

        bool confirmed = WorldSession.WaitForCharacterLogOffConfirmation(
            channel.Reader,
            TimeSpan.FromSeconds(2),
            _ => false);

        Assert.False(confirmed);
        Assert.True(stopwatch.Elapsed < TimeSpan.FromSeconds(1));
    }

    [Fact]
    public void WaitForConfirmation_IgnoresMalformedBodiesUntilExactOpcodeOnlyBody()
    {
        Channel<byte[]> channel = Channel.CreateUnbounded<byte[]>();
        channel.Writer.TryWrite([0x53, 0xF6, 0x00]);
        channel.Writer.TryWrite(BitConverter.GetBytes(0xF654u));
        channel.Writer.TryWrite(CharacterLogOff.BuildRequestBody(0x50000001u));
        channel.Writer.TryWrite(BitConverter.GetBytes(CharacterLogOff.Opcode));
        channel.Writer.TryComplete();
        int processed = 0;

        bool confirmed = WorldSession.WaitForCharacterLogOffConfirmation(
            channel.Reader,
            TimeSpan.FromSeconds(1),
            body =>
            {
                processed++;
                return CharacterLogOff.IsConfirmation(body);
            });

        Assert.True(confirmed);
        Assert.Equal(4, processed);
    }

    [Fact]
    public void WaitForConfirmation_TimeoutWinsDuringContinuousUnrelatedDrain()
    {
        Channel<byte[]> channel = Channel.CreateUnbounded<byte[]>();
        for (int i = 0; i < 100; i++)
            channel.Writer.TryWrite(BitConverter.GetBytes(0xF654u));
        int processed = 0;
        var stopwatch = Stopwatch.StartNew();

        bool confirmed = WorldSession.WaitForCharacterLogOffConfirmation(
            channel.Reader,
            TimeSpan.FromMilliseconds(25),
            _ =>
            {
                processed++;
                Thread.Sleep(2);
                return false;
            });

        Assert.False(confirmed);
        Assert.True(processed < 100);
        Assert.True(stopwatch.Elapsed < TimeSpan.FromSeconds(1));
    }
}
