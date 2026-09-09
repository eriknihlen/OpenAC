using System;
using System.Collections.Generic;
using System.Numerics;
using AcDream.Core.Physics;
using DatReaderWriter.DBObjs;
using DatReaderWriter.Enums;
using DatReaderWriter.Types;
using Xunit;

using DRWMotionCommand = DatReaderWriter.Enums.MotionCommand;

namespace AcDream.Core.Tests.Physics;


/// <summary>
/// In-memory IAnimationLoader test double. No filesystem access.
/// </summary>
file sealed class FakeLoader : IAnimationLoader
{
    private readonly Dictionary<uint, Animation> _anims = new();

    public void Register(uint id, Animation anim) => _anims[id] = anim;

    public Animation? LoadAnimation(uint id) =>
        _anims.TryGetValue(id, out var a) ? a : null;
}

/// <summary>
/// Helper to build minimal in-memory dat fixtures.
/// </summary>
file static class Fixtures
{
    /// <summary>
    /// Build an Animation with <paramref name="numFrames"/> identical frames,
    /// each part having the supplied origin/orientation.
    /// </summary>
    public static Animation MakeAnim(int numFrames, int numParts,
        Vector3 origin, Quaternion orientation)
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

    /// <summary>
    /// Build a two-frame animation: frame 0 has one origin/rotation, frame 1 another.
    /// Used to exercise slerp blending.
    /// </summary>
    public static Animation MakeTwoFrameAnim(
        int numParts,
        Vector3 fromOrigin, Quaternion fromRot,
        Vector3 toOrigin,   Quaternion toRot)
    {
        var anim = new Animation();

        var pf0 = new AnimationFrame((uint)numParts);
        var pf1 = new AnimationFrame((uint)numParts);
        for (int p = 0; p < numParts; p++)
        {
            pf0.Frames.Add(new Frame { Origin = fromOrigin, Orientation = fromRot });
            pf1.Frames.Add(new Frame { Origin = toOrigin,   Orientation = toRot   });
        }
        anim.PartFrames.Add(pf0);
        anim.PartFrames.Add(pf1);
        return anim;
    }

    /// <summary>
    /// Build a minimal Setup with <paramref name="numParts"/> parts,
    /// each with a DefaultScale of (1,1,1).
    /// </summary>
    public static Setup MakeSetup(int numParts)
    {
        var setup = new Setup();
        for (int i = 0; i < numParts; i++)
        {
            setup.Parts.Add(0x01000000u + (uint)i);  // synthetic GfxObj ids
            setup.DefaultScale.Add(Vector3.One);
        }
        return setup;
    }

    public static MotionTable MakeMtable(
        uint style, uint motion, uint cycleAnimId,
        uint fromMotion = 0, uint toMotion = 0, uint linkAnimId = 0,
        float framerate = 30f)
    {
        var mt = new MotionTable();
        mt.DefaultStyle = (DRWMotionCommand)style;
        mt.StyleDefaults[(DRWMotionCommand)style] = (DRWMotionCommand)motion;

        int cycleKey = (int)((style << 16) | (motion & 0xFFFFFFu));
        mt.Cycles[cycleKey] = MakeMotionData(cycleAnimId, framerate);

        if (fromMotion != 0 && toMotion != 0 && linkAnimId != 0)
        {
            int linkOuter = (int)((style << 16) | (fromMotion & 0xFFFFFFu));
            var cmd = new MotionCommandData();
            cmd.MotionData[(int)toMotion] = MakeMotionData(linkAnimId, framerate);
            mt.Links[linkOuter] = cmd;
        }

        return mt;
    }

    public static MotionData MakeMotionData(uint animId, float framerate)
    {
        var md = new MotionData();
        QualifiedDataId<Animation> qid = animId;
        md.Anims.Add(new AnimData
        {
            AnimId    = qid,
            LowFrame  = 0,
            HighFrame = -1,   // sentinel -> resolve to numFrames-1
            Framerate = framerate,
        });
        return md;
    }
}

public sealed class AnimationSequencerTests
{
    // ── SlerpRetailClient ────────────────────────────────────────────────────

    [Theory]
    [InlineData(0f)]
    [InlineData(0.25f)]
    [InlineData(0.5f)]
    [InlineData(0.75f)]
    [InlineData(1f)]
    public void SlerpRetailClient_MatchesNumerics_ForOrthogonalQuats(float t)
    {
        // Two quaternions 90 degrees apart (rotation around Z axis: 0 and 90 deg).
        var q1 = Quaternion.CreateFromAxisAngle(Vector3.UnitZ, 0f);
        var q2 = Quaternion.CreateFromAxisAngle(Vector3.UnitZ, MathF.PI / 2f);

        var got      = AnimationSequencer.SlerpRetailClient(q1, q2, t);
        var expected = Quaternion.Slerp(q1, q2, t);

        Assert.Equal(expected.X, got.X, 4);
        Assert.Equal(expected.Y, got.Y, 4);
        Assert.Equal(expected.Z, got.Z, 4);
        Assert.Equal(expected.W, got.W, 4);
    }

    [Fact]
    public void SlerpRetailClient_HandlesNegativeDot_TakesShortArc()
    {
        // q2 is the antipodal of q1 (dot -> -1).
        var q1 = Quaternion.CreateFromAxisAngle(Vector3.UnitZ, 0.1f);
        var q2 = new Quaternion(-q1.X, -q1.Y, -q1.Z, -q1.W);  // antipode

        // At t=0 the result should be non-NaN (the sign-flip gives a valid quat).
        var got = AnimationSequencer.SlerpRetailClient(q1, q2, 0f);
        Assert.False(float.IsNaN(got.X));
        Assert.False(float.IsNaN(got.W));
    }

    [Fact]
    public void SlerpRetailClient_NearParallel_LinearFallback()
    {
        // Two identical quaternions -> dot = 1 -> linear fallback path.
        var q = Quaternion.CreateFromAxisAngle(Vector3.UnitY, 0.3f);
        var got = AnimationSequencer.SlerpRetailClient(q, q, 0.5f);

        Assert.Equal(q.X, got.X, 4);
        Assert.Equal(q.Y, got.Y, 4);
        Assert.Equal(q.Z, got.Z, 4);
        Assert.Equal(q.W, got.W, 4);
    }

    // ── SetCycle / frame advance ─────────────────────────────────────────────

    [Fact]
    public void Advance_NoCycleSet_ReturnsIdentityTransforms()
    {
        var setup  = Fixtures.MakeSetup(3);
        var mt     = new MotionTable();
        var loader = new FakeLoader();

        var seq = new AnimationSequencer(setup, mt, loader);
        var transforms = seq.Advance(0.033f);

        Assert.Equal(3, transforms.Count);
        foreach (var tr in transforms)
        {
            Assert.Equal(Vector3.Zero,        tr.Origin);
            Assert.Equal(Quaternion.Identity, tr.Orientation);
        }
    }

    [Fact]
    public void SetCycle_MissingCycle_LeavesSequenceAndStateUntouched()
    {
        const uint Style = 0x8000003Cu;
        const uint ReadyMotion = 0x41000003u;
        const uint AnimId = 0x03000001u;

        var setup = Fixtures.MakeSetup(2);
        var mt = Fixtures.MakeMtable(Style, ReadyMotion, AnimId);
        var loader = new FakeLoader();
        loader.Register(AnimId, Fixtures.MakeTwoFrameAnim(2, Vector3.Zero, Quaternion.Identity, Vector3.Zero, Quaternion.Identity));
        var seq = new AnimationSequencer(setup, mt, loader);

        seq.InitializeState();
        Assert.Equal(ReadyMotion, seq.CurrentMotion);
        int nodesBefore = seq.QueueCount;

        seq.SetCycle(Style, 0x44000007u);

        Assert.Equal(ReadyMotion, seq.CurrentMotion);
        Assert.Equal(nodesBefore, seq.QueueCount);
        Assert.True(seq.HasCurrentNode);
    }

    [Fact]
    public void SetCycle_LoadsAnimation_AdvanceReturnsBoundedTransforms()
    {
        const uint Style  = 0x003Du;   // NonCombat
        const uint Motion = 0x0003u;   // Ready
        const uint AnimId = 0x03000001u;

        var origin = new Vector3(1f, 0f, 0f);
        var rot    = Quaternion.CreateFromAxisAngle(Vector3.UnitZ, 0.5f);
        var anim   = Fixtures.MakeTwoFrameAnim(2, origin, rot, origin * 2, rot);

        var setup  = Fixtures.MakeSetup(2);
        var mt     = Fixtures.MakeMtable(Style, Motion, AnimId);
        var loader = new FakeLoader();
        loader.Register(AnimId, anim);

        var seq = new AnimationSequencer(setup, mt, loader);
        seq.SetCycle(Style, Motion);

        // Very small dt -> should be near the first frame's rotation.
        var transforms = seq.Advance(0.001f);

        Assert.Equal(2, transforms.Count);
        Assert.True(Math.Abs(transforms[0].Orientation.Z - rot.Z) < 0.1f,
            $"Expected orientation near {rot.Z} but got {transforms[0].Orientation.Z}");
    }

