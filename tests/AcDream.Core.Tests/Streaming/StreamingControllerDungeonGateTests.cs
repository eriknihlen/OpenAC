using System;
using System.Collections.Generic;
using System.Linq;
using AcDream.App.Streaming;
using AcDream.Core.World;
using Xunit;

namespace AcDream.Core.Tests.Streaming;

public class StreamingControllerDungeonGateTests
{
    private static uint Encode(int x, int y) => ((uint)x << 24) | ((uint)y << 16) | 0xFFFFu;

    private static LoadedLandblock MakeLb(int x, int y) => new LoadedLandblock(
        Encode(x, y),
        Heightmap: null!,
        Entities: Array.Empty<WorldEntity>());

    private sealed record Harness(
        StreamingController Ctrl,
        List<(uint Id, LandblockStreamJobKind Kind)> Loads,
        List<uint> Unloads,
        Func<int> ClearCalls,
        GpuWorldState State);

    private static Harness Make()
    {
        var loads = new List<(uint, LandblockStreamJobKind)>();
        var unloads = new List<uint>();
        int clearCalls = 0;
        var state = new GpuWorldState();
        var ctrl = new StreamingController(
            enqueueLoad: (id, kind) => loads.Add((id, kind)),
            enqueueUnload: unloads.Add,
            drainCompletions: _ => Array.Empty<LandblockStreamResult>(),
            applyTerrain: (_, _) => { },
            state: state,
            nearRadius: 4,
            farRadius: 12,
            clearPendingLoads: () => clearCalls++);
        return new Harness(ctrl, loads, unloads, () => clearCalls, state);
    }

    [Fact]
    public void EntersDungeon_CancelsPending_UnloadsNeighbors_KeepsCenter()
    {
        var h = Make();
        uint center = Encode(0, 7);
        h.State.AddLandblock(MakeLb(0, 7));   // the dungeon landblock
        h.State.AddLandblock(MakeLb(0, 8));   // a neighbor ocean dungeon
        h.State.AddLandblock(MakeLb(1, 7));   // another neighbor

        h.Ctrl.Tick(observerCx: 0, observerCy: 7, insideDungeon: true);

        Assert.Equal(1, h.ClearCalls());
        Assert.Contains(Encode(0, 8), h.Unloads);         // neighbor unloaded
        Assert.Contains(Encode(1, 7), h.Unloads);         // neighbor unloaded
        Assert.DoesNotContain(center, h.Unloads);         // dungeon landblock kept
        Assert.DoesNotContain(h.Loads, l => l.Id == center); // already loaded → no reload
    }

    [Fact]
    public void EntersDungeon_CenterNotLoaded_EnqueuesCenterLoad()
    {
        var h = Make();   // empty state — the dungeon landblock isn't resident yet

        h.Ctrl.Tick(observerCx: 0, observerCy: 7, insideDungeon: true);

        Assert.Equal(1, h.ClearCalls());
        Assert.Contains(h.Loads, l => l.Id == Encode(0, 7)
            && l.Kind == LandblockStreamJobKind.LoadNear);
    }

    [Fact]
    public void StayingCollapsed_SweepsStragglerThatFinishedAfterTheEdge()
    {
        var h = Make();
        h.State.AddLandblock(MakeLb(0, 7));
        h.Ctrl.Tick(0, 7, insideDungeon: true);
        h.Unloads.Clear();

        h.State.AddLandblock(MakeLb(0, 8));
        h.Ctrl.Tick(0, 7, insideDungeon: true);   // sweep

        Assert.Contains(Encode(0, 8), h.Unloads);
        Assert.DoesNotContain(Encode(0, 7), h.Unloads);
    }

    [Fact]
    public void StayingCollapsed_DoesNotReClearOrReloadCenter()
    {
        var h = Make();
        h.State.AddLandblock(MakeLb(0, 7));
        h.Ctrl.Tick(0, 7, insideDungeon: true);
        h.Loads.Clear();

        h.Ctrl.Tick(0, 7, insideDungeon: true);

        Assert.Equal(1, h.ClearCalls());
        Assert.Empty(h.Loads);
    }

    [Fact]
    public void Collapsed_CurrCellFlickersToAdjacentOffByOne_DoesNotExpand()
    {
        var h = Make();
        h.State.AddLandblock(MakeLb(0, 7));
        h.Ctrl.Tick(0, 7, insideDungeon: true);
        h.Loads.Clear();
        h.Unloads.Clear();

        h.Ctrl.Tick(0, 6, insideDungeon: false);  // flicker → adjacent off-by-one

        Assert.Empty(h.Loads);    // NO full-window reload
        Assert.Empty(h.Unloads);  // dungeon (0,7) preserved; nothing else resident
    }

    [Fact]
    public void ExitsDungeon_RebuildsFullWindow_UnloadsStaleDungeonLandblock()
    {
        var h = Make();
        h.State.AddLandblock(MakeLb(0, 7));
        h.Ctrl.Tick(0, 7, insideDungeon: true);
        h.Loads.Clear();
        h.Unloads.Clear();

        // Exit through a portal to an outdoor location far from the dungeon block.
        h.Ctrl.Tick(observerCx: 100, observerCy: 100, insideDungeon: false);

        Assert.Contains(h.Loads, l => l.Kind == LandblockStreamJobKind.LoadNear);
        Assert.Contains(h.Loads, l => l.Kind == LandblockStreamJobKind.LoadFar);
        Assert.Contains(Encode(0, 7), h.Unloads); // stale dungeon block, outside new window
    }

