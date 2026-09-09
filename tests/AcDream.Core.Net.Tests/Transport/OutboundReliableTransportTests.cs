using System.Buffers;
using System.Buffers.Binary;
using System.Net;
using AcDream.Core.Net.Cryptography;
using AcDream.Core.Net.Messages;
using AcDream.Core.Net.Packets;
using AcDream.Core.Net.Transport;

namespace AcDream.Core.Net.Tests.Transport;

public sealed class OutboundReliableTransportTests
{
    private const uint ClientSeed = 0x11AA22BBu;
    private const uint ServerSeed = 0x33CC44DDu;
    private const uint ClientId = 0x1234u;
    private const ushort SessionIteration = 0x0007;
    private const ulong Cookie = 0xFEEDFACECAFEBABEUL;


    [Fact]
    public void SequenceMath_IsNewer_IsWrapSafe()
    {
        Assert.True(SequenceMath.IsNewer(2u, 1u));
        Assert.False(SequenceMath.IsNewer(1u, 2u));
        Assert.False(SequenceMath.IsNewer(7u, 7u));

        Assert.True(SequenceMath.IsNewer(1u, uint.MaxValue));
        Assert.False(SequenceMath.IsNewer(uint.MaxValue, 1u));

        Assert.Equal(5u, SequenceMath.Max(5u, 3u));
        Assert.Equal(5u, SequenceMath.Max(3u, 5u));
        // Wrap-safe max: a small post-wrap value beats a huge pre-wrap one.
        Assert.Equal(5u, SequenceMath.Max(0xFFFFFFF6u, 5u));
    }


    [Fact]
    public void TransportClock_StartsAtOne_AdvancesEveryHalfSecond_AndWraps()
    {
        var virtualClock = new VirtualClock();
        var clock = new TransportClock(
            virtualClock.GetTimestamp,
            virtualClock.Frequency);
        Assert.Equal((ushort)1, clock.IntervalId);

        // Under half a second: no advance.
        virtualClock.Advance(TimeSpan.FromSeconds(0.49));
        clock.Update();
        Assert.Equal((ushort)1, clock.IntervalId);

        virtualClock.Advance(TimeSpan.FromSeconds(0.01));
        clock.Update();
        Assert.Equal((ushort)2, clock.IntervalId);

        // A long gap advances by the whole number of elapsed intervals,
        // preserving the fractional remainder.
        virtualClock.Advance(TimeSpan.FromSeconds(2.75));
        clock.Update();
        Assert.Equal((ushort)7, clock.IntervalId);
        virtualClock.Advance(TimeSpan.FromSeconds(0.25));
        clock.Update();
        Assert.Equal((ushort)8, clock.IntervalId);

        // Natural ushort wrap: 65531 more intervals take 8 → 3 (mod 65536).
        virtualClock.Advance(TimeSpan.FromSeconds(0.5 * 65531));
        clock.Update();
        Assert.Equal((ushort)3, clock.IntervalId);
    }


    [Fact]
    public void SentPacketStore_FifoContainsAndStrictFlush()
    {
        var pool = new CountingPool();
        using var store = new SentPacketStore(pool);
        store.Add(RentedEntry(pool, 2u), optionalLength: 0);
        store.Add(RentedEntry(pool, 3u), optionalLength: 0);
        store.Add(RentedEntry(pool, 4u), optionalLength: 0);
        Assert.Equal(3, store.Count);
        Assert.True(store.Contains(3u));
        Assert.False(store.Contains(5u));
        Assert.True(store.TryGet(2u, out SentPacketStore.CachedPacket got));
        Assert.Equal(2u, got.Sequence);

        store.FlushOlderThan(3u);
        Assert.Equal(2, store.Count);
        Assert.False(store.Contains(2u));
        Assert.True(store.Contains(3u));
        Assert.True(store.Contains(4u));
        Assert.Equal(1, pool.Returned);

        store.FlushOlderThan(5u);
        Assert.Equal(0, store.Count);
        Assert.Equal(3, pool.Returned);
        Assert.Equal(pool.Rented, pool.Returned);
    }

