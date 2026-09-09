using System.Buffers.Binary;
using AcDream.Core.Net.Cryptography;
using AcDream.Core.Net.Messages;
using AcDream.Core.Net.Packets;

namespace AcDream.Core.Net.Tests.Transport;

internal enum AceTerminationReason
{
    None,
    PacketHeaderDisconnect,
    ClientSentNetworkErrorDisconnect,
    AbnormalSequenceReceived,
    NetworkTimeout,
}

internal enum AceSessionState
{
    AuthLoginRequest,
    AuthConnectResponse,
    AuthConnected,
}

internal enum AceTerminationPhase
{
    /// <summary>Session.cs:126-131 — the ~2 s window in which inbound and outbound still run.</summary>
    Initialized,
    /// <summary>Session.cs:130-131 — the window elapsed; WorldManager may now DropSession.</summary>
    SessionWorkCompleted,
}

internal sealed class AceSessionModel
{
    private const int MaxNumNakSeqIds = 115;
    /// <summary>ServerPacket.cs:11 — the S2C body budget after the 20-byte header.</summary>
    private const int MaxPacketSize = 464;
    /// <summary>GameMessageGroup.cs:18 — the bundle array length.</summary>
    private const int QueueMax = 0x0C;
    /// <summary>NetworkSession.cs:359 — `new TimeSpan(0, 0, 1)` NAK rate limit.</summary>
    private static readonly long NakRateLimitTicks = TimeSpan.FromSeconds(1).Ticks;
    /// <summary>NetworkSession.cs:32 — timeBetweenAck = 2000 ms.</summary>
    private static readonly long AckIntervalTicks = TimeSpan.FromSeconds(2).Ticks;
    /// <summary>NetworkSession.cs:31 — timeBetweenTimeSync = 20000 ms.</summary>
    private static readonly long TimeSyncIntervalTicks = TimeSpan.FromSeconds(20).Ticks;
    /// <summary>NetworkManager.DefaultSessionTimeout (60 s), applied at NetworkSession.cs:329-331.</summary>
    private static readonly long SessionTimeoutTicks = TimeSpan.FromSeconds(60).Ticks;
    private static readonly long CachePruneIntervalTicks = TimeSpan.FromSeconds(5).Ticks;
    private const int CachedPacketRetentionSeconds = 120;
    /// <summary>SessionTerminationDetails.cs:12 — TerminationEndTicks = start + 2 s.</summary>
    private static readonly long TerminationWindowTicks = TimeSpan.FromSeconds(2).Ticks;

    // ---- identity / handshake material ----
    private readonly VirtualClock _clock;
    private readonly ushort _serverId;
    private readonly uint _clientId;
    private readonly ulong _cookie;
    private readonly uint _clientSeed;
    private readonly uint _serverSeed;

    public AceCryptoModel Crypto { get; }

    /// <summary>S2C keystream — SessionConnectionData.IssacServer (SessionConnectionData.cs:62).</summary>
    private readonly IsaacRandom _s2cKeystream;

    // ---- receive state ----
    /// <summary>NetworkSession.cs:57 — starts at 1.</summary>
    private uint _lastReceivedPacketSequence = 1;
    /// <summary>NetworkSession.cs:58 — starts at 0.</summary>
    private uint _lastReceivedFragmentSequence;
    /// <summary>NetworkSession.cs:41 — outOfOrderPackets (parsed + CRC-verified; never re-verified).</summary>
    private readonly Dictionary<uint, ParsedPacket> _outOfOrderPackets = new();
    /// <summary>NetworkSession.cs:42 — partialFragments (multi-fragment C2S reassembly).</summary>
    private readonly Dictionary<uint, PartialC2SMessage> _partialFragments = new();
    /// <summary>NetworkSession.cs:43 — outOfOrderFragments (the C2S fragment gate buffer).</summary>
    private readonly Dictionary<uint, byte[]> _outOfOrderFragments = new();
    /// <summary>NetworkSession.cs:428 — LastRequestForRetransmitTime (DateTime.MinValue ≙ null).</summary>
    private long? _lastNakTimestamp;

    private uint _packetSequence = uint.MaxValue;
    /// <summary>SessionConnectionData.cs:36 — FragmentSequence, default 0; assigned at bundle flush (NetworkSession.cs:821).</summary>
    private uint _s2cFragmentSequence;
    private readonly Dictionary<uint, CachedS2CPacket> _cachedPackets = new();
    private long? _lastPruneTimestamp;
    private long _nextAckTimestamp;
    private long? _nextResyncTimestamp;
    private bool _sendResync;
    /// <summary>NetworkSession.cs:38-39 — one NetworkBundle per GameMessageGroup.</summary>
    private readonly PendingBundle[] _bundles = new PendingBundle[QueueMax];
    /// <summary>NetworkSession.cs:81 — packetQueue, drained by FlushPackets in Update.</summary>
    private readonly Queue<OutboundDraft> _flushQueue = new();

    // ---- termination state ----
    private long _terminationEndTimestamp;

    // ---- observable outputs ----
    private readonly List<byte[]> _dispatchedMessages = new();
    private readonly List<byte[]> _sentDatagrams = new();
    private readonly Queue<byte[]> _pendingOutbound = new();

