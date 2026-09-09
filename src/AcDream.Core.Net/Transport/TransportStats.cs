namespace AcDream.Core.Net.Transport;

internal sealed class TransportStats
{
    public long PacketsReceived;

    public long PacketsSent;

    /// <summary>Datagrams re-emitted in response to a server NAK.</summary>
    public long ResendsSent;

    public long NakRequestsReceived;

    public long UncachedNakIds;

    /// <summary>Inbound <c>AckSequence</c> values folded into the watermark
    /// (explicit acks plus the NAK <c>ids[0]</c> implicit ack).</summary>
    public long AcksConsumed;

    public long InboundDupsDropped;

    public long InboundSanityDrops;

    public long ChecksumFailures;

    public long KeysParked;

    public long AcksSent;

    public long NaksSent;

    public long NakIdsSent;

    public long RejectWordsReclaimed;

    public long RejectsReceived;

    public int CacheDepth => CacheDepthSource?.Invoke() ?? 0;

    internal Func<int>? CacheDepthSource { get; set; }
}
