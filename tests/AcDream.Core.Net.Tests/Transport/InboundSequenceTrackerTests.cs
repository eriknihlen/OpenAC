using System.Buffers.Binary;
using System.Net;
using AcDream.Core.Net.Cryptography;
using AcDream.Core.Net.Messages;
using AcDream.Core.Net.Packets;
using AcDream.Core.Net.Transport;

namespace AcDream.Core.Net.Tests.Transport;

public sealed class InboundSequenceTrackerTests
{
    private const uint Seed = 0x5EED5EEDu;


    [Fact]
    public void GapWalk_ParksTheMissingKey_LaterPacketsAndLateArrivalDecode()
    {
        (InboundSequenceTracker tracker, TransportStats stats) =
            CreateTracker(initialWatermark: 9);
        IsaacRandom shadow = MakeIsaac(Seed);
        uint w10 = shadow.Next();
        uint w11 = shadow.Next();
        uint w12 = shadow.Next();
        uint w13 = shadow.Next();
        uint w14 = shadow.Next();
        uint w15 = shadow.Next();

        // In-order packets draw in sequence order.
        Assert.Equal(w10, Admitted(tracker, 10));
        Assert.Equal(w11, Admitted(tracker, 11));

        Assert.Equal(w13, Admitted(tracker, 13));
        Assert.Equal(1, tracker.NakCount);
        Assert.Equal(1, stats.KeysParked);
        Assert.Equal(13u, tracker.HighestIdReceived);

        // 14 keeps flowing on the aligned stream.
        Assert.Equal(w14, Admitted(tracker, 14));

        Assert.Equal(w12, Admitted(tracker, 12));
        Assert.Equal(0, tracker.NakCount);
        Assert.Equal(14u, tracker.HighestIdReceived);

        // And the next fresh packet takes the next fresh word.
        Assert.Equal(w15, Admitted(tracker, 15));
        Assert.Equal(0, stats.InboundDupsDropped);
        Assert.Equal(0, stats.InboundSanityDrops);
    }


    [Fact]
    public void Duplicate_NeverNakked_DropsAtZeroKeystreamCost()
    {
        (InboundSequenceTracker tracker, TransportStats stats) =
            CreateTracker(initialWatermark: 9);
        IsaacRandom shadow = MakeIsaac(Seed);
        uint w10 = shadow.Next();
        uint w11 = shadow.Next();

        Assert.Equal(w10, Admitted(tracker, 10));

        // A duplicate of the already-decoded 10: dropped BEFORE any
        // keystream access (pre-N2 this burned one word and shifted the
        // stream).
        InboundSequenceTracker.Admission dup = tracker.Admit(10, encrypted: true);
        Assert.True(dup.Drop);
        Assert.Equal(1, stats.InboundDupsDropped);

        // The shadow position is unchanged: 11 draws the very next word.
        Assert.Equal(w11, Admitted(tracker, 11));
    }

    [Fact]
    public void ChecksumFailureRepark_TheRetransmissionDecodesWithTheSameKey()
    {
        (InboundSequenceTracker tracker, TransportStats stats) =
            CreateTracker(initialWatermark: 9);
        IsaacRandom shadow = MakeIsaac(Seed);
        uint w10 = shadow.Next();
        uint w11 = shadow.Next();

        InboundSequenceTracker.Admission corrupt =
            tracker.Admit(10, encrypted: true);
        Assert.False(corrupt.Drop);
        Assert.Equal(w10, corrupt.VerifyKey);
        tracker.ReparkKey(10, w10, corrupt.VerifyKeyDrawOrder);
        Assert.Equal(1, tracker.NakCount);
        Assert.Equal(1, stats.KeysParked);

        // The byte-identical retransmission decodes with the SAME word.
        Assert.Equal(w10, Admitted(tracker, 10));
        Assert.Equal(0, tracker.NakCount);

        // Alignment held throughout.
        Assert.Equal(w11, Admitted(tracker, 11));
    }


