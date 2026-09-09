using System.Collections.Generic;
using System.Numerics;
using AcDream.Core.Physics;
using AcDream.Core.Physics.Motion;
using DatReaderWriter.DBObjs;
using DatReaderWriter.Enums;
using DatReaderWriter.Types;

namespace AcDream.Core.Tests.Physics.Motion;

public class CSequenceUpdateTests
{
    private sealed class MapLoader : IAnimationLoader
    {
        private readonly Dictionary<uint, Animation> _map = new();
        public void Add(uint id, Animation anim) => _map[id] = anim;
        public Animation? LoadAnimation(uint id) => _map.TryGetValue(id, out var a) ? a : null;
    }

    private sealed class RecordingHookQueue : IAnimHookQueue
    {
        public readonly List<string> Events = new();
        public void AddAnimHook(AnimationHook hook)
            => Events.Add($"hook:{((TaggedHook)hook).Tag}:{hook.Direction}");
        public void AddAnimDoneHook() => Events.Add("ANIMDONE");
    }

    private sealed class TaggedHook : AnimationHook
    {
        public string Tag = "";
        public override AnimationHookType HookType => (AnimationHookType)0;
    }

    /// <summary>numFrames frames; one Forward-direction tagged hook per frame.</summary>
    private static Animation MakeAnim(int numFrames, bool posFrames = false, string hookPrefix = "f")
    {
        var anim = new Animation();
        for (int f = 0; f < numFrames; f++)
        {
            var pf = new AnimationFrame(1u);
            pf.Frames.Add(new Frame { Origin = new Vector3(f, 0, 0), Orientation = Quaternion.Identity });
            pf.Hooks.Add(new TaggedHook { Tag = $"{hookPrefix}{f}", Direction = AnimationHookDir.Both });
            anim.PartFrames.Add(pf);
            if (posFrames)
                anim.PosFrames.Add(new Frame { Origin = new Vector3(1, 0, 0), Orientation = Quaternion.Identity });
        }
        return anim;
    }

    private static AnimData Ad(uint animId, float framerate = 30f, int low = 0, int high = -1)
    {
        QualifiedDataId<Animation> qid = animId;
        return new AnimData { AnimId = qid, LowFrame = low, HighFrame = high, Framerate = framerate };
    }

    private static Animation MakePoseAnim(float poseX)
    {
        var anim = new Animation();
        var part = new AnimationFrame(1u);
        part.Frames.Add(new Frame
        {
            Origin = Vector3.Zero,
            Orientation = Quaternion.Identity,
        });
        anim.PartFrames.Add(part);
        anim.PosFrames.Add(new Frame
        {
            Origin = new Vector3(poseX, 0f, 0f),
            Orientation = Quaternion.Identity,
        });
        return anim;
    }

    private static (CSequence seq, RecordingHookQueue hooks) Cyclic(int frames, float framerate = 30f)
    {
        var loader = new MapLoader();
        loader.Add(1u, MakeAnim(frames));
        var seq = new CSequence(loader);
        var hooks = new RecordingHookQueue();
        seq.HookObj = hooks;
        seq.AppendAnimation(Ad(1u, framerate));
        return (seq, hooks);
    }

    // ── forward advance basics (G3) ─────────────────────────────────────

    [Fact]
    public void Forward_SingleTick_AdvancesOneFrame_FiresExitedFrameHook()
    {
        var (seq, hooks) = Cyclic(5);

        seq.Update(1.0 / 30.0, null);

        Assert.Equal(1.0, seq.FrameNumber, 9);
        Assert.Equal(new[] { "hook:f0:Both" }, hooks.Events);
    }

    [Fact]
    public void Forward_ExactBoundaryLanding_NoAdvance()
    {
        var (seq, hooks) = Cyclic(5);
        seq.FrameNumber = 3.5;

        seq.Update(0.5 / 30.0, null); // lands exactly at 4.0 == high_frame

        Assert.Equal(4.0, seq.FrameNumber, 9);
        Assert.Equal(new[] { "hook:f3:Both" }, hooks.Events);
        Assert.DoesNotContain("ANIMDONE", hooks.Events);
    }

