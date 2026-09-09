using System.Buffers.Binary;
using System.Net;
using AcDream.Core.Net.Messages;
using AcDream.Core.Net.Packets;
using AcDream.Core.Net.Transport;

namespace AcDream.Core.Net.Tests.Transport;

[Collection(AcDream.Core.Net.Tests.NetProcessStaticsCollection.Name)]
public sealed class LossyTransportDecoratorTests
{
    // =====================================================================
    // Determinism
    // =====================================================================

    [Fact]
    public void SameSeed_ProducesIdenticalDropPattern_DifferentSeedDiffers()
    {
        bool[] first = OutboundSurvivalPattern(seed: 42, count: 400);
        bool[] second = OutboundSurvivalPattern(seed: 42, count: 400);
        bool[] third = OutboundSurvivalPattern(seed: 43, count: 400);

        Assert.Equal(first, second);
        Assert.NotEqual(first, third);
        Assert.Contains(false, first);
        Assert.Contains(true, first);
    }

    private static bool[] OutboundSurvivalPattern(int seed, int count)
    {
        var inner = new RecordingTransport();
        var lossy = new LossyTransportDecorator(
            inner, dropPercent: 25, seed, NetDropDirection.Both);
        Arm(lossy, inner);

        bool[] survived = new bool[count];
        int forwardedBefore = inner.Sent.Count;
        for (int i = 0; i < count; i++)
        {
            lossy.Send(EncryptedDatagram(sequence: (uint)(i + 2)));
            survived[i] = inner.Sent.Count > forwardedBefore;
            forwardedBefore = inner.Sent.Count;
        }

        return survived;
    }

    // =====================================================================
    // Direction mask
    // =====================================================================

    [Fact]
    public void DirectionOut_DropsOutboundOnly_InboundAllDelivered()
    {
        var inner = new RecordingTransport();
        var lossy = new LossyTransportDecorator(
            inner, dropPercent: 100, seed: 1, NetDropDirection.Out);
        Arm(lossy, inner);
        int armedForwardCount = inner.Sent.Count;

        // Every post-arming outbound datagram dies at 100%.
        for (int i = 0; i < 20; i++)
            lossy.Send(EncryptedDatagram(sequence: (uint)(i + 2)));
        Assert.Equal(armedForwardCount, inner.Sent.Count);
        Assert.Equal(20, lossy.OutboundDropped);

        // Inbound is untouched by the Out mask.
        for (int i = 0; i < 20; i++)
            inner.Inbound.Enqueue(EncryptedDatagram(sequence: (uint)(i + 2)));
        Span<byte> buffer = stackalloc byte[64];
        for (int i = 0; i < 20; i++)
        {
            Assert.True(
                lossy.Receive(buffer, TimeSpan.FromMilliseconds(50), out _) > 0);
        }

        Assert.Equal(0, lossy.InboundDropped);
    }

    [Fact]
    public void DirectionIn_DropsInboundOnly_OutboundAllForwarded()
    {
        var inner = new RecordingTransport();
        var lossy = new LossyTransportDecorator(
            inner, dropPercent: 100, seed: 1, NetDropDirection.In);
        Arm(lossy, inner);
        int armedForwardCount = inner.Sent.Count;

        // Outbound is untouched by the In mask.
        for (int i = 0; i < 20; i++)
            lossy.Send(EncryptedDatagram(sequence: (uint)(i + 2)));
        Assert.Equal(armedForwardCount + 20, inner.Sent.Count);
        Assert.Equal(0, lossy.OutboundDropped);

        // Every queued inbound datagram is eaten; the exhausted inner then
        // reports timeout (-1) and the decorator surfaces it.
        for (int i = 0; i < 20; i++)
            inner.Inbound.Enqueue(EncryptedDatagram(sequence: (uint)(i + 2)));
        byte[] buffer = new byte[64];
        Assert.Equal(
            -1,
            lossy.Receive(buffer, TimeSpan.FromMilliseconds(50), out _));
        Assert.Equal(20, lossy.InboundDropped);
    }

    // =====================================================================
    // Structural absence at 0%
    // =====================================================================

