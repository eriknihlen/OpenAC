using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using System.Numerics;
using System.Text;
using AcDream.Core.Physics;
using DatReaderWriter.DBObjs;
using DatReaderWriter.Types;
using Xunit;

using DRWMotionCommand = DatReaderWriter.Enums.MotionCommand;

namespace AcDream.Core.Tests.Physics;


internal sealed class TraceLoader : IAnimationLoader
{
    private readonly Dictionary<uint, Animation> _anims = new();
    private readonly Dictionary<Animation, uint> _ids = new();

    public void Register(uint id, Animation anim)
    {
        _anims[id] = anim;
        _ids[anim] = id;
    }

    public Animation? LoadAnimation(uint id) =>
        _anims.TryGetValue(id, out var a) ? a : null;

    public uint IdOf(Animation anim) => _ids.TryGetValue(anim, out var id) ? id : 0;
}

public sealed class AnimationSequencerCutoverTraceTests
{
    private const uint NC = 0x8000003Du;      // NonCombat
    private const uint Style2 = 0x8000004Cu;  // synthetic second style

    private const uint Ready = 0x41000003u;
    private const uint Walk = 0x45000005u;
    private const uint WalkBack = 0x45000006u;
    private const uint Run = 0x44000007u;
    private const uint Falling = 0x40000015u;

    private const uint EmoteAction = 0x10000062u;
    private const uint TurnMod = 0x6500000Du;

    // Anim resource ids.
    private const uint ReadyAnim = 0x100u;       // 4 frames
    private const uint WalkAnim = 0x101u;        // 6 frames
    private const uint RunAnim = 0x102u;         // 6 frames
    private const uint ReadyToWalkLink = 0x103u; // 2 frames
    private const uint ReadyToRunLink = 0x104u;  // 2 frames
    private const uint WalkToReadyLink = 0x105u; // 3 frames
    private const uint RunToReadyLink = 0x108u;  // 3 frames
    private const uint TurnModAnim = 0x109u;     // 2 frames
    private const uint ReadyToStyle2Link = 0x10Au; // 2 frames
    private const uint ReadyToEmoteLink = 0x10Bu;  // 5 frames
    private const uint FallAnim = 0x10Cu;        // 4 frames
    private const uint Style2ReadyAnim = 0x107u; // 4 frames

    private static Animation MakeAnim(int numFrames, int numParts = 1)
    {
        var anim = new Animation();
        for (int f = 0; f < numFrames; f++)
        {
            var pf = new AnimationFrame((uint)numParts);
            for (int p = 0; p < numParts; p++)
                pf.Frames.Add(new Frame { Origin = Vector3.Zero, Orientation = Quaternion.Identity });
            anim.PartFrames.Add(pf);
        }
        return anim;
    }

    private static MotionData MakeMd(uint animId, float framerate = 30f,
        Vector3? velocity = null, Vector3? omega = null)
    {
        var md = new MotionData();
        QualifiedDataId<Animation> qid = animId;
        md.Anims.Add(new AnimData
        {
            AnimId = qid,
            LowFrame = 0,
            HighFrame = -1,
            Framerate = framerate,
        });
        if (velocity is { } v)
        {
            md.Velocity = v;
            md.Flags |= DatReaderWriter.Enums.MotionDataFlags.HasVelocity;
        }
        if (omega is { } o)
        {
            md.Omega = o;
            md.Flags |= DatReaderWriter.Enums.MotionDataFlags.HasOmega;
        }
        return md;
    }

    private static void AddLink(MotionTable mt, uint style, uint from, uint to, MotionData md)
    {
        int outer = (int)((style << 16) | (from & 0xFFFFFFu));
        if (!mt.Links.TryGetValue(outer, out var cmd))
        {
            cmd = new MotionCommandData();
            mt.Links[outer] = cmd;
        }
        cmd.MotionData[(int)to] = md;
    }

