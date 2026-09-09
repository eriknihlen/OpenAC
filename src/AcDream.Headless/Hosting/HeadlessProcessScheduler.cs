using AcDream.Runtime;

namespace AcDream.Headless.Hosting;

internal sealed class HeadlessProcessScheduler
{
    private static readonly TimeSpan MinimumTimerDelay =
        TimeSpan.FromMilliseconds(1);

    private sealed class Slot
    {
        internal Slot(
            HeadlessSessionHost session,
            long startTimestamp,
            long periodTicks)
        {
            Session = session;
            LastTurnTimestamp = startTimestamp;
            NextDeadline = AddTicks(startTimestamp, periodTicks);
        }

        internal HeadlessSessionHost Session { get; }
        internal long LastTurnTimestamp { get; set; }
        internal long NextDeadline { get; set; }
    }

    private readonly TimeProvider _timeProvider;
    private readonly long _periodTicks;
    private readonly int _maximumCatchUpTurns;
    private readonly Slot[] _slots;
    private readonly Action<HeadlessSchedulerSnapshot>? _observation;
    private readonly long _observationPeriodTicks;
    private long _nextObservationDeadline;
    private long _waitCount;
    private long _turnCount;
    private long _catchUpCollapseCount;
    private long _lateDeadlineCount;
    private long _totalLatenessTicks;
    private long _maximumLatenessTicks;

    internal HeadlessProcessScheduler(
        IReadOnlyList<HeadlessSessionHost> sessions,
        TimeProvider? timeProvider = null,
        TimeSpan? turnPeriod = null,
        int maximumCatchUpTurns = 8,
        Action<HeadlessSchedulerSnapshot>? observation = null,
        TimeSpan? observationPeriod = null)
    {
        ArgumentNullException.ThrowIfNull(sessions);
        if (sessions.Count == 0)
        {
            throw new ArgumentException(
                "A headless scheduler requires at least one session.",
                nameof(sessions));
        }
        if (maximumCatchUpTurns <= 0)
        {
            throw new ArgumentOutOfRangeException(
                nameof(maximumCatchUpTurns));
        }

        _timeProvider = timeProvider ?? TimeProvider.System;
        TimeSpan configuredTurnPeriod =
            turnPeriod ?? TimeSpan.FromMilliseconds(15);
        if (configuredTurnPeriod <= TimeSpan.Zero)
            throw new ArgumentOutOfRangeException(nameof(turnPeriod));
        _periodTicks = DurationToTimestampTicks(
            _timeProvider,
            configuredTurnPeriod);
        _maximumCatchUpTurns = maximumCatchUpTurns;
        _observation = observation;
        TimeSpan configuredObservationPeriod =
            observationPeriod ?? TimeSpan.FromSeconds(30);
        if (configuredObservationPeriod <= TimeSpan.Zero)
        {
            throw new ArgumentOutOfRangeException(
                nameof(observationPeriod));
        }
        _observationPeriodTicks = DurationToTimestampTicks(
            _timeProvider,
            configuredObservationPeriod);

        long start = _timeProvider.GetTimestamp();
        _nextObservationDeadline = AddTicks(
            start,
            _observationPeriodTicks);
        _slots = new Slot[sessions.Count];
        for (int index = 0; index < sessions.Count; index++)
        {
            _slots[index] = new Slot(
                sessions[index]
                    ?? throw new ArgumentException(
                        "A headless scheduler session cannot be null.",
                        nameof(sessions)),
                start,
                _periodTicks);
        }
    }

    internal HeadlessSchedulerSnapshot CaptureSnapshot()
    {
        int activeSessionCount = 0;
        int faultedSessionCount = 0;
        for (int index = 0; index < _slots.Length; index++)
        {
            if (_slots[index].Session.IsFaulted)
                faultedSessionCount++;
            else if (!_slots[index].Session.IsPolicyComplete)
                activeSessionCount++;
        }

        return new HeadlessSchedulerSnapshot(
            _slots.Length,
            Interlocked.Read(ref _waitCount),
            Interlocked.Read(ref _turnCount),
            Interlocked.Read(ref _catchUpCollapseCount),
            Interlocked.Read(ref _lateDeadlineCount),
            Interlocked.Read(ref _totalLatenessTicks),
            Interlocked.Read(ref _maximumLatenessTicks),
            _timeProvider.TimestampFrequency,
            activeSessionCount,
            faultedSessionCount,
            NextDeadline());
    }

