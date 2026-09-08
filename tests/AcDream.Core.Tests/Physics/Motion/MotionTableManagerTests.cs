using System.Collections.Generic;
using System.Linq;
using System.Numerics;
using AcDream.Core.Physics;
using AcDream.Core.Physics.Motion;
using DatReaderWriter.DBObjs;
using DatReaderWriter.Types;
using Xunit;

using DRWMotionCommand = DatReaderWriter.Enums.MotionCommand;

namespace AcDream.Core.Tests.Physics.Motion;


file static class Fixtures
{
    public static Animation MakeAnim(int numFrames, int numParts, Vector3 origin, Quaternion orientation)
    {
        var anim = new Animation();
        for (int f = 0; f < numFrames; f++)
        {
            var pf = new AnimationFrame((uint)numParts);
            for (int p = 0; p < numParts; p++)
                pf.Frames.Add(new Frame { Origin = origin, Orientation = orientation });
            anim.PartFrames.Add(pf);
        }
        return anim;
    }

    public static AnimData MakeAnimData(uint animId, float framerate)
        => new()
        {
            AnimId = (QualifiedDataId<Animation>)animId,
            LowFrame = 0,
            HighFrame = -1,
            Framerate = framerate,
        };

    public static MotionData MakeMotionData(uint animId, float framerate, byte bitfield = 0)
    {
        var md = new MotionData { Bitfield = bitfield };
        md.Anims.Add(MakeAnimData(animId, framerate));
        return md;
    }

    public static void AddLink(MotionTable mt, uint style, uint fromSubstate, uint toSubstate, MotionData data)
    {
        int outerKey = (int)((style << 16) | (fromSubstate & 0xFFFFFFu));
        if (!mt.Links.TryGetValue(outerKey, out var cmd))
        {
            cmd = new MotionCommandData();
            mt.Links[outerKey] = cmd;
        }
        cmd.MotionData[(int)toSubstate] = data;
    }

    public static void AddCycle(MotionTable mt, uint style, uint substate, MotionData data)
    {
        int key = (int)((style << 16) | (substate & 0xFFFFFFu));
        mt.Cycles[key] = data;
    }
}

internal sealed class RecordingSink : IMotionDoneSink
{
    public readonly List<(uint Motion, bool Success)> Calls = new();
    public void MotionDone(uint motion, bool success) => Calls.Add((motion, success));
}

file sealed class NullLoader : IAnimationLoader
{
    public Animation? LoadAnimation(uint id) => null;
}

file sealed class FakeLoader : IAnimationLoader
{
    private readonly Dictionary<uint, Animation> _anims = new();
    public void Register(uint id, Animation anim) => _anims[id] = anim;
    public Animation? LoadAnimation(uint id) => _anims.TryGetValue(id, out var a) ? a : null;
}

public sealed class MotionTableManagerTests
{
    private const uint Ready = 0x41000003u;
    private const uint WalkForward = 0x45000005u;
    private const uint RunForward = 0x44000007u;
    private const uint ThrustMed = 0x10000058u;     // action only
    private const uint NonCombatStyle = 0x8000003Du; // style, top bit set
    private const uint HandCombatStyle = 0x8000003Cu; // style, top bit set

    private static (MotionTableManager mgr, RecordingSink sink, MotionState state, CSequence seq) MakeManager(CMotionTable? table = null)
    {
        var sink = new RecordingSink();
        var state = new MotionState();
        var seq = new CSequence(new NullLoader());
        var mgr = new MotionTableManager(table, state, seq, sink);
        return (mgr, sink, state, seq);
    }

    // ── add_to_queue / basic queueing ───────────────────────────────────

    [Fact]
    public void AddToQueue_AppendsNode_InOrder()
    {
        var (mgr, _, _, _) = MakeManager();

        mgr.AddToQueue(WalkForward, 3);
        mgr.AddToQueue(RunForward, 5);

        var list = mgr.PendingAnimations.ToList();
        Assert.Equal(2, list.Count);
        Assert.Equal(WalkForward, list[0].Motion);
        Assert.Equal(3u, list[0].NumAnims);
        Assert.Equal(RunForward, list[1].Motion);
        Assert.Equal(5u, list[1].NumAnims);
    }


    [Fact]
    public void AnimationDone_CounterRollover_CompletesMultipleQueuedEntries_InOneCall()
    {
        var (mgr, sink, _, _) = MakeManager();
        mgr.AddToQueue(WalkForward, 1);
        mgr.AddToQueue(RunForward, 0);

        mgr.AnimationDone(true);

        Assert.Equal(2, sink.Calls.Count);
        Assert.Equal((WalkForward, true), sink.Calls[0]);
        Assert.Equal((RunForward, true), sink.Calls[1]);
        Assert.Empty(mgr.PendingAnimations);
        Assert.Equal(0, mgr.AnimationCounter);
    }

