namespace AcDream.App.Rendering;

internal interface IGpuResourceRetirementQueue
{
    void Retire(Action release);
}

internal sealed class RetryableGpuResourceRelease
{
    private readonly Action[] _stages;
    private int _nextStage;
    private bool _running;

    public RetryableGpuResourceRelease(params Action[] stages)
    {
        ArgumentNullException.ThrowIfNull(stages);
        if (stages.Length == 0)
            throw new ArgumentException("At least one release stage is required.", nameof(stages));
        if (Array.Exists(stages, static stage => stage is null))
            throw new ArgumentException("Release stages cannot contain null.", nameof(stages));
        _stages = stages;
    }

    public int CompletedStageCount => _nextStage;
    public bool IsComplete => _nextStage == _stages.Length;

    public void Run()
    {
        if (_running)
            return;

        _running = true;
        try
        {
            while (_nextStage < _stages.Length)
            {
                _stages[_nextStage]();
                _nextStage++;
            }
        }
        finally
        {
            _running = false;
        }
    }
}

internal sealed class GpuResourceMutationException : InvalidOperationException
{
    public GpuResourceMutationException(
        string message,
        bool mutationCommitted,
        Exception innerException)
        : base(message, innerException) => MutationCommitted = mutationCommitted;

    public bool MutationCommitted { get; }
}

internal sealed class GpuRetirementLedger
{
    private readonly IGpuResourceRetirementQueue _queue;
    private readonly List<RetryableGpuResourceRelease> _awaitingPublication = [];
    private readonly HashSet<RetryableGpuResourceRelease> _publishing =
        new(ReferenceEqualityComparer.Instance);

    public GpuRetirementLedger(IGpuResourceRetirementQueue queue) =>
        _queue = queue ?? throw new ArgumentNullException(nameof(queue));

    public int AwaitingPublicationCount => _awaitingPublication.Count;

    public void Retire(RetryableGpuResourceRelease release)
    {
        ArgumentNullException.ThrowIfNull(release);
        _awaitingPublication.Add(release);
        PublishAt(_awaitingPublication.Count - 1);
    }

    public void RetireMany(IEnumerable<RetryableGpuResourceRelease> releases)
    {
        ArgumentNullException.ThrowIfNull(releases);
        RetryableGpuResourceRelease[] batch = releases.ToArray();
        if (batch.Length == 0)
            return;
        if (Array.Exists(batch, static release => release is null))
            throw new ArgumentException("Retirement batches cannot contain null.", nameof(releases));

        _awaitingPublication.AddRange(batch);
        List<Exception>? failures = null;
        for (int i = 0; i < batch.Length; i++)
        {
            try { Publish(batch[i]); }
            catch (Exception error) { (failures ??= []).Add(error); }
        }
        if (failures is not null)
            throw new AggregateException(
                "One or more GPU retirement callbacks could not be published.",
                failures);
    }

    public void RetryPendingPublications()
    {
        RetryableGpuResourceRelease[] pending = _awaitingPublication.ToArray();
        List<Exception>? failures = null;
        for (int i = 0; i < pending.Length; i++)
        {
            RetryableGpuResourceRelease release = pending[i];
            if (!_awaitingPublication.Contains(release, ReferenceEqualityComparer.Instance)
                || _publishing.Contains(release))
            {
                continue;
            }

            try
            {
                Publish(release);
            }
            catch (Exception error)
            {
                (failures ??= []).Add(error);
            }
        }

        if (failures is not null)
            throw new AggregateException(
                "One or more GPU retirement callbacks could not be published.",
                failures);
    }

    public void RetryPendingPublication(RetryableGpuResourceRelease release)
    {
        ArgumentNullException.ThrowIfNull(release);
        if (!_awaitingPublication.Contains(release, ReferenceEqualityComparer.Instance)
            || _publishing.Contains(release))
        {
            return;
        }

        Publish(release);
    }

    private void PublishAt(int index)
    {
        RetryableGpuResourceRelease release = _awaitingPublication[index];
        Publish(release);
    }

    private void Publish(RetryableGpuResourceRelease release)
    {
        if (!_publishing.Add(release))
            return;
        try
        {
            _queue.Retire(release.Run);
            _awaitingPublication.Remove(release);
        }
        catch
        {
            if (release.IsComplete)
                _awaitingPublication.Remove(release);
            throw;
        }
        finally
        {
            _publishing.Remove(release);
        }
    }
}

internal sealed class ImmediateGpuResourceRetirementQueue : IGpuResourceRetirementQueue
{
    public static ImmediateGpuResourceRetirementQueue Instance { get; } = new();

    private ImmediateGpuResourceRetirementQueue()
    {
    }

    public void Retire(Action release)
    {
        ArgumentNullException.ThrowIfNull(release);
        release();
    }
}

