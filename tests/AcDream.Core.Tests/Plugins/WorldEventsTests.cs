using System.Numerics;
using AcDream.Core.Plugins;
using AcDream.Plugin.Abstractions;

namespace AcDream.Core.Tests.Plugins;

public class WorldEventsTests
{
    private static WorldEntitySnapshot S(uint id) => new(id, SourceId: 0x01000000u, Position: Vector3.Zero, Rotation: Quaternion.Identity);

    [Fact]
    public void FireBeforeAnySubscriber_LateSubscribeReceivesReplay()
    {
        var events = new WorldEvents();
        events.FireEntitySpawned(S(1));
        events.FireEntitySpawned(S(2));
        events.FireEntitySpawned(S(3));

        var seen = new List<uint>();
        events.EntitySpawned += e => seen.Add(e.Id);

        Assert.Equal(new uint[] { 1, 2, 3 }, seen);
    }

    [Fact]
    public void FireAfterSubscribe_ReachesSubscriber()
    {
        var events = new WorldEvents();
        var seen = new List<uint>();
        events.EntitySpawned += e => seen.Add(e.Id);

        events.FireEntitySpawned(S(10));
        events.FireEntitySpawned(S(20));

        Assert.Equal(new uint[] { 10, 20 }, seen);
    }

    [Fact]
    public void ReplayPlusLive_DeliversExactlyOnceEach()
    {
        var events = new WorldEvents();
        events.FireEntitySpawned(S(1));  // pre-subscribe

        var seen = new List<uint>();
        events.EntitySpawned += e => seen.Add(e.Id);  // replay fires 1

        events.FireEntitySpawned(S(2));  // live fires 2

        Assert.Equal(new uint[] { 1, 2 }, seen);
    }

    [Fact]
    public void Unsubscribe_StopsLiveDelivery()
    {
        var events = new WorldEvents();
        var seen = new List<uint>();
        Action<WorldEntitySnapshot> handler = e => seen.Add(e.Id);

        events.EntitySpawned += handler;
        events.FireEntitySpawned(S(1));
        events.EntitySpawned -= handler;
        events.FireEntitySpawned(S(2));

        Assert.Equal(new uint[] { 1 }, seen);
    }

    [Fact]
    public void HandlerThrowsDuringReplay_OtherReplayEntriesStillDelivered()
    {
        var events = new WorldEvents();
        events.FireEntitySpawned(S(1));
        events.FireEntitySpawned(S(2));
        events.FireEntitySpawned(S(3));

        var seen = new List<uint>();
        events.EntitySpawned += e =>
        {
            if (e.Id == 2) throw new InvalidOperationException("boom");
            seen.Add(e.Id);
        };

        // No exception propagates out of the += add; 1 and 3 were still delivered.
        Assert.Contains(1u, seen);
        Assert.Contains(3u, seen);
    }

    [Fact]
    public void RehydratedEntity_ReplacesReplaySnapshotInsteadOfAppendingHistory()
    {
        var events = new WorldEvents();
        events.FireEntitySpawned(S(1));
        events.FireEntitySpawned(S(1) with { SourceId = 0x01000001u });

        var seen = new List<WorldEntitySnapshot>();
        events.EntitySpawned += seen.Add;

        WorldEntitySnapshot replay = Assert.Single(seen);
        Assert.Equal(0x01000001u, replay.SourceId);
    }

    [Fact]
    public void ForgottenEntity_IsNotReplayedToLateSubscriber()
    {
        var events = new WorldEvents();
        events.FireEntitySpawned(S(1));
        events.FireEntitySpawned(S(2));

        Assert.True(events.ForgetEntity(1));

        var seen = new List<uint>();
        events.EntitySpawned += e => seen.Add(e.Id);
        Assert.Equal(new uint[] { 2 }, seen);
    }

    [Fact]
    public void ClearCurrent_LeavesNoReplayHistory()
    {
        var events = new WorldEvents();
        events.FireEntitySpawned(S(1));
        events.FireEntitySpawned(S(2));
        events.ClearCurrent();

        var seen = new List<uint>();
        events.EntitySpawned += e => seen.Add(e.Id);
        Assert.Empty(seen);
    }

    [Fact]
    public async Task LiveEventDuringReplay_IsDeliveredAfterSnapshotWithoutStaleReordering()
    {
        var events = new WorldEvents();
        events.FireEntitySpawned(S(1));
        using var replayEntered = new ManualResetEventSlim();
        using var releaseReplay = new ManualResetEventSlim();
        var seen = new List<uint>();
        Action<WorldEntitySnapshot> handler = snapshot =>
        {
            lock (seen)
                seen.Add(snapshot.SourceId);
            if (snapshot.SourceId == 0x01000000u)
            {
                replayEntered.Set();
                releaseReplay.Wait();
            }
        };

        Task subscribe = Task.Run(() => events.EntitySpawned += handler);
        Assert.True(replayEntered.Wait(5_000));
        events.FireEntitySpawned(S(1) with { SourceId = 0x01000001u });
        releaseReplay.Set();
        await subscribe;

        lock (seen)
            Assert.Equal(new uint[] { 0x01000000u, 0x01000001u }, seen);
    }

    [Fact]
    public async Task ClearCurrent_DropsLiveEventsQueuedBehindAnActiveReplay()
    {
        var events = new WorldEvents();
        events.FireEntitySpawned(S(1));
        using var replayEntered = new ManualResetEventSlim();
        using var releaseReplay = new ManualResetEventSlim();
        var seen = new List<uint>();
        Action<WorldEntitySnapshot> handler = snapshot =>
        {
            lock (seen)
                seen.Add(snapshot.Id);
            if (snapshot.Id == 1)
            {
                replayEntered.Set();
                releaseReplay.Wait();
            }
        };

        Task subscribe = Task.Run(() => events.EntitySpawned += handler);
        Assert.True(replayEntered.Wait(5_000));
        events.FireEntitySpawned(S(2));
        events.ClearCurrent();
        releaseReplay.Set();
        await subscribe;

        lock (seen)
            Assert.Equal(new uint[] { 1 }, seen);
    }

    [Fact]
    public void UpsertCurrent_RestoresLateReplayWithoutNotifyingExistingSubscriber()
    {
        var events = new WorldEvents();
        var existing = new List<uint>();
        events.EntitySpawned += snapshot => existing.Add(snapshot.Id);
        events.FireEntitySpawned(S(1));
        events.ForgetEntity(1);

        events.UpsertCurrent(S(1) with { SourceId = 0x01000001u });

        Assert.Equal(new uint[] { 1 }, existing);
        var late = new List<WorldEntitySnapshot>();
        events.EntitySpawned += late.Add;
        Assert.Equal(0x01000001u, Assert.Single(late).SourceId);
    }
}
