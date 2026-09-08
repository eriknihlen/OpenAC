using System.Numerics;
using AcDream.Core.Physics;
using AcDream.Core.Physics.Motion;
using DatReaderWriter.DBObjs;
using DatReaderWriter.Types;

namespace AcDream.Core.Tests.Physics.Motion;

public class CSequenceTests
{
    private sealed class MapLoader : IAnimationLoader
    {
        private readonly Dictionary<uint, Animation> _map = new();
        public void Add(uint id, Animation anim) => _map[id] = anim;
        public Animation? LoadAnimation(uint id) => _map.TryGetValue(id, out var a) ? a : null;
    }

    private static Animation MakeAnim(int numFrames)
    {
        var anim = new Animation();
        for (int f = 0; f < numFrames; f++)
        {
            var pf = new AnimationFrame(1u);
            pf.Frames.Add(new Frame { Origin = new Vector3(f, 0, 0), Orientation = Quaternion.Identity });
            anim.PartFrames.Add(pf);
        }
        return anim;
    }

    private static AnimData Ad(uint animId, int low = 0, int high = -1, float framerate = 30f)
    {
        QualifiedDataId<Animation> qid = animId;
        return new AnimData { AnimId = qid, LowFrame = low, HighFrame = high, Framerate = framerate };
    }

    private static (CSequence seq, MapLoader loader) NewSeq(params (uint id, int frames)[] anims)
    {
        var loader = new MapLoader();
        foreach (var (id, frames) in anims)
            loader.Add(id, MakeAnim(frames));
        return (new CSequence(loader), loader);
    }

    // ── append_animation (G10) ──────────────────────────────────────────

    [Fact]
    public void Append_First_SeedsCurrAnimAndFrameNumber()
    {
        var (seq, _) = NewSeq((1u, 10));
        seq.AppendAnimation(Ad(1u, low: 3));

        Assert.Equal(1, seq.Count);
        Assert.NotNull(seq.CurrAnim);
        Assert.Same(seq.CurrAnim, seq.FirstCyclic);
        Assert.Equal(3.0, seq.FrameNumber); // head.get_starting_frame()
    }

    [Fact]
    public void Append_SlidesFirstCyclicToNewTail_EveryCall()
    {
        var (seq, _) = NewSeq((1u, 10), (2u, 5), (3u, 4));
        seq.AppendAnimation(Ad(1u));
        seq.AppendAnimation(Ad(2u));
        seq.AppendAnimation(Ad(3u));

        Assert.Equal(3, seq.Count);
        Assert.Equal(4 - 1, seq.FirstCyclic!.HighFrame); // the LAST appended (anim 3, 4 frames)
        Assert.Equal(10 - 1, seq.CurrAnim!.HighFrame);
        Assert.Equal(0.0, seq.FrameNumber);
    }

    [Fact]
    public void Append_UnresolvableAnim_Discarded()
    {
        var (seq, _) = NewSeq((1u, 10));
        seq.AppendAnimation(Ad(999u)); // not in loader
        Assert.Equal(0, seq.Count);
        Assert.Null(seq.CurrAnim);
        Assert.False(seq.HasAnims());
    }

    // ── remove_cyclic_anims (G11) ───────────────────────────────────────

    [Fact]
    public void RemoveCyclicAnims_CurrOutsideTail_KeepsCurr_FirstCyclicToNewTail()
    {
        var (seq, _) = NewSeq((1u, 10), (2u, 5));
        seq.AppendAnimation(Ad(1u));
        seq.AppendAnimation(Ad(2u));

        seq.RemoveCyclicAnims();

        Assert.Equal(1, seq.Count);              // B deleted
        Assert.Equal(9, seq.CurrAnim!.HighFrame); // still A
        Assert.Same(seq.CurrAnim, seq.FirstCyclic); // first_cyclic = new tail (A)
    }