    public AceSessionModel(
        VirtualClock clock,
        uint clientSeed,
        uint serverSeed,
        uint clientId,
        ulong cookie,
        ushort serverId = 0x000C)
    {
        _clock = clock;
        _clientSeed = clientSeed;
        _serverSeed = serverSeed;
        _clientId = clientId;
        _cookie = cookie;
        _serverId = serverId;

        Crypto = new AceCryptoModel(clientSeed);
        Span<byte> seedBytes = stackalloc byte[4];
        BinaryPrimitives.WriteUInt32LittleEndian(seedBytes, serverSeed);
        _s2cKeystream = new IsaacRandom(seedBytes);

        for (int i = 0; i < _bundles.Length; i++)
            _bundles[i] = new PendingBundle(); // NetworkSession.cs:105-109

        // NetworkSession.cs:54-55 — sendAck starts true with the 2 s delay armed.
        _nextAckTimestamp = clock.GetTimestamp() + AckIntervalTicks;
        // Simplified from NetworkSession.cs:102-103 (15 s pre-auth window);
        // the double pins the 60 s in-world horizon of :329-331 only.
        TimeoutDeadlineTimestamp = clock.GetTimestamp() + SessionTimeoutTicks;
    }

    public AceSessionState State { get; private set; } = AceSessionState.AuthLoginRequest;
    public uint LastReceivedPacketSequence => _lastReceivedPacketSequence;
    public uint LastReceivedFragmentSequence => _lastReceivedFragmentSequence;
    public IReadOnlyList<byte[]> DispatchedMessages => _dispatchedMessages;
    public IReadOnlyList<byte[]> SentDatagrams => _sentDatagrams;
    public bool IsTerminated => TerminationPhase is not null;
    /// <summary>Session.PendingTermination.TerminationStatus (Session.cs:56, :126-131).</summary>
    public AceTerminationPhase? TerminationPhase { get; private set; }
    public bool IsReleased { get; private set; }
    public AceTerminationReason TerminationReason { get; private set; } = AceTerminationReason.None;
    public long TimeoutDeadlineTimestamp { get; private set; }
    public int OutOfOrderPacketCount => _outOfOrderPackets.Count;
    public int FragmentGateBufferCount => _outOfOrderFragments.Count;
    public int PartialFragmentBufferCount => _partialFragments.Count;
    public int CachedPacketCount => _cachedPackets.Count;
    public IReadOnlyCollection<uint> CachedPacketSequences => _cachedPackets.Keys;
    /// <summary>Packets silently dropped by CRC/Search failure (NetworkSession.cs:277-280).</summary>
    public int CrcDropCount { get; private set; }
    public uint LastCrcDropSequence { get; private set; }
    public PacketHeaderFlags LastCrcDropFlags { get; private set; }
    public uint LastCrcDropWatermark { get; private set; }
    /// <summary>Packets dropped by the duplicate-rejection rule (NetworkSession.cs:342-347).</summary>
    public int DuplicateDropCount { get; private set; }
    public int StateDropCount { get; private set; }
    public int RetransmitsServed { get; private set; }

    // ---- script hooks for FakeAceTransport ----
    /// <summary>Fired when a LoginRequest packet is handled (NetworkSession.cs:463-468).</summary>
    public event Action? LoginRequestReceived;
    public event Action? ConnectResponseAccepted;
    public event Action<byte[]>? MessageDispatched;

    public List<byte[]> TakePendingDatagrams()
    {
        var drained = new List<byte[]>(_pendingOutbound.Count);
        while (_pendingOutbound.TryDequeue(out byte[]? datagram))
            drained.Add(datagram);
        return drained;
    }

