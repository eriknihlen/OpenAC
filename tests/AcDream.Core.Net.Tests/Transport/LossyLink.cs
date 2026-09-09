namespace AcDream.Core.Net.Tests.Transport;

internal enum LinkDirection
{
    ClientToServer,
    ServerToClient,
}

internal sealed class LossyLink
{
    private sealed class DirectionState
    {
        public int TransmitIndex;
        public int PendingDropCount;
        public readonly HashSet<int> DropIndices = new();
        public readonly List<Func<int, byte[], bool>> DropPredicates = new();
        public Random? LossRandom;
        public double LossProbability;
        public bool ReorderArmed;
        public byte[]? Held;
        public int Dropped;
        public int Delivered;
    }

    private readonly DirectionState _clientToServer = new();
    private readonly DirectionState _serverToClient = new();

    private DirectionState State(LinkDirection direction) =>
        direction == LinkDirection.ClientToServer ? _clientToServer : _serverToClient;

    public void DropNext(LinkDirection direction, int count = 1)
    {
        ArgumentOutOfRangeException.ThrowIfNegative(count);
        State(direction).PendingDropCount += count;
    }

    /// <summary>Drop the datagram with the given per-direction transmit index (0-based).</summary>
    public void DropAt(LinkDirection direction, int transmitIndex) =>
        State(direction).DropIndices.Add(transmitIndex);

    /// <summary>
    /// Drop every datagram matching <paramref name="predicate"/> (persistent;
    /// receives the per-direction transmit index and the datagram bytes).
    /// </summary>
    public void Drop(LinkDirection direction, Func<int, byte[], bool> predicate) =>
        State(direction).DropPredicates.Add(predicate);

    /// <summary>
    /// Swap the next two surviving datagrams: the next survivor is held and
    /// released right after the survivor that follows it.
    /// </summary>
    public void Reorder(LinkDirection direction) =>
        State(direction).ReorderArmed = true;

    /// <summary>
    /// Enable seeded random loss: each surviving datagram is dropped with
    /// <paramref name="probability"/> using <c>Random(seed)</c> — fully
    /// deterministic for a given seed + transmit sequence.
    /// </summary>
    public void RandomLoss(LinkDirection direction, double probability, int seed)
    {
        ArgumentOutOfRangeException.ThrowIfNegative(probability);
        ArgumentOutOfRangeException.ThrowIfGreaterThan(probability, 1.0);
        DirectionState state = State(direction);
        state.LossProbability = probability;
        state.LossRandom = new Random(seed);
    }

    public int TransmitCount(LinkDirection direction) => State(direction).TransmitIndex;
    public int DroppedCount(LinkDirection direction) => State(direction).Dropped;
    public int DeliveredCount(LinkDirection direction) => State(direction).Delivered;

    /// <summary>
    /// Push one datagram through the link. Returns the datagrams to deliver
    /// now, in order (0, 1, or 2 entries — 2 when a held reordered datagram
    /// is released).
    /// </summary>
    public IReadOnlyList<byte[]> Transmit(LinkDirection direction, ReadOnlySpan<byte> datagram)
    {
        DirectionState state = State(direction);
        int index = state.TransmitIndex++;
        byte[] copy = datagram.ToArray();

        bool drop = state.DropIndices.Remove(index);
        if (!drop && state.PendingDropCount > 0)
        {
            state.PendingDropCount--;
            drop = true;
        }

        if (!drop)
        {
            foreach (Func<int, byte[], bool> predicate in state.DropPredicates)
            {
                if (predicate(index, copy))
                {
                    drop = true;
                    break;
                }
            }
        }

        if (!drop
            && state.LossRandom is not null
            && state.LossRandom.NextDouble() < state.LossProbability)
        {
            drop = true;
        }

        if (drop)
        {
            state.Dropped++;
            return Array.Empty<byte[]>();
        }

        if (state.ReorderArmed)
        {
            state.ReorderArmed = false;
            state.Held = copy;
            return Array.Empty<byte[]>();
        }

        if (state.Held is not null)
        {
            byte[] held = state.Held;
            state.Held = null;
            state.Delivered += 2;
            return new[] { copy, held };
        }

        state.Delivered++;
        return new[] { copy };
    }

    public IReadOnlyList<byte[]> DrainHeld(LinkDirection direction)
    {
        DirectionState state = State(direction);
        if (state.Held is null)
            return Array.Empty<byte[]>();
        byte[] held = state.Held;
        state.Held = null;
        state.Delivered++;
        return new[] { held };
    }
}