    [Fact]
    public void Cleartext_AtHighestPlusOne_NaksItsOwnBorrowedId()
    {
        (InboundSequenceTracker tracker, TransportStats stats) =
            CreateTracker(initialWatermark: 9);
        IsaacRandom shadow = MakeIsaac(Seed);
        uint w10 = shadow.Next();
        uint w11 = shadow.Next();

        InboundSequenceTracker.Admission cleartext =
            tracker.Admit(10, encrypted: false);
        Assert.False(cleartext.Drop);
        Assert.Null(cleartext.VerifyKey);
        Assert.Equal(10u, tracker.HighestIdReceived);
        Assert.Equal(1, tracker.NakCount);
        Assert.Equal(1, stats.KeysParked);

        // The real encrypted 10 arrives later: parked key, exact word.
        Assert.Equal(w10, Admitted(tracker, 10));
        Assert.Equal(0, tracker.NakCount);
        Assert.Equal(w11, Admitted(tracker, 11));
    }

    [Fact]
    public void Cleartext_AtHighest_NoNak_NoKey_NoWatermarkChange()
    {
        (InboundSequenceTracker tracker, TransportStats stats) =
            CreateTracker(initialWatermark: 9);
        IsaacRandom shadow = MakeIsaac(Seed);
        uint w10 = shadow.Next();
        uint w11 = shadow.Next();

        Assert.Equal(w10, Admitted(tracker, 10));

        InboundSequenceTracker.Admission ack =
            tracker.Admit(10, encrypted: false);
        Assert.False(ack.Drop);
        Assert.Null(ack.VerifyKey);
        Assert.Equal(10u, tracker.HighestIdReceived);
        Assert.Equal(0, tracker.NakCount);
        Assert.Equal(0, stats.KeysParked);

        Assert.Equal(w11, Admitted(tracker, 11));
    }


    [Fact]
    public void SanityWindow_HighestPlus0x7FFF_Accepted_OnePastIt_Dropped()
    {
        (InboundSequenceTracker accepted, _) =
            CreateTracker(initialWatermark: 100);
        InboundSequenceTracker.Admission atBoundary =
            accepted.Admit(100u + 0x7FFFu, encrypted: true);
        Assert.False(atBoundary.Drop);
        Assert.Equal(100u + 0x7FFFu, accepted.HighestIdReceived);

        (InboundSequenceTracker dropped, TransportStats stats) =
            CreateTracker(initialWatermark: 100);
        IsaacRandom shadow = MakeIsaac(Seed);
        uint w101 = shadow.Next();
        InboundSequenceTracker.Admission pastBoundary =
            dropped.Admit(100u + 0x8000u, encrypted: true);
        Assert.True(pastBoundary.Drop);
        Assert.Equal(1, stats.InboundSanityDrops);
        Assert.Equal(100u, dropped.HighestIdReceived);
        Assert.Equal(0, dropped.NakCount);
        Assert.Equal(w101, Admitted(dropped, 101));
    }

    [Fact]
    public void SanityWindow_IsWrapSafe()
    {
        // Watermark near the 32-bit wrap: the horizon lands past 0.
        (InboundSequenceTracker tracker, TransportStats stats) =
            CreateTracker(initialWatermark: 0xFFFFFF00u);

        // watermark + 0x8000 wraps to 0x7F00 — still one past the horizon,
        // still dropped.
        InboundSequenceTracker.Admission pastBoundary =
            tracker.Admit(unchecked(0xFFFFFF00u + 0x8000u), encrypted: true);
        Assert.True(pastBoundary.Drop);
        Assert.Equal(1, stats.InboundSanityDrops);

        InboundSequenceTracker.Admission postWrap =
            tracker.Admit(3u, encrypted: true);
        Assert.False(postWrap.Drop);
        Assert.Equal(3u, tracker.HighestIdReceived);
    }

    [Fact]
    public void GapWalk_SkipsSequenceZero_AcrossTheWrap()
    {
        (InboundSequenceTracker tracker, _) =
            CreateTracker(initialWatermark: 0xFFFFFFFEu);
        IsaacRandom shadow = MakeIsaac(Seed);
        uint wMax = shadow.Next();
        uint w1 = shadow.Next();    // parked for 1 (0 skipped between)
        uint w2 = shadow.Next();    // the arriving packet's own key

        Assert.Equal(w2, Admitted(tracker, 2));
        Assert.Equal(2, tracker.NakCount);

        var naks = new List<uint>();
        tracker.CopyNakkedSequencesAscending(naks);
        Assert.Equal(new uint[] { 1u, 0xFFFFFFFFu }, naks);

        // Both parked keys decode their late arrivals.
        Assert.Equal(wMax, Admitted(tracker, 0xFFFFFFFFu));
        Assert.Equal(w1, Admitted(tracker, 1));
        Assert.Equal(0, tracker.NakCount);
    }


