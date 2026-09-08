using System.Reflection;
using AcDream.App.Rendering;

namespace AcDream.App.Tests.Rendering;

public sealed class RetailAlphaQueueTests
{
    [Fact]
    public void Flush_FifoBeatsReversedDistanceInOneCell()
    {
        var log = new List<string>();
        var source = new RecordingSource("alpha", log);
        var queue = new RetailAlphaQueue();

        queue.BeginFrame();
        // "Reversed distance": entry 0 is submitted first but would be
        // farthest under the deleted distance model; entry 2 nearest.
        // FIFO means append order alone decides replay order now.
        Assert.True(queue.TryAppend(RetailAlphaList.Alpha, source, 0, false));
        Assert.True(queue.TryAppend(RetailAlphaList.Alpha, source, 1, false));
        Assert.True(queue.TryAppend(RetailAlphaList.Alpha, source, 2, false));
        queue.EndFrame();

        Assert.Equal(new[] { "alpha:0", "alpha:1", "alpha:2" }, log);
    }

    [Fact]
    public void Flush_EqualPrioritySourcesPreserveSubmissionInterleave()
    {
        var log = new List<string>();
        var objects = new RecordingSource("object", log);
        var particles = new RecordingSource("particle", log);
        var queue = new RetailAlphaQueue();

        queue.BeginFrame();
        Assert.True(queue.TryAppend(RetailAlphaList.Alpha, objects, 7, false));
        Assert.True(queue.TryAppend(RetailAlphaList.Alpha, particles, 4, false));
        Assert.True(queue.TryAppend(RetailAlphaList.Alpha, objects, 8, false));
        queue.EndFrame();

        Assert.Equal(new[] { "object:7", "particle:4", "object:8" }, log);
    }

    [Fact]
    public void Flush_TwoScopesNeverInterleaveRegardlessOfGlobalOrder()
    {
        var log = new List<string>();
        var source = new RecordingSource("alpha", log);
        var queue = new RetailAlphaQueue();

        queue.BeginFrame();
        Assert.True(queue.TryAppend(RetailAlphaList.Alpha, source, 1, false)); // "far"
        Assert.True(queue.TryAppend(RetailAlphaList.Alpha, source, 2, false)); // "near"
        queue.EndFrame();

        queue.BeginFrame();
        Assert.True(queue.TryAppend(RetailAlphaList.Alpha, source, 9, false)); // "very near"
        queue.EndFrame();

        Assert.Equal(new[] { "alpha:1", "alpha:2", "alpha:9" }, log);
    }

    [Fact]
    public void Flush_ParticleObjectAndCellSourcesOverlapInSubmissionOrder()
    {
        var log = new List<string>();
        var objects = new RecordingSource("object", log);
        var particles = new RecordingSource("particle", log);
        var cellShells = new RecordingSource("cell", log);
        var queue = new RetailAlphaQueue();

        queue.BeginFrame();
        Assert.True(queue.TryAppend(RetailAlphaList.Alpha, objects, 1, false));
        Assert.True(queue.TryAppend(RetailAlphaList.Alpha, particles, 1, false));
        Assert.True(queue.TryAppend(RetailAlphaList.Alpha, cellShells, 1, false));
        Assert.True(queue.TryAppend(RetailAlphaList.Alpha, objects, 2, false));
        queue.EndFrame();

        Assert.Equal(new[] { "object:1", "particle:1", "cell:1", "object:2" }, log);
    }

    [Fact]
    public void Flush_DrawBuildingSiteAtZeroThresholdAlwaysDrains()
    {
        var log = new List<string>();
        var source = new RecordingSource("alpha", log);
        var queue = new RetailAlphaQueue();

        queue.BeginFrame();
        Assert.True(queue.TryAppend(RetailAlphaList.Alpha, source, 1, false));
        queue.Flush(RetailAlphaFlushSite.DrawBuilding, 0f);

        Assert.Equal(new[] { "alpha:1" }, log);
        Assert.Equal(0, queue.PendingCount);
        Assert.True(queue.IsCollecting);
        queue.EndFrame();
    }

    [Fact]
    public void Flush_PreClearThenFinalFlushBothDrainInOrder()
    {
        var log = new List<string>();
        var source = new RecordingSource("alpha", log);
        var queue = new RetailAlphaQueue();

        queue.BeginFrame();
        Assert.True(queue.TryAppend(RetailAlphaList.Alpha, source, 1, false));
        queue.Flush(RetailAlphaFlushSite.LandscapeFlush, 0f);

        Assert.True(queue.IsCollecting);
        Assert.Equal(0, queue.PendingCount);
        Assert.Equal(new[] { "alpha:1" }, log);

        Assert.True(queue.TryAppend(RetailAlphaList.Alpha, source, 2, false));
        queue.EndFrame();

        Assert.False(queue.IsCollecting);
        Assert.Equal(new[] { "alpha:1", "alpha:2" }, log);
        Assert.Equal(2, source.ResetCount);
    }