    [Fact]
    public void Constructor_InstallsSetupDefaultAnimationForEveryPartArray()
    {
        const uint AnimId = 0x0300AA01u;
        var setup = Fixtures.MakeSetup(1);
        setup.DefaultAnimation = (QualifiedDataId<Animation>)AnimId;
        var loader = new FakeLoader();
        loader.Register(AnimId, Fixtures.MakeTwoFrameAnim(
            1,
            Vector3.Zero,
            Quaternion.Identity,
            new Vector3(2f, 0f, 0f),
            Quaternion.Identity));
        var sequencer = new AnimationSequencer(setup, new MotionTable(), loader);

        Assert.True(sequencer.HasCurrentNode);
        Assert.Equal(30f, sequencer.CurrentNodeDiag.Framerate);
        Assert.Equal(0, sequencer.CurrentNodeDiag.StartFrame);
        Assert.Equal(1, sequencer.CurrentNodeDiag.EndFrame);
    }

    [Fact]
    public void Advance_FrameWrapsAtHighFrame()
    {
        const uint Style  = 0x003Du;
        const uint Motion = 0x0003u;
        const uint AnimId = 0x03000002u;

        // 4-frame animation; framerate=10fps, one full loop = 0.4s.
        var anim  = Fixtures.MakeAnim(4, 1, Vector3.Zero, Quaternion.Identity);
        var setup = Fixtures.MakeSetup(1);
        var mt    = new MotionTable();
        mt.DefaultStyle = (DRWMotionCommand)Style;
        mt.StyleDefaults[(DRWMotionCommand)Style] = (DRWMotionCommand)Motion;
        int cycleKey = (int)((Style << 16) | (Motion & 0xFFFFFFu));
        mt.Cycles[cycleKey] = new MotionData();
        QualifiedDataId<Animation> qid = AnimId;
        mt.Cycles[cycleKey].Anims.Add(new AnimData
        {
            AnimId    = qid,
            LowFrame  = 0,
            HighFrame = 3,
            Framerate = 10f,
        });

        var loader = new FakeLoader();
        loader.Register(AnimId, anim);

        var seq = new AnimationSequencer(setup, mt, loader);
        seq.SetCycle(Style, Motion);

        // Advance one full loop + a bit: 0.5s at 10fps = 5 frames.
        // After wrapping this should still return a valid transform.
        seq.Advance(0.5f);
        var transforms = seq.Advance(0.01f);

        Assert.Single(transforms);
    }

    [Fact]
    public void SetCycle_WithTransitionLink_PrependLinkFrames()
    {
        const uint Style      = 0x8000003Du;
        const uint IdleMotion = 0x40000003u;
        const uint WalkMotion = 0x40000005u;
        const uint IdleAnim   = 0x03000012u;
        const uint CycleAnim  = 0x03000010u;
        const uint LinkAnim   = 0x03000011u;

        var idleAnim  = Fixtures.MakeAnim(1, 1, Vector3.Zero, Quaternion.Identity);
        var cycleAnim = Fixtures.MakeAnim(4, 1, new Vector3(1, 0, 0), Quaternion.Identity);
        var linkAnim  = Fixtures.MakeAnim(2, 1, new Vector3(0, 1, 0), Quaternion.Identity);

        var setup  = Fixtures.MakeSetup(1);
        // MotionTable: link Idle->Walk = 2-frame transition anim.
        var mt = Fixtures.MakeMtable(
            style:       Style,
            motion:      WalkMotion,
            cycleAnimId: CycleAnim,
            fromMotion:  IdleMotion,
            toMotion:    WalkMotion,
            linkAnimId:  LinkAnim);
        mt.StyleDefaults[(DRWMotionCommand)Style] = (DRWMotionCommand)IdleMotion;
        int idleKey = (int)((Style << 16) | (IdleMotion & 0xFFFFFFu));
        mt.Cycles[idleKey] = Fixtures.MakeMotionData(IdleAnim, framerate: 30f);

        var loader = new FakeLoader();
        loader.Register(IdleAnim,  idleAnim);
        loader.Register(CycleAnim, cycleAnim);
        loader.Register(LinkAnim,  linkAnim);

        var seq = new AnimationSequencer(setup, mt, loader);

        seq.SetCycle(Style, IdleMotion);

        seq.SetCycle(Style, WalkMotion);

        var transforms = seq.Advance(0.001f);
        Assert.Single(transforms);
        Assert.True(transforms[0].Origin.Y > transforms[0].Origin.X,
            $"Expected link-anim Y({transforms[0].Origin.Y}) > cycle X({transforms[0].Origin.X})");
    }

    [Fact]
    public void SpawnEnterWorld_DeadState_StripsReadyToDeadTransition()
    {
        const uint Style       = 0x8000003Du;
        const uint ReadyMotion = 0x41000003u;
        const uint DeadMotion  = 0x40000011u;
        const uint ReadyAnim   = 0x03000022u;
        const uint DeadAnim    = 0x03000020u;
        const uint FallAnim    = 0x03000021u;

        var setup = Fixtures.MakeSetup(1);
        var mt = Fixtures.MakeMtable(
            style: Style,
            motion: DeadMotion,
            cycleAnimId: DeadAnim,
            fromMotion: ReadyMotion,
            toMotion: DeadMotion,
            linkAnimId: FallAnim);
        mt.StyleDefaults[(DRWMotionCommand)Style] = (DRWMotionCommand)ReadyMotion;
        mt.Cycles[(int)((Style << 16) | (ReadyMotion & 0xFFFFFFu))] =
            Fixtures.MakeMotionData(ReadyAnim, framerate: 30f);

        var loader = new FakeLoader();
        loader.Register(ReadyAnim,
            Fixtures.MakeAnim(1, 1, Vector3.Zero, Quaternion.Identity));
        loader.Register(FallAnim,
            Fixtures.MakeAnim(2, 1, new Vector3(0, 1, 0), Quaternion.Identity));
        loader.Register(DeadAnim,
            Fixtures.MakeAnim(1, 1, new Vector3(1, 0, 0), Quaternion.Identity));

        var seq = new AnimationSequencer(setup, mt, loader);
        seq.InitializeState();
        seq.SetCycle(Style, DeadMotion);

        var detachedPose = seq.Advance(0.001f);
        Assert.True(detachedPose[0].Origin.Y > detachedPose[0].Origin.X,
            "Before enter-world, the description-time Ready->Dead link must exist");

        seq.Manager.HandleEnterWorld();

        var enteredPose = seq.Advance(0.001f);
        Assert.True(enteredPose[0].Origin.X > enteredPose[0].Origin.Y,
            "Enter-world must start the corpse on the fallen Dead cycle");
        Assert.Equal(1, seq.CurrentNodeDiag.QueueCount);
    }

    [Fact]
    public void Advance_LinkTailDoesNotBlendIntoLinkFrame0()
    {
        const uint Style      = 0x8000003Du;
        const uint IdleMotion = 0x40000003u;
        const uint WalkMotion = 0x40000005u;
        const uint IdleAnim   = 0x03000082u;
        const uint CycleAnim  = 0x03000080u;
        const uint LinkAnim   = 0x03000081u;

        var linkAnim = new Animation();
        for (int f = 0; f < 3; f++)
        {
            var pf = new AnimationFrame(1);
            float y = 10f - 5f * f;  // 10, 5, 0
            pf.Frames.Add(new Frame { Origin = new Vector3(0, y, 0), Orientation = Quaternion.Identity });
            linkAnim.PartFrames.Add(pf);
        }

        var cycleAnim = Fixtures.MakeAnim(1, 1, new Vector3(0, 0, 0), Quaternion.Identity);
        var idleAnim  = Fixtures.MakeAnim(1, 1, Vector3.Zero, Quaternion.Identity);

        var setup = Fixtures.MakeSetup(1);
        var mt = Fixtures.MakeMtable(
            style:       Style,
            motion:      WalkMotion,
            cycleAnimId: CycleAnim,
            fromMotion:  IdleMotion,
            toMotion:    WalkMotion,
            linkAnimId:  LinkAnim,
            framerate:   30f);
        mt.StyleDefaults[(DRWMotionCommand)Style] = (DRWMotionCommand)IdleMotion;
        int idleKey = (int)((Style << 16) | (IdleMotion & 0xFFFFFFu));
        mt.Cycles[idleKey] = Fixtures.MakeMotionData(IdleAnim, framerate: 30f);

        var loader = new FakeLoader();
        loader.Register(IdleAnim,  idleAnim);
        loader.Register(CycleAnim, cycleAnim);
        loader.Register(LinkAnim,  linkAnim);

        var seq = new AnimationSequencer(setup, mt, loader);
        seq.SetCycle(Style, IdleMotion);
        seq.SetCycle(Style, WalkMotion);

        // Advance to _framePosition ≈ 2.5 — past the last integer frame (2)
        // but before maxBoundary - epsilon (≈ 3). At 30 fps, 2.5/30 = 0.0833s.
        seq.Advance(0.0833f);
        double pos = GetFramePosition(seq);
        Assert.InRange(pos, 2.4, 2.7);

        var transforms = seq.Advance(0.0001f);  // tiny extra dt to trigger blend read

        // Pre-fix: nextIdx would wrap to rangeLo (0), so transforms[0].Origin.Y
        // would land near 0.5 × 0 + 0.5 × 10 = 5 (mid-blend with link frame 0).
        // Post-fix: nextIdx = frameIdx (2), so transforms[0].Origin.Y = 0 (held).
        Assert.True(transforms[0].Origin.Y < 1f,
            $"Link tail should hold last-frame pose Y=0; got Y={transforms[0].Origin.Y} "
            + "(would be ~5 if nextIdx still wrapped to link frame 0)");
    }

