using System.Buffers.Binary;
using System.Net;
using AcDream.Core.Net.Cryptography;
using AcDream.Core.Net.Messages;
using AcDream.Core.Net.Packets;
using AcDream.Core.Net.Transport;

namespace AcDream.Core.Net.Tests.Transport;

public sealed class AckNakSchedulerTests
{
    private const uint ClientSeed = 0x11AA22BBu;
    private const uint ServerSeed = 0x33CC44DDu;
    private const uint ClientId = 0x1234u;
    private const ushort SessionIteration = 0x0007;
    private const ulong Cookie = 0xFEEDFACECAFEBABEUL;


    [Fact]
    public void CumulativeAck_TwoSecondGate_CarriesWatermarkAtEmission()
    {
        (ReliableTransport transport, VirtualClock clock, List<byte[]> sent) =
            CreateTransport();
        Admit(transport, 2u);
        Admit(transport, 3u);

        transport.Sweep();
        clock.Advance(TimeSpan.FromSeconds(1.99));
        transport.Sweep();
        Assert.Empty(sent);

        clock.Advance(TimeSpan.FromSeconds(0.01));
        transport.Sweep();
        Assert.Equal((ushort)5, transport.Clock.IntervalId);
        AssertAckShape(
            Assert.Single(sent),
            expectedSequence: 1u,
            expectedValue: 3u,
            expectedTime: transport.Clock.IntervalId);
        Assert.Equal(1, transport.Stats.AcksSent);

        // The gate reset: silent until the next 2.0 s elapses.
        transport.Sweep();
        clock.Advance(TimeSpan.FromSeconds(1.99));
        transport.Sweep();
        Assert.Single(sent);

        Admit(transport, 4u);
        Admit(transport, 5u);
        clock.Advance(TimeSpan.FromSeconds(0.01));
        transport.Sweep();
        Assert.Equal(2, sent.Count);
        AssertAckShape(
            sent[1],
            expectedSequence: 1u,
            expectedValue: 5u,
            expectedTime: transport.Clock.IntervalId);
        Assert.Equal(2, transport.Stats.AcksSent);
    }


    [Fact]
    public void ModelAcceptsAck_AtReusedSequence_WithoutAdvancingWatermark()
    {
        (AceSessionModel model, _) = CreateNegotiatedModel();
        (ReliableTransport transport, VirtualClock clock, List<byte[]> sent) =
            CreateTransport();

        transport.Outbound.SendGameMessage(
            MakeMessage(1), GameMessageGroup.UIQueue);
        transport.Outbound.SendGameMessage(
            MakeMessage(2), GameMessageGroup.UIQueue);
        foreach (byte[] datagram in sent)
            model.Receive(datagram);
        Assert.Equal(3u, model.LastReceivedPacketSequence);
        sent.Clear();

        clock.Advance(TimeSpan.FromSeconds(2));
        transport.Sweep();
        byte[] ack = Assert.Single(sent);
        PacketHeader ackHeader = PacketHeader.Unpack(ack);
        Assert.Equal(3u, ackHeader.Sequence);
        Assert.Equal(
            (uint)PacketHeaderFlags.AckSequence,
            (uint)ackHeader.Flags);

        model.Receive(ack);
        Assert.Equal(0, model.DuplicateDropCount);
        Assert.Equal(0, model.CrcDropCount);
        Assert.Equal(0, model.StateDropCount);
        // The watermark did NOT advance (:474-476 — exact-flags skip).
        Assert.Equal(3u, model.LastReceivedPacketSequence);
    }


    [Fact]
    public void ParkedNak_SuppressesTheAck_AckResumesWhenTheGapClears()
    {
        (ReliableTransport transport, VirtualClock clock, List<byte[]> sent) =
            CreateTransport();
        Admit(transport, 2u); // watermark 2, no gap
        Admit(transport, 4u); // gap walk parks id 3
        Assert.Equal(1, transport.Inbound.NakCount);

        clock.Advance(TimeSpan.FromSeconds(2.5));
        transport.Sweep();
        transport.Sweep();
        byte[] nak = Assert.Single(sent);
        Assert.Equal(
            (uint)PacketHeaderFlags.RequestRetransmit,
            (uint)PacketHeader.Unpack(nak).Flags);
        Assert.Equal(0, transport.Stats.AcksSent);
        Assert.Equal(1, transport.Stats.NaksSent);
        sent.Clear();

        Admit(transport, 3u);
        Assert.Equal(0, transport.Inbound.NakCount);
        transport.Sweep();
        Assert.Empty(sent);
        clock.Advance(TimeSpan.FromSeconds(2.0));
        transport.Sweep();
        AssertAckShape(
            Assert.Single(sent),
            expectedSequence: 1u,
            expectedValue: 4u,
            expectedTime: transport.Clock.IntervalId);
    }