    private static (Setup, MotionTable, TraceLoader) BuildFixture()
    {
        var setup = new Setup();
        setup.Parts.Add(0x01000000u);
        setup.DefaultScale.Add(Vector3.One);

        var loader = new TraceLoader();
        loader.Register(ReadyAnim, MakeAnim(4));
        loader.Register(WalkAnim, MakeAnim(6));
        loader.Register(RunAnim, MakeAnim(6));
        loader.Register(ReadyToWalkLink, MakeAnim(2));
        loader.Register(ReadyToRunLink, MakeAnim(2));
        loader.Register(WalkToReadyLink, MakeAnim(3));
        loader.Register(RunToReadyLink, MakeAnim(3));
        loader.Register(TurnModAnim, MakeAnim(2));
        loader.Register(ReadyToStyle2Link, MakeAnim(2));
        loader.Register(ReadyToEmoteLink, MakeAnim(5));
        loader.Register(FallAnim, MakeAnim(4));
        loader.Register(Style2ReadyAnim, MakeAnim(4));

        var mt = new MotionTable
        {
            DefaultStyle = (DRWMotionCommand)NC,
        };
        mt.StyleDefaults[(DRWMotionCommand)NC] = (DRWMotionCommand)Ready;
        mt.StyleDefaults[(DRWMotionCommand)Style2] = (DRWMotionCommand)Ready;

        int CycleKey(uint style, uint substate) => (int)((style << 16) | (substate & 0xFFFFFFu));
        mt.Cycles[CycleKey(NC, Ready)] = MakeMd(ReadyAnim);
        mt.Cycles[CycleKey(NC, Walk)] = MakeMd(WalkAnim, velocity: new Vector3(0f, 3.12f, 0f));
        mt.Cycles[CycleKey(NC, Run)] = MakeMd(RunAnim, velocity: new Vector3(0f, 4.0f, 0f));
        mt.Cycles[CycleKey(NC, Falling)] = MakeMd(FallAnim);
        mt.Cycles[CycleKey(Style2, Ready)] = MakeMd(Style2ReadyAnim);

        AddLink(mt, NC, Ready, Walk, MakeMd(ReadyToWalkLink));
        AddLink(mt, NC, Ready, Run, MakeMd(ReadyToRunLink));
        AddLink(mt, NC, Walk, Ready, MakeMd(WalkToReadyLink));
        AddLink(mt, NC, Run, Ready, MakeMd(RunToReadyLink));
        AddLink(mt, NC, Ready, EmoteAction, MakeMd(ReadyToEmoteLink));
        AddLink(mt, NC, Ready, Style2, MakeMd(ReadyToStyle2Link));

        // Modifier: styled key (styleMasked<<16 | low24).
        int modKey = (int)((NC << 16) | (TurnMod & 0xFFFFFFu));
        mt.Modifiers[modKey] = MakeMd(TurnModAnim, omega: new Vector3(0f, 0f, 1.5f));

        return (setup, mt, loader);
    }

    private static string Describe(AnimationSequencer seq, TraceLoader loader)
    {
        var core = seq.Core;
        var sb = new StringBuilder();
        bool first = true;
        for (var n = core.AnimList.First; n is not null; n = n.Next)
        {
            if (!first) sb.Append(',');
            first = false;
            uint id = n.Value.Anim is null ? 0u : loader.IdOf(n.Value.Anim);
            sb.Append(CultureInfo.InvariantCulture, $"{id:X}@{n.Value.Framerate:F1}");
            if (ReferenceEquals(n, core.FirstCyclicNode)) sb.Append('*');
            if (ReferenceEquals(n, core.CurrAnimNode)) sb.Append('^');
        }
        var v = core.Velocity;
        var o = core.Omega;
        sb.Append(CultureInfo.InvariantCulture, $" | frame={core.FrameNumber:F1}");
        sb.Append(CultureInfo.InvariantCulture, $" vel=({v.X:F2},{v.Y:F2},{v.Z:F2})");
        sb.Append(CultureInfo.InvariantCulture, $" om=({o.X:F2},{o.Y:F2},{o.Z:F2})");
        sb.Append(
            CultureInfo.InvariantCulture,
            $" style={seq.CurrentStyle:X8} motion={seq.CurrentMotion:X8} mod={seq.CurrentSpeedMod:F2}");
        return sb.ToString();
    }

    private static AnimationSequencer NewSeq(out TraceLoader loader)
    {
        var (setup, mt, l) = BuildFixture();
        loader = l;
        return new AnimationSequencer(setup, mt, l);
    }

    // ── Scenarios ───────────────────────────────────────────────────────────

    [Fact]
    public void S1_SpawnIdle()
    {
        var seq = NewSeq(out var loader);
        seq.SetCycle(NC, Ready);
        Assert.Equal(
            "100@30.0*^ | frame=0.0 vel=(0.00,0.00,0.00) om=(0.00,0.00,0.00) style=8000003D motion=41000003 mod=1.00",
            Describe(seq, loader));
    }

    [Fact]
    public void S2_IdleToWalk_LinkThenCycle()
    {
        var seq = NewSeq(out var loader);
        seq.SetCycle(NC, Ready);
        seq.SetCycle(NC, Walk, 1.0f);
        Assert.Equal(
            "103@30.0^,101@30.0* | frame=0.0 vel=(0.00,3.12,0.00) om=(0.00,0.00,0.00) style=8000003D motion=45000005 mod=1.00",
            Describe(seq, loader));
    }