    [Fact]
    public void RemoveCyclicAnims_CurrInsideTail_SnapsBackToPrevAtEndingFrame()
    {
        var (seq, _) = NewSeq((1u, 10), (2u, 5));
        seq.AppendAnimation(Ad(1u));
        seq.AppendAnimation(Ad(2u));
        seq.SetCurrAnimForTest(1);

        seq.RemoveCyclicAnims();

        Assert.Equal(1, seq.Count);
        Assert.Equal(9, seq.CurrAnim!.HighFrame); // snapped back to A
        Assert.Equal(10.0, seq.FrameNumber);      // A.get_ending_frame() = high+1 = 10
    }

    [Fact]
    public void RemoveCyclicAnims_SingleNode_EmptiesAndZeroes()
    {
        var (seq, _) = NewSeq((1u, 10));
        seq.AppendAnimation(Ad(1u));

        seq.RemoveCyclicAnims();

        Assert.Equal(0, seq.Count);
        Assert.Null(seq.CurrAnim);
        Assert.Null(seq.FirstCyclic);
        Assert.Equal(0.0, seq.FrameNumber);
    }

    // ── remove_link_animations / remove_all_link_animations (G11) ──────

    [Fact]
    public void RemoveLinkAnimations_RemovesPredecessorsOfFirstCyclic()
    {
        var (seq, _) = NewSeq((1u, 10), (2u, 5), (3u, 4));
        seq.AppendAnimation(Ad(1u)); // A
        seq.AppendAnimation(Ad(2u)); // B
        seq.AppendAnimation(Ad(3u)); // C = first_cyclic

        seq.RemoveLinkAnimations(1); // removes B (immediate predecessor)

        Assert.Equal(2, seq.Count);
        Assert.Equal(9, seq.CurrAnim!.HighFrame);
        Assert.Equal(3, seq.FirstCyclic!.HighFrame); // C untouched
    }

    [Fact]
    public void RemoveLinkAnimations_CurrRemoved_SnapsForwardToFirstCyclicStart()
    {
        var (seq, _) = NewSeq((1u, 10), (2u, 5));
        seq.AppendAnimation(Ad(1u));
        seq.AppendAnimation(Ad(2u)); // B = first_cyclic

        seq.RemoveLinkAnimations(1);

        Assert.Equal(1, seq.Count);
        Assert.Same(seq.FirstCyclic, seq.CurrAnim);
        Assert.Equal(0.0, seq.FrameNumber); // B.get_starting_frame() = low = 0
    }

    [Fact]
    public void RemoveAllLinkAnimations_RemovesEverythingBeforeFirstCyclic()
    {
        var (seq, _) = NewSeq((1u, 10), (2u, 5), (3u, 4));
        seq.AppendAnimation(Ad(1u));
        seq.AppendAnimation(Ad(2u));
        seq.AppendAnimation(Ad(3u)); // first_cyclic

        seq.RemoveAllLinkAnimations();

        Assert.Equal(1, seq.Count);
        Assert.Same(seq.FirstCyclic, seq.CurrAnim);
        Assert.Equal(3, seq.CurrAnim!.HighFrame);
    }

    // ── apricot (G11/G19) ───────────────────────────────────────────────

    [Fact]
    public void Apricot_HeadIsCurr_NoOp()
    {
        var (seq, _) = NewSeq((1u, 10), (2u, 5));
        seq.AppendAnimation(Ad(1u));
        seq.AppendAnimation(Ad(2u));

        seq.Apricot();

        Assert.Equal(2, seq.Count);
    }

    [Fact]
    public void Apricot_TrimsConsumedHeads_StopsAtCurr()
    {
        var (seq, _) = NewSeq((1u, 10), (2u, 5), (3u, 4));
        seq.AppendAnimation(Ad(1u));
        seq.AppendAnimation(Ad(2u));
        seq.AppendAnimation(Ad(3u)); // C = first_cyclic
        seq.SetCurrAnimForTest(1);

        seq.Apricot();

        Assert.Equal(2, seq.Count);               // A trimmed
        Assert.Equal(4, seq.CurrAnim!.HighFrame);
    }

