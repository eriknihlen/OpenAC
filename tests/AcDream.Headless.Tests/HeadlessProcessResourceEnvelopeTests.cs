using AcDream.Headless.Diagnostics;
using AcDream.Headless.Hosting;

namespace AcDream.Headless.Tests;

public sealed class HeadlessProcessResourceEnvelopeTests
{
    [Fact]
    public void MultiSessionProfileScalesOnlyPerSessionDimensions()
    {
        HeadlessProcessResourceCeilings ceilings =
            HeadlessProcessResourceCeilings.K4Linux;

        Assert.Equal(
            288L * HeadlessProcessResourceCeilings.MiB,
            ceilings.MaximumPrivateBytes(1));
        Assert.Equal(
            352L * HeadlessProcessResourceCeilings.MiB,
            ceilings.MaximumPrivateBytes(2));
        Assert.Equal(
            2144L * HeadlessProcessResourceCeilings.MiB,
            ceilings.MaximumPrivateBytes(30));
        Assert.Equal(8d, ceilings.MaximumCpuCorePercent(2));
        Assert.Equal(92d, ceilings.MaximumCpuCorePercent(30));
        Assert.Equal(
            160L * HeadlessProcessResourceCeilings.MiB,
            ceilings.MaximumManagedHeapBytes(2));
        Assert.Equal(48, ceilings.MaximumThreadCount);
        Assert.Equal(5, ceilings.MaximumSocketCount(2));
        Assert.Equal(61, ceilings.MaximumSocketCount(30));
    }

    [Fact]
    public void FixedBinaryTwoSessionRynthidSamplePasses()
    {
        var evaluator =
            new HeadlessProcessResourceEnvelopeEvaluator();
        _ = evaluator.Evaluate(
            "running-start",
            Process(
                privateBytes: 207_966_208L,
                workingSetBytes: 190_509_056L),
            Managed(71_036_416L),
            RunningSessions(2),
            Scheduler(2),
            RunningContent(2));

        HeadlessProcessResourceEnvelopeSnapshot result =
            evaluator.Evaluate(
                "periodic",
                Process(
                    privateBytes: 313_847_808L,
                    workingSetBytes: 302_817_280L,
                    cpuCorePercent: 6.2811d,
                    sampleIntervalMilliseconds: 30_000d,
                    threadCount: 17,
                    handleCount: 115,
                    descriptorCount: 116,
                    socketCount: 3),
                Managed(122_179_536L),
                RunningSessions(2),
                Scheduler(
                    2,
                    waitCount: 2918L,
                    turnCount: 3974L,
                    catchUpCount: 2L,
                    lateCount: 3974L,
                    meanLatenessMilliseconds: 3.31d,
                    maximumLatenessMilliseconds: 302.16d),
                RunningContent(2));

        Assert.True(result.HasRateSample);
        Assert.True(result.WithinCeilings);
        Assert.Empty(result.Violations);
        Assert.Equal(97.2667d, result.WaitsPerSecond, precision: 4);
        Assert.Equal(132.4667d, result.TurnsPerSecond, precision: 4);
        Assert.Equal(0.0667d, result.CatchUpsPerSecond, precision: 4);
    }