    [Fact]
    public void AnimationDone_MultiTickCountdown_PopsOnlyWhenCounterReachesDuration()
    {
        var (mgr, sink, _, _) = MakeManager();
        mgr.AddToQueue(WalkForward, 3);

        mgr.AnimationDone(true);
        Assert.Empty(sink.Calls);
        Assert.Single(mgr.PendingAnimations);
        Assert.Equal(1, mgr.AnimationCounter);

        mgr.AnimationDone(true);
        Assert.Empty(sink.Calls);
        Assert.Equal(2, mgr.AnimationCounter);

        mgr.AnimationDone(true);
        Assert.Single(sink.Calls);
        Assert.Equal((WalkForward, true), sink.Calls[0]);
        Assert.Empty(mgr.PendingAnimations);
        Assert.Equal(0, mgr.AnimationCounter);
    }

    [Fact]
    public void AnimationDone_LeftoverCounter_ResetOnlyWhenListFullyDrains()
    {
        var (mgr, sink, _, _) = MakeManager();
        mgr.AddToQueue(WalkForward, 1);
        mgr.AddToQueue(RunForward, 5);

        mgr.AnimationDone(true);

        Assert.Single(sink.Calls);
        Assert.Equal((WalkForward, true), sink.Calls[0]);
        Assert.Single(mgr.PendingAnimations);
        Assert.Equal(RunForward, mgr.PendingAnimations.First().Motion);
        Assert.Equal(0, mgr.AnimationCounter);
    }

    [Fact]
    public void AnimationDone_EmptyQueue_IsNoOp()
    {
        var (mgr, sink, _, _) = MakeManager();

        mgr.AnimationDone(true);

        Assert.Empty(sink.Calls);
        Assert.Equal(0, mgr.AnimationCounter);
    }


    [Fact]
    public void CheckForCompletedMotions_PopsOnlyZeroTickHeads_NeverIncrementsCounter()
    {
        var (mgr, sink, _, _) = MakeManager();
        mgr.AddToQueue(WalkForward, 0); // zero-tick: pops immediately
        mgr.AddToQueue(RunForward, 2);  // non-zero: stays

        mgr.CheckForCompletedMotions();

        Assert.Single(sink.Calls);
        Assert.Equal((WalkForward, true), sink.Calls[0]); // success hardcoded true
        Assert.Single(mgr.PendingAnimations);
        Assert.Equal(RunForward, mgr.PendingAnimations.First().Motion);
        Assert.Equal(0, mgr.AnimationCounter); // untouched
    }

    [Fact]
    public void CheckForCompletedMotions_NonZeroHead_DoesNotPop()
    {
        var (mgr, sink, _, _) = MakeManager();
        mgr.AddToQueue(WalkForward, 4);

        mgr.CheckForCompletedMotions();

        Assert.Empty(sink.Calls);
        Assert.Single(mgr.PendingAnimations);
    }

    [Fact]
    public void UseTime_IsAliasFor_CheckForCompletedMotions()
    {
        var (mgr, sink, _, _) = MakeManager();
        mgr.AddToQueue(WalkForward, 0);

        mgr.UseTime();

        Assert.Single(sink.Calls);
        Assert.Equal((WalkForward, true), sink.Calls[0]);
    }

    [Fact]
    public void AnimationDone_Distinguished_FromCheckForCompletedMotions_ByCounterUse()
    {
        var (mgrA, sinkA, _, _) = MakeManager();
        mgrA.AddToQueue(WalkForward, 1);
        mgrA.CheckForCompletedMotions();
        Assert.Empty(sinkA.Calls);
        Assert.Single(mgrA.PendingAnimations);

        var (mgrB, sinkB, _, _) = MakeManager();
        mgrB.AddToQueue(WalkForward, 1);
        mgrB.AnimationDone(true);
        Assert.Single(sinkB.Calls);
        Assert.Empty(mgrB.PendingAnimations);
    }


    [Fact]
    public void AnimationDone_ActionClassMotion_PopsActionHead()
    {
        var (mgr, sink, state, _) = MakeManager();
        state.AddAction(ThrustMed, 1f);
        Assert.Single(state.Actions);

        mgr.AddToQueue(ThrustMed, 0); // zero-duration -> pops on first tick

        mgr.AnimationDone(true);

        Assert.Empty(state.Actions); // popped
        Assert.Single(sink.Calls);
        Assert.Equal((ThrustMed, true), sink.Calls[0]);
    }

    [Fact]
    public void CheckForCompletedMotions_ActionClassMotion_PopsActionHead()
    {
        var (mgr, sink, state, _) = MakeManager();
        state.AddAction(ThrustMed, 1f);

        mgr.AddToQueue(ThrustMed, 0);
        mgr.CheckForCompletedMotions();

        Assert.Empty(state.Actions);
        Assert.Single(sink.Calls);
    }

    [Fact]
    public void AnimationDone_NonActionClassMotion_DoesNotTouchActionFifo()
    {
        var (mgr, _, state, _) = MakeManager();
        state.AddAction(ThrustMed, 1f);

        mgr.AddToQueue(WalkForward, 0);
        mgr.AnimationDone(true);

        Assert.Single(state.Actions); // untouched
    }

