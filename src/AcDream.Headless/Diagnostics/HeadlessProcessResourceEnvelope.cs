using AcDream.Headless.Hosting;

namespace AcDream.Headless.Diagnostics;

internal readonly record struct HeadlessProcessResourceCeilings(
    string Profile,
    long SharedPrivateBytes,
    long PrivateBytesPerSession,
    long SharedWorkingSetBytes,
    long WorkingSetBytesPerSession,
    long SharedManagedLiveBytes,
    long ManagedLiveBytesPerSession,
    long SharedManagedHeapBytes,
    long ManagedHeapBytesPerSession,
    double SharedCpuCorePercent,
    double CpuCorePercentPerSession,
    int MaximumThreadCount,
    int SharedHandleCount,
    int HandleCountPerSession,
    int SharedDescriptorCount,
    int DescriptorCountPerSession,
    int SharedSocketCount,
    int SocketCountPerSession,
    double MaximumWaitsPerSecond,
    double MaximumCatchUpsPerSecond,
    double MaximumMeanLatenessMilliseconds,
    double MaximumLatenessMilliseconds)
{
    internal const long MiB = 1024L * 1024L;

    internal static HeadlessProcessResourceCeilings K4Linux { get; } =
        new(
            Profile: "k4-linux-30-session",
            SharedPrivateBytes: 224L * MiB,
            PrivateBytesPerSession: 64L * MiB,
            SharedWorkingSetBytes: 224L * MiB,
            WorkingSetBytesPerSession: 64L * MiB,
            SharedManagedLiveBytes: 64L * MiB,
            ManagedLiveBytesPerSession: 48L * MiB,
            SharedManagedHeapBytes: 64L * MiB,
            ManagedHeapBytesPerSession: 48L * MiB,
            SharedCpuCorePercent: 2d,
            CpuCorePercentPerSession: 3d,
            MaximumThreadCount: 48,
            SharedHandleCount: 128,
            HandleCountPerSession: 3,
            SharedDescriptorCount: 128,
            DescriptorCountPerSession: 3,
            SharedSocketCount: 1,
            SocketCountPerSession: 2,
            MaximumWaitsPerSecond: 250d,
            MaximumCatchUpsPerSecond: 1d,
            MaximumMeanLatenessMilliseconds: 10d,
            MaximumLatenessMilliseconds: 750d);

    internal long MaximumPrivateBytes(int sessionCount) =>
        Scale(SharedPrivateBytes, PrivateBytesPerSession, sessionCount);

    internal long MaximumWorkingSetBytes(int sessionCount) =>
        Scale(SharedWorkingSetBytes, WorkingSetBytesPerSession, sessionCount);

    internal long MaximumManagedLiveBytes(int sessionCount) =>
        Scale(
            SharedManagedLiveBytes,
            ManagedLiveBytesPerSession,
            sessionCount);

    internal long MaximumManagedHeapBytes(int sessionCount) =>
        Scale(
            SharedManagedHeapBytes,
            ManagedHeapBytesPerSession,
            sessionCount);

    internal double MaximumCpuCorePercent(int sessionCount) =>
        SharedCpuCorePercent
        + CpuCorePercentPerSession * Math.Max(0, sessionCount);

    internal int MaximumHandleCount(int sessionCount) =>
        Scale(SharedHandleCount, HandleCountPerSession, sessionCount);

    internal int MaximumDescriptorCount(int sessionCount) =>
        Scale(
            SharedDescriptorCount,
            DescriptorCountPerSession,
            sessionCount);

    internal int MaximumSocketCount(int sessionCount) =>
        Scale(SharedSocketCount, SocketCountPerSession, sessionCount);

    private static long Scale(
        long shared,
        long perSession,
        int sessionCount) =>
        checked(shared + perSession * Math.Max(0, sessionCount));

    private static int Scale(
        int shared,
        int perSession,
        int sessionCount) =>
        checked(shared + perSession * Math.Max(0, sessionCount));
}

