using System.Buffers.Binary;
using AcDream.Core.Net.Cryptography;
using AcDream.Core.Net.Messages;
using AcDream.Core.Net.Packets;

namespace AcDream.Core.Net.Tests.Transport;

public sealed class AceSessionModelTests
{
    private const uint ClientSeed = 0x11AA22BBu;
    private const uint ServerSeed = 0x33CC44DDu;
    private const uint ClientId = 0x1234u;
    private const ulong Cookie = 0xFEEDFACECAFEBABEUL;


    [Fact]
    public void CheckState_DropsControlPacketsBeforeNegotiation_ThenConsumesThemAfter()
    {
        var clock = new VirtualClock();
        var model = new AceSessionModel(clock, ClientSeed, ServerSeed, ClientId, Cookie);
        model.LoginRequestReceived += model.SendConnectRequest;
        var client = new TestAcClient(ClientSeed);
        Assert.Equal(AceSessionState.AuthLoginRequest, model.State);

        byte[] cleartextAck = client.BuildCleartextAck(headerSequence: 2, ackValue: 1);
        byte[] encryptedAck = client.BuildEncryptedAck(headerSequence: 2, ackValue: 1);
        uint keyBeforeGate = model.Crypto.CurrentKey;

        model.Receive(cleartextAck);
        model.Receive(encryptedAck);
        Assert.Equal(2, model.StateDropCount);
        Assert.Equal(0, model.CrcDropCount);
        Assert.Equal(0, model.DuplicateDropCount);
        Assert.Equal(1u, model.LastReceivedPacketSequence);
        Assert.Equal(keyBeforeGate, model.Crypto.CurrentKey);
        Assert.Equal(256, model.Crypto.Headroom);
        Assert.Equal(0, model.Crypto.OrphanCount);

        model.Receive(BuildLoginRequest());
        Assert.Equal(AceSessionState.AuthConnectResponse, model.State);
        model.Update();
        model.TakePendingDatagrams();
        model.Receive(BuildConnectResponse());
        Assert.Equal(AceSessionState.AuthConnected, model.State);
        model.Update();
        model.TakePendingDatagrams();

        model.Receive(cleartextAck);
        Assert.Equal(2, model.StateDropCount);
        Assert.Equal(0, model.CrcDropCount);
        Assert.Equal(1u, model.LastReceivedPacketSequence);
        Assert.Equal(keyBeforeGate, model.Crypto.CurrentKey);

        model.Receive(encryptedAck);
        Assert.Equal(0, model.CrcDropCount);
        Assert.Equal(2u, model.LastReceivedPacketSequence);
        Assert.NotEqual(keyBeforeGate, model.Crypto.CurrentKey);
        Assert.Equal(256, model.Crypto.Headroom);
    }

    [Fact]
    public void CheckState_DropsLoginRequestAndConnectResponseOutOfState()
    {
        (AceSessionModel model, _, _) = CreateNegotiatedModel();
        Assert.Equal(AceSessionState.AuthConnected, model.State);

        int loginRequests = 0;
        int connectResponses = 0;
        model.LoginRequestReceived += () => loginRequests++;
        model.ConnectResponseAccepted += () => connectResponses++;

        // Session.cs:95-96 — a LoginRequest after the handshake is dropped
        // before the auth handler ever sees it.
        model.Receive(BuildLoginRequest());
        Assert.Equal(1, model.StateDropCount);
        Assert.Equal(0, loginRequests);

        model.Receive(BuildConnectResponse());
        Assert.Equal(2, model.StateDropCount);
        Assert.Equal(0, connectResponses);

        Assert.Equal(0, model.CrcDropCount);
        Assert.Equal(0, model.DuplicateDropCount);
        Assert.Equal(AceSessionState.AuthConnected, model.State);
    }


    [Fact]
    public void Nak_FiresOnlyAtDesiredPlusTwo_WithOneSecondRateLimit()
    {
        (AceSessionModel model, TestAcClient client, VirtualClock clock) = CreateNegotiatedModel();
        byte[][] packets = BuildSequentialPackets(client, count: 7); // seq 2..8

        model.Receive(packets[1]);
        model.Update();
        Assert.Empty(OfExactFlags(model.TakePendingDatagrams(), PacketHeaderFlags.RequestRetransmit));
        Assert.Equal(1, model.OutOfOrderPacketCount);
        Assert.Equal(1u, model.LastReceivedPacketSequence);

        model.Receive(packets[2]);
        model.Update();
        byte[] nak = Assert.Single(
            OfExactFlags(model.TakePendingDatagrams(), PacketHeaderFlags.RequestRetransmit));
        Assert.Equal(new uint[] { 2u }, NakIds(nak));

        // Within the 1 s limit (:359) another eligible arrival does NOT re-NAK.
        model.Receive(packets[3]);
        model.Update();
        Assert.Empty(OfExactFlags(model.TakePendingDatagrams(), PacketHeaderFlags.RequestRetransmit));

        clock.Advance(TimeSpan.FromSeconds(0.9));
        model.Receive(packets[4]);
        model.Update();
        Assert.Empty(OfExactFlags(model.TakePendingDatagrams(), PacketHeaderFlags.RequestRetransmit));

        clock.Advance(TimeSpan.FromSeconds(0.1));
        model.Receive(packets[5]);
        model.Update();
        Assert.Empty(OfExactFlags(model.TakePendingDatagrams(), PacketHeaderFlags.RequestRetransmit));

        // Limiter reopens strictly after 1 s.
        clock.Advance(TimeSpan.FromSeconds(0.1));
        model.Receive(packets[6]);
        model.Update();
        byte[] second = Assert.Single(
            OfExactFlags(model.TakePendingDatagrams(), PacketHeaderFlags.RequestRetransmit));
        Assert.Equal(new uint[] { 2u }, NakIds(second));
    }