    [Fact]
    public void RejectRetransmit_RemovesIds_AndTheStreamStaysAligned()
    {
        (InboundSequenceTracker tracker, TransportStats stats) =
            CreateTracker(initialWatermark: 9);
        IsaacRandom shadow = MakeIsaac(Seed);
        uint w10 = shadow.Next();
        _ = shadow.Next();          // w11 — parked, then abandoned
        _ = shadow.Next();          // w12 — parked, then abandoned
        uint w13 = shadow.Next();
        uint w14 = shadow.Next();

        Assert.Equal(w10, Admitted(tracker, 10));
        Assert.Equal(w13, Admitted(tracker, 13)); // parks 11 + 12
        Assert.Equal(2, tracker.NakCount);

        // The server answers RejectRetransmit [11, 12]: silent abandonment.
        Span<byte> ids = stackalloc byte[8];
        BinaryPrimitives.WriteUInt32LittleEndian(ids, 11u);
        BinaryPrimitives.WriteUInt32LittleEndian(ids.Slice(4), 12u);
        tracker.OnRejectRetransmit(ids, count: 2);
        Assert.Equal(0, tracker.NakCount);

        // Alignment holds: the words were already drawn in sequence order.
        Assert.Equal(w14, Admitted(tracker, 14));

        Assert.True(tracker.Admit(11, encrypted: true).Drop);
        Assert.Equal(1, stats.InboundDupsDropped);
    }

    // =====================================================================
    // Zero-alloc steady state
    // =====================================================================

    [Fact]
    public void WarmAdmit_NoGap_AllocatesNothing()
    {
        (InboundSequenceTracker tracker, _) = CreateTracker(initialWatermark: 1);
        uint sequence = 2;
        for (int i = 0; i < 128; i++)
            _ = tracker.Admit(sequence++, encrypted: true);

        long before = GC.GetAllocatedBytesForCurrentThread();
        for (int i = 0; i < 1_000; i++)
            _ = tracker.Admit(sequence++, encrypted: true);
        long allocated = GC.GetAllocatedBytesForCurrentThread() - before;

        Assert.Equal(0, allocated);
    }


    [Fact]
    public void CleanLifecycle_ZeroNaks_ZeroSpuriousDrops()
    {
        var transport = new FakeAceTransport();
        var session = new WorldSession(
            new IPEndPoint(IPAddress.Loopback, 9000),
            transport);
        try
        {
            session.Connect("testaccount", "testpassword", TimeSpan.FromSeconds(10));
            AssertNoInboundFaults(session);

            session.EnterWorld(0, TimeSpan.FromSeconds(10));
            AssertNoInboundFaults(session);

            var messages = new List<string>();
            session.ServerMessageReceived += m => messages.Add(m.Message);
            transport.Model.EnqueueGameMessage(
                BuildServerMessage("clean lifecycle"),
                GameMessageGroup.UIQueue);
            transport.PumpServer();
            PumpUntil(session, () => messages.Count > 0);
            Assert.Equal("clean lifecycle", Assert.Single(messages));
            AssertNoInboundFaults(session);

            // The model's dance, pinned: the first ENCRYPTED sequenced S2C
            // packet is sequence 2 (never 1) — the fact the init watermark
            // adaptation is built on.
            uint minEncrypted = uint.MaxValue;
            uint maxSequence = 0;
            foreach (byte[] datagram in transport.Model.SentDatagrams)
            {
                PacketHeader header = PacketHeader.Unpack(datagram);
                if (header.HasFlag(PacketHeaderFlags.EncryptedChecksum)
                    && header.Sequence != 0
                    && header.Sequence < minEncrypted)
                {
                    minEncrypted = header.Sequence;
                }

                maxSequence = SequenceMath.Max(maxSequence, header.Sequence);
            }

            Assert.Equal(2u, minEncrypted);

            Assert.Equal(
                maxSequence,
                session.Transport!.Inbound.HighestIdReceived);
        }
        finally
        {
            session.Dispose();
        }

        Assert.Equal(WorldSession.State.Disconnected, session.CurrentState);
        Assert.Equal(0, transport.Model.CrcDropCount);
    }