    // ── enter/exit-world drains fire MotionDone(success=false) ──────────

    [Fact]
    public void HandleExitWorld_DrainsEntireQueue_FiringMotionDoneFalse_ForEveryEntry()
    {
        var (mgr, sink, _, _) = MakeManager();
        mgr.AddToQueue(WalkForward, 0);
        mgr.AddToQueue(RunForward, 0);
        mgr.AddToQueue(ThrustMed, 0);

        mgr.HandleExitWorld();

        Assert.Equal(3, sink.Calls.Count);
        Assert.All(sink.Calls, c => Assert.False(c.Success));
        Assert.Empty(mgr.PendingAnimations);
    }

    [Fact]
    public void HandleExitWorld_NonZeroDurationEntries_StillFullyDrained()
    {
        var (mgr, sink, _, _) = MakeManager();
        mgr.AddToQueue(WalkForward, 3);

        mgr.HandleExitWorld();

        Assert.Single(sink.Calls);
        Assert.Equal((WalkForward, false), sink.Calls[0]);
        Assert.Empty(mgr.PendingAnimations);
    }

    [Fact]
    public void HandleEnterWorld_DrainsQueue_AndStripsLinkAnimations()
    {
        var loader = new FakeLoader();
        uint linkAnim = 0x03000200u, cycleAnim = 0x03000201u;
        loader.Register(linkAnim, Fixtures.MakeAnim(2, 1, Vector3.Zero, Quaternion.Identity));
        loader.Register(cycleAnim, Fixtures.MakeAnim(4, 1, Vector3.Zero, Quaternion.Identity));

        var sink = new RecordingSink();
        var state = new MotionState { Style = NonCombatStyle, Substate = Ready, SubstateMod = 1f };
        var seq = new CSequence(loader);
        CMotionTable.AddMotion(seq, Fixtures.MakeMotionData(linkAnim, 30f), 1f);
        CMotionTable.AddMotion(seq, Fixtures.MakeMotionData(cycleAnim, 30f), 1f);
        Assert.Equal(2, seq.Count);

        var mgr = new MotionTableManager(null, state, seq, sink);
        mgr.AddToQueue(WalkForward, 0);

        mgr.HandleEnterWorld();

        // Queue drained, MotionDone(false) fired.
        Assert.Single(sink.Calls);
        Assert.False(sink.Calls[0].Success);
        Assert.Empty(mgr.PendingAnimations);
        Assert.Equal(1, seq.Count);
    }

    [Fact]
    public void HandleEnterWorld_EmptyQueue_StillStripsLinkAnimations_NoOpOnDrain()
    {
        var loader = new FakeLoader();
        uint linkAnim = 0x03000210u, cycleAnim = 0x03000211u;
        loader.Register(linkAnim, Fixtures.MakeAnim(2, 1, Vector3.Zero, Quaternion.Identity));
        loader.Register(cycleAnim, Fixtures.MakeAnim(4, 1, Vector3.Zero, Quaternion.Identity));

        var sink = new RecordingSink();
        var state = new MotionState();
        var seq = new CSequence(loader);
        CMotionTable.AddMotion(seq, Fixtures.MakeMotionData(linkAnim, 30f), 1f);
        CMotionTable.AddMotion(seq, Fixtures.MakeMotionData(cycleAnim, 30f), 1f);

        var mgr = new MotionTableManager(null, state, seq, sink);

        mgr.HandleEnterWorld();

        Assert.Empty(sink.Calls);
        Assert.Equal(1, seq.Count);
    }


    [Fact]
    public void RemoveRedundantLinks_CycleClassTail_EarlierSameMotion_NoBlocker_Truncates()
    {
        var (mgr, _, _, _) = MakeManager();
        mgr.AddToQueue(WalkForward, 5); // earlier same-motion, non-zero
        mgr.AddToQueue(WalkForward, 3); // new tail, same motion -> matches the
                                          // earlier node immediately (no
                                          // intervening node to block) ->
                                          // truncate walks tail_..matched
                                          // EXCLUSIVE, so the TAIL node itself
                                          // (this new entry) is in the walked
                                          // range and gets zeroed; the earlier
                                          // matched node (the stop boundary)
                                          // is untouched.

        var list = mgr.PendingAnimations.ToList();
        Assert.Equal(2, list.Count);
        Assert.Equal(5u, list[0].NumAnims); // matched node (stop boundary) untouched
        Assert.Equal(0u, list[1].NumAnims);
    }

    [Fact]
    public void RemoveRedundantLinks_CycleClassTail_WithInterveningZeroTickNode_Truncates()
    {
        var (mgr, _, _, _) = MakeManager();
        mgr.AddToQueue(WalkForward, 5);       // earlier match
        mgr.AddToQueue(ThrustMed, 0);          // intervening, ZERO ticks -> does not block (only non-zero blocks)
        mgr.AddToQueue(WalkForward, 3);        // new tail, same motion

        var list = mgr.PendingAnimations.ToList();
        Assert.Equal(3, list.Count);
        Assert.Equal(5u, list[0].NumAnims);   // matched node (stop boundary) untouched
        Assert.Equal(0u, list[1].NumAnims);   // already zero (unaffected either way)
        Assert.Equal(0u, list[2].NumAnims);   // the new tail — ALSO in the truncation range
                                                // (truncate walks tail..matched EXCLUSIVE, so
                                                // the tail itself IS zeroed too)
    }