    [Fact]
    public void WrapIfConfigured_ZeroPercent_ReturnsTheRawTransport()
    {
        int savedPercent = NetDiagnostics.NetDropPercent;
        int savedSeed = NetDiagnostics.NetDropSeed;
        NetDropDirection savedDir = NetDiagnostics.NetDropDir;
        try
        {
            var inner = new RecordingTransport();

            NetDiagnostics.NetDropPercent = 0;
            Assert.Same(inner, LossyTransportDecorator.WrapIfConfigured(inner));

            NetDiagnostics.NetDropPercent = 2;
            NetDiagnostics.NetDropSeed = 7;
            NetDiagnostics.NetDropDir = NetDropDirection.Both;
            IWorldSessionTransport wrapped =
                LossyTransportDecorator.WrapIfConfigured(inner);
            Assert.IsType<LossyTransportDecorator>(wrapped);
            Assert.NotSame(inner, wrapped);
        }
        finally
        {
            NetDiagnostics.NetDropPercent = savedPercent;
            NetDiagnostics.NetDropSeed = savedSeed;
            NetDiagnostics.NetDropDir = savedDir;
        }
    }

    [Fact]
    public void EnvParsing_RejectsOutOfRangeAndGarbage()
    {
        Assert.Equal(0, NetDiagnostics.ParseDropPercent(null));
        Assert.Equal(0, NetDiagnostics.ParseDropPercent(""));
        Assert.Equal(0, NetDiagnostics.ParseDropPercent("banana"));
        Assert.Equal(0, NetDiagnostics.ParseDropPercent("-1"));
        Assert.Equal(0, NetDiagnostics.ParseDropPercent("101"));
        Assert.Equal(2, NetDiagnostics.ParseDropPercent("2"));
        Assert.Equal(100, NetDiagnostics.ParseDropPercent("100"));

        Assert.Equal(
            NetDropDirection.Both, NetDiagnostics.ParseDropDirection(null));
        Assert.Equal(
            NetDropDirection.Out, NetDiagnostics.ParseDropDirection("out"));
        Assert.Equal(
            NetDropDirection.In, NetDiagnostics.ParseDropDirection("In"));
        Assert.Equal(
            NetDropDirection.Both, NetDiagnostics.ParseDropDirection("both"));
        Assert.Equal(
            NetDropDirection.Both, NetDiagnostics.ParseDropDirection("weird"));
    }

    // =====================================================================
    // The arming gate
    // =====================================================================

    [Fact]
    public void NothingDrops_UntilTheFirstEncryptedOutboundHasBeenForwarded()
    {
        var inner = new RecordingTransport();
        var lossy = new LossyTransportDecorator(
            inner, dropPercent: 100, seed: 1, NetDropDirection.Both);

        for (int i = 0; i < 5; i++)
            lossy.Send(CleartextDatagram());
        Assert.Equal(5, inner.Sent.Count);
        Assert.False(lossy.IsArmed);
        Assert.Equal(0, lossy.OutboundDropped);

        // Pre-arming: inbound always delivers.
        inner.Inbound.Enqueue(EncryptedDatagram(sequence: 2));
        byte[] buffer = new byte[64];
        Assert.True(
            lossy.Receive(buffer, TimeSpan.FromMilliseconds(50), out _) > 0);
        Assert.Equal(0, lossy.InboundDropped);

        lossy.Send(EncryptedDatagram(sequence: 2));
        Assert.Equal(6, inner.Sent.Count);
        Assert.True(lossy.IsArmed);
        Assert.Equal(0, lossy.OutboundDropped);

        // From the next datagram on, 100% eats everything in both
        // directions.
        lossy.Send(EncryptedDatagram(sequence: 3));
        lossy.Send(CleartextDatagram());
        Assert.Equal(6, inner.Sent.Count);
        Assert.Equal(2, lossy.OutboundDropped);
        inner.Inbound.Enqueue(EncryptedDatagram(sequence: 3));
        Assert.Equal(
            -1,
            lossy.Receive(buffer, TimeSpan.FromMilliseconds(50), out _));
        Assert.Equal(1, lossy.InboundDropped);
    }