    [Fact]
    public void ValidResend_IsAccepted_AndOrderingRestored()
    {
        (AceSessionModel model, TestAcClient client, _) = CreateNegotiatedModel();
        byte[][] packets = BuildSequentialPackets(client, 3); // seq 2(w1), 3(w2), 4(w3)

        model.Receive(packets[0]); // in order
        model.Receive(packets[2]);
        Assert.Single(model.DispatchedMessages);
        Assert.Equal(255, model.Crypto.Headroom);

        model.Receive(packets[1]);
        Assert.Equal(new byte[] { 2, 3, 4 }, Markers(model));
        Assert.Equal(4u, model.LastReceivedPacketSequence);
        Assert.Equal(0, model.OutOfOrderPacketCount);
        Assert.Equal(256, model.Crypto.Headroom);
        Assert.Equal(0, model.Crypto.OrphanCount);
    }

    [Fact]
    public void ReKeyedResend_PermanentlyOrphansAKeystreamWord()
    {
        (AceSessionModel model, TestAcClient client, _) = CreateNegotiatedModel();
        byte[][] packets = BuildSequentialPackets(client, 3); // seq 2(w1), 3(w2), 4(w3)

        model.Receive(packets[0]);
        model.Receive(packets[2]); // parks w2 for the pending retransmission
        Assert.Equal(255, model.Crypto.Headroom);

        byte[] rekeyed = client.BuildGameMessagePacket(
            packetSequence: 3,
            fragmentSequence: 2,
            MakeMessage(3));
        model.Receive(rekeyed);
        Assert.Equal(new byte[] { 2, 3, 4 }, Markers(model)); // accepted, ordering restored
        Assert.Equal(255, model.Crypto.Headroom);
        Assert.Equal(1, model.Crypto.OrphanCount);

        // Healthy follow-on traffic never recovers the orphan.
        model.Receive(client.BuildGameMessagePacket(MakeMessage(5))); // seq 5
        model.Receive(client.BuildGameMessagePacket(MakeMessage(6))); // seq 6
        Assert.Equal(new byte[] { 2, 3, 4, 5, 6 }, Markers(model));
        Assert.Equal(255, model.Crypto.Headroom);
        Assert.Equal(1, model.Crypto.OrphanCount);
    }

    [Fact]
    public void ResendOfAlreadyAcceptedPacket_BurnsTheSearchWindow()
    {
        (AceSessionModel model, TestAcClient client, _) = CreateNegotiatedModel();
        byte[][] packets = BuildSequentialPackets(client, 2); // seq 2(w1), 3(w2)

        model.Receive(packets[0]);
        Assert.Single(model.DispatchedMessages);

        model.Receive(packets[0]);
        Assert.Equal(1, model.CrcDropCount);
        Assert.Single(model.DispatchedMessages);
        Assert.Equal(0, model.Crypto.Headroom);
        Assert.Equal(256, model.Crypto.OrphanCount);

        model.Receive(packets[1]);
        Assert.Equal(2, model.DispatchedMessages.Count);
        Assert.Equal(1, model.Crypto.Headroom);
    }

    [Fact]
    public void AckOnlyPacketAtSameSequence_AcceptedWithoutAdvancingWatermark()
    {
        (AceSessionModel model, TestAcClient client, _) = CreateNegotiatedModel();
        Assert.Equal(new uint[] { 2u }, model.CachedPacketSequences.ToArray());

        model.Receive(client.BuildGameMessagePacket(MakeMessage(2)));
        Assert.Equal(2u, model.LastReceivedPacketSequence);

        model.EnqueueGameMessage(MakeMessage(0xEE), GameMessageGroup.UIQueue);
        model.Update();
        Assert.Equal(2, model.CachedPacketCount);

        model.Receive(client.BuildCleartextAck(headerSequence: 2, ackValue: 3));
        Assert.Equal(0, model.DuplicateDropCount);
        Assert.Equal(2u, model.LastReceivedPacketSequence);
        Assert.Equal(new uint[] { 3u }, model.CachedPacketSequences.ToArray());

        // Repeatable at the same sequence.
        model.Receive(client.BuildCleartextAck(2, 4));
        Assert.Equal(0, model.DuplicateDropCount);
        Assert.Empty(model.CachedPacketSequences);
        Assert.Equal(2u, model.LastReceivedPacketSequence);

        // The exemption is equality, not <=: an ack at an OLDER sequence is
        // rejected as a duplicate.
        model.Receive(client.BuildCleartextAck(1, 4));
        Assert.Equal(1, model.DuplicateDropCount);
    }

    [Fact]
    public void CleartextNonAckAdvancesWatermark_TheAceHole()
    {
        (AceSessionModel model, TestAcClient client, _) = CreateNegotiatedModel();
        byte[][] packets = BuildSequentialPackets(client, 2); // seq 2(w1), 3(w2)
        model.Receive(packets[0]); // watermark 2

        model.Receive(client.BuildCleartextEchoRequest(headerSequence: 3, clientTime: 1.5f));
        Assert.Equal(3u, model.LastReceivedPacketSequence);

        model.Receive(packets[1]);
        Assert.Equal(1, model.DuplicateDropCount);
        Assert.Single(model.DispatchedMessages);
        Assert.Equal(3u, model.LastReceivedPacketSequence);
        Assert.Equal(256, model.Crypto.Headroom); // no orphan — the loss is pure payload
    }