    internal void RebaseDeadlinesAfterSessionStart()
    {
        if (Interlocked.Read(ref _waitCount) != 0L
            || Interlocked.Read(ref _turnCount) != 0L
            || Interlocked.Read(ref _catchUpCollapseCount) != 0L
            || Interlocked.Read(ref _lateDeadlineCount) != 0L)
        {
            throw new InvalidOperationException(
                "A running headless scheduler cannot rebase its deadlines.");
        }

        long start = _timeProvider.GetTimestamp();
        for (int index = 0; index < _slots.Length; index++)
        {
            _slots[index].LastTurnTimestamp = start;
            _slots[index].NextDeadline = AddTicks(
                start,
                _periodTicks);
        }
        _nextObservationDeadline = AddTicks(
            start,
            _observationPeriodTicks);
    }

    internal void Run(CancellationToken cancellationToken)
    {
        using var wake = new ManualResetEventSlim(false);
        using ITimer timer = _timeProvider.CreateTimer(
            static state => ((ManualResetEventSlim)state!).Set(),
            wake,
            Timeout.InfiniteTimeSpan,
            Timeout.InfiniteTimeSpan);
        WaitHandle[] waitHandles =
            [wake.WaitHandle, cancellationToken.WaitHandle];
        while (!cancellationToken.IsCancellationRequested
            && HasActiveSession())
        {
            long now = _timeProvider.GetTimestamp();
            bool dispatched = DispatchDue(now);
            bool observed = DispatchObservationDue(now);
            if (dispatched || observed)
                continue;

            long deadline = NextDeadline();
            if (_observation is not null
                && _nextObservationDeadline < deadline)
            {
                deadline = _nextObservationDeadline;
            }
            TimeSpan delay = deadline <= now
                ? TimeSpan.Zero
                : _timeProvider.GetElapsedTime(now, deadline);
            delay = NormalizeTimerDelay(delay);
            Interlocked.Increment(ref _waitCount);
            wake.Reset();
            timer.Change(delay, Timeout.InfiniteTimeSpan);
            WaitHandle.WaitAny(waitHandles);
        }
    }

    internal static TimeSpan NormalizeTimerDelay(TimeSpan delay) =>
        delay > TimeSpan.Zero
        && delay < MinimumTimerDelay
            ? MinimumTimerDelay
            : delay;

    internal bool DispatchDue(long nowTimestamp)
    {
        bool dispatched = false;
        foreach (Slot slot in _slots)
        {
            HeadlessSessionHost session = slot.Session;
            if (session.IsPolicyComplete)
                continue;

            try
            {
                if (session.IsReconnectPending)
                {
                    if (nowTimestamp < session.ReconnectDeadline)
                        continue;

                    RecordLateness(
                        nowTimestamp,
                        session.ReconnectDeadline);
                    RuntimeSessionStartResult result =
                        session.CompletePendingReconnect(nowTimestamp);
                    if (result.Status
                        is not RuntimeSessionStartStatus.Connected)
                    {
                        throw result.Error
                            ?? new InvalidOperationException(
                                $"Headless session reconnect completed with {result.Status}.");
                    }
                    slot.LastTurnTimestamp = nowTimestamp;
                    slot.NextDeadline = AddTicks(
                        nowTimestamp,
                        _periodTicks);
                    dispatched = true;
                    continue;
                }

                if (nowTimestamp < slot.NextDeadline)
                    continue;

                DispatchSessionDue(slot, nowTimestamp);
                dispatched = true;
            }
            catch (Exception error)
            {
                session.Quarantine(error);
                dispatched = true;
            }
        }
        return dispatched;
    }

    internal bool DispatchObservationDue(long nowTimestamp)
    {
        if (_observation is null
            || nowTimestamp < _nextObservationDeadline)
        {
            return false;
        }

        long dueCount =
            1L
            + ((nowTimestamp - _nextObservationDeadline)
                / _observationPeriodTicks);
        long advance = dueCount
            > long.MaxValue / _observationPeriodTicks
                ? long.MaxValue
                : dueCount * _observationPeriodTicks;
        _nextObservationDeadline = AddTicks(
            _nextObservationDeadline,
            advance);
        _observation(CaptureSnapshot());
        return true;
    }