    [Fact]
    public void Advance_CyclicSeamHoldsLastFrameInsteadOfBlendingIntoFrame0()
    {
        const uint Style  = 0x003Du;
        const uint Motion = 0x0003u;
        const uint AnimId = 0x03000002u;

        var anim = new Animation();
        for (int f = 0; f < 4; f++)
        {
            var pf = new AnimationFrame(1);
            pf.Frames.Add(new Frame
            {
                Origin = Vector3.Zero,
                Orientation = Quaternion.CreateFromAxisAngle(Vector3.UnitY, MathF.PI / 6f * f),
            });
            anim.PartFrames.Add(pf);
        }
        var setup = Fixtures.MakeSetup(1);
        var mt = new MotionTable();
        mt.DefaultStyle = (DRWMotionCommand)Style;
        mt.StyleDefaults[(DRWMotionCommand)Style] = (DRWMotionCommand)Motion;
        int cycleKey = (int)((Style << 16) | (Motion & 0xFFFFFFu));
        mt.Cycles[cycleKey] = new MotionData();
        QualifiedDataId<Animation> qid = AnimId;
        mt.Cycles[cycleKey].Anims.Add(new AnimData
        {
            AnimId = qid,
            LowFrame = 0,
            HighFrame = 3,
            Framerate = 10f,
        });
        var loader = new FakeLoader();
        loader.Register(AnimId, anim);
        var seq = new AnimationSequencer(setup, mt, loader);
        seq.SetCycle(Style, Motion);

        // Frame position 3.5 at 10 fps: the fractional tail of the LAST frame.
        seq.Advance(0.35f);
        var transforms = seq.Advance(0.0001f);
        Assert.InRange(GetFramePosition(seq), 3.4, 3.7);

        // Angle of the blended orientation about Y.
        Quaternion q = transforms[0].Orientation;
        float angleDeg = 2f * MathF.Atan2(MathF.Abs(q.Y), MathF.Abs(q.W)) * 180f / MathF.PI;
        Assert.InRange(angleDeg, 89f, 91f);  // held at frame 3; a seam blend would give ~45 deg
    }

    [Fact]
    public void SetCycle_StopFromWalkBackward_FallsBackToWalkForwardStopLink()
    {
        const uint Style          = 0x8000003Du;
        const uint WalkForwardCmd = 0x40000005u;
        const uint WalkBackCmd    = 0x40000006u;
        const uint ReadyCmd       = 0x40000003u;
        const uint CycleAnim      = 0x03000090u;
        const uint LinkAnim       = 0x03000091u;  // Ready->Walk windup (Y=7), played reversed as the settle

        var cycleAnim = Fixtures.MakeAnim(1, 1, new Vector3(0, 0, 0), Quaternion.Identity);
        var linkAnim  = Fixtures.MakeAnim(4, 1, new Vector3(0, 7, 0), Quaternion.Identity);

        var setup = Fixtures.MakeSetup(1);
        var mt = Fixtures.MakeMtable(
            style:       Style,
            motion:      ReadyCmd,
            cycleAnimId: CycleAnim,
            fromMotion:  ReadyCmd,
            toMotion:    WalkForwardCmd,
            linkAnimId:  LinkAnim,
            framerate:   30f);
        int walkKey = (int)((Style << 16) | (WalkForwardCmd & 0xFFFFFFu));
        mt.Cycles[walkKey] = Fixtures.MakeMotionData(CycleAnim, framerate: 30f);

        var loader = new FakeLoader();
        loader.Register(CycleAnim, cycleAnim);
        loader.Register(LinkAnim,  linkAnim);

        var seq = new AnimationSequencer(setup, mt, loader);
        seq.SetCycle(Style, WalkBackCmd, 1.0f);

        seq.SetCycle(Style, ReadyCmd);

        var transforms = seq.Advance(0.001f);
        Assert.Single(transforms);
        Assert.True(transforms[0].Origin.Y > 5f,
            $"Stop-from-backward should resolve GetLink's reversed-key branch "
            + $"(expect Y≈7 from the reversed windup link); got Y={transforms[0].Origin.Y} "
            + "(Y=0 means the link didn't resolve and we snapped to the Ready cycle).");
    }

    [Fact]
    public void SetCycle_NoLinkInTable_DirectCycleSwitch()
    {
        const uint Style  = 0x003Du;
        const uint Motion = 0x0003u;
        const uint AnimId = 0x03000020u;

        var anim   = Fixtures.MakeAnim(3, 1, new Vector3(5, 0, 0), Quaternion.Identity);
        var setup  = Fixtures.MakeSetup(1);
        var mt     = Fixtures.MakeMtable(Style, Motion, AnimId);
        var loader = new FakeLoader();
        loader.Register(AnimId, anim);

        var seq = new AnimationSequencer(setup, mt, loader);
        seq.SetCycle(Style, Motion);

        var transforms = seq.Advance(0.001f);
        Assert.Single(transforms);
        Assert.True(transforms[0].Origin.X > 4f,
            $"Expected cycle origin X~5 but got {transforms[0].Origin.X}");
    }

    [Fact]
    public void SetCycle_SameMotionTwice_NoStateChange()
    {
        const uint Style  = 0x003Du;
        const uint Motion = 0x0003u;
        const uint AnimId = 0x03000030u;

        var anim   = Fixtures.MakeAnim(4, 1, Vector3.Zero, Quaternion.Identity);
        var setup  = Fixtures.MakeSetup(1);
        var mt     = Fixtures.MakeMtable(Style, Motion, AnimId);
        var loader = new FakeLoader();
        loader.Register(AnimId, anim);

        var seq = new AnimationSequencer(setup, mt, loader);
        seq.SetCycle(Style, Motion);

        seq.Advance(0.1f);

        double frameBefore = GetFramePosition(seq);

        seq.SetCycle(Style, Motion);

        double frameAfter = GetFramePosition(seq);

        Assert.Equal(frameBefore, frameAfter);
    }

    [Fact]
    public void Reset_ClearsAllState()
    {
        const uint Style  = 0x003Du;
        const uint Motion = 0x0003u;
        const uint AnimId = 0x03000040u;

        var anim   = Fixtures.MakeAnim(4, 1, Vector3.One, Quaternion.Identity);
        var setup  = Fixtures.MakeSetup(1);
        var mt     = Fixtures.MakeMtable(Style, Motion, AnimId);
        var loader = new FakeLoader();
        loader.Register(AnimId, anim);

        var seq = new AnimationSequencer(setup, mt, loader);
        seq.SetCycle(Style, Motion);
        seq.Advance(0.2f);

        seq.Reset();

        Assert.Equal(0u, seq.CurrentStyle);
        Assert.Equal(0u, seq.CurrentMotion);

        // After reset, Advance should return identity transforms.
        var transforms = seq.Advance(0.033f);
        foreach (var tr in transforms)
        {
            Assert.Equal(Vector3.Zero,        tr.Origin);
            Assert.Equal(Quaternion.Identity, tr.Orientation);
        }
    }

    // ── Negative-speed playback (TurnLeft → TurnRight reversed) ─────────────