    [Fact]
    public void EveryResourceAndRuntimeDebtIsNamed()
    {
        var evaluator =
            new HeadlessProcessResourceEnvelopeEvaluator();
        _ = evaluator.Evaluate(
            "running-start",
            Process(1L, 1L),
            Managed(1L),
            RunningSessions(2),
            Scheduler(2),
            RunningContent(2));
        HeadlessProcessResourceCeilings ceilings =
            HeadlessProcessResourceCeilings.K4Linux;
        var sessions = new HeadlessSessionUsageSnapshot(
            ConfiguredCount: 2,
            InWorldCount: 0,
            FaultedCount: 1,
            PendingReconnectCount: 0,
            ConvergedRuntimeCount: 0,
            EntityCount: 0,
            InventoryObjectCount: 0,
            HostLeaseCount: 0);

        HeadlessProcessResourceEnvelopeSnapshot result =
            evaluator.Evaluate(
                "periodic",
                Process(
                    ceilings.MaximumPrivateBytes(2) + 1L,
                    ceilings.MaximumWorkingSetBytes(2) + 1L,
                    cpuCorePercent:
                        ceilings.MaximumCpuCorePercent(2) + 0.01d,
                    sampleIntervalMilliseconds: 30_000d,
                    threadCount: ceilings.MaximumThreadCount + 1,
                    handleCount:
                        ceilings.MaximumHandleCount(2) + 1,
                    descriptorCount:
                        ceilings.MaximumDescriptorCount(2) + 1,
                    socketCount:
                        ceilings.MaximumSocketCount(2) + 1),
                Managed(
                    ceilings.MaximumManagedLiveBytes(2) + 1L),
                sessions,
                Scheduler(
                    sessionCount: 1,
                    waitCount: 7501L,
                    turnCount: 1L,
                    catchUpCount: 31L,
                    lateCount: 1L,
                    meanLatenessMilliseconds: 10.01d,
                    maximumLatenessMilliseconds: 750.01d,
                    faultedCount: 1),
                new HeadlessProcessContentSnapshot(
                    0,
                    IsDisposeRequested: true,
                    IsDisposed: false,
                    MappedVirtualBytes: 1L));

        Assert.False(result.WithinCeilings);
        string[] expected =
        [
            "private-bytes",
            "working-set-bytes",
            "managed-live-bytes",
            "managed-heap-bytes",
            "cpu-core-percent",
            "thread-count",
            "handle-count",
            "descriptor-count",
            "socket-count",
            "scheduler-waits-per-second",
            "scheduler-catchups-per-second",
            "scheduler-mean-lateness-milliseconds",
            "scheduler-maximum-lateness-milliseconds",
            "faulted-session-debt",
            "scheduler-session-count",
            "runtime-host-lease-debt",
            "unaccounted-session-lifecycle",
            "shared-content-lease-debt",
        ];
        Assert.Equal(expected, result.Violations);
    }

    [Fact]
    public void DisposedSampleRequiresExactRuntimeAndContentConvergence()
    {
        var failing =
            new HeadlessProcessResourceEnvelopeEvaluator();
        HeadlessProcessResourceEnvelopeSnapshot debt =
            failing.Evaluate(
                "disposed",
                Process(1L, 1L),
                Managed(1L),
                RunningSessions(2),
                Scheduler(2),
                RunningContent(2));

        Assert.False(debt.WithinCeilings);
        Assert.Contains("runtime-terminal-debt", debt.Violations);
        Assert.Contains(
            "shared-content-terminal-debt",
            debt.Violations);

        var passing =
            new HeadlessProcessResourceEnvelopeEvaluator();
        HeadlessProcessResourceEnvelopeSnapshot converged =
            passing.Evaluate(
                "disposed",
                Process(1L, 1L),
                Managed(1L),
                new HeadlessSessionUsageSnapshot(
                    ConfiguredCount: 2,
                    InWorldCount: 0,
                    FaultedCount: 0,
                    PendingReconnectCount: 0,
                    ConvergedRuntimeCount: 2,
                    EntityCount: 0,
                    InventoryObjectCount: 0,
                    HostLeaseCount: 0),
                Scheduler(2),
                new HeadlessProcessContentSnapshot(
                    0,
                    IsDisposeRequested: true,
                    IsDisposed: true,
                    MappedVirtualBytes: 0L));

        Assert.True(converged.WithinCeilings);
        Assert.Empty(converged.Violations);
    }