    [Fact]
    public void SentPacketStore_FlushIsWrapSafe_AcrossTheSequenceWrap()
    {
        var pool = new CountingPool();
        using var store = new SentPacketStore(pool);
        store.Add(RentedEntry(pool, 0xFFFFFFFEu), optionalLength: 0);
        store.Add(RentedEntry(pool, 0xFFFFFFFFu), optionalLength: 0);
        store.Add(RentedEntry(pool, 1u), optionalLength: 0);
        store.Add(RentedEntry(pool, 2u), optionalLength: 0);

        store.FlushOlderThan(1u);
        Assert.Equal(2, store.Count);
        Assert.False(store.Contains(0xFFFFFFFEu));
        Assert.False(store.Contains(0xFFFFFFFFu));
        Assert.True(store.Contains(1u));
        Assert.True(store.Contains(2u));
        Assert.Equal(2, pool.Returned);

        store.FlushOlderThan(3u);
        Assert.Equal(0, store.Count);
        Assert.Equal(pool.Rented, pool.Returned);
    }

    [Fact]
    public void SentPacketStore_Dispose_ReturnsEveryRentedBuffer()
    {
        var pool = new CountingPool();
        var store = new SentPacketStore(pool);
        store.Add(RentedEntry(pool, 2u), optionalLength: 0);
        store.Add(RentedEntry(pool, 3u), optionalLength: 0);
        store.Dispose();
        Assert.Equal(pool.Rented, pool.Returned);
    }

    [Fact]
    public void SentPacketStore_Add_AssertsNoOptionalHeaders()
    {
        var pool = new CountingPool();
        using var store = new SentPacketStore(pool);
        SentPacketStore.CachedPacket entry = RentedEntry(pool, 2u);
        Assert.Throws<InvalidOperationException>(
            () => store.Add(entry, optionalLength: 4));
        pool.Return(entry.Buffer); // the failed Add never took ownership
    }


    [Fact]
    public void Resend_RebuildsHeaderOnly_FlagsTimeChecksum_BodyBitIdentical()
    {
        (OutboundFlowQueue queue, VirtualClock virtualClock,
            TransportClock clock, TransportStats stats, List<byte[]> sent) =
            CreateQueue();

        queue.SendGameMessage(MakeMessage(0xA1), GameMessageGroup.UIQueue);
        byte[] original = Assert.Single(sent);
        PacketHeader originalHeader = PacketHeader.Unpack(original);
        Assert.Equal(2u, originalHeader.Sequence);
        Assert.Equal(
            PacketHeaderFlags.BlobFragments | PacketHeaderFlags.EncryptedChecksum,
            originalHeader.Flags);
        Assert.Equal((ushort)1, originalHeader.Time);
        Assert.Equal(SessionIteration, originalHeader.Iteration);

        // 1.2 s later (interval id 1 → 3) the server NAKs sequence 2.
        virtualClock.Advance(TimeSpan.FromSeconds(1.2));
        clock.Update();
        Nak(queue, 2u);
        sent.Clear();
        queue.TransmitPendingResends();

        byte[] resent = Assert.Single(sent);
        PacketHeader resentHeader = PacketHeader.Unpack(resent);

        Assert.Equal(
            PacketHeaderFlags.Retransmission
            | PacketHeaderFlags.EncryptedChecksum
            | PacketHeaderFlags.BlobFragments,
            resentHeader.Flags);
        Assert.Equal((uint)7, (uint)resentHeader.Flags);
        Assert.Equal((ushort)3, resentHeader.Time);
        Assert.Equal(originalHeader.Sequence, resentHeader.Sequence);
        Assert.Equal(originalHeader.DataSize, resentHeader.DataSize);
        Assert.Equal(originalHeader.Id, resentHeader.Id);
        Assert.Equal(originalHeader.Iteration, resentHeader.Iteration);

        uint sealedChecksum =
            originalHeader.Checksum - originalHeader.CalculateHeaderHash32();
        Assert.Equal(
            resentHeader.CalculateHeaderHash32() + sealedChecksum,
            resentHeader.Checksum);

        // Body bytes bit-identical.
        Assert.Equal(
            original.AsSpan(PacketHeader.Size).ToArray(),
            resent.AsSpan(PacketHeader.Size).ToArray());
        Assert.Equal(1, stats.ResendsSent);
        Assert.Equal(1, stats.NakRequestsReceived);
        Assert.Equal(0, stats.UncachedNakIds);
    }

