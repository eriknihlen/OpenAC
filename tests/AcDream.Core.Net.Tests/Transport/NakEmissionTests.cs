using System.Buffers.Binary;
using System.Net;
using AcDream.Core.Net.Cryptography;
using AcDream.Core.Net.Messages;
using AcDream.Core.Net.Packets;
using AcDream.Core.Net.Transport;

namespace AcDream.Core.Net.Tests.Transport;

public sealed class NakEmissionTests
{
    private const uint ClientSeed = 0x77EE88FFu;
    private const uint ServerSeed = 0x55DD66CCu;
    private const uint ClientId = 0x1234u;
    private const ushort SessionIteration = 0x0007;
    private const uint TrackerSeed = 0x5EED5EEDu;


    [Fact]
    public void NakGate_ClosedAtExactly600ms_OpensJustPastIt()
    {
        (ReliableTransport transport, VirtualClock clock, List<byte[]> sent) =
            CreateTransport();
        Admit(transport, 2u);
        Admit(transport, 4u); // parks id 3
        Assert.Equal(1, transport.Inbound.NakCount);

        clock.Advance(TimeSpan.FromSeconds(0.6));
        transport.Sweep();
        Assert.Empty(sent);
        Assert.Equal(0, transport.Stats.NaksSent);

        // One millisecond past: open.
        clock.Advance(TimeSpan.FromMilliseconds(1));
        transport.Sweep();
        Assert.Single(sent);
        Assert.Equal(1, transport.Stats.NaksSent);

        transport.Sweep();
        clock.Advance(TimeSpan.FromSeconds(0.6));
        transport.Sweep();
        Assert.Single(sent);
        clock.Advance(TimeSpan.FromMilliseconds(1));
        transport.Sweep();
        Assert.Equal(2, sent.Count);
        Assert.Equal(2, transport.Stats.NaksSent);
    }


    [Fact]
    public void SharedTimestamp_AckDelaysNak_NakDelaysAck()
    {
        (ReliableTransport transport, VirtualClock clock, List<byte[]> sent) =
            CreateTransport();
        Admit(transport, 2u);

        // An ack goes out at t = 2.0 and stamps the SHARED timestamp.
        clock.Advance(TimeSpan.FromSeconds(2.0));
        transport.Sweep();
        Assert.Equal(1, transport.Stats.AcksSent);
        sent.Clear();

        Admit(transport, 4u); // parks id 3
        clock.Advance(TimeSpan.FromSeconds(0.6));
        transport.Sweep();
        Assert.Empty(sent);
        clock.Advance(TimeSpan.FromMilliseconds(1));
        transport.Sweep();
        byte[] nak = Assert.Single(sent);
        Assert.Equal(
            (uint)PacketHeaderFlags.RequestRetransmit,
            (uint)PacketHeader.Unpack(nak).Flags);
        sent.Clear();

        Admit(transport, 3u);
        Assert.Equal(0, transport.Inbound.NakCount);
        clock.Advance(TimeSpan.FromSeconds(1.999));
        transport.Sweep();
        Assert.Empty(sent);
        Assert.Equal(1, transport.Stats.AcksSent);
        clock.Advance(TimeSpan.FromSeconds(0.001));
        transport.Sweep();
        Assert.Single(sent);
        Assert.Equal(2, transport.Stats.AcksSent);
    }

    [Fact]
    public void NakSweep_BothGatesOpen_EmitsTheNakAndNeverTheAck()
    {
        (ReliableTransport transport, VirtualClock clock, List<byte[]> sent) =
            CreateTransport();
        Admit(transport, 2u);
        Admit(transport, 4u); // parks id 3

        clock.Advance(TimeSpan.FromSeconds(5.0));
        transport.Sweep();
        byte[] nak = Assert.Single(sent);
        Assert.Equal(
            (uint)PacketHeaderFlags.RequestRetransmit,
            (uint)PacketHeader.Unpack(nak).Flags);
        Assert.Equal(1, transport.Stats.NaksSent);
        Assert.Equal(0, transport.Stats.AcksSent);
    }


