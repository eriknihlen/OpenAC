using System.Diagnostics;

namespace AcDream.App.Streaming;

/// <summary>
/// Immutable, validated per-frame streaming work profile.
/// </summary>
public readonly record struct StreamingWorkBudget
{
    public StreamingWorkBudget(
        TimeSpan maxUpdateTime,
        int maxCompletionAdmissions,
        long maxAdoptedCpuBytes,
        int maxEntityOperations,
        long maxGpuUploadBytes,
        int maxGlRetireOperations,
        float destinationReserveFraction)
    {
        if (maxUpdateTime <= TimeSpan.Zero)
            throw new ArgumentOutOfRangeException(nameof(maxUpdateTime));
        if (maxCompletionAdmissions <= 0)
            throw new ArgumentOutOfRangeException(nameof(maxCompletionAdmissions));
        if (maxAdoptedCpuBytes <= 0)
            throw new ArgumentOutOfRangeException(nameof(maxAdoptedCpuBytes));
        if (maxEntityOperations <= 0)
            throw new ArgumentOutOfRangeException(nameof(maxEntityOperations));
        if (maxGpuUploadBytes <= 0)
            throw new ArgumentOutOfRangeException(nameof(maxGpuUploadBytes));
        if (maxGlRetireOperations <= 0)
            throw new ArgumentOutOfRangeException(nameof(maxGlRetireOperations));
        if (!float.IsFinite(destinationReserveFraction)
            || destinationReserveFraction <= 0f
            || destinationReserveFraction >= 1f)
        {
            throw new ArgumentOutOfRangeException(
                nameof(destinationReserveFraction));
        }

        MaxUpdateTime = maxUpdateTime;
        MaxCompletionAdmissions = maxCompletionAdmissions;
        MaxAdoptedCpuBytes = maxAdoptedCpuBytes;
        MaxEntityOperations = maxEntityOperations;
        MaxGpuUploadBytes = maxGpuUploadBytes;
        MaxGlRetireOperations = maxGlRetireOperations;
        DestinationReserveFraction = destinationReserveFraction;
    }

    public TimeSpan MaxUpdateTime { get; }
    public int MaxCompletionAdmissions { get; }
    public long MaxAdoptedCpuBytes { get; }
    public int MaxEntityOperations { get; }
    public long MaxGpuUploadBytes { get; }
    public int MaxGlRetireOperations { get; }
    public float DestinationReserveFraction { get; }

    internal void Validate()
    {
        if (MaxUpdateTime <= TimeSpan.Zero
            || MaxCompletionAdmissions <= 0
            || MaxAdoptedCpuBytes <= 0
            || MaxEntityOperations <= 0
            || MaxGpuUploadBytes <= 0
            || MaxGlRetireOperations <= 0
            || !float.IsFinite(DestinationReserveFraction)
            || DestinationReserveFraction <= 0f
            || DestinationReserveFraction >= 1f)
        {
            throw new ArgumentException(
                "Streaming work budget is not a valid complete profile.",
                nameof(StreamingWorkBudget));
        }
    }

    public StreamingWorkBudget WidenForDestinationHold(
        double ceilingMilliseconds)
    {
        if (!double.IsFinite(ceilingMilliseconds) || ceilingMilliseconds <= 0)
            return this;

        double scale =
            ceilingMilliseconds / MaxUpdateTime.TotalMilliseconds;
        if (scale <= 1.0)
            return this;

        float widenedReserve = (float)(
            1.0 - (1.0 - DestinationReserveFraction) / scale);
        if (widenedReserve >= 1f)
            widenedReserve = MathF.BitDecrement(1f);

        return new StreamingWorkBudget(
            TimeSpan.FromMilliseconds(ceilingMilliseconds),
            StreamingWorkBudgetOptions.Scale(MaxCompletionAdmissions, scale),
            StreamingWorkBudgetOptions.Scale(MaxAdoptedCpuBytes, scale),
            StreamingWorkBudgetOptions.Scale(MaxEntityOperations, scale),
            StreamingWorkBudgetOptions.Scale(MaxGpuUploadBytes, scale),
            StreamingWorkBudgetOptions.Scale(MaxGlRetireOperations, scale),
            widenedReserve);
    }
}