    [Fact]
    public void NonFiniteResourceRatesFailClosed()
    {
        var evaluator =
            new HeadlessProcessResourceEnvelopeEvaluator();

        HeadlessProcessResourceEnvelopeSnapshot result =
            evaluator.Evaluate(
                "periodic",
                Process(
                    1L,
                    1L,
                    cpuCorePercent: double.NaN,
                    sampleIntervalMilliseconds: 30_000d),
                Managed(1L),
                RunningSessions(1),
                Scheduler(1),
                RunningContent(1));

        Assert.False(result.WithinCeilings);
        Assert.Contains("cpu-core-percent", result.Violations);
    }

    private static HeadlessProcessUsageSnapshot Process(
        long privateBytes,
        long workingSetBytes,
        double cpuCorePercent = 0d,
        double sampleIntervalMilliseconds = 0d,
        int threadCount = 16,
        int handleCount = 113,
        int descriptorCount = 112,
        int socketCount = 3) =>
        new(
            WorkingSetBytes: workingSetBytes,
            PrivateBytes: privateBytes,
            VirtualBytes: 30_000_000_000L,
            TotalProcessorTimeTicks: 0L,
            SampleIntervalMilliseconds: sampleIntervalMilliseconds,
            CpuCorePercent: cpuCorePercent,
            CpuMachinePercent: cpuCorePercent / 16d,
            ProcessorCount: 16,
            ThreadCount: threadCount,
            HandleCount: handleCount,
            FileDescriptorCount: descriptorCount,
            SocketDescriptorCount: socketCount);

    private static HeadlessManagedUsageSnapshot Managed(
        long liveBytes) =>
        new(
            LiveBytes: liveBytes,
            HeapSizeBytes: liveBytes,
            FragmentedBytes: 0L,
            CommittedBytes: liveBytes,
            TotalAllocatedBytes: liveBytes,
            MemoryLoadBytes: liveBytes,
            HighMemoryLoadThresholdBytes: long.MaxValue,
            PauseTimePercentage: 0d,
            Gen0Collections: 0,
            Gen1Collections: 0,
            Gen2Collections: 0);

    private static HeadlessSessionUsageSnapshot RunningSessions(
        int sessionCount) =>
        new(
            ConfiguredCount: sessionCount,
            InWorldCount: sessionCount,
            FaultedCount: 0,
            PendingReconnectCount: 0,
            ConvergedRuntimeCount: 0,
            EntityCount: sessionCount,
            InventoryObjectCount: sessionCount,
            HostLeaseCount: sessionCount);

    private static HeadlessSchedulerSnapshot Scheduler(
        int sessionCount,
        long waitCount = 0L,
        long turnCount = 0L,
        long catchUpCount = 0L,
        long lateCount = 0L,
        double meanLatenessMilliseconds = 0d,
        double maximumLatenessMilliseconds = 0d,
        int faultedCount = 0)
    {
        const long frequency = TimeSpan.TicksPerSecond;
        long totalLatenessTicks = checked((long)(
            meanLatenessMilliseconds
            * TimeSpan.TicksPerMillisecond
            * lateCount));
        long maximumLatenessTicks = checked((long)(
            maximumLatenessMilliseconds
            * TimeSpan.TicksPerMillisecond));
        return new HeadlessSchedulerSnapshot(
            SessionCount: sessionCount,
            WaitCount: waitCount,
            TurnCount: turnCount,
            CatchUpCollapseCount: catchUpCount,
            LateDeadlineCount: lateCount,
            TotalLatenessTicks: totalLatenessTicks,
            MaximumLatenessTicks: maximumLatenessTicks,
            TimestampFrequency: frequency,
            ActiveSessionCount: sessionCount - faultedCount,
            FaultedSessionCount: faultedCount,
            NextDeadline: 0L);
    }

    private static HeadlessProcessContentSnapshot RunningContent(
        int sessionCount) =>
        new(
            LeaseCount: sessionCount,
            IsDisposeRequested: false,
            IsDisposed: false,
            MappedVirtualBytes: 29_908_271_024L);
}