    [Fact]
    public void NakShape_CleartextExactFlags_BorrowedSequence_AscendingIds()
    {
        (ReliableTransport transport, VirtualClock clock, List<byte[]> sent) =
            CreateTransport();

        // Two reliable sends so the borrowed sequence is nontrivial.
        transport.Outbound.SendGameMessage(
            MakeMessage(1), GameMessageGroup.UIQueue);
        transport.Outbound.SendGameMessage(
            MakeMessage(2), GameMessageGroup.UIQueue);
        Assert.Equal(3u, transport.Outbound.HighestIdSent);
        sent.Clear();

        Admit(transport, 2u);
        Admit(transport, 6u); // parks 3, 4, 5
        clock.Advance(TimeSpan.FromSeconds(0.7));
        transport.Sweep();

        byte[] nak = Assert.Single(sent);
        PacketHeader header = PacketHeader.Unpack(nak);

        Assert.Equal(
            (uint)PacketHeaderFlags.RequestRetransmit,
            (uint)header.Flags);

        Assert.Equal(transport.Outbound.HighestIdSent, header.Sequence);
        Assert.Equal(3u, transport.Outbound.HighestIdSent);
        Assert.Equal((ushort)ClientId, header.Id);
        Assert.Equal(transport.Clock.IntervalId, header.Time);
        Assert.Equal((ushort)2, header.Time); // 0.7 s of 0.5 s intervals + 1
        Assert.Equal(SessionIteration, header.Iteration);

        Assert.Equal((ushort)16, header.DataSize);
        Assert.Equal(PacketHeader.Size + 16, nak.Length);
        Assert.Equal(
            3u,
            BinaryPrimitives.ReadUInt32LittleEndian(
                nak.AsSpan(PacketHeader.Size)));
        Assert.Equal(
            3u,
            BinaryPrimitives.ReadUInt32LittleEndian(
                nak.AsSpan(PacketHeader.Size + 4)));
        Assert.Equal(
            4u,
            BinaryPrimitives.ReadUInt32LittleEndian(
                nak.AsSpan(PacketHeader.Size + 8)));
        Assert.Equal(
            5u,
            BinaryPrimitives.ReadUInt32LittleEndian(
                nak.AsSpan(PacketHeader.Size + 12)));

        PacketCodec.PacketDecodeResult decoded =
            PacketCodec.TryDecode(nak, inboundIsaac: null);
        Assert.True(decoded.IsOk, decoded.Error.ToString());
        Assert.Equal(
            new uint[] { 3u, 4u, 5u },
            decoded.Packet!.Optional.RetransmitRequests);

        Assert.Equal(3, transport.Inbound.NakCount);
    }

    [Fact]
    public void NakList_CapsAt114LowestAscending_SetUntouched()
    {
        (ReliableTransport transport, VirtualClock clock, List<byte[]> sent) =
            CreateTransport();
        Admit(transport, 2u);
        Admit(transport, 203u); // parks 3..202 — 200 ids
        Assert.Equal(200, transport.Inbound.NakCount);

        clock.Advance(TimeSpan.FromSeconds(0.7));
        transport.Sweep();

        byte[] nak = Assert.Single(sent);
        PacketHeader header = PacketHeader.Unpack(nak);
        Assert.Equal((ushort)(4 + 114 * 4), header.DataSize);
        Assert.Equal(
            114u,
            BinaryPrimitives.ReadUInt32LittleEndian(
                nak.AsSpan(PacketHeader.Size)));
        for (int i = 0; i < 114; i++)
        {
            Assert.Equal(
                (uint)(3 + i),
                BinaryPrimitives.ReadUInt32LittleEndian(
                    nak.AsSpan(PacketHeader.Size + 4 + i * 4)));
        }

        Assert.Equal(200, transport.Inbound.NakCount);
    }


    [Fact]
    public void RejectInOrder_OwnMisparkedWord_FeedsTheNextFreshDraw()
    {
        (InboundSequenceTracker tracker, _) = CreateTracker(9);
        IsaacRandom shadow = MakeIsaac(TrackerSeed);
        uint a = shadow.Next();
        uint b = shadow.Next();
        uint c = shadow.Next();

        Assert.False(tracker.Admit(10, encrypted: false).Drop);
        Assert.Equal(1, tracker.NakCount);

        tracker.OnCleartextRejectSequence(10);
        Assert.Equal(0, tracker.NakCount);
        Assert.Equal(1, tracker.ReclaimedWordCount);

        Assert.Equal(a, Admitted(tracker, 11));
        Assert.Equal(0, tracker.ReclaimedWordCount);
        Assert.Equal(b, Admitted(tracker, 12));
        Assert.Equal(c, Admitted(tracker, 13));
        Assert.Equal(0, tracker.NakCount);
    }