    [Fact]
    public void CreateObjectFlood_InsideOneWindow_CollapsesToOneAck()
    {
        (ReliableTransport transport, VirtualClock clock, List<byte[]> sent) =
            CreateTransport();

        uint sequence = 2;
        for (int i = 0; i < 50; i++)
        {
            Admit(transport, sequence++);
            clock.Advance(TimeSpan.FromMilliseconds(20));
            transport.Sweep();
        }

        Assert.Empty(sent);

        clock.Advance(TimeSpan.FromSeconds(1.0));
        transport.Sweep();
        AssertAckShape(
            Assert.Single(sent),
            expectedSequence: 1u,
            expectedValue: 51u,
            expectedTime: transport.Clock.IntervalId);
        Assert.Equal(1, transport.Stats.AcksSent);
    }


    [Fact]
    public void QuietSession_CumulativeAcksKeepAceAlive_PastThe60sHorizon()
    {
        var transport = new FakeAceTransport();
        var session = new WorldSession(
            new IPEndPoint(IPAddress.Loopback, 9000),
            transport);
        session.TransportClockSource =
            (transport.Clock.GetTimestamp, transport.Clock.Frequency);
        try
        {
            session.Connect(
                "testaccount", "testpassword", TimeSpan.FromSeconds(10));
            session.EnterWorld(0, TimeSpan.FromSeconds(10));
            Assert.Equal(WorldSession.State.InWorld, session.CurrentState);

            for (int frame = 0; frame < 240; frame++)
            {
                transport.Clock.Advance(TimeSpan.FromMilliseconds(500));
                transport.PumpServer();
                session.Tick();
                Thread.Sleep(1);
            }

            Assert.False(transport.Model.IsTerminated);
            Assert.Equal(
                AceTerminationReason.None,
                transport.Model.TerminationReason);

            // The deadline is FRESH — refreshed within the last ack
            // interval, not merely unexpired.
            long margin = transport.Model.TimeoutDeadlineTimestamp
                - transport.Clock.GetTimestamp();
            Assert.True(
                margin > TimeSpan.FromSeconds(50).Ticks,
                $"TimeoutDeadline margin {margin} ticks — the acks are not refreshing it");

            long acksSent = session.Transport!.Stats.AcksSent;
            Assert.True(
                acksSent >= 40,
                $"only {acksSent} cumulative acks over 120 virtual seconds");

            Assert.Equal(0, transport.Model.CrcDropCount);
            Assert.Equal(0, transport.Model.StateDropCount);
            Assert.Equal(0, transport.Model.DuplicateDropCount);
            Assert.Equal(0, session.Transport.Inbound.NakCount);
        }
        finally
        {
            session.Dispose();
        }

        Assert.Equal(WorldSession.State.Disconnected, session.CurrentState);
    }

    [Fact]
    public void FullLifecycle_CleanRun_FloodCollapses_GracefulTeardown()
    {
        var transport = new FakeAceTransport();
        var session = new WorldSession(
            new IPEndPoint(IPAddress.Loopback, 9000),
            transport);
        session.TransportClockSource =
            (transport.Clock.GetTimestamp, transport.Clock.Frequency);
        try
        {
            session.Connect(
                "testaccount", "testpassword", TimeSpan.FromSeconds(10));
            session.EnterWorld(0, TimeSpan.FromSeconds(10));
            Assert.Equal(WorldSession.State.InWorld, session.CurrentState);

            var messages = new List<string>();
            session.ServerMessageReceived += m => messages.Add(m.Message);

            // An S2C flood inside one 2 s window: 20 messages, each pumped
            // into its own sequenced packet (the pre-N3 reflex ack answered
            // every one of them).
            long acksBefore = session.Transport!.Stats.AcksSent;
            for (int i = 0; i < 20; i++)
            {
                transport.Model.EnqueueGameMessage(
                    BuildServerMessage($"flood {i}"),
                    GameMessageGroup.UIQueue);
                transport.PumpServer();
            }

            PumpUntil(session, () => messages.Count >= 20);
            Assert.Equal(acksBefore, session.Transport.Stats.AcksSent);

            transport.Clock.Advance(TimeSpan.FromSeconds(2));
            session.Tick();
            Assert.Equal(
                acksBefore + 1,
                session.Transport.Stats.AcksSent);

            // A few more quiet gates keep flowing.
            for (int frame = 0; frame < 10; frame++)
            {
                transport.Clock.Advance(TimeSpan.FromMilliseconds(500));
                transport.PumpServer();
                session.Tick();
                Thread.Sleep(1);
            }

            Assert.False(transport.Model.IsTerminated);
            Assert.Equal(0, transport.Model.CrcDropCount);
            Assert.Equal(0, transport.Model.StateDropCount);
            Assert.Equal(0, transport.Model.DuplicateDropCount);
            Assert.Equal(256, transport.Model.Crypto.Headroom);
            Assert.Equal(0, session.Transport.Inbound.NakCount);
        }
        finally
        {
            session.Dispose();
        }

        // The model terminated on OUR transport Disconnect — the 60 s
        // timeout never fired.
        Assert.Equal(WorldSession.State.Disconnected, session.CurrentState);
        Assert.True(transport.Model.IsTerminated);
        Assert.Equal(
            AceTerminationReason.PacketHeaderDisconnect,
            transport.Model.TerminationReason);
    }