    [Fact]
    public void FragmentGate_StallsOnGap_AndHealsWhenMissingFragmentArrives()
    {
        (AceSessionModel model, TestAcClient client, _) = CreateNegotiatedModel();
        byte[] first = client.BuildGameMessagePacket(2, 1, MakeMessage(1));
        byte[] third = client.BuildGameMessagePacket(3, 3, MakeMessage(3));
        byte[] second = client.BuildGameMessagePacket(4, 2, MakeMessage(2));

        model.Receive(first);
        Assert.Equal(new byte[] { 1 }, Markers(model));

        model.Receive(third);
        Assert.Equal(3u, model.LastReceivedPacketSequence);
        Assert.Equal(new byte[] { 1 }, Markers(model));
        Assert.Equal(1, model.FragmentGateBufferCount);
        Assert.Equal(1u, model.LastReceivedFragmentSequence);

        // The missing fragment arrives (here aboard the next packet — on a
        // real link, via packet retransmission): the gate dispatches it and
        // drains the parked fragment in order (:571-578).
        model.Receive(second);
        Assert.Equal(new byte[] { 1, 2, 3 }, Markers(model));
        Assert.Equal(0, model.FragmentGateBufferCount);
        Assert.Equal(3u, model.LastReceivedFragmentSequence);
    }


    [Fact]
    public void SplitC2SMessage_StaysIncompleteUntilTheDroppedPacketIsRedelivered()
    {
        (AceSessionModel model, TestAcClient client, _) = CreateNegotiatedModel();
        byte[] partA = { 0x11, 0x22, 0x33, 0x44 };
        byte[] partB = { 0x55, 0x66, 0x77, 0x88 };

        byte[] head = client.BuildFragmentPacket(2, fragmentSequence: 1, count: 2, index: 0, partA);
        byte[] tail = client.BuildFragmentPacket(3, fragmentSequence: 1, count: 2, index: 1, partB);
        byte[] third = client.BuildGameMessagePacket(4, 2, MakeMessage(4));
        byte[] fourth = client.BuildGameMessagePacket(5, 3, MakeMessage(5));

        model.Receive(head);
        Assert.Equal(1, model.PartialFragmentBufferCount);
        Assert.Empty(model.DispatchedMessages);

        model.Receive(third);
        model.Receive(fourth);
        model.Update();
        byte[] nak = Assert.Single(
            OfExactFlags(model.TakePendingDatagrams(), PacketHeaderFlags.RequestRetransmit));
        Assert.Equal(new uint[] { 3u }, NakIds(nak));
        Assert.Equal(1, model.PartialFragmentBufferCount);
        Assert.Empty(model.DispatchedMessages);
        Assert.Equal(0u, model.LastReceivedFragmentSequence);

        model.Receive(tail);
        Assert.Equal(3, model.DispatchedMessages.Count);
        Assert.Equal(partA.Concat(partB).ToArray(), model.DispatchedMessages[0]);
        Assert.Equal(new byte[] { 4, 5 }, model.DispatchedMessages.Skip(1).Select(MessageMarker).ToArray());
        Assert.Equal(0, model.PartialFragmentBufferCount);
        Assert.Equal(0, model.OutOfOrderPacketCount);
        Assert.Equal(3u, model.LastReceivedFragmentSequence);
        Assert.Equal(256, model.Crypto.Headroom); // the parked key was recovered
    }

    [Fact]
    public void SplitC2SMessage_UnderFourBytes_IsDroppedAndStallsTheFragmentGate()
    {
        (AceSessionModel model, TestAcClient client, _) = CreateNegotiatedModel();

        model.Receive(client.BuildFragmentPacket(2, 1, 2, 0, new byte[] { 0xAA }));
        model.Receive(client.BuildFragmentPacket(3, 1, 2, 1, new byte[] { 0xBB }));
        Assert.Empty(model.DispatchedMessages);
        Assert.Equal(0, model.PartialFragmentBufferCount);
        Assert.Equal(0u, model.LastReceivedFragmentSequence);
        Assert.Equal(0, model.CrcDropCount);

        model.Receive(client.BuildGameMessagePacket(4, 2, MakeMessage(4)));
        Assert.Empty(model.DispatchedMessages);
        Assert.Equal(1, model.FragmentGateBufferCount);
        Assert.Equal(4u, model.LastReceivedPacketSequence);
    }

    [Fact]
    public void SplitC2SMessage_ToleratesLaterFragmentWithLargerCountAndIndex()
    {
        (AceSessionModel model, TestAcClient client, _) = CreateNegotiatedModel();
        byte[] partA = { 0x11, 0x22, 0x33, 0x44 };
        byte[] partB = { 0x55, 0x66, 0x77, 0x88 };

        model.Receive(client.BuildFragmentPacket(2, 1, count: 2, index: 0, partA));
        model.Receive(client.BuildFragmentPacket(3, 1, count: 3, index: 2, partB));

        byte[] assembled = Assert.Single(model.DispatchedMessages);
        Assert.Equal(partA.Concat(partB).ToArray(), assembled); // sorted by Index (:38)
        Assert.Equal(0, model.PartialFragmentBufferCount);
        Assert.Equal(1u, model.LastReceivedFragmentSequence);
        Assert.Equal(0, model.CrcDropCount);
    }

