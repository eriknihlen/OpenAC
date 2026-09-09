using System.Diagnostics;
using AcDream.App.Streaming;

namespace AcDream.App.Tests.Streaming;

public sealed class StreamingCompletionQueueTests
{
    [Fact]
    public void PrioritySelectionIsStableFifoAndSkipsOnlyBlockedHead()
    {
        var queue = new StreamingCompletionQueue();
        StreamingQueuedCompletion farFirst = Completion(
            new LandblockStreamResult.Failed(1, "far-first"),
            StreamingCompletionPriority.Far,
            sequence: 1);
        StreamingQueuedCompletion nearBlocked = Completion(
            new LandblockStreamResult.Failed(2, "near-blocked"),
            StreamingCompletionPriority.Near,
            sequence: 2);
        StreamingQueuedCompletion nearTail = Completion(
            new LandblockStreamResult.Failed(3, "near-tail"),
            StreamingCompletionPriority.Near,
            sequence: 3);
        StreamingQueuedCompletion destinationFirst = Completion(
            new LandblockStreamResult.Failed(4, "destination-first"),
            StreamingCompletionPriority.Destination,
            sequence: 4);
        StreamingQueuedCompletion destinationTail = Completion(
            new LandblockStreamResult.Failed(5, "destination-tail"),
            StreamingCompletionPriority.Destination,
            sequence: 5);

        queue.Enqueue(farFirst);
        queue.Enqueue(nearBlocked);
        queue.Enqueue(nearTail);
        queue.Enqueue(destinationFirst);
        queue.Enqueue(destinationTail);

        Assert.True(queue.TryPeekNext(
            static _ => false,
            out StreamingQueuedCompletion? selected));
        Assert.Equal(destinationFirst.Sequence, selected?.Sequence);
        Assert.Same(destinationFirst.Result, selected?.Result);
        queue.RemoveHead(destinationFirst);

        Assert.True(queue.TryPeekNext(
            result => ReferenceEquals(result, destinationTail.Result)
                || ReferenceEquals(result, nearBlocked.Result),
            out selected));
        Assert.Equal(farFirst.Sequence, selected?.Sequence);
        Assert.Same(farFirst.Result, selected?.Result);

        Assert.Throws<InvalidOperationException>(() =>
            queue.RemoveHead(nearTail));

        Assert.True(queue.TryPeekNext(
            static _ => false,
            out selected));
        Assert.Equal(destinationTail.Sequence, selected?.Sequence);
        Assert.Same(destinationTail.Result, selected?.Result);
        queue.RemoveHead(destinationTail);
        Assert.True(queue.TryPeekNext(
            static _ => false,
            out selected));
        Assert.Equal(nearBlocked.Sequence, selected?.Sequence);
        Assert.Same(nearBlocked.Result, selected?.Result);
    }

    [Fact]
    public void RetainedBytesAndPriorityFactsTrackExactQueueLifetime()
    {
        var queue = new StreamingCompletionQueue();
        StreamingQueuedCompletion destination = Completion(
            new LandblockStreamResult.Failed(10, "destination"),
            StreamingCompletionPriority.Destination,
            sequence: 1,
            adoptedBytes: 12);
        StreamingQueuedCompletion unload = Completion(
            new LandblockStreamResult.Unloaded(11),
            StreamingCompletionPriority.Unload,
            sequence: 2,
            adoptedBytes: 20);
        StreamingQueuedCompletion near = Completion(
            new LandblockStreamResult.Failed(12, "near"),
            StreamingCompletionPriority.Near,
            sequence: 3,
            adoptedBytes: 30);

        queue.Enqueue(destination);
        queue.Enqueue(unload);
        queue.Enqueue(near);

        StreamingCompletionQueueSnapshot snapshot = queue.CaptureSnapshot();
        Assert.Equal(3, snapshot.Count);
        Assert.Equal(62, snapshot.RetainedCpuBytes);
        Assert.Equal(1, snapshot.Destination);
        Assert.Equal(1, snapshot.Unload);
        Assert.Equal(1, snapshot.Near);
        Assert.True(snapshot.OldestAgeMilliseconds >= 0);

        queue.RemoveHead(destination);
        snapshot = queue.CaptureSnapshot();
        Assert.Equal(2, snapshot.Count);
        Assert.Equal(50, snapshot.RetainedCpuBytes);

        queue.Clear();
        snapshot = queue.CaptureSnapshot();
        Assert.Equal(0, snapshot.Count);
        Assert.Equal(0, snapshot.RetainedCpuBytes);
        Assert.Equal(0, snapshot.OldestAgeMilliseconds);
    }

