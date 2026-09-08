using System.Diagnostics;

namespace AcDream.Core.Net.Packets;

public sealed class FragmentAssembler
{
    internal const double PartialTtlSeconds = 60.0;

    internal const int CompletedRingSize = 64;

    private static double DefaultNowSeconds() =>
        (double)Stopwatch.GetTimestamp() / Stopwatch.Frequency;

    private readonly Dictionary<uint, PartialMessage> _inFlight = new();
    private readonly Func<double> _nowSeconds;

    private readonly uint[] _completedSequences = new uint[CompletedRingSize];
    private int _completedNext;
    private int _completedCount;

    public FragmentAssembler()
        : this(null)
    {
    }

    internal FragmentAssembler(Func<double>? nowSeconds) =>
        _nowSeconds = nowSeconds ?? DefaultNowSeconds;

    public int PartialCount => _inFlight.Count;

    public byte[]? Ingest(in MessageFragment fragment, out ushort messageQueue)
    {
        var h = fragment.Header;
        messageQueue = 0;

        // Single-fragment message: shortcut to avoid the dictionary.
        if (h.Count == 1 && h.Index == 0)
        {
            messageQueue = h.Queue;
            return fragment.Payload;
        }

        if (!_inFlight.TryGetValue(h.Sequence, out var partial))
        {
            if (WasRecentlyCompleted(h.Sequence))
                return null;

            partial = new PartialMessage(h.Count, h.Queue, _nowSeconds());
            _inFlight[h.Sequence] = partial;
        }

        // Idempotent: receiving the same index twice is not an error.
        if (partial.Fragments[h.Index] is null)
        {
            partial.Fragments[h.Index] = fragment.Payload;
            partial.ReceivedCount++;
            partial.LastFragmentSeconds = _nowSeconds();
        }

        if (partial.ReceivedCount < partial.TotalFragments)
            return null;

        int totalBytes = 0;
        for (int i = 0; i < partial.TotalFragments; i++)
            totalBytes += partial.Fragments[i]!.Length;

        var combined = new byte[totalBytes];
        int offset = 0;
        for (int i = 0; i < partial.TotalFragments; i++)
        {
            var p = partial.Fragments[i]!;
            Buffer.BlockCopy(p, 0, combined, offset, p.Length);
            offset += p.Length;
        }

        _inFlight.Remove(h.Sequence);
        RememberCompleted(h.Sequence);
        messageQueue = partial.Queue;
        return combined;
    }

    internal bool TryIngest(
        in BorrowedMessageFragment fragment,
        out ReadOnlyMemory<byte> message,
        out ushort messageQueue)
    {
        MessageFragmentHeader header = fragment.Header;
        message = ReadOnlyMemory<byte>.Empty;
        messageQueue = 0;

        if (header.Count == 1 && header.Index == 0)
        {
            message = fragment.Payload;
            messageQueue = header.Queue;
            return true;
        }

        if (!_inFlight.TryGetValue(
                header.Sequence,
                out PartialMessage? partial))
        {
            if (WasRecentlyCompleted(header.Sequence))
                return false;

            partial = new PartialMessage(
                header.Count,
                header.Queue,
                _nowSeconds());
            _inFlight[header.Sequence] = partial;
        }
        else if (partial.TotalFragments != header.Count
                 || partial.Queue != header.Queue)
        {
            return false;
        }

        if (partial.Fragments[header.Index] is null)
        {
            partial.Fragments[header.Index] =
                fragment.Payload.ToArray();
            partial.ReceivedCount++;
            partial.LastFragmentSeconds = _nowSeconds();
        }

        if (partial.ReceivedCount < partial.TotalFragments)
            return false;

        int totalBytes = 0;
        for (int index = 0;
             index < partial.TotalFragments;
             index++)
        {
            totalBytes += partial.Fragments[index]!.Length;
        }

        var combined = new byte[totalBytes];
        int offset = 0;
        for (int index = 0;
             index < partial.TotalFragments;
             index++)
        {
            byte[] payload = partial.Fragments[index]!;
            payload.CopyTo(combined, offset);
            offset += payload.Length;
        }

        _inFlight.Remove(header.Sequence);
        RememberCompleted(header.Sequence);
        message = combined;
        messageQueue = partial.Queue;
        return true;
    }

    internal int SweepExpired()
    {
        if (_inFlight.Count == 0)
            return 0;

        double now = _nowSeconds();
        int evicted = 0;
        foreach ((uint sequence, PartialMessage partial) in _inFlight)
        {
            if (now - partial.LastFragmentSeconds > PartialTtlSeconds)
            {
                _inFlight.Remove(sequence);
                evicted++;
            }
        }

        return evicted;
    }

    public void DropAll()
    {
        _inFlight.Clear();
        _completedNext = 0;
        _completedCount = 0;
    }

    private bool WasRecentlyCompleted(uint sequence)
    {
        for (int i = 0; i < _completedCount; i++)
        {
            if (_completedSequences[i] == sequence)
                return true;
        }

        return false;
    }

    private void RememberCompleted(uint sequence)
    {
        _completedSequences[_completedNext] = sequence;
        _completedNext = (_completedNext + 1) % CompletedRingSize;
        if (_completedCount < CompletedRingSize)
            _completedCount++;
    }

    private sealed class PartialMessage
    {
        public readonly byte[]?[] Fragments;
        public readonly int TotalFragments;
        public readonly ushort Queue;
        public int ReceivedCount;

        public double LastFragmentSeconds;

        public PartialMessage(int count, ushort queue, double nowSeconds)
        {
            TotalFragments = count;
            Fragments = new byte[count][];
            Queue = queue;
            LastFragmentSeconds = nowSeconds;
        }
    }
}
