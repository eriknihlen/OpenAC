using AcDream.App.Audio;

namespace AcDream.App.Tests.Audio;

public sealed class AlBufferBudgetTrackerTests
{
    [Fact]
    public void RecordCreated_TracksResidentBytesAndCount()
    {
        var tracker = new AlBufferBudgetTracker(maxBytes: 1000);

        tracker.RecordCreated(waveId: 1, bufferId: 101, bytes: 200);
        tracker.RecordCreated(waveId: 2, bufferId: 102, bytes: 300);

        Assert.Equal(500, tracker.ResidentBytes);
        Assert.Equal(2, tracker.Count);
        Assert.True(tracker.TryGetBufferId(1, out uint buf1));
        Assert.Equal(101u, buf1);
        Assert.True(tracker.TryGetBufferId(2, out uint buf2));
        Assert.Equal(102u, buf2);
    }

    [Fact]
    public void TryGetBufferId_UnknownWaveId_ReturnsFalse()
    {
        var tracker = new AlBufferBudgetTracker(maxBytes: 1000);

        Assert.False(tracker.TryGetBufferId(42, out uint bufferId));
        Assert.Equal(0u, bufferId);
    }

    [Fact]
    public void TryEvictOldestUnprotected_PicksLeastRecentlyUsedEntry()
    {
        var tracker = new AlBufferBudgetTracker(maxBytes: 1000);
        tracker.RecordCreated(1, 101, 100); // oldest
        tracker.RecordCreated(2, 102, 100);
        tracker.RecordCreated(3, 103, 100); // newest

        bool evicted = tracker.TryEvictOldestUnprotected(
            static _ => false, out uint waveId, out uint bufferId);

        Assert.True(evicted);
        Assert.Equal(1u, waveId);
        Assert.Equal(101u, bufferId);
        Assert.Equal(200, tracker.ResidentBytes);
        Assert.Equal(2, tracker.Count);
        Assert.False(tracker.TryGetBufferId(1, out _));
    }

    [Fact]
    public void Touch_MovesEntryToMostRecentlyUsed()
    {
        var tracker = new AlBufferBudgetTracker(maxBytes: 1000);
        tracker.RecordCreated(1, 101, 100);
        tracker.RecordCreated(2, 102, 100);
        tracker.RecordCreated(3, 103, 100);

        // Without a touch, wave 1 (buffer 101) is oldest and would be evicted.
        tracker.Touch(1);

        bool evicted = tracker.TryEvictOldestUnprotected(
            static _ => false, out uint waveId, out _);

        Assert.True(evicted);
        Assert.Equal(2u, waveId); // 2 is now the least-recently-used, not 1
    }

    [Fact]
    public void Touch_UnknownWaveId_IsNoOp()
    {
        var tracker = new AlBufferBudgetTracker(maxBytes: 1000);
        tracker.RecordCreated(1, 101, 100);

        tracker.Touch(999);

        Assert.Equal(1, tracker.Count);
    }

    [Fact]
    public void TryEvictOldestUnprotected_SkipsProtectedEntriesAndPicksNextOldest()
    {
        var tracker = new AlBufferBudgetTracker(maxBytes: 1000);
        tracker.RecordCreated(1, 101, 100); // oldest, but protected (attached to a live source)
        tracker.RecordCreated(2, 102, 100); // next-oldest, unprotected — should be picked
        tracker.RecordCreated(3, 103, 100); // newest

        bool evicted = tracker.TryEvictOldestUnprotected(
            bufferId => bufferId == 101, out uint waveId, out uint bufferIdOut);

        Assert.True(evicted);
        Assert.Equal(2u, waveId);
        Assert.Equal(102u, bufferIdOut);
        Assert.True(tracker.TryGetBufferId(1, out _)); // still resident — protected
    }

    [Fact]
    public void TryEvictOldestUnprotected_EverythingProtected_ReturnsFalseAndChangesNothing()
    {
        var tracker = new AlBufferBudgetTracker(maxBytes: 1000);
        tracker.RecordCreated(1, 101, 100);
        tracker.RecordCreated(2, 102, 100);

        bool evicted = tracker.TryEvictOldestUnprotected(
            static _ => true, out uint waveId, out uint bufferId);

        Assert.False(evicted);
        Assert.Equal(0u, waveId);
        Assert.Equal(0u, bufferId);
        Assert.Equal(200, tracker.ResidentBytes);
        Assert.Equal(2, tracker.Count);
    }

    [Fact]
    public void TryEvictOldestUnprotected_EmptyTracker_ReturnsFalse()
    {
        var tracker = new AlBufferBudgetTracker(maxBytes: 1000);

        bool evicted = tracker.TryEvictOldestUnprotected(
            static _ => false, out _, out _);

        Assert.False(evicted);
    }

    [Fact]
    public void RecordCreated_ZeroOrNegativeBytes_IsChargedAtLeastOneByte()
    {
        var tracker = new AlBufferBudgetTracker(maxBytes: 1000);

        tracker.RecordCreated(1, 101, bytes: 0);

        Assert.Equal(1, tracker.ResidentBytes);
    }

    [Fact]
    public void Constructor_RejectsNonPositiveBudget()
    {
        Assert.Throws<ArgumentOutOfRangeException>(() => new AlBufferBudgetTracker(0));
        Assert.Throws<ArgumentOutOfRangeException>(() => new AlBufferBudgetTracker(-1));
    }
}