public readonly record struct StreamingWorkCost(
    int CompletionAdmissions = 0,
    long AdoptedCpuBytes = 0,
    int EntityOperations = 0,
    long GpuUploadBytes = 0,
    int GlRetireOperations = 0)
{
    public bool IsZero =>
        CompletionAdmissions == 0
        && AdoptedCpuBytes == 0
        && EntityOperations == 0
        && GpuUploadBytes == 0
        && GlRetireOperations == 0;

    internal void Validate()
    {
        if (CompletionAdmissions < 0)
            throw new ArgumentOutOfRangeException(nameof(CompletionAdmissions));
        if (AdoptedCpuBytes < 0)
            throw new ArgumentOutOfRangeException(nameof(AdoptedCpuBytes));
        if (EntityOperations < 0)
            throw new ArgumentOutOfRangeException(nameof(EntityOperations));
        if (GpuUploadBytes < 0)
            throw new ArgumentOutOfRangeException(nameof(GpuUploadBytes));
        if (GlRetireOperations < 0)
            throw new ArgumentOutOfRangeException(nameof(GlRetireOperations));
    }

    internal StreamingWorkCost Add(StreamingWorkCost other) => new(
        SaturatingAdd(CompletionAdmissions, other.CompletionAdmissions),
        SaturatingAdd(AdoptedCpuBytes, other.AdoptedCpuBytes),
        SaturatingAdd(EntityOperations, other.EntityOperations),
        SaturatingAdd(GpuUploadBytes, other.GpuUploadBytes),
        SaturatingAdd(GlRetireOperations, other.GlRetireOperations));

    private static int SaturatingAdd(int left, int right) =>
        left > int.MaxValue - right ? int.MaxValue : left + right;

    private static long SaturatingAdd(long left, long right) =>
        left > long.MaxValue - right ? long.MaxValue : left + right;
}

public enum StreamingWorkAdmission : byte
{
    Admitted,
    OversizedProgress,
    Yielded,
}

public enum StreamingWorkLimit : byte
{
    None,
    Time,
    CompletionAdmissions,
    AdoptedCpuBytes,
    EntityOperations,
    GpuUploadBytes,
    GlRetireOperations,
}

internal enum StreamingWorkLane : byte
{
    NonDestination,
    Destination,
}

/// <summary>
/// Immutable observation of one frame's streaming work.
/// </summary>
public readonly record struct StreamingWorkMeterSnapshot(
    StreamingWorkCost Used,
    StreamingWorkCost DestinationUsed,
    StreamingWorkCost NonDestinationUsed,
    int Operations,
    int CompletedOperations,
    int YieldCount,
    int OverrunCount,
    int OversizedProgressCount,
    int FailureCount,
    double ElapsedMilliseconds,
    double MaximumOperationMilliseconds,
    string? MaximumOperationStage,
    string? LastStage,
    StreamingWorkLimit LastLimit);

/// <summary>
/// Read-only streaming scheduler facts published to lifecycle artifacts.
/// </summary>
public readonly record struct StreamingWorkDiagnostics(
    StreamingWorkMeterSnapshot LastFrame,
    long LifetimeFrameOverrunCount,
    long LifetimeOversizedProgressCount,
    double MaximumFrameMilliseconds,
    string? MaximumFrameStage,
    double MaximumOperationMilliseconds,
    string? MaximumOperationStage,
    int DeferredCompletions,
    long DeferredAdoptedCpuBytes,
    double OldestDeferredAgeMilliseconds,
    int PendingPublications,
    int PendingRetirements,
    int WorkerCompletionBacklog,
    int DestinationBacklog,
    int ControlBacklog,
    int UnloadBacklog,
    int NearBacklog,
    int FarBacklog);

public sealed class StreamingWorkMeter
{
    private readonly StreamingWorkBudget _budget;
    private readonly Func<long> _timestamp;
    private readonly long _frequency;
    private readonly long _start;
    private readonly bool _destinationReservationActive;
    private StreamingWorkCost _used;
    private StreamingWorkCost _destinationUsed;
    private StreamingWorkCost _nonDestinationUsed;
    private StreamingWorkLane _lane;
    private long _destinationElapsedTicks;
    private long _nonDestinationElapsedTicks;
    private long _activeOperationStart;
    private double _maximumOperationMilliseconds;
    private string? _maximumOperationStage;
    private int _operations;
    private int _completed;
    private int _yields;
    private int _overruns;
    private int _oversizedProgress;
    private int _failures;
    private bool _frameOverrunRecorded;
    private bool _reservationActive;
    private bool _ensuredProgressGranted;
    private string? _activeStage;
    private string? _lastStage;
    private StreamingWorkLimit _lastLimit;

    public StreamingWorkMeter(
        StreamingWorkBudget budget,
        bool destinationReservationActive = false)
        : this(
            budget,
            Stopwatch.GetTimestamp,
            Stopwatch.Frequency,
            destinationReservationActive)
    {
    }