    [Fact]
    public void ZeroCountFragment_IsAcceptedByTheParse_ThenSilentlyDropped()
    {
        (AceSessionModel model, TestAcClient client, _) = CreateNegotiatedModel();
        byte[] zeroCount = client.BuildFragmentPacket(2, 1, count: 0, index: 0, MakeMessage(0x77));

        Assert.Equal(
            PacketCodec.DecodeError.InvalidFragment,
            PacketCodec.TryDecode(zeroCount, inboundIsaac: null).Error);

        model.Receive(zeroCount);
        Assert.Equal(0, model.CrcDropCount);
        Assert.Empty(model.DispatchedMessages);
        Assert.Equal(1, model.PartialFragmentBufferCount);
        Assert.Equal(0u, model.LastReceivedFragmentSequence);
        Assert.Equal(2u, model.LastReceivedPacketSequence);
    }

    // =====================================================================
    // Termination + timeout
    // =====================================================================

    [Fact]
    public void Termination_KeepsRunningForTwoSeconds_ThenReleases()
    {
        (AceSessionModel model, TestAcClient client, VirtualClock clock) = CreateNegotiatedModel();
        model.Receive(client.BuildGameMessagePacket(MakeMessage(2)));
        Assert.Single(model.DispatchedMessages);

        // Session.Terminate (Session.cs:281-298) only ARMS PendingTermination
        // with a 2 s window (SessionTerminationDetails.cs:12).
        model.Receive(TransportDisconnect.Build((ushort)ClientId, iteration: 1));
        Assert.True(model.IsTerminated);
        Assert.False(model.IsReleased);
        Assert.Equal(AceTerminationPhase.Initialized, model.TerminationPhase);
        Assert.Equal(AceTerminationReason.PacketHeaderDisconnect, model.TerminationReason);

        clock.Advance(TimeSpan.FromSeconds(1));
        model.Receive(client.BuildGameMessagePacket(MakeMessage(3)));
        Assert.Equal(2, model.DispatchedMessages.Count);

        // ...and Network.Update() still runs, so queued messages still leave
        // ("boot messages may need sending", :129).
        model.EnqueueGameMessage(MakeMessage(0xEE), GameMessageGroup.UIQueue);
        model.Update();
        Assert.False(model.IsReleased);
        byte[] flushed = Assert.Single(model.TakePendingDatagrams());
        Assert.Equal(
            PacketHeaderFlags.BlobFragments | PacketHeaderFlags.EncryptedChecksum,
            Head(flushed).Flags);

        clock.Advance(TimeSpan.FromSeconds(1.2));
        model.Update();
        Assert.True(model.IsReleased);
        Assert.Equal(AceTerminationPhase.SessionWorkCompleted, model.TerminationPhase);
        model.TakePendingDatagrams();

        // Released (NetworkSession.cs:271-272, :184-185): inbound and outbound
        // are both no-ops.
        model.Receive(client.BuildGameMessagePacket(MakeMessage(4)));
        model.Update();
        Assert.Equal(2, model.DispatchedMessages.Count);
        Assert.Empty(model.TakePendingDatagrams());
    }

    [Fact]
    public void SixtySecondTimeout_Terminates_AndCleartextNaksDoNotRefreshIt()
    {
        (AceSessionModel model, TestAcClient client, VirtualClock clock) = CreateNegotiatedModel();
        model.Receive(client.BuildGameMessagePacket(MakeMessage(2))); // refresh → +60 s (:329-331)
        long deadline = model.TimeoutDeadlineTimestamp;

        clock.Advance(TimeSpan.FromSeconds(59));
        model.Receive(client.BuildCleartextNak(2, 2u));
        Assert.Equal(1, model.RetransmitsServed);
        Assert.Equal(deadline, model.TimeoutDeadlineTimestamp);
        model.Update();
        Assert.False(model.IsTerminated);

        clock.Advance(TimeSpan.FromSeconds(1));
        Assert.Equal(deadline, clock.GetTimestamp());
        model.TakePendingDatagrams();
        model.Update();
        Assert.True(model.IsTerminated);
        Assert.Equal(AceTerminationReason.NetworkTimeout, model.TerminationReason);
        Assert.Empty(model.TakePendingDatagrams());
    }

    [Fact]
    public void GapBeyondSearchWindow_TerminatesAbnormalSequenceReceived()
    {
        (AceSessionModel model, TestAcClient client, _) = CreateNegotiatedModel();
        model.Receive(client.BuildGameMessagePacket(259, 1, MakeMessage(1)));
        Assert.False(model.IsTerminated);
        model.Update();
        byte[] nak = Assert.Single(
            OfExactFlags(model.TakePendingDatagrams(), PacketHeaderFlags.RequestRetransmit));
        uint[] ids = NakIds(nak);
        Assert.Equal(115, ids.Length);
        Assert.Equal(2u, ids[0]);    // desiredSeq leads the list (:390-391)
        Assert.Equal(116u, ids[^1]); // then 3..116 — the 115-id cap

        // One past the window: rcvd − bottom > 256 → AbnormalSequenceReceived
        // (:393-397), and no NAK goes out.
        (AceSessionModel model2, TestAcClient client2, _) = CreateNegotiatedModel();
        model2.Receive(client2.BuildGameMessagePacket(260, 1, MakeMessage(1)));
        Assert.True(model2.IsTerminated);
        Assert.Equal(AceTerminationReason.AbnormalSequenceReceived, model2.TerminationReason);
        model2.Update();
        Assert.Empty(OfExactFlags(model2.TakePendingDatagrams(), PacketHeaderFlags.RequestRetransmit));
    }