    public void Receive(ReadOnlySpan<byte> datagram)
    {
        if (IsReleased)
            return; // isReleased guard (:271-272)

        if (!TryParse(datagram, out ParsedPacket packet))
            return;

        if (!CheckState(packet.Header))
        {
            StateDropCount++;
            return;
        }

        if ((packet.Header.Flags & PacketHeaderFlags.ConnectResponse) != 0)
        {
            HandleConnectResponse(packet);
            return;
        }

        if (!VerifyCrc(packet))
        {
            CrcDropCount++;
            LastCrcDropSequence = packet.Header.Sequence;
            LastCrcDropFlags = packet.Header.Flags;
            LastCrcDropWatermark = _lastReceivedPacketSequence;
            return;
        }

        if ((packet.Header.Flags & PacketHeaderFlags.RequestRetransmit) != 0
            && (packet.Header.Flags & PacketHeaderFlags.EncryptedChecksum) == 0)
        {
            List<uint>? uncached = null;
            foreach (uint sequence in packet.Optional.RetransmitRequests)
            {
                if (!TryRetransmit(sequence))
                    (uncached ??= new List<uint>()).Add(sequence);
            }

            if (uncached is not null)
                EnqueueRejectRetransmit(uncached); // :299-304 (sent on the next Update flush)
            return; // :307
        }

        // 3. Disconnect headers (:312-321).
        if ((packet.Header.Flags & PacketHeaderFlags.Disconnect) != 0)
        {
            Terminate(AceTerminationReason.PacketHeaderDisconnect);
            return;
        }

        if ((packet.Header.Flags & PacketHeaderFlags.NetErrorDisconnect) != 0)
        {
            Terminate(AceTerminationReason.ClientSentNetworkErrorDisconnect);
            return;
        }

        // 4. Timeout refresh (:329-331) — 60 s in-world horizon.
        TimeoutDeadlineTimestamp = _clock.GetTimestamp() + SessionTimeoutTicks;

        if (packet.Header.Sequence <= _lastReceivedPacketSequence
            && packet.Header.Sequence != 0
            && !(packet.Header.Flags == PacketHeaderFlags.AckSequence
                 && packet.Header.Sequence == _lastReceivedPacketSequence))
        {
            DuplicateDropCount++;
            return;
        }

        // 6. Out-of-order buffering (:351-363). NAK trigger fires only at
        //    desiredSeq + 2 ≤ arrivedSeq, arrival-driven, with a 1 s rate
        //    limit; a quiet link is never NAKed.
        uint desiredSeq = _lastReceivedPacketSequence + 1;
        if (packet.Header.Sequence > desiredSeq)
        {
            if (!_outOfOrderPackets.ContainsKey(packet.Header.Sequence))
                _outOfOrderPackets.Add(packet.Header.Sequence, packet);

            bool rateLimitOpen =
                _lastNakTimestamp is null
                || _clock.GetTimestamp() - _lastNakTimestamp.Value > NakRateLimitTicks;
            if (desiredSeq + 2 <= packet.Header.Sequence && rateLimitOpen)
                DoRequestForRetransmission(packet.Header.Sequence);
            return;
        }

        // 7. Final processing (:367-378).
        HandleOrderedPacket(packet);
        CheckOutOfOrderPackets();
        CheckOutOfOrderFragments();
    }

    private bool CheckState(PacketHeader header)
    {
        // :95-96
        if ((header.Flags & PacketHeaderFlags.LoginRequest) != 0
            && State != AceSessionState.AuthLoginRequest)
        {
            return false;
        }

        // :98-99 (and the identical requirement on NetworkManager's port+1
        // path, NetworkManager.cs:60-66).
        if ((header.Flags & PacketHeaderFlags.ConnectResponse) != 0
            && State != AceSessionState.AuthConnectResponse)
        {
            return false;
        }

        // :101-102
        const PacketHeaderFlags controlFlags =
            PacketHeaderFlags.AckSequence
            | PacketHeaderFlags.TimeSync
            | PacketHeaderFlags.EchoRequest
            | PacketHeaderFlags.Flow;
        if ((header.Flags & controlFlags) != 0
            && State == AceSessionState.AuthLoginRequest)
        {
            return false;
        }

        return true;
    }

    private bool VerifyCrc(ParsedPacket packet)
    {
        uint headerHash = packet.Header.CalculateHeaderHash32();
        uint payloadHash = packet.Optional.CalculateHash32() + packet.FragmentHash;

        if ((packet.Header.Flags & PacketHeaderFlags.EncryptedChecksum) != 0)
        {
            uint key = (packet.Header.Checksum - headerHash) ^ payloadHash;
            if (Crypto.Search(key))
            {
                Crypto.ConsumeKey(key);
                return true;
            }

            return false;
        }

        return headerHash + payloadHash == packet.Header.Checksum;
    }

    private void HandleConnectResponse(ParsedPacket packet)
    {
        if (packet.Header.Flags != PacketHeaderFlags.ConnectResponse)
            return;
        if (packet.Optional.RawBytes.Length < 8)
            return;
        ulong cookie = BinaryPrimitives.ReadUInt64LittleEndian(packet.Optional.RawBytes);
        if (cookie != _cookie)
            return;

        State = AceSessionState.AuthConnected; // NetworkManager.cs:77
        _sendResync = true; // NetworkManager.cs:78 — first TimeSync goes out immediately (:47-50)
        TimeoutDeadlineTimestamp = _clock.GetTimestamp() + SessionTimeoutTicks;
        ConnectResponseAccepted?.Invoke();
    }

    /// <summary>NetworkSession.HandleOrderedPacket (:435-477).</summary>
    private void HandleOrderedPacket(ParsedPacket packet)
    {
        if ((packet.Header.Flags & PacketHeaderFlags.EchoRequest) != 0)
            FlagEcho(packet.Optional.EchoRequestClientTime);

        if ((packet.Header.Flags & PacketHeaderFlags.AckSequence) != 0)
            AcknowledgeSequence(packet.Optional.AckSequence);

        // :450-457 — inbound TimeSync is read and ignored.

        if ((packet.Header.Flags & PacketHeaderFlags.LoginRequest) != 0)
        {
            LoginRequestReceived?.Invoke();
            return;
        }

        // :471-472 — fragments.
        foreach (MessageFragment fragment in packet.Fragments)
            ProcessFragment(fragment);

        if (packet.Header.Sequence != 0
            && packet.Header.Flags != PacketHeaderFlags.AckSequence)
        {
            _lastReceivedPacketSequence = packet.Header.Sequence;
        }
    }

    /// <summary>NetworkSession.FlagEcho (:650-661).</summary>
    private void FlagEcho(float clientTime)
    {
        PendingBundle bundle = _bundles[(int)GameMessageGroup.InvalidQueue];
        bundle.ClientTime = clientTime;
        bundle.EncryptedChecksum = true;
    }