    [Fact]
    public void RejectAfterHigherArrival_BubbleRealignsTheParkedChain()
    {
        (InboundSequenceTracker tracker, _) = CreateTracker(9);
        IsaacRandom shadow = MakeIsaac(TrackerSeed);
        uint a = shadow.Next();
        uint b = shadow.Next();
        uint c = shadow.Next();

        InboundSequenceTracker.Admission eleven =
            tracker.Admit(11, encrypted: true);
        Assert.Equal(b, eleven.VerifyKey);
        tracker.ReparkKey(11, b, eleven.VerifyKeyDrawOrder);
        Assert.Equal(2, tracker.NakCount); // 10 and 11 both parked

        Assert.False(tracker.Admit(10, encrypted: false).Drop);
        tracker.OnCleartextRejectSequence(10);
        Assert.Equal(1, tracker.NakCount);
        Assert.Equal(1, tracker.ReclaimedWordCount);

        // The retransmission of 11 decodes with its TRUE word.
        Assert.Equal(a, Admitted(tracker, 11));
        Assert.Equal(0, tracker.NakCount);

        Assert.Equal(b, Admitted(tracker, 12));
        Assert.Equal(0, tracker.ReclaimedWordCount);
        Assert.Equal(c, Admitted(tracker, 13));
    }

    [Fact]
    public void RejectBodyIds_StayDiscarded_OnlyTheOwnSequenceReclaims()
    {
        (InboundSequenceTracker tracker, _) = CreateTracker(9);
        IsaacRandom shadow = MakeIsaac(TrackerSeed);
        uint s1 = shadow.Next();
        uint s2 = shadow.Next();
        uint s3 = shadow.Next();
        uint s4 = shadow.Next();
        uint s5 = shadow.Next();

        // Encrypted 10, 11 (sealed s1, s2) are lost; encrypted 12 (s3)
        // arrives: parks s1 beside 10, s2 beside 11, decodes with s3.
        Assert.Equal(s3, Admitted(tracker, 12));
        Assert.Equal(2, tracker.NakCount);

        Assert.False(tracker.Admit(13, encrypted: false).Drop);
        Span<byte> ids = stackalloc byte[8];
        BinaryPrimitives.WriteUInt32LittleEndian(ids, 10u);
        BinaryPrimitives.WriteUInt32LittleEndian(ids.Slice(4), 11u);
        tracker.OnRejectRetransmit(ids, count: 2);
        tracker.OnCleartextRejectSequence(13);

        Assert.Equal(0, tracker.NakCount);
        Assert.Equal(1, tracker.ReclaimedWordCount); // s4 — 13's mis-park

        Assert.Equal(s4, Admitted(tracker, 14));
        Assert.Equal(s5, Admitted(tracker, 15));
        Assert.Equal(0, tracker.ReclaimedWordCount);
    }

    [Fact]
    public void TwoInterleavedRejects_InOrder_PoolPreservesAlignment()
    {
        (InboundSequenceTracker tracker, _) = CreateTracker(9);
        IsaacRandom shadow = MakeIsaac(TrackerSeed);
        uint s1 = shadow.Next();
        uint s2 = shadow.Next();

        Assert.False(tracker.Admit(10, encrypted: false).Drop);
        tracker.OnCleartextRejectSequence(10);
        Assert.False(tracker.Admit(11, encrypted: false).Drop);
        tracker.OnCleartextRejectSequence(11);
        Assert.Equal(0, tracker.NakCount);
        Assert.Equal(1, tracker.ReclaimedWordCount);

        Assert.Equal(s1, Admitted(tracker, 12));
        Assert.Equal(s2, Admitted(tracker, 13));
        Assert.Equal(0, tracker.ReclaimedWordCount);
    }

