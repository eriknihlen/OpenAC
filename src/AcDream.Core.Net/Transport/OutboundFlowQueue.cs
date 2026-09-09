using System.Buffers;
using System.Buffers.Binary;
using AcDream.Core.Net.Cryptography;
using AcDream.Core.Net.Messages;
using AcDream.Core.Net.Packets;

namespace AcDream.Core.Net.Transport;

/// <summary>Sends one finalized datagram to the wire. Span-shaped so the
/// send path stays allocation-free.</summary>
internal delegate void DatagramSendDelegate(ReadOnlySpan<byte> datagram);

internal sealed class OutboundFlowQueue : IDisposable
{
    private readonly IsaacRandom _outboundIsaac;
    private readonly TransportClock _clock;
    private readonly TransportStats _stats;
    private readonly DatagramSendDelegate _send;
    private readonly SentPacketStore _store;
    private readonly ArrayPool<byte> _pool;
    private readonly ushort _sessionClientId;
    private readonly ushort _sessionIteration;

    private readonly List<uint> _pendingResends = new();

    public uint HighestIdSent { get; private set; }

    /// <summary>The fragment sequence the NEXT reliable message will use.
    /// Starts 1, exactly like the pre-N1 <c>WorldSession</c> field.</summary>
    public uint FragmentSequence { get; private set; }

    public uint AckWatermark { get; private set; }

    public int CacheDepth => _store.Count;

    public int PendingResendCount => _pendingResends.Count;

    public OutboundFlowQueue(
        IsaacRandom outboundIsaac,
        ushort sessionClientId,
        ushort sessionIteration,
        TransportClock clock,
        TransportStats stats,
        DatagramSendDelegate send,
        ArrayPool<byte>? pool = null,
        uint highestIdSent = 1,
        uint fragmentSequence = 1)
    {
        ArgumentNullException.ThrowIfNull(outboundIsaac);
        ArgumentNullException.ThrowIfNull(clock);
        ArgumentNullException.ThrowIfNull(stats);
        ArgumentNullException.ThrowIfNull(send);

        _outboundIsaac = outboundIsaac;
        _sessionClientId = sessionClientId;
        _sessionIteration = sessionIteration;
        _clock = clock;
        _stats = stats;
        _send = send;
        _pool = pool ?? ArrayPool<byte>.Shared;
        _store = new SentPacketStore(_pool);
        HighestIdSent = highestIdSent;
        FragmentSequence = fragmentSequence;
    }

    public uint PeekNextPacketSequence => NextSequenceAfter(HighestIdSent);

    private static uint NextSequenceAfter(uint sequence) =>
        sequence == uint.MaxValue ? 1u : sequence + 1u;

    public void SendGameMessage(
        ReadOnlySpan<byte> gameMessageBody,
        GameMessageGroup queue)
    {
        byte[] buffer = _pool.Rent(
            PacketHeader.Size
            + MessageFragmentHeader.Size
            + gameMessageBody.Length);
        try
        {
            int fragmentLength = GameMessageFragment.WriteSingleFragment(
                buffer.AsSpan(PacketHeader.Size),
                FragmentSequence,
                queue,
                gameMessageBody);
            var header = new PacketHeader
            {
                Sequence = PeekNextPacketSequence,
                Flags = PacketHeaderFlags.BlobFragments
                    | PacketHeaderFlags.EncryptedChecksum,
                Id = _sessionClientId,
                Time = _clock.IntervalId,
                Iteration = _sessionIteration,
            };
            int datagramLength = PacketCodec.FinalizeInPlace(
                header,
                buffer,
                fragmentLength,
                optionalLength: 0,
                _outboundIsaac,
                out uint isaacKeyUsed,
                out uint sealedChecksum);

            FragmentSequence++;
            HighestIdSent = header.Sequence;

            _send(buffer.AsSpan(0, datagramLength));

            _store.Add(
                new SentPacketStore.CachedPacket(
                    header.Sequence,
                    buffer,
                    fragmentLength,
                    sealedChecksum,
                    isaacKeyUsed,
                    hasFragments: true),
                optionalLength: 0);
        }
        catch
        {
            _pool.Return(buffer);
            throw;
        }
    }

    public void OnAckSequence(uint ackSequence)
    {
        _stats.AcksConsumed++;
        AckWatermark = SequenceMath.Max(AckWatermark, ackSequence);
    }

    public void OnRetransmitRequest(ReadOnlySpan<byte> idBytes, int count)
    {
        if (count <= 0 || idBytes.Length < count * 4)
            return;

        _stats.NakRequestsReceived++;
        for (int i = 0; i < count; i++)
        {
            uint id = BinaryPrimitives.ReadUInt32LittleEndian(
                idBytes.Slice(i * 4));
            if (i == 0)
                OnAckSequence(id);

            if (_store.Contains(id))
                MergeInsertPending(id);
            else
                _stats.UncachedNakIds++;
        }
    }

    private void MergeInsertPending(uint id)
    {
        int index = 0;
        while (index < _pendingResends.Count
               && SequenceMath.IsNewer(id, _pendingResends[index]))
        {
            index++;
        }

        if (index < _pendingResends.Count && _pendingResends[index] == id)
            return;

        _pendingResends.Insert(index, id);
    }

    public void TransmitPendingResends()
    {
        if (_pendingResends.Count > 0)
        {
            for (int i = 0; i < _pendingResends.Count; i++)
            {
                if (AckWatermark != 0
                    && SequenceMath.IsNewer(
                        AckWatermark,
                        _pendingResends[i]))
                {
                    continue;
                }

                if (!_store.TryGet(
                        _pendingResends[i],
                        out SentPacketStore.CachedPacket cached))
                {
                    continue;
                }

                Resend(in cached);
            }

            _pendingResends.Clear();
        }

        _store.FlushOlderThan(AckWatermark);
    }

    private void Resend(in SentPacketStore.CachedPacket cached)
    {
        PacketHeader header = BuildResendHeader(in cached, _clock.IntervalId);
        header.Pack(cached.Buffer);
        _send(cached.Buffer.AsSpan(
            0,
            PacketHeader.Size + cached.BodyLength));
        _stats.ResendsSent++;
    }

    internal static PacketHeader BuildResendHeader(
        in SentPacketStore.CachedPacket cached,
        ushort intervalId)
    {
        PacketHeader header = PacketHeader.Unpack(cached.Buffer);
        PacketHeaderFlags flags = PacketHeaderFlags.Retransmission
            | PacketHeaderFlags.EncryptedChecksum;
        if (cached.HasFragments)
            flags |= PacketHeaderFlags.BlobFragments;
        header.Flags = flags;
        header.Time = intervalId;
        header.Checksum =
            header.CalculateHeaderHash32() + cached.SealedChecksum;
        return header;
    }

    public void Dispose() => _store.Dispose();
}
