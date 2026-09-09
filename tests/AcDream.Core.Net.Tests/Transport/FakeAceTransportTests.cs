using System.Buffers.Binary;
using System.Net;
using AcDream.Core.Net.Messages;
using AcDream.Core.Net.Packets;
using AcDream.Core.Net.Transport;

namespace AcDream.Core.Net.Tests.Transport;

public sealed class FakeAceTransportTests
{
    private static readonly TimeSpan HarnessPatience = TimeSpan.FromSeconds(60);

    // ---- LossyLink ----

    [Fact]
    public void LossyLink_DropNextAndDropAt_DropDeterministically()
    {
        var link = new LossyLink();
        link.DropNext(LinkDirection.ClientToServer);
        link.DropAt(LinkDirection.ClientToServer, 2);

        Assert.Empty(link.Transmit(LinkDirection.ClientToServer, new byte[] { 1 })); // index 0: DropNext
        Assert.Single(link.Transmit(LinkDirection.ClientToServer, new byte[] { 2 })); // index 1
        Assert.Empty(link.Transmit(LinkDirection.ClientToServer, new byte[] { 3 })); // index 2: DropAt
        Assert.Single(link.Transmit(LinkDirection.ClientToServer, new byte[] { 4 })); // index 3

        Assert.Equal(4, link.TransmitCount(LinkDirection.ClientToServer));
        Assert.Equal(2, link.DroppedCount(LinkDirection.ClientToServer));
        Assert.Equal(2, link.DeliveredCount(LinkDirection.ClientToServer));
        // Directions are independent.
        Assert.Equal(0, link.TransmitCount(LinkDirection.ServerToClient));
    }

    [Fact]
    public void LossyLink_PredicateDrop_IsPersistent()
    {
        var link = new LossyLink();
        link.Drop(LinkDirection.ServerToClient, (_, datagram) => datagram[0] == 0xAA);

        Assert.Empty(link.Transmit(LinkDirection.ServerToClient, new byte[] { 0xAA }));
        Assert.Single(link.Transmit(LinkDirection.ServerToClient, new byte[] { 0xBB }));
        Assert.Empty(link.Transmit(LinkDirection.ServerToClient, new byte[] { 0xAA }));
        Assert.Equal(2, link.DroppedCount(LinkDirection.ServerToClient));
    }

    [Fact]
    public void LossyLink_Reorder_SwapsAdjacentDatagrams()
    {
        var link = new LossyLink();
        link.Reorder(LinkDirection.ClientToServer);

        Assert.Empty(link.Transmit(LinkDirection.ClientToServer, new byte[] { 1 })); // held
        IReadOnlyList<byte[]> delivered =
            link.Transmit(LinkDirection.ClientToServer, new byte[] { 2 });
        Assert.Equal(2, delivered.Count);
        Assert.Equal(2, delivered[0][0]); // the follower first
        Assert.Equal(1, delivered[1][0]); // then the held one

        // A held datagram with no follower can be force-released.
        link.Reorder(LinkDirection.ClientToServer);
        Assert.Empty(link.Transmit(LinkDirection.ClientToServer, new byte[] { 3 }));
        IReadOnlyList<byte[]> drained = link.DrainHeld(LinkDirection.ClientToServer);
        Assert.Equal(3, Assert.Single(drained)[0]);
    }

    [Fact]
    public void LossyLink_SeededRandomLoss_IsDeterministic()
    {
        var first = new LossyLink();
        var second = new LossyLink();
        first.RandomLoss(LinkDirection.ClientToServer, probability: 0.5, seed: 42);
        second.RandomLoss(LinkDirection.ClientToServer, probability: 0.5, seed: 42);

        for (int i = 0; i < 100; i++)
        {
            byte[] datagram = { (byte)i };
            Assert.Equal(
                first.Transmit(LinkDirection.ClientToServer, datagram).Count,
                second.Transmit(LinkDirection.ClientToServer, datagram).Count);
        }

        // At 50% over 100 datagrams both outcomes occur.
        Assert.True(first.DroppedCount(LinkDirection.ClientToServer) > 0);
        Assert.True(first.DeliveredCount(LinkDirection.ClientToServer) > 0);
    }

    // ---- FakeAceTransport end-to-end ----

