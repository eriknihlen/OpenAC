namespace AcDream.Core.Net.Transport;

internal sealed class RetailPacketLossAverager
{
    public const int WindowSize = 40;
    public const double SnapshotSeconds = 2.0;

    private readonly Sample[] _samples = new Sample[WindowSize];
    private readonly long _snapshotTicks;
    private long _snapshotTimestamp;
    private long _lastPacketsSent;
    private long _lastRetransmitsSent;
    private long _lastPacketsReceived;
    private long _lastNakIdsSent;
    private long _sentTotal;
    private long _retransmitTotal;
    private long _receivedTotal;
    private long _nakTotal;
    private int _next;
    private int _count;

    public RetailPacketLossAverager(TransportClock clock, TransportStats stats)
    {
        ArgumentNullException.ThrowIfNull(clock);
        ArgumentNullException.ThrowIfNull(stats);

        _snapshotTicks = (long)Math.Round(SnapshotSeconds * clock.Frequency);
        _snapshotTimestamp = clock.GetTimestamp();
        CaptureBaselines(stats);
    }

    public double Percentage
    {
        get
        {
            long denominator = _receivedTotal + _sentTotal;
            return denominator <= 0
                ? 0d
                : 100d * (_nakTotal + _retransmitTotal) / denominator;
        }
    }

    public void Sweep(long now, TransportStats stats)
    {
        ArgumentNullException.ThrowIfNull(stats);
        if (now - _snapshotTimestamp < _snapshotTicks)
            return;

        // ProcessConnection records one snapshot when the heartbeat branch
        // runs; it does not synthesize empty samples for skipped frames.
        _snapshotTimestamp = now;

        var sample = new Sample(
            DeltaAsRetailUShort(stats.PacketsSent, ref _lastPacketsSent),
            DeltaAsRetailUShort(stats.ResendsSent, ref _lastRetransmitsSent),
            DeltaAsRetailUShort(stats.PacketsReceived, ref _lastPacketsReceived),
            DeltaAsRetailUShort(stats.NakIdsSent, ref _lastNakIdsSent));

        if (_count == WindowSize)
            Remove(_samples[_next]);
        else
            _count++;

        _samples[_next] = sample;
        _next = (_next + 1) % WindowSize;
        Add(sample);
    }

    private void CaptureBaselines(TransportStats stats)
    {
        _lastPacketsSent = stats.PacketsSent;
        _lastRetransmitsSent = stats.ResendsSent;
        _lastPacketsReceived = stats.PacketsReceived;
        _lastNakIdsSent = stats.NakIdsSent;
    }

    private static ushort DeltaAsRetailUShort(long current, ref long previous)
    {
        long delta = current - previous;
        previous = current;
        return unchecked((ushort)Math.Max(0L, delta));
    }

    private void Add(Sample sample)
    {
        _sentTotal += sample.Sent;
        _retransmitTotal += sample.Retransmitted;
        _receivedTotal += sample.Received;
        _nakTotal += sample.Naked;
    }

    private void Remove(Sample sample)
    {
        _sentTotal -= sample.Sent;
        _retransmitTotal -= sample.Retransmitted;
        _receivedTotal -= sample.Received;
        _nakTotal -= sample.Naked;
    }

    private readonly record struct Sample(
        ushort Sent,
        ushort Retransmitted,
        ushort Received,
        ushort Naked);
}