    [Fact]
    public void BuildResendHeader_WithoutFragments_FlagsAreExactlyThree()
    {
        // Fragmentless reliable packets do not exist on the N1 send path
        // (every reliable message rides a fragment), but the rebuild rule is
        // pinned for both shapes: 3 without fragments, 7 with.
        byte[] buffer = new byte[PacketHeader.Size];
        var header = new PacketHeader
        {
            Sequence = 9u,
            Flags = PacketHeaderFlags.EncryptedChecksum,
            Id = 0x1234,
            DataSize = 0,
        };
        header.Pack(buffer);
        var cached = new SentPacketStore.CachedPacket(
            9u, buffer, bodyLength: 0, sealedChecksum: 0xDEADBEEFu,
            isaacKey: 0u, hasFragments: false);

        PacketHeader rebuilt =
            OutboundFlowQueue.BuildResendHeader(in cached, intervalId: 42);
        Assert.Equal(
            PacketHeaderFlags.Retransmission | PacketHeaderFlags.EncryptedChecksum,
            rebuilt.Flags);
        Assert.Equal((uint)3, (uint)rebuilt.Flags);
        Assert.Equal((ushort)42, rebuilt.Time);
        Assert.Equal(9u, rebuilt.Sequence);
        Assert.Equal(
            rebuilt.CalculateHeaderHash32() + 0xDEADBEEFu,
            rebuilt.Checksum);
    }

    [Fact]
    public void Resend_ConsumesNoOutboundIsaacWord()
    {
        (OutboundFlowQueue queue, _, _, _, List<byte[]> sent) = CreateQueue();
        IsaacRandom shadow = MakeIsaac(ClientSeed);
        uint w1 = shadow.Next();
        uint w2 = shadow.Next();
        uint w3 = shadow.Next();

        queue.SendGameMessage(MakeMessage(0xA1), GameMessageGroup.UIQueue); // seq 2, w1
        queue.SendGameMessage(MakeMessage(0xB2), GameMessageGroup.UIQueue); // seq 3, w2
        Assert.Equal(w1, ExtractIsaacKey(sent[0]));
        Assert.Equal(w2, ExtractIsaacKey(sent[1]));

        Nak(queue, 2u);
        sent.Clear();
        queue.TransmitPendingResends();
        Assert.Equal(w1, ExtractIsaacKey(Assert.Single(sent)));

        // The wheel did not move: the next fresh packet takes w3.
        sent.Clear();
        queue.SendGameMessage(MakeMessage(0xC3), GameMessageGroup.UIQueue); // seq 4, w3
        Assert.Equal(w3, ExtractIsaacKey(Assert.Single(sent)));
    }

    [Fact]
    public void Nak_ForUncachedId_SendsNothing_AndCounts()
    {
        (OutboundFlowQueue queue, _, _, TransportStats stats, List<byte[]> sent) =
            CreateQueue();
        queue.SendGameMessage(MakeMessage(0xA1), GameMessageGroup.UIQueue); // seq 2
        sent.Clear();

        Nak(queue, 40u);
        queue.TransmitPendingResends();
        Assert.Empty(sent);
        Assert.Equal(1, stats.UncachedNakIds);
        Assert.Equal(0, stats.ResendsSent);
        Assert.Equal(0, queue.PendingResendCount);
    }

    [Fact]
    public void NakFirstId_FoldsTheAckWatermark_AndSweepPrunesStrictlyBelow()
    {
        (OutboundFlowQueue queue, _, _, TransportStats stats, List<byte[]> sent) =
            CreateQueue();
        queue.SendGameMessage(MakeMessage(0xA1), GameMessageGroup.UIQueue); // seq 2
        queue.SendGameMessage(MakeMessage(0xB2), GameMessageGroup.UIQueue); // seq 3
        queue.SendGameMessage(MakeMessage(0xC3), GameMessageGroup.UIQueue); // seq 4
        Assert.Equal(3, queue.CacheDepth);
        sent.Clear();

        Nak(queue, 4u);
        Assert.Equal(4u, queue.AckWatermark);
        queue.TransmitPendingResends();
        Assert.Equal(4u, PacketHeader.Unpack(Assert.Single(sent)).Sequence);
        Assert.Equal(1, queue.CacheDepth);
        Assert.True(stats.AcksConsumed >= 1);
    }