    [Fact]
    public void TwoInterleavedRejects_Crossed_PoolDrainsInDrawOrder()
    {
        (InboundSequenceTracker tracker, _) = CreateTracker(9);
        IsaacRandom shadow = MakeIsaac(TrackerSeed);
        uint s1 = shadow.Next();
        uint s2 = shadow.Next();
        uint s3 = shadow.Next();
        uint s4 = shadow.Next();
        uint s5 = shadow.Next();

        InboundSequenceTracker.Admission twelve =
            tracker.Admit(12, encrypted: true);
        Assert.Equal(s3, twelve.VerifyKey);
        tracker.ReparkKey(12, s3, twelve.VerifyKeyDrawOrder);
        InboundSequenceTracker.Admission thirteen =
            tracker.Admit(13, encrypted: true);
        Assert.Equal(s4, thirteen.VerifyKey);
        tracker.ReparkKey(13, s4, thirteen.VerifyKeyDrawOrder);
        Assert.Equal(4, tracker.NakCount);

        Assert.False(tracker.Admit(11, encrypted: false).Drop);
        tracker.OnCleartextRejectSequence(11);
        Assert.False(tracker.Admit(10, encrypted: false).Drop);
        tracker.OnCleartextRejectSequence(10);
        Assert.Equal(2, tracker.NakCount);          // 12, 13 remain
        Assert.Equal(2, tracker.ReclaimedWordCount); // s3, s4

        // The retransmissions decode with their TRUE words (the bubble
        // put s1 beside 12 and s2 beside 13).
        Assert.Equal(s1, Admitted(tracker, 12));
        Assert.Equal(s2, Admitted(tracker, 13));
        Assert.Equal(0, tracker.NakCount);

        Assert.Equal(s3, Admitted(tracker, 14));
        Assert.Equal(s4, Admitted(tracker, 15));
        Assert.Equal(0, tracker.ReclaimedWordCount);
        Assert.Equal(s5, Admitted(tracker, 16));
    }


    [Fact]
    public void S2CLoss_NakRoundTrip_ModelRetransmission_AcksResume()
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

            var messages = new List<string>();
            session.ServerMessageReceived += m => messages.Add(m.Message);

            transport.Link.DropNext(LinkDirection.ServerToClient);
            transport.Model.EnqueueGameMessage(
                BuildServerMessage("lost"), GameMessageGroup.UIQueue);
            transport.PumpServer();
            transport.Model.EnqueueGameMessage(
                BuildServerMessage("marker"), GameMessageGroup.UIQueue);
            transport.PumpServer();
            PumpUntil(session, () => session.Transport!.Inbound.NakCount == 1);
            PumpUntil(session, () => messages.Contains("marker"));

            long naksBefore = session.Transport!.Stats.NaksSent;
            long acksBefore = session.Transport.Stats.AcksSent;
            int servedBefore = transport.Model.RetransmitsServed;

            transport.Clock.Advance(TimeSpan.FromSeconds(0.7));
            session.Tick();
            Assert.Equal(naksBefore + 1, session.Transport.Stats.NaksSent);
            Assert.Equal(servedBefore + 1, transport.Model.RetransmitsServed);

            bool sawRetransmission = false;
            foreach (byte[] datagram in transport.Model.SentDatagrams)
            {
                if ((PacketHeader.Unpack(datagram).Flags
                     & PacketHeaderFlags.Retransmission) != 0)
                {
                    sawRetransmission = true;
                }
            }

            Assert.True(sawRetransmission);

            // The parked key decodes the retransmission; the message
            // dispatches and the set empties.
            PumpUntil(session, () => messages.Contains("lost"));
            Assert.Equal(0, session.Transport.Inbound.NakCount);
            Assert.Equal(0, session.Transport.Stats.ChecksumFailures);
            Assert.Equal(0, transport.Model.CrcDropCount);