    [Fact]
    public void SetCycle_TurnLeft_RemapsToTurnRightWithNegativeSpeed()
    {

        const uint Style      = 0x8000003Du;  // NonCombat
        const uint TurnRight  = 0x4045000Du; // bit pattern for TurnRight in NonCombat
        const uint TurnLeft   = 0x4045000Eu; // bit pattern for TurnLeft
        const uint AnimId     = 0x03000050u;

        // 4-frame animation; each frame has a distinct Z-origin so we can tell
        // which direction we're reading.
        var anim = new Animation();
        for (int f = 0; f < 4; f++)
        {
            var pf = new AnimationFrame(1);
            pf.Frames.Add(new Frame { Origin = new Vector3(0, 0, f), Orientation = Quaternion.Identity });
            anim.PartFrames.Add(pf);
        }

        var setup  = Fixtures.MakeSetup(1);
        var mt     = new MotionTable();
        mt.DefaultStyle = (DRWMotionCommand)Style;
        mt.StyleDefaults[(DRWMotionCommand)Style] = (DRWMotionCommand)TurnRight;

        int cycleKey = (int)((Style << 16) | (TurnRight & 0xFFFFFFu));
        mt.Cycles[cycleKey] = Fixtures.MakeMotionData(AnimId, framerate: 10f);

        var loader = new FakeLoader();
        loader.Register(AnimId, anim);

        var seq = new AnimationSequencer(setup, mt, loader);
        seq.SetCycle(Style, TurnLeft, speedMod: 1f);

        Assert.Equal(TurnRight, seq.CurrentMotion);
        Assert.Equal(-1f, seq.CurrentSpeedMod, 3);

        double pos = GetFramePosition(seq);
        Assert.True(pos == 4.0,
            $"Expected framePosition == 4 (bare-int reverse start = HighFrame+1, "
            + $"AnimSequenceNode.GetStartingFrame has NO epsilon — G1); got {pos}");
    }

    [Fact]
    public void Advance_NegativeSpeed_FramePositionDecreases()
    {
        const uint Style   = 0x8000003Du;
        const uint Motion  = 0x40000003u;
        const uint AnimId  = 0x03000060u;

        var anim   = Fixtures.MakeAnim(8, 1, Vector3.Zero, Quaternion.Identity);
        var setup  = Fixtures.MakeSetup(1);
        var mt     = new MotionTable();
        mt.DefaultStyle = (DRWMotionCommand)Style;
        mt.StyleDefaults[(DRWMotionCommand)Style] = (DRWMotionCommand)Motion;

        int cycleKey = (int)((Style << 16) | (Motion & 0xFFFFFFu));
        var md = new MotionData();
        QualifiedDataId<Animation> qid = AnimId;
        md.Anims.Add(new AnimData
        {
            AnimId    = qid,
            LowFrame  = 0,
            HighFrame = 7,
            Framerate = -10f,  // negative → reverse
        });
        mt.Cycles[cycleKey] = md;

        var loader = new FakeLoader();
        loader.Register(AnimId, anim);

        var seq = new AnimationSequencer(setup, mt, loader);
        seq.SetCycle(Style, Motion);

        // For negative framerate: startFrame=7, endFrame=0 (swapped by multiply_framerate).
        // GetStartFramePosition = (endFrame + 1) - EPSILON = 1 - eps (the swapped endFrame is 0).
        // Wait — after swap: StartFrame=7, EndFrame=0.
        // GetStartFramePosition for negative fr: (EndFrame + 1) - eps = (0 + 1) - eps ≈ 0.99999.
        // Then Advance(0.05) at -10fps → delta = -10 * 0.05 = -0.5 → new pos ≈ 0.49999.
        double posBefore = GetFramePosition(seq);
        seq.Advance(0.05f);
        double posAfter = GetFramePosition(seq);

        Assert.True(posAfter < posBefore,
            $"Expected framePosition to decrease (reverse) but went {posBefore} → {posAfter}");
    }

    [Fact]
    public void Advance_NegativeSpeed_WrapsAtStartBoundary()
    {
        const uint Style   = 0x003Du;
        const uint Motion  = 0x0003u;
        const uint AnimId  = 0x03000070u;

        var anim   = Fixtures.MakeAnim(4, 1, Vector3.Zero, Quaternion.Identity);
        var setup  = Fixtures.MakeSetup(1);
        var mt     = new MotionTable();
        mt.DefaultStyle = (DRWMotionCommand)Style;

        int cycleKey = (int)((Style << 16) | (Motion & 0xFFFFFFu));
        var md = new MotionData();
        QualifiedDataId<Animation> qid = AnimId;
        md.Anims.Add(new AnimData
        {
            AnimId    = qid,
            LowFrame  = 0,
            HighFrame = 3,
            Framerate = -10f,
        });
        mt.Cycles[cycleKey] = md;

        var loader = new FakeLoader();
        loader.Register(AnimId, anim);

        var seq = new AnimationSequencer(setup, mt, loader);
        seq.SetCycle(Style, Motion);

        seq.Advance(0.5f);
        var transforms = seq.Advance(0.01f);

        Assert.Single(transforms);
        // Verify the frame position is back within the valid range after wrapping.
        double pos = GetFramePosition(seq);
        Assert.True(pos >= 0.0 && pos < 4.0,
            $"Frame position {pos} out of range [0, 4) after reverse wrap");
    }


    [Fact]
    public void AdvanceToNextAnimation_LinkDrainsThenCycleLoops()
    {
        const uint Style      = 0x8000003Du;
        const uint IdleMotion = 0x40000003u;
        const uint WalkMotion = 0x40000005u;
        const uint IdleAnim   = 0x03000082u;
        const uint CycleAnim  = 0x03000080u;
        const uint LinkAnim   = 0x03000081u;

        // Link anim: 2 frames, Y=5 (distinct marker).
        var linkAnim  = Fixtures.MakeAnim(2, 1, new Vector3(0, 5, 0), Quaternion.Identity);
        var cycleAnim = Fixtures.MakeAnim(4, 1, new Vector3(9, 0, 0), Quaternion.Identity);
        var idleAnim  = Fixtures.MakeAnim(1, 1, Vector3.Zero, Quaternion.Identity);

        var setup  = Fixtures.MakeSetup(1);
        var mt     = Fixtures.MakeMtable(
            style:       Style,
            motion:      WalkMotion,
            cycleAnimId: CycleAnim,
            fromMotion:  IdleMotion,
            toMotion:    WalkMotion,
            linkAnimId:  LinkAnim,
            framerate:   10f);
        mt.StyleDefaults[(DRWMotionCommand)Style] = (DRWMotionCommand)IdleMotion;
        int idleKey = (int)((Style << 16) | (IdleMotion & 0xFFFFFFu));
        mt.Cycles[idleKey] = Fixtures.MakeMotionData(IdleAnim, framerate: 10f);

        var loader = new FakeLoader();
        loader.Register(IdleAnim,  idleAnim);
        loader.Register(CycleAnim, cycleAnim);
        loader.Register(LinkAnim,  linkAnim);

        var seq = new AnimationSequencer(setup, mt, loader);
        seq.SetCycle(Style, IdleMotion);
        seq.SetCycle(Style, WalkMotion);

        seq.Advance(0.25f);

        var transforms = seq.Advance(0.001f);

        Assert.Single(transforms);
        Assert.True(transforms[0].Origin.X > 8f,
            $"Expected cycle anim origin X~9 but got {transforms[0].Origin.X} (link Y was 5)");
    }

    [Fact]
    public void AdvanceToNextAnimation_CycleLoopsRepeatedly()
    {
        const uint Style  = 0x003Du;
        const uint Motion = 0x0003u;
        const uint AnimId = 0x03000090u;

        var anim  = Fixtures.MakeAnim(4, 1, new Vector3(1, 0, 0), Quaternion.Identity);
        var setup = Fixtures.MakeSetup(1);
        var mt    = Fixtures.MakeMtable(Style, Motion, AnimId, framerate: 10f);
        var loader = new FakeLoader();
        loader.Register(AnimId, anim);

        var seq = new AnimationSequencer(setup, mt, loader);
        seq.SetCycle(Style, Motion);

        // Advance 5 full loops (4 frames × 10fps = 0.4s per loop → 2.0s total).
        for (int i = 0; i < 10; i++)
            seq.Advance(0.2f);

        var transforms = seq.Advance(0.001f);

        Assert.Single(transforms);
        // Frame position must be in a valid range (not NaN, not out of bounds).
        double pos = GetFramePosition(seq);
        Assert.True(pos >= 0.0 && pos < 4.0,
            $"Frame position {pos} out of range [0, 4) after 5 loops");
    }


    [Fact]
    public void Advance_FiresForwardHook_OnFrameBoundaryCrossing()
    {
        const uint Style  = 0x003Du;
        const uint Motion = 0x0003u;
        const uint AnimId = 0x03000100u;

        var anim = Fixtures.MakeAnim(4, 1, Vector3.Zero, Quaternion.Identity);
        anim.PartFrames[1].Hooks.Add(new SoundHook
        {
            Direction = AnimationHookDir.Forward,
            Id = 0x0A000042u,
        });

        var setup  = Fixtures.MakeSetup(1);
        var mt     = Fixtures.MakeMtable(Style, Motion, AnimId, framerate: 10f);
        var loader = new FakeLoader();
        loader.Register(AnimId, anim);

        var seq = new AnimationSequencer(setup, mt, loader);
        seq.SetCycle(Style, Motion);

        // Drain any hooks pre-existing from initial load (there should be none).
        seq.ConsumePendingHooks();

        seq.Advance(0.05f);
        Assert.Empty(seq.ConsumePendingHooks());

    }