    [Fact]
    public void FreshSend_StampsCurrentIntervalId_ResendRestampsNewer()
    {
        (OutboundFlowQueue queue, VirtualClock virtualClock,
            TransportClock clock, _, List<byte[]> sent) = CreateQueue();

        // K interval ticks before the send: 2.5 s = 5 intervals, id 1 → 6.
        virtualClock.Advance(TimeSpan.FromSeconds(2.5));
        clock.Update();
        Assert.Equal((ushort)6, clock.IntervalId);
        queue.SendGameMessage(MakeMessage(0xA1), GameMessageGroup.UIQueue);
        PacketHeader freshHeader = PacketHeader.Unpack(Assert.Single(sent));
        Assert.Equal((ushort)6, freshHeader.Time);
        Assert.Equal(SessionIteration, freshHeader.Iteration);

        virtualClock.Advance(TimeSpan.FromSeconds(1.0));
        clock.Update();
        Assert.Equal((ushort)8, clock.IntervalId);
        Nak(queue, 2u);
        sent.Clear();
        queue.TransmitPendingResends();
        PacketHeader resentHeader = PacketHeader.Unpack(Assert.Single(sent));
        Assert.Equal((ushort)8, resentHeader.Time);
        Assert.Equal(SessionIteration, resentHeader.Iteration);
    }

    [Fact]
    public void OnAckSequence_IsWrapSafeMax_AndNeverRegresses()
    {
        (OutboundFlowQueue queue, _, _, _, _) = CreateQueue();
        queue.OnAckSequence(10u);
        Assert.Equal(10u, queue.AckWatermark);
        queue.OnAckSequence(3u); // stale ack must not roll the watermark back
        Assert.Equal(10u, queue.AckWatermark);

        // Across the wrap: walk the watermark up in half-window-safe steps
        // (like a live sequence stream does), then a small post-wrap value
        // is NEWER than the huge pre-wrap one — and the reverse is stale.
        (OutboundFlowQueue wrapQueue, _, _, _, _) = CreateQueue();
        wrapQueue.OnAckSequence(0x60000000u);
        wrapQueue.OnAckSequence(0xC0000000u);
        wrapQueue.OnAckSequence(0xFFFFFFF6u);
        Assert.Equal(0xFFFFFFF6u, wrapQueue.AckWatermark);
        wrapQueue.OnAckSequence(5u); // newer across the wrap
        Assert.Equal(5u, wrapQueue.AckWatermark);
        wrapQueue.OnAckSequence(0xFFFFFFF6u); // now stale — must not regress
        Assert.Equal(5u, wrapQueue.AckWatermark);
    }

    [Fact]
    public void NewerAckOvertakingQueuedNak_SuppressesStaleEncryptedResend()
    {
        (OutboundFlowQueue queue, _, _, TransportStats stats,
            List<byte[]> sent) = CreateQueue();
        queue.SendGameMessage(MakeMessage(2), GameMessageGroup.UIQueue);
        queue.SendGameMessage(MakeMessage(3), GameMessageGroup.UIQueue);
        queue.SendGameMessage(MakeMessage(4), GameMessageGroup.UIQueue);

        Nak(queue, 2u);
        queue.OnAckSequence(3u);
        sent.Clear();

        queue.TransmitPendingResends();

        Assert.Empty(sent);
        Assert.Equal(0, stats.ResendsSent);
        Assert.Equal(2, queue.CacheDepth); // watermark itself + newer seq 4
        Assert.Equal(0, queue.PendingResendCount);
    }