    private void DispatchSessionDue(Slot slot, long nowTimestamp)
    {
        RecordLateness(nowTimestamp, slot.NextDeadline);
        long dueCount =
            1L + ((nowTimestamp - slot.NextDeadline) / _periodTicks);
        int turns = (int)Math.Min(
            dueCount,
            _maximumCatchUpTurns);
        bool collapse = dueCount > _maximumCatchUpTurns;

        for (int turn = 0; turn < turns; turn++)
        {
            if (slot.Session.IsPolicyComplete
                || slot.Session.IsReconnectPending)
            {
                break;
            }

            bool finalCollapsedTurn =
                collapse && turn == turns - 1;
            long targetTimestamp = finalCollapsedTurn
                ? nowTimestamp
                : slot.NextDeadline;
            double deltaSeconds = _timeProvider
                .GetElapsedTime(
                    slot.LastTurnTimestamp,
                    targetTimestamp)
                .TotalSeconds;
            slot.Session.Tick(deltaSeconds);
            Interlocked.Increment(ref _turnCount);
            slot.LastTurnTimestamp = targetTimestamp;
            slot.NextDeadline = AddTicks(
                targetTimestamp,
                _periodTicks);
        }

        if (collapse)
            Interlocked.Increment(ref _catchUpCollapseCount);
    }

    private void RecordLateness(long nowTimestamp, long deadline)
    {
        if (nowTimestamp <= deadline)
            return;

        long lateness = nowTimestamp - deadline;
        Interlocked.Increment(ref _lateDeadlineCount);
        Interlocked.Add(ref _totalLatenessTicks, lateness);
        long maximum = Interlocked.Read(ref _maximumLatenessTicks);
        while (lateness > maximum)
        {
            long observed = Interlocked.CompareExchange(
                ref _maximumLatenessTicks,
                lateness,
                maximum);
            if (observed == maximum)
                break;
            maximum = observed;
        }
    }

    private long NextDeadline()
    {
        long next = long.MaxValue;
        foreach (Slot slot in _slots)
        {
            if (slot.Session.IsPolicyComplete)
                continue;
            long candidate = slot.Session.IsReconnectPending
                ? slot.Session.ReconnectDeadline
                : slot.NextDeadline;
            if (candidate < next)
                next = candidate;
        }
        return next;
    }

    private bool HasActiveSession()
    {
        for (int index = 0; index < _slots.Length; index++)
        {
            if (!_slots[index].Session.IsPolicyComplete)
                return true;
        }
        return false;
    }

    private static long DurationToTimestampTicks(
        TimeProvider timeProvider,
        TimeSpan duration)
    {
        double ticks =
            duration.TotalSeconds * timeProvider.TimestampFrequency;
        if (!double.IsFinite(ticks)
            || ticks <= 0d
            || ticks > long.MaxValue)
        {
            throw new ArgumentOutOfRangeException(nameof(duration));
        }
        return Math.Max(
            1L,
            checked((long)Math.Ceiling(ticks)));
    }

    private static long AddTicks(long timestamp, long ticks) =>
        timestamp > long.MaxValue - ticks
            ? long.MaxValue
            : timestamp + ticks;
}

internal readonly record struct HeadlessSchedulerSnapshot(
    int SessionCount,
    long WaitCount,
    long TurnCount,
    long CatchUpCollapseCount,
    long LateDeadlineCount,
    long TotalLatenessTicks,
    long MaximumLatenessTicks,
    long TimestampFrequency,
    int ActiveSessionCount,
    int FaultedSessionCount,
    long NextDeadline)
{
    internal double MeanLatenessMilliseconds =>
        LateDeadlineCount == 0
            ? 0d
            : TimestampTicksToMilliseconds(
                TotalLatenessTicks / (double)LateDeadlineCount);

    internal double MaximumLatenessMilliseconds =>
        TimestampTicksToMilliseconds(MaximumLatenessTicks);

    private double TimestampTicksToMilliseconds(double ticks) =>
        TimestampFrequency <= 0L
            ? 0d
            : ticks * 1000d / TimestampFrequency;
}