    /// <summary>NetworkSession.ProcessFragment (:483-544).</summary>
    private void ProcessFragment(MessageFragment fragment)
    {
        byte[]? message = null;

        if (fragment.Header.Count != 1)
        {
            if (_partialFragments.TryGetValue(fragment.Header.Sequence, out PartialC2SMessage? buffer))
            {
                buffer.AddFragment(fragment.Header.Index, fragment.Payload);
                if (buffer.Complete)
                {
                    // :504-506 — TryGetMessage may return null (assembled
                    // stream under 4 bytes, MessageBuffer.cs:49-50) but the
                    // buffer is removed EITHER WAY.
                    message = buffer.TryGetMessage();
                    _partialFragments.Remove(fragment.Header.Sequence);
                }
            }
            else
            {
                var newBuffer = new PartialC2SMessage(fragment.Header.Count);
                newBuffer.AddFragment(fragment.Header.Index, fragment.Payload);
                _partialFragments.TryAdd(fragment.Header.Sequence, newBuffer);
            }
        }
        else if (fragment.Payload.Length >= 4)
        {
            message = fragment.Payload;
        }

        if (message is null)
            return;

        if (fragment.Header.Sequence == _lastReceivedFragmentSequence + 1)
            HandleFragment(message);
        else
            _outOfOrderFragments.TryAdd(fragment.Header.Sequence, message);
    }

    /// <summary>NetworkSession.HandleFragment (:550-554).</summary>
    private void HandleFragment(byte[] message)
    {
        _dispatchedMessages.Add(message);
        MessageDispatched?.Invoke(message);
        _lastReceivedFragmentSequence++;
    }

    private void CheckOutOfOrderPackets()
    {
        while (_outOfOrderPackets.Remove(_lastReceivedPacketSequence + 1, out ParsedPacket? packet))
            HandleOrderedPacket(packet);
    }

    private void CheckOutOfOrderFragments()
    {
        while (_outOfOrderFragments.Remove(_lastReceivedFragmentSequence + 1, out byte[]? message))
            HandleFragment(message);
    }

    private void AcknowledgeSequence(uint sequence)
    {
        List<uint>? removal = null;
        foreach (uint key in _cachedPackets.Keys)
        {
            if (key < sequence)
                (removal ??= new List<uint>()).Add(key);
        }

        if (removal is null)
            return;
        foreach (uint key in removal)
            _cachedPackets.Remove(key);
    }

    /// <summary>NetworkSession.DoRequestForRetransmission (:387-426).</summary>
    private void DoRequestForRetransmission(uint rcvdSeq)
    {
        uint desiredSeq = _lastReceivedPacketSequence + 1; // :389
        var needSeq = new List<uint> { desiredSeq };       // :390-391
        uint bottom = desiredSeq + 1;                      // :392
        if (rcvdSeq < bottom || rcvdSeq - bottom > AceCryptoModel.MaximumEffortLevel)
        {
            Terminate(AceTerminationReason.AbnormalSequenceReceived);
            return;
        }

        uint seqIdCount = 1; // :398-410 — cap at 115 ids, skipping buffered arrivals
        for (uint a = bottom; a < rcvdSeq; a++)
        {
            if (_outOfOrderPackets.ContainsKey(a))
                continue;
            needSeq.Add(a);
            seqIdCount++;
            if (seqIdCount >= MaxNumNakSeqIds)
                break;
        }

        byte[] body = new byte[4 + needSeq.Count * 4];
        BinaryPrimitives.WriteUInt32LittleEndian(body, (uint)needSeq.Count);
        for (int i = 0; i < needSeq.Count; i++)
        {
            BinaryPrimitives.WriteUInt32LittleEndian(
                body.AsSpan(4 + i * 4),
                needSeq[i]);
        }

        _flushQueue.Enqueue(new OutboundDraft(
            PacketHeaderFlags.RequestRetransmit,
            body,
            OptionalLength: body.Length));

        _lastNakTimestamp = _clock.GetTimestamp(); // :422
    }

    private bool TryRetransmit(uint sequence)
    {
        if (!_cachedPackets.TryGetValue(sequence, out CachedS2CPacket? cached))
            return false;

        cached.Flags |= PacketHeaderFlags.Retransmission;

        Emit(cached.Sequence, cached.Flags, cached.Time, cached.Body, cached.OptionalLength, cached.IsaacXor);
        RetransmitsServed++;
        return true;
    }

    private void EnqueueRejectRetransmit(List<uint> uncached)
    {
        byte[] body = new byte[4 + uncached.Count * 4];
        BinaryPrimitives.WriteUInt32LittleEndian(body, (uint)uncached.Count);
        for (int i = 0; i < uncached.Count; i++)
        {
            BinaryPrimitives.WriteUInt32LittleEndian(
                body.AsSpan(4 + i * 4),
                uncached[i]);
        }

        _flushQueue.Enqueue(new OutboundDraft(
            PacketHeaderFlags.RejectRetransmit,
            body,
            OptionalLength: body.Length));
    }