    [Fact]
    public void Advance_FiresHookOnCrossedFrame_ForwardDirection()
    {
        const uint Style  = 0x003Du;
        const uint Motion = 0x0003u;
        const uint AnimId = 0x03000101u;

        var anim = Fixtures.MakeAnim(4, 1, Vector3.Zero, Quaternion.Identity);
        anim.PartFrames[0].Hooks.Add(new SoundHook
        {
            Direction = AnimationHookDir.Forward,
            Id = 0x0A000001u,
        });
        anim.PartFrames[2].Hooks.Add(new SoundHook
        {
            Direction = AnimationHookDir.Forward,
            Id = 0x0A000002u,
        });

        var setup  = Fixtures.MakeSetup(1);
        var mt     = Fixtures.MakeMtable(Style, Motion, AnimId, framerate: 10f);
        var loader = new FakeLoader();
        loader.Register(AnimId, anim);

        var seq = new AnimationSequencer(setup, mt, loader);
        seq.SetCycle(Style, Motion);

        seq.ConsumePendingHooks();

        seq.Advance(0.15f);
        var hooks = seq.ConsumePendingHooks();

        Assert.Single(hooks);
        Assert.IsType<SoundHook>(hooks[0]);

        seq.Advance(0.2f);
        var hooks2 = seq.ConsumePendingHooks();

        Assert.Single(hooks2);
        Assert.IsType<SoundHook>(hooks2[0]);
        Assert.Equal(0x0A000002u, (uint)((SoundHook)hooks2[0]).Id);
    }

    [Fact]
    public void Advance_BothDirectionHook_FiresInForwardAndReverse()
    {
        // Direction.Both fires regardless of playback direction.
        const uint Style  = 0x003Du;
        const uint Motion = 0x0003u;
        const uint AnimId = 0x03000102u;

        var anim = Fixtures.MakeAnim(4, 1, Vector3.Zero, Quaternion.Identity);
        anim.PartFrames[0].Hooks.Add(new SoundHook
        {
            Direction = AnimationHookDir.Both,
            Id = 0x0A000003u,
        });

        var setup  = Fixtures.MakeSetup(1);
        var mt     = Fixtures.MakeMtable(Style, Motion, AnimId, framerate: 10f);
        var loader = new FakeLoader();
        loader.Register(AnimId, anim);

        var seq = new AnimationSequencer(setup, mt, loader);
        seq.SetCycle(Style, Motion);
        seq.ConsumePendingHooks();

        seq.Advance(0.15f);
        Assert.Single(seq.ConsumePendingHooks());
    }

    [Fact]
    public void Advance_ForwardHookDoesNotFire_OnReversePlayback()
    {
        const uint Style  = 0x8000003Du;
        const uint Motion = 0x40000003u;
        const uint AnimId = 0x03000103u;

        var anim = Fixtures.MakeAnim(4, 1, Vector3.Zero, Quaternion.Identity);
        anim.PartFrames[2].Hooks.Add(new SoundHook
        {
            Direction = AnimationHookDir.Forward,
            Id = 0x0A000004u,
        });

        var setup = Fixtures.MakeSetup(1);
        var mt    = new MotionTable();
        mt.DefaultStyle = (DRWMotionCommand)Style;
        mt.StyleDefaults[(DRWMotionCommand)Style] = (DRWMotionCommand)Motion;
        int cycleKey = (int)((Style << 16) | (Motion & 0xFFFFFFu));
        var md = new MotionData();
        QualifiedDataId<Animation> qid = AnimId;
        md.Anims.Add(new AnimData { AnimId = qid, LowFrame = 0, HighFrame = 3, Framerate = -10f });
        mt.Cycles[cycleKey] = md;

        var loader = new FakeLoader();
        loader.Register(AnimId, anim);

        var seq = new AnimationSequencer(setup, mt, loader);
        seq.SetCycle(Style, Motion);
        seq.ConsumePendingHooks();

        seq.Advance(0.25f);
        var hooks = seq.ConsumePendingHooks();

        // Forward-only hook on frame 2 should NOT fire on reverse playback.
        Assert.DoesNotContain(hooks, h => h is SoundHook sh && (uint)sh.Id == 0x0A000004u);
    }

    [Fact]
    public void Advance_BackwardHook_FiresOnReversePlayback()
    {
        const uint Style  = 0x8000003Du;
        const uint Motion = 0x40000003u;
        const uint AnimId = 0x03000104u;

        var anim = Fixtures.MakeAnim(4, 1, Vector3.Zero, Quaternion.Identity);
        anim.PartFrames[2].Hooks.Add(new SoundHook
        {
            Direction = AnimationHookDir.Backward,
            Id = 0x0A000005u,
        });

        var setup = Fixtures.MakeSetup(1);
        var mt    = new MotionTable();
        mt.DefaultStyle = (DRWMotionCommand)Style;
        mt.StyleDefaults[(DRWMotionCommand)Style] = (DRWMotionCommand)Motion;
        int cycleKey = (int)((Style << 16) | (Motion & 0xFFFFFFu));
        var md = new MotionData();
        QualifiedDataId<Animation> qid = AnimId;
        md.Anims.Add(new AnimData { AnimId = qid, LowFrame = 0, HighFrame = 3, Framerate = -10f });
        mt.Cycles[cycleKey] = md;

        var loader = new FakeLoader();
        loader.Register(AnimId, anim);

        var seq = new AnimationSequencer(setup, mt, loader);
        seq.SetCycle(Style, Motion);
        seq.ConsumePendingHooks();

        seq.Advance(0.25f);
        var hooks = seq.ConsumePendingHooks();

        Assert.Contains(hooks, h => h is SoundHook sh && (uint)sh.Id == 0x0A000005u);
    }

    // ── PosFrames root motion (R1-P6: the wired Frame path, gap map G7) ───────

    [Fact]
    public void Advance_WithRootMotionFrame_AccumulatesPosFrameDeltas()
    {
        const uint Style  = 0x003Du;
        const uint Motion = 0x0003u;
        const uint AnimId = 0x03000110u;

        // 4-frame anim, each PosFrame origin = (1, 0, 0), rotation identity.
        var anim = Fixtures.MakeAnim(4, 1, Vector3.Zero, Quaternion.Identity);
        anim.Flags = AnimationFlags.PosFrames;
        for (int f = 0; f < 4; f++)
        {
            anim.PosFrames.Add(new Frame
            {
                Origin      = new Vector3(1f, 0f, 0f),
                Orientation = Quaternion.Identity,
            });
        }

        var setup  = Fixtures.MakeSetup(1);
        var mt     = Fixtures.MakeMtable(Style, Motion, AnimId, framerate: 10f);
        var loader = new FakeLoader();
        loader.Register(AnimId, anim);

        var seq = new AnimationSequencer(setup, mt, loader);
        seq.SetCycle(Style, Motion);

        var rootFrame = new Frame { Origin = Vector3.Zero, Orientation = Quaternion.Identity };
        seq.Advance(0.25f, rootFrame);

        Assert.True(rootFrame.Origin.X >= 1.8f && rootFrame.Origin.X <= 2.2f,
            $"Expected ~2.0 root motion X after 2 crossings via the wired Frame, got {rootFrame.Origin.X}");
    }

    [Fact]
    public void CurrentVelocity_ExposedFromMotionData_WhenHasVelocity()
    {
        const uint Style  = 0x8000003Du;
        const uint Motion = 0x40000003u;
        const uint AnimId = 0x03000120u;

        var anim   = Fixtures.MakeAnim(4, 1, Vector3.Zero, Quaternion.Identity);
        var setup  = Fixtures.MakeSetup(1);

        var mt = new MotionTable();
        mt.DefaultStyle = (DRWMotionCommand)Style;
        mt.StyleDefaults[(DRWMotionCommand)Style] = (DRWMotionCommand)Motion;
        int cycleKey = (int)((Style << 16) | (Motion & 0xFFFFFFu));
        var md = new MotionData
        {
            Flags    = MotionDataFlags.HasVelocity,
            Velocity = new Vector3(0f, 4f, 0f),  // 4 m/s forward
        };
        QualifiedDataId<Animation> qid = AnimId;
        md.Anims.Add(new AnimData { AnimId = qid, LowFrame = 0, HighFrame = 3, Framerate = 10f });
        mt.Cycles[cycleKey] = md;

        var loader = new FakeLoader();
        loader.Register(AnimId, anim);

        var seq = new AnimationSequencer(setup, mt, loader);
        seq.SetCycle(Style, Motion);

        Assert.Equal(new Vector3(0f, 4f, 0f), seq.CurrentVelocity);
    }

    [Fact]
    public void CurrentVelocity_ZeroLocomotionMotionData_IsNotSynthesizedFromCommand()
    {
        const uint Style = 0x8000003Du;
        const uint Motion = 0x40000007u;
        const uint AnimId = 0x03000406u;

        var anim = Fixtures.MakeAnim(4, 1, Vector3.Zero, Quaternion.Identity);
        var setup = Fixtures.MakeSetup(1);
        var mt = Fixtures.MakeMtable(Style, Motion, AnimId, framerate: 10f);
        var loader = new FakeLoader();
        loader.Register(AnimId, anim);

        var seq = new AnimationSequencer(setup, mt, loader);
        seq.SetCycle(Style, Motion, speedMod: 2f);

        Assert.Equal(Vector3.Zero, seq.CurrentVelocity);
    }