    // =====================================================================
    // Fixture helpers
    // =====================================================================

    private static (ReliableTransport Transport, VirtualClock Clock,
        List<byte[]> Sent) CreateTransport()
    {
        var virtualClock = new VirtualClock();
        var sent = new List<byte[]>();
        var transport = new ReliableTransport(
            MakeIsaac(ClientSeed),
            MakeIsaac(ServerSeed),
            (ushort)ClientId,
            SessionIteration,
            datagram => sent.Add(datagram.ToArray()),
            new TransportClock(
                virtualClock.GetTimestamp,
                virtualClock.Frequency));
        return (transport, virtualClock, sent);
    }

    /// <summary>Admit one encrypted sequenced arrival that must not drop.</summary>
    private static void Admit(ReliableTransport transport, uint sequence)
    {
        InboundSequenceTracker.Admission admission =
            transport.Inbound.Admit(sequence, encrypted: true);
        Assert.False(admission.Drop);
    }

    private static void AssertAckShape(
        byte[] datagram,
        uint expectedSequence,
        uint expectedValue,
        ushort expectedTime)
    {
        Assert.Equal(PacketHeader.Size + sizeof(uint), datagram.Length);
        PacketHeader header = PacketHeader.Unpack(datagram);
        Assert.Equal(
            (uint)PacketHeaderFlags.AckSequence,
            (uint)header.Flags);
        Assert.Equal(expectedSequence, header.Sequence);
        Assert.Equal((ushort)ClientId, header.Id);
        Assert.Equal(expectedTime, header.Time);
        Assert.Equal(SessionIteration, header.Iteration);
        Assert.Equal((ushort)sizeof(uint), header.DataSize);
        Assert.Equal(
            expectedValue,
            BinaryPrimitives.ReadUInt32LittleEndian(
                datagram.AsSpan(PacketHeader.Size)));

        PacketCodec.PacketDecodeResult decoded =
            PacketCodec.TryDecode(datagram, inboundIsaac: null);
        Assert.True(decoded.IsOk, decoded.Error.ToString());
        Assert.Equal(expectedValue, decoded.Packet!.Optional.AckSequence);
    }

    /// <summary>An 8-byte message body whose first byte is a test marker.</summary>
    private static byte[] MakeMessage(byte marker) =>
        new byte[] { marker, 0x11, 0x22, 0x33, 0x00, 0x00, 0x00, 0x00 };

    private static IsaacRandom MakeIsaac(uint seed)
    {
        Span<byte> seedBytes = stackalloc byte[4];
        BinaryPrimitives.WriteUInt32LittleEndian(seedBytes, seed);
        return new IsaacRandom(seedBytes);
    }

    private static (AceSessionModel Model, VirtualClock Clock)
        CreateNegotiatedModel()
    {
        var clock = new VirtualClock();
        var model = new AceSessionModel(
            clock, ClientSeed, ServerSeed, ClientId, Cookie);
        model.LoginRequestReceived += model.SendConnectRequest;

        byte[] login = PacketCodec.Encode(
            new PacketHeader { Flags = PacketHeaderFlags.LoginRequest },
            LoginRequest.Build("testaccount", "testpassword", 1234),
            outboundIsaac: null);
        model.Receive(login);
        model.Update();
        model.TakePendingDatagrams();

        byte[] cookieBody = new byte[8];
        BinaryPrimitives.WriteUInt64LittleEndian(cookieBody, Cookie);
        byte[] connectResponse = PacketCodec.Encode(
            new PacketHeader
            { Sequence = 1, Flags = PacketHeaderFlags.ConnectResponse },
            cookieBody,
            outboundIsaac: null);
        model.Receive(connectResponse);
        model.Update();
        model.TakePendingDatagrams();

        return (model, clock);
    }

    private static void PumpUntil(WorldSession session, Func<bool> condition)
    {
        DateTime deadline = DateTime.UtcNow.AddSeconds(10);
        while (!condition() && DateTime.UtcNow < deadline)
        {
            session.Tick();
            Thread.Sleep(5);
        }

        Assert.True(condition(), "condition not reached before the deadline");
    }

    private static byte[] BuildServerMessage(string text)
    {
        var writer = new PacketWriter(64 + text.Length);
        writer.WriteUInt32(ServerMessage.Opcode); // 0xF7E0
        writer.WriteString16L(text);
        writer.WriteUInt32(1);
        return writer.ToArray();
    }
}
