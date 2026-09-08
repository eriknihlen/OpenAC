using System.Buffers.Binary;
using AcDream.Core.Net.Packets;

namespace AcDream.Core.Net.Transport;

internal sealed class AckNakScheduler
{
    public const double AckGateSeconds = 2.0;

    public const double NakGateSeconds = 0.6;

    public const int MaxNakIdsPerPacket = 114;

    private readonly TransportClock _clock;
    private readonly InboundSequenceTracker _inbound;
    private readonly OutboundFlowQueue _outbound;
    private readonly TransportStats _stats;
    private readonly DatagramSendDelegate _send;
    private readonly ushort _sessionClientId;
    private readonly ushort _sessionIteration;
    private readonly long _ackGateTicks;
    private readonly long _nakGateTicks;
    private readonly List<uint> _nakScratch = new();

    private long _sharedTimestamp;

    public AckNakScheduler(
        TransportClock clock,
        InboundSequenceTracker inbound,
        OutboundFlowQueue outbound,
        ushort sessionClientId,
        ushort sessionIteration,
        TransportStats stats,
        DatagramSendDelegate send)
    {
        ArgumentNullException.ThrowIfNull(clock);
        ArgumentNullException.ThrowIfNull(inbound);
        ArgumentNullException.ThrowIfNull(outbound);
        ArgumentNullException.ThrowIfNull(stats);
        ArgumentNullException.ThrowIfNull(send);

        _clock = clock;
        _inbound = inbound;
        _outbound = outbound;
        _sessionClientId = sessionClientId;
        _sessionIteration = sessionIteration;
        _stats = stats;
        _send = send;
        _ackGateTicks = (long)Math.Round(AckGateSeconds * clock.Frequency);
        _nakGateTicks = (long)Math.Round(NakGateSeconds * clock.Frequency);
        _sharedTimestamp = clock.GetTimestamp();
    }

    public void Sweep(long now)
    {
        if (_inbound.NakCount > 0)
        {
            if (now - _sharedTimestamp <= _nakGateTicks)
                return;

            EmitRequestRetransmit();
            _sharedTimestamp = now;
            return;
        }

        if (now - _sharedTimestamp < _ackGateTicks)
            return;

        EmitCumulativeAck();
        _sharedTimestamp = now;
    }

    private void EmitCumulativeAck()
    {
        Span<byte> datagram = stackalloc byte[PacketHeader.Size + sizeof(uint)];
        BinaryPrimitives.WriteUInt32LittleEndian(
            datagram.Slice(PacketHeader.Size),
            _inbound.HighestIdReceived);

        var header = new PacketHeader
        {
            Sequence = _outbound.HighestIdSent,
            Flags = PacketHeaderFlags.AckSequence,
            Id = _sessionClientId,
            Time = _clock.IntervalId,
            Iteration = _sessionIteration,
        };

        int datagramLength = PacketCodec.FinalizeInPlace(
            header,
            datagram,
            bodyLength: sizeof(uint),
            optionalLength: sizeof(uint),
            outboundIsaac: null);
        _send(datagram.Slice(0, datagramLength));
        _stats.AcksSent++;
    }

    private void EmitRequestRetransmit()
    {
        _inbound.CopyNakkedSequencesAscending(_nakScratch, MaxNakIdsPerPacket);
        int count = _nakScratch.Count;
        int bodyLength = sizeof(uint) + count * sizeof(uint);

        Span<byte> datagram = stackalloc byte[
            PacketHeader.Size + sizeof(uint)
            + MaxNakIdsPerPacket * sizeof(uint)];
        BinaryPrimitives.WriteUInt32LittleEndian(
            datagram.Slice(PacketHeader.Size),
            (uint)count);
        for (int i = 0; i < count; i++)
        {
            BinaryPrimitives.WriteUInt32LittleEndian(
                datagram.Slice(
                    PacketHeader.Size + sizeof(uint) + i * sizeof(uint)),
                _nakScratch[i]);
        }

        var header = new PacketHeader
        {
            Sequence = _outbound.HighestIdSent,
            Flags = PacketHeaderFlags.RequestRetransmit,
            Id = _sessionClientId,
            Time = _clock.IntervalId,
            Iteration = _sessionIteration,
        };

        int datagramLength = PacketCodec.FinalizeInPlace(
            header,
            datagram,
            bodyLength,
            optionalLength: bodyLength,
            outboundIsaac: null);
        _send(datagram.Slice(0, datagramLength));
        _stats.NaksSent++;
        _stats.NakIdsSent += count;
    }
}