    [Fact]
    public void Retransmit_ServesCachedBytes_WithRetransmissionFlag_AndNoNewIsaacWord()
    {
        (AceSessionModel model, TestAcClient client, _) = CreateNegotiatedModel();
        // Shadow the S2C keystream: word 1 went to the immediate TimeSync the
        // negotiation helper drained.
        IsaacRandom shadow = MakeIsaac(ServerSeed);
        uint w1 = shadow.Next();
        uint w2 = shadow.Next();
        uint w3 = shadow.Next();
        uint w4 = shadow.Next();
        Assert.NotEqual(w1, w2); // sanity on the shadow itself

        model.EnqueueGameMessage(MakeMessage(0xA1), GameMessageGroup.UIQueue);
        model.Update();
        byte[] packetA = Assert.Single(model.TakePendingDatagrams());
        Assert.Equal(3u, Head(packetA).Sequence); // TimeSync took 2; UIntSequence increments
        Assert.Equal(w2, ExtractIsaacKey(packetA));

        model.EnqueueGameMessage(MakeMessage(0xB2), GameMessageGroup.UIQueue);
        model.Update();
        byte[] packetB = Assert.Single(model.TakePendingDatagrams());
        Assert.Equal(w3, ExtractIsaacKey(packetB));

        model.Receive(client.BuildCleartextNak(2, 3u));
        byte[] resent = Assert.Single(model.TakePendingDatagrams());
        PacketHeader resentHeader = Head(resent);
        Assert.Equal(3u, resentHeader.Sequence);
        Assert.Equal(
            PacketHeaderFlags.Retransmission
            | PacketHeaderFlags.EncryptedChecksum
            | PacketHeaderFlags.BlobFragments,
            resentHeader.Flags);
        Assert.Equal(
            packetA.AsSpan(PacketHeader.Size).ToArray(),
            resent.AsSpan(PacketHeader.Size).ToArray());
        Assert.Equal(w2, ExtractIsaacKey(resent));
        Assert.Equal(Head(packetA).Time, resentHeader.Time);
        Assert.Equal(1, model.RetransmitsServed);

        // The S2C keystream was not disturbed: the next fresh packet uses w4.
        model.EnqueueGameMessage(MakeMessage(0xC3), GameMessageGroup.UIQueue);
        model.Update();
        byte[] packetC = Assert.Single(model.TakePendingDatagrams());
        Assert.Equal(w4, ExtractIsaacKey(packetC));

        model.Receive(client.BuildCleartextNak(2, 40u));
        model.Update();
        byte[] reject = Assert.Single(
            model.TakePendingDatagrams(),
            d => (Head(d).Flags & PacketHeaderFlags.RejectRetransmit) != 0);
        Assert.Equal(new uint[] { 40u }, RejectIds(reject));
    }

    [Fact]
    public void CumulativeAck_EveryTwoSeconds_CleartextExactFlags_ReusedSequence()
    {
        (AceSessionModel model, TestAcClient client, VirtualClock clock) = CreateNegotiatedModel();
        model.Receive(client.BuildGameMessagePacket(MakeMessage(2)));
        model.Receive(client.BuildGameMessagePacket(MakeMessage(3))); // watermark 3
        model.Update();
        Assert.Empty(model.TakePendingDatagrams()); // 2 s gate not due (:55, :211)

        clock.Advance(TimeSpan.FromSeconds(2.1));
        model.Update();
        byte[] ack = Assert.Single(model.TakePendingDatagrams());
        PacketHeader ackHeader = Head(ack);
        Assert.Equal(PacketHeaderFlags.AckSequence, ackHeader.Flags);
        Assert.Equal(2u, ackHeader.Sequence);
        Assert.Equal(
            3u,
            BinaryPrimitives.ReadUInt32LittleEndian(ack.AsSpan(PacketHeader.Size)));

        model.Update(); // gate re-armed (:215) — no second ack
        Assert.Empty(model.TakePendingDatagrams());

        model.EnqueueGameMessage(MakeMessage(0xEE), GameMessageGroup.UIQueue);
        model.Update();
        Assert.Equal(3u, Head(Assert.Single(model.TakePendingDatagrams())).Sequence);
    }

    [Fact]
    public void EchoRequest_GetsEchoResponse()
    {
        (AceSessionModel model, TestAcClient client, VirtualClock clock) = CreateNegotiatedModel();
        model.Receive(client.BuildCleartextEchoRequest(headerSequence: 2, clientTime: 5.5f));
        clock.Advance(TimeSpan.FromSeconds(0.5));
        model.Update();
        byte[] echo = Assert.Single(model.TakePendingDatagrams());
        Assert.Equal(
            PacketHeaderFlags.EchoResponse | PacketHeaderFlags.EncryptedChecksum,
            Head(echo).Flags);
        Assert.Equal(
            5.5f,
            BinaryPrimitives.ReadSingleLittleEndian(echo.AsSpan(PacketHeader.Size)));
        Assert.Equal(
            0.5f - 5.5f,
            BinaryPrimitives.ReadSingleLittleEndian(echo.AsSpan(PacketHeader.Size + 4)));
    }