    [Fact]
    public void RemoveRedundantLinks_CycleClassTail_InterveningDifferentCycle_NotBlocked_Truncates()
    {
        var (mgr, _, _, _) = MakeManager();
        mgr.AddToQueue(WalkForward, 5);
        mgr.AddToQueue(RunForward, 4);
        mgr.AddToQueue(WalkForward, 3);   // new tail, same motion as the earlier node

        var list = mgr.PendingAnimations.ToList();
        Assert.Equal(3, list.Count);
        Assert.Equal(5u, list[0].NumAnims);   // matched (stop boundary), untouched
        Assert.Equal(0u, list[1].NumAnims);   // truncated — RunForward zeroed
        Assert.Equal(0u, list[2].NumAnims);   // truncated — the new tail itself zeroed
    }

    [Fact]
    public void RemoveRedundantLinks_CycleClassTail_BlockedByInterveningNonZeroModifierClassNode()
    {
        var (mgr, _, _, _) = MakeManager();
        const uint pureModifier = 0x20000099u;
        mgr.AddToQueue(WalkForward, 5);
        mgr.AddToQueue(pureModifier, 2);
        mgr.AddToQueue(WalkForward, 3);

        var list = mgr.PendingAnimations.ToList();
        Assert.Equal(3, list.Count);
        // Blocked -> nothing truncated, all NumAnims preserved as queued.
        Assert.Equal(5u, list[0].NumAnims);
        Assert.Equal(2u, list[1].NumAnims);
        Assert.Equal(3u, list[2].NumAnims);
    }

    [Fact]
    public void RemoveRedundantLinks_CycleClassTail_BlockedByInterveningNonZeroActionClassNode()
    {
        var (mgr, _, _, _) = MakeManager();
        mgr.AddToQueue(WalkForward, 5);
        mgr.AddToQueue(ThrustMed, 2);
        mgr.AddToQueue(WalkForward, 3);

        var list = mgr.PendingAnimations.ToList();
        Assert.Equal(3, list.Count);
        Assert.Equal(5u, list[0].NumAnims);
        Assert.Equal(2u, list[1].NumAnims); // untouched — blocked
        Assert.Equal(3u, list[2].NumAnims); // untouched — blocked
    }

    [Fact]
    public void RemoveRedundantLinks_CycleClassTail_BlockedByInterveningNonZeroStyleClassNode()
    {
        var (mgr, _, _, _) = MakeManager();
        mgr.AddToQueue(WalkForward, 5);
        mgr.AddToQueue(HandCombatStyle, 2);
        mgr.AddToQueue(WalkForward, 3);

        var list = mgr.PendingAnimations.ToList();
        Assert.Equal(3, list.Count);
        Assert.Equal(5u, list[0].NumAnims);
        Assert.Equal(2u, list[1].NumAnims);
        Assert.Equal(3u, list[2].NumAnims);
    }

    [Fact]
    public void RemoveRedundantLinks_StyleClassTail_EarlierSameMotion_Truncates()
    {
        var (mgr, _, _, _) = MakeManager();
        mgr.AddToQueue(HandCombatStyle, 5);
        mgr.AddToQueue(ThrustMed, 0);          // zero-tick intervening, no block
        mgr.AddToQueue(HandCombatStyle, 3);    // new tail, same style motion

        var list = mgr.PendingAnimations.ToList();
        Assert.Equal(3, list.Count);
        Assert.Equal(5u, list[0].NumAnims);
        Assert.Equal(0u, list[1].NumAnims);
        Assert.Equal(0u, list[2].NumAnims);
    }

    [Fact]
    public void RemoveRedundantLinks_StyleClassTail_BlockedByInterveningNonZeroModifierClassNode()
    {
        var (mgr, _, _, _) = MakeManager();
        const uint pureModifier = 0x20000099u;
        mgr.AddToQueue(HandCombatStyle, 5);
        mgr.AddToQueue(pureModifier, 2);
        mgr.AddToQueue(HandCombatStyle, 3);

        var list = mgr.PendingAnimations.ToList();
        Assert.Equal(3, list.Count);
        Assert.Equal(5u, list[0].NumAnims);
        Assert.Equal(2u, list[1].NumAnims); // untouched — blocked
        Assert.Equal(3u, list[2].NumAnims); // untouched — blocked
    }