    public StreamingWorkMeter(
        StreamingWorkBudget budget,
        Func<long> timestamp,
        long timestampFrequency,
        bool destinationReservationActive = false)
    {
        ArgumentNullException.ThrowIfNull(timestamp);
        if (timestampFrequency <= 0)
            throw new ArgumentOutOfRangeException(nameof(timestampFrequency));
        budget.Validate();

        _budget = budget;
        _timestamp = timestamp;
        _frequency = timestampFrequency;
        _start = timestamp();
        _destinationReservationActive = destinationReservationActive;
    }

    internal LaneScope EnterLane(StreamingWorkLane lane)
    {
        if (_reservationActive)
            throw new InvalidOperationException(
                "Cannot change streaming lane during an active operation.");

        StreamingWorkLane previous = _lane;
        _lane = lane;
        return new LaneScope(this, previous);
    }

    public StreamingWorkAdmission TryReserve(
        StreamingWorkCost cost,
        string stage,
        bool ensureProgress = false)
    {
        cost.Validate();
        ArgumentException.ThrowIfNullOrWhiteSpace(stage);
        if (_reservationActive)
            throw new InvalidOperationException(
                "The prior streaming operation has not completed.");

        StreamingWorkLimit limit = FindLimit(cost);
        bool grantEnsuredProgress =
            ensureProgress && !_ensuredProgressGranted;
        if (limit != StreamingWorkLimit.None
            && _operations != 0
            && !grantEnsuredProgress)
        {
            _yields++;
            _lastStage = stage;
            _lastLimit = limit;
            return StreamingWorkAdmission.Yielded;
        }

        _used = _used.Add(cost);
        if (_lane == StreamingWorkLane.Destination)
            _destinationUsed = _destinationUsed.Add(cost);
        else
            _nonDestinationUsed = _nonDestinationUsed.Add(cost);
        _operations++;
        _reservationActive = true;
        _activeStage = stage;
        _activeOperationStart = _timestamp();
        if (ensureProgress)
            _ensuredProgressGranted = true;
        if (limit != StreamingWorkLimit.None)
        {
            _lastStage = stage;
            _lastLimit = limit;
            _oversizedProgress++;
            return StreamingWorkAdmission.OversizedProgress;
        }

        if (_lastLimit == StreamingWorkLimit.None)
            _lastStage = stage;
        return StreamingWorkAdmission.Admitted;
    }

    public void Complete()
    {
        EnsureReservation();
        CompleteLaneTiming();
        _completed++;
        _reservationActive = false;
        ObserveElapsedOverrun();
        _activeStage = null;
    }

    public void Fail()
    {
        EnsureReservation();
        CompleteLaneTiming();
        _failures++;
        _reservationActive = false;
        ObserveElapsedOverrun();
        _activeStage = null;
    }

    public void FinishFrame()
    {
        if (_reservationActive)
            throw new InvalidOperationException(
                "A streaming operation is still reserved at frame end.");
        ObserveElapsedOverrun();
    }

    public StreamingWorkMeterSnapshot Snapshot
    {
        get
        {
            long now = _timestamp();
            return new StreamingWorkMeterSnapshot(
                _used,
                _destinationUsed,
                _nonDestinationUsed,
                _operations,
                _completed,
                _yields,
                _overruns,
                _oversizedProgress,
                _failures,
                ElapsedMilliseconds(now),
                _maximumOperationMilliseconds,
                _maximumOperationStage,
                _lastStage,
                _lastLimit);
        }
    }