internal readonly record struct HeadlessProcessResourceEnvelopeSnapshot(
    HeadlessProcessResourceCeilings Ceilings,
    bool HasRateSample,
    bool WithinCeilings,
    long MaximumPrivateBytes,
    long MaximumWorkingSetBytes,
    long MaximumManagedLiveBytes,
    long MaximumManagedHeapBytes,
    double MaximumCpuCorePercent,
    int MaximumThreadCount,
    int MaximumHandleCount,
    int MaximumDescriptorCount,
    int MaximumSocketCount,
    double MaximumWaitsPerSecond,
    double MaximumCatchUpsPerSecond,
    double MaximumMeanLatenessMilliseconds,
    double MaximumLatenessMilliseconds,
    double WaitsPerSecond,
    double TurnsPerSecond,
    double CatchUpsPerSecond,
    string[] Violations);

internal sealed class HeadlessProcessResourceEnvelopeEvaluator
{
    private readonly HeadlessProcessResourceCeilings _ceilings;
    private long _previousWaitCount;
    private long _previousTurnCount;
    private long _previousCatchUpCount;
    private bool _hasPreviousCounters;

    internal HeadlessProcessResourceEnvelopeEvaluator(
        HeadlessProcessResourceCeilings? ceilings = null)
    {
        _ceilings = ceilings
            ?? HeadlessProcessResourceCeilings.K4Linux;
    }

    internal HeadlessProcessResourceEnvelopeSnapshot Evaluate(
        string state,
        in HeadlessProcessUsageSnapshot process,
        in HeadlessManagedUsageSnapshot managed,
        in HeadlessSessionUsageSnapshot sessions,
        in HeadlessSchedulerSnapshot scheduler,
        HeadlessProcessContentSnapshot? content)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(state);
        int sessionCount = sessions.ConfiguredCount;
        long maximumPrivateBytes =
            _ceilings.MaximumPrivateBytes(sessionCount);
        long maximumWorkingSetBytes =
            _ceilings.MaximumWorkingSetBytes(sessionCount);
        long maximumManagedLiveBytes =
            _ceilings.MaximumManagedLiveBytes(sessionCount);
        long maximumManagedHeapBytes =
            _ceilings.MaximumManagedHeapBytes(sessionCount);
        double maximumCpuCorePercent =
            _ceilings.MaximumCpuCorePercent(sessionCount);
        int maximumHandleCount =
            _ceilings.MaximumHandleCount(sessionCount);
        int maximumDescriptorCount =
            _ceilings.MaximumDescriptorCount(sessionCount);
        int maximumSocketCount =
            _ceilings.MaximumSocketCount(sessionCount);

        bool hasRateSample =
            _hasPreviousCounters
            && process.SampleIntervalMilliseconds > 0d;
        double intervalSeconds =
            process.SampleIntervalMilliseconds / 1000d;
        double waitsPerSecond = hasRateSample
            ? Delta(scheduler.WaitCount, _previousWaitCount)
                / intervalSeconds
            : 0d;
        double turnsPerSecond = hasRateSample
            ? Delta(scheduler.TurnCount, _previousTurnCount)
                / intervalSeconds
            : 0d;
        double catchUpsPerSecond = hasRateSample
            ? Delta(
                    scheduler.CatchUpCollapseCount,
                    _previousCatchUpCount)
                / intervalSeconds
            : 0d;
        _previousWaitCount = scheduler.WaitCount;
        _previousTurnCount = scheduler.TurnCount;
        _previousCatchUpCount = scheduler.CatchUpCollapseCount;
        _hasPreviousCounters = true;

        var violations = new List<string>();
        AddIfAbove(
            violations,
            "private-bytes",
            process.PrivateBytes,
            maximumPrivateBytes);
        AddIfAbove(
            violations,
            "working-set-bytes",
            process.WorkingSetBytes,
            maximumWorkingSetBytes);
        AddIfAbove(
            violations,
            "managed-live-bytes",
            managed.LiveBytes,
            maximumManagedLiveBytes);
        AddIfAbove(
            violations,
            "managed-heap-bytes",
            managed.HeapSizeBytes,
            maximumManagedHeapBytes);
        if (process.SampleIntervalMilliseconds > 0d)
        {
            AddIfAbove(
                violations,
                "cpu-core-percent",
                process.CpuCorePercent,
                maximumCpuCorePercent);
        }
        AddIfAbove(
            violations,
            "thread-count",
            process.ThreadCount,
            _ceilings.MaximumThreadCount);
        AddIfSupportedAbove(
            violations,
            "handle-count",
            process.HandleCount,
            maximumHandleCount);
        AddIfSupportedAbove(
            violations,
            "descriptor-count",
            process.FileDescriptorCount,
            maximumDescriptorCount);
        AddIfSupportedAbove(
            violations,
            "socket-count",
            process.SocketDescriptorCount,
            maximumSocketCount);
        if (hasRateSample)
        {
            AddIfAbove(
                violations,
                "scheduler-waits-per-second",
                waitsPerSecond,
                _ceilings.MaximumWaitsPerSecond);
            AddIfAbove(
                violations,
                "scheduler-catchups-per-second",
                catchUpsPerSecond,
                _ceilings.MaximumCatchUpsPerSecond);
        }
        AddIfAbove(
            violations,
            "scheduler-mean-lateness-milliseconds",
            scheduler.MeanLatenessMilliseconds,
            _ceilings.MaximumMeanLatenessMilliseconds);
        AddIfAbove(
            violations,
            "scheduler-maximum-lateness-milliseconds",
            scheduler.MaximumLatenessMilliseconds,
            _ceilings.MaximumLatenessMilliseconds);
        if (sessions.FaultedCount != 0
            || scheduler.FaultedSessionCount != 0)
        {
            violations.Add("faulted-session-debt");
        }