    [Fact]
    public void PreCollapse_BeforeAnyTick_LoadsOnlyDungeon_NeverBootstrapsWindow()
    {
        var h = Make();   // empty state — nothing resident, _region is null

        h.Ctrl.PreCollapseToDungeon(0, 7);

        Assert.Single(h.Loads);                                  // exactly one load
        Assert.Equal(Encode(0, 7), h.Loads[0].Id);              // the dungeon landblock
        Assert.Equal(LandblockStreamJobKind.LoadNear, h.Loads[0].Kind);
        Assert.DoesNotContain(h.Loads, l => l.Kind == LandblockStreamJobKind.LoadFar);
    }

    [Fact]
    public void PreCollapse_CenterResidentOnlyAsFarTier_EnqueuesPromotion()
    {
        var h = Make();
        uint center = Encode(0, 7);
        h.State.AddLandblock(MakeLb(0, 7), tier: LandblockStreamTier.Far);

        h.Ctrl.PreCollapseToDungeon(0, 7);

        Assert.False(h.State.IsNearTier(center));
        Assert.Contains(h.Loads, load =>
            load.Id == center && load.Kind == LandblockStreamJobKind.PromoteToNear);
        Assert.DoesNotContain(h.Loads, load =>
            load.Id == center && load.Kind == LandblockStreamJobKind.LoadNear);
    }

    [Fact]
    public void InitializeKnownLoginCenter_DungeonPerformsOneCollapseWithoutReloadChurn()
    {
        var h = Make();

        h.Ctrl.InitializeKnownLoginCenter(0, 7, isSealedDungeon: true);
        h.Ctrl.InitializeKnownLoginCenter(0, 7, isSealedDungeon: true);

        Assert.Single(h.Loads);
        Assert.Equal(Encode(0, 7), h.Loads[0].Id);
        Assert.Equal(1, h.ClearCalls());
    }

    [Fact]
    public void InitializeKnownLoginCenter_OutdoorWaitsForFirstCorrectlyCenteredTick()
    {
        var h = Make();

        h.Ctrl.InitializeKnownLoginCenter(140, 4, isSealedDungeon: false);

        Assert.Empty(h.Loads);
        Assert.Equal(0, h.ClearCalls());

        h.Ctrl.Tick(140, 4, insideDungeon: false);
        Assert.Contains(h.Loads, load => load.Id == Encode(140, 4));
    }

    [Fact]
    public void PreCollapse_AfterBootstrapTick_CancelsWindow_UnloadsResidentNeighbors_KeepsDungeon()
    {
        var h = Make();

        h.Ctrl.Tick(0, 7, insideDungeon: false);   // frame 1: NormalTick bootstraps the window
        Assert.True(h.Loads.Count > 1);            // the full window was enqueued

        h.State.AddLandblock(MakeLb(0, 7));        // the dungeon landblock itself
        h.State.AddLandblock(MakeLb(0, 8));        // a neighbor ocean dungeon that loaded
        h.State.AddLandblock(MakeLb(1, 7));        // another neighbor
        h.Loads.Clear();
        h.Unloads.Clear();

        h.Ctrl.PreCollapseToDungeon(0, 7);

        Assert.Equal(1, h.ClearCalls());
        Assert.Contains(Encode(0, 8), h.Unloads);        // resident neighbor unloaded
        Assert.Contains(Encode(1, 7), h.Unloads);
        Assert.DoesNotContain(Encode(0, 7), h.Unloads);  // dungeon landblock kept
    }

    [Fact]
    public void PreCollapse_ThenHoldTicksWithStaleObserver_StaysCollapsed()
    {
        var h = Make();
        h.Ctrl.PreCollapseToDungeon(0, 7);
        h.Loads.Clear();
        h.Unloads.Clear();

        h.Ctrl.Tick(0, 7, insideDungeon: false);   // hold frame: not placed yet

        Assert.Empty(h.Loads);     // no neighbor window
        Assert.Empty(h.Unloads);
    }

    [Fact]
    public void PreCollapse_IsIdempotent_OnSameLandblock()
    {
        var h = Make();
        h.Ctrl.PreCollapseToDungeon(0, 7);
        h.Loads.Clear();

        h.Ctrl.PreCollapseToDungeon(0, 7);

        Assert.Equal(1, h.ClearCalls());
        Assert.Empty(h.Loads);             // no second dungeon load
    }

    [Fact]
    public void PreCollapse_ThenPlaced_InsideDungeonTick_StaysCollapsed()
    {
        var h = Make();
        h.State.AddLandblock(MakeLb(0, 7));        // dungeon landblock finished loading
        h.Ctrl.PreCollapseToDungeon(0, 7);
        h.Loads.Clear();
        h.Unloads.Clear();

        h.Ctrl.Tick(0, 7, insideDungeon: true);    // placed: gate now fires

        Assert.Equal(1, h.ClearCalls());
        Assert.Empty(h.Loads);
        Assert.DoesNotContain(Encode(0, 7), h.Unloads);
    }

    [Fact]
    public void NormalOutdoorTick_Unchanged_NoCollapseNoClear()
    {
        var h = Make();

        h.Ctrl.Tick(observerCx: 100, observerCy: 100);   // default insideDungeon: false

        Assert.Equal(0, h.ClearCalls());
        Assert.Empty(h.Unloads);
        // 9 near (9×9? no — nearRadius 4 → 9×9=81) + far ring loads enqueued.
        Assert.Contains(h.Loads, l => l.Kind == LandblockStreamJobKind.LoadNear);
        Assert.Contains(h.Loads, l => l.Kind == LandblockStreamJobKind.LoadFar);
    }
}