    [Fact]
    public void Flush_SortCellExitValveDrainsExactlyAtTwoThousandTwoHundredFifty()
    {
        var log = new List<string>();
        var source = new CountingSource();
        var queue = new RetailAlphaQueue();

        queue.BeginFrame();
        for (int i = 0; i < 2250; i++)
            Assert.True(queue.TryAppend(RetailAlphaList.Alpha, source, i, false));

        queue.Flush(RetailAlphaFlushSite.SortCellExit, 0.75f);

        Assert.Equal(0, queue.PendingCount);
        Assert.Equal(2250, source.LastDrawCount);
        Assert.Equal(1, source.ResetCount);
        queue.AbortFrame();
    }

    [Fact]
    public void Flush_SortCellExitValveIsANoOpOneBelowTheBoundary()
    {
        var log = new List<string>();
        var source = new CountingSource();
        var queue = new RetailAlphaQueue();

        queue.BeginFrame();
        for (int i = 0; i < 2249; i++)
            Assert.True(queue.TryAppend(RetailAlphaList.Alpha, source, i, false));

        queue.Flush(RetailAlphaFlushSite.SortCellExit, 0.75f);

        Assert.Equal(2249, queue.PendingCount);
        Assert.Equal(0, source.PrepareCount);
        Assert.Equal(0, source.ResetCount);
        queue.AbortFrame();
    }

    [Fact]
    public void Flush_BatchesAdjacentSameSourceEntriesAcrossTheClipAlphaBoundary()
    {
        var log = new List<string>();
        var shared = new RecordingSource("shared", log);
        var other = new RecordingSource("other", log);
        var queue = new RetailAlphaQueue();

        queue.BeginFrame();
        Assert.True(queue.TryAppend(RetailAlphaList.Clip, shared, 100, false));
        Assert.True(queue.TryAppend(RetailAlphaList.Clip, other, 200, false));
        Assert.True(queue.TryAppend(RetailAlphaList.Alpha, shared, 300, false));
        queue.EndFrame();

        Assert.Equal(new[] { "shared:100", "other:200", "shared:300" }, log);
        Assert.Equal(new[] { 1, 1 }, shared.BatchSizes);
        Assert.Equal(new[] { 1 }, other.BatchSizes);
        Assert.Equal(1, shared.PrepareCount);
        Assert.Equal(1, other.PrepareCount);
    }

    [Fact]
    public void TryAppend_CapacityOverflowDropsTheSubsetWithoutRecovery()
    {
        var source = new CountingSource();
        var queue = new RetailAlphaQueue();

        queue.BeginFrame();
        for (int i = 0; i < RetailAlphaQueue.ListCapacity; i++)
            Assert.True(queue.TryAppend(RetailAlphaList.Clip, source, i, false));

        bool overflowed = queue.TryAppend(RetailAlphaList.Clip, source, 3000, false);

        Assert.False(overflowed);
        Assert.Equal(RetailAlphaQueue.ListCapacity, queue.ClipCount);
        Assert.Equal(RetailAlphaQueue.ListCapacity, queue.PendingCount);

        queue.Flush(RetailAlphaFlushSite.RenderNormalMode, 0f);
        Assert.Equal(RetailAlphaQueue.ListCapacity, source.LastDrawCount);
    }

    [Theory]
    [InlineData("flush")]
    [InlineData("end")]
    [InlineData("abort")]
    public void RejectedFirstUseSource_IsCleanedWithoutPrepareOrDraw(string terminal)
    {
        var filling = new CountingSource();
        var rejected = new RetainedPayloadSource();
        var queue = new RetailAlphaQueue();

        queue.BeginFrame();
        for (int i = 0; i < RetailAlphaQueue.ListCapacity; i++)
            Assert.True(queue.TryAppend(RetailAlphaList.Alpha, filling, i, false));

        int rejectedToken = rejected.Reserve(91);
        Assert.False(queue.TryAppend(
            RetailAlphaList.Alpha,
            rejected,
            rejectedToken,
            overrideClipmap: false));

        switch (terminal)
        {
            case "flush":
                queue.Flush(RetailAlphaFlushSite.DrawBuilding, 0f);
                queue.AbortFrame();
                break;
            case "end":
                queue.EndFrame();
                break;
            case "abort":
                queue.AbortFrame();
                break;
            default:
                throw new ArgumentOutOfRangeException(nameof(terminal));
        }

        Assert.Equal(0, rejected.PrepareCount);
        Assert.Equal(0, rejected.DrawCount);
        Assert.Equal(1, rejected.ResetCount);
        Assert.Equal(0, rejected.PendingCount);

        queue.BeginFrame();
        int acceptedToken = rejected.Reserve(92);
        Assert.True(queue.TryAppend(
            RetailAlphaList.Alpha,
            rejected,
            acceptedToken,
            overrideClipmap: false));
        queue.EndFrame();

        Assert.Equal(1, rejected.PrepareCount);
        Assert.Equal(1, rejected.DrawCount);
        Assert.Equal(2, rejected.ResetCount);
        Assert.Equal(0, rejected.PendingCount);
    }