        if (string.Equals(
                state,
                "disposed",
                StringComparison.Ordinal))
        {
            EvaluateTerminal(
                violations,
                sessions,
                content);
        }
        else
        {
            EvaluateRunning(
                violations,
                sessions,
                scheduler,
                content);
        }

        return new HeadlessProcessResourceEnvelopeSnapshot(
            _ceilings,
            hasRateSample,
            violations.Count == 0,
            maximumPrivateBytes,
            maximumWorkingSetBytes,
            maximumManagedLiveBytes,
            maximumManagedHeapBytes,
            maximumCpuCorePercent,
            _ceilings.MaximumThreadCount,
            maximumHandleCount,
            maximumDescriptorCount,
            maximumSocketCount,
            _ceilings.MaximumWaitsPerSecond,
            _ceilings.MaximumCatchUpsPerSecond,
            _ceilings.MaximumMeanLatenessMilliseconds,
            _ceilings.MaximumLatenessMilliseconds,
            waitsPerSecond,
            turnsPerSecond,
            catchUpsPerSecond,
            [.. violations]);
    }

    private static void EvaluateRunning(
        List<string> violations,
        in HeadlessSessionUsageSnapshot sessions,
        in HeadlessSchedulerSnapshot scheduler,
        HeadlessProcessContentSnapshot? content)
    {
        if (scheduler.SessionCount != sessions.ConfiguredCount)
            violations.Add("scheduler-session-count");
        if (sessions.HostLeaseCount != sessions.ConfiguredCount)
            violations.Add("runtime-host-lease-debt");
        if (sessions.InWorldCount + sessions.PendingReconnectCount
            < sessions.ConfiguredCount)
        {
            violations.Add("unaccounted-session-lifecycle");
        }
        if (content is { } current
            && (current.LeaseCount != sessions.ConfiguredCount
                || current.IsDisposeRequested
                || current.IsDisposed))
        {
            violations.Add("shared-content-lease-debt");
        }
    }

    private static void EvaluateTerminal(
        List<string> violations,
        in HeadlessSessionUsageSnapshot sessions,
        HeadlessProcessContentSnapshot? content)
    {
        if (sessions.InWorldCount != 0
            || sessions.PendingReconnectCount != 0
            || sessions.ConvergedRuntimeCount
                != sessions.ConfiguredCount
            || sessions.EntityCount != 0
            || sessions.InventoryObjectCount != 0
            || sessions.HostLeaseCount != 0)
        {
            violations.Add("runtime-terminal-debt");
        }
        if (content is { IsConverged: false })
            violations.Add("shared-content-terminal-debt");
    }

    private static long Delta(long current, long previous) =>
        Math.Max(0L, current - previous);

    private static void AddIfSupportedAbove(
        List<string> violations,
        string name,
        int value,
        int ceiling)
    {
        if (value >= 0)
            AddIfAbove(violations, name, value, ceiling);
    }

    private static void AddIfAbove(
        List<string> violations,
        string name,
        long value,
        long ceiling)
    {
        if (value > ceiling)
            violations.Add(name);
    }

    private static void AddIfAbove(
        List<string> violations,
        string name,
        double value,
        double ceiling)
    {
        if (!double.IsFinite(value) || value > ceiling)
            violations.Add(name);
    }
}
