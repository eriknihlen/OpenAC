using System.Buffers;

namespace AcDream.Core.Net.Transport;

internal sealed class SentPacketStore : IDisposable
{
    internal readonly struct CachedPacket(
        uint sequence,
        byte[] buffer,
        int bodyLength,
        uint sealedChecksum,
        uint isaacKey,
        bool hasFragments)
    {
        public uint Sequence { get; } = sequence;

        /// <summary>Rented wire buffer: header [0..20), body [20..20+BodyLength).</summary>
        public byte[] Buffer { get; } = buffer;

        public int BodyLength { get; } = bodyLength;

        public uint SealedChecksum { get; } = sealedChecksum;

        public uint IsaacKey { get; } = isaacKey;

        public bool HasFragments { get; } = hasFragments;
    }

    private readonly ArrayPool<byte> _pool;
    private readonly Queue<CachedPacket> _fifo = new();
    private readonly Dictionary<uint, CachedPacket> _bySequence = new();

    public SentPacketStore(ArrayPool<byte>? pool = null) =>
        _pool = pool ?? ArrayPool<byte>.Shared;

    public int Count => _fifo.Count;

    public void Add(in CachedPacket packet, int optionalLength)
    {
        if (optionalLength != 0)
        {
            throw new InvalidOperationException(
                "reliable packets must not carry optional headers; the sent-packet "
                + "store keeps only the headerless body for retransmission");
        }

        _bySequence.Add(packet.Sequence, packet);
        _fifo.Enqueue(packet);
    }

    public bool Contains(uint sequence) => _bySequence.ContainsKey(sequence);

    public bool TryGet(uint sequence, out CachedPacket packet) =>
        _bySequence.TryGetValue(sequence, out packet);

    public void FlushOlderThan(uint watermark)
    {
        while (_fifo.TryPeek(out CachedPacket head)
               && SequenceMath.IsNewer(watermark, head.Sequence))
        {
            _fifo.Dequeue();
            _bySequence.Remove(head.Sequence);
            _pool.Return(head.Buffer);
        }
    }

    /// <summary>Return every rented buffer to the pool.</summary>
    public void Dispose()
    {
        while (_fifo.TryDequeue(out CachedPacket entry))
            _pool.Return(entry.Buffer);
        _bySequence.Clear();
    }
}