    [Fact]
    // Lane=Timing: outcome depends on real elapsed time or OS scheduling.
    // Late-redelivery ordering under simulated loss. Failed 1/3 stress rounds.
    [Trait("Lane", "Timing")]
    public void S2CLoss_LaterPacketsStillDecode_LateRedeliveryCompletesTheMessage()
    {
        var transport = new FakeAceTransport();
        var session = new WorldSession(
            new IPEndPoint(IPAddress.Loopback, 9000),
            transport);
        try
        {
            session.Connect("testaccount", "testpassword", TimeSpan.FromSeconds(10));
            session.EnterWorld(0, TimeSpan.FromSeconds(10));

            var messages = new List<string>();
            session.ServerMessageReceived += m => messages.Add(m.Message);

            string bigText = new string('x', 600);
            transport.Link.DropNext(LinkDirection.ServerToClient);
            transport.Model.EnqueueGameMessage(
                BuildServerMessage(bigText),
                GameMessageGroup.UIQueue);
            transport.PumpServer();
            PumpUntil(
                session,
                () => session.Transport!.Stats.KeysParked > 0);

            Assert.Equal(1, session.Transport!.Inbound.NakCount);
            Assert.Equal(1, session.Transport.Stats.KeysParked);
            Assert.Empty(messages);

            transport.Model.EnqueueGameMessage(
                BuildServerMessage("after the loss"),
                GameMessageGroup.UIQueue);
            transport.PumpServer();
            PumpUntil(session, () => messages.Count > 0);
            Assert.Equal("after the loss", Assert.Single(messages));
            Assert.Equal(0, session.Transport.Stats.ChecksumFailures);

            var parked = new List<uint>();
            session.Transport.Inbound.CopyNakkedSequencesAscending(parked);
            uint missingSequence = Assert.Single(parked);
            byte[]? droppedDatagram = null;
            foreach (byte[] datagram in transport.Model.SentDatagrams)
            {
                if (PacketHeader.Unpack(datagram).Sequence == missingSequence)
                {
                    droppedDatagram = datagram;
                    break;
                }
            }

            Assert.NotNull(droppedDatagram);
            transport.InjectServerDatagram(droppedDatagram!);
            PumpUntil(session, () => messages.Count > 1);

            Assert.Equal(2, messages.Count);
            Assert.Equal(bigText, messages[1]);
            Assert.Equal(0, session.Transport.Inbound.NakCount);
            Assert.Equal(0, session.Transport.Stats.InboundDupsDropped);
            Assert.Equal(0, session.Transport.Stats.ChecksumFailures);
        }
        finally
        {
            session.Dispose();
        }
    }

    [Fact]
    public void DuplicateServerPacket_DropsBeforeDispatch()
    {
        var transport = new FakeAceTransport();
        var session = new WorldSession(
            new IPEndPoint(IPAddress.Loopback, 9000),
            transport);
        try
        {
            session.Connect("testaccount", "testpassword", TimeSpan.FromSeconds(10));
            session.EnterWorld(0, TimeSpan.FromSeconds(10));

            var messages = new List<string>();
            session.ServerMessageReceived += m => messages.Add(m.Message);

            transport.Model.EnqueueGameMessage(
                BuildServerMessage("once only"),
                GameMessageGroup.UIQueue);
            transport.PumpServer();
            PumpUntil(session, () => messages.Count > 0);
            Assert.Equal("once only", Assert.Single(messages));

            byte[]? carrier = null;
            foreach (byte[] datagram in transport.Model.SentDatagrams)
            {
                PacketHeader header = PacketHeader.Unpack(datagram);
                if (header.HasFlag(PacketHeaderFlags.BlobFragments)
                    && header.Sequence
                        == session.Transport!.Inbound.HighestIdReceived)
                {
                    carrier = datagram;
                }
            }

            Assert.NotNull(carrier);
            transport.InjectServerDatagram(carrier!);
            PumpUntil(
                session,
                () => session.Transport!.Stats.InboundDupsDropped > 0);

            // Dropped before dispatch: the message did NOT arrive twice.
            Assert.Single(messages);
            Assert.Equal(1, session.Transport!.Stats.InboundDupsDropped);

            // And the keystream did not move: fresh traffic still decodes.
            transport.Model.EnqueueGameMessage(
                BuildServerMessage("still aligned"),
                GameMessageGroup.UIQueue);
            transport.PumpServer();
            PumpUntil(session, () => messages.Count > 1);
            Assert.Equal("still aligned", messages[1]);
            Assert.Equal(0, session.Transport.Stats.ChecksumFailures);
        }
        finally
        {
            session.Dispose();
        }
    }