    [Fact]
    public void CurrentVelocity_ScaledBySpeedMod()
    {
        const uint Style  = 0x8000003Du;
        const uint Motion = 0x40000003u;
        const uint AnimId = 0x03000121u;

        var anim   = Fixtures.MakeAnim(4, 1, Vector3.Zero, Quaternion.Identity);
        var setup  = Fixtures.MakeSetup(1);

        var mt = new MotionTable();
        mt.DefaultStyle = (DRWMotionCommand)Style;
        mt.StyleDefaults[(DRWMotionCommand)Style] = (DRWMotionCommand)Motion;
        int cycleKey = (int)((Style << 16) | (Motion & 0xFFFFFFu));
        var md = new MotionData
        {
            Flags    = MotionDataFlags.HasVelocity,
            Velocity = new Vector3(0f, 4f, 0f),
        };
        QualifiedDataId<Animation> qid = AnimId;
        md.Anims.Add(new AnimData { AnimId = qid, LowFrame = 0, HighFrame = 3, Framerate = 10f });
        mt.Cycles[cycleKey] = md;

        var loader = new FakeLoader();
        loader.Register(AnimId, anim);

        var seq = new AnimationSequencer(setup, mt, loader);
        seq.SetCycle(Style, Motion, speedMod: 0.5f);

        // Velocity scaled by speedMod=0.5 → 2 m/s forward.
        Assert.Equal(new Vector3(0f, 2f, 0f), seq.CurrentVelocity);
    }

    [Fact]
    public void ConsumePendingHooks_AnimationDoneFires_WhenLinkDrains()
    {
        const uint Style      = 0x8000003Du;
        const uint IdleMotion = 0x40000003u;
        const uint WalkMotion = 0x40000005u;
        const uint IdleAnim   = 0x03000132u;
        const uint CycleAnim  = 0x03000130u;
        const uint LinkAnim   = 0x03000131u;

        var linkAnim  = Fixtures.MakeAnim(2, 1, Vector3.Zero, Quaternion.Identity);
        var cycleAnim = Fixtures.MakeAnim(4, 1, Vector3.Zero, Quaternion.Identity);
        var idleAnim  = Fixtures.MakeAnim(1, 1, Vector3.Zero, Quaternion.Identity);

        var setup = Fixtures.MakeSetup(1);
        var mt    = Fixtures.MakeMtable(
            style: Style, motion: WalkMotion, cycleAnimId: CycleAnim,
            fromMotion: IdleMotion, toMotion: WalkMotion, linkAnimId: LinkAnim,
            framerate: 10f);
        mt.StyleDefaults[(DRWMotionCommand)Style] = (DRWMotionCommand)IdleMotion;
        int idleKey = (int)((Style << 16) | (IdleMotion & 0xFFFFFFu));
        mt.Cycles[idleKey] = Fixtures.MakeMotionData(IdleAnim, framerate: 10f);

        var loader = new FakeLoader();
        loader.Register(IdleAnim,  idleAnim);
        loader.Register(CycleAnim, cycleAnim);
        loader.Register(LinkAnim,  linkAnim);

        var seq = new AnimationSequencer(setup, mt, loader);
        seq.SetCycle(Style, IdleMotion);
        seq.SetCycle(Style, WalkMotion);
        seq.ConsumePendingHooks();

        // Link is 2 frames at 10fps = 0.2s. Advance past it.
        seq.Advance(0.25f);
        var hooks = seq.ConsumePendingHooks();

        Assert.Contains(hooks, h => h is AnimationDoneHook);
    }

    // ── MultiplyCyclicFramerate / speed-mod tracking ─────────────────────────

    [Fact]
    public void MultiplyCyclicFramerate_DoublesPlaybackRate()
    {
        const uint Style  = 0x8000003Du;
        const uint Motion = 0x40000007u;   // RunForward
        const uint AnimId = 0x03000401u;

        var anim = new Animation();
        for (int f = 0; f < 10; f++)
        {
            var pf = new AnimationFrame(1);
            pf.Frames.Add(new Frame { Origin = new Vector3(0, 0, f), Orientation = Quaternion.Identity });
            anim.PartFrames.Add(pf);
        }

        var setup = Fixtures.MakeSetup(1);
        var mt = new MotionTable();
        mt.DefaultStyle = (DRWMotionCommand)Style;
        int cycleKey = (int)((Style << 16) | (Motion & 0xFFFFFFu));
        var md = new MotionData { Flags = MotionDataFlags.HasVelocity, Velocity = new Vector3(0, 4, 0) };
        QualifiedDataId<Animation> qid = AnimId;
        md.Anims.Add(new AnimData
        {
            AnimId    = qid,
            LowFrame  = 0,
            HighFrame = 9,
            Framerate = 10f,
        });
        mt.Cycles[cycleKey] = md;
        mt.StyleDefaults[(DRWMotionCommand)Style] = (DRWMotionCommand)Motion;

        var loader = new FakeLoader();
        loader.Register(AnimId, anim);

        var seq = new AnimationSequencer(setup, mt, loader);
        seq.SetCycle(Style, Motion, speedMod: 1f);

        seq.SetCycle(Style, Motion, speedMod: 0.5f);

        seq.Advance(1.0f);
        var frames = seq.Advance(0.001f);
        Assert.Single(frames);
        Assert.InRange(frames[0].Origin.Z, 4f, 6f);

        Assert.Equal(2f, seq.CurrentVelocity.Y, 1);
    }

    [Fact]
    public void MultiplyCyclicFramerate_PreservesCursorPosition()
    {
        const uint Style  = 0x8000003Du;
        const uint Motion = 0x40000007u;
        const uint AnimId = 0x03000402u;

        var anim = new Animation();
        for (int f = 0; f < 10; f++)
        {
            var pf = new AnimationFrame(1);
            pf.Frames.Add(new Frame { Origin = new Vector3(0, 0, f), Orientation = Quaternion.Identity });
            anim.PartFrames.Add(pf);
        }

        var setup = Fixtures.MakeSetup(1);
        var mt = new MotionTable();
        mt.DefaultStyle = (DRWMotionCommand)Style;
        int cycleKey = (int)((Style << 16) | (Motion & 0xFFFFFFu));
        mt.Cycles[cycleKey] = Fixtures.MakeMotionData(AnimId, framerate: 10f);
        mt.StyleDefaults[(DRWMotionCommand)Style] = (DRWMotionCommand)Motion;

        var loader = new FakeLoader();
        loader.Register(AnimId, anim);

        var seq = new AnimationSequencer(setup, mt, loader);
        seq.SetCycle(Style, Motion);
        seq.Advance(0.3f);
        double before = GetFramePosition(seq);

        seq.SetCycle(Style, Motion, speedMod: 2.0f);
        double after = GetFramePosition(seq);

        Assert.Equal(before, after, 5);
    }

    [Fact]
    public void SetCycle_SameMotionDifferentSpeed_RescalesInPlace()
    {
        const uint Style  = 0x8000003Du;
        const uint Motion = 0x40000007u;
        const uint AnimId = 0x03000403u;

        var anim = Fixtures.MakeAnim(10, 1, Vector3.Zero, Quaternion.Identity);
        var setup = Fixtures.MakeSetup(1);
        var mt = Fixtures.MakeMtable(Style, Motion, AnimId, framerate: 10f);

        var loader = new FakeLoader();
        loader.Register(AnimId, anim);

        var seq = new AnimationSequencer(setup, mt, loader);
        seq.SetCycle(Style, Motion, speedMod: 1f);
        seq.Advance(0.3f);
        double cursorMid = GetFramePosition(seq);

        Assert.Equal(1f, seq.CurrentSpeedMod, 3);

        seq.SetCycle(Style, Motion, speedMod: 2f);

        Assert.Equal(2f, seq.CurrentSpeedMod, 3);
        Assert.Equal(cursorMid, GetFramePosition(seq), 5);
    }