    [Fact]
    public void TryAppend_ClipAndAlphaCapacitiesAreIndependent()
    {
        var source = new CountingSource();
        var queue = new RetailAlphaQueue();

        queue.BeginFrame();
        for (int i = 0; i < RetailAlphaQueue.ListCapacity; i++)
            Assert.True(queue.TryAppend(RetailAlphaList.Clip, source, i, false));

        Assert.True(queue.TryAppend(RetailAlphaList.Alpha, source, 9999, false));
        Assert.Equal(1, queue.AlphaCount);
        queue.AbortFrame();
    }

    [Fact]
    public void RetainedScratchConvergesAfterAOneScopeSpike()
    {
        const int budgetBytes = 128 * 1024;
        var source = new CountingSource();
        var queue = new RetailAlphaQueue(budgetBytes);

        queue.BeginFrame();
        for (int i = 0; i < 8_192; i++)
            queue.TryAppend(i % 2 == 0 ? RetailAlphaList.Clip : RetailAlphaList.Alpha, source, i, false);
        queue.EndFrame();
        Assert.True(queue.RetainedScratchBytes > budgetBytes);

        for (int i = 0; i < 3; i++)
        {
            queue.BeginFrame();
            queue.EndFrame();
        }

        Assert.True(queue.RetainedScratchBytes <= budgetBytes);
        Assert.False(queue.IsCollecting);
        Assert.Equal(0, queue.PendingCount);
    }

    [Fact]
    public void RetainedSourceCapacity_ConvergesToTheRealSourceCountNotTheEntryCount()
    {
        const int budgetBytes = 128 * 1024;
        var manySources = new CountingSource[100];
        for (int i = 0; i < manySources.Length; i++)
            manySources[i] = new CountingSource();
        var queue = new RetailAlphaQueue(budgetBytes);
        FieldInfo sourcesField = typeof(RetailAlphaQueue).GetField(
            "_sources", BindingFlags.NonPublic | BindingFlags.Instance)!;

        queue.BeginFrame();
        for (int i = 0; i < 8_192; i++)
            queue.TryAppend(RetailAlphaList.Alpha, manySources[i % manySources.Length], i, false);
        queue.EndFrame();

        var sourcesAfterSpike = (List<IRetailAlphaDrawSource>)sourcesField.GetValue(queue)!;
        Assert.True(
            sourcesAfterSpike.Capacity > 8,
            "Test setup check: the 100-distinct-source spike must grow _sources' own capacity "
            + $"past its initial 4 (observed {sourcesAfterSpike.Capacity}) — otherwise the "
            + "Math.Min clamp below hides the bug regardless of which formula runs.");

        CountingSource repeatedSource = manySources[0];
        for (int i = 0; i < 3; i++)
        {
            queue.BeginFrame();
            for (int j = 0; j < 10; j++)
                queue.TryAppend(RetailAlphaList.Alpha, repeatedSource, j, false);
            queue.EndFrame();
        }

        var sources = (List<IRetailAlphaDrawSource>)sourcesField.GetValue(queue)!;
        Assert.True(
            sources.Capacity <= 8,
            "Expected the retained source-array capacity to converge toward the real source "
            + $"count (1), but it stayed at {sources.Capacity} — the entry count (10), not the "
            + "source count (1), must have driven ApplyScratchRetention's second argument.");
    }

    [Fact]
    public void AbortFrame_DiscardsPayloadAndAllowsTheNextFrameToRender()
    {
        var log = new List<string>();
        var source = new RecordingSource("alpha", log);
        var queue = new RetailAlphaQueue();

        queue.BeginFrame();
        queue.TryAppend(RetailAlphaList.Alpha, source, 1, false);
        queue.AbortFrame();

        Assert.False(queue.IsCollecting);
        Assert.Equal(0, queue.PendingCount);
        Assert.Empty(log);
        Assert.Equal(1, source.ResetCount);

        queue.BeginFrame();
        queue.TryAppend(RetailAlphaList.Alpha, source, 2, false);
        queue.EndFrame();

        Assert.Equal(new[] { "alpha:2" }, log);
        Assert.Equal(2, source.ResetCount);
    }