            // The ack resumes 2.0 s after the NAK's stamp of the shared
            // timestamp.
            transport.Clock.Advance(TimeSpan.FromSeconds(2.1));
            session.Tick();
            Assert.True(session.Transport.Stats.AcksSent > acksBefore);
            Assert.False(transport.Model.IsTerminated);
        }
        finally
        {
            session.Dispose();
        }
    }

    [Fact]
    public void PrunedId_RejectAtFreshSequence_ReclaimKeepsTheStreamAligned()
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

            var messages = new List<string>();
            session.ServerMessageReceived += m => messages.Add(m.Message);

            // Open the gap: "victim" is eaten, "marker" parks its id.
            transport.Link.DropNext(LinkDirection.ServerToClient);
            transport.Model.EnqueueGameMessage(
                BuildServerMessage("victim"), GameMessageGroup.UIQueue);
            transport.PumpServer();
            transport.Model.EnqueueGameMessage(
                BuildServerMessage("marker"), GameMessageGroup.UIQueue);
            transport.PumpServer();
            PumpUntil(session, () => session.Transport!.Inbound.NakCount == 1);

            bool s2cBlocked = true;
            transport.Link.Drop(
                LinkDirection.ServerToClient, (_, _) => s2cBlocked);

            for (int step = 0; step < 260; step++)
            {
                transport.Clock.Advance(TimeSpan.FromMilliseconds(500));
                session.SendTalk($"keepalive {step}");
                transport.PumpServer();
                session.Tick();
                if ((step & 15) == 0)
                    Thread.Sleep(1);
            }

            Assert.False(transport.Model.IsTerminated);
            Assert.True(
                transport.Model.RetransmitsServed > 0,
                "the outage should have served retransmits into the void");

            s2cBlocked = false;
            DateTime deadline = DateTime.UtcNow.AddSeconds(20);
            while (session.Transport!.Inbound.NakCount > 0
                   && DateTime.UtcNow < deadline)
            {
                transport.Clock.Advance(TimeSpan.FromMilliseconds(700));
                transport.PumpServer();
                session.Tick();
                Thread.Sleep(1);
            }

            Assert.Equal(0, session.Transport.Inbound.NakCount);
            Assert.True(
                session.Transport.Stats.RejectWordsReclaimed >= 1,
                "at least one reject fresh-sequence mis-park must have been reclaimed");

            bool modelSentReject = false;
            foreach (byte[] datagram in transport.Model.SentDatagrams)
            {
                if ((PacketHeader.Unpack(datagram).Flags
                     & PacketHeaderFlags.RejectRetransmit) != 0)
                {
                    modelSentReject = true;
                }
            }

            Assert.True(modelSentReject);

            Assert.DoesNotContain("victim", messages);

            int drainTarget =
                session.Transport.Inbound.ReclaimedWordCount + 3;
            long checksumFailuresBefore =
                session.Transport.Stats.ChecksumFailures;
            for (int i = 0; i < drainTarget; i++)
            {
                transport.Model.EnqueueGameMessage(
                    BuildServerMessage($"post-heal {i}"),
                    GameMessageGroup.UIQueue);
                transport.PumpServer();
                int expected = i;
                PumpUntil(
                    session,
                    () => messages.Contains($"post-heal {expected}"));
            }

            Assert.Equal(
                checksumFailuresBefore,
                session.Transport.Stats.ChecksumFailures);
            Assert.Equal(0, session.Transport.Inbound.ReclaimedWordCount);
            Assert.Equal(0, session.Transport.Inbound.NakCount);
            Assert.Equal(256, transport.Model.Crypto.Headroom);
            Assert.False(transport.Model.IsTerminated);
        }
        finally
        {
            session.Dispose();
        }
    }

    [Fact]
    public void LongLoss_NaksOnTheGateCadence_NoAcks_GapHealsInsideTheWindow()
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

            var messages = new List<string>();
            session.ServerMessageReceived += m => messages.Add(m.Message);

            transport.Link.DropNext(LinkDirection.ServerToClient);
            transport.Model.EnqueueGameMessage(
                BuildServerMessage("lost"), GameMessageGroup.UIQueue);
            transport.PumpServer();
            transport.Model.EnqueueGameMessage(
                BuildServerMessage("marker"), GameMessageGroup.UIQueue);
            transport.PumpServer();
            PumpUntil(session, () => session.Transport!.Inbound.NakCount == 1);

            long naksBefore = session.Transport!.Stats.NaksSent;
            long acksBefore = session.Transport.Stats.AcksSent;

            bool s2cBlocked = true;
            transport.Link.Drop(
                LinkDirection.ServerToClient, (_, _) => s2cBlocked);
            for (int step = 0; step < 40; step++)
            {
                transport.Clock.Advance(TimeSpan.FromMilliseconds(250));
                transport.PumpServer();
                session.Tick();
                if ((step & 7) == 0)
                    Thread.Sleep(1);
            }

            long naksDuringWindow =
                session.Transport.Stats.NaksSent - naksBefore;
            Assert.InRange(naksDuringWindow, 11, 15);
            Assert.Equal(acksBefore, session.Transport.Stats.AcksSent);
            Assert.False(transport.Model.IsTerminated);

            // Heal: the next NAK round-trips, the parked key decodes the
            // retransmission, and the ack resumes 2.0 s after that NAK.
            s2cBlocked = false;
            DateTime deadline = DateTime.UtcNow.AddSeconds(15);
            while ((session.Transport.Inbound.NakCount > 0
                    || !messages.Contains("lost"))
                   && DateTime.UtcNow < deadline)
            {
                transport.Clock.Advance(TimeSpan.FromMilliseconds(700));
                transport.PumpServer();
                session.Tick();
                Thread.Sleep(1);
            }

            Assert.Contains("lost", messages);
            Assert.Equal(0, session.Transport.Inbound.NakCount);
            Assert.Equal(0, session.Transport.Stats.ChecksumFailures);

            transport.Clock.Advance(TimeSpan.FromSeconds(2.1));
            session.Tick();
            Assert.True(session.Transport.Stats.AcksSent > acksBefore);
            Assert.False(transport.Model.IsTerminated);
        }
        finally
        {
            session.Dispose();
        }
    }


    [Fact]
    // Lane=Timing: outcome depends on real elapsed time or OS scheduling.
    // A 2% bidirectional loss soak. Failed 1/3 stress rounds on Linux, and on
    // Windows the moment its assembly was serialized.
    [Trait("Lane", "Timing")]
    public async Task LossSoak_TwoPercentBidirectional_ZeroMessageLoss_LedgersConverge()
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

            int s2cReceived = 0;
            session.ServerMessageReceived += m =>
            {
                if (m.Message.StartsWith("s2c ", StringComparison.Ordinal))
                    s2cReceived++;
            };

            int c2sDispatched = 0;
            transport.Model.MessageDispatched += body =>
            {
                if (body.AsSpan().IndexOf("c2s "u8) >= 0)
                    c2sDispatched++;
            };

            transport.Link.RandomLoss(
                LinkDirection.ClientToServer, 0.02, seed: 0x5EED0001);
            transport.Link.RandomLoss(
                LinkDirection.ServerToClient, 0.02, seed: 0x5EED0002);

            const int MessagesEachWay = 5_000;
            for (int i = 0; i < MessagesEachWay; i++)
            {
                transport.Clock.Advance(TimeSpan.FromMilliseconds(25));
                session.SendTalk($"c2s {i}");
                transport.Model.EnqueueGameMessage(
                    BuildServerMessage($"s2c {i}"),
                    GameMessageGroup.UIQueue);
                transport.PumpServer();
                session.Tick();

                if ((i & 15) == 0)
                    await Task.Yield();

                if (i % 500 == 0)
                {
                    Assert.True(
                        transport.Model.Crypto.Headroom >= 250,
                        $"headroom {transport.Model.Crypto.Headroom} at "
                        + $"message {i} — the search window is eroding");
                    Assert.False(transport.Model.IsTerminated);
                }
            }

            int trickle = 0;
            const int MaxConvergenceSteps = 120; // 60 virtual seconds at 0.5 s/step
            for (int step = 0;
                 step < MaxConvergenceSteps
                 && (s2cReceived != MessagesEachWay
                     || c2sDispatched != MessagesEachWay);
                 step++)
            {
                transport.Clock.Advance(TimeSpan.FromMilliseconds(500));
                session.SendTalk($"trickle {trickle++}");
                transport.PumpServer();
                session.Tick();
                await Task.Yield();
            }

            int quietIterations = 0;
            for (int step = 0; step < MaxConvergenceSteps; step++)
            {
                transport.Clock.Advance(TimeSpan.FromMilliseconds(500));
                if (session.Transport!.Outbound.CacheDepth > 1
                    && ++quietIterations % 8 == 0)
                {
                    session.SendTalk($"trickle {trickle++}");
                }

                transport.PumpServer();
                session.Tick();
                await Task.Yield();

                if (s2cReceived == MessagesEachWay
                    && c2sDispatched == MessagesEachWay
                    && session.Transport.Inbound.NakCount == 0
                    && session.Transport.Inbound.ReclaimedWordCount == 0
                    && session.Transport.Outbound.PendingResendCount == 0
                    && session.Transport.Outbound.CacheDepth <= 1
                    && transport.Model.OutOfOrderPacketCount == 0)
                {
                    break;
                }
            }

            // Zero message loss, both directions.
            string ledger =
                $"s2c={s2cReceived} "
                + $"c2s={c2sDispatched} "
                + $"nak-set={session.Transport!.Inbound.NakCount} "
                + $"reclaim={session.Transport.Inbound.ReclaimedWordCount} "
                + $"pending={session.Transport.Outbound.PendingResendCount} "
                + $"cache={session.Transport.Outbound.CacheDepth} "
                + $"ooo={transport.Model.OutOfOrderPacketCount} "
                + $"fraggate={transport.Model.FragmentGateBufferCount} "
                + $"cksumfail={session.Transport.Stats.ChecksumFailures} "
                + $"dups={session.Transport.Stats.InboundDupsDropped} "
                + $"naks-sent={session.Transport.Stats.NaksSent} "
                + $"resends={session.Transport.Stats.ResendsSent} "
                + $"served={transport.Model.RetransmitsServed} "
                + $"headroom={transport.Model.Crypto.Headroom} "
                + $"terminated={transport.Model.IsTerminated}";
            Assert.True(
                s2cReceived == MessagesEachWay,
                $"S2C loss: {ledger}");
            Assert.True(
                c2sDispatched == MessagesEachWay,
                $"C2S loss: {ledger}");

            // The loss was real and both recovery directions fired.
            Assert.True(
                transport.Link.DroppedCount(LinkDirection.ClientToServer) > 0);
            Assert.True(
                transport.Link.DroppedCount(LinkDirection.ServerToClient) > 0);
            Assert.True(session.Transport!.Stats.ResendsSent > 0);
            Assert.True(session.Transport.Stats.NaksSent > 0);
            Assert.True(transport.Model.RetransmitsServed > 0);

            Assert.Equal(256, transport.Model.Crypto.Headroom);
            Assert.Equal(0, session.Transport.Inbound.NakCount);
            Assert.Equal(0, session.Transport.Inbound.ReclaimedWordCount);
            Assert.Equal(0, session.Transport.Outbound.PendingResendCount);
            Assert.True(
                session.Transport.Outbound.CacheDepth <= 1,
                $"cache depth {session.Transport.Outbound.CacheDepth} — "
                + "only the watermark entry may remain");
            Assert.Equal(0, transport.Model.OutOfOrderPacketCount);
            Assert.Equal(0, transport.Model.FragmentGateBufferCount);
            Assert.Equal(0, session.Transport.Stats.ChecksumFailures);

            // Alive at the end.
            Assert.False(transport.Model.IsTerminated);
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

    private static (InboundSequenceTracker Tracker, TransportStats Stats)
        CreateTracker(uint initialWatermark)
    {
        var stats = new TransportStats();
        return (
            new InboundSequenceTracker(
                MakeIsaac(TrackerSeed), stats, initialWatermark),
            stats);
    }

    private static void Admit(ReliableTransport transport, uint sequence)
    {
        InboundSequenceTracker.Admission admission =
            transport.Inbound.Admit(sequence, encrypted: true);
        Assert.False(admission.Drop);
    }

    private static uint Admitted(InboundSequenceTracker tracker, uint sequence)
    {
        InboundSequenceTracker.Admission admission =
            tracker.Admit(sequence, encrypted: true);
        Assert.False(admission.Drop);
        Assert.NotNull(admission.VerifyKey);
        return admission.VerifyKey!.Value;
    }

    private static byte[] MakeMessage(byte marker) =>
        new byte[] { marker, 0x11, 0x22, 0x33, 0x00, 0x00, 0x00, 0x00 };

    private static IsaacRandom MakeIsaac(uint seed)
    {
        Span<byte> seedBytes = stackalloc byte[4];
        BinaryPrimitives.WriteUInt32LittleEndian(seedBytes, seed);
        return new IsaacRandom(seedBytes);
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