    [Fact]
    public void S2b_LinkDrain_FiresOneAnimDoneSentinel()
    {
        var seq = NewSeq(out var loader);
        seq.SetCycle(NC, Ready);
        seq.SetCycle(NC, Walk, 1.0f);

        int animDone = 0;
        for (int i = 0; i < 10; i++)
        {
            seq.Advance(0.02f);
            foreach (var h in seq.ConsumePendingHooks())
                if (h is DatReaderWriter.Types.AnimationDoneHook)
                    animDone++;
        }
        Assert.Equal(1, animDone);
    }

    [Fact]
    public void S3_WalkToRun_CyclicToCyclic()
    {
        var seq = NewSeq(out var loader);
        seq.SetCycle(NC, Ready);
        seq.SetCycle(NC, Walk, 1.0f);
        seq.SetCycle(NC, Run, 2.0f);
        Assert.Equal(
            "103@30.0^,105@30.0,104@60.0,102@60.0* | frame=0.0 vel=(0.00,8.00,0.00) om=(0.00,0.00,0.00) style=8000003D motion=44000007 mod=2.00",
            Describe(seq, loader));
    }

    [Fact]
    public void S4_RunReSpeed_FastPath()
    {
        var seq = NewSeq(out var loader);
        seq.SetCycle(NC, Ready);
        seq.SetCycle(NC, Walk, 1.0f);
        seq.SetCycle(NC, Run, 2.0f);
        seq.SetCycle(NC, Run, 2.5f);
        Assert.Equal(
            "103@30.0^,105@30.0,104@60.0,102@75.0* | frame=0.0 vel=(0.00,10.00,0.00) om=(0.00,0.00,0.00) style=8000003D motion=44000007 mod=2.50",
            Describe(seq, loader));
    }

    [Fact]
    public void S5_WalkBackward_RemapNegativeSpeed()
    {
        var seq = NewSeq(out var loader);
        seq.SetCycle(NC, Ready);
        seq.SetCycle(NC, WalkBack, 1.0f);
        Assert.Equal(
            "105@-19.5^,101@-19.5* | frame=3.0 vel=(-0.00,-2.03,-0.00) om=(-0.00,-0.00,-0.00) style=8000003D motion=45000005 mod=-0.65",
            Describe(seq, loader));
    }

    [Fact]
    public void S6_WalkBackToReady_StopSettleFallback()
    {
        var seq = NewSeq(out var loader);
        seq.SetCycle(NC, Ready);
        seq.SetCycle(NC, WalkBack, 1.0f);
        seq.SetCycle(NC, Ready, 1.0f);
        Assert.Equal(
            "105@-19.5^,103@-30.0,100@30.0* | frame=3.0 vel=(0.00,0.00,0.00) om=(0.00,0.00,0.00) style=8000003D motion=41000003 mod=1.00",
            Describe(seq, loader));
    }

    [Fact]
    public void S7_EmoteAction_MidReady()
    {
        var seq = NewSeq(out var loader);
        seq.SetCycle(NC, Ready);
        seq.PlayAction(EmoteAction, 1.0f);
        Assert.Equal(
            "10B@30.0^,100@30.0* | frame=0.0 vel=(0.00,0.00,0.00) om=(0.00,0.00,0.00) style=8000003D motion=41000003 mod=1.00",
            Describe(seq, loader));
    }

    [Fact]
    public void S8_LeaveGroundLinkStrip_FallingEngagesInstantly()
    {
        var seq = NewSeq(out var loader);
        seq.SetCycle(NC, Ready);
        seq.SetCycle(NC, Walk, 1.0f);
        seq.SetCycle(NC, Falling, 1.0f);
        seq.RemoveAllLinkAnimations(); // = LeaveGround's seam firing
        Assert.Equal(
            "10C@30.0*^ | frame=0.0 vel=(0.00,0.00,0.00) om=(0.00,0.00,0.00) style=8000003D motion=40000015 mod=1.00",
            Describe(seq, loader));
    }

    [Fact]
    public void S9_TurnModifier()
    {
        var seq = NewSeq(out var loader);
        seq.SetCycle(NC, Ready);
        seq.SetCycle(NC, Walk, 1.0f);
        seq.PlayAction(TurnMod, 1.0f);
        Assert.Equal(
            "103@30.0^,101@30.0* | frame=0.0 vel=(0.00,3.12,0.00) om=(0.00,0.00,1.50) style=8000003D motion=45000005 mod=1.00",
            Describe(seq, loader));
    }

    [Fact]
    public void S10_StyleChange()
    {
        var seq = NewSeq(out var loader);
        seq.SetCycle(NC, Ready);
        seq.SetCycle(Style2, Ready, 1.0f);
        Assert.Equal(
            "10A@30.0^,107@30.0* | frame=0.0 vel=(0.00,0.00,0.00) om=(0.00,0.00,0.00) style=8000004C motion=41000003 mod=1.00",
            Describe(seq, loader));
    }
}