    [Fact]
    public void ShortOrCleartextDatagrams_NeverArm()
    {
        var inner = new RecordingTransport();
        var lossy = new LossyTransportDecorator(
            inner, dropPercent: 100, seed: 1, NetDropDirection.Both);

        lossy.Send(new byte[PacketHeader.Size]);
        lossy.Send(CleartextDatagram());
        Assert.False(lossy.IsArmed);
        Assert.Equal(2, inner.Sent.Count);
    }


    [Fact]
    [Trait("Lane", "Timing")]
    public void LossySession_FivePercentSeeded_ZeroMessageLoss_Headroom256()
    {
        var fake = new FakeAceTransport
        {
            AutoAdvanceOnBlockingReceive = TimeSpan.FromSeconds(3),
        };
        var lossy = new LossyTransportDecorator(
            fake, dropPercent: 5, seed: 424242, NetDropDirection.Both);
        var session = new WorldSession(
            new IPEndPoint(IPAddress.Loopback, 9000),
            lossy);
        session.TransportClockSource =
            (fake.Clock.GetTimestamp, fake.Clock.Frequency);
        try
        {
            session.Connect(
                "testaccount", "testpassword", TimeSpan.FromSeconds(10));
            session.EnterWorld(0, TimeSpan.FromSeconds(10));
            Assert.Equal(WorldSession.State.InWorld, session.CurrentState);

            int s2cReceived = 0;
            session.ServerMessageReceived += m =>
            {
                if (m.Message.StartsWith("s2c ", StringComparison.Ordinal))
                    s2cReceived++;
            };
            int c2sDispatched = 0;
            fake.Model.MessageDispatched += body =>
            {
                if (body.AsSpan().IndexOf("c2s "u8) >= 0)
                    c2sDispatched++;
            };

            const int MessagesEachWay = 1_000;
            for (int i = 0; i < MessagesEachWay; i++)
            {
                fake.Clock.Advance(TimeSpan.FromMilliseconds(25));
                session.SendTalk($"c2s {i}");
                fake.Model.EnqueueGameMessage(
                    BuildServerMessage($"s2c {i}"),
                    GameMessageGroup.UIQueue);
                fake.PumpServer();
                session.Tick();
                if ((i & 15) == 0)
                    Thread.Sleep(1);
            }

            int trickle = 0;
            DateTime deadline = DateTime.UtcNow.AddSeconds(60);
            while (DateTime.UtcNow < deadline
                   && (s2cReceived != MessagesEachWay
                       || c2sDispatched != MessagesEachWay))
            {
                fake.Clock.Advance(TimeSpan.FromMilliseconds(500));
                session.SendTalk($"trickle {trickle++}");
                fake.PumpServer();
                session.Tick();
                Thread.Sleep(1);
            }

            int quietIterations = 0;
            while (DateTime.UtcNow < deadline)
            {
                fake.Clock.Advance(TimeSpan.FromMilliseconds(500));
                if (session.Transport!.Outbound.CacheDepth > 1
                    && ++quietIterations % 8 == 0)
                {
                    session.SendTalk($"trickle {trickle++}");
                }

                fake.PumpServer();
                session.Tick();
                Thread.Sleep(1);

                if (s2cReceived == MessagesEachWay
                    && c2sDispatched == MessagesEachWay
                    && session.Transport.Inbound.NakCount == 0
                    && session.Transport.Outbound.PendingResendCount == 0
                    && session.Transport.Outbound.CacheDepth <= 1)
                {
                    break;
                }
            }

            string ledger =
                $"s2c={s2cReceived} c2s={c2sDispatched} "
                + $"dropped-out={lossy.OutboundDropped} "
                + $"dropped-in={lossy.InboundDropped} "
                + $"resends={session.Transport!.Stats.ResendsSent} "
                + $"naks-sent={session.Transport.Stats.NaksSent} "
                + $"headroom={fake.Model.Crypto.Headroom} "
                + $"crc-drops={fake.Model.CrcDropCount} "
                + $"last-crc-seq={fake.Model.LastCrcDropSequence} "
                + $"last-crc-flags={fake.Model.LastCrcDropFlags} "
                + $"last-crc-watermark={fake.Model.LastCrcDropWatermark} "
                + $"dup-drops={fake.Model.DuplicateDropCount} "
                + $"ooo={fake.Model.OutOfOrderPacketCount} "
                + $"last-seq={fake.Model.LastReceivedPacketSequence} "
                + $"cache={session.Transport.Outbound.CacheDepth}";

            // Zero message loss, both directions.
            Assert.True(s2cReceived == MessagesEachWay, $"S2C loss: {ledger}");
            Assert.True(
                c2sDispatched == MessagesEachWay, $"C2S loss: {ledger}");

            // The decorator injected real loss in both directions, and the
            // N1–N4 machinery healed it.
            Assert.True(lossy.OutboundDropped > 0, ledger);
            Assert.True(lossy.InboundDropped > 0, ledger);
            Assert.True(session.Transport.Stats.ResendsSent > 0, ledger);
            Assert.True(session.Transport.Stats.NaksSent > 0, ledger);

            Assert.True(fake.Model.Crypto.Headroom == 256, ledger);
            Assert.True(fake.Model.Crypto.OrphanCount == 0, ledger);
            Assert.False(fake.Model.IsTerminated);
            Assert.Equal(WorldSession.State.InWorld, session.CurrentState);
        }
        finally
        {
            session.Dispose();
        }
    }