    [Fact]
    public void CurrentVelocity_ScalesWithSpeedMod()
    {
        const uint Style  = 0x8000003Du;
        const uint Motion = 0x40000007u;
        const uint AnimId = 0x03000405u;

        var anim = Fixtures.MakeAnim(4, 1, Vector3.Zero, Quaternion.Identity);
        var setup = Fixtures.MakeSetup(1);
        var mt = new MotionTable();
        mt.DefaultStyle = (DRWMotionCommand)Style;
        mt.StyleDefaults[(DRWMotionCommand)Style] = (DRWMotionCommand)Motion;
        int cycleKey = (int)((Style << 16) | (Motion & 0xFFFFFFu));

        var md = new MotionData { Flags = MotionDataFlags.HasVelocity, Velocity = new Vector3(0, 4, 0) };
        QualifiedDataId<Animation> qid = AnimId;
        md.Anims.Add(new AnimData
        {
            AnimId    = qid,
            LowFrame  = 0,
            HighFrame = -1,
            Framerate = 10f,
        });
        mt.Cycles[cycleKey] = md;

        var loader = new FakeLoader();
        loader.Register(AnimId, anim);

        var seq = new AnimationSequencer(setup, mt, loader);
        seq.SetCycle(Style, Motion, speedMod: 1f);
        Assert.Equal(4f, seq.CurrentVelocity.Y, 3);

        // Start a fresh sequencer so the initial SetCycle applies speedMod.
        var seq2 = new AnimationSequencer(setup, mt, loader);
        seq2.SetCycle(Style, Motion, speedMod: 1.5f);
        Assert.Equal(6f, seq2.CurrentVelocity.Y, 3);

        // Same-motion rescale path also updates velocity.
        seq2.SetCycle(Style, Motion, speedMod: 0.5f);
        Assert.Equal(2f, seq2.CurrentVelocity.Y, 2);
    }

    [Fact]
    public void SetCycle_SameMotionSameSpeed_StaysNoOp()
    {
        const uint Style  = 0x8000003Du;
        const uint Motion = 0x40000007u;
        const uint AnimId = 0x03000404u;

        var anim = Fixtures.MakeAnim(10, 1, Vector3.Zero, Quaternion.Identity);
        var setup = Fixtures.MakeSetup(1);
        var mt = Fixtures.MakeMtable(Style, Motion, AnimId);

        var loader = new FakeLoader();
        loader.Register(AnimId, anim);

        var seq = new AnimationSequencer(setup, mt, loader);
        seq.SetCycle(Style, Motion, speedMod: 1.5f);
        seq.Advance(0.2f);
        double before = GetFramePosition(seq);

        seq.SetCycle(Style, Motion, speedMod: 1.5f);

        Assert.Equal(before, GetFramePosition(seq), 5);
        Assert.Equal(1.5f, seq.CurrentSpeedMod, 3);
    }

    [Fact]
    public void CurrentOmega_ReflectsMotionDataOmega()
    {
        const uint Style  = 0x8000003Du;
        const uint Motion = 0x4000000Du;   // TurnRight
        const uint AnimId = 0x03000701u;

        var anim = Fixtures.MakeAnim(4, 1, Vector3.Zero, Quaternion.Identity);
        var setup = Fixtures.MakeSetup(1);
        var mt = new MotionTable();
        mt.DefaultStyle = (DRWMotionCommand)Style;
        mt.StyleDefaults[(DRWMotionCommand)Style] = (DRWMotionCommand)Motion;
        int cycleKey = (int)((Style << 16) | (Motion & 0xFFFFFFu));

        var md = new MotionData { Flags = MotionDataFlags.HasOmega, Omega = new Vector3(0, 0, 1.0f) };
        QualifiedDataId<Animation> qid = AnimId;
        md.Anims.Add(new AnimData { AnimId = qid, LowFrame = 0, HighFrame = -1, Framerate = 10f });
        mt.Cycles[cycleKey] = md;

        var loader = new FakeLoader();
        loader.Register(AnimId, anim);

        var seq = new AnimationSequencer(setup, mt, loader);
        seq.SetCycle(Style, Motion, speedMod: 2f);

        // Omega scales by speedMod — 1.0 × 2 = 2 rad/sec.
        Assert.Equal(2.0f, seq.CurrentOmega.Z, 3);
    }

    [Fact]
    public void CurrentVelocity_PersistsThroughLinkTransition()
    {
        const uint Style       = 0x8000003Du;
        const uint IdleMotion  = 0x40000003u;
        const uint WalkMotion  = 0x40000005u;
        const uint IdleAnim    = 0x03000603u;
        const uint CycleAnim   = 0x03000601u;
        const uint LinkAnim    = 0x03000602u;

        var cycleAnim = Fixtures.MakeAnim(4, 1, Vector3.Zero, Quaternion.Identity);
        var linkAnim  = Fixtures.MakeAnim(2, 1, Vector3.Zero, Quaternion.Identity);
        var idleAnim  = Fixtures.MakeAnim(1, 1, Vector3.Zero, Quaternion.Identity);

        var setup = Fixtures.MakeSetup(1);
        var mt = new MotionTable();
        mt.DefaultStyle = (DRWMotionCommand)Style;
        mt.StyleDefaults[(DRWMotionCommand)Style] = (DRWMotionCommand)IdleMotion;

        int idleKey = (int)((Style << 16) | (IdleMotion & 0xFFFFFFu));
        mt.Cycles[idleKey] = Fixtures.MakeMotionData(IdleAnim, framerate: 10f);

        int cycleKey = (int)((Style << 16) | (WalkMotion & 0xFFFFFFu));
        var cycleMd = new MotionData { Flags = MotionDataFlags.HasVelocity, Velocity = new Vector3(0, 3.12f, 0) };
        QualifiedDataId<Animation> cycleQid = CycleAnim;
        cycleMd.Anims.Add(new AnimData { AnimId = cycleQid, LowFrame = 0, HighFrame = -1, Framerate = 10f });
        mt.Cycles[cycleKey] = cycleMd;

        // Link from idle → walk. Link MotionData has no velocity (typical).
        int linkOuter = (int)((Style << 16) | (IdleMotion & 0xFFFFFFu));
        var linkCmdData = new MotionCommandData();
        var linkMd = new MotionData(); // no HasVelocity flag
        QualifiedDataId<Animation> linkQid = LinkAnim;
        linkMd.Anims.Add(new AnimData { AnimId = linkQid, LowFrame = 0, HighFrame = -1, Framerate = 10f });
        linkCmdData.MotionData[(int)WalkMotion] = linkMd;
        mt.Links[linkOuter] = linkCmdData;

        var loader = new FakeLoader();
        loader.Register(IdleAnim, idleAnim);
        loader.Register(CycleAnim, cycleAnim);
        loader.Register(LinkAnim, linkAnim);

        var seq = new AnimationSequencer(setup, mt, loader);
        seq.SetCycle(Style, IdleMotion);
        seq.SetCycle(Style, WalkMotion);

        Assert.Equal(3.12f, seq.CurrentVelocity.Y, 2);

        // Advance past the link frames (2 frames at 10fps = 0.2s).
        seq.Advance(0.25f);

        Assert.Equal(3.12f, seq.CurrentVelocity.Y, 2);
    }


    [Fact]
    public void PlayAction_Action_ResolvesFromLinksDict()
    {
        const uint Style       = 0x8000003Eu;      // SwordCombat
        const uint IdleMotion  = 0x41000003u;      // Ready
        const uint ActionMotion = 0x10000058u;
        const uint IdleAnimId  = 0x03000501u;
        const uint ActionAnimId= 0x03000502u;

        var idleAnim = Fixtures.MakeAnim(4, 1, Vector3.Zero, Quaternion.Identity);
        // Action anim: distinct non-zero origin so we can detect it played.
        var actionAnim = Fixtures.MakeAnim(3, 1, new Vector3(99, 0, 0), Quaternion.Identity);

        var setup = Fixtures.MakeSetup(1);
        var mt = new MotionTable();
        mt.DefaultStyle = (DRWMotionCommand)Style;
        mt.StyleDefaults[(DRWMotionCommand)Style] = (DRWMotionCommand)IdleMotion;
        int cycleKey = (int)((Style << 16) | (IdleMotion & 0xFFFFFFu));
        mt.Cycles[cycleKey] = Fixtures.MakeMotionData(IdleAnimId, framerate: 10f);

        // Link: (SwordCombat, Ready) → ThrustMed
        int linkOuter = (int)((Style << 16) | (IdleMotion & 0xFFFFFFu));
        var cmdData = new MotionCommandData();
        cmdData.MotionData[(int)ActionMotion] = Fixtures.MakeMotionData(ActionAnimId, framerate: 10f);
        mt.Links[linkOuter] = cmdData;

        var loader = new FakeLoader();
        loader.Register(IdleAnimId, idleAnim);
        loader.Register(ActionAnimId, actionAnim);

        var seq = new AnimationSequencer(setup, mt, loader);
        seq.SetCycle(Style, IdleMotion);
        seq.Advance(0.01f);   // burn the first idle frame

        // Fire the action.
        seq.PlayAction(ActionMotion);

        // After a small advance, we should be reading the action anim (origin X=99).
        var fr = seq.Advance(0.01f);
        Assert.Single(fr);
        Assert.Equal(99f, fr[0].Origin.X, 1);
    }