    [Fact]
    public void RemoveRedundantLinks_StyleClassTail_BlockedByInterveningNonZeroCycleClassNode()
    {
        var (mgr, _, _, _) = MakeManager();
        mgr.AddToQueue(HandCombatStyle, 5);
        mgr.AddToQueue(WalkForward, 4);
        mgr.AddToQueue(HandCombatStyle, 3);

        var list = mgr.PendingAnimations.ToList();
        Assert.Equal(3, list.Count);
        Assert.Equal(5u, list[0].NumAnims);
        Assert.Equal(4u, list[1].NumAnims);
        Assert.Equal(3u, list[2].NumAnims);
    }

    [Fact]
    public void RemoveRedundantLinks_NoEarlierMatch_ScanExhausts_NoTruncation()
    {
        var (mgr, _, _, _) = MakeManager();
        mgr.AddToQueue(RunForward, 4);
        mgr.AddToQueue(WalkForward, 3); // no earlier WalkForward anywhere

        var list = mgr.PendingAnimations.ToList();
        Assert.Equal(2, list.Count);
        Assert.Equal(4u, list[0].NumAnims);
        Assert.Equal(3u, list[1].NumAnims);
    }

    [Fact]
    public void RemoveRedundantLinks_TrailingZeroTickNodesSkipped_BeforeScanning()
    {
        var (mgr, _, _, _) = MakeManager();
        mgr.AddToQueue(WalkForward, 5);
        mgr.AddToQueue(WalkForward, 0); // trailing zero-tick — skipped as "effective tail" search

        var list = mgr.PendingAnimations.ToList();
        // Effective tail (after skip) = list[0] (WalkForward, 5) itself —
        // scanning backward from it finds nothing earlier -> no truncation.
        Assert.Equal(2, list.Count);
        Assert.Equal(5u, list[0].NumAnims);
        Assert.Equal(0u, list[1].NumAnims);
    }

    [Fact]
    public void RemoveRedundantLinks_AllZeroTickNodes_ScanBottomsOut_ReturnsQuietly()
    {
        var (mgr, _, _, _) = MakeManager();
        mgr.AddToQueue(WalkForward, 0);
        mgr.AddToQueue(RunForward, 0);

        // Must not throw; nothing to truncate.
        mgr.RemoveRedundantLinks();

        var list = mgr.PendingAnimations.ToList();
        Assert.Equal(2, list.Count);
        Assert.Equal(0u, list[0].NumAnims);
        Assert.Equal(0u, list[1].NumAnims);
    }

    [Fact]
    public void RemoveRedundantLinks_EmptyQueue_IsNoOp()
    {
        var (mgr, _, _, _) = MakeManager();
        mgr.RemoveRedundantLinks(); // must not throw
        Assert.Empty(mgr.PendingAnimations);
    }

    [Fact]
    public void RemoveRedundantLinks_ModifierOrActionClassTail_NoScanPerformed()
    {
        var (mgr, _, _, _) = MakeManager();
        const uint pureModifier = 0x20000099u;
        mgr.AddToQueue(pureModifier, 5);
        mgr.AddToQueue(pureModifier, 3);

        var list = mgr.PendingAnimations.ToList();
        Assert.Equal(2, list.Count);
        Assert.Equal(5u, list[0].NumAnims);
        Assert.Equal(3u, list[1].NumAnims);
    }

    // ── truncate_animation_list: zeroes in place, nodes stay queued ─────

    [Fact]
    public void Truncate_ViaRedundantCollapse_NodesStayQueued_OnlyNumAnimsZeroed()
    {
        var (mgr, _, _, _) = MakeManager();
        mgr.AddToQueue(WalkForward, 5);
        mgr.AddToQueue(RunForward, 2); // will get zeroed but not removed
        mgr.AddToQueue(WalkForward, 3);

        var list = mgr.PendingAnimations.ToList();
        Assert.Equal(3, list.Count);
        Assert.Equal(RunForward, list[1].Motion); // motion id preserved, only NumAnims zeroed
        Assert.Equal(0u, list[1].NumAnims);
    }

    [Fact]
    public void Truncate_StripsLinkAnimationsFromSequence_ByTotalRemovedTicks()
    {
        var loader = new FakeLoader();
        uint link1 = 0x03000300u, link2 = 0x03000301u, cyc = 0x03000302u;
        loader.Register(link1, Fixtures.MakeAnim(1, 1, Vector3.Zero, Quaternion.Identity));
        loader.Register(link2, Fixtures.MakeAnim(1, 1, Vector3.Zero, Quaternion.Identity));
        loader.Register(cyc, Fixtures.MakeAnim(1, 1, Vector3.Zero, Quaternion.Identity));

        var sink = new RecordingSink();
        var state = new MotionState();
        var seq = new CSequence(loader);
        CMotionTable.AddMotion(seq, Fixtures.MakeMotionData(link1, 30f), 1f);
        CMotionTable.AddMotion(seq, Fixtures.MakeMotionData(link2, 30f), 1f);
        CMotionTable.AddMotion(seq, Fixtures.MakeMotionData(cyc, 30f), 1f);
        Assert.Equal(3, seq.Count);

        var mgr = new MotionTableManager(null, state, seq, sink);
        mgr.AddToQueue(WalkForward, 2); // earlier match, non-zero
        mgr.AddToQueue(WalkForward, 2); // new tail, same motion -> truncates the earlier
                                          // one's successor range (which is just this
                                          // node itself, ticks=2) -> RemoveLinkAnimations(2)

        // RemoveLinkAnimations(2) removes up to 2 predecessors of first_cyclic
        // (link2, then link1) from the live sequence.
        Assert.Equal(1, seq.Count);
    }