    public void Update()
    {
        if (IsReleased)
            return; // NetworkSession.cs:184-185

        if (TerminationPhase is not null)
        {
            if (TerminationPhase == AceTerminationPhase.Initialized)
            {
                RunNetworkUpdate(); // :129 — "boot messages may need sending"
                if (_clock.GetTimestamp() > _terminationEndTimestamp)
                    TerminationPhase = AceTerminationPhase.SessionWorkCompleted; // :130-131
            }

            if (TerminationPhase == AceTerminationPhase.SessionWorkCompleted)
                Release();

            return; // :133
        }

        if (_clock.GetTimestamp() >= TimeoutDeadlineTimestamp)
        {
            Terminate(AceTerminationReason.NetworkTimeout);
            return;
        }

        RunNetworkUpdate(); // :147
    }

    /// <summary>NetworkSession.Update (:182-249).</summary>
    private void RunNetworkUpdate()
    {
        if (_lastPruneTimestamp is null
            || _clock.GetTimestamp() - _lastPruneTimestamp.Value > CachePruneIntervalTicks)
        {
            PruneCachedPackets();
        }

        if (State == AceSessionState.AuthConnected)
        {
            for (int i = 0; i < QueueMax; i++)
            {
                var group = (GameMessageGroup)i;
                PendingBundle bundle = _bundles[i];

                if (group == GameMessageGroup.InvalidQueue)
                {
                    if (_sendResync
                        && !bundle.TimeSync
                        && (_nextResyncTimestamp is null
                            || _clock.GetTimestamp() > _nextResyncTimestamp.Value))
                    {
                        bundle.TimeSync = true;
                        bundle.EncryptedChecksum = true;
                        _nextResyncTimestamp = _clock.GetTimestamp() + TimeSyncIntervalTicks;
                    }

                    if (!bundle.SendAck && _clock.GetTimestamp() > _nextAckTimestamp)
                    {
                        bundle.SendAck = true;
                        _nextAckTimestamp = _clock.GetTimestamp() + AckIntervalTicks;
                    }
                }

                if (!bundle.NeedsSending)
                    continue;

                _bundles[i] = new PendingBundle(); // :223 / :233 — swap
                SendBundle(bundle, group);         // :243
            }
        }

        // FlushPackets (:710-735) — drains receive-time NAK/Reject enqueues
        // first (FIFO), then this pump's bundles.
        while (_flushQueue.TryDequeue(out OutboundDraft draft))
            FlushOne(draft);
    }

    public void EnqueueGameMessage(byte[] gameMessageBody, GameMessageGroup group)
    {
        PendingBundle bundle = _bundles[(int)group];
        bundle.EncryptedChecksum = true;
        bundle.Enqueue(gameMessageBody);
    }

    public void SendConnectRequest()
    {
        byte[] body = new byte[32];
        BinaryPrimitives.WriteInt64LittleEndian(
            body,
            BitConverter.DoubleToInt64Bits(_clock.Seconds));
        BinaryPrimitives.WriteUInt64LittleEndian(body.AsSpan(8), _cookie);
        BinaryPrimitives.WriteUInt32LittleEndian(body.AsSpan(16), _clientId);
        BinaryPrimitives.WriteUInt32LittleEndian(body.AsSpan(20), _serverSeed);
        BinaryPrimitives.WriteUInt32LittleEndian(body.AsSpan(24), _clientSeed);
        // bytes 28..31: trailing padding uint, zero.

        _flushQueue.Enqueue(new OutboundDraft(
            PacketHeaderFlags.ConnectRequest,
            body,
            OptionalLength: body.Length));

        State = AceSessionState.AuthConnectResponse; // AuthenticationHandler.cs:232
    }

    private void SendBundle(PendingBundle bundle, GameMessageGroup group)
    {
        bool writeOptionalHeaders = true;

        // :817-823 — pull every message out and wrap it in a MessageFragment.
        var fragments = new List<OutboundMessage>();
        while (bundle.HasMoreMessages)
            fragments.Add(new OutboundMessage(bundle.Dequeue(), _s2cFragmentSequence++, group));

        // :828 — loop while we have fragments (or still owe optional headers).
        while (fragments.Count > 0 || writeOptionalHeaders)
        {
            var flags = PacketHeaderFlags.None;
            var packetFragments = new List<MessageFragment>();
            byte[] optionalBytes = Array.Empty<byte>();

            if (fragments.Count > 0)
                flags |= PacketHeaderFlags.BlobFragments; // :833-834
            if (bundle.EncryptedChecksum)
                flags |= PacketHeaderFlags.EncryptedChecksum; // :836-837

            int availableSpace = MaxPacketSize; // :839

            OutboundMessage? firstMessage = fragments.Count > 0 ? fragments[0] : null; // :842
            if (firstMessage is not null)
            {
                if (firstMessage.DataRemaining >= availableSpace)
                {
                    // :846-854 — a large message fills the whole packet alone.
                    MessageFragment spf = firstMessage.GetNextFragment();
                    packetFragments.Add(spf);
                    availableSpace -= spf.WireSize;
                    if (firstMessage.DataRemaining <= 0)
                        fragments.Remove(firstMessage);
                }
                else
                {
                    // :856-903 — optional headers first, then pack in as many
                    // small messages (and large-message tails) as fit.
                    if (writeOptionalHeaders)
                    {
                        writeOptionalHeaders = false;
                        optionalBytes = WriteOptionalHeaders(bundle, ref flags);
                        availableSpace -= optionalBytes.Length;
                    }

                    var removeList = new List<OutboundMessage>();
                    foreach (OutboundMessage fragment in fragments)
                    {
                        bool fragmentSkipped = false;

                        if (!fragment.TailSent && availableSpace >= fragment.TailSize)
                        {
                            // :874-880 — the tail of an already-split message.
                            MessageFragment spf = fragment.GetTailFragment();
                            packetFragments.Add(spf);
                            availableSpace -= spf.WireSize;
                        }
                        else if (availableSpace >= fragment.NextSize)
                        {
                            // :882-888 — a whole small message.
                            MessageFragment spf = fragment.GetNextFragment();
                            packetFragments.Add(spf);
                            availableSpace -= spf.WireSize;
                        }
                        else
                        {
                            fragmentSkipped = true;
                        }

                        if (fragment.DataRemaining <= 0)
                            removeList.Add(fragment); // :892-894

                        // :896-898 — UIQueue must stay strictly ordered.
                        if (fragmentSkipped && group == GameMessageGroup.UIQueue)
                            break;
                    }

                    fragments.RemoveAll(removeList.Contains); // :902
                }
            }
            else if (writeOptionalHeaders)
            {
                writeOptionalHeaders = false;
                optionalBytes = WriteOptionalHeaders(bundle, ref flags);
            }

            _flushQueue.Enqueue(BuildDraft(flags, optionalBytes, packetFragments)); // :917
        }
    }