internal sealed class GpuFrameFlightController :
    IGpuResourceRetirementQueue,
    IRenderFrameLifetime,
    IRenderFrameSlotSource,
    IDisposable
{
    internal const int DefaultMaximumFramesInFlight = 3;
    private const ulong WaitSliceNanoseconds = 1_000_000;

    private readonly IGpuFenceApi _fenceApi;
    private readonly nint[] _fences;
    private readonly long[] _fenceSerials;
    private readonly SortedDictionary<long, List<Action>> _retirements = new();
    private int _slot;
    private long _lastSubmittedSerial;
    private bool _frameOpen;
    private bool _disposed;

    public int CurrentSlot => _slot;
    public int SlotCount => _fences.Length;
    internal int PendingRetirementCount => _retirements.Sum(entry => entry.Value.Count);

    internal GpuFrameFlightController(
        IGpuFenceApi fenceApi,
        int maximumFramesInFlight = DefaultMaximumFramesInFlight)
    {
        ArgumentNullException.ThrowIfNull(fenceApi);
        ArgumentOutOfRangeException.ThrowIfLessThan(maximumFramesInFlight, 1);

        _fenceApi = fenceApi;
        _fences = new nint[maximumFramesInFlight];
        _fenceSerials = new long[maximumFramesInFlight];
    }

    public void BeginFrame()
    {
        ObjectDisposedException.ThrowIf(_disposed, this);
        if (_frameOpen)
            throw new InvalidOperationException("EndFrame must close the current frame before BeginFrame is called again.");

        nint fence = _fences[_slot];
        if (fence != 0)
            RetireFence(_slot);

        _frameOpen = true;
    }

    private void RetireFence(int slot)
    {
        nint fence = _fences[slot];
        if (fence == 0)
            return;

        bool flushCommands = true;
        while (true)
        {
            GpuFenceWaitResult result = _fenceApi.Wait(
                fence,
                flushCommands,
                WaitSliceNanoseconds);
            flushCommands = false;

            if (result == GpuFenceWaitResult.Timeout)
                continue;
            if (result == GpuFenceWaitResult.Failed)
                throw new InvalidOperationException("OpenGL failed while waiting for an in-flight frame fence.");

            break;
        }

        _fenceApi.Delete(fence);
        _fences[slot] = 0;
        long completedSerial = _fenceSerials[slot];
        _fenceSerials[slot] = 0;
        RunRetirementsThrough(completedSerial);
    }

    public void EndFrame()
    {
        ObjectDisposedException.ThrowIf(_disposed, this);
        if (!_frameOpen)
            throw new InvalidOperationException("BeginFrame must be called before EndFrame.");
        if (_fences[_slot] != 0)
            throw new InvalidOperationException("BeginFrame must retire the current frame slot before EndFrame.");

        nint fence = _fenceApi.Insert();
        if (fence == 0)
            throw new InvalidOperationException("OpenGL did not create an in-flight frame fence.");

        _fences[_slot] = fence;
        _fenceSerials[_slot] = ++_lastSubmittedSerial;
        _frameOpen = false;
        _slot = (_slot + 1) % _fences.Length;
    }

    public void Retire(Action release)
    {
        ObjectDisposedException.ThrowIf(_disposed, this);
        ArgumentNullException.ThrowIfNull(release);

        long targetSerial = _frameOpen
            ? _lastSubmittedSerial + 1
            : _lastSubmittedSerial;
        if (targetSerial == 0)
        {
            release();
            return;
        }

        if (!_retirements.TryGetValue(targetSerial, out List<Action>? releases))
        {
            releases = [];
            _retirements.Add(targetSerial, releases);
        }

        releases.Add(release);
    }

    public void WaitForSubmittedWork()
    {
        ObjectDisposedException.ThrowIf(_disposed, this);
        if (_frameOpen)
            throw new InvalidOperationException("Cannot drain submitted work while a render frame is open.");

        List<Exception>? failures = null;
        for (int i = 0; i < _fences.Length; i++)
        {
            int slot = (_slot + i) % _fences.Length;
            try
            {
                RetireFence(slot);
            }
            catch (AggregateException ex)
            {
                (failures ??= []).AddRange(ex.InnerExceptions);
            }
        }

        try
        {
            RunRetirementsThrough(_lastSubmittedSerial);
        }
        catch (AggregateException ex)
        {
            (failures ??= []).AddRange(ex.InnerExceptions);
        }

        if (failures is not null)
            throw new AggregateException("One or more GPU resource retirements failed.", failures);
    }

    private void RunRetirementsThrough(long completedSerial)
    {
        List<Exception>? failures = null;
        List<(long Serial, Action Release)>? retry = null;
        while (_retirements.Count != 0)
        {
            KeyValuePair<long, List<Action>> first;
            using (IEnumerator<KeyValuePair<long, List<Action>>> enumerator = _retirements.GetEnumerator())
            {
                if (!enumerator.MoveNext() || enumerator.Current.Key > completedSerial)
                    break;
                first = enumerator.Current;
            }

            if (!_retirements.Remove(first.Key))
                break;

            List<Action> releases = first.Value;
            for (int i = 0; i < releases.Count; i++)
            {
                try
                {
                    releases[i]();
                }
                catch (Exception ex)
                {
                    (failures ??= []).Add(ex);
                    (retry ??= []).Add((first.Key, releases[i]));
                }
            }
        }

        if (retry is not null)
        {
            for (int i = 0; i < retry.Count; i++)
            {
                (long serial, Action release) = retry[i];
                if (!_retirements.TryGetValue(serial, out List<Action>? releases))
                {
                    releases = [];
                    _retirements.Add(serial, releases);
                }
                releases.Add(release);
            }
        }

        if (failures is not null)
            throw new AggregateException("One or more GPU resource retirements failed.", failures);
    }

    public void Dispose()
    {
        if (_disposed)
            return;

        if (_frameOpen)
            EndFrame();
        WaitForSubmittedWork();

        Array.Clear(_fences);
        Array.Clear(_fenceSerials);
        _disposed = true;
    }
}

internal enum GpuFenceWaitResult
{
    Signaled,
    Timeout,
    Failed,
}

internal interface IGpuFenceApi
{
    nint Insert();
    GpuFenceWaitResult Wait(nint fence, bool flushCommands, ulong timeoutNanoseconds);
    void Delete(nint fence);
}