    [Fact]
    public void SendBundle_CoalescesSmallMessagesIntoOnePacket()
    {
        (AceSessionModel model, _, _) = CreateNegotiatedModel();
        IsaacRandom shadow = MakeIsaac(ServerSeed);
        shadow.Next();            // w1 — the negotiation TimeSync
        uint w2 = shadow.Next();
        uint w3 = shadow.Next();

        // Three messages enqueued into the same bundle before one pump.
        model.EnqueueGameMessage(MakeMessage(0xA1), GameMessageGroup.UIQueue);
        model.EnqueueGameMessage(MakeMessage(0xB2), GameMessageGroup.UIQueue);
        model.EnqueueGameMessage(MakeMessage(0xC3), GameMessageGroup.UIQueue);
        model.Update();

        byte[] packet = Assert.Single(model.TakePendingDatagrams());
        PacketHeader header = Head(packet);
        Assert.Equal(3u, header.Sequence);
        Assert.Equal(
            PacketHeaderFlags.BlobFragments | PacketHeaderFlags.EncryptedChecksum,
            header.Flags);
        Assert.Equal(w2, ExtractIsaacKey(packet));

        MessageFragment[] fragments = FragmentsOf(packet);
        Assert.Equal(3, fragments.Length);
        Assert.Equal(new uint[] { 0u, 1u, 2u }, fragments.Select(f => f.Header.Sequence).ToArray());
        Assert.All(fragments, f => Assert.Equal(1, (int)f.Header.Count));
        Assert.All(fragments, f => Assert.Equal(0, (int)f.Header.Index));
        Assert.All(fragments, f => Assert.Equal(GameMessageFragment.OutboundFragmentId, f.Header.Id));
        Assert.Equal(
            new byte[] { 0xA1, 0xB2, 0xC3 },
            fragments.Select(f => f.Payload[0]).ToArray());

        model.EnqueueGameMessage(MakeMessage(0xD4), GameMessageGroup.UIQueue);
        model.Update();
        byte[] next = Assert.Single(model.TakePendingDatagrams());
        Assert.Equal(4u, Head(next).Sequence);
        Assert.Equal(w3, ExtractIsaacKey(next));
        Assert.Equal(3u, Assert.Single(FragmentsOf(next)).Header.Sequence);
    }

    [Fact]
    public void SendBundle_SplitsLargeMessageAcrossPacketsWithCountGreaterThanOne()
    {
        (AceSessionModel model, _, _) = CreateNegotiatedModel();
        IsaacRandom shadow = MakeIsaac(ServerSeed);
        shadow.Next();            // w1 — the negotiation TimeSync
        uint w2 = shadow.Next();
        uint w3 = shadow.Next();

        byte[] large = MakeLargeMessage(600);
        model.EnqueueGameMessage(large, GameMessageGroup.UIQueue);
        model.Update();

        List<byte[]> sent = model.TakePendingDatagrams();
        Assert.Equal(2, sent.Count);
        Assert.Equal(new uint[] { 3u, 4u }, sent.Select(d => Head(d).Sequence).ToArray());
        Assert.Equal(w2, ExtractIsaacKey(sent[0]));
        Assert.Equal(w3, ExtractIsaacKey(sent[1]));

        MessageFragment head = Assert.Single(FragmentsOf(sent[0]));
        MessageFragment tail = Assert.Single(FragmentsOf(sent[1]));
        Assert.Equal(2, (int)head.Header.Count);
        Assert.Equal(0, (int)head.Header.Index);
        Assert.Equal(MessageFragmentHeader.MaxFragmentDataSize, head.Payload.Length);
        Assert.Equal(2, (int)tail.Header.Count);
        Assert.Equal(1, (int)tail.Header.Index);
        Assert.Equal(600 - MessageFragmentHeader.MaxFragmentDataSize, tail.Payload.Length);
        Assert.Equal(head.Header.Sequence, tail.Header.Sequence);
        Assert.Equal(large, head.Payload.Concat(tail.Payload).ToArray());
    }

    [Fact]
    public void CachedPackets_PruneAfter120Seconds_ThenStaleNakGetsRejectRetransmit()
    {
        (AceSessionModel model, TestAcClient client, VirtualClock clock) = CreateNegotiatedModel();
        Assert.Equal(new uint[] { 2u }, model.CachedPacketSequences.ToArray()); // the t=0 TimeSync

        clock.Advance(TimeSpan.FromSeconds(50));
        model.Receive(client.BuildGameMessagePacket(MakeMessage(2)));
        clock.Advance(TimeSpan.FromSeconds(50));
        model.Receive(client.BuildGameMessagePacket(MakeMessage(3)));
        model.Update(); // t = 100 s: prune runs, the seq-2 entry is well inside
        Assert.Contains(2u, model.CachedPacketSequences);

        // The retention test is STRICTLY greater than 120 (:258), so at
        // exactly 120 s the entry survives.
        clock.Advance(TimeSpan.FromSeconds(20));
        model.Receive(client.BuildGameMessagePacket(MakeMessage(4)));
        model.Update();
        Assert.Contains(2u, model.CachedPacketSequences);

        clock.Advance(TimeSpan.FromSeconds(5.1));
        model.Receive(client.BuildGameMessagePacket(MakeMessage(5)));
        model.Update();
        Assert.DoesNotContain(2u, model.CachedPacketSequences);

        model.Receive(client.BuildCleartextNak(6, 2u));
        model.Update();
        byte[] reject = Assert.Single(
            model.TakePendingDatagrams(),
            d => (Head(d).Flags & PacketHeaderFlags.RejectRetransmit) != 0);
        Assert.Equal(new uint[] { 2u }, RejectIds(reject));
        Assert.Equal(0, model.RetransmitsServed);
    }