    private byte[] WriteOptionalHeaders(PendingBundle bundle, ref PacketHeaderFlags flags)
    {
        var writer = new PacketWriter(24);

        if (bundle.SendAck) // :925-931
        {
            flags |= PacketHeaderFlags.AckSequence;
            writer.WriteUInt32(_lastReceivedPacketSequence);
        }

        if (bundle.TimeSync) // :933-939
        {
            flags |= PacketHeaderFlags.TimeSync;
            Span<byte> value = stackalloc byte[8];
            BinaryPrimitives.WriteInt64LittleEndian(
                value,
                BitConverter.DoubleToInt64Bits(_clock.Seconds));
            writer.WriteBytes(value);
        }

        if (bundle.ClientTime != -1f) // :941-948
        {
            flags |= PacketHeaderFlags.EchoResponse;
            writer.WriteFloat(bundle.ClientTime);
            writer.WriteFloat((float)_clock.Seconds - bundle.ClientTime);
        }

        return writer.ToArray();
    }

    private static OutboundDraft BuildDraft(
        PacketHeaderFlags flags,
        byte[] optionalBytes,
        List<MessageFragment> fragments)
    {
        int total = optionalBytes.Length;
        foreach (MessageFragment fragment in fragments)
            total += fragment.WireSize;

        byte[] body = new byte[total];
        optionalBytes.CopyTo(body.AsSpan());
        int offset = optionalBytes.Length;
        foreach (MessageFragment fragment in fragments)
        {
            fragment.Header.Pack(body.AsSpan(offset));
            fragment.Payload.CopyTo(body.AsSpan(offset + MessageFragmentHeader.Size));
            offset += fragment.WireSize;
        }

        return new OutboundDraft(flags, body, optionalBytes.Length);
    }

    /// <summary>FlushPackets, per packet (:710-735) + SendPacket (:737-752).</summary>
    private void FlushOne(OutboundDraft draft)
    {
        bool encrypted = (draft.Flags & PacketHeaderFlags.EncryptedChecksum) != 0;

        if (encrypted && _packetSequence == 0)
            _packetSequence = 1;

        bool isNak = (draft.Flags & PacketHeaderFlags.RequestRetransmit) != 0; // :719

        uint sequence = draft.Flags == PacketHeaderFlags.AckSequence || isNak
            ? _packetSequence
            : NextPacketSequence();

        // :728 — Header.Time = (ushort)PortalYearTicks (whole seconds).
        ushort time = (ushort)(long)_clock.Seconds;

        uint isaacXor = encrypted ? _s2cKeystream.Next() : 0u;

        if (sequence >= 2u && !isNak)
        {
            _cachedPackets.TryAdd(sequence, new CachedS2CPacket
            {
                Sequence = sequence,
                Flags = draft.Flags,
                Time = time,
                Body = draft.Body,
                OptionalLength = draft.OptionalLength,
                IsaacXor = isaacXor,
            });
        }

        Emit(sequence, draft.Flags, time, draft.Body, draft.OptionalLength, isaacXor);
    }

    /// <summary>UIntSequence.NextValue (UIntSequence.cs:30-41): wrap max → 0.</summary>
    private uint NextPacketSequence()
    {
        _packetSequence = _packetSequence == uint.MaxValue ? 0u : _packetSequence + 1u;
        return _packetSequence;
    }

