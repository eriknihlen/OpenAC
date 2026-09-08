using System.Diagnostics;

namespace AcDream.Core.Net.Transport;

internal sealed class TransportClock
{
    private readonly Func<long> _timestampSource;
    private readonly long _ticksPerInterval;
    private long _intervalBaseTimestamp;

    /// <summary>Timestamp ticks per second of the injected source.</summary>
    public long Frequency { get; }

    public ushort IntervalId { get; private set; }

    public TransportClock(
        Func<long>? timestampSource = null,
        long? frequency = null)
    {
        _timestampSource = timestampSource ?? Stopwatch.GetTimestamp;
        Frequency = frequency ?? Stopwatch.Frequency;
        if (Frequency < 2)
        {
            throw new ArgumentOutOfRangeException(
                nameof(frequency),
                "the interval clock needs at least 2 ticks per second");
        }

        _ticksPerInterval = Frequency / 2;
        _intervalBaseTimestamp = _timestampSource();
        IntervalId = 1;
    }

    public long GetTimestamp() => _timestampSource();

    public void Update()
    {
        long elapsed = _timestampSource() - _intervalBaseTimestamp;
        if (elapsed < _ticksPerInterval)
            return;

        long steps = elapsed / _ticksPerInterval;
        IntervalId = unchecked((ushort)(IntervalId + steps));
        _intervalBaseTimestamp += steps * _ticksPerInterval;
    }
}