    [Fact]
    public void ConnectRequest_MatchesNegotiationFixtureLayout()
    {
        var clock = new VirtualClock();
        var model = new AceSessionModel(clock, ClientSeed, ServerSeed, ClientId, Cookie);
        model.LoginRequestReceived += model.SendConnectRequest;
        model.Receive(BuildLoginRequest());
        model.Update();

        byte[] connectRequest = Assert.Single(model.TakePendingDatagrams());
        PacketCodec.PacketDecodeResult decoded =
            PacketCodec.TryDecode(connectRequest, inboundIsaac: null);
        Assert.True(decoded.IsOk, decoded.Error.ToString());
        Packet packet = decoded.Packet!;
        Assert.True(packet.Header.HasFlag(PacketHeaderFlags.ConnectRequest));
        Assert.Equal(0u, packet.Header.Sequence); // first NextValue of the unprimed UIntSequence
        Assert.Equal((ushort)1, packet.Header.Iteration);
        Assert.Equal(Cookie, packet.Optional.ConnectRequestCookie);
        Assert.Equal(ClientId, packet.Optional.ConnectRequestClientId);
        Assert.Equal(ServerSeed, packet.Optional.ConnectRequestServerSeed);
        Assert.Equal(ClientSeed, packet.Optional.ConnectRequestClientSeed);
    }

    // =====================================================================
    // Fixture helpers
    // =====================================================================

    private static (AceSessionModel Model, TestAcClient Client, VirtualClock Clock)
        CreateNegotiatedModel()
    {
        var clock = new VirtualClock();
        var model = new AceSessionModel(clock, ClientSeed, ServerSeed, ClientId, Cookie);
        model.LoginRequestReceived += model.SendConnectRequest;

        model.Receive(BuildLoginRequest());
        model.Update();
        model.TakePendingDatagrams();

        model.Receive(BuildConnectResponse());
        model.Update();
        model.TakePendingDatagrams(); // discard the immediate first TimeSync (sequence 2)

        return (model, new TestAcClient(ClientSeed), clock);
    }

    private static byte[] BuildLoginRequest()
    {
        byte[] payload = LoginRequest.Build("testaccount", "testpassword", 1234);
        return PacketCodec.Encode(
            new PacketHeader { Flags = PacketHeaderFlags.LoginRequest },
            payload,
            outboundIsaac: null);
    }

    private static byte[] BuildConnectResponse()
    {
        byte[] body = new byte[8];
        BinaryPrimitives.WriteUInt64LittleEndian(body, Cookie);
        return PacketCodec.Encode(
            new PacketHeader { Sequence = 1, Flags = PacketHeaderFlags.ConnectResponse },
            body,
            outboundIsaac: null);
    }

    /// <summary>Sequential post-handshake game-message packets: sequences 2..,
    /// fragment sequences 1.., one keystream word each, marker = index + 2.</summary>
    private static byte[][] BuildSequentialPackets(TestAcClient client, int count) =>
        Enumerable.Range(0, count)
            .Select(i => client.BuildGameMessagePacket(MakeMessage((byte)(i + 2))))
            .ToArray();

    /// <summary>An 8-byte message body whose first byte is a test marker.</summary>
    private static byte[] MakeMessage(byte marker) =>
        new byte[] { marker, 0x11, 0x22, 0x33, 0x00, 0x00, 0x00, 0x00 };

    private static byte[] MakeLargeMessage(int length)
    {
        byte[] body = new byte[length];
        for (int i = 0; i < length; i++)
            body[i] = (byte)(i * 7 + 3);
        return body;
    }

    private static byte MessageMarker(byte[] messageBody) => messageBody[0];

    private static byte[] Markers(AceSessionModel model) =>
        model.DispatchedMessages.Select(MessageMarker).ToArray();

    private static PacketHeader Head(byte[] datagram) => PacketHeader.Unpack(datagram);

    private static List<byte[]> OfExactFlags(
        IEnumerable<byte[]> datagrams,
        PacketHeaderFlags flags) =>
        datagrams.Where(d => Head(d).Flags == flags).ToList();

    private static MessageFragment[] FragmentsOf(byte[] datagram)
    {
        PacketHeader header = PacketHeader.Unpack(datagram);
        ReadOnlySpan<byte> body = datagram.AsSpan(PacketHeader.Size, header.DataSize);
        var optional = new PacketHeaderOptional();
        int consumed = optional.Parse(body, header.Flags);
        Assert.True(consumed >= 0);

        var fragments = new List<MessageFragment>();
        ReadOnlySpan<byte> remaining = body.Slice(consumed);
        while (!remaining.IsEmpty)
        {
            (MessageFragment? fragment, int fragmentBytes) = MessageFragment.TryParse(remaining);
            Assert.NotNull(fragment);
            fragments.Add(fragment!.Value);
            remaining = remaining.Slice(fragmentBytes);
        }

        return fragments.ToArray();
    }

    private static uint[] NakIds(byte[] nakDatagram)
    {
        PacketCodec.PacketDecodeResult decoded =
            PacketCodec.TryDecode(nakDatagram, inboundIsaac: null);
        Assert.True(decoded.IsOk, decoded.Error.ToString());
        return decoded.Packet!.Optional.RetransmitRequests.ToArray();
    }