    private void Emit(
        uint sequence,
        PacketHeaderFlags flags,
        ushort time,
        byte[] body,
        int optionalLength,
        uint isaacXor)
    {
        var header = new PacketHeader
        {
            Sequence = sequence,
            Flags = flags,
            Id = _serverId,       // :726
            Iteration = 1,        // :727
            Time = time,
            DataSize = checked((ushort)body.Length),
        };

        uint payloadHash = ComputePayloadHash(body, flags, optionalLength);
        uint headerHash = header.CalculateHeaderHash32();
        header.Checksum = headerHash + (payloadHash ^ isaacXor); // ServerPacket.cs:70

        byte[] datagram = new byte[PacketHeader.Size + body.Length];
        header.Pack(datagram);
        body.CopyTo(datagram.AsSpan(PacketHeader.Size));

        _sentDatagrams.Add(datagram);
        _pendingOutbound.Enqueue(datagram);
    }

    /// <summary>ServerPacket.cs:48-62 — Hash32(data section) + Σ fragment hashes.</summary>
    private static uint ComputePayloadHash(
        ReadOnlySpan<byte> body,
        PacketHeaderFlags flags,
        int optionalLength)
    {
        uint hash = Hash32.Calculate(body.Slice(0, optionalLength));
        if ((flags & PacketHeaderFlags.BlobFragments) == 0)
            return hash;

        ReadOnlySpan<byte> remaining = body.Slice(optionalLength);
        while (!remaining.IsEmpty)
        {
            if (!TryParseClientFragment(remaining, out MessageFragment fragment, out int consumed))
                throw new InvalidOperationException("the model built a malformed fragment");
            hash += PacketCodec.CalculateFragmentHash32(fragment);
            remaining = remaining.Slice(consumed);
        }

        return hash;
    }

    private void PruneCachedPackets()
    {
        _lastPruneTimestamp = _clock.GetTimestamp(); // :253
        ushort currentTime = (ushort)(long)_clock.Seconds; // :255

        List<uint>? removal = null;
        foreach (CachedS2CPacket packet in _cachedPackets.Values)
        {
            if ((currentTime >= packet.Time ? currentTime : currentTime + ushort.MaxValue) - packet.Time
                > CachedPacketRetentionSeconds)
            {
                (removal ??= new List<uint>()).Add(packet.Sequence);
            }
        }

        if (removal is null)
            return;
        foreach (uint sequence in removal)
            _cachedPackets.Remove(sequence);
    }

    private void Terminate(AceTerminationReason reason)
    {
        TerminationReason = reason;
        TerminationPhase = AceTerminationPhase.Initialized;
        _terminationEndTimestamp = _clock.GetTimestamp() + TerminationWindowTicks;
    }

    /// <summary>NetworkSession.ReleaseResources (:958-974), reached through
    /// Session.DropSession (:300-334).</summary>
    private void Release()
    {
        IsReleased = true;
        _outOfOrderPackets.Clear();
        _partialFragments.Clear();
        _outOfOrderFragments.Clear();
        _cachedPackets.Clear();
        _flushQueue.Clear();
        for (int i = 0; i < _bundles.Length; i++)
            _bundles[i] = new PendingBundle();
    }

    private static bool TryParse(ReadOnlySpan<byte> datagram, out ParsedPacket packet)
    {
        packet = null!;
        if (datagram.Length < PacketHeader.Size)
            return false;

        PacketHeader header = PacketHeader.Unpack(datagram);
        if (header.DataSize > datagram.Length - PacketHeader.Size)
            return false;

        ReadOnlySpan<byte> body = datagram.Slice(PacketHeader.Size, header.DataSize);
        var optional = new PacketHeaderOptional();
        int optionalConsumed = optional.Parse(body, header.Flags);
        if (optionalConsumed < 0)
            return false;

        var fragments = new List<MessageFragment>();
        uint fragmentHash = 0;
        if ((header.Flags & PacketHeaderFlags.BlobFragments) != 0)
        {
            ReadOnlySpan<byte> remaining = body.Slice(optionalConsumed);
            while (!remaining.IsEmpty)
            {
                if (!TryParseClientFragment(remaining, out MessageFragment fragment, out int consumed))
                    return false;
                fragments.Add(fragment);
                fragmentHash += PacketCodec.CalculateFragmentHash32(fragment);
                remaining = remaining.Slice(consumed);
            }
        }

        packet = new ParsedPacket(header, optional, fragments, fragmentHash);
        return true;
    }

    private static bool TryParseClientFragment(
        ReadOnlySpan<byte> source,
        out MessageFragment fragment,
        out int consumed)
    {
        fragment = default;
        consumed = 0;

        if (source.Length < MessageFragmentHeader.Size)
            return false;

        MessageFragmentHeader header = MessageFragmentHeader.Unpack(source);
        if (header.TotalSize < MessageFragmentHeader.Size)
            return false;
        if (header.TotalSize > MessageFragmentHeader.MaxFragmentSize)
            return false;

        int payloadLength = Math.Min(
            header.TotalSize - MessageFragmentHeader.Size,
            source.Length - MessageFragmentHeader.Size);
        fragment = new MessageFragment(
            header,
            source.Slice(MessageFragmentHeader.Size, payloadLength).ToArray());
        consumed = MessageFragmentHeader.Size + payloadLength;
        return true;
    }

    private sealed record ParsedPacket(
        PacketHeader Header,
        PacketHeaderOptional Optional,
        List<MessageFragment> Fragments,
        uint FragmentHash);

    private readonly record struct OutboundDraft(
        PacketHeaderFlags Flags,
        byte[] Body,
        int OptionalLength);