    [Fact]
    public void RapidSameMotionReissue_ThroughAddToQueue_CollapsesRedundantMiddleNodes()
    {
        var (mgr, _, _, _) = MakeManager();
        mgr.AddToQueue(WalkForward, 5);
        mgr.AddToQueue(RunForward, 4);
        mgr.AddToQueue(WalkForward, 3);
        mgr.AddToQueue(RunForward, 2);

        var list = mgr.PendingAnimations.ToList();
        Assert.Equal(4, list.Count);
        Assert.Equal(5u, list[0].NumAnims); // original WalkForward — the surviving anchor
        Assert.Equal(0u, list[1].NumAnims);
        Assert.Equal(0u, list[2].NumAnims);
        Assert.Equal(2u, list[3].NumAnims); // newest RunForward — survives (its own scan found no match)
    }

    [Fact]
    public void RapidSameMotionReissue_ZeroTickIntervening_DoesCollapse()
    {
        var (mgr, _, _, _) = MakeManager();
        mgr.AddToQueue(WalkForward, 5);
        mgr.AddToQueue(WalkForward, 0); // e.g. a fast-path re-speed, zero outTicks
        mgr.AddToQueue(WalkForward, 3); // new tail, same motion

        var list = mgr.PendingAnimations.ToList();
        Assert.Equal(3, list.Count);
        Assert.Equal(5u, list[0].NumAnims); // matched node preserved
        Assert.Equal(0u, list[1].NumAnims); // already zero
        Assert.Equal(0u, list[2].NumAnims); // truncated
    }


    [Fact]
    public void Golden_CyclicToCyclicWalkThenRun_BothNodesQueued_TruncateNotFiring()
    {
        var (mgr, _, _, _) = MakeManager();
        mgr.AddToQueue(WalkForward, 3);
        mgr.AddToQueue(RunForward, 4);

        var list = mgr.PendingAnimations.ToList();
        Assert.Equal(2, list.Count);
        Assert.Equal(WalkForward, list[0].Motion);
        Assert.Equal(3u, list[0].NumAnims); // untouched — no truncation
        Assert.Equal(RunForward, list[1].Motion);
        Assert.Equal(4u, list[1].NumAnims); // untouched
    }

    // ── initialize_state ──────────────────────────────────────────────

    [Fact]
    public void InitializeState_QueuesReadySentinel_WithSetDefaultStateOutTicks()
    {
        var loader = new FakeLoader();
        uint readyAnim = 0x03000400u;
        loader.Register(readyAnim, Fixtures.MakeAnim(4, 1, Vector3.Zero, Quaternion.Identity));

        var mt = new MotionTable { DefaultStyle = (DRWMotionCommand)NonCombatStyle };
        mt.StyleDefaults[(DRWMotionCommand)NonCombatStyle] = (DRWMotionCommand)Ready;
        Fixtures.AddCycle(mt, NonCombatStyle, Ready, Fixtures.MakeMotionData(readyAnim, 30f));

        var cmt = new CMotionTable(mt);
        var sink = new RecordingSink();
        var state = new MotionState();
        var seq = new CSequence(loader);
        var mgr = new MotionTableManager(cmt, state, seq, sink);

        mgr.InitializeState();

        Assert.Equal(NonCombatStyle, state.Style);
        Assert.Equal(Ready, state.Substate);
        Assert.Single(mgr.PendingAnimations);
        var node = mgr.PendingAnimations.First();
        Assert.Equal(MotionTableManager.ReadySentinel, node.Motion);
        Assert.Equal(0u, node.NumAnims);
    }

    [Fact]
    public void InitializeState_NoTableLoaded_QueuesSentinelWithZeroTicks_NoThrow()
    {
        var sink = new RecordingSink();
        var state = new MotionState();
        var seq = new CSequence(new NullLoader());
        var mgr = new MotionTableManager(null, state, seq, sink);

        mgr.InitializeState();

        Assert.Single(mgr.PendingAnimations);
        Assert.Equal(MotionTableManager.ReadySentinel, mgr.PendingAnimations.First().Motion);
        Assert.Equal(0u, mgr.PendingAnimations.First().NumAnims);
    }


    [Fact]
    public void PerformMovement_NoTableLoaded_ReturnsError7()
    {
        var (mgr, _, _, _) = MakeManager(table: null);

        uint result = mgr.PerformMovement(MotionTableMovement.Interpreted(WalkForward, 1f));

        Assert.Equal(MotionTableManagerError.NoTable, result);
        Assert.Equal(7u, result);
    }