    [Fact]
    public void RebuiltResend_VerifiesUnderAceCrypto_WithTheOriginalKey()
    {
        (AceSessionModel model, _) = CreateNegotiatedModel();
        (OutboundFlowQueue queue, _, _, _, List<byte[]> sent) = CreateQueue();

        // Four reliable packets, seq 2..5; seq 3 is "lost".
        queue.SendGameMessage(MakeMessage(2), GameMessageGroup.UIQueue);
        queue.SendGameMessage(MakeMessage(3), GameMessageGroup.UIQueue);
        queue.SendGameMessage(MakeMessage(4), GameMessageGroup.UIQueue);
        queue.SendGameMessage(MakeMessage(5), GameMessageGroup.UIQueue);

        model.Receive(sent[0]); // seq 2 in order
        model.Receive(sent[2]); // seq 4: buffered, gap of one — no NAK yet
        model.Receive(sent[3]); // seq 5: desired+2 ≤ arrived → NAK fires
        model.Update();
        byte[] nak = Assert.Single(
            model.TakePendingDatagrams(),
            d => PacketHeader.Unpack(d).Flags
                == PacketHeaderFlags.RequestRetransmit);

        PacketCodec.PacketDecodeResult decodedNak =
            PacketCodec.TryDecode(nak, inboundIsaac: null);
        Assert.True(decodedNak.IsOk, decodedNak.Error.ToString());
        uint[] ids = decodedNak.Packet!.Optional.RetransmitRequests.ToArray();
        Assert.Equal(new uint[] { 3u }, ids);
        sent.Clear();
        Nak(queue, ids);
        queue.TransmitPendingResends();
        byte[] resent = Assert.Single(sent);
        Assert.Equal(
            PacketHeaderFlags.Retransmission
            | PacketHeaderFlags.EncryptedChecksum
            | PacketHeaderFlags.BlobFragments,
            PacketHeader.Unpack(resent).Flags);

        model.Receive(resent);
        Assert.Equal(
            new byte[] { 2, 3, 4, 5 },
            model.DispatchedMessages.Select(m => m[0]).ToArray());
        Assert.Equal(5u, model.LastReceivedPacketSequence);
        Assert.Equal(0, model.CrcDropCount);
        Assert.Equal(0, model.DuplicateDropCount);
        Assert.Equal(256, model.Crypto.Headroom);
        Assert.Equal(0, model.Crypto.OrphanCount);

        Assert.Equal(3, queue.CacheDepth);
    }

    [Fact]
    public void LostGameAction_IsResentOnNak_AllMessagesDispatchInOrder()
    {
        var transport = new FakeAceTransport();
        var session = new WorldSession(
            new IPEndPoint(IPAddress.Loopback, 9000),
            transport);
        try
        {
            session.Connect("testaccount", "testpassword", TimeSpan.FromSeconds(10));
            session.EnterWorld(0, TimeSpan.FromSeconds(10));
            Assert.Equal(WorldSession.State.InWorld, session.CurrentState);
            int baselineDispatched = transport.Model.DispatchedMessages.Count;

            var expectedBodies = new List<byte[]>();
            for (int i = 0; i < 10; i++)
            {
                if (i == 4)
                    transport.Link.DropNext(LinkDirection.ClientToServer);
                string text = $"msg {i}";
                expectedBodies.Add(ChatRequests.BuildTalk((uint)(i + 1), text));
                session.SendTalk(text);
            }

            DateTime deadline = DateTime.UtcNow.AddSeconds(10);
            while (transport.Model.DispatchedMessages.Count
                       < baselineDispatched + 10
                   && DateTime.UtcNow < deadline)
            {
                session.Tick();
                Thread.Sleep(5);
            }

            // All ten dispatched, byte-identical, in fragment order.
            Assert.Equal(
                expectedBodies,
                transport.Model.DispatchedMessages
                    .Skip(baselineDispatched)
                    .ToList());

            // Exactly one resend healed exactly one loss.
            Assert.Equal(1, transport.Link.DroppedCount(LinkDirection.ClientToServer));
            Assert.Equal(1, session.Transport!.Stats.ResendsSent);
            Assert.Equal(1, session.Transport.Stats.NakRequestsReceived);
            Assert.Equal(0, session.Transport.Stats.UncachedNakIds);

            Assert.False(transport.Model.IsTerminated);
            Assert.Equal(0, transport.Model.CrcDropCount);
            Assert.Equal(0, transport.Model.DuplicateDropCount);
            Assert.Equal(256, transport.Model.Crypto.Headroom);
            Assert.Equal(0, transport.Model.Crypto.OrphanCount);
        }
        finally
        {
            session.Dispose();
        }

        Assert.Equal(WorldSession.State.Disconnected, session.CurrentState);
    }

    // =====================================================================
    // Zero-alloc steady state
    // =====================================================================