    [Fact]
    public void RealWorldSession_HandshakeEnterWorldTickAndGracefulLogout_NoSockets()
    {
        var transport = new FakeAceTransport();
        var session = new WorldSession(
            new IPEndPoint(IPAddress.Loopback, 9000),
            transport);
        try
        {
            session.Connect("testaccount", "testpassword", TimeSpan.FromSeconds(10));
            Assert.Equal(WorldSession.State.InCharacterSelect, session.CurrentState);
            Assert.NotNull(session.Characters);
            CharacterList.Character character = Assert.Single(session.Characters!.Characters);
            Assert.Equal(FakeAceTransport.DefaultCharacterName, character.Name);
            Assert.Equal(FakeAceTransport.DefaultAccountName, session.Characters.AccountName);

            var messages = new List<string>();
            session.ServerMessageReceived += m => messages.Add(m.Message);

            session.EnterWorld(0, TimeSpan.FromSeconds(10));
            Assert.Equal(WorldSession.State.InWorld, session.CurrentState);

            // A world message flows model → link → async receive loop →
            // Tick() → typed event.
            transport.Model.EnqueueGameMessage(
                BuildServerMessage("hello acdream"),
                GameMessageGroup.UIQueue);
            transport.PumpServer();
            DateTime deadline = DateTime.UtcNow.AddSeconds(10);
            while (messages.Count == 0 && DateTime.UtcNow < deadline)
            {
                session.Tick();
                Thread.Sleep(5);
            }

            Assert.Equal("hello acdream", Assert.Single(messages));

            Assert.Equal(
                new[]
                {
                    DddInterrogationResponse.Opcode,
                    0xF7EAu,
                    CharacterEnterWorld.EnterWorldRequestOpcode,
                    CharacterEnterWorld.EnterWorldOpcode,
                },
                transport.Model.DispatchedMessages.Select(ReadOpcode).ToArray());
            Assert.Equal(0, transport.Model.CrcDropCount);
            Assert.Equal(0, transport.Model.DuplicateDropCount);
            Assert.Equal(256, transport.Model.Crypto.Headroom);
        }
        finally
        {
            session.Dispose();
        }

        Assert.Equal(WorldSession.State.Disconnected, session.CurrentState);
        Assert.True(transport.Model.IsTerminated);
        Assert.Equal(
            AceTerminationReason.PacketHeaderDisconnect,
            transport.Model.TerminationReason);
        Assert.Equal(
            CharacterLogOff.Opcode,
            ReadOpcode(transport.Model.DispatchedMessages[^1]));
        Assert.Equal(0, transport.Model.CrcDropCount);
    }

    [Fact]
    [Trait("Lane", "Timing")]
    public async Task PausedSelector_SeededDroppedServerReady_RecoversOnIdleSweep()
    {
        var fake = new FakeAceTransport();
        var lossy = new LossyTransportDecorator(
            fake,
            dropPercent: 50,
            seed: 13,
            NetDropDirection.In);
        var session = new WorldSession(
            new IPEndPoint(IPAddress.Loopback, 9000),
            lossy)
        {
            TransportClockSource =
                (fake.Clock.GetTimestamp, fake.Clock.Frequency),
        };
        using var enterRequest = new ManualResetEventSlim();
        fake.Model.MessageDispatched += body =>
        {
            if (ReadOpcode(body)
                == CharacterEnterWorld.EnterWorldRequestOpcode)
            {
                enterRequest.Set();
            }
        };

        try
        {
            session.Connect(
                FakeAceTransport.DefaultAccountName,
                "testpassword",
                TimeSpan.FromSeconds(5));
            session.StartCharacterSelectionReceive();

            for (int i = 0; i < 3; i++)
            {
                fake.Clock.Advance(TimeSpan.FromMilliseconds(50));
                session.Tick();
            }

            Task gapDriver = Task.Run(() =>
            {
                Assert.True(enterRequest.Wait(HarnessPatience));
                // This later sequenced packet passes the seeded loss gate,
                // exposing the missing ServerReady and parking behind it.
                fake.EnqueueServerGameMessage(
                    BuildServerMessage("post-ready follower"),
                    GameMessageGroup.UIQueue);
                Assert.True(SpinWait.SpinUntil(
                    () => session.Transport?.Inbound.NakCount > 0,
                    HarnessPatience));

                // No datagram follows this virtual-time edge. Recovery now
                // requires paused EnterWorld's independent periodic sweep.
                fake.Clock.Advance(TimeSpan.FromSeconds(1));
            });

            session.EnterWorld(0, TimeSpan.FromSeconds(5));
            await gapDriver;

            Assert.Equal(WorldSession.State.InWorld, session.CurrentState);
            Assert.Equal(1, lossy.InboundDropped);
            Assert.True(session.Transport!.Stats.NaksSent > 0);
            Assert.True(fake.Model.RetransmitsServed > 0);
            Assert.Equal(0, session.Transport.Inbound.NakCount);
            Assert.Equal(0, session.Transport.Outbound.PendingResendCount);
            Assert.Equal(
                new[]
                {
                    CharacterEnterWorld.EnterWorldRequestOpcode,
                    CharacterEnterWorld.EnterWorldOpcode,
                },
                fake.Model.DispatchedMessages.Select(ReadOpcode).ToArray());
            Assert.Equal(256, fake.Model.Crypto.Headroom);
            Assert.Equal(0, fake.Model.Crypto.OrphanCount);
            Assert.Equal(0, fake.Model.CrcDropCount);
            Assert.False(fake.Model.IsTerminated);
        }
        finally
        {
            session.Dispose();
        }

        Assert.Equal(WorldSession.State.Disconnected, session.CurrentState);
        Assert.True(fake.Model.IsTerminated);
        Assert.Equal(
            AceTerminationReason.PacketHeaderDisconnect,
            fake.Model.TerminationReason);
        Assert.Equal(
            CharacterLogOff.Opcode,
            ReadOpcode(fake.Model.DispatchedMessages[^1]));
        Assert.Equal(256, fake.Model.Crypto.Headroom);
        Assert.Equal(0, fake.Model.Crypto.OrphanCount);
    }

    private static uint ReadOpcode(byte[] messageBody) =>
        BinaryPrimitives.ReadUInt32LittleEndian(messageBody);

    private static byte[] BuildServerMessage(string text)
    {
        var writer = new PacketWriter(64);
        writer.WriteUInt32(ServerMessage.Opcode); // 0xF7E0
        writer.WriteString16L(text);
        writer.WriteUInt32(1);
        return writer.ToArray();
    }
}