    [Fact]
    public void Apricot_BoundedByFirstCyclic_EvenIfCurrBeyond()
    {
        var (seq, _) = NewSeq((1u, 10), (2u, 5), (3u, 4));
        seq.AppendAnimation(Ad(1u)); // A
        seq.AppendAnimation(Ad(2u)); // B
        seq.AppendAnimation(Ad(3u));
        seq.SetCurrAnimForTest(2);

        seq.Apricot();

        Assert.Equal(1, seq.Count); // A and B trimmed; stops at first_cyclic
        Assert.Equal(3, seq.CurrAnim!.HighFrame);
    }

    // ── physics accumulators (G12) ──────────────────────────────────────

    [Fact]
    public void Physics_SetCombineSubtract()
    {
        var (seq, _) = NewSeq();
        seq.SetVelocity(new Vector3(1, 2, 3));
        seq.SetOmega(new Vector3(0, 0, 1));
        seq.CombinePhysics(new Vector3(1, 0, 0), new Vector3(0, 0, 0.5f));
        Assert.Equal(new Vector3(2, 2, 3), seq.Velocity);
        Assert.Equal(new Vector3(0, 0, 1.5f), seq.Omega);
        seq.SubtractPhysics(new Vector3(2, 2, 3), new Vector3(0, 0, 1.5f));
        Assert.Equal(Vector3.Zero, seq.Velocity);
        Assert.Equal(Vector3.Zero, seq.Omega);
    }

    // ── multiply_cyclic_animation_fr (G13) ──────────────────────────────

    [Fact]
    public void MultiplyCyclicFramerate_TouchesOnlyCyclicTailFramerates()
    {
        var (seq, _) = NewSeq((1u, 10), (2u, 5));
        seq.AppendAnimation(Ad(1u, framerate: 30f));
        seq.AppendAnimation(Ad(2u, framerate: 30f)); // B = first_cyclic

        seq.MultiplyCyclicAnimationFramerate(2f);

        Assert.Equal(30f, seq.CurrAnim!.Framerate);    // A untouched
        Assert.Equal(60f, seq.FirstCyclic!.Framerate); // B scaled
        Assert.Equal(Vector3.Zero, seq.Velocity);
    }


    [Fact]
    public void Clear_WipesAnimsPhysicsAndPlacement()
    {
        var (seq, _) = NewSeq((1u, 10));
        seq.AppendAnimation(Ad(1u));
        seq.SetVelocity(new Vector3(1, 1, 1));
        var pf = new AnimationFrame(1u);
        seq.SetPlacementFrame(pf, 0x65u);

        seq.Clear();

        Assert.Equal(0, seq.Count);
        Assert.Null(seq.CurrAnim);
        Assert.Equal(0.0, seq.FrameNumber);
        Assert.Equal(Vector3.Zero, seq.Velocity);
        Assert.Null(seq.PlacementFrame);
        Assert.Equal(0u, seq.PlacementFrameId);
    }

    // ── placement + accessors (G14) ─────────────────────────────────────

    [Fact]
    public void GetCurrAnimframe_PlacementFallback_WhenNoCurrAnim()
    {
        var (seq, _) = NewSeq();
        var pf = new AnimationFrame(1u);
        seq.SetPlacementFrame(pf, 0x65u);
        Assert.Same(pf, seq.GetCurrAnimframe());
    }

    [Fact]
    public void GetCurrAnimframe_FlooredFrameLookup()
    {
        var (seq, _) = NewSeq((1u, 10));
        seq.AppendAnimation(Ad(1u));
        seq.FrameNumber = 2.9;
        var frame = seq.GetCurrAnimframe();
        Assert.NotNull(frame);
        Assert.Equal(2f, frame!.Frames[0].Origin.X); // frame index 2

        Assert.Equal(2, seq.GetCurrFrameNumber());
    }
}