    [Fact]
    public void SendGameMessage_SteadyState_AllocatesNothingOnceThePoolWarms()
    {
        var stats = new TransportStats();
        var virtualClock = new VirtualClock();
        var clock = new TransportClock(
            virtualClock.GetTimestamp,
            virtualClock.Frequency);
        var queue = new OutboundFlowQueue(
            MakeIsaac(ClientSeed),
            (ushort)ClientId,
            SessionIteration,
            clock,
            stats,
            static _ => { });
        byte[] body = MakeMessage(0x42);

        for (int i = 0; i < 128; i++)
        {
            queue.SendGameMessage(body, GameMessageGroup.UIQueue);
            queue.OnAckSequence(queue.HighestIdSent);
            queue.TransmitPendingResends();
        }

        long before = GC.GetAllocatedBytesForCurrentThread();
        for (int i = 0; i < 1_000; i++)
        {
            queue.SendGameMessage(body, GameMessageGroup.UIQueue);
            queue.OnAckSequence(queue.HighestIdSent);
            queue.TransmitPendingResends();
        }

        long allocated = GC.GetAllocatedBytesForCurrentThread() - before;
        Assert.Equal(0, allocated);
        queue.Dispose();
    }

    // =====================================================================
    // Fixture helpers
    // =====================================================================

    private (OutboundFlowQueue Queue, VirtualClock VirtualClock,
        TransportClock Clock, TransportStats Stats, List<byte[]> Sent)
        CreateQueue()
    {
        var virtualClock = new VirtualClock();
        var clock = new TransportClock(
            virtualClock.GetTimestamp,
            virtualClock.Frequency);
        var stats = new TransportStats();
        var sent = new List<byte[]>();
        var queue = new OutboundFlowQueue(
            MakeIsaac(ClientSeed),
            (ushort)ClientId,
            SessionIteration,
            clock,
            stats,
            datagram => sent.Add(datagram.ToArray()));
        return (queue, virtualClock, clock, stats, sent);
    }

    private static void Nak(OutboundFlowQueue queue, params uint[] ids)
    {
        byte[] bytes = new byte[ids.Length * 4];
        for (int i = 0; i < ids.Length; i++)
            BinaryPrimitives.WriteUInt32LittleEndian(bytes.AsSpan(i * 4), ids[i]);
        queue.OnRetransmitRequest(bytes, ids.Length);
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

    private static (AceSessionModel Model, VirtualClock Clock) CreateNegotiatedModel()
    {
        var clock = new VirtualClock();
        var model = new AceSessionModel(clock, ClientSeed, ServerSeed, ClientId, Cookie);
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
            new PacketHeader { Sequence = 1, Flags = PacketHeaderFlags.ConnectResponse },
            cookieBody,
            outboundIsaac: null);
        model.Receive(connectResponse);
        model.Update();
        model.TakePendingDatagrams();

        return (model, clock);
    }

    private static uint ExtractIsaacKey(byte[] datagram)
    {
        PacketHeader header = PacketHeader.Unpack(datagram);
        ReadOnlySpan<byte> body = datagram.AsSpan(PacketHeader.Size, header.DataSize);
        var optional = new PacketHeaderOptional();
        int consumed = optional.Parse(body, header.Flags);
        Assert.True(consumed >= 0);
        uint payloadHash = optional.CalculateHash32();
        if ((header.Flags & PacketHeaderFlags.BlobFragments) != 0)
        {
            ReadOnlySpan<byte> remaining = body.Slice(consumed);
            while (!remaining.IsEmpty)
            {
                (MessageFragment? fragment, int fragmentBytes) =
                    MessageFragment.TryParse(remaining);
                Assert.NotNull(fragment);
                payloadHash += PacketCodec.CalculateFragmentHash32(fragment!.Value);
                remaining = remaining.Slice(fragmentBytes);
            }
        }

        return (header.Checksum - header.CalculateHeaderHash32()) ^ payloadHash;
    }

    private static SentPacketStore.CachedPacket RentedEntry(
        CountingPool pool,
        uint sequence)
    {
        byte[] buffer = pool.Rent(PacketHeader.Size + 24);
        return new SentPacketStore.CachedPacket(
            sequence,
            buffer,
            bodyLength: 24,
            sealedChecksum: 0u,
            isaacKey: 0u,
            hasFragments: true);
    }

    private sealed class CountingPool : ArrayPool<byte>
    {
        public int Rented { get; private set; }
        public int Returned { get; private set; }

        public override byte[] Rent(int minimumLength)
        {
            Rented++;
            return Shared.Rent(minimumLength);
        }

        public override void Return(byte[] array, bool clearArray = false)
        {
            Returned++;
            Shared.Return(array, clearArray);
        }
    }
}