    [Fact]
    public void PerformMovement_InterpretedCommand_Success_QueuesMotionWithOutTicks_ReturnsZero()
    {
        var loader = new FakeLoader();
        uint readyAnim = 0x03000500u, walkAnim = 0x03000501u, linkAnim = 0x03000502u;
        loader.Register(readyAnim, Fixtures.MakeAnim(4, 1, Vector3.Zero, Quaternion.Identity));
        loader.Register(walkAnim, Fixtures.MakeAnim(4, 1, Vector3.Zero, Quaternion.Identity));
        loader.Register(linkAnim, Fixtures.MakeAnim(2, 1, Vector3.Zero, Quaternion.Identity));

        var mt = new MotionTable { DefaultStyle = (DRWMotionCommand)NonCombatStyle };
        mt.StyleDefaults[(DRWMotionCommand)NonCombatStyle] = (DRWMotionCommand)Ready;
        Fixtures.AddCycle(mt, NonCombatStyle, Ready, Fixtures.MakeMotionData(readyAnim, 30f));
        Fixtures.AddCycle(mt, NonCombatStyle, WalkForward, Fixtures.MakeMotionData(walkAnim, 30f));
        Fixtures.AddLink(mt, NonCombatStyle, Ready, WalkForward, Fixtures.MakeMotionData(linkAnim, 30f));

        var cmt = new CMotionTable(mt);
        var sink = new RecordingSink();
        var state = new MotionState { Style = NonCombatStyle, Substate = Ready, SubstateMod = 1f };
        var seq = new CSequence(loader);
        var mgr = new MotionTableManager(cmt, state, seq, sink);

        uint result = mgr.PerformMovement(MotionTableMovement.Interpreted(WalkForward, 1f));

        Assert.Equal(MotionTableManagerError.Success, result);
        Assert.Equal(WalkForward, state.Substate);
        Assert.Single(mgr.PendingAnimations);
        Assert.Equal(WalkForward, mgr.PendingAnimations.First().Motion);
        Assert.Equal(1u, mgr.PendingAnimations.First().NumAnims);
    }

    [Fact]
    public void PerformMovement_InterpretedCommand_Failure_ReturnsError0x43_NoQueue()
    {
        var mt = new MotionTable { DefaultStyle = (DRWMotionCommand)NonCombatStyle };
        var cmt = new CMotionTable(mt);
        var (mgr, _, state, _) = MakeManager(cmt);
        state.Style = NonCombatStyle;
        state.Substate = Ready;
        state.SubstateMod = 1f;

        uint result = mgr.PerformMovement(MotionTableMovement.Interpreted(WalkForward, 1f));

        Assert.Equal(MotionTableManagerError.MotionFailed, result);
        Assert.Equal(0x43u, result);
        Assert.Empty(mgr.PendingAnimations);
    }

    [Fact]
    public void PerformMovement_StopInterpretedCommand_Success_QueuesReadySentinel()
    {
        var loader = new FakeLoader();
        uint runAnim = 0x03000510u, readyAnim = 0x03000511u, exitLinkAnim = 0x03000512u;
        loader.Register(runAnim, Fixtures.MakeAnim(4, 1, Vector3.Zero, Quaternion.Identity));
        loader.Register(readyAnim, Fixtures.MakeAnim(4, 1, Vector3.Zero, Quaternion.Identity));
        loader.Register(exitLinkAnim, Fixtures.MakeAnim(2, 1, Vector3.Zero, Quaternion.Identity));

        var mt = new MotionTable { DefaultStyle = (DRWMotionCommand)NonCombatStyle };
        mt.StyleDefaults[(DRWMotionCommand)NonCombatStyle] = (DRWMotionCommand)Ready;
        Fixtures.AddCycle(mt, NonCombatStyle, RunForward, Fixtures.MakeMotionData(runAnim, 30f));
        Fixtures.AddCycle(mt, NonCombatStyle, Ready, Fixtures.MakeMotionData(readyAnim, 30f));
        Fixtures.AddLink(mt, NonCombatStyle, RunForward, Ready, Fixtures.MakeMotionData(exitLinkAnim, 30f));

        var cmt = new CMotionTable(mt);
        var sink = new RecordingSink();
        var state = new MotionState { Style = NonCombatStyle, Substate = RunForward, SubstateMod = 1f };
        var seq = new CSequence(loader);
        var mgr = new MotionTableManager(cmt, state, seq, sink);
        Assert.True(cmt.DoObjectMotion(RunForward, state, seq, 1f, out _));

        uint result = mgr.PerformMovement(MotionTableMovement.StopInterpreted(RunForward, 1f));

        Assert.Equal(MotionTableManagerError.Success, result);
        Assert.Equal(Ready, state.Substate); // re-driven to style default
        Assert.Single(mgr.PendingAnimations);
        Assert.Equal(MotionTableManager.ReadySentinel, mgr.PendingAnimations.First().Motion);
    }