    [Fact]
    public void EndFrame_DrawAndResetFailuresPreserveThePrimaryFailureAndClearTheFrame()
    {
        var drawSource = new FailureSource("draw failed", "first reset failed");
        var secondSource = new FailureSource(null, null);
        var queue = new RetailAlphaQueue();

        queue.BeginFrame();
        queue.TryAppend(RetailAlphaList.Alpha, drawSource, 1, false);
        queue.TryAppend(RetailAlphaList.Alpha, secondSource, 2, false);

        AggregateException failure = Assert.Throws<AggregateException>(queue.EndFrame);

        Assert.Collection(
            failure.InnerExceptions,
            error => Assert.Equal("draw failed", error.Message),
            error => Assert.Equal("first reset failed", error.Message));
        Assert.Equal(1, drawSource.ResetCount);
        Assert.Equal(1, secondSource.ResetCount);
        Assert.Equal(0, queue.PendingCount);
        Assert.False(queue.IsCollecting);
    }

    [Fact]
    public void EndFrame_MultipleResetFailuresAttemptEverySourceAndClearTheFrame()
    {
        var first = new FailureSource(null, "first reset failed");
        var second = new FailureSource(null, "second reset failed");
        var queue = new RetailAlphaQueue();

        queue.BeginFrame();
        queue.TryAppend(RetailAlphaList.Alpha, first, 1, false);
        queue.TryAppend(RetailAlphaList.Alpha, second, 2, false);

        AggregateException failure = Assert.Throws<AggregateException>(queue.EndFrame);

        Assert.Collection(
            failure.InnerExceptions,
            error => Assert.Equal("first reset failed", error.Message),
            error => Assert.Equal("second reset failed", error.Message));
        Assert.Equal(1, first.ResetCount);
        Assert.Equal(1, second.ResetCount);
        Assert.Equal(0, queue.PendingCount);
        Assert.False(queue.IsCollecting);
    }

    private sealed class RecordingSource(string name, List<string> log) : IRetailAlphaDrawSource
    {
        public List<int> BatchSizes { get; } = new();
        public int ResetCount { get; private set; }
        public int PrepareCount { get; private set; }
        private int[] _prepared = [];

        public void PrepareAlphaDraws(ReadOnlySpan<int> tokens)
        {
            PrepareCount++;
            _prepared = tokens.ToArray();
        }

        public void DrawPreparedAlphaBatch(int firstPreparedDraw, int drawCount)
        {
            BatchSizes.Add(drawCount);
            for (int i = 0; i < drawCount; i++)
                log.Add($"{name}:{_prepared[firstPreparedDraw + i]}");
        }

        public void ResetAlphaSubmissions() => ResetCount++;
    }

    private sealed class CountingSource : IRetailAlphaDrawSource
    {
        public int PrepareCount { get; private set; }
        public int ResetCount { get; private set; }
        public int LastDrawCount { get; private set; }

        public void PrepareAlphaDraws(ReadOnlySpan<int> tokens) => PrepareCount++;

        public void DrawPreparedAlphaBatch(int firstPreparedDraw, int drawCount) =>
            LastDrawCount = drawCount;

        public void ResetAlphaSubmissions() => ResetCount++;
    }

    private sealed class RetainedPayloadSource : IRetailAlphaDrawSource
    {
        private readonly List<int> _pending = new();
        private int[] _prepared = [];

        public int PrepareCount { get; private set; }
        public int DrawCount { get; private set; }
        public int ResetCount { get; private set; }
        public int PendingCount => _pending.Count;

        public int Reserve(int value)
        {
            int token = _pending.Count;
            _pending.Add(value);
            return token;
        }

        public void PrepareAlphaDraws(ReadOnlySpan<int> tokens)
        {
            PrepareCount++;
            _prepared = new int[tokens.Length];
            for (int i = 0; i < tokens.Length; i++)
                _prepared[i] = _pending[tokens[i]];
        }

        public void DrawPreparedAlphaBatch(int firstPreparedDraw, int drawCount)
        {
            Assert.InRange(firstPreparedDraw, 0, _prepared.Length - drawCount);
            DrawCount += drawCount;
        }

        public void ResetAlphaSubmissions()
        {
            ResetCount++;
            _pending.Clear();
            _prepared = [];
        }
    }

    private sealed class FailureSource(
        string? drawFailure,
        string? resetFailure) : IRetailAlphaDrawSource
    {
        public int ResetCount { get; private set; }

        public void PrepareAlphaDraws(ReadOnlySpan<int> tokens)
        {
        }

        public void DrawPreparedAlphaBatch(int firstPreparedDraw, int drawCount)
        {
            if (drawFailure is not null)
                throw new InvalidOperationException(drawFailure);
        }

        public void ResetAlphaSubmissions()
        {
            ResetCount++;
            if (resetFailure is not null)
                throw new InvalidOperationException(resetFailure);
        }
    }
}