    [Fact]
    public void SequenceZero_CleartextBypassesTracker_EncryptedDrops()
    {
        var transport = new FakeAceTransport();
        var session = new WorldSession(
            new IPEndPoint(IPAddress.Loopback, 9000),
            transport);
        try
        {
            session.Connect("testaccount", "testpassword", TimeSpan.FromSeconds(10));
            session.EnterWorld(0, TimeSpan.FromSeconds(10));

            uint watermarkBefore = session.Transport!.Inbound.HighestIdReceived;
            long parkedBefore = session.Transport.Stats.KeysParked;

            var serverTimes = new List<double>();
            session.ServerTimeUpdated += t => serverTimes.Add(t);

            byte[] timeSyncBody = new byte[8];
            BinaryPrimitives.WriteInt64LittleEndian(
                timeSyncBody,
                BitConverter.DoubleToInt64Bits(777.5));
            transport.InjectServerDatagram(PacketCodec.Encode(
                new PacketHeader
                {
                    Sequence = 0,
                    Flags = PacketHeaderFlags.TimeSync,
                },
                timeSyncBody,
                outboundIsaac: null));
            PumpUntil(session, () => serverTimes.Contains(777.5));

            Assert.Equal(
                watermarkBefore,
                session.Transport.Inbound.HighestIdReceived);
            Assert.Equal(0, session.Transport.Inbound.NakCount);
            Assert.Equal(parkedBefore, session.Transport.Stats.KeysParked);

            var throwawayIsaac = MakeIsaac(0xDEADBEEFu);
            transport.InjectServerDatagram(PacketCodec.Encode(
                new PacketHeader
                {
                    Sequence = 0,
                    Flags = PacketHeaderFlags.TimeSync
                        | PacketHeaderFlags.EncryptedChecksum,
                },
                timeSyncBody,
                throwawayIsaac));

            var messages = new List<string>();
            session.ServerMessageReceived += m => messages.Add(m.Message);
            transport.Model.EnqueueGameMessage(
                BuildServerMessage("wheel intact"),
                GameMessageGroup.UIQueue);
            transport.PumpServer();
            PumpUntil(session, () => messages.Count > 0);
            Assert.Equal("wheel intact", Assert.Single(messages));
            Assert.Equal(0, session.Transport.Stats.ChecksumFailures);
        }
        finally
        {
            session.Dispose();
        }
    }

    // =====================================================================
    // Fixture helpers
    // =====================================================================

    private static (InboundSequenceTracker Tracker, TransportStats Stats)
        CreateTracker(uint initialWatermark)
    {
        var stats = new TransportStats();
        return (
            new InboundSequenceTracker(MakeIsaac(Seed), stats, initialWatermark),
            stats);
    }

    /// <summary>Admit an encrypted packet that must NOT drop; returns the
    /// verify key the tracker handed out.</summary>
    private static uint Admitted(InboundSequenceTracker tracker, uint sequence)
    {
        InboundSequenceTracker.Admission admission =
            tracker.Admit(sequence, encrypted: true);
        Assert.False(admission.Drop);
        Assert.NotNull(admission.VerifyKey);
        return admission.VerifyKey!.Value;
    }

    private static void AssertNoInboundFaults(WorldSession session)
    {
        AcDream.Core.Net.Transport.ReliableTransport? transport = session.Transport;
        Assert.NotNull(transport);
        Assert.Equal(0, transport!.Inbound.NakCount);
        Assert.Equal(0, transport.Stats.KeysParked);
        Assert.Equal(0, transport.Stats.InboundDupsDropped);
        Assert.Equal(0, transport.Stats.InboundSanityDrops);
        Assert.Equal(0, transport.Stats.ChecksumFailures);
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

    private static IsaacRandom MakeIsaac(uint seed)
    {
        Span<byte> seedBytes = stackalloc byte[4];
        BinaryPrimitives.WriteUInt32LittleEndian(seedBytes, seed);
        return new IsaacRandom(seedBytes);
    }
}