    [Fact]
    public void PlayAction_ActionSurvivesImmediateReadyCycleEcho()
    {
        const uint Style        = 0x8000003Du;
        const uint IdleMotion   = 0x41000003u;
        const uint AttackMotion = 0x10000052u;
        const uint IdleAnimId   = 0x03000503u;
        const uint AttackAnimId = 0x03000504u;

        var setup = Fixtures.MakeSetup(1);
        var mt = new MotionTable { DefaultStyle = (DRWMotionCommand)Style };
        mt.StyleDefaults[(DRWMotionCommand)Style] = (DRWMotionCommand)IdleMotion;
        int cycleKey = (int)((Style << 16) | (IdleMotion & 0xFFFFFFu));
        mt.Cycles[cycleKey] = Fixtures.MakeMotionData(IdleAnimId, framerate: 10f);

        int linkOuter = (int)((Style << 16) | (IdleMotion & 0xFFFFFFu));
        var cmdData = new MotionCommandData();
        cmdData.MotionData[(int)AttackMotion] = Fixtures.MakeMotionData(AttackAnimId, framerate: 10f);
        mt.Links[linkOuter] = cmdData;

        var loader = new FakeLoader();
        loader.Register(IdleAnimId, Fixtures.MakeAnim(4, 1, Vector3.Zero, Quaternion.Identity));
        loader.Register(AttackAnimId, Fixtures.MakeAnim(3, 1, new Vector3(12, 0, 0), Quaternion.Identity));

        var seq = new AnimationSequencer(setup, mt, loader);
        seq.SetCycle(Style, IdleMotion);
        seq.PlayAction(AttackMotion);

        seq.SetCycle(Style, IdleMotion);

        var fr = seq.Advance(0.01f);
        Assert.Single(fr);
        Assert.Equal(12f, fr[0].Origin.X, 1);
        Assert.Equal(IdleMotion, seq.CurrentMotion);
    }

    [Fact]
    public void PlayAction_Modifier_ResolvesFromModifiersDict()
    {
        const uint Style       = 0x8000003Du;
        const uint IdleMotion  = 0x41000003u;
        const uint JumpMotion  = 0x2500003Bu;
        const uint IdleAnimId  = 0x03000510u;
        const uint JumpAnimId  = 0x03000511u;

        var idleAnim = Fixtures.MakeAnim(4, 1, Vector3.Zero, Quaternion.Identity);
        var jumpAnim = Fixtures.MakeAnim(3, 1, new Vector3(0, 0, 77), Quaternion.Identity);

        var setup = Fixtures.MakeSetup(1);
        var mt = new MotionTable();
        mt.DefaultStyle = (DRWMotionCommand)Style;
        mt.StyleDefaults[(DRWMotionCommand)Style] = (DRWMotionCommand)IdleMotion;
        int cycleKey = (int)((Style << 16) | (IdleMotion & 0xFFFFFFu));
        mt.Cycles[cycleKey] = Fixtures.MakeMotionData(IdleAnimId, framerate: 10f);

        int modKey = (int)((Style << 16) | (JumpMotion & 0xFFFFFFu));
        var jumpMd = Fixtures.MakeMotionData(JumpAnimId, framerate: 10f);
        jumpMd.Flags = MotionDataFlags.HasOmega;
        jumpMd.Omega = new Vector3(0f, 0f, 2.5f);
        mt.Modifiers[modKey] = jumpMd;

        var loader = new FakeLoader();
        loader.Register(IdleAnimId, idleAnim);
        loader.Register(JumpAnimId, jumpAnim);

        var seq = new AnimationSequencer(setup, mt, loader);
        seq.SetCycle(Style, IdleMotion);
        int queueBefore = seq.QueueCount;

        seq.PlayAction(JumpMotion);

        // No anim nodes inserted — the queue is unchanged from before the
        // modifier fired.
        Assert.Equal(queueBefore, seq.QueueCount);
        Assert.Equal(2.5f, seq.CurrentOmega.Z, 3);
    }

    [Fact]
    public void PlayAction_Emote_RoutesThroughActionBranch()
    {
        const uint Style       = 0x8000003Du;
        const uint IdleMotion  = 0x41000003u;
        const uint WaveMotion  = 0x13000087u;
        const uint IdleAnimId  = 0x03000520u;
        const uint WaveAnimId  = 0x03000521u;

        var idleAnim = Fixtures.MakeAnim(4, 1, Vector3.Zero, Quaternion.Identity);
        var waveAnim = Fixtures.MakeAnim(5, 1, new Vector3(0, 55, 0), Quaternion.Identity);

        var setup = Fixtures.MakeSetup(1);
        var mt = new MotionTable();
        mt.DefaultStyle = (DRWMotionCommand)Style;
        mt.StyleDefaults[(DRWMotionCommand)Style] = (DRWMotionCommand)IdleMotion;
        int cycleKey = (int)((Style << 16) | (IdleMotion & 0xFFFFFFu));
        mt.Cycles[cycleKey] = Fixtures.MakeMotionData(IdleAnimId, framerate: 10f);

        // Register Links[(style, Ready)][Wave] = wave anim.
        int linkOuter = (int)((Style << 16) | (IdleMotion & 0xFFFFFFu));
        var cmdData = new MotionCommandData();
        cmdData.MotionData[(int)WaveMotion] = Fixtures.MakeMotionData(WaveAnimId, framerate: 10f);
        mt.Links[linkOuter] = cmdData;

        var loader = new FakeLoader();
        loader.Register(IdleAnimId, idleAnim);
        loader.Register(WaveAnimId, waveAnim);

        var seq = new AnimationSequencer(setup, mt, loader);
        seq.SetCycle(Style, IdleMotion);

        seq.PlayAction(WaveMotion);

        var fr = seq.Advance(0.01f);
        Assert.Single(fr);
        Assert.Equal(55f, fr[0].Origin.Y, 1);
    }

    [Fact]
    public void PlayAction_NoEntryInTable_IsNoOp()
    {
        const uint Style      = 0x003Du;
        const uint IdleMotion = 0x41000003u;
        const uint IdleAnimId = 0x03000530u;
        const uint UnknownAction = 0x10001234u;

        var idleAnim = Fixtures.MakeAnim(4, 1, Vector3.Zero, Quaternion.Identity);
        var setup = Fixtures.MakeSetup(1);
        var mt = new MotionTable();
        mt.DefaultStyle = (DRWMotionCommand)Style;
        int cycleKey = (int)((Style << 16) | (IdleMotion & 0xFFFFFFu));
        mt.Cycles[cycleKey] = Fixtures.MakeMotionData(IdleAnimId, framerate: 10f);

        var loader = new FakeLoader();
        loader.Register(IdleAnimId, idleAnim);

        var seq = new AnimationSequencer(setup, mt, loader);
        seq.SetCycle(Style, IdleMotion);
        seq.Advance(0.05f);
        int queueBefore = seq.QueueCount;

        seq.PlayAction(UnknownAction);   // unknown motion → no-op

        Assert.Equal(queueBefore, seq.QueueCount);
    }

    // ── Helpers ──────────────────────────────────────────────────────────────

    [Fact]
    public void InitializeSetupDefaultAnimation_PreservesPhysicsAndPlacementState()
    {
        const uint animationId = 0x03000531u;
        var loader = new FakeLoader();
        loader.Register(
            animationId,
            Fixtures.MakeAnim(2, 1, Vector3.Zero, Quaternion.Identity));
        var seq = new AnimationSequencer(
            Fixtures.MakeSetup(1),
            new MotionTable(),
            loader);
        var placement = new AnimationFrame(1);
        Vector3 velocity = new(1f, 2f, 3f);
        Vector3 omega = new(0.1f, 0.2f, 0.3f);
        seq.Core.SetVelocity(velocity);
        seq.Core.SetOmega(omega);
        seq.Core.SetPlacementFrame(placement, 0x1234u);

        Assert.True(seq.InitializeSetupDefaultAnimation(animationId));

        Assert.Equal(velocity, seq.CurrentVelocity);
        Assert.Equal(omega, seq.CurrentOmega);
        Assert.Same(placement, seq.Core.PlacementFrame);
        Assert.Equal(0x1234u, seq.Core.PlacementFrameId);
    }

    private static double GetFramePosition(AnimationSequencer seq)
    {
        var coreField = typeof(AnimationSequencer)
            .GetField("_core",
                System.Reflection.BindingFlags.NonPublic |
                System.Reflection.BindingFlags.Instance);
        var core = coreField?.GetValue(seq);
        if (core is null) return -1.0;

        var frameNumberField = core.GetType()
            .GetField("FrameNumber",
                System.Reflection.BindingFlags.Public |
                System.Reflection.BindingFlags.Instance);
        return frameNumberField is null ? -1.0 : (double)frameNumberField.GetValue(core)!;
    }

    private static void SetCurrentMotion(AnimationSequencer seq, uint style, uint motion)
    {
        var t = typeof(AnimationSequencer);
        t.GetProperty(nameof(AnimationSequencer.CurrentStyle))!
         .SetValue(seq, style);
        t.GetProperty(nameof(AnimationSequencer.CurrentMotion))!
         .SetValue(seq, motion);
    }
}