    private static uint[] RejectIds(byte[] rejectDatagram)
    {
        ReadOnlySpan<byte> body = rejectDatagram.AsSpan(PacketHeader.Size);
        uint count = BinaryPrimitives.ReadUInt32LittleEndian(body);
        var ids = new uint[count];
        for (int i = 0; i < ids.Length; i++)
            ids[i] = BinaryPrimitives.ReadUInt32LittleEndian(body.Slice(4 + i * 4));
        return ids;
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

    private static IsaacRandom MakeIsaac(uint seed)
    {
        Span<byte> seedBytes = stackalloc byte[4];
        BinaryPrimitives.WriteUInt32LittleEndian(seedBytes, seed);
        return new IsaacRandom(seedBytes);
    }

    private sealed class TestAcClient
    {
        private readonly IsaacRandom _outboundIsaac;

        /// <summary>WorldSession.cs:868 — the post-handshake reliable stream starts at 2.</summary>
        public uint PacketSequence = 2;

        /// <summary>WorldSession.cs:680 — fragment sequence starts at 1.</summary>
        public uint FragmentSequence = 1;

        public TestAcClient(uint clientSeed) => _outboundIsaac = MakeIsaac(clientSeed);

        public byte[] BuildGameMessagePacket(byte[] messageBody) =>
            BuildGameMessagePacket(PacketSequence++, FragmentSequence++, messageBody);

        public byte[] BuildGameMessagePacket(
            uint packetSequence,
            uint fragmentSequence,
            byte[] messageBody)
        {
            byte[] fragment = GameMessageFragment.Serialize(
                GameMessageFragment.BuildSingleFragment(
                    fragmentSequence,
                    GameMessageGroup.UIQueue,
                    messageBody));
            var header = new PacketHeader
            {
                Sequence = packetSequence,
                Flags = PacketHeaderFlags.BlobFragments | PacketHeaderFlags.EncryptedChecksum,
                Id = (ushort)ClientId,
            };
            return PacketCodec.Encode(header, fragment, _outboundIsaac);
        }

        public byte[] BuildFragmentPacket(
            uint packetSequence,
            uint fragmentSequence,
            ushort count,
            ushort index,
            byte[] payload)
        {
            var fragmentHeader = new MessageFragmentHeader
            {
                Sequence = fragmentSequence,
                Id = GameMessageFragment.OutboundFragmentId,
                Count = count,
                TotalSize = (ushort)(MessageFragmentHeader.Size + payload.Length),
                Index = index,
                Queue = (ushort)GameMessageGroup.UIQueue,
            };

            int bodyLength = MessageFragmentHeader.Size + payload.Length;
            byte[] datagram = new byte[PacketHeader.Size + bodyLength];
            fragmentHeader.Pack(datagram.AsSpan(PacketHeader.Size));
            payload.CopyTo(datagram.AsSpan(PacketHeader.Size + MessageFragmentHeader.Size));

            var header = new PacketHeader
            {
                Sequence = packetSequence,
                Flags = PacketHeaderFlags.BlobFragments | PacketHeaderFlags.EncryptedChecksum,
                Id = (ushort)ClientId,
                DataSize = (ushort)bodyLength,
            };
            uint payloadHash = PacketCodec.CalculateFragmentHash32(
                new MessageFragment(fragmentHeader, payload));
            header.Checksum =
                header.CalculateHeaderHash32() + (_outboundIsaac.Next() ^ payloadHash);
            header.Pack(datagram);
            return datagram;
        }

        public byte[] BuildCleartextAck(uint headerSequence, uint ackValue) =>
            BuildAck(headerSequence, ackValue, encrypted: false);

        public byte[] BuildEncryptedAck(uint headerSequence, uint ackValue) =>
            BuildAck(headerSequence, ackValue, encrypted: true);

        private byte[] BuildAck(uint headerSequence, uint ackValue, bool encrypted)
        {
            byte[] body = new byte[4];
            BinaryPrimitives.WriteUInt32LittleEndian(body, ackValue);
            return PacketCodec.Encode(
                new PacketHeader
                {
                    Sequence = headerSequence,
                    Flags = encrypted
                        ? PacketHeaderFlags.AckSequence | PacketHeaderFlags.EncryptedChecksum
                        : PacketHeaderFlags.AckSequence,
                    Id = (ushort)ClientId,
                },
                body,
                encrypted ? _outboundIsaac : null);
        }

        public byte[] BuildCleartextNak(uint headerSequence, params uint[] ids)
        {
            byte[] body = new byte[4 + ids.Length * 4];
            BinaryPrimitives.WriteUInt32LittleEndian(body, (uint)ids.Length);
            for (int i = 0; i < ids.Length; i++)
                BinaryPrimitives.WriteUInt32LittleEndian(body.AsSpan(4 + i * 4), ids[i]);
            return PacketCodec.Encode(
                new PacketHeader
                {
                    Sequence = headerSequence,
                    Flags = PacketHeaderFlags.RequestRetransmit,
                    Id = (ushort)ClientId,
                },
                body,
                outboundIsaac: null);
        }

        public byte[] BuildCleartextEchoRequest(uint headerSequence, float clientTime)
        {
            byte[] body = new byte[4];
            BinaryPrimitives.WriteSingleLittleEndian(body, clientTime);
            return PacketCodec.Encode(
                new PacketHeader
                {
                    Sequence = headerSequence,
                    Flags = PacketHeaderFlags.EchoRequest,
                    Id = (ushort)ClientId,
                },
                body,
                outboundIsaac: null);
        }
    }
}