    private sealed class CachedS2CPacket
    {
        public uint Sequence;
        public PacketHeaderFlags Flags;
        public ushort Time;
        public byte[] Body = Array.Empty<byte>();
        public int OptionalLength;
        public uint IsaacXor;
    }

    private sealed class PendingBundle
    {
        private readonly Queue<byte[]> _messages = new();
        private bool _propChanged;

        /// <summary>NetworkBundle.cs:11.</summary>
        public bool NeedsSending => _propChanged || _messages.Count > 0;
        /// <summary>NetworkBundle.cs:13.</summary>
        public bool HasMoreMessages => _messages.Count > 0;

        private float _clientTime = -1f;
        /// <summary>NetworkBundle.cs:18-27 — -1f means "no echo pending".</summary>
        public float ClientTime
        {
            get => _clientTime;
            set { _clientTime = value; _propChanged = true; }
        }

        private bool _timeSync;
        /// <summary>NetworkBundle.cs:29-38.</summary>
        public bool TimeSync
        {
            get => _timeSync;
            set { _timeSync = value; _propChanged = true; }
        }

        private bool _sendAck;
        /// <summary>NetworkBundle.cs:40-49.</summary>
        public bool SendAck
        {
            get => _sendAck;
            set { _sendAck = value; _propChanged = true; }
        }

        /// <summary>NetworkBundle.cs:51.</summary>
        public bool EncryptedChecksum { get; set; }

        public void Enqueue(byte[] message) => _messages.Enqueue(message);

        public byte[] Dequeue() => _messages.Dequeue();
    }

    private sealed class OutboundMessage
    {
        private readonly byte[] _data;
        private readonly GameMessageGroup _group;
        private ushort _index;

        public uint Sequence { get; }
        public ushort Count { get; }
        public int DataRemaining { get; private set; }
        public bool TailSent { get; private set; }

        public int DataLength => _data.Length;
        /// <summary>MessageFragment.cs:27-36.</summary>
        public int NextSize =>
            MessageFragmentHeader.Size
            + Math.Min(DataRemaining, MessageFragmentHeader.MaxFragmentDataSize);
        /// <summary>MessageFragment.cs:38.</summary>
        public int TailSize =>
            MessageFragmentHeader.Size
            + (DataLength % MessageFragmentHeader.MaxFragmentDataSize);

        public OutboundMessage(byte[] data, uint sequence, GameMessageGroup group)
        {
            _data = data;
            _group = group;
            Sequence = sequence;
            DataRemaining = data.Length;
            Count = (ushort)Math.Ceiling(
                (double)data.Length / MessageFragmentHeader.MaxFragmentDataSize);
            _index = 0;
            if (Count == 1)
                TailSent = true; // :49-50
        }

        /// <summary>MessageFragment.cs:54-59.</summary>
        public MessageFragment GetTailFragment()
        {
            var index = (ushort)(Count - 1);
            TailSent = true;
            return CreateFragment(index);
        }

        /// <summary>MessageFragment.cs:61-64.</summary>
        public MessageFragment GetNextFragment() => CreateFragment(_index++);

        /// <summary>MessageFragment.cs:66-102.</summary>
        private MessageFragment CreateFragment(ushort index)
        {
            if (index >= Count)
                throw new ArgumentOutOfRangeException(nameof(index), index, "index beyond computed count");

            int position = index * MessageFragmentHeader.MaxFragmentDataSize;
            int dataToSend = Math.Min(
                DataLength - position,
                MessageFragmentHeader.MaxFragmentDataSize);
            if (DataRemaining < dataToSend)
                throw new InvalidOperationException("more data to send than data remaining");

            byte[] payload = _data.AsSpan(position, dataToSend).ToArray();
            DataRemaining -= dataToSend;

            return new MessageFragment(
                new MessageFragmentHeader
                {
                    Sequence = Sequence,
                    Id = GameMessageFragment.OutboundFragmentId,
                    Count = Count,
                    TotalSize = (ushort)(MessageFragmentHeader.Size + dataToSend),
                    Index = index,
                    Queue = (ushort)_group,
                },
                payload);
        }
    }

    private sealed class PartialC2SMessage
    {
        private readonly List<(ushort Index, byte[] Payload)> _fragments = new();
        private readonly int _totalFragments;

        public PartialC2SMessage(int totalFragments) => _totalFragments = totalFragments;

        /// <summary>MessageBuffer.cs:14.</summary>
        public bool Complete => _fragments.Count == _totalFragments;

        public void AddFragment(ushort index, byte[] payload)
        {
            if (Complete)
                return;
            foreach ((ushort existing, _) in _fragments)
            {
                if (existing == index)
                    return;
            }

            _fragments.Add((index, payload));
        }

        public byte[]? TryGetMessage()
        {
            _fragments.Sort((x, y) => x.Index - y.Index); // :38

            int total = 0;
            foreach ((_, byte[] payload) in _fragments)
                total += payload.Length;

            if (total < 4)
                return null; // :49-50

            byte[] message = new byte[total];
            int offset = 0;
            foreach ((_, byte[] payload) in _fragments)
            {
                payload.CopyTo(message.AsSpan(offset));
                offset += payload.Length;
            }

            return message;
        }
    }
}