    [Fact]
    public void Forward_CyclicWrap_NoAnimDone_RestartsAtStartingFrame()
    {
        var (seq, hooks) = Cyclic(3);
        seq.FrameNumber = 2.0;

        seq.Update(1.0 / 30.0, null);

        Assert.Equal(0.0, seq.FrameNumber, 9);
        Assert.DoesNotContain("ANIMDONE", hooks.Events);
        Assert.Same(seq.FirstCyclic, seq.CurrAnim);
    }


    [Fact]
    public void Forward_LinkToCycle_FiresAnimDone_AndCarriesLeftover()
    {
        var loader = new MapLoader();
        loader.Add(1u, MakeAnim(2, hookPrefix: "link"));
        loader.Add(2u, MakeAnim(4, hookPrefix: "cyc"));
        var seq = new CSequence(loader);
        var hooks = new RecordingHookQueue();
        seq.HookObj = hooks;
        seq.AppendAnimation(Ad(1u)); // link — first_cyclic slides…
        seq.AppendAnimation(Ad(2u));

        seq.Update(0.1, null); // 3 frames of time

        Assert.Equal(new[] { "hook:link0:Both", "ANIMDONE", "hook:cyc0:Both" }, hooks.Events);
        Assert.Equal(1.0, seq.FrameNumber, 6);
        Assert.Same(seq.FirstCyclic, seq.CurrAnim);
    }

    // ── reverse playback (G3/G9) ────────────────────────────────────────

    [Fact]
    public void Reverse_DescendingHooks_BackwardDirection()
    {
        var (seq, hooks) = Cyclic(5, framerate: -30f);
        Assert.Equal(5.0, seq.FrameNumber, 9); // get_starting_frame = high+1

        seq.Update(1.0 / 30.0, null); // 5→4 (leaves OOB index 5 — nothing)
        seq.Update(1.0 / 30.0, null); // 4→3 (leaves frame 4)
        seq.Update(1.0 / 30.0, null); // 3→2 (leaves frame 3)

        Assert.Equal(2.0, seq.FrameNumber, 9);
        Assert.Equal(new[] { "hook:f4:Both", "hook:f3:Both" }, hooks.Events);
    }

    [Fact]
    public void Reverse_AdvanceWrapsToListTail()
    {
        var loader = new MapLoader();
        loader.Add(1u, MakeAnim(3, hookPrefix: "a"));
        loader.Add(2u, MakeAnim(4, hookPrefix: "b"));
        var seq = new CSequence(loader);
        var hooks = new RecordingHookQueue();
        seq.HookObj = hooks;
        seq.AppendAnimation(Ad(1u, framerate: -30f));
        seq.AppendAnimation(Ad(2u));                  // B = first_cyclic = tail
        seq.FrameNumber = 0.5;                        // mid-window of A

        seq.Update(0.02, null);

        Assert.NotNull(seq.CurrAnim);
        Assert.Equal(3, seq.CurrAnim!.HighFrame); // B (4 frames → high 3)
    }

    [Fact]
    public void AdvanceToNextAnimation_PositiveElapsed_SkipsPositiveOutgoingPose()
    {
        var loader = new MapLoader();
        loader.Add(1u, MakePoseAnim(1f));
        loader.Add(2u, MakePoseAnim(2f));
        var seq = new CSequence(loader);
        seq.AppendAnimation(Ad(1u, framerate: 30f, high: 0));
        seq.AppendAnimation(Ad(2u, framerate: 30f, high: 0));
        seq.FrameNumber = 0;
        var frame = new Frame
        {
            Origin = new Vector3(10f, 0f, 0f),
            Orientation = Quaternion.Identity,
        };

        seq.AdvanceToNextAnimation(timeElapsed: 0.1, frame);

        Assert.Equal(12f, frame.Origin.X, 5);
    }

