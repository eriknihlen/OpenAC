using System.Diagnostics;

namespace AcDream.App.Streaming;

public interface ILandblockCompletionSource
{
    int BacklogCount { get; }
    bool TryPeek(out LandblockStreamResult? result);
    bool TryRead(out LandblockStreamResult? result);
}

internal enum StreamingCompletionPriority : byte
{
    Destination = 0,
    Control = 1,
    Unload = 2,
    Near = 3,
    Far = 4,
}

internal readonly record struct StreamingQueuedCompletion(
    LandblockStreamResult Result,
    LandblockStreamCostEstimate Estimate,
    StreamingCompletionPriority Priority,
    long RevealGeneration,
    ulong Generation,
    long Sequence,
    long EnqueuedTimestamp);

internal readonly record struct StreamingCompletionQueueSnapshot(
    int Count,
    long RetainedCpuBytes,
    double OldestAgeMilliseconds,
    int Destination,
    int Control,
    int Unload,
    int Near,
    int Far);

internal sealed class StreamingCompletionQueue
{
    private readonly Queue<StreamingQueuedCompletion>[] _queues =
        Enumerable.Range(0, Enum.GetValues<StreamingCompletionPriority>().Length)
            .Select(static _ => new Queue<StreamingQueuedCompletion>())
            .ToArray();
    private long _retainedCpuBytes;
    private int _count;

    public int Count => _count;
    public long RetainedCpuBytes => _retainedCpuBytes;

    public bool HasPriority(StreamingCompletionPriority priority) =>
        _queues[(int)priority].Count != 0;

    public void Enqueue(StreamingQueuedCompletion completion)
    {
        _queues[(int)completion.Priority].Enqueue(completion);
        _count++;
        _retainedCpuBytes = SaturatingAdd(
            _retainedCpuBytes,
            completion.Estimate.Work.AdoptedCpuBytes);
    }

    public bool TryPeekNext(
        Func<LandblockStreamResult, bool> isBlocked,
        out StreamingQueuedCompletion? completion,
        StreamingCompletionPriority maximumPriority =
            StreamingCompletionPriority.Far)
    {
        ArgumentNullException.ThrowIfNull(isBlocked);
        int lastPriority = Math.Min(
            (int)maximumPriority,
            _queues.Length - 1);
        for (int priority = 0; priority <= lastPriority; priority++)
        {
            Queue<StreamingQueuedCompletion> queue = _queues[priority];
            if (queue.Count == 0)
                continue;
            StreamingQueuedCompletion head = queue.Peek();
            if (isBlocked(head.Result))
                continue;

            completion = head;
            return true;
        }

        completion = null;
        return false;
    }

    public void RemoveHead(StreamingQueuedCompletion completion)
    {
        Queue<StreamingQueuedCompletion> queue =
            _queues[(int)completion.Priority];
        if (queue.Count == 0)
        {
            throw new InvalidOperationException(
                "Streaming completion priority FIFO is empty.");
        }

        StreamingQueuedCompletion head = queue.Peek();
        if (head.Sequence != completion.Sequence
            || !ReferenceEquals(head.Result, completion.Result))
        {
            throw new InvalidOperationException(
                "Streaming completion is not the head of its priority FIFO.");
        }

        queue.Dequeue();
        Release(completion);
    }

    public int RemoveResults(
        IReadOnlyList<LandblockStreamResult> results,
        Func<LandblockStreamResult, bool> shouldRemove)
    {
        ArgumentNullException.ThrowIfNull(results);
        ArgumentNullException.ThrowIfNull(shouldRemove);
        int removed = 0;
        for (int priority = 0; priority < _queues.Length; priority++)
        {
            Queue<StreamingQueuedCompletion> queue = _queues[priority];
            int count = queue.Count;
            for (int entry = 0; entry < count; entry++)
            {
                StreamingQueuedCompletion current = queue.Dequeue();
                bool matches = false;
                for (int i = 0; i < results.Count; i++)
                {
                    if (ReferenceEquals(current.Result, results[i]))
                    {
                        matches = true;
                        break;
                    }
                }

                if (matches && shouldRemove(current.Result))
                {
                    Release(current);
                    removed++;
                }
                else
                {
                    queue.Enqueue(current);
                }
            }
        }
        return removed;
    }

    public void Clear()
    {
        for (int i = 0; i < _queues.Length; i++)
            _queues[i].Clear();
        _count = 0;
        _retainedCpuBytes = 0;
    }

    public bool TryRemoveOne()
    {
        for (int priority = 0; priority < _queues.Length; priority++)
        {
            Queue<StreamingQueuedCompletion> queue = _queues[priority];
            if (!queue.TryDequeue(out StreamingQueuedCompletion completion))
                continue;

            Release(completion);
            return true;
        }

        return false;
    }

    public StreamingCompletionQueueSnapshot CaptureSnapshot()
    {
        long now = Stopwatch.GetTimestamp();
        long oldest = now;
        bool found = false;
        for (int i = 0; i < _queues.Length; i++)
        {
            foreach (StreamingQueuedCompletion completion in _queues[i])
            {
                oldest = Math.Min(oldest, completion.EnqueuedTimestamp);
                found = true;
            }
        }

        return new StreamingCompletionQueueSnapshot(
            _count,
            _retainedCpuBytes,
            found
                ? Math.Max(0L, now - oldest) * 1000.0 / Stopwatch.Frequency
                : 0,
            _queues[(int)StreamingCompletionPriority.Destination].Count,
            _queues[(int)StreamingCompletionPriority.Control].Count,
            _queues[(int)StreamingCompletionPriority.Unload].Count,
            _queues[(int)StreamingCompletionPriority.Near].Count,
            _queues[(int)StreamingCompletionPriority.Far].Count);
    }

    private void Release(StreamingQueuedCompletion completion)
    {
        _count--;
        _retainedCpuBytes = Math.Max(
            0,
            _retainedCpuBytes - completion.Estimate.Work.AdoptedCpuBytes);
    }

    private static long SaturatingAdd(long left, long right) =>
        left > long.MaxValue - right ? long.MaxValue : left + right;
}

internal sealed class DelegateLandblockCompletionSource(
    Func<int, IReadOnlyList<LandblockStreamResult>> drain)
    : ILandblockCompletionSource
{
    private readonly Func<int, IReadOnlyList<LandblockStreamResult>> _drain =
        drain ?? throw new ArgumentNullException(nameof(drain));
    private LandblockStreamResult? _peeked;

    public int BacklogCount => _peeked is null ? 0 : 1;

    public bool TryPeek(out LandblockStreamResult? result)
    {
        if (_peeked is null)
        {
            IReadOnlyList<LandblockStreamResult> batch = _drain(1);
            if (batch.Count > 1)
            {
                throw new InvalidOperationException(
                    "A single-result completion drain returned more than one result.");
            }
            if (batch.Count == 0)
            {
                result = null;
                return false;
            }
            _peeked = batch[0];
        }

        result = _peeked;
        return true;
    }

    public bool TryRead(out LandblockStreamResult? result)
    {
        if (_peeked is not null)
        {
            result = _peeked;
            _peeked = null;
            return true;
        }

        IReadOnlyList<LandblockStreamResult> batch = _drain(1);
        if (batch.Count > 1)
        {
            throw new InvalidOperationException(
                "A single-result completion drain returned more than one result.");
        }
        if (batch.Count == 0)
        {
            result = null;
            return false;
        }

        result = batch[0];
        return true;
    }
}
