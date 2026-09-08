using System.Diagnostics;
using System.Text;

namespace AcDream.App.Streaming;

internal sealed class PublicationStageTimings
{
    private readonly Dictionary<string, (long Ticks, int Count)> _stages = new();
    private long _totalTicks;

    public long TotalTicks => _totalTicks;

    public void Add(string stage, long ticks)
    {
        if (ticks < 0)
            ticks = 0;
        _totalTicks += ticks;
        _stages[stage] = _stages.TryGetValue(stage, out (long Ticks, int Count) prior)
            ? (prior.Ticks + ticks, prior.Count + 1)
            : (ticks, 1);
    }

    public IReadOnlyDictionary<string, (long Ticks, int Count)> Stages => _stages;
}

internal static class PublicationTimingProbe
{
    private const int CumulativeEmitInterval = 64;

    private static readonly Dictionary<string, (long Ticks, int Count)>
        s_cumulativeStages = new();
    private static readonly Dictionary<string, int> s_tickWindowYieldReasons =
        new();
    private static long s_cumulativeTicks;
    private static int s_publications;
    private static double s_tickWindowSumMs;
    private static double s_tickWindowMaxMs;
    private static int s_tickWindowCount;
    private static int s_tickWindowYields;
    private static int s_tickWindowOperations;
    private static int s_tickWindowAdmissions;
    private static int s_lastWorkerBacklog;
    private static int s_lastQueuedCompletions;

    public static bool Enabled => StreamingDiagnostics.ProbeRevealTiming;

    /// <summary>Per-transaction accumulator, or null when the probe is off.</summary>
    public static PublicationStageTimings? CreateTimings() =>
        Enabled ? new PublicationStageTimings() : null;

    public static void EmitPublication(
        uint landblockId,
        string kind,
        PublicationStageTimings timings)
    {
        s_publications++;
        s_cumulativeTicks += timings.TotalTicks;
        var line = new StringBuilder(256);
        line.Append("[publish-timing] lb=0x")
            .Append(landblockId.ToString("X8"))
            .Append(" kind=").Append(kind)
            .Append(" totalMs=")
            .Append(ToMs(timings.TotalTicks).ToString("F2"))
            .Append(" stages=");
        AppendStagesByCost(line, timings.Stages);
        Console.WriteLine(line.ToString());

        foreach ((string stage, (long ticks, int count)) in timings.Stages)
        {
            s_cumulativeStages[stage] = s_cumulativeStages.TryGetValue(
                stage,
                out (long Ticks, int Count) prior)
                ? (prior.Ticks + ticks, prior.Count + count)
                : (ticks, count);
        }

        if (s_publications % CumulativeEmitInterval == 0)
            EmitCumulative();
    }

    private static void EmitCumulative()
    {
        var line = new StringBuilder(256);
        line.Append("[publish-timing] CUMULATIVE landblocks=")
            .Append(s_publications)
            .Append(" totalMs=")
            .Append(ToMs(s_cumulativeTicks).ToString("F0"))
            .Append(" stages=");
        AppendStagesByCost(line, s_cumulativeStages);
        Console.WriteLine(line.ToString());
    }

    private static void AppendStagesByCost(
        StringBuilder line,
        IReadOnlyDictionary<string, (long Ticks, int Count)> stages)
    {
        bool first = true;
        foreach ((string stage, (long ticks, int count)) in
            stages.OrderByDescending(static pair => pair.Value.Ticks))
        {
            if (!first)
                line.Append(',');
            first = false;
            line.Append(TrimStagePrefix(stage))
                .Append(':')
                .Append(ToMs(ticks).ToString("F2"))
                .Append('/')
                .Append(count);
        }
    }

    /// <summary>
    /// Every pipeline stage name starts with <c>publication-</c>; dropping the
    /// shared prefix keeps the per-landblock line readable.
    /// </summary>
    private static string TrimStagePrefix(string stage) =>
        stage.StartsWith("publication-", StringComparison.Ordinal)
            ? stage["publication-".Length..]
            : stage;

    public static void ObserveStreamingTick(
        in StreamingWorkMeterSnapshot snapshot,
        int workerBacklog,
        int queuedCompletions)
    {
        s_tickWindowCount++;
        s_tickWindowSumMs += snapshot.ElapsedMilliseconds;
        if (snapshot.ElapsedMilliseconds > s_tickWindowMaxMs)
            s_tickWindowMaxMs = snapshot.ElapsedMilliseconds;
        s_tickWindowYields += snapshot.YieldCount;
        s_tickWindowOperations += snapshot.Operations;
        s_tickWindowAdmissions += snapshot.Used.CompletionAdmissions;
        if (snapshot.YieldCount > 0
            && snapshot.LastLimit != StreamingWorkLimit.None
            && snapshot.LastStage is { } stage)
        {
            string reason = $"{stage}/{snapshot.LastLimit}";
            s_tickWindowYieldReasons[reason] =
                s_tickWindowYieldReasons.TryGetValue(reason, out int prior)
                    ? prior + 1
                    : 1;
        }
        s_lastWorkerBacklog = workerBacklog;
        s_lastQueuedCompletions = queuedCompletions;
    }

    public static void EmitStreamingTickWindow()
    {
        var line = new StringBuilder(192);
        line.Append("[stream-tick] ticks=").Append(s_tickWindowCount)
            .Append(" sumMs=").Append(s_tickWindowSumMs.ToString("F1"))
            .Append(" maxMs=").Append(s_tickWindowMaxMs.ToString("F1"))
            .Append(" ops=").Append(s_tickWindowOperations)
            .Append(" yields=").Append(s_tickWindowYields)
            .Append(" admissions=").Append(s_tickWindowAdmissions)
            .Append(" workerBacklog=").Append(s_lastWorkerBacklog)
            .Append(" queuedCompletions=").Append(s_lastQueuedCompletions)
            .Append(" yieldReasons=");
        bool first = true;
        foreach ((string reason, int count) in
            s_tickWindowYieldReasons.OrderByDescending(
                static pair => pair.Value))
        {
            if (!first)
                line.Append(',');
            first = false;
            line.Append(reason).Append('x').Append(count);
        }
        if (first)
            line.Append("none");
        Console.WriteLine(line.ToString());

        s_tickWindowCount = 0;
        s_tickWindowSumMs = 0;
        s_tickWindowMaxMs = 0;
        s_tickWindowYields = 0;
        s_tickWindowOperations = 0;
        s_tickWindowAdmissions = 0;
        s_tickWindowYieldReasons.Clear();
    }

    private static double ToMs(long stopwatchTicks) =>
        stopwatchTicks * 1000.0 / Stopwatch.Frequency;
}