    [Fact]
    public void AdvanceToNextAnimation_NegativeElapsed_SkipsNegativeOutgoingPose()
    {
        var loader = new MapLoader();
        loader.Add(1u, MakePoseAnim(1f));
        loader.Add(2u, MakePoseAnim(2f));
        var seq = new CSequence(loader);
        seq.AppendAnimation(Ad(1u, framerate: -30f, high: 0));
        seq.AppendAnimation(Ad(2u, framerate: -30f, high: 0));
        seq.FrameNumber = 0;
        var frame = new Frame
        {
            Origin = new Vector3(10f, 0f, 0f),
            Orientation = Quaternion.Identity,
        };

        seq.AdvanceToNextAnimation(timeElapsed: -0.1, frame);

        Assert.Equal(12f, frame.Origin.X, 5);
    }

    [Fact]
    public void AdvanceToNextAnimation_ExactEpsilon_AppliesPoseButNotPhysics()
    {
        var loader = new MapLoader();
        loader.Add(1u, MakePoseAnim(1f));
        loader.Add(2u, MakePoseAnim(2f));
        var seq = new CSequence(loader);
        seq.AppendAnimation(Ad(1u, framerate: 30f, high: 0));
        seq.AppendAnimation(Ad(2u, framerate: FrameOps.FEpsilon, high: 0));
        seq.SetVelocity(Vector3.UnitX);
        var frame = new Frame
        {
            Origin = new Vector3(10f, 0f, 0f),
            Orientation = Quaternion.Identity,
        };

        seq.AdvanceToNextAnimation(timeElapsed: 0.1, frame);

        // The sign gate owns pose application. Physics has the separate,
        // strict abs(framerate) > F_EPSILON boundary.
        Assert.Equal(12f, frame.Origin.X, 5);
    }

    // ── stationary / degenerate (G8 + else-branch) ──────────────────────

    [Fact]
    public void ZeroFramerate_PhysicsOnlyAndReturn()
    {
        var (seq, hooks) = Cyclic(3, framerate: 0f);
        seq.SetVelocity(new Vector3(2, 0, 0));
        var frame = new Frame { Origin = Vector3.Zero, Orientation = Quaternion.Identity };

        seq.Update(0.5, frame);

        // frametime == 0 → apply_physics(frame, elapsed, elapsed) then return.
        Assert.Equal(1.0f, frame.Origin.X, 5);
        Assert.Empty(hooks.Events);
        Assert.Equal(0.0, seq.FrameNumber, 9);
    }

    [Fact]
    public void EmptyList_Update_AppliesAccumulatedPhysics()
    {
        var seq = new CSequence(new MapLoader());
        seq.SetVelocity(new Vector3(0, 3, 0));
        var frame = new Frame { Origin = Vector3.Zero, Orientation = Quaternion.Identity };

        seq.Update(2.0, frame);

        Assert.Equal(6.0f, frame.Origin.Y, 5);
    }


    [Fact]
    public void Forward_PosFrames_CombinedIntoCallerFrame()
    {
        var loader = new MapLoader();
        loader.Add(1u, MakeAnim(5, posFrames: true));
        var seq = new CSequence(loader);
        seq.AppendAnimation(Ad(1u));
        var frame = new Frame { Origin = Vector3.Zero, Orientation = Quaternion.Identity };

        seq.Update(2.0 / 30.0, frame);

        Assert.Equal(2.0f, frame.Origin.X, 5);
        Assert.Equal(2.0, seq.FrameNumber, 9);
    }


    [Fact]
    public void Update_RunsApricot_TrimmingConsumedLinkNodes()
    {
        var loader = new MapLoader();
        loader.Add(1u, MakeAnim(2, hookPrefix: "link"));
        loader.Add(2u, MakeAnim(4, hookPrefix: "cyc"));
        var seq = new CSequence(loader);
        seq.AppendAnimation(Ad(1u));
        seq.AppendAnimation(Ad(2u));
        Assert.Equal(2, seq.Count);

        seq.Update(0.1, null);

        Assert.Equal(1, seq.Count); // link trimmed by apricot
        Assert.Same(seq.FirstCyclic, seq.CurrAnim);
    }
}