    private StreamingWorkLimit FindLimit(StreamingWorkCost cost)
    {
        if (ElapsedTicks(_timestamp()) >= _budget.MaxUpdateTime.Ticks)
            return StreamingWorkLimit.Time;
        if ((long)_used.CompletionAdmissions + cost.CompletionAdmissions
            > _budget.MaxCompletionAdmissions)
        {
            return StreamingWorkLimit.CompletionAdmissions;
        }
        if (_used.AdoptedCpuBytes > _budget.MaxAdoptedCpuBytes - cost.AdoptedCpuBytes)
            return StreamingWorkLimit.AdoptedCpuBytes;
        if ((long)_used.EntityOperations + cost.EntityOperations
            > _budget.MaxEntityOperations)
        {
            return StreamingWorkLimit.EntityOperations;
        }
        if (_used.GpuUploadBytes > _budget.MaxGpuUploadBytes - cost.GpuUploadBytes)
            return StreamingWorkLimit.GpuUploadBytes;
        if ((long)_used.GlRetireOperations + cost.GlRetireOperations
            > _budget.MaxGlRetireOperations)
        {
            return StreamingWorkLimit.GlRetireOperations;
        }

        if (_destinationReservationActive
            && _lane == StreamingWorkLane.NonDestination)
        {
            double unreservedFraction =
                1.0 - _budget.DestinationReserveFraction;
            long maxTimeTicks = ReservedLimit(
                _budget.MaxUpdateTime.Ticks,
                unreservedFraction);
            if (_nonDestinationElapsedTicks >= maxTimeTicks)
                return StreamingWorkLimit.Time;
            if ((long)_nonDestinationUsed.CompletionAdmissions
                    + cost.CompletionAdmissions
                > ReservedLimit(
                    _budget.MaxCompletionAdmissions,
                    unreservedFraction))
            {
                return StreamingWorkLimit.CompletionAdmissions;
            }
            if (_nonDestinationUsed.AdoptedCpuBytes
                > ReservedLimit(
                    _budget.MaxAdoptedCpuBytes,
                    unreservedFraction) - cost.AdoptedCpuBytes)
            {
                return StreamingWorkLimit.AdoptedCpuBytes;
            }
            if ((long)_nonDestinationUsed.EntityOperations
                    + cost.EntityOperations
                > ReservedLimit(
                    _budget.MaxEntityOperations,
                    unreservedFraction))
            {
                return StreamingWorkLimit.EntityOperations;
            }
            if (_nonDestinationUsed.GpuUploadBytes
                > ReservedLimit(
                    _budget.MaxGpuUploadBytes,
                    unreservedFraction) - cost.GpuUploadBytes)
            {
                return StreamingWorkLimit.GpuUploadBytes;
            }
            if ((long)_nonDestinationUsed.GlRetireOperations
                    + cost.GlRetireOperations
                > ReservedLimit(
                    _budget.MaxGlRetireOperations,
                    unreservedFraction))
            {
                return StreamingWorkLimit.GlRetireOperations;
            }
        }
        return StreamingWorkLimit.None;
    }

    private static long ReservedLimit(long total, double fraction) =>
        Math.Max(1L, (long)Math.Floor(total * fraction));

    private void CompleteLaneTiming()
    {
        long raw = Math.Max(0L, _timestamp() - _activeOperationStart);
        double elapsedMilliseconds = raw * 1000.0 / _frequency;
        if (_maximumOperationStage is null
            || elapsedMilliseconds > _maximumOperationMilliseconds)
        {
            _maximumOperationMilliseconds = elapsedMilliseconds;
            _maximumOperationStage = _activeStage;
        }
        long elapsed = raw > long.MaxValue / TimeSpan.TicksPerSecond
            ? long.MaxValue
            : raw * TimeSpan.TicksPerSecond / _frequency;
        if (_lane == StreamingWorkLane.Destination)
            _destinationElapsedTicks = SaturatingAdd(
                _destinationElapsedTicks,
                elapsed);
        else
            _nonDestinationElapsedTicks = SaturatingAdd(
                _nonDestinationElapsedTicks,
                elapsed);
    }

    private static long SaturatingAdd(long left, long right) =>
        left > long.MaxValue - right ? long.MaxValue : left + right;

    private void RestoreLane(StreamingWorkLane lane)
    {
        if (_reservationActive)
            throw new InvalidOperationException(
                "Cannot restore streaming lane during an active operation.");
        _lane = lane;
    }

    internal readonly struct LaneScope : IDisposable
    {
        private readonly StreamingWorkMeter _meter;
        private readonly StreamingWorkLane _previous;

        internal LaneScope(
            StreamingWorkMeter meter,
            StreamingWorkLane previous)
        {
            _meter = meter;
            _previous = previous;
        }

        public void Dispose() => _meter.RestoreLane(_previous);
    }

    private void EnsureReservation()
    {
        if (!_reservationActive)
            throw new InvalidOperationException(
                "No streaming operation is reserved.");
    }

    private void ObserveElapsedOverrun()
    {
        if (!_frameOverrunRecorded
            && ElapsedTicks(_timestamp()) > _budget.MaxUpdateTime.Ticks)
        {
            _frameOverrunRecorded = true;
            _overruns++;
            _lastLimit = StreamingWorkLimit.Time;
            _lastStage = _activeStage ?? _lastStage;
        }
    }

    private long ElapsedTicks(long timestamp)
    {
        long raw = Math.Max(0L, timestamp - _start);
        return raw > long.MaxValue / TimeSpan.TicksPerSecond
            ? long.MaxValue
            : raw * TimeSpan.TicksPerSecond / _frequency;
    }

    private double ElapsedMilliseconds(long timestamp) =>
        Math.Max(0L, timestamp - _start) * 1000.0 / _frequency;
}