    [Fact]
    public void PerformMovement_StopInterpretedCommand_Failure_ReturnsError0x43_NoQueue()
    {
        var mt = new MotionTable { DefaultStyle = (DRWMotionCommand)NonCombatStyle };
        var cmt = new CMotionTable(mt);
        var (mgr, _, state, _) = MakeManager(cmt);
        state.Style = NonCombatStyle;
        state.Substate = Ready;
        state.SubstateMod = 1f;
        const uint unrelatedModifier = 0x20000099u;

        uint result = mgr.PerformMovement(MotionTableMovement.StopInterpreted(unrelatedModifier, 1f));

        Assert.Equal(MotionTableManagerError.MotionFailed, result);
        Assert.Empty(mgr.PendingAnimations);
    }

    [Fact]
    public void PerformMovement_StopCompletely_UnconditionallyQueuesReadySentinel_EvenOnNoOpStop()
    {
        var mt = new MotionTable { DefaultStyle = (DRWMotionCommand)NonCombatStyle };
        var cmt = new CMotionTable(mt);
        var (mgr, _, state, _) = MakeManager(cmt);
        // state.Substate stays 0 (default) -> StopObjectCompletely no-ops.

        uint result = mgr.PerformMovement(MotionTableMovement.StopCompletely());

        Assert.Equal(MotionTableManagerError.Success, result);
        Assert.Single(mgr.PendingAnimations);
        Assert.Equal(MotionTableManager.ReadySentinel, mgr.PendingAnimations.First().Motion);
    }

    [Fact]
    public void PerformMovement_StopCompletely_DrainsModifiers_QueuesReadySentinel()
    {
        var loader = new FakeLoader();
        uint runAnim = 0x03000520u, readyAnim = 0x03000521u, exitLinkAnim = 0x03000522u;
        loader.Register(runAnim, Fixtures.MakeAnim(4, 1, Vector3.Zero, Quaternion.Identity));
        loader.Register(readyAnim, Fixtures.MakeAnim(4, 1, Vector3.Zero, Quaternion.Identity));
        loader.Register(exitLinkAnim, Fixtures.MakeAnim(2, 1, Vector3.Zero, Quaternion.Identity));

        var mt = new MotionTable { DefaultStyle = (DRWMotionCommand)NonCombatStyle };
        mt.StyleDefaults[(DRWMotionCommand)NonCombatStyle] = (DRWMotionCommand)Ready;
        Fixtures.AddCycle(mt, NonCombatStyle, RunForward, Fixtures.MakeMotionData(runAnim, 30f));
        Fixtures.AddCycle(mt, NonCombatStyle, Ready, Fixtures.MakeMotionData(readyAnim, 30f));
        Fixtures.AddLink(mt, NonCombatStyle, RunForward, Ready, Fixtures.MakeMotionData(exitLinkAnim, 30f));

        var cmt = new CMotionTable(mt);
        var sink = new RecordingSink();
        var state = new MotionState { Style = NonCombatStyle, Substate = RunForward, SubstateMod = 1f };
        var seq = new CSequence(loader);
        var mgr = new MotionTableManager(cmt, state, seq, sink);
        Assert.True(cmt.DoObjectMotion(RunForward, state, seq, 1f, out _));

        uint result = mgr.PerformMovement(MotionTableMovement.StopCompletely());

        Assert.Equal(MotionTableManagerError.Success, result);
        Assert.Equal(Ready, state.Substate);
        Assert.Single(mgr.PendingAnimations);
        Assert.Equal(MotionTableManager.ReadySentinel, mgr.PendingAnimations.First().Motion);
    }

    [Fact]
    public void PerformMovement_UnhandledType_ReturnsNotHandled_NoQueue()
    {
        var mt = new MotionTable { DefaultStyle = (DRWMotionCommand)NonCombatStyle };
        var cmt = new CMotionTable(mt);
        var (mgr, _, _, _) = MakeManager(cmt);

        uint result = mgr.PerformMovement(new MotionTableMovement(MovementType.RawCommand, WalkForward, 1f));

        Assert.Equal(MotionTableManagerError.NotHandled, result);
        Assert.Empty(mgr.PendingAnimations);
    }

    [Fact]
    public void PerformMovement_StopRawCommand_ReturnsNotHandled()
    {
        var mt = new MotionTable { DefaultStyle = (DRWMotionCommand)NonCombatStyle };
        var cmt = new CMotionTable(mt);
        var (mgr, _, _, _) = MakeManager(cmt);

        uint result = mgr.PerformMovement(new MotionTableMovement(MovementType.StopRawCommand, WalkForward, 1f));

        Assert.Equal(MotionTableManagerError.NotHandled, result);
    }


    [Fact]
    public void AddToQueue_CallsRemoveRedundantLinks_Immediately_NotDeferred()
    {
        var (mgr, _, _, _) = MakeManager();
        mgr.AddToQueue(WalkForward, 5);
        mgr.AddToQueue(ThrustMed, 0);

        mgr.AddToQueue(WalkForward, 3);

        var list = mgr.PendingAnimations.ToList();
        Assert.Equal(0u, list[1].NumAnims); // already truncated by the time AddToQueue returns
        Assert.Equal(0u, list[2].NumAnims);
    }
}