    [Fact]
    public void PriorityCeilingLeavesNearAndFarWorkRetained()
    {
        var queue = new StreamingCompletionQueue();
        StreamingQueuedCompletion near = Completion(
            new LandblockStreamResult.Failed(20, "near"),
            StreamingCompletionPriority.Near,
            sequence: 1);
        StreamingQueuedCompletion unload = Completion(
            new LandblockStreamResult.Unloaded(21),
            StreamingCompletionPriority.Unload,
            sequence: 2);
        queue.Enqueue(near);
        queue.Enqueue(unload);

        Assert.True(queue.TryPeekNext(
            static _ => false,
            out StreamingQueuedCompletion? selected,
            StreamingCompletionPriority.Unload));
        Assert.Equal(unload.Sequence, selected?.Sequence);
        queue.RemoveHead(unload);

        Assert.False(queue.TryPeekNext(
            static _ => false,
            out selected,
            StreamingCompletionPriority.Unload));
        Assert.Equal(1, queue.Count);
        Assert.True(queue.TryPeekNext(
            static _ => false,
            out selected));
        Assert.Equal(near.Sequence, selected?.Sequence);
    }

    [Fact]
    public void ExactReferenceRemovalDoesNotConflateEqualRecords()
    {
        var queue = new StreamingCompletionQueue();
        var firstResult =
            new LandblockStreamResult.Failed(20, "same", Generation: 7);
        var equalButDistinctResult =
            new LandblockStreamResult.Failed(20, "same", Generation: 7);
        StreamingQueuedCompletion first = Completion(
            firstResult,
            StreamingCompletionPriority.Control,
            sequence: 1,
            adoptedBytes: 11);
        StreamingQueuedCompletion second = Completion(
            equalButDistinctResult,
            StreamingCompletionPriority.Control,
            sequence: 2,
            adoptedBytes: 13);
        queue.Enqueue(first);
        queue.Enqueue(second);

        int removed = queue.RemoveResults(
            [equalButDistinctResult],
            static _ => true);

        Assert.Equal(1, removed);
        Assert.Equal(1, queue.Count);
        Assert.Equal(11, queue.RetainedCpuBytes);
        Assert.True(queue.TryPeekNext(
            static _ => false,
            out StreamingQueuedCompletion? selected));
        Assert.Equal(first.Sequence, selected?.Sequence);
        Assert.Same(first.Result, selected?.Result);
    }

    [Fact]
    public void CallbackAppendNeverInvalidatesActiveHead()
    {
        var queue = new StreamingCompletionQueue();
        StreamingQueuedCompletion active = Completion(
            new LandblockStreamResult.Failed(30, "active"),
            StreamingCompletionPriority.Near,
            sequence: 1);
        StreamingQueuedCompletion appended = Completion(
            new LandblockStreamResult.Failed(31, "appended"),
            StreamingCompletionPriority.Near,
            sequence: 2);
        queue.Enqueue(active);

        Assert.True(queue.TryPeekNext(
            static _ => false,
            out StreamingQueuedCompletion? selected));
        Assert.Equal(active.Sequence, selected?.Sequence);
        Assert.Same(active.Result, selected?.Result);

        queue.Enqueue(appended);
        queue.RemoveHead(active);

        Assert.True(queue.TryPeekNext(
            static _ => false,
            out selected));
        Assert.Equal(appended.Sequence, selected?.Sequence);
        Assert.Same(appended.Result, selected?.Result);
    }

    private static StreamingQueuedCompletion Completion(
        LandblockStreamResult result,
        StreamingCompletionPriority priority,
        long sequence,
        long adoptedBytes = 0)
    {
        var estimate = new LandblockStreamCostEstimate(
            Work: new StreamingWorkCost(
                CompletionAdmissions: 1,
                AdoptedCpuBytes: adoptedBytes),
            TerrainPayloadBytes: 0,
            Entities: 0,
            MeshReferences: 0,
            VisibilityCells: 0,
            EnvCellShells: 0,
            PortalConnections: 0,
            PortalPolygonVertices: 0,
            PhysicsEnvCells: 0,
            PhysicsEnvironments: 0,
            PhysicsSetups: 0,
            PhysicsGfxObjects: 0);
        return new StreamingQueuedCompletion(
            result,
            estimate,
            priority,
            priority == StreamingCompletionPriority.Destination ? 1L : 0L,
            result.Generation,
            sequence,
            Stopwatch.GetTimestamp());
    }
}
