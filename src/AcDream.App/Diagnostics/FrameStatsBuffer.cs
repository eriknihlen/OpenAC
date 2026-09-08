using System;

namespace AcDream.App.Diagnostics;

public sealed class FrameStatsBuffer
{
    private readonly long[] _samples;
    private readonly long[] _scratch;
    private int _cursor;
    private int _count;

    public FrameStatsBuffer(int capacity)
    {
        if (capacity <= 0) throw new ArgumentOutOfRangeException(nameof(capacity));
        _samples = new long[capacity];
        _scratch = new long[capacity];
    }

    public int Count => _count;

    public void Push(long value)
    {
        _samples[_cursor] = value;
        _cursor = (_cursor + 1) % _samples.Length;
        if (_count < _samples.Length) _count++;
    }

    public void Reset()
    {
        _cursor = 0;
        _count = 0;
    }

    public long Percentile(double q)
    {
        if (_count == 0) return 0;
        Array.Copy(_samples, _scratch, _count);
        Array.Sort(_scratch, 0, _count);
        int rank = (int)Math.Ceiling(q * _count);   // 1-based nearest rank
        if (rank < 1) rank = 1;
        if (rank > _count) rank = _count;
        return _scratch[rank - 1];
    }

    public long Max()
    {
        if (_count == 0) return 0;   // documented empty behavior, matches Percentile
        long max = _samples[0];
        for (int i = 1; i < _count; i++)
            if (_samples[i] > max) max = _samples[i];
        return max;
    }
}