    // =====================================================================
    // Fixture helpers
    // =====================================================================

    /// <summary>Arms the decorator by forwarding one encrypted datagram
    /// (the arming witness is never dropped).</summary>
    private static void Arm(
        LossyTransportDecorator lossy,
        RecordingTransport inner)
    {
        int before = inner.Sent.Count;
        lossy.Send(EncryptedDatagram(sequence: 2));
        Assert.Equal(before + 1, inner.Sent.Count);
        Assert.True(lossy.IsArmed);
    }

    private static byte[] EncryptedDatagram(uint sequence)
    {
        byte[] buffer = new byte[PacketHeader.Size + 8];
        new PacketHeader
        {
            Sequence = sequence,
            Flags = PacketHeaderFlags.BlobFragments
                | PacketHeaderFlags.EncryptedChecksum,
            DataSize = 8,
        }.Pack(buffer);
        return buffer;
    }

    private static byte[] CleartextDatagram()
    {
        byte[] buffer = new byte[PacketHeader.Size + 4];
        new PacketHeader
        {
            Sequence = 2,
            Flags = PacketHeaderFlags.AckSequence,
            DataSize = 4,
        }.Pack(buffer);
        return buffer;
    }

    private static byte[] BuildServerMessage(string text)
    {
        var writer = new PacketWriter(64 + text.Length);
        writer.WriteUInt32(ServerMessage.Opcode); // 0xF7E0
        writer.WriteString16L(text);
        writer.WriteUInt32(1);
        return writer.ToArray();
    }

    private sealed class RecordingTransport : IWorldSessionTransport
    {
        public List<byte[]> Sent { get; } = new();
        public Queue<byte[]> Inbound { get; } = new();
        public bool Disposed { get; private set; }

        public void Send(ReadOnlySpan<byte> datagram) =>
            Sent.Add(datagram.ToArray());

        public void Send(IPEndPoint remote, ReadOnlySpan<byte> datagram) =>
            Sent.Add(datagram.ToArray());

        public int Receive(
            Span<byte> destination,
            TimeSpan timeout,
            out IPEndPoint? from)
        {
            if (Inbound.Count == 0)
            {
                from = null;
                return -1;
            }

            byte[] datagram = Inbound.Dequeue();
            datagram.CopyTo(destination);
            from = new IPEndPoint(IPAddress.Loopback, 9000);
            return datagram.Length;
        }

        public ValueTask<NetReceiveResult> ReceiveAsync(
            Memory<byte> destination,
            CancellationToken cancellationToken)
        {
            if (Inbound.Count == 0)
                throw new OperationCanceledException(cancellationToken);
            byte[] datagram = Inbound.Dequeue();
            datagram.CopyTo(destination);
            return ValueTask.FromResult(new NetReceiveResult(
                datagram.Length,
                new IPEndPoint(IPAddress.Loopback, 9000)));
        }

        public void Dispose() => Disposed = true;
    }
}
